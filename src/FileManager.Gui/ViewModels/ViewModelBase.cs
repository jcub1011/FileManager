using CommunityToolkit.Mvvm.ComponentModel;

namespace FileManager.Gui.ViewModels;

/// <summary>
/// Base for all view-models: a CommunityToolkit <see cref="ObservableObject"/> (source-generated
/// <c>INotifyPropertyChanged</c>, no reflection). View-models are plain POCOs with no Avalonia
/// dependency, so every one is unit-testable without a UI thread or a running app.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
