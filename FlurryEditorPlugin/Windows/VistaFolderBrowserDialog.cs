using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Flurry.Editor.Windows
{
    /// <summary>
    /// Vista-style folder browser dialog using COM IFileOpenDialog.
    /// </summary>
    public class VistaFolderBrowserDialog
    {
        public string SelectedPath { get; private set; }
        public string Title { get; set; }

        public VistaFolderBrowserDialog(string title = "Select Folder")
        {
            Title = title;
        }

        public bool ShowDialog(Window owner = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();

            try
            {
                dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM | FOS.FOS_PATHMUSTEXIST);
                dialog.SetTitle(Title);

                IntPtr hwnd = IntPtr.Zero;
                if (owner != null)
                    hwnd = new WindowInteropHelper(owner).Handle;
                else if (Application.Current?.MainWindow != null)
                    hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;

                int hr = dialog.Show(hwnd);
                if (hr != 0) // User cancelled or error
                    return false;

                dialog.GetResult(out IShellItem item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
                SelectedPath = path;
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        #region COM Interop

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions(FOS fos);
            void GetOptions();
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel();
            void SetFileNameLabel();
            void GetResult(out IShellItem ppsi);
            void AddPlace();
            void SetDefaultExtension();
            void Close();
            void SetClientGuid();
            void ClearClientData();
            void SetFilter();
            void GetResults();
            void GetSelectedItems();
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes();
            void Compare();
        }

        [Flags]
        private enum FOS : uint
        {
            FOS_PICKFOLDERS = 0x00000020,
            FOS_FORCEFILESYSTEM = 0x00000040,
            FOS_PATHMUSTEXIST = 0x00000800,
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000,
        }

        #endregion
    }
}
