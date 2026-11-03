# Deploy Agent Assist to another Foundry project

This walks through deploying the hosted agent `agent-assist-skills` into a Foundry project other than the one it was first deployed to. The MAPS mock API stays where it is (App Service `mdlineapps`); only the agent, its toolbox and the API-key connection are created in the new project. Nothing in the repository is tied to a particular project: the target comes entirely from an azd environment.

Everything below was exercised against `VSI-PROJ` on 2026-11-03. Commands are shown for Git Bash; a PowerShell variant is given where it differs.

## 1. Prerequisites on your machine

| Tool | Version | Check |
| --- | --- | --- |
| .NET SDK | 10.0.x (pinned by `global.json`) | `dotnet --version` |
| Azure CLI | any recent | `az --version` |
| Azure Developer CLI | 1.27.1 or later | `azd version` (upgrade with `winget upgrade Microsoft.Azd`) |
| azd extensions | `azure.ai.agents`, `azure.ai.toolboxes`, `azure.ai.connections` | `azd extension list` |
| Git | any | `git --version` |

Install the extensions once:

```bash
azd extension install azure.ai.agents
azd extension install azure.ai.toolboxes
azd extension install azure.ai.connections
```

Sign in with the account that has access to the target subscription, and make azd use that same sign-in (azd keeps its own login and can silently be a different account):

```bash
az login
azd config set auth.useAzCliAuth true
azd auth login --check-status
```

## 2. What the target project needs

- A Foundry project (an account of kind `AIServices` with a project under it).
- A chat model deployment in that project, for example `gpt-4.1-mini` or `gpt-5.4-mini`.
- Your account holding the **Azure AI User** role (role id `53ca6127-db72-4b80-b1b0-d745d6d5456d`, shown as "Foundry User" in the portal) on the project. `azd ai agent doctor` checks this.

Find the values you will need:

```bash
# Projects in the subscription, with their ARM ids
az resource list --resource-type "Microsoft.CognitiveServices/accounts/projects" \
  --query "[].{name:name,rg:resourceGroup,loc:location,id:id}" -o table

# The project's endpoint (replace <project-arm-id>)
az rest --method get --url "https://management.azure.com<project-arm-id>?api-version=2025-06-01" \
  --query "properties.endpoints.\"AI Foundry API\"" -o tsv

# Model deployments on the account (replace <account> and <rg>)
az cognitiveservices account deployment list -n <account> -g <rg> -o table

# Subscription and tenant
az account show --query "{subscription:id,tenant:tenantId}" -o json
```

The endpoint has the form `https://<account>.services.ai.azure.com/api/projects/<project>`.

## 3. Get the code and prove it builds

```bash
git clone https://github.com/varunreddycs/AgentAssist.git
cd AgentAssist
dotnet build -c Release
dotnet test -c Release
```

The tests are offline (a scripted model stands in for the LLM). Expect 0 warnings and all tests passing before you go further.

## 4. Create the azd environment for the target

One environment per target project. The name is yours to choose; it only names the folder under `.azure/`, which is not committed.

```bash
azd env new <env-name>
azd env set AZURE_SUBSCRIPTION_ID "<subscription-id>"
azd env set AZURE_LOCATION "<region of the project, e.g. eastus2>"
azd env set AZURE_TENANT_ID "<tenant-id>"
azd env set AZURE_AI_PROJECT_ID "<project-arm-id>"
azd env set AZURE_AI_PROJECT_ENDPOINT "<project-endpoint>"
azd env set FOUNDRY_PROJECT_ENDPOINT "<project-endpoint>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "<model-deployment-name>"
azd env get-values
```

Notes:

- Both `AZURE_AI_PROJECT_ENDPOINT` and `FOUNDRY_PROJECT_ENDPOINT` are needed; the agents extension reads the second one when deploying into an existing project and fails with "FOUNDRY_PROJECT_ENDPOINT is required" without it.
- In Git Bash, prefix commands whose arguments start with `/subscriptions/...` with `MSYS_NO_PATHCONV=1`, or the shell rewrites the ARM id into a Windows path. PowerShell has no such issue.
- No `azd provision` is needed: the project and model already exist, and `azure.yaml` declares only the agent.

## 5. Create the API-key connection for the mock API

The toolbox authenticates to the mock API through a project connection named `maps-mock-key` (category **Custom keys**), holding the header `x-api-key`. The key itself is the mock web app's `MOCK_API_KEY` setting; read it with your own account:

```bash
az webapp config appsettings list -n mdlineapps -g az-vsi --query "[?name=='MOCK_API_KEY'].value" -o tsv
```

Create the connection in the target project. The portal route is the one Microsoft documents for OpenAPI API-key auth:

1. Open the project at https://ai.azure.com, then **Management center** > **Connected resources** > **New connection**.
2. Choose **Custom keys**.
3. Name: `maps-mock-key`. Add one key: name `x-api-key`, value the key from above. Target (if asked): `https://mdlineapps-f0bgc7e5cyasdths.westus3-01.azurewebsites.net`.
4. Save.

The same connection as an ARM call, if you prefer the command line (this is the resource shape the existing connection has; the key goes in `credentials.keys`):

