# Spike: Yoto stream tracks as triggers

Question: can a MYO **stream track** (a track whose `trackUrl` points at a URL we serve) act as an input? Examples:
mark a card "activated" for the week when its first track is played, fire a Home Assistant action when a track
starts, or run a puzzle-hunt card that makes the lights blink. How fast is it, and what does the player's request
tell us?

Status: desk research done, live test pending (needs a real player). Tools: `tools/StreamProbe` (logging server) and
`tools/YotoSpike streams` (creates a test playlist). Fill in **Results** after the run.

## What's known

| Item | Finding | Confidence |
|---|---|---|
| Track shape | `type: "stream"`, `format: "mp3"` or `"aac"`, `trackUrl: https://…`. No upload or sha needed. | Documented ([yoto.dev streaming tracks](https://yoto.dev/myo/streaming-tracks/)) |
| When fetched | At playback time, so content can change without editing the card. | Documented |
| Who fetches | The player itself, with a plain HTTP GET. Yoto's servers aren't in the path. | Inferred from a community project ([yoto-smart-stream](https://github.com/earchibald/yoto-smart-stream)), not verified |
| HTTPS | Required. | Community claim |
| Range, redirects, HEAD, HLS, timeouts, UA, headers | Not documented anywhere I could find. | Unknown, so the probe tests each one |
| Prefetch | Unknown whether chapter 2 is fetched while chapter 1 plays, or whether a card insert fetches before play starts. | Unknown |
| Resume | Players resume a card part-way through. If that holds for MYO cards, chapter 1 isn't requested on every insert. | Inferred, needs testing |

### The other channel: MQTT player events

Yoto also exposes per-player events over MQTT ([yoto.dev MQTT docs](https://yoto.dev/players-mqtt/mqtt-docs)). The
broker is AWS IoT over websockets, authenticated with the same OAuth access token this app already holds. The
`device/{id}/data/events` payload includes `cardId`, `chapterKey`, `trackKey`, `playbackStatus`
(`playing`/`paused`/`stopped`), `position`, `source` (`card`, `remote`, `button`…) and `eventUtc`. The Home Assistant
Yoto integration is built on this and exposes a `media_player` entity per player.

The two channels answer different questions:

| | Stream request (HTTP) | MQTT events / HA integration |
|---|---|---|
| Which players | **Any** player that plays the card, including ones on other accounts | Only players on Ben's account |
| Which cards | Only cards with a stream track we serve | Any card, including uploaded audio and store cards |
| Identity | The URL (we choose it per card/chapter), plus client IP. No device id expected | `deviceId`, `cardId`, chapter/track keys |
| Feedback to the listener | **Yes**: the response *is* the audio, so it can be chosen per request (a different clue, "the lights are on") | No; the player plays what's on the card |
| Needs | Public HTTPS endpoint without SSO, our server up at play time | A long-lived MQTT subscriber, or HA |
| Fires on resume / mid-card | Only if that chapter is fetched | Yes, on any state change |

So for the use cases:

- **Weekly activation of our own card:** MQTT (or HA's `media_player` state) is the more reliable signal, because
  it sees any playback of the card, and resume or a cached chapter 1 doesn't hide it. A stream "intro" chapter works
  too, but only if chapter 1 is actually requested on each use (resume test below).
- **"Track started" → HA action at home:** HA's Yoto integration can do this today with no code. Trigger on the
  `media_player` attributes (`media_content_id` / card id). The probe compares its latency with the HTTP path.
- **Puzzle hunt:** stream tracks are the only route when players aren't on Ben's account, and the only one that can
  talk back. Each clue card gets its own URL; the server updates puzzle state, fires HA, and picks the audio to return.

## Latency, expected

- **HTTP path:** card insert → NFC read → player resolves the card's content (from Yoto's API, or cached) → DNS + TLS
  to our host → GET. My guess is 1–4 s from insert to our first byte, plus our webhook → HA → light (tens of ms on
  the LAN, bulb-dependent after that). This is a guess. The probe measures it.
- **MQTT path:** an event is published on playback start. AWS IoT → subscriber is typically sub-second. The HA
  integration may add its own delay.
- The player starts playing only after our response begins, so a slow trigger handler delays the audio. Fire the
  webhook asynchronously and answer immediately, as the probe does.

## Design notes if it works

- **Unguessable URLs.** The trigger endpoint has to bypass PocketID, so anyone with the URL can fire it. Use a random
  per-card token in the path (`/t/{token}/…`), rate-limit it, and keep everything else behind SSO.
- **Dedupe.** Count only a `GET` with no `Range` header or with `bytes=0-`, once per window (the probe uses 1 min).
  Resume, seek and prefetch requests then don't re-fire.
- **Weekly activation in this app.** Add a `Subscription`-level (or playlist-level) `LastActivatedAt`.
  `TrackRotation.Current` would only advance to the next week when the current week's set was activated. Otherwise it
  keeps the stored set. The signal comes from either an `/activate/{token}` stream chapter or an MQTT subscriber
  matching `cardId == playlist.YotoCardId`. The MQTT one also needs a hosted service and the `user:content:view`
  token already held.
- **Short audio.** An intro or trigger chapter can be a 2–3 s chime or a spoken "this week's stories". Dynamic
  audio (TTS through HA, or pre-recorded variants) is possible because the response is generated per request.
- **Offline.** If the server is down, the stream chapter fails. Check what the player does (the 404 chapter) before
  putting a trigger chapter first on a bedtime card.

## Live test

### 1. Run the probe

On the Windows PC (it's a throwaway, so no container):

```powershell
cd tools/StreamProbe
dotnet run -- --urls http://0.0.0.0:5099 `
  --Probe:HaWebhookUrl https://ha.example.com/api/webhook/yoto-probe   # optional
```

Every request prints on arrival (method, URL, client IP, Range, User-Agent) and again on completion (status, bytes
sent, duration, aborted). Full headers go to `.stream-probe/hits.jsonl` and to `GET /hits`. Allow TCP 5099 inbound
in Windows Firewall for the Traefik host only. `Probe:Audio` can point at any MP3; use a real 15-minute episode for the
throughput test in step 4. The bundled `probe.mp3` is 2 min of quiet beeps every 5 s, with a longer beep each minute.

### 2. Expose it through Traefik, with SSO only on `/hits`

Add to the Traefik dynamic config (`dynamic_conf.yml`). Replace the host and the PC's LAN IP:

```yaml
http:
  routers:
    stream-probe:                      # the player can't do SSO
      rule: Host(`probe.yoto.example.com`) && (PathPrefix(`/p/`) || PathPrefix(`/t/`))
      priority: 100
      entryPoints: [websecure]
      tls: { certResolver: lets-encrypt }
      service: stream-probe
    stream-probe-admin:                # /hits and everything else
      rule: Host(`probe.yoto.example.com`)
      priority: 10
      entryPoints: [websecure]
      tls: { certResolver: lets-encrypt }
      middlewares: [oidc_auth]
      service: stream-probe
  services:
    stream-probe:
      loadBalancer:
        servers: [{ url: "http://192.168.x.y:5099" }]
```

Check from a phone on mobile data: `https://probe.yoto.example.com/p/check.mp3` should play, and the console should
show the phone's carrier IP. Traefik terminates TLS, so the probe sees Traefik's `X-Forwarded-*` headers. TLS details
(SNI, TLS version, cipher) are only in Traefik's access log, if that's enabled.

### 3. Optional: HA side

- Webhook automation with webhook id `yoto-probe`. HA webhooks are local-only by default, which is fine when the
  probe reaches HA on the LAN. Action: flash a light, and log `{{ trigger.json }}` to the logbook.
- If the Yoto integration is installed, open the player's `media_player` history next to the probe log. It gives
  the MQTT-path timestamps for the same inserts.

### 4. Create the test card and run the checks

```powershell
cd tools/YotoSpike
dotnet run -- streams --base https://probe.yoto.example.com          # prints cardId and run id
```

Watch the probe console as this runs. A request right after the POST means Yoto validates or probes stream URLs
server-side, and its IP and UA identify Yoto's fetcher. Link the playlist to a spare MYO card in the Yoto app.

The playlist's chapters: 1 Plain A (has a query string), 2 Plain B, 3 Redirect (302), 4 Slow 5 s, 5 Slow 20 s,
6 Missing (404), 7 Trigger (fires the webhook).

For each check, note the wall-clock time of the action (or use the HA history) against the probe's timestamps.

| # | Do | Answers |
|---|---|---|
| a | Insert the card, let chapter 1 play 30 s | Insert → first request latency. UA, IP (home WAN = direct, AWS = proxied), all headers, HEAD first?, Range pattern, query string kept? Is chapter 2 fetched early (prefetch)? |
| b | Pause 30 s, resume | New request with `Range` on resume, or the same connection? |
| c | Let chapter 1 run to the end (or use a real episode as `Probe:Audio`) | Bytes vs time: one fast download (buffer/cache), or paced at the bitrate (true streaming)? |
| d | Skip to chapters 3, 4, 5 and 6 | Redirects followed? How long a wait is tolerated? What happens on a 404: skip, error sound, or stop? |
| e | Skip to chapter 7 | Insert/skip → webhook → light latency, end to end |
| f | Remove the card, reinsert within 1 min, then again after 10 min | Re-fetched or cached? Resumes at chapter 1 or where it left off? |
| g | Same card on a second player, if there is one | Any per-device header or id? |
| h | Player on a phone hotspot, insert again | Client IP follows the player's network → confirms direct fetch |
| i | Stop the probe, insert the card | Offline behaviour (does the card still start; what's heard) |
| j | Play the playlist in the Yoto app, if it allows | App UA/IP vs player, i.e. whether the app or a Yoto proxy fetches |

Then send `.stream-probe/hits.jsonl` back to the project so the results can be written up here.

## Results

_Not run yet._

| # | Result |
|---|---|
| a | |
| b | |
| c | |
| d | |
| e | |
| f | |
| g | |
| h | |
| i | |
| j | |
