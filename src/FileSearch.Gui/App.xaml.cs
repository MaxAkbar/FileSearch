using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using FileSearch.Core;
using FileSearch.Core.Indexing;
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
    private SingleInstanceCoordinator? _singleInstance;
    private bool _explicitExitRequested;
    private bool _backgroundWorkerHandoffRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startupOptions = AppStartupOptions.Parse(e.Args, Directory.Exists);
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _ = SingleInstanceCoordinator
                .TrySendActivationAsync(startupOptions, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Shutdown();
            return;
        }

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
                services.AddSingleton<IFileOperationService, FileOperationService>();
                services.AddSingleton<IFileSavePicker, FileSavePicker>();
                services.AddSingleton<IShellIntegrationService, ShellIntegrationService>();
                services.AddSingleton<IStartupRegistrationService, StartupRegistrationService>();
                services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
                services.AddSingleton<IBackgroundIndexerProcessService, BackgroundIndexerProcessService>();
                services.AddSingleton<IIndexingSearchCoordinator, BackgroundIndexerSearchCoordinator>();
                services.AddSingleton<IFolderPicker, FolderPicker>();
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddSingleton<StatusBarViewModel>();
                services.AddSingleton<ApplicationSettingsViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SearchViewModel>();
                services.AddSingleton<IndexViewModel>();
                services.AddSingleton<WorkflowsViewModel>();
                services.AddSingleton<QuickSearchViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<QuickSearchWindow>();
            })
            .Build();

        // Apply persisted theme before showing the window so there's no flash.
        // MainViewModel loads its own recent-queries/paths from the same
        // shared settings instance, so we don't need to push them in here.
        var savedSettings = _host.Services.GetRequiredService<ISettingsService>().Current;
        var themeService = _host.Services.GetRequiredService<IThemeService>();
        if (string.IsNullOrWhiteSpace(savedSettings.CustomThemeFileName) ||
            !themeService.TrySetCustomTheme(savedSettings.CustomThemeFileName, out _))
        {
            themeService.SetTheme(savedSettings.Theme);
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        if (startupOptions.StartupFolder is not null)
            viewModel.Search.SearchPath = startupOptions.StartupFolder;

        window.DataContext = viewModel;
        var quickWindow = _host.Services.GetRequiredService<QuickSearchWindow>();
        quickWindow.DataContext = _host.Services.GetRequiredService<QuickSearchViewModel>();
        window.Closing += (_, args) =>
        {
            if (!AppWindowLifetime.ShouldKeepResidentOnMainWindowClose(
                    viewModel.Settings.KeepIndexUpdatedAfterClose,
                    _explicitExitRequested))
                return;

            args.Cancel = true;
            HideMainWindowForTray(window, viewModel);
        };
        window.Closed += (_, _) =>
        {
            if (!_explicitExitRequested)
                RequestExit();
        };
        CreateTrayIcon(window, viewModel, quickWindow);
        RegisterQuickSearchHotkey(quickWindow, savedSettings);
        _singleInstance.StartServer(options => Dispatcher.Invoke(() => ActivateFromOptions(window, viewModel, options)));

        var backgroundIndexer = _host.Services.GetRequiredService<IBackgroundIndexerProcessService>();
        if (startupOptions.StartInBackground && viewModel.Settings.StartBackgroundIndexerAtSignIn)
        {
            _ = StartBackgroundWorkerAndExitAsync(backgroundIndexer);
            return;
        }

        if (!viewModel.Settings.StartBackgroundIndexerAtSignIn)
        {
            try
            {
                _ = backgroundIndexer.ShutdownIfRunningAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        if (AppWindowLifetime.ShouldShowOnStartup(startupOptions))
            window.Show();

        _ = viewModel.StartBackgroundIndexingAsync();
    }

    private void CreateTrayIcon(MainWindow window, MainViewModel viewModel, QuickSearchWindow quickWindow)
    {
        _trayIconImage = LoadTrayIconImage();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Quick Search", null, (_, _) => Dispatcher.Invoke(quickWindow.ShowFromHotkey));
        menu.Items.Add("Show FileSearch", null, (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Install Windows integration", null, (_, _) => Dispatcher.Invoke(() => viewModel.InstallWindowsIntegrationCommand.Execute(null)));
        menu.Items.Add("Remove Windows integration", null, (_, _) => Dispatcher.Invoke(() => viewModel.RemoveWindowsIntegrationCommand.Execute(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(RequestExit));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FileSearch",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => ShowMainWindow(window));
    }

    private void RegisterQuickSearchHotkey(QuickSearchWindow quickWindow, AppSettings settings)
    {
        if (_host is null)
            return;

        var hotkeyService = _host.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.HotkeyPressed += (_, _) => Dispatcher.Invoke(quickWindow.ShowFromHotkey);
        var registered = hotkeyService.Register(settings.QuickSearchHotkey);
        if (!registered)
        {
            _host.Services.GetRequiredService<StatusBarViewModel>().Text =
                $"Quick Search hotkey could not be registered: {hotkeyService.LastError}";
        }
    }

    internal void RequestExit()
    {
        _explicitExitRequested = true;
        Shutdown();
    }

    private async Task StartBackgroundWorkerAndExitAsync(IBackgroundIndexerProcessService backgroundIndexer)
    {
        await backgroundIndexer.EnsureRunningAsync(CancellationToken.None).ConfigureAwait(true);
        _backgroundWorkerHandoffRequested = true;
        RequestExit();
    }

    private static void ActivateFromOptions(
        MainWindow window,
        MainViewModel viewModel,
        AppStartupOptions options)
    {
        if (options.StartupFolder is not null)
            viewModel.Search.SearchPath = options.StartupFolder;

        if (AppWindowLifetime.ShouldShowOnActivation(options))
            ShowMainWindow(window);
    }

    private static void ShowMainWindow(Window window)
    {
        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static void HideMainWindowForTray(Window window, MainViewModel viewModel)
    {
        window.Hide();
        viewModel.Status.Text = "FileSearch is still running in the tray. Quick Search remains available.";
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
        _singleInstance?.Dispose();

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

                if (_explicitExitRequested && !_backgroundWorkerHandoffRequested)
                {
                    _ = Task.Run(() => _host.Services
                            .GetRequiredService<IBackgroundIndexerProcessService>()
                            .ShutdownIfRunningAsync(CancellationToken.None))
                        .Wait(TimeSpan.FromSeconds(3));
                }

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
