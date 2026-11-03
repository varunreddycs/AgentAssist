using AgentAssist.Configuration;
using Xunit;

namespace AgentAssist.Tests;

/// <summary>
/// Environment-variable parsing. Runs alone, after the parallel tests: it sets process-wide
/// variables that the hosted test's AgentHost would otherwise read mid-run.
/// </summary>
[Collection(nameof(AgentSettingsTests))]
public sealed class AgentSettingsTests
{
    private const string PlaceholderEndpoint = "https://placeholder.invalid/api/projects/agent-assist-tests";

    [Fact]
    public void Toolbox_names_are_split_on_commas_trimmed_and_blanks_dropped()
    {
        var settings = FromEnvironment(toolboxName: " maps-tools, other ");

        Assert.Equal(["maps-tools", "other"], settings.ToolboxNames);
    }

    [Fact]
    public void Unset_toolbox_name_means_no_toolboxes()
    {
        var settings = FromEnvironment(toolboxName: null);

        Assert.Empty(settings.ToolboxNames);
    }

    private static AgentSettings FromEnvironment(string? toolboxName)
    {
        var previousEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        var previousToolbox = Environment.GetEnvironmentVariable("TOOLBOX_NAME");

        try
        {
            Environment.SetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT", PlaceholderEndpoint);
            Environment.SetEnvironmentVariable("TOOLBOX_NAME", toolboxName);
            return AgentSettings.FromEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT", previousEndpoint);
            Environment.SetEnvironmentVariable("TOOLBOX_NAME", previousToolbox);
        }
    }
}

[CollectionDefinition(nameof(AgentSettingsTests), DisableParallelization = true)]
public sealed class AgentSettingsTestsCollection;
