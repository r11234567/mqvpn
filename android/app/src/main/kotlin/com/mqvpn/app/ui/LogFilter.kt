// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.app.ui

import com.mqvpn.sdk.core.MqvpnLogLine
import com.mqvpn.sdk.core.mqvpnLogLevelTag

/*
 * Filtering and text export for the log viewer, kept free of any
 * Android/Compose dependency so the rules can be exercised in isolation — the
 * same split as EventLog and the dashboard rows that render it.
 */

/**
 * Lines at or above [minLevel] whose message contains [query], case
 * insensitively. Order is preserved — oldest-first, as the ring hands it over —
 * so the same list can be exported and, reversed, displayed newest-first.
 */
internal fun filterLogLines(
    lines: List<MqvpnLogLine>,
    minLevel: Int,
    query: String,
): List<MqvpnLogLine> {
    val needle = query.trim()
    if (minLevel <= 0 && needle.isEmpty()) return lines
    return lines.filter { line ->
        line.level >= minLevel &&
            (needle.isEmpty() || line.message.contains(needle, ignoreCase = true))
    }
}

/**
 * Plain-text rendering for the clipboard, oldest-first — the order a log is
 * read in.
 *
 * [dropped] is stated in the header rather than left out: a log that silently
 * begins mid-story reads like the whole story, and the missing prefix is often
 * where the first failure was.
 */
internal fun formatLogForExport(
    lines: List<MqvpnLogLine>,
    dropped: Long,
    filterNote: String,
    formatTime: (Long) -> String,
): String = buildString {
    append("mqvpn log: ")
    append(lines.size)
    append(if (lines.size == 1) " line" else " lines")
    if (filterNote.isNotEmpty()) {
        append(" (")
        append(filterNote)
        append(")")
    }
    if (dropped > 0) {
        append(", ")
        append(dropped)
        append(" earlier lines dropped")
    }
    append('\n')
    for (line in lines) {
        append(formatTime(line.timeMs))
        append(' ')
        append(mqvpnLogLevelTag(line.level))
        append(' ')
        append(line.message)
        append('\n')
    }
}
