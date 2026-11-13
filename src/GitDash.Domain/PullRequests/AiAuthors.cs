namespace GitDash.Domain.PullRequests;

/// <summary>Recognises comments written by AI review tools from the author's login.</summary>
public static class AiAuthors
{
    private static readonly string[] Markers =
    [
        "copilot", "claude", "chatgpt", "openai", "codex", "gemini", "coderabbit", "sourcery", "codeium",
        "cursor", "devin", "qodo", "codium", "greptile", "ellipsis", "sweep-ai", "amazon-q", "graphite-app",
        "korbit", "bito", "sonarqube-ai", "ai-review", "ai-bot",
    ];

    public static bool IsAi(string? login)
    {
        if (string.IsNullOrEmpty(login)) return false;
        var name = login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? login[..^5] : login;
        return Markers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
