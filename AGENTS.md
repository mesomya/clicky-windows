# Clicky for Windows — Agent Instructions

<!-- Single source of truth for AI coding agents working on this repo. -->
<!-- This is a Windows port of https://github.com/farzaa/clicky (macOS). -->

## Overview

System-tray companion app for Windows. Lives entirely in the tray (no taskbar window, no main window). Clicking the tray icon opens a floating control panel with companion voice controls. Hold **Ctrl + Alt** (push-to-talk) to capture voice, transcribe it locally, and send the transcript + a screenshot of every monitor to Claude. Claude responds with text (spoken via Edge TTS) and can embed `[POINT:x,y:label]` tags that make a blue triangle cursor fly across the screen and point at UI elements.

This is a faithful port of the original macOS app. When in doubt about intended behavior, consult the original Swift source — the C# is structured to mirror it file-for-file.

## The three replacements (port-defining decisions)

The original used three paid services behind a Cloudflare Worker. This port replaces each with a free equivalent and needs **no API keys**:

| Concern | Original | This port | File |
|---------|----------|-----------|------|
| Vision + chat brain | Anthropic API | Claude Code CLI (headless `stream-json`), riding the user's subscription | `Brain/ClaudeCodeBrainClient.cs` |
| Speech-to-text | AssemblyAI streaming | Local Whisper (whisper.cpp via Whisper.net), with Windows Speech fallback | `Audio/WhisperTranscriptionProvider.cs`, `Audio/WindowsSpeechTranscriptionProvider.cs` |
| Text-to-speech | ElevenLabs | Microsoft Edge read-aloud neural voices | `Tts/EdgeTtsClient.cs` |

The brain is a long-lived `claude -p --input-format stream-json --output-format stream-json` process. Conversation memory lives in the Claude session (via `--resume`), replacing the original's manual `conversationHistory` array. Cancelling a turn kills the process; the next turn resumes the session.

## Architecture

