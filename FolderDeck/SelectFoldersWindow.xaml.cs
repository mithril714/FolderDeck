using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace FolderDeck
{
    public partial class SelectFoldersWindow : Window
    {
        public sealed class FolderChoice : Observable
        {
            private bool _selected;
            public string Path { get; set; }
            public string Name { get; set; }
            public bool Selected { get { return _selected; } set { Set(ref _selected, value); } }
        }
        private readonly List<FolderChoice> _folders;
        public BackupMode SelectedMode { get { return (BackupMode)Math.Max(0, InitialModeCombo.SelectedIndex); } }
        public IEnumerable<string> SelectedFolders { get { return _folders.Where(f => f.Selected).Select(f => f.Path).ToArray(); } }
        public SelectFoldersWindow(string parent)
        {
            InitializeComponent(); ParentPath.Text = parent;
            _folders = Directory.GetDirectories(parent).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => new FolderChoice { Path = p, Name = System.IO.Path.GetFileName(p) }).ToList();
            FolderList.ItemsSource = _folders;
        }
        private void All_Click(object sender, RoutedEventArgs e) { foreach (var f in _folders) f.Selected = true; }
        private void None_Click(object sender, RoutedEventArgs e) { foreach (var f in _folders) f.Selected = false; }
        private void Ok_Click(object sender, RoutedEventArgs e)
        { if (!SelectedFolders.Any()) { MessageBox.Show(this, "フォルダを選択してください。", "FolderDeck"); return; } DialogResult = true; }
    }
}
