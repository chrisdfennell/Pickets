using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Pickets.Services
{
    /// <summary>
    /// Keeps fences visible on "Show desktop" (Win+D, or the corner of the taskbar). Windows does
    /// that by lifting the desktop window above every other window, which covered the fences
    /// (they're ordinary windows kept low in the z-order, not part of the desktop). So: when the
    /// desktop comes to the front above the fences, they're lifted just above it; as soon as
    /// anything else comes to the front, they go back down. A plain click on the desktop doesn't
    /// lift anything, since the desktop window stays at the bottom then.
    /// </summary>
    internal static class ShowDesktopWatcher
    {
        /// <summary>Fences are lifted above the shown desktop right now.</summary>
        public static bool Lifted { get; private set; }

        private static IntPtr _hook;
        private static WinEventDelegate? _proc; // kept alive while the hook exists

        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = OnForegroundChanged;
            // Out-of-context events arrive through this (the UI) thread's message loop.
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc,
                                    0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public static void Stop()
        {
            _check?.Stop();
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _proc = null;
            Lifted = false;
        }

        private static void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
                                                uint thread, uint time)
        {
            try
            {
                if (hwnd == IntPtr.Zero || IsFence(hwnd)) return; // clicking a fence keeps them up

                // Any other window, ours too (the Pickets window, a dialog), puts them back
                // down, or they'd cover it.
                if (IsDesktop(hwnd)) CheckSoon();
                else if (Lifted)
                {
                    Lifted = false;
                    foreach (var f in FenceWindow.AllFences.Where(f => f.IsVisible))
                    {
                        DesktopHelper.SetTopmost(new WindowInteropHelper(f).Handle, false);
                        f.EnsureBottomZOrder();
                    }
                }
            }
            catch { /* never let a stray window event take the app down */ }
        }

        /// <summary>
        /// Lift the fences if the desktop is shown above them. Called when the desktop comes to
        /// the front, and whenever a fence goes back to the desktop layer (shown again with
        /// Ctrl+Alt+H, a new fence, a fence clicked), since that can happen while the desktop is
        /// shown. The desktop becomes the foreground window a moment before Windows lifts it, so
        /// this looks a few times over the next second.
        /// </summary>
        public static void CheckSoon()
        {
            if (_hook == IntPtr.Zero || Lifted) return;
            var fg = GetForegroundWindow();
            if (!IsDesktop(fg) && !IsFence(fg)) return; // an app is in front: not "Show desktop"
            _checksLeft = 8;
            _check ??= CreateCheckTimer();
            _check.Stop();
            _check.Start();
            CheckLift();
        }

        private static bool IsFence(IntPtr hwnd) =>
            FenceWindow.AllFences.Any(f => new WindowInteropHelper(f).Handle == hwnd);

        private static System.Windows.Threading.DispatcherTimer? _check;
        private static int _checksLeft;

        private static System.Windows.Threading.DispatcherTimer CreateCheckTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
            timer.Tick += (_, __) =>
            {
                if (--_checksLeft <= 0 || Lifted) timer.Stop();
                CheckLift();
            };
            return timer;
        }

        private static void CheckLift()
        {
            var fg = GetForegroundWindow();
            if (Lifted || !(IsDesktop(fg) || IsFence(fg))) { _check?.Stop(); return; }
            var fences = FenceWindow.AllFences.Where(f => f.IsVisible)
                                              .Select(f => new WindowInteropHelper(f).Handle)
                                              .Where(h => h != IntPtr.Zero).ToList();
            if (fences.Count > 0 && DesktopIsAbove(fences))
            {
                Lift(fences);
                _check?.Stop();
            }
        }

        private static bool IsDesktop(IntPtr hwnd) => ClassOf(hwnd) is "Progman" or "WorkerW";

        /// <summary>Whether a desktop window sits above every fence: "Show desktop", not a plain
        /// click on the desktop (which leaves it at the bottom).</summary>
        internal static bool DesktopIsAbove(ICollection<IntPtr> fences)
        {
            for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (fences.Contains(h)) return false;
                if (IsWindowVisible(h) && IsDesktop(h)) return true;
            }
            return false;
        }

        /// <summary>Puts the fences above the shown desktop, keeping their order among themselves.</summary>
        public static void Lift(IList<IntPtr> fences)
        {
            Lifted = true;
            var order = ZOrder(fences);
            for (int i = order.Count - 1; i >= 0; i--) BringToTop(order[i]); // lowest first, so the highest ends up on top
        }

        /// <summary>One fence above the shown desktop, without activating it. Windows keeps the
        /// shown desktop above every normal window (the top of them isn't enough), so this is
        /// "always on top", only while the desktop is shown: every app is minimized then.</summary>
        public static void BringToTop(IntPtr hwnd) =>
            SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        private static List<IntPtr> ZOrder(ICollection<IntPtr> windows)
        {
            var list = new List<IntPtr>();
            for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && list.Count < windows.Count; h = GetWindow(h, GW_HWNDNEXT))
                if (windows.Contains(h)) list.Add(h);
            return list; // top first
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        // ----- P/Invoke -----
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint GW_HWNDNEXT = 2;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
                                               int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                                                     WinEventDelegate lpfnWinEventProc, uint idProcess,
                                                     uint idThread, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
