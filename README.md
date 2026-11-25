# Audix

Send each Windows app's sound to the speakers you choose: one app to your Bluetooth speaker, another to your laptop speakers, or the same app on several at once, kept in sync.

## Download

Get **Audix-Setup.exe** from the [latest release](https://github.com/kanmiezah/AudioMap/releases/latest) and run it.

The installer isn't code-signed yet, so Windows SmartScreen may say it "protected your PC". Click **More info → Run anyway**.

Requires Windows 10 (1809) or later, 64-bit.

## What it does

- **See what's playing where.** Each output lists the apps playing on it, with a live level meter.
- **Play an app on more speakers.** Drag an app onto a device (or use its › menu) to add it there; *All outputs* plays it everywhere.
- **Move an app.** Drag a playing chip to another device; ✕ takes it off a device.
- **Stay in sync.** When an app plays on Bluetooth and wired speakers, the wired copy is delayed to match (click a chip to fine-tune).
- **Per-app volume and mute,** like Windows' Volume mixer.
- **Defaults.** Choose where newly opened apps play, and set Windows' default output from each device's › menu.

Closing the window keeps it running in the notification area; routes stay active.

## Build from source

```powershell
dotnet publish MultiAudio.App\MultiAudio.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Then build the installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
ISCC.exe installer\Audix.iss
```

The exe is `Audix.exe`; saved settings live in `%AppData%\MultiAudio` (its original folder name).
