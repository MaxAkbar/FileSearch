using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;

namespace FileSearch.Gui.ViewModels;

/// <summary>Builds contiguous groups without changing the displayed ranking.</summary>
internal sealed class ResultCollectionView(System.Collections.IList source) : ListCollectionView(source)
{
    private GroupSnapshot? _groupSnapshot;
    private ReadOnlyObservableCollection<object>? _groups;
    private ReadOnlyObservableCollection<object>? _nativeGroups;
    private bool _refreshing;

    public override ReadOnlyObservableCollection<object>? Groups =>
        base.Groups is null ? null : GetGroupSnapshot();

    private ReadOnlyObservableCollection<object> GetGroupSnapshot()
    {
        _groupSnapshot ??= new GroupSnapshot();
        return _groups ??= new ReadOnlyObservableCollection<object>(_groupSnapshot);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
    {
        // WPF adds native groups one at a time even during Refresh. Publishing
        // those intermediate groups makes the panel recalculate its extent for
        // every addition, particularly after a collapsed group was recycled.
        // Expose the finished group tree with one reset instead.
        if (base.Groups is { } nativeGroups)
        {
            GetGroupSnapshot();
            if (!ReferenceEquals(_nativeGroups, nativeGroups))
            {
                if (_nativeGroups is not null)
                    ((INotifyCollectionChanged)_nativeGroups).CollectionChanged -= OnNativeGroupsChanged;
                _nativeGroups = nativeGroups;
                ((INotifyCollectionChanged)_nativeGroups).CollectionChanged += OnNativeGroupsChanged;
            }
            if (args.Action == NotifyCollectionChangedAction.Reset)
                _groupSnapshot!.ReplaceWith(nativeGroups);
        }
        else if (_groupSnapshot is { Count: > 0 })
        {
            _groupSnapshot.ReplaceWith([]);
        }
        base.OnCollectionChanged(args);
    }

    private void OnNativeGroupsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_refreshing)
            _groupSnapshot!.ApplyChange(args, _nativeGroups!);
    }

    protected override void RefreshOverride()
    {
        IsDataInGroupOrder = false;
        if (GroupDescriptions.Count == 1 && GroupDescriptions[0] is PropertyGroupDescription group &&
            SortDescriptions.Any(sort => sort.PropertyName == nameof(FileResultViewModel.ResultGroupRank)))
        {
            // A grouped view already displays each group contiguously, in the
            // order of its first matching ranked file. Compute that same order
            // before WPF builds groups. Otherwise each new group name causes
            // a linear scan of all earlier groups, even during a full refresh.
            var ranked = new ListCollectionView(SourceCollection.Cast<FileResultViewModel>().ToArray());
            using (ranked.DeferRefresh())
            {
                if (Culture is { } culture)
                    ranked.Culture = culture;
                ranked.Filter = Filter;
                foreach (var sort in SortDescriptions)
                    if (sort.PropertyName != nameof(FileResultViewModel.ResultGroupRank))
                        ranked.SortDescriptions.Add(sort);
            }

            var groupRanks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in ranked.Cast<FileResultViewModel>())
            {
                var key = (string?)group.GroupNameFromItem(file, 0, Culture) ?? string.Empty;
                if (!groupRanks.TryGetValue(key, out var rank))
                {
                    rank = groupRanks.Count;
                    groupRanks.Add(key, rank);
                }
                file.ResultGroupRank = rank;
            }
            IsDataInGroupOrder = true;
        }
        _refreshing = true;
        try { base.RefreshOverride(); }
        finally { _refreshing = false; }
    }

    private sealed class GroupSnapshot : ObservableCollection<object>
    {
        internal void ApplyChange(NotifyCollectionChangedEventArgs args, IEnumerable<object> groups)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    for (int i = 0; i < args.NewItems!.Count; i++)
                        Insert(args.NewStartingIndex + i, args.NewItems[i]!);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    for (int i = 0; i < args.OldItems!.Count; i++)
                        RemoveAt(args.OldStartingIndex);
                    break;
                default:
                    ReplaceWith(groups);
                    break;
            }
        }

        internal void ReplaceWith(IEnumerable<object> groups)
        {
            Items.Clear();
            foreach (var group in groups)
                Items.Add(group);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
