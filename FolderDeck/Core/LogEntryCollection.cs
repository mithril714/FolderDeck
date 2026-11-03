using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace FolderDeck.Core
{
    public sealed class LogEntryCollection : ObservableCollection<LogEntry>
    {
        public void AppendRange(IEnumerable<LogEntry> entries, int capacity = 3000)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            var all = this.Concat(entries).ToList();
            var keep = all.Skip(Math.Max(0, all.Count - capacity)).ToArray();
            CheckReentrancy();
            Items.Clear();
            foreach (var entry in keep) Items.Add(entry);
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
