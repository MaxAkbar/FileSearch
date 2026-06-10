using System;
using System.Collections.ObjectModel;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Most-recent-first history semantics shared by the query and path
/// dropdowns: case-insensitive dedupe, promote-to-front (refreshing the
/// stored casing), and a capped length.
/// </summary>
internal static class SearchHistory
{
    public static void PromoteToFront(ObservableCollection<string> list, string? value, int maxEntries)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var matchIndex = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (i != matchIndex && string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                if (i < matchIndex)
                    matchIndex--;
            }
        }

        if (matchIndex < 0)
        {
            list.Insert(0, trimmed);
        }
        else
        {
            if (!string.Equals(list[matchIndex], trimmed, StringComparison.Ordinal))
                list[matchIndex] = trimmed;
            if (matchIndex > 0)
                list.Move(matchIndex, 0);
        }

        while (list.Count > maxEntries)
            list.RemoveAt(list.Count - 1);
    }

    public static void Remove(ObservableCollection<string> list, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], trimmed, StringComparison.OrdinalIgnoreCase))
                list.RemoveAt(i);
        }
    }
}
