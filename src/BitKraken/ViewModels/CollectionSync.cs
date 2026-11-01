using System.Collections.ObjectModel;

namespace BitKraken.ViewModels;

/// <summary>
/// Brings an observable collection into line with a desired list using the fewest edits it can.
/// </summary>
/// <remarks>
/// The torrent list used to be cleared and refilled whenever the filter's answer changed. That was fine
/// with one selected torrent, which the view-model put back by hand, but the list now keeps a selection of
/// many, and a <c>Reset</c> drops all of it - on every tick in which a single torrent changed state.
/// Removing and inserting only what changed leaves every untouched item, and its selection, alone.
/// Items are matched by reference; a view-model is the same torrent only if it is the same object.
/// </remarks>
internal static class CollectionSync
{
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            // Order changes are rare (the list sorts by when a torrent was added, which never changes),
            // so a linear search for an item that has to move is cheaper than keeping an index.
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item)) { existing = j; break; }
            }

            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, item);
        }
    }
}
