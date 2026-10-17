using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using Pickets.Services;
using Xunit;

namespace Pickets.UiTests
{
    /// <summary>Real fence windows on screen: stacks, fitting, portals and what screen readers get.</summary>
    public class FenceWindowTests
    {
        private static FenceWindow Open(FenceModel model)
        {
            var w = new FenceWindow(model);
            w.Show();
            return w;
        }

        private static FenceModel Fence(string name, double left, double top, double width, double height, bool collapsed = false) =>
            new() { Name = name, Left = left, Top = top, Width = width, Height = height, Collapsed = collapsed };

        private static Rect Px(Window w) => DesktopHelper.WindowRectPx(new WindowInteropHelper(w).Handle);

        private static double Scale(Window w) => System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleY;

        private static void Settle() => Ui.Pump(700); // placement, the 160 ms roll-up animation, saves

        [Fact]
        public void Rolling_up_a_fence_pulls_the_fences_stacked_below_it_up()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions { MoveStacksTogether = true };
                var top = Open(Fence("Top", 100, 100, 300, 200));
                var middle = Open(Fence("Middle", 100, 308, 300, 150));   // snapped with the 8 px gap
                var bottom = Open(Fence("Bottom", 100, 466, 300, 120));
                var apart = Open(Fence("Apart", 600, 308, 300, 150));     // not in the stack
                Settle();
                var before = new[] { Px(middle), Px(bottom), Px(apart) };
                double shift = (44 - 200) * Scale(top);

                top.ToggleCollapsed();
                Settle();

                Assert.True(top.IsCollapsed);
                Assert.InRange(Px(middle).Top - before[0].Top, shift - 2, shift + 2);
                Assert.InRange(Px(bottom).Top - before[1].Top, shift - 2, shift + 2);
                Assert.Equal(before[2].Top, Px(apart).Top);

