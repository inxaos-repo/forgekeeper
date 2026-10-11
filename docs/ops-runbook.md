# Ops runbook (production)

Prod runs in the homelab k8s cluster, namespace `forgekeeper`, deployed by Flux from **inxaos-flux**
`apps/forgekeeper/release.yaml`. The `k8s/` folder in this repo is only an example.

## Deploy a merged build
1. Wait for the `build.yml` run of the merge commit to go green. It pushes `:main`, `:<sha>`, `:sha-<short>` and
   `:main-<YYYYMMDDHHmmss>-<sha>` (#31). The last two are immutable. If an older run is still going, cancel it so it
   can't overwrite `:main`.
2. Get the digest: `docker buildx imagetools inspect ghcr.io/inxaos-repo/forgekeeper:sha-<short>` (or the run log).
3. inxaos-flux PR on a `bob/*` branch: set `image.tag: main@sha256:<digest>` with a comment naming the commit/PR.
   - Bump the **mmf-fetch sidecar** pin (`ghcr.io/inxaos-repo/forgekeeper-mmf-fetch`, `main@sha256:…`) only if
     `sidecars/mmf-fetch` changed; take its digest from the `mmf-fetch.yml` run.
   - Keep `Plugins__PreferBundled: "true"`.
   - Keep the main container at request 512Mi / **limit 4Gi**. After #74 the plugin's memory is bounded; on OOM,
     lower `MAX_UNZIP_BACKLOG` first.
   - `kubectl kustomize apps/forgekeeper` must render; merge; reconcile.
4. Don't roll out during a sync (the restart kills it) unless the boss agrees.
5. Verify: pod 1/1, 0 restarts, new digest; log `Loaded plugin: MyMiniFactory v1.1.x … /app/plugins`; EF migrations
   in `__EFMigrationsHistory`; no exceptions.

## Before any sync
```sql
select key, value from plugin_configs where plugin_slug='mmf'
  and key in ('DRY_RUN','MAX_ITEMS','MAX_UNZIP_BACKLOG','SYNC_INTERVAL_HOURS');
```
- `DRY_RUN=true` unless a real run is approved; first real run after a change uses a small `MAX_ITEMS`.
- Start a fresh Sync; Resume only to continue a known checkpoint.
- `POST …/sync` blocks; start it detached and watch `GET /api/v1/plugins/mmf/status`, `sync_runs` and the logs.
- Large uploads (manifest) go through `kubectl -n forgekeeper port-forward svc/forgekeeper 15000:5000`; the ingress
  rejects bodies over 1 MB and cuts requests at 60s.

## Monitoring a sync
- Alert on HTTP status codes in context (`HTTP 403`, `status=429`, `StatusCode":5xx`), never on bare numbers —
  a bare `403` matched sizes and ids.
- Watch pod memory against the 4Gi limit and the "Waiting for extraction backlog" log line.

## Data operations (merge, rehome, backfill, moves)
Only when **every plugin reports `isRunning=false` and no scan is running**.

1. Back up the DB first: `pg_dump` the `forgekeeper` database from the CNPG pod to a dated file.
2. Never hard-delete. Move files to a `.trash-<date>` directory (the scanner skips dot-dirs); delete rows only after
   a backup, and prefer flags/merges over deletes.
3. Run in dry-run mode, review the report, then apply.

### Orphan merge (#72, #73)
```bash
curl -X POST "$FK/api/v1/maintenance/mmf-orphans/merge"                 # dry run → 202 + statusUrl
curl "$FK/api/v1/maintenance/mmf-orphans/merge/status"                  # {status, blockedBy}
curl -X POST "$FK/api/v1/maintenance/mmf-orphans/merge?apply=true"      # after review
# includeLive=true also merges live metadata-only twins; reportDir= overrides the report location
```
202 = started; 409 = refused (scan, sync or another job running — see `blockedBy`). The job runs in the background,
so the 60s ingress timeout no longer cancels it.

### Re-home `unknown/` folders
`POST /api/v1/plugins/mmf/rehome/plan` writes a report; `POST …/rehome/apply?report=<file>.json` moves only those
folders inside `sources/mmf`, skips existing targets, never deletes, is idempotent. Apply only after the report is reviewed.

### Source backfill
`POST /api/v1/maintenance/source/backfill` (dry run) then `?apply=true`.

## Scanner
- Non-UTF-8 file names: by default the scanner warns and keeps the files; set `Scanner__RepairNonUtf8Names=true`
  to rename them to valid UTF-8 on disk (#82).
- `file_issues` has `model_id`/`variant_id` indexes created `CONCURRENTLY` (#56).

## Troubleshooting
- Old plugin version loads → `Plugins__PreferBundled` missing.
- Thumbnail worker stuck on OBJ → fixed by #84 (temp STL + renderer killed on timeout).
- Stale `running` rows in `sync_runs` → reconciled since #27 (PR #80); still check before resuming.
