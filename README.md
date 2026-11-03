# Agent Assist

Agent Assist is a Microsoft Foundry **hosted agent** written in C# (.NET 10, Microsoft Agent Framework). It is the conversational front door for MAPS (MedPack and MedQuote) users. Its behaviour does not live in one long system prompt: the base instructions are six short lines, and every playbook (identifiers, clarification, support and policy, response style) is an **Agent Skill**, a `SKILL.md` folder that the model loads on demand. It has the same shape as the MAPS conversation agent (`Program.cs` + `AgentComposition.cs`, `AgentHost` + Responses protocol, scripted-model tests), so it drops into the team's build and deploy process.

## Why skills instead of a long prompt

- **Tokens.** A system prompt is paid on every model call. Here the model always receives the base prompt (about 560 characters, roughly 140 tokens) plus the skills index (the framework's short instruction block plus name and description per skill, roughly 75 tokens per skill, about 300 for four skills plus about 175 for the block). The skill bodies (about 1,600 tokens together) and the reference table (about 280 tokens) are sent only on the turns that load them. The MAPS conversation agent's prompt, for comparison, is about 2,800 characters on every call, and it grows with every rule added. Estimates use 4 characters per token.
- **One place per playbook.** Each skill has an owner (`metadata.owner`) and one topic. Changing the expedite wording means editing `support-and-policy/SKILL.md`, not hunting through a prompt.
- **Updates without touching code.** A skill is a Markdown file shipped next to the binary. Editing one changes no C#; the tests then confirm it still loads and is advertised.
- **The documented mechanism.** This is the Agent Framework's `AgentSkillsProvider`, exactly as the C# docs describe: https://learn.microsoft.com/en-us/agent-framework/agents/skills?pivots=programming-language-csharp

## How progressive disclosure works here

`AgentComposition.Build` attaches one `AgentSkillsProvider` over `skills/` as an `AIContextProvider`. On every turn:

1. **Advertise.** The provider adds an `<available_skills>` block with each skill's name and description to the instructions. Nothing else from the skills is sent.
2. **`load_skill`.** When the turn needs a playbook, the model calls `load_skill` with the skill name and gets the full `SKILL.md` body, followed by the list of that skill's resources and scripts.
3. **`read_skill_resource`.** The model reads a reference file by the name listed, relative to the skill folder (`references/identifier-rules.md`).
4. **`run_skill_script`.** The model runs a script by the name listed (`scripts/normalize_ids.py`) with a JSON array of string arguments. `SkillRuntime/PythonScriptRunner.cs` runs it with `python -I` in the script folder, with a minimal environment (no managed-identity or endpoint variables), closed stdin, output capped at 64 KB per stream, and a timeout over the whole run, and returns its output. Only `.py` scripts are advertised.

| Skill | Description (what the model sees every turn) | Resources | Scripts |
| --- | --- | --- | --- |
| `clarification` | When and how to ask the user a clarifying question, including one question per turn, numbered choices, resolving references like 'the second one' or 'that pack', subpack versus pack compare, and how many packs a compare needs. Use before asking any question that is not a simple yes or no. | none | none |
| `identifier-resolution` | How to recognise and handle MedPack and MedQuote identifiers (pack, component, standard pack, temporary component, quote and account numbers), bare-number messages, multiple numbers, hyphenated pack numbers, and items that are not found, discontinued or being sourced. Use the first time identifiers appear in a conversation. | `references/identifier-rules.md` | `scripts/normalize_ids.py` |
| `response-style` | How Agent Assist phrases replies, covering length, numbered lists and tables, what never to show (raw JSON, tool names, internal ids), and tone. Use when composing any reply longer than one sentence. | none | none |
| `support-and-policy` | Support contacts and policy wording for unsupported items (standard packs, temporary components), discontinued or not-found items, expedite requests and escalation, and fields to leave out of answers. Use for unsupported items, expedite, escalation, contact or support questions. | none | none |

## Layout

```
AgentAssist/
  AgentAssist.sln
  global.json                      pins the .NET 10 SDK (10.0.403, latest patch)
  azure.yaml                       azd descriptor: hosted agent, Responses protocol 2.0.0
  CLAUDE.md
  docs/skills-instead-of-prompts.md
  src/agent-assist/
    agent-assist.csproj            Microsoft.Agents.AI.Foundry.Hosting + DotNetEnv; copies skills/ to the output
    Program.cs                     settings, credential, AIProjectClient, AgentHost
    AgentComposition.cs            base prompt + AgentSkillsProvider around the core agent
    Configuration/AgentSettings.cs environment variables
    Prompts/AgentInstructions.cs   the short base prompt (MaxLength guard)
    SkillRuntime/SkillCatalog.cs   startup validation of skills/ (fails loudly on a bad or missing SKILL.md)
    SkillRuntime/PythonScriptRunner.cs  run_skill_script runner for .py scripts
    .env.example  .agentignore
    skills/
      clarification/SKILL.md
      identifier-resolution/SKILL.md
      identifier-resolution/references/identifier-rules.md
      identifier-resolution/scripts/normalize_ids.py
      response-style/SKILL.md
      support-and-policy/SKILL.md
  tests/agent-assist.tests/        xunit, scripted model, real AgentHost over HTTP; all offline
```

The C# helpers live in `SkillRuntime/`, not `Skills/`: on Windows `Skills` and `skills` are the same folder, which would mix code into the skills content and break the `skills\**\*` copy on a case-sensitive Linux build.

## Build and test

```
dotnet build -c Release
dotnet test -c Release
```

The tests need no Azure: a scripted chat client stands in for the model. The script test needs `python` (Windows) or `python3` on PATH and is skipped with a message otherwise; set `AGENT_SKILLS_PYTHON` to point elsewhere.

## Run locally

1. `copy src\agent-assist\.env.example src\agent-assist\.env` and set `FOUNDRY_PROJECT_ENDPOINT` and `AZURE_AI_MODEL_DEPLOYMENT_NAME`.
2. `az login`
3. `dotnet run --project src/agent-assist`
4. Call it on port 8088:

```bash
curl -s http://localhost:8088/responses -H "Content-Type: application/json" -d '{"input":"can you expedite DYNJ40380J?"}'
```

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8088/responses -ContentType 'application/json' -Body '{"input":"can you expedite DYNJ40380J?"}'
```

To continue the conversation, send the returned `id` as `previous_response_id` with the next `input`.

The azd way (from the repository root): `azd ai agent run --no-client`, then `azd ai agent invoke --local "can you expedite DYNJ40380J?"`.

## Deploy

Deployed on 2026-11-03 as hosted agent `agent-assist-skills` (version 2; version 1 crashed at startup because the `IServiceCollection` overload of `AddFoundryResponses` is rejected in the hosted environment on 1.27, so `Program.cs` uses `builder.WebApplicationBuilder.AddFoundryResponses(agent)`) into the Foundry project `VSI-PROJ` (account `VSI`, resource group `az-vsi`, East US 2) with model deployment `gpt-4.1-mini`. The azd environment `agent-assist` (under `.azure/`, not committed) holds `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, `AZURE_TENANT_ID`, `AZURE_AI_PROJECT_ID`, `AZURE_AI_PROJECT_ENDPOINT`, `FOUNDRY_PROJECT_ENDPOINT` and `AZURE_AI_MODEL_DEPLOYMENT_NAME`; the extension needs `FOUNDRY_PROJECT_ENDPOINT` to deploy into an existing project. azd was set to reuse the Azure CLI sign-in (`azd config set auth.useAzCliAuth true`).

To ship a new version after a change:

```
azd deploy
azd ai agent show agent-assist-skills
azd ai agent invoke agent-assist-skills "can you expedite DYNJ40380J?"
```

First-time setup on another machine:

```
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME gpt-5.4-mini
azd deploy
```

`azure.yaml` requires azd 1.27.1 or later and the `azure.ai.agents` extension (1.0.0-beta.9 or later); `winget upgrade Microsoft.Azd` if needed. `azure.yaml` declares only the agent service: it deploys into the existing Foundry project selected by the azd environment (no `ai-project` service is provisioned). The hosted runtime injects `FOUNDRY_PROJECT_ENDPOINT`; the code-deploy ZIP includes `skills/` (`.agentignore` excludes only secrets and build output). If `skills/` is missing or empty the agent refuses to start (`InvalidOperationException: No skills found at ...`), so a container without its playbooks never reports healthy. The container image needs Python 3 on PATH for `run_skill_script`; without it the tool answers "Python interpreter not found" and the rest of the agent keeps working.

## Add a skill

1. Create `src/agent-assist/skills/<name>/SKILL.md`. `<name>` is lowercase letters, digits and single hyphens, and must equal the `name` in the front matter.
2. Front matter, values unquoted:
   ```
   ---
   name: <name>
   description: What it covers. Use when <the situation that should trigger it>.
   metadata:
     owner: agent-assist
   ---
   ```
   The description (max 1024 characters) is all the model sees until it loads the skill, so say **when** to use it. Because the values are unquoted YAML, `name` and `description` must not contain `: ` or end with `:` (write "..., including ..." instead of "...: ..."); real YAML parsers, such as the Foundry Skills resource's, reject them, and `SkillCatalog` fails startup on them.
3. Body: one playbook, under about 60 lines. Put long tables in `references/*.md` and mention them by path (`references/x.md`).
4. Scripts only for deterministic work, as `scripts/*.py`, stdlib only, arguments as positional strings, output on stdout.
5. Run `dotnet test -c Release`. `SkillCatalogTests` fails on a bad front matter; update the expected skill list there.

## Approval policy

Skill tools run without an approval prompt (`DisableLoadSkillApproval`, `DisableReadSkillResourceApproval`, `DisableRunSkillScriptApproval`). The skills ship inside the container with the code and are reviewed with it, so they are as trusted as the code, and a hosted agent has no human in the loop to answer an approval request. If skills ever come from an outside source (for example a Foundry Skills resource edited outside this repository), revisit this, at least for `run_skill_script`.

## Deliberately not here

- No function tools of our own and no toolbox yet. Data (pack lookup, inventory, ETA) comes later by reference, as in the MAPS conversation agent; the agent carries no hand-written API-call code.
- No custom middleware, conversation-state provider or delegation to a supervisor.

## Status

Builds with 0 warnings and all tests pass (offline, including two HTTP turns through the real `AgentHost`). Deployed to Foundry as `agent-assist-skills` version 2 and smoke-tested: an expedite question was answered from the support-and-policy skill.
