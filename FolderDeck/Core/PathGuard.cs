using System;
using System.Collections.Generic;
using System.IO;

namespace FolderDeck.Core
{
    public static class PathGuard
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("コピー元・コピー先を指定してください。");
            string value = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(value);
            return value.Length > root.Length ? value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : value;
        }
        public static bool Contains(string parent, string child)
        {
            parent = Normalize(parent); child = Normalize(child);
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        public static string Combine(string root, string relative)
        {
            string path = string.IsNullOrEmpty(relative) ? Normalize(root) : Path.GetFullPath(Path.Combine(root, relative));
            if (!Contains(root, path)) throw new IOException("ルート外のパスは処理できません。");
            return path;
        }
        public static void NoReparseAncestors(string path)
        {
            string value = Normalize(path);
            while (!string.IsNullOrEmpty(value))
            {
                // File.GetAttributes reports access failures; Directory.Exists alone hides them.
                try
                {
                    if ((File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("ジャンクション／シンボリックリンクを経由するパスは対象外です: " + value);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(value);
                if (string.Equals(parent, value, StringComparison.OrdinalIgnoreCase)) break;
                value = parent;
            }
        }
        public static void ValidatePlans(IList<JobPlan> plans)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                p.Source = Normalize(p.Source); p.Destination = Normalize(p.Destination);
                if (!Enum.IsDefined(typeof(BackupMode), p.Mode)) throw new IOException("動作モードが不正です。");
                if (!Enum.IsDefined(typeof(ComparisonMethod), p.CompareMethod)) throw new IOException("比較方法が不正です。");
                if (Contains(p.Source, p.Destination) || Contains(p.Destination, p.Source))
                    throw new IOException(p.Name + ": コピー元とコピー先が同じ、または親子関係にあります。");
                if (File.Exists(p.Destination)) throw new IOException(p.Name + ": コピー先に同名のファイルがあります。");
                for (int j = 0; j < i; j++)
                {
                    var q = plans[j];
                    if (Contains(q.Destination, p.Destination) || Contains(p.Destination, q.Destination) ||
                        Contains(q.Source, p.Destination) || Contains(p.Destination, q.Source) ||
                        Contains(p.Source, q.Destination) || Contains(q.Destination, p.Source))
                        throw new IOException("ジョブ間でコピー先、またはコピー元とコピー先が重なっています: " + q.Name + " / " + p.Name);
                }
            }
        }
    }
}
