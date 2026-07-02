# Clicky for Windows — Agent Instructions

<!-- Single source of truth for AI coding agents working on this repo. -->
<!-- CLAUDE.md points here. Keep this file current (see "Self-update"). -->

## Repositories & reference

| | Repo | Notes |
|---|---|---|
| **This project** | https://github.com/mesomya/clicky-windows | The Windows port. What you edit. |
| **Reference (original)** | https://github.com/farzaa/clicky | Farza's original **macOS** Clicky (Swift). MIT. Our C# mirrors it file-for-file. |

The reference is not checked in here. If you need to read the original Swift, re-clone it:
```powershell
git clone https://github.com/farzaa/clicky.git   # read-only reference; do not commit into this repo
```
Original demo / context: the [viral tweet](https://x.com/FarzaTV/status/2041314633978659092). The original is a *teacher* (sees screen, talks, points) — it does **not** control the PC. Neither does this port. (A separate "agent that does things" — "Step 2" — is a future direction, not built yet.)

## Overview

System-tray companion app for Windows. Lives entirely in the tray (no taskbar window, no main window). Clicking the tray icon opens a floating control panel. Hold **Ctrl + Alt** (push-to-talk) to capture voice, transcribe it locally, and send the transcript + a screenshot of every monitor to Claude. Claude's reply is:
1. **shown as on-screen text** in a bubble next to the cursor (always — independent of audio), and
2. **spoken** via Edge TTS, and
3. can embed a `[POINT:x,y:label]` tag that flies a blue triangle cursor across the screen to point at a UI element.

This is a faithful port of the original macOS app. When unsure about intended behavior, consult the original Swift — the C# is structured to mirror it.

## The three replacements (port-defining decisions)

The original used three paid services behind a Cloudflare Worker. This port replaces each with a free equivalent and needs **no API keys**:

| Concern | Original | This port | File |
|---------|----------|-----------|------|
| Vision + chat brain | Anthropic API | Claude Code CLI (headless `stream-json`), riding the user's Claude subscription | `Brain/ClaudeCodeBrainClient.cs` |
| Speech-to-text | AssemblyAI streaming | Local Whisper (whisper.cpp via Whisper.net), Windows Speech fallback | `Audio/WhisperTranscriptionProvider.cs`, `Audio/WindowsSpeechTranscriptionProvider.cs` |
| Text-to-speech | ElevenLabs | Microsoft Edge read-aloud neural voices | `Tts/EdgeTtsClient.cs` |

The brain is a long-lived `claude -p --input-format stream-json --output-format stream-json` process. Conversation memory lives in the Claude session (`--resume`), replacing the original's manual `conversationHistory` array. Cancelling a turn kills the process; the next turn resumes the session. It is **warmed up at launch** (along with Whisper + the Edge connection) so the first interaction isn't slow.

## Architecture

- **App type:** tray-only WPF (`OutputType=WinExe`, `ShutdownMode=OnExplicitShutdown`). WinForms is referenced only for `NotifyIcon` and `Screen`; its global usings are removed in the csproj to avoid `Color`/`Application`/`Path` collisions with WPF.
- **Framework:** WPF (.NET 8, `net8.0-windows`), Per-Monitor V2 DPI aware via `app.manifest`.
- **Overlay:** one borderless transparent `OverlayWindow` per monitor. Each is click-through (`WS_EX_TRANSPARENT`), never-activating (`WS_EX_NOACTIVATE`), hidden from alt-tab (`WS_EX_TOOLWINDOW`), and excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`) so the buddy never appears in its own screenshots. Sized to the monitor **work area** (not full bounds) so it never covers the taskbar. All motion is driven from `CompositionTarget.Rendering`.
- **Hotkey:** global `WH_KEYBOARD_LL` low-level keyboard hook (no permission needed on Windows, unlike the Mac's Accessibility-gated CGEvent tap). Modifier-only Ctrl+Alt. **Runs on its own dedicated message-pump thread** (see gotchas — this is load-bearing).
- **Capture:** GDI `Graphics.CopyFromScreen` per monitor, downscaled to max 1280px longest edge, JPEG q80. No permission needed on Windows.
- **State machine:** `CompanionManager` owns voice state (idle/listening/processing/responding), the dictation manager, the hotkey monitor, the overlay manager, the brain client, and the TTS client.
- **Panel:** `WS_EX_NOACTIVATE` + `ShowActivated=false` so opening it never steals focus (which would dim the user's foreground app).

## The pipeline (one push-to-talk cycle)

1. **Ctrl+Alt down** → hotkey monitor → `CompanionManager` → `BuddyDictationManager` starts WASAPI mic capture; state → *listening* (waveform).
2. **Ctrl+Alt up** → stop capture; state → *processing* (spinner). Buffered audio → Whisper (16kHz mono) → transcript. Non-speech markers (`[BLANK_AUDIO]` etc.) are filtered to empty.
3. Empty transcript → back to *idle*, nothing sent. Non-empty → screenshot every monitor → send images+transcript to the brain.
4. Brain returns text → `CompanionManager.ParsePointingCoordinates` strips any `[POINT:x,y:label:screenN]` tag → `ResponseText` set (shows the on-screen bubble immediately) → Edge TTS speaks it (state → *responding*).
5. If a `[POINT]` tag was present, `DetectedElement*` is set → the matching monitor's overlay flies the buddy to the mapped coordinate and shows a pointer bubble, then flies back.
6. Playback finishes → *idle*.

## Animation fidelity (OverlayWindow.cs)

Ported precisely from `OverlayWindow.swift` — preserve these:
- **Cursor follow:** critically-damped spring matching SwiftUI `spring(response: 0.2, dampingFraction: 0.6)`, integrated per frame. Buddy trails the real cursor by `(+35, +25)` DIPs.
- **Pointing flight:** quadratic bezier arc, duration `clamp(distance/800, 0.6, 1.4)`s, smoothstep easing, midpoint raised by `min(distance*0.2, 80)`. Triangle rotates to the curve tangent (`atan2 + 90°`) and scale-pulses to ~1.3× mid-flight.
- **Cross-fades:** triangle/waveform/spinner all stay in the tree and cross-fade by opacity (no add/remove) to avoid pop. Triangle 0.25s, waveform/spinner 0.15s.
- **Waveform:** 5 bars, profile `[0.4,0.7,1.0,0.7,0.4]`, audio-reactive with the original's boost/ease constants.
- **Bubbles:** pop in at scale 0.5→1.0 with a glow flare that settles; small bubbles stream text char-by-char (30–60ms). The **response bubble** (Claude's full answer) wraps, is frozen at its anchor so it doesn't chase the cursor, and dwells 14–40s scaled to length.
- **Coordinate mapping:** Windows uses a top-left origin globally, so unlike the Mac original there is **no Y-axis flip** when mapping `[POINT]` pixels to screen coordinates. Screenshot pixels are scaled to the display's native resolution, then offset by the display's global origin.

## Key files

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point, single-instance mutex (2nd launch signals the running one to open its panel), startup registration, crash reporting (MessageBox + `crash.log`), `--selftest`/`--rendertest` dispatch, debug command poller |
| `CompanionManager.cs` | Central state machine + AI response pipeline + onboarding + `[POINT]` parsing + `ResponseText` + keepalive removed (see history) |
| `ClickySettings.cs` | Persisted prefs in `%APPDATA%\Clicky\settings.json` (model, cursor toggle, onboarding, STT provider, Whisper size, TTS voice, **OutputDeviceId/Name**) |
| `ClickyAnalytics.cs` | No-op stubs (original reported to Farza's PostHog; this port reports nothing) |
| `DebugTrace.cs` | Flag-gated tracing to `clicky-debug.log` next to the exe |
| `DesignSystem.cs` | `DS.Colors` / `DS.Brushes` — ported from `DesignSystem.swift` |
| `Native/NativeMethods.cs` | Win32 P/Invoke: cursor, LL keyboard hook + message pump, ex-styles, capture exclusion, SetWindowPos, GetAsyncKeyState |
| `Hotkey/GlobalPushToTalkShortcutMonitor.cs` | Ctrl+Alt hook on a **dedicated thread**; reads true key state via `GetAsyncKeyState` |
| `Capture/CompanionScreenCaptureUtility.cs` | Multi-monitor GDI capture, cursor-screen-first ordering |
| `Audio/BuddyDictationManager.cs` | Push-to-talk pipeline: NAudio WASAPI capture, audio power for the waveform, finalize/reset |
| `Audio/BuddyTranscriptionProvider.cs` | Provider protocol + factory (mirrors the original's pluggable layer) |
| `Audio/WhisperTranscriptionProvider.cs` | Local Whisper STT, model download/caching to `%APPDATA%\Clicky\models`, shared factory, non-speech-marker filtering |
| `Audio/WindowsSpeechTranscriptionProvider.cs` | Windows built-in STT fallback (System.Speech) |
| `Audio/BuddyAudioConversionSupport.cs` | Mono float conversion, linear resample to 16kHz, RMS |
| `Brain/ClaudeCodeBrainClient.cs` | Drives the `claude` CLI as the vision brain; CLI discovery, warm-up, per-turn timeout |
| `Tts/EdgeTtsClient.cs` | Edge read-aloud websocket TTS (Sec-MS-GEC token) + **WASAPI output to the resolved device** + startup warm-up |
| `Ui/OverlayWindow.cs` | The buddy overlay + response bubble + `OverlayWindowManager` |
| `Ui/TrayIconManager.cs` | Tray icon (runtime-drawn triangle) + panel lifecycle |
| `Ui/CompanionPanelWindow.cs` | Dark control panel: status, model picker, STT picker, setup rows, start/quit |
| `SelfTest.cs` / `OverlayRenderTest.cs` | Headless verification harnesses (see below) |

## Build, run, publish

.NET 8 SDK. In this dev sandbox the SDK is user-local: use `& "$env:USERPROFILE\.dotnet\dotnet.exe"` instead of a bare `dotnet`.

```powershell
# build
& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/Clicky/Clicky.csproj -c Release

# self-contained publish (~172MB unzipped / ~69MB zipped, no .NET needed on target)
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src/Clicky/Clicky.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Releases are GitHub Releases with the zipped `publish/win-x64` (strip `*.mp3`, `debug-*.flag`, logs first). Current: v0.1.0, v0.1.1.

## Verification harness (no human, no UI)

`Clicky.exe --selftest <name> [arg]` — each logs to `%TEMP%\clicky-selftest.log`:

| Command | What it proves |
|---|---|
| `point` | `[POINT]` tag parsing assertions |
| `tts` | Edge TTS synth + playback |
| `brain` | screenshot → Claude → response round trip |
| `stt` | SAPI-generated speech → Whisper transcript |
| `diagnose` | **full loop on real hardware**: records the mic, reports level, transcribes, asks Claude, speaks the answer; lists the mic/speaker devices |
| `audioroute` | plays TTS while loopback-recording the output device — proves audio physically reaches it (peak > 0) |
| `speakers [N]` | plays "device number N" through each active output device; `speakers 2` **pins** device 2 as the TTS output |
| `btdiag` | plays to the default device with no mic / right after a mic open-close / 5s after — detects whether opening the mic suspends the output (Bluetooth A2DP↔HFP) |

`Clicky.exe --rendertest` → renders the overlay visuals to `%TEMP%\clicky-overlay-render.png`.

### Debugging a live instance
Drop a **`debug-capture.flag`** file next to the exe (then launch). It: (a) disables `WDA_EXCLUDEFROMCAPTURE` so the overlay shows in screenshots, (b) enables `DebugTrace` → `clicky-debug.log` next to the exe (every state transition + pipeline stage), (c) enables a 1s command poller. Command files next to the exe (flag-gated):
- `debug-start-onboarding.flag` → triggers onboarding.
- `debug-say.txt` → runs the real screenshot→Claude→TTS pipeline on its text, **bypassing the mic** (great for reproducing response/audio bugs without speaking).

**These flags must never ship** — they're gitignored. To see the buddy without computer-use: set the flag, launch via explorer, then GDI `CopyFromScreen` from PowerShell (`System.Drawing.Bitmap` + `Graphics.CopyFromScreen`) and crop.

## Gotchas (the ones that bite)

**Build / CLI**
- **WinForms global usings are removed** in the csproj. Import `System.Drawing`/`System.Windows.Forms` explicitly or fully-qualify. Watch `Color`, `Application`, `Path`, `Rectangle`, `Point`, `Brushes`. `WasapiCapture` is in `NAudio.CoreAudioApi`; `WasapiLoopbackCapture` is in `NAudio.Wave`.
- **`--mcp-config` must be `{"mcpServers":{}}`**, not `{}`. **stdin to the CLI must be BOM-less UTF-8** (`new UTF8Encoding(false)`).
- **Don't commit `*.mp3`** (copyrighted onboarding music; build globs `assets/*.mp3` so absence is fine).

**Hotkey**
- **The `WH_KEYBOARD_LL` hook MUST run on its own message-pump thread, not the UI thread.** Symptom if it doesn't: hotkey works, then goes dead while the app is still alive ("cursor is dead / won't listen"). Windows silently removes an LL hook whose owning thread stalls > `LowLevelHooksTimeout` (~300ms), and the UI thread constantly stalls (render loop, panel rebuilds). Read true Ctrl/Alt state with `GetAsyncKeyState` per event so a missed event can't leave it stuck.

**Overlay / windows**
- **Don't remove `WDA_EXCLUDEFROMCAPTURE`** from overlay/panel windows — it keeps the buddy out of the AI's screenshots.
- Overlay covers the **work area**, not full monitor bounds — a full-monitor topmost layered window over the taskbar makes Windows disable taskbar transparency ("faded") and can dim other windows. Panel must be `WS_EX_NOACTIVATE` so it doesn't steal focus (which dims the user's app).

