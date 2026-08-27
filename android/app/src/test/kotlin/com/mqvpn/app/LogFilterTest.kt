// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.app

import com.mqvpn.app.ui.filterLogLines
import com.mqvpn.app.ui.formatLogForExport
import com.mqvpn.sdk.core.MqvpnLogLine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/** Unit tests for the log viewer's level/text filter and clipboard export. */
class LogFilterTest {

    private val lines = listOf(
        MqvpnLogLine(seq = 1, timeMs = 1_000, level = 0, message = "path[0] probing"),
        MqvpnLogLine(seq = 2, timeMs = 2_000, level = 1, message = "TUN mqvpn0 addr set"),
        MqvpnLogLine(seq = 3, timeMs = 3_000, level = 2, message = "path WLAN degraded"),
        MqvpnLogLine(
            seq = 4,
            timeMs = 4_000,
            level = 3,
            message = "TLS certificate verification failed for '203.0.113.5'",
        ),
    )

    // -- level filter ---------------------------------------------------------

    @Test
    fun `level zero with no query returns the original list untouched`() {
        // Identity, not a copy: the common case must not walk the whole ring.
        assertSame(lines, filterLogLines(lines, minLevel = 0, query = ""))
    }

    @Test
    fun `minLevel keeps that level and above`() {
        assertEquals(listOf(3L, 4L), filterLogLines(lines, 2, "").map { it.seq })
        assertEquals(listOf(4L), filterLogLines(lines, 3, "").map { it.seq })
    }

    @Test
    fun `minLevel above every line yields nothing`() {
        assertTrue(filterLogLines(lines, 9, "").isEmpty())
    }

    // -- text filter ----------------------------------------------------------

    @Test
    fun `query matches a substring case-insensitively`() {
        assertEquals(listOf(4L), filterLogLines(lines, 0, "CERTIFICATE").map { it.seq })
        assertEquals(listOf(1L, 3L), filterLogLines(lines, 0, "path").map { it.seq })
    }

    @Test
    fun `whitespace-only query is not a filter`() {
        assertEquals(lines.size, filterLogLines(lines, 0, "   ").size)
    }

    @Test
    fun `query is trimmed before matching`() {
        assertEquals(listOf(2L), filterLogLines(lines, 0, "  TUN  ").map { it.seq })
    }

    @Test
    fun `level and query both apply`() {
        assertEquals(listOf(3L), filterLogLines(lines, 2, "path").map { it.seq })
        assertTrue(filterLogLines(lines, 3, "path").isEmpty())
    }

    @Test
    fun `filter preserves oldest-first order`() {
        // "a" occurs in all four messages, so the result is the whole log and
        // its order is the only thing under test.
        assertEquals(listOf(1L, 2L, 3L, 4L), filterLogLines(lines, 0, "a").map { it.seq })
    }

    // -- export ---------------------------------------------------------------

    private fun export(
        subset: List<MqvpnLogLine> = lines,
        dropped: Long = 0,
        filterNote: String = "",
    ) = formatLogForExport(subset, dropped, filterNote) { "t$it" }

    @Test
    fun `export writes a header then one line per entry, oldest first`() {
        val text = export(subset = lines.take(2))
        assertEquals(
            listOf(
                "mqvpn log: 2 lines",
                "t1000 D path[0] probing",
                "t2000 I TUN mqvpn0 addr set",
            ),
            text.trimEnd('\n').split("\n"),
        )
    }

    @Test
    fun `export names the dropped lines so truncation is not silent`() {
        val text = export(subset = lines.take(1), dropped = 42)
        assertTrue(text, text.startsWith("mqvpn log: 1 line, 42 earlier lines dropped\n"))
    }

    @Test
    fun `export records the active filter so a partial log is not mistaken for the whole`() {
        val text = export(subset = lines.take(1), filterNote = "level >= W")
        assertTrue(text, text.startsWith("mqvpn log: 1 line (level >= W)\n"))
    }

    @Test
    fun `export of an empty selection is just the header`() {
        assertEquals("mqvpn log: 0 lines\n", export(subset = emptyList()))
    }
}
