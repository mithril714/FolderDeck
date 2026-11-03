using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace FolderDeck
{
    public enum BackupMode { Normal, FolderTree, FolderNamesOnly }
    public enum ComparisonMethod { Metadata, Contents }
    public enum EntryState { SourceOnly, Changed, DestinationOnly, Same, Conflict, Error, Skipped }

    [DataContract]
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Notify([CallerMemberName] string name = null)
        { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; Notify(name); return true;
        }
    }

    [DataContract]
    public sealed class JobDefinition : Observable
    {
        private string _name = "新しいジョブ", _source = "", _destination = "", _lastRun = "未実行";
        private bool _enabled = true, _recursive = true, _forceCopy;
        private bool _mirrorDestination = true;
        private BackupMode _mode;
        private ComparisonMethod _compareMethod = ComparisonMethod.Metadata;
        [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public string Name { get { return _name; } set { Set(ref _name, value); } }
        [DataMember] public string Source { get { return _source; } set { Set(ref _source, value); } }
        [DataMember] public string Destination { get { return _destination; } set { Set(ref _destination, value); } }
        [DataMember] public bool Enabled { get { return _enabled; } set { Set(ref _enabled, value); } }
        [DataMember] public bool IncludeSubfolders { get { return _recursive; } set { Set(ref _recursive, value); } }
        [DataMember(EmitDefaultValue = false)] public bool ForceCopy { get { return _forceCopy; } set { if (Set(ref _forceCopy, value)) Notify("ModeLabel"); } }
        [DataMember] public bool MirrorDestination { get { return _mirrorDestination; } set { if (Set(ref _mirrorDestination, value)) Notify("ModeLabel"); } }
        [DataMember] public ComparisonMethod CompareMethod { get { return _compareMethod; } set { Set(ref _compareMethod, value); } }
        [DataMember] public BackupMode Mode { get { return _mode; } set { if (Set(ref _mode, value)) Notify("ModeLabel"); } }
        [DataMember] public string LastRun { get { return _lastRun; } set { Set(ref _lastRun, value); } }
        public string ModeLabel
        {
            get { return Mode == BackupMode.Normal ? (MirrorDestination ? "ミラー · " : "バックアップ · ") + (ForceCopy ? "強制" : "差分") : Mode == BackupMode.FolderTree ? "フォルダ構成のみ" : "フォルダ名のみ"; }
        }
        [OnDeserializing]
        private void ReadLegacyDefaults(StreamingContext context) { _mirrorDestination = false; _compareMethod = ComparisonMethod.Contents; }
        public JobPlan Snapshot()
        { return new JobPlan { Id = Id, Name = Name, Source = Source, Destination = Destination, Mode = Mode, Recursive = IncludeSubfolders, ForceCopy = ForceCopy, MirrorDestination = MirrorDestination, CompareMethod = CompareMethod }; }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public int CopyParallelism { get; set; } = 2;
        [DataMember(EmitDefaultValue = false)] public string LastSelectedJobId { get; set; }
        [DataMember(EmitDefaultValue = false)] public string LastComparisonKey { get; set; }
        [DataMember(EmitDefaultValue = false)] public double LastComparisonSeconds { get; set; }
        [DataMember] public List<JobDefinition> Jobs { get; set; } = new List<JobDefinition>();
        [DataMember] public double Width { get; set; } = 1240;
        [DataMember] public double Height { get; set; } = 820;
    }

    public sealed class JobPlan
    {
        public string Id, Name, Source, Destination;
        public BackupMode Mode;
        public bool Recursive;
        public bool ForceCopy;
        public bool MirrorDestination;
        public ComparisonMethod CompareMethod;
        public string ComparisonLabel { get { return Mode != BackupMode.Normal ? "フォルダのみ" : CompareMethod == ComparisonMethod.Metadata ? "高速（サイズ・更新日時）" : "厳密（ファイル内容）"; } }
        public bool IsMirrorEnabled { get { return Mode == BackupMode.Normal && MirrorDestination; } }
        public string CopyPolicyLabel
        { get { return Mode == BackupMode.Normal ? (ForceCopy ? "すべて強制コピー" : "差分だけコピー") + (IsMirrorEnabled ? " ＋ 不要項目を手動確認" : "") : Mode == BackupMode.FolderTree ? "フォルダ構成のみ" : "フォルダ名のみ"; } }
    }

    public sealed class FileStamp
    {
        public long Length;
        public DateTime LastWriteUtc;
        public string Hash;
    }

    public sealed class ComparisonStatistics
    {
        public long FilesEnumerated, FilesHashed, BytesHashed, SameEntriesOmitted, NativeDirectories, LargeFetchDirectories;
    }

    public sealed class CompareEntry : Observable
    {
        private bool _selected;
        public JobPlan Job { get; set; }
        public string RelativePath { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public bool IsDirectory { get; set; }
        public EntryState State { get; set; }
        public FileStamp SourceStamp { get; set; }
        public FileStamp DestinationStamp { get; set; }
        public string Message { get; set; }
        public bool DeleteBlocked { get; set; }
        public string ContentCheckError { get; private set; }
        private bool HasContentHashes { get { return SourceStamp != null && DestinationStamp != null && !string.IsNullOrEmpty(SourceStamp.Hash) && !string.IsNullOrEmpty(DestinationStamp.Hash); } }
        public bool IsTimestampOnly { get { return !IsDirectory && HasContentHashes && SourceStamp.Length == DestinationStamp.Length && SourceStamp.Hash == DestinationStamp.Hash && SourceStamp.LastWriteUtc != DestinationStamp.LastWriteUtc; } }
        public bool NeedsContentVerification { get { return State == EntryState.Changed && !IsDirectory && SourceStamp != null && DestinationStamp != null && SourceStamp.Length == DestinationStamp.Length && !HasContentHashes; } }
        public string DifferenceDetail
        {
            get
            {
                if (!string.IsNullOrEmpty(ContentCheckError)) return "内容確認不可: " + ContentCheckError;
                if (IsDirectory || SourceStamp == null || DestinationStamp == null || (State != EntryState.Changed && State != EntryState.Same)) return "";
                if (SourceStamp.Length != DestinationStamp.Length) return "ファイルサイズが異なるため、内容も異なります。";
                if (HasContentHashes)
                {
                    if (SourceStamp.Hash != DestinationStamp.Hash) return "ファイル内容が異なります（SHA-256照合）。";
                    return IsTimestampOnly ? "内容は同一で、更新日時だけが異なります（SHA-256照合）。コピー元: " + SourceStamp.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss.fffffff") + " / 保存先: " + DestinationStamp.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss.fffffff") : "ファイル内容は同一です（SHA-256照合）。";
                }
                return State == EntryState.Changed ? "サイズは同じで更新日時が異なります。内容は未確認です。［中身を確認］で変更候補の内容を照合できます。" : "サイズ・更新日時は同一です。内容は未確認です。同じサイズ・日時の内容変更は厳密比較で確認してください。";
            }
        }
        public void ApplyContentCheck(FileStamp source, FileStamp destination, string error)
        {
            ContentCheckError = error;
            if (error == null) { SourceStamp = source; DestinationStamp = destination; }
            Notify("StateLabel"); Notify("StateColor"); Notify("DifferenceDetail"); Notify("IsTimestampOnly"); Notify("NeedsContentVerification");
        }
        public void HoldDeletion(string reason)
        {
            DeleteBlocked = true; Message = reason; Selected = false;
            Notify("StateLabel"); Notify("StateColor"); Notify("CanApply"); Notify("CanDelete"); Notify("ActionLabel"); Notify("Message");
        }
        public bool IsForcedCopy { get { return State == EntryState.Same && !IsDirectory && Job != null && Job.Mode == BackupMode.Normal && Job.ForceCopy; } }
        public bool CanBackup { get { return State == EntryState.SourceOnly || (State == EntryState.Changed && !IsDirectory) || IsForcedCopy; } }
        public bool CanDelete { get { return State == EntryState.DestinationOnly && !DeleteBlocked && Job != null && Job.IsMirrorEnabled && !string.IsNullOrEmpty(RelativePath); } }
        public bool CanApply { get { return CanBackup || CanDelete; } }
        public string ActionLabel { get { return CanDelete ? (Selected ? "手動で確認" : "手動で確認（未選択）") : CanBackup ? (Selected ? "コピー・作成" : "コピー・作成（未選択）") : "対象外"; } }
        public bool Selected { get { return _selected; } set { Set(ref _selected, value && CanApply); } }
        public string JobName { get { return Job.Name; } }
        public string DisplayPath { get { return string.IsNullOrEmpty(RelativePath) ? "（ルートフォルダ）" : RelativePath; } }
        public string KindLabel { get { return IsDirectory ? "フォルダ" : "ファイル"; } }
        public string SourceSize { get { return SourceStamp == null ? "—" : FormatSize(SourceStamp.Length); } }
        public string DestinationSize { get { return DestinationStamp == null ? "—" : FormatSize(DestinationStamp.Length); } }
        public string SourceTime { get { return SourceStamp == null ? "—" : SourceStamp.LastWriteUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"); } }
        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case EntryState.SourceOnly: return "新規";
                    case EntryState.Changed: return !string.IsNullOrEmpty(ContentCheckError) ? "確認不可" : IsTimestampOnly ? "日時のみ" : NeedsContentVerification ? "内容未確認" : "内容変更";
                    case EntryState.DestinationOnly: return DeleteBlocked ? "削除保留" : CanDelete ? "不要（手動）" : "コピー先のみ";
                    case EntryState.Same: return IsTimestampOnly ? (IsForcedCopy ? "日時のみ（強制）" : "日時のみ") : IsForcedCopy ? "同一（強制）" : "同一";
                    case EntryState.Conflict: return "種類の競合";
                    case EntryState.Skipped: return "除外";
                    default: return "エラー";
                }
            }
        }
        public string StateColor
        {
            get
            {
                switch (State)
                {
                    case EntryState.SourceOnly: return "#7DE6C4";
                    case EntryState.Changed: return !string.IsNullOrEmpty(ContentCheckError) ? "#FFACB4" : IsTimestampOnly ? "#A9C8FF" : "#FFD398";
                    case EntryState.DestinationOnly: return CanDelete || DeleteBlocked ? "#FFACB4" : "#A9C8FF";
                    case EntryState.Same: return IsForcedCopy ? "#FFD398" : IsTimestampOnly ? "#A9C8FF" : "#B5C4D8";
                    case EntryState.Skipped: return "#C5BCEF";
                    default: return "#FFACB4";
                }
            }
        }
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576d).ToString("0.0") + " MB";
            return (bytes / 1073741824d).ToString("0.00") + " GB";
        }
    }

    public sealed class WorkProgress
    {
        public string Message { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public double CompletedWork { get; set; }
        public double TotalWork { get; set; }
        public bool IsDetail { get; set; }
    }

    public sealed class RecycleHold
    {
        public CompareEntry Entry;
        public string Reason;
    }

    public sealed class BackupResult
    {
        public int Files, Folders, Errors;
        public double CopySeconds, DeleteSeconds;
        public int DeletedFiles, DeletedFolders, SkippedDeletes;
        public List<RecycleHold> Holds = new List<RecycleHold>();
        public bool Cancelled;
        public List<string> Messages = new List<string>();
    }

    public sealed class LogEntry
    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Text { get; set; }
        public string Color { get; set; } = "#D0D9E5";
    }
}
