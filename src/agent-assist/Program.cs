// Agent Assist: hosted code agent on Microsoft Foundry (Responses protocol 2.0.0).
//
// The conversational front door for MAPS (MedPack and MedQuote). Its behaviour is not one long
// system prompt: the base instructions (Prompts/AgentInstructions.cs) are a few lines, and every
// playbook is an Agent Skill under skills/<name>/SKILL.md, copied next to the binary.
//
// What the model does with them (AgentSkillsProvider, wired in AgentComposition.cs):
//   every turn   the skills' names and descriptions are advertised in the system prompt
//   load_skill            returns a skill's full SKILL.md body when the turn needs it
//   read_skill_resource   returns a reference file (for example a result table) from a skill
//   run_skill_script      runs a skill's Python script (SkillRuntime/PythonScriptRunner.cs)
//
// Hosting: AgentHost + AddFoundryResponses + MapFoundryResponses, as in the MAPS conversation agent.

using AgentAssist;
using AgentAssist.Configuration;
using AgentAssist.SkillRuntime;
using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.Logging;

// Local convenience only; the hosted runtime has no .env and must not fail without one.
try { Env.NoClobber().TraversePath().Load(); } catch (IOException) { /* no readable .env: use the process environment */ }

var settings = AgentSettings.FromEnvironment();

using var loggerFactory = LoggerFactory.Create(logging => logging
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger(AgentComposition.AgentName);

// In Foundry this resolves to the agent's own identity; locally to your `az login`.
var credential = new DefaultAzureCredential();
var projectClient = new AIProjectClient(settings.ProjectEndpoint, credential);

var runner = new PythonScriptRunner(settings.PythonExecutable, settings.ScriptTimeout, loggerFactory.CreateLogger<PythonScriptRunner>());

AIAgent agent = AgentComposition.Build(
    createCoreAgent: options => projectClient.AsAIAgent(options, loggerFactory: loggerFactory),
    modelDeployment: settings.ModelDeployment,
    skillsPath: settings.SkillsPath,
    scriptRunner: runner.RunAsync,
    loggerFactory: loggerFactory);

log.LogInformation("agent-assist starting: model {Model}, skills {SkillsPath}, python {Python}",
    settings.ModelDeployment, settings.SkillsPath, settings.PythonExecutable);

// AgentHost: host preconfigured for Foundry (port 8088, readiness, OpenTelemetry, request
// context). AddFoundryResponses: Responses protocol handler; it persists the AgentSession per
// user and conversation, which keeps the conversation going across turns.
var builder = AgentHost.CreateBuilder(args);
// The host-builder overload: in the hosted environment the IServiceCollection overload throws at
// startup (response and task storage must bind from the host configuration).
builder.WebApplicationBuilder.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();
