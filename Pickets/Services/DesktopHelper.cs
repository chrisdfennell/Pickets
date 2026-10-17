using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Pickets
{
    internal static class DesktopHelper
    {
        // ---- Messages / commands ----
        private const int WM_COMMAND = 0x0111;
        private const int CMD_TOGGLE_DESKTOP = 0x7402;     // "Show desktop icons" verb

        // SMTO flags
        private const uint SMTO_NORMAL = 0x0000;
        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        // ShowWindow
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        // SetWindowPos
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // Cached handles to the desktop hierarchy
        private static IntPtr _progman = IntPtr.Zero;   // "Progman"
        private static IntPtr _workerW = IntPtr.Zero;   // WorkerW that hosts the desktop
        private static IntPtr _defView = IntPtr.Zero;   // "SHELLDLL_DefView"
        private static IntPtr _listView = IntPtr.Zero;  // "SysListView32" (desktop icons)

        // ---------- Public API ----------

        /// <summary>Call once on startup (we re-ensure as needed).</summary>
        public static void InitializeDesktopHandles()
        {
            // Ask Progman to create WorkerWs (for builds that use WorkerW).
            _progman = FindWindow("Progman", "Program Manager");
            if (_progman != IntPtr.Zero)
                SendMessageTimeout(_progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);

            RefreshHandles();
        }

        /// <summary>
        /// Robust toggle: reads current visibility, asks shell to toggle if needed (non-blocking), then verifies/forces state.
        /// </summary>
        public static void ToggleDesktopIconsRobust()
        {
            SetDesktopIconsVisible(!AreIconsVisible());
        }

        /// <summary>
        /// Ensures icons are visible/hidden. Uses PostMessage to avoid blocking Explorer; verifies after a short delay.
        /// </summary>
        public static void SetDesktopIconsVisible(bool show)
        {
            if (Pickets.Services.Sandbox.IsActive) return; // the real desktop isn't the sandbox's
            EnsureHandles();
            bool before = AreIconsVisible();

            // 1) Asynchronously ask shell to toggle if needed (non-blocking)
            if (before != show && _defView != IntPtr.Zero)
            {
                // PostMessage avoids Explorer stalls/lag vs SendMessage
                PostMessage(_defView, WM_COMMAND, new IntPtr(CMD_TOGGLE_DESKTOP), IntPtr.Zero);
            }

            // 2) After a short delay, verify and force the desired state only if still wrong
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120) // time for the shell to process the command
            };
            timer.Tick += (s, e) =>
            {
                try
                {
                    timer.Stop();
                    EnsureHandles();
                    bool now = AreIconsVisible();

                    if (now != show && _listView != IntPtr.Zero)
                    {
                        // Force final state as a fallback (rare)
                        ShowWindow(_listView, show ? SW_SHOW : SW_HIDE);
                    }
                }
                catch { /* ignore */ }
            };
            timer.Start();
        }

        // Legacy names kept for callers — single definitions only.
        public static void ToggleDesktopIcons() => ToggleDesktopIconsRobust();
        public static void ShowDesktopIcons(bool visible) => SetDesktopIconsVisible(visible);

        /// <summary>
        /// Synchronously restore desktop icons on app exit. Unlike SetDesktopIconsVisible,
        /// this does NOT rely on a DispatcherTimer (which never fires once we call
        /// Application.Shutdown). It is registry-gated so it only acts when icons are
        /// genuinely hidden, making it safe to call from multiple exit paths.
        /// </summary>
        public static void RestoreDesktopIconsOnExit()
        {
            if (RestoreIconsOnExit) ShowDesktopIconsNow();
        }

        /// <summary>Whether quitting brings the desktop icons back: only while Pickets has taken
        /// over the desktop (MainWindow keeps this in step). Someone who keeps Windows' desktop
        /// icons off, and whose fences hold no desktop items, keeps them off.</summary>
        public static bool RestoreIconsOnExit { get; set; }

        private static void ShowDesktopIconsNow()
        {
            if (Pickets.Services.Sandbox.IsActive) return;
            try
            {
                if (!DesktopIconsHiddenPerRegistry()) return; // already shown — nothing to do

                EnsureHandles();
                if (_defView == IntPtr.Zero) RefreshHandles();
                if (_defView == IntPtr.Zero) return;

                // Synchronous toggle so Explorer processes it before the process dies.
                SendMessageTimeout(_defView, WM_COMMAND, new IntPtr(CMD_TOGGLE_DESKTOP),
                                   IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Recovery ("Pickets.exe --restore-icons"): make the desktop icons visible again, e.g. after
        /// Pickets was killed while they were hidden. Returns true if they're showing afterwards.
        /// </summary>
        public static bool ForceShowDesktopIcons()
        {
            if (Pickets.Services.Sandbox.IsActive) return false; // the real desktop isn't the sandbox's
            try
            {
                InitializeDesktopHandles();
                ShowDesktopIconsNow(); // the shell's own "Show desktop icons" toggle
                RefreshHandles();
                if (_listView != IntPtr.Zero && !IsWindowVisible(_listView))
                    ShowWindow(_listView, SW_SHOW);
                return !DesktopIconsHiddenPerRegistry() && AreIconsVisible();
            }
            catch { return false; }
        }

        private static bool DesktopIconsHiddenPerRegistry()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return key?.GetValue("HideIcons") is int i && i == 1;
            }
            catch { return false; }
        }

        /// <summary>True if the desktop icon list view is visible.</summary>
        public static bool AreIconsVisible()
        {
            EnsureHandles();
            return _listView != IntPtr.Zero && IsWindowVisible(_listView);
        }

        /// <summary>
        /// The desktop's SysListView32 HWND (the control that hosts the real desktop icons),
        /// or IntPtr.Zero if it can't be located. Used by DesktopIconManager to read/move icons.
        /// </summary>
        public static IntPtr GetIconListView()
        {
            EnsureHandles();
            return _listView;
        }

        /// <summary>
        /// The desktop's SHELLDLL_DefView HWND (the parent that owns the icon list view and
        /// handles the desktop's WM_COMMAND verbs, e.g. the auto-arrange toggle).
        /// </summary>
        public static IntPtr GetDefView()
        {
            EnsureHandles();
            return _defView;
        }

        /// <summary>
        /// Conservative, cheap check used from the hook: verifies the window under the cursor is explorer’s desktop.
        /// </summary>
        public static bool IsLikelyDesktopUnderCursor()
        {
            if (!GetCursorPos(out POINT ptScreen)) return false;
            return IsDesktopAt(ptScreen);
        }

        /// <summary>
        /// True only when (x, y) is over EMPTY desktop: the desktop window itself, and not
        /// on one of its icons. Used by the double-click toggle so double-clicking an icon
        /// (to open it) or anything in another window doesn't show/hide the desktop.
        /// </summary>
        public static bool IsEmptyDesktopAt(int x, int y)
        {
            var pt = new POINT { X = x, Y = y };
            if (!IsDesktopAt(pt)) return false;

            // With icons hidden there's nothing to hit; otherwise require whitespace.
            if (_listView == IntPtr.Zero || !IsWindowVisible(_listView)) return true;

            var ptClient = pt;
            ScreenToClient(_listView, ref ptClient);
            return Services.DesktopIconManager.HitTest(ptClient.X, ptClient.Y) == -1;
        }

        private static bool IsDesktopAt(POINT ptScreen)
        {
            EnsureHandles();
            if (_defView == IntPtr.Zero) return false;

            IntPtr hwndUnder = WindowFromPoint(ptScreen);
            if (hwndUnder == IntPtr.Zero) return false;
            if (BelongsToCurrentProcess(hwndUnder)) return false;

            // The desktop's top-level window is Progman or WorkerW. File Explorer windows
            // (CabinetWClass) also host a SHELLDLL_DefView, so checking for that ancestor
            // alone wrongly treats clicks inside any folder window as desktop clicks.
            IntPtr root = GetAncestor(hwndUnder, GA_ROOT);
            if (root == IntPtr.Zero) return false;
            if (!IsClass(root, "Progman") && !IsClass(root, "WorkerW")) return false;

            return BelongsToExplorerProcess(root);
        }

        // ---------- internals ----------

        private static void EnsureHandles()
        {
            if (_progman == IntPtr.Zero || !IsWindow(_progman))
                _progman = FindWindow("Progman", "Program Manager");

            if (_workerW == IntPtr.Zero || !IsWindow(_workerW) ||
                _defView == IntPtr.Zero || !IsWindow(_defView) ||
                _listView == IntPtr.Zero || !IsWindow(_listView))
            {
                RefreshHandles();
            }
        }

        private static void RefreshHandles()
        {
            _defView = IntPtr.Zero;
            _workerW = IntPtr.Zero;
            _listView = IntPtr.Zero;

            // First, try WorkerW → DefView
            EnumWindows((hwnd, l) =>
            {
                if (IsClass(hwnd, "WorkerW"))
                {
                    var def = FindChildByClass(hwnd, "SHELLDLL_DefView");
                    if (def != IntPtr.Zero)
                    {
                        _workerW = hwnd;
                        _defView = def;
                        return false; // stop
                    }
                }
                return true;
            }, IntPtr.Zero);

            // Some builds host DefView directly under Progman
            if (_defView == IntPtr.Zero)
            {
                _progman = (_progman == IntPtr.Zero || !IsWindow(_progman))
                    ? FindWindow("Progman", "Program Manager")
                    : _progman;

                if (_progman != IntPtr.Zero)
                {
                    var def = FindChildByClass(_progman, "SHELLDLL_DefView");
                    if (def != IntPtr.Zero)
                    {
                        _defView = def;
                        _workerW = _progman;
                    }
                }
            }

            if (_defView != IntPtr.Zero)
                _listView = FindWindowEx(_defView, IntPtr.Zero, "SysListView32", null);
        }

        private static bool BelongsToCurrentProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        private static bool BelongsToExplorerProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                // ProcessName returns without ".exe"
                return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsClass(IntPtr hwnd, string className)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString().Equals(className, StringComparison.Ordinal);
        }

        private static bool AncestorHasClass(IntPtr hwnd, string className)
        {
            IntPtr cur = hwnd;
            while (cur != IntPtr.Zero)
            {
                if (IsClass(cur, className)) return true;
                cur = GetParent(cur);
            }
            return false;
        }

        private static IntPtr FindChildByClass(IntPtr parent, string className)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildWindows(parent, (h, l) =>
            {
                if (IsClass(h, className))
                {
                    result = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// Push a window to the "desktop layer": above the wallpaper & icons (WorkerW/Progman), below normal apps.
        /// </summary>
        public static void SendToDesktopLayer(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SendToDesktopLayer(hwnd);
        }

        /// <summary>
        /// Marks a window as a tool window so it never shows up in the Alt-Tab switcher
        /// (real fences live on the desktop, not in the app list).
        /// </summary>
        public static void HideFromAltTab(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            long style = ex.ToInt64();
            style |= WS_EX_TOOLWINDOW;
            style &= ~WS_EX_APPWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }

        /// <summary>
        /// Put <paramref name="window"/> at a DIP rect (see ScreenLayout) on whichever monitor that
        /// is. Setting Left/Top only converts with the scale of the monitor the window is on now,
        /// so a window going to a monitor with another scale lands in the wrong place; this
        /// positions it in screen pixels instead. Before the window exists it waits for it.
        /// </summary>
        public static void PlaceWindow(Window window, Rect dip)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                window.Left = dip.Left; window.Top = dip.Top; window.Width = dip.Width; window.Height = dip.Height;
                void Once(object? s, EventArgs e) { window.SourceInitialized -= Once; PlaceWindow(window, dip); }
                window.SourceInitialized += Once;
                return;
            }

            var px = Pickets.Services.ScreenLayout.PxFromDip(dip);
            int x = (int)Math.Round(px.X), y = (int)Math.Round(px.Y);
            // Move first: arriving on a monitor with another scale makes WPF rescale the window
            // to keep its size in DIPs. Then size it with the scale it ended up with.
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (double.IsNaN(dip.Width) || double.IsNaN(dip.Height)) return; // sized to content: move only
            double s = GetDpiForWindow(hwnd) is uint dpi and > 0 ? dpi / 96.0 : 1.0;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, (int)Math.Round(dip.Width * s), (int)Math.Round(dip.Height * s),
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Move a window to a screen-pixel position, keeping its size and z-order.</summary>
        public static void MoveWindowPx(IntPtr hwnd, int x, int y)
        {
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>A window's rectangle in screen pixels.</summary>
        public static Rect WindowRectPx(IntPtr hwnd) =>
            GetWindowRect(hwnd, out var r) ? new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : Rect.Empty;

        private const uint SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        public static void SendToDesktopLayer(IntPtr hwnd)
        {
            EnsureHandles();

            // Place directly above the WorkerW (or Progman if no WorkerW).
            IntPtr insertAfter = (_workerW != IntPtr.Zero) ? _workerW : _progman;
            if (insertAfter == IntPtr.Zero) return;

            SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Lift a fence above normal app windows (while searching) or drop it back. Callers put
        /// it back in the desktop layer with SendToDesktopLayer afterwards.
        /// </summary>
        public static void SetTopmost(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, topmost ? new IntPtr(-1) /*HWND_TOPMOST*/ : new IntPtr(-2) /*HWND_NOTOPMOST*/,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>
        /// Puts <paramref name="hwnd"/> directly above the highest of <paramref name="others"/>
        /// (the other fences), without lifting it over normal app windows. No-op if it's
        /// already above all of them.
        /// </summary>
        public static void RaiseAbove(IntPtr hwnd, ICollection<IntPtr> others)
        {
            if (hwnd == IntPtr.Zero || others.Count == 0) return;

            // Walk the z-order from the top; the first fence we meet is the highest one.
            for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (h == hwnd) return;
                if (!others.Contains(h)) continue;

                // Insert right below whatever sits above that fence, i.e. just above the fence.
                var above = GetWindow(h, GW_HWNDPREV);
                SetWindowPos(hwnd, above == IntPtr.Zero ? IntPtr.Zero /*HWND_TOP*/ : above, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                return;
            }
        }

        private const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;

        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        // ---------- P/Invoke ----------

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        // Timeout (IntPtr lParam overload)
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Extended-style access (Alt-Tab visibility)
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // structs
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
    }
}
