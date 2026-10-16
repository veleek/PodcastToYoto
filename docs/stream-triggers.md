# Spike: Yoto stream tracks as triggers

Question: can a MYO **stream track** (a track whose `trackUrl` points at a URL we serve) act as an input? Examples:
mark a card "activated" for the week when its first track is played, fire a Home Assistant action when a track
starts, or run a puzzle-hunt card that makes the lights blink. How fast is it, and what does the player's request
tell us?

Status: desk research done, live tests pending (need a real player). Direction as of 2026-10-16: MQTT player events, see "MQTT puzzle mechanics". Tools: `tools/StreamProbe` (logging server) and
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

## MQTT puzzle mechanics

Ben's direction (2026-10-16): MQTT is the main route. The puzzle ideas are to insert a card and then enter a "passcode"
with button presses, or play tracks in a specific order, or require headphones. A correct answer shows something on
the display or plays custom audio.

What the [MQTT docs](https://yoto.dev/players-mqtt/mqtt-docs) expose for each one:

| Mechanic | Input available | Verdict |
|---|---|---|
| Card inserted | `status.cardInserted`, `status.activeCard`; events `cardId` with `source: card` | Supported |
| Tracks played in order | Each event carries `chapterKey`/`trackKey`. The puzzle state machine keys on the sequence of chapters reached. | Supported if an event is published on every chapter change. Needs testing |
| Button "passcode" | **No raw button events.** A button press shows up only as its effect: a chapter or track change (`source: button`), play/pause (`playbackStatus`), or a `volume` change. | Feasible as a code made of effects: skip forward/back to chapter N, pause, or set the volume dial to positions. A literal press sequence isn't visible |
| Headphones | `status.headphones` (wired) and `status.bluetoothHp` | Supported if status is pushed on change. Otherwise the server asks for `status/request` on each puzzle step |
| Show something | `command/display/preview` `{ uri, timeout, animated }` shows **one 16×16 icon** for `timeout` seconds | Small pixel art only. Several previews in a row can make a short animation or "spell" something. The URI format is undocumented; test a yoto icon `mediaUrl` and a `yoto:#` ref |
| Play custom audio | `command/card/start` `{ uri: "https://yoto.io/{cardId}", chapterKey, trackKey, secondsIn, cutOff, anyButtonStop }` | Supported. The reward is a hidden MYO playlist on the account. It plays without inserting its card, and `cutOff` plays just a clip. For dynamic audio, the reward playlist can be stream tracks from StreamProbe |
| Other outputs | `volume/set`, `ambients/set` (nightlight colour), `sleep-timer/set`, `card/stop` | Nightlight colour is another feedback channel. The payload shape needs testing |

Constraints:
- Only players on Ben's account, so the puzzle runs on his own players.
- The token needs `family:devices:view` and `family:devices:control` ([scopes](https://yoto.dev/authentication/scopes/)).
  The app's default scopes don't include them, so the dev-portal app may need those scopes enabled, and a fresh login.
- Broker: AWS IoT over `wss`, username `{deviceId}?x-amz-customauthorizer-name=PublicJWTAuthorizer`, password = access
  token, client id `DASH{deviceId}`. There's one connection per player. Idle connections drop after ~5 min, so
  publish `events/request` every 295 s.
- `anyButtonStop: true` on a reward `card/start` would end it on any press. It's useful for "press to continue".
- Overriding the inserted card: if `card/start` plays a different playlist while the puzzle card is in, it isn't
  known what happens when the child presses a button, or whether removing and reinserting returns to the card.

### MQTT test (`YotoSpike mqtt`)

```powershell
cd tools/YotoSpike
dotnet run -- login --scopes "user:content:view user:content:manage family:devices:view family:devices:control offline_access"
dotnet run -- mqtt --status-every 0            # every player on the account; --device <id|name> for one
```

It prints one line per message, with the gap since the previous one, and writes full payloads to
`.yoto-spike/mqtt.jsonl` (and the device list to `devices.json`). Commands typed at the prompt go to every connected
player: `status`, `events`, `pause`, `resume`, `stop`, `vol <n>`, `play <cardId> [chapterKey] [trackKey]
[secondsIn] [cutOff]`, `preview <icon uri> [seconds] [animated]`, and `raw <command path> <json>`.

The connection code follows Yoto's docs but is **untested**. The container I built it in can't open websockets
through its proxy. If it fails to connect, the log line and the exception text say why.

| # | Do | Answers |
|---|---|---|
| m1 | Insert a card | Which messages arrive, how long after insert, and does `status` arrive unprompted |
| m2 | Skip forward 3 chapters, back 1, quickly | One event per chapter change, or coalesced? Latency per press |
| m3 | Pause/resume, turn the volume up 2 and down 1 | Does each press produce an event? Volume units |
| m4 | Plug in and unplug headphones (and BT if available) | Pushed on change, or only on `status`? |
| m5 | `preview <a yoto icon mediaUrl> 5`, then a `yoto:#…` icon ref | Which URI format works, and how fast it shows |
| m6 | With a card in, `play <other cardId> 01 01 0 10` | Plays a 10 s clip without inserting the card? What happens after the cut-off, and on the next button press? |
| m7 | `raw ambients/set {"r":255,"g":0,"b":0}` (guessed shape) | Nightlight colour as feedback |
| m8 | Leave it running for 10 min, idle | Keep-alive and reconnect work |

## Live test (stream tracks)

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
