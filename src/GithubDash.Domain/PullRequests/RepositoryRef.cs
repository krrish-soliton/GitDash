namespace GithubDash.Domain.PullRequests;

/// <summary>Identifies a GitHub repository as owner/name.</summary>
public readonly record struct RepositoryRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public bool Matches(RepositoryRef other) =>
        string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => FullName;
}