                top.ToggleCollapsed(); // and back down again
                Settle();
                Assert.InRange(Px(middle).Top, before[0].Top - 2, before[0].Top + 2);
            });
        }

        [Fact]
        public void With_one_open_per_stack_opening_a_fence_rolls_up_the_others()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions { MoveStacksTogether = true, StackOneOpen = true };
                var upper = Open(Fence("Upper", 100, 100, 300, 200));
                var lower = Open(Fence("Lower", 100, 308, 300, 180, collapsed: true));
                Settle();

                lower.ToggleCollapsed();
                Settle();

                Assert.True(upper.IsCollapsed);
                Assert.False(lower.IsCollapsed);
                // Still stacked: the lower fence moved up under the rolled-up one.
                Assert.True(FenceStacks.Touches(Px(upper), Px(lower), FenceSnapper.GapDip * Scale(upper)),
                            $"upper {Px(upper)}, lower {Px(lower)}");
            });
        }

        [Fact]
        public void Dragging_a_title_bar_moves_just_that_fence_by_default()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions { MoveStacksTogether = true };
                var (dx, dy, below, apart) = DragTopOfStack();
                Assert.NotEqual(0, dx);
                Assert.Equal(below.Start, below.End);
                Assert.Equal(apart.Start, apart.End);
            });
        }

        [Fact]
        public void With_drag_the_whole_stack_a_title_bar_drag_moves_the_stack_but_not_other_fences()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions { MoveStacksTogether = true, DragStacksTogether = true };
                var (dx, dy, below, apart) = DragTopOfStack();
                Assert.NotEqual(0, dx);
                Assert.Equal(below.Start.Left + dx, below.End.Left);
                Assert.Equal(below.Start.Top + dy, below.End.Top);
                Assert.Equal(apart.Start, apart.End);
            });
        }

        /// <summary>Drags the top fence of a two-fence stack (with a third fence apart) by its
        /// title bar: how far it moved, and where the other two were before and after.</summary>
        private static (int Dx, int Dy, (Rect Start, Rect End) Below, (Rect Start, Rect End) Apart) DragTopOfStack()
        {
            var top = Open(Fence("Top", 100, 100, 300, 200));
            var below = Open(Fence("Below", 100, 300, 300, 150));      // touching
            var apart = Open(Fence("Apart", 600, 100, 300, 150));
            Settle();
            var start = Px(top);
            var belowStart = Px(below);
            var apartStart = Px(apart);

            // What Windows sends during a title-bar drag.
            var hwnd = new WindowInteropHelper(top).Handle;
            SendMessage(hwnd, 0x0231 /*WM_ENTERSIZEMOVE*/, IntPtr.Zero, IntPtr.Zero);
            var proposed = new RECT { Left = (int)start.Left + 60, Top = (int)start.Top + 40, Right = (int)start.Right + 60, Bottom = (int)start.Bottom + 40 };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
            try
            {
                Marshal.StructureToPtr(proposed, ptr, false);
                SendMessage(hwnd, 0x0216 /*WM_MOVING*/, IntPtr.Zero, ptr);
                proposed = Marshal.PtrToStructure<RECT>(ptr); // after snapping
            }
            finally { Marshal.FreeHGlobal(ptr); }
            SendMessage(hwnd, 0x0232 /*WM_EXITSIZEMOVE*/, IntPtr.Zero, IntPtr.Zero);
            Ui.Pump(200);

            return (proposed.Left - (int)start.Left, proposed.Top - (int)start.Top,
                    (belowStart, Px(below)), (apartStart, Px(apart)));
        }

        [Fact]
        public void Keep_fitted_to_contents_follows_the_items()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions();
                var dir = Ui.TempFolder();
                var model = Fence("Fitted", 100, 100, 320, 500);
                model.AutoHeight = true;
                var fence = Open(model);
                Settle();
                double empty = fence.ActualHeight;
                Assert.Equal(120, empty, 0); // the smallest a fence gets

                var files = Enumerable.Range(1, 12).Select(i => Path.Combine(dir, $"File {i}.txt")).ToList();
                foreach (var f in files) File.WriteAllText(f, "x");
                fence.AddItems(files);
                Settle();

                var scroller = (ScrollViewer)fence.FindName("Scroller");
                Assert.True(fence.ActualHeight > empty);
                Assert.Equal(0, scroller.ScrollableHeight, 0);
            });
        }

        [Fact]
        public void An_unreachable_portal_comes_back_by_itself()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions();
                FenceWindow.ReconnectIntervalMs = 200;
                var folder = Path.Combine(Ui.TempFolder(), "Share");
                var model = Fence("Share", 100, 100, 320, 240);
                model.IsPortal = true;
                model.FolderPath = folder;
                var portal = Open(model);
                var title = (TextBlock)portal.FindName("TitleText");
                Settle();
                Assert.EndsWith("(unavailable)", title.Text);

                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "Report.txt"), "x");

                Assert.True(Ui.WaitUntil(() => !title.Text.EndsWith("(unavailable)") && portal.ItemsSource.Count == 1), title.Text);
            });
        }

        [Fact]
        public void Screen_readers_see_a_named_list_of_named_tiles()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions();
                var dir = Ui.TempFolder();
                var files = new[] { "Budget.xlsx", "Notes.txt" }.Select(n => Path.Combine(dir, n)).ToList();
                foreach (var f in files) File.WriteAllText(f, "x");
                var model = Fence("Work", 100, 100, 400, 240);
                model.ItemPaths = files;
                var fence = Open(model);
                Settle();

                Assert.Equal("Work fence", fence.Title);
                var list = UIElementAutomationPeer.CreatePeerForElement((UIElement)fence.FindName("Items"));
                Assert.Equal(AutomationControlType.List, list.GetAutomationControlType());
                Assert.Equal("Work", list.GetName());

                var tiles = list.GetChildren();
                Assert.Equal(new[] { "Budget.xlsx", "Notes.txt" }, tiles.Select(t => t.GetName()));
                Assert.All(tiles, t => Assert.Equal(AutomationControlType.ListItem, t.GetAutomationControlType()));

                // Selecting through UI Automation selects the item.
                var second = (ISelectionItemProvider)tiles[1].GetPattern(PatternInterface.SelectionItem);
                second.Select();
                Assert.True(fence.ItemsSource[1].IsSelected);
                Assert.False(fence.ItemsSource[0].IsSelected);
            });
        }

        [Fact]
        public void Settings_switches_and_icon_buttons_get_readable_names()
        {
            Ui.Run(() =>
            {
                var rowStyle = new Style(typeof(DockPanel));
                var toggle = new System.Windows.Controls.CheckBox();
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = "Snap to a grid" });
                text.Children.Add(new TextBlock { Text = "Line fences up on a 20-pixel grid." });
                var row = new DockPanel { Style = rowStyle };
                row.Children.Add(toggle);
                row.Children.Add(text);
                var glyphButton = new System.Windows.Controls.Button { Content = "", ToolTip = "Delete fence…" };
                var labelled = new System.Windows.Controls.Button { Content = new StackPanel { Children = { new TextBlock { Text = "" }, new TextBlock { Text = "Hide to tray" } } } };
                var root = new StackPanel { Children = { row, glyphButton, labelled } };

                Pickets.Services.Accessibility.NameControls(root, rowStyle);

                Assert.Equal("Snap to a grid", AutomationProperties.GetName(toggle));
                Assert.Equal("Line fences up on a 20-pixel grid.", AutomationProperties.GetHelpText(toggle));
                Assert.Equal("Delete fence…", AutomationProperties.GetName(glyphButton));
                Assert.Equal("Hide to tray", AutomationProperties.GetName(labelled));
            });
        }

        [Fact]
        public void Alt_F4_closes_a_fence_like_its_X_instead_of_destroying_it()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions();
                var model = Fence("Work", 100, 100, 300, 200);
                var fence = Open(model);
                Settle();

                // Alt+F4 arrives as the window menu's Close.
                SendMessage(new WindowInteropHelper(fence).Handle, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF060) /*SC_CLOSE*/, IntPtr.Zero);
                Settle();

                Assert.True(model.Closed);             // closed like ✕: kept, and reopenable
                Assert.False(fence.IsVisible);
                Assert.Contains(fence, FenceWindow.AllFences); // the window wasn't destroyed
            });
        }

        [Fact]
        public void Selecting_in_one_fence_clears_the_selection_in_the_others_and_closing_clears_it_too()
        {
            Ui.Run(() =>
            {
                FenceWindow.Options = new AppOptions();
                var dir = Ui.TempFolder();
                string Make(string name) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); return p; }
                var a = Open(Fence("A", 100, 100, 300, 200));
                var b = Open(Fence("B", 500, 100, 300, 200));
                a.AddItems(new[] { Make("one.txt"), Make("two.txt") });
                b.AddItems(new[] { Make("three.txt") });
                Settle();

                var selectOnly = typeof(FenceWindow).GetMethod("SelectOnly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                selectOnly.Invoke(a, new object?[] { a.ItemsSource[0] });   // a click in A
                Assert.Equal(1, a.SelectedCount);
                selectOnly.Invoke(b, new object?[] { b.ItemsSource[0] });   // then a click in B
                Assert.Equal(0, a.SelectedCount); // Delete in B now can't take A's item along
                Assert.Equal(1, b.SelectedCount);

                b.CloseFence();
                Assert.Equal(0, b.SelectedCount);
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
