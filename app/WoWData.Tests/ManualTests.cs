using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace WoWData.Tests;

/// <summary>
/// The user manual (docs\manual) stays in step with the app (docs\manual\MAINTAINING.md):
/// * every **bold** text in a chapter is a UI label (menu paths split at " > ") that exists in the app's XAML or C#;
/// * every referenced screenshot exists; every link to a chapter resolves; every chapter is linked from index.md;
/// * every F1 topic (s:Manual.Topic) and every Help-menu chapter in the XAML has its chapter file.
/// No game data needed; runs in about a second.
/// </summary>
public class ManualTests
{
    private static string ManualDir => Path.Combine(Oracle.ToolDir, "docs", "manual");
    private static string AppDir => Path.Combine(Oracle.ToolDir, "app");
    private static readonly string[] NotShipped = { "MAINTAINING.md" };

    private static IEnumerable<string> SourceFiles(string pattern, params string[] projects) =>
        projects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(AppDir, p), pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>index.md plus every chapter it links, in order.</summary>
    private static List<string> Chapters()
    {
        var list = new List<string> { "index.md" };
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(ManualDir, "index.md")), @"(?<!!)\[[^\]]*\]\(([^)#\s]+\.md)(#[^)]*)?\)"))
            if (!list.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) list.Add(m.Groups[1].Value);
        return list;
    }

    /// <summary>A chapter without fenced code blocks and inline code spans (whose text is not checked).</summary>
    private static string Prose(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(ManualDir, file));
        bool fence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                lines[i] = "";
                continue;
            }
            lines[i] = fence ? "" : Regex.Replace(lines[i], "`[^`]*`", "``");
        }
        return string.Join("\n", lines);
    }

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    internal sealed class LabelCorpus
    {
        public readonly List<string> Texts = new();
        public readonly List<Regex> Templates = new();

        public void Add(string raw)
        {
            var s = Collapse(raw);
            if (s.Length == 0) return;
            Texts.Add(s);
            if (s.Contains('_')) Texts.Add(Regex.Replace(s, @"_(?=\w)", ""));   // access keys: "_File", "E_xit"
            // a text with placeholders ($"Resize to {w}x{h}", StringFormat {0}) matches any value in their place, as long as
            // it has some words of its own ($"{a} ({b})" alone would match everything)
            if (Regex.IsMatch(s, @"\{[^{}]+\}") && Regex.Replace(s, @"\{[^{}]+\}", "").Count(char.IsLetter) >= 4)
            {
                var parts = Regex.Split(s, @"\{[^{}]+\}").Select(Regex.Escape);
                Templates.Add(new Regex("^" + string.Join(".+?", parts) + "$", RegexOptions.CultureInvariant));
            }
        }

        public bool Has(string label) => Texts.Any(t => t.Contains(label, StringComparison.Ordinal)) || Templates.Any(r => r.IsMatch(label));
    }

    internal static readonly Lazy<LabelCorpus> Labels = new(() =>
    {
        var c = new LabelCorpus();
        foreach (var f in SourceFiles("*.xaml", "WoWDataStudio"))
        {
            var x = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(x, "=\"([^\"]*)\"")) c.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(x, ">([^<>]+)<")) c.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        }
        // 0.7.0: the names of the data tables and of their columns, shown by the Data tables tab, come from the layout file
        foreach (var t in WoWData.Core.Dbc.DbcLayouts.Default.Tables)
        {
            c.Add(t.Name);
            foreach (var col in t.Columns) c.Add(col.Name);
        }
        // 0.9.0: the names of the server's tables and of their described columns, shown by the Server data tab, come from
        // the server layout file
        foreach (var t in WoWData.Core.ServerData.ServerLayouts.Default.Tables)
        {
            c.Add(t.Name);
            foreach (var col in t.Columns) c.Add(col.Name);
        }
        // 0.8.0: the feature registry names the app's labels itself; it must not count as the place they exist
        foreach (var f in SourceFiles("*.cs", "WoWDataStudio", "WoWData.Core").Where(f => !f.EndsWith("FeatureRegistry.cs", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\\$?@?\"((?:[^\"\\\\\\n]|\\\\.)*)\""))
            {
                var v = m.Groups[1].Value.Replace("{{", "(").Replace("}}", ")");
                try { v = Regex.Unescape(v); }
                catch (ArgumentException) { }   // a verbatim string with a backslash
                c.Add(v);
            }
        return c;
    });

    [Fact]
    public void BoldUiLabelsExistInTheApp()
    {
        var missing = new List<string>();
        int checkedCount = 0;
        foreach (var ch in Chapters().Where(c => File.Exists(Path.Combine(ManualDir, c))))
        {
            var text = Prose(ch);
            foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*", RegexOptions.Singleline))
            {
                var bold = Collapse(m.Groups[1].Value);
                foreach (var part in bold.Split(" > ").Select(p => p.Trim()).Where(p => p.Length > 0))
                {
                    checkedCount++;
                    if (!Labels.Value.Has(part)) missing.Add($"{ch}:{LineOf(text, m.Index)}: **{part}**");
                }
            }
        }
        Assert.True(checkedCount > 100, $"only {checkedCount} labels found in the manual");
        Assert.True(missing.Count == 0,
            "These bold texts in the manual are not labels of the app (renamed or removed control?). Bold is for exact UI labels only " +
            "(docs\\manual\\MAINTAINING.md); fix the manual or use *italics*:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void ScreenshotsAndChapterLinksExist()
    {
        var problems = new List<string>();
        int images = 0;
        foreach (var ch in Chapters())
        {
            var path = Path.Combine(ManualDir, ch);
            if (!File.Exists(path))
            {
                problems.Add($"index.md links to {ch}, which does not exist");
                continue;
            }
            var text = Prose(ch);
            foreach (Match m in Regex.Matches(text, @"!\[[^\]]*\]\(([^)\s]+)\)"))
            {
                images++;
                if (!File.Exists(Path.GetFullPath(Path.Combine(ManualDir, m.Groups[1].Value))))
                    problems.Add($"{ch}:{LineOf(text, m.Index)}: screenshot {m.Groups[1].Value} is missing");
            }
            foreach (Match m in Regex.Matches(text, @"(?<!!)\[[^\]]*\]\(([^)#\s]+\.md)(#[^)]*)?\)"))
                if (!File.Exists(Path.Combine(ManualDir, m.Groups[1].Value)))
                    problems.Add($"{ch}:{LineOf(text, m.Index)}: link to {m.Groups[1].Value}, which does not exist");
        }
        var linked = Chapters();
        foreach (var f in Directory.EnumerateFiles(ManualDir, "*.md").Select(Path.GetFileName))
            if (!linked.Contains(f!, StringComparer.OrdinalIgnoreCase) && !NotShipped.Contains(f, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{f} is not linked from index.md (it would not be shipped)");
        Assert.True(images > 20, $"only {images} screenshots referenced");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>0.6.1: the list of known bugs is kept (docs\manual\MAINTAINING.md): known-issues.md exists and is linked from the
    /// contents, the troubleshooting chapter and README's "Known issues"; every entry says what happens, who is affected,
    /// the workaround and its status, and no entry is marked fixed (fixed bugs are removed and recorded in CHANGELOG.md,
    /// which has a section for the app's version).</summary>
    [Fact]
    public void KnownIssuesAndChangelogAreKept()
    {
        var problems = new List<string>();
        var ki = Path.Combine(ManualDir, "known-issues.md");
        Assert.True(File.Exists(ki), @"docs\manual\known-issues.md is missing");
        if (!Chapters().Contains("known-issues.md", StringComparer.OrdinalIgnoreCase)) problems.Add("index.md does not link known-issues.md");
        if (!File.ReadAllText(Path.Combine(ManualDir, "troubleshooting.md")).Contains("(known-issues.md)", StringComparison.Ordinal)) problems.Add("troubleshooting.md does not link known-issues.md");
        var readme = File.ReadAllText(Path.Combine(Oracle.ToolDir, "README.md"));
        if (!Regex.IsMatch(readme, @"^#+ Known issues", RegexOptions.Multiline) || !readme.Contains("docs/manual/known-issues.md", StringComparison.Ordinal))
            problems.Add("README.md has no \"Known issues\" section pointing to docs/manual/known-issues.md");
        var text = File.ReadAllText(ki);
        var entries = Regex.Split(text, @"^### ", RegexOptions.Multiline).Skip(1).ToList();
        if (entries.Count < 3) problems.Add($"known-issues.md has only {entries.Count} entries");
        foreach (var e in entries)
        {
            var title = e.Split('\n')[0].Trim();
            foreach (var field in new[] { "*What happens:*", "*Who is affected:*", "*Workaround:*", "*Status:*" })
                if (!e.Contains(field, StringComparison.Ordinal)) problems.Add($"known issue \"{title}\" has no {field}");
            var status = Regex.Match(e, @"\*Status:\*\s*([^\n]*)").Groups[1].Value;
            if (Regex.IsMatch(status, @"\bfixed\b", RegexOptions.IgnoreCase))
                problems.Add($"known issue \"{title}\" is marked fixed ({status}): remove it and record the fix in CHANGELOG.md");
        }
        // 0.8.0: the one version of the app and the command line is in app\Directory.Build.props
        var props = File.ReadAllText(Path.Combine(AppDir, "Directory.Build.props"));
        var version = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(WoWData.Core.StudioInfo.Version, version);
        var changelog = Path.Combine(Oracle.ToolDir, "CHANGELOG.md");
        if (!File.Exists(changelog)) problems.Add("CHANGELOG.md is missing");
        else if (!Regex.IsMatch(File.ReadAllText(changelog), @"^## " + Regex.Escape(version) + @"\b", RegexOptions.Multiline))
            problems.Add($"CHANGELOG.md has no section \"## {version}\" for the app's version");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>F1 finds the installed manual (&lt;install&gt;\bin\app.exe + &lt;install&gt;\manual\), falls back to the contents
    /// page for an unknown chapter, and to the Markdown source when there is no HTML.</summary>
    [Fact]
    public void HelpFindsTheManualInTheInstalledLayout()
    {
        var root = TestEnv.TempDir("manual");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            Assert.Null(WoWDataStudio.Services.Manual.Locate("settings", bin));
            Directory.CreateDirectory(Path.Combine(root, "docs", "manual"));
            File.WriteAllText(Path.Combine(root, "docs", "manual", "settings.md"), "# Settings");
            Assert.Equal(Path.Combine(root, "docs", "manual", "settings.md"), WoWDataStudio.Services.Manual.Locate("settings", bin));
            Directory.CreateDirectory(Path.Combine(root, "manual"));
            File.WriteAllText(Path.Combine(root, "manual", "index.html"), "<p>contents</p>");
            File.WriteAllText(Path.Combine(root, "manual", "settings.html"), "<p>settings</p>");
            Assert.Equal(Path.Combine(root, "manual", "settings.html"), WoWDataStudio.Services.Manual.Locate("settings", bin));
            Assert.Equal(Path.Combine(root, "manual", "index.html"), WoWDataStudio.Services.Manual.Locate("no-such-chapter", bin));
            Assert.Equal(Path.Combine(root, "manual", "index.html"), WoWDataStudio.Services.Manual.Locate(null, bin));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The words after "wds" in a command written in the manual, with the global options and their values removed.</summary>
    private static List<string> CommandWords(string line)
    {
        var words = Regex.Matches(line, "\"[^\"]*\"|\\S+").Select(m => m.Value).ToList();
        int i = words.IndexOf("wds");
        var res = new List<string>();
        for (i++; i < words.Count; i++)
        {
            var w = words[i];
            if (w is "--home" or "--client") { i++; continue; }
            if (w is "--json" or "--quiet" or "--yes" or "-y") continue;
            res.Add(w);
        }
        return res;
    }

    /// <summary>0.8.0: every command written in the manual (in code: `wds ...` and code blocks) exists in wds, with options it
    /// has; command-line.md documents every command (a "#### wds &lt;command&gt;" section each), its coverage table is the
    /// feature registry, and its exit codes are the program's.</summary>
    [Fact]
    public void CommandLineChapterMatchesTheProgram()
    {
        var problems = new List<string>();
        int commands = 0;
        foreach (var ch in Chapters().Where(c => File.Exists(Path.Combine(ManualDir, c))))
        {
            var text = File.ReadAllText(Path.Combine(ManualDir, ch));
            var lines = Regex.Matches(text, "`(wds [^`]*)`").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(text, "```\\s*\\n(.*?)```", RegexOptions.Singleline).SelectMany(m => m.Groups[1].Value.Split('\n')).Select(l => l.Trim()).Where(l => l.StartsWith("wds ", StringComparison.Ordinal)));
            foreach (var line in lines)
            {
                var w = CommandWords(line);
                if (w.Count == 0 || w[0].StartsWith("--", StringComparison.Ordinal) || w[0] == "<group>" || w[0] == "[global") continue;
                commands++;
                var spec = w.Count >= 2 ? WoWData.Cli.CliApp.Find(w[0] + " " + w[1]) : null;
                spec ??= WoWData.Cli.CliApp.Find(w[0]);
                if (spec == null)
                {
                    bool group = WoWData.Cli.CliApp.Tree.Any(c => c.Group == w[0]);
                    if (!(group && (w.Count == 1 || w[1].StartsWith("--", StringComparison.Ordinal) || w[1] == "<command>")))
                        problems.Add($"{ch}: \"{line}\": no such command");
                    continue;
                }
                foreach (var o in w.Where(x => x.StartsWith("--", StringComparison.Ordinal)).Select(x => x[2..].Split('=')[0]))
                    if (!spec.Options.Any(x => x.Name == o) && o is not ("help" or "dry-run" or "version"))
                        problems.Add($"{ch}: \"{line}\": wds {spec.Path} has no --{o}");
            }
        }
        Assert.True(commands > 100, $"only {commands} commands found in the manual");

        var cl = File.ReadAllText(Path.Combine(ManualDir, "command-line.md"));
        var documented = Regex.Matches(cl, @"^#### wds (.+?)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var c in WoWData.Cli.CliApp.Tree.Where(c => !documented.Contains(c.Path))) problems.Add($"command-line.md has no section \"#### wds {c.Path}\"");
        foreach (var d in documented.Where(d => WoWData.Cli.CliApp.Find(d) == null)) problems.Add($"command-line.md documents \"wds {d}\", which does not exist");

        // the coverage table is the feature registry
        var coverage = cl[cl.IndexOf("## Coverage", StringComparison.Ordinal)..];
        coverage = coverage[..coverage.IndexOf("\n## ", 5, StringComparison.Ordinal)];
        var rows = Regex.Matches(coverage, @"^\| ([a-z0-9.\-]+) \| (.*?) \| (.*?) \|\s*$", RegexOptions.Multiline)
            .Select(m => (Id: m.Groups[1].Value, Gui: m.Groups[2].Value, Cli: m.Groups[3].Value)).ToList();
        var expected = WoWData.Core.Features.FeatureRegistry.All
            .Select(f => (f.Id, Gui: f.HasGui ? "**" + f.Gui + "**" : "(command line only)", Cli: f.HasCli ? "`wds " + f.Cli + "`" : "(app only: " + f.Reason + ")")).ToList();
        foreach (var e in expected.Except(rows)) problems.Add($"command-line.md's coverage table lacks (or differs in): | {e.Id} | {e.Gui} | {e.Cli} | (\"wds features --markdown\" prints the table)");
        foreach (var r in rows.Except(expected)) problems.Add($"command-line.md's coverage table has a row the feature registry does not: | {r.Id} | {r.Gui} | {r.Cli} |");

        // the exit codes
        var codes = cl[cl.IndexOf("## Exit codes", StringComparison.Ordinal)..];
        codes = codes[..codes.IndexOf("\n## ", 5, StringComparison.Ordinal)];
        var table = Regex.Matches(codes, @"^\| (\d+) \| ([^:|]+)", RegexOptions.Multiline).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value.Trim());
        foreach (var (code, name) in WoWData.Cli.ExitCode.Names)
            if (!table.TryGetValue(code, out var doc) || !string.Equals(doc, name, StringComparison.Ordinal) && !name.StartsWith(doc, StringComparison.Ordinal))
                problems.Add($"command-line.md's exit codes: {code} should be \"{name}\" (is \"{doc}\")");
        foreach (var c in table.Keys.Where(c => !WoWData.Cli.ExitCode.Names.ContainsKey(c))) problems.Add($"command-line.md documents exit code {c}, which wds does not use");

        // linked from the contents, getting started and troubleshooting; every feature chapter has its "Command line" note
        foreach (var f in new[] { "index.md", "getting-started.md", "troubleshooting.md" })
            if (!File.ReadAllText(Path.Combine(ManualDir, f)).Contains("(command-line.md", StringComparison.Ordinal)) problems.Add($"{f} does not link command-line.md");
        foreach (var f in new[] { "main-window.md", "finding-things.md", "texture-viewer.md", "editing-textures.md", "model-viewer.md", "testing-in-game.md", "releasing.md",
                     "my-changes.md", "model-export-import.md", "settings.md", "used-in-game.md", "maps-and-terrain.md", "terrain-in-blender.md", "dbc-editor.md", "server-data.md", "safety.md" })
            if (!Regex.IsMatch(File.ReadAllText(Path.Combine(ManualDir, f)), @"^## Command line\s*$", RegexOptions.Multiline)) problems.Add($"{f} has no \"## Command line\" note");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void HelpTopicsHaveChapters()
    {
        var problems = new List<string>();
        int topics = 0;
        foreach (var f in SourceFiles("*.xaml", "WoWDataStudio"))
        {
            var x = File.ReadAllText(f);
            var names = Regex.Matches(x, "Manual\\.Topic=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(x, "Manual\\.OpenCommand\\}\"\\s+CommandParameter=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
            foreach (var n in names)
            {
                topics++;
                if (!File.Exists(Path.Combine(ManualDir, n + ".md"))) problems.Add($"{Path.GetFileName(f)}: help topic \"{n}\" has no docs\\manual\\{n}.md");
            }
        }
        Assert.True(topics >= 10, $"only {topics} help topics in the XAML");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
