# Skills instead of long prompts

## The problem with long prompts

- **Every token, every turn.** The system prompt is sent on every model call, including each step of a tool loop. A 3,000-token prompt on a five-step turn is 15,000 input tokens before the user has said anything.
- **One blob nobody owns.** Identifier rules, support contacts, tone and clarification policy end up in one string. Nobody can say which paragraph is theirs, and a change to one rule risks the others.
- **Drift.** Rules are appended, rarely removed, and start contradicting each other. The model's attention is spread across text that is irrelevant to most turns.

## What a skill is

A skill follows the open Agent Skills specification (https://agentskills.io/): a folder with a `SKILL.md` file whose YAML front matter holds a `name` (equal to the folder name) and a `description` (what it covers and when to use it), followed by a Markdown body with the instructions. Optional `references/` files hold longer material such as tables, and optional `scripts/` hold deterministic helpers. Only the name and description are advertised to the model up front; the body, references and scripts are fetched when the model decides it needs them (progressive disclosure).

## Three ways to use skills on Foundry

| Option | What it is | Status | Choice |
| --- | --- | --- | --- |
| (a) Agent Framework file skills in the hosted agent container | `AgentSkillsProvider` reads `skills/` shipped with the code and exposes `load_skill`, `read_skill_resource`, `run_skill_script` | GA in `Microsoft.Agents.AI` | **Chosen** |
| (b) Foundry server-side Skills resource | Skills uploaded to the project (`azd ai skill create`), then downloaded by the agent at startup or served through a Toolbox over MCP | Preview | Later, if skills need a lifecycle separate from the code |
| (c) Skills on a prompt agent with the Copilot harness | Skills referenced from a prompt (declarative) agent | Preview | Not used: the team builds hosted code agents |

Why (a): it is generally available, it fits the MAPS conversation agent's hosted-code shape, the skills are versioned and reviewed with the code, and the whole flow is testable offline with a scripted model. Moving to (b) later changes only the provider's source (for example a startup download into a folder, or the MCP skills source); the `SKILL.md` files are the same format and need no rewrite.

## Rules for writing a skill

1. **One playbook per skill.** If a skill needs two owners, split it.
2. **The description says WHEN to use it.** It is the only part the model sees before deciding; end it with "Use when ...".
3. **Keep bodies under about 60 lines.** Imperative, specific, with an example reply where tone matters.
4. **Put tables in references.** Long lookup tables go in `references/*.md`, named in the body by their path.
5. **Scripts only for deterministic work.** Parsing, normalising, calculating: stdlib Python, positional string arguments, JSON or plain text on stdout. Never business data or network calls.
6. **Front matter values unquoted, name equals folder, no `: ` inside a value.** Run the tests after every edit; they fail on a malformed skill.
