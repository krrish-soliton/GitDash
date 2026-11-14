using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Domain.Actions;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>One workflow run in the Actions panel.</summary>
public sealed record RunItemViewModel(
    long Id, string Name, string Where, string Who, string PullRequest, string Status, string Tone,
    double Progress, bool IsActive, bool IsWaiting, string Elapsed, string Url, string Accent);

/// <summary>
/// The Actions panel of the My PRs page: every recent workflow run, running ones first, each with a progress bar.
/// While the panel is open the monitor polls every few seconds, so the bars move.
/// </summary>
public sealed class ActionsViewModel : ObservableObject, IDisposable
{
    private readonly ActionsMonitor _monitor;
    private readonly IUrlLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private bool _isOpen, _hasRuns, _hasError;
    private int _activeCount;
    private string _header = "Actions", _error = string.Empty;
    private HashSet<string> _scope = new(StringComparer.OrdinalIgnoreCase);
    private string? _current;
    private bool _onlyCurrent;

    public ActionsViewModel(ActionsMonitor monitor, IUrlLauncher launcher, IUiDispatcher ui, TimeProvider time)
    {
        _monitor = monitor;
        _launcher = launcher;
        _ui = ui;
        _time = time;
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        OpenRunCommand = new RelayCommand<RunItemViewModel>(r => _launcher.Open(r.Url));
        _monitor.Changed += OnChanged;
        Apply(_monitor.State);
    }

    public ObservableCollection<RunItemViewModel> Runs { get; } = [];

    /// <summary>
    /// Which pull requests the panel is about: the ones on the cards (keys from <see cref="ActionsState.KeyOf"/>) and the one
    /// in front. Runs that belong to no card are not listed.
    /// </summary>
    public void SetScope(IEnumerable<string> cardKeys, string? currentKey)
    {
        var keys = new HashSet<string>(cardKeys, StringComparer.OrdinalIgnoreCase);
        if (keys.SetEquals(_scope) && string.Equals(_current, currentKey, StringComparison.OrdinalIgnoreCase)) return;
        _scope = keys;
        _current = currentKey;
        Apply(_monitor.State);
    }

    /// <summary>Show only the runs of the card that is in front.</summary>
    public bool OnlyCurrentCard
    {
        get => _onlyCurrent;
        set
        {
            if (!Set(ref _onlyCurrent, value)) return;
            Raise(nameof(ScopeHint));
            Apply(_monitor.State);
        }
    }

    public string ScopeHint => _onlyCurrent ? "Runs of the pull request in front" : "Runs of the pull requests on the cards";

    private bool InScope(WorkflowRun run)
    {
        if (_onlyCurrent) return _current is not null && run.PullRequests.Any(n => string.Equals(ActionsState.KeyOf(run.Repository, n), _current, StringComparison.OrdinalIgnoreCase));
        return run.PullRequests.Any(n => _scope.Contains(ActionsState.KeyOf(run.Repository, n)));
    }

    /// <summary>The runs of one pull request, for its tab in the details popup.</summary>
    public IReadOnlyList<RunItemViewModel> RunsFor(GitDash.Domain.PullRequests.RepositoryRef repository, int number)
    {
        var now = _time.GetUtcNow();
        return [.. _monitor.State.RunsFor(repository, number).Select(r => Map(r, now))];
    }

    /// <summary>Latest state, for the run counts and tags on the pull request cards.</summary>
    public ActionsState State => _monitor.State;

    /// <summary>Raised on the UI thread after the list was refreshed.</summary>
    public event Action<ActionsState>? Updated;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!Set(ref _isOpen, value)) return;
            _monitor.SetLive(value);
        }
    }

    public bool HasRuns { get => _hasRuns; private set => Set(ref _hasRuns, value); }
    public int ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }
    public string Header { get => _header; private set => Set(ref _header, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenRunCommand { get; }

    private void OnChanged(ActionsState state) => _ui.Post(() => Apply(state));

    private void Apply(ActionsState state)
    {
        var now = _time.GetUtcNow();
        var inScope = state.Runs.Where(InScope).ToList();
        var fresh = inScope.Select(r => Map(r, now)).ToList();
        if (Runs.Select(r => r.Id).SequenceEqual(fresh.Select(r => r.Id)))
        {
            // Same runs: swap only the rows whose numbers moved, so the list does not flash on every poll.
            for (var i = 0; i < fresh.Count; i++)
            {
                if (Runs[i] != fresh[i]) Runs[i] = fresh[i];
            }
        }
        else
        {
            Runs.Clear();
            foreach (var run in fresh) Runs.Add(run);
        }

        HasRuns = Runs.Count > 0;
        ActiveCount = inScope.Count(r => r.IsActive);
        Header = ActiveCount > 0 ? $"Actions · {ActiveCount} running" : "Actions";
        HasError = !string.IsNullOrEmpty(state.Error);
        Error = state.Error ?? string.Empty;
        Updated?.Invoke(state);
    }

    private static RunItemViewModel Map(WorkflowRun run, DateTimeOffset now) => new(
        run.Id,
        run.Name,
        $"{run.Repository.Name} · {run.Branch}",
        run.Actor,
        run.PullRequests.Count == 0 ? string.Empty : string.Join(" ", run.PullRequests.Select(n => $"#{n}")),
        run.Status switch
        {
            RunStatus.Queued => "Queued",
            RunStatus.InProgress => run.StepsTotal > 0 ? $"Running · {run.StepsDone}/{run.StepsTotal} steps" : "Running",
            _ when run.Succeeded => "Passed",
            _ when run.Failed => "Failed",
            _ => run.Conclusion is { Length: > 0 } c ? char.ToUpperInvariant(c[0]) + c[1..].Replace('_', ' ') : "Finished",
        },
        run.Status switch
        {
            RunStatus.Queued => "assigned",
            RunStatus.InProgress => "open",
            _ when run.Succeeded => "approved",
            _ when run.Failed => "removed",
            _ => "unreviewed",
        },
        run.Progress,
        run.IsActive,
        run.Status == RunStatus.Queued,
        run.IsActive ? Humanize.Clock(now - run.StartedAt) : Humanize.Ago(now - run.UpdatedAt),
        run.Url,
        run.Status switch
        {
            RunStatus.Queued => "#FBBF24",
            RunStatus.InProgress => "#22D3EE",
            _ when run.Succeeded => "#34D399",
            _ when run.Failed => "#F87171",
            _ => "#94A3B8",
        });

    public void Dispose()
    {
        _monitor.Changed -= OnChanged;
        _monitor.SetLive(false);
    }
}
