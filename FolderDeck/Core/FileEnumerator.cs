using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FolderDeck.Core
{
    internal struct EntryMetadata
    {
        public string Name, Issue;
        public FileAttributes Attributes;
        public long Length;
        public DateTime LastWriteUtc;
    }
    internal static class FileEnumerator
    {
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public FileAttributes Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public FindHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { return FindClose(handle); }
        }
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr FindFirstFileEx(string name, int level, out FindData data, int operation, IntPtr filter, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextFile(FindHandle handle, out FindData data);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
        public static IEnumerable<EntryMetadata> Enumerate(string folder, CancellationToken token, ComparisonStatistics stats)
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? EnumerateWindows(folder, token, stats) : EnumerateManaged(folder, token);
        }
        private static bool Unsupported(int error) { return error == 87 || error == 50 || error == 124; }
        private static void ThrowError(string folder, int error)
        {
            if (error == 5) throw new UnauthorizedAccessException("フォルダを読み取れません: " + folder);
            if (error == 3) throw new DirectoryNotFoundException("フォルダが見つかりません: " + folder);
            throw new IOException("フォルダを列挙できません: " + folder, new Win32Exception(error));
        }
        private static IEnumerable<EntryMetadata> EnumerateWindows(string folder, CancellationToken token, ComparisonStatistics stats)
        {
            token.ThrowIfCancellationRequested();
            FindData data; string pattern = Path.Combine(folder, "*");
            const uint LargeFetch = 2;
            IntPtr pointer = FindFirstFileEx(pattern, 1, out data, 0, IntPtr.Zero, LargeFetch);
            int error = pointer == InvalidHandle ? Marshal.GetLastWin32Error() : 0;
            bool largeFetch = error == 0;
            if (pointer == InvalidHandle && Unsupported(error))
            {
                pointer = FindFirstFileEx(pattern, 1, out data, 0, IntPtr.Zero, 0);
                error = pointer == InvalidHandle ? Marshal.GetLastWin32Error() : 0; largeFetch = false;
            }
            if (pointer == InvalidHandle && Unsupported(error))
            {
                pointer = FindFirstFileEx(pattern, 0, out data, 0, IntPtr.Zero, 0);
                error = pointer == InvalidHandle ? Marshal.GetLastWin32Error() : 0;
            }
            if (pointer == InvalidHandle)
            {
                if (error == 2 || error == 18) yield break;
                ThrowError(folder, error);
            }
            using (var handle = new FindHandle(pointer))
            {
                stats.NativeDirectories++; if (largeFetch) stats.LargeFetchDirectories++;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (data.Name != "." && data.Name != "..")
                    {
                        var entry = new EntryMetadata { Name = data.Name, Attributes = data.Attributes };
                        if ((data.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                        {
                            entry.Length = ((long)data.SizeHigh << 32) | data.SizeLow;
                            try { entry.LastWriteUtc = DateTime.FromFileTimeUtc(((long)data.WriteHigh << 32) | data.WriteLow); }
                            catch (ArgumentOutOfRangeException) { entry.Issue = "ファイルの更新日時が不正です。"; }
                        }
                        yield return entry;
                    }
                    if (!FindNextFile(handle, out data))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error == 18) break;
                        ThrowError(folder, error);
                    }
                }
            }
        }
        private static IEnumerable<EntryMetadata> EnumerateManaged(string folder, CancellationToken token)
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                var entry = new EntryMetadata { Name = info.Name };
                try
                {
                    entry.Attributes = info.Attributes;
                    if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                    { entry.Length = ((FileInfo)info).Length; entry.LastWriteUtc = info.LastWriteTimeUtc; }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                { entry.Issue = ex.Message; }
                yield return entry;
            }
        }
    }
}
