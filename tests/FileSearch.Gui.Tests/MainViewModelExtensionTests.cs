using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class MainViewModelExtensionTests
{
    [Fact]
    public void ParseExtensions_NormalizesSeparatorsDotsAndDuplicates()
    {
        var extensions = SearchViewModel.ParseExtensions("liquid; *.tmpl, .FOO\nfoo  bar");

        Assert.Equal(new[] { ".liquid", ".tmpl", ".foo", ".bar" }, extensions);
    }
}
