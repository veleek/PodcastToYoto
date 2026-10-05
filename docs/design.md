# PodcastToYotoPlaylist — Design

Status: draft v1 (2026-10-05). Builds on [requirements.md](requirements.md) and [spikes.md](spikes.md).

## Key choices

| Area | Choice | Why |
|---|---|---|
| Host | Single ASP.NET Core app (.NET 10): web UI + background sync in one process | One container, one volume, no queue/infra |
| UI | Blazor (Interactive Server) | All C#, live status updates over SignalR for free, no JS build chain. Internal tool with one user, so server-side circuits are fine. |
| Persistence | EF Core + SQLite in `/data/app.db` | Zero-ops, migrations, easy to inspect |
| Scheduling | `BackgroundService` + `PeriodicTimer` + `Channel` for "sync now" | Nothing beyond the BCL needed; one sync at a time |
| Secrets at rest | ASP.NET Data Protection (keys in `/data/keys`) for the stored tokens | Cheap; tokens aren't plaintext in the DB file |
| Logging | `Microsoft.Extensions.Logging` console (JSON formatter option) | No extra dependencies |
| User auth | None in-app; Traefik + PocketID forward-auth in front | Already in place. App must only be reachable through Traefik (firewall / no published port). |

## Solution layout

```
src/
  PodcastToYoto.Core/     Yoto API client, OAuth/PKCE, RSS parser, episode downloader (no persistence)  [exists]
  PodcastToYoto.Web/      ASP.NET Core host: EF Core, sync engine, scheduler, auth endpoints, Blazor UI
tests/
  PodcastToYoto.Tests/    xUnit: feed parsing, playlist planning, sync engine against a fake Yoto API
tools/
  YotoSpike/              Spike CLI (kept for poking at the API)                                       [exists]
```

Split `Web` into `Web` + `Sync` later only if it gets unwieldy.

## Data model

```
YotoAccount (single row)
  AccessToken*, RefreshToken*, ExpiresAt, Scope, Status (Connected | NeedsReauth), LastError, UpdatedAt
  (* encrypted via Data Protection)

Subscription
  Id, Name, FeedUrl, Enabled
  YotoCardId?            null → create on first sync
  TitleOverride?, RecentCount (25), MaxTracks (100), Order (NewestFirst | OldestFirst)
  IncludeTrailers (false), PollInterval? (null → global default)
  CoverSourceUrl?, CoverMediaUrl?          re-upload cover only when the feed image URL changes
  FeedETag?, FeedLastModified?, LastPolledAt?
  LastPublishedHash?, LastSyncedAt?, LastSyncStatus?, LastError?

Episode
  Id, SubscriptionId, Guid (unique per subscription), Title, PublishedAt?, EnclosureUrl, EnclosureLength?,
  DurationSeconds?, EpisodeType?, ImageUrl?, IsFavourite, FirstSeenAt, MissingFromFeedSince?
  AudioId? → Audio

Audio  (one row per distinct source file, shared across subscriptions)
  Id, SourceSha256 (unique), TranscodedSha256, DurationSeconds, FileSize, Channels, Format, UploadedAt

SyncRun  (history; keep last ~200)
  Id, SubscriptionId, Trigger (Schedule | Manual | Startup), StartedAt, FinishedAt,
  Result (NoChange | Updated | Failed | DryRun), NewEpisodes, Uploaded, Message
```

Episodes are never deleted when they fall out of the feed (`MissingFromFeedSince` is set instead), so favourites from the back-catalogue stay playable.

## Sync engine

Runs per subscription, one subscription at a time:

1. **Poll**: conditional GET with ETag / Last-Modified. A 304 skips parsing. Otherwise upsert episodes by GUID and record new ones.
2. **Plan** (pure function, unit tested):
   - candidates = episodes, excluding trailers unless `IncludeTrailers`
   - desired = newest `RecentCount` ∪ favourites
   - if the count is over `MaxTracks`, drop the oldest non-favourites, then the oldest favourites; warn either way
   - sort by `Order`
