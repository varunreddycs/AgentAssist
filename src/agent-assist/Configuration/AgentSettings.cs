namespace AgentAssist.Configuration;

/// <summary>
/// All configuration the agent reads, from environment variables only. In the hosted runtime they
/// come from the agent version definition (azure.yaml env); locally from .env. Names avoid the
/// FOUNDRY_ prefix, which the platform reserves (FOUNDRY_PROJECT_ENDPOINT is the one platform
/// variable read here).
/// </summary>
public sealed record AgentSettings
{
    public required Uri ProjectEndpoint { get; init; }

    public required string ModelDeployment { get; init; }

    /// <summary>Folder holding one sub-folder per skill, each with a SKILL.md.</summary>
    public required string SkillsPath { get; init; }

    /// <summary>Interpreter used by run_skill_script for the skills' Python scripts.</summary>
    public required string PythonExecutable { get; init; }

    public required TimeSpan ScriptTimeout { get; init; }

    /// <summary>
    /// Foundry toolboxes whose tools the agent serves (TOOLBOX_NAME, comma-separated). Toolboxes are
    /// the only way data tools reach this agent; none means skills only.
    /// </summary>
    public required IReadOnlyList<string> ToolboxNames { get; init; }

    public static AgentSettings FromEnvironment()
    {
        var endpoint = Get("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");

        var timeoutText = Get("AGENT_SKILLS_SCRIPT_TIMEOUT_SECONDS");
        var timeoutSeconds = 30;
        if (timeoutText is not null && (!int.TryParse(timeoutText, out timeoutSeconds) || timeoutSeconds <= 0))
        {
            throw new InvalidOperationException(
                $"AGENT_SKILLS_SCRIPT_TIMEOUT_SECONDS must be a positive whole number of seconds, got '{timeoutText}'.");
        }

        return new AgentSettings
        {
            ProjectEndpoint = new Uri(endpoint),
            ModelDeployment = Get("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5.4-mini",
            SkillsPath = Get("AGENT_SKILLS_PATH") ?? Path.Combine(AppContext.BaseDirectory, "skills"),
            PythonExecutable = Get("AGENT_SKILLS_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            ScriptTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            ToolboxNames = Get("TOOLBOX_NAME")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [],
        };

        static string? Get(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
