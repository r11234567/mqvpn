// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors
//
// Offline check for the dashboard's data plumbing:  node test/check.mjs
//
// Runs against the real benchmark documents committed under bench_results/,
// so a benchmark that changes shape breaks this rather than silently
// producing an empty chart. Only src/metrics.js is covered — src/index.js
// imports dashboard.html as a text module and needs the wrangler bundler.

import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

import { buildSeries, flattenNumbers, flattenStrings, pickRuns, testIdFromFile } from '../src/metrics.js';

const here = dirname(fileURLToPath(import.meta.url));
const samples = join(here, '..', '..', '..', 'bench_results');

let passed = 0;
function check(name, fn) {
  try {
    fn();
    passed++;
    console.log(`  ok    ${name}`);
  } catch (err) {
    console.error(`  FAIL  ${name}\n        ${err.message}`);
    process.exitCode = 1;
  }
}

console.log('testIdFromFile');
check('CI filename -> bare test id', () => {
  assert.equal(testIdFromFile('aggregate_20260823_080000.json'), 'aggregate');
});
check('underscores in the id survive', () => {
  assert.equal(testIdFromFile('raw_throughput_20260823_080000.json'), 'raw_throughput');
  assert.equal(testIdFromFile('multipath_scheduler_20260823_080000.json'), 'multipath_scheduler');
});
check('local _netns variant folds onto the same id', () => {
  assert.equal(testIdFromFile('failover_netns_20260222_184036.json'), 'failover');
});
check('unexpected name degrades to the stem, not a crash', () => {
  assert.equal(testIdFromFile('weird.json'), 'weird');
});
check('a key path cannot escape via the filename', () => {
  // apiRaw rejects these separately; this pins that the id itself is inert.
  assert.equal(testIdFromFile('../../etc/passwd'), 'passwd');
});

console.log('flattenNumbers against the committed sample documents');
const parsed = {};
for (const f of readdirSync(samples).filter((f) => f.endsWith('.json'))) {
  parsed[testIdFromFile(f)] = JSON.parse(readFileSync(join(samples, f), 'utf8'));
}

check('samples were found', () => {
  assert.ok(Object.keys(parsed).length >= 3, `only found: ${Object.keys(parsed)}`);
});

check('aggregate: array entries are labelled by stream count, not index', () => {
  const flat = flattenNumbers(parsed.aggregate);
  assert.ok('results[streams=8].multipath_mbps' in flat, 'missing streams-keyed path');
  assert.equal(flat['theoretical_max_mbps'], 380);
  assert.equal(flat['netem.path_a.delay_ms'], 10);
});

check('failover: nested interval series flattens', () => {
  const flat = flattenNumbers(parsed.failover);
  assert.ok(Object.keys(flat).some((k) => k.startsWith('intervals[time_sec=')), 'no interval keys');
  assert.equal(flat['duration_sec'], 60);
});

check('udp_sweep: two levels of arrays both get labelled', () => {
  const flat = flattenNumbers(parsed.udp_sweep);
  const key = Object.keys(flat).find((k) => k.includes('sweeps[') && k.includes('throughput_mbps'));
  assert.ok(key, 'no sweep throughput key');
  assert.ok(key.includes('target_mbps='), `inner array not labelled: ${key}`);
});

check('strings, booleans and nulls are not charted as numbers', () => {
  const flat = flattenNumbers({ a: 1, s: 'x', b: true, n: null, nested: { m: 2 } });
  assert.deepEqual(Object.keys(flat).sort(), ['a', 'nested.m']);
});

check('a cyclic-depth document terminates', () => {
  let deep = { v: 1 };
  for (let i = 0; i < 40; i++) deep = { nest: deep };
  assert.doesNotThrow(() => flattenNumbers(deep));
});

console.log('buildSeries');
check('metrics align by run and missing ones become gaps, not zeros', () => {
  const { metrics, totalMetrics } = buildSeries([
    { a: 1 },
    { a: 2, b: 9 },
    null, // unreadable object
    { a: 4 },
  ]);
  assert.equal(totalMetrics, 2);
  assert.deepEqual(metrics.a, [1, 2, null, 4]);
  assert.deepEqual(metrics.b, [null, 9, null, null]);
});

check('every series has one value per run', () => {
  const docs = [parsed.aggregate, parsed.aggregate, parsed.aggregate];
  const { metrics } = buildSeries(docs);
  for (const [name, values] of Object.entries(metrics)) {
    assert.equal(values.length, docs.length, `${name} has ${values.length} points`);
  }
});

console.log('flattenStrings / scenario context');
check('strings are collected on the same paths the numbers use', () => {
  const flat = flattenStrings({
    results: [{ scenario: 'mtu_split', path_a: 'eth:bgp_plain:public:1500', multipath_mbps: 12 }],
  });
  assert.equal(flat['results[scenario=mtu_split].path_a'], 'eth:bgp_plain:public:1500');
  // The number is not a string and must not appear here.
  assert.ok(!('results[scenario=mtu_split].multipath_mbps' in flat));
});

check('prose-length strings are dropped rather than shown as a label', () => {
  const flat = flattenStrings({ note: 'x'.repeat(200), short: 'ok' });
  assert.ok(!('note' in flat));
  assert.equal(flat.short, 'ok');
});

check('context describes the newest run, not an older one', () => {
  const { context } = buildSeries([
    { results: [{ scenario: 's', path_a: 'old-spec', v: 1 }] },
    { results: [{ scenario: 's', path_a: 'new-spec', v: 2 }] },
  ]);
  assert.equal(context['results[scenario=s].path_a'], 'new-spec');
});

check('a cyclic-depth document terminates in flattenStrings too', () => {
  let deep = { s: 'leaf' };
  for (let i = 0; i < 40; i++) deep = { nested: deep };
  assert.doesNotThrow(() => flattenStrings(deep));
});

console.log('pickRuns');
const index = [
  { commit: 'newest', timestamp: '2026-08-23T00:00:00Z', type: 'push', files: ['aggregate_20260823_000000.json'] },
  { commit: 'mid', timestamp: '2026-08-22T00:00:00Z', type: 'push', files: ['failover_20260822_000000.json'] },
  { commit: 'oldest', timestamp: '2026-08-21T00:00:00Z', type: 'push', files: ['aggregate_20260821_000000.json', 'failover_20260821_000000.json'] },
];

check('output is oldest-first even though the index is newest-first', () => {
  const runs = pickRuns(index, 'aggregate', 10);
  assert.deepEqual(runs.map((r) => r.commit), ['oldest', 'newest']);
});

check('runs without this benchmark are skipped, not left as gaps', () => {
  assert.equal(pickRuns(index, 'aggregate', 10).length, 2);
  assert.equal(pickRuns(index, 'failover', 10).length, 2);
});

check('the limit keeps the NEWEST runs, then still charts them oldest-first', () => {
  const runs = pickRuns(index, 'aggregate', 1);
  assert.deepEqual(runs.map((r) => r.commit), ['newest']);
});

check('an unknown benchmark yields nothing rather than throwing', () => {
  assert.deepEqual(pickRuns(index, 'nope', 10), []);
});

check('a malformed entry does not abort the walk', () => {
  const runs = pickRuns([{ commit: 'bad' }, ...index], 'aggregate', 10);
  assert.deepEqual(runs.map((r) => r.commit), ['oldest', 'newest']);
});

console.log(`\n${passed} checks passed${process.exitCode ? ' (with failures above)' : ''}`);
