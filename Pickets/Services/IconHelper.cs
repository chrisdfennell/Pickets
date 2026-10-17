// File: Services/IconHelper.cs
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pickets.Services
{
    public static class IconHelper
    {
        // --- Public API: get a WPF ImageSource for a file/shortcut/CLSID/shell path ---
        public static ImageSource? GetImageSourceForPath(string pathOrShell) => ForPath(pathOrShell, 0);

        // A shortcut to a shortcut is followed a few hops at most: two shortcuts pointing at each
        // other would otherwise recurse until the process dies (a stack overflow can't be caught).
        private const int MaxShortcutHops = 4;

        private static ImageSource? ForPath(string pathOrShell, int hops)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrShell)) return null;

                // .lnk: prefer icon location stored in the shortcut; else resolve target and use that
                if (string.Equals(Path.GetExtension(pathOrShell), ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetShortcutIcon(pathOrShell, out string? iconPath, out int iconIndex))
                    {
                        var src = ExtractIconFromFile(iconPath!, iconIndex, large: true) ??
                                  ExtractIconFromFile(iconPath!, iconIndex, large: false);
                        if (src != null) return src;
                    }

                    var target = hops < MaxShortcutHops ? ShellLinkTryGetTarget(pathOrShell) : null;
                    if (!string.IsNullOrWhiteSpace(target))
                        return ForPath(target!, hops + 1);

                    // fall back to associated icon for the .lnk file itself
                    return GetFileAssocIcon(pathOrShell, large: true) ??
                           GetFileAssocIcon(pathOrShell, large: false);
                }

                // Shell CLSID patterns (e.g., "::{GUID}" or "...::{GUID}...")
                var clsid = TryExtractClsid(pathOrShell);
                if (!string.IsNullOrEmpty(clsid))
                {
                    return GetShellObjectIconFromClsid(clsid!, large: true) ??
                           GetShellObjectIconFromClsid(clsid!, large: false);
                }

                // Normal files/folders: associated icon
                return GetFileAssocIcon(pathOrShell, large: true) ??
                       GetFileAssocIcon(pathOrShell, large: false);
            }
            catch { return null; }
        }

        // --- .lnk helpers (IShellLink) ---
        private static bool TryGetShortcutIcon(string lnkPath, out string? iconPath, out int iconIndex)
        {
            iconPath = null; iconIndex = 0;
            try
            {
                var link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(260);
                link.GetIconLocation(sb, sb.Capacity, out iconIndex);
                iconPath = sb.ToString();
                if (!string.IsNullOrWhiteSpace(iconPath)) return true;
                return false;
            }
            catch { return false; }
        }

        private static string? ShellLinkTryGetTarget(string lnkPath)
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(520);
                WIN32_FIND_DATAW data;
                link.GetPath(sb, sb.Capacity, out data, 0);
                var path = sb.ToString();
                if (!string.IsNullOrWhiteSpace(path)) return path;

                // Some links point to explorer.exe with CLSID in arguments; try parse CLSID for icon
                sb.Clear();
                link.GetArguments(sb, 520);
                var args = sb.ToString();
                var clsid = TryExtractClsid(args);
                if (!string.IsNullOrEmpty(clsid)) return "shell:::" + clsid;
                return null;
            }
            catch { return null; }
        }

        private static string? TryExtractClsid(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf("::", StringComparison.Ordinal);
            if (i < 0) return null;
            int brace = s.IndexOf('{', i);
            if (brace < 0) return null;
            int end = s.IndexOf('}', brace);
            if (end < 0) return null;
            var guid = s.Substring(brace, end - brace + 1);
            return guid;
        }

        // --- Associated icons / Shell PIDL icons ---
        private static ImageSource? GetFileAssocIcon(string path, bool large)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                SHFILEINFO sfi = new();
                var flags = SHGFI.ICON | (large ? SHGFI.LARGEICON : SHGFI.SMALLICON);
                // If file may not exist (e.g., broken link target), use attributes hint
                var attr = File.Exists(path) || Directory.Exists(path) ? 0 : FILE_ATTRIBUTE_NORMAL;
                IntPtr res = SHGetFileInfo(path, attr, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags | (attr != 0 ? SHGFI.USEFILEATTRIBUTES : 0));
                if (res == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
                hIcon = sfi.hIcon;
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }

        private static ImageSource? GetShellObjectIconFromClsid(string clsid, bool large)
        {
            IntPtr pidl = IntPtr.Zero;
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                uint pdwAttributes = 0;
                var hr = SHParseDisplayName("shell:::" + clsid, IntPtr.Zero, out pidl, 0, ref pdwAttributes);
                if (hr != 0 || pidl == IntPtr.Zero) return null;

                SHFILEINFO sfi = new();
                var flags = SHGFI.ICON | SHGFI.PIDL | (large ? SHGFI.LARGEICON : SHGFI.SMALLICON);
                IntPtr res = SHGetFileInfo(pidl, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (res == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
                hIcon = sfi.hIcon;

                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }

        private static ImageSource? ExtractIconFromFile(string iconPath, int index, bool large)
        {
            if (string.IsNullOrWhiteSpace(iconPath)) return null;

            // Shortcuts often store icon paths with env vars (e.g. %windir%\...);
            // ExtractIconEx doesn't expand them, so do it ourselves.
            iconPath = Environment.ExpandEnvironmentVariables(iconPath);
            IntPtr largePtr = IntPtr.Zero, smallPtr = IntPtr.Zero;
            try
            {
                int extracted = ExtractIconEx(iconPath, index, out largePtr, out smallPtr, 1);
                IntPtr use = large ? largePtr : smallPtr;
                if (extracted > 0 && use != IntPtr.Zero)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(use, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                return null;
            }
            finally
            {
                if (largePtr != IntPtr.Zero) DestroyIcon(largePtr);
                if (smallPtr != IntPtr.Zero) DestroyIcon(smallPtr);
            }
        }

        // --- P/Invoke ---

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [Flags]
        private enum SHGFI : uint
        {
            ICON = 0x000000100,
            DISPLAYNAME = 0x000000200,
            TYPENAME = 0x000000400,
            ATTRIBUTES = 0x000000800,
            ICONLOCATION = 0x000001000,
            EXETYPE = 0x000002000,
            SYSICONINDEX = 0x000004000,
            LINKOVERLAY = 0x000008000,
            SELECTED = 0x000010000,
            ATTR_SPECIFIED = 0x000020000,
            LARGEICON = 0x000000000,
            SMALLICON = 0x000000001,
            OPENICON = 0x000000002,
            SHELLICONSIZE = 0x000000004,
            PIDL = 0x000000008,
            USEFILEATTRIBUTES = 0x000000010,
        }

        private const int FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, int dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, SHGFI uFlags);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHGetFileInfo(IntPtr pidl, int dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, SHGFI uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, ref uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        // COM for IShellLink (minimal)
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, out WIN32_FIND_DATAW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
