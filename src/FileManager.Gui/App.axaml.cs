using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileManager.Core.FileSystem;
using FileManager.Gui.Ipc;
using FileManager.Gui.Services;
using FileManager.Gui.ViewModels;
using FileManager.Gui.Views;

namespace FileManager.Gui;

/// <summary>
/// The Avalonia application. On framework init it composes the object graph by hand (no reflection-based
/// DI container, matching the codebase's AOT ethos) — the production IPC client over the per-OS
/// transport, the OS notifier, and the Avalonia UI dispatcher — and shows the <see cref="MainWindow"/>
/// bound to a <see cref="MainWindowViewModel"/>.
/// </summary>
public sealed class App : Application
{
    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IServiceClient client = new IpcClient(new OsIpcClientTransport());
            var notifications = new NotificationService();
            IUiDispatcher dispatcher = new AvaloniaUiDispatcher();
            IFileSystemService fileSystem = new FileSystemService();
            var pathPicker = new PathPickerService(fileSystem);
            var folderBrowser = new AvaloniaFolderBrowser();

            var viewModel = new MainWindowViewModel(client, notifications, dispatcher, pathPicker, folderBrowser);
            viewModel.Start();

            var window = new MainWindow { DataContext = viewModel };
            // Give the notifier + folder browser a TopLevel so failure notifications render as visible
            // in-app toasts (§7) and the editor's Browse… opens the OS folder picker (FIX 5).
            notifications.AttachTopLevel(window);
            folderBrowser.AttachTopLevel(window);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => viewModel.Stop();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
