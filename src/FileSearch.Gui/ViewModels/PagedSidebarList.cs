using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileSearch.Gui.ViewModels;

public sealed class PagedSidebarList<T> : ObservableObject
{
    private readonly IEnumerable _source;
    private readonly Func<T, string, bool> _matches;
    private readonly string _itemLabel;
    private readonly int _pageSize;
    private string _searchText = string.Empty;
    private int _pageIndex;
    private int _filteredItemCount;

    public PagedSidebarList(
        IEnumerable source,
        Func<T, string, bool> matches,
        string itemLabel,
        int pageSize)
    {
        _source = source;
        _matches = matches;
        _itemLabel = itemLabel;
        _pageSize = Math.Max(1, pageSize);
        PreviousPageCommand = new RelayCommand(PreviousPage, CanPreviousPage);
        NextPageCommand = new RelayCommand(NextPage, CanNextPage);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => HasSearchText);

        if (source is INotifyCollectionChanged changed)
            changed.CollectionChanged += (_, _) => Refresh();

        Refresh();
    }

    public ObservableCollection<T> Items { get; } = new();

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;

            _pageIndex = 0;
            Refresh();
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasItems => Items.Count > 0;

    public bool IsPagerVisible => _filteredItemCount > _pageSize;

    public bool IsFooterVisible => IsPagerVisible || HasSearchText || _filteredItemCount == 0;

    public string PageSummaryText
    {
        get
        {
            if (_filteredItemCount == 0)
                return HasSearchText ? "No matches" : $"No {_itemLabel}";

            var start = _pageIndex * _pageSize + 1;
            var end = Math.Min(start + Items.Count - 1, _filteredItemCount);
            return $"{start:n0}-{end:n0} of {_filteredItemCount:n0}";
        }
    }

    public void Refresh()
    {
        var needle = SearchText.Trim();
        var matches = _source
            .OfType<T>()
            .Where(item => string.IsNullOrWhiteSpace(needle) || _matches(item, needle))
            .ToList();

        _filteredItemCount = matches.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredItemCount / (double)_pageSize));
        if (_pageIndex >= pageCount)
            _pageIndex = pageCount - 1;

        Items.Clear();
        foreach (var item in matches.Skip(_pageIndex * _pageSize).Take(_pageSize))
            Items.Add(item);

        NotifyStateChanged();
    }

    private void PreviousPage()
    {
        if (!CanPreviousPage())
            return;

        _pageIndex--;
        Refresh();
    }

    private bool CanPreviousPage() => _pageIndex > 0;

    private void NextPage()
    {
        if (!CanNextPage())
            return;

        _pageIndex++;
        Refresh();
    }

    private bool CanNextPage() => (_pageIndex + 1) * _pageSize < _filteredItemCount;

    private void ClearSearch() => SearchText = string.Empty;

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsPagerVisible));
        OnPropertyChanged(nameof(IsFooterVisible));
        OnPropertyChanged(nameof(PageSummaryText));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ClearSearchCommand.NotifyCanExecuteChanged();
    }
}
