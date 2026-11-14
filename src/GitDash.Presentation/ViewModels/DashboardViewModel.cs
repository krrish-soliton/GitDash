using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Domain.Notifications;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

public sealed record RepositoryRowViewModel(string Name, int Open, int Mine, double Share);

/// <summary>One PR drawn as a book lying on the desk.</summary>
public sealed record DeskBookViewModel(
    string Title, string Repository, string Number, string Accent, double Height, double Tilt, bool IsAssigned, bool NeedsApproval,
    string Waiting, string Url, IReadOnlyList<PullRequestTag> Tags, string Author,
    string Id, double Width, int Additions, int Deletions, int Files, int Commits, string Summary)
    : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Set in place when the book is picked, so the shelf is not rebuilt (and the books do not drop in again).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public bool HasTags => Tags.Count > 0;
    public string Tip => $"{Title} · by {Author}";
    public string Lines => $"+{Additions:N0} / −{Deletions:N0}";
    public string FilesText => Files == 1 ? "1 file" : $"{Files:N0} files";
    public string CommitsText => Commits == 1 ? "1 commit" : $"{Commits:N0} commits";
    public string Size => Humanize.SizeLabel(Additions + Deletions);
    public bool HasSummary => Summary.Length > 0;
    public bool HasWaiting => Waiting.Length > 0;
    public string Where => $"{Repository} · {Number}";
}

/// <summary>One shelf of the bookcase.</summary>
public sealed record ShelfViewModel(IReadOnlyList<DeskBookViewModel> Books);

public sealed record ActivityItemViewModel(string Title, string Message, string When, string Tone, string Url);

public sealed class DashboardViewModel : PageViewModel
{
    private readonly TimeProvider _time;
    private int _totalOpen, _assignedToMe, _needsMyApproval;
    private bool _isEmpty = true, _hasActivity, _deskIsEmpty = true;
    private string _deskSummary = string.Empty, _oldestAssignedOnDesk = string.Empty, _oldestReviewOnDesk = string.Empty;
    private int _deskOverflow;
    private List<DeskBookViewModel> _books = [];
    private string? _selectedId;
    private DeskBookViewModel? _selectedBook;
    private const int BooksPerShelf = 8, ShelfCount = 3;

    public DashboardViewModel(IUrlLauncher launcher, TimeProvider time, Action openSettings)
        : base("Dashboard", "IconDashboard")
    {
        _time = time;
        OpenUrlCommand = new RelayCommand<string>(url => launcher.Open(url));
        OpenPullRequestCommand = new RelayCommand<string>(url =>
        {
            if (string.IsNullOrEmpty(url)) return;
            if (PullRequestOpener is { } open) open(url); else launcher.Open(url);
        });
        OpenSettingsCommand = new RelayCommand(openSettings);
        SelectBookCommand = new RelayCommand<DeskBookViewModel>(book => Select(book?.Id));
        CloseBookCommand = new RelayCommand(() => Select(null));
    }

    /// <summary>The book whose details are open in the popup, if any; it is highlighted in the list.</summary>
    public DeskBookViewModel? SelectedBook
    {
        get => _selectedBook;
        private set { if (Set(ref _selectedBook, value)) Raise(nameof(HasSelectedBook)); }
    }
    public bool HasSelectedBook => _selectedBook is not null;
    public ICommand SelectBookCommand { get; }
    public ICommand CloseBookCommand { get; }

    private void Select(string? id)
    {
        _selectedId = string.IsNullOrEmpty(id) || id == _selectedId ? null : id;
        Publish();
    }

    private void Publish()
    {
        foreach (var b in _books) b.IsSelected = b.Id == _selectedId;
        SelectedBook = _books.FirstOrDefault(b => b.IsSelected);
        if (SelectedBook is null) _selectedId = null;
        if (!DeskBooks.SequenceEqual(_books))
        {
            Replace(DeskBooks, _books);
            Replace(Shelves, Enumerable.Range(0, ShelfCount)
                .Select(row => new ShelfViewModel(_books.Skip(row * BooksPerShelf).Take(BooksPerShelf).ToList())));
        }
    }

    /// <summary>The timers that are running right now (shared with the Timers page).</summary>
    public ObservableCollection<RunningTimerViewModel> RunningTimers { get; private set; } = [];
    public bool HasRunningTimers => RunningTimers.Count > 0;

