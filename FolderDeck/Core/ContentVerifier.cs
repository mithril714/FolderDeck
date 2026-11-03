using System;
using System.IO;
using System.Threading;

namespace FolderDeck.Core
{
    public sealed class ContentCheckResult
    {
        public CompareEntry Entry;
        public FileStamp Source, Destination;
        public string Error;
    }
    public static class ContentVerifier
    {
        public static ContentCheckResult Check(CompareEntry entry, CancellationToken token, Action<int> onRead = null)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.NeedsContentVerification) throw new ArgumentException("内容未確認の同サイズ変更候補を指定してください。", nameof(entry));
            var result = new ContentCheckResult { Entry = entry };
            try
            {
                if (!CompareService.Matches(entry.SourcePath, entry.SourceStamp, token) || !CompareService.Matches(entry.DestinationPath, entry.DestinationStamp, token))
                    throw new IOException("比較後にファイルが変化しました。再比較してください。");
                result.Source = CompareService.Stamp(entry.SourcePath, token, onRead);
                result.Destination = CompareService.Stamp(entry.DestinationPath, token, onRead);
                if (!SameMetadata(result.Source, entry.SourceStamp) || !SameMetadata(result.Destination, entry.DestinationStamp) ||
                    !SameMetadata(CompareService.MetadataStamp(entry.SourcePath, token), entry.SourceStamp) ||
                    !SameMetadata(CompareService.MetadataStamp(entry.DestinationPath, token), entry.DestinationStamp))
                    throw new IOException("内容確認中にファイルが変化しました。再比較してください。");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            { result.Error = ex.Message; result.Source = result.Destination = null; }
            return result;
        }
        private static bool SameMetadata(FileStamp a, FileStamp b)
        { return a != null && b != null && a.Length == b.Length && a.LastWriteUtc == b.LastWriteUtc; }
    }
}
