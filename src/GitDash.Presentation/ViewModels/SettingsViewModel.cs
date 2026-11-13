using System.Collections.ObjectModel;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Accounts;
using GitDash.Application.Monitoring;
using GitDash.Application.Settings;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

public sealed record AccountItemViewModel(
    Guid Id, string Login, string Name, string Method, string Initials, string Accent, string? AvatarUrl = null,
    int RepositoryCount = 0)
{
    public string RepositoriesLabel => RepositoryCount == 1 ? "1 repository watched" : $"{RepositoryCount} repositories watched";
}

/// <summary>One row of the repository picker.</summary>
public sealed class RepositoryChoiceViewModel(
    RepositoryInfo info, bool isSelected, Action<RepositoryChoiceViewModel> onToggled) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Owner => info.Repository.Owner;
    public string Name => info.Repository.Name;
    public string FullName => info.Repository.FullName;
    public string Kind => info.IsOrganization ? "Organization" : "Personal";
    public bool IsPrivate => info.IsPrivate;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) onToggled(this);
        }
    }
}

public sealed class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly NotificationRouter _notifications;
    private readonly IUrlLauncher _launcher;
    private readonly IUiDispatcher _ui;

    /// <summary>AI usage tracker shown in the settings page.</summary>
    public AiToolsViewModel? AiTools { get; init; }

    private List<RepositoryChoiceViewModel> _allRepositories = [];
    private AccountItemViewModel? _selectedAccount;
    private CancellationTokenSource? _deviceFlow;
    private string _tokenInput = string.Empty, _clientId, _authMessage = string.Empty;
    private string _userCode = string.Empty, _verificationUri = string.Empty;
    private string _repositoryFilter = string.Empty, _repositoryMessage = string.Empty, _selectionSummary = string.Empty;
    private bool _isAuthError, _isDeviceFlowPending, _isLoadingRepositories;

    public SettingsViewModel(
        SettingsService settings, AccountService accounts, NotificationRouter notifications,
        IUrlLauncher launcher, IUiDispatcher ui)
        : base("Settings", "IconSettings")
    {
        _settings = settings;
        _accounts = accounts;
        _notifications = notifications;
        _launcher = launcher;
        _ui = ui;
        _clientId = settings.Current.OAuthClientId;

        AddTokenCommand = new AsyncRelayCommand(AddTokenAsync, ShowAuthError);
        SignInCommand = new AsyncRelayCommand(SignInWithBrowserAsync, ShowAuthError);
        CancelSignInCommand = new RelayCommand(() => _deviceFlow?.Cancel());
        OpenVerificationCommand = new RelayCommand(() => _launcher.Open(VerificationUri));
        OpenOAuthAppsCommand = new RelayCommand(() => _launcher.Open(NewOAuthAppUrl));
        RemoveAccountCommand = new RelayCommand<AccountItemViewModel>(a => _ = RemoveAsync(a));
        ReloadRepositoriesCommand = new AsyncRelayCommand(LoadRepositoriesAsync, ex => RepositoryMessage = ex.Message);
        TestNotificationCommand = new RelayCommand(() => _ = _notifications.PublishAsync(new ToastRequest(
            "Test notification", "This is how GitDash will let you know.", ToastTone.Review)));

        _settings.Changed += _ => _ui.Post(() =>
        {
            SyncAccounts();
            foreach (var name in new[]
                     {
                         nameof(IsDark), nameof(IsLight), nameof(FollowsSystemTheme), nameof(AutoCloseAssigned), nameof(AutoCloseReview), nameof(AutoCloseComment),
                         nameof(AutoCloseMerged), nameof(AutoCloseTimer), nameof(AutoCloseRemoved), nameof(ShowToastWithIsland),
                         nameof(WidgetOpacityPercent), nameof(IslandOpacityPercent), nameof(ClickThroughOverlays),
                         nameof(WidgetUnlocked), nameof(MiniWidget), nameof(GlassEffect), nameof(WidgetXPercent), nameof(WidgetYPercent), nameof(MiniXPercent), nameof(MiniYPercent), nameof(ShowTimerWidget), nameof(ShowMultipleTimers), nameof(HideToTrayOnMinimize), nameof(HideToTrayOnClose), nameof(TimeFormatIndex), nameof(ShowDynamicIsland), nameof(ShowFloatingWidget), nameof(ReduceMotion),
                     })
                Raise(name);
        });
        SyncAccounts();
    }

    // ----- Accounts -----
    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public AccountItemViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!Set(ref _selectedAccount, value)) return;
            ReloadRepositoriesCommand.Execute(null);
        }
    }

    /// <summary>Bound to a masked box; cleared the moment it has been handed to the secret store.</summary>
    public string TokenInput { get => _tokenInput; set => Set(ref _tokenInput, value); }
    public string ClientId { get => _clientId; set => Set(ref _clientId, value); }
    public string AuthMessage { get => _authMessage; private set => Set(ref _authMessage, value); }
    public bool IsAuthError { get => _isAuthError; private set => Set(ref _isAuthError, value); }
    public bool IsDeviceFlowPending { get => _isDeviceFlowPending; private set => Set(ref _isDeviceFlowPending, value); }
    public string UserCode { get => _userCode; private set => Set(ref _userCode, value); }
    public string VerificationUri { get => _verificationUri; private set => Set(ref _verificationUri, value); }

    public ICommand AddTokenCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public ICommand OpenVerificationCommand { get; }

    /// <summary>Opens GitHub's "Register a new OAuth application" page.</summary>
    public ICommand OpenOAuthAppsCommand { get; }

    public const string NewOAuthAppUrl = "https://github.com/settings/applications/new";
    public ICommand RemoveAccountCommand { get; }

    // ----- Repository picker -----
    public ObservableCollection<RepositoryChoiceViewModel> Repositories { get; } = [];

    public string RepositoryFilter
    {
        get => _repositoryFilter;
        set
        {
            if (Set(ref _repositoryFilter, value)) ApplyFilter();
        }
    }

    public bool IsLoadingRepositories { get => _isLoadingRepositories; private set => Set(ref _isLoadingRepositories, value); }
    public string RepositoryMessage { get => _repositoryMessage; private set => Set(ref _repositoryMessage, value); }
    public string SelectionSummary { get => _selectionSummary; private set => Set(ref _selectionSummary, value); }
    public AsyncRelayCommand ReloadRepositoriesCommand { get; }

    // ----- Notifications -----
    public bool NotifyAssigned
    {
        get => _settings.Current.Notifications.AssignedToMe;
        set => Save(s => s with { Notifications = s.Notifications with { AssignedToMe = value } });
    }

    public bool NotifyReviewRequested
    {
        get => _settings.Current.Notifications.ReviewRequested;
        set => Save(s => s with { Notifications = s.Notifications with { ReviewRequested = value } });
    }

    public bool NotifyUnassigned
    {
        get => _settings.Current.Notifications.UnassignedFromMe;
        set => Save(s => s with { Notifications = s.Notifications with { UnassignedFromMe = value } });
    }

    public bool NotifyComments
    {
        get => _settings.Current.Notifications.NewComment;
        set => Save(s => s with { Notifications = s.Notifications with { NewComment = value } });
    }

    public bool NotifyMerged
    {
        get => _settings.Current.Notifications.Merged;
        set => Save(s => s with { Notifications = s.Notifications with { Merged = value } });
    }

    public bool UseNativeNotifications
    {
        get => _settings.Current.NotificationStyle == NotificationStyle.Native;
        set => Save(s => s with { NotificationStyle = value ? NotificationStyle.Native : NotificationStyle.Custom });
    }

    public ICommand TestNotificationCommand { get; }

    // ----- General -----
    public decimal? LiveCheckSeconds
    {
        get => _settings.Current.LiveCheckSeconds;
        set => Save(s => s with
        {
            LiveCheckSeconds = (int)Math.Clamp(
                value ?? s.LiveCheckSeconds, AppSettings.MinLiveCheckSeconds, AppSettings.MaxLiveCheckSeconds),
        });
    }

    public bool ReduceMotion
    {
        get => _settings.Current.ReduceMotion;
        set => Save(s => s with { ReduceMotion = value });
    }

    public bool IsDark
    {
        get => _settings.Current.Theme == AppTheme.Dark;
        set { if (value) Save(s => s with { Theme = AppTheme.Dark }); }
    }

    public bool IsLight
    {
        get => _settings.Current.Theme == AppTheme.Light;
        set { if (value) Save(s => s with { Theme = AppTheme.Light }); }
    }

    public bool FollowsSystemTheme
    {
        get => _settings.Current.Theme == AppTheme.System;
        set { if (value) Save(s => s with { Theme = AppTheme.System }); }
    }

    /// <summary>With the island on, also show the corner toast.</summary>
    public bool ShowToastWithIsland
    {
        get => _settings.Current.ShowToastWithIsland;
        set => Save(s => s with { ShowToastWithIsland = value });
    }

    /// <summary>Overlay transparency while the cursor is away, as 20–100 (100 = solid).</summary>
    public double WidgetOpacityPercent
    {
        get => Math.Round(_settings.Current.WidgetOpacity * 100);
        set => Save(s => s with { WidgetOpacity = Math.Clamp(value, 20, 100) / 100 });
    }

    public double IslandOpacityPercent
    {
        get => Math.Round(_settings.Current.IslandOpacity * 100);
        set => Save(s => s with { IslandOpacity = Math.Clamp(value, 20, 100) / 100 });
    }

    /// <summary>Mouse clicks go through the widgets and the island to whatever is underneath.</summary>
    public bool ClickThroughOverlays
    {
        get => _settings.Current.ClickThroughOverlays;
        set => Save(s => s with { ClickThroughOverlays = value });
    }

    // One switch per notification type: on = disappears by itself, off = stays until closed.
    public bool AutoCloseAssigned { get => AutoCloses(ToastTone.Assigned); set => SetAutoClose(ToastTone.Assigned, value); }
    public bool AutoCloseReview { get => AutoCloses(ToastTone.Review); set => SetAutoClose(ToastTone.Review, value); }
    public bool AutoCloseComment { get => AutoCloses(ToastTone.Comment); set => SetAutoClose(ToastTone.Comment, value); }
    public bool AutoCloseMerged { get => AutoCloses(ToastTone.Merged); set => SetAutoClose(ToastTone.Merged, value); }
    public bool AutoCloseRemoved { get => AutoCloses(ToastTone.Removed); set => SetAutoClose(ToastTone.Removed, value); }
    public bool AutoCloseTimer { get => AutoCloses(ToastTone.Timer); set => SetAutoClose(ToastTone.Timer, value); }

    private bool AutoCloses(ToastTone tone) => _settings.Current.AutoCloses(tone);

    private void SetAutoClose(ToastTone tone, bool autoClose) => Save(s => s with
    {
        StickyTones = [.. s.StickyTones.Where(t => t != tone), .. autoClose ? [] : new[] { tone }],
    });

    /// <summary>Unlocked: the floating widgets can be dragged; locked: they are click-through.</summary>
    public bool WidgetUnlocked
    {
        get => !_settings.Current.WidgetLocked;
        set => Save(s => s with { WidgetLocked = !value });
    }

    /// <summary>On: the counter widget is a small circle that expands on hover. Off: it stays as the full card.</summary>
    public bool MiniWidget
    {
        get => _settings.Current.MiniWidget;
        set => Save(s => s with { MiniWidget = value });
    }

    /// <summary>Glass gloss, glowing rim and a sweeping sheen on the island and the widgets.</summary>
    public bool GlassEffect
    {
        get => _settings.Current.GlassEffect;
        set => Save(s => s with { GlassEffect = value });
    }

    public double WidgetXPercent
    {
        get => Math.Round(_settings.Current.WidgetXPercent);
        set => Save(s => s with { WidgetXPercent = Math.Clamp(value, 0, 100) });
    }

    public double WidgetYPercent
    {
        get => Math.Round(_settings.Current.WidgetYPercent);
        set => Save(s => s with { WidgetYPercent = Math.Clamp(value, 0, 100) });
    }

    /// <summary>Where the mini circle sits inside the expanded widget (0–100 %).</summary>
    public double MiniXPercent
    {
        get => Math.Round(_settings.Current.MiniXPercent);
        set => Save(s => s with { MiniXPercent = Math.Clamp(value, 0, 100) });
    }

    public double MiniYPercent
    {
        get => Math.Round(_settings.Current.MiniYPercent);
        set => Save(s => s with { MiniYPercent = Math.Clamp(value, 0, 100) });
    }

    public bool ShowDynamicIsland
    {
        get => _settings.Current.ShowDynamicIsland;
        set => Save(s => s with { ShowDynamicIsland = value });
    }

    public bool ShowTimerWidget
    {
        get => _settings.Current.ShowTimerWidget;
        set => Save(s => s with { ShowTimerWidget = value });
    }

    public bool ShowMultipleTimers
    {
        get => _settings.Current.ShowMultipleTimers;
        set => Save(s => s with { ShowMultipleTimers = value });
    }

    public bool ShowFloatingWidget
    {
        get => _settings.Current.ShowFloatingWidget;
        set => Save(s => s with { ShowFloatingWidget = value });
    }

    public bool HideToTrayOnMinimize
    {
        get => _settings.Current.HideToTrayOnMinimize;
        set => Save(s => s with { HideToTrayOnMinimize = value });
    }

    public bool HideToTrayOnClose
    {
        get => _settings.Current.HideToTrayOnClose;
        set => Save(s => s with { HideToTrayOnClose = value });
    }

    public IReadOnlyList<string> TimeFormatOptions { get; } =
        ["Local time, system format", "Local time, 24-hour", "Local time, 12-hour", "UTC"];

    public int TimeFormatIndex
    {
        get => (int)_settings.Current.TimeFormat;
        set
        {
            if (value < 0 || value >= TimeFormatOptions.Count) return;
            Save(s => s with { TimeFormat = (TimeFormat)value });
        }
    }

    public bool ShowTrayIcon
    {
        get => _settings.Current.ShowTrayIcon;
        set => Save(s => s with { ShowTrayIcon = value });
    }

    public override void Apply(MonitorState state)
    {
        // Settings do not depend on synced data.
    }

    private void Save(Func<AppSettings, AppSettings> change) => _ = _settings.UpdateAsync(change);

    private void SyncAccounts()
    {
        var accounts = _settings.Current.Accounts
            .Select(a => new AccountItemViewModel(
                a.Id, a.Login, a.Name ?? a.Login,
                a.Method == AuthMethod.DeviceFlow ? "Browser sign-in" : "Access token",
                Humanize.Initials(a.Login), Humanize.Accent(a.Login), a.AvatarUrl,
                _settings.Current.Repositories.Count(r => r.AccountId == a.Id)))
            .ToList();
        if (!Accounts.SequenceEqual(accounts))
        {
            var selectedId = _selectedAccount?.Id;
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();
        }

        UpdateSelectionSummary();
    }

    private async Task AddTokenAsync()
    {
        var token = TokenInput;
        TokenInput = string.Empty;
        ShowAuth("Checking the token with GitHub…");
        var account = await Task.Run(() => _accounts.AddWithTokenAsync(token, AuthMethod.PersonalAccessToken));
        ShowAuth($"Signed in as {account.Login}.");
    }

    private async Task SignInWithBrowserAsync()
    {
        await _settings.UpdateAsync(s => s with { OAuthClientId = ClientId.Trim() });
        using var cts = _deviceFlow = new CancellationTokenSource();
        try
        {
            ShowAuth("Contacting GitHub…");
            var prompt = await Task.Run(() => _accounts.BeginDeviceFlowAsync(cts.Token));
            UserCode = prompt.UserCode;
            VerificationUri = prompt.VerificationUri;
            IsDeviceFlowPending = true;
            ShowAuth("Enter the code in your browser to finish signing in.");
            _launcher.Open(prompt.VerificationUri);

            var account = await Task.Run(() => _accounts.CompleteDeviceFlowAsync(prompt, cts.Token));
            ShowAuth($"Signed in as {account.Login}.");
        }
        catch (OperationCanceledException)
        {
            ShowAuth("Sign-in cancelled.");
        }
        finally
        {
            IsDeviceFlowPending = false;
            UserCode = string.Empty;
            _deviceFlow = null;
        }
    }

    private async Task RemoveAsync(AccountItemViewModel account)
    {
        try
        {
            await _accounts.RemoveAsync(account.Id);
            ShowAuth($"Removed {account.Login} and deleted its token.");
        }
        catch (Exception ex)
        {
            ShowAuthError(ex);
        }
    }

    private async Task LoadRepositoriesAsync()
    {
        _allRepositories = [];
        Repositories.Clear();
        RepositoryMessage = string.Empty;
        if (_selectedAccount is not { } account) return;

        IsLoadingRepositories = true;
        try
        {
            var repos = await Task.Run(() => _accounts.ListRepositoriesAsync(account.Id));
            if (_selectedAccount?.Id != account.Id) return; // superseded; the reload below takes over

            var monitored = _settings.Current.Repositories
                .Where(r => r.AccountId == account.Id)
                .Select(r => r.ToRef().FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _allRepositories =
            [
                .. repos
                    .OrderByDescending(r => monitored.Contains(r.Repository.FullName))
                    .ThenBy(r => r.Repository.FullName, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new RepositoryChoiceViewModel(r, monitored.Contains(r.Repository.FullName), OnToggled)),
            ];
            ApplyFilter();
            if (_allRepositories.Count == 0) RepositoryMessage = "This account cannot see any repositories.";
        }
        finally
        {
            IsLoadingRepositories = false;
        }

        // The selection changed while this load was running; load the newly selected account.
        if (_selectedAccount?.Id != account.Id) await LoadRepositoriesAsync();
    }

    private void ApplyFilter()
    {
        var filter = RepositoryFilter.Trim();
        Repositories.Clear();
        foreach (var repo in _allRepositories)
        {
            if (filter.Length == 0 || repo.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Repositories.Add(repo);
            }
        }
    }

    private void OnToggled(RepositoryChoiceViewModel choice)
    {
        if (_selectedAccount is not { } account) return;
        var entry = new MonitoredRepository(account.Id, choice.Owner, choice.Name);
        Save(s => s with
        {
            Repositories = choice.IsSelected
                ? [.. s.Repositories.Where(r => !Same(r, entry)), entry]
                : [.. s.Repositories.Where(r => !Same(r, entry))],
        });
    }

    private static bool Same(MonitoredRepository a, MonitoredRepository b) =>
        a.AccountId == b.AccountId && a.ToRef().Matches(b.ToRef());

    private void UpdateSelectionSummary()
    {
        var count = _settings.Current.Repositories.Count;
        SelectionSummary = count switch
        {
            0 => "No repositories monitored yet",
            1 => "Monitoring 1 repository",
            _ => $"Monitoring {count} repositories",
        };
    }

    private void ShowAuth(string message)
    {
        IsAuthError = false;
        AuthMessage = message;
    }

    private void ShowAuthError(Exception ex)
    {
        IsAuthError = true;
        AuthMessage = ex.Message;
    }
}
