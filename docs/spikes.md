# Yoto API spikes

Goal: settle the open items in [requirements.md](requirements.md) before designing the service.
Tool: `tools/YotoSpike` (no NuGet dependencies). State lives in `./.yoto-spike/` (git-ignored).

## What the docs already answer

| Item | Finding | Source |
|---|---|---|
| Update in place | `POST /content` with `cardId` updates; without it creates and returns a new `cardId`. | API ref: createOrUpdateContent |
| List playlists | `GET /content/mine` (scope `user:content:view`); chapters only via `GET /content/{cardId}`. | API ref: getUserSMyoContent |
| Scopes | `user:content:manage` covers view + create/edit/delete MYO + icons; plus `offline_access` for refresh tokens. | Auth: scopes |
| Upload dedupe | `GET /media/transcode/audio/uploadUrl?sha256=` returns `uploadUrl: null` if Yoto already has that file → skip PUT. | API ref: getAnUploadUrl |
| Transcode | Poll `GET /media/upload/{uploadId}/transcoded?loudnorm=false` until `transcode.transcodedSha256`; track URL is `yoto:#<sha>`. | MYO: uploading to cards |
| Cover image | `POST /media/coverImage/user/me/upload?imageUrl=…&autoconvert=true&coverType=myo` → `coverImage.mediaUrl`. | API ref: uploadCoverImage |
| Auth | Auth code + PKCE, `https://login.yotoplay.com/authorize` / `/oauth/token`, audience `https://api.yotoplay.com`. Refresh tokens are **single-use** (rotated). | Auth: browser auth |
| Limits (third-party, unconfirmed) | 100 tracks/card, 100 MB and 60 min per track, 500 MB per card, "about 5 hours" per card. | wayfablestories.com guide |

**Limits:** Ben has manually built MYO cards with ~100 Silly Stories episodes (~25 h), so the 5 h / 500 MB figures are not hard caps. 100 tracks is the remaining likely limit. N=25 is fine.

## Setup

1. Create an app at <https://dashboard.yoto.dev/> → note the **Client ID**.
2. Register redirect URIs:
   - `http://127.0.0.1:8787/callback` (spike / local dev)
   - `https://<your-host>/auth/callback` (service; this is what S4 tests)
3. `$env:YOTO_CLIENT_ID = "<client id>"`

## Runbook

```powershell
cd tools/YotoSpike
dotnet run -- feed --count 25        # sanity check RSS parsing + total duration/size for 25 eps
dotnet run -- login                  # browser login → .yoto-spike/tokens.json
dotnet run -- refresh                # S5: rotation + token lifetime
dotnet run -- list                   # existing MYO playlists
dotnet run -- push --count 2         # S1a: create new playlist from 2 newest eps → note cardId
#   link that playlist to a physical MYO card in the Yoto app, play it
dotnet run -- push --count 3 --card <cardId>   # S1b: update in place (2 cached + 1 new upload)
dotnet run -- get <cardId>           # compare stored JSON with .yoto-spike/last-request.json
#   re-insert the card in the player: does it pick up the 3rd episode without relinking?
dotnet run -- push --count 25 --card <cardId>  # S2: realistic default size (optional; 100 eps already known to work manually)
```

Flags without values (`--dry-run`, `--no-cover`) should come last or before another `--flag`.

## Results

Fill in as spikes are run. Test playlist created 2026-10-04: `4OA8g` (2 newest episodes, podcast cover).

| # | Question | Result |
|---|---|---|
| S1 | Update with `cardId` keeps the same playlist; linked physical card picks up changes without relinking? | API returns the same `cardId` on update (documented). Physical-card behaviour not tested; Ben is ~95% confident from experience that playlist edits apply without relinking. |
| S2 | Hard limits on track count / total duration / size? Error shape when exceeded? | Existing 100-episode card: 78,678 s (21.9 h), 958 MB. Duration/size are not practical limits; 100-track cap unconfirmed. |
| S3 | `user:content:manage offline_access` sufficient for list, upload, cover, create/update? | Mostly: list, upload, transcode, cover and create/update work. **But `GET /content/{cardId}` returns 403 unless `user:content:view` (or `family:library:view`) is granted explicitly**, even though the docs say manage includes view. Default scopes are now `user:content:view user:content:manage offline_access`. |
| S4 | Dev portal accepts a non-loopback HTTPS redirect URI? | ✅ Portal accepted `https://yoto.example.com/auth/callback`. End-to-end flow untested until the service exists. |
| S5 | Access token lifetime; refresh token rotates every refresh? | Access token ≈ 8 h. Refresh token rotates on every refresh (confirmed). |
| S6 | Chapter/track icon required, or does the player show a default when omitted? | Optional. Player shows a default icon (crosshair with corner markers) when omitted. |
| S7 | `transcodedInfo` field types (`channels`, `format`, `duration`) and units of `readableFileSize`? | Transcoded to Opus/Ogg 48 kHz ~104 kbps (~12 MB per 15 min ep). `channels` is a string (`"stereo"`), `format` `"opus"`, `duration`/`fileSize` integers (s, bytes). Yoto drops `readableFileSize`. Dedupe hit on an episode already in the library returned the existing transcode (incl. loudnorm stats). |
| S8 | Typical upload + transcode time for a ~15 min episode? | Download ~1 s (22 MB MP3); upload + transcode 6–8 s. |
