# PodcastToYotoPlaylist — Requirements

Status: draft v2 (2026-10-04)

## Purpose

Self-hosted service that watches podcast RSS feeds and keeps a Yoto Make-Your-Own (MYO) playlist per feed up to date, with no manual download/upload steps. First subscription: *Silly Stories for Kids* (Samuel Ramsden) — `https://feeds.megaphone.fm/EUAET6896760602`.

## Decisions

| Area | Decision |
|---|---|
| Language / runtime | C# / .NET (ASP.NET Core + hosted background worker), single process |
| Hosting | Docker container on home Linux server, behind Traefik + PocketID |
| Scope | Multiple feed → playlist subscriptions |
| Playlist contents | Newest N episodes (default 25, configurable per subscription) + pinned favourites |
| Order | Newest first (configurable) |
| Notifications | Structured logs for v1; Home Assistant integration later |

## Configuration vs state

- **Config (appsettings / env vars, read-only):** deployment-level settings only — Yoto client ID, public base URL (for OAuth redirect), data directory, poll interval default, log level. Secrets via env vars.
- **Database (SQLite on mounted volume, source of truth):** subscriptions, per-subscription settings (N, order, icon), favourites, episode catalogue, Yoto track references, tokens, sync history.
- The UI never writes back to config files. Subscriptions + favourites can be **exported/imported as JSON** for backup or seeding a fresh install.

## Functional requirements

### Subscriptions
- F1. Add / edit / remove subscriptions in the web UI: feed URL, target playlist, N, order, enabled flag.
- F2. Target playlist: pick an existing MYO playlist from the user's Yoto account (`getUserSMyoContent`) or create a new one. If none chosen, a new playlist is created on first sync.
- F3. Playlist title, cover image and default chapter icon derived from feed metadata, overridable. Podcast artwork uploaded as the playlist cover.

### Feed polling
- F4. Poll each enabled subscription on a schedule (default hourly) using conditional GET (ETag / Last-Modified).
- F5. Episodes identified by RSS `guid` (fallback: enclosure URL). Full episode list stored, not just the active window, so favourites can be chosen from back-catalogue.
- F6. "Sync now" per subscription from the UI.

### Favourites
- F7. UI lists all known episodes for a subscription with a toggle to pin/unpin. Pinned episodes stay on the playlist regardless of age.
- F8. Pinned episodes are included in addition to the newest N (they don't count against N). Ordering still applies across the combined set.

### Sync to Yoto
- F9. Desired playlist = (newest N ∪ favourites), ordered, trimmed to the track limit (default 100) by dropping the oldest non-favourites, with a warning. One chapter per episode, one track per chapter, title = episode title.
- F10. Only episodes without a stored Yoto track reference are downloaded and uploaded; Yoto's sha256 dedupe (`uploadUrl: null`) skips the PUT for files it already has (HttpClient, follow redirect chains, stream to temp file) → upload URL → PUT → poll transcode → record `yoto:#<sha>` and track metadata.
- F11. Write full playlist content via create-or-update content endpoint. The service owns the playlist content; manual edits in the Yoto app are overwritten.
- F12. Skip the write if desired content is unchanged since last successful sync.
- F13. Sync is idempotent and safe to retry after partial failure (uploads recorded as they complete).
- F14. Dry-run mode: compute and log/display the desired playlist without uploading or writing.

### Authentication
- F15. Connect Yoto account from the web UI: "Connect" button → Yoto authorization code + PKCE flow → redirect back to `https://<host>/auth/callback`. No copying values from logs.
- F16. Request `offline_access`; persist refresh token immediately after every refresh (tokens are single-use). Refresh proactively before access token expiry.
- F17. If refresh fails, mark the account disconnected, pause syncs, surface prominently in UI + logs, and offer "Reconnect".

### Status UI
- F18. Dashboard: subscriptions, last poll, last sync result, current playlist contents, recent errors, Yoto connection status.
- F19. UI is protected by Traefik/PocketID forward-auth; the app itself does no user auth beyond trusting that layer (except the OAuth callback route, which must be reachable during the flow).

## Non-functional requirements

- N1. Single container image; config via env; `/data` volume for SQLite + temp files. Temp audio deleted after upload.
- N2. `/health` endpoint (liveness + Yoto auth status).
- N3. Structured logging (console, JSON option). Events for: new episode detected, upload complete, playlist updated, auth failure.
- N4. Respectful polling (conditional GET, backoff on errors); serialized Yoto API calls with retry/backoff on 429/5xx.
- N5. Notification abstraction so a Home Assistant sink (webhook or MQTT) can be added later without touching sync logic.
- N6. Source in public GitHub repo; no secrets committed.

## Out of scope (v1)

- Ad trimming / audio editing (feed is on Megaphone; dynamic ads may be present).
- Audio normalisation.
- Creating or physically linking cards (linking is done in the Yoto app).
- Multiple Yoto accounts.
- Home Assistant notifications (designed for, not built).

## Open items / spikes

Tracked in [spikes.md](spikes.md). Summary:

- S1. `createOrUpdateContent` with an existing `cardId` updates in place (documented); still to confirm a linked physical card picks up changes without relinking.
- S2. Playlist limits. ~100 episodes (~25 h) on one card works via the Yoto app, so duration/size are not practical limits; assume a 100-track cap. The sync still enforces a configurable max track count (default 100) by dropping the oldest non-favourites, with a warning.
- S3. Scopes: `user:content:manage offline_access` (documented) — confirm in practice.
- S4. Dev portal accepts a non-loopback HTTPS redirect URI (needed for F15); fallback is loopback/CLI flow.
- S5. Token lifetime and rotation behaviour.
- S6. Whether chapter icons are required; nice-to-have: pick public icons per episode (e.g. match the leading emoji).
