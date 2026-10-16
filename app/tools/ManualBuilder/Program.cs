// Renders the WoW Data Studio user manual (docs\manual\*.md) to static HTML pages.
//
//   dotnet run --project app\tools\ManualBuilder -- [--manual <docs\manual>] [--out <folder>] [--version <x.y.z>]
//
// Defaults (found from the current folder upwards): --manual <WoWDataTool>\docs\manual, --out <WoWDataTool>\build\manual.
// The chapters and their order come from the links in index.md. Links between chapters (x.md, x.md#part) become x.html,
// images under ..\screenshots\ are copied to <out>\screenshots\. Exit code 1 when a linked chapter or an image is missing.
using System.Net;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

string? manualDir = null, outDir = null, version = null;
for (int i = 0; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--manual": manualDir = Next(); break;
        case "--out": outDir = Next(); break;
        case "--version": version = Next(); break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

if (manualDir == null)
{
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
    {
        var m = Path.Combine(d.FullName, "docs", "manual");
        if (File.Exists(Path.Combine(m, "index.md"))) { manualDir = m; break; }
    }
    if (manualDir == null) { Console.Error.WriteLine("docs\\manual\\index.md not found; pass --manual"); return 2; }
}
manualDir = Path.GetFullPath(manualDir);
outDir = Path.GetFullPath(outDir ?? Path.Combine(manualDir, "..", "..", "build", "manual"));

var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
var problems = new List<string>();

// chapter order = links in index.md
var indexDoc = Markdown.Parse(File.ReadAllText(Path.Combine(manualDir, "index.md")), pipeline);
var chapters = new List<string> { "index" };
foreach (var link in indexDoc.Descendants<LinkInline>())
{
    var url = link.Url ?? "";
    if (link.IsImage || url.Contains("://")) continue;
    var file = url.Split('#')[0];
    if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
    var name = Path.GetFileNameWithoutExtension(file);
    if (!chapters.Contains(name, StringComparer.OrdinalIgnoreCase)) chapters.Add(name);
}

if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(Path.Combine(outDir, "screenshots"));

var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var docs = new Dictionary<string, MarkdownDocument>(StringComparer.OrdinalIgnoreCase);
foreach (var ch in chapters)
{
    var md = Path.Combine(manualDir, ch + ".md");
    if (!File.Exists(md)) { problems.Add($"index.md links to {ch}.md, which does not exist"); continue; }
    var doc = Markdown.Parse(File.ReadAllText(md), pipeline);
    docs[ch] = doc;
    var h1 = doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
    titles[ch] = h1?.Inline != null ? PlainText(h1.Inline) : ch;
}

var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var present = chapters.Where(docs.ContainsKey).ToList();
for (int n = 0; n < present.Count; n++)
{
    var ch = present[n];
    var doc = docs[ch];
    foreach (var link in doc.Descendants<LinkInline>())
    {
        var url = link.Url ?? "";
        if (url.Length == 0 || url.Contains("://") || url.StartsWith('#') || url.StartsWith("mailto:", StringComparison.Ordinal)) continue;
        if (link.IsImage)
        {
            var src = Path.GetFullPath(Path.Combine(manualDir, url.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(src)) { problems.Add($"{ch}.md: image {url} is missing"); continue; }
            var name = Path.GetFileName(src);
            if (copied.Add(name)) File.Copy(src, Path.Combine(outDir, "screenshots", name), true);
            link.Url = "screenshots/" + name;
            continue;
        }
        var parts = url.Split('#', 2);
        if (parts[0].EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.GetFileNameWithoutExtension(parts[0]);
            if (!File.Exists(Path.Combine(manualDir, parts[0]))) problems.Add($"{ch}.md: link to {parts[0]}, which does not exist");
            link.Url = target + ".html" + (parts.Length > 1 ? "#" + parts[1] : "");
        }
    }
    var body = new StringWriter();
    var renderer = new HtmlRenderer(body);
    pipeline.Setup(renderer);
    renderer.Render(doc);
    body.Flush();
    var prev = n > 0 ? present[n - 1] : null;
    var next = n + 1 < present.Count ? present[n + 1] : null;
    File.WriteAllText(Path.Combine(outDir, ch + ".html"), Page(ch, titles[ch], body.ToString(), prev, next), new UTF8Encoding(false));
}

// every "page.html#part" link must point at a heading that exists (headings get their ids while rendering)
var ids = present.ToDictionary(c => c + ".html", c => System.Text.RegularExpressions.Regex.Matches(
        File.ReadAllText(Path.Combine(outDir, c + ".html")), "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(),
    StringComparer.OrdinalIgnoreCase);
foreach (var c in present)
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                 File.ReadAllText(Path.Combine(outDir, c + ".html")), "href=\"([^\":]*)#([^\"]+)\""))
    {
        var page = m.Groups[1].Value.Length == 0 ? c + ".html" : m.Groups[1].Value;
        if (ids.TryGetValue(page, out var set) && !set.Contains(m.Groups[2].Value))
            problems.Add($"{c}.md: link to {page}#{m.Groups[2].Value}, but that page has no such heading");
    }

Console.WriteLine($"manual: {present.Count} pages, {copied.Count} images -> {outDir}");
foreach (var p in problems) Console.Error.WriteLine("problem: " + p);
return problems.Count == 0 ? 0 : 1;

string PlainText(ContainerInline inline)
{
    var sb = new StringBuilder();
    foreach (var x in inline.Descendants<LiteralInline>()) sb.Append(x.Content.ToString());
    foreach (var x in inline.Descendants<CodeInline>()) sb.Append(x.Content);
    return sb.ToString().Trim();
}

string Page(string ch, string title, string body, string? prev, string? next)
{
    string E(string s) => WebUtility.HtmlEncode(s);
    var nav = new StringBuilder();
    foreach (var c in present)
        nav.Append($"<li{(c == ch ? " class=\"here\"" : "")}><a href=\"{c}.html\">{E(c == "index" ? "Contents" : titles[c])}</a></li>\n");
    var pager = new StringBuilder("<nav class=\"pager\">");
    if (prev != null) pager.Append($"<a href=\"{prev}.html\">&larr; {E(prev == "index" ? "Contents" : titles[prev])}</a>");
    pager.Append("<span></span>");
    if (next != null) pager.Append($"<a href=\"{next}.html\">{E(titles[next])} &rarr;</a>");
    pager.Append("</nav>");
    var ver = version != null ? " " + E(version) : "";
    return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{E(title)}} - WoW Data Studio manual</title>
