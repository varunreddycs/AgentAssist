# Deploy Agent Assist to another Foundry project

This walks through deploying the hosted agent into a Foundry project other than the one it was first deployed to. The MAPS mock API stays where it is (App Service `mdlineapps`); only the agent, its toolbox and the API-key connection are created in the new project. Nothing in the repository is tied to a particular project.

There are two ways to run the steps. **Path A** needs no Azure Developer CLI (azd): a small .NET program in `tools/deploy` signs in with `DefaultAzureCredential` (Visual Studio, Azure CLI, VS Code or a managed identity) and calls the same Foundry APIs azd uses. This is the path for the team environment. **Path B** uses azd and is kept for machines that have it. Both were exercised against `VSI-PROJ` on 2026-11-03.

## 1. Prerequisites

| Tool | Version | Check |
| --- | --- | --- |
| .NET SDK | 10.0.x (pinned by `global.json`) | `dotnet --version` |
| A sign-in that `DefaultAzureCredential` can use | Visual Studio (Tools > Options > Azure Service Authentication), or `az login`, or VS Code Azure sign-in | the deployer prints the failing credential if none works |
| Git | any | `git --version` |

Roles on the target Foundry project for the account you sign in with:

- **Azure AI User** (shown as "Foundry User", role id `53ca6127-db72-4b80-b1b0-d745d6d5456d`) to read and invoke.
- **Azure AI Project Manager** (or Owner/Contributor on the project resource) to create agents, toolboxes and connections. Creating the connection also needs write access to the project ARM resource, since connections are ARM resources.

If the sign-in has several tenants or subscriptions, set `AZURE_TENANT_ID` so the credential picks the right one. On a developer machine without a managed identity, set `AZURE_TOKEN_CREDENTIALS=dev` so `DefaultAzureCredential` does not spend time probing the instance metadata endpoint.

## 2. What the target project needs

- A Foundry project (an `AIServices` account with a project under it).
- A chat model deployment in that project, for example `gpt-4.1-mini` or `gpt-5.4-mini`.

