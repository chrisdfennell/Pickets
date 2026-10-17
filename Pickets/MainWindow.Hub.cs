using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pickets.Services;

using MessageBox = Pickets.ThemedMessageBox;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;

namespace Pickets
{
    // The main window's pages: Home (fence list + quick actions), Settings and About.
    public partial class MainWindow
    {
        /// <summary>One row in the Home page's fence list.</summary>
        public sealed class FenceRow
        {
            public FenceRow(FenceWindow window) => Window = window;
            public FenceWindow Window { get; }
            public string Name { get; init; } = "";
            public string Details { get; init; } = "";
            public Brush Swatch { get; init; } = Brushes.Gray;
            public string State { get; init; } = "";
            public Brush StateBrush { get; init; } = Brushes.Transparent;
            public string ShowLabel { get; init; } = "";
        }

        /// <summary>One saved layout on the Settings page.</summary>
        public sealed class LayoutRow
        {
            public string Title { get; init; } = "";
            public string Details { get; init; } = "";
            public object? Snapshot { get; init; }
        }

        private void InitHub()
        {
            // The switch shows the real state (the registry), even if config disagrees.
            _config.Options.RunAtStartup = _config.Options.RunAtStartup || StartupHelper.IsRunAtStartupEnabled();
            SettingsPage.DataContext = _config.Options;

            var version = "Version " + UpdateService.CurrentVersion.ToString(3) + (Sandbox.IsActive ? " · sandbox" : "");
            SidebarVersion.Text = version;
            VersionText.Text = version;
            AboutVersion.Text = version;

            StateChanged += (_, __) =>
            {
                // Maximized borderless windows overhang the screen slightly; keep content inside.
                if (Content is FrameworkElement root)
                    root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
                MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
            };

            RestoreWindowPlacement();
            Closing += (_, __) => RememberWindowPlacement();

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.F1) return;
                ShowKeyboardHelp();
                e.Handled = true;
            };
            FenceWindow.RequestKeyboardHelp = ShowKeyboardHelp;

            InitMonitorSettings();
            RefreshSettingsPage();
            RefreshHome();

