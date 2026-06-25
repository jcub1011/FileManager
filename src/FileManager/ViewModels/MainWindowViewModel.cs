using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Services;

namespace FileManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFileSystemService _fileSystem;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private bool _canGoUp;

    public ObservableCollection<FileItemViewModel> Entries { get; } = [];

    /// <summary>Root shortcuts (drives + home) shown in the side list.</summary>
    public ObservableCollection<FileItemViewModel> Roots { get; } = [];

    // Parameterless ctor for the XAML design-time previewer; uses the real service.
    public MainWindowViewModel() : this(new FileSystemService())
    {
    }

    public MainWindowViewModel(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;

        foreach (var root in _fileSystem.GetRoots())
            Roots.Add(new FileItemViewModel(root));

        Navigate(_fileSystem.GetHomeDirectory());
    }

    /// <summary>Opens a directory entry (descends in); files are ignored for now.</summary>
    [RelayCommand]
    private void Open(FileItemViewModel? item)
    {
        if (item is { IsDirectory: true })
            Navigate(item.FullPath);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        var parent = _fileSystem.GetParent(CurrentPath);
        if (parent is not null)
            Navigate(parent);
    }

    private void Navigate(string path)
    {
        CurrentPath = path;
        CanGoUp = _fileSystem.GetParent(path) is not null;

        Entries.Clear();
        foreach (var entry in _fileSystem.GetEntries(path))
            Entries.Add(new FileItemViewModel(entry));
    }
}
