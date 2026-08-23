// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors
//
// Read-only dashboard over the CI benchmark history in R2.
//
// Data model, as produced by scripts/ci_publish/publish_to_r2.sh:
//
//   <prefix>/index.json          JSON array, NEWEST FIRST, capped at 100:
//                                  [{ commit, timestamp, type, files: [...] }]
//   <prefix>/<test>_<ts>.json    one benchmark result, immutable
//
// Two prefixes are in use: `perf-data` (per-commit) and `perf-data/weekly`.
// The weekly one nests under the push one, which is why this Worker resolves
// files through index.json and never list()s a prefix — a list of `perf-data/`
// would sweep the weekly objects in with the push ones.
//
// Result files have no common schema: raw_throughput keys results by
// direction, aggregate returns an array per stream count, failover nests
// per scheduler and fault. Rather than teach this Worker each shape (and
// re-teach it whenever a benchmark grows a field), it flattens every finite
// number in a document to a dotted path and charts whatever it finds.
//
// Read-only is enforced two ways: non-GET methods are rejected up front, and
// the only R2 call anywhere in this file is .get().

import DASHBOARD_HTML from './dashboard.html';
import { MAX_LIMIT, buildSeries, pickRuns, testIdFromFile } from './metrics.js';

/** Parallel R2 reads per request. */
const FETCH_CONCURRENCY = 12;
/** Matches the Cache-Control the publisher puts on index.json. */
const API_CACHE_SECONDS = 60;

export default {
  async fetch(request, env, ctx) {
    // Read-only at the protocol level, before anything else runs.
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return json({ error: 'This dashboard is read-only.' }, 405, { Allow: 'GET, HEAD' });
    }

    const url = new URL(request.url);
    const denied = checkToken(request, url, env);
    if (denied) return denied;

    try {
      switch (url.pathname) {
        case '/':
          return htmlResponse(DASHBOARD_HTML);
        case '/healthz':
          return json({ ok: true, bucketBound: Boolean(env.PERF_BUCKET) });
        case '/api/tests':
          return await cachedJson(request, ctx, () => apiTests(url, env));
        case '/api/series':
          return await cachedJson(request, ctx, () => apiSeries(url, env));
      }
      if (url.pathname.startsWith('/api/raw/')) return await apiRaw(url, env);
      return json({ error: 'not found' }, 404);
    } catch (err) {
      // A bad query string is the caller's fault; reporting it as 500 would
      // send whoever debugs it looking at the Worker instead of their URL.
      const status = err instanceof HttpError ? err.status : 500;
      return json({ error: err && err.message ? err.message : String(err) }, status);
    }
  },
};

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

/* ── configuration ─────────────────────────────────────────────────────── */

function channelConfig(env) {
  const trim = (s, fallback) => String(s || fallback).replace(/^\/+|\/+$/g, '');
  return {
    push: { prefix: trim(env.PUSH_PREFIX, 'perf-data'), label: 'Per-commit (push to main)' },
    weekly: { prefix: trim(env.WEEKLY_PREFIX, 'perf-data/weekly'), label: 'Weekly full suite' },
  };
}

function resolveChannel(url, env) {
  const cfg = channelConfig(env);
  const name = url.searchParams.get('channel') || 'push';
  if (!cfg[name]) throw new HttpError(400, `unknown channel '${name}' (expected push or weekly)`);
  return { name, ...cfg[name] };
}

function bucket(env) {
  if (!env.PERF_BUCKET) {
    throw new Error('R2 binding PERF_BUCKET is missing — check [[r2_buckets]] in wrangler.toml');
  }
  return env.PERF_BUCKET;
}

/* ── API handlers ──────────────────────────────────────────────────────── */

async function readIndex(env, prefix) {
  const obj = await bucket(env).get(`${prefix}/index.json`);
  if (!obj) return [];
  const data = await obj.json();
  return Array.isArray(data) ? data : [];
}

async function apiTests(url, env) {
  const channel = resolveChannel(url, env);
  const index = await readIndex(env, channel.prefix);

  const counts = new Map();
  for (const entry of index) {
    for (const f of entry.files || []) {
      const id = testIdFromFile(f);
      counts.set(id, (counts.get(id) || 0) + 1);
    }
  }

  return {
    channel: channel.name,
    label: channel.label,
    runs: index.length,
    newest: index[0] || null,
    tests: [...counts.entries()]
      .map(([id, runs]) => ({ id, runs }))
      .sort((a, b) => b.runs - a.runs || a.id.localeCompare(b.id)),
  };
}

