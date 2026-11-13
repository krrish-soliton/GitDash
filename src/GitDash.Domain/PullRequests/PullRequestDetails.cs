using System.Text.RegularExpressions;

namespace GitDash.Domain.PullRequests;

public enum CommentSource
{
    /// <summary>A comment in the pull request conversation.</summary>
    Conversation,
    /// <summary>The summary text of a review.</summary>
    Review,
    /// <summary>A comment on a line of code.</summary>
    Inline,
}

public sealed record PullRequestComment(
    DateTimeOffset At,
    string Author,
    string Body,
    CommentSource Source,
    string? Path = null,
    bool? IsResolved = null)
{
    /// <summary>A code comment whose conversation was marked resolved.</summary>
    public bool IsResolvedThread => IsResolved == true;

    /// <summary>A code comment whose conversation is still open (other kinds of comment cannot be resolved).</summary>
    public bool IsUnresolvedThread => IsResolved == false;

    /// <summary>Written by an AI review tool (Copilot, Claude, CodeRabbit, …).</summary>
    public bool IsAi => AiAuthors.IsAi(Author);

    /// <summary>True when the text @-mentions the login as a whole word ("@krish", not "@krishna").</summary>
    public bool Mentions(string login) =>
        login.Length > 0 &&
        Regex.IsMatch(
            Body,
            $@"(?<![\w-])@{Regex.Escape(login)}(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
}

/// <summary>Everything shown when a pull request is opened: loaded on demand, never during regular syncing.</summary>
public sealed record PullRequestDetails(
    string Description,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyList<PullRequestComment> Comments);
