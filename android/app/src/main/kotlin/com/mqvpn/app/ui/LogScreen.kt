// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.app.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.widget.Toast
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.core.content.getSystemService
import com.mqvpn.sdk.core.MqvpnLogBuffer
import com.mqvpn.sdk.core.MqvpnLogLine
import com.mqvpn.sdk.core.mqvpnLogLevelTag
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.delay

/** How often the viewer re-reads the ring while running. */
private const val REFRESH_MS = 400L

/** Level filter options, as (minimum level, label) pairs. */
private val LEVEL_FILTERS = listOf(
    0 to "All",
    1 to "Info",
    2 to "Warn",
    3 to "Error",
)

private data class LogSnapshot(val lines: List<MqvpnLogLine>, val dropped: Long)

/**
 * libmqvpn's log, on the device.
 *
 * The engine's log is the only place several failures are ever named — a
 * rejected certificate says why only here — and on a phone there is no console
 * to read it from. So it is polled out of [MqvpnLogBuffer] rather than pushed:
 * at `debug` the engine emits far faster than a screen can render, and a
 * fixed-interval read costs the same whatever the log rate.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LogScreen(onNavigateUp: () -> Unit) {
    val context = LocalContext.current

    var minLevel by rememberSaveable { mutableIntStateOf(0) }
    var query by rememberSaveable { mutableStateOf("") }
    var paused by rememberSaveable { mutableStateOf(false) }
    // Bumped to force one immediate re-read, so Clear takes effect even while
    // polling is paused.
    var refreshKey by rememberSaveable { mutableIntStateOf(0) }

    val initial = remember { LogSnapshot(MqvpnLogBuffer.snapshot(), MqvpnLogBuffer.dropped()) }
    val snapshot by produceState(initial, paused, refreshKey) {
        value = LogSnapshot(MqvpnLogBuffer.snapshot(), MqvpnLogBuffer.dropped())
        while (!paused) {
            delay(REFRESH_MS)
            value = LogSnapshot(MqvpnLogBuffer.snapshot(), MqvpnLogBuffer.dropped())
        }
    }

    val timeFormat = remember { SimpleDateFormat("HH:mm:ss.SSS", Locale.US) }
    val formatTime = remember(timeFormat) {
        { millis: Long -> timeFormat.format(Date(millis)) }
    }

    val filtered = filterLogLines(snapshot.lines, minLevel, query)

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Logs") },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(onClick = { paused = !paused }) {
                        Icon(
                            if (paused) Icons.Filled.PlayArrow else Icons.Filled.Pause,
                            contentDescription = if (paused) "Resume" else "Pause",
                        )
                    }
                    IconButton(
                        onClick = {
                            copyToClipboard(
                                context = context,
                                text = formatLogForExport(
                                    lines = filtered,
                                    dropped = snapshot.dropped,
                                    filterNote = filterNote(minLevel, query),
                                    formatTime = formatTime,
                                ),
                                lineCount = filtered.size,
                            )
                        },
                        enabled = filtered.isNotEmpty(),
                    ) {
                        Icon(Icons.Filled.ContentCopy, contentDescription = "Copy")
                    }
                    IconButton(
                        onClick = {
                            MqvpnLogBuffer.clear()
                            refreshKey++
                        },
                        enabled = snapshot.lines.isNotEmpty(),
                    ) {
                        Icon(Icons.Filled.DeleteSweep, contentDescription = "Clear")
                    }
                },
            )
        },
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .padding(horizontal = 12.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                LEVEL_FILTERS.forEach { (level, label) ->
                    FilterChip(
                        selected = minLevel == level,
                        onClick = { minLevel = level },
                        label = { Text(label) },
                    )
                }
            }

            Spacer(modifier = Modifier.height(4.dp))
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                label = { Text("Filter") },
                placeholder = { Text("e.g. certificate, path, TUN") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
            )

            Spacer(modifier = Modifier.height(4.dp))
            Text(
                statusLine(
                    shown = filtered.size,
                    total = snapshot.lines.size,
                    dropped = snapshot.dropped,
                    paused = paused,
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(modifier = Modifier.height(4.dp))

            if (snapshot.lines.isEmpty()) {
                Text(
                    "No log lines yet. Connect once to fill the log; " +
                        "raise Log Level to Debug in Settings for more detail.",
                    style = MaterialTheme.typography.bodySmall,
                )
                return@Column
            }
            if (filtered.isEmpty()) {
                Text(
                    "No lines match this filter.",
                    style = MaterialTheme.typography.bodySmall,
                )
                return@Column
            }

            // Newest-first, which needs no scroll management to keep the
            // interesting end in view. Keyed by seq so rows are stable as the
            // ring grows and evicts underneath.
            SelectionContainer(modifier = Modifier.weight(1f)) {
                LazyColumn(modifier = Modifier.fillMaxWidth()) {
                    items(filtered.asReversed(), key = { it.seq }) { line ->
                        LogRow(line, formatTime)
                    }
                }
            }
        }
    }
}

@Composable
private fun LogRow(line: MqvpnLogLine, formatTime: (Long) -> String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 1.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Text(
            "${formatTime(line.timeMs)} ${mqvpnLogLevelTag(line.level)}",
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            color = levelColor(line.level),
        )
        Text(
            " ${line.message}",
            style = MaterialTheme.typography.bodySmall,
            fontFamily = FontFamily.Monospace,
            color = levelColor(line.level),
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun levelColor(level: Int): Color = when (level) {
    3 -> MaterialTheme.colorScheme.error
    2 -> WarningColor
    0 -> MaterialTheme.colorScheme.onSurfaceVariant
    else -> MaterialTheme.colorScheme.onSurface
}

private fun statusLine(shown: Int, total: Int, dropped: Long, paused: Boolean): String {
    val parts = mutableListOf<String>()
    parts += if (shown == total) "$total lines" else "$shown of $total lines"
    if (dropped > 0) parts += "$dropped dropped"
    if (paused) parts += "paused"
    return parts.joinToString(" | ")
}

/** Describes the active filter for the exported header; empty when unfiltered. */
private fun filterNote(minLevel: Int, query: String): String {
    val parts = mutableListOf<String>()
    if (minLevel > 0) {
        parts += "level >= " + mqvpnLogLevelTag(minLevel)
    }
    if (query.isNotBlank()) parts += "matching \"${query.trim()}\""
    return parts.joinToString(", ")
}

private fun copyToClipboard(context: Context, text: String, lineCount: Int) {
    val clipboard = context.getSystemService<ClipboardManager>()
    if (clipboard == null) {
        Toast.makeText(context, "Clipboard unavailable", Toast.LENGTH_SHORT).show()
        return
    }
    clipboard.setPrimaryClip(ClipData.newPlainText("mqvpn log", text))
    // Android 13+ posts its own copy confirmation; a second one is noise.
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
        Toast.makeText(context, "Copied $lineCount lines", Toast.LENGTH_SHORT).show()
    }
}
