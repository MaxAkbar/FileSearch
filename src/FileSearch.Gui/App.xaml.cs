using System;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows;
using FileSearch.Core;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Forms = System.Windows.Forms;

namespace FileSearch.Gui;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                services.AddSingleton<IFileTypeOptionsStore, JsonFileTypeOptionsStore>();
                services.AddFileSearchCore();
                services.AddSingleton<IFilePreviewService, FilePreviewService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<IFileLauncher, FileLauncher>();
                services.AddSingleton<IShellIntegrationService, ShellIntegrationService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Apply persisted theme before showing the window so there's no flash.
        // MainViewModel loads its own recent-queries/paths from the same
        // store, so we don't need to push them in here.
        var savedTheme = _host.Services.GetRequiredService<ISettingsStore>().Load().Theme;
        _host.Services.GetRequiredService<IThemeService>().SetTheme(savedTheme);

        var window = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var startupFolder = StartupFolderResolver.ResolveFolderPath(e.Args, Directory.Exists);
        if (startupFolder is not null)
            viewModel.SearchPath = startupFolder;

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
        // history are also saved eagerly whenever they change, so this is
        // really just defensive against in-memory changes that never
        // triggered an eager save.
        if (_host is not null)
        {
            try
            {
                var store = _host.Services.GetRequiredService<ISettingsStore>();
                var fileTypeStore = _host.Services.GetRequiredService<IFileTypeOptionsStore>();
                var theme = _host.Services.GetRequiredService<IThemeService>();
                var vm = _host.Services.GetRequiredService<MainViewModel>();

                var settings = store.Load();
                settings.Theme = theme.CurrentTheme;
                settings.RecentQueries = vm.RecentQueries.ToList();
                settings.RecentPaths = vm.RecentPaths.ToList();
                settings.CustomScopes = vm.CustomScopes
                    .Where(scope => !string.IsNullOrWhiteSpace(scope.Name))
                    .Select(scope => new SearchScope
                    {
                        Name = scope.Name.Trim(),
                        FileNamePattern = scope.FileNamePattern?.Trim() ?? string.Empty,
                    })
                    .ToList();
                settings.SkipUnknownFileTypes = vm.SkipUnknownFileTypes;
                settings.UseIndex = vm.UseIndex;
                settings.IndexedLocations = vm.IndexedLocations
                    .Where(location => !string.IsNullOrWhiteSpace(location.Root))
                    .Select(location => new IndexedLocationSettings
                    {
                        Root = location.Root,
                        Recursive = location.Recursive,
                        IncludeHidden = location.IncludeHidden,
                        EnableDocumentExtraction = location.EnableDocumentExtraction,
                        SkipUnknownFileTypes = location.SkipUnknownFileTypes,
                        WatchEnabled = location.WatchEnabled,
                        LastIndexedUtcTicks = location.LastIndexedUtcTicks,
                        FileCount = location.FileCount,
                        LineCount = location.LineCount,
                    })
                    .ToList();
                settings.LastIndexedRoot = string.Empty;
                store.Save(settings);
                fileTypeStore.Save(vm.BuildFileTypeOptions());
                vm.StopBackgroundIndexingAsync().GetAwaiter().GetResult();
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
