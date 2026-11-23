**Apple Music for Stream Deck (Windows).** Controls the **Apple Music app for Windows** (Microsoft Store app,
not iTunes): album cover with the song title, play/pause, next, previous, shuffle and repeat. Shuffle and repeat
go through Apple Music's own player-bar buttons (UI Automation), because Apple Music does not offer them to
Windows' media controls; its window must be open (minimized is fine).

### Install

1. Download `com.panix8th.applemusic.streamDeckPlugin` and double-click it. Stream Deck
   (7.1 or newer) installs it; the actions appear under **Apple Music**.
2. Drag *Now Playing*, *Play/Pause*, *Next Track*, *Previous Track*, *Shuffle* and *Repeat* onto your keys.

The plugin starts `AppleMusicBridge.exe` (included) in the background. It never launches Apple Music; while Apple
Music is closed the keys are dimmed.

### Files

| File | What it is |
| --- | --- |
| `com.panix8th.applemusic.streamDeckPlugin` | the plugin installer (includes the helper) |
| `AppleMusicBridge.exe` | the helper on its own, for testing in a terminal (`--list-sessions`, `--dump-ui`, `--debug`) |
| `*.sha256` | SHA-256 checksums: compare with `Get-FileHash <file> -Algorithm SHA256` in PowerShell |

Built and tested by GitHub Actions on Windows from the tagged commit. The files are unsigned, so Windows may
warn about them.
