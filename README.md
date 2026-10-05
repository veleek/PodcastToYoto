# PodcastToYotoPlaylist

Keeps Yoto Make-Your-Own playlists in sync with podcast RSS feeds. Built first for *Silly Stories for Kids*.

Status: **M1–M4 done; M5 (deploy) next**. See [docs/requirements.md](docs/requirements.md), [docs/design.md](docs/design.md), [docs/spikes.md](docs/spikes.md).

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
- The dev instance has its own `.data/` and its own Yoto login. Never share a DB between dev and prod: refresh tokens are single-use, so the two would log each other out. Point dev subscriptions at test playlists so both instances don't sync the same card.
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

## Deploy (container)

The image is self-contained: port 8080, non-root user (UID 1654), all state in `/data`.

```powershell
./build.ps1                                    # test + build podcast-to-yoto:latest and :<git sha> (linux/amd64)
./build.ps1 -SaveTo podcast-to-yoto.tar        # ...and export it to a tarball
./build.ps1 -Registry <host>:5000 -Push        # ...or tag for and push to a local registry
```

Getting the image onto the server, without a public registry:

- **No registry:** `docker save podcast-to-yoto:latest | ssh <server> docker load` (or copy the tarball from `-SaveTo` and run `docker load -i`).
- **Local registry:** run `registry:2` on this machine (`docker run -d -p 5000:5000 --restart unless-stopped --name registry registry:2`). Push with `-Registry <this-host>:5000 -Push`, and set `IMAGE=<this-host>:5000/podcast-to-yoto:latest` on the server. Plain-HTTP registries must be listed under `insecure-registries` in the server's `/etc/docker/daemon.json`.

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

