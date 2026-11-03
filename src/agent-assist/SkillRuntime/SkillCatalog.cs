using System.Text.RegularExpressions;

namespace AgentAssist.SkillRuntime;

/// <summary>One skill folder as found on disk. Paths are relative to <see cref="Directory"/>, with forward slashes.</summary>
public sealed record SkillInfo(
    string Name,
    string Description,
    string Directory,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Scripts);

/// <summary>
/// Startup validation of the skills folder, independent of the framework. AgentSkillsProvider skips
/// a malformed skill with a log warning; this catalog throws instead, so a broken SKILL.md fails
/// the tests and shows up loudly in the container log rather than silently vanishing from the prompt.
/// </summary>
public static partial class SkillCatalog
{
    public const int MaxDescriptionLength = 1024;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Reads every <c>&lt;skillsPath&gt;/*/SKILL.md</c> and validates its front matter.
    /// </summary>
    /// <returns>The skills ordered by name, or an empty list when the folder does not exist (the caller logs it).</returns>
    /// <exception cref="InvalidDataException">A SKILL.md breaks a front-matter rule; the message names the file and the rule.</exception>
    public static IReadOnlyList<SkillInfo> Load(string skillsPath)
    {
        if (!System.IO.Directory.Exists(skillsPath))
        {
            return [];
        }

        var skills = new List<SkillInfo>();
        foreach (var skillDirectory in System.IO.Directory.EnumerateDirectories(skillsPath))
        {
            var file = Path.Combine(skillDirectory, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }

            var (name, description) = ReadFrontMatter(file);
            var folder = Path.GetFileName(skillDirectory);

            if (string.IsNullOrEmpty(name))
            {
                throw Invalid(file, "front matter has no 'name'");
            }

            if (!NamePattern().IsMatch(name))
            {
                throw Invalid(file, $"name '{name}' must be lowercase letters, digits and single hyphens");
            }

            if (!string.Equals(name, folder, StringComparison.Ordinal))
            {
                throw Invalid(file, $"name '{name}' must equal the folder name '{folder}'");
            }

            if (string.IsNullOrEmpty(description))
            {
                throw Invalid(file, "front matter has no 'description'");
            }

            if (description.Length > MaxDescriptionLength)
            {
                throw Invalid(file, $"description is {description.Length} characters; the limit is {MaxDescriptionLength}");
            }

            skills.Add(new SkillInfo(
                name,
                description,
                skillDirectory,
                ListFiles(skillDirectory, "references", "*", SearchOption.AllDirectories),
                ListFiles(skillDirectory, "scripts", "*.py", SearchOption.TopDirectoryOnly)));
        }

        return skills.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>One line per skill, for the startup log.</summary>
    public static string Summary(IReadOnlyList<SkillInfo> skills) =>
        string.Join(Environment.NewLine, skills.Select(s =>
            $"{s.Name}: {s.Resources.Count} resource(s), {s.Scripts.Count} script(s)"
            + (s.Resources.Count + s.Scripts.Count > 0 ? $" [{string.Join(", ", s.Resources.Concat(s.Scripts))}]" : "")));

    private static (string? Name, string? Description) ReadFrontMatter(string file)
    {
        var lines = File.ReadAllLines(file);
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw Invalid(file, "must start with a '---' front-matter line");
        }

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            throw Invalid(file, "front matter is not closed with a second '---' line");
        }

        string? name = null;
        string? description = null;
        foreach (var line in lines[1..end])
        {
            // Only top-level keys count; indented lines belong to a map such as metadata:.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            if (key is not ("name" or "description"))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value.Length > 0 && (value[0] == '"' || value[0] == '\''))
            {
                throw Invalid(file, $"'{key}' must be unquoted");
            }

            // The framework's parser accepts these, but in an unquoted YAML scalar ": " (or a
            // trailing ":") starts a mapping, so real YAML parsers, such as the Foundry Skills
            // resource's, reject the file.
            if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':'))
            {
                throw Invalid(file, $"'{key}' must not contain ': ' or end with ':' (invalid in unquoted YAML; reword, for example with ', including')");
            }

            if (key == "name")
            {
                name = value;
            }
            else
            {
                description = value;
            }
        }

        return (name, description);
    }

    private static IReadOnlyList<string> ListFiles(string skillDirectory, string subfolder, string pattern, SearchOption option)
    {
        var root = Path.Combine(skillDirectory, subfolder);
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(root, pattern, option)
            .Select(f => Path.GetRelativePath(skillDirectory, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static InvalidDataException Invalid(string file, string rule) =>
        new($"Invalid skill file {file}: {rule}.");
}
