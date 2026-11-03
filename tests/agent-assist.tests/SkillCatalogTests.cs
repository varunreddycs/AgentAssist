using AgentAssist.SkillRuntime;
using Xunit;

namespace AgentAssist.Tests;

/// <summary>Startup validation of the skills folder (independent of the framework's own parser).</summary>
public sealed class SkillCatalogTests
{
    [Fact]
    public void All_five_skills_load_with_name_equal_to_folder()
    {
        var skills = SkillCatalog.Load(TestAgent.SkillsPath);

        Assert.Equal(
            ["clarification", "identifier-resolution", "maps-data", "response-style", "support-and-policy"],
            skills.Select(s => s.Name));
        Assert.All(skills, s =>
        {
            Assert.Equal(Path.GetFileName(s.Directory), s.Name);
            Assert.InRange(s.Description.Length, 1, SkillCatalog.MaxDescriptionLength);
        });

        var identifiers = skills.Single(s => s.Name == "identifier-resolution");
        Assert.Equal(["references/identifier-rules.md"], identifiers.Resources);
        Assert.Equal(["scripts/normalize_ids.py"], identifiers.Scripts);
        Assert.Contains("identifier-resolution: 1 resource(s), 1 script(s)", SkillCatalog.Summary(skills));
    }

    [Fact]
    public void Name_that_differs_from_the_folder_throws_and_names_the_file()
    {
        var error = LoadInvalid("expedite", "---\nname: expedite-requests\ndescription: Expedite rules. Use for expedite questions.\n---\n\n# Expedite\n", out var file);

        Assert.Contains(file, error.Message);
        Assert.Contains("folder name 'expedite'", error.Message);
    }

    [Theory]
    [InlineData("description: Expedite rules: who to contact. Use for expedite questions.")]
    [InlineData("description: Expedite rules, use for:")]
    public void Colon_that_breaks_unquoted_yaml_throws(string descriptionLine)
    {
        var error = LoadInvalid("expedite", $"---\nname: expedite\n{descriptionLine}\n---\n\n# Expedite\n", out var file);

        Assert.Contains(file, error.Message);
        Assert.Contains("'description' must not contain ': ' or end with ':'", error.Message);
    }

    private static InvalidDataException LoadInvalid(string folderName, string skillMarkdown, out string file)
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-assist-tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(file, skillMarkdown);

        try
        {
            return Assert.Throws<InvalidDataException>(() => SkillCatalog.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_folder_returns_an_empty_list()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agent-assist-tests", Guid.NewGuid().ToString("N"), "skills");

        Assert.Empty(SkillCatalog.Load(missing));
    }
}
