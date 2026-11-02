# Audio Router

A small Windows tray app for per-app audio. Pick which output each app plays through, and have each app come back at the volume you left it.

## What it does

- **One row per app.** Apps are listed once each, not once per device.
- **Saved volume and mute.** Move an app's slider and that level is saved. The next time the app starts making sound, it's set back to that level.
- **Per-app output.** Choose an output device for any app. The choice sticks across restarts.
- **Backup output.** If the chosen device is unavailable (Bluetooth earbuds switched off, say), the app goes to its backup instead, and moves back automatically when the device reconnects.
- **Matched by app name.** Rules are keyed on the exe name (`spotify.exe`), not its full path, so apps that update into new folders (Discord and friends) keep their settings.
- **Master volume** for the current default output at the top.

## Install

1. Open the latest run of the **build** workflow under the repo's **Actions** tab and download the `AudioRouter` artifact (or grab `AudioRouter.exe` from a release).
2. Put `AudioRouter.exe` anywhere you like and run it. No installer, no .NET install needed.
3. Left-click the tray icon to open the mixer. Right-click for **Start with Windows**, the settings folder, and Exit.

Windows SmartScreen may warn the first time because the exe isn't signed. Click **More info → Run anyway**.

## Settings and logs

Stored in `%AppData%\AudioRouter`:

- `rules.json` holds your saved apps and the list of devices seen so far.
- `log.txt` records what the app did (devices coming and going, routes applied, errors). Handy when something misbehaves.

## Notes

- Per-app output uses the same undocumented Windows interface as the Settings app's "App volume and device preferences" page. Tested target is Windows 10 (1803 or newer).
- Some apps (often games) only pick up an output change after a restart.
- Only volume changes made in Audio Router are saved. Changing an app's volume elsewhere doesn't overwrite its saved level.

## Building

```
dotnet publish AudioRouter.csproj -c Release -o out
```

Requires the .NET 8 SDK. Builds on Linux too (thanks to `EnableWindowsTargeting`), but only runs on Windows.
