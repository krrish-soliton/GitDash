namespace GitDash.Domain.PullRequests;

public enum ReviewState
{
    Pending,
    Commented,
    Approved,
    ChangesRequested,
    Dismissed,
}

public sealed record Review(string Author, ReviewState State, DateTimeOffset SubmittedAt, int CommentCount);
