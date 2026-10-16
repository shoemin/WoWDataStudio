namespace WoWData.Core;

/// <summary>
/// 0.8.0 (moved from the app): where a chapter of the user manual is. The installed manual (&lt;install&gt;\manual\*.html
/// next to &lt;install&gt;\bin), a developer build's build\manual, else the Markdown source docs\manual\*.md. F1 / Help in the
/// app and <c>wds manual</c> use it.
/// </summary>
public static class ManualLocator
{
    public const string Contents = "index";

    /// <summary>The file to open for a chapter: rendered HTML if present, else the Markdown source, else null.</summary>
    public static string? Locate(string? topic, string? startDir = null)
    {
        topic = string.IsNullOrWhiteSpace(topic) ? Contents : topic.Trim();
        string? htmlIndex = null, markdown = null;
        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            foreach (var rel in new[] { "manual", Path.Combine("build", "manual") })
            {
                var html = Path.Combine(dir.FullName, rel, topic + ".html");
                if (File.Exists(html)) return html;
                var idx = Path.Combine(dir.FullName, rel, Contents + ".html");
                if (htmlIndex == null && File.Exists(idx)) htmlIndex = idx;
            }
            var md = Path.Combine(dir.FullName, "docs", "manual", topic + ".md");
            if (markdown == null && File.Exists(md)) markdown = md;
        }
        return htmlIndex ?? markdown;
    }
}
