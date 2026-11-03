using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FolderDeck
{
    // Opens and selects only. Never invokes a verb or sends keyboard input.
    internal static class ExplorerSelection
    {
        public static void Open(string parent, IList<string> paths)
        {
            if (paths.Count == 0) throw new IOException("選択できる項目がありません。再比較してください。");
            int initialized = CoInitializeEx(IntPtr.Zero, 2);
            Marshal.ThrowExceptionForHR(initialized);
            var allocated = new List<IntPtr>();
            try
            {
                Func<string, IntPtr> parse = path => {
                    IntPtr value; uint attributes;
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out value, 0, out attributes));
                    if (value == IntPtr.Zero) throw new IOException("エクスプローラーでパスを認識できません: " + path);
                    allocated.Add(value); return value;
                };
                IntPtr folder = parse(parent);
                var children = paths.Select(p => ILFindLastID(parse(p))).ToArray();
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, (uint)children.Length, children, 0));
            }
            finally { foreach (var value in allocated) Marshal.FreeCoTaskMem(value); CoUninitialize(); }
        }
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint mask, out uint attributes);
        [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr item);
        [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] children, uint flags);
    }
}
