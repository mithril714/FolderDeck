using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FolderDeck.Core
{
    public static class CsvDiffExporter
    {
        public static string DiffPath(string listPath)
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(listPath)), Path.GetFileNameWithoutExtension(listPath) + "-内容差分.csv"); }

        public static void Export(string listPath, IList<CompareEntry> entries, DateTime compared,
            CancellationToken token = default(CancellationToken), IProgress<WorkProgress> progress = null)
        {
            token.ThrowIfCancellationRequested();
            string[] targets = { Path.GetFullPath(listPath), DiffPath(listPath) };
            string parent = Path.GetDirectoryName(targets[0]);
            string[] temps = Enumerable.Range(0, 2).Select(i => Path.Combine(parent, ".folderdeck-csv-" + Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
            try
            {
                using (var list = new StreamWriter(temps[0], false, new UTF8Encoding(true)))
                using (var detail = new StreamWriter(temps[1], false, new UTF8Encoding(true)))
                {
                    list.WriteLine("比較日時,ジョブ,コピー方式,比較方法,状態,予定動作,種類,相対パス,コピー元,コピー先,元サイズ,先サイズ,元更新日時,先更新日時,元SHA256,先SHA256,備考,追加行数,削除行数,内容差分の備考");
                    detail.WriteLine("比較日時,ジョブ,状態,相対パス,コピー元,コピー先,区分,変更前行番号,変更後行番号,行内容,備考");
                    for (int i = 0; i < entries.Count; i++)
                    {
                        token.ThrowIfCancellationRequested(); var entry = entries[i];
                        progress?.Report(new WorkProgress { Message = "CSV内容差分を出力中: " + entry.JobName + " / " + entry.DisplayPath, IsDetail = true, Completed = i, Total = entries.Count });
                        string added = "", removed = "", note = "対象外（内容差分は変更ファイルのみ）";
                        if (entry.State == EntryState.Changed && !entry.IsDirectory)
                        {
                            TextDiffResult diff;
                            try { diff = TextDiffer.ForEntry(entry, token); }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                            { diff = new TextDiffResult { Note = "内容差分を保存できません: " + ex.Message }; }
                            note = diff.IsText ? diff.Metadata + (string.IsNullOrEmpty(diff.Note) ? "" : " / " + diff.Note) : diff.Note ?? "内容差分を保存できません。";
                            if (diff.IsText)
                            {
                                added = diff.Added.ToString(); removed = diff.Removed.ToString();
                                foreach (var line in diff.Lines.Where(l => l.Kind == '+' || l.Kind == '-'))
                                {
                                    token.ThrowIfCancellationRequested();
                                    DetailRow(detail, compared, entry, line.Kind == '+' ? "追加" : "削除", line.OldLabel, line.NewLabel, line.Text, note);
                                }
                                if (diff.Added == 0 && diff.Removed == 0) DetailRow(detail, compared, entry, "情報", "", "", "", note);
                            }
                            else DetailRow(detail, compared, entry, "保存不可", "", "", "", note);
                        }
                        WriteRow(list, ReportExporter.CsvValues(entry, compared).Concat(new[] { added, removed, note }));
                        progress?.Report(new WorkProgress { Message = "CSV内容差分を出力中…", IsDetail = true, Completed = i + 1, Total = entries.Count });
                    }
                }
                token.ThrowIfCancellationRequested();
                CommitPair(temps, targets);
            }
            finally { foreach (var temp in temps) DeleteTemporary(temp); }
        }
        private static void DetailRow(TextWriter writer, DateTime compared, CompareEntry e, string kind, string oldLine, string newLine, string content, string note)
        { WriteRow(writer, new[] { compared.ToString("yyyy-MM-dd HH:mm:ss"), e.JobName, e.StateLabel, e.DisplayPath, e.SourcePath, e.DestinationPath, kind, oldLine, newLine, content, note }); }
        private static void WriteRow(TextWriter writer, IEnumerable<string> values)
        { writer.WriteLine(string.Join(",", values.Select(ReportExporter.Csv))); }
        private static void DeleteTemporary(string path)
        { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // Stage both files before touching either destination. Roll back completed writes if a later commit fails.
        // This is not a cross-file transaction against power loss or simultaneous writes by another program.
        private static void CommitPair(string[] temps, string[] targets)
        {
            foreach (var path in targets) if (Directory.Exists(path)) throw new IOException("CSVの保存先がフォルダです: " + path);
            var backups = new string[2]; var installed = new bool[2];
            bool preserveBackups = false;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    if (File.Exists(targets[i]))
                    {
                        backups[i] = Path.Combine(Path.GetDirectoryName(targets[i]), ".folderdeck-csv-previous-" + Guid.NewGuid().ToString("N") + ".bak");
                        File.Replace(temps[i], targets[i], backups[i]);
                    }
                    else File.Move(temps[i], targets[i]);
                    installed[i] = true;
                }
            }
            catch (Exception error)
            {
                var failures = new List<string>();
                for (int i = 1; i >= 0; i--)
                {
                    if (!installed[i]) continue;
                    try
                    {
                        if (backups[i] != null) File.Replace(backups[i], targets[i], null);
                        else File.Delete(targets[i]);
                    }
                    catch (Exception rollback) when (rollback is IOException || rollback is UnauthorizedAccessException || rollback is System.Security.SecurityException)
                    { failures.Add(targets[i] + " / 以前のファイル: " + (backups[i] ?? "なし") + " / " + rollback.Message); }
                }
                if (failures.Count > 0)
                {
                    preserveBackups = true;
                    throw new IOException("CSV保存に失敗し、一部を元に戻せませんでした。退避ファイルを残しています。\n" + string.Join("\n", failures), error);
                }
                throw;
            }
            finally { if (!preserveBackups) foreach (var backup in backups) if (backup != null) DeleteTemporary(backup); }
        }
    }
}
