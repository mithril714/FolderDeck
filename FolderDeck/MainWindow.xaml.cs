using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FolderDeck.Core;
using Microsoft.Win32;

namespace FolderDeck
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<JobDefinition> _jobs = new ObservableCollection<JobDefinition>();
        private readonly LogEntryCollection _logs = new LogEntryCollection();
        private ObservableCollection<CompareEntry> _entries = new ObservableCollection<CompareEntry>();
        private readonly Dictionary<CompareEntry, TextDiffResult> _diffCache = new Dictionary<CompareEntry, TextDiffResult>();
        private ICollectionView _view;
        private CancellationTokenSource _workCts, _diffCts;
        private DateTime _compared;
        private bool _ready, _busy, _snapshotReady, _afterBackup, _updatingEditor, _closeWhenIdle, _settingsLoadFailed;
        private JobDefinition _editorJob;
        private bool _manualRemovalBlocked;
        private bool _showDetails, _batchSelection, _batchJobEdit;
        private bool _entriesHaveForce, _entriesHaveDifference, _entriesHaveMirror, _showDiffOnly = true;
        private int _selectedCount, _selectedCopies, _selectedDeletes, _selectedForcedSame, _entryJobCount;
        private long _selectedBytes, _sameEntriesOmitted;
        private int _pendingContentChecks;
        private string _searchText = "";
        private readonly Stopwatch _workClock = new Stopwatch();
        private readonly System.Windows.Threading.DispatcherTimer _timeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private double _completedWork, _totalWork, _previousEstimate, _lastComparisonSeconds;
        private string _lastComparisonKey;
        private WorkProgressBuffer _activeProgress;
        private int _copyParallelism = 2;
        private double _currentComparisonSeconds;
        private readonly System.Windows.Threading.DispatcherTimer _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private TextDiffResult _currentDiff;
        private CompareEntry _currentDiffEntry;
        private JobDefinition CurrentJob { get { return JobList.SelectedItem as JobDefinition; } }

        public MainWindow()
        {
            string lastSelectedJobId = null;
            InitializeComponent();
            _timeTimer.Tick += (sender, e) => { DrainProgress(true); UpdateTiming(); };
            _filterTimer.Tick += (sender, e) => { _filterTimer.Stop(); if (_ready && !_busy) RefreshView(); };
            JobList.ItemsSource = _jobs; LogList.ItemsSource = _logs;
            try
            {
                var settings = SettingsStore.Load(SettingsStore.SettingsPath);
                Width = Math.Max(MinWidth, Math.Min(settings.Width, SystemParameters.WorkArea.Width));
                Height = Math.Max(MinHeight, Math.Min(settings.Height, SystemParameters.WorkArea.Height));
                _copyParallelism = settings.CopyParallelism == 1 || settings.CopyParallelism == 4 ? settings.CopyParallelism : 2;
                lastSelectedJobId = settings.LastSelectedJobId;
                _lastComparisonKey = settings.LastComparisonKey; _lastComparisonSeconds = settings.LastComparisonSeconds;
                foreach (var job in settings.Jobs) AttachJob(job);
            }
            catch (Exception ex)
            {
                _settingsLoadFailed = true;
                MessageBox.Show("設定を読み込めませんでした。既存の設定は自動で上書きしません。\n\n" + ex.Message, "FolderDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _ready = true; SetEntries(new List<CompareEntry>());
            if (_jobs.Count == 0) AddJob(BackupMode.Normal);
            else JobList.SelectedItem = _jobs.FirstOrDefault(j => j.Id == lastSelectedJobId) ?? _jobs.FirstOrDefault(j => j.Mode == BackupMode.Normal) ?? _jobs[0];
            AddLog("準備完了。コピー元と保存先を指定してください。");
        }
        private void AttachJob(JobDefinition job) { job.PropertyChanged += JobChanged; _jobs.Add(job); }
        private void JobChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_ready || _batchJobEdit || e.PropertyName == "LastRun" || e.PropertyName == "ModeLabel") return;
            InvalidateComparison();
            if (ReferenceEquals(sender, CurrentJob)) UpdateModeNote();
        }
        private void InvalidateComparison()
        {
            if (!_ready) return;
            bool hadResults = _entries.Count > 0 || _sameEntriesOmitted > 0;
            _snapshotReady = false; _afterBackup = false; _sameEntriesOmitted = 0; CompareTime.Text = "";
            if (hadResults) { SetEntries(new List<CompareEntry>()); StatusText.Text = "設定が変わりました。再比較してください。"; }
            UpdateActions();
        }
        private void SetEntries(IList<CompareEntry> entries)
        {
            _diffCts?.Cancel();
            foreach (var item in _entries) item.PropertyChanged -= EntryChanged;
            _entries = new ObservableCollection<CompareEntry>(entries);
            CacheEntries();
            foreach (var item in _entries) item.PropertyChanged += EntryChanged;
            _diffCache.Clear(); _currentDiff = null; _currentDiffEntry = null;
            CacheFilter();
            _view = CollectionViewSource.GetDefaultView(_entries); _view.Filter = MatchesFilter;
            ResultsGrid.ItemsSource = _view;
            UpdateResultColumns();
            DiffList.ItemsSource = null; DiffEmpty.Text = "比較結果からファイルを選ぶと、内容の差分を表示します。";
            DiffEmpty.Visibility = Visibility.Visible; DiffTitle.Text = "ファイルを選択"; DiffMeta.Text = "比較方向: コピー先 → コピー元";
            DiffSaveButton.IsEnabled = false; RefreshView();
        }
        private void CacheEntries()
        {
            _selectedCount = _selectedCopies = _selectedDeletes = _selectedForcedSame = 0; _selectedBytes = 0; _pendingContentChecks = 0;
            _entriesHaveForce = _entriesHaveDifference = _entriesHaveMirror = false;
            var ids = new HashSet<string>();
            foreach (var item in _entries)
            {
                ids.Add(item.Job.Id);
                if (item.NeedsContentVerification) _pendingContentChecks++;
                _entriesHaveForce |= item.Job.Mode == BackupMode.Normal && item.Job.ForceCopy;
                _entriesHaveDifference |= item.Job.Mode == BackupMode.Normal && !item.Job.ForceCopy;
                _entriesHaveMirror |= item.Job.IsMirrorEnabled;
                if (item.Selected) CountSelection(item, 1);
            }
            _entryJobCount = ids.Count;
        }
        private void CountSelection(CompareEntry item, int delta)
        {
            _selectedCount += delta;
            if (item.CanBackup) { _selectedCopies += delta; if (!item.IsDirectory) _selectedBytes += delta * (item.SourceStamp?.Length ?? 0); }
            if (item.CanDelete) _selectedDeletes += delta;
            if (item.IsForcedCopy) _selectedForcedSame += delta;
        }
        private void EntryChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Selected") return;
            var item = (CompareEntry)sender; CountSelection(item, item.Selected ? 1 : -1);
            if (!_batchSelection) UpdateActions();
        }
        private void CacheFilter()
        { _showDiffOnly = DiffOnlyCheck.IsChecked == true; _searchText = SearchBox.Text.Trim(); }
        private bool MatchesFilter(object value)
        {
            var e = value as CompareEntry; if (e == null) return false;
            if (_showDiffOnly && e.State == EntryState.Same && !e.IsForcedCopy && !e.IsTimestampOnly) return false;
            string search = _searchText;
            return search.Length == 0 || e.DisplayPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || e.JobName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private void RefreshView()
        {
            if (_view == null) return;
            CacheFilter(); _view.Refresh();
            var listView = _view as ListCollectionView; int shown = listView != null ? listView.Count : _view.Cast<CompareEntry>().Count();
            ResultCount.Text = (_entries.Count == 0 ? "" : shown + " 件") + (_sameEntriesOmitted > 0 ? "  / 同一 " + _sameEntriesOmitted + " 件省略" : "");
            EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions();
        }
        private void UpdateActions()
        {
            if (!_ready) return;
            int selected = _selectedCount; long bytes = _selectedBytes;
            bool hasJob = CurrentJob != null;
            Visibility visible = hasJob ? Visibility.Visible : Visibility.Collapsed;
            JobEditor.Visibility = visible;
            ResultsToolbar.Visibility = DetailsButton.Visibility = BackupFooter.Visibility = visible;
            UpdateDetailsView();
            SettingsTab.IsEnabled = SettingsButton.IsEnabled = !_busy;
            int forcedSame = _selectedForcedSame;
            bool forcePlan = _entries.Count > 0 ? _entriesHaveForce
                : hasJob && CurrentJob.Mode == BackupMode.Normal && CurrentJob.ForceCopy;
            bool differencePlan = _entriesHaveDifference;
            bool mirrorPlan = _entries.Count > 0 ? _entriesHaveMirror
                : hasJob && CurrentJob.Mode == BackupMode.Normal && CurrentJob.MirrorDestination;
            int selectedDeletes = _selectedDeletes; int selectedCopies = _selectedCopies;
            DiffOnlyCheck.Content = forcePlan ? "差分・コピー対象" : "差分だけ";
            DiffOnlyCheck.ToolTip = "一覧の表示を絞り込みます。強制コピーする同一ファイルは表示します。";
            SelectionSummary.Text = (mirrorPlan ? "コピー " + selectedCopies + " / 不要（手動）" + selectedDeletes : selected + " 項目を選択") + "  /  " + CompareEntry.FormatSize(bytes)
                + (forcedSame > 0 ? "  （同一も " + forcedSame + " 件）" : "");
            EmptyHeading.Text = _busy ? "処理中です…" : _entries.Count > 0 ? "表示する項目がありません"
                : _snapshotReady ? "差分はありません" : "差分を確認すると、ここに表示されます";
            EmptyNote.Text = _sameEntriesOmitted > 0 && DiffOnlyCheck.IsChecked != true ? "同一ファイルを含める場合は、この状態で再比較してください。" : _entries.Count > 0 ? (string.IsNullOrWhiteSpace(SearchBox.Text) ? "「" + DiffOnlyCheck.Content + "」を外すと、対象外の同一項目も表示されます。" : "検索条件を変えてください。") : "";
            CompareButton.IsEnabled = !_busy && hasJob && !string.IsNullOrWhiteSpace(CurrentJob.Source) && !string.IsNullOrWhiteSpace(CurrentJob.Destination);
            CompareButton.Style = (Style)FindResource(_snapshotReady && selected > 0 ? "QuietButton" : "PrimaryButton");
            CompareButton.Content = _afterBackup ? "1  もう一度確認" : mirrorPlan ? "1  コピー・不要項目を確認" : forcePlan ? "1  コピー対象を確認" : "1  差分を確認";
            bool foldersOnly = _entries.Count > 0 ? !(_entriesHaveForce || _entriesHaveDifference) : hasJob && CurrentJob.Mode != BackupMode.Normal;
            BackupButton.Content = foldersOnly ? "2  フォルダを作成" : mirrorPlan ? "2  コピーを実行" : forcePlan && differencePlan ? "2  コピーを実行"
                : forcePlan ? "2  強制コピーする" : "2  差分をコピー";
            BackupButton.ToolTip = !_snapshotReady ? "先に差分を確認してください" : selected == 0 ? "処理する項目を選択してください" : "一覧で選択した項目を実行";
            BackupButton.IsEnabled = !_busy && _snapshotReady && selectedCopies > 0;
            ExplorerButton.Visibility = mirrorPlan ? Visibility.Visible : Visibility.Collapsed;
            ExplorerButton.IsEnabled = !_busy && !_manualRemovalBlocked && (_snapshotReady || _afterBackup) && selectedDeletes > 0;
            CsvButton.IsEnabled = HtmlButton.IsEnabled = !_busy && _entries.Count > 0;
            VerifyContentButton.IsEnabled = !_busy && _snapshotReady && _pendingContentChecks > 0;
            CompareAllButton.IsEnabled = !_busy && _jobs.Any(j => j.Enabled && (!string.IsNullOrWhiteSpace(j.Source) || !string.IsNullOrWhiteSpace(j.Destination)));
            FastAllButton.IsEnabled = !_busy && _jobs.Count > 0;
            AddButton.IsEnabled = FolderToolsMenu.IsEnabled = BulkButton.IsEnabled = !_busy;
            CopyParallelismMenu.IsEnabled = !_busy;
            CopyParallelismMenu.Header = "コピーの並列数（現在: " + _copyParallelism + "）";
            CopyOneMenu.IsChecked = _copyParallelism == 1; CopyTwoMenu.IsChecked = _copyParallelism == 2; CopyFourMenu.IsChecked = _copyParallelism == 4;
            SettingsMenuButton.IsEnabled = JobList.IsEnabled = !_busy; JobEditor.IsEnabled = !_busy && CurrentJob != null;
            ResultsGrid.IsEnabled = SearchBox.IsEnabled = DiffOnlyCheck.IsEnabled = !_busy;
            DiffSaveButton.IsEnabled = !_busy && _currentDiff != null && _currentDiff.IsText;
        }
        private void Add_Click(object sender, RoutedEventArgs e)
        { if (!_busy) AddJob(BackupMode.Normal); }
        private JobDefinition AddJob(BackupMode mode)
        {
            string name = mode == BackupMode.Normal ? "バックアップ " : mode == BackupMode.FolderTree ? "フォルダ構成 " : "フォルダ名 ";
            var job = new JobDefinition { Name = name + (_jobs.Count + 1), Mode = mode };
            AttachJob(job); JobList.SelectedItem = job; InvalidateComparison(); SourceBox.Focus();
            return job;
        }
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var job = CurrentJob; if (job == null) return;
            if (MessageBox.Show("ジョブ「" + job.Name + "」を一覧から削除しますか？\nフォルダやファイルは削除しません。", "FolderDeck", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            job.PropertyChanged -= JobChanged; _jobs.Remove(job); InvalidateComparison();
            if (_jobs.Count == 0) AddJob(BackupMode.Normal);
            else JobList.SelectedItem = _jobs.FirstOrDefault(j => j.Mode == BackupMode.Normal) ?? _jobs[0];
            UpdateActions();
        }
        private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (!ReferenceEquals(_editorJob, CurrentJob))
            {
                _editorJob = CurrentJob; InvalidateComparison();
                _showDetails = false; DetailsTabs.SelectedIndex = 0;
            }
            _updatingEditor = true; JobEditor.DataContext = CurrentJob;
            BackupMode mode = CurrentJob == null ? BackupMode.Normal : CurrentJob.Mode;
            ModeCombo.SelectedIndex = (int)mode;
            CopyPolicyCombo.SelectedIndex = CurrentJob != null && CurrentJob.ForceCopy ? 1 : 0;
            CompareMethodCombo.SelectedIndex = CurrentJob == null ? 0 : (int)CurrentJob.CompareMethod;
            _updatingEditor = false; UpdateModeNote(); UpdateActions();
        }
        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingEditor || _busy || CurrentJob == null || ModeCombo.SelectedIndex < 0) return;
            CurrentJob.Mode = (BackupMode)ModeCombo.SelectedIndex;
        }
        private void CopyPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingEditor || _busy || CurrentJob == null || CopyPolicyCombo.SelectedIndex < 0) return;
            CurrentJob.ForceCopy = CopyPolicyCombo.SelectedIndex == 1;
        }
        private void FastAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _batchJobEdit = true;
            try { foreach (var job in _jobs) job.CompareMethod = ComparisonMethod.Metadata; }
            finally { _batchJobEdit = false; }
            _updatingEditor = true; CompareMethodCombo.SelectedIndex = 0; _updatingEditor = false;
            InvalidateComparison(); StatusText.Text = "全設定を高速比較（サイズ・更新日時）にしました。再比較してください。";
            AddLog(StatusText.Text);
        }
        private void CompareMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingEditor || _busy || CurrentJob == null || CompareMethodCombo.SelectedIndex < 0) return;
            CurrentJob.CompareMethod = (ComparisonMethod)CompareMethodCombo.SelectedIndex;
        }
        private void FolderTool_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var mode = (BackupMode)int.Parse((string)((MenuItem)sender).Tag);
            var job = CurrentJob;
            if (job == null || !string.IsNullOrWhiteSpace(job.Source) || !string.IsNullOrWhiteSpace(job.Destination)) AddJob(mode);
            else
            {
                job.Mode = mode;
                job.Name = (mode == BackupMode.FolderTree ? "フォルダ構成 " : "フォルダ名 ") + (_jobs.IndexOf(job) + 1);
            }
            JobList_SelectionChanged(this, null);
            SourceBox.Focus();
        }
        private void UpdateModeNote()
        {
            var job = CurrentJob;
            if (job == null) return;
            CopyPolicyPanel.Visibility = CompareMethodPanel.Visibility = MirrorCheck.Visibility = job.Mode == BackupMode.Normal ? Visibility.Visible : Visibility.Collapsed;
            RecursiveCheck.IsEnabled = job.Mode == BackupMode.Normal;
            ModeNote.Visibility = job.Mode == BackupMode.Normal ? Visibility.Collapsed : Visibility.Visible;
            ModeNote.Text = job.Mode == BackupMode.FolderTree ? "下の階層も含めて、空のフォルダを作成します。"
                : "保存先に指定した名前の空フォルダを作成します。";
        }
        private string PickFolder(string title, string current = null)
        {
            try
            {
                return FolderPicker.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle, title, current);
            }
            catch (Exception ex) { ShowError(ex); return null; }
        }
        private bool TryDroppedFolder(DragEventArgs e, out string folder, out string reason)
        {
            folder = null; reason = null;
            if (_busy || CurrentJob == null) { reason = "処理中はフォルダを変更できません。"; return false; }
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { reason = "エクスプローラーからフォルダを1つドロップしてください。"; return false; }
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length != 1) { reason = "この入力欄にはフォルダを1つずつドロップしてください。"; return false; }
                folder = PathGuard.Normalize(paths[0]);
                if (!Directory.Exists(folder)) { reason = "フォルダをドロップしてください。ファイルの指定には対応していません。"; folder = null; return false; }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException || ex is System.Runtime.InteropServices.COMException)
            { reason = "フォルダを確認できません: " + ex.Message; folder = null; return false; }
        }
        private void PathBox_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            string folder, reason; bool accepted = TryDroppedFolder(e, out folder, out reason);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            var box = sender as TextBox;
            if (box != null)
            {
                if (accepted) box.SetResourceReference(Control.BorderBrushProperty, "Accent");
                else box.ClearValue(Control.BorderBrushProperty);
            }
            if (!_busy) StatusText.Text = accepted ? "ここにドロップして" + (ReferenceEquals(sender, SourceBox) ? "コピー元" : "保存先") + "を指定: " + folder : reason;
        }
        private void PathBox_DragLeave(object sender, DragEventArgs e)
        {
            var box = sender as TextBox; if (box != null) box.ClearValue(Control.BorderBrushProperty);
            e.Handled = true;
        }
        private void PathBox_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            var box = sender as TextBox; if (box != null) box.ClearValue(Control.BorderBrushProperty);
            string folder, reason;
            if (!TryDroppedFolder(e, out folder, out reason)) { if (!_busy) StatusText.Text = reason; return; }
            try
            {
                PathGuard.NoReparseAncestors(folder);
                var job = CurrentJob;
                if (ReferenceEquals(sender, SourceBox)) SetSourceFolder(job, folder);
                else if (ReferenceEquals(sender, DestinationBox)) job.Destination = folder;
                else return;
                // Also invalidate when the same folder was dropped and no PropertyChanged was raised.
                InvalidateComparison();
                e.Effects = DragDropEffects.Copy;
                StatusText.Text = (ReferenceEquals(sender, SourceBox) ? "コピー元" : "保存先") + "を指定しました。差分を確認してください。";
            }
            catch (Exception ex) { StatusText.Text = "フォルダを指定できません: " + ex.Message; }
        }
        private void SetSourceFolder(JobDefinition job, string folder)
        {
            job.Source = folder;
            if (job.Name.StartsWith("バックアップ ", StringComparison.Ordinal) || job.Name.StartsWith("フォルダ構成 ", StringComparison.Ordinal)
                || job.Name.StartsWith("フォルダ名 ", StringComparison.Ordinal) || job.Name == "新しいジョブ")
            {
                string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(name)) job.Name = name;
            }
        }
        private void SourceBrowse_Click(object sender, RoutedEventArgs e)
        {
            var job = CurrentJob; if (job == null) return; string folder = PickFolder("コピー元フォルダを選択", job.Source);
            if (folder != null) SetSourceFolder(job, folder);
        }
        private void DestinationBrowse_Click(object sender, RoutedEventArgs e)
        {
            var job = CurrentJob; if (job == null) return; string folder = PickFolder("コピー先フォルダを選択", job.Destination);
            if (folder != null) job.Destination = folder;
        }
        private void Bulk_Click(object sender, RoutedEventArgs e)
        {
            string parent = PickFolder("追加したい複数フォルダが入っている、親フォルダを選択"); if (parent == null) return;
            try
            {
                var select = new SelectFoldersWindow(parent) { Owner = this };
                if (select.ShowDialog() != true) return;
                string destination = PickFolder("コピー先の親フォルダを選択（その下に各フォルダ名を作成）");
                if (destination == null) return;
                AddFolders(select.SelectedFolders, destination, select.SelectedMode);
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private void AddFolders(IEnumerable<string> sources, string destination, BackupMode mode = BackupMode.Normal)
        {
            JobDefinition first = null;
            foreach (string source in sources)
            {
                var normalized = PathGuard.Normalize(source);
                string name = Path.GetFileName(normalized);
                if (string.IsNullOrEmpty(name)) throw new IOException("ドライブのルートは個別のジョブとして登録してください。");
                var job = new JobDefinition { Name = name, Source = normalized, Destination = Path.Combine(destination, name), Mode = mode };
                AttachJob(job); if (first == null) first = job;
            }
            if (first != null) JobList.SelectedItem = first;
            InvalidateComparison(); AddLog("複数フォルダをジョブとして登録しました。モードは各ジョブで変更できます。");
        }
        private void JobList_DragOver(object sender, DragEventArgs e)
        { e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
        private void JobList_Drop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var folders = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(Directory.Exists).ToArray();
            if (folders.Length == 0) return;
            string destination = PickFolder("コピー先の親フォルダを選択");
            if (destination == null) return;
            try { AddFolders(folders, destination); } catch (Exception ex) { ShowError(ex); }
        }
        private async void Compare_Click(object sender, RoutedEventArgs e)
        { if (CurrentJob != null) await RunComparison(new List<JobPlan> { CurrentJob.Snapshot() }); }
        private async void CompareAll_Click(object sender, RoutedEventArgs e)
        { await RunComparison(_jobs.Where(j => j.Enabled && (!string.IsNullOrWhiteSpace(j.Source) || !string.IsNullOrWhiteSpace(j.Destination))).Select(j => j.Snapshot()).ToList()); }
        private async Task RunComparison(IList<JobPlan> plans)
        {
            if (_busy || plans.Count == 0) return;
            _snapshotReady = false; _afterBackup = false; _sameEntriesOmitted = 0; SetEntries(new List<CompareEntry>());
            string comparisonKey = string.Join("\n", plans.Select(p => string.Join("\t", p.Id, p.Source, p.Destination,
                ((int)p.Mode).ToString(), p.Recursive.ToString(), p.ForceCopy.ToString(), p.MirrorDestination.ToString(),
                ((int)p.CompareMethod).ToString()))) + "\n" + (DiffOnlyCheck.IsChecked == true);
            var token = BeginBusy("比較を開始しています…", comparisonKey == _lastComparisonKey ? _lastComparisonSeconds : 0); AddLog("比較開始: " + plans.Count + " ジョブ");
            try
            {
                var progress = Progress(false);
                var statistics = new ComparisonStatistics(); var elapsed = Stopwatch.StartNew();
                bool omitUnchanged = DiffOnlyCheck.IsChecked == true;
                var results = await Task.Run(() => CompareService.Compare(plans, token, progress, statistics, omitUnchanged), token);
                _sameEntriesOmitted = statistics.SameEntriesOmitted;
                elapsed.Stop(); _currentComparisonSeconds = elapsed.Elapsed.TotalSeconds;
                DrainProgress(false);
                AddLog("比較: ファイル情報 " + statistics.FilesEnumerated + " 件 / 内容検証 " + statistics.FilesHashed + " 件・" + CompareEntry.FormatSize(statistics.BytesHashed) + " / 同一省略 " + statistics.SameEntriesOmitted + " 項目 / Windows先読み " + statistics.LargeFetchDirectories + " フォルダ / 経過 " + elapsed.Elapsed);
                _compared = DateTime.Now; SetEntries(results); _snapshotReady = true; _manualRemovalBlocked = false;
                CompareTime.Text = "比較所要 " + WorkTimeEstimate.Duration(_currentComparisonSeconds) + " / " + _compared.ToString("yyyy/MM/dd HH:mm:ss") + "  /  " + string.Join(" / ", plans.Select(p => p.ComparisonLabel).Distinct());
                int errors = results.Count(x => x.State == EntryState.Error || x.State == EntryState.Conflict);
                int skips = results.Count(x => x.State == EntryState.Skipped);
                if (errors == 0 && skips == 0)
                { _lastComparisonKey = comparisonKey; _lastComparisonSeconds = elapsed.Elapsed.TotalSeconds; }
                StatusText.Text = "比較完了  /  " + results.Count + " 項目" + (_sameEntriesOmitted > 0 ? "  /  同一 " + _sameEntriesOmitted + " 項目省略" : "") + (errors > 0 ? "  /  エラー・競合 " + errors : "") + (skips > 0 ? "  /  除外 " + skips : "");
                AddLog(StatusText.Text, errors > 0 ? "#FFD398" : "#7DE6C4");
                var first = _view.Cast<CompareEntry>().FirstOrDefault(x => !x.IsDirectory && x.State == EntryState.Changed && IsTextExtension(x.DisplayPath))
                    ?? _view.Cast<CompareEntry>().FirstOrDefault(x => !x.IsDirectory && x.State == EntryState.Changed)
                    ?? _view.Cast<CompareEntry>().FirstOrDefault();
                if (first != null) { ResultsGrid.SelectedItem = first; ResultsGrid.ScrollIntoView(first); }
            }
            catch (OperationCanceledException) { StatusText.Text = "比較をキャンセルしました。"; AddLog(StatusText.Text); }
            catch (Exception ex) { StatusText.Text = "比較できませんでした。"; AddLog(ex.Message, "#FFACB4"); ShowError(ex); }
            finally { EndBusy(); }
        }
        private CancellationToken BeginBusy(string message, double previousEstimate = 0)
        {
            _filterTimer.Stop(); _diffCts?.Cancel(); _workCts = new CancellationTokenSource(); _busy = true;
            _activeProgress = null;
            _completedWork = _totalWork = 0; _previousEstimate = previousEstimate;
            _workClock.Restart(); UpdateTiming(); _timeTimer.Start();
            StatusText.Text = message; WorkBar.Visibility = Visibility.Visible; WorkBar.IsIndeterminate = true;
            CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true; UpdateActions(); return _workCts.Token;
        }
        private void EndBusy()
        {
            _timeTimer.Stop(); DrainProgress(false); _activeProgress = null; _workClock.Stop();
            TimingText.Text = "前回の所要: " + WorkTimeEstimate.Duration(_workClock.Elapsed.TotalSeconds);
            _busy = false; WorkBar.Visibility = Visibility.Collapsed; WorkBar.IsIndeterminate = false;
            CancelButton.Visibility = Visibility.Collapsed; _workCts?.Dispose(); _workCts = null; UpdateActions();
            if (_closeWhenIdle) Close();
            else if (ResultsGrid.SelectedItem != null && _currentDiff == null) ResultsGrid_SelectionChanged(this, null);
        }
        private IProgress<WorkProgress> Progress(bool log)
        { _activeProgress = new WorkProgressBuffer(log); return _activeProgress; }
        private void DrainProgress(bool updateStatus)
        {
            if (_activeProgress == null) return;
            WorkProgress p; string[] messages;
            _activeProgress.Drain(out p, out messages);
            if (messages.Length > 0) AddLogBatch(messages);
            if (p == null || !updateStatus || !_busy || _workCts == null || _workCts.IsCancellationRequested) return;
            StatusText.Text = p.Message;
            if (p.TotalWork > 0)
            {
                _completedWork = p.CompletedWork; _totalWork = p.TotalWork;
                WorkBar.IsIndeterminate = false; WorkBar.Maximum = p.TotalWork; WorkBar.Value = p.CompletedWork;
            }
            else if (p.Total > 0) { WorkBar.IsIndeterminate = false; WorkBar.Maximum = p.Total; WorkBar.Value = p.Completed; }
        }
        private void CopyParallelism_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _copyParallelism = int.Parse((string)((MenuItem)sender).Tag);
            UpdateActions(); StatusText.Text = "コピーの並列数: " + _copyParallelism;
        }
        private void UpdateTiming()
        {
            if (!_busy) return;
            double elapsed = _workClock.Elapsed.TotalSeconds;
            string prefix = "経過 " + WorkTimeEstimate.Duration(elapsed) + " / ";
            if (_workCts != null && _workCts.IsCancellationRequested)
            { TimingText.Text = prefix + "キャンセル待ち"; return; }
            if (_totalWork > 0 && _completedWork >= _totalWork)
            { TimingText.Text = prefix + "仕上げ中"; return; }
            double? remaining = WorkTimeEstimate.RemainingSeconds(elapsed, _completedWork, _totalWork, _previousEstimate);
            TimingText.Text = prefix + (remaining.HasValue ? "残り 約 " + WorkTimeEstimate.Duration(remaining.Value)
                + (_totalWork <= 0 ? "（前回実績）" : "（目安）")
                : _totalWork <= 0 && _previousEstimate > 0 && elapsed >= _previousEstimate
                    ? "残り 算出中（前回実績を超過）" : "残り 算出中");
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        { _workCts?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "キャンセルしています…"; UpdateTiming(); }
        private void Explorer_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _manualRemovalBlocked || !(_snapshotReady || _afterBackup)) return;
            try
            {
                var groups = ManualRemoval.Groups(_entries);
                if (groups.Count == 0) return;
                new ManualRemovalWindow(groups, message => AddLog(message)) { Owner = this }.ShowDialog();
                _snapshotReady = false; _afterBackup = true;
                StatusText.Text = "手動操作の結果は未確認です。［1 もう一度確認］で再比較してください。";
                UpdateActions();
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private async void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !_snapshotReady) return;
            var selected = _entries.Where(x => x.Selected && x.CanBackup).ToArray(); if (selected.Length == 0) return;
            int overwrite = selected.Count(x => x.CanBackup && !x.IsDirectory && x.DestinationStamp != null);
            int forcedSame = selected.Count(x => x.IsForcedCopy);
            string policy = string.Join(" / ", selected.Select(x => x.Job.CopyPolicyLabel).Distinct());
            int copied = selected.Count(x => x.CanBackup);
            int blocked = _entries.Count(x => x.DeleteBlocked);
            string confirmation = "コピー方式: " + policy + "\n比較方法: " + string.Join(" / ", selected.Select(x => x.Job.ComparisonLabel).Distinct())
                + "\n\nコピー・作成: " + copied + " 項目\n上書き: " + overwrite + " 件"
                + (forcedSame > 0 ? "\nうち同一内容: " + forcedSame + " 件" : "")
                + "\n\n不要項目は削除しません。コピー後に［3 不要項目を開く］から確認してください。";
            confirmation += "\n\n実行しますか？";
            if (MessageBox.Show(confirmation, "バックアップの実行", MessageBoxButton.OKCancel,
                overwrite > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _manualRemovalBlocked = true;
            var token = BeginBusy("バックアップを開始しています…"); AddLog("バックアップ開始: " + policy + " / " + selected.Length + " 項目 / コピー並列数 " + _copyParallelism);
            try
            {
                string reportDir = Path.Combine(SettingsStore.DataDirectory, "Reports"); Directory.CreateDirectory(reportDir);
                string report = Path.Combine(reportDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "-before.csv");
                ReportExporter.CsvReport(report, _entries, _compared); AddLog("実行前の比較一覧を保存: " + report);
                var progress = Progress(true);
                var result = await Task.Run(() => BackupService.Execute(selected, token, progress, _copyParallelism), token);
                DrainProgress(false);
                _batchSelection = true;
                try { foreach (var hold in result.Holds) hold.Entry.HoldDeletion(hold.Reason); }
                finally { _batchSelection = false; CacheEntries(); }
                _afterBackup = true; _snapshotReady = false;
                _manualRemovalBlocked = result.Errors > 0 || result.Warnings > 0 || result.Cancelled;
                string label = result.Cancelled ? "中断" : result.Errors > 0 || result.Warnings > 0 || result.SkippedDeletes > 0 || blocked > 0 ? "一部未完了" : "完了";
                foreach (var job in _jobs.Where(j => selected.Any(x => x.Job.Id == j.Id))) job.LastRun = DateTime.Now.ToString("MM/dd HH:mm") + "  " + label;
                StatusText.Text = "処理" + label + "  /  コピー " + result.Files + " 件・作成 " + result.Folders + " 件・不要項目は手動で確認・削除保留 " + (result.SkippedDeletes + blocked) + " 件・エラー " + result.Errors + " 件・警告 " + result.Warnings + " 件";
                AddLog(StatusText.Text, result.Errors == 0 && result.Warnings == 0 ? "#7DE6C4" : "#FFD398");
                AddLog("工程別所要: 比較 " + WorkTimeEstimate.Duration(_currentComparisonSeconds)
                    + " / コピー・作成 " + WorkTimeEstimate.Duration(result.CopySeconds)
                    + " / 並列数 " + _copyParallelism);
                CompareTime.Text = "比較 " + WorkTimeEstimate.Duration(_currentComparisonSeconds)
                    + " / コピー・作成 " + WorkTimeEstimate.Duration(result.CopySeconds)
                    + " / 不要項目は手動確認・操作後に再比較";
                if (!_settingsLoadFailed) SaveSettings(); DetailsTabs.SelectedIndex = 1;
                if (result.Errors > 0 || result.Warnings > 0 || result.SkippedDeletes > 0 || blocked > 0) { _showDetails = true; UpdateDetailsView(); }
            }
            catch (OperationCanceledException) { _snapshotReady = false; _afterBackup = true; StatusText.Text = "キャンセルしました。再比較してください。"; AddLog(StatusText.Text); }
            catch (Exception ex) { _snapshotReady = false; _afterBackup = true; AddLog(ex.Message, "#FFACB4"); StatusText.Text = "バックアップでエラーが発生しました。"; ShowError(ex); }
            finally { EndBusy(); }
        }
        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _busy) return;
            var entry = ResultsGrid.SelectedItem as CompareEntry; if (entry == null) return;
            _diffCts?.Cancel(); _diffCts = new CancellationTokenSource(); var token = _diffCts.Token;
            _currentDiff = null; _currentDiffEntry = null; DiffSaveButton.IsEnabled = false;
            DiffTitle.Text = entry.JobName + " / " + entry.DisplayPath; DiffMeta.Text = "比較方向: コピー先 → コピー元";
            DiffList.ItemsSource = null; DiffEmpty.Text = "読み込み中…"; DiffEmpty.Visibility = Visibility.Visible;
            try
            {
                TextDiffResult diff;
                if (!_diffCache.TryGetValue(entry, out diff))
                {
                    if (_afterBackup) { DiffEmpty.Text = "実行前の内容diffは保持していません。現在の内容を確認する場合は再比較してください。"; return; }
                    diff = await Task.Run(() => TextDiffer.ForEntry(entry, token), token);
                    token.ThrowIfCancellationRequested();
                    // A single selected-file cache keeps memory bounded; it remains exportable after backup.
                    _diffCache.Clear(); _diffCache[entry] = diff;
                }
                if (!ReferenceEquals(ResultsGrid.SelectedItem, entry)) return;
                _currentDiff = diff; _currentDiffEntry = entry;
                DiffMeta.Text = diff.IsText ? diff.Metadata + "  /  +" + diff.Added + "  −" + diff.Removed + (string.IsNullOrEmpty(diff.Note) ? "" : "  /  " + diff.Note) : entry.StateLabel;
                DiffList.ItemsSource = diff.Lines;
                DiffEmpty.Text = diff.Note ?? "行の内容は同一です。";
                DiffEmpty.Visibility = diff.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                DiffSaveButton.IsEnabled = diff.IsText;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) { DiffEmpty.Text = ex.Message; DiffEmpty.Visibility = Visibility.Visible; } }
        }
        private void Filter_Changed(object sender, RoutedEventArgs e) { if (_ready) { _filterTimer.Stop(); RefreshView(); if (_sameEntriesOmitted > 0 && DiffOnlyCheck.IsChecked != true) StatusText.Text = "同一項目の一覧は省略しています。この状態で再比較すると表示できます。"; } }
        private void Search_Changed(object sender, TextChangedEventArgs e)
        { if (_ready && !_busy) { _filterTimer.Stop(); _filterTimer.Start(); } }
        private void SelectAll_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy || _view == null) return;
            _batchSelection = true;
            try { foreach (var item in _view.Cast<CompareEntry>()) item.Selected = AllSelectedCheck.IsChecked == true; }
            finally { _batchSelection = false; UpdateActions(); }
        }
        private async void VerifyContent_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !_snapshotReady) return;
            var targets = _view.Cast<CompareEntry>().Where(x => x.NeedsContentVerification).ToArray();
            if (targets.Length == 0) { StatusText.Text = "表示中に内容未確認の変更候補はありません。"; return; }
            var results = new List<ContentCheckResult>(); bool cancelled = false;
            var token = BeginBusy("変更候補の中身を確認しています…");
            double totalBytes = targets.Sum(x => 2d * x.SourceStamp.Length);
            try
            {
                var progress = Progress(true);
                await Task.Run(() => {
                    double readBytes = 0; var clock = Stopwatch.StartNew();
                    for (int i = 0; i < targets.Length; i++)
                    {
                        token.ThrowIfCancellationRequested(); var entry = targets[i];
                        results.Add(ContentVerifier.Check(entry, token, bytes => {
                            readBytes += bytes;
                            if (clock.ElapsedMilliseconds < 250) return;
                            clock.Restart(); progress.Report(new WorkProgress { Message = "内容確認中: " + entry.JobName + " / " + entry.DisplayPath, IsDetail = true,
                                Completed = i, Total = targets.Length, CompletedWork = readBytes, TotalWork = totalBytes });
                        }));
                        progress.Report(new WorkProgress { Message = "内容確認: " + (i + 1) + " / " + targets.Length, IsDetail = true,
                            Completed = i + 1, Total = targets.Length, CompletedWork = readBytes, TotalWork = totalBytes });
                    }
                });
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { _snapshotReady = false; _manualRemovalBlocked = true; ShowError(ex); }
            finally
            {
                DrainProgress(false);
                foreach (var result in results)
                {
                    result.Entry.ApplyContentCheck(result.Source, result.Destination, result.Error);
                    _diffCache.Remove(result.Entry);
                }
                int errors = results.Count(r => r.Error != null);
                if (errors > 0) { _snapshotReady = false; _manualRemovalBlocked = true; }
                CacheEntries(); RefreshView();
                StatusText.Text = "内容確認" + (cancelled ? "を中断" : "完了") + ": 日時のみ " + results.Count(r => r.Error == null && r.Entry.IsTimestampOnly)
                    + " 件 / 内容変更 " + results.Count(r => r.Error == null && !r.Entry.IsTimestampOnly) + " 件 / 確認不可 " + errors + " 件"
                    + (errors > 0 || !_snapshotReady ? "。ファイルが変わった可能性があります。再比較してください。" : cancelled ? "。未完了の項目は内容未確認のままです。" : "");
                AddLog(StatusText.Text); EndBusy();
            }
        }
        private void Csv_Click(object sender, RoutedEventArgs e) { Export(false); }
        private void Html_Click(object sender, RoutedEventArgs e) { Export(true); }
        private async void Export(bool html)
        {
            if (_busy) return;
            var dialog = new SaveFileDialog { FileName = "FolderDeck-" + _compared.ToString("yyyyMMdd-HHmmss") + (html ? ".html" : ".csv"),
                Filter = html ? "HTMLレポート|*.html" : "CSV比較一覧（内容差分を別CSVに保存）|*.csv", OverwritePrompt = html };
            if (dialog.ShowDialog(this) != true) return;
            var entries = _view.Cast<CompareEntry>().ToList();
            var compared = _compared;
            string diffPath = html ? null : CsvDiffExporter.DiffPath(dialog.FileName);
            if (!html && (File.Exists(dialog.FileName) || File.Exists(diffPath)) &&
                MessageBox.Show("次の2ファイルを保存します。既存ファイルは上書きします。\n\n" + dialog.FileName + "\n" + diffPath,
                    "CSVの保存", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var token = BeginBusy(html ? "内容差分付きHTMLを作成しています…" : "比較一覧と内容差分CSVを作成しています…");
            try
            {
                var progress = Progress(true);
                await Task.Run(() => {
                    if (html) ReportExporter.HtmlReport(dialog.FileName, entries, compared, token, progress);
                    else CsvDiffExporter.Export(dialog.FileName, entries, compared, token, progress);
                });
                DrainProgress(false);
                StatusText.Text = "出力しました: " + dialog.FileName + (html ? "" : " / " + diffPath); AddLog(StatusText.Text);
            }
            catch (OperationCanceledException) { StatusText.Text = "レポート出力をキャンセルしました。保存先は変更していません。"; AddLog(StatusText.Text); }
            catch (Exception ex) { ShowError(ex); }
            finally { EndBusy(); }
        }
        private void DiffSave_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDiff == null || !_currentDiff.IsText || _currentDiffEntry == null) return;
            string name = Path.GetFileName(_currentDiffEntry.SourcePath);
            var dialog = new SaveFileDialog { FileName = name + ".diff.html", Filter = "色付きHTML|*.html|Unified diff|*.diff" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (dialog.FilterIndex == 2) File.WriteAllText(dialog.FileName, TextDiffer.Unified(_currentDiffEntry, _currentDiff), new UTF8Encoding(false));
                else ReportExporter.DiffHtml(dialog.FileName, _currentDiffEntry, _currentDiff, _compared);
                StatusText.Text = "diffを出力しました: " + dialog.FileName; AddLog(StatusText.Text);
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private void SaveSettings()
        {
            var bounds = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : new Size(RestoreBounds.Width, RestoreBounds.Height);
            SettingsStore.Save(SettingsStore.SettingsPath, new AppSettings { CopyParallelism = _copyParallelism, Jobs = _jobs.ToList(), LastSelectedJobId = CurrentJob == null ? null : CurrentJob.Id, LastComparisonKey = _lastComparisonKey, LastComparisonSeconds = _lastComparisonSeconds, Width = bounds.Width, Height = bounds.Height });
        }
        private void AddLog(string text, string color = "#D0D9E5") { AddLogBatch(new[] { text }, color); }
        private void AddLogBatch(IEnumerable<string> messages, string color = null)
        {
            var batch = messages.ToArray(); if (batch.Length == 0) return;
            _logs.AppendRange(batch.Select(text => new LogEntry { Text = text,
                Color = color ?? (text.StartsWith("エラー", StringComparison.Ordinal) ? "#FFACB4" : "#D0D9E5") }));
            try { SettingsStore.AppendLogs(batch); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { StatusText.ToolTip = "ログを保存できません: " + ex.Message; }
        }
        private void OpenData_Click(object sender, RoutedEventArgs e)
        { try { Directory.CreateDirectory(SettingsStore.DataDirectory); Process.Start(new ProcessStartInfo(SettingsStore.DataDirectory) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex); } }
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_ready) return;
            _filterTimer.Stop();
            if (_busy)
            {
                e.Cancel = true;
                if (MessageBox.Show("処理をキャンセルして終了しますか？", "FolderDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                { _closeWhenIdle = true; _workCts?.Cancel(); }
                return;
            }
            _diffCts?.Cancel();
            if (_settingsLoadFailed) return;
            try { SaveSettings(); }
            catch (Exception ex) { e.Cancel = true; ShowError(ex); }
        }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void Maximize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        private void SettingsMenu_Click(object sender, RoutedEventArgs e)
        {
            SettingsMenuButton.ContextMenu.PlacementTarget = SettingsMenuButton;
            SettingsMenuButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            SettingsMenuButton.ContextMenu.IsOpen = true;
        }
        private void Tools_Click(object sender, RoutedEventArgs e)
        {
            ToolsButton.ContextMenu.PlacementTarget = ToolsButton;
            ToolsButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ToolsButton.ContextMenu.IsOpen = true;
        }
        private void UpdateResultColumns()
        {
            bool detailed = DetailedColumnsMenu.IsChecked;
            DestinationSizeColumn.Visibility = SourceTimeColumn.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
            JobColumn.Visibility = detailed || _entryJobCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        private void DetailedColumns_Changed(object sender, RoutedEventArgs e)
        { if (_ready) UpdateResultColumns(); }
        private void UpdateDetailsView()
        {
            bool hasJob = CurrentJob != null;
            ResultsPane.Visibility = hasJob && !_showDetails ? Visibility.Visible : Visibility.Collapsed;
            DetailsPane.Visibility = hasJob && _showDetails ? Visibility.Visible : Visibility.Collapsed;
            DetailsButton.Content = _showDetails ? "‹  一覧に戻る" : "›  内容の差分・履歴";
        }
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || CurrentJob == null) return;
            DetailsTabs.SelectedIndex = 2; _showDetails = true; UpdateDetailsView();
        }
        private void Details_Click(object sender, RoutedEventArgs e)
        {
            _showDetails = !_showDetails;
            if (_showDetails && DetailsTabs.SelectedIndex == 2) DetailsTabs.SelectedIndex = 0;
            UpdateDetailsView();
        }
        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_busy || ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) as DataGridRow == null) return;
            DetailsTabs.SelectedIndex = 0; _showDetails = true; UpdateDetailsView();
        }
        private void Help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("コピー元と保存先を選び、［1 差分を確認］→［2 差分をコピー］の順に操作します。\n\n"
                + "［コピー方式］で、新規・変更分だけのコピーと、同じ内容も含めた強制コピーを選べます。変更後は再比較してください。\n\n"
                + "高速比較で［差分だけ］をオンにすると、同一項目は一覧に保持せずメモリを節約します。オフにして再比較すると同一項目も表示します。\n\n"
                + "大量のファイルでは［比較］を［高速］にします。同じサイズ・同じ日時の内容変更まで調べる場合は［厳密］を選びます。\n\n"
                + "設定は終了時に自動保存され、次回起動時に復元されます。\n\n"
                + "［不要項目も確認］では、コピー元にない項目を表示します。コピー後に［3 不要項目を開く］でエクスプローラーを開き、選択内容を確認してDeleteキーで操作します。FolderDeckは自動削除しません。削除後は再比較してください。\n\n"
                + "空フォルダの作成は、右上の［•••］→［空フォルダを作成］から開けます。既存設定の処理内容は［名前・詳細設定］で変更できます。\n\n"
                + "内容の差分は一覧をダブルクリック、まとめて追加・比較やCSV出力は右上の［•••］から開けます。\n設定は正常終了時にも保存します。", "FolderDeck の使い方", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void ShowError(Exception ex) { MessageBox.Show(ex.Message, "FolderDeck", MessageBoxButton.OK, MessageBoxImage.Error); }
        private static bool IsTextExtension(string path)
        { return new[] { ".txt", ".json", ".xml", ".config", ".cs", ".cpp", ".c", ".h", ".md", ".ini", ".csv", ".log", ".xaml", ".html", ".yml", ".yaml" }.Contains(Path.GetExtension(path).ToLowerInvariant()); }
    }
}
