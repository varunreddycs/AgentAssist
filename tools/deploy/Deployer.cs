using System.ClientModel;
using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects.Agents;
using Azure.Core;

namespace AgentAssist.Deploy;

/// <summary>
/// The four deploy steps against one Foundry project, in the order a fresh project needs them:
/// connection (ARM), toolbox (data plane), agent version from code (data plane, multipart upload),
/// invoke (data plane). Each step is idempotent enough to rerun.
/// </summary>
public sealed class Deployer(DeploySettings settings, TokenCredential credential)
{
    private const string ApiVersion = "v1";
    private const string FoundryScope = "https://ai.azure.com/.default";
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2025-06-01";

    private static readonly string[] StagingExcludedDirectories = ["bin", "obj", ".vs", ".azure", ".git", ".checkpoints"];
    private static readonly string[] StagingExcludedFiles = [".env"];

    private readonly AgentAdministrationClient _admin = new(settings.ProjectEndpoint, credential);

    // ---- connection ---------------------------------------------------------------------------

    /// <summary>
    /// Creates or updates the CustomKeys project connection that the toolbox's OpenAPI tool uses to
    /// send the mock API's x-api-key header. Goes through ARM, so the caller needs write access to
    /// the project resource; the key is read from MOCK_API_KEY and never printed.
    /// </summary>
    public async Task EnsureConnectionAsync(CancellationToken cancellationToken = default)
    {
        var projectId = settings.ProjectId ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ID is required for the connection command.");
        var url = $"https://management.azure.com{projectId}/connections/{settings.ConnectionName}?api-version={ArmApiVersion}";
        using var http = await ArmClientAsync(cancellationToken);

        var existing = await http.GetAsync(url, cancellationToken);
        if (existing.IsSuccessStatusCode && settings.MockApiKey is null)
        {
            Console.WriteLine($"Connection '{settings.ConnectionName}' already exists; set MOCK_API_KEY to update its key.");
            return;
        }

        var key = settings.MockApiKey
            ?? throw new InvalidOperationException($"Connection '{settings.ConnectionName}' does not exist and MOCK_API_KEY is not set.");

        var body = new
        {
            properties = new
            {
                category = "CustomKeys",
                authType = "CustomKeys",
                target = settings.MockApiUrl.ToString().TrimEnd('/'),
                isSharedToAll = false,
                credentials = new { keys = new Dictionary<string, string> { ["x-api-key"] = key } },
                metadata = new Dictionary<string, string> { ["purpose"] = "MAPS mock OpenAPI tool" },
            },
        };
        using var response = await http.PutAsJsonAsync(url, body, cancellationToken);
        await EnsureSuccessAsync(response, "create the connection", cancellationToken);
        Console.WriteLine($"Connection '{settings.ConnectionName}' {(existing.IsSuccessStatusCode ? "updated" : "created")} ({projectId}/connections/{settings.ConnectionName}).");
    }

    // ---- toolbox -------------------------------------------------------------------------------

    /// <summary>
    /// Creates a new version of the toolbox from toolbox/maps.openapi.json (one OpenAPI tool named
    /// "maps" with project-connection auth) and makes it the default version.
    /// </summary>
    public async Task CreateToolboxVersionAsync(CancellationToken cancellationToken = default)
    {
        var projectId = settings.ProjectId ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ID is required for the toolbox command.");
        var spec = await File.ReadAllBytesAsync(settings.OpenApiSpecPath, cancellationToken);
        var connectionId = $"{projectId}/connections/{settings.ConnectionName}";

        var definition = new OpenApiFunctionDefinition(
            name: "maps",
            specification: BinaryData.FromBytes(spec),
            authentication: new OpenApiProjectConnectionAuthenticationDetails(new OpenApiProjectConnectionSecurityScheme(connectionId)))
        {
            Description = "MAPS mock data: classify identifiers, subpack, inventory, ETA, approvers, BOM, choice items, shelf item, component details and pack compare.",
        };

        var toolboxes = _admin.GetAgentToolboxes();
        ToolboxVersion version = await toolboxes.CreateVersionAsync(
            name: settings.ToolboxName,
            tools: [new OpenApiToolboxTool(definition)],
            description: "MAPS mock API for Agent Assist, attached by reference: one OpenAPI tool with an API-key connection.",
            cancellationToken: cancellationToken);
        await toolboxes.UpdateDefaultVersionAsync(settings.ToolboxName, version.Version);

        Console.WriteLine($"Toolbox '{version.Name}' version {version.Version} created and set as default ({version.Tools.Count} tool entry).");
        Console.WriteLine($"Consumer endpoint: {settings.ProjectEndpoint}/toolboxes/{settings.ToolboxName}/mcp?api-version={ApiVersion}");

        var tools = await ListToolboxToolsAsync(cancellationToken);
        Console.WriteLine($"tools/list returns {tools.Count} tools: {string.Join(", ", tools)}");
    }

