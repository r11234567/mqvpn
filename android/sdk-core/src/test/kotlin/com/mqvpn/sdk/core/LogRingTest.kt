// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.sdk.core

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LogRingTest {

    private fun ring(capacity: Int = 4): LogRing {
        var now = 1_000L
        return LogRing(capacity = capacity, clock = { now++ })
    }

    @Test
    fun `snapshot is oldest-first`() {
        val ring = ring()
        ring.append(1, "first")
        ring.append(1, "second")

        assertEquals(listOf("first", "second"), ring.snapshot().map { it.message })
    }

    @Test
    fun `sequence numbers and timestamps come from the ring`() {
        val ring = ring()
        ring.append(3, "a")
        ring.append(0, "b")

        val lines = ring.snapshot()
        assertEquals(listOf(1L, 2L), lines.map { it.seq })
        assertEquals(listOf(1_000L, 1_001L), lines.map { it.timeMs })
        assertEquals(listOf(3, 0), lines.map { it.level })
    }

    @Test
    fun `oldest lines are evicted once capacity is reached`() {
        val ring = ring(capacity = 3)
        repeat(5) { i -> ring.append(1, "line $i") }

        assertEquals(listOf("line 2", "line 3", "line 4"), ring.snapshot().map { it.message })
    }

    @Test
    fun `dropped counts evicted lines so truncation is visible`() {
        val ring = ring(capacity = 3)
        assertEquals(0L, ring.dropped())

        repeat(3) { ring.append(1, "fits") }
        assertEquals(0L, ring.dropped())

        repeat(4) { ring.append(1, "overflows") }
        assertEquals(4L, ring.dropped())
    }

    @Test
    fun `sequence numbers keep rising across eviction`() {
        // The seq is what a viewer uses as a stable list key, so it must not
        // restart when the ring wraps.
        val ring = ring(capacity = 2)
        repeat(5) { ring.append(1, "x") }

        assertEquals(listOf(4L, 5L), ring.snapshot().map { it.seq })
    }

    @Test
    fun `clear empties the ring and resets the dropped count`() {
        val ring = ring(capacity = 2)
        repeat(5) { ring.append(1, "x") }
        ring.clear()

        assertTrue(ring.snapshot().isEmpty())
        assertEquals(0L, ring.dropped())
    }

    @Test
    fun `snapshot is a copy, not a live view`() {
        val ring = ring()
        ring.append(1, "before")
        val taken = ring.snapshot()
        ring.append(1, "after")

        assertEquals(listOf("before"), taken.map { it.message })
    }

    @Test
    fun `concurrent appends neither lose lines nor corrupt the ring`() {
        // The engine appends from the executor thread while the viewer
        // snapshots from the UI thread, so both have to be safe at once.
        val capacity = 64
        val ring = LogRing(capacity = capacity, clock = { 0L })
        val writers = 4
        val perWriter = 500
        val pool = Executors.newFixedThreadPool(writers + 1)
        val start = CountDownLatch(1)
        val done = CountDownLatch(writers)

        repeat(writers) { w ->
            pool.execute {
                start.await()
                repeat(perWriter) { i -> ring.append(1, "w$w-$i") }
                done.countDown()
            }
        }
        // A reader running throughout: a snapshot taken mid-eviction must not
        // throw or observe a half-updated ring.
        val readerStop = AtomicBoolean(false)
        val readerFailure = AtomicReference<Throwable?>(null)
        pool.execute {
            start.await()
            try {
                while (!readerStop.get()) {
                    assertTrue(ring.snapshot().size <= capacity)
                }
            } catch (t: Throwable) {
                readerFailure.set(t)
            }
        }

        start.countDown()
        assertTrue("writers did not finish", done.await(20, TimeUnit.SECONDS))
        readerStop.set(true)
        pool.shutdown()
        assertTrue(pool.awaitTermination(20, TimeUnit.SECONDS))

        assertNull(readerFailure.get())
        val total = (writers * perWriter).toLong()
        val lines = ring.snapshot()
        assertEquals(capacity.toLong(), lines.size.toLong())
        assertEquals(total - capacity, ring.dropped())
        // Every append got a distinct sequence number.
        assertEquals(lines.size.toLong(), lines.map { it.seq }.distinct().size.toLong())
        assertEquals(total, lines.maxOf { it.seq })
    }

    @Test
    fun `level tags match the native log levels`() {
        assertEquals("D", mqvpnLogLevelTag(0))
        assertEquals("I", mqvpnLogLevelTag(1))
        assertEquals("W", mqvpnLogLevelTag(2))
        assertEquals("E", mqvpnLogLevelTag(3))
        assertEquals("?", mqvpnLogLevelTag(7))
    }
}
