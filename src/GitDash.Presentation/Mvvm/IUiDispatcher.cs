namespace GitDash.Presentation.Mvvm;

/// <summary>Marshals work onto the UI thread; implemented by the UI framework layer.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