Values you will need, and where to read them in the Foundry portal (https://ai.azure.com, open the project, **Overview**):

| Value | Where |
| --- | --- |
| Project endpoint, `https://<account>.services.ai.azure.com/api/projects/<project>` | Overview > Endpoints and keys > Foundry project endpoint |
| Project ARM id, `/subscriptions/.../resourceGroups/.../providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project>` | Overview > Properties (resource id), or Management center > Project details |
| Model deployment name | Models + endpoints |

With the Azure CLI, if available:

```bash
az resource list --resource-type "Microsoft.CognitiveServices/accounts/projects" --query "[].{name:name,rg:resourceGroup,id:id}" -o table
az rest --method get --url "https://management.azure.com<project-arm-id>?api-version=2025-06-01" --query "properties.endpoints.\"AI Foundry API\"" -o tsv
az cognitiveservices account deployment list -n <account> -g <rg> -o table
```

## 3. Get the code and prove it builds

```bash
git clone https://github.com/varunreddycs/AgentAssist.git
cd AgentAssist
dotnet build -c Release
dotnet test -c Release
```

The tests are offline (a scripted model stands in for the LLM). Expect 0 warnings and all tests passing before going further. The solution also builds `tools/deploy`.

## Path A: deploy without azd (`tools/deploy`)

### A1. Settings

The deployer reads environment variables, or a `.env` file in the current folder or any parent (the repo's `.gitignore` keeps `.env` out of git). Create `.env` in the repository root:

```env
FOUNDRY_PROJECT_ENDPOINT=https://<account>.services.ai.azure.com/api/projects/<project>
AZURE_AI_PROJECT_ID=/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project>
AZURE_AI_MODEL_DEPLOYMENT_NAME=gpt-4.1-mini
AZURE_TOKEN_CREDENTIALS=dev
# Optional, these are the defaults:
# AGENT_NAME=agent-assist-skills
# TOOLBOX_NAME=maps-tools
# CONNECTION_NAME=maps-mock-key
# MOCK_API_URL=https://mdlineapps-f0bgc7e5cyasdths.westus3-01.azurewebsites.net
# Only for the connection step; never commit it:
# MOCK_API_KEY=<the mock web app's MOCK_API_KEY setting>
```

In PowerShell the same values can be set with `$env:FOUNDRY_PROJECT_ENDPOINT = "..."` instead of a file.

### A2. The API-key connection

The toolbox authenticates to the mock API through a project connection named `maps-mock-key` (category **Custom keys**) holding the header `x-api-key`. The key is the mock web app's `MOCK_API_KEY` application setting (App Service `mdlineapps`, resource group `az-vsi`); read it in the Azure portal or with `az webapp config appsettings list -n mdlineapps -g az-vsi --query "[?name=='MOCK_API_KEY'].value" -o tsv`.

Either create it in the portal: **Management center** > **Connected resources** > **New connection** > **Custom keys**, name `maps-mock-key`, one key `x-api-key` with the value above, target the mock URL.

Or let the deployer do it (ARM `PUT` on the project's connection resource):

```bash
MOCK_API_KEY=<key> dotnet run --project tools/deploy -- connection
```

Rerunning with `MOCK_API_KEY` set updates the key; rerunning without it just reports that the connection exists.

### A3. The toolbox

```bash
dotnet run --project tools/deploy -- toolbox
```

This reads `toolbox/maps.openapi.json` (the mock's Foundry OpenAPI spec), creates a new version of the `maps-tools` toolbox with one OpenAPI tool named `maps` authenticated through `<project-id>/connections/maps-mock-key`, makes that version the default, and then lists the tools through the toolbox's MCP endpoint. Expect ten `maps___*` tools. Toolbox versions are immutable, so every run creates a new version; that is how a changed spec is rolled out.

### A4. The agent

```bash
dotnet run --project tools/deploy -- agent
```

This stages `src/agent-assist` without build output, editor state or `.env` files, zips it with the sources at the zip root, and uploads it as a hosted agent version (runtime `dotnet_10`, entry point `dotnet agent-assist.dll`, remote build, Responses protocol 2.0.0, 0.5 CPU and 1 GiB, environment `AZURE_AI_MODEL_DEPLOYMENT_NAME` and `TOOLBOX_NAME`). The first run creates the agent; later runs add versions. It then polls until the version is `active` (Foundry restores and publishes the project; one to three minutes) and prints the Responses endpoint and the Playground URL.

Foundry versions by content: uploading the same zip and definition again does not mint a new version.

### A5. Smoke test

```bash
dotnet run --project tools/deploy -- invoke "can you expedite DYNJ40380J?"
dotnet run --project tools/deploy -- invoke "what is the inventory for DYNJ40380J?"
```

The first answer should quote the expedite policy and the KSIS contact (loaded from the `support-and-policy` skill). The second should return on-hand, allocated and available quantities (fetched through `maps___classifyItems` then `maps___getInventory`). The Playground in the portal shows the same, with the session log stream.

`dotnet run --project tools/deploy -- all` runs A2 (only when `MOCK_API_KEY` is set), A3 and A4 in one go.

## Path B: deploy with azd

Same outcome, different tooling. Needs azd 1.27.1 or later with the `azure.ai.agents`, `azure.ai.toolboxes` and `azure.ai.connections` extensions, and `azd config set auth.useAzCliAuth true` so azd reuses the Azure CLI sign-in.

```bash
azd env new <env-name>
azd env set AZURE_SUBSCRIPTION_ID "<subscription-id>"
azd env set AZURE_LOCATION "<region of the project>"
azd env set AZURE_TENANT_ID "<tenant-id>"
azd env set AZURE_AI_PROJECT_ID "<project-arm-id>"
azd env set AZURE_AI_PROJECT_ENDPOINT "<project-endpoint>"
azd env set FOUNDRY_PROJECT_ENDPOINT "<project-endpoint>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "<model-deployment-name>"
```

Then the connection as in A2, the toolbox with `scripts/create-toolbox.sh` (or `.ps1`, which render `toolbox/maps.toolbox.yaml` for the active environment and set `TOOLBOX_NAME`), and:

```bash
azd deploy --no-prompt
azd ai agent show agent-assist-skills --output json
azd ai agent invoke agent-assist-skills --new-session "what is the inventory for DYNJ40380J?"
azd ai agent monitor agent-assist-skills --tail 100
```

In Git Bash, prefix commands whose arguments start with `/subscriptions/...` with `MSYS_NO_PATHCONV=1`.

## Agent name

Both paths deploy the agent as `agent-assist-skills`. Agent names are unique per project. To use another name: Path A, set `AGENT_NAME`; Path B, change both the service key and `name:` in `azure.yaml`.

## Day-to-day changes

| Change | Steps |
| --- | --- |
| A skill or the base prompt | edit under `src/agent-assist/skills/` or `Prompts/`, `dotnet test -c Release`, `deploy agent` (or `azd deploy`) |
| Code | `dotnet build -c Release`, `dotnet test -c Release`, `deploy agent` |
| The tool set | update `toolbox/maps.openapi.json` from the mock's `/openapi.foundry.json` (and `toolbox/maps.toolbox.yaml` if azd is used), `deploy toolbox`; new sessions pick it up, no agent redeploy needed |
| The mock API key | `MOCK_API_KEY=<new> deploy connection`, or update the connection in the portal; no redeploy needed |
| Remove the agent | portal, or `azd ai agent delete agent-assist-skills` |

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `Azure sign-in failed` from the deployer | No usable credential. Sign in to Visual Studio (Tools > Options > Azure Service Authentication) or run `az login`; set `AZURE_TENANT_ID` if the account spans tenants; `AZURE_TOKEN_CREDENTIALS=dev` avoids the managed-identity probe. |
| `HTTP 403` creating the agent, toolbox or connection | The account lacks Azure AI Project Manager on the project (or write access to the project ARM resource for connections). |
| `HTTP 409` on the first agent deploy | The name is taken; set `AGENT_NAME`. |
| Version ends in `failed` | The remote build failed; open the version in the portal for the build log (usually a NuGet restore or compile error). Build locally first with `dotnet build -c Release`. |
| Invoke returns HTTP 424 `session_not_ready` | The container did not pass readiness. Open the agent in the portal and read the log stream: an unhandled exception is a startup crash; `Connecting to toolbox` with no `discovered` line means the toolbox could not be read; otherwise it is a cold start, wait 30 seconds and retry. |
| Toolbox connect fails with 401/403 at startup | The agent's identity cannot read the project. Grant it Azure AI User: read `instance_identity.principal_id` from the version (portal or `GET {endpoint}/agents/{name}/versions/{v}?api-version=v1`), then `az role assignment create --assignee-object-id <principal-id> --assignee-principal-type ServicePrincipal --role 53ca6127-db72-4b80-b1b0-d745d6d5456d --scope "<project-arm-id>"`. In `VSI-PROJ` this was not needed. |
| Tool call returns `unknown auth type 'connection'` | The toolbox was created from an azd YAML with the old auth shape. Use `deploy toolbox` or `scripts/create-toolbox.sh --replace`. |
| Tool call returns 401 from the mock | The connection's key is wrong or rotated; rerun `deploy connection` with the current key. |
| Model errors (404 deployment not found) | `AZURE_AI_MODEL_DEPLOYMENT_NAME` does not match a deployment in the target project; fix it and redeploy. |
| `No skills found at /app/skills` in the log | The skills folder did not ship; the deployer refuses to package without it, so check the source folder. |
