using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentAssist.SkillRuntime;

/// <summary>
/// Runs a skill's Python script for <c>run_skill_script</c>. <see cref="RunAsync"/> matches the
/// <see cref="AgentFileSkillScriptRunner"/> delegate. Modelled on Microsoft's sample
/// SubprocessScriptRunner, hardened for a hosted container: Python only, isolated mode, a minimal
/// environment, closed stdin, capped output, a timeout over the whole run that kills the process
/// tree, and argument or runtime problems returned to the model as text (so it can correct itself)
/// instead of thrown.
/// </summary>
public sealed class PythonScriptRunner(string pythonExecutable, TimeSpan timeout, ILogger<PythonScriptRunner> logger)
{
    public async Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(script.FullPath), ".py", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: script '{script.Name}' is not a Python (.py) script; only Python scripts can run here.";
        }

        var args = new List<string>();
        if (arguments is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return ArgumentShapeError(script, $"an array element of kind {element.ValueKind}");
                }

                args.Add(element.GetString()!);
            }
        }
        else if (arguments is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } other)
        {
            return ArgumentShapeError(script, $"a JSON {other.ValueKind}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(script.FullPath) ?? skill.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The host's environment carries managed-identity and endpoint settings; a skill script
        // gets only what an interpreter needs to start.
        startInfo.Environment.Clear();
        foreach (var name in InheritedVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        // -I: ignore PYTHON* variables, user site-packages and the script folder on sys.path, so the
        // script sees only the stdlib. -X utf8: -I also ignores PYTHONIOENCODING, and without UTF-8
        // mode Windows writes redirected output in the ANSI code page.
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("utf8");
        startInfo.ArgumentList.Add(script.FullPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Process.Start returned no process for '{pythonExecutable}'.");
        }
        catch (Win32Exception)
        {
            logger.LogWarning("Skill script {Skill}/{Script} not run: Python interpreter {Python} not found", skill.Frontmatter.Name, script.Name, pythonExecutable);
            return $"Error: Python interpreter '{pythonExecutable}' not found; file-based skill scripts are unavailable in this runtime.";
        }

        using (process)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);

            // No script gets input; closing stdin makes one that reads it see EOF instead of waiting.
            process.StandardInput.Close();

            // Read both pipes concurrently so a script that fills one cannot block on the other.
            var stdoutTask = ReadCappedAsync(process.StandardOutput, timeoutSource.Token);
            var stderrTask = ReadCappedAsync(process.StandardError, timeoutSource.Token);

            string stdout;
            string stderr;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

                // A grandchild that inherited the pipes can keep them open after the script exits,
                // so the reads are under the same timeout.
                var streams = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                stdout = streams[0];
                stderr = streams[1];
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                logger.LogInformation(
                    "Skill script {Skill}/{Script} stopped after {ElapsedMs} ms ({Reason})",
                    skill.Frontmatter.Name, script.Name, stopwatch.ElapsedMilliseconds,
                    cancellationToken.IsCancellationRequested ? "caller cancelled" : "timeout");

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return $"Error: script '{script.Name}' timed out after {timeout.TotalSeconds:0} s";
            }

            logger.LogInformation(
                "Skill script {Skill}/{Script} exited with code {ExitCode} in {ElapsedMs} ms",
                skill.Frontmatter.Name, script.Name, process.ExitCode, stopwatch.ElapsedMilliseconds);

            var output = new StringBuilder(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.Append("\nStderr:\n").Append(stderr);
            }

            if (process.ExitCode != 0)
            {
                output.Append($"\nScript exited with code {process.ExitCode}");
            }

            var text = output.ToString().Trim();
            return text.Length == 0 ? "(no output)" : text;
        }
    }

    /// <summary>Per-stream cap on captured output (characters, about 64 KB of ASCII).</summary>
    public const int MaxCapturedChars = 64 * 1024;

    private static readonly string[] InheritedVariables =
        ["PATH", "SYSTEMROOT", "TEMP", "TMP", "HOME", "USERPROFILE", "LANG", "LC_ALL"];

    /// <summary>
    /// Reads a stream to its end but keeps at most <see cref="MaxCapturedChars"/>; the rest is
    /// drained and dropped so the script never blocks on a full pipe.
    /// </summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = MaxCapturedChars - captured.Length;
            if (read > room)
            {
                truncated = true;
            }

            if (room > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, room));
            }
        }

        return truncated ? captured.Append("... [truncated]").ToString() : captured.ToString();
    }

    private static string ArgumentShapeError(AgentFileSkillScript script, string received) =>
        $"Error: script '{script.Name}' expects arguments as a JSON array of strings (positional command-line arguments), for example [\"first\", \"second\"], but received {received}. Call run_skill_script again with that shape.";

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout firing and the kill; nothing left to stop.
        }
    }
}
