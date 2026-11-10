# Plan: Scrape Online-Fix fixes from online-fix.me for the Fixes tab

User preference (apply everywhere, keep for future work): **"Use as little tokens as possible, low-medium effort only, don't think too much, just do what's requested."** (Also to be saved as a project skill/rule — see Task 6.)

## Goal

In the Fixes tab, fixes tagged **"Online Fix" (`online`)** should come from **online-fix.me directly** (scraped in-app, porting the user's Node `searchOnlineFixDeep` script to C#), **instead of** relying on depotbox's fixes that carry the "onlinefix" tag. Depotbox remains the source for Bypass fixes and everything else.

## Current state (verified)

- `FixesViewModel` → `DataJackUIApiClient.GetDenuvoListingsAsync()` (Services/DataJackUIApiClient.cs:1260) and `GetDenuvoFixesAsync()` (:1470) pull JSON from `{ApiBaseUrl}/api/fixes/list`, `{ApiBaseUrl}/api/depotbox/api/game-fixes`, `https://depotbox.org/api/game-fixes`. `ApiBaseUrl = https://auth.netrunnergames.workers.dev` (AppConfig.cs:11).
- No HTML scraping exists today; the client only parses JSON.
- Download flow: `RunDownload` → `EnqueueDownloadJob` (FixesViewModel.cs:706) → `jobs.CreateDenuvoJob` (ManifestJobFactory.cs:91) → `api.DownloadDenuvoAsync(fixId, slot, ...)` — a backend signed-URL download, then `ApplyDenuvoFix` extracts the zip into the game folder.
- `DenuvoFix` model (FixModels.cs) already has `Title`, `Description`, `DownloadUrl`, `FixFilename`, `FileSize`, `Tags`.

## Design decisions (defaults chosen, per user's low-effort directive)

- **Replace, not merge**: for the `online` tag, depotbox's onlinefix fixes are dropped in favor of scraped online-fix.me entries. Bypass and other tags untouched.
- **Scrape client-side** (new C# service), no backend change — the user's script logic lives in the app.
- Scrape happens **at fix-list load time per game** (article page + hoster page), and the scraped `direct_link` is downloaded **immediately** (links are short-lived).
- Game → article matching via online-fix.me search: `https://online-fix.me/index.php?do=search&subaction=search&story={gameName}` (DLE GET search), take top result(s) under `/games/...`. Decode windows-1251 with `Encoding.GetEncoding("windows-1251")` (needs `System.Text.Encoding.CodePages` registration on Core: `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`).

## Tasks

1. **New `Services/OnlineFixScraperService.cs`** — C# port of the Node script:
   - `SearchAsync(gameName)` → fetch search page (UA + `Referer: https://online-fix.me/`, windows-1251 decode), regex article links `/games/....html`, return top N (title + url).
   - `ScrapeArticleAsync(articleUrl)` → port of `searchOnlineFixDeep`: extract `Game version` via the 3 regexes from the script; extract `hosters.online-fix.me:2053/...` href; fetch hoster page, unescape `&quot;`/`&amp;`, regex `data-links="[{...}]"`, JSON-parse, pick first item whose `file_name` contains `fix_repair` or `fix`; return `versionStr`, `hosterUrl`, `directFixUrl`, `fixFileName`, `fileSize` when present.
   - All requests carry the Chrome UA + online-fix.me Referer headers.
2. **Wire into per-game fix load** (`FixesViewModel.LoadGameDetailAsync`, ~line 592):
   - Call `api.GetDenuvoFixesAsync(appid)` as today, but **filter out** fixes tagged `online` (keep everything else).
   - Then call the scraper: search → scrape article → build `DenuvoFix` entries: `Id = "{appid}_ofme_{n}"`, `Title` = article title, `Description` = `Game version: ...` from `versionStr`, `DownloadUrl` = `directFixUrl`, `FixFilename` = `fixFileName`, tags = online. If scraping/search fails or returns nothing, show no online fix (no synthetic fallback for online — only bypass fallback stays).
   - If the search returns multiple plausible articles, take the first (low-effort default); surface title so the user can see what matched.
3. **Download path** for scraped fixes:
   - Fix ids with the `_ofme_` marker use a new direct-download branch in `ManifestJobFactory`: a job whose download delegate does `HttpClient` GET of `DownloadUrl` with the online-fix UA/Referer headers (referer required by hoster), streaming to the same staged zip; then reuse the existing `ApplyDenuvoFix` install step unchanged.
   - Simplest: add `CreateOnlineFixDirectJob(url, filename, ...)` in `ManifestJobFactory` and route in `EnqueueDownloadJob` when `fix.Id.Contains("_ofme_")`.
4. **Model tweak** (`DenuvoFix`): ensure `Tags` list can carry `online` so existing filter UI shows the scraped fix as "Online Fix". Add optional `Source = "online-fix.me"` note into `Description` (no new fields needed).
5. **Resilience/low-cost**: wrap all scraping in try/catch → empty result on failure; cache last successful scrape per appid in memory for the session (avoids re-scraping hoster page on every reopen).
6. **Save the user preference** as a workspace rule/skill file (`.agents/skills/low-effort-mode/SKILL.md` or a `.kilo` rule): "Use as little tokens as possible, low-medium effort only, don't think too much, just do what's requested."

## Risks / notes

- online-fix.me search endpoint shape (DLE) may differ / need POST+csrf; if GET search fails, fall back to fetching the game-name slug URL or return "no online fix found" gracefully.
- `direct_link` values may be relative or expire; scrape at download-time if a URL fails (retry-once inside the job).
- windows-1251 needs CodePages registration at app startup (App.xaml.cs).
- Hoster may rate-limit; the per-session cache in Task 5 mitigates.

## Validation

- Run the app, open Fixes tab, open a known online-fix game (e.g. Palworld): verify an "Online Fix" card appears with scraped version string and downloads+applies via the existing fix installer.
- Verify Bypass fixes still load from depotbox and Bypass/manifest slots are unchanged.
- Existing unit tests (`SourcePriorityTests`, fixes-related) still pass: `dotnet test`.

## Out of scope

- Backend/Workers changes; scraping bypass fixes; mass-listings scrape (grid view keeps depotbox data for browsing — scraping is per-game only).
