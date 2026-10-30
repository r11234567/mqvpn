# Windows GUI

This fork's WPF client uses MQVPN for the VPN and Xray for GUI split routing.
PR builds are testing packages, not a signed stable release. Download the
`mqvpn_windows_gui_x64` or `mqvpn_windows_gui_arm64` ZIP artifact from the
current PR's CI run, extract the entire ZIP, and run `MQVPN.exe` as administrator.
Use the matching native architecture. The GUI and update helper include .NET;
Wintun, Xray, SQLite, licenses and a source/dependency manifest ship in the ZIP.
Native x64/ARM64 package checks run in CI; see [verification](PHASE_B_REVIEW.md)
for the scope actually completed locally.

## Import and connect

1. Prepare the Linux server using the paired server/client example in the
   root [README](../README.md#this-fork-paired-server-and-client-examples). Use the same PSK,
   server address and certificate name in [examples/client.json](examples/client.json).
   The supplied address and PSK are placeholders. The example enables Hybrid,
   so the server must enable it too.
2. In the node list, right-click a node and choose **导入 JSON 为新节点**. Select
   the client JSON, review the editor's settings, then save. The source JSON
   is never modified; cancelling adds no node. **添加配置** creates a new node
   with the same Windows defaults. **编辑** changes the selected node.
3. Choose up to eight physical adapters. Leaving all unchecked selects default
   adapter mode: the GUI resolves the current default physical interface on
   each connection. An empty stored `paths` array is valid in this GUI mode;
   a direct Windows CLI invocation must supply at least one adapter. Adapter
   and TUN names support Unicode and spaces, with a 31-byte UTF-8 limit from
   the core configuration buffers (not 31 Chinese characters).
4. Select the node, choose a routing policy, and connect. A missing/unavailable
   adapter or invalid configuration is an error. The GUI does not invent a
   usable path or silently correct invalid values. The main window's log-level
   selection overrides the node's saved default at launch.

## What the settings control

| Settings | Windows behavior |
| --- | --- |
| Endpoint / SNI / PSK / DNS | `host:port`, IPv4 or `[IPv6]:port`; port 1–65535. Up to four DNS IP addresses. Empty SNI uses the endpoint host for certificate identity. |
| TLS | `insecure` defaults false. Windows normally uses its certificate stores. The core's existing `SSL_CERT_FILE` PEM bundle / `SSL_CERT_DIR` hashed-directory environment overrides are supported. Install a private/self-signed CA into the relevant trust store, or supply the CA bundle; self-signed does not inherently require skipping verification. No GUI CA/PQC/AES switches are invented. |
| GUI global / split routing | Global MQVPN sends traffic through the MQVPN TUN without Xray. Split mode uses Xray TUN and routing rules to choose MQVPN or direct. |
| `manage_routes` | New GUI nodes explicitly use false: GUI routing owns the policy. Imported missing values retain the CLI default true. Core route management cannot be combined with GUI split routing; select global MQVPN or explicitly turn it off. Saving/connecting reports the conflict instead of overriding it. |
| `kill_switch` | Independent core WFP protection against traffic outside the tunnel, including applications bound to a physical interface. It can block split-routing direct traffic. Route management alone does not enable it. Filters belong to the core's dynamic WFP session; this is not a persistent system firewall mode after the core exits. |
| Scheduler / CC / redundancy | WLB/MinRTT/WLB UDP pin/backup FEC are datagram choices. Hybrid STREAM always uses MinRTT. This fork's packaged core enables BBR2, unlimited CC, FEC/XOR and Hybrid. A separately replaced core must have matching build capabilities. Enabling redundancy recommends BBR but does not change CC. UDP copy ports: up to 16; empty means all UDP. |
| Reinjection | `off`, `deadline`, `idle`; factor 100–1000%, deadlines 1–60000 ms, lower ≤ hard deadline. Only deadline mode uses those deadline controls. |
| Path ID / reconnect | `init_max_path_id`: 0 uses default 8, or 1–128. Old larger values remain visible and must be corrected explicitly. Reconnect interval: 1–2147483647 seconds. |
| MTU / PathMTU / PMTUD | 0 means default; nonzero 1280–9000. The client MTU caps negotiation. PMTUD is opt-in, false by default; its use depends on the peer and network. |
| Hybrid | Client controls: enabled, `auto` / `stream` / `raw`, max flows 1–4096, idle timeout 0–4294967295 seconds (0 disables idle reclamation). Server connect timeout, global flow budget and egress ACL are preserved on import but not offered as client tuning. |
| Reorder | Off by default. With rules, matches the specified UDP source/destination ports; GUI rules use unique ports 1–65535, max 16. Zero rules is the core's all-inner-flow mode, including inner TCP; use deliberately. `cellular_bond`/`fiber_lte` are presets, `quic_bulk` aliases cellular; `low_latency` is reserved/inert and `default_udp` passes through. |
| Reorder overrides | Blank per-rule values inherit explicit globals, then presets/defaults. Global wait may be 0; per-rule wait must be positive. Packet caps must be nonzero powers of two. Ingress idle < egress idle. Optional global fields remain absent until explicitly enabled; 64-bit byte limits are entered and saved exactly. |
| Receive limit | `advanced.recv_rate_limit`: 0–10000000000 bytes/s, 0 disables it. This bounds receive-window growth/memory; it is not a precise traffic shaper. |
| Linux-only | Queue length, TUN read batch and UDP/TUN offload controls are not used on Windows. Existing values and other unknown fields survive editing and backup/export. New Windows nodes do not inject them. |

Integer fields accept whole decimal digits, without exponent/fraction/clamping.
The editor rejects wrong JSON types, out-of-range values and incompatible
routing choices; unchanged fields retain their JSON values and presence.
Recognized legacy boolean strings are migrated to real JSON booleans when
saving, and legacy `tun_mtu` is migrated to the core's `mtu` key.

## Updates, backup and reset

Use **检查更新** for the installed architecture. Updates stage and validate
files, stop the affected processes, then replace them with rollback on failure;
they retain user configuration. During this PR, use the complete current CI
package when evaluating changes; release updates may point to a different SHA.

Settings supports ZIP backup/import and factory reset. Reset creates a safety
backup first and replaces application-managed nodes/settings/routes/default
configuration. It does not remove unrelated external files or historical
backups. Restoring a backup is separate from importing one client JSON.

Actual configuration roundtrips are checked through the production editor,
SQLite/files, backup ZIP, runtime-path resolution, C CLI parser and shared
client configuration bridge. These offline checks do not certify live routing,
WFP interaction or network connectivity on a user's machine.
