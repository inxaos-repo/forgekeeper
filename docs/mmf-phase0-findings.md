# MMF modernization: Phase 0 findings (2026-10-08)

Collected by Bob on the live cluster with read-only access. No sync was triggered, nothing on NFS was changed, no cluster resources were modified, and no secret values were read or printed.

## 0.5 Current auth failure

### Where to look
- Namespace `forgekeeper`, pod `forgekeeper-d79d76474-jb4sx`. It has 0 restarts and has been up for about 2 days, so there are no `--previous` logs.
- FlareSolverr is **running** (`flaresolverr` namespace, 1/1, 3 restarts).
- Pod logs from the last 72 hours have **no MMF scraper output**. There is only the hourly `GET /api/v1/plugins/mmf/status 200`, which returns `isRunning=false, lastSyncAt=null, totalModels=0`.
- Loki keeps logs for about 30 days (2026-09-09 to now). In that window MMF appears only in plugin-load lines (`Loaded plugin: MyMiniFactory ... (mmf)`, `No manifest.json found — loading without validation (legacy plugin)`) and in `Scanning source: mmf`. **There were no sync or auth attempts in the last 30 days.** The plugin logs from the real attempts (April) have aged out.
- The only remaining record is the `sync_runs` table (25 rows, slug `mmf`).

### Sync history (`sync_runs`, UTC)
| started | status | total | scraped | failed | files | error |
|---|---|---|---|---|---|---|
| 04-20 18:43 → 04-21 16:19 (13 runs) | completed | 0 | 0 | 0 | 0 | — |
| 04-21 18:09 | completed | 7296 | 842 | 1 | **0** | — |
| 04-21 20:05, 20:12 | failed | 0 | 0 | 0 | 0 | `Auth failed: MMF OAuth authorization required — click to connect` |
| 04-21 21:09 | failed | 0 | 0 | 0 | 0 | `The operation was canceled.` |
| 04-21 21:27, 22:23, 22:51, 23:06 | **stuck `running`** (orphaned, never completed) | 7296–7307 | 140/160/50/10 | 0 | 0 | — |
| 04-21 23:25, 04-22 00:16 | completed | 0 | 0 | 0 | 0 | — |
| 04-22 13:51 | completed | 7296 | 111 | 1 | **0** | — |
| **04-22 14:49 → 04-25 01:20** (latest) | completed | 7324 | 7324 | 0 | **0** | — |

### Classification
| Episode | Class | Evidence |
|---|---|---|
| 04-20 → 04-21 morning: 13 runs with total=0 | **Login form (A1/A5)** *(inferred)* | No manifest rows came back from any run, and no error was recorded. That fits the password/Playwright login failing silently on the Facebook-linked account. `MMF_PASSWORD` was last updated at 04-21 15:54, mid-series, and the runs after it still returned 0. |
| 04-21 20:05 / 20:12 | **OAuth callback (`NeedsBrowser`)** | `Auth failed: MMF OAuth authorization required — click to connect` |
| 04-21 21:27–23:06 | Other: **orphaned runs** | 4 rows stuck in `running` with no `completed_at`. The pod was restarted or redeployed mid-sync and nothing cleans up the run state. |
| **04-22 runs (latest)** | **401 partway through (A3)**, silent *(inferred)* | The manifest loaded through session cookies (`__token__session_cookies` was saved at 04-22 14:50:13, 1 minute after the run started). But **7324 items were "scraped" with 0 files and 0 bytes downloaded**. The OAuth token rows (`access_token` 04-21 22:51:29, `token_expires_at` 04-21 22:54:28) were last written the evening before, so the token had almost certainly expired before this run began. Every per-item download then fell through the 401/403 → archive → Playwright fallbacks and was still counted as success. |

**Current blocker:** A3 (expired implicit-flow token) combined with silent success-counting. The A1/A5 login form failure came first. The fixes the plan proposes for A2/A3 and D4 (treat 401 as expiry, pause, do not count as scraped) and for orphaned runs are confirmed as needed.

**`expires_in`:** this is not logged anywhere that survives. The implicit flow stores only the encrypted `__token__token_expires_at`. I did not decrypt it.

### MMF settings (key names only, never values)
The Deployment has **no `MMF_*` env vars and no `envFrom`**. There is also no MMF k8s Secret. All plugin config lives in Postgres `plugin_configs` (slug `mmf`):

| key | state | encrypted |
|---|---|---|
| MMF_USERNAME | set | no |
| MMF_PASSWORD | set | yes |
| CLIENT_ID | set | no |
| CLIENT_SECRET | set | yes |
| CALLBACK_URL | set | no |
| FLARESOLVERR_URL | set | no |
| DELAY_MS, DOWNLOAD_DELAY_MS, RESTORE_MODE, VERBOSE_LOGGING | set | no |
| SKIP_CREATORS | **empty** | no |
| __token__access_token, __token__token_expires_at, __token__oauth_state | set (04-21 ~22:5x) | yes |
| __token__session_cookies, __token__session_useragent | set (04-22 14:50) | yes |

