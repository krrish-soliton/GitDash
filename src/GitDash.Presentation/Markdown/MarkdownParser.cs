using System.Text.RegularExpressions;

namespace GitDash.Presentation.Markdown;

public enum InlineKind { Text, Bold, Italic, Code, Link, Strike, LineBreak, Mention }

/// <summary>A run of inline text; <see cref="Url"/> is only set for links.</summary>
public sealed record MarkdownInline(InlineKind Kind, string Text, string? Url = null);

public enum BlockKind { Paragraph, Heading, Bullet, Numbered, Task, Quote, Code, Rule, Table }

/// <summary>One block of a Markdown document. A table keeps its cells in <see cref="Rows"/> (the first row is the header).</summary>
public sealed record MarkdownBlock(
    BlockKind Kind, IReadOnlyList<MarkdownInline> Inlines, int Level = 0, bool Checked = false, string? Marker = null,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>>? Rows = null)
{
    public string PlainText => string.Concat(Inlines.Select(i => i.Kind == InlineKind.LineBreak ? "\n" : i.Text));
}

/// <summary>
/// Small, dependency-free Markdown reader covering what GitHub PR and comment text actually uses: headings, lists, task
/// lists, quotes, fenced code (including suggested changes), tables, rules and bold / italic / strikethrough / inline
/// code / links / images / @mentions. Like GitHub comments, single line breaks are kept. The handful of HTML tags that
/// people and review bots put into comments (br, details, summary, img, a, b, i, code, comments) are turned into their
/// Markdown equivalents and the rest is dropped; HTML is never interpreted.
/// </summary>
public static partial class MarkdownParser
{
    private const int MaxLength = 60_000;

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        if (markdown.Length > MaxLength) markdown = markdown[..MaxLength];

        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var lines = CleanOutsideFences(markdown.Replace("\r\n", "\n").Replace('\r', '\n')).Split('\n');

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock(BlockKind.Paragraph, JoinLines(paragraph)));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var language = trimmed[3..].Trim().ToLowerInvariant();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(trimmed[..3], StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new MarkdownBlock(BlockKind.Code, [new MarkdownInline(InlineKind.Code, string.Join('\n', code))],
                    Marker: language.Length == 0 ? null : language));
                continue;
            }

            if (trimmed.Length == 0) { Flush(); continue; }

            if (RuleRegex().IsMatch(trimmed)) { Flush(); blocks.Add(new MarkdownBlock(BlockKind.Rule, [])); continue; }

            // A table is a row of cells followed by a separator row like |---|:---:|.
            if (trimmed.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1]))
            {
                Flush();
                var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>> { Cells(trimmed) };
                for (i += 2; i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains('|'); i++) rows.Add(Cells(lines[i].Trim()));
                i--;
                blocks.Add(new MarkdownBlock(BlockKind.Table, [], Rows: rows));
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, ParseInlines(heading.Groups[2].Value), heading.Groups[1].Length));
                continue;
            }

            var task = TaskRegex().Match(line);
            if (task.Success)
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Task, ParseInlines(task.Groups[4].Value), task.Groups[1].Length / 2,
                    task.Groups[3].Value is "x" or "X"));
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Bullet, ParseInlines(bullet.Groups[2].Value), bullet.Groups[1].Length / 2));
                continue;
            }

            var numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Numbered, ParseInlines(numbered.Groups[3].Value),
                    numbered.Groups[1].Length / 2, Marker: numbered.Groups[2].Value + "."));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                Flush();
                // Consecutive quoted lines are one quote.
                var quoted = new List<string> { trimmed.TrimStart('>').Trim() };
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('>')) quoted.Add(lines[++i].Trim().TrimStart('>').Trim());
                blocks.Add(new MarkdownBlock(BlockKind.Quote, JoinLines(quoted)));
                continue;
            }

            paragraph.Add(trimmed);
        }

        Flush();
        return blocks;
    }

    /// <summary>Lines of one paragraph, with a line break between them (GitHub keeps the author's line breaks in comments).</summary>
    private static List<MarkdownInline> JoinLines(IReadOnlyList<string> lines)
    {
        var result = new List<MarkdownInline>();
        for (var n = 0; n < lines.Count; n++)
        {
            if (n > 0) result.Add(new MarkdownInline(InlineKind.LineBreak, string.Empty));
            result.AddRange(ParseInlines(lines[n]));
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells(string row)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        return [.. SplitCells(text).Select(c => (IReadOnlyList<MarkdownInline>)ParseInlines(c.Trim().Replace("\\|", "|")))];
    }

    private static IEnumerable<string> SplitCells(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '|' || (i > 0 && text[i - 1] == '\\')) continue;
            yield return text[start..i];
            start = i + 1;
        }

        yield return text[start..];
    }

    /// <summary>Turns the HTML that shows up in comments into Markdown (or nothing), leaving fenced code exactly as written.</summary>
    private static string CleanOutsideFences(string text)
    {
        if (!text.Contains('<') && !text.Contains("```") && !text.Contains("~~~")) return text;
        var output = new List<string>();
        var chunk = new List<string>();
        var fence = (string?)null;
        void FlushChunk()
        {
            if (chunk.Count == 0) return;
            output.Add(CleanHtml(string.Join('\n', chunk)));
            chunk.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                FlushChunk();
                fence = trimmed[..3];
                output.Add(line);
            }
            else if (fence is not null)
            {
                output.Add(line);
                if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
            }
            else
            {
                chunk.Add(line);
            }
        }

        FlushChunk();
        return string.Join('\n', output);
    }

    private static string CleanHtml(string text)
    {
        if (!text.Contains('<')) return text;
        text = HtmlCommentRegex().Replace(text, string.Empty);
        text = HtmlBreakRegex().Replace(text, "\n");
        text = HtmlSummaryRegex().Replace(text, m => "\n**▸ " + StripTags(m.Groups[1].Value).Trim() + "**\n");
        text = HtmlImageRegex().Replace(text, m =>
        {
            var tag = m.Value;
            var src = Attribute(tag, "src");
            var alt = Attribute(tag, "alt");
            var label = string.IsNullOrWhiteSpace(alt) ? "image" : alt;
            return src is not null && src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"[🖼 {label}]({src})" : $"🖼 {label}";
        });
        text = HtmlLinkRegex().Replace(text, m => $"[{StripTags(m.Groups[2].Value).Trim()}]({m.Groups[1].Value})");
        text = HtmlBoldRegex().Replace(text, "**$2**");
        text = HtmlItalicRegex().Replace(text, "*$2*");
        text = HtmlStrikeRegex().Replace(text, "~~$2~~");
        text = HtmlCodeRegex().Replace(text, "`$1`");
        text = HtmlBlockTagRegex().Replace(text, "\n");
        return HtmlOtherTagRegex().Replace(text, string.Empty);
    }

    private static string StripTags(string text) => HtmlAnyTagRegex().Replace(text, string.Empty);

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        var index = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > index) result.Add(new MarkdownInline(InlineKind.Text, text[index..m.Index]));
            if (m.Groups["code"].Success) result.Add(new MarkdownInline(InlineKind.Code, m.Groups["code"].Value));
            else if (m.Groups["img"].Success)
                result.Add(new MarkdownInline(InlineKind.Link,
                    "🖼 " + (m.Groups["alt"].Value.Length > 0 ? m.Groups["alt"].Value : "image"), m.Groups["img"].Value));
            else if (m.Groups["bold"].Success) result.Add(new MarkdownInline(InlineKind.Bold, m.Groups["bold"].Value));
            else if (m.Groups["strike"].Success) result.Add(new MarkdownInline(InlineKind.Strike, m.Groups["strike"].Value));
            else if (m.Groups["italic"].Success) result.Add(new MarkdownInline(InlineKind.Italic, m.Groups["italic"].Value));
            else if (m.Groups["label"].Success)
                result.Add(new MarkdownInline(InlineKind.Link, m.Groups["label"].Value, m.Groups["url"].Value));
            else if (m.Groups["bare"].Success)
                result.Add(new MarkdownInline(InlineKind.Link, m.Groups["bare"].Value, m.Groups["bare"].Value));
            else if (m.Groups["mention"].Success) result.Add(new MarkdownInline(InlineKind.Mention, "@" + m.Groups["mention"].Value));
            index = m.Index + m.Length;
        }

        if (index < text.Length) result.Add(new MarkdownInline(InlineKind.Text, text[index..]));
        return result;
    }

    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^(\s*)([-*+])\s+\[( |x|X)\]\s+(.*)$")] private static partial Regex TaskRegex();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$")] private static partial Regex NumberedRegex();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex HtmlCommentRegex();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBreakRegex();
    [GeneratedRegex(@"<summary[^>]*>(.*?)</summary>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlSummaryRegex();
    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlImageRegex();
    [GeneratedRegex(@"<a\s[^>]*?href\s*=\s*""(https?://[^""]+)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlLinkRegex();
    [GeneratedRegex(@"<(b|strong)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlBoldRegex();
    [GeneratedRegex(@"<(i|em)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlItalicRegex();
    [GeneratedRegex(@"<(s|del|strike)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlStrikeRegex();
    [GeneratedRegex(@"<code>(.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HtmlCodeRegex();
    [GeneratedRegex(@"</?(details|p|div|h[1-6]|ul|ol|li|tr|table|thead|tbody|blockquote|pre|hr|center|picture)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBlockTagRegex();
    [GeneratedRegex(@"</?(span|sub|sup|kbd|ins|td|th|source|font|u|small|big|mark|abbr|figure|figcaption)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HtmlOtherTagRegex();
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")] private static partial Regex HtmlAnyTagRegex();

    [GeneratedRegex(@"`(?<code>[^`]+)`|!\[(?<alt>[^\]]*)\]\((?<img>https?://[^\s)]+)\)|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|~~(?<strike>.+?)~~|(?<![\*\w])\*(?<italic>[^*\s][^*]*?)\*(?!\w)|(?<![\w_])_(?<italic>[^_\s][^_]*?)_(?![\w_])|\[(?<label>[^\]]+)\]\((?<url>https?://[^\s)]+)\)|(?<bare>https?://[^\s<>)]+)|(?<![\w/@`.-])@(?<mention>[A-Za-z0-9][A-Za-z0-9-]{0,38})(?![\w-])")]
    private static partial Regex InlineRegex();
}
