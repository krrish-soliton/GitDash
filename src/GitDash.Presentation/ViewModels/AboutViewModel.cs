using System.Reflection;
using System.Windows.Input;
using GitDash.Application.Abstractions;
using GitDash.Application.Monitoring;
using GitDash.Presentation.Mvvm;

namespace GitDash.Presentation.ViewModels;

/// <summary>The About page: who made the app, how to reach them, and which version is running.</summary>
public sealed class AboutViewModel : PageViewModel
{
    public const string RepositoryUrl = "https://github.com/krrish-soliton/GitDash";

    public AboutViewModel(IUrlLauncher launcher) : base("About", "IconInfo")
    {
        OpenRepositoryCommand = new RelayCommand(() => launcher.Open(RepositoryUrl));
    }

    public string AppName => "GitDash";
    public string Tagline => "Keep every pull request that needs you in sight: assigned, waiting for review and raised by your team.";

    public string AuthorName => "Gowtham Krishnan";
    public string AuthorInitials => "GK";
    public string Organization => "Solitontech";
    public string Email => "gowtham.krishnan@solitontech.com";

    public string Version { get; } = VersionOf(Assembly.GetEntryAssembly());
    public string VersionLine => $"Version {Version}";
    public string BuiltWith => "Built with .NET and Avalonia";
    public ICommand OpenRepositoryCommand { get; }

    /// <summary>"1.2.3" from an assembly's informational version, without the build metadata after "+".</summary>
    public static string VersionOf(Assembly? assembly)
    {
        var info = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return assembly?.GetName().Version?.ToString(3) ?? "dev";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }

    public override void Apply(MonitorState state)
    {
        // Nothing here depends on the pull request data.
    }
}
