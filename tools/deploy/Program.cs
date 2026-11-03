// Deploys Agent Assist to a Foundry project without the Azure Developer CLI.
//
// Authenticates with DefaultAzureCredential (Visual Studio, Azure CLI, VS Code or a managed
// identity) and talks to the same Foundry APIs azd uses: ARM for the project connection, the
// project data plane for the toolbox and for the agent version (code upload, remote build).
//
//   dotnet run --project tools/deploy -- connection   create/update the maps-mock-key connection (needs MOCK_API_KEY)
//   dotnet run --project tools/deploy -- toolbox      create a toolbox version from toolbox/maps.openapi.json and publish it
//   dotnet run --project tools/deploy -- agent        package src/agent-assist and deploy a new agent version
//   dotnet run --project tools/deploy -- invoke "..." send one message to the deployed agent
//   dotnet run --project tools/deploy -- all          connection (only when MOCK_API_KEY is set), toolbox, agent
//
// Settings come from environment variables or a .env file (see docs/deploy-to-another-foundry.md).

using System.ClientModel;
using AgentAssist.Deploy;
using Azure.Identity;
using DotNetEnv;

// Local convenience only; a missing or unreadable .env must not stop a deploy.
try { Env.NoClobber().TraversePath().Load(); } catch (IOException) { /* no .env: use the process environment */ }

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (command is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

try
{
    var settings = DeploySettings.FromEnvironment();
    var deployer = new Deployer(settings, new DefaultAzureCredential());
    Console.WriteLine($"Project {settings.ProjectEndpoint}, agent {settings.AgentName}, toolbox {settings.ToolboxName}");

    switch (command)
    {
        case "connection":
            await deployer.EnsureConnectionAsync();
            break;
        case "toolbox":
            await deployer.CreateToolboxVersionAsync();
            break;
        case "agent":
            await deployer.DeployAgentAsync();
            break;
        case "invoke":
            await deployer.InvokeAsync(string.Join(' ', args.Skip(1)));
            break;
        case "all":
            if (settings.MockApiKey is not null)
            {
                await deployer.EnsureConnectionAsync();
            }
            else
            {
                Console.WriteLine("MOCK_API_KEY not set: skipping the connection step (it must already exist).");
            }

            await deployer.CreateToolboxVersionAsync();
            await deployer.DeployAgentAsync();
            break;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintHelp();
            return 2;
    }

    return 0;
}
catch (CredentialUnavailableException ex)
{
    Console.Error.WriteLine($"Azure sign-in failed: {ex.Message}");
    Console.Error.WriteLine("Sign in with Visual Studio, `az login` or VS Code, or set AZURE_TOKEN_CREDENTIALS=dev to skip the managed-identity probe.");
    return 1;
}
catch (ClientResultException ex)
{
    Console.Error.WriteLine($"Foundry returned HTTP {ex.Status}: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        deploy <command>

          connection   Create or update the CustomKeys connection for the mock API key (MOCK_API_KEY).
          toolbox      Create a toolbox version from toolbox/maps.openapi.json and set it as default.
          agent        Package src/agent-assist and deploy it as a new hosted agent version.
          invoke "…"   Send one message to the deployed agent and print the reply.
          all          connection (when MOCK_API_KEY is set), toolbox, agent.

        Environment (or .env): FOUNDRY_PROJECT_ENDPOINT, AZURE_AI_PROJECT_ID, AZURE_AI_MODEL_DEPLOYMENT_NAME,
        optional AGENT_NAME, TOOLBOX_NAME, CONNECTION_NAME, MOCK_API_URL, MOCK_API_KEY, AGENT_SOURCE, OPENAPI_SPEC.
        """);
}