**Audio (the hard part)**
- **Play TTS to the exact default render endpoint via WASAPI, not WaveOut.** `WaveOutEvent { DeviceNumber = -1 }` routes to the built-in laptop speakers on Bluetooth setups → audio "plays" but is inaudible on headphones. Use `WasapiOut(MMDevice, Shared)` + `MediaFoundationResampler` to the device mix format. Playback init runs on a background thread with an **8s timeout** because WASAPI init can block forever on a flaky Bluetooth device.
- **Edge TTS 403 at the websocket handshake = stale version.** Bump `EdgeVersion`/`UserAgent` in `EdgeTtsClient.cs` to current edge-tts values. First-call cold start can be ~24s (DNS/TLS/IPv6) — that's why TTS is warmed up at launch.
- **Bluetooth is the flakiest output.** Opening the *headset's own* mic forces HFP mono (suspends A2DP → silence); opening a *Communications-category* render stream on the headset does too. Opening a **different** (laptop) mic does not. The bigger real-world failure is the BT device *flickering connected/disconnected*, so "default" flips between the headset and laptop speakers second-to-second. Mitigations shipped: WASAPI-to-resolved-device, an **output device picker** (`ClickySettings.OutputDeviceId`, `--selftest speakers`), and the **on-screen text bubble** so answers are usable regardless of audio. The durable *user-side* fix (per MS docs) is to uncheck **Hands-Free Telephony** for the headset (Control Panel → Devices and Printers → device → Properties → Services) so it stays A2DP-only. NOTE: a keepalive tone was tried and reverted — it made playback init hang.

