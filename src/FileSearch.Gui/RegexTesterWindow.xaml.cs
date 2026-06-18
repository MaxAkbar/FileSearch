using System.Windows;

namespace FileSearch.Gui;

public partial class RegexTesterWindow : Window
{
    public RegexTesterWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
