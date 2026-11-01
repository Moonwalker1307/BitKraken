using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BitKraken.ViewModels;
using Xunit;

namespace BitKraken.Tests;

public class CollectionSyncTests
{
    private sealed record Item(string Name);

    private static (ObservableCollection<Item> List, List<NotifyCollectionChangedAction> Edits) Watched(params Item[] items)
    {
        var list = new ObservableCollection<Item>(items);
        var edits = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => edits.Add(e.Action);
        return (list, edits);
    }

    [Fact]
    public void A_list_that_already_matches_is_not_touched()
    {
        Item a = new("a"), b = new("b");
        var (list, edits) = Watched(a, b);

        list.SyncTo([a, b]);

        Assert.Empty(edits);
    }

    [Fact]
    public void Items_that_drop_out_are_removed_without_resetting_the_ones_that_stay()
    {
        Item a = new("a"), b = new("b"), c = new("c");
        var (list, edits) = Watched(a, b, c);

        list.SyncTo([a, c]);

        Assert.Equal(new[] { a, c }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, edits);
    }

    [Fact]
    public void New_items_are_inserted_where_they_belong()
    {
        Item a = new("a"), b = new("b"), c = new("c");
        var (list, edits) = Watched(a, c);

        list.SyncTo([b, a, c]);

        Assert.Equal(new[] { b, a, c }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, edits);
    }

    [Fact]
    public void A_reordered_list_ends_up_in_the_new_order_without_a_reset()
    {
        Item a = new("a"), b = new("b"), c = new("c"), d = new("d");
        var (list, edits) = Watched(a, b, c);

        list.SyncTo([c, d, a]);

        Assert.Equal(new[] { c, d, a }, list);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, edits);
    }

    [Fact]
    public void Items_are_matched_by_identity_not_by_value()
    {
        Item first = new("same"), second = new("same");
        var list = new ObservableCollection<Item>([first]);

        list.SyncTo([second]);

        Assert.Same(second, Assert.Single(list));
    }
}
