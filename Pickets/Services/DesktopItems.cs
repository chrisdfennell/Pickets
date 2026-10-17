using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Pickets.Services
{
    /// <summary>
    /// Enumerates what actually lives on the Windows desktop as launchable entries, so fences
    /// can *render* the items themselves (like Stardock Fences) instead of shoving the real
    /// explorer icons around.
    ///
    /// The desktop is a shell folder = union of the per-user Desktop folder, the common
    /// (public) Desktop folder, and the special CLSID items (This PC, Recycle Bin, …) that the
    /// user has chosen to show. We enumerate the two file-system roots directly and add the
    /// special items whose "show on desktop" flag is enabled, resolving each to a path or a
    /// "shell:::{CLSID}" moniker that <see cref="IconHelper"/> and ShellExecute both understand.
    /// </summary>
    internal static class DesktopItems
    {
        /// <summary>A desktop entry: friendly label, a launchable path (file path or shell moniker),
        /// and whether it's a special (non-file) shell object.</summary>
        internal readonly record struct Entry(string DisplayName, string Path, bool IsSpecial);

        // Well-known desktop icons. Display names are English (matches the rest of the app).
        // The Clsid is used both for the icon (via IconHelper) and to gate visibility against
        // the HideDesktopIcons\NewStartPanel registry flags.
        private static readonly (string Name, string Clsid)[] SpecialItems =
        {
            ("This PC",       "{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
            ("User's Files",  "{59031A47-3F72-44A7-89C5-5595FE6B30EE}"),
            ("Network",       "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"),
            ("Recycle Bin",   "{645FF040-5081-101B-9F08-00AA002F954E}"),
            ("Control Panel", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}"),
        };

        private const string HideIconsKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

        /// <summary>The per-user and common (public) Desktop folders.</summary>
        public static IEnumerable<string> Roots() => Sandbox.DesktopRoots;

        /// <summary>Every item currently on the desktop, resolved to a launchable entry.</summary>
        public static List<Entry> Enumerate()
        {
            var result = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // de-dupe by path

            foreach (var root in Roots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var path in SafeEntries(root))
                {
                    if (seen.Add(path))
                        result.Add(new Entry(LabelFor(path), path, false));
                }
            }

            foreach (var (name, clsid) in SpecialItems)
            {
                if (Sandbox.IsActive || !IsSpecialShown(clsid)) continue; // a sandbox desktop is just its folder
                string shell = "shell:::" + clsid;
                if (seen.Add(shell))
                    result.Add(new Entry(name, shell, true));
            }

            return result;
        }

        /// <summary>Resolve a desktop icon's display name to a launchable path/moniker, or null
        /// if nothing on the desktop matches. Used to migrate legacy name-based ownership.</summary>
        public static string? ResolvePath(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            foreach (var (name, clsid) in SpecialItems)
                if (string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                    return "shell:::" + clsid;

            // Legacy alias: the profile folder used to be claimed under the user's account name.
            string user = System.IO.Path.GetFileName(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (string.Equals(user, displayName, StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var root in Roots())
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    var dir = System.IO.Path.Combine(root, displayName);
                    if (Directory.Exists(dir)) return dir;

                    var exact = System.IO.Path.Combine(root, displayName);
                    if (File.Exists(exact)) return exact;

                    var match = Directory.EnumerateFiles(root, displayName + ".*").FirstOrDefault();
                    if (match != null) return match;
                }
                catch { /* skip this root */ }
            }
            return null;
        }

        /// <summary>True if this path is a real item on either desktop root.</summary>
        public static bool IsOnDesktop(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (dir == null) return false;
                return Roots().Any(r => !string.IsNullOrEmpty(r) &&
                    string.Equals(dir, r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>The label the desktop shows for a file-system item (hides .lnk/.url extensions).</summary>
        public static string LabelFor(string path)
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                var match = SpecialItems.FirstOrDefault(
                    s => path.IndexOf(s.Clsid, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(match.Name)) return match.Name;
            }
            if (Directory.Exists(path)) return System.IO.Path.GetFileName(path);
            string ext = System.IO.Path.GetExtension(path);
            return (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : System.IO.Path.GetFileName(path);
        }

        // A special (CLSID) desktop icon is shown unless its HideDesktopIcons flag is 1.
        // Recycle Bin defaults to shown (flag absent); the others default to hidden.
        private static bool IsSpecialShown(string clsid)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(HideIconsKey);
                object? val = key?.GetValue(clsid);
                if (val is int i) return i == 0;
                // Absent: Recycle Bin is on by default; the rest are off.
                return string.Equals(clsid, "{645FF040-5081-101B-9F08-00AA002F954E}",
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static IEnumerable<string> SafeEntries(string root)
        {
            if (!Directory.Exists(root)) return Enumerable.Empty<string>();
            try
            {
                var shows = ExplorerShows.Read();
                var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
                var all = new DirectoryInfo(root).EnumerateFileSystemInfos("*", options).ToList();
                return all.Where(i => i is DirectoryInfo).Concat(all.Where(i => i is not DirectoryInfo))
                          .Where(i => shows.Shows(i.FullName, i.Attributes))
                          .Select(i => Path.Combine(root, i.Name)) // the root as given, as before (not its long form)
                          .ToList();
            }
            catch { return Enumerable.Empty<string>(); }
        }

        /// <summary>Whether Explorer itself would show this desktop item: hidden ones (Word's
        /// "~$Report.docx" lock files) only with Show hidden items on, protected system files
        /// (desktop.ini, Thumbs.db) only when those are shown too. Temp files never.</summary>
        public static bool IsShown(string path)
        {
            try { return ExplorerShows.Read().Shows(path, File.GetAttributes(path)); }
            catch { return false; } // gone already
        }

        /// <summary>Explorer's View → Show hidden items, and "Hide protected operating system files".</summary>
        internal readonly record struct ExplorerShows(bool Hidden, bool ProtectedSystem)
        {
            public static ExplorerShows Read()
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                    return new ExplorerShows(key?.GetValue("Hidden") is int h && h == 1,
                                             key?.GetValue("ShowSuperHidden") is int s && s == 1);
                }
                catch { return default; }
            }

            public bool Shows(string path, FileAttributes a)
            {
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                bool hidden = (a & FileAttributes.Hidden) != 0, system = (a & FileAttributes.System) != 0;
                if (hidden && system) return Hidden && ProtectedSystem;
                return !hidden || Hidden;
            }
        }
    }
}
