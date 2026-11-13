using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using GithubDash.Application.Abstractions;
using GithubDash.Application.Accounts;
using GithubDash.Application.Monitoring;
using GithubDash.Application.Settings;
using GithubDash.Presentation.Mvvm;

namespace GithubDash.Presentation.ViewModels;

public sealed record ProfileStatViewModel(string Label, string Value);

/// <summary>A pull request assigned to me, shown at the top of the home page.</summary>
public sealed record AssignedPullRequestViewModel(
    string Title, string Repository, string Number, string Waiting, string Accent, bool IsOverdue, string Url);

/// <summary>"About me": the signed-in GitHub profile plus today's picture of the user's own work.</summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly IUrlLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private GitHubProfile? _profile;
    private Guid _loadedFor;
    private string _name = "Welcome", _login = string.Empty, _bio = string.Empty, _meta = string.Empty;
    private string _since = string.Empty, _message = string.Empty, _greeting = string.Empty, _initials = "?", _accent = "#6C5CE7";
    private string? _avatarUrl, _profileUrl;
    private bool _hasProfile, _isLoading, _hasAccount;
    private string _oldestAssigned = "none", _oldestReview = "none";
    private int _onMyDesk, _raisedByMe, _awaitingMyApproval, _assignedToMe;

    public HomeViewModel(
        SettingsService settings, AccountService accounts, IUrlLauncher launcher, IUiDispatcher ui, TimeProvider time)
        : base("Home", "IconHome")
    {
        _settings = settings;
        _accounts = accounts;
        _launcher = launcher;
        _ui = ui;
        _time = time;
        OpenUrlCommand = new RelayCommand<string>(url => { if (!string.IsNullOrEmpty(url)) _launcher.Open(url); });
        OpenProfileCommand = new RelayCommand(() => { if (ProfileUrl is { } u) _launcher.Open(u); });
        RefreshCommand = new AsyncRelayCommand(LoadAsync, ex => Message = ex.Message);
        _settings.Changed += changed => _ui.Post(() => { if (IsSelected) _ = LoadAsync(); });
    }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Login { get => _login; private set => Set(ref _login, value); }
    public string Bio { get => _bio; private set => Set(ref _bio, value); }
    public string Meta { get => _meta; private set => Set(ref _meta, value); }
    public string Since { get => _since; private set => Set(ref _since, value); }
    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Initials { get => _initials; private set => Set(ref _initials, value); }
    public string Accent { get => _accent; private set => Set(ref _accent, value); }
    public string? AvatarUrl { get => _avatarUrl; private set => Set(ref _avatarUrl, value); }
    public string? ProfileUrl { get => _profileUrl; private set => Set(ref _profileUrl, value); }
    public bool HasProfile { get => _hasProfile; private set => Set(ref _hasProfile, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool HasAccount { get => _hasAccount; private set => Set(ref _hasAccount, value); }
    /// <summary>Pull requests assigned to me, the home page's priority.</summary>
    public int AssignedToMe { get => _assignedToMe; private set { if (Set(ref _assignedToMe, value)) Raise(nameof(HasAssigned)); } }
    public bool HasAssigned => _assignedToMe > 0;
    public string OldestAssigned { get => _oldestAssigned; private set => Set(ref _oldestAssigned, value); }
    public string OldestReview { get => _oldestReview; private set => Set(ref _oldestReview, value); }
    public ObservableCollection<AssignedPullRequestViewModel> Assigned { get; } = [];
    public ICommand OpenUrlCommand { get; }

    public int OnMyDesk { get => _onMyDesk; private set => Set(ref _onMyDesk, value); }
    public int RaisedByMe { get => _raisedByMe; private set => Set(ref _raisedByMe, value); }
    public int AwaitingMyApproval { get => _awaitingMyApproval; private set => Set(ref _awaitingMyApproval, value); }

    public ObservableCollection<ProfileStatViewModel> Stats { get; } = [];
    public ObservableCollection<string> Organizations { get; } = [];
    public bool HasOrganizations => Organizations.Count > 0;

    public ICommand OpenProfileCommand { get; }
    public ICommand RefreshCommand { get; }

    public override void Apply(MonitorState state)
    {
        var mine = state.Open.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        OnMyDesk = mine.Count(p => p.IsOnViewersDesk);
        RaisedByMe = mine.Count(p => p.IsAuthoredByViewer);
        AwaitingMyApproval = mine.Count(p => p.NeedsViewerApproval);
        var now = _time.GetUtcNow();
        var assigned = mine.Where(p => p.IsAssignedToViewer).OrderBy(p => p.AssignedToViewerAt ?? p.CreatedAt).ToList();
        var review = mine.Where(p => p.NeedsViewerApproval).OrderBy(p => p.ReviewRequestedFromViewerAt ?? p.CreatedAt).FirstOrDefault();
        OldestAssigned = assigned.Count == 0 ? "none" : Humanize.Age(now - (assigned[0].AssignedToViewerAt ?? assigned[0].CreatedAt));
        OldestReview = review is null ? "none" : Humanize.Age(now - (review.ReviewRequestedFromViewerAt ?? review.CreatedAt));
        AssignedToMe = assigned.Count;
        DashboardViewModel.Replace(Assigned, assigned.Select(p =>
        {
            var waiting = now - (p.AssignedToViewerAt ?? p.CreatedAt);
            return new AssignedPullRequestViewModel(
                p.Title, p.Repository.FullName, $"#{p.Number}", Humanize.Age(waiting),
                Humanize.Accent(p.Repository.FullName), waiting > TimeSpan.FromDays(2), p.Url);
        }));
        Greeting = GreetingFor(_time.GetLocalNow().Hour);
        if (_loadedFor != FirstAccountId) _ = LoadAsync();
    }

    private Guid FirstAccountId => _settings.Current.Accounts.FirstOrDefault()?.Id ?? Guid.Empty;

    private async Task LoadAsync()
    {
        var accountId = FirstAccountId;
        HasAccount = accountId != Guid.Empty;
        if (!HasAccount)
        {
            _profile = null;
            HasProfile = false;
            Message = "Sign in from Settings to see your GitHub profile here.";
            return;
        }

        IsLoading = true;
        Message = string.Empty;
        try
        {
            var profile = await Task.Run(() => _accounts.GetProfileAsync(accountId, CancellationToken.None));
            _loadedFor = accountId;
            _profile = profile;
            Show(profile);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                                       or System.Text.Json.JsonException or GitHubRateLimitException)
        {
            // Remember the failure so the page does not retry on every sync; "Refresh" tries again.
            _loadedFor = accountId;
            Message = "Could not load your GitHub profile: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(GitHubProfile p)
    {
        var inv = CultureInfo.InvariantCulture;
        Name = string.IsNullOrWhiteSpace(p.Name) ? p.Login : p.Name;
        Login = "@" + p.Login;
        Bio = p.Bio ?? string.Empty;
        Initials = Humanize.Initials(p.Login);
        Accent = Humanize.Accent(p.Login);
        AvatarUrl = p.AvatarUrl;
        ProfileUrl = p.Url;
        Meta = string.Join("  ·  ", new[] { p.Company, p.Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Since = p.JoinedAt is { } joined ? $"On GitHub since {joined.ToString("MMMM yyyy", inv)}" : string.Empty;
        DashboardViewModel.Replace(Stats,
        [
            new("Repositories", p.Repositories.ToString("N0", inv)),
            new("Followers", p.Followers.ToString("N0", inv)),
            new("Following", p.Following.ToString("N0", inv)),
            new("Pull requests", p.TotalPullRequests.ToString("N0", inv)),
            new("Merged", p.MergedPullRequests.ToString("N0", inv)),
            new("Open", p.OpenPullRequests.ToString("N0", inv)),
            new("Contributions this year", p.ContributionsThisYear.ToString("N0", inv)),
        ]);
        DashboardViewModel.Replace(Organizations, p.Organizations);
        Raise(nameof(HasOrganizations));
        HasProfile = true;
    }

    internal static string GreetingFor(int hour) => hour switch
    {
        < 5 => "Burning the midnight oil",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };
}
