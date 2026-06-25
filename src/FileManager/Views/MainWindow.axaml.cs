using Avalonia.Controls;
using Avalonia.Input;
using FileManager.ViewModels;

namespace FileManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Double-click navigation. The list's SelectedItem is the FileItemViewModel under
    // the cursor; we forward it to the VM's Open command (directories descend, files no-op).
    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected(EntriesList);

    private void OnRootDoubleTapped(object? sender, TappedEventArgs e) => OpenSelected(RootsList);

    private void OpenSelected(ListBox list)
    {
        if (DataContext is MainWindowViewModel vm && list.SelectedItem is FileItemViewModel item)
            vm.OpenCommand.Execute(item);
    }
}
