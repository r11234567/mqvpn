// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.sdk.core

/** One line as libmqvpn emitted it. [level] is the native `mqvpn_log_level_t`. */
data class MqvpnLogLine(
    val seq: Long,
    val timeMs: Long,
    val level: Int,
    val message: String,
)

/** Single-letter level tag for compact log rendering; `?` for an unknown level. */
fun mqvpnLogLevelTag(level: Int): String = when (level) {
    0 -> "D"
    1 -> "I"
    2 -> "W"
    3 -> "E"
    else -> "?"
}

/**
 * Bounded, thread-safe newest-wins ring of log lines.
 *
 * No Android dependency, so the eviction and filtering rules are exercised
 * directly in JVM unit tests.
 */
open class LogRing(
    private val capacity: Int = DEFAULT_CAPACITY,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val lock = Any()
    private val lines = ArrayDeque<MqvpnLogLine>()
    private var nextSeq = 1L
    private var droppedCount = 0L

    /**
     * Append one line. Called from the executor thread that runs the engine,
     * so this must not block on anything but the ring's own lock.
     */
    fun append(level: Int, message: String) {
        synchronized(lock) {
            lines.addLast(MqvpnLogLine(nextSeq++, clock(), level, message))
            while (lines.size > capacity) {
                lines.removeFirst()
                droppedCount++
            }
        }
    }

    /** Oldest-first copy of the ring. Oldest-first because that is the order a
     * log is read and exported in; the viewer reverses it for display. */
    fun snapshot(): List<MqvpnLogLine> = synchronized(lock) { lines.toList() }

    /**
     * Lines evicted since the last [clear], so a viewer can say that history
     * was lost instead of quietly showing a truncated log as if it were whole.
     */
    fun dropped(): Long = synchronized(lock) { droppedCount }

    fun clear() {
        synchronized(lock) {
            lines.clear()
            droppedCount = 0
        }
    }

    companion object {
        /**
         * Enough to hold a full connect attempt and a few reconnects at `info`.
         * At `debug` the engine outruns any plausible buffer, which is what
         * [dropped] is for.
         */
        const val DEFAULT_CAPACITY = 3000
    }
}

/**
 * Process-wide sink for libmqvpn's log callback.
 *
 * Deliberately a singleton rather than state on [MqvpnManager]. The lines that
 * matter most — certificate rejection, path setup, the first handshake — are
 * emitted while `startTunnel` is still running on the executor thread, which
 * can be before the Activity's `bindService` has completed and before any
 * observer exists. A sink that only existed once something was listening would
 * miss exactly the failure being diagnosed. Matches the single-client-per-
 * process model the JNI layer already assumes.
 *
 * Written by [MqvpnVpnService.onNativeLog]; read by whatever wants to show it.
 */
object MqvpnLogBuffer : LogRing()