- **App type:** tray-only WPF (`OutputType=WinExe`, `ShutdownMode=OnExplicitShutdown`). WinForms is referenced only for `NotifyIcon` and `Screen`; its global usings are removed in the csproj to avoid `Color`/`Application`/`Path` collisions with WPF.
- **Framework:** WPF (.NET 8, `net8.0-windows`), Per-Monitor V2 DPI aware via `app.manifest`.
- **Overlay:** one borderless transparent `OverlayWindow` per monitor. Each is click-through (`WS_EX_TRANSPARENT`), never-activating (`WS_EX_NOACTIVATE`), hidden from alt-tab (`WS_EX_TOOLWINDOW`), and excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`) so the buddy never appears in its own screenshots. All motion is driven from `CompositionTarget.Rendering`.
- **Hotkey:** global `WH_KEYBOARD_LL` low-level keyboard hook (no permission needed on Windows, unlike the Mac's Accessibility-gated CGEvent tap). Modifier-only Ctrl+Alt.
- **Capture:** GDI `Graphics.CopyFromScreen` per monitor, downscaled to max 1280px longest edge, JPEG q80. No permission needed on Windows.
- **State machine:** `CompanionManager` owns voice state (idle/listening/processing/responding), the dictation manager, the hotkey monitor, the overlay manager, the brain client, and the TTS client.

## Animation fidelity (OverlayWindow.cs)

These are ported precisely from `OverlayWindow.swift` — preserve them:
- **Cursor follow:** critically-damped spring matching SwiftUI's `spring(response: 0.2, dampingFraction: 0.6)`, integrated per frame. Buddy trails the real cursor by `(+35, +25)` DIPs.
- **Pointing flight:** quadratic bezier arc, duration `clamp(distance/800, 0.6, 1.4)`s, smoothstep easing, midpoint raised by `min(distance*0.2, 80)`. Triangle rotates to the curve tangent (`atan2 + 90°`) and scale-pulses to ~1.3× mid-flight.
- **Cross-fades:** triangle/waveform/spinner all stay in the tree and cross-fade by opacity (no add/remove) to avoid pop. Triangle 0.25s, waveform/spinner 0.15s.
- **Waveform:** 5 bars, profile `[0.4,0.7,1.0,0.7,0.4]`, audio-reactive height with the same boost/ease constants as the original.
- **Bubbles:** pop in at scale 0.5→1.0 with a glow flare that settles; text streams character-by-character at 30–60ms.
- **Coordinate mapping:** Windows uses a top-left origin globally, so unlike the Mac original there is **no Y-axis flip** when mapping `[POINT]` pixels to screen coordinates. Screenshot pixels are scaled to the display's native resolution, then offset by the display's global origin.

## Key files

| File | Purpose |
|------|---------|
| `Program.cs` | Entry point, single-instance mutex, startup-registration, `--selftest`/`--rendertest` dispatch |
| `CompanionManager.cs` | Central state machine + AI response pipeline + onboarding + `[POINT]` parsing (`ParsePointingCoordinates`) |
| `ClickySettings.cs` | Persisted prefs in `%APPDATA%\Clicky\settings.json` |
| `DesignSystem.cs` | `DS.Colors` / `DS.Brushes` — ported from `DesignSystem.swift` |
| `Native/NativeMethods.cs` | Win32 P/Invoke: cursor, keyboard hook, ex-styles, capture exclusion, SetWindowPos |
| `Hotkey/GlobalPushToTalkShortcutMonitor.cs` | Ctrl+Alt low-level keyboard hook, transition publisher |
| `Capture/CompanionScreenCaptureUtility.cs` | Multi-monitor GDI capture, cursor-screen-first ordering |
| `Audio/BuddyDictationManager.cs` | Push-to-talk pipeline: NAudio WASAPI capture, audio power for the waveform |
| `Audio/BuddyTranscriptionProvider.cs` | Provider protocol + factory (mirrors the original's pluggable layer) |
| `Audio/WhisperTranscriptionProvider.cs` | Local Whisper STT, model download/caching, shared factory |
| `Audio/WindowsSpeechTranscriptionProvider.cs` | Windows built-in STT fallback (System.Speech) |
| `Audio/BuddyAudioConversionSupport.cs` | Mono float conversion, linear resample to 16kHz, RMS |
| `Brain/ClaudeCodeBrainClient.cs` | Drives the `claude` CLI as the vision brain |
| `Tts/EdgeTtsClient.cs` | Edge read-aloud websocket TTS (Sec-MS-GEC token) |
| `Ui/OverlayWindow.cs` | The buddy overlay + `OverlayWindowManager` |
| `Ui/TrayIconManager.cs` | Tray icon (runtime-drawn triangle) + panel lifecycle |
| `Ui/CompanionPanelWindow.cs` | Dark control panel: status, model picker, STT picker, setup rows, start/quit |
| `SelfTest.cs` / `OverlayRenderTest.cs` | Headless verification harnesses |

## Build & run

```powershell
dotnet build src/Clicky/Clicky.csproj -c Release
.\src\Clicky\bin\Release\net8.0-windows\Clicky.exe
```

Verification (no UI needed):
```powershell
Clicky.exe --selftest point|tts|brain|stt    # logs to %TEMP%\clicky-selftest.log
Clicky.exe --rendertest                        # -> %TEMP%\clicky-overlay-render.png
```

## Gotchas

- **WinForms global usings are removed** in the csproj. If you use `System.Drawing` or `System.Windows.Forms` types, import them explicitly or fully-qualify. Watch for `Color`, `Application`, `Path`, `Rectangle`, `Point`, `Brushes` ambiguity.
- **`--mcp-config` must be `{"mcpServers":{}}`**, not `{}`. The CLI rejects the latter.
- **stdin to the CLI must be BOM-less UTF-8** (`new UTF8Encoding(false)`), or the CLI's stream-json parser chokes on the BOM.
- **The Edge TTS version string goes stale.** A 403 at the websocket handshake means `EdgeVersion`/`UserAgent` in `EdgeTtsClient.cs` need bumping to the current edge-tts values.
- **Don't commit `*.mp3`** — the original's onboarding music is copyrighted. The build globs `assets/*.mp3` so its absence is fine.
- **Don't remove `WDA_EXCLUDEFROMCAPTURE`** from overlay/panel windows — it's what keeps the buddy out of the screenshots sent to the AI.

## Conventions

Ported from the original's AGENTS.md and kept:
- **Clarity over concision.** Long, specific names. A reader with zero context should understand a name on sight. No single-letter variables.
- Comments explain **why**, especially for Win32 interop and animation math.
- Match the original's behavior; don't add features or "improvements" beyond the port unless asked.

## Self-update

When you change something that affects this doc (new files, architecture shifts, build changes, new conventions), update this file to match.