## 0.7 `unknown` folders
- Library: PV `forgekeeper-stl-library` → NFS `192.168.4.10:/warehousepool/3d printing`, mounted at `/library`. MMF root is `/library/sources/mmf`.
- **`sources/mmf/unknown` holds 4,819 entries, about 248 GB.** D1 has already happened at scale, so the Phase 1 cleanup is required.
- `sources/mmf` has **166 top-level folders**: 165 creators plus `unknown`.
- First entries (alphabetical sample): `"Beastmen Unleashed" - November '23 Fantasy Module and Battlemaps`, `(0111) Male ranger hunter post - apocalyptic sci-fi sniper with antigas mask`, `(FREE) Shipping Containers x2 for FDM`, `(T.M.T.F) Tank McTank Face`, `- Septembre Release`, `001 OLD RUINS` … `026 TECH BAZAAR` (a 26-item numbered terrain set), `10 Vertical Stones`, `10 miniatures - EARLY BIRD - complete RPG ogres expansion game - …`, `100MM Wide Cliff Expansion for Bridges and Dwarf Door`, `101 PRECINCT FORTRESS` … `113 CHAOS FANE`, `12 Days of ME 2023`, `12 Month Loyalty Reward - Etheum Rig`, `12 miniatures - 32mm - Heroes of the Blade - DRAGONBLADE`.
- To get the full list for the Phase 1 re-home, generate it read-only: `ls -1 /library/sources/mmf/unknown`. Then match each folder's `metadata.json` ID against the manifest.

## 0.4 Bundles 3147 / 2652 / 2447
Public fetches of `https://www.myminifactory.com/bundle/{id}` return **403 Cloudflare "Just a moment…"**, and web search has no indexed pages for them. **Needs Damon** (logged-in browser). Paste this in the browser console:

```js
for (const id of [3147, 2652, 2447]) {
  const r = await fetch(`/api/v2/bundles/${id}`, {credentials:'include'});
  console.log(id, r.status, r.ok ? (await r.json()) : '');
}
// If that 404s, open https://www.myminifactory.com/bundle/<id> and copy the item list.
```

## 0.3 Pagination / repeated rows (needs Damon)
Paste in the console on myminifactory.com while logged in:

```js
(async () => {
  const summarize = async (qs) => {
    const r = await fetch('/api/data-library/objectPreviews' + qs, {credentials:'include', headers:{accept:'application/json'}});
    const ct = r.headers.get('content-type'); if (!r.ok || !ct?.includes('json')) return {qs, status:r.status, ct};
    const j = await r.json();
    const rows = Array.isArray(j) ? j : (j.items || j.data || j.objects || j.results || []);
    const ids = rows.map(x => x.id);
    const counts = {}; ids.forEach(i => counts[i] = (counts[i]||0)+1);
    const dup = Object.entries(counts).filter(([,n]) => n>1);
    return {qs, status:r.status, topKeys:Array.isArray(j)?'array':Object.keys(j), rows:rows.length, distinct:new Set(ids).size,
            dupIds:dup.length, dupSample:dup.slice(0,5), firstId:ids[0], lastId:ids.at(-1),
            pagingHints:Array.isArray(j)?null:{total:j.total??j.totalCount??j.count, next:j.next??j.nextPage, page:j.page}};
  };
  const out = [];
  out.push(await summarize(''));
  out.push(await summarize(''));          // re-fetch: are the counts stable?
  out.push(await summarize('?page=2'));
  out.push(await summarize('?offset=100'));
  out.push(await summarize('?page=2&per_page=100'));
  console.table(out.map(o => ({...o, topKeys:String(o.topKeys), dupSample:JSON.stringify(o.dupSample), pagingHints:JSON.stringify(o.pagingHints)})));
  // For a repeated id, check how the repeats differ:
  // (await (await fetch('/api/data-library/objectPreviews',{credentials:'include'})).json()) → filter by id, compare source/release/yearmonth
})();
```
If `?page=2` / `?offset=` returns the same `firstId` and `rows` as the unparameterized call, there is no pagination.

## 0.6 Session download (needs Damon)
Replace `<id>` with a known object ID from your library (for example one from the fixture). This reports only metadata; it doesn't save the file:

```js
(async () => {
  const id = '<id>';   // numeric object id
  for (const url of [`/download/object-${id}`, `/download/${id}`]) {
    const r = await fetch(url, {credentials:'include', redirect:'follow'});
    console.log(url, {status:r.status, redirected:r.redirected, finalHost:new URL(r.url).host,
      contentType:r.headers.get('content-type'), contentLength:r.headers.get('content-length')});
    r.body?.cancel();
  }
})();
```
A `200` with a `zip`/`octet-stream` content type means session download works and Phase 2c OAuth becomes optional. A `text/html` response or a 302 to a login page means it doesn't.

## Still needs Damon
- **0.1** Set an MMF password (Forgot password on the Facebook-linked email) and confirm that both login methods work.
- **0.2** Request an OAuth authorization-code client with redirect `https://forgekeeper.k8s.inxaos.com/auth/mmf/callback`.
- **0.3** Run the pagination snippet above.
- **0.4** Run the bundle snippet above, or read the bundle pages.
- **0.6** Run the session-download snippet above.

## Side findings worth issues
- Orphaned `sync_runs` rows stuck in `running` (4 from 04-21).
- The plugin counts items as "scraped" with 0 files downloaded, so a sync can report success with nothing done.
- The plugin loads as a "legacy plugin" with no `manifest.json` from both `/app/plugins` and `/data/plugins/mmf` (a duplicate load).
- Note: branch `chore/mmf-phase0` did not exist on the remote when this was written, so the fixture and `docs/mmf-library-shape.md` the plan marks as done are not on GitHub (possibly never pushed).
