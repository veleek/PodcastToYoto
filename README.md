# PodcastToYotoPlaylist

Keeps Yoto Make-Your-Own playlists in sync with podcast RSS feeds. Built first for *Silly Stories for Kids*.

Status: **M1–M8 done; deployed at yoto.example.com**. See [docs/requirements.md](docs/requirements.md), [docs/design.md](docs/design.md), [docs/spikes.md](docs/spikes.md).

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

# run (binds http://0.0.0.0:5164; Traefik routes https://dev.yoto.example.com here)
dotnet run --project src/PodcastToYoto.Web --launch-profile http
```

- Migrations are applied automatically on startup.
- The dev instance has its own `.data/` and its own Yoto login. Never share a DB between dev and prod: refresh tokens are single-use, so the two would log each other out. Point dev playlists at test Yoto playlists so both instances don't sync the same card.
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
| `Notifications:HomeAssistant:BaseUrl` | (off) | e.g. `https://homeassistant.example.com` |
| `Notifications:HomeAssistant:WebhookId` | | POSTs JSON to `/api/webhook/{id}`; no token needed |
| `Notifications:HomeAssistant:Service` | | e.g. `notify.mobile_app_phone` or `persistent_notification.create` |
| `Notifications:HomeAssistant:Token` | | long-lived access token, only for `Service` |
| `Notifications:HomeAssistant:Events` | all | any of `PlaylistUpdated`, `SyncFailed`, `YotoNeedsLogin` |

### Home Assistant

Either (or both):

- **Webhook** (most flexible): create an automation with a *Webhook* trigger, set `WebhookId` to its id. The body is
  `{ kind, title, message, playlist_id, playlist, page_url, playlist_url }`, available as `trigger.json.*` in the
  automation. (`subscription_id`, `subscription` and `subscription_url` are still sent, with the playlist's values, so
  automations written before playlists held several podcasts keep working.)
- **Service call**: set `Service` and `Token` (Profile → Security → Long-lived access tokens). For `notify.*` services the
  playlist page URL is passed as `data.url` / `data.clickAction` so tapping the phone notification opens it.

Use **Settings → Send test notification** to check it.

### Episode icons

Each episode's icon is picked from Yoto's public icon library by matching the **emoji in its title** (via their
Unicode names, e.g. 🪜 → "ladder") against icon titles and tags; if no emoji matches, the title's words are tried
against icon titles. No match → the player's default icon. Turn it off per podcast in its settings. Needs the
`user:icons:manage` scope (reconnect Yoto after upgrading).

**Podcast logo icons**: for podcasts whose titles don't map onto Yoto's icons, a podcast's settings page can make
16×16 icons from its artwork (whole image or centre crops; natural, bold or posterised colours), upload the chosen one
to Yoto as a custom icon, and use it when an episode has no other icon or on every episode. A hand-picked episode icon
always wins.

## Playlists and podcasts

A **playlist** is one Yoto MYO playlist; it draws from one or more **podcasts**.

- Each podcast has its own rules: newest N episodes, favourites, a max episodes cap, trailers, icon settings, and its
  own order (used when the playlist groups by podcast). It can be excluded without being removed.
- The playlist has the Yoto playlist, title, cover (first podcast's artwork unless overridden), sync schedule, a max
  total tracks cap (oldest non-favourites are dropped first, across podcasts), and the track order: by release date
  (newest or oldest first) or grouped by podcast in the playlist's podcast order.
- Suggested podcasts on the add-podcast page: Silly Stories for Kids, But Why, Flip and Mozi, Who Smarted?
  (`src/PodcastToYoto.Web/Subscriptions/PodcastSuggestions.cs`).

Upgrading migrates each existing subscription into a one-podcast playlist with the same Yoto playlist, title, cover,
schedule and history, so nothing is re-uploaded or republished. Exports from before then import the same way.

## Deploy (container)

The image is self-contained: port 8080, non-root user (UID 1654), all state in `/data`.

```powershell
./build.ps1                                    # test + build podcast-to-yoto:latest and :<git sha> (linux/amd64)
./build.ps1 -SaveTo podcast-to-yoto.tar        # ...and export it to a tarball
./build.ps1 -Registry <host>:5000 -Push        # ...or tag for and push to a local registry
```

Getting the image onto the server, without a public registry:

- **`./build.ps1 -Deploy`** (current setup): builds, `docker save`s the image, `scp`s it to the server (with progress), `docker load`s it and
  restarts the container with compose. Defaults: `-DeployHost cake -DeployDir ~/services`. The server's `IMAGE` stays `podcast-to-yoto:latest`.
  Sends the whole image (~230 MB) each time.
- **Registry (optional, faster):** `./build.ps1 -Deploy -Registry <host:port>` pushes only changed layers and has the server `compose pull`.
  Docker only talks plain HTTP to `localhost` registries, so either run the registry on the server bound to `127.0.0.1:5000` and push through
  `ssh -N -L 5000:localhost:5000 cake`, or list the registry under `insecure-registries` in the server's `/etc/docker/daemon.json`.

On the server, [`deploy/compose.yaml`](deploy/compose.yaml) lives at `services/yoto/compose.yaml` and is pulled into the parent services compose file:

```yaml
# services/compose.yaml
include:
  - path: ./yoto/compose.yaml
```

```bash
cd services/yoto
cp .env.example .env            # set YOTO_HOST, YOTO_CLIENT_ID (and IMAGE if using a registry)
mkdir -p data && sudo chown -R 1654:1654 data
cd .. && docker compose up -d podcast-to-yoto
```

- The container joins the shared `services` network (where Traefik lives) and has no published ports.
- `dns: [1.1.1.1]` is set because containers on this host can't resolve external names through the default resolver.
- The Yoto redirect URI registered in the developer portal must be `https://$YOTO_HOST/auth/callback`.
- Remove the `yoto` router/service from Traefik's `dynamic_conf.yml` that pointed at the dev PC; otherwise two routers claim the host name.
- **Moving existing state:** copy the dev `.data/` folder (`app.db` **and** `keys/`) into the server's `data/`, then re-run `sudo chown -R 1654:1654 data` (copied files are owned by your user, and SQLite fails with "attempt to write a readonly database"). Without `keys/` the stored Yoto tokens can't be decrypted and you'll need to reconnect. Don't keep the dev instance running against the same account afterwards: Yoto refresh tokens are single-use, so two instances will log each other out.

### Backups

A nightly backup (03:30 local time by default) writes `podcast-to-yoto-<timestamp>.zip` to `{data}/backups`, keeping the newest 14. Each zip holds
`app.db` (SQLite online backup, safe while running), `keys/` (Data Protection keys, needed to decrypt the stored Yoto login) and `export.json`.
**Settings → Backups** shows recent backups (downloadable) and has **Back up now**.

| Key | Default | |
|---|---|---|
| `Backup:Enabled` | `true` | |
| `Backup:Directory` | `backups` | relative to the data directory; point it at a separate mount to keep backups off the data volume |
| `Backup:TimeOfDay` | `03:30` | container local time (`TZ`) |
| `Backup:Keep` | `14` | |

To restore: stop the container, unzip `app.db` and `keys/` into the data directory (replacing what's there), `chown -R 1654:1654`, start it again.

