using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace FolderDeck.Core
{
    public static class ReportExporter
    {
        private static string Html(string value) { return WebUtility.HtmlEncode(value ?? ""); }
        internal static string Csv(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        public static void CsvReport(string path, IEnumerable<CompareEntry> entries, DateTime compared)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("比較日時,ジョブ,コピー方式,比較方法,状態,予定動作,種類,相対パス,コピー元,コピー先,元サイズ,先サイズ,元更新日時,先更新日時,元SHA256,先SHA256,備考");
                foreach (var e in entries)
                {
                    var values = CsvValues(e, compared);
                    writer.WriteLine(string.Join(",", values.Select(Csv)));
                }
            }
        }
        internal static string[] CsvValues(CompareEntry e, DateTime compared)
        { return new[] { compared.ToString("yyyy-MM-dd HH:mm:ss"), e.JobName, e.Job.CopyPolicyLabel, e.Job.ComparisonLabel, e.StateLabel, e.ActionLabel, e.KindLabel, e.DisplayPath,
                        e.SourcePath, e.DestinationPath, e.SourceStamp?.Length.ToString(), e.DestinationStamp?.Length.ToString(),
                        e.SourceStamp?.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), e.DestinationStamp?.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        e.SourceStamp?.Hash, e.DestinationStamp?.Hash, ComparisonNote(e) }; }
        private static string ComparisonNote(CompareEntry e)
        { return string.IsNullOrEmpty(e.DifferenceDetail) ? e.Message : string.IsNullOrEmpty(e.Message) ? e.DifferenceDetail : e.Message + " / " + e.DifferenceDetail; }
        private static StringBuilder Begin(string title, DateTime time)
        {
            var b = new StringBuilder();
            b.Append("<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>");
            b.Append("<title>").Append(Html(title)).Append("</title><style>");
            b.Append("*{box-sizing:border-box}body{margin:0;background:#10151d;color:#dde6f2;font:14px 'Segoe UI','Yu Gothic UI',sans-serif;padding:44px}main{max-width:1440px;margin:auto}header{border-bottom:1px solid #303b49;padding-bottom:24px;margin-bottom:28px}.brand{color:#65d9b3;font-weight:700;letter-spacing:3px;font-size:12px}h1{font-size:30px;letter-spacing:-.7px;margin:12px 0}p{color:#98a8bf;line-height:1.8}.path{overflow-wrap:anywhere}table{width:100%;border-collapse:collapse;background:#161d28}th,td{text-align:left;padding:12px 15px;border-bottom:1px solid #293344;vertical-align:top}th{font-size:12px;color:#8c9db6}td.path{font-family:Consolas,monospace}.tag{font-weight:600;white-space:nowrap}.summary{display:flex;gap:20px;margin:20px 0;flex-wrap:wrap}.stat{padding:16px 24px;background:#192230;border-radius:9px}code{font-family:Consolas,'Yu Gothic UI',monospace;white-space:pre;tab-size:4}.diff{overflow:auto;border:1px solid #293344;border-radius:9px}.diff table{font:13px Consolas,'Yu Gothic UI',monospace}.diff td{padding:3px 10px;border:0}.num{color:#75869d;width:52px;text-align:right;user-select:none}.prefix{width:24px;text-align:center}.add{background:#173c30;color:#a0e8ca}.del{background:#40262f;color:#ffb5c0}.eq{color:#bdc9d9}.note{border-left:3px solid #f1bf76;padding-left:16px}footer{margin-top:24px;color:#8190a4;font-size:12px}@media print{body{background:white;color:#111;padding:16px}table,.stat{background:white}th,p,footer{color:#444}.eq{color:#111}.add{background:#e2f8e8;color:#115526}.del{background:#ffe6eb;color:#9b1231}}" );
            b.Append("</style></head><body><main><header><div class='brand'>FOLDERDECK / REPORT</div><h1>").Append(Html(title)).Append("</h1><p>比較日時: ")
                .Append(time.ToString("yyyy/MM/dd HH:mm:ss")).Append("</p></header>");
            return b;
        }
        public static void HtmlReport(string path, IList<CompareEntry> entries, DateTime compared,
            CancellationToken token = default(CancellationToken), IProgress<WorkProgress> progress = null)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(path);
            string temp = Path.Combine(Path.GetDirectoryName(target), ".folderdeck-report-" + Guid.NewGuid().ToString("N") + ".tmp");
            var changed = entries.Where(e => e.State == EntryState.Changed && !e.IsDirectory).ToList();
            try
            {
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
                {
                    var b = Begin("フォルダ比較レポート（内容差分付き）", compared);
                    b.Append("<style>a{color:#8bd9ff}details{margin:18px 0;border:1px solid #52677e;border-radius:8px;padding:14px}summary{cursor:pointer;font-weight:600;overflow-wrap:anywhere}details:target{border-color:#65d9b3}</style>");
                    b.Append("<div class='summary'><div class='stat'>新規　").Append(entries.Count(e => e.State == EntryState.SourceOnly))
                        .Append("</div><div class='stat'>変更　").Append(changed.Count)
                        .Append("</div><div class='stat'>コピー先のみ　").Append(entries.Count(e => e.State == EntryState.DestinationOnly))
                        .Append("</div><div class='stat'>同一　").Append(entries.Count(e => e.State == EntryState.Same)).Append("</div></div>");
                    b.Append("<p>変更ファイルのパスをクリックすると内容差分へ移動します。各見出しでも開閉できます。方向はコピー先（変更前）→ コピー元（変更後）です。内容は出力時に再読込し、比較後の変更を検出した場合は理由を表示します。コピー前に保存してください。</p>");
                    foreach (var job in entries.Select(e => e.Job).GroupBy(j => j.Id).Select(g => g.First()))
                        b.Append("<p class='path'><b>").Append(Html(job.Name)).Append("</b><br>コピー元: ").Append(Html(job.Source))
                            .Append("<br>コピー先: ").Append(Html(job.Destination)).Append("<br>コピー方式: ").Append(Html(job.CopyPolicyLabel)).Append("<br>比較方法: ").Append(Html(job.ComparisonLabel)).Append("</p>");
                    b.Append("<table><thead><tr><th>状態</th><th>予定動作</th><th>ジョブ / パス</th><th>種類</th><th>元サイズ</th><th>先サイズ</th><th>備考</th></tr></thead><tbody>");
                    writer.Write(b.ToString());
                    int diffIndex = 0;
                    foreach (var e in entries)
                    {
                        token.ThrowIfCancellationRequested(); b.Clear();
                        bool content = e.State == EntryState.Changed && !e.IsDirectory;
                        b.Append("<tr><td class='tag' style='color:").Append(Html(e.StateColor)).Append("'>").Append(Html(e.StateLabel))
                            .Append("</td><td>").Append(Html(e.ActionLabel)).Append("</td><td class='path'>");
                        if (content) b.Append("<a href='#file-diff-").Append(diffIndex++).Append("'>");
                        b.Append(Html(e.JobName)).Append(" / ").Append(Html(e.DisplayPath));
                        if (content) b.Append("</a>");
                        b.Append("</td><td>").Append(Html(e.KindLabel)).Append("</td><td>").Append(Html(e.SourceSize)).Append("</td><td>").Append(Html(e.DestinationSize))
                            .Append("</td><td>").Append(Html(ComparisonNote(e))).Append("</td></tr>");
                        writer.Write(b.ToString());
                    }
                    writer.Write("</tbody></table><h2>変更ファイルの内容差分</h2>");
                    if (changed.Count == 0) writer.Write("<p>表示対象に変更ファイルはありません。</p>");
                    for (int i = 0; i < changed.Count; i++)
                    {
                        token.ThrowIfCancellationRequested(); var entry = changed[i];
                        progress?.Report(new WorkProgress { Message = "内容差分を出力中: " + entry.JobName + " / " + entry.DisplayPath, IsDetail = true, Completed = i, Total = changed.Count });
                        TextDiffResult diff;
                        try { diff = TextDiffer.ForEntry(entry, token); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        { diff = new TextDiffResult { Note = "内容差分を保存できません: " + ex.Message }; }
                        b.Clear(); b.Append("<details id='file-diff-").Append(i).Append("'><summary>").Append(Html(entry.JobName + " / " + entry.DisplayPath)).Append("</summary>");
                        b.Append("<p class='path'>コピー先（変更前）: ").Append(Html(entry.DestinationPath)).Append("<br>コピー元（変更後）: ").Append(Html(entry.SourcePath)).Append("</p>");
                        AppendDiff(b, diff); b.Append("</details>"); writer.Write(b.ToString());
                        progress?.Report(new WorkProgress { Message = "内容差分を出力中…", IsDetail = true, Completed = i + 1, Total = changed.Count });
                    }
                    writer.Write("<footer>高速比較はサイズ・更新日時、厳密比較はファイル内容で判定します。高速比較では同じサイズ・日時の変更を見逃す場合があります。内容差分の対象は、この一覧に含まれる『変更』のファイルです。新規・同一・コピー先のみの項目は一覧のみです。不要項目の削除はエクスプローラーで手動操作します。表示フィルターを反映しています。</footer>");
                    writer.Write("<script>function openDiff(){var id=location.hash.slice(1);if(!/^file-diff-[0-9]+$/.test(id))return;var el=document.getElementById(id);if(el){el.open=true;el.scrollIntoView();}}window.addEventListener('hashchange',openDiff);openDiff();</script></main></body></html>");
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void AppendDiff(StringBuilder b, TextDiffResult diff)
        {
            if (diff.IsText)
            {
                b.Append("<p>").Append(Html(diff.Metadata)).Append("</p>");
                b.Append("<div class='summary'><div class='stat'>追加 +").Append(diff.Added).Append("</div><div class='stat'>削除 −").Append(diff.Removed).Append("</div></div>");
            }
            if (!string.IsNullOrEmpty(diff.Note)) b.Append("<p class='note'>").Append(Html(diff.Note)).Append("</p>");
            if (!diff.IsText) return;
            b.Append("<div class='diff'><table><thead><tr><th>変更前</th><th>変更後</th><th></th><th>内容</th></tr></thead><tbody>");
            foreach (var line in diff.Lines)
            {
                string kind = line.Kind == '+' ? "add" : line.Kind == '-' ? "del" : "eq";
                b.Append("<tr class='").Append(kind).Append("'><td class='num'>").Append(line.OldLabel).Append("</td><td class='num'>").Append(line.NewLabel)
                    .Append("</td><td class='prefix'>").Append(Html(line.Prefix)).Append("</td><td><code>").Append(Html(line.Text)).Append("</code></td></tr>");
            }
            b.Append("</tbody></table></div>");
        }
        public static void DiffHtml(string path, CompareEntry entry, TextDiffResult diff, DateTime compared)
        {
            var b = Begin("ファイル内容の差分", compared);
            b.Append("<h2 class='path'>").Append(Html(entry.JobName + " / " + entry.DisplayPath)).Append("</h2><p class='path'>コピー先（変更前）: ")
                .Append(Html(entry.DestinationPath)).Append("<br>コピー元（変更後）: ").Append(Html(entry.SourcePath)).Append("<br>").Append(Html(diff.Metadata)).Append("</p>");
            b.Append("<div class='summary'><div class='stat'>追加 +").Append(diff.Added).Append("</div><div class='stat'>削除 −").Append(diff.Removed).Append("</div></div>");
            if (!string.IsNullOrEmpty(diff.Note)) b.Append("<p class='note'>").Append(Html(diff.Note)).Append("</p>");
            b.Append("<div class='diff'><table><tbody>");
            foreach (var line in diff.Lines)
            {
                string kind = line.Kind == '+' ? "add" : line.Kind == '-' ? "del" : "eq";
                b.Append("<tr class='").Append(kind).Append("'><td class='num'>").Append(line.OldLabel).Append("</td><td class='num'>").Append(line.NewLabel)
                    .Append("</td><td class='prefix'>").Append(Html(line.Prefix)).Append("</td><td><code>").Append(Html(line.Text)).Append("</code></td></tr>");
            }
            b.Append("</tbody></table></div><footer>比較方向: コピー先 → コピー元。バックアップで削除する項目を示すものではありません。</footer></main></body></html>");
            File.WriteAllText(path, b.ToString(), new UTF8Encoding(true));
        }
    }
}
