using AgentAssist.Prompts;
using AgentAssist.SkillRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentAssist;

/// <summary>
/// Assembles Agent Assist: a short base prompt plus an <see cref="AgentSkillsProvider"/> over the
/// skills folder, around a core ChatClientAgent. Program.cs passes a factory that creates the core
/// agent on the Foundry project (AIProjectClient.AsAIAgent); tests pass one backed by a scripted
/// chat client, so both run exactly the same wiring.
/// </summary>
public static class AgentComposition
{
    public const string AgentName = "agent-assist";

    public const string AgentDescription =
        "Agent Assist: MAPS conversational front door whose playbooks are Agent Skills loaded on demand.";

    public static AIAgent Build(
        Func<ChatClientAgentOptions, AIAgent> createCoreAgent,
        string modelDeployment,
        string skillsPath,
        AgentFileSkillScriptRunner? scriptRunner,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger(AgentName);

        // Throws InvalidDataException on a malformed SKILL.md: a broken playbook should stop the
        // container, not quietly disappear from the prompt.
        var skills = SkillCatalog.Load(skillsPath);

        // The skills are the agent's behaviour; a container without them must fail its startup
        // rather than report healthy and answer from six lines of prompt.
        if (skills.Count == 0)
        {
            throw new InvalidOperationException($"No skills found at {skillsPath}; the skills folder must ship with the code");
        }

        log.LogInformation("Skills at {Path}:{NewLine}{Summary}", skillsPath, Environment.NewLine, SkillCatalog.Summary(skills));

        var skillsProvider = new AgentSkillsProvider(
            skillsPath,
            scriptRunner,
            // Advertise only scripts PythonScriptRunner can run (the framework default also lists
            // .js, .sh, .ps1, .cs and .csx).
            fileOptions: new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [".py"] },
            options: new AgentSkillsProviderOptions
            {
                // The skills ship inside the container with the code, so they are exactly as
                // trusted as the code. A hosted agent has no human in the loop to answer an
                // approval prompt; leaving approval on would stall every turn that loads a skill.
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                DisableRunSkillScriptApproval = true,
            },
            loggerFactory: loggerFactory);

        // No function tools of our own: data tools arrive later by reference (toolbox), so the
        // agent carries no hand-written API-call code.
        return createCoreAgent(new ChatClientAgentOptions
        {
            Name = AgentName,
            Description = AgentDescription,
            ChatOptions = new ChatOptions
            {
                ModelId = modelDeployment, // required by AIProjectClient.AsAIAgent(options)
                Instructions = AgentInstructions.Text,
            },
            AIContextProviders = [skillsProvider],
        });
    }
}
