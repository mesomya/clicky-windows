# Clicky for Windows

An AI buddy that lives next to your cursor. Hold a hotkey, talk to it, and it sees your screen and helps — it can even fly over and point at things. Kinda like having a teacher sitting next to you.

This is a **Windows port** of [Clicky](https://github.com/farzaa/clicky), the lovely macOS app made by [Farza](https://x.com/farzatv). All the credit for the idea, the design, and the original implementation is his. This repo exists because Clicky is Mac-only and some of us are on Windows.

> **Status:** works end-to-end — push-to-talk, screen vision, spoken answers, and the pointing cursor. It's a faithful port, not a pixel-perfect clone. See [What's different](#whats-different-from-the-mac-version).

---

## What it does

1. You hold **Ctrl + Alt** (the push-to-talk hotkey) and speak.
2. Clicky transcribes your voice, grabs a screenshot of your screen(s), and sends both to Claude.
3. Claude answers, and the answer is **spoken aloud**.
4. If it helps, a little blue triangle **flies across your screen and points** at whatever it's talking about.

It lives in your system tray — no taskbar window, no clutter. Click the tray icon for the control panel.

---

## The big idea: it's free to run

The original Clicky needs three paid API keys (Anthropic, AssemblyAI, ElevenLabs) wired through a Cloudflare Worker. This port replaces every paid piece with a free one, so **you don't need a single API key or a credit card.**

| Job | Original (Mac) | This port (Windows) | Cost |
|-----|---------------|---------------------|------|
| **Brain** (vision + chat) | Anthropic API (paid) | Your **Claude Code** subscription, driven via its CLI | **$0** — uses what you already pay for |
| **Ears** (speech-to-text) | AssemblyAI streaming (paid) | **Local Whisper** (runs on your CPU) | **$0**, fully offline |
| **Voice** (text-to-speech) | ElevenLabs (paid) | **Microsoft Edge** neural voices | **$0** |
| **Key storage** | Cloudflare Worker | not needed — no keys to hide | **$0** |

The only requirement is that you have **Claude Code installed and signed in** (which you do, if you're reading this). Clicky shells out to the `claude` CLI in headless streaming mode and rides your existing plan. Every voice answer counts against your normal Claude usage, same as if you'd typed it.

---

## Requirements

- **Windows 10 (2004+) or Windows 11**
- **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** (to run) or **.NET 8 SDK** (to build)
- **[Claude Code](https://claude.com/claude-code)** installed and signed in — this is the brain
- A **microphone**
- Internet (for the Claude brain and the Edge voice; only Whisper transcription is offline)

---

## Setup

```powershell
git clone https://github.com/mesomya/clicky-windows.git
cd clicky-windows
dotnet build src/Clicky/Clicky.csproj -c Release
```

Then run it:

```powershell
.\src\Clicky\bin\Release\net8.0-windows\Clicky.exe
```

On first launch:
1. The control panel opens from the tray. It checks two things: **Microphone** access and **Claude Code** being installed.
2. If the microphone is blocked, click **Grant** — it opens Windows' microphone privacy settings. Make sure "Let desktop apps access your microphone" is on.
3. Hit **Start**. Clicky introduces itself, then prompts you to hold **Ctrl + Alt** and say hi.

The first time you use voice, Clicky downloads the Whisper speech model (~150 MB) once into `%APPDATA%\Clicky\models`. After that it's instant and offline.

> No build? Once a release is cut, a prebuilt `Clicky.exe` will be attached to the [Releases](https://github.com/mesomya/clicky-windows/releases) page so you can skip the SDK.

---

## What's different from the Mac version

I tried to keep these to an absolute minimum. Every change is here either because the original relied on something Apple-only, or because the original relied on something paid.

**Swapped because it was paid (now free):**
- **Brain:** Anthropic API → your Claude Code subscription via the CLI. Same Claude models. Answers come back a couple seconds slower than a raw API call because a CLI process is involved, but it's free.
- **Speech-to-text:** AssemblyAI live streaming → local Whisper. The original also shipped a "buffer then transcribe on release" mode (its OpenAI fallback), so this matches that behavior rather than true live streaming.
- **Text-to-speech:** ElevenLabs → Edge neural voices. Very natural, but a different voice than the original's.

**Swapped because it was Apple-only:**
- **Menu bar** → **system tray.** Same idea, same minimal control panel.
- **ScreenCaptureKit** → **GDI capture.** On Windows this needs no permission at all.
- **Accessibility-based global hotkey (CGEvent tap)** → **low-level keyboard hook.** Also needs no special permission on Windows. The shortcut is **Ctrl + Alt** (the Windows cousin of the Mac's Ctrl + Option).
- **Native AppKit overlay** → **WPF transparent overlay**, click-through and excluded from screen capture so the buddy never appears in its own screenshots.

**Dropped:**
- The original streamed a hosted **intro video** during onboarding. That's Mac-app-specific content, so the port does welcome bubble → "try it" prompt → live pointing demo instead — same beats, no video.
- **PostHog analytics** is a no-op here. Your usage stays on your machine.
- **Sparkle auto-update** (Mac-only framework) isn't wired up.

Everything else — the state machine, the system prompt, the `[POINT:x,y]` pointing protocol, the bezier-arc flight animation, the spring cursor-follow, the waveform, the spinner, the speech bubbles, the dark blue design system — is ported as faithfully as I could manage.

---

## About local Whisper (the thing you were worried about)

Whisper runs on your **CPU**, but it only runs for a few seconds *after you release the hotkey* — never continuously, and never in the background. It won't sit there eating your battery or freezing your machine while you work. Transcription happens on a background thread, so the app and your cursor never freeze even while it's thinking.

On a modest laptop (e.g. an Intel i3), the default **`base`** model transcribes a short question in roughly **3–8 seconds**. If that feels slow:

- Open the panel → **Speech to Text** → switch to **Windows** to use Windows' built-in speech recognition instead. It's instant and fully offline, just a bit less accurate.
- Or edit `%APPDATA%\Clicky\settings.json` and set `"WhisperModel": "tiny"` for ~2–3× faster transcription at slightly lower accuracy.

Idle memory sits around ~200 MB (the .NET/WPF runtime plus the loaded model). It does not grow while idle.

---

## Verify your setup

The app has built-in smoke tests so you can confirm each free replacement works on your machine:

```powershell
$exe = ".\src\Clicky\bin\Release\net8.0-windows\Clicky.exe"
& $exe --selftest point   # [POINT] tag parsing (instant)
& $exe --selftest tts     # Edge voice — you'll hear Clicky speak
& $exe --selftest brain   # screenshot -> Claude -> response (uses your plan)
& $exe --selftest stt     # generates speech, transcribes it with Whisper
& $exe --rendertest       # renders the buddy's visuals to %TEMP%\clicky-overlay-render.png
```

Results print to `%TEMP%\clicky-selftest.log`.

---

## How it works

Clicky is a tray-only WPF app (`Clicky.exe`). Holding **Ctrl + Alt** is detected by a low-level keyboard hook. While held, the microphone is captured with NAudio and fed to the active transcription provider. On release, the transcript plus a GDI screenshot of every monitor go to the Claude Code CLI (a long-lived `claude -p --input-format stream-json` process that keeps conversation memory across turns). Claude's reply is spoken through Edge's read-aloud websocket. If the reply ends with a `[POINT:x,y:label]` tag, the overlay maps those screenshot pixels to the right monitor and animates the blue triangle along a bezier arc to point at it.

```
src/Clicky/
  Program.cs                         App entry, single-instance, startup registration, selftest dispatch
  CompanionManager.cs                Central state machine (idle/listening/processing/responding)
  ClickySettings.cs                  Persisted prefs (%APPDATA%\Clicky\settings.json)
  DesignSystem.cs                    Colors, radii — ported from the original's DesignSystem.swift
  Native/NativeMethods.cs            Win32 interop (cursor, hooks, click-through, capture exclusion)
  Hotkey/GlobalPushToTalkShortcutMonitor.cs   Ctrl+Alt low-level keyboard hook
  Capture/CompanionScreenCaptureUtility.cs    Multi-monitor GDI screenshots
  Audio/BuddyDictationManager.cs     Push-to-talk pipeline (NAudio capture + provider)
  Audio/WhisperTranscriptionProvider.cs       Local Whisper STT (default)
  Audio/WindowsSpeechTranscriptionProvider.cs Windows built-in STT (fallback)
  Brain/ClaudeCodeBrainClient.cs     Drives the Claude Code CLI as the vision brain
  Tts/EdgeTtsClient.cs               Microsoft Edge neural TTS
  Ui/OverlayWindow.cs                The blue cursor buddy: follow, fly, point, waveform, spinner, bubbles
  Ui/TrayIconManager.cs              System tray icon + panel lifecycle
  Ui/CompanionPanelWindow.cs         The dark control panel
```

If you point Claude Code at this repo, read `AGENTS.md` — it's the full architecture doc for agents, mirroring the original's.

---

## Credit

Clicky is Farza's. He made something genuinely delightful and open-sourced it. This is just the Windows shadow of it.

- Original: **https://github.com/farzaa/clicky**
- Farza: **https://x.com/farzatv**

Licensed MIT, preserving the original copyright. See [LICENSE](LICENSE).
