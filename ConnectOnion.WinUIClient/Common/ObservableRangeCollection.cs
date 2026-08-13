using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Observable collection with explicit bulk operations. WinUI receives one Reset notification
/// instead of one notification per item, which keeps large transcript restores off the layout
/// hot path while preserving the ordinary ObservableCollection API used by live projections.
/// </summary>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items as IReadOnlyCollection<T> ?? items.ToArray();

        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot) Items.Add(item);
        RaiseReset();
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items as IReadOnlyCollection<T> ?? items.ToArray();
        if (snapshot.Count == 0) return;

        CheckReentrancy();
        var insertionIndex = index;
        foreach (var item in snapshot) Items.Insert(insertionIndex++, item);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
