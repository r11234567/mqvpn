# perf-dashboard

A read-only Cloudflare Worker that charts the CI benchmark history the perf
workflows publish to R2.

It reads two prefixes in one bucket, written by
`scripts/ci_publish/publish_to_r2.sh`:

| Prefix | Written by | Contents |
|---|---|---|
| `perf-data/` | `.github/workflows/perf.yml` | one set of results per push to `main` |
| `perf-data/weekly/` | `.github/workflows/perf-weekly.yml` | the full suite, Sundays |

Each prefix holds immutable `<test>_<timestamp>.json` result documents plus an
`index.json` — a newest-first array of `{commit, timestamp, type, files[]}`,
capped at 100 entries.

## What it shows

Pick a source and a benchmark; the Worker flattens every number in that
benchmark's documents to a dotted path (`results[streams=8].multipath_mbps`)
and charts each one across runs. There is no per-benchmark schema in the code,
so a benchmark that grows a field starts charting it with no change here.

Because these numbers come from shared GitHub-hosted vCPUs, each card also
shows a **CV** (coefficient of variation) over the window. Read it before
reading anything into a movement: a metric with CV 25 % has not told you
anything by moving 10 %. Sort by *noisiest* to see which metrics are worth
trusting at all, and by *biggest move* to see what changed.

Deltas are shown without a good/bad colour on purpose. Direction is not
interpretable across metrics — higher `_mbps` is better, lower `ttf_sec` is
better — and colouring a throughput drop green would be worse than no colour.

## Read-only

The Worker rejects every method except GET and HEAD, and the only R2 call in
the source is `.get()`. There is no code path that writes or deletes, so
deploying it cannot damage benchmark history.

## Deploy

You need the Cloudflare account that owns the bucket. The Worker itself needs
**no credentials**: the R2 binding grants access directly, so there is no key
to leak or rotate.

1. **Create the bucket** — Cloudflare dashboard → R2 → *Create bucket*, e.g.
   `mqvpn-perf`. Leave public access **disabled**; this Worker is the reader.

2. **Point the config at it** — in `wrangler.toml`, set `bucket_name` to the
   name you just used. Leave `binding = "PERF_BUCKET"` alone; the code looks
   that name up.

3. **Deploy**

   ```bash
   cd tools/perf-dashboard
   npm install
   npx wrangler login
   npx wrangler deploy
   ```

   Wrangler prints the `*.workers.dev` URL. `npx wrangler dev` runs it locally
   against the real bucket if you want to look first.

### Bindings and variables

| Kind | Name | Value | Where |
|---|---|---|---|
| R2 bucket binding | `PERF_BUCKET` | your bucket | `wrangler.toml` |
| Var | `PUSH_PREFIX` | `perf-data` | `wrangler.toml` |
| Var | `WEEKLY_PREFIX` | `perf-data/weekly` | `wrangler.toml` |
| Var | `DEFAULT_LIMIT` | `40` | `wrangler.toml` |
| Secret *(optional)* | `VIEW_TOKEN` | any string | `wrangler secret put` |

The two prefixes must match the arguments the workflows pass to
`publish_to_r2.sh`. If you change one, change both.

### Restricting access

Unset, `VIEW_TOKEN` leaves the dashboard public — fine for a public repo whose
commit SHAs are already visible. To gate it:

```bash
npx wrangler secret put VIEW_TOKEN
```

Then open `https://<worker>/?token=<value>` once. The Worker moves the token
into an `HttpOnly` cookie and redirects, so it stops appearing in the address
bar, browser history and `Referer` headers. `Authorization: Bearer <value>`
also works for scripted access.

For real access control (SSO, per-user audit) put Cloudflare Access in front of
the Worker instead; the token is a convenience, not an authorization system.

## GitHub configuration

Add four **repository secrets** (Settings → Secrets and variables → Actions →
*New repository secret*). Nothing else — no variables, no environments.

| Secret | Where to get it |
|---|---|
| `R2_ACCOUNT_ID` | Cloudflare dashboard → R2 — the account ID shown on the page |
| `R2_ACCESS_KEY_ID` | R2 → *Manage R2 API Tokens* → create a token |
| `R2_SECRET_ACCESS_KEY` | shown once when that token is created |
| `R2_BUCKET` | the bucket name, e.g. `mqvpn-perf` |

Give the API token **Object Read & Write**, scoped to just this bucket — the
publisher uploads results, rewrites `index.json`, and deletes files that age
out of the 100-run window.

Until those secrets exist the workflows still run and still attach results to
the run as an artifact; the publish step skips itself with a notice rather than
failing the run.

## Endpoints

| Path | Purpose |
|---|---|
| `/` | the dashboard |
| `/api/tests?channel=push` | benchmarks present, with run counts |
| `/api/series?channel=push&test=<id>&limit=40` | aligned series for every metric of one benchmark |
| `/api/raw/<file>.json?channel=push` | one raw result document |
| `/healthz` | liveness plus whether the R2 binding resolved |

`/api/tests` and `/api/series` are edge-cached for 60 s, matching the
`Cache-Control` the publisher sets on `index.json`.

## Tests

```bash
npm test
```

Exercises the flattening, alignment and run-selection logic against the real
benchmark documents committed under `bench_results/`, so a benchmark that
changes shape fails here instead of quietly producing an empty chart.
