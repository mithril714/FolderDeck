using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderDeck.Core
{
    public sealed class RemovalGroup
    {
        public string Parent { get; set; }
        public List<CompareEntry> Entries { get; set; }
        public override string ToString() { return Entries.Count + " 件  —  " + Parent; }
    }
    public static class ManualRemoval
    {
        public static List<RemovalGroup> Groups(IEnumerable<CompareEntry> entries)
        {
            return entries.Where(e => e.Selected && e.CanDelete)
                .GroupBy(e => Path.GetDirectoryName(PathGuard.Normalize(e.DestinationPath)), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Key.Length).Select(g => new RemovalGroup { Parent = g.Key, Entries = g.ToList() }).ToList();
        }
        public static List<string> Validate(RemovalGroup group, CancellationToken token, out List<string> omitted)
        {
            var paths = new List<string>(); omitted = new List<string>();
            PathGuard.ValidatePlans(group.Entries.Select(e => e.Job).GroupBy(j => j.Id).Select(g => g.First()).ToList());
            foreach (var entry in group.Entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!entry.Selected || !entry.CanDelete) continue;
                    string path = PathGuard.Normalize(entry.DestinationPath);
                    if (!string.Equals(Path.GetDirectoryName(path), group.Parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("親フォルダが一致しません。");
                    BackupService.VerifyRecycleEntry(entry, token);
                    paths.Add(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                { omitted.Add(entry.DisplayPath + ": " + ex.Message); }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