    /// <summary>MCP tools/list against the consumer endpoint, the same call the hosted agent makes at startup.</summary>
    private async Task<List<string>> ListToolboxToolsAsync(CancellationToken cancellationToken)
    {
        using var http = await FoundryClientAsync(cancellationToken);
        var url = $"{settings.ProjectEndpoint}/toolboxes/{settings.ToolboxName}/mcp?api-version={ApiVersion}";
        using var response = await http.PostAsync(url, new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"), cancellationToken);
        await EnsureSuccessAsync(response, "list the toolbox tools", cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"tools/list failed: {error.GetRawText()}");
        }

        return doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? "?")
            .ToList();
    }

    // ---- agent ---------------------------------------------------------------------------------

    /// <summary>
    /// Stages the agent source (without build output and secrets), zips it, and uploads it as a new
    /// agent version with the hosted definition; creates the agent on the first deploy. Waits until
    /// the version is active (Foundry restores and publishes the project remotely).
    /// </summary>
    public async Task DeployAgentAsync(CancellationToken cancellationToken = default)
    {
        var model = settings.ModelDeployment ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is required for the agent command.");
        if (!File.Exists(Path.Combine(settings.AgentSourcePath, "agent-assist.csproj")))
        {
            throw new InvalidOperationException($"Agent source not found at {settings.AgentSourcePath} (expected agent-assist.csproj).");
        }

        var zip = await StageAndZipAsync(cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(zip));
        Console.WriteLine($"Packaged {settings.AgentSourcePath}: {zip.Length / 1024} KB, sha256 {sha256}");

        var definition = new HostedAgentDefinition([new ProtocolVersionRecord(ProjectsAgentProtocol.Responses, "2.0.0")], cpu: "0.5", memory: "1Gi")
        {
            CodeConfiguration = new CodeConfiguration("dotnet_10", ["dotnet", "agent-assist.dll"], CodeDependencyResolution.RemoteBuild),
        };
        definition.EnvironmentVariables["AZURE_AI_MODEL_DEPLOYMENT_NAME"] = model;
        definition.EnvironmentVariables["TOOLBOX_NAME"] = settings.ToolboxName;
        var metadata = new AgentVersionFromCodeMetadata(definition)
        {
            Description = "MAPS conversational front door whose playbooks are Agent Skills (SKILL.md) loaded on demand instead of a long prompt.",
        };
        metadata.Metadata["enableVnextExperience"] = "true";
        metadata.Metadata["deployedBy"] = "tools/deploy";

        var exists = await AgentExistsAsync(cancellationToken);
        var url = exists
            ? $"{settings.ProjectEndpoint}/agents/{settings.AgentName}/versions?api-version={ApiVersion}"
            : $"{settings.ProjectEndpoint}/agents?api-version={ApiVersion}";
        Console.WriteLine(exists ? $"Agent '{settings.AgentName}' exists; creating a new version." : $"Agent '{settings.AgentName}' does not exist; creating it.");

        // The service wants multipart/form-data with a JSON "metadata" part and a binary "code" part,
        // the agent name in x-ms-agent-name on create, and the zip's SHA-256 in x-ms-code-zip-sha256.
        using var http = await FoundryClientAsync(cancellationToken);
        using var form = new MultipartFormDataContent();
        // On create the REST contract also wants the name inside the metadata; the SDK model has no
        // such property, so it is added to the serialized JSON here.
        var metadataNode = JsonNode.Parse(ModelReaderWriter.Write(metadata).ToString())!.AsObject();
        if (!exists)
        {
            metadataNode["name"] = settings.AgentName;
        }
        var metadataJson = new StringContent(metadataNode.ToJsonString(), Encoding.UTF8, "application/json");
        form.Add(metadataJson, "metadata");
        var code = new ByteArrayContent(zip);
        code.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(code, "code", "code.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Add("x-ms-agent-name", settings.AgentName);
        request.Headers.Add("x-ms-code-zip-sha256", sha256);

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload the agent code", cancellationToken);
        var created = ModelReaderWriter.Read<ProjectsAgentVersion>(BinaryData.FromString(await response.Content.ReadAsStringAsync(cancellationToken)))
            ?? throw new InvalidOperationException("Foundry returned an empty agent version.");
        Console.WriteLine($"Version {created.Version} created ({created.Id}); waiting for it to become active...");

        var version = await WaitForActiveAsync(created, cancellationToken);
        Console.WriteLine($"Version {version.Version} is {version.Status}.");
        Console.WriteLine($"Responses endpoint: {settings.ProjectEndpoint}/agents/{settings.AgentName}/endpoint/protocols/openai/responses?api-version={ApiVersion}");
        if (PlaygroundUrl(version.Version) is { } playground)
        {
            Console.WriteLine($"Playground: {playground}");
        }
    }

    private async Task<bool> AgentExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _admin.GetAgentAsync(settings.AgentName, cancellationToken);
            return true;
        }
        catch (ClientResultException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private async Task<ProjectsAgentVersion> WaitForActiveAsync(ProjectsAgentVersion version, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (version.Status != AgentVersionStatus.Active && version.Status != AgentVersionStatus.Failed)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Version {version.Version} is still {version.Status} after 10 minutes; check the agent in the portal.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            version = await _admin.GetAgentVersionAsync(agentName: version.Name, agentVersion: version.Version, cancellationToken: cancellationToken);
            Console.WriteLine($"  status {version.Status}");
        }

        if (version.Status == AgentVersionStatus.Failed)
        {
            throw new InvalidOperationException($"Version {version.Version} failed to provision; open it in the portal for the build log.");
        }

        return version;
    }

    /// <summary>
    /// Copies the agent folder to a temp directory without build output, editor state and .env
    /// files (the same exclusions as .agentignore), then zips it with the files at the zip root.
    /// </summary>
    private async Task<byte[]> StageAndZipAsync(CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), "agent-assist-deploy", Guid.NewGuid().ToString("N"));
        var stagedRoot = Path.Combine(staging, "src");
        Directory.CreateDirectory(stagedRoot);
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(settings.AgentSourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(settings.AgentSourcePath, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = segments[^1];
                if (segments[..^1].Any(s => StagingExcludedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase))
                    || StagingExcludedFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && name != ".env.example"
                    || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(stagedRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                count++;
            }

            if (!Directory.Exists(Path.Combine(stagedRoot, "skills")))
            {
                throw new InvalidOperationException("The staged source has no skills folder; the agent refuses to start without it.");
            }

            var zipPath = Path.Combine(staging, "code.zip");
            ZipFile.CreateFromDirectory(stagedRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            Console.WriteLine($"Staged {count} files.");
            return await File.ReadAllBytesAsync(zipPath, cancellationToken);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private string? PlaygroundUrl(string version)
    {
        if (settings.ProjectIdParts is not var (subscription, resourceGroup, account, project))
        {
            return null;
        }

        // The portal encodes the GUID's canonical (big-endian) bytes, not Guid.ToByteArray()'s mixed order.
        var encodedSubscription = Convert.ToBase64String(Guid.Parse(subscription).ToByteArray(bigEndian: true))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"https://ai.azure.com/nextgen/r/{encodedSubscription},{resourceGroup},,{account},{project}/build/agents/{settings.AgentName}/build?version={version}";
    }

    // ---- invoke --------------------------------------------------------------------------------

    /// <summary>Sends one message to the deployed agent over the Responses protocol and prints the reply.</summary>
    public async Task InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("invoke needs a message, for example: deploy invoke \"what is the inventory for DYNJ40380J?\"");
        }

        using var http = await FoundryClientAsync(cancellationToken);
        http.Timeout = TimeSpan.FromMinutes(5);
        var url = $"{settings.ProjectEndpoint}/agents/{settings.AgentName}/endpoint/protocols/openai/responses?api-version={ApiVersion}";
        using var response = await http.PostAsJsonAsync(url, new { input }, cancellationToken);
        await EnsureSuccessAsync(response, "invoke the agent", cancellationToken);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        Console.WriteLine($"Response {root.GetProperty("id").GetString()} ({root.GetProperty("status").GetString()})");
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message" || !item.TryGetProperty("content", out var content))
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                {
                    Console.WriteLine(text.GetString());
                }
            }
        }
    }

    // ---- http helpers --------------------------------------------------------------------------

    private Task<HttpClient> FoundryClientAsync(CancellationToken cancellationToken) => ClientWithTokenAsync(FoundryScope, cancellationToken);

    private Task<HttpClient> ArmClientAsync(CancellationToken cancellationToken) => ClientWithTokenAsync(ArmScope, cancellationToken);

    private async Task<HttpClient> ClientWithTokenAsync(string scope, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return http;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Could not {action}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body[..Math.Min(body.Length, 1500)]}");
    }
}
