# PodcastToYotoPlaylist

Keeps Yoto Make-Your-Own playlists in sync with podcast RSS feeds. Built first for *Silly Stories for Kids*.

Status: **M1–M3 done (end-to-end sync works); M4 curation + M5 deploy next**. See [docs/requirements.md](docs/requirements.md), [docs/design.md](docs/design.md), [docs/spikes.md](docs/spikes.md).

## Layout

| Path | What |
|---|---|
| `src/PodcastToYoto.Core` | Yoto API client (OAuth/PKCE, content, media upload), RSS parsing, episode download |
| `src/PodcastToYoto.Web` | ASP.NET Core + Blazor Server app: UI, Yoto auth endpoints, EF Core/SQLite, (soon) sync engine |
| `tests/PodcastToYoto.Tests` | xUnit tests (`dotnet test`) |
| `tools/YotoSpike` | CLI used to validate Yoto API behaviour |
| `docs/` | Requirements, design, spike notes |

## Dev setup

Requires the .NET 10 SDK.

```powershell
# one-off
dotnet tool install --global dotnet-ef
dotnet user-secrets set Yoto:ClientId <client id> --project src/PodcastToYoto.Web

# after any change to the EF model
dotnet ef migrations add <Name> --project src/PodcastToYoto.Web --output-dir Data/Migrations

# run (binds http://0.0.0.0:5164; Traefik → https://yoto.example.com)
dotnet run --project src/PodcastToYoto.Web --launch-profile http
```

- Migrations are applied automatically on startup.
- `App:PublicBaseUrl` must be the externally reachable URL. The Yoto redirect URI is `{PublicBaseUrl}/auth/callback` and has to be registered in the Yoto developer portal.
- Runtime state (SQLite DB, Data Protection keys) lives in `.data/` at the repo root in Development (git-ignored) and `/data` in the container. `src/PodcastToYoto.Web/Data/` is source code (EF model + migrations) and is committed.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `App:PublicBaseUrl` | (required) | e.g. `https://yoto.example.com` |
| `App:DataDirectory` | `/data` | relative paths resolve against the content root |
| `Yoto:ClientId` | (required) | user-secrets in dev, `Yoto__ClientId` env var in the container |
| `Yoto:Scopes` | `user:content:view user:content:manage offline_access` | `view` is needed explicitly for `GET /content/{id}` |
| `Sync:DefaultPollInterval` | `01:00:00` | |
| `Sync:RunOnStartup` | `true` | |
| `Sync:DryRun` | `false` | |
