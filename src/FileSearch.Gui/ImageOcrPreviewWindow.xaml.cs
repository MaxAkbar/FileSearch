using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

public partial class ImageOcrPreviewWindow : Window
{
    public ImageOcrPreviewWindow()
    {
        InitializeComponent();
    }

    private ImageOcrPreviewViewModel? Preview => DataContext as ImageOcrPreviewViewModel;

    private void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        var path = Preview?.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnRevealImageClick(object sender, RoutedEventArgs e)
    {
        var path = Preview?.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void OnCopyImagePathClick(object sender, RoutedEventArgs e)
    {
        var path = Preview?.ImagePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            System.Windows.Clipboard.SetText(path);
        }
        catch (Exception)
        {
            // Clipboard can be temporarily owned by another app; let the user retry.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
