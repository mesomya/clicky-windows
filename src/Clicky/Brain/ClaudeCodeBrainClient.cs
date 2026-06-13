//
//  ClaudeCodeBrainClient.cs
//  Clicky for Windows
//
//  The AI brain. Where the original called the Anthropic API through a
//  Cloudflare Worker (paid API key), this port drives the locally installed
//  Claude Code CLI in headless stream-json mode — riding the user's
//  existing Claude subscription, so the brain is literally the same Claude
//  models at zero extra cost.
//
//  One long-lived `claude` process is kept alive across turns:
//    stdin  ← user messages (screenshots as base64 image blocks + transcript)
//    stdout → assistant events; the `result` event closes each turn
//  Conversation memory lives inside the session, replacing the original's
//  manual conversationHistory array. Cancelling a turn kills the process
//  and the next turn respawns it with --resume so memory survives.
//

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Clicky.Brain;

public sealed class ClaudeCodeBrainClient : IDisposable
{
    private readonly string systemPrompt;
    private readonly bool isEphemeralSession;
    private string model;

    private Process? claudeProcess;
    private StreamWriter? claudeStdin;
    private string? sessionId;
    private TaskCompletionSource<string>? pendingTurnCompletion;
    private readonly SemaphoreSlim turnLock = new(1, 1);

    private static string BrainWorkingDirectory =>
        Path.Combine(ClickySettings.AppDataDirectory, "brain");

    public ClaudeCodeBrainClient(string systemPrompt, string model, bool isEphemeralSession = false)
    {
        this.systemPrompt = systemPrompt;
        this.model = model;
        this.isEphemeralSession = isEphemeralSession;
    }

    /// Switching models mid-session: kill the process; the next turn
    /// respawns with --resume plus the new --model, keeping the memory.
    public void SetModel(string newModel)
    {
        if (model == newModel)
        {
            return;
        }
        model = newModel;
        KillProcess();
    }

    /// Pre-spawns the headless claude process at app launch so the first
    /// real query doesn't pay the cold process-start + model-load cost.
    /// Best-effort; call on a background thread (it blocks briefly).
    public void WarmUp()
    {
        try
        {
            turnLock.Wait();
            try { EnsureProcessStarted(); }
            finally { turnLock.Release(); }
        }
        catch
        {
            // Warm-up is best-effort; the first real turn will start it.
        }
    }

    // ── CLI discovery ────────────────────────────────────────────────

    private static string? cachedCliPath;

    public static string? ResolveClaudeCliPath()
    {
        if (cachedCliPath != null)
        {
            return cachedCliPath;
        }

        try
        {
            var whereProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "claude",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (whereProcess != null)
            {
                string output = whereProcess.StandardOutput.ReadToEnd();
                whereProcess.WaitForExit(5000);
                // Prefer a real executable over a .cmd npm shim — direct exe
                // launch avoids a cmd.exe wrapper and its quoting pitfalls.
                var candidatePaths = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(File.Exists)
                    .ToList();
                cachedCliPath = candidatePaths.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? candidatePaths.FirstOrDefault();
            }
        }
        catch
        {
            // where.exe unavailable — fall through to the known install path.
        }

        if (cachedCliPath == null)
        {
            string nativeInstallPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "claude.exe");
            if (File.Exists(nativeInstallPath))
            {
                cachedCliPath = nativeInstallPath;
            }
        }

