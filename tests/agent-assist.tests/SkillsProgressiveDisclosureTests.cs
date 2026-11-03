using System.Security;
using System.Text.Json;
using AgentAssist.Prompts;
using AgentAssist.SkillRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AgentAssist.Tests.ScriptedChatClient;

namespace AgentAssist.Tests;

/// <summary>
/// Proves the "skills instead of a long prompt" mechanism end to end with a scripted model: skills
/// are advertised by name and description only, and bodies, reference files and scripts reach the
/// model through the skill tools, without an approval prompt.
/// </summary>
public sealed class SkillsProgressiveDisclosureTests(ITestOutputHelper output)
{
    private const string BodyOnlyPhone = "1-888-860-1201";

    [Fact]
    public void Base_instructions_stay_short()
    {
        Assert.True(
            AgentInstructions.Text.Length <= AgentInstructions.MaxLength,
            $"AgentInstructions.Text is {AgentInstructions.Text.Length} characters (limit {AgentInstructions.MaxLength}). Move the new guidance into a skill.");
        Assert.DoesNotContain("medpacksupport@", AgentInstructions.Text);
        Assert.DoesNotContain("the second one", AgentInstructions.Text);
        Assert.DoesNotContain(BodyOnlyPhone, AgentInstructions.Text);
    }

    [Fact]
    public void Missing_skills_folder_fails_startup()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agent-assist-tests", Guid.NewGuid().ToString("N"), "skills");

        var error = Assert.Throws<InvalidOperationException>(() => AgentComposition.Build(
            options => new ChatClientAgent(new ScriptedChatClient(), options, NullLoggerFactory.Instance),
            "test-model", missing, scriptRunner: null, NullLoggerFactory.Instance));

