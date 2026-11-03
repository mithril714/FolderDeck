using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FolderDeck.Core
{
    public static class SettingsStore
    {
        public static string DataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderDeck"); } }
        public static string SettingsPath { get { return Path.Combine(DataDirectory, "settings.json"); } }
        public static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new AppSettings();
            using (var stream = File.OpenRead(path))
            {
                var value = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
                if (value == null || value.Jobs == null) throw new IOException("設定ファイルの形式が不正です。");
                if (value.Version != 1) throw new IOException("この設定ファイルのバージョンには対応していません。");
                foreach (var job in value.Jobs)
                { if (job == null || string.IsNullOrEmpty(job.Id)) throw new IOException("ジョブの設定が不正です。"); }
                return value;
            }
        }
        public static void Save(string path, AppSettings settings)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path)); Directory.CreateDirectory(parent);
            string temp = Path.Combine(parent, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, settings);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void AppendLog(string text) { AppendLogs(new[] { text }); }
        public static void AppendLogs(IEnumerable<string> messages)
        {
            var now = DateTime.Now;
            var buffer = new StringBuilder();
            foreach (string text in messages) buffer.Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").Append(text).AppendLine();
            if (buffer.Length == 0) return;
            Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs"));
            File.AppendAllText(Path.Combine(DataDirectory, "Logs", now.ToString("yyyy-MM-dd") + ".log"), buffer.ToString(), new UTF8Encoding(true));
        }
    }
}
