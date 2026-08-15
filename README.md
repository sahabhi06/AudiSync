# AudiSync
# AudiSync

A Windows desktop app that plays the same audio simultaneously across multiple Bluetooth speakers, headphones, and earbuds connected to one PC — with per-device delay and volume control, including automatic mic-based calibration, to keep everything in sync.

## Download

Grab the latest build from the [Releases](../../releases) page — download `AudiSync-vX.X.zip`, extract it anywhere, and run `AudiSync.exe`. No installation required, but see **Requirements** below — you'll need VB-CABLE installed first (the app will detect and guide you if it's missing).

## What it does

- Detects all connected playback devices and flags which ones are Bluetooth
- Lets you check any combination of devices to sync audio to
- Captures whatever your PC is currently playing (Spotify, YouTube, VLC, games, etc.) and fans it out to every checked device at once
- Automatically switches your Windows default output when syncing starts, and restores your original device when you stop
- Gives each device its own adjustable delay and volume, both changeable live while audio is playing
- **Auto-Calibrate**: one-click, mic-based automatic timing correction — plays a short test tone through each device, measures it with your PC's microphone, and sets delays automatically. Works even mid-playback (briefly mutes each device during its own test, then resumes)
- Remembers each device's delay/volume settings between app restarts
- Detects when a device disconnects mid-sync and keeps the rest playing without crashing
- Detects if the required VB-CABLE driver isn't installed and shows a clear guide instead of a cryptic error

## Why this is hard (and what we can't fix)

Windows only allows one "default" audio output at a time — there's no built-in way to send the same audio to two Bluetooth devices simultaneously. AudiSync works around this using a virtual audio cable to capture system audio, then re-streams that captured audio independently to every selected device.

Because each Bluetooth device negotiates its own connection, codec, and internal buffering, some timing drift between devices is unavoidable — even professional multi-speaker products face this. AudiSync's delay calibration (manual or automatic) compensates for this in software, typically getting devices within single-digit-to-low-double-digit milliseconds of each other — close enough that audio feels synced, even if it isn't frame-perfect at the hardware level.

**Also confirmed not possible:** splitting the two earbuds of a single wireless pair to connect to two different host devices (phones/PCs) without physically going out of range. That behavior is controlled entirely by the earbuds' own firmware and the private radio link between the two buds — no software running on a PC (including this one) has any visibility into or control over that negotiation.

## Requirements

