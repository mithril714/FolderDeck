using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FolderDeck.Core;

namespace FolderDeck
{
    internal sealed class ManualRemovalWindow : Window
    {
        public ManualRemovalWindow(List<RemovalGroup> groups, Action<string> log)
        {
            Title = "不要項目をエクスプローラーで確認"; Width = 840; Height = 540;
            MinWidth = 650; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(20) }; Content = panel;
            var note = new TextBlock { Text = "親フォルダを選び、［選択して開く］を押してください。\nエクスプローラーの選択内容を確認し、Deleteキーで操作します。FolderDeckは削除しません。\n非空のフォルダや変更された項目は選択しません。操作後はメイン画面で再比較してください。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            var open = new Button { Content = "選択して開く", Margin = new Thickness(0, 0, 10, 0) };
            var close = new Button { Content = "閉じる（結果は再比較で確認）" }; buttons.Children.Add(open); buttons.Children.Add(close);
            var status = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 95, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
            var list = new ListBox { ItemsSource = groups, SelectedIndex = groups.Count > 0 ? 0 : -1 }; panel.Children.Add(list);
            close.Click += (s, e) => Close();
            bool working = false;
            Closing += (s, e) => { if (working) e.Cancel = true; };
            list.SelectionChanged += (s, e) => {
                var group = list.SelectedItem as RemovalGroup;
                status.Text = group == null ? "" : "候補（開く直前に再確認）:\n" + string.Join("\n", group.Entries.Take(20).Select(x => x.DisplayPath)) + (group.Entries.Count > 20 ? "\n…" : "");
            };
            status.Text = "フォルダを1つずつ開きます。選択が意図どおりか、必ずエクスプローラーで確認してください。";
            open.Click += async (s, e) => {
                var group = list.SelectedItem as RemovalGroup; if (group == null || working) return;
                working = true; open.IsEnabled = close.IsEnabled = list.IsEnabled = false; status.Text = "対象を再確認中…";
                try
                {
                    List<string> omitted = null;
                    var paths = await Task.Run(() => ManualRemoval.Validate(group, CancellationToken.None, out omitted));
                    status.Text = "選択可能 " + paths.Count + " 件 / 見送り " + omitted.Count + " 件\n" + string.Join("\n", omitted.Take(10));
                    if (paths.Count == 0) { log("手動確認: 選択可能な項目なし / " + group.Parent); return; }
                    // The only Shell action is opening and selecting; deletion remains an explicit user action.
                    ExplorerSelection.Open(group.Parent, paths);
                    log("エクスプローラーで選択: " + group.Parent + " / " + paths.Count + " 件（削除結果は未確認）");
                    status.Text += "\nエクスプローラーを開きました。選択内容を確認してから操作してください。";
                }
                catch (Exception ex) { status.Text = ex.Message + "\n再比較してください。"; log("手動確認エラー: " + ex.Message); }
                finally { working = false; open.IsEnabled = close.IsEnabled = list.IsEnabled = true; }
            };
        }
    }
}
