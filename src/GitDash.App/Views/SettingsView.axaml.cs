using Avalonia.Controls;
using Avalonia.Interactivity;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnRemoveAccountClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AccountItemViewModel account } &&
            DataContext is SettingsViewModel page)
        {
            page.RemoveAccountCommand.Execute(account);
        }
    }

    private void OnOpenNewTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel page) page.OpenNewTokenCommand.Execute(null);
    }

    private void OnOpenOAuthAppsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel page) page.OpenOAuthAppsCommand.Execute(null);
    }

    private void OnLimitReachedClick(object? sender, RoutedEventArgs e) => ToolCommand(sender, page => page.LimitReachedCommand);

    private void OnClearLimitClick(object? sender, RoutedEventArgs e) => ToolCommand(sender, page => page.ClearLimitCommand);

    private void OnRemoveToolClick(object? sender, RoutedEventArgs e) => ToolCommand(sender, page => page.RemoveCommand);

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string name } && DataContext is SettingsViewModel { AiTools: { } tools })
            tools.PresetCommand.Execute(name);
    }

    private void ToolCommand(object? sender, Func<AiToolsViewModel, System.Windows.Input.ICommand> command)
    {
        if (sender is Control { DataContext: AiToolItemViewModel tool } && DataContext is SettingsViewModel { AiTools: { } tools })
            command(tools).Execute(tool);
    }
}
