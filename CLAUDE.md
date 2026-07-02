# CLAUDE.md

**Read [AGENTS.md](./AGENTS.md) first — it is the single source of truth for this repo.** This file is a quick-orientation pointer so nothing important is missed; every detail (architecture, pipeline, gotchas, current status) lives in AGENTS.md.

## What this is

Clicky for Windows — a tray-only WPF app that is a **keyless Windows port** of Farza's macOS Clicky.
- **This repo:** https://github.com/mesomya/clicky-windows
- **Reference (original macOS, Swift):** https://github.com/farzaa/clicky — the C# here mirrors it; re-clone it read-only if you need to compare (`git clone https://github.com/farzaa/clicky.git`).

Hold **Ctrl+Alt**, talk → it transcribes locally (Whisper), sends the words + a screenshot of your screens to Claude, then **shows the answer as on-screen text** and **speaks it** (Edge voice), and can fly a blue triangle to **point** at things. No API keys: the brain is the local **Claude Code CLI** on your subscription.

## Quick reference

```powershell
# build / run  (dev sandbox: dotnet is user-local)
& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/Clicky/Clicky.csproj -c Release
.\src\Clicky\bin\Release\net8.0-windows\Clicky.exe

# headless verification (logs to %TEMP%\clicky-selftest.log)
Clicky.exe --selftest point|tts|brain|stt|diagnose|audioroute|speakers|btdiag
Clicky.exe --rendertest        # overlay -> %TEMP%\clicky-overlay-render.png
```

- **Source:** `src/Clicky/` (state machine `CompanionManager.cs`; overlay `Ui/OverlayWindow.cs`; brain `Brain/ClaudeCodeBrainClient.cs`; STT `Audio/`; TTS `Tts/EdgeTtsClient.cs`; hotkey `Hotkey/`).
- **Live debugging:** drop `debug-capture.flag` next to the exe → tracing to `clicky-debug.log` + overlay visible to screenshots + `debug-say.txt` to run the pipeline without the mic. Never commit these.

## The three biggest traps (full list in AGENTS.md)

1. **The keyboard hook must live on its own message-pump thread**, or the hotkey silently dies after a UI stall.
2. **TTS must play via WASAPI to the exact default/pinned device** — WaveOut misroutes to laptop speakers on Bluetooth ("I hear nothing"). Bluetooth output is inherently flaky; the on-screen text bubble exists so answers work regardless.
3. **WinForms global usings are removed** in the csproj — fully-qualify `Color`/`Application`/`Path`/`Point`/`Rectangle`/`Brushes`.

## Current open bug

The **2nd/3rd consecutive Ctrl+Alt sometimes doesn't start listening** (waveform flashes, 0 audio captured, empty transcript). The 1st works. Reproduce with `debug-capture.flag` + **real** key presses (injected keys aren't faithful), read `clicky-debug.log`. See AGENTS.md → "Current status / known issues".
