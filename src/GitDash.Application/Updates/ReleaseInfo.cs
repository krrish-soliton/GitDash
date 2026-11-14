using GitDash.Domain.Updates;

namespace GitDash.Application.Updates;

/// <summary>A file attached to a GitHub release. <see cref="Sha256"/> is the published checksum when GitHub reports one.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, string ApiUrl, long Size, string? Sha256 = null);

public sealed record ReleaseInfo(
    AppVersion Version, string Tag, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);