```bash
MSYS_NO_PATHCONV=1 az rest --method put \
  --url "https://management.azure.com<project-arm-id>/connections/maps-mock-key?api-version=2025-06-01" \
  --body '{"properties":{"category":"CustomKeys","authType":"CustomKeys","target":"https://mdlineapps-f0bgc7e5cyasdths.westus3-01.azurewebsites.net","credentials":{"keys":{"x-api-key":"<MOCK_API_KEY>"}},"metadata":{"purpose":"MAPS mock OpenAPI tool"}}}'
```

Verify it exists (the key is never returned):

```bash
MSYS_NO_PATHCONV=1 az rest --method get \
  --url "https://management.azure.com<project-arm-id>/connections/maps-mock-key?api-version=2025-06-01" \
  --query "{category:properties.category,authType:properties.authType,target:properties.target}" -o json
```

## 6. Create the toolbox

`toolbox/maps.toolbox.yaml` is a template: the connection reference contains `${AZURE_AI_PROJECT_ID}`. The script fills it from the azd environment, creates the toolbox and sets `TOOLBOX_NAME`:

```bash
scripts/create-toolbox.sh
```

```powershell
scripts\create-toolbox.ps1
```

Check the result; the consumer endpoint must list ten `maps___*` tools:

```bash
azd ai toolbox show maps-tools --project-endpoint "<project-endpoint>" --output json
```

Toolbox versions are immutable and the CLI cannot add an OpenAPI tool to an existing toolbox. After editing the template, rerun with `--replace` (bash) or `-Replace` (PowerShell), which deletes and recreates it.

## 7. Pick the agent name

`azure.yaml` deploys the agent as `agent-assist-skills`. Agent names are unique per project, so if that name is taken in the target, change both the service key and `name:` in `azure.yaml` (they must match), and use the new name in the commands below.

## 8. Deploy

```bash
azd deploy --no-prompt
azd ai agent show agent-assist-skills --output json
```

`azd deploy` zips `src/agent-assist` (honouring `.agentignore`, so `skills/` ships and `.env` does not), uploads it, has Foundry run `dotnet publish`, registers a new agent version and waits for it to become active. Expect about one to two minutes. The output ends with the Playground URL and the Responses endpoint.

The first deploy into a project creates the agent and its managed identity. In `VSI-PROJ` that identity could read the toolbox without any extra role assignment; see Troubleshooting if yours cannot.

## 9. Smoke test

```bash
azd ai agent invoke agent-assist-skills --new-session "can you expedite DYNJ40380J?"
azd ai agent invoke agent-assist-skills --new-session "what is the inventory for DYNJ40380J?"
```

The first answer should quote the expedite policy and the KSIS contact (loaded from the `support-and-policy` skill). The second should return on-hand, allocated and available quantities (fetched through `maps___classifyItems` then `maps___getInventory`). The container log proves what happened:

```bash
azd ai agent monitor agent-assist-skills --tail 100
```

Look for `Toolbox 'maps-tools': discovered 10 tool(s)` at startup and `Loading skill: ...` lines during the turn.

## 10. Day-to-day changes

| Change | Steps |
| --- | --- |
| A skill or the base prompt | edit under `src/agent-assist/skills/` or `Prompts/`, `dotnet test -c Release`, `azd deploy` |
| Code | `dotnet build -c Release`, `dotnet test -c Release`, `azd deploy` |
| The tool set | edit `toolbox/maps.toolbox.yaml`, `scripts/create-toolbox.sh --replace`, no redeploy needed (the agent discovers tools at startup; restart by redeploying or wait for a new session) |
| The mock API key | update the `maps-mock-key` connection in the portal; no redeploy needed |
| Remove the agent | `azd ai agent delete agent-assist-skills` |

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT is required` on `azd deploy` | Set it with `azd env set FOUNDRY_PROJECT_ENDPOINT "<project-endpoint>"` (step 4). |
| `azd auth login --check-status` shows a different account than `az` | Run `azd config set auth.useAzCliAuth true`, or `azd auth login` with the right account. |
| Invoke returns HTTP 424 `session_not_ready` | The container did not pass `/readiness`. Run `azd ai agent monitor agent-assist-skills` and read the startup lines: an unhandled exception means a startup crash; `Connecting to toolbox` with no `discovered` line means the toolbox could not be read; otherwise it is a cold start, so wait 30 seconds and retry. |
| Toolbox connect fails with 401/403 at startup | The agent's identity cannot read the project. Grant it the Azure AI User role: read `instance_identity.principal_id` from `azd ai agent show agent-assist-skills --output json`, then `az role assignment create --assignee-object-id <principal-id> --assignee-principal-type ServicePrincipal --role 53ca6127-db72-4b80-b1b0-d745d6d5456d --scope "<project-arm-id>"`, and redeploy. |
| Tool call returns `unknown auth type 'connection'` | The toolbox was created from an unrendered or hand-edited file. The auth must be `type: project_connection` with the connection's full ARM id; rerun `scripts/create-toolbox.sh --replace`. |
| Tool call returns 401 from the mock | The connection's key is wrong or the key was rotated. Update the `maps-mock-key` connection. |
| Model errors (404 deployment not found) | `AZURE_AI_MODEL_DEPLOYMENT_NAME` does not match a deployment in the target project; fix it and redeploy. |
| `No skills found at /app/skills` in the log | The skills folder did not ship. Check `.agentignore` does not exclude `skills/` and that the folder name is lowercase `skills`. |
| Agent name already exists | Rename in `azure.yaml` (step 7). |