async function apiSeries(url, env) {
  const channel = resolveChannel(url, env);
  const test = url.searchParams.get('test');
  if (!test) throw new HttpError(400, 'missing ?test=');

  const requested = Number(url.searchParams.get('limit') || env.DEFAULT_LIMIT || 40);
  const limit = Math.min(MAX_LIMIT, Math.max(1, Number.isFinite(requested) ? requested : 40));

  const index = await readIndex(env, channel.prefix);
  const picked = pickRuns(index, test, limit, channel.name);

  const errors = [];
  const docs = await mapLimit(picked, FETCH_CONCURRENCY, async (run) => {
    try {
      const obj = await bucket(env).get(`${channel.prefix}/${run.file}`);
      if (!obj) {
        errors.push({ file: run.file, error: 'object missing' });
        return null;
      }
      return await obj.json();
    } catch (err) {
      errors.push({ file: run.file, error: String(err && err.message ? err.message : err) });
      return null;
    }
  });

  const { metrics, truncated, totalMetrics } = buildSeries(docs);

  return {
    channel: channel.name,
    test,
    limit,
    runs: picked,
    metrics,
    totalMetrics,
    truncated,
    errors,
  };
}

/**
 * One raw result document, for drilling into a point on a chart.
 * The filename is validated rather than concatenated blindly: a `..` segment
 * would otherwise let a caller read outside the configured prefix.
 */
async function apiRaw(url, env) {
  const channel = resolveChannel(url, env);
  const file = decodeURIComponent(url.pathname.slice('/api/raw/'.length));
  if (!/^[A-Za-z0-9._-]+\.json$/.test(file)) {
    return json({ error: 'bad filename' }, 400);
  }
  const obj = await bucket(env).get(`${channel.prefix}/${file}`);
  if (!obj) return json({ error: 'not found' }, 404);
  return new Response(obj.body, {
    headers: {
      'content-type': 'application/json; charset=utf-8',
      // Result objects are immutable (the filename carries a timestamp).
      'cache-control': 'public, max-age=31536000, immutable',
    },
  });
}

/* ── plumbing ──────────────────────────────────────────────────────────── */

async function mapLimit(items, limit, fn) {
  const out = new Array(items.length);
  let next = 0;
  const worker = async () => {
    for (;;) {
      const i = next++;
      if (i >= items.length) return;
      out[i] = await fn(items[i], i);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return out;
}

/**
 * Edge-cache a JSON endpoint for API_CACHE_SECONDS. Without this, every
 * reload re-reads up to `limit` objects from R2; with it, a burst of viewers
 * costs one set of reads per minute.
 */
async function cachedJson(request, ctx, produce) {
  const cache = caches.default;
  const hit = await cache.match(request);
  if (hit) return hit;

  const response = json(await produce());
  if (request.method === 'GET') {
    ctx.waitUntil(cache.put(request, response.clone()));
  }
  return response;
}

function json(body, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': status === 200 ? `public, max-age=${API_CACHE_SECONDS}` : 'no-store',
      ...extraHeaders,
    },
  });
}

function htmlResponse(body) {
  return new Response(body, {
    headers: {
      'content-type': 'text/html; charset=utf-8',
      'cache-control': 'public, max-age=300',
      'x-content-type-options': 'nosniff',
      'referrer-policy': 'no-referrer',
    },
  });
}

/* ── optional token gate ───────────────────────────────────────────────── */

function cookieValue(request, name) {
  const raw = request.headers.get('cookie') || '';
  for (const part of raw.split(';')) {
    const [k, ...v] = part.trim().split('=');
    if (k === name) return decodeURIComponent(v.join('='));
  }
  return null;
}

function safeEqual(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/**
 * Returns a Response to short-circuit with, or null to continue.
 * VIEW_TOKEN unset => the dashboard is public.
 */
function checkToken(request, url, env) {
  const want = env.VIEW_TOKEN;
  if (!want) return null;

  const queryToken = url.searchParams.get('token');
  const supplied =
    queryToken ||
    cookieValue(request, 'perf_token') ||
    (request.headers.get('authorization') || '').replace(/^Bearer\s+/i, '');

  if (!safeEqual(supplied || '', want)) {
    return new Response('Unauthorized\n', {
      status: 401,
      headers: { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' },
    });
  }

  // Valid token in the URL: move it into a cookie and redirect, so the secret
  // stops travelling in Referer headers, browser history and the address bar.
  if (queryToken) {
    const clean = new URL(url);
    clean.searchParams.delete('token');
    return new Response(null, {
      status: 302,
      headers: {
        Location: clean.pathname + clean.search,
        'Set-Cookie': `perf_token=${encodeURIComponent(want)}; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=31536000`,
        'cache-control': 'no-store',
      },
    });
  }
  return null;
}
