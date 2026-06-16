using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using FileSearch.Core;
using FileSearch.Core.Logging;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace FileSearch.Gui;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application instances live for the whole process; the tray icon is disposed in OnExit.")]
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new FileLoggerProvider(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileSearch", "logs"),
                "filesearch-gui")))
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IFileTypeOptionsStore, JsonFileTypeOptionsStore>();
                services.AddFileSearchCore();
                services.AddSingleton<IFilePreviewService, FilePreviewService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<IFileLauncher, FileLauncher>();
                services.AddSingleton<IShellIntegrationService, ShellIntegrationService>();
                services.AddSingleton<IFolderPicker, FolderPicker>();
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddSingleton<StatusBarViewModel>();
                services.AddSingleton<ApplicationSettingsViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SearchViewModel>();
                services.AddSingleton<IndexViewModel>();
                services.AddSingleton<WorkflowsViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Apply persisted theme before showing the window so there's no flash.
        // MainViewModel loads its own recent-queries/paths from the same
        // shared settings instance, so we don't need to push them in here.
        var savedTheme = _host.Services.GetRequiredService<ISettingsService>().Current.Theme;
        _host.Services.GetRequiredService<IThemeService>().SetTheme(savedTheme);

        var window = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var startupFolder = StartupFolderResolver.ResolveFolderPath(e.Args, Directory.Exists);
        if (startupFolder is not null)
            viewModel.Search.SearchPath = startupFolder;

        window.DataContext = viewModel;
        window.Show();
        _ = viewModel.StartBackgroundIndexingAsync();

        CreateTrayIcon(window, viewModel);
    }

    private void CreateTrayIcon(MainWindow window, MainViewModel viewModel)
    {
        _trayIconImage = LoadTrayIconImage();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show FileSearch", null, (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Install Windows integration", null, (_, _) => Dispatcher.Invoke(() => viewModel.InstallWindowsIntegrationCommand.Execute(null)));
        menu.Items.Add("Remove Windows integration", null, (_, _) => Dispatcher.Invoke(() => viewModel.RemoveWindowsIntegrationCommand.Execute(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FileSearch",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window));
    }

    private static void ShowMainWindow(Window window)
    {
        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static Icon LoadTrayIconImage()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            using var associatedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (associatedIcon is not null)
                return (Icon)associatedIcon.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();

        // Snapshot current state on the way out as a safety net. Theme and
        // history are saved eagerly whenever they change; the view model's
        // PersistSettings covers anything that never triggered an eager save.
        if (_host is not null)
        {
            try
            {
                var fileTypeStore = _host.Services.GetRequiredService<IFileTypeOptionsStore>();
                var vm = _host.Services.GetRequiredService<MainViewModel>();

                vm.PersistSettings();
                fileTypeStore.Save(vm.BuildFileTypeOptions());

                // Stop indexing off the dispatcher and with a hard bound:
                // blocking the UI thread on a dispatcher-resuming task
                // deadlocked shutdown, and exit must never hang even if a
                // background write is wedged.
                if (!Task.Run(vm.StopBackgroundIndexingAsync).Wait(TimeSpan.FromSeconds(5)))
                {
                    _host.Services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        ?.CreateLogger<App>()
                        .LogWarning("Background indexing did not stop within the exit grace period.");
                }
            }
            catch
            {
                // Settings persistence is a convenience — never block exit.
            }
        }

        _host?.Dispose();
        base.OnExit(e);
    }
}
