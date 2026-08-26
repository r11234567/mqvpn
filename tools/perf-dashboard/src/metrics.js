// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors
//
// Pure helpers that turn benchmark result documents into chartable series.
// Kept out of index.js so they can be exercised by test/check.mjs under plain
// node: index.js imports dashboard.html as a text module, which only the
// wrangler bundler understands.

/** index.json is capped at 100 entries by the publisher; asking for more is pointless. */
export const MAX_LIMIT = 100;
/** Guard against one pathological document (a sweep with thousands of points) swamping a response. */
export const MAX_METRICS = 400;
/** Same guard for the context strings that ride alongside those metrics. */
export const MAX_CONTEXT = 200;

/**
 * Benchmark id from a result filename: `raw_throughput_20260823_080000.json`
 * -> `raw_throughput`. Only the trailing `_YYYYMMDD_HHMMSS` is stripped, so
 * ids containing underscores survive. The `_netns` suffix that the local
 * (non-CI) harness adds is dropped so both sources land on one series.
 */
export function testIdFromFile(filename) {
  const base = String(filename).replace(/^.*\//, '');
  const m = /^(.+?)_\d{8}_\d{6}\.json$/.exec(base);
  const id = m ? m[1] : base.replace(/\.json$/, '');
  return id.replace(/_netns$/, '');
}

/**
 * Fields worth using to label an element of an array of objects. A bare index
 * (`results[3].multipath_mbps`) says nothing about which datapoint it is;
 * these arrays almost always carry a natural key, so prefer it
 * (`results[streams=8].multipath_mbps`). Order matters — the first match wins.
 */
const ARRAY_KEY_FIELDS = [
  'name', 'scenario', 'condition', 'label', 'direction', 'scheduler',
  'streams', 'target_mbps', 'packet_size', 'loss_percent', 'time_sec',
];

export function arrayLabel(item, index) {
  if (item && typeof item === 'object' && !Array.isArray(item)) {
    for (const k of ARRAY_KEY_FIELDS) {
      const v = item[k];
      if (typeof v === 'string' || typeof v === 'number') return `${k}=${v}`;
    }
  }
  return String(index);
}

/**
 * Collect every finite number in a document, keyed by dotted path.
 *
 * Booleans and nulls are skipped deliberately: neither plots usefully, and
 * coercing a boolean to 0/1 would invent a metric that reads like a
 * measurement. Non-finite numbers (NaN/Infinity, which JSON.parse can produce
 * only via a literal like 1e999) are dropped for the same reason.
 */
export function flattenNumbers(value, prefix = '', out = {}, depth = 0) {
  if (depth > 8) return out;
  if (typeof value === 'number') {
    if (Number.isFinite(value) && prefix) out[prefix] = value;
    return out;
  }
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i++) {
      flattenNumbers(value[i], `${prefix}[${arrayLabel(value[i], i)}]`, out, depth + 1);
    }
    return out;
  }
  if (value && typeof value === 'object') {
    for (const k of Object.keys(value)) {
      flattenNumbers(value[k], prefix ? `${prefix}.${k}` : k, out, depth + 1);
    }
  }
  return out;
}

/**
 * Collect every string in a document, keyed by the same dotted paths
 * flattenNumbers uses.
 *
 * These are not plottable, which is exactly why they matter: they are the
 * emulated profile a number was produced under (`path_a`, `tier_props`,
 * `mode`). A throughput figure read without them is unattributable, so the
 * page shows them beside the value.
 *
 * Long strings are dropped rather than truncated — anything past label length
 * is prose that belongs in the raw document, not on a card.
 */
export function flattenStrings(value, prefix = '', out = {}, depth = 0) {
  if (depth > 8) return out;
  if (typeof value === 'string') {
    if (prefix && value.length <= 120) out[prefix] = value;
    return out;
  }
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i++) {
      flattenStrings(value[i], `${prefix}[${arrayLabel(value[i], i)}]`, out, depth + 1);
    }
    return out;
  }
  if (value && typeof value === 'object') {
    for (const k of Object.keys(value)) {
      flattenStrings(value[k], prefix ? `${prefix}.${k}` : k, out, depth + 1);
    }
  }
  return out;
}

/**
 * Choose which runs to chart, oldest-first.
 *
 * index.json is newest-first, so the newest `limit` matching entries are taken
 * from the front and then reversed — charting them in index order would run
 * time backwards. Entries that never produced this benchmark are skipped
 * entirely rather than becoming gaps: a weekly-only test would otherwise show
 * as mostly-empty in the per-commit channel.
 *
 * `fallbackType` labels entries written before the publisher recorded a type.
 */
export function pickRuns(index, test, limit, fallbackType = null) {
  const picked = [];
  for (const entry of index) {
    const file = (entry.files || []).find((f) => testIdFromFile(f) === test);
    if (!file) continue;
    picked.push({
      commit: entry.commit || null,
      timestamp: entry.timestamp || null,
      type: entry.type || fallbackType,
      file,
    });
    if (picked.length >= limit) break;
  }
  picked.reverse();
  return picked;
}

/**
 * Align a list of parsed documents into one series per metric.
 *
 * `docs` is oldest-first and index-aligned with the run list. A null doc (its
 * object was unreadable) contributes nulls rather than being skipped, so a
 * chart shows a gap at that commit instead of silently closing over it.
 */
export function buildSeries(docs) {
  const flat = docs.map((d) => (d ? flattenNumbers(d) : {}));

  const names = [];
  const seen = new Set();
  // Walk newest-first so a metric introduced by a recent commit still makes
  // the cut when MAX_METRICS trims the list.
  for (let i = flat.length - 1; i >= 0; i--) {
    for (const k of Object.keys(flat[i])) {
      if (!seen.has(k)) {
        seen.add(k);
        names.push(k);
      }
    }
  }
  names.sort();

  const truncated = names.length > MAX_METRICS;
  const kept = truncated ? names.slice(0, MAX_METRICS) : names;

  const metrics = {};
  for (const name of kept) {
    metrics[name] = flat.map((f) => (name in f ? f[name] : null));
  }

  // Context comes from the newest document only. It describes the conditions a
  // run was measured under, and showing the oldest run's emulated profile next
  // to the newest run's number would be worse than showing none.
  const newest = docs.length ? docs[docs.length - 1] : null;
  const allContext = newest ? flattenStrings(newest) : {};
  const context = {};
  for (const k of Object.keys(allContext).slice(0, MAX_CONTEXT)) {
    context[k] = allContext[k];
  }

  return { metrics, context, truncated, totalMetrics: names.length };
}
