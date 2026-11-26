using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using FileSearch.Core.Logging;

namespace FileSearch.Gui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        VersionText = GetVersionText();
        RuntimeText = RuntimeInformation.FrameworkDescription;
        LocationText = AppContext.BaseDirectory;

        InitializeComponent();
        DataContext = this;
    }

    public string VersionText { get; }

    public string RuntimeText { get; }

    public string LocationText { get; }

    private static string GetVersionText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        return assembly.GetName().Version?.ToString() ?? "Unknown";
    }

    private void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(
                $"FileSearch{Environment.NewLine}" +
                $"Version: {VersionText}{Environment.NewLine}" +
                $"Runtime: {RuntimeText}{Environment.NewLine}" +
                $"Framework: .NET 11 WPF{Environment.NewLine}" +
                $"Location: {LocationText}");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"The application details could not be copied.\n\n{ex.Message}",
                "About FileSearch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCopyCrashClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = CrashDiagnostics.ReportDirectory;
            var latest = Enum.GetValues<CrashProcessRole>()
                .Select(role => new FileInfo(Path.Combine(directory, $"{role}.txt")))
                .Where(file => file.Exists)
                .MaxBy(file => file.LastWriteTimeUtc);
            if (latest is null)
            {
                System.Windows.MessageBox.Show(this, "No local crash summary is available.", "FileSearch");
                return;
            }

            System.Windows.Clipboard.SetText(File.ReadAllText(latest.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            System.Windows.MessageBox.Show(this, "The crash summary could not be copied.", "FileSearch");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
