using System.Text.RegularExpressions;

namespace GitDash.Presentation.Markdown;

public enum InlineKind { Text, Bold, Italic, Code, Link }

/// <summary>A run of inline text; <see cref="Url"/> is only set for links.</summary>
public sealed record MarkdownInline(InlineKind Kind, string Text, string? Url = null);

public enum BlockKind { Paragraph, Heading, Bullet, Numbered, Task, Quote, Code, Rule }

/// <summary>One block of a Markdown document.</summary>
public sealed record MarkdownBlock(
    BlockKind Kind, IReadOnlyList<MarkdownInline> Inlines, int Level = 0, bool Checked = false, string? Marker = null)
{
    public string PlainText => string.Concat(Inlines.Select(i => i.Text));
}

/// <summary>
/// Small, dependency-free Markdown reader covering what GitHub PR text actually uses: headings, lists, task lists,
/// quotes, fenced code, rules and bold / italic / inline code / links. HTML is never interpreted, only shown as text.
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
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        void Flush()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock(BlockKind.Paragraph, ParseInlines(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(trimmed[..3], StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new MarkdownBlock(BlockKind.Code, [new MarkdownInline(InlineKind.Code, string.Join('\n', code))]));
                continue;
            }

            if (trimmed.Length == 0) { Flush(); continue; }

            if (RuleRegex().IsMatch(trimmed)) { Flush(); blocks.Add(new MarkdownBlock(BlockKind.Rule, [])); continue; }

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
                blocks.Add(new MarkdownBlock(BlockKind.Quote, ParseInlines(trimmed.TrimStart('>').Trim())));
                continue;
            }

            paragraph.Add(trimmed);
        }

        Flush();
        return blocks;
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        var index = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > index) result.Add(new MarkdownInline(InlineKind.Text, text[index..m.Index]));
            if (m.Groups["code"].Success) result.Add(new MarkdownInline(InlineKind.Code, m.Groups["code"].Value));
            else if (m.Groups["bold"].Success) result.Add(new MarkdownInline(InlineKind.Bold, m.Groups["bold"].Value));
            else if (m.Groups["italic"].Success) result.Add(new MarkdownInline(InlineKind.Italic, m.Groups["italic"].Value));
            else if (m.Groups["label"].Success)
                result.Add(new MarkdownInline(InlineKind.Link, m.Groups["label"].Value, m.Groups["url"].Value));
            else if (m.Groups["bare"].Success)
                result.Add(new MarkdownInline(InlineKind.Link, m.Groups["bare"].Value, m.Groups["bare"].Value));
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

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|(?<![\*\w])\*(?<italic>[^*\s][^*]*?)\*(?!\w)|\[(?<label>[^\]]+)\]\((?<url>https?://[^\s)]+)\)|(?<bare>https?://[^\s<>)]+)")]
    private static partial Regex InlineRegex();
}