        Assert.Contains($"No skills found at {missing}", error.Message);
    }

    [Fact]
    public async Task Skills_are_advertised_not_inlined()
    {
        var (agent, model) = TestAgent.Create();
        var skills = SkillCatalog.Load(TestAgent.SkillsPath);
        Assert.Equal(5, skills.Count);

        ModelCall? first = null;
        model.Then(call =>
        {
            first = call;
            return Say("Hi, I can help with MedPack and MedQuote items.");
        });

        await agent.RunAsync("hello");

        Assert.NotNull(first);
        output.WriteLine(first.AllInstructions);
        foreach (var skill in skills)
        {
            Assert.Contains($"<name>{skill.Name}</name>", first.AllInstructions);
            // The provider XML-escapes descriptions (an apostrophe arrives as &apos;).
            Assert.Contains(SecurityElement.Escape(skill.Description), first.AllInstructions);
        }

        // The base prompt also says "load_skill"; this block only exists if the provider ran.
        Assert.Contains("<available_skills>", first.AllInstructions);
        Assert.DoesNotContain(BodyOnlyPhone, first.AllInstructions);
        Assert.DoesNotContain("in-transit column of the KIR", first.AllInstructions);

        Assert.Contains(AgentSkillsProvider.LoadSkillToolName, first.ToolNames);
        Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, first.ToolNames);
        Assert.Contains(AgentSkillsProvider.RunSkillScriptToolName, first.ToolNames);
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task Load_skill_returns_the_body_without_an_approval_prompt()
    {
        var (agent, model) = TestAgent.Create();
        string? loaded = null;

        model
            .Then(call => Call(AgentSkillsProvider.LoadSkillToolName, new Dictionary<string, object>
            {
                [TestAgent.FirstParameterName(call, AgentSkillsProvider.LoadSkillToolName)] = "support-and-policy",
            }))
            .Then(call =>
            {
                loaded = call.LastToolResult;
                return Say($"Expedite requests go to the KSIS team at {BodyOnlyPhone}.");
            });

        var response = await agent.RunAsync("can you expedite DYNJ40380J?");

        Assert.NotNull(loaded);
        output.WriteLine(loaded);
        Assert.Contains(BodyOnlyPhone, loaded);
        Assert.Contains("# Support and policy", loaded);
        Assert.Empty(response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Contains(BodyOnlyPhone, response.Text);
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task Read_skill_resource_returns_the_reference_file()
    {
        var (agent, model) = TestAgent.Create();
        IReadOnlyList<string> parameters = [];
        string? result = null;

        model
            .Then(call =>
            {
                parameters = TestAgent.ParameterNames(call, AgentSkillsProvider.ReadSkillResourceToolName);
                return Call(AgentSkillsProvider.ReadSkillResourceToolName, new Dictionary<string, object>
                {
                    [parameters[0]] = "identifier-resolution",
                    [parameters[1]] = "references/identifier-rules.md",
                });
            })
            .Then(call =>
            {
                result = call.LastToolResult;
                return Say("Inventory and ETA are not available while the pack is being sourced.");
            });

        await agent.RunAsync("DYNJ40380J is being sourced, what can I see?");

        output.WriteLine($"read_skill_resource parameters: {string.Join(", ", parameters)}");
        output.WriteLine(result);
        Assert.NotNull(result);
        Assert.Contains("Being sourced", result);
        Assert.Equal(0, model.Remaining);
    }

    [PythonFact]
    public async Task Run_skill_script_normalises_identifiers()
    {
        var (agent, model) = TestAgent.Create();
        IReadOnlyList<string> parameters = [];
        string? result = null;

        model
            .Then(call =>
            {
                parameters = TestAgent.ParameterNames(call, AgentSkillsProvider.RunSkillScriptToolName);
                return Call(AgentSkillsProvider.RunSkillScriptToolName, new Dictionary<string, object>
                {
                    [parameters[0]] = "identifier-resolution",
                    [parameters[1]] = "scripts/normalize_ids.py",
                    [parameters[2]] = new[] { "inventory for DYNJ86645A-QPC and 43221" },
                });
            })
            .Then(call =>
            {
                result = call.LastToolResult;
                return Say("I found DYNJ86645A-QPC and 43221.");
            });

        await agent.RunAsync("inventory for DYNJ86645A-QPC and 43221");

        output.WriteLine($"run_skill_script parameters: {string.Join(", ", parameters)}");
        output.WriteLine(result);
        Assert.NotNull(result);

        using var json = JsonDocument.Parse(result);
        var ids = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal("DYNJ86645A-QPC", ids[0].GetProperty("id").GetString());
        Assert.Equal("DYNJ86645A", ids[0].GetProperty("base").GetString());
        Assert.True(ids[0].GetProperty("hyphenated").GetBoolean());
        Assert.Equal("43221", ids[1].GetProperty("id").GetString());
        Assert.False(ids[1].GetProperty("hyphenated").GetBoolean());
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public async Task Session_round_trip_keeps_working()
    {
        var (agent, model) = TestAgent.Create();
        var session = await agent.CreateSessionAsync();

        model
            .Then(call => Call(AgentSkillsProvider.LoadSkillToolName, new Dictionary<string, object>
            {
                [TestAgent.FirstParameterName(call, AgentSkillsProvider.LoadSkillToolName)] = "identifier-resolution",
            }))
            .Then(call =>
            {
                output.WriteLine(call.LastToolResult);
                Assert.Contains("# Identifier resolution", call.LastToolResult);

                // load_skill appends the names read_skill_resource and run_skill_script accept: the
                // path relative to the skill folder, extension included.
                Assert.Contains("<resource name=\"references/identifier-rules.md\"/>", call.LastToolResult);
                Assert.Contains("<script name=\"scripts/normalize_ids.py\">", call.LastToolResult);
                return Say("What would you like to see for DYNJ40380J?\n1. Subpack\n2. Inventory");
            });

        await agent.RunAsync("DYNJ40380J", session);
        session = await TestAgent.RoundTrip(agent, session);

        ModelCall? second = null;
        model.Then(call =>
        {
            second = call;
            return Say("Inventory for DYNJ40380J.");
        });

        await agent.RunAsync("2", session);

        Assert.NotNull(second);
        Assert.Contains("<name>identifier-resolution</name>", second.AllInstructions);
        Assert.Contains(AgentSkillsProvider.LoadSkillToolName, second.ToolNames);
        Assert.Contains(second.Messages, m => m.Role == ChatRole.User && m.Text == "DYNJ40380J");
        Assert.Equal(0, model.Remaining);
    }
}
