using System.Net.Http.Json;
using System.Text.Json;
using Azure.AI.AgentServer.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;
using static AgentAssist.Tests.ScriptedChatClient;

namespace AgentAssist.Tests;

/// <summary>
/// Runs the agent inside the real Foundry hosting stack (AgentHost + AddFoundryResponses) on a
/// local port and talks to it over HTTP, as the Playground or an orchestrator would. Proves the
/// host starts offline, serves skills-driven turns, and chains a second turn with previous_response_id.
/// </summary>
public sealed class HostedResponsesTests(ITestOutputHelper output)
{
    private const string FirstInput = "can you expedite DYNJ40380J?";

    [Fact]
    public async Task Two_turns_over_http_with_previous_response_id()
    {
        var (agent, model) = TestAgent.Create();

        var previousPort = Environment.GetEnvironmentVariable("PORT");
        var port = 18000 + Random.Shared.Next(1000);
        Environment.SetEnvironmentVariable("PORT", port.ToString());
        WebApplication? app = null;

        try
        {
            var builder = AgentHost.CreateBuilder([]);
            builder.Services.AddFoundryResponses(agent);
            builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());
            var host = builder.Build();
            app = host.App; // the underlying WebApplication
            await app.StartAsync();

            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

            // Turn 1: the model loads the support-and-policy skill, then answers from its body.
            // Assertions stay outside the scripted steps: an exception thrown inside one becomes a
            // "failed" response, not a test failure.
            string? loaded = null;
            model
                .Then(call => Call(AgentSkillsProvider.LoadSkillToolName, new Dictionary<string, object>
                {
                    [TestAgent.FirstParameterName(call, AgentSkillsProvider.LoadSkillToolName)] = "support-and-policy",
                }))
                .Then(call =>
                {
                    loaded = call.LastToolResult;
                    return Say("I can't expedite, but the KSIS team can: support@medlinekitting.zendesk.com or 1-888-860-1201.");
                });

            var first = await PostAsync(http, new { input = FirstInput });
            var firstId = first.GetProperty("id").GetString();
            Assert.False(string.IsNullOrEmpty(firstId));
            Assert.NotNull(loaded);
            Assert.Contains("1-888-860-1201", loaded);

            // Turn 2, chained to turn 1: the model must see the first turn's user text.
            ModelCall? second = null;
            model.Then(call =>
            {
                second = call;
                return Say("Before sending it, check the in-transit column of the KIR.");
            });

            await PostAsync(http, new { input = "anything to check first?", previous_response_id = firstId });

            Assert.NotNull(second);
            Assert.Contains(second.Messages, m => m.Role == ChatRole.User && m.Text.Contains(FirstInput, StringComparison.Ordinal));
            Assert.Equal(0, model.Remaining);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
            }

            Environment.SetEnvironmentVariable("PORT", previousPort);
        }
    }

    private async Task<JsonElement> PostAsync(HttpClient http, object body)
    {
        var response = await http.PostAsJsonAsync("/responses", body);
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine($"{(int)response.StatusCode}: {text[..Math.Min(text.Length, 600)]}");
        Assert.True(response.IsSuccessStatusCode, text);

        // The host answers 200 even when the agent run fails; the outcome is in "status".
        var json = JsonDocument.Parse(text).RootElement.Clone();
        var status = json.TryGetProperty("status", out var s) ? s.GetString() : null;
        Assert.True(status == "completed", $"Response status '{status}', expected 'completed': {text}");
        return json;
    }
}
