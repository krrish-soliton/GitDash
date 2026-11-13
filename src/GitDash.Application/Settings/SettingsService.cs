using GitDash.Application.Abstractions;

namespace GitDash.Application.Settings;

/// <summary>Single owner of the current settings; all changes go through <see cref="UpdateAsync"/>.</summary>
public sealed class SettingsService(ISettingsStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised (on an arbitrary thread) after settings were changed and saved.</summary>
    public event Action<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Current = await store.LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)
    {
        AppSettings updated;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            updated = change(Current);
            if (ReferenceEquals(updated, Current)) return;
            await store.SaveAsync(updated, ct).ConfigureAwait(false);
            Current = updated;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(updated);
    }
}
