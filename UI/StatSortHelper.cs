using System.Collections;
using System.ComponentModel;
using System.Windows.Data;

namespace CraftStats;

public enum StatSortMode
{
    ValueDescending,
    NameAscending
}

public static class StatSortHelper
{
    public static void Apply(object itemsSource, StatSortMode mode)
    {
        var view = CollectionViewSource.GetDefaultView(itemsSource);
        view.SortDescriptions.Clear();

        if (view is ListCollectionView listView)
        {
            listView.CustomSort = mode == StatSortMode.ValueDescending
                ? StatEntryValueComparer.Instance
                : StatEntryNameComparer.Instance;
        }
        else
        {
            view.SortDescriptions.Add(new SortDescription(nameof(StatEntry.Name), ListSortDirection.Ascending));
        }
    }

    public static void Refresh(object itemsSource)
        => CollectionViewSource.GetDefaultView(itemsSource).Refresh();

    private sealed class StatEntryValueComparer : IComparer
    {
        public static readonly StatEntryValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is not StatEntry left || y is not StatEntry right)
                return 0;
            var valueCompare = GetSortableValue(right).CompareTo(GetSortableValue(left));
            return valueCompare != 0
                ? valueCompare
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        }

        private static double GetSortableValue(StatEntry entry)
            => entry.Duration > TimeSpan.Zero ? entry.Duration.TotalSeconds : entry.Count;
    }

    private sealed class StatEntryNameComparer : IComparer
    {
        public static readonly StatEntryNameComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is not StatEntry left || y is not StatEntry right)
                return 0;
            return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}