<style>
:root { --bg:#f7f8fa; --panel:#ffffff; --text:#1d2430; --muted:#5d6878; --accent:#1f5fae; --border:#dfe3ea; --code:#eef1f5; }
@media (prefers-color-scheme: dark) { :root { --bg:#16191e; --panel:#1e2228; --text:#e3e7ee; --muted:#9aa5b4; --accent:#7fb2f0; --border:#323842; --code:#2a2f37; } }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:16px/1.6 "Segoe UI", system-ui, sans-serif; }
header { background:var(--accent); color:#fff; padding:10px 20px; font-weight:600; }
header span { font-weight:400; opacity:.85; }
.wrap { display:flex; max-width:1280px; margin:0 auto; }
aside { width:270px; flex:none; padding:18px 12px; border-right:1px solid var(--border); }
aside ol { margin:0; padding-left:0; list-style:none; } aside li { margin:3px 0; font-size:14px; }
aside li.here > a { font-weight:700; color:var(--text); }
main { flex:1; min-width:0; padding:8px 36px 40px; background:var(--panel); }
a { color:var(--accent); }
h1 { font-size:30px; margin:18px 0 8px; } h2 { margin-top:34px; border-bottom:1px solid var(--border); padding-bottom:4px; } h3 { margin-top:26px; }
img { max-width:100%; height:auto; border:1px solid var(--border); border-radius:6px; margin:8px 0; }
code { background:var(--code); padding:1px 5px; border-radius:4px; font-size:90%; }
pre { background:var(--code); padding:10px 14px; border-radius:6px; overflow-x:auto; } pre code { padding:0; }
kbd { border:1px solid var(--border); border-bottom-width:2px; border-radius:4px; padding:0 5px; font-size:90%; background:var(--code); }
table { border-collapse:collapse; margin:12px 0; display:block; overflow-x:auto; } th, td { border:1px solid var(--border); padding:5px 10px; text-align:left; vertical-align:top; }
blockquote { margin:12px 0; padding:8px 14px; border-left:4px solid var(--accent); background:var(--code); border-radius:0 6px 6px 0; }
blockquote p { margin:4px 0; }
.pager { display:flex; justify-content:space-between; gap:12px; margin-top:40px; padding-top:14px; border-top:1px solid var(--border); }
@media (max-width: 800px) { .wrap { display:block; } aside { width:auto; border-right:0; border-bottom:1px solid var(--border); } main { padding:8px 16px 30px; } }
</style>
</head>
<body>
<header>WoW Data Studio{{ver}} <span>&middot; user manual</span></header>
<div class="wrap">
<aside><ol>
{{nav}}</ol></aside>
<main>
{{body}}
{{pager}}
</main>
</div>
</body>
</html>
""";
}
