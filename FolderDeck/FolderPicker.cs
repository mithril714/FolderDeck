using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FolderDeck
{
    // Windows Common Item Dialog. No additional packages or COM reference required.
    internal static class FolderPicker
    {
        private const int Cancelled = unchecked((int)0x800704C7);
        private const uint PickFolders = 0x20, ForceFileSystem = 0x40,
            PathMustExist = 0x800, NoChangeDirectory = 0x8;
        private const uint FileSystemPath = 0x80058000;

        public static string Show(IntPtr owner, string title, string current)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("フォルダ選択は画面のスレッドから呼び出してください。");

            IFileDialog dialog = null;
            IShellItem initial = null, result = null;
            IntPtr displayName = IntPtr.Zero;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true);
                dialog = (IFileDialog)Activator.CreateInstance(type);
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist | NoChangeDirectory);
                dialog.SetTitle(title);
                dialog.SetOkButtonLabel("このフォルダを選択");

                string folder = ExistingFolder(current);
                if (folder != null)
                {
                    // An unavailable initial location must not prevent opening the picker.
                    try
                    {
                        var iid = typeof(IShellItem).GUID;
                        SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out initial);
                        dialog.SetFolder(initial);
                    }
                    catch (COMException) { }
                }

                int hr = dialog.Show(owner);
                if (hr == Cancelled) return null;
                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out result);
                result.GetDisplayName(FileSystemPath, out displayName);
                string path = Marshal.PtrToStringUni(displayName);
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("選択したフォルダのパスを取得できませんでした。");
                return path;
            }
            finally
            {
                if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName);
                if (result != null) Marshal.ReleaseComObject(result);
                if (initial != null) Marshal.ReleaseComObject(initial);
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        private static string ExistingFolder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
                while (!string.IsNullOrEmpty(path))
                {
                    if (Directory.Exists(path)) return path;
                    string parent = Path.GetDirectoryName(path);
                    if (string.Equals(parent, path, StringComparison.OrdinalIgnoreCase)) break;
                    path = parent;
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext,
            ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        // Declaration order follows the native vtable, including IModalWindow.Show.
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem folder);
            void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem folder);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem folder);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
            void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
            void GetDisplayName(uint kind, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
        }
    }
}
