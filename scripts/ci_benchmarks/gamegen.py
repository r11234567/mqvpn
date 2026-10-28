#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""gamegen.py -- tick-shaped UDP load, and the stall it causes.

A game proxy's failure mode is not lost bandwidth, it is a player watching
another vehicle teleport. That happens when an ordered channel holds packets
it already has, waiting for one that is late. Nothing in the harness could
measure the duration of that hold, and no packaged tool can either:

  iperf2 2.1.9   ticks (--isochronous) and full-duplex, and it is the only
                 tool here that reports reordering at all (-y C field 14).
                 But payload size is a single fixed -l, and --trip-times
                 refuses any -l below 128, so 10-30 byte packets and one-way
                 delay are mutually exclusive.
  sockperf 3.7   the only tool with random per-packet sizes (-m 20 -r 10),
                 floor 14 bytes. Its --full-log SKIPS reordered packets
                 (client.cpp:507 `continue`) and its first column is a loop
                 index, not a sequence number -- so the one log that exists
                 discards exactly the packets a reorder study needs.
  pktgen         `burst` is documented as xmit_more batching.
  trafgen        no receive-side measurement at all.

RFC 4737 defines Reordering Extent and a Late Time Offset (§4.3,
DstTime(i) - DstTime(i-e)); RFC 5236 defines Reorder Buffer-occupancy
Density. Neither defines a stall DURATION -- RBD is indexed by arrival
instant, not by time, and its reference algorithm has no clock in it. Valve
names the missing metric outright in steamnetworkingsockets_snp.h, beside
k_usecNackFlush: "we should probably try to learn the delay. E.g. a
probability distribution P(t)". This file measures P(t).

Both ends share CLOCK_MONOTONIC (same host, separate netns), so one-way
delay here is real rather than half an RTT.

  recv --bind IP:PORT --out FILE [--secs N]
  send --to IP:PORT [--secs 20] [--tick-hz 10] [--pps 17067]
       [--len 10:30] [--burst-mult 2.0] [--burst-every 10]
  analyze --in FILE [--rto-ms 410] [--fragment]
