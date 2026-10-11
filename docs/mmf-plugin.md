# MyMiniFactory (MMF) plugin

Built-in plugin at `plugins/Forgekeeper.Scraper.Mmf/` (slug `mmf`, v1.1.x). It reads the user's MMF library,
downloads model files into `/library/sources/mmf/<creator>/<model>/`, and writes a `metadata.json` next to each
model. History and design: `mmf-phase0-findings.md` and the wiki page `plans/forgekeeper-mmf-modernization`.

## Pieces
- **Plugin DLL** (`MmfScraperPlugin.cs`) — loaded once with its `manifest.json` by `PluginHostService` (#79).
- **mmf-fetch sidecar** (`sidecars/mmf-fetch`, image `ghcr.io/inxaos-repo/forgekeeper-mmf-fetch`) — Python +
  curl_cffi with browser TLS fingerprints, so Cloudflare doesn't challenge API and file requests. Streams
  downloads with bounded buffers and curl backpressure (#57, #60, #61). Runs in the same pod.
- **Library manifest** — the user's library export (`objectPreviews` JSON) uploaded with
  `POST /api/v1/plugins/mmf/manifest` (multipart field `manifest`). `object-12345` ids are stripped to `12345`.

## Sync flow
1. Build the item list from the manifest/API. Select new-only items by default, apply `MAX_ITEMS`.
2. For each item: resolve per-file download URLs first, then archive URLs (#53). An MMF HTML page is not treated as
   a Cloudflare challenge.
3. Stream the download to a `.part` file on disk, rename on completion, and queue the archive for extraction.
   Downloads wait while `MAX_UNZIP_BACKLOG` (default 2) archives are queued (#74), which keeps memory bounded.
4. Write `metadata.json` including `dates.downloaded` (#76).
5. After the run: post-sync library scan (#55), persistent failed-items list, sync_runs row closed.

Robustness rules (#27 #28 #75 #77): partial-download folders are ignored, orphan `.part` files are swept, an item
with no files is not counted as a success, and stale `running` rows don't resume old checkpoints.

## Settings (`plugin_configs`, slug `mmf`)
| Key | Default | Notes |
|---|---|---|
| `DRY_RUN` | false | Keep `true` in prod unless a real sync is approved. Writes `dryrun-mmf-*` reports only. |
| `MAX_ITEMS` | 0 (no cap) | Per-run download cap; skipped items don't use a slot. |
| `MAX_UNZIP_BACKLOG` | 2 | Extraction queue length before downloads pause. Lower if memory is tight. |
| `RESTORE_MODE` | false | Ignore lastSynced; still skips files already on disk. |
| `SYNC_INTERVAL_HOURS` | unset | Nothing runs on a schedule unless > 0. |
| `REHOME_UNKNOWN_APPLY` | — | Deprecated (#33). Use the rehome endpoints. |
| `__token__*` | — | Auth tokens/cookies. Never print them. |

## Reports
`/library/sources/mmf/.forgekeeper-reports/` (the scanner skips dot-dirs):
`dryrun-mmf-*`, `rehome-unknown-mmf-*`, `rehome-apply-*`, orphan-merge reports. JSON + CSV.

## Related endpoints
- `POST /api/v1/plugins/mmf/sync` (blocks until done — start it detached), `?resume=true` to continue a checkpoint
- `POST /api/v1/plugins/mmf/sync/cancel`, `GET /api/v1/plugins/mmf/status` (`isRunning`), SSE `/progress`
- `POST /api/v1/plugins/mmf/rehome/plan`, `POST /api/v1/plugins/mmf/rehome/apply?report=<file>.json`
- `POST /api/v1/maintenance/mmf-orphans/merge` (async, 202) + `GET .../merge/status` — see `ops-runbook.md`
- `POST /api/v1/maintenance/source/backfill?apply=`

## Safety
See "MMF sync safety" in `CLAUDE.md` and `ops-runbook.md`. In short: dry run first, small `MAX_ITEMS`, no data
ops while anything is running, no hard deletes, and log monitors match HTTP status codes in context.
