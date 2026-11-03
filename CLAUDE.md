# Agent Assist

Foundry hosted agent in C# (.NET 10, Microsoft Agent Framework) for MAPS users. Behaviour comes from Agent Skills in `src/agent-assist/skills/`, loaded on demand, not from a long system prompt. Same shape as the MAPS conversation agent.

This project was built with the microsoft-foundry skill. Before working on or answering questions about foundry agents, read the microsoft-foundry skill first.

## Commands

- Build: `dotnet build -c Release`
- Test: `dotnet test` (offline; scripted model; the script test needs `python` on PATH)
- Deploy without azd: `dotnet run --project tools/deploy -- agent` (see docs/deploy-to-another-foundry.md)

## Rules

- Behaviour goes in `skills/<name>/SKILL.md`. Never grow `Prompts/AgentInstructions.Text`; a test caps it at `MaxLength`.
- Front matter `name` and `description` are unquoted; `name` equals the folder name (lowercase, digits, hyphens); neither may contain `: ` or end with `:` (invalid unquoted YAML).
- The agent refuses to start without skills (`AgentComposition.Build` throws); `skills/` must ship with the code.
- `azure.yaml` declares only the hosted agent service; it deploys into the existing project chosen by the azd environment.
- Tables go in `references/`, deterministic helpers in `scripts/*.py` (stdlib only).
- Run the tests after editing a skill.
- C# helpers for skills live in `SkillRuntime/`, never in a `Skills/` folder (same folder as `skills/` on Windows).
- Data tools are attached through the toolbox (`toolbox/maps.openapi.json` for `tools/deploy`, `toolbox/maps.toolbox.yaml` for azd; loaded by name via `TOOLBOX_NAME`), never as C# functions. Keep the two spec copies in sync with the mock's `/openapi.foundry.json`.
- Never commit `.env`; never copy values from the MAPS conversation agent's `.env.example`.
