namespace AgentAssist.Deploy;

/// <summary>
/// Everything the deployer reads, from environment variables (or a .env in the current folder or
/// a parent). The names match the azd environment so the two deploy paths share one vocabulary.
/// </summary>
public sealed record DeploySettings
{
    /// <summary>Data-plane endpoint: https://account.services.ai.azure.com/api/projects/project.</summary>
    public required Uri ProjectEndpoint { get; init; }

    /// <summary>ARM resource id of the project; needed for the connection and the toolbox's connection reference.</summary>
    public string? ProjectId { get; init; }

    public string? ModelDeployment { get; init; }

    public required string AgentName { get; init; }

    public required string ToolboxName { get; init; }

    public required string ConnectionName { get; init; }

    public required Uri MockApiUrl { get; init; }

    /// <summary>Only read by the connection command; never logged.</summary>
    public string? MockApiKey { get; init; }

    /// <summary>The hosted agent's source folder (the one azure.yaml points at).</summary>
    public required string AgentSourcePath { get; init; }

    /// <summary>Path of the OpenAPI spec the toolbox tool is built from.</summary>
    public required string OpenApiSpecPath { get; init; }

    public static DeploySettings FromEnvironment()
    {
        var repoRoot = FindRepoRoot();
        var endpoint = Get("FOUNDRY_PROJECT_ENDPOINT") ?? Get("AZURE_AI_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");

        return new DeploySettings
        {
            ProjectEndpoint = new Uri(endpoint.TrimEnd('/')),
            ProjectId = Get("AZURE_AI_PROJECT_ID"),
            ModelDeployment = Get("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
            AgentName = Get("AGENT_NAME") ?? "agent-assist-skills",
            ToolboxName = Get("TOOLBOX_NAME") ?? "maps-tools",
            ConnectionName = Get("CONNECTION_NAME") ?? "maps-mock-key",
            MockApiUrl = new Uri(Get("MOCK_API_URL") ?? "https://mdlineapps-f0bgc7e5cyasdths.westus3-01.azurewebsites.net"),
            MockApiKey = Get("MOCK_API_KEY"),
            AgentSourcePath = Get("AGENT_SOURCE") ?? Path.Combine(repoRoot, "src", "agent-assist"),
            OpenApiSpecPath = Get("OPENAPI_SPEC") ?? Path.Combine(repoRoot, "toolbox", "maps.openapi.json"),
        };

        static string? Get(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>The ARM id split into its parts, for the portal URL and the connection resource id.</summary>
    public (string Subscription, string ResourceGroup, string Account, string Project)? ProjectIdParts
    {
        get
        {
            if (ProjectId is null)
            {
                return null;
            }

            var parts = ProjectId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? Part(string key)
            {
                var i = Array.FindIndex(parts, p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
            }

            var sub = Part("subscriptions");
            var rg = Part("resourceGroups");
            var account = Part("accounts");
            var project = Part("projects");
            return sub is null || rg is null || account is null || project is null ? null : (sub, rg, account, project);
        }
    }

    /// <summary>Walks up from the deployer's folder (or the current directory) to the folder holding AgentAssist.sln.</summary>
    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentAssist.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
