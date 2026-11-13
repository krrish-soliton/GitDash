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
    string Waiting, string Url);

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
    private const int BooksPerShelf = 8, ShelfCount = 3;

    public DashboardViewModel(IUrlLauncher launcher, TimeProvider time, Action openSettings)
        : base("Dashboard", "IconDashboard")
    {
        _time = time;
        OpenUrlCommand = new RelayCommand<string>(url => launcher.Open(url));
        OpenSettingsCommand = new RelayCommand(openSettings);
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
            .Select(g => (Name: g.Key, Open: g.Count(), Mine: g.Count(p => p.IsOnViewersDesk)))
            .OrderByDescending(r => r.Open)
            .ToList();
        var max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Open));
        Replace(Repositories, rows.Select(r => new RepositoryRowViewModel(r.Name, r.Open, r.Mine, (double)r.Open / max)));

        var now = _time.GetUtcNow();
        var desk = state.Open.GroupBy(p => p.Id).Select(g => g.First())
            .Where(p => p.IsOnViewersDesk)
            .OrderBy(p => p.ArrivedAt ?? p.CreatedAt)
            .ToList();
        DeskIsEmpty = desk.Count == 0;
        DeskOverflow = Math.Max(0, desk.Count - BooksPerShelf * ShelfCount);
        var books = desk.Select((p, i) =>
        {
            var waiting = now - (p.ArrivedAt ?? p.CreatedAt);
            // Older work makes a taller book; the tilt is deterministic so redraws do not shuffle the shelf.
            var height = 40 + Math.Min(34, waiting.TotalDays * 5) + p.TotalComments % 5 * 2;
            return new DeskBookViewModel(
                p.Title, p.Repository.Name, $"#{p.Number}", Humanize.Accent(p.Repository.FullName), height,
                (i % 5 - 2) * 1.2, p.IsAssignedToViewer, p.NeedsViewerApproval, WaitText(p, now), p.Url);
        }).ToList();
        Replace(DeskBooks, books);
        Replace(Shelves, Enumerable.Range(0, ShelfCount)
            .Select(row => new ShelfViewModel(books.Skip(row * BooksPerShelf).Take(BooksPerShelf).ToList())));
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
    internal static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var fresh = items.ToList();
        if (target.SequenceEqual(fresh)) return;
        target.Clear();
        foreach (var item in fresh) target.Add(item);
    }
}