            // Screen readers: name icon buttons and the switches in Settings (Services/Accessibility).
            Pickets.Services.Accessibility.NameControls(this, (Style)FindResource("Hub.SettingRow"));
        }

        // ---------- Window placement ----------
        // Reopen where it was (if that's still on a screen), at the same size and page.
        private void RestoreWindowPlacement()
        {
            var o = _config.Options;
            if (o.MainWidth is double w && o.MainHeight is double h && o.MainLeft is double l && o.MainTop is double t)
            {
                var r = ScreenLayout.FitOnScreen(new Rect(l, t, Math.Max(MinWidth, w), Math.Max(MinHeight, h)));
                WindowStartupLocation = WindowStartupLocation.Manual;
                DesktopHelper.PlaceWindow(this, r);
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (o.MainMaximized) WindowState = WindowState.Maximized;

            switch (o.MainPage)
            {
                case "Settings": NavSettings.IsChecked = true; break;
                case "About": NavAbout.IsChecked = true; break;
            }
        }

        private void RememberWindowPlacement()
        {
            var o = _config.Options;
            var r = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            o.MainLeft = r.Left; o.MainTop = r.Top; o.MainWidth = r.Width; o.MainHeight = r.Height;
            o.MainMaximized = WindowState == WindowState.Maximized;
            o.MainPage = NavSettings.IsChecked == true ? "Settings" : NavAbout.IsChecked == true ? "About" : "Home";
        }

        // ---------- Window buttons ----------
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized; // → tray
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        // The X asks; tray Exit and Alt+F4 still quit straight away.
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var choice = MessageBox.Show(this,
                "Minimize Pickets to the tray, or exit?\n\nYour fences only stay on the desktop while Pickets is running.",
                "Close Pickets", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes,
                new Dictionary<MessageBoxResult, string>
                {
                    [MessageBoxResult.Yes] = "Minimize to tray",
                    [MessageBoxResult.No] = "Exit",
                });
            if (choice == MessageBoxResult.Yes) WindowState = WindowState.Minimized; // → tray
            else if (choice == MessageBoxResult.No) Close();
        }

        // ---------- Navigation ----------
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (HomePage == null) return; // during InitializeComponent
            var page = (sender as FrameworkElement)?.Tag as string;
            HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
            PageScroller.ScrollToTop();

            if (page == "Home") RefreshHome();
            if (page == "Settings") RefreshSettingsPage();
        }

        // F1 (here or in a fence), or About → Keyboard shortcuts. One window, brought back if open.
        private KeyboardHelpWindow? _keyboardHelp;

        private void ShowKeyboardHelp()
        {
            if (_keyboardHelp != null) { _keyboardHelp.Activate(); return; }
            _keyboardHelp = new KeyboardHelpWindow(_config.Options);
            _keyboardHelp.Closed += (_, __) => _keyboardHelp = null;
            _keyboardHelp.Show();
            _keyboardHelp.Activate();
        }

        private void KeyboardHelp_Click(object sender, RoutedEventArgs e) => ShowKeyboardHelp();

        // About → Copy diagnostics: a redacted summary to paste into a bug report.
        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var log = (System.Windows.Application.Current as App)?.LogPath ?? "";
            try
            {
                System.Windows.Clipboard.SetText(Diagnostics.Build(_config, log));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The diagnostics couldn't be copied:\n" + ex.Message, "Pickets",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show(this, "Diagnostics copied. Paste them into your bug report.\n\n" +
                                  "Your user name and PC name are replaced, and fence and file names aren't included, " +
                                  "but have a quick look before you share it.",
                            "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Link_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string url) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
        }

        // ---------- Home ----------
        private bool _homeRefreshQueued;

        // Many changes save the config; refresh the list once things settle.
        private void QueueHomeRefresh()
        {
            if (_homeRefreshQueued || FenceList == null) return;
            _homeRefreshQueued = true;
            Dispatcher.BeginInvoke(() => { _homeRefreshQueued = false; RefreshHome(); },
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshHome()
        {
            if (FenceList == null) return;

            // From the current palette (light or dark); the list is rebuilt when the theme changes.
            var defaultSwatch = (Brush)FindResource("Hub.Swatch");
            var shownBrush = (Brush)FindResource("Hub.StateShown");
            var closedBrush = (Brush)FindResource("Hub.StateClosed");
            var rolledBrush = (Brush)FindResource("Hub.StateRolled");

            var rows = _openWindows
                .OrderBy(w => w.IsClosedByUser)
                .ThenBy(w => w.FenceName, StringComparer.CurrentCultureIgnoreCase)
                .Select(w =>
                {
                    string state; Brush stateBrush;
                    if (w.IsClosedByUser) { state = "Closed"; stateBrush = closedBrush; }
                    else if (_peeked) { state = "Hidden"; stateBrush = closedBrush; }
                    else if (w.IsCollapsed) { state = "Rolled up"; stateBrush = rolledBrush; }
                    else { state = "Shown"; stateBrush = shownBrush; }

                    Brush swatch = defaultSwatch;
                    if (!string.IsNullOrEmpty(w.AccentColor))
                    {
                        try { swatch = (Brush)new BrushConverter().ConvertFromString(w.AccentColor)!; } catch { /* keep default */ }
                    }

                    int n = w.ItemCount;
                    string items = n == 1 ? "1 item" : $"{n} items";
                    return new FenceRow(w)
                    {
                        Name = w.FenceName,
                        Details = w.IsPortal ? $"Folder portal · {items} · {w.FolderPath}"
                                : w.IsMonitor ? "System monitor · " + w.MonitorSummary
                                : w.TabCount > 0 ? $"{items} · {w.TabCount} tabs" : items,
                        Swatch = swatch,
                        State = state,
                        StateBrush = stateBrush,
                        ShowLabel = w.IsClosedByUser ? "Open" : "Close"
                    };
                })
                .ToList();
            FenceList.ItemsSource = rows;

            int fences = rows.Count;
            int totalItems = RealFences.Sum(w => w.ItemCount);
            int closed = _openWindows.Count(w => w.IsClosedByUser);
            SummaryText.Text = (fences == 1 ? "1 fence" : $"{fences} fences") +
                               $" · {totalItems} desktop items" +
                               (closed > 0 ? $" · {closed} closed" : "");

            ToggleAllButton.Content = _peeked ? "Show all fences" : "Hide all fences";
            RefreshProfileButton();
            SearchTileHint.Text = _config.Options.GlobalHotkeys
                ? $"Find any item ({HotkeyText.NoBreak(_config.Options.SearchHotkey)})"
                : "Find any item in any fence";
        }

        private void ToggleAllFences_Click(object sender, RoutedEventArgs e)
        {
            TogglePeek();
            RefreshHome();
        }

        private static FenceWindow? RowWindow(object sender) => ((sender as FrameworkElement)?.Tag as FenceRow)?.Window;

        private void FenceRow_ShowClose_Click(object sender, RoutedEventArgs e)
        {
            if (RowWindow(sender) is not { } w) return;
            if (w.IsClosedByUser) w.Reopen();
            else w.CloseFence();
            SaveConfig();
        }

        private void FenceRow_Locate_Click(object sender, RoutedEventArgs e)
        {
            if (RowWindow(sender) is not { } w) return;
            if (w.IsClosedByUser) { w.Reopen(); SaveConfig(); }
            w.Locate();
        }

        private void FenceRow_Delete_Click(object sender, RoutedEventArgs e)
        {
            RowWindow(sender)?.RequestDelete();
            RefreshHome();
        }

        // ---------- Settings ----------
        private bool _lastShowThumbnails, _lastHideExtensions;

        private void RefreshSettingsPage()
        {
            if (SettingsPage == null) return;
            // Re-bind so every switch shows the current values.
            SettingsPage.DataContext = null;
            SettingsPage.DataContext = _config.Options;
            _lastShowThumbnails = _config.Options.ShowThumbnails;
            _lastHideExtensions = _config.Options.HideFileExtensions;

            SearchHotkeyBox.Text = _config.Options.SearchHotkey;
            ToggleHotkeyBox.Text = _config.Options.ToggleFencesHotkey;
            FrontHotkeyBox.Text = _config.Options.FrontHotkey;
            ProfileHotkeyBox.Text = _config.Options.ProfileHotkey;
            ThemeButton.Content = ThemeLabel(_config.Options.Theme) + "  ▾";
            UpdateHotkeyStatus();
            RefreshLayouts();
            RefreshMonitorSettings();
        }

        private static string ThemeLabel(AppTheme t) => t switch
        {
            AppTheme.Light => "Light",
            AppTheme.Dark => "Dark",
            _ => "System",
        };

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var t in new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark })
            {
                var item = new MenuItem { Header = ThemeLabel(t), IsCheckable = true, IsChecked = t == _config.Options.Theme };
                item.Click += (_, __) =>
                {
                    _config.Options.Theme = t;
                    Theme.Apply(t);
                    ThemeButton.Content = ThemeLabel(t) + "  ▾";
                    SaveConfig();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // Every switch on the Settings page: the binding has already updated the option.
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            var o = _config.Options;

            StartupHelper.SetRunAtStartup(o.RunAtStartup);
            if (!o.DoubleClickPeekFences && _peeked) SetFencesHidden(false); // un-peek when turning it off
            ApplyDoubleClickSetting();
            ApplyAutoOrganizeSetting();
            ApplyHotkeySetting();
            UpdateHotkeyStatus();

            if (o.ShowThumbnails != _lastShowThumbnails || o.HideFileExtensions != _lastHideExtensions)
            {
                _lastShowThumbnails = o.ShowThumbnails;
                _lastHideExtensions = o.HideFileExtensions;
                foreach (var w in _openWindows) w.RefreshItems();
            }
            SystemMonitor.PublicIpEnabled = o.MonitorPublicIp;
            ApplyMonitorOptionsToAll(); // units, warning colors, public IP

            SaveConfig();
        }

        private void UpdateHotkeyStatus()
        {
            bool problem = _config.Options.GlobalHotkeys && _hotkeyProblems.Count > 0;
            HotkeyStatus.Text = string.Join("  ", _hotkeyProblems) + (problem ? " Pick a different shortcut." : "");
            HotkeyStatus.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;
            SearchHotkeyBox.IsEnabled = ToggleHotkeyBox.IsEnabled = FrontHotkeyBox.IsEnabled =
                ProfileHotkeyBox.IsEnabled = _config.Options.GlobalHotkeys;
        }

        // Which option a shortcut box edits (its Tag).
        private string HotkeyFor(TextBox box) => box.Tag switch
        {
            "Search" => _config.Options.SearchHotkey,
            "Front" => _config.Options.FrontHotkey,
            "Profile" => _config.Options.ProfileHotkey,
            _ => _config.Options.ToggleFencesHotkey,
        };

        // ---- Shortcut recorder: click the box, press the new combination (Esc cancels) ----
        private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box) box.Text = "Press a shortcut…";
        }

        private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox box) return;
            box.Text = HotkeyFor(box);
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }

            var text = HotkeyText.Format(Keyboard.Modifiers, key);
            if (text == null) return; // still holding modifiers, or no modifier yet

            switch (box.Tag)
            {
                case "Search": _config.Options.SearchHotkey = text; break;
                case "Front": _config.Options.FrontHotkey = text; break;
                case "Profile": _config.Options.ProfileHotkey = text; break;
                default: _config.Options.ToggleFencesHotkey = text; break;
            }

            ApplyHotkeySetting();
            UpdateHotkeyStatus();
            SaveConfig();
            Keyboard.ClearFocus(); // shows the saved shortcut
        }

        // ---- Layouts ----
        private void RefreshLayouts()
        {
            var rows = LayoutSnapshots.List().Select(s => new LayoutRow
            {
                Title = s.Name,
                Details = (s.Automatic ? "Saved automatically · " : "") +
                          $"{s.Created:MMM d, yyyy h:mm tt} · {s.Fences.Count} fences",
                Snapshot = s
            }).ToList();
            LayoutList.ItemsSource = rows;
            NoLayoutsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveLayout_Click(object sender, RoutedEventArgs e)
        {
            SaveLayoutSnapshot();
            RefreshLayouts();
        }

        private void LayoutRestore_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is LayoutSnapshots.Snapshot snap)
            {
                RestoreLayoutSnapshot(snap);
                RefreshLayouts();
            }
        }

        private void LayoutDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not LayoutSnapshots.Snapshot snap) return;
            if (MessageBox.Show($"Delete the saved layout “{snap.Name}”?", "Layouts",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            LayoutSnapshots.Delete(snap);
            RefreshLayouts();
        }

        private void ExportLayout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export your Pickets layout",
                FileName = $"Pickets layout {DateTime.Now:yyyy-MM-dd}",
                DefaultExt = ".json",
                Filter = "Pickets layout|*.json"
            };
            if (dlg.ShowDialog(this) != true) return;
            try { LayoutSnapshots.Export(dlg.FileName, _fences, _config.Rules); }
            catch (Exception ex)
            {
                MessageBox.Show("The layout couldn't be exported:\n" + ex.Message, "Pickets",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportLayout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a Pickets layout",
                Filter = "Pickets layout|*.json|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            LayoutSnapshots.Snapshot snap;
            try { snap = LayoutSnapshots.Import(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show("That file couldn't be imported:\n" + ex.Message, "Pickets",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (snap.NetworkFolders.Count > 0)
            {
                var keep = MessageBox.Show(
                    "This layout has folder portals that show folders on other computers:\n\n" +
                    string.Join("\n", snap.NetworkFolders.Distinct(StringComparer.OrdinalIgnoreCase).Take(8)) +
                    "\n\nOpening them connects to those computers. Only keep them if you know them.\n\nKeep these portals?",
                    "Import Layout", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
                if (keep == MessageBoxResult.Cancel) return;
                if (keep == MessageBoxResult.No) LayoutSnapshots.RemoveNetworkPortals(snap);
            }
            RestoreLayoutSnapshot(snap);
            RefreshLayouts();
        }

        private void OpenLayoutsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(LayoutSnapshots.Folder);
                Process.Start(new ProcessStartInfo { FileName = LayoutSnapshots.Folder, UseShellExecute = true });
            }
            catch { /* ignore */ }
        }
    }
}
