using System.Text.Json;
using System.Text.Json.Serialization;
using GitDash.Application.Abstractions;
using GitDash.Application.Settings;
using GitDash.Domain.Timers;

namespace GitDash.Infrastructure.Storage;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class JsonSettingsStore(AppPaths paths) : ISettingsStore
{
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(paths.SettingsFile)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(paths.SettingsFile);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Json.Options, ct).ConfigureAwait(false);
            return Sanitize(loaded ?? new AppSettings());
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start clean rather than crash on launch.
            File.Move(paths.SettingsFile, paths.SettingsFile + ".corrupt", overwrite: true);
            return new AppSettings();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) =>
        AppPaths.WriteAtomicAsync(
            paths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(Sanitize(settings), Json.Options), ct);

    /// <summary>The file is user-editable, so treat it as untrusted input.</summary>
    private static AppSettings Sanitize(AppSettings s)
    {
        var accounts = (s.Accounts ?? [])
            .Where(a => a is not null && a.Id != Guid.Empty && IsName(a.Login))
            .DistinctBy(a => a.Id)
            .ToList();
        var ids = accounts.Select(a => a.Id).ToHashSet();
        return s with
        {
            Accounts = accounts,
            Repositories =
            [
                .. (s.Repositories ?? [])
                    .Where(r => r is not null && ids.Contains(r.AccountId) && IsName(r.Owner) && IsName(r.Name))
                    .DistinctBy(r => (r.AccountId, r.Owner.ToLowerInvariant(), r.Name.ToLowerInvariant())),
            ],
            Notifications = s.Notifications ?? new(),
            Pomodoro = s.Pomodoro ?? new PomodoroSettings(),
            OAuthClientId = (s.OAuthClientId ?? string.Empty).Trim(),
            AiTools =
            [
                .. (s.AiTools ?? [])
                    .Where(t => t is not null && t.Id != Guid.Empty && !string.IsNullOrWhiteSpace(t.Name))
                    .DistinctBy(t => t.Id)
                    .Take(20)
                    .Select(t => t with
                    {
                        Name = t.Name.Trim().Length > 40 ? t.Name.Trim()[..40] : t.Name.Trim(),
                        WindowMinutes = Math.Clamp(t.WindowMinutes, 1, 60 * 24 * 31),
                    }),
            ],
            LiveCheckSeconds = Math.Clamp(s.LiveCheckSeconds, AppSettings.MinLiveCheckSeconds, AppSettings.MaxLiveCheckSeconds),
        };
    }

    private static bool IsName(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 100 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed class JsonTimerSessionStore(AppPaths paths) : ITimerSessionStore
{
    public async Task<IReadOnlyList<TimerSession>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(paths.TimerSessionsFile)) return [];
        try
        {
            await using var stream = File.OpenRead(paths.TimerSessionsFile);
            return await JsonSerializer.DeserializeAsync<List<TimerSession>>(stream, Json.Options, ct)
                .ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task SaveAsync(IReadOnlyList<TimerSession> sessions, CancellationToken ct = default) =>
        AppPaths.WriteAtomicAsync(
            paths.TimerSessionsFile, JsonSerializer.SerializeToUtf8Bytes(sessions, Json.Options), ct);
}
