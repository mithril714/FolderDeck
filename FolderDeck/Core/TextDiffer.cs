using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FolderDeck.Core
{
    public sealed class TextDocument
    {
        public string[] Lines = new string[0];
        public bool FinalNewline;
        public string EncodingName = "（なし）";
        public string NewlineName = "—";
    }
    public sealed class DiffLine
    {
        public char Kind;
        public int OldNumber, NewNumber;
        public string Text { get; set; }
        public string OldLabel { get { return OldNumber == 0 ? "" : OldNumber.ToString(); } }
        public string NewLabel { get { return NewNumber == 0 ? "" : NewNumber.ToString(); } }
        public string Prefix { get { return Kind == ' ' ? " " : Kind.ToString(); } }
        public string Background { get { return Kind == '+' ? "#254D42" : Kind == '-' ? "#573B46" : "#334355"; } }
        public string Foreground { get { return Kind == '+' ? "#B3F0D6" : Kind == '-' ? "#FFD1D9" : "#E0E8F2"; } }
    }
    public sealed class TextDiffResult
    {
        public bool IsText;
        public string Note;
        public TextDocument Old = new TextDocument(), New = new TextDocument();
        public List<DiffLine> Lines = new List<DiffLine>();
        public int Added { get { return Lines.Count(l => l.Kind == '+'); } }
        public int Removed { get { return Lines.Count(l => l.Kind == '-'); } }
        public string Metadata { get { return "コピー先: " + Old.EncodingName + " / " + Old.NewlineName + "   →   コピー元: " + New.EncodingName + " / " + New.NewlineName; } }
    }
    public static class TextDiffer
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        public const int MaxLines = 20000;
        public static TextDiffResult ForEntry(CompareEntry entry, CancellationToken token)
        {
            if (entry.DeleteBlocked || entry.State == EntryState.Error || entry.State == EntryState.Skipped || entry.State == EntryState.Conflict)
                return new TextDiffResult { Note = entry.Message ?? "この項目は比較できません。" };
            if (entry.IsDirectory) return new TextDiffResult { Note = entry.Message ?? "フォルダが選択されています。ファイルを選ぶと内容の差分を表示します。" };
            TextDocument oldDoc, newDoc; string reason;
            if (!Read(entry.DestinationPath, entry.DestinationStamp, token, out oldDoc, out reason) ||
                !Read(entry.SourcePath, entry.SourceStamp, token, out newDoc, out reason))
                return new TextDiffResult { Note = reason };
            var result = Build(oldDoc, newDoc, token);
            if (entry.State == EntryState.Changed && result.Added == 0 && result.Removed == 0)
                result.Note = entry.Job.CompareMethod == ComparisonMethod.Metadata ? "行の内容は同一です。更新日時・文字コード・改行などの違いを確認してください。" : "行の内容は同一です。文字コード、BOM、改行コードなどのバイト差があります。";
            return result;
        }
        private static bool Read(string path, FileStamp stamp, CancellationToken token, out TextDocument document, out string reason)
        {
            document = new TextDocument(); reason = null;
            token.ThrowIfCancellationRequested();
            if (stamp == null)
            {
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("比較後にファイルが作成されています。再比較してください。");
                return true;
            }
            if (stamp.Length > MaxBytes) { reason = "内容diffの表示上限は1ファイル2 MBです。比較一覧の対象を確認してください。"; return false; }
            if (!CompareService.Matches(path, stamp, token)) throw new IOException("比較後にファイルが変化しました。再比較してください。");
            byte[] bytes;
            PathGuard.NoReparseAncestors(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxBytes) throw new IOException("ファイルサイズが変化しました。再比較してください。");
                bytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < bytes.Length)
                { token.ThrowIfCancellationRequested(); int read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            }
            if (!string.IsNullOrEmpty(stamp.Hash)) using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") != stamp.Hash)
                    throw new IOException("内容diffを読む前にファイルが変化しました。再比較してください。");
            }
            if (string.IsNullOrEmpty(stamp.Hash) && !CompareService.Matches(path, stamp, token)) throw new IOException("内容diffの読み込み中にファイルが変化しました。再比較してください。");
            string text, encoding;
            if (!TryDecode(bytes, out text, out encoding)) { reason = "バイナリファイルのためテキストの差分は表示できません。"; return false; }
            document = FromString(text, encoding);
            if (document.Lines.Length > MaxLines) { reason = "内容diffの表示上限は20,000行です。比較一覧の対象を確認してください。"; return false; }
            return true;
        }
        private static bool TryDecode(byte[] bytes, out string text, out string name)
        {
            text = null; name = "";
            try
            {
                if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
                { text = new UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4); name = "UTF-32 LE BOM"; }
                else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                { text = new UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4); name = "UTF-32 BE BOM"; }
                else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                { text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2); name = "UTF-16 LE BOM"; }
                else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                { text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2); name = "UTF-16 BE BOM"; }
                else
                {
                    int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    try { text = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start); name = start == 3 ? "UTF-8 BOM" : "UTF-8"; }
                    catch (DecoderFallbackException)
                    { text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); name = "Shift_JIS (CP932)"; }
                }
                if (text.Any(c => c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f'))) return false;
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        public static TextDocument FromString(string text, string encoding = "UTF-8")
        {
            bool final = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
            var lines = text.Length == 0 ? new string[0] : text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (final) Array.Resize(ref lines, lines.Length - 1);
            bool crlf = text.Contains("\r\n"); string rest = text.Replace("\r\n", "");
            bool lf = rest.Contains("\n"), cr = rest.Contains("\r");
            string newline = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1 ? "混在" : crlf ? "CRLF" : lf ? "LF" : cr ? "CR" : "改行なし";
            return new TextDocument { Lines = lines, FinalNewline = final, EncodingName = encoding, NewlineName = newline };
        }
        public static TextDiffResult Build(TextDocument oldDoc, TextDocument newDoc, CancellationToken token)
        {
            var result = new TextDiffResult { IsText = true, Old = oldDoc, New = newDoc };
            string[] a = oldDoc.Lines, b = newDoc.Lines;
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - suffix - 1] == b[b.Length - suffix - 1]) suffix++;
            int oldNo = 0, newNo = 0;
            Action<char, string> add = (kind, text) => result.Lines.Add(new DiffLine { Kind = kind, Text = text,
                OldNumber = kind == '+' ? 0 : ++oldNo, NewNumber = kind == '-' ? 0 : ++newNo });
            for (int i = 0; i < prefix; i++) add(' ', a[i]);
            int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
            if ((long)(n + 1) * (m + 1) <= 4000000L)
            {
                var lcs = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                {
                    token.ThrowIfCancellationRequested();
                    for (int j = m - 1; j >= 0; j--)
                        lcs[i, j] = a[prefix + i] == b[prefix + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
                int x = 0, y = 0;
                while (x < n || y < m)
                {
                    token.ThrowIfCancellationRequested();
                    if (x < n && y < m && a[prefix + x] == b[prefix + y]) { add(' ', a[prefix + x++]); y++; }
                    else if (x < n && (y >= m || lcs[x + 1, y] >= lcs[x, y + 1])) add('-', a[prefix + x++]);
                    else add('+', b[prefix + y++]);
                }
            }
            else
            {
                result.Note = "差分が大きいため、変更範囲をまとめて表示しています（省略なし）。";
                for (int i = 0; i < n; i++) { token.ThrowIfCancellationRequested(); add('-', a[prefix + i]); }
                for (int j = 0; j < m; j++) { token.ThrowIfCancellationRequested(); add('+', b[prefix + j]); }
            }
            for (int i = suffix; i > 0; i--) add(' ', a[a.Length - i]);
            if (oldDoc.FinalNewline != newDoc.FinalNewline && a.Length > 0 && b.Length > 0)
            {
                int index = result.Lines.FindLastIndex(l => l.Kind == ' ' && l.OldNumber == a.Length && l.NewNumber == b.Length);
                if (index >= 0)
                {
                    var line = result.Lines[index]; result.Lines.RemoveAt(index);
                    result.Lines.Insert(index, new DiffLine { Kind = '-', Text = line.Text, OldNumber = a.Length });
                    result.Lines.Insert(index + 1, new DiffLine { Kind = '+', Text = line.Text, NewNumber = b.Length });
                }
            }
            return result;
        }
        public static string Unified(CompareEntry entry, TextDiffResult diff)
        {
            var text = new StringBuilder();
            string name = entry.DisplayPath.Replace('\\', '/').Replace("\r", " ").Replace("\n", " ");
            text.Append("--- destination/").AppendLine(name).Append("+++ source/").AppendLine(name);
            text.AppendFormat("@@ -{0},{1} +{2},{3} @@", diff.Old.Lines.Length == 0 ? 0 : 1, diff.Old.Lines.Length,
                diff.New.Lines.Length == 0 ? 0 : 1, diff.New.Lines.Length).AppendLine();
            foreach (var line in diff.Lines)
            {
                text.Append(line.Kind).AppendLine(line.Text);
                if ((line.Kind != '+' && line.OldNumber == diff.Old.Lines.Length && !diff.Old.FinalNewline) ||
                    (line.Kind != '-' && line.NewNumber == diff.New.Lines.Length && !diff.New.FinalNewline))
                    text.AppendLine("\\ No newline at end of file");
            }
            return text.ToString();
        }
    }
}
