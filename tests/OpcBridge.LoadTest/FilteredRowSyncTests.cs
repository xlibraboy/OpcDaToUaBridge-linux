using System.Collections.ObjectModel;
using System.Collections.Specialized;
using OpcBridge.Hmi.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Covers the tag browser's visible-row sync. Rows must be added and removed one at a time:
/// rebuilding the list (or replacing the row objects) makes the list control recreate every
/// container it holds, which drops the hover and the selection the operator is on.
/// </summary>
public sealed class FilteredRowSyncTests
{
    private static ObservableCollection<string> Rows(params string[] rows) => new(rows);

    private static List<NotifyCollectionChangedAction> Track(ObservableCollection<string> target)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    [Fact]
    public void Apply_RowsUnchanged_ChangesNothing()
    {
        var source = new[] { "a", "b", "c" };
        var target = Rows("a", "b", "c");
        List<NotifyCollectionChangedAction> actions = Track(target);

        FilteredRowSync.Apply(source, target, _ => true);

        Assert.Empty(actions);
        Assert.Equal(source, target);
    }

    [Fact]
    public void Apply_RowBecomesHidden_RemovesThatRowOnly()
    {
        var source = new[] { "a", "b", "c" };
        var target = Rows("a", "b", "c");
        List<NotifyCollectionChangedAction> actions = Track(target);

        FilteredRowSync.Apply(source, target, row => row != "b");

        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, actions);
        Assert.Equal(new[] { "a", "c" }, target);
    }

    [Fact]
    public void Apply_RowBecomesVisible_InsertsItInSourceOrder()
    {
        var source = new[] { "a", "b", "c" };
        var target = Rows("a", "c");
        List<NotifyCollectionChangedAction> actions = Track(target);

        FilteredRowSync.Apply(source, target, _ => true);

        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, actions);
        Assert.Equal(source, target);
    }

    [Fact]
    public void Apply_KeptRows_AreTheSameInstances()
    {
        var first = new object();
        var hidden = new object();
        var last = new object();
        var source = new[] { first, hidden, last };
        var target = new ObservableCollection<object>(source);

        FilteredRowSync.Apply(source, target, row => !ReferenceEquals(row, hidden));

        Assert.Same(first, target[0]);
        Assert.Same(last, target[1]);
    }

    [Fact]
    public void Apply_RowLeavesTheSource_IsDroppedFromTheList()
    {
        var source = new[] { "a", "c" };
        var target = Rows("a", "b", "c");

        FilteredRowSync.Apply(source, target, _ => true);

        Assert.Equal(source, target);
    }

    [Fact]
    public void Apply_NothingVisible_EmptiesTheList()
    {
        var source = new[] { "a", "b" };
        var target = Rows("a", "b");

        FilteredRowSync.Apply(source, target, _ => false);

        Assert.Empty(target);
    }
}