        return cachedCliPath;
    }

    public static bool IsClaudeCliAvailable => ResolveClaudeCliPath() != null;

    // ── Process lifecycle ────────────────────────────────────────────

    private void EnsureProcessStarted()
    {
        if (claudeProcess is { HasExited: false })
        {
            return;
        }

        string cliPath = ResolveClaudeCliPath()
            ?? throw new InvalidOperationException(
                "Claude Code CLI not found. Install it from https://claude.com/claude-code and sign in once.");

        Directory.CreateDirectory(BrainWorkingDirectory);

        // The system prompt goes through a file rather than an argument so
        // its newlines and quotes survive every launch path (including the
        // cmd.exe wrapper used for npm-shim installs).
        string systemPromptFilePath = Path.Combine(
            BrainWorkingDirectory, $"system-prompt-{(isEphemeralSession ? "demo" : "companion")}.txt");
        File.WriteAllText(systemPromptFilePath, systemPrompt);

        var argumentParts = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--model", model,
            "--system-prompt-file", systemPromptFilePath,
            // No tools: every turn is a pure vision+text exchange, which keeps
            // responses fast — the same shape as the original's API calls.
            "--tools", "\"\"",
            "--strict-mcp-config",
            "--mcp-config", "{\"mcpServers\":{}}",
        };

        if (isEphemeralSession)
        {
            argumentParts.Add("--no-session-persistence");
        }
        else if (sessionId != null)
        {
            argumentParts.Add("--resume");
            argumentParts.Add(sessionId);
        }

        ProcessStartInfo startInfo;
        if (cliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo { FileName = cliPath };
            foreach (string part in argumentParts)
            {
                startInfo.ArgumentList.Add(part.Trim('"'));
            }
        }
        else
        {
            // npm-style .cmd shim — must run through cmd.exe.
            startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"\"{cliPath}\" {string.Join(' ', argumentParts.Select(QuoteForCmd))}\"",
            };
        }

        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = BrainWorkingDirectory;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        Debug.WriteLine($"🧠 Brain: starting claude ({model}{(sessionId != null ? $", resume {sessionId}" : "")})");

        claudeProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Claude Code CLI process.");

        claudeStdin = new StreamWriter(claudeProcess.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };

        var processForReaders = claudeProcess;
        _ = Task.Run(() => ReadStdoutLoop(processForReaders));
        _ = Task.Run(() =>
        {
            try
            {
                string? errorLine;
                while ((errorLine = processForReaders.StandardError.ReadLine()) != null)
                {
                    Debug.WriteLine($"🧠 Brain stderr: {errorLine}");
                }
            }
            catch { /* process ended */ }
        });
    }

    private static string QuoteForCmd(string argument)
    {
        if (argument.Length > 0 && !argument.Contains(' ') && !argument.Contains('"'))
        {
            return argument;
        }
        return "\"" + argument.Replace("\"", "\\\"") + "\"";
    }

    private void ReadStdoutLoop(Process process)
    {
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                HandleOutputLine(line);
            }
        }
        catch (Exception readError)
        {
            Debug.WriteLine($"🧠 Brain stdout loop ended: {readError.Message}");
        }

        // Process exited — fail any in-flight turn so the UI can recover.
        pendingTurnCompletion?.TrySetException(
            new InvalidOperationException("the claude process exited unexpectedly."));
    }

    private void HandleOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch
        {
            return; // Non-JSON noise (e.g. update notices) — ignore.
        }

        using (document)
        {
            var root = document.RootElement;
            string? eventType = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;

            if (root.TryGetProperty("session_id", out var sessionIdProperty))
            {
                sessionId = sessionIdProperty.GetString();
            }

            if (eventType == "result")
            {
                bool isError = root.TryGetProperty("is_error", out var isErrorProperty) && isErrorProperty.GetBoolean();
                string resultText = root.TryGetProperty("result", out var resultProperty)
                    ? resultProperty.GetString() ?? ""
                    : "";

                if (isError)
                {
                    string errorDetail = string.IsNullOrEmpty(resultText)
                        ? (root.TryGetProperty("subtype", out var subtypeProperty) ? subtypeProperty.GetString() ?? "unknown error" : "unknown error")
                        : resultText;
                    pendingTurnCompletion?.TrySetException(new InvalidOperationException($"claude error: {errorDetail}"));
                }
                else
                {
                    pendingTurnCompletion?.TrySetResult(resultText);
                }
            }
        }
    }

    private void KillProcess()
    {
        try
        {
            claudeStdin?.Dispose();
            if (claudeProcess is { HasExited: false })
            {
                claudeProcess.Kill(entireProcessTree: true);
            }
            claudeProcess?.Dispose();
        }
        catch
        {
            // Already gone — fine.
        }
        claudeStdin = null;
        claudeProcess = null;
    }

    // ── Vision chat ──────────────────────────────────────────────────

    /// Sends labeled screenshots + the user's transcript and returns the
    /// full response text. Mirrors ClaudeAPI.analyzeImageStreaming's role;
    /// the original never displayed partial text (spinner until TTS), so a
    /// single awaited result keeps identical behavior with less machinery.
    public async Task<string> AnalyzeImagesAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        await turnLock.WaitAsync(cancellationToken);
        try
        {
            EnsureProcessStarted();

            var contentBlocks = new List<object>();
            foreach (var image in images)
            {
                contentBlocks.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(image.Data),
                    },
                });
                contentBlocks.Add(new { type = "text", text = image.Label });
            }
            contentBlocks.Add(new { type = "text", text = userPrompt });

            var userMessage = new
            {
                type = "user",
                message = new { role = "user", content = contentBlocks },
            };

            string messageJson = JsonSerializer.Serialize(userMessage);
            Debug.WriteLine($"🧠 Brain request: {messageJson.Length / 1024}KB, {images.Count} image(s)");

            var turnCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingTurnCompletion = turnCompletion;

            await claudeStdin!.WriteLineAsync(messageJson);

            // Turn timeout: generous because cold process start + big images
            // can take a while on slow connections.
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(180));

            await using var cancellationRegistration = timeoutSource.Token.Register(() =>
            {
                // Cancelling a turn means killing the process — there's no
                // clean per-turn abort. The next turn resumes the session.
                turnCompletion.TrySetCanceled();
                KillProcess();
            });

            string responseText = await turnCompletion.Task;
            return responseText.Trim();
        }
        finally
        {
            pendingTurnCompletion = null;
            turnLock.Release();
        }
    }

    public void Dispose()
    {
        KillProcess();
        turnLock.Dispose();
    }
}
