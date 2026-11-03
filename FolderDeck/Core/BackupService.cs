using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDeck.Core
{
    public static class BackupService
    {
        public static BackupResult Execute(IEnumerable<CompareEntry> entries, CancellationToken token, IProgress<WorkProgress> progress, int copyParallelism = 1, IRecycleBin recycleBin = null)
        {
            if (copyParallelism < 1 || copyParallelism > 4) throw new ArgumentOutOfRangeException(nameof(copyParallelism));
            recycleBin = recycleBin ?? new WindowsRecycleBin();
            var selected = entries.Where(e => e.Selected && e.CanApply).ToArray();
            var directories = selected.Where(e => !e.CanDelete && e.IsDirectory).OrderBy(e => e.DestinationPath.Length).ToArray();
            var files = selected.Where(e => !e.CanDelete && !e.IsDirectory).ToArray();
            var removals = selected.Where(e => e.CanDelete).OrderBy(e => e.IsDirectory ? 1 : 0).ThenByDescending(e => e.DestinationPath.Length).ToArray();
            var failedCopyJobs = new HashSet<string>(StringComparer.Ordinal);
            var result = new BackupResult();
            var gate = new object();
            var activeWork = new Dictionary<CompareEntry, double>();
            int done = 0;
            double completedWork = 0, totalWork = selected.Sum(WorkUnits);
            var progressClock = Stopwatch.StartNew();
            var copyClock = new Stopwatch(); var deleteClock = new Stopwatch();
            Action<string, bool> report = (message, detail) => {
                // Call only while holding gate, or before/after all copy workers.
                progress?.Report(new WorkProgress { Message = message, IsDetail = detail, Completed = done, Total = selected.Length,
                    CompletedWork = completedWork + activeWork.Values.Sum(), TotalWork = totalWork });
            };
            report("処理の準備・変更確認中…", true);
            Action<CompareEntry> process = entry => {
                token.ThrowIfCancellationRequested();
                bool deleting = entry.CanDelete;
                string message;
                int copiedFiles = 0, createdFolders = 0, deletedFiles = 0, deletedFolders = 0, skipped = 0;
                bool failed = false; string holdReason = null;
                var warnings = new List<string>();
                Action flushWarnings = () => {
                    // Caller holds gate. Retained temporary files must be reported without masking failure/cancellation.
                    result.Warnings += warnings.Count;
                    foreach (var warning in warnings) { result.Messages.Add(warning); report(warning, false); }
                    warnings.Clear();
                };
                try
                {
                    // Deletions run only after every copy worker has completed.
                    if (deleting && failedCopyJobs.Contains(entry.Job.Id))
                    { skipped = 1; message = "削除見送り（コピー失敗）: " + entry.JobName + " / " + entry.DisplayPath; }
                    else if (deleting)
                    {
                        recycleBin.Recycle(entry.DestinationPath, token, () => VerifyRecycleEntry(entry, token));
                        if (entry.IsDirectory) deletedFolders = 1; else deletedFiles = 1;
                        message = "ごみ箱へ移動（保存先）: " + entry.JobName + " / " + entry.DisplayPath;
                    }
                    else
                    {
                        PathGuard.NoReparseAncestors(entry.SourcePath);
                        PathGuard.NoReparseAncestors(entry.DestinationPath);
                        if (entry.IsDirectory)
                        {
                            if (!Directory.Exists(entry.SourcePath)) throw new IOException("コピー元フォルダが変化しました。再比較してください。");
                            if (File.Exists(entry.DestinationPath)) throw new IOException("コピー先にファイルが作成されています。再比較してください。");
                            bool existed = Directory.Exists(entry.DestinationPath);
                            Directory.CreateDirectory(entry.DestinationPath);
                            if (!existed) createdFolders = 1;
                        }
                        else
                        {
                            CopyFile(entry, token, bytes => {
                                if (progress == null) return;
                                lock (gate)
                                {
                                    double size = entry.SourceStamp == null ? 0 : entry.SourceStamp.Length;
                                    activeWork[entry] = WorkUnits(entry) * (size > 0 ? Math.Min(0.95, bytes / size) : 0);
                                    if (progressClock.ElapsedMilliseconds < 250) return;
                                    progressClock.Restart();
                                    report("コピー中: " + entry.JobName + " / " + entry.DisplayPath, true);
                                }
                            }, warnings.Add);
                            copiedFiles = 1;
                        }
                        message = (entry.IsDirectory ? "フォルダ作成: " : entry.Job.ForceCopy ? "強制コピー完了: " : "コピー完了: ") + entry.JobName + " / " + entry.DisplayPath;
                    }
                }
                catch (OperationCanceledException) { lock (gate) flushWarnings(); throw; }
                catch (RecycleUnavailableException ex)
                { skipped = 1; holdReason = ex.Message; message = "削除保留（ごみ箱へ移動不可）: " + entry.JobName + " / " + entry.DisplayPath + " / " + ex.Message; }
                catch (Exception ex) when (IsFileOperationFailure(ex))
                { failed = true; message = "エラー（" + (deleting ? "削除" : "コピー") + "）: " + entry.JobName + " / " + entry.DisplayPath + " / " + ex.Message; }
                lock (gate)
                {
                    flushWarnings();
                    if (failed) { result.Errors++; if (!deleting) failedCopyJobs.Add(entry.Job.Id); }
                    result.Files += copiedFiles; result.Folders += createdFolders;
                    result.DeletedFiles += deletedFiles; result.DeletedFolders += deletedFolders; result.SkippedDeletes += skipped;
                    if (holdReason != null) result.Holds.Add(new RecycleHold { Entry = entry, Reason = holdReason });
                    result.Messages.Add(message);
                    activeWork.Remove(entry); completedWork += WorkUnits(entry); done++;
                    report(message, false);
                }
            };
            try
            {
                PathGuard.ValidatePlans(selected.Select(e => e.Job).GroupBy(j => j.Id).Select(g => g.First()).ToList());
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if (!destinations.Add(PathGuard.Normalize(entry.DestinationPath))) throw new IOException("処理対象の保存先が重複しています。再比較してください。");
                }
                copyClock.Start();
                foreach (var entry in directories) process(entry);
                if (copyParallelism == 1) { foreach (var entry in files) process(entry); }
                else Parallel.ForEach(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = copyParallelism }, process);
                copyClock.Stop();
                token.ThrowIfCancellationRequested();
                deleteClock.Start();
                report("保存先のごみ箱移動対象を確認中…", true);
                if (removals.Length > 0) recycleBin.Run(() => { foreach (var entry in removals) process(entry); });
                deleteClock.Stop();
            }
            catch (OperationCanceledException)
            { result.Cancelled = true; result.Messages.Add("キャンセルしました。完了済みのコピー・ごみ箱移動は残ります。"); }
            finally
            { copyClock.Stop(); deleteClock.Stop(); result.CopySeconds = copyClock.Elapsed.TotalSeconds; result.DeleteSeconds = deleteClock.Elapsed.TotalSeconds; }
            return result;
        }

        private static string[] ValidateDeleteEntry(CompareEntry entry)
        {
            if (!entry.CanDelete || string.IsNullOrEmpty(entry.RelativePath) || Path.IsPathRooted(entry.RelativePath))
                throw new IOException("削除対象の比較情報が不正です。");
            var parts = entry.RelativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => p == "." || p == "..")) throw new IOException("削除対象の相対パスが不正です。");
            if (!entry.Job.Recursive && (entry.IsDirectory || parts.Length != 1)) throw new IOException("下位階層は削除対象外です。");
            string source = PathGuard.Combine(entry.Job.Source, entry.RelativePath);
            string destination = PathGuard.Combine(entry.Job.Destination, entry.RelativePath);
            if (!string.Equals(source, PathGuard.Normalize(entry.SourcePath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(destination, PathGuard.Normalize(entry.DestinationPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(destination, PathGuard.Normalize(entry.Job.Destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("比較時の削除パスが一致しません。");
            return parts;
        }

        private static void VerifyReadableDirectory(string path)
        {
            PathGuard.NoReparseAncestors(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException("コピー元のフォルダが変化しました。");
            // Unlike Directory.Exists, access and connectivity errors must propagate.
            using (var iterator = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) { iterator.MoveNext(); }
        }

        private static void VerifySourceAbsent(CompareEntry entry, string[] parts)
        {
            string parent = PathGuard.Normalize(entry.Job.Source);
            VerifyReadableDirectory(parent);
            for (int i = 0; i < parts.Length; i++)
            {
                string child = Path.Combine(parent, parts[i]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (FileNotFoundException) { VerifyReadableDirectory(parent); return; }
                catch (DirectoryNotFoundException) { VerifyReadableDirectory(parent); return; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("コピー元にリンクが現れました。再比較してください。");
                if (i == parts.Length - 1) throw new IOException("コピー元に項目が現れました。再比較してください。");
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("コピー元の上位フォルダが変化しました。再比較してください。");
                parent = child;
            }
        }

        internal static void VerifyRecycleEntry(CompareEntry entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string[] parts = ValidateDeleteEntry(entry);
            VerifySourceAbsent(entry, parts);
            PathGuard.NoReparseAncestors(entry.DestinationPath);
            FileAttributes attributes = File.GetAttributes(entry.DestinationPath);
            if (((attributes & FileAttributes.Directory) != 0) != entry.IsDirectory)
                throw new IOException("保存先の種類が変化しました。再比較してください。");
            if (entry.IsDirectory)
            {
                if (Directory.GetFileSystemEntries(entry.DestinationPath).Length != 0)
                    throw new IOException("保存先フォルダに未選択・変更・追加の項目が残っているため削除しません。");
                token.ThrowIfCancellationRequested();
                VerifySourceAbsent(entry, parts);
                PathGuard.NoReparseAncestors(entry.DestinationPath);
                token.ThrowIfCancellationRequested();
                // The Shell callback performs the recycle after this validation.
            }
            else
            {
                if (entry.DestinationStamp == null || string.IsNullOrEmpty(entry.DestinationStamp.Hash) || !CompareService.Matches(entry.DestinationPath, entry.DestinationStamp, token))
                    throw new IOException("比較後に保存先が変化しました。再比較してください。");
                token.ThrowIfCancellationRequested();
                VerifySourceAbsent(entry, parts);
                PathGuard.NoReparseAncestors(entry.DestinationPath);
                token.ThrowIfCancellationRequested();
                // No direct deletion of a mirror target is allowed.
            }
        }

        private static double WorkUnits(CompareEntry entry)
        {
            var stamp = entry.CanDelete ? entry.DestinationStamp : entry.SourceStamp;
            return Math.Max(65536d, entry.IsDirectory || stamp == null ? 0d : stamp.Length);
        }
        private static bool IsFileOperationFailure(Exception ex)
        { return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is CryptographicException; }
        private static string FailureDetails(Exception ex)
        { return ex.GetType().FullName + " / HRESULT 0x" + ex.HResult.ToString("X8") + " / " + ex.Message; }
        private static void CopyFile(CompareEntry entry, CancellationToken token, Action<long> onCopied, Action<string> onWarning = null)
        {
            string temp = null, phase = "コピー前の確認";
            bool ownsTemp = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (entry.SourceStamp == null) throw new IOException("比較情報がありません。再比較してください。");
                if (!CompareService.Matches(entry.DestinationPath, entry.DestinationStamp, token))
                    throw new IOException("比較後にコピー先が変化しました。再比較してください。");
                phase = "保存先フォルダの準備";
                string parent = Path.GetDirectoryName(entry.DestinationPath);
                Directory.CreateDirectory(parent);
                PathGuard.NoReparseAncestors(parent);
                temp = Path.Combine(parent, ".folderdeck-" + Guid.NewGuid().ToString("N") + ".tmp");
                phase = "コピー元・一時ファイルを開く";
                using (var source = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
                using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
                {
                    ownsTemp = true;
                    phase = "内容検証の準備";
                    using (var sha = string.IsNullOrEmpty(entry.SourceStamp.Hash) ? null : SHA256.Create())
                    {
                        phase = "コピー元の再確認";
                        if (source.Length != entry.SourceStamp.Length || File.GetLastWriteTimeUtc(entry.SourcePath) != entry.SourceStamp.LastWriteUtc)
                            throw new IOException("比較後にコピー元が変化しました。再比較してください。");
                        byte[] buffer = new byte[131072]; int length; long transferred = 0;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            phase = "コピー元の読み取り";
                            length = source.Read(buffer, 0, buffer.Length); if (length == 0) break;
                            phase = "一時ファイルへの書き込み";
                            destination.Write(buffer, 0, length);
                            phase = "コピー内容の検証";
                            sha?.TransformBlock(buffer, 0, length, buffer, 0);
                            transferred += length; onCopied?.Invoke(transferred);
                        }
                        phase = "コピー元の内容・更新日時の最終確認";
                        sha?.TransformFinalBlock(new byte[0], 0, 0);
                        if ((sha != null && BitConverter.ToString(sha.Hash).Replace("-", "") != entry.SourceStamp.Hash) || source.Length != entry.SourceStamp.Length ||
                            File.GetLastWriteTimeUtc(entry.SourcePath) != entry.SourceStamp.LastWriteUtc)
                            throw new IOException("コピー元の内容が比較時から変化しました。再比較してください。");
                        phase = "一時ファイルの書き込み確定・クローズ";
                        destination.Flush(true);
                    }
                }
                phase = "更新日時の設定";
                File.SetLastWriteTimeUtc(temp, entry.SourceStamp.LastWriteUtc);
                token.ThrowIfCancellationRequested();
                phase = "保存先の再確認";
                PathGuard.NoReparseAncestors(entry.DestinationPath);
                if (!CompareService.Matches(entry.DestinationPath, entry.DestinationStamp, token))
                    throw new IOException("コピー中にコピー先が変化しました。再比較してください。");
                token.ThrowIfCancellationRequested();
                phase = entry.DestinationStamp == null ? "新規ファイルの名前変更" : "既存ファイルの置き換え";
                if (entry.DestinationStamp == null) File.Move(temp, entry.DestinationPath);
                else File.Replace(temp, entry.DestinationPath, null);
                ownsTemp = false;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsFileOperationFailure(ex))
            {
                throw new IOException("コピー失敗【" + phase + "】 / コピー元: " + entry.SourcePath + " / 保存先: " + entry.DestinationPath
                    + " / 一時ファイル: " + (temp ?? "未作成") + " / " + FailureDetails(ex), ex);
            }
            finally
            {
                // Diagnostic mode: leave our temporary file in place on failure/cancellation.
                // Do not delete it here or during a later run. Successful Move/Replace consumes it.
                if (ownsTemp)
                {
                    onWarning?.Invoke("警告（一時ファイルを保持・自動削除しません）: " + temp + " / コピーが完了していないため、内容が不完全な場合があります。");
                }
            }
        }
    }
}
