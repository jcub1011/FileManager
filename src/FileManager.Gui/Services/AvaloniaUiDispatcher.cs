using Avalonia.Threading;

namespace FileManager.Gui.Services;

/// <summary>The production <see cref="IUiDispatcher"/>: posts onto Avalonia's UI <see cref="Dispatcher"/>.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc/>
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
