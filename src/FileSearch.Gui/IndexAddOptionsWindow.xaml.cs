using System.Windows;

namespace FileSearch.Gui;

public partial class IndexAddOptionsWindow : Window
{
    public IndexAddOptionsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

