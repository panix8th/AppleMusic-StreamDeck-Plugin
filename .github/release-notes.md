**AppleMusicBridge helper, preview for testing.** The Stream Deck plugin itself is not part of this release yet.

`AppleMusicBridge.exe` is the small background program the Stream Deck plugin starts. It reads and controls the
**Apple Music app for Windows** (Microsoft Store app, not iTunes) through Windows' media session API. It is
self-contained (no .NET installation needed) and was built and unit-tested by GitHub Actions from the tagged commit.

### Try it

1. Download `AppleMusicBridge.exe`. It is unsigned, so Windows may warn about it: run
   `Unblock-File .\AppleMusicBridge.exe` in PowerShell, or choose *More info → Run anyway* in SmartScreen.
2. Play a song in Apple Music.
3. In PowerShell, in the download folder:
   - `.\AppleMusicBridge.exe --list-sessions` shows every media session and which one is Apple Music.
   - `.\AppleMusicBridge.exe --debug` prints a JSON `state` line on every change. Type `togglePlayPause`,
     `next`, `previous`, `toggleShuffle`, `cycleRepeat`, `focus` or `refresh` and press Enter.
     Quit with Ctrl+Z, Enter.

The full checklist is in the README, section *Testing the helper on its own*.

### Verify the download

`AppleMusicBridge.exe.sha256` holds the SHA-256 checksum. Compare it with:

```powershell
Get-FileHash .\AppleMusicBridge.exe -Algorithm SHA256
```