"""

import argparse
import array
import json
import os
import random
import re
import socket
import struct
import sys
import time

HDR = struct.Struct("!II")          # seq, tx_us  -- 8 bytes, so min len is 10
HDR_LEN = HDR.size
FIN = 0xFFFFFFFF
WRAP = 1 << 32


def now_us():
    return (time.monotonic_ns() // 1000) & 0xFFFFFFFF


def sock_drops(sock):
    """Kernel drops on this socket, from /proc/net/udp.

    A generator that cannot keep up looks exactly like a tunnel that dropped
    packets. This is what tells the two apart, so it is not optional.
    """
    try:
        ino = os.readlink("/proc/self/fd/%d" % sock.fileno())
        m = re.search(r"socket:\[(\d+)\]", ino)
        if not m:
            return None
        want = m.group(1)
        for path in ("/proc/net/udp", "/proc/net/udp6"):
            try:
                with open(path) as fh:
                    next(fh)
                    for line in fh:
                        p = line.split()
                        if len(p) > 12 and p[9] == want:
                            return int(p[-1])
            except (OSError, StopIteration):
                continue
    except (OSError, ValueError):
        pass
    return None


def do_recv(a):
    host, _, port = a.bind.rpartition(":")
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    # Large, so a scheduling gap in this process does not become a "loss".
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16 << 20)
    s.bind((host, int(port)))
    s.settimeout(a.idle)

    seqs, txs, rxs = (array.array("I") for _ in range(3))
    ap_s, ap_t, ap_r = seqs.append, txs.append, rxs.append
    unpack, recv_into = HDR.unpack_from, s.recv_into
    mv = memoryview(bytearray(2048))
    mono = time.monotonic_ns

    d0 = sock_drops(s)
    deadline = time.monotonic() + a.secs
    n = 0
    while True:
        try:
            got = recv_into(mv)
        except socket.timeout:
            break
        rx = (mono() // 1000) & 0xFFFFFFFF
        if got < HDR_LEN:
            continue
        sq, tx = unpack(mv)
        if sq == FIN:
            break
        ap_s(sq)
        ap_t(tx)
        ap_r(rx)
        n += 1
        # Clock calls are not free at 17k pps; check the wall rarely.
        if not (n & 1023) and time.monotonic() > deadline:
            break
    d1 = sock_drops(s)
    s.close()

    drops = None if d0 is None or d1 is None else d1 - d0
    # One JSON line then three raw u32 arrays: at 340k packets a text format
    # would cost more to write than the measurement costs to run.
    with open(a.out, "wb") as fh:
        fh.write((json.dumps({"n": len(seqs), "rcvbuf_drops": drops})
                  + "\n").encode())
        for arr in (seqs, txs, rxs):
            arr.tofile(fh)
    sys.stderr.write("gamegen recv: %d packets, rcvbuf_drops=%s\n"
                     % (len(seqs), "?" if drops is None else drops))


def load(path):
    with open(path, "rb") as fh:
        meta = json.loads(fh.readline().decode())
        out = []
        for _ in range(3):
            arr = array.array("I")
            arr.fromfile(fh, meta["n"])
            out.append(arr)
    return meta, out


def do_send(a):
    host, _, port = a.to.rpartition(":")
    dst = (host, int(port))
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 8 << 20)

    lo, _, hi = a.len.partition(":")
    lo, hi = max(HDR_LEN, int(lo)), int(hi or lo)
    period = 1.0 / a.tick_hz
    per_tick = max(1, round(a.pps / a.tick_hz))
    ticks = int(a.secs * a.tick_hz)
    # One buffer, resliced. Content is irrelevant; only the header is read.
    pad = bytes(hi)
    rnd, pack, sendto = random.Random(a.seed).randrange, HDR.pack, s.sendto
    mono, sleep = time.monotonic, time.sleep

    seq = 0
    t0 = mono()
    for t in range(ticks):
        target = t0 + t * period
        gap = target - mono()
        if gap > 0:
            sleep(gap)
        # A burst tick is the same shape, more of it -- that is what an
        # instantaneous spike is: the tick that had more to say.
        n = per_tick
        if a.burst_every and t and t % a.burst_every == 0:
            n = int(n * a.burst_mult)
        for _ in range(n):
            ln = rnd(lo, hi + 1)
            try:
                sendto(pack(seq, now_us()) + pad[:ln - HDR_LEN], dst)
            except OSError:
                pass          # ENOBUFS on a full ring is the thing under test
            seq += 1

    fin = pack(FIN, 0) + pad[:2]
    for _ in range(8):
        try:
            s.sendto(fin, dst)
        except OSError:
            pass
        sleep(0.002)
    s.close()
    sys.stderr.write("gamegen send: %d packets over %d ticks\n" % (seq, ticks))


def pct(sorted_vals, q):
    if not sorted_vals:
        return None
    i = min(len(sorted_vals) - 1, int(q * (len(sorted_vals) - 1)))
    return sorted_vals[i]


def do_analyze(a):
    meta, (seqs, txs, rxs) = load(a.inp)
    out = {"gg_received": len(seqs), "gg_rcvbuf_drops": meta.get("rcvbuf_drops")}
    if not seqs:
        out["gg_status"] = "no_packets"
        return emit(out, a.fragment)
    out["gg_status"] = "ok"

    # One-way delay. Same host, same CLOCK_MONOTONIC, so this is one way.
    owd = sorted((rxs[i] - txs[i]) % WRAP for i in range(len(seqs)))
    for k, q in (("p50", .5), ("p99", .99), ("p999", .999)):
        out["gg_owd_%s_ms" % k] = round(pct(owd, q) / 1000.0, 3)
    out["gg_owd_max_ms"] = round(owd[-1] / 1000.0, 3)

    hi = max(seqs)
    out["gg_sent_est"] = hi + 1
    out["gg_lost"] = hi + 1 - len(set(seqs))
    out["gg_loss_pct"] = round(100.0 * out["gg_lost"] / (hi + 1), 3)

    # RFC 4737 §3: non-reversing NextExp. Identical to iperf2's criterion
    # (Reporter.c:1030), so the two can corroborate each other.
    nxt = 0
    reordered = 0
    for sq in seqs:
        if sq < nxt:
            reordered += 1
        elif sq >= nxt:
            nxt = sq + 1
    out["gg_reordered"] = reordered
    out["gg_reordered_pct"] = round(100.0 * reordered / len(seqs), 3)

    # The stall window. A RakNet ordered channel buffers early packets on a
    # per-channel min-heap and delivers nothing past a gap until it fills
    # (ReliabilityLayer.cpp:1281-1440, "Return off heap until order lost").
    # So the player-visible freeze is: from the arrival of the first packet
    # that could not be delivered, to the arrival of the one that unblocks it.
    #
    # Bounded by an assumed retransmit. Without it a single permanently lost
    # packet makes the entire remainder of the run one enormous stall, which
    # is not what a player sees: RakNet resends after RTT*2 clamped to
    # [100, 1000] ms (CCRakNetUDT.cpp:371). The cap is on the row, because a
    # stall reported as exactly rto_ms is this model's ceiling, not a
    # measurement.
    rto = a.rto_ms * 1000
    import heapq
    heap = []
    nxt = 0
    blocked_at = None
    stalls, capped = [], 0
    for i in range(len(seqs)):
        sq, rx = seqs[i], rxs[i]

        if heap and blocked_at is not None and (rx - blocked_at) % WRAP > rto:
            # Give up on the hole the way a resend would: deliver everything
            # the heap is holding and start clean. Advancing nxt without
            # DRAINING left the heap permanently non-empty, which made every
            # later stall look like a continuation of this one -- 0.3%
            # reordering scored 4 stalls where 0.05% scored 105.
            stalls.append(rto)
            capped += 1
            while heap:
                got = heapq.heappop(heap)
                if got >= nxt:
                    nxt = got + 1
            blocked_at = None

        if sq < nxt:
            continue                      # already delivered past this one
        if sq == nxt:
            nxt += 1
            # Drain the run this arrival unblocked. `<= nxt` rather than
            # `== nxt` so duplicates already covered are discarded too.
            while heap and heap[0] <= nxt:
                if heapq.heappop(heap) == nxt:
                    nxt += 1
            if not heap and blocked_at is not None:
                stalls.append((rx - blocked_at) % WRAP)
                blocked_at = None
        else:
            if not heap:
                blocked_at = rx           # first packet held back
            heapq.heappush(heap, sq)

    censored = 0
    if heap and blocked_at is not None:
        # Still blocked when the capture ended. The observed duration is a
        # lower bound only -- reporting it would turn an unfilled hole into a
        # tiny stall, which is the opposite of the truth. Use the model's
        # answer, the same one the mid-loop cap uses, and count it separately
        # so a reader can discount it.
        stalls.append(rto)
        capped += 1
        censored = 1

    span_us = (rxs[-1] - rxs[0]) % WRAP or 1
    out["gg_stalls"] = len(stalls)
    out["gg_stalls_rto_capped"] = capped
    out["gg_stalls_censored"] = censored
    out["gg_stall_rto_ms"] = a.rto_ms
    if stalls:
        ss = sorted(stalls)
        out["gg_stall_p50_ms"] = round(pct(ss, .5) / 1000.0, 2)
        out["gg_stall_p99_ms"] = round(pct(ss, .99) / 1000.0, 2)
        out["gg_stall_max_ms"] = round(ss[-1] / 1000.0, 2)
        out["gg_stall_total_ms"] = round(sum(ss) / 1000.0, 1)
        out["gg_stall_time_pct"] = round(100.0 * sum(ss) / span_us, 2)
        out["gg_stalls_per_min"] = round(60e6 * len(ss) / span_us, 1)
    out["gg_stall_note"] = (
        "duration an ordered channel would hold deliverable packets waiting "
        "for a gap; RakNet min-heap semantics, resend assumed at %d ms"
        % a.rto_ms)
    emit(out, a.fragment)


def emit(d, fragment):
    if fragment:
        print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                             for k, v in d.items()))
    else:
        print(json.dumps(d, indent=2))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)

    r = sub.add_parser("recv")
    r.add_argument("--bind", required=True)
    r.add_argument("--out", required=True)
    r.add_argument("--secs", type=float, default=60.0)
    r.add_argument("--idle", type=float, default=3.0)
    r.set_defaults(fn=do_recv)

    s = sub.add_parser("send")
    s.add_argument("--to", required=True)
    s.add_argument("--secs", type=float, default=20.0)
    s.add_argument("--tick-hz", type=float, default=10.0)
    s.add_argument("--pps", type=float, default=17067)
    s.add_argument("--len", default="10:30")
    s.add_argument("--burst-mult", type=float, default=2.0)
    s.add_argument("--burst-every", type=int, default=10)
    s.add_argument("--seed", type=int, default=1)
    s.set_defaults(fn=do_send)

    z = sub.add_parser("analyze")
    z.add_argument("--in", dest="inp", required=True)
    z.add_argument("--rto-ms", type=float, default=410.0)
    z.add_argument("--fragment", action="store_true")
    z.set_defaults(fn=do_analyze)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