**Dev sandbox (if you're a Claude Code agent in the same environment)**
- This shell redirects `%LOCALAPPDATA%`, `AppData\Local\Temp`, `AppData\Roaming` into a Packages container. **Documents/Desktop are NOT redirected.** So install/run the app under `Documents\Clicky` (real, user-visible), and have `DebugTrace`/logs write next to the exe (also under Documents) so you can read them. `settings.json` lives at the **real** `%APPDATA%\Clicky\settings.json`.
- **Launch the exe unsandboxed** via `Start-Process explorer.exe -ArgumentList <exe>` so it gets real AppData + real global hooks.
- Injected keys (`keybd_event`) don't auto-repeat like a physical hold, so they're **not** faithful for testing push-to-talk timing — use real key presses (or `debug-say.txt` to bypass the mic).
- computer-use screen access needs the user to approve a dialog (often times out here) — prefer the `debug-capture.flag` + GDI-screenshot route.

## Current status / known issues

Working, verified on-device: no-crash startup, tray + panel, onboarding, cursor-follow + pointing flight, Whisper STT on real mic, Claude brain, `[POINT]` mapping, Edge TTS routed to the chosen device, on-screen answer bubble.

**Open bug (under investigation):** the **2nd/3rd consecutive push-to-talk sometimes doesn't start listening** — the waveform flashes and 0 audio is captured (empty transcript → nothing happens), while the 1st works. Likely a dictation-session reset/race or mic re-acquire timing after a full interaction. Reproduce with `debug-capture.flag` + **real** Ctrl+Alt presses and read `clicky-debug.log` (look for `hotkey pressed but IGNORED` vs `whisper: 0 captured`). This is the next thing to fix.

**Environmental (not a code bug):** the test user runs Bluetooth AirPods with an unstable connection; audio output can drop. The text bubble exists so the app is usable anyway.

## Conventions

Ported from the original's AGENTS.md and kept:
- **Clarity over concision.** Long, specific names. A reader with zero context should understand a name on sight. No single-letter variables.
- Comments explain **why**, especially for Win32 interop, animation math, and audio-device quirks.
- Match the original's behavior; don't add features or "improvements" beyond the port unless asked.
- Commit messages: imperative, explain the "why". End with the Co-Authored-By trailer.

## Self-update

When you change something that affects this doc (new files, architecture shifts, build changes, new gotchas, fixing the open bug), update this file to match.
