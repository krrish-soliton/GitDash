using GitDash.Application.Abstractions;

namespace GitDash.Application.Notifications;

/// <summary>One notification that was shown, kept so it can be read again later.</summary>
public sealed record NotificationEntry(
    Guid Id, DateTimeOffset At, string Title, string Message, ToastTone Tone, string? Url, string? Actor, bool Read);

/// <summary>
/// The last <see cref="Capacity"/> notifications, newest first, with a read flag. Saved after every change so the
/// list survives a restart; a failed save never disturbs the app.
/// </summary>
public sealed class NotificationHistory(INotificationStore store, TimeProvider time)
{
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private List<NotificationEntry> _entries = [];

    /// <summary>Raised after any change; may come from any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<NotificationEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public int UnreadCount
    {
        get { lock (_gate) return _entries.Count(e => !e.Read); }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NotificationEntry> loaded;
        try { loaded = await store.LoadAsync(ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { loaded = []; }

        lock (_gate)
        {
            // Anything recorded while loading stays on top.
            _entries = [.. _entries.Concat(loaded.Where(l => _entries.All(e => e.Id != l.Id)))
                .OrderByDescending(e => e.At).Take(Capacity)];
        }

        Changed?.Invoke();
    }

    public void Record(ToastRequest request)
    {
        var entry = new NotificationEntry(
            Guid.NewGuid(), time.GetUtcNow(), request.Title, request.Message, request.Tone, request.Url, request.Actor, Read: false);
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }

        Commit();
    }

    public void MarkRead(Guid id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0 || _entries[index].Read) return;
            _entries[index] = _entries[index] with { Read = true };
        }

        Commit();
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            if (_entries.All(e => e.Read)) return;
            _entries = [.. _entries.Select(e => e with { Read = true })];
        }

        Commit();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries = [];
        }

        Commit();
    }

    private void Commit()
    {
        Changed?.Invoke();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            await store.SaveAsync(Entries).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // History is a convenience; losing a save is better than failing the notification itself.
        }
        finally
        {
            _saving.Release();
        }
    }
}
