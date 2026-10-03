using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using FileSearch.Gui.ViewModels;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace FileSearch.Gui.Controls;

/// <summary>
/// The search results list. UI Automation sees the cards currently on
/// screen as one flat list, each card a single element named after the
/// file, instead of a peer for every result (and group) plus a tree of
/// every text run and button inside each card.
/// </summary>
/// <remarks>
/// Whenever any UI Automation client is attached (screen readers, but also
/// pen and touch services, screen-capture tools, and assistive overlays),
/// WPF re-synchronizes the automation tree after every layout pass and
/// raises cross-process property events for each changed peer. The stock
/// list peer creates a peer per data item, so a streaming search with
/// thousands of results spent most of the UI thread there. Exposing only
/// realized cards keeps that work proportional to what is on screen; the
/// folder is part of each card's name, so nothing is lost by flattening
/// the folder groups.
/// </remarks>
public sealed class ResultsListBox : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new ResultCardItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is ResultCardItem;

    protected override AutomationPeer OnCreateAutomationPeer() => new ResultsListBoxAutomationPeer(this);

    private sealed class ResultsListBoxAutomationPeer(ResultsListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var children = new List<AutomationPeer>();
            CollectRealizedCards(owner, children);
            return children.Count == 0 ? null : children;
        }

        private static void CollectRealizedCards(DependencyObject parent, List<AutomationPeer> children)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ResultCardItem card)
                {
                    if (card.IsVisible && UIElementAutomationPeer.CreatePeerForElement(card) is { } peer)
                        children.Add(peer);
                    continue;
                }

                CollectRealizedCards(child, children);
            }
        }
    }
}

public sealed class ResultCardItem : ListBoxItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ResultCardAutomationPeer(this);

    private sealed class ResultCardAutomationPeer(ResultCardItem owner) : ListBoxItemWrapperAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override string GetNameCore() =>
            owner.DataContext is FileResultViewModel file
                ? $"{file.DisplayName}, {file.MatchCountText}, {file.DisplayDirectory}"
                : base.GetNameCore();
    }
}
