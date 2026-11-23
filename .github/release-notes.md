**Apple Music for Stream Deck (Windows).** Controls the **Apple Music app for Windows** (Microsoft Store app,
not iTunes): album cover with the song title, play/pause, next, previous. Shuffle and repeat keys are included
but shown as unavailable, because Apple Music does not offer them to Windows' media controls.

### Install

1. Download `com.panix8th.applemusic.streamDeckPlugin` and double-click it. Stream Deck
   (7.1 or newer) installs it; the actions appear under **Apple Music**.
2. Drag *Now Playing*, *Play/Pause*, *Next Track* and *Previous Track* onto your keys.

The plugin starts `AppleMusicBridge.exe` (included) in the background. It never launches Apple Music; while Apple
Music is closed the keys are dimmed.

### Files

| File | What it is |
| --- | --- |
| `com.panix8th.applemusic.streamDeckPlugin` | the plugin installer (includes the helper) |
| `AppleMusicBridge.exe` | the helper on its own, for testing in a terminal (`--list-sessions`, `--debug`) |
| `*.sha256` | SHA-256 checksums: compare with `Get-FileHash <file> -Algorithm SHA256` in PowerShell |

Built and tested by GitHub Actions on Windows from the tagged commit. The files are unsigned, so Windows may
warn about them.