    public void AttachTimers(TimersViewModel timers)
    {
        RunningTimers = timers.RunningTimers;
        RunningTimers.CollectionChanged += (_, _) => Raise(nameof(HasRunningTimers));
        Raise(nameof(RunningTimers));
        Raise(nameof(HasRunningTimers));
    }

    public int TotalOpen { get => _totalOpen; private set => Set(ref _totalOpen, value); }
    public int AssignedToMe { get => _assignedToMe; private set => Set(ref _assignedToMe, value); }
    public int NeedsMyApproval { get => _needsMyApproval; private set => Set(ref _needsMyApproval, value); }

    /// <summary>No repositories are being monitored yet.</summary>
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public bool HasActivity { get => _hasActivity; private set => Set(ref _hasActivity, value); }

    public ObservableCollection<RepositoryRowViewModel> Repositories { get; } = [];
    /// <summary>Every PR on my desk, oldest first: listed in full beside the shelf, in a scrollable list.</summary>
    public ObservableCollection<DeskBookViewModel> DeskBooks { get; } = [];
    /// <summary>The first books, arranged on the shelves of the bookcase (the list holds all of them).</summary>
    public ObservableCollection<ShelfViewModel> Shelves { get; } = [];
    public string DeskSummary { get => _deskSummary; private set => Set(ref _deskSummary, value); }
    public string OldestAssignedOnDesk { get => _oldestAssignedOnDesk; private set => Set(ref _oldestAssignedOnDesk, value); }
    public string OldestReviewOnDesk { get => _oldestReviewOnDesk; private set => Set(ref _oldestReviewOnDesk, value); }
    public int DeskOverflow { get => _deskOverflow; private set { if (Set(ref _deskOverflow, value)) Raise(nameof(HasOverflow)); } }
    public bool HasOverflow => _deskOverflow > 0;
    public bool DeskIsEmpty { get => _deskIsEmpty; private set => Set(ref _deskIsEmpty, value); }
    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];

    public ICommand OpenUrlCommand { get; }

    /// <summary>Opens a pull request in the in-app detail screen (set by the shell); the browser is only a fallback.</summary>
    public ICommand OpenPullRequestCommand { get; }
    public Action<string>? PullRequestOpener { get; set; }
    public ICommand OpenSettingsCommand { get; }

    public override void Apply(MonitorState state)
    {
        TotalOpen = state.Summary.TotalOpen;
        AssignedToMe = state.Summary.AssignedToMe;
        NeedsMyApproval = state.Summary.NeedsMyApproval;
        IsEmpty = state.RepositoryCount == 0;

        var rows = state.Open
            .GroupBy(p => p.Id).Select(g => g.First())
            .GroupBy(p => p.Repository.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Open: g.Count(), Mine: g.Count(p => p.IsOnViewersDesk),
                Assigned: g.Count(p => p.IsAssignedToViewer), Review: g.Count(p => !p.IsAssignedToViewer && p.NeedsViewerApproval)))
            .OrderByDescending(r => r.Assigned).ThenByDescending(r => r.Review).ThenByDescending(r => r.Open)
            .ToList();
        var max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Open));
        Replace(Repositories, rows.Select(r => new RepositoryRowViewModel(r.Name, r.Open, r.Mine, (double)r.Open / max)));

        var now = _time.GetUtcNow();
        var desk = state.Open.GroupBy(p => p.Id).Select(g => g.First())
            .Where(p => p.IsOnViewersDesk)
            .OrderBy(p => p.ViewerRank).ThenBy(p => p.ArrivedAt ?? p.CreatedAt)
            .ToList();
        DeskIsEmpty = desk.Count == 0;
        DeskOverflow = Math.Max(0, desk.Count - BooksPerShelf * ShelfCount);
        _books = desk.Select((p, i) =>
        {
            // A bigger change makes a taller, wider book; the tilt is deterministic so redraws do not shuffle the shelf.
            return new DeskBookViewModel(
                p.Title, p.Repository.Name, $"#{p.Number}", PullRequestStatus.ColorOf(p),
                BookHeightFor(p.Additions, p.Deletions, p.ChangedFiles),
                (i % 5 - 2) * 1.2, p.IsAssignedToViewer, p.NeedsViewerApproval, WaitText(p, now), p.Url, PullRequestStatus.TagsOf(p), p.Author,
                p.Id, BookWidthFor(p.Additions, p.Deletions, p.ChangedFiles), p.Additions, p.Deletions, p.ChangedFiles, p.CommitCount, p.Summary);
        }).ToList();
        Publish();
        DeskSummary = desk.Count == 0
            ? "Your desk is clear."
            : $"{desk.Count} on your desk · {desk.Count(p => p.NeedsViewerApproval)} to approve · {desk.Count(p => p.IsReapproval)} back for re-approval";
        var oldestAssigned = desk.Where(p => p.IsAssignedToViewer).OrderBy(p => p.AssignedToViewerAt ?? p.CreatedAt).FirstOrDefault();
        var oldestReview = desk.Where(p => p.NeedsViewerApproval).OrderBy(p => p.ReviewRequestedFromViewerAt ?? p.CreatedAt).FirstOrDefault();
        OldestAssignedOnDesk = oldestAssigned is null ? string.Empty
            : $"Oldest Assigned {Humanize.Age(now - (oldestAssigned.AssignedToViewerAt ?? oldestAssigned.CreatedAt))}";
        OldestReviewOnDesk = oldestReview is null ? string.Empty
            : $"Oldest Review {Humanize.Age(now - (oldestReview.ReviewRequestedFromViewerAt ?? oldestReview.CreatedAt))}";

        Replace(Activity, state.RecentActivity.Take(12).Select(n => new ActivityItemViewModel(
            n.Title, n.Message, Humanize.Ago(now - n.At), ToneOf(n.Kind), n.PullRequest.Url)));
        HasActivity = Activity.Count > 0;
    }

    /// <summary>Both waits: how long it has been assigned to me and how long my approval has been awaited.</summary>
    private static string WaitText(GitDash.Domain.PullRequests.PullRequest p, DateTimeOffset now)
    {
        var parts = new List<string>(2);
        if (p.IsAssignedToViewer) parts.Add($"Assigned {Humanize.Age(now - (p.AssignedToViewerAt ?? p.CreatedAt))}");
        if (p.NeedsViewerApproval) parts.Add($"Review {Humanize.Age(now - (p.ReviewRequestedFromViewerAt ?? p.CreatedAt))}");
        return string.Join(" · ", parts);
    }

    internal static string ToneOf(NotificationKind kind) => kind switch
    {
        NotificationKind.AssignedToMe => "Assigned",
        NotificationKind.ReviewRequested => "Review",
        NotificationKind.NewComment => "Comment",
        NotificationKind.UnassignedFromMe => "Removed",
        _ => "Merged",
    };

    /// <summary>Records compare by value, so unchanged rows are left alone and the list does not flicker.</summary>
    public const double MinBookHeight = 30, MaxBookHeight = 82, MinBookWidth = 20, MaxBookWidth = 36;

    /// <summary>
    /// How big a PR is, 0 (tiny) to 1 (about 3,000 weighted lines): lines changed plus a weight per file, log-scaled so
    /// one huge PR does not flatten the rest, then eased so small and big PRs stand clearly apart.
    /// </summary>
    public static double SizeScale(int additions, int deletions, int files)
    {
        var size = Math.Max(0, additions) + Math.Max(0, deletions) + Math.Max(0, files) * 20.0;
        var scale = Math.Min(1.0, Math.Log10(1 + size) / Math.Log10(3001));
        return Math.Pow(scale, 1.6);
    }

    /// <summary>Book height from PR size, between <see cref="MinBookHeight"/> and <see cref="MaxBookHeight"/>.</summary>
    public static double BookHeightFor(int additions, int deletions, int files) =>
        MinBookHeight + (MaxBookHeight - MinBookHeight) * SizeScale(additions, deletions, files);

    /// <summary>Book width from PR size (thicker book for a bigger change).</summary>
    public static double BookWidthFor(int additions, int deletions, int files) =>
        MinBookWidth + (MaxBookWidth - MinBookWidth) * SizeScale(additions, deletions, files);

    internal static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var fresh = items.ToList();
        if (target.SequenceEqual(fresh)) return;
        target.Clear();
        foreach (var item in fresh) target.Add(item);
    }
}
