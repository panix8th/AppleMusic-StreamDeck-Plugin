# Helper protocol

`AppleMusicBridge.exe` talks to the Stream Deck plugin over its standard streams:

| Stream | Direction | Content |
| --- | --- | --- |
| stdout | helper → plugin | protocol messages, one JSON object per line (`\n`), pure ASCII (non-ASCII is `\uXXXX`-escaped) |
| stdin | plugin → helper | commands, one per line |
| stderr | helper → plugin | log lines `[LEVEL] message`, LEVEL = `DEBUG`, `INFO`, `WARN`, `ERROR` |

The helper exits when stdin is closed (the plugin exited or was killed) or, when started with
`--parent-pid <pid>`, as soon as that process exits.

## helper → plugin

### `hello`
First line after start.
```json
{"type":"hello","version":"0.1.0","pid":4242,"debug":false}
```

### `state`
Sent after start, whenever something meaningful changed, and after a `refresh` command.
Position-only changes that match normal playback progress are not sent (extrapolate on your side).
```json
{"type":"state","appRunning":true,"hasSession":true,"title":"Song","artist":"Artist","album":"Album",
 "isPlaying":true,"status":"playing","shuffle":false,"repeat":"all","position":63.2,"duration":241,
 "timestamp":1764000000000,"albumArtId":"9f2c41d07a1b3e55","albumArt":"iVBORw0KGgo...",
 "supports":{"play":true,"pause":true,"playPause":true,"next":true,"previous":true,"shuffle":true,"repeat":true},
 "sourceAppId":"AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"}
```

| Field | Meaning |
| --- | --- |
| `appRunning` | Apple Music has a media session **or** its process is running |
| `hasSession` | Apple Music registered a media session (required for playback commands) |
| `title`, `artist`, `album` | strings, `""` when unknown |
| `isPlaying` | `true` while playing (kept stable while `status` is `changing`) |
| `status` | `none` (no session), `closed`, `opened`, `changing`, `stopped`, `playing`, `paused` |
| `shuffle` | `true`/`false`, or `null` if the session does not report it |
| `repeat` | `off`, `all`, `one`, or `null` if the session does not report it |
| `position`, `duration` | seconds; `duration` is `0` when unknown. `position` was sampled at `timestamp` (Unix ms) |
| `albumArtId` | id of the current cover, `null` = no cover. **Always present.** |
| `albumArt` | base64 PNG, 144×144. **Only present when `albumArtId` changed** (or after `refresh`). When absent, keep the cover you already have for that id |
| `supports` | which controls the session reports as enabled; `shuffle`/`repeat` decide whether those keys are usable |
| `sourceAppId` | the matched session's `SourceAppUserModelId` (diagnostics) |

### `result`
One per command, in order.
```json
{"type":"result","id":7,"cmd":"next","ok":true}
{"type":"result","id":8,"cmd":"toggleShuffle","ok":false,"error":"unsupported"}
```
Error codes: `not-running`, `no-session`, `unsupported`, `rejected` (Apple Music declined), `timeout`, `failed`,
`no-window`, `foreground-denied` (focus), `unknown-command`, `invalid-json`, `missing-cmd`.

## plugin → helper

```json
{"id":7,"cmd":"next"}
```
`id` is optional and can be any JSON value; it is echoed in the `result`. For manual testing a bare command name
(`next`) is accepted too.

| Command | Effect |
| --- | --- |
| `play`, `pause`, `togglePlayPause` | playback |
| `next`, `previous` | skip |
| `toggleShuffle` | flip shuffle (`unsupported` if the session does not support it) |
| `cycleRepeat` | off → all → one → off (`unsupported` if not supported) |
| `focus` | bring the existing Apple Music window to the front (never launches the app) |
| `refresh` | re-check everything and send a full `state` including `albumArt` |
| `ping` | answers `ok` |