3. **Ensure audio**: for each desired episode without an `Audio` row, download it, hash it, and reuse an `Audio` row with the same source hash if there is one. Otherwise upload → transcode → insert `Audio`. Each episode is saved as it completes, so a crashed sync resumes cheaply. Yoto's own sha dedupe (`uploadUrl: null`) skips the PUT for files it already has.
4. **Ensure card**: no `YotoCardId` → create the playlist. Cover is uploaded once and refreshed when the feed image changes.
5. **Publish**: build the `/content` body. On update, first `GET /content/{cardId}` and keep the existing `content.config` / `playbackType`, so Yoto-side settings like resume timeout aren't reset. Hash the body: if it matches `LastPublishedHash`, skip the POST. Otherwise POST with `cardId` and store the hash.
   - Card deleted or not found → mark the subscription failed with "playlist missing"; the UI offers "create new playlist".

**Failure handling**:
- Yoto 401 or a failed refresh → account becomes `NeedsReauth`, all syncs pause, and the UI shows a banner.
- Transient errors are retried in the API client; anything else is recorded on the `SyncRun` and retried next interval.

**Scheduler**:
- Ticks every minute and runs subscriptions whose `LastPolledAt + PollInterval` has passed.
- "Sync now" posts the subscription ID to a channel.
- Optionally runs once on startup.
- A global lock means only one sync runs at a time.

## Yoto auth (web flow)

- `GET /auth/login` generates the PKCE verifier and state, keeps them in a short-lived Data-Protected cookie, and redirects to Yoto.
- `GET /auth/callback` validates state, exchanges the code, saves the tokens and redirects to `/`.
- `POST /auth/disconnect` clears the tokens.
- `redirect_uri` = `{App:PublicBaseUrl}/auth/callback`, always taken from config and never from the request. Forwarded headers are enabled for correct scheme and host in logs and links.
- `DbTokenStore : ITokenStore` plugs into the existing `YotoTokenProvider`. Rotated refresh tokens are saved before use.

## UI

| Page | Contents |
|---|---|
| `/` Dashboard | Yoto connection banner (Connect / Reconnect); subscription cards with status, last sync, next poll, episode counts, **Sync now** |
| `/subscriptions/new` | Feed URL → preview (title, cover, latest episodes) → settings → target playlist: pick existing (`/content/mine`) or "create new" |
| `/subscriptions/{id}` | Settings; **Episodes** table (favourite toggle, on-playlist indicator, date, duration); **Planned playlist** preview; sync history; dry-run button |
| `/settings` | Export / import subscriptions + favourites as JSON; global defaults |

## Configuration

`appsettings.json` / environment variables (`Section__Key`):

| Key | Default | Notes |
|---|---|---|
| `App:PublicBaseUrl` | — (required) | `https://yoto.example.com` |
| `App:DataDirectory` | `/data` (container), `./data` (dev) | DB, Data Protection keys, temp downloads |
| `Yoto:ClientId` | — (required) | |
| `Sync:DefaultPollInterval` | `01:00:00` | |
| `Sync:RunOnStartup` | `true` | |
| `Sync:DryRun` | `false` | Global kill-switch for writes to Yoto |

Dev: `launchSettings.json` binds `http://0.0.0.0:5164`. Container: listens on 8080.

## Observability

- Log events: episode discovered, upload complete (with timings), playlist published, sync failed, auth needs re-login.
- `/health` covers DB reachability and the Yoto account state. It's excluded from PocketID if uptime checks are wanted.
- `INotificationSink` has a log implementation now and Home Assistant later.

## Milestones

1. **M1 Foundation** ✅ *(2026-10-05)*: Web project, EF Core + migrations, Data Protection, Yoto connect/reconnect flow, dashboard showing account status.
2. **M2 Feeds**: subscription CRUD, feed preview, polling + episode upsert, episodes table.
3. **M3 Sync**: planner, audio pipeline, publish, scheduler, Sync now, sync history. *End-to-end usable here.*
4. **M4 Curation**: favourites, playlist picker for existing cards, planned-playlist preview, export/import.
5. **M5 Deploy**: Dockerfile (non-root, `/data` volume), compose snippet, GHCR build via GitHub Actions.

## Later / ideas

- Home Assistant notifications (webhook or MQTT).
- Per-episode icons from Yoto's public icon library (e.g. match the title's leading emoji).
- "Adopt" an existing manual playlist: match its tracks to feed episodes and mark them as favourites.
