using System.ComponentModel;
using System.Diagnostics;
using AgentAssist.SkillRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentAssist.Tests;

/// <summary>Builds the real agent wiring around a <see cref="ScriptedChatClient"/>.</summary>
internal static class TestAgent
{
    public static readonly string SkillsPath = Path.Combine(AppContext.BaseDirectory, "skills");

    public static string PythonExecutable { get; } =
        Environment.GetEnvironmentVariable("AGENT_SKILLS_PYTHON") is { Length: > 0 } configured
            ? configured.Trim()
            : OperatingSystem.IsWindows() ? "python" : "python3";

    public static (AIAgent Agent, ScriptedChatClient Model) Create()
    {
        var model = new ScriptedChatClient();
        var runner = new PythonScriptRunner(PythonExecutable, TimeSpan.FromSeconds(30), NullLogger<PythonScriptRunner>.Instance);
        var agent = AgentComposition.Build(
            createCoreAgent: options => new ChatClientAgent(model, options, NullLoggerFactory.Instance),
            modelDeployment: "test-model",
            skillsPath: SkillsPath,
            scriptRunner: runner.RunAsync,
            loggerFactory: NullLoggerFactory.Instance);
        return (agent, model);
    }

    /// <summary>Save and restore the session through JSON, like the hosted session store.</summary>
    public static async Task<AgentSession> RoundTrip(AIAgent agent, AgentSession session)
    {
        var json = await agent.SerializeSessionAsync(session);
        return await agent.DeserializeSessionAsync(json);
    }

    /// <summary>The tool's parameter names in schema order, read from what the model is actually offered.</summary>
    public static IReadOnlyList<string> ParameterNames(ModelCall call, string toolName)
    {
        var tool = call.Options!.Tools!.OfType<AIFunction>().Single(t => t.Name == toolName);
        return tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
    }

    public static string FirstParameterName(ModelCall call, string toolName) => ParameterNames(call, toolName)[0];
}

/// <summary>A fact that is skipped, with the reason, when the configured Python interpreter is not on PATH.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public PythonFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = $"Python interpreter '{TestAgent.PythonExecutable}' is not on PATH (set AGENT_SKILLS_PYTHON to override); skill scripts cannot run here.";
        }
    }

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(TestAgent.PythonExecutable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
