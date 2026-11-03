using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace FolderDeck.Core
{
    public static class CompareService
    {
        private sealed class Node
        {
            public bool Directory, Skipped;
            public string Issue;
            public FileStamp Stamp;
        }
        private sealed class Tracker
        {
            private readonly IProgress<WorkProgress> _progress;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            public readonly ComparisonStatistics Stats;
            public Tracker(IProgress<WorkProgress> progress, ComparisonStatistics stats)
            { _progress = progress; Stats = stats; }
            private bool Due()
            { if (_clock.ElapsedMilliseconds < 250) return false; _clock.Restart(); return true; }
            public void Scanned(string label, long count)
            {
                if (_progress != null && Due()) _progress.Report(new WorkProgress { Message = label + " / ファイル情報 " + count + " 項目" });
            }
            public FileStamp Hash(string path, string label, CancellationToken token)
            {
                long read = 0;
                var stamp = Stamp(path, token, bytes => {
                    Stats.BytesHashed += bytes; read += bytes;
                    if (_progress != null && Due()) _progress.Report(new WorkProgress { Message = label + " / 内容検証中: " + Path.GetFileName(path) + " / " + CompareEntry.FormatSize(read) });
                });
                Stats.FilesHashed++; return stamp;
            }
        }
        public static FileStamp Stamp(string path, CancellationToken token, Action<int> onRead = null)
        {
            token.ThrowIfCancellationRequested();
            PathGuard.NoReparseAncestors(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                long length = stream.Length;
                DateTime time = File.GetLastWriteTimeUtc(path);
                byte[] buffer = new byte[131072]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0); onRead?.Invoke(read);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (stream.Length != length || File.GetLastWriteTimeUtc(path) != time)
                    throw new IOException("比較中にファイルが変化しました。再比較してください: " + path);
                return new FileStamp { Length = length, LastWriteUtc = time, Hash = BitConverter.ToString(sha.Hash).Replace("-", "") };
            }
        }
        public static FileStamp MetadataStamp(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); PathGuard.NoReparseAncestors(path);
            var file = new FileInfo(path); file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("ファイルを読み取れません。", path);
            return new FileStamp { Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
        }
        private static bool SameMetadata(FileStamp a, FileStamp b)
        { return a != null && b != null && a.Length == b.Length && a.LastWriteUtc == b.LastWriteUtc; }
        public static bool Matches(string path, FileStamp expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (expected == null) return !File.Exists(path) && !Directory.Exists(path);
            if (!File.Exists(path)) return false;
            if (!SameMetadata(MetadataStamp(path, token), expected)) return false;
            if (string.IsNullOrEmpty(expected.Hash)) return true;
            var now = Stamp(path, token);
            return SameMetadata(now, expected) && now.Hash == expected.Hash;
        }
        public static List<CompareEntry> Compare(IList<JobPlan> plans, CancellationToken token, IProgress<WorkProgress> progress, ComparisonStatistics statistics = null, bool omitUnchanged = false)
        {
            PathGuard.ValidatePlans(plans);
            var tracker = new Tracker(progress, statistics ?? new ComparisonStatistics());
            var result = new List<CompareEntry>();
            foreach (var job in plans)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new WorkProgress { Message = job.Name + " / " + job.ComparisonLabel + " で比較中…" });
                int jobStart = result.Count;
                try
                {
                    PathGuard.NoReparseAncestors(job.Source); PathGuard.NoReparseAncestors(job.Destination);
                    if (!Directory.Exists(job.Source)) throw new IOException("コピー元フォルダが見つかりません。");
                    var source = Scan(job.Source, true, job, token, tracker);
                    var destination = Scan(job.Destination, false, job, token, tracker);
                    var keys = source.Keys.Concat(destination.Keys.Where(k => !source.ContainsKey(k)));
                    var blocked = new HashSet<string>(source.Concat(destination).Where(p => p.Value.Issue != null && p.Value.Directory).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
                    bool scanFailed = source.Values.Concat(destination.Values).Any(n => n.Issue != null && !n.Skipped);
                    long compared = 0; string label = job.Name + " / 判定中";
                    foreach (string key in keys)
                    {
                        token.ThrowIfCancellationRequested();
                        tracker.Scanned(label, ++compared);
                        Node a, b; source.TryGetValue(key, out a); destination.TryGetValue(key, out b);
                        if (job.Mode != BackupMode.Normal && a == null) continue;
                        if (job.Mode != BackupMode.Normal && a != null && !a.Directory) continue;
                        bool ancestorBlocked = HasBlockedAncestor(blocked, key);
                        if (omitUnchanged && job.CompareMethod == ComparisonMethod.Metadata && a != null && b != null &&
                            a.Issue == null && b.Issue == null && a.Directory == b.Directory && !ancestorBlocked &&
                            (a.Directory || (!job.ForceCopy && SameMetadata(a.Stamp, b.Stamp))))
                        { tracker.Stats.SameEntriesOmitted++; continue; }
                        var entry = new CompareEntry {
                            Job = job, RelativePath = key,
                            SourcePath = PathGuard.Combine(job.Source, key), DestinationPath = PathGuard.Combine(job.Destination, key),
                            IsDirectory = a != null ? a.Directory : b.Directory,
                            SourceStamp = a?.Stamp, DestinationStamp = b?.Stamp
                        };
                        if (a?.Issue != null || b?.Issue != null)
                        {
                            entry.State = (a?.Skipped == true || b?.Skipped == true) ? EntryState.Skipped : EntryState.Error;
                            entry.Message = a?.Issue ?? b?.Issue;
                        }
                        else if (ancestorBlocked)
                        { entry.State = EntryState.Skipped; entry.Message = "上位フォルダを走査できないため判定対象外です。"; }
                        else if (a == null) entry.State = EntryState.DestinationOnly;
                        else if (b == null) entry.State = EntryState.SourceOnly;
                        else if (a.Directory != b.Directory) { entry.State = EntryState.Conflict; entry.Message = "同じパスにファイルとフォルダが混在しています。"; }
                        else if (a.Directory) entry.State = EntryState.Same;
                        else
                        {
                            bool same = job.CompareMethod == ComparisonMethod.Metadata ? SameMetadata(a.Stamp, b.Stamp)
                                : a.Stamp.Length == b.Stamp.Length && a.Stamp.Hash == b.Stamp.Hash;
                            entry.State = same ? EntryState.Same : EntryState.Changed;
                        }
                        // Fast comparison still captures content hashes for files that may be deleted.
                        if (entry.CanDelete && !entry.IsDirectory && !scanFailed && job.CompareMethod == ComparisonMethod.Metadata)
                        {
                            try
                            {
                                var verified = tracker.Hash(entry.DestinationPath, job.Name + " / 削除対象", token);
                                if (!SameMetadata(verified, entry.DestinationStamp)) throw new IOException("比較中に保存先が変化しました。再比較してください。");
                                entry.DestinationStamp = verified;
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                            { entry.State = EntryState.Error; entry.Message = ex.Message; }
                        }
                        if (entry.CanDelete && entry.IsDirectory) entry.Message = "子項目は各行のチェックで選択します。空になった場合だけ削除します。";
                        entry.Selected = entry.CanApply; result.Add(entry);
                    }
                    if (job.IsMirrorEnabled && result.Skip(jobStart).Any(e => e.State == EntryState.Error || e.State == EntryState.Conflict))
                    {
                        string reason = "比較エラー・競合があるため、この設定の削除は実行しません。\n原因（先頭3件まで）:\n"
                            + string.Join("\n", result.Skip(jobStart).Where(e => e.State == EntryState.Error || e.State == EntryState.Conflict)
                                .Take(3).Select(e => e.DisplayPath + ": " + (e.Message ?? e.StateLabel)));
                        foreach (var entry in result.Skip(jobStart).Where(e => e.State == EntryState.DestinationOnly))
                        {
                            entry.DeleteBlocked = true;
                            entry.Message = reason;
                            entry.Selected = false;
                        }
                    }
                    result.Sort(jobStart, result.Count - jobStart, Comparer<CompareEntry>.Create((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    result.Add(new CompareEntry { Job = job, RelativePath = "", SourcePath = job.Source, DestinationPath = job.Destination,
                        IsDirectory = true, State = EntryState.Error, Message = ex.Message });
                }
            }
            return result;
        }
        private static bool HasBlockedAncestor(HashSet<string> blocked, string path)
        {
            if (blocked.Count == 0 || path.Length == 0) return false;
            string parent = Path.GetDirectoryName(path) ?? "";
            while (true)
            {
                if (blocked.Contains(parent)) return true;
                if (parent.Length == 0) return false;
                parent = Path.GetDirectoryName(parent) ?? "";
            }
        }
        private static Dictionary<string, Node> Scan(string root, bool source, JobPlan job, CancellationToken token, Tracker tracker)
        {
            var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root)) return nodes;
            nodes.Add("", new Node { Directory = true });
            if (job.Mode == BackupMode.FolderNamesOnly) return nodes;
            var pending = new Stack<string>(); pending.Push(""); long count = 0;
            string label = job.Name + " / " + (source ? "コピー元" : "コピー先");
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string relative = pending.Pop(); string folder = PathGuard.Combine(root, relative);
                try
                {
                    PathGuard.NoReparseAncestors(folder);
                    // Enumeration supplies cached attributes and timestamps; avoid a separate attributes call for every file.
                    foreach (var child in FileEnumerator.Enumerate(folder, token, tracker.Stats))
                    {
                        token.ThrowIfCancellationRequested();
                        string key = relative.Length == 0 ? child.Name : Path.Combine(relative, child.Name);
                        if (child.Issue != null) { nodes[key] = new Node { Issue = child.Issue, Directory = (child.Attributes & FileAttributes.Directory) != 0 }; continue; }
                        FileAttributes attributes = child.Attributes;
                        bool directory = (attributes & FileAttributes.Directory) != 0;
                        if (directory && job.Mode == BackupMode.Normal && !job.Recursive) continue;
                        var node = new Node { Directory = directory }; nodes[key] = node;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        { node.Issue = "ジャンクション／シンボリックリンクは処理対象外です。"; node.Skipped = true; continue; }
                        if (directory) pending.Push(key);
                        else if (job.Mode == BackupMode.Normal)
                        {
                            tracker.Stats.FilesEnumerated++;
                            try
                            {
                                if (job.CompareMethod == ComparisonMethod.Contents) node.Stamp = tracker.Hash(Path.Combine(folder, child.Name), label, token);
                                else
                                {
                                    node.Stamp = new FileStamp { Length = child.Length, LastWriteUtc = child.LastWriteUtc };
                                }
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                            { node.Issue = ex.Message; }
                        }
                        tracker.Scanned(label, ++count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                { nodes[relative].Issue = "フォルダを読み取れません: " + ex.Message; }
            }
            return nodes;
        }
    }
}
