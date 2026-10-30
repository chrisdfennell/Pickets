using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pickets.Services;

// Aliases to avoid WinForms/WPF ambiguity
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using MessageBox = Pickets.ThemedMessageBox;

namespace Pickets
{
    public partial class MainWindow : Window
    {
        // ---------- Paths & state ----------
        private readonly string _configPath = Path.Combine(Sandbox.DataDir, "config.json");

        private AppConfig _config = new();                // holds Fences + Options
        private List<FenceModel> _fences => _config.Fences;

        private readonly List<FenceWindow> _openWindows = new();

        // Tray
        private WinForms.NotifyIcon? _tray;
        private WinForms.ContextMenuStrip? _trayMenu;

        public MainWindow()
        {
            InitializeComponent();

            // Desktop host handles (WorkerW/Progman/DefView)
            DesktopHelper.InitializeDesktopHandles();

            // Load or create config
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            bool firstRun = !File.Exists(_configPath) && !File.Exists(BackupConfigPath);
            LoadConfig();

            // Migrate legacy name-based ownership (IconNames) → path-based (ItemPaths).
            MigrateLegacyOwnership();

            // Older versions could leave duplicate fences (e.g. several "Desktop" fences).
            RepairDuplicateFences();

            // Fences read app-wide options (snapping, hover roll-up) live.
            FenceWindow.Options = _config.Options;

            // Light/dark before any fence is built. Fences paint some colors in code, so they
            // repaint when the theme (or Windows' app mode, under "System") changes.
            Theme.Apply(_config.Options.Theme);
            Theme.Changed += () =>
            {
                foreach (var w in _openWindows) w.ApplyTheme();
                RefreshHome();
            };

            // Apply settings effects at startup
            if (_config.Options.RunAtStartup || StartupHelper.IsRunAtStartupEnabled())
                StartupHelper.SetRunAtStartup(true);

            TakenOver = DesktopTakeover.IsTakenOver(_config.Options.DesktopTakenOver, _fences);
            if (TakenOver && _config.Options.HideIconsOnStartup)
                DesktopHelper.SetDesktopIconsVisible(false);

            // Double-click-empty-desktop behavior (toggle icons and/or peek fences)
            ApplyDoubleClickSetting();

            // Continuous auto-organize of new desktop items
            ApplyAutoOrganizeSetting();

            // Cross-fence drops route through here so an item leaves any prior fence.
            FenceWindow.RequestAssignItems = (target, paths) =>
            {
                Undoable(FenceWindow.MoveDescription(paths.Count, $"“{target.FenceName}”"), () => AssignItemsToFence(target, paths));
                TakeOverIfNeeded(); // the first items dragged into a fence
            };

            // Ctrl+Z in a fence undoes the last organizing action (MainWindow.Undo.cs).
            FenceWindow.RequestUndoable = Undoable;
            FenceWindow.RequestUndo = Undo;

            // Spawn fence windows from config
            SpawnFencesFromConfig();

            // Once Pickets has taken over (Services/DesktopTakeover): sweep any desktop items not
            // yet owned into the catch-all "Desktop" fence, then hide the real desktop icons —
            // everything is rendered inside fences. Until then the desktop is left alone.
            if (TakenOver)
            {
                BuildCatchAll();
                DesktopHelper.SetDesktopIconsVisible(false);
            }
            StartDesktopWatcher();

            // Tray + minimize-to-tray behavior
            StateChanged += MainWindow_StateChanged;
            InitTrayIcon();

            // Desktop profiles that switch at a time of day
            StartProfileSchedule();

            // Right-click-drag rectangle to create an (empty) fence or a folder portal there
            if (!Sandbox.IsActive) DesktopRightDragFenceSelector.Start(CreateFenceFromRect, r => CreateFolderPortal(r), r => CreateSystemMonitor(r));

            // Delete/Enter operate on the whole selection across all fences
            FenceWindow.RequestDeleteSelected = DeleteAllSelected;
            FenceWindow.RequestOpenSelected = OpenAllSelected;
            FenceWindow.RequestRemoveSelected = RemoveSelectedFromFences;
            FenceWindow.RequestNewFenceWithItems = CreateFenceWithItems;

            // Left-drag on the empty desktop = lasso that selects items across fences
            if (!Sandbox.IsActive) DesktopLeftDragLasso.Start(OnLassoUpdate, OnLassoEnd);

            // GitHub release checks (startup + daily, if enabled in Settings)
            StartUpdateChecks();

            // Docking/undocking, resolution or scaling changes: put fences back on screen
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // System-wide shortcuts (Ctrl+Alt+H hide/show fences, …)
            ApplyHotkeySetting();

            // Fences stay visible on "Show desktop" (Win+D)
            ShowDesktopWatcher.Start();

            // Main window: Home (fence list), Settings, About.
            InitHub();

            // Brand-new install: explain the basics once fences are on screen.
            if (firstRun)
                Dispatcher.BeginInvoke(ShowWelcome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ========== Welcome tour ==========
        private void ShowWelcome()
        {
            var dlg = new WelcomeDialog();
            if (IsVisible) { dlg.Owner = this; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            dlg.ShowDialog();
            if (dlg.OrganizeRequested) AutoImportDesktop_Click(null, null);
        }

        private void Welcome_Click(object? sender, RoutedEventArgs? e) => ShowWelcome();

        // ========== Global hotkeys ==========
        private GlobalHotkeys? _hotkeys;
        private readonly List<string> _hotkeyProblems = new();

        // A shortcut another app already owns can't be registered; remember it so Settings can say so.
        private void RegisterHotkey(string text, Action action)
        {
            if (!HotkeyText.TryParse(text, out var mods, out var vk))
            {
                _hotkeyProblems.Add($"“{text}” isn't a valid shortcut.");
                return;
            }
            if (!_hotkeys!.Register(mods, vk, action))
            {
                _hotkeyProblems.Add($"{text} is already used by another app.");
                (System.Windows.Application.Current as App)?.SafeLog("Hotkey " + text, new InvalidOperationException("Already in use by another app."));
            }
        }

        private void ApplyHotkeySetting()
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            if (!_config.Options.GlobalHotkeys || Sandbox.IsActive) return; // the real Pickets owns them

            try
            {
                _hotkeys = new GlobalHotkeys(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle());
                _hotkeyProblems.Clear();
                RegisterHotkey(_config.Options.ToggleFencesHotkey, TogglePeek);
                RegisterHotkey(_config.Options.SearchHotkey, OpenSearch);
                RegisterHotkey(_config.Options.FrontHotkey, ToggleFencesInFront);
                RegisterHotkey(_config.Options.ProfileHotkey, NextProfile);
            }
            catch (Exception ex)
            {
                (System.Windows.Application.Current as App)?.SafeLog("Hotkeys", ex);
            }
        }

        // ========== Search across fences ==========
        private SearchWindow? _search;

        private void OpenSearch()
        {
            if (_search != null) { _search.Activate(); return; }
            if (_openWindows.Count == 0) return;

            _search = new SearchWindow(_openWindows);
            _search.Closed += (_, __) => _search = null;
            _search.Show();
        }

        private void Search_Click(object? sender, RoutedEventArgs? e) => OpenSearch();

        // ========== Fences in front of app windows ==========
        // Hidden fences (Ctrl+Alt+H) come out for the occasion and go away again afterwards.
        private void ToggleFencesInFront()
        {
            if (FencesInFront.IsActive) { FencesInFront.Exit(); return; }

            bool wasHidden = _peeked;
            if (wasHidden) SetFencesHidden(false);
            FencesInFront.Enter(() =>
            {
                if (wasHidden) SetFencesHidden(true);
                RefreshHome();
            });
        }

        // ========== Monitor arrangement changes ==========
        private System.Windows.Threading.DispatcherTimer? _displayTimer;

        // Windows raises several of these while monitors settle, so wait for a quiet moment.
        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_displayTimer == null)
                {
                    _displayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    _displayTimer.Tick += (_, __) =>
                    {
                        _displayTimer.Stop();
                        bool moved = false;
                        foreach (var w in _openWindows.ToList())
                            if (w.FitToCurrentScreens()) moved = true;
                        if (moved) SaveConfig();
                    };
                }
                _displayTimer.Stop();
                _displayTimer.Start();
            });
        }

        // ========== Cross-fence selection (lasso + bulk actions) ==========
        private void OnLassoUpdate(Rect lassoPx, bool additive)
        {
            foreach (var w in _openWindows)
                if (w.IsVisible) w.SelectItemsInScreenRectPx(lassoPx, additive);
        }

        private void OnLassoEnd()
        {
            // Selection stays highlighted. Act on it via Delete/Enter (with a fence focused)
            // or an item's right-click → Delete, which both route through the global handlers.
        }

        // Fences on screen: a closed or hidden fence's selection never counts.
        private IEnumerable<FenceWindow> ShownWindows => _openWindows.Where(w => w.IsVisible);

        private void DeleteAllSelected()
        {
            int total = ShownWindows.Sum(w => w.SelectedCount);
            if (total == 0) return;

            if (!FenceWindow.ConfirmDelete(total, offerRemove: ShownWindows.Any(w => w.CanRemoveSelected)))
                return;

            var failed = new List<string>();
            foreach (var w in ShownWindows.ToList()) failed.AddRange(w.DeleteSelectedItemsNoConfirm());
            FenceWindow.ReportDeleteFailures(failed);
        }

        // "Remove from fence": hand the selected items back to the catch-all "Desktop" fence.
        // Nothing is deleted. Portals mirror a real folder, so they're left alone.
        private void RemoveSelectedFromFences()
        {
            var paths = RealFences.Where(w => w.IsVisible)
                .Where(w => !IsCatchAll(w))
                .SelectMany(w => w.SelectedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) return;

            Undoable("Remove from fence", () => AssignItemsToFence(EnsureRealFence(CatchAllFenceName), paths));
        }

        private static bool IsCatchAll(FenceWindow w) =>
            !w.IsPortal && !w.IsMonitor && string.Equals(w.FenceName, CatchAllFenceName, StringComparison.OrdinalIgnoreCase);

        private void OpenAllSelected()
        {
            foreach (var w in ShownWindows.ToList()) w.OpenSelectedItems();
        }

        private void CheckForUpdatesNow_Click(object? sender, RoutedEventArgs? e) => _ = CheckForUpdatesAsync(manual: true);

        // ========== Updates ==========
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private bool _updateDialogOpen;

        // First check shortly after launch (so startup isn't slowed), then once a day.
        private void StartUpdateChecks()
        {
            if (Sandbox.IsActive) return;
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _updateTimer.Tick += (_, __) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(24);
                if (_config.Options.CheckForUpdates) _ = CheckForUpdatesAsync(manual: false);
            };
            _updateTimer.Start();
        }

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (_updateDialogOpen) return;

            UpdateInfo? update;
            try
            {
                update = await UpdateService.CheckAsync();
            }
            catch (Exception ex)
            {
                // Automatic checks fail quietly (offline, rate-limited…); a manual check says why.
                (System.Windows.Application.Current as App)?.SafeLog("Update check", ex);
                if (manual)
                    MessageBox.Show("Couldn't check for updates:\n" + ex.Message,
                                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (update == null)
            {
                if (manual)
                    MessageBox.Show($"You're up to date (version {UpdateService.CurrentVersion.ToString(3)}).",
                                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Automatic checks respect "Skip this version"; a manual check always offers it.
            if (!manual && string.Equals(_config.Options.SkippedVersion, update.Version.ToString(3), StringComparison.Ordinal))
                return;

            _updateDialogOpen = true;
            UpdateDialog dlg;
            try
            {
                dlg = new UpdateDialog(update);
                if (IsVisible) { dlg.Owner = this; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                dlg.ShowDialog();
            }
            finally { _updateDialogOpen = false; }

            switch (dlg.Choice)
            {
                case UpdateDialog.Result.Skip:
                    _config.Options.SkippedVersion = update.Version.ToString(3);
                    SaveConfig();
                    break;

                case UpdateDialog.Result.ReadyToInstall when dlg.MsiPath != null:
                    try
                    {
                        UpdateService.LaunchInstallerAndRestart(dlg.MsiPath);
                    }
                    catch (Exception ex)
                    {
                        (System.Windows.Application.Current as App)?.SafeLog("Update install", ex);
                        MessageBox.Show("The update couldn't be started:\n" + ex.Message,
                                        "Pickets", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    // Exit normally so the config is saved and the desktop icons come back; the
                    // helper installs once we're gone and then starts the new version.
                    Close();
                    break;
            }
        }


        // ========== Tray ==========
        private void InitTrayIcon()
        {
            _trayMenu = new WinForms.ContextMenuStrip();
            // Follows the app theme (dark/light), including later switches.
            TrayMenuTheme.Apply(_trayMenu, Theme.IsLight);
            Theme.Changed += () => { if (_trayMenu != null) TrayMenuTheme.Apply(_trayMenu, Theme.IsLight); };

            var restore = new WinForms.ToolStripMenuItem("Restore Pickets", null, (_, __) => RestoreFromTray());
            var undo = new WinForms.ToolStripMenuItem("Undo", null, (_, __) => Undo());
            _trayMenu.Opening += (_, __) => { undo.Text = UndoMenuText; undo.Enabled = _undo.Count > 0; };
            var newFence = new WinForms.ToolStripMenuItem("New Fence", null, (_, __) => NewFence_Click(null!, null!));
            var newPortal = new WinForms.ToolStripMenuItem("New Folder Portal…", null, (_, __) => NewFolderPortal_Click(null!, null!));
            var newMonitor = new WinForms.ToolStripMenuItem("New System Monitor", null, (_, __) => NewSystemMonitor_Click(null, null));
            var search = new WinForms.ToolStripMenuItem("Search Fences…", null, (_, __) => OpenSearch());
            var showAll = new WinForms.ToolStripMenuItem("Show All Fences", null, (_, __) => ShowAll_Click(null!, null!));
            var deleteAll = new WinForms.ToolStripMenuItem("Delete All Fences…", null, (_, __) => DeleteAllFences_Click(null, null));
            var hideAll = new WinForms.ToolStripMenuItem("Hide All Fences", null, (_, __) => HideAll_Click(null!, null!));
            var toggle = new WinForms.ToolStripMenuItem("Toggle Desktop Icons", null, (_, __) => ToggleDesktopIcons_Click(null!, null!));
            var updates = new WinForms.ToolStripMenuItem("Check for Updates…", null, (_, __) => CheckForUpdatesNow_Click(null, null));
            var exit = new WinForms.ToolStripMenuItem("Exit", null, (_, __) => Close());

            _trayMenu.Items.Add(restore);
            _trayMenu.Items.Add(undo);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(newFence);
            _trayMenu.Items.Add(newPortal);
            _trayMenu.Items.Add(newMonitor);
            _trayMenu.Items.Add(search);
            _trayMenu.Items.Add(showAll);
            _trayMenu.Items.Add(hideAll);
            _trayMenu.Items.Add(toggle);
            _trayMenu.Items.Add(deleteAll);
            _trayMenu.Items.Add(BuildTrayProfilesMenu());
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            _trayMenu.Items.Add(updates);
            _trayMenu.Items.Add(exit);

            _tray = new WinForms.NotifyIcon
            {
                Text = "Pickets",
                Icon = LoadAppIconOrFallback(),
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
            _tray.DoubleClick += (_, __) => RestoreFromTray();
        }

        // Ask for the small-icon size so the tray gets the crisp 16px (or DPI-scaled) frame.
        // Try the .ico shipped next to the exe, then the embedded resource, then the exe's own
        // icon; failures are logged rather than silently showing the generic app icon.
        private static Drawing.Icon LoadAppIconOrFallback()
        {
            var size = WinForms.SystemInformation.SmallIconSize;
            var log = (System.Windows.Application.Current as App);

            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Assets", "open-fence.ico");
                if (File.Exists(file)) return new Drawing.Icon(file, size);
            }
            catch (Exception ex) { log?.SafeLog("Tray icon (file)", ex); }

            try
            {
                var uri = new Uri("pack://application:,,,/Pickets;component/Assets/open-fence.ico", UriKind.Absolute);
                var s = System.Windows.Application.GetResourceStream(uri)?.Stream;
                if (s != null) return new Drawing.Icon(s, size);
            }
            catch (Exception ex) { log?.SafeLog("Tray icon (resource)", ex); }

            try
            {
                if (Environment.ProcessPath is { } exe && Drawing.Icon.ExtractAssociatedIcon(exe) is { } assoc)
                    return new Drawing.Icon(assoc, size);
            }
            catch (Exception ex) { log?.SafeLog("Tray icon (exe)", ex); }

            return Drawing.SystemIcons.Application;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                MinimizeToTray();
        }

        private void MinimizeToTray()
        {
            Hide();
            ShowInTaskbar = false;

            // Explain it once; after that, minimizing to the tray is expected.
            if (_tray is { } ni && !_config.Options.TrayHintShown)
            {
                ni.BalloonTipTitle = "Pickets";
                ni.BalloonTipText = "Still running. Double-click the tray icon to restore.";
                ni.ShowBalloonTip(1200);
                _config.Options.TrayHintShown = true;
                SaveConfig();
            }
        }

        internal void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Activate();
        }

        // ========== Config I/O ==========
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // SaveConfig keeps the previous good config here, so a damaged config.json can be recovered.
        private string BackupConfigPath => _configPath + ".bak";

        private void LoadConfig()
        {
            bool mainExists = File.Exists(_configPath);
            if (TryReadConfig(_configPath, out var cfg)) { _config = cfg; return; }
            if (!mainExists && !File.Exists(BackupConfigPath)) return; // first run

            // config.json is missing or unreadable. Set the damaged file aside (never silently
            // overwrite it), then fall back to the backup from the last good save.
            string? damagedCopy = null;
            if (mainExists)
            {
                try
                {
                    damagedCopy = Path.Combine(Path.GetDirectoryName(_configPath)!,
                        $"config.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Move(_configPath, damagedCopy);
                }
                catch { damagedCopy = null; }
            }

            if (TryReadConfig(BackupConfigPath, out var backup))
            {
                _config = backup;
                if (mainExists)
                    MessageBox.Show("Your Pickets settings file was damaged, so your fences were restored " +
                                    "from the last backup." +
                                    (damagedCopy != null ? $"\n\nThe damaged file was kept as:\n{damagedCopy}" : ""),
                                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (mainExists)
                MessageBox.Show("Your Pickets settings file couldn't be read and no backup was available, " +
                                "so Pickets is starting with a fresh layout." +
                                (damagedCopy != null ? $"\n\nThe damaged file was kept as:\n{damagedCopy}" : ""),
                                "Pickets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static bool TryReadConfig(string path, out AppConfig config)
        {
            config = new AppConfig();
            try
            {
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);

                // Back-compat: old format was just a list of FenceModel
                if (json.TrimStart().StartsWith("["))
                {
                    var legacy = JsonSerializer.Deserialize<List<FenceModel>>(json);
                    if (legacy == null) return false;
                    config.Fences = legacy;
                    return true;
                }

                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg == null) return false;
                config = cfg;
                return true;
            }
            catch { return false; }
        }

        // Write to a temp file, flush it to disk, then swap it in. A crash or power loss can
        // never leave a half-written config.json, and the previous version is kept as .bak.
        private void SaveConfig()
        {
            QueueHomeRefresh();
            var tmp = _configPath + ".tmp";
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(_config, JsonOpts);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(json, 0, json.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_configPath))
                    File.Replace(tmp, _configPath, BackupConfigPath, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, _configPath);
            }
            catch (Exception ex)
            {
                (System.Windows.Application.Current as App)?.SafeLog("SaveConfig", ex);
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }

        // ========== Fence windows ==========
        private void SpawnFencesFromConfig()
        {
            foreach (var m in _fences.ToList())
            {
                // One broken fence must never stop the app (and every other fence) from starting.
                try
                {
                    var modelRef = m; // explicit capture
                    var win = new FenceWindow(modelRef);
                    HookFenceWindow(win, modelRef);
                    _openWindows.Add(win);
                    if (!m.Closed) win.Show(); // closed fences stay closed but keep their items
                }
                catch (Exception ex)
                {
                    (System.Windows.Application.Current as App)?.SafeLog($"Spawn fence '{m.Name}'", ex);
                }
            }
        }

        private void HookFenceWindow(FenceWindow win, FenceModel model)
        {
            win.FenceRenamed += (_, __) => SaveConfig();
            win.Changed += (_, __) => SaveConfig();
            // A monitor's tiles changed on the fence: keep Settings → System monitor in step.
            if (model.IsMonitor)
                win.Changed += (_, __) => { if (SettingsPage.Visibility == Visibility.Visible) RefreshMonitorSettings(); };
            win.Closed += (_, __) => _openWindows.Remove(win);

            // Delete fence → remove from config (+ optional folder delete)
            win.DeleteRequested += (_, __) =>
            {
                if (model.IsMonitor)
                {
                    // Nothing to re-home: a monitor holds no items.
                    if (MessageBox.Show($"Delete the system monitor “{model.Name}”?", "Delete Fence",
                                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    Undoable($"Delete system monitor “{model.Name}”", () =>
                    {
                        _fences.Remove(model);
                        SaveConfig();
                        win.Close();
                    });
                    return;
                }

                if (model.IsPortal)
                {
                    // A portal points at the user's own folder — NEVER delete it; just unlink.
                    var ok = MessageBox.Show(
                        $"Remove the portal “{model.Name}”?\n\nThis only removes the fence. Your folder is not deleted:\n{model.FolderPath}",
                        "Remove Portal",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question);

                    if (ok != MessageBoxResult.OK) return;

                    Undoable($"Remove portal “{model.Name}”", () =>
                    {
                        _fences.Remove(model);
                        SaveConfig();
                        win.Close();
                    });
                    return;
                }

                // The catch-all is where released items go, so it can only be deleted once empty.
                if (IsCatchAll(win) && model.ItemPaths.Count > 0)
                {
                    // Its items have nowhere else to go, so "delete" hides it for good instead.
                    var hide = MessageBox.Show(
                        $"The “{CatchAllFenceName}” fence holds the {model.ItemPaths.Count} desktop item(s) that aren't in any " +
                        "other fence, so it can't be removed, but it can be hidden.\n\n" +
                        "Hide it? It stays hidden after restarts. Its items stay on your desktop: Ctrl+Alt+F still finds them, " +
                        "and the Pickets window (Home) brings the fence back.",
                        "Delete Fence", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
                    if (hide == MessageBoxResult.OK) win.CloseFence();
                    return;
                }

                var choice = MessageBox.Show(
                    $"Delete fence “{model.Name}”?\n\n" +
                    $"Nothing is deleted from your desktop — its items move to the “{CatchAllFenceName}” fence.",
                    "Delete Fence",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK);

                if (choice != MessageBoxResult.OK) return;

                Undoable($"Delete fence “{model.Name}”", () =>
                {
                    var released = model.ItemPaths.ToList();
                    _fences.Remove(model);
                    win.Close();

                    // Desktop icons stay hidden while Pickets runs, so the items need a new home
                    // right away or they'd vanish until the next launch.
                    if (released.Count > 0)
                        AssignItemsToFence(EnsureRealFence(CatchAllFenceName), released); // saves
                    else
                        SaveConfig();
                });
            };
        }

        // ========== Right-drag rectangle → Create fence ==========
        // Desktop icons are hidden and rendered inside fences, so a drawn box just creates a
        // new empty fence at that spot; drag items into it afterward.
        private void CreateFenceFromRect(Rect screenRect)
        {
            var model = new FenceModel
            {
                Name = UniqueFenceName(),
                Left = screenRect.Left,
                Top = screenRect.Top,
                Width = Math.Max(160, screenRect.Width),
                Height = Math.Max(120, screenRect.Height),
                Collapsed = false,
            };

            _fences.Add(model);
            SaveConfig();

            var win = new FenceWindow(model);
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
            win.EnsureBottomZOrder();
        }

        // "Move to new fence": a new fence just right of the source fence holding the items,
        // named right away.
        private void CreateFenceWithItems(FenceWindow source, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            Undoable(paths.Count == 1 ? "Move 1 item to a new fence" : $"Move {paths.Count} items to a new fence",
                     () => CreateFenceWithItemsCore(source, paths));
        }

        private void CreateFenceWithItemsCore(FenceWindow source, IReadOnlyList<string> paths)
        {
            var model = new FenceModel
            {
                Name = UniqueFenceName(),
                Left = source.Left + source.ActualWidth + 16,
                Top = source.Top,
                Width = 380,
                Height = 240
            };
            _fences.Add(model);

            var win = new FenceWindow(model); // keeps itself on screen if there's no room
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
            AssignItemsToFence(win, paths); // saves
            win.Activate();
            win.PromptRename();
        }

        private string UniqueFenceName()
        {
            string baseName = "Fence";
            int suffix = 1;
            string name;
            do { name = $"{baseName} {suffix++}"; }
            while (_fences.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            return name;
        }

        // ========== Desktop-item ownership (path-based, rendered) ==========
        internal const string CatchAllFenceName = "Desktop";

        // Convert legacy name-based ownership into path-based ownership so old configs keep
        // their groupings after the render pivot.
        private void MigrateLegacyOwnership()
        {
            bool changed = false;
            foreach (var m in _fences)
            {
                if (!m.HoldsDesktopItems || m.IconNames.Count == 0) continue;
                foreach (var name in m.IconNames)
                {
                    var path = DesktopItems.ResolvePath(name);
                    if (path != null && !m.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        m.ItemPaths.Add(path);
                }
                m.IconNames.Clear(); // fully migrated
                changed = true;
            }
            if (changed) SaveConfig();
        }

        // Earlier versions let the ✕ button close a fence's window while keeping the fence, and
        // then created a fresh fence of the same name when one was needed, so configs could pile
        // up several "Desktop"/"Apps" fences that all reopen at startup. Merge same-named real
        // fences into the first one, and keep each desktop item in only one fence (a specific
        // fence wins over the catch-all).
        private void RepairDuplicateFences()
        {
            if (FenceRepair.Repair(_fences, CatchAllFenceName)) SaveConfig();
        }

        // Fences that own desktop items (not portals or system monitors).
        private IEnumerable<FenceWindow> RealFences => _openWindows.Where(w => !w.IsPortal && !w.IsMonitor);

        private bool AnyFenceOwns(string path) => RealFences.Any(w => w.OwnsPath(path));

        private bool TakenOver
        {
            get => _config.Options.DesktopTakenOver == true;
            set
            {
                _config.Options.DesktopTakenOver = value;
                DesktopHelper.RestoreIconsOnExit = value; // only then does quitting show the icons
            }
        }

        // The first time desktop items are in a fence, take over the desktop: the rest go into
        // the Desktop fence and the real icons are hidden (they come back when Pickets quits).
        private void TakeOverIfNeeded()
        {
            if (TakenOver || !DesktopTakeover.HoldsDesktopItems(_fences)) return;
            TakenOver = true;
            BuildCatchAll();
            SaveConfig(); // BuildCatchAll only saves when it placed something
            DesktopHelper.SetDesktopIconsVisible(false);
        }

        // After fences were replaced or restored: anything they don't place goes to the Desktop
        // fence, or, before Pickets has taken over, the takeover starts if they hold items now.
        private void PlaceUnownedItems()
        {
            if (TakenOver) BuildCatchAll();
            else TakeOverIfNeeded();
        }

        // Put every desktop item not already owned by a fence into the catch-all "Desktop" fence.
        private void BuildCatchAll()
        {
            var unowned = DesktopItems.Enumerate()
                .Select(e => e.Path)
                .Where(p => !AnyFenceOwns(p))
                .ToList();
            if (unowned.Count == 0 && RealFences.Any()) return;

            EnsureRealFence(CatchAllFenceName, 80, 80).AddItems(unowned);
            SaveConfig();
        }

        // RequestAssignItems callback: give these desktop items to the target fence, removing
        // them from any other fence that held them, and persist.
        private void AssignItemsToFence(FenceWindow target, IReadOnlyList<string> paths)
        {
            foreach (var path in paths)
                foreach (var w in RealFences)
                    if (w != target) w.RemoveItemPath(path);

            target.AddItems(paths);
            SaveConfig();
        }

        private FenceWindow? FindRealFence(string name) =>
            RealFences.FirstOrDefault(w => string.Equals(w.FenceName, name, StringComparison.OrdinalIgnoreCase));

        // Returns the open window for a named real fence, creating it if needed.
        private FenceWindow EnsureRealFence(string name, double left = 80, double top = 80)
        {
            var existing = FindRealFence(name);
            if (existing != null) return existing;

            // A fence with this name exists but its window isn't open (e.g. it failed to
            // spawn): reopen it rather than creating a duplicate.
            var model0 = _fences.FirstOrDefault(f => f.HoldsDesktopItems &&
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (model0 != null)
            {
                var reopened = new FenceWindow(model0);
                HookFenceWindow(reopened, model0);
                _openWindows.Add(reopened);
                reopened.Show();
                return reopened;
            }

            var model = new FenceModel
            {
                Name = name,
                Left = left,
                Top = top,
                Width = 420,
                Height = 260,
                Collapsed = false
            };
            _fences.Add(model);
            SaveConfig();

            var win = new FenceWindow(model);
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
            return win;
        }

        // ========== Live desktop folder watcher ==========
        private readonly List<FileSystemWatcher> _desktopWatchers = new();

        private void StartDesktopWatcher()
        {
            foreach (var root in DesktopItems.Roots())
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    // A bigger buffer than the 8 KB default, so copying or unzipping lots of files at
                    // once doesn't overflow it; if it does anyway (or the folder goes away), Error
                    // fires and the whole desktop is checked again.
                    var w = new FileSystemWatcher(root) { IncludeSubdirectories = false, InternalBufferSize = 64 * 1024, EnableRaisingEvents = true };
                    w.Error += (_, e) => Dispatcher.BeginInvoke(() => ResyncDesktop(e.GetException()));
                    w.Created += (_, e) => Dispatcher.BeginInvoke(() => OnDesktopFileCreated(e.FullPath));
                    w.Deleted += (_, e) => Dispatcher.BeginInvoke(() => QueueDesktopRemoval(e.FullPath));
                    w.Renamed += (_, e) => Dispatcher.BeginInvoke(() => OnDesktopFileRenamed(e.OldFullPath, e.FullPath));
                    _desktopWatchers.Add(w);
                }
                catch { /* skip this root */ }
            }
        }

        // The watcher missed events: restart it, drop items whose files are gone, and give new
        // ones a fence, as a fresh start would.
        private void ResyncDesktop(Exception? why)
        {
            (System.Windows.Application.Current as App)?.SafeLog("Desktop watcher (checking the desktop again)", why);
            StopDesktopWatcher();
            StartDesktopWatcher();
            var gone = RealFences.SelectMany(w => w.OwnedItemPaths)
                .Where(p => !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !File.Exists(p) && !Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var p in gone) OnDesktopFileRemoved(p);
            if (TakenOver) BuildCatchAll();
        }

        private void StopDesktopWatcher()
        {
            foreach (var w in _desktopWatchers)
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* ignore */ }
            _desktopWatchers.Clear();
        }

        // A new desktop item appeared → route to a rule target (if auto-organize is on) or the
        // catch-all "Desktop" fence.
        private void OnDesktopFileCreated(string path)
        {
            try
            {
                // The original's name back (a save by renaming, below): it stays in its fence.
                if (_pendingRenames.Remove(path)) return;
                if (!TakenOver || AnyFenceOwns(path)) return; // the desktop is still left alone
                if (!DesktopItems.IsShown(path)) return; // hidden, system or temp: Explorer wouldn't show it either

                string target = CatchAllFenceName;
                if (_config.Options.AutoOrganize)
                {
                    var ruleTarget = DesktopRules.ResolveTargetFence(path, _config.Rules);
                    if (!string.IsNullOrWhiteSpace(ruleTarget)) target = ruleTarget!;
                }

                AssignItemsToFence(EnsureRealFence(target), new[] { path });
            }
            catch { /* ignore one bad item */ }
        }

        // A renamed item stays in the fence it was in (instead of being treated as a new file),
        // but only once the rename has settled. Word, Excel and others save by renaming the
        // original to a temp name and the new version into its place: following the first rename
        // at once moved the document into the Desktop fence on every save.
        private readonly Dictionary<string, string> _pendingRenames = new(StringComparer.OrdinalIgnoreCase); // old → new

        private void OnDesktopFileRenamed(string oldPath, string newPath)
        {
            if (_pendingRenames.Remove(newPath)) return; // the original's name came back: a save
            if (RealFences.Any(w => w.OwnsPath(oldPath)))
            {
                _pendingRenames[oldPath] = newPath;
                ScheduleDesktopSettle();
                return;
            }
            OnDesktopFileCreated(newPath); // wasn't in a fence (e.g. a temp file renamed into place)
        }

        // After the short wait: a rename whose old name is still free is real, so the fence
        // follows it; one whose file vanished altogether is a removal.
        private void SettlePendingRenames()
        {
            bool changed = false;
            foreach (var (oldPath, newPath) in _pendingRenames.ToList())
            {
                if (File.Exists(oldPath) || Directory.Exists(oldPath)) continue; // saved in place
                if (File.Exists(newPath) || Directory.Exists(newPath))
                {
                    foreach (var w in RealFences)
                        if (w.ReplaceItemPath(oldPath, newPath)) { changed = true; break; }
                }
                else OnDesktopFileRemoved(oldPath);
            }
            _pendingRenames.Clear();
            if (changed) SaveConfig();
        }

        // Deletions are confirmed after a short delay. Apps like Office save by deleting the
        // original and renaming a temp file into its place; acting at once would drop the file
        // from its fence and the rename would then land it in the Desktop fence.
        private readonly HashSet<string> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Threading.DispatcherTimer? _removalTimer;

        private void QueueDesktopRemoval(string path)
        {
            _pendingRemovals.Add(path);
            ScheduleDesktopSettle();
        }

        // Renames and removals are settled together, 2 seconds after the last one.
        private void ScheduleDesktopSettle()
        {
            if (_removalTimer == null)
            {
                _removalTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _removalTimer.Tick += (_, __) =>
                {
                    _removalTimer.Stop();
                    SettlePendingRenames();
                    foreach (var p in _pendingRemovals.ToList())
                        if (!File.Exists(p) && !Directory.Exists(p)) OnDesktopFileRemoved(p);
                    _pendingRemovals.Clear();
                };
            }
            _removalTimer.Stop();
            _removalTimer.Start();
        }

        private void OnDesktopFileRemoved(string path)
        {
            bool changed = false;
            foreach (var w in RealFences)
                if (w.RemoveItemPath(path)) changed = true;
            if (changed) SaveConfig();
        }

        // ========== Menu / Buttons ==========
        // New fences/portals open on whichever monitor the cursor is on, so they're
        // never lost off-screen on a multi-monitor setup.
        private (double left, double top) SpawnNearCursor()
        {
            try
            {
                // Nudge so the title bar sits under the cursor.
                var p = ScreenLayout.CursorDip();
                return (p.X - 40, p.Y - 16);
            }
            catch { return (120, 120); }
        }

        private void NewFence_Click(object? sender, RoutedEventArgs? e)
        {
            var (left, top) = SpawnNearCursor();
            var model = new FenceModel
            {
                Name = UniqueFenceName(),
                Left = left,
                Top = top,
                Width = 420,
                Height = 260,
                Collapsed = false
            };

            _fences.Add(model);
            SaveConfig();

            var win = new FenceWindow(model);
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
        }

        private void NewFolderPortal_Click(object? sender, RoutedEventArgs? e) => CreateFolderPortal(null);

        /// <summary>Ask for a folder and add a portal showing it: in <paramref name="screenRect"/>
        /// (a box drawn by right-dragging on the desktop), or near the mouse when null.</summary>
        private void CreateFolderPortal(Rect? screenRect)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose a folder to mirror as a Portal"
            };
            if (dlg.ShowDialog() != true) return;

            string folder = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            AddPortal(folder, new DirectoryInfo(folder).Name, screenRect);
        }

        // A ready-made portal of Windows' Recent folder: shortcuts to recently opened files,
        // newest first. The filter hides its two internal subfolders (jump-list data).
        private void RecentFiles_Click(object? sender, RoutedEventArgs? e)
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(recent)) return;
            AddPortal(recent, "Recent files", null, m =>
            {
                m.Sort = FenceSort.DateModified;
                m.PortalFilter = "*.lnk";
                m.PortalMaxItems = 30;
                m.View = FenceView.List;
            });
        }

        /// <summary>Add a portal showing <paramref name="folder"/>, named <paramref name="baseName"/>
        /// (made unique), in <paramref name="screenRect"/> or near the mouse.</summary>
        private void AddPortal(string folder, string baseName, Rect? screenRect, Action<FenceModel>? setup = null)
        {
            // De-dupe the name against existing fences.
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Portal";
            string name = baseName;
            int n = 1;
            while (_fences.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} ({++n})";

            var (left, top) = SpawnNearCursor();
            double width = 440, height = 320;
            if (screenRect is Rect r)
                (left, top, width, height) = (r.Left, r.Top, Math.Max(160, r.Width), Math.Max(120, r.Height));

            var model = new FenceModel
            {
                Name = name,
                FolderPath = folder,
                IsPortal = true,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Collapsed = false
            };
            setup?.Invoke(model);

            _fences.Add(model);
            SaveConfig();

            var win = new FenceWindow(model);
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
        }

        private void NewSystemMonitor_Click(object? sender, RoutedEventArgs? e) => CreateSystemMonitor(null);

        /// <summary>Add a system monitor fence (CPU, memory, disk space, network, battery): in
        /// <paramref name="screenRect"/> (a box drawn by right-dragging on the desktop), or near the mouse.</summary>
        private void CreateSystemMonitor(Rect? screenRect)
        {
            string name = "System";
            int n = 1;
            while (_fences.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"System ({++n})";

            // Two tiles across, as tall as the tiles need (a drive or battery can come and go);
            // a box drawn on the desktop keeps the size it was drawn at.
            var (left, top) = SpawnNearCursor();
            double width = 352, height = 440;
            bool fit = screenRect == null;
            if (screenRect is Rect r)
                (left, top, width, height) = (r.Left, r.Top, Math.Max(160, r.Width), Math.Max(120, r.Height));

            var model = new FenceModel
            {
                Name = name,
                IsMonitor = true,
                Tiles = MonitorLayout.Defaults(SystemMonitor.FixedDrives().Select(d => d.Name)),
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                AutoHeight = fit,
                Collapsed = false
            };
            _fences.Add(model);
            SaveConfig();

            var win = new FenceWindow(model);
            HookFenceWindow(win, model);
            _openWindows.Add(win);
            win.Show();
            win.EnsureBottomZOrder();
            RefreshMonitorSettings();
        }

        private void About_Click(object? sender, RoutedEventArgs? e)
        {
            RestoreFromTray();
            NavAbout.IsChecked = true;
        }

        private void ToggleDesktopIcons_Click(object? sender, RoutedEventArgs? e)
        {
            DesktopHelper.ToggleDesktopIcons();
        }

        // Show All also reopens fences closed with ✕.
        private void ShowAll_Click(object? sender, RoutedEventArgs? e)
        {
            foreach (var w in _openWindows.Where(w => w.IsClosedByUser).ToList()) w.Reopen();
            SetFencesHidden(false);
        }

        private void HideAll_Click(object? sender, RoutedEventArgs? e) => SetFencesHidden(true);

        // Organizes the *real* desktop icons into Apps / Documents / System fences by claiming
        // them (no shortcuts created). Each icon is classified by the desktop file behind it.
        private void AutoImportDesktop_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                var entries = DesktopItems.Enumerate();
                if (entries.Count == 0)
                {
                    MessageBox.Show("No desktop items were found to organize.",
                                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                void AddTo(string fence, string path)
                {
                    if (!groups.TryGetValue(fence, out var list)) groups[fence] = list = new();
                    list.Add(path);
                }

                AutoSnapshot("Before Auto-Import");

                int apps = 0, docs = 0, sys = 0;
                foreach (var entry in entries)
                {
                    if (entry.IsSpecial) { AddTo("System", entry.Path); sys++; continue; }
                    if (DesktopRules.IsExecutableItem(entry.Path)) { AddTo("Apps", entry.Path); apps++; }
                    else { AddTo("Documents", entry.Path); docs++; }
                }

                var pos = new Dictionary<string, (double l, double t)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Apps"] = (80, 80),
                    ["Documents"] = (520, 80),
                    ["System"] = (80, 380),
                };
                Undoable("Auto-Import", () =>
                {
                    foreach (var kv in groups)
                    {
                        var (l, t) = pos.TryGetValue(kv.Key, out var p) ? p : (80, 80);
                        var target = EnsureRealFence(kv.Key, l, t);
                        if (target.IsClosedByUser) target.Reopen(); // the user asked to see these
                        AssignItemsToFence(target, kv.Value);
                    }
                });
                TakeOverIfNeeded();

                SaveConfig();
                MessageBox.Show($"Auto-import complete.\n\nApps: {apps}\nDocuments: {docs}\nSystem: {sys}",
                                "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Auto-import failed:\n" + ex.Message,
                                "Pickets", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ========== Delete all fences ==========
        // With no fences left, Pickets gives the desktop back: the real icons show again and
        // nothing is taken over until items are put in a fence again (Undo does that too).
        private void DeleteAllFences_Click(object? sender, RoutedEventArgs? e)
        {
            var ok = MessageBox.Show(
                "Delete all fences and portals?\n\n" +
                "Nothing on your desktop is deleted: your normal desktop icons come back. " +
                "Portals are removed but their folders are left alone.\n\n" +
                "The current layout is saved first, so you can bring it back from Settings → Layouts.",
                "Delete All Fences", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (ok != MessageBoxResult.OK) return;

            AutoSnapshot("Before deleting all fences");

            Undoable("Delete all fences", () =>
            {
                foreach (var w in _openWindows.ToList()) w.Close();
                _openWindows.Clear();
                _fences.Clear();
                _peeked = false;

                TakenOver = false;
                DesktopHelper.SetDesktopIconsVisible(true);
                SaveConfig();
            });
        }

        // ========== Layout snapshots ==========
        private void SaveLayoutSnapshot()
        {
            var prompt = new InputDialog("Save Layout", "Name this layout:", $"Layout {DateTime.Now:MMM d}");
            if (IsVisible) prompt.Owner = this;
            if (prompt.ShowDialog() != true) return;

            var name = prompt.Value.Trim();
            if (name.Length == 0) return;
            try { LayoutSnapshots.Save(name, _fences, automatic: false); }
            catch (Exception ex)
            {
                MessageBox.Show("The layout couldn't be saved:\n" + ex.Message, "Pickets",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Taken before anything that rearranges many fences at once, so it can be undone.
        private void AutoSnapshot(string reason)
        {
            try { LayoutSnapshots.Save(reason, _fences, automatic: true); }
            catch (Exception ex) { (System.Windows.Application.Current as App)?.SafeLog("Auto snapshot", ex); }
        }

        private void RestoreLayoutSnapshot(LayoutSnapshots.Snapshot snap)
        {
            var ok = MessageBox.Show(
                $"Restore the layout “{snap.Name}”?\n\nYour current fences are replaced by the ones in this layout" +
                (snap.Rules != null ? ", and your auto-organize rules by its rules" : "") + ". " +
                "Nothing on your desktop is deleted, and the current layout is saved first so you can go back.",
                "Restore Layout", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;

            AutoSnapshot($"Before restoring “{snap.Name}”");
            ReplaceFences(snap.Fences);
            if (snap.Rules != null) { _config.Rules = snap.Rules; SaveConfig(); } // imported from a file, once the fences are in
        }

        /// <summary>Show <paramref name="source"/> (copied) instead of the current fences. Items that
        /// no longer exist are dropped, an item claimed by two fences stays in the first, and
        /// anything on the desktop the new set doesn't place goes to the Desktop fence.</summary>
        private void ReplaceFences(IEnumerable<FenceModel> source)
        {
            FencesInFront.Exit();
            var fences = LayoutSnapshots.Clone(source);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fences.Where(f => f.HoldsDesktopItems))
                f.ItemPaths = f.ItemPaths
                    .Where(p => p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(p) || Directory.Exists(p))
                    .Where(p => claimed.Add(p))
                    .ToList();

            foreach (var w in _openWindows.ToList()) w.Close();
            _openWindows.Clear();
            _config.Fences = fences;
            _peeked = false;
            _undo.Clear(); // its steps belong to the fences that were just replaced

            SpawnFencesFromConfig();
            PlaceUnownedItems(); // anything on the desktop the new set doesn't place
            SaveConfig();
            // Settings' tile editor pointed at a monitor that's just been closed: show the new ones.
            _monitorTarget = null;
            RefreshMonitorSettings();
        }

        // ========== Rules editor ==========
        private void EditRules_Click(object? sender, RoutedEventArgs? e)
        {
            var dlg = new RulesDialog(_config.Rules, _config.Options.AutoOrganize, RealFences.Select(w => w.FenceName));
            if (IsVisible) dlg.Owner = this;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (dlg.ShowDialog() != true) return;

            _config.Rules = dlg.Rules;
            _config.Options.AutoOrganize = dlg.AutoOrganize;
            RefreshSettingsPage();
            SaveConfig();

            if (dlg.ApplyNow)
            {
                AutoSnapshot("Before sorting the Desktop fence");
                ApplyRulesToCatchAll();
            }
        }

        // Re-sort what's already in the Desktop fence with the current rules. System items
        // (This PC, Recycle Bin…) stay put; rules are about files.
        private void ApplyRulesToCatchAll()
        {
            var catchAll = FindRealFence(CatchAllFenceName);
            if (catchAll == null) return;

            var groups = catchAll.OwnedItemPaths
                .Where(p => !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                .Select(p => (path: p, target: DesktopRules.ResolveTargetFence(p, _config.Rules)))
                .Where(x => !string.IsNullOrWhiteSpace(x.target) &&
                            !string.Equals(x.target, CatchAllFenceName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.target!, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int moved = 0;
            if (groups.Count > 0)
                Undoable("Sort the Desktop fence", () =>
                {
                    foreach (var g in groups)
                    {
                        var paths = g.Select(x => x.path).ToList();
                        AssignItemsToFence(EnsureRealFence(g.Key), paths);
                        moved += paths.Count;
                    }
                });

            MessageBox.Show(moved == 0
                    ? "Nothing in the Desktop fence matched a rule."
                    : $"Moved {moved} item(s) out of the Desktop fence into {groups.Count} fence(s).",
                "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ========== Rules engine: continuous auto-organize ==========
        private void ApplyAutoOrganizeSetting()
        {
            // The live desktop watcher (StartDesktopWatcher) always runs and reads this flag at
            // event time, so there's no separate organizer to start/stop here.
        }

        // ========== Double-click empty desktop: peek fences and/or toggle icons ==========
        private bool _peeked;

        private void ApplyDoubleClickSetting()
        {
            if (!Sandbox.IsActive && (_config.Options.DoubleClickDesktopToToggleIcons || _config.Options.DoubleClickPeekFences))
                DesktopDoubleClickMonitor.Start(OnDesktopDoubleClick);
            else
                DesktopDoubleClickMonitor.Stop();
        }

        private void OnDesktopDoubleClick()
        {
            if (_config.Options.DoubleClickPeekFences) TogglePeek();
            if (_config.Options.DoubleClickDesktopToToggleIcons) DesktopHelper.ToggleDesktopIconsRobust();
        }

        // Shared by the double-click peek, Ctrl+Alt+H and the Show/Hide All menu items, so
        // they always agree on whether fences are currently hidden.
        private void TogglePeek() => SetFencesHidden(!_peeked);

        private void SetFencesHidden(bool hidden)
        {
            if (hidden) FencesInFront.Exit();
            _peeked = hidden;
            foreach (var w in _openWindows)
            {
                if (hidden) w.Hide();
                else if (!w.IsClosedByUser)
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.EnsureBottomZOrder();
                }
            }
        }

        // ========== Shutdown ==========
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // QoL: always restore the real desktop icons on exit so users aren’t “stuck hidden”.
            // Synchronous (no DispatcherTimer) because we Shutdown() moments later.
            try { DesktopHelper.RestoreDesktopIconsOnExit(); } catch { }

            SaveConfig();

            _updateTimer?.Stop();
            _displayTimer?.Stop();
            _hotkeys?.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            StopDesktopWatcher();
            ShowDesktopWatcher.Stop();
            DesktopDoubleClickMonitor.Stop();
            DesktopRightDragFenceSelector.Stop();
            DesktopLeftDragLasso.Stop();

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _trayMenu?.Dispose();

            System.Windows.Application.Current?.Shutdown();
        }
    }
}
