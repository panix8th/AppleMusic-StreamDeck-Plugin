# Plan (approved)

Stream Deck plugin for Windows that controls the Microsoft Store **Apple Music** app
(not iTunes; the iTunes COM API is not used anywhere).

## Decisions

| Topic | Decision |
| --- | --- |
| Stream Deck SDK | `@elgato/streamdeck` v3 (3.0.6), scaffolded with `@elgato/cli` (1.10.5) |
| Requirements from the SDK docs | Node.js 24+ to build, Stream Deck 7.1+, manifest `SDKVersion: 3`, `Nodejs.Version: "24"` |
| Plugin UUID | `com.panix8th.applemusic` (must never change once keys are placed) |
| Apple Music access | Windows System Media Transport Controls (`Windows.Media.Control`) |
| Bridge | C# console helper `AppleMusicBridge.exe` (.NET 8, `net8.0-windows10.0.19041.0`, self-contained single-file, win-x64) spawned once by the plugin |
| Transport | Newline-delimited JSON: helper stdout = protocol only, helper stderr = logs (forwarded into the plugin log) |
| Album art | Resized to 144x144 PNG by the helper; `albumArt` is only sent when the cover changes, `albumArtId` is always sent |
| App detection | `appRunning` = Apple Music media session exists OR Apple Music process exists; re-checked on every media event and on `refresh`, which the plugin sends when Stream Deck reports the exe launching/terminating (`ApplicationsToMonitor`) |
| Never launch Apple Music | No activation APIs are used; "bring to front" only touches an existing window |
| Property Inspector | None in phase 1 (nothing needs configuring) |

## Repository layout

```
helper/AppleMusicBridge/   C# helper
plugin/                    Stream Deck plugin (TypeScript, rollup)
plugin/icons/              SVG icon sources, exported to PNG @1x/@2x
docs/                      plan + protocol
```

## Steps

1. Helper on its own: build, run in a terminal with `--debug`, confirm the JSON state events and the
   commands, and read the exact Apple Music `SourceAppUserModelId` + process name on the target machine.
   Then hard-match on that ID.
2. Plugin: helper process manager (spawn once, auto-restart with backoff, kill on exit), actions:
   Now Playing, Play/Pause, Next, Previous, Shuffle, Repeat. Dimmed "not running" state.
3. Phase 2 (only after phase 1 works): Stream Deck+ dial for Apple Music's per-app volume
   (Core Audio `ISimpleAudioVolume` via NAudio), press = mute, touch strip layout with cover/title/volume.

## Known risks

- Apple Music may not expose shuffle/repeat through SMTC; then those keys are shown as disabled.
- Windows' foreground-lock rules can block "bring to front"; worst case the taskbar button flashes.
- The self-contained single-file helper is large (~35 MB compressed, untrimmed).

## Findings from testing on Windows

- Apple Music's media session `SourceAppUserModelId` is `AppleInc.AppleMusicWin_nzyj5cx40ttqa!App`; the helper
  now matches it exactly.
- Title, artist, album, cover, play state, position and duration are all reported.
- Apple Music does not enable shuffle or repeat in its media session (`IsShuffleEnabled` / `IsRepeatEnabled`
  false, `IsShuffleActive` / `AutoRepeatMode` null), so those keys are shown crossed out.
- Release v1.0 (helper only) was published by the release workflow; from v1.1.0 releases also contain the plugin.
