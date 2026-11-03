using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentAssist.Tests;

/// <summary>What the agent sent to the "model" on one call.</summary>
public sealed record ModelCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>Base instructions plus everything context providers added (instructions or system messages).</summary>
    public string AllInstructions =>
        string.Join("\n", new[] { Options?.Instructions ?? "" }
            .Concat(Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));

    public IReadOnlyList<string> ToolNames =>
        Options?.Tools?.Select(t => t.Name).ToList() ?? [];

    /// <summary>Text of the most recent tool result in the conversation sent to the model.</summary>
    public string LastToolResult =>
        Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result switch
        {
            null => "",
            string s => s,
            JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(),
            var other => other.ToString() ?? "",
        };
}

/// <summary>
/// Stands in for the LLM. Each step receives what the agent sent and returns the next "model"
/// message: a tool call or text. This exercises the real providers, tools, middleware, skills and
/// session handling without a model deployment.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<ModelCall, ChatMessage>> _steps = new();

    public List<ModelCall> Calls { get; } = [];

    public ScriptedChatClient Then(Func<ModelCall, ChatMessage> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public int Remaining => _steps.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = new ModelCall(messages.ToList(), options);
        Calls.Add(call);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("The script has no more steps.");
        }

        return Task.FromResult(new ChatResponse(_steps.Dequeue()(call)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    // ---- helpers for writing steps --------------------------------------------------------

    private static int _callIds;

    /// <summary>A tool call whose arguments arrive as JsonElements, as they do from a real model.</summary>
    public static ChatMessage Call(string tool, object arguments)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Interlocked.Increment(ref _callIds)}", tool, args)]);
    }

    public static ChatMessage Say(string text) => new(ChatRole.Assistant, text);
}