- Windows 10/11 (64-bit)
- [VB-CABLE](https://vb-audio.com/Cable/) (free virtual audio device) — **required**. AudiSync detects if it's missing on startup and shows a download link; install it and restart the app
- One or more Bluetooth audio devices, paired and connected via Windows Bluetooth settings
- A working microphone (built-in or external, **not** Bluetooth) — only needed if you want to use Auto-Calibrate

The published release is self-contained and does **not** require .NET to be installed separately.

## Tech stack

- **C# + WPF** (.NET) — the application itself
- **NAudio** — wraps Windows' WASAPI audio engine for capture, playback, and volume control
- **System.Management** — queries Windows' paired Bluetooth device list (via WMI) to correctly identify which playback devices are Bluetooth
- **A custom `PolicyConfig` COM interop helper** — switches the Windows default playback device programmatically (the same undocumented interface tools like EarTrumpet use)
- **System.Text.Json** — persists per-device settings to a local file

## How it works (architecture)

1. **VB-CABLE** acts as a virtual "pipe": AudiSync sets it as your Windows default output, so all system audio flows into it instead of a physical speaker.
2. AudiSync captures audio from the **CABLE Output** side of that pipe using `WasapiCapture`.
3. For each device you've checked, AudiSync opens an independent `WasapiOut` playback stream with its own `BufferedWaveProvider`.
4. Every chunk of captured audio is copied into *every* selected device's buffer at the same time — this is the core "fan-out" that makes simultaneous playback possible.
5. Each device's buffer is wrapped in a `VolumeSampleProvider` for live volume control, and can have silence inserted or audio discarded from its buffer in real time to shift its delay forward or backward — no restart required.
6. **Auto-Calibrate** plays a short 2500Hz test tone through one device at a time and listens via a non-Bluetooth microphone to measure exactly when each tone arrives, then calculates and applies the relative delay needed to line every device up.
7. On Stop, AudiSync restores your original default playback device automatically.
8. Per-device delay and volume are saved to `%AppData%\AudiSync\device-settings.json`, keyed by device name, and reloaded automatically next time the app opens.

## Build history / development log

This project was built incrementally, phase by phase, with testing after every change:

**Phase 1 — Device detection**
Enumerated all Windows playback devices via NAudio's `MMDeviceEnumerator`, then cross-referenced against Windows' paired Bluetooth device list (via WMI `Win32_PnPEntity`) to correctly tag which ones are Bluetooth — simple string-matching on device names alone wasn't reliable, since Windows doesn't label Bluetooth devices by name.

**Phase 2 — Audio capture and fan-out**
Set up VB-CABLE as the capture source and implemented the core multi-device playback loop: one `WasapiCapture` feeding multiple simultaneous `WasapiOut` instances, each with its own buffer.

**Phase 3 — Automatic default-device switching**
Originally attempted using the `AudioSwitcher.AudioApi.CoreAudio` NuGet package, but this caused a COM interop conflict with NAudio (`InvalidCastException` on `MMDeviceEnumerator`) — both libraries independently wrap the same underlying Windows COM interfaces, and mixing them in one process corrupts the COM object cache. Resolved by removing AudioSwitcher entirely and implementing default-device switching directly via a minimal `PolicyConfig` COM interop wrapper.

**Phase 4 — Delay and volume calibration**
Added per-device sliders with +/- buttons for fine control, first as "apply on next Start," then upgraded to fully live adjustment — inserting silence into a device's buffer increases its delay, discarding queued audio decreases it, both without needing to restart playback.

**Phase 5 — Error handling**
Added graceful handling for devices disconnecting mid-sync (e.g., a Bluetooth earbud going out of range or powering off), using `WasapiOut.PlaybackStopped` to detect failures, clean up that device's resources, and keep the remaining devices playing uninterrupted instead of crashing.

**Phase 6 — UI polish**
Added an application icon, custom color-coded buttons, a card-style layout for the device list, and a properly resizable/scrollable window (an earlier `SizeToContent`-based approach caused the window to become unresizable and unscrollable once content exceeded screen height — fixed by switching to a normal resizable window wrapped in a `ScrollViewer`).

**Phase 7 — Automatic calibration**
Added a one-click "Auto-Calibrate" feature: plays a short test tone through each selected device one at a time, uses the PC's microphone to detect exactly when each tone is heard, and automatically calculates and applies the correct delay offset for each device relative to the fastest one. Initially used the Windows "communications" default microphone, which turned out to sometimes select the Bluetooth earbuds' own mic — forcing a profile switch (A2DP to low-quality HFP) that caused audio degradation and disconnects. Fixed by explicitly filtering out any microphone associated with a paired Bluetooth device.

**Phase 8 — Settings persistence**
Added save/load of per-device delay and volume to a local JSON file (`%AppData%\AudiSync\device-settings.json`), keyed by device friendly name so calibration work survives app restarts.

**Phase 9 — Packaging**
Published as a self-contained, single-file Release build (bundles the .NET runtime, so no separate install needed on the target PC — though a handful of native WPF support DLLs still ship alongside the main `.exe`, since those can't be merged into a single file). Added on-screen detection and guidance for the VB-CABLE dependency, so a new user without it installed gets a clear instructional banner instead of a cryptic error.

## Known issues encountered during development

- **Security software false positives**: both Windows Smart App Control and third-party antivirus (e.g. McAfee's Application Control) can block a freshly-built unsigned `.dll`/`.exe`, sometimes with a delay (blocking only after a sleep/wake cycle triggers a cloud reputation re-check). Fixed during development by adding the project folder as an exclusion in the relevant security software. End users running the packaged release may hit similar prompts on first launch since the app isn't code-signed.
- **COM interop conflicts**: mixing multiple third-party libraries that each independently wrap the same Windows COM interfaces (NAudio + AudioSwitcher) can cause cast exceptions. Avoided by using only one COM-wrapping library (NAudio) plus a minimal custom interop helper where needed.
- **Bluetooth microphone profile switching**: letting Windows pick a "default communications" microphone for calibration can silently select a Bluetooth earbud's mic, forcing a low-quality HFP profile switch. Fixed by explicitly excluding Bluetooth-associated microphones from calibration.
- **Auto-calibration accuracy**: microphone-based latency measurement includes the time sound takes to travel through air to the mic, so devices at very different distances from the PC will show a small measurement skew. Best results come from calibrating with all devices roughly equidistant from the microphone.
- **WPF single-file publishing**: a handful of native (non-.NET) DLLs required by WPF's graphics/input system can't be merged into a single-file publish. The whole output folder must be shared together, not just the `.exe`.

## Roadmap / possible future work

- Save named "device groups" (e.g., a preset for "Living room speakers")
- Left/Right channel-split mode (send different stereo channels to different devices) — proposed but not yet built
- Code-signing the release build to avoid security-software warnings on first launch for other users

## Setup instructions (for building this project yourself)

1. Install [Visual Studio Community](https://visualstudio.microsoft.com/downloads/) with the ".NET desktop development" workload
2. Install [VB-CABLE](https://vb-audio.com/Cable/) and reboot
3. Clone this repository
4. Open the solution in Visual Studio, restore NuGet packages (`NAudio`, `NAudio.Wasapi`, `System.Management`)
5. Build and run
6. Connect your Bluetooth devices via Windows Bluetooth settings before opening the app (or hit Refresh after connecting)

## Publishing a Release build yourself

1. Switch the build configuration dropdown from **Debug** to **Release**
2. Right-click the project → **Publish** → target **Folder**
3. Under settings: **Deployment mode: Self-contained**, **Target runtime: win-x64**, **Produce single file: checked**
4. Publish, then select the whole output folder (the `.exe` plus its accompanying native DLLs) and zip it together — the `.exe` alone will not run without the other files
5. Test the zip by extracting it somewhere outside your dev folder and running it standalone before sharing
