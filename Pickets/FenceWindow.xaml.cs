using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

// Avoid WinForms clash
using MessageBox = Pickets.ThemedMessageBox;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Pickets
{
    public partial class FenceWindow : Window, INotifyPropertyChanged
    {
        private const double CollapsedHeight = 44;
        private const double MinExpandedHeight = 120;

        private readonly FenceModel _model;
        // Portals watch their mirrored folder. Real-icon fences own desktop icons by
        // name and have no backing folder, so the watcher is null for them.
        private FileSystemWatcher? _watcher;

        // Portals: the folder currently shown. Starts at the portal's folder and changes as the
        // user opens subfolders (not persisted; a portal always reopens at its own folder).
        private string _currentFolder = "";

        // A "real" fence == every fence that isn't a portal or a system monitor. It renders the
        // desktop items it owns (_model.ItemPaths) as tiles, like a portal scoped to specific items.
        private bool IsRealIcon => _model.HoldsDesktopItems;


        private const double TitleBarHeight = 34;

        // Set true while we drive Height/Width programmatically (collapse animation,
        // initial layout) so SizeChanged doesn't clobber the model's remembered size.
        private bool _suppressGeometrySave;

        public ObservableCollection<FenceItem> ItemsSource { get; } = new();

        public event EventHandler? FenceRenamed;
        public event EventHandler? DeleteRequested;
        // Raised whenever a persisted setting changes (sort, icon size, transparency).
        public event EventHandler? Changed;

        // ----- Layout metrics bound by the XAML (per-fence icon size) -----
        public int IconPx => (_model?.IconSize ?? FenceIconSize.Medium) switch
        {
            FenceIconSize.Small => 32,
            FenceIconSize.Large => 64,
            _ => 48
        };
        // List view rows use a smaller icon that still follows the icon-size setting.
        public int RowIconPx => (_model?.IconSize ?? FenceIconSize.Medium) switch
        {
            FenceIconSize.Small => 16,
            FenceIconSize.Large => 24,
            _ => 20
        };
        private bool IsListView => _model?.View == FenceView.List;
        public double TileWidth => IconPx + 44;
        public double TileHeight => IconPx + 50;
        public double TileContentWidth => TileWidth - 8;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseLayoutMetricsChanged()
        {
            OnPropertyChanged(nameof(IconPx));
            OnPropertyChanged(nameof(RowIconPx));
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(TileContentWidth));
            OnPropertyChanged(nameof(MonitorTileWidth));
            OnPropertyChanged(nameof(MonitorValueSize));
            OnPropertyChanged(nameof(MonitorGraphHeight));
            QueueAutoFit();
        }


        /// <summary>App-wide options (snapping, hover roll-up…), wired by MainWindow.</summary>
        public static AppOptions? Options;

        /// <summary>Every open fence window, so fences can snap to and search across each other.</summary>
        public static readonly List<FenceWindow> AllFences = new();

        public FenceWindow(FenceModel model)
        {
            InitializeComponent();
            _model = model;
            AllFences.Add(this);
            Closed += (_, __) => AllFences.Remove(this);

            TitleText.Text = model.IsPortal ? "🗁 " + model.Name : model.Name;
            UpdateAccessibleName();
            Left = model.Left; Top = model.Top;
            Width = model.Width; Height = model.Height;
            FitToCurrentScreens();

            ApplyBackground();
            ApplyLock();

            // Both portal and real fences render their items as tiles in the scrollable
            // content area, and both accept drops.
            AllowDrop = true;
            DragEnter += FenceWindow_DragEnter;
            DragOver += FenceWindow_DragOver;
            MouseEnter += FenceWindow_MouseEnter;
            PreviewMouseWheel += FenceWindow_PreviewMouseWheel;
            ItemsSource.CollectionChanged += (_, __) => { UpdateEmptyHint(); UpdateCountBadge(); QueueAutoFit(); };
            IsVisibleChanged += (_, __) => UpdateBackgroundPlayback();
            MouseLeave += FenceWindow_MouseLeave;
            Drop += FenceWindow_Drop;
            Items.ItemsSource = ItemsSource;
            ApplyView();

            if (_model.IsPortal)
            {
                // ----- Portal: windowed view of a real folder -----
                NavigatePortal(_model.FolderPath);
            }
            else if (_model.IsMonitor)
            {
                // ----- System monitor: live readings instead of items (FenceWindow.Monitor.cs) -----
                InitMonitor();
            }
            else
            {
                // ----- Real fence: renders the desktop items it owns (by path) as tiles -----
                Pickets.Services.FenceTabs.Normalize(_model);
                ReloadRealItems();
            }
            RebuildTabs();
            // Follow the body's Visibility itself (IsVisibleChanged doesn't fire before the
            // window is shown, e.g. for a fence that starts rolled up).
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(VisibilityProperty, typeof(ScrollViewer))
                .AddValueChanged(Scroller, (_, __) => { UpdateTabStripVisibility(); UpdateEmptyHint(); });

            if (_model.Collapsed) SetCollapsed(true, animate: false);

            // Keep fences out of the Alt-Tab switcher (must run once the HWND exists), and
            // watch the move/resize loop for snapping and per-monitor-layout memory.
            SourceInitialized += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                DesktopHelper.HideFromAltTab(hwnd);
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
                ApplyGlass();
            };

            // Land on the right monitor at the right scale once the window exists.
            _suppressGeometrySave = true;
            DesktopHelper.PlaceWindow(this, new Rect(Left, Top, Width, Height));
            SourceInitialized += (_, __) => _suppressGeometrySave = false;

            Loaded += (_, __) => { EnsureBottomZOrder(); QueueAutoFit(); };
            // A fence is never minimized: it has no taskbar button to bring it back, and
            // Ctrl+Alt+H's Show() leaves a minimized window minimized (so: invisible).
            StateChanged += (_, __) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
            // Clicking a fence: back below app windows, but in front of the other fences. Keyboard
            // focus goes to a tile, so a screen reader has something to read.
            Activated += (_, __) =>
            {
                EnsureBottomZOrder();
                BringToFrontOfFences();
                if (!Items.IsKeyboardFocusWithin) FocusCurrentTile();
            };

            LocationChanged += SaveGeometry;
            SizeChanged += (_, e) =>
            {
                SaveGeometry(null, null);
                if (_model.Glass && !Pickets.Services.Theme.IsHighContrast) UpdateGlassRegion();
                PushFollowers();
                if (e.WidthChanged) QueueAutoFit(); // narrower means more rows
            };

            // Stop watching the backing folder once this fence is gone.
            Closed += (_, __) =>
            {
                _hoverTimer?.Stop();
                _rollTimer?.Stop();
                _reloadTimer?.Stop();
                _reconnectTimer?.Stop();
                ReleaseMedia();
                try { _watcher?.Dispose(); } catch { /* ignore */ }
            };
        }

        // ---------- UI/Background ----------

        private static readonly MediaColor DefaultBody = MediaColor.FromRgb(0x20, 0x20, 0x20);  // #202020
        private static readonly MediaColor DefaultTitle = MediaColor.FromRgb(0x2B, 0x2B, 0x2B); // #2B2B2B
        private static readonly MediaColor LightBody = MediaColor.FromRgb(0xF4, 0xF5, 0xF7);    // light theme
        private static readonly MediaColor LightTitle = MediaColor.FromRgb(0xE3, 0xE6, 0xEB);

        private void ApplyBackground()
        {
            if (Pickets.Services.Theme.IsHighContrast) { ApplyContrastColors(); return; }

            // An accent tints the base (strongly on the title bar, lightly on the body). The base
            // follows the app theme, except under a picture/video, where the darkening layer keeps
            // the dark base so "Darken" still darkens.
            var media = MediaBrush();
            bool light = Pickets.Services.Theme.IsLight && media == null;
            MediaColor body = light ? LightBody : DefaultBody, title = light ? LightTitle : DefaultTitle;
            if (TryParseColor(_model.AccentColor, out var accent))
            {
                body = Mix(accent, body, light ? 0.12 : 0.16);
                title = Mix(accent, title, light ? 0.40 : 0.55);
            }

            double opacity = Math.Clamp(_model.BackgroundOpacity, 0.0, 1.0);
            // A mostly see-through fence shows the wallpaper, not its body color: keep light labels.
            ApplyTextColors(titleIsLight: IsLightColor(title), bodyIsLight: IsLightColor(body) && opacity >= 0.5);
            if (media != null)
            {
                // Picture/video background: it fades with the fence's transparency, and a
                // darkening layer in the fence's body color keeps the labels readable.
                media.Opacity = opacity;
                MediaLayer.Background = media;
                // In Fit mode the picture doesn't cover the whole fence; the bands around it show
                // the fence's normal color. Fill/Stretch cover everything, so nothing goes under.
                RootBorder.Background = _model.BackgroundFit == FenceBackgroundFit.Fit
                    ? new SolidColorBrush(MediaColor.FromArgb((byte)Math.Round(255 * opacity), body.R, body.G, body.B))
                    : System.Windows.Media.Brushes.Transparent;
                byte dim = (byte)Math.Round(255 * Math.Clamp(_model.BackgroundDim, 0.0, 1.0) * opacity);
                TintLayer.Background = new SolidColorBrush(MediaColor.FromArgb(dim, body.R, body.G, body.B));
            }
            else
            {
                byte a = (byte)Math.Round(255 * opacity);
                RootBorder.Background = new SolidColorBrush(MediaColor.FromArgb(a, body.R, body.G, body.B));
                MediaLayer.Background = System.Windows.Media.Brushes.Transparent;
                TintLayer.Background = System.Windows.Media.Brushes.Transparent;
            }
            TitleBar.Background = new SolidColorBrush(title);
            TitleText.FontSize = _model.TitleFontSize > 0 ? _model.TitleFontSize : 12;

            // System monitor graphs and bars: the fence's color, or a calm blue.
            var graph = TryParseColor(_model.AccentColor, out var g) ? g : MediaColor.FromRgb(0x3B, 0x82, 0xF6);
            bool lightBody = IsLightColor(body) && opacity >= 0.5;
            Resources["Fence.Graph"] = Frozen(graph);
            Resources["Fence.Track"] = Frozen(lightBody ? MediaColor.FromArgb(0x22, 0, 0, 0) : MediaColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            // Readings that are high or low (Settings → warning colors): darker shades on a light body.
            Resources["Fence.Warn"] = Frozen(lightBody ? MediaColor.FromRgb(0xB4, 0x53, 0x09) : MediaColor.FromRgb(0xFB, 0xBF, 0x24));
            Resources["Fence.Critical"] = Frozen(lightBody ? MediaColor.FromRgb(0xC8, 0x1E, 0x1E) : MediaColor.FromRgb(0xF8, 0x71, 0x71));
        }

        // High contrast: Windows' own colors, opaque, with no accent, picture, video or glass.
        private void ApplyContrastColors()
        {
            static SolidColorBrush C(Pickets.Services.ContrastRole r) => Pickets.Services.Theme.ContrastBrush(r);
            const Pickets.Services.ContrastRole Window = Pickets.Services.ContrastRole.Window,
                                                Text = Pickets.Services.ContrastRole.WindowText,
                                                Highlight = Pickets.Services.ContrastRole.Highlight,
                                                HighlightText = Pickets.Services.ContrastRole.HighlightText;
            ReleaseMedia();
            RootBorder.Background = C(Window);
            TitleBar.Background = C(Window);
            MediaLayer.Background = System.Windows.Media.Brushes.Transparent;
            TintLayer.Background = System.Windows.Media.Brushes.Transparent;
            TitleText.FontSize = _model.TitleFontSize > 0 ? _model.TitleFontSize : 12;

            Resources["Fence.TitleFg"] = C(Text);
            Resources["Fence.ButtonFg"] = C(Text);
            Resources["Fence.ButtonHoverFg"] = C(HighlightText);
            Resources["Fence.ButtonHover"] = C(Highlight);
            Resources["Fence.ButtonPress"] = C(Highlight);
            Resources["Fence.LabelFg"] = C(Text);
            Resources["Fence.Hint"] = C(Text);
            Resources["Fence.ItemHover"] = C(Window);
            Resources["Fence.Edge"] = C(Text);
            Resources["Fence.Selected"] = C(Highlight);
            Resources["Fence.SelectedEdge"] = C(Text);
            Resources["Fence.Graph"] = C(Highlight);
            Resources["Fence.Track"] = C(Text);
            // High contrast keeps the theme's text color; the spoken value still says it.
            Resources["Fence.Warn"] = C(Text);
            Resources["Fence.Critical"] = C(Text);
        }

        // ---------- Background picture / video ----------
        private static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm" };
        private System.Windows.Media.Brush? _mediaBrush;
        private string? _mediaBrushKey;   // path + fit the brush was built for
        private MediaPlayer? _bgPlayer;

        private static bool IsVideo(string path) =>
            Array.IndexOf(VideoExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>The brush for the fence's background picture/video, or null when it has
        /// none (or the file is gone). Built once per path/fit and reused.</summary>
        private System.Windows.Media.Brush? MediaBrush()
        {
            var path = _model.BackgroundMedia;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ReleaseMedia();
                return null;
            }

            var key = path + "|" + _model.BackgroundFit;
            if (_mediaBrush != null && _mediaBrushKey == key) return _mediaBrush;
            ReleaseMedia();

            var stretch = _model.BackgroundFit switch
            {
                FenceBackgroundFit.Fit => Stretch.Uniform,
                FenceBackgroundFit.Stretch => Stretch.Fill,
                _ => Stretch.UniformToFill
            };

            try
            {
                if (IsVideo(path))
                {
                    // MediaPlayer + VideoDrawing plays without being in the visual tree; looping,
                    // muted unless sound was turned on for this fence.
                    var player = new MediaPlayer { IsMuted = _model.BackgroundMuted, Volume = 0.5 };
                    var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 16, 9) };
                    player.MediaOpened += (_, __) =>
                    {
                        if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
                            drawing.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);
                    };
                    player.MediaEnded += (_, __) => { player.Position = TimeSpan.Zero; player.Play(); };
                    player.MediaFailed += (_, e) =>
                        (System.Windows.Application.Current as App)?.SafeLog("Background video", e.ErrorException);
                    player.Open(new Uri(path));
                    _bgPlayer = player;
                    _mediaBrush = new DrawingBrush(drawing) { Stretch = stretch };
                    UpdateBackgroundPlayback();
                }
                else
                {
                    // Decode at a sensible size: big photos would otherwise sit in memory at full size.
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = (int)Math.Min(2560, 1600 * VisualTreeHelper.GetDpi(this).DpiScaleX);
                    bmp.EndInit();
                    bmp.Freeze();
                    _mediaBrush = new ImageBrush(bmp) { Stretch = stretch };
                }
                _mediaBrushKey = key;
                return _mediaBrush;
            }
            catch (Exception ex)
            {
                (System.Windows.Application.Current as App)?.SafeLog("Fence background", ex);
                ReleaseMedia();
                return null;
            }
        }

        private void ReleaseMedia()
        {
            try { _bgPlayer?.Close(); } catch { /* ignore */ }
            _bgPlayer = null;
            _mediaBrush = null;
            _mediaBrushKey = null;
        }

        /// <summary>Videos only play while the fence's body can be seen: not while the fence is
        /// hidden or rolled up (unless it's peeking open).</summary>
        private void UpdateBackgroundPlayback()
        {
            UpdateMonitorViewing(); // monitors read the PC's vitals only while they can be seen
            if (_bgPlayer == null) return;
            bool showing = IsVisible && (!_model.Collapsed || _tempExpanded);
            if (showing) _bgPlayer.Play();
            else _bgPlayer.Pause();
        }

        private void ChooseBackground_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a background picture or video",
                Filter = "Pictures and videos|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm" +
                         "|Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff" +
                         "|Videos|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm"
            };
            if (dlg.ShowDialog(this) != true) return;

            _model.BackgroundMedia = dlg.FileName;
            ApplyBackground();
            if (_mediaBrush == null)
            {
                _model.BackgroundMedia = null;
                MessageBox.Show("That file couldn't be used as a background.", "Pickets",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                ApplyBackground();
                return;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Per-fence: turn a background video's sound on or off.
        private void MuteBackground_Click(object sender, RoutedEventArgs e)
        {
            _model.BackgroundMuted = !_model.BackgroundMuted;
            if (_bgPlayer != null) _bgPlayer.IsMuted = _model.BackgroundMuted;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void RemoveBackground_Click(object sender, RoutedEventArgs e)
        {
            _model.BackgroundMedia = null;
            ApplyBackground();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void BackgroundFit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && Enum.TryParse<FenceBackgroundFit>(Convert.ToString(mi.Tag), out var fit))
            {
                _model.BackgroundFit = fit;
                ApplyBackground();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void BackgroundDim_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi &&
                double.TryParse(Convert.ToString(mi.Tag), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var dim))
            {
                _model.BackgroundDim = dim;
                ApplyBackground();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static bool IsLightColor(MediaColor c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150;

        private static SolidColorBrush Frozen(MediaColor c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Text and hover colors for the title bar and the tiles, picked for what's behind them.
        // The fence's XAML uses these keys with DynamicResource.
        private void ApplyTextColors(bool titleIsLight, bool bodyIsLight)
        {
            Resources["Fence.TitleFg"] = Frozen(titleIsLight ? MediaColor.FromRgb(0x1F, 0x23, 0x2B) : MediaColor.FromRgb(0xE0, 0xE0, 0xE0));
            Resources["Fence.ButtonFg"] = Frozen(titleIsLight ? MediaColor.FromRgb(0x4A, 0x51, 0x5E) : MediaColor.FromRgb(0xC7, 0xCB, 0xD1));
            Resources["Fence.ButtonHoverFg"] = Frozen(titleIsLight ? MediaColor.FromRgb(0x10, 0x13, 0x18) : MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
            Resources["Fence.ButtonHover"] = Frozen(titleIsLight ? MediaColor.FromArgb(0x1F, 0, 0, 0) : MediaColor.FromRgb(0x3A, 0x3D, 0x44));
            Resources["Fence.ButtonPress"] = Frozen(titleIsLight ? MediaColor.FromArgb(0x33, 0, 0, 0) : MediaColor.FromRgb(0x4A, 0x4E, 0x57));
            Resources["Fence.LabelFg"] = Frozen(bodyIsLight ? MediaColor.FromRgb(0x1A, 0x1D, 0x24) : MediaColor.FromRgb(0xEA, 0xEA, 0xEA));
            Resources["Fence.Hint"] = Frozen(bodyIsLight ? MediaColor.FromRgb(0x5C, 0x65, 0x77) : MediaColor.FromRgb(0x9A, 0xA3, 0xB5));
            Resources["Fence.ItemHover"] = Frozen(bodyIsLight ? MediaColor.FromArgb(0x16, 0, 0, 0) : MediaColor.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            Resources["Fence.Edge"] = Frozen(Pickets.Services.Theme.IsLight ? MediaColor.FromRgb(0xC9, 0xCF, 0xD9) : MediaColor.FromRgb(0x3A, 0x3A, 0x3A));
            Resources["Fence.Selected"] = Frozen(MediaColor.FromArgb(0x33, 0x54, 0x70, 0x8F));
            Resources["Fence.SelectedEdge"] = Frozen(MediaColor.FromRgb(0x5A, 0x8F, 0xD8));
        }

        /// <summary>The app switched between light and dark.</summary>
        public void ApplyTheme()
        {
            ApplyBackground();
            ApplyGlass(); // off under high contrast
            RebuildTabs();
        }

        private static MediaColor Mix(MediaColor c, MediaColor baseColor, double amount) => MediaColor.FromRgb(
            (byte)Math.Round(baseColor.R + (c.R - baseColor.R) * amount),
            (byte)Math.Round(baseColor.G + (c.G - baseColor.G) * amount),
            (byte)Math.Round(baseColor.B + (c.B - baseColor.B) * amount));

        private static bool TryParseColor(string? hex, out MediaColor color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try { color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex); return true; }
            catch { return false; }
        }

        // ---------- Real fence: owned desktop items (by path) ----------

        public string FenceName => _model.Name;

        /// <summary>The fence's saved settings (MainWindow matches windows to models with it).</summary>
        internal FenceModel Model => _model;

        /// <summary>Wired by MainWindow: record an undo step, then run the action (see Services/UndoHistory).</summary>
        public static Action<string, Action>? RequestUndoable;
        /// <summary>Wired by MainWindow: Ctrl+Z.</summary>
        public static Action? RequestUndo;
        /// <summary>Wired by MainWindow: F1 shows the keyboard shortcuts.</summary>
        public static Action? RequestKeyboardHelp;

        private static void Undoable(string description, Action action)
        {
            if (RequestUndoable != null) RequestUndoable(description, action);
            else action();
        }

        // ---------- Screen readers (see Controls/FenceTiles and Services/Accessibility) ----------

        /// <summary>The window's title isn't shown, but it's what a screen reader calls the fence.</summary>
        private void UpdateAccessibleName()
        {
            Title = _model.IsPortal ? $"{_model.Name} folder portal"
                  : _model.IsMonitor ? $"{_model.Name} system monitor"
                  : $"{_model.Name} fence";
            System.Windows.Automation.AutomationProperties.SetName(Items, _model.Name);
            System.Windows.Automation.AutomationProperties.SetName(MonitorItems, _model.Name);
        }

        /// <summary>Say something without moving focus (rolled up, tab switched, undone…).</summary>
        public void Announce(string message) => Pickets.Services.Accessibility.Announce(Announcer, message);

        private void FocusTile(int index)
        {
            if (index >= 0 && index < ItemsSource.Count &&
                Items.ItemContainerGenerator.ContainerFromIndex(index) is UIElement tile)
                tile.Focus();
            else
                Scroller.Focus();
        }

        /// <summary>Focus the tile the keyboard is on, else the first selected one, else the first.</summary>
        internal void FocusCurrentTile()
        {
            if (IsMonitor) { if (!MonitorItems.IsKeyboardFocusWithin) FocusFirstMonitorTile(); return; }
            if (ItemsSource.Count == 0) { Scroller.Focus(); return; }
            if (_cursorIndex < 0 || _cursorIndex >= ItemsSource.Count)
                _cursorIndex = Math.Max(0, ItemsSource.ToList().FindIndex(i => i.IsSelected));
            FocusTile(_cursorIndex);
        }

        /// <summary>F6 / Shift+F6: on to the next (or previous) fence, left to right, then top down.</summary>
        private void FocusNextFence(int direction)
        {
            var shown = AllFences.Where(f => f.IsVisible).OrderBy(f => f.Left).ThenBy(f => f.Top).ToList();
            if (shown.Count < 2) return;
            var next = shown[(shown.IndexOf(this) + direction + shown.Count) % shown.Count];
            next.Activate();
            next.FocusCurrentTile();
        }

        /// <summary>Undo put this fence's items (and tabs) back: show them again.</summary>
        internal void ReloadAfterUndo()
        {
            if (IsPortal) return;
            _cursorIndex = -1;
            ReloadRealItems();
            RebuildTabs();
        }

        /// <summary>The launchable paths of the desktop items this real fence owns.</summary>
        public IReadOnlyList<string> OwnedItemPaths => _model.ItemPaths;

        /// <summary>Wired by MainWindow so a drop can transfer items away from other fences and
        /// persist. Given (targetFence, paths), it assigns those paths to the target.</summary>
        public static Action<FenceWindow, IReadOnlyList<string>>? RequestAssignItems;

        public bool OwnsPath(string path) =>
            _model.ItemPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

        /// <summary>Add desktop items (by path) to this fence and re-render. Does not remove them
        /// from other fences — go through MainWindow (RequestAssignItems) for that.</summary>
        public void AddItems(IEnumerable<string> paths)
        {
            if (IsMonitor) return; // monitors never hold items
            bool any = false;
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!_model.ItemPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    _model.ItemPaths.Add(p);
                    // New items land on the tab that's showing.
                    if (HasTabs) Pickets.Services.FenceTabs.MoveTo(_model, new[] { p }, _model.ActiveTab);
                    any = true;
                }
            }
            if (any) { Changed?.Invoke(this, EventArgs.Empty); ReloadRealItems(); }
        }

        /// <summary>The item was renamed on disk: keep it in this fence (same position) under its
        /// new path. Returns false if this fence doesn't own <paramref name="oldPath"/>.</summary>
        public bool ReplaceItemPath(string oldPath, string newPath)
        {
            int i = _model.ItemPaths.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            _model.ItemPaths[i] = newPath;
            foreach (var tab in _model.Tabs)
            {
                int t = tab.ItemPaths.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
                if (t >= 0) tab.ItemPaths[t] = newPath;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            ReloadRealItems();
            return true;
        }

        /// <summary>Remove a desktop item (by path) from this fence and re-render.</summary>
        public bool RemoveItemPath(string path)
        {
            int removed = _model.ItemPaths.RemoveAll(
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            foreach (var tab in _model.Tabs)
                tab.ItemPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) { Changed?.Invoke(this, EventArgs.Empty); ReloadRealItems(); return true; }
            return false;
        }

        /// <summary>Rebuild this real fence's tiles from the desktop items it owns.</summary>
        public void ReloadRealItems()
        {
            if (!IsRealIcon) return;

            // With tabs, show only the active tab's items.
            var paths = SortEntries(_model.ItemPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !HasTabs || Pickets.Services.FenceTabs.TabOf(_model, p) == _model.ActiveTab)
                .ToList());

            ItemsSource.Clear();
            foreach (var path in paths)
            {
                ItemsSource.Add(new FenceItem
                {
                    Path = path,
                    DisplayName = LabelFor(path)
                });
            }

            LoadIconsAsync();
        }

        /// <summary>Tile label: desktop-style name (shortcut extensions hidden), and without any
        /// file extension when Settings → Hide file extensions is on.</summary>
        private static string LabelFor(string path)
        {
            var label = Pickets.Services.DesktopItems.LabelFor(path);
            if (Options?.HideFileExtensions == true && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(path))
                label = Path.GetFileNameWithoutExtension(label);
            return label;
        }

        /// <summary>Re-read labels and icons (after a display setting changes).</summary>
        public void RefreshItems()
        {
            if (IsPortal) ReloadItems();
            else ReloadRealItems();
        }

        public void EnsureBottomZOrder()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // While fences are in front of app windows, clicking or opening one keeps it there.
            if (Pickets.Services.FencesInFront.IsActive) DesktopHelper.SetTopmost(hwnd, true);
            // While "Show desktop" is on, the desktop window is on top: stay above it.
            else if (Pickets.Services.ShowDesktopWatcher.Lifted) Pickets.Services.ShowDesktopWatcher.BringToTop(hwnd);
            else DesktopHelper.SendToDesktopLayer(hwnd);
        }

        /// <summary>Stack this fence above every other fence (still below normal app windows),
        /// so an opened roll-up or a clicked fence isn't hidden behind its neighbours.</summary>
        public void BringToFrontOfFences()
        {
            var others = AllFences
                .Where(f => f != this && f.IsVisible)
                .Select(f => new WindowInteropHelper(f).Handle)
                .Where(h => h != IntPtr.Zero)
                .ToHashSet();
            DesktopHelper.RaiseAbove(new WindowInteropHelper(this).Handle, others);
        }

        private void SaveGeometry(object? sender, EventArgs? e)
        {
            // While we're animating the collapse, or while collapsed, the window
            // Height is the 44px stub — never persist that as the fence's real size.
            if (_suppressGeometrySave) return;

            _model.Left = Left;
            _model.Top = Top;

            if (!_model.Collapsed)
            {
                _model.Width = Width;
                _model.Height = Height;
            }
            else
            {
                // Width can still change while collapsed; height stays remembered.
                _model.Width = Width;
            }

            ScheduleSave();
        }

        // ---------- Monitor arrangements ----------
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;

        // The move/resize loop in progress: where the fence started and whether it's a move
        // (1) or a resize (2), known from the first WM_MOVING or WM_SIZING.
        private Rect _sizeMoveStartPx;
        private int _sizeMoveKind;
        private int _sizingEdge;
        private bool _inSizeMove;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // While Windows' item menu is open, let it fill submenus (Send to, Open with…).
            if (Pickets.Services.ShellContextMenu.HandleMenuMessage(msg, wParam, lParam, out var menuResult))
            {
                handled = true;
                return menuResult;
            }

            switch (msg)
            {
                // Alt+F4 (or Close from the window menu) would destroy the window while the fence
                // stays in the config, so its items showed nowhere until restart. Do what ✕ does.
                case 0x0112 /*WM_SYSCOMMAND*/ when (wParam.ToInt64() & 0xFFF0) == 0xF060 /*SC_CLOSE*/:
                    handled = true;
                    Dispatcher.BeginInvoke(CloseFence);
                    return IntPtr.Zero;

                case WM_ENTERSIZEMOVE:
                    _sizeMoveStartPx = DesktopHelper.WindowRectPx(hwnd);
                    _sizeMoveKind = 0;
                    _inSizeMove = true;
                    break;

                case Pickets.Services.FenceSnapper.WM_MOVING:
                    if (_sizeMoveKind == 0) { _sizeMoveKind = 1; BeginStackDrag(); }
                    SnapProposedRect(hwnd, 0, lParam);
                    MoveStackWith(lParam);
                    break;

                case Pickets.Services.FenceSnapper.WM_SIZING:
                    // Dragging the bottom edge pushes (or pulls) the fences stacked below.
                    if (_sizeMoveKind == 0)
                    {
                        _sizeMoveKind = 2;
                        _sizingEdge = wParam.ToInt32();
                        if (wParam.ToInt32() is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT) BeginPushFollowers();
                    }
                    SnapProposedRect(hwnd, wParam.ToInt32(), lParam);
                    break;

                // Only a user move/resize is remembered for this monitor arrangement. Windows
                // also moves fences itself when a monitor disappears; that must not overwrite
                // the spot the user chose for the arrangement it came from.
                case WM_EXITSIZEMOVE:
                    _inSizeMove = false;
                    EndStackDrag();
                    EndPushFollowers();
                    // Choosing a height by hand ends "Keep fitted to contents".
                    if (_sizeMoveKind == 2 && _model.AutoHeight && _sizingEdge >= WMSZ_TOP &&
                        Math.Abs(DesktopHelper.WindowRectPx(hwnd).Height - _sizeMoveStartPx.Height) >= 1)
                    {
                        _model.AutoHeight = false;
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    QueueAutoFit();
                    RememberPlacementForScreens();
                    break;
            }
            return IntPtr.Zero;
        }

        private void SnapProposedRect(IntPtr self, int sizingEdge, IntPtr lParam)
        {
            var opts = Options;
            if (opts == null || lParam == IntPtr.Zero) return;

            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<Pickets.Services.FenceSnapper.RECT>(lParam);
            // Fences moving along with this one (its stack) aren't something to snap to.
            var others = AllFences
                .Where(f => f != this && f.IsVisible && _stackDrag?.Any(m => m.Fence == f) != true)
                .Select(f => new WindowInteropHelper(f).Handle)
                .Where(h => h != IntPtr.Zero && h != self)
                .ToList();

            if (Pickets.Services.FenceSnapper.Snap(ref r, sizingEdge, others, opts.SnapToEdges, opts.SnapToGrid))
                System.Runtime.InteropServices.Marshal.StructureToPtr(r, lParam, false);
        }

        // ---------- Stacks (Services/FenceStacks) ----------
        private const int WMSZ_TOP = 3, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

        private IntPtr Hwnd => new WindowInteropHelper(this).Handle;
        private bool StacksOn => Options?.MoveStacksTogether == true;

        /// <summary>Where this fence sits in a stack, in screen pixels. A rolled-up fence that's
        /// only peeking open still takes up just its title bar.</summary>
        private Rect StackRectPx
        {
            get
            {
                var r = DesktopHelper.WindowRectPx(Hwnd);
                if (_tempExpanded && !r.IsEmpty) r.Height = Math.Round(CollapsedHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
                return r;
            }
        }

        private List<FenceWindow> StackNeighbours(bool belowOnly)
        {
            var candidates = AllFences
                .Where(f => f != this && f.IsVisible && !f._model.Locked && f.Hwnd != IntPtr.Zero)
                .ToList();
            var rects = candidates.Select(f => f.StackRectPx).ToList();
            double gap = Pickets.Services.FenceSnapper.GapDip * VisualTreeHelper.GetDpi(this).DpiScaleY;
            var found = belowOnly ? Pickets.Services.FenceStacks.Below(rects, StackRectPx, gap)
                                  : Pickets.Services.FenceStacks.Column(rects, StackRectPx, gap);
            return found.Select(i => candidates[i]).ToList();
        }

        // Title-bar drag: the rest of the stack follows with Alt held, or without it when Drag the
        // whole stack is on.
        private List<(FenceWindow Fence, Rect Start)>? _stackDrag;

        private void BeginStackDrag()
        {
            _stackDrag = null;
            bool alt = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Alt) != 0;
            if (!Pickets.Services.FenceStacks.DragsWholeStack(StacksOn, Options?.DragStacksTogether == true, alt)) return;
            var column = StackNeighbours(belowOnly: false);
            if (column.Count > 0)
                _stackDrag = column.Select(f => (f, DesktopHelper.WindowRectPx(f.Hwnd))).ToList();
        }

        private void MoveStackWith(IntPtr proposedRect)
        {
            if (_stackDrag == null || proposedRect == IntPtr.Zero) return;
            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<Pickets.Services.FenceSnapper.RECT>(proposedRect);
            int dx = r.Left - (int)_sizeMoveStartPx.Left, dy = r.Top - (int)_sizeMoveStartPx.Top;
            foreach (var (f, start) in _stackDrag)
                DesktopHelper.MoveWindowPx(f.Hwnd, (int)start.Left + dx, (int)start.Top + dy);
        }

        private void EndStackDrag()
        {
            if (_stackDrag == null) return;
            foreach (var (f, _) in _stackDrag) f.RememberPlacementForScreens();
            _stackDrag = null;
        }

        // Height changes (rolling up, opening, resizing the bottom edge, fitting) move the fences
        // stacked below by the same amount. They're found before the change, and moved by the
        // difference from the starting height, so several fences changing at once add up.
        private List<FenceWindow>? _followers;
        private double _followStartHeight;
        private int _followMovedPx;

        private void BeginPushFollowers()
        {
            _followers = StacksOn ? StackNeighbours(belowOnly: true) : null;
            _followStartHeight = _tempExpanded ? CollapsedHeight : ActualHeight;
            _followMovedPx = 0;
        }

        private void PushFollowers()
        {
            if (_followers == null || _followers.Count == 0) return;
            int want = (int)Math.Round((ActualHeight - _followStartHeight) * VisualTreeHelper.GetDpi(this).DpiScaleY);
            int d = want - _followMovedPx;
            if (d == 0) return;
            foreach (var f in _followers)
            {
                var r = DesktopHelper.WindowRectPx(f.Hwnd);
                if (!r.IsEmpty) DesktopHelper.MoveWindowPx(f.Hwnd, (int)r.Left, (int)r.Top + d);
            }
            _followMovedPx = want;
        }

        private void EndPushFollowers()
        {
            if (_followers == null) return;
            PushFollowers();
            foreach (var f in _followers) f.RememberPlacementForScreens();
            _followers = null;
        }

        /// <summary>Change the height in code (fit to contents), taking the stack below along.</summary>
        private void SetHeightWithStack(double height)
        {
            BeginPushFollowers();
            Height = height;
            UpdateLayout();
            EndPushFollowers();
        }

        /// <summary>"One open fence per stack": another fence in the stack opened.</summary>
        private void RollUpInStack()
        {
            if (_model.Collapsed) return;
            BeginPushFollowers();
            SetCollapsed(true, animate: true, done: EndPushFollowers);
            ScheduleSave();
        }

        private void RememberPlacementForScreens()
        {
            _model.Layouts[Pickets.Services.ScreenLayout.CurrentKey()] = new FenceRect
            {
                Left = _model.Left,
                Top = _model.Top,
                Width = _model.Width,
                Height = _model.Height
            };
            ScheduleSave();
        }

        /// <summary>Monitors changed (or startup): go back to where the user put this fence for
        /// this arrangement, and make sure it's on a visible screen either way.
        /// Returns true if the fence moved.</summary>
        public bool FitToCurrentScreens()
        {
            var r = _model.Layouts.TryGetValue(Pickets.Services.ScreenLayout.CurrentKey(), out var saved)
                ? new Rect(saved.Left, saved.Top, saved.Width, saved.Height)
                : new Rect(_model.Left, _model.Top, _model.Width, _model.Height);
            r = Pickets.Services.ScreenLayout.FitOnScreen(r);

            if (r.Left == _model.Left && r.Top == _model.Top &&
                r.Width == _model.Width && r.Height == _model.Height) return false;

            _model.Left = r.Left; _model.Top = r.Top;
            _model.Width = r.Width; _model.Height = r.Height;

            var target = new Rect(r.Left, r.Top, r.Width, _model.Collapsed ? Height : r.Height);
            if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
            {
                // Still in the constructor: placed for real once the window exists.
                Left = target.Left; Top = target.Top; Width = target.Width; Height = target.Height;
                return true;
            }
            _suppressGeometrySave = true;
            DesktopHelper.PlaceWindow(this, target);
            _suppressGeometrySave = false;
            return true;
        }

        // Moving/resizing fires many events; persist once the fence has settled, so the layout
        // survives a crash or an upgrade instead of only being saved on a clean exit.
        private System.Windows.Threading.DispatcherTimer? _saveTimer;

        private void ScheduleSave()
        {
            if (!IsLoaded) return; // initial placement in the constructor isn't a user change
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (_, __) => { _saveTimer.Stop(); Changed?.Invoke(this, EventArgs.Empty); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ---------- Portal navigation ----------

        /// <summary>Show <paramref name="folder"/> (the portal's folder or one inside it) and
        /// watch it for changes. The folder may be gone or on a drive that isn't connected: never
        /// recreate it (that would leave an empty stand-in), just show the portal as unavailable
        /// until it can be reached again.</summary>
        private void NavigatePortal(string folder)
        {
            try { _watcher?.Dispose(); } catch { /* ignore */ }
            _watcher = null;
            _reconnectTimer?.Stop();
            _currentFolder = folder;
            TitleText.Text = "🗁 " + _model.Name;
            var filter = Pickets.Services.PortalFilter.Describe(_model.PortalFilter, _model.PortalMaxAgeDays);
            TitleText.ToolTip = filter.Length > 0 ? $"{_model.FolderPath}\nShowing: {filter}" : _model.FolderPath;
            EnsureAgeRefresh();

            try
            {
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException(folder);

                ReloadItems();

                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += (_, __) => Dispatcher.BeginInvoke(ScheduleReload);
                _watcher.Deleted += (_, __) => Dispatcher.BeginInvoke(ScheduleReload);
                _watcher.Renamed += (_, __) => Dispatcher.BeginInvoke(ScheduleReload);
                // Sorted by date, an edit (e.g. reopening a recent file) changes the order.
                _watcher.Changed += (_, __) =>
                {
                    if (_model.Sort == FenceSort.DateModified) Dispatcher.BeginInvoke(ScheduleReload);
                };
                // Raised when the drive goes away (unplugged, network share dropped).
                _watcher.Error += (_, __) => Dispatcher.BeginInvoke(CheckPortalFolderStillThere);
            }
            catch
            {
                ShowPortalUnavailable(folder);
            }

            UpdateBreadcrumbs();
        }

        private bool IsPortalUnavailable => TitleText.Text.EndsWith("(unavailable)", StringComparison.Ordinal);

        private void ShowPortalUnavailable(string folder)
        {
            try { _watcher?.Dispose(); } catch { /* ignore */ }
            _watcher = null;
            ItemsSource.Clear();
            TitleText.Text = "🗁 " + _model.Name + " (unavailable)";
            TitleText.ToolTip = $"This folder can't be reached:\n{folder}\n\n" +
                                "It shows up again by itself once the drive is connected (or the folder is back).";
            UpdateEmptyHint();
            _reconnectTimer ??= NewTimer(ReconnectIntervalMs, TryReconnectPortal);
            _reconnectTimer.Start();
        }

        // The folder (or its drive) disappeared while it was showing.
        private void CheckPortalFolderStillThere()
        {
            if (_model.IsPortal && !IsPortalUnavailable && !Directory.Exists(_currentFolder))
                ShowPortalUnavailable(_currentFolder);
        }

        // An unavailable portal checks back every few seconds. Off the UI thread: a network share
        // that's down can take a long time to answer.
        private System.Windows.Threading.DispatcherTimer? _reconnectTimer;
        private bool _reconnectChecking;
        internal static int ReconnectIntervalMs = 5000; // tests shorten it

        private async void TryReconnectPortal()
        {
            if (_reconnectChecking || !_model.IsPortal) return;
            _reconnectChecking = true;
            string folder = _currentFolder, root = _model.FolderPath;
            string? back;
            try
            {
                // Back where it was, or at the portal's own folder if the subfolder it showed is gone.
                back = await Task.Run(() => Directory.Exists(folder) ? folder : Directory.Exists(root) ? root : null);
            }
            catch { back = null; }
            finally { _reconnectChecking = false; }

            if (back == null || !IsPortalUnavailable || folder != _currentFolder) return;
            _reconnectTimer?.Stop();
            NavigatePortal(back);
        }

        private bool IsInSubfolder =>
            _model.IsPortal && !string.Equals(Path.GetFullPath(_currentFolder).TrimEnd('\\'),
                                              Path.GetFullPath(_model.FolderPath).TrimEnd('\\'),
                                              StringComparison.OrdinalIgnoreCase);

        /// <summary>Back/breadcrumb bar under the title, shown only below the portal's own folder.</summary>
        private void UpdateBreadcrumbs()
        {
            Crumbs.Children.Clear();
            if (!IsInSubfolder)
            {
                BreadcrumbBar.Visibility = Visibility.Collapsed;
                return;
            }
            BreadcrumbBar.Visibility = Visibility.Visible;

            var root = Path.GetFullPath(_model.FolderPath).TrimEnd('\\');
            var rel = Path.GetRelativePath(root, _currentFolder);
            var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            AddCrumb(_model.Name, root, isLast: false);
            string path = root;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
                Crumbs.Children.Add(new TextBlock { Text = "›", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
                AddCrumb(parts[i], path, isLast: i == parts.Length - 1);
            }
        }

        private void AddCrumb(string label, string folder, bool isLast)
        {
            // The title-button template ignores Padding, so the label carries its own margin.
            var b = new System.Windows.Controls.Button
            {
                Content = new TextBlock { Text = label, Margin = new Thickness(6, 0, 6, 0) },
                Style = (Style)FindResource("FenceTitleButton"),
                FontSize = 12,
                Height = 22,
                FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = folder
            };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            if (!isLast) b.Click += (_, __) => NavigatePortal(folder);
            Crumbs.Children.Add(b);
        }

        private void PortalUp()
        {
            if (!IsInSubfolder) return;
            var parent = Path.GetDirectoryName(_currentFolder);
            if (!string.IsNullOrEmpty(parent)) NavigatePortal(parent);
        }

        private void Back_Click(object sender, RoutedEventArgs e) => PortalUp();

        /// <summary>Point this portal at a different folder (menu or folder drop).</summary>
        private void RetargetPortal(string folder)
        {
            if (!_model.IsPortal || !Directory.Exists(folder)) return;

            // A portal still named after its old folder follows the new one.
            var oldName = new DirectoryInfo(_model.FolderPath).Name;
            if (string.Equals(_model.Name, oldName, StringComparison.OrdinalIgnoreCase))
                _model.Name = new DirectoryInfo(folder).Name;

            _model.FolderPath = folder;
            NavigatePortal(folder);
            UpdateAccessibleName();
            FenceRenamed?.Invoke(this, EventArgs.Empty); // persists
        }

        // Folder events come in bursts (a download, a save); reload once things settle.
        private System.Windows.Threading.DispatcherTimer? _reloadTimer;

        private void ScheduleReload()
        {
            if (_reloadTimer == null)
            {
                _reloadTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _reloadTimer.Tick += (_, __) => { _reloadTimer.Stop(); ReloadItems(); };
            }
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }

        // With "changed within N days", items age out without any file event; re-check now and then.
        private System.Windows.Threading.DispatcherTimer? _ageTimer;

        private void EnsureAgeRefresh()
        {
            if (_model.PortalMaxAgeDays is not > 0) { _ageTimer?.Stop(); return; }
            if (_ageTimer == null)
            {
                _ageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
                _ageTimer.Tick += (_, __) => ReloadItems();
                Closed += (_, __) => _ageTimer.Stop();
            }
            _ageTimer.Start();
        }

        private void PortalFilter_Click(object sender, RoutedEventArgs e)
        {
            // Owned by the fence so it stays above it, even while fences are in front of windows.
            var dlg = new PortalFilterDialog(_model.PortalFilter, _model.PortalMaxAgeDays) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            _model.PortalFilter = string.IsNullOrWhiteSpace(dlg.Patterns) ? null : dlg.Patterns.Trim();
            _model.PortalMaxAgeDays = dlg.MaxAgeDays;
            NavigatePortal(_currentFolder);
            Changed?.Invoke(this, EventArgs.Empty); // persists
        }

        private void ChangePortalFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the folder this portal shows",
                InitialDirectory = Directory.Exists(_model.FolderPath) ? _model.FolderPath : ""
            };
            if (dlg.ShowDialog() == true) RetargetPortal(dlg.FolderName);
        }

        public void ReloadItems()
        {
            ItemsSource.Clear();

            List<string> entries = new();
            try
            {
                // A portal mirrors a real folder: show its subfolders and files.
                // A normal fence only holds its own .lnk shortcuts (files).
                if (_model.IsPortal)
                    entries.AddRange(Directory.EnumerateDirectories(_currentFolder));

                entries.AddRange(Directory.EnumerateFiles(_currentFolder)
                                          .Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));

                if (_model.IsPortal && Pickets.Services.PortalFilter.IsActive(_model.PortalFilter, _model.PortalMaxAgeDays))
                {
                    var patterns = Pickets.Services.PortalFilter.ParsePatterns(_model.PortalFilter);
                    var now = DateTime.UtcNow;
                    entries = entries.Where(p =>
                    {
                        bool isDir = Directory.Exists(p);
                        var written = isDir ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p);
                        return Pickets.Services.PortalFilter.Matches(Path.GetFileName(p), isDir, written,
                                                                     patterns, _model.PortalMaxAgeDays, now);
                    }).ToList();
                }
            }
            catch
            {
                // The folder may have been deleted or renamed, or its drive disconnected.
                if (_model.IsPortal) Dispatcher.BeginInvoke(CheckPortalFolderStillThere);
                return;
            }

            entries = SortEntries(entries);
            if (_model.IsPortal && _model.PortalMaxItems is int max and > 0 && entries.Count > max)
                entries = entries.Take(max).ToList();

            // Add tiles immediately (no icon yet) so the UI never blocks on shell calls.
            foreach (var path in entries)
            {
                ItemsSource.Add(new FenceItem
                {
                    Path = path,
                    DisplayName = LabelFor(path)
                });
            }

            LoadIconsAsync();
        }

        /// <summary>Resolve tile icons off the UI thread so big fences don't freeze. This MUST run
        /// on an STA thread: IconHelper uses apartment-threaded shell COM (IShellLink), which is
        /// unreliable on MTA thread-pool threads and silently drops icons. IconHelper freezes the
        /// ImageSources, so they're safe to hand back to the UI thread.</summary>
        private void LoadIconsAsync()
        {
            var snapshot = ItemsSource.ToList();
            bool thumbnails = Options?.ShowThumbnails ?? true;
            int thumbPx = (int)Math.Round(IconPx * VisualTreeHelper.GetDpi(this).DpiScaleX * 2); // room for crisp scaling
            var loader = new System.Threading.Thread(() =>
            {
                foreach (var item in snapshot)
                {
                    var details = Pickets.Services.ItemDetails.For(item.Path);
                    Dispatcher.BeginInvoke(() => item.Details = details);

                    var icon = (thumbnails && Pickets.Services.ShellThumbnail.HasPreview(item.Path)
                                   ? Pickets.Services.ShellThumbnail.Get(item.Path, thumbPx)
                                   : null)
                               ?? Pickets.Services.IconHelper.GetImageSourceForPath(item.Path);
                    if (icon == null) continue;
                    Dispatcher.BeginInvoke(() => item.Icon = icon);
                }
            })
            {
                IsBackground = true,
                Name = "FenceIconLoader"
            };
            loader.SetApartmentState(System.Threading.ApartmentState.STA);
            loader.Start();
        }

        private List<string> SortEntries(List<string> entries)
        {
            // Portals list folders before files; normal fences have no folders.
            bool dirsFirst = _model.IsPortal;
            var seeded = dirsFirst
                ? entries.OrderBy(p => Directory.Exists(p) ? 0 : 1)
                : entries.OrderBy(_ => 0);

            var oic = StringComparer.OrdinalIgnoreCase;

            IOrderedEnumerable<string> ordered = _model.Sort switch
            {
                FenceSort.Manual => seeded,
                FenceSort.Type => seeded.ThenBy(p => Path.GetExtension(p), oic)
                                        .ThenBy(p => Path.GetFileName(p), oic),
                FenceSort.DateModified => seeded.ThenByDescending(SafeWriteTime),
                FenceSort.Size => seeded.ThenByDescending(SafeSize),
                _ => seeded.ThenBy(p => Path.GetFileName(p), oic) // Name
            };

            return ordered.ToList();
        }

        private static DateTime SafeWriteTime(string p)
        {
            try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; }
        }

        private static long SafeSize(string p)
        {
            try { return Directory.Exists(p) ? 0 : new FileInfo(p).Length; } catch { return 0; }
        }

        // ---------- Sort / icon-size menu actions ----------
        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && Enum.TryParse<FenceSort>(Convert.ToString(mi.Tag), out var mode))
            {
                _model.Sort = mode;
                if (IsRealIcon) ReloadRealItems();
                else ReloadItems();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void View_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && Enum.TryParse<FenceView>(Convert.ToString(mi.Tag), out var view))
            {
                _model.View = view;
                ApplyView();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Show items as tiles or as list rows (Window.Resources: Tile*/Row*).</summary>
        private void ApplyView()
        {
            MonitorItems.ItemsPanel = (ItemsPanelTemplate)FindResource(IsListView ? "MonitorRowPanel" : "MonitorTilePanel");
            MonitorItems.ItemTemplate = (DataTemplate)FindResource(IsListView ? "MonitorRowTemplate" : "MonitorTileTemplate");
            Items.ItemsPanel = (ItemsPanelTemplate)FindResource(IsListView ? "RowPanel" : "TilePanel");
            Items.ItemTemplate = (DataTemplate)FindResource(IsListView ? "RowTemplate" : "TileTemplate");
            QueueAutoFit();
        }

        private void IconSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && Enum.TryParse<FenceIconSize>(Convert.ToString(mi.Tag), out var size))
            {
                _model.IconSize = size;
                RaiseLayoutMetricsChanged();   // tiles rebind their width/height from IconPx
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        // ---------- Search ----------
        /// <summary>Fade out items that don't match <paramref name="query"/> (null clears), and
        /// hold a rolled-up fence open while it has matches. Returns the number of matches.</summary>
        public int ApplySearch(string? query)
        {
            int matches = 0;
            foreach (var item in ItemsSource)
            {
                bool match = string.IsNullOrEmpty(query) ||
                             item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
                item.IsDimmed = !string.IsNullOrEmpty(query) && !match;
                if (match && !string.IsNullOrEmpty(query)) matches++;
            }
            // A monitor's readings aren't items: they fade with everything else that doesn't match.
            if (IsMonitor) MonitorItems.Opacity = string.IsNullOrEmpty(query) ? 1 : 0.25;
            HoldOpen(matches > 0);
            return matches;
        }

        /// <summary>While searching, fences come up above app windows so matches are visible.</summary>
        public void SetRaised(bool raised)
        {
            if (!raised && Pickets.Services.FencesInFront.IsActive) return; // search closed; stay in front
            var hwnd = new WindowInteropHelper(this).Handle;
            DesktopHelper.SetTopmost(hwnd, raised);
            if (!raised) EnsureBottomZOrder();
        }

        // ---------- Context menu ----------
        private FenceItem? MenuSenderToItem(object sender)
        {
            if (sender is FrameworkElement fe)
                return fe.DataContext as FenceItem;
            return null;
        }

        // ---------- Context menu actions ----------
        private void Item_Open_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSenderToItem(sender) is FenceItem item) LaunchPath(item.Path);
        }

        private void Item_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSenderToItem(sender) is not FenceItem item) return;
            if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return; // no containing folder
            try
            {
                var dir = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { /* ignore */ }
        }

        // Launch a desktop item: a real file/folder via ShellExecute, or a special item via its
        // shell:::{CLSID} moniker (opened through Explorer).
        internal static void LaunchPath(string path)
        {
            try
            {
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        }

        private void Item_Delete_Click(object sender, RoutedEventArgs e)
        {
            // Right-click already selected the item; delete the whole selection together.
            if (MenuSenderToItem(sender) is FenceItem item && !item.IsSelected) SelectOnly(item);
            (RequestDeleteSelected ?? DeleteOwnSelectedWithConfirm)();
        }

        public void SetWatcherEnabled(bool enabled)
        {
            try { if (_watcher != null) _watcher.EnableRaisingEvents = enabled; } catch { /* ignore */ }
        }

        // ---------- Title bar ----------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                ToggleCollapsed();
            else if (e.LeftButton == MouseButtonState.Pressed && !_model.Locked)
                DragMove();
        }

        // ---------- Frosted glass ----------
        private void Glass_Click(object sender, RoutedEventArgs e)
        {
            _model.Glass = !_model.Glass;
            // Glass needs a see-through body to show; nudge a nearly opaque fence down.
            if (_model.Glass && _model.BackgroundOpacity > 0.7) _model.BackgroundOpacity = 0.6;
            ApplyBackground();
            ApplyGlass();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyGlass()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            bool glass = _model.Glass && !Pickets.Services.Theme.IsHighContrast;
            Pickets.Services.WindowBlur.Set(hwnd, glass);
            if (glass) UpdateGlassRegion();
        }

        // The blur covers the whole window rectangle; clip it to the card's rounded corners.
        private void UpdateGlassRegion()
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Pickets.Services.WindowBlur.SetRoundedRegion(new WindowInteropHelper(this).Handle,
                (int)Math.Round(ActualWidth * s), (int)Math.Round(ActualHeight * s), (int)Math.Round(10 * s));
        }

        // ---------- Lock ----------
        private void Lock_Click(object sender, RoutedEventArgs e)
        {
            _model.Locked = !_model.Locked;
            ApplyLock();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Locked: no dragging (title bar) and no resize grips.
        private void ApplyLock()
        {
            // Work on a copy: the chrome from XAML may be frozen.
            if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            {
                var updated = (System.Windows.Shell.WindowChrome)chrome.Clone();
                updated.ResizeBorderThickness = _model.Locked ? new Thickness(0) : new Thickness(6);
                System.Windows.Shell.WindowChrome.SetWindowChrome(this, updated);
            }
            LockGlyph.Visibility = _model.Locked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetCollapsed(bool collapsed, bool animate, Action? done = null)
        {
            _model.Collapsed = collapsed;
            UpdateBackgroundPlayback();
            UpdateCountBadge();

            // Target height: collapsed -> stub; expanded -> remembered height (model.Height
            // is preserved because SaveGeometry skips writes while collapsed/animating).
            double target = collapsed ? CollapsedHeight : Math.Max(_model.Height, MinExpandedHeight);

            if (!animate)
            {
                Scroller.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                _suppressGeometrySave = true;
                Height = target;
                _suppressGeometrySave = false;
                done?.Invoke();
                return;
            }

            if (collapsed)
            {
                // Animate down, then hide the content so it doesn't overflow the stub.
                AnimateHeight(target, onCompleted: () => { Scroller.Visibility = Visibility.Collapsed; done?.Invoke(); });
            }
            else
            {
                // Show content first, then animate open.
                Scroller.Visibility = Visibility.Visible;
                AnimateHeight(target, onCompleted: done);
            }
        }

        private void AnimateHeight(double to, Action? onCompleted)
        {
            double from = ActualHeight > 0 ? ActualHeight : Height;

            _suppressGeometrySave = true;
            var anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (_, __) =>
            {
                BeginAnimation(HeightProperty, null);
                Height = to;                 // commit final value as a local value
                _suppressGeometrySave = false;
                onCompleted?.Invoke();
            };
            BeginAnimation(HeightProperty, anim);
        }

        // ---------- Roll-up: collapsed fences open temporarily on hover / drag-over ----------
        // While "temp-expanded" the model stays Collapsed, so nothing is persisted; a poll timer
        // closes it again once the cursor has left (IsMouseOver is unreliable during drag/drop).
        private bool _tempExpanded;
        private bool _holdOpen;
        private System.Windows.Threading.DispatcherTimer? _hoverTimer;
        private System.Windows.Threading.DispatcherTimer? _rollTimer;

        private void FenceWindow_MouseEnter(object sender, MouseEventArgs e)
        {
            // Pointing at a rolled-up fence brings it (title bar included) in front of others.
            if (_model.Collapsed) BringToFrontOfFences();

            if (!_model.Collapsed || _tempExpanded || Options?.ExpandCollapsedOnHover != true) return;
            _hoverTimer ??= NewTimer(350, () =>
            {
                _hoverTimer!.Stop();
                if (CursorInside()) TempExpand(true);
            });
            _hoverTimer.Start();
        }

        private void FenceWindow_MouseLeave(object sender, MouseEventArgs e) => _hoverTimer?.Stop();

        /// <summary>Keep a collapsed fence open (e.g. while search shows its matches).
        /// Releasing lets it roll back up once the cursor isn't over it.</summary>
        public void HoldOpen(bool hold)
        {
            _holdOpen = hold;
            if (hold) TempExpand(true);
        }

        private void TempExpand(bool open)
        {
            if (open)
            {
                if (!_model.Collapsed || _tempExpanded) return;
                _tempExpanded = true;
                UpdateBackgroundPlayback();
                UpdateCountBadge();
                BringToFrontOfFences(); // in front of neighbouring fences
                Scroller.Visibility = Visibility.Visible;
                AnimateHeight(Math.Max(_model.Height, MinExpandedHeight), onCompleted: null);

                _rollTimer ??= NewTimer(400, () =>
                {
                    if (_holdOpen || CursorInside() || _marqueeActive || AnyMenuOpen()) return;
                    TempExpand(false);
                });
                _rollTimer.Start();
            }
            else
            {
                if (!_tempExpanded) return;
                _tempExpanded = false;
                UpdateBackgroundPlayback();
                UpdateCountBadge();
                _rollTimer?.Stop();
                if (_model.Collapsed)
                    AnimateHeight(CollapsedHeight, onCompleted: () => Scroller.Visibility = Visibility.Collapsed);
            }
        }

        private System.Windows.Threading.DispatcherTimer NewTimer(int ms, Action tick)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => tick();
            return t;
        }

        private bool CursorInside()
        {
            var p = System.Windows.Forms.Cursor.Position;
            return DesktopHelper.WindowRectPx(new WindowInteropHelper(this).Handle).Contains(p.X, p.Y);
        }

        // Item right-click menus are ContextMenus too, but they close when the cursor leaves.
        private bool AnyMenuOpen() => FenceMenu?.IsOpen == true || _dropMenu?.IsOpen == true;

        private void Collapse_Click(object sender, RoutedEventArgs e) => ToggleCollapsed();

        // User-driven collapse/expand. Bring this fence to the front of the other fences (still
        // in the desktop layer) first, so expanding never leaves its body hidden behind a
        // neighbouring fence. Activated only re-stacks when the fence wasn't already active,
        // so re-stack explicitly too.
        internal void ToggleCollapsed()
        {
            Activate();
            Focus();
            EnsureBottomZOrder();
            BringToFrontOfFences();

            // "One open fence per stack": the others in the stack roll up as this one opens.
            if (_model.Collapsed && StacksOn && Options?.StackOneOpen == true)
                foreach (var f in StackNeighbours(belowOnly: false))
                    f.RollUpInStack();

            // Opened by hover: collapsing again would feel like nothing happened, so keep it open.
            // The fences stacked below were under its title bar; they move down now.
            if (_tempExpanded)
            {
                BeginPushFollowers();
                _tempExpanded = false;
                _rollTimer?.Stop();
                _model.Collapsed = false;
                UpdateCountBadge();
                EndPushFollowers();
                ScheduleSave();
                Announce("Opened");
                return;
            }

            BeginPushFollowers();
            SetCollapsed(!_model.Collapsed, animate: true, done: () => { EndPushFollowers(); QueueAutoFit(); });
            ScheduleSave();
            Announce(_model.Collapsed ? $"Rolled up, {CountBadge.Text}" : "Opened");
        }


        // ---------- Items ----------

        private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not FenceItem item) return;
            _cursorIndex = ItemsSource.IndexOf(item);
            FocusTile(_cursorIndex);
            if (e.ClickCount == 2) { OpenItem(item); return; }

            // Remember where a possible drag starts.
            _dragItem = item;
            _dragStart = e.GetPosition(this);
            _deferredSelectOnly = null;

            // Ctrl+click toggles; plain click selects just this one. Clicking an item that's
            // already part of a multi-selection keeps the selection until mouse-up, so the whole
            // selection can be dragged.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                item.IsSelected = !item.IsSelected;
            else if (item.IsSelected && SelectedCount > 1)
                _deferredSelectOnly = item;
            else
                SelectOnly(item);
        }

        private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_deferredSelectOnly != null) SelectOnly(_deferredSelectOnly); // a click, not a drag
            _deferredSelectOnly = null;
            _dragItem = null;
        }

        // ---------- Dragging tiles ----------
        // Tiles drag out as real files (Explorer, mail, apps) and as Pickets items, so another
        // fence takes them over and the same fence reorders them.
        private const string ItemPathsFormat = "Pickets.ItemPaths";
        private static FenceWindow? _dragSource;
        private FenceItem? _dragItem;
        private FenceItem? _deferredSelectOnly;
        private Point _dragStart;

        private void Item_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed || sender is not DependencyObject source) return;

            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _deferredSelectOnly = null;
            if (!_dragItem.IsSelected) SelectOnly(_dragItem);
            var paths = ItemsSource.Where(i => i.IsSelected).Select(i => i.Path).ToArray();
            _dragItem = null;
            if (paths.Length == 0) return;

            var data = new System.Windows.DataObject();
            data.SetData(ItemPathsFormat, paths);
            // Special items (This PC, …) aren't files; only real paths go to other apps.
            var files = paths.Where(p => !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length > 0)
                data.SetData(System.Windows.DataFormats.FileDrop, files);

            _dragSource = this;
            try
            {
                System.Windows.DragDrop.DoDragDrop(source, data,
                    System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                (System.Windows.Application.Current as App)?.SafeLog("Drag tiles", ex);
            }
            finally { _dragSource = null; }
        }

        /// <summary>Move the dragged paths to just before <paramref name="target"/> (or to the end)
        /// and switch the fence to Manual sort so the order sticks.</summary>
        private void ReorderItems(IReadOnlyCollection<string> moving, FenceItem? target)
        {
            if (!IsRealIcon) return;

            // Start from what's on screen, so switching from another sort keeps the visible order.
            var order = ItemsSource.Select(i => i.Path).ToList();
            foreach (var p in _model.ItemPaths)
                if (!order.Contains(p, StringComparer.OrdinalIgnoreCase)) order.Add(p);

            var set = new HashSet<string>(moving, StringComparer.OrdinalIgnoreCase);
            if (target != null && set.Contains(target.Path)) return; // dropped onto itself

            var moved = order.Where(set.Contains).ToList();
            order.RemoveAll(set.Contains);
            int at = target == null ? order.Count
                                    : Math.Max(0, order.FindIndex(p => string.Equals(p, target.Path, StringComparison.OrdinalIgnoreCase)));
            order.InsertRange(at, moved);

            Undoable("Rearrange items", () =>
            {
                _model.ItemPaths = order;
                _model.Sort = FenceSort.Manual;
                ReloadRealItems();
                foreach (var i in ItemsSource) i.IsSelected = set.Contains(i.Path);
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        // Right-clicking an unselected item selects just it (so the menu acts on it).
        private void Item_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FenceItem item && !item.IsSelected)
                SelectOnly(item);
        }

        // Right-click shows Windows' own menu for the selection (Open with, Send to, Properties…)
        // with "Remove from fence" on top; special items like This PC use our own menu.
        private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not FenceItem item) return;
            e.Handled = true;

            var paths = ItemsSource.Where(i => i.IsSelected).Select(i => i.Path).ToList();
            if (paths.Count == 0) paths.Add(item.Path);

            bool special = paths.Any(p => p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
            if (!special)
            {
                var extras = new List<Pickets.Services.ShellContextMenu.Extra>
                {
                    new("Quick Look\tSpace", () => QuickLookSelected(item))
                };
                if (IsRealIcon)
                {
                    var moving = paths.ToList();
                    extras.Add(new("Move to fence…", () => ShowMoveToMenu(moving)));
                    extras.Add(new("Move to new fence", () => RequestNewFenceWithItems?.Invoke(this, moving)));
                }
                if (CanRemoveSelected)
                    extras.Add(new("Remove from fence", () => RequestRemoveSelected?.Invoke()));

                var hwnd = new WindowInteropHelper(this).Handle;
                if (Pickets.Services.ShellContextMenu.Show(hwnd, paths, extras, () => RenameItem(item)))
                    return;
            }

            if (fe.ContextMenu is ContextMenu menu)
            {
                menu.PlacementTarget = fe;
                menu.DataContext = item;
                menu.IsOpen = true;
            }
        }

        // ---------- Tabs ----------
        private bool HasTabs => Pickets.Services.FenceTabs.HasTabs(_model);
        public int TabCount => HasTabs ? _model.Tabs.Count : 0;

        /// <summary>Rebuild the tab headers (or hide the strip when there are no tabs).</summary>
        private void RebuildTabs()
        {
            TabHeaders.Children.Clear();
            UpdateTabStripVisibility();
            QueueAutoFit(); // the tab strip takes room
            if (!HasTabs) return;

            for (int i = 0; i < _model.Tabs.Count; i++)
            {
                int index = i;
                bool active = i == _model.ActiveTab;
                var label = new TextBlock
                {
                    Text = _model.Tabs[i].Name,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Fence.LabelFg" : "Fence.Hint");
                var header = new Border
                {
                    Child = label,
                    Padding = new Thickness(10, 3, 10, 4),
                    Margin = new Thickness(0, 0, 4, 2),
                    CornerRadius = new CornerRadius(6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    AllowDrop = true,
                    ToolTip = "Click to show · drop tiles here to move them to this tab · right-click for options"
                };
                if (active) header.Background = Pickets.Services.Theme.IsHighContrast
                    ? Pickets.Services.Theme.ContrastBrush(Pickets.Services.ContrastRole.Highlight)
                    : new SolidColorBrush(MediaColor.FromArgb(0x55, 0x5A, 0x8F, 0xD8));
                else header.SetResourceReference(Border.BackgroundProperty, "Fence.ItemHover");
                header.MouseLeftButtonUp += (_, __) => SwitchTab(index);
                header.DragOver += (_, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(ItemPathsFormat) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
                    e.Handled = true;
                };
                header.Drop += (_, e) => DropOnTab(e, index);

                var menu = new ContextMenu();
                var rename = new MenuItem { Header = "Rename tab…" };
                rename.Click += (_, __) => RenameTab(index);
                menu.Items.Add(rename);
                if (index > 0)
                {
                    var remove = new MenuItem { Header = "Delete tab (items move to the first tab)" };
                    remove.Click += (_, __) => DeleteTab(index);
                    menu.Items.Add(remove);
                }
                header.ContextMenu = menu;

                TabHeaders.Children.Add(header);
            }
        }

        // Tabs and breadcrumbs belong to the body, so they hide while the fence is rolled up.
        private void UpdateTabStripVisibility()
        {
            bool bodyShown = Scroller.Visibility == Visibility.Visible;
            TabStrip.Visibility = HasTabs && bodyShown ? Visibility.Visible : Visibility.Collapsed;
            if (!bodyShown) BreadcrumbBar.Visibility = Visibility.Collapsed;
            else if (IsInSubfolder) BreadcrumbBar.Visibility = Visibility.Visible;
        }

        private void SwitchTab(int index)
        {
            if (!HasTabs || index < 0 || index >= _model.Tabs.Count || index == _model.ActiveTab) return;
            _model.ActiveTab = index;
            _cursorIndex = -1;
            ReloadRealItems();
            RebuildTabs();
            Changed?.Invoke(this, EventArgs.Empty);
            Announce($"Tab {_model.Tabs[index].Name}, {index + 1} of {_model.Tabs.Count}, " +
                     (ItemsSource.Count == 1 ? "1 item" : $"{ItemsSource.Count} items"));
            Dispatcher.BeginInvoke(FocusCurrentTile, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AddTab_Click(object sender, RoutedEventArgs e)
        {
            if (IsPortal) return;
            var prompt = new InputDialog("Add Tab", "Name for the new tab:", $"Tab {Math.Max(2, _model.Tabs.Count + 1)}") { Owner = this };
            if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;

            Undoable("Add tab", () =>
            {
                _model.ActiveTab = Pickets.Services.FenceTabs.Add(_model, prompt.Value.Trim());
                ReloadRealItems();
                RebuildTabs();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        private void RenameTab(int index)
        {
            var prompt = new InputDialog("Rename Tab", "New name for this tab:", _model.Tabs[index].Name) { Owner = this };
            if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
            Undoable("Rename tab", () =>
            {
                _model.Tabs[index].Name = prompt.Value.Trim();
                RebuildTabs();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        private void DeleteTab(int index)
        {
            var name = _model.Tabs[index].Name;
            if (MessageBox.Show($"Delete the tab “{name}”?\n\nIts items move to the “{_model.Tabs[0].Name}” tab. Nothing is deleted from your desktop.",
                                "Delete Tab", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Undoable($"Delete tab “{name}”", () =>
            {
                Pickets.Services.FenceTabs.Remove(_model, index);
                ReloadRealItems();
                RebuildTabs();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        // Tiles dropped on a tab header move to that tab (from this fence, or from another).
        private void DropOnTab(System.Windows.DragEventArgs e, int index)
        {
            if (e.Data.GetData(ItemPathsFormat) is not string[] paths || paths.Length == 0 || _dragSource is not { IsPortal: false } source) return;
            e.Handled = true;

            Undoable(MoveDescription(paths.Length, $"“{_model.Tabs[index].Name}”"), () =>
            {
                if (!ReferenceEquals(source, this)) AssignToThisFence(paths);
                Pickets.Services.FenceTabs.MoveTo(_model, paths, index);
                ReloadRealItems();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        /// <summary>Every item for search, including ones on tabs that aren't showing (those
        /// come without an icon), with where to find it.</summary>
        public IEnumerable<(FenceItem Item, string Where)> SearchableItems()
        {
            foreach (var i in ItemsSource) yield return (i, HasTabs ? $"{FenceName} › {_model.Tabs[_model.ActiveTab].Name}" : FenceName);
            if (!HasTabs) yield break;
            foreach (var p in _model.ItemPaths)
            {
                int tab = Pickets.Services.FenceTabs.TabOf(_model, p);
                if (tab == _model.ActiveTab) continue;
                yield return (new FenceItem { Path = p, DisplayName = LabelFor(p) }, $"{FenceName} › {_model.Tabs[tab].Name}");
            }
        }

        // ---------- Moving items to another fence ----------
        /// <summary>Wired by MainWindow: create a new fence next to this one holding these items.</summary>
        public static Action<FenceWindow, IReadOnlyList<string>>? RequestNewFenceWithItems;

        /// <summary>Names of this fence's tabs (empty when it has none).</summary>
        public IReadOnlyList<string> TabNames => HasTabs ? _model.Tabs.Select(t => t.Name).ToList() : new List<string>();

        /// <summary>Take these desktop items into this fence (off any other), optionally onto a tab.</summary>
        public void TakeItems(IReadOnlyList<string> paths, int tab = -1)
        {
            if (!IsRealIcon || paths.Count == 0) return;
            string where = tab >= 0 && HasTabs ? $"“{_model.Tabs[tab].Name}”" : $"“{FenceName}”";
            Undoable(MoveDescription(paths.Count, where), () =>
            {
                AssignToThisFence(paths);
                if (tab >= 0 && HasTabs) MoveToTab(paths, tab);
            });
        }

        /// <summary>"Move 3 items to “Apps”" (undo step names).</summary>
        internal static string MoveDescription(int count, string where) =>
            (count == 1 ? "Move 1 item" : $"Move {count} items") + " to " + where;

        private void MoveToTab(IReadOnlyList<string> paths, int tab)
        {
            Pickets.Services.FenceTabs.MoveTo(_model, paths, tab);
            ReloadRealItems();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>"Move to fence…": a menu of the other fences (and this fence's other tabs).</summary>
        private void ShowMoveToMenu(List<string> paths)
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

            if (HasTabs)
            {
                for (int i = 0; i < _model.Tabs.Count; i++)
                {
                    if (i == _model.ActiveTab) continue;
                    int tab = i;
                    var mi = new MenuItem { Header = $"This fence › {_model.Tabs[i].Name}" };
                    mi.Click += (_, __) => TakeItems(paths, tab);
                    menu.Items.Add(mi);
                }
            }

            var others = AllFences.Where(f => f != this && !f.IsPortal && !f.IsMonitor)
                                  .OrderBy(f => f.FenceName, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (menu.Items.Count > 0 && others.Count > 0) menu.Items.Add(new Separator());
            foreach (var f in others)
            {
                var target = f;
                var tabs = target.TabNames;
                var mi = new MenuItem { Header = target.FenceName + (target.IsClosedByUser ? " (closed)" : "") };
                if (tabs.Count > 0)
                {
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        int tab = i;
                        var sub = new MenuItem { Header = tabs[i] };
                        sub.Click += (_, __) => target.TakeItems(paths, tab);
                        mi.Items.Add(sub);
                    }
                }
                else
                {
                    mi.Click += (_, __) => target.TakeItems(paths);
                }
                menu.Items.Add(mi);
            }

            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var create = new MenuItem { Header = "New fence…" };
            create.Click += (_, __) => RequestNewFenceWithItems?.Invoke(this, paths);
            menu.Items.Add(create);
            menu.IsOpen = true;
        }

        private List<string> SelectedOrClicked(object sender)
        {
            if (MenuSenderToItem(sender) is FenceItem item && !item.IsSelected) SelectOnly(item);
            return ItemsSource.Where(i => i.IsSelected).Select(i => i.Path).ToList();
        }

        private void Item_MoveToFence_Click(object sender, RoutedEventArgs e) => ShowMoveToMenu(SelectedOrClicked(sender));

        private void Item_MoveToNewFence_Click(object sender, RoutedEventArgs e) =>
            RequestNewFenceWithItems?.Invoke(this, SelectedOrClicked(sender));

        /// <summary>Ask for a new name right away (used for a freshly created fence).</summary>
        public void PromptRename() => Rename_Click(this, new RoutedEventArgs());

        // ---------- Small conveniences ----------

        // Rolled up: show how many items are inside ("12 items") at the right of the title.
        private void UpdateCountBadge()
        {
            bool rolledUp = _model.Collapsed && !_tempExpanded && !IsMonitor;
            int n = ItemCount;
            CountBadge.Text = n == 1 ? "1 item" : $"{n} items";
            CountBadge.Visibility = rolledUp ? Visibility.Visible : Visibility.Collapsed;
        }

        // An empty fence says what to do instead of being a blank box.
        private void UpdateEmptyHint()
        {
            EmptyHint.Text = IsMonitor ? "Pick what to show: fence menu → Show"
                : !IsPortal ? "Drag items here"
                : Pickets.Services.PortalFilter.IsActive(_model.PortalFilter, _model.PortalMaxAgeDays) ? "Nothing matches the filter"
                : "This folder is empty";
            int shown = IsMonitor ? MonitorTiles.Count : ItemsSource.Count;
            // Only with the body showing: a rolled-up fence would show it cut off under the title.
            EmptyHint.Visibility = shown == 0 && Scroller.Visibility == Visibility.Visible && !IsPortalUnavailable
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // Tooltip with the full name plus type, size, date and folder.
        private void Item_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not FenceItem item) return;
            var details = item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? "Windows item"
                : QuickLookWindow.Details(item.Path).Replace(" · ", "\n");
            fe.ToolTip = item.DisplayName + "\n" + details;
        }

        // Ctrl + mouse wheel: bigger or smaller icons, like Explorer.
        private void FenceWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            var next = e.Delta > 0
                ? (FenceIconSize)Math.Min((int)FenceIconSize.Large, (int)_model.IconSize + 1)
                : (FenceIconSize)Math.Max((int)FenceIconSize.Small, (int)_model.IconSize - 1);
            if (next == _model.IconSize) return;
            _model.IconSize = next;
            RaiseLayoutMetricsChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Opens everything on the current tab, e.g. a "Work" fence that starts your work apps.
        private void OpenAll_Click(object sender, RoutedEventArgs e)
        {
            var paths = ItemsSource.Select(i => i.Path).ToList();
            if (paths.Count == 0) return;
            if (paths.Count > 8 &&
                MessageBox.Show(this, $"Open all {paths.Count} items?", "Open all",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (var p in paths) LaunchPath(p);
        }

        // The other half of Open all: politely closes the running apps the current tab starts.
        private void CloseAll_Click(object sender, RoutedEventArgs e)
        {
            var apps = Pickets.Services.AppCloser.FindRunning(ItemsSource.Select(i => i.Path));
            if (apps.Count == 0)
            {
                MessageBox.Show(this, "None of the apps in this fence are running.", "Close all",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string names = string.Join(Environment.NewLine, apps.Select(a => "  • " + a.Name));
            string what = apps.Count == 1 ? apps[0].Name : $"these {apps.Count} apps";
            if (MessageBox.Show(this, $"Close {what}?{Environment.NewLine}{Environment.NewLine}{names}" +
                                      $"{Environment.NewLine}{Environment.NewLine}Apps with unsaved work will ask you to save it first.",
                                "Close all", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Pickets.Services.AppCloser.Close(apps);
        }

        // Fit to contents: narrow the fence if it has room to spare, then make it exactly as tall
        // as its tiles (no scrollbar), within the screen.
        private void FitToContents_Click(object sender, RoutedEventArgs e)
        {
            if (_model.Collapsed) SetCollapsed(false, animate: false);
            int n = IsMonitor ? MonitorTiles.Count : ItemsSource.Count;
            if (n == 0) return;

            // Icons: narrow a fence whose tiles don't fill one row. A list keeps its width.
            if (!IsListView)
            {
                double tile = IsMonitor ? MonitorTileWidth + 8 : TileWidth;
                int cols = Math.Max(1, (int)(Scroller.ViewportWidth / tile));
                if (n < cols)
                    Width = Math.Max(200, Width - Scroller.ViewportWidth + n * tile + 4);
            }
            SetHeightWithStack(FittedHeight());
            ScheduleSave();
        }

        /// <summary>The height that shows every tile without a scrollbar, within the screen.</summary>
        private double FittedHeight()
        {
            UpdateLayout();
            double chrome = ActualHeight - Scroller.ViewportHeight;
            double content = IsMonitor ? (MonitorTiles.Count > 0 ? MonitorItems.ActualHeight : 0)
                                       : (ItemsSource.Count > 0 ? Items.ActualHeight : 0);
            double wanted = chrome + content + 4;
            double maxHeight = Pickets.Services.ScreenLayout.WorkAreas()
                .Where(a => a.Contains(new Point(Left + 10, Top + 10)))
                .Select(a => a.Bottom - Top).DefaultIfEmpty(wanted).First();
            return Math.Max(MinExpandedHeight, Math.Min(wanted, maxHeight));
        }

        // "Keep fitted to contents": the fence grows and shrinks with its items. Changes come in
        // bursts (a reload adds tiles one by one), so fit once things settle.
        private bool _autoFitQueued;

        private void QueueAutoFit()
        {
            if (!_model.AutoHeight || _autoFitQueued) return;
            _autoFitQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _autoFitQueued = false;
                if (!_model.AutoHeight || !IsLoaded || _model.Collapsed || _inSizeMove ||
                    Scroller.Visibility != Visibility.Visible) return;
                double h = FittedHeight();
                if (Math.Abs(h - ActualHeight) < 1) return;
                SetHeightWithStack(h);
                ScheduleSave();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AutoHeight_Click(object sender, RoutedEventArgs e)
        {
            _model.AutoHeight = !_model.AutoHeight;
            if (_model.AutoHeight && _model.Collapsed) ToggleCollapsed(); // fitting needs it open
            QueueAutoFit();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ---------- Quick Look ----------
        /// <summary>Preview the selected item (or the one under the keyboard cursor) in a large
        /// window; ←/→ there step through this fence's items.</summary>
        private void QuickLookSelected(FenceItem? item = null)
        {
            item ??= ItemsSource.FirstOrDefault(i => i.IsSelected)
                     ?? (_cursorIndex >= 0 && _cursorIndex < ItemsSource.Count ? ItemsSource[_cursorIndex] : null);
            if (item == null) return;

            var list = ItemsSource.ToList();
            new QuickLookWindow(list, list.IndexOf(item)).Show();
        }

        private void Item_QuickLook_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSenderToItem(sender) is FenceItem item) QuickLookSelected(item);
        }

        // ---------- Rename items ----------
        /// <summary>Rename the real file/folder behind a tile. Shortcut extensions (.lnk/.url)
        /// stay hidden, like on the desktop. A renamed item stays in this fence.</summary>
        private void RenameItem(FenceItem item)
        {
            var path = item.Path;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return;

            bool isDir = Directory.Exists(path);
            string ext = Path.GetExtension(path);
            bool hideExt = !isDir && (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                      ext.Equals(".url", StringComparison.OrdinalIgnoreCase));
            string current = hideExt ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);

            var prompt = new InputDialog("Rename", "New name:", current) { Owner = this };
            if (prompt.ShowDialog() != true) return;

            var name = prompt.Value.Trim();
            if (name.Length == 0 || name == current) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("A name can't contain any of these characters:\n\\ / : * ? \" < > |",
                                "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dir = Path.GetDirectoryName(path)!;
            var newPath = Path.Combine(dir, hideExt ? name + ext : name);
            bool caseOnly = string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                MessageBox.Show($"There's already an item called “{Path.GetFileName(newPath)}” there.",
                                "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Update the fence first, so the file watcher sees a path we already own.
            bool owned = IsRealIcon && ReplaceItemPath(path, newPath);
            try
            {
                if (isDir) Directory.Move(path, newPath);
                else File.Move(path, newPath);
            }
            catch (Exception ex)
            {
                if (owned) ReplaceItemPath(newPath, path);
                MessageBox.Show("The item couldn't be renamed:\n" + ex.Message, "Rename",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!IsRealIcon) ReloadItems();
        }

        // A plain click (or Esc, or a fresh marquee) replaces the whole selection, not just this
        // fence's: Delete, Enter and Remove act on every fence's selection, so one left behind in
        // another fence would go too. Ctrl+click and Ctrl-marquee add to it instead.
        private void SelectOnly(FenceItem? item)
        {
            foreach (var i in ItemsSource)
                i.IsSelected = ReferenceEquals(i, item);
            foreach (var other in AllFences)
                if (other != this) other.ClearSelection();
        }

        // In a portal, folders open inside the portal (Ctrl+double-click opens Explorer instead).
        private void OpenItem(FenceItem item)
        {
            if (_model.IsPortal && Directory.Exists(item.Path) &&
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                NavigatePortal(item.Path);
                return;
            }
            LaunchPath(item.Path);
        }

        // MainWindow wires these so Delete/Enter act on the WHOLE selection across all
        // fences (e.g. after a desktop lasso). Fallback to this fence if not wired.
        public static Action? RequestDeleteSelected;
        public static Action? RequestOpenSelected;

        public bool IsPortal => _model.IsPortal;
        public int SelectedCount => ItemsSource.Count(i => i.IsSelected);
        public void ClearSelection()
        {
            foreach (var i in ItemsSource) i.IsSelected = false;
        }

        public void OpenSelectedItems()
        {
            foreach (var item in ItemsSource.Where(i => i.IsSelected).ToList()) OpenItem(item);
        }

        public static Action? RequestRemoveSelected;

        /// <summary>Paths of the selected items (used by "Remove from fence").</summary>
        public IEnumerable<string> SelectedPaths => ItemsSource.Where(i => i.IsSelected).Select(i => i.Path).ToList();

        /// <summary>True when "Remove from fence" means something here: a real fence other than
        /// the catch-all (where removed items go) with a selection.</summary>
        public bool CanRemoveSelected => IsRealIcon && !IsCatchAllFence && SelectedCount > 0;

        private bool IsCatchAllFence =>
            string.Equals(_model.Name, MainWindow.CatchAllFenceName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The one delete confirmation. It says plainly that real files are deleted
        /// (to the Recycle Bin), and points at "Remove from fence" when that applies.</summary>
        public static bool ConfirmDelete(int count, bool offerRemove)
        {
            string what = count == 1 ? "the selected item" : $"{count} selected items";
            string msg = $"Move {what} to the Recycle Bin?\n\n" +
                         "This deletes the actual file(s) or folder(s) from your computer, not just from the fence.";
            if (offerRemove)
                msg += "\n\nTo only take items out of a fence, right-click and choose “Remove from fence” instead.";
            return MessageBox.Show(msg, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                                   MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        public static void ReportDeleteFailures(IReadOnlyCollection<string> failed)
        {
            if (failed.Count == 0) return;
            var names = string.Join("\n", failed.Take(10).Select(p => "• " + Path.GetFileName(p)));
            if (failed.Count > 10) names += $"\n…and {failed.Count - 10} more";
            MessageBox.Show($"These items couldn't be deleted (they may be in use, need admin rights, " +
                            $"or the delete was cancelled):\n\n{names}",
                            "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Sends this fence's selected items to the Recycle Bin without prompting (caller
        // confirms). Returns the paths that couldn't be deleted.
        public List<string> DeleteSelectedItemsNoConfirm()
        {
            var failed = new List<string>();
            var selected = ItemsSource.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return failed;

            SetWatcherEnabled(false);
            try
            {
                foreach (var item in selected)
                {
                    // Special (CLSID) items have no file to delete — just release them.
                    bool gone = item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                                || Pickets.Services.RecycleBin.Send(item.Path);
                    if (!gone) { failed.Add(item.Path); continue; }

                    // Real fences track ownership by path; drop it so it doesn't linger.
                    _model.ItemPaths.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
                    ItemsSource.Remove(item);
                }
                if (IsRealIcon) Changed?.Invoke(this, EventArgs.Empty);
            }
            finally { SetWatcherEnabled(true); }
            return failed;
        }

        // Selects items whose on-screen bounds fall inside a physical-pixel rect
        // (used by the cross-fence desktop lasso).
        public void SelectItemsInScreenRectPx(Rect lassoPx, bool additive)
        {
            for (int i = 0; i < ItemsSource.Count; i++)
            {
                if (Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                Rect itemPx;
                try
                {
                    var tl = c.PointToScreen(new Point(0, 0));
                    var br = c.PointToScreen(new Point(c.ActualWidth, c.ActualHeight));
                    itemPx = new Rect(tl, br);
                }
                catch { continue; }

                if (lassoPx.IntersectsWith(itemPx)) ItemsSource[i].IsSelected = true;
                else if (!additive) ItemsSource[i].IsSelected = false;
            }
        }

        // Fallback delete (single fence) used only if MainWindow hasn't wired the global one.
        private void DeleteOwnSelectedWithConfirm()
        {
            int n = SelectedCount;
            if (n == 0) return;
            if (!ConfirmDelete(n, offerRemove: CanRemoveSelected)) return;
            ReportDeleteFailures(DeleteSelectedItemsNoConfirm());
        }

        // Item menu: "Remove from fence" only applies to real fences other than the catch-all.
        private void ItemMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;
            foreach (var obj in menu.Items)
                if (obj is MenuItem mi)
                {
                    if (Equals(mi.Tag, "RemoveFromFence"))
                        mi.Visibility = IsRealIcon && !IsCatchAllFence ? Visibility.Visible : Visibility.Collapsed;
                    else if (Equals(mi.Tag, "MoveToFence") || Equals(mi.Tag, "MoveToNewFence"))
                        mi.Visibility = IsRealIcon ? Visibility.Visible : Visibility.Collapsed;
                }
        }

        private void Item_RemoveFromFence_Click(object sender, RoutedEventArgs e)
        {
            if (MenuSenderToItem(sender) is FenceItem item && !item.IsSelected) SelectOnly(item);
            RequestRemoveSelected?.Invoke();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsMonitor && e.Key is Key.Delete or Key.Enter) return; // no items to act on here
            if (e.Key == Key.Delete) { (RequestDeleteSelected ?? DeleteOwnSelectedWithConfirm)(); e.Handled = true; }
            else if (e.Key == Key.Enter) { (RequestOpenSelected ?? OpenSelectedItems)(); e.Handled = true; }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                foreach (var i in ItemsSource) i.IsSelected = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                RequestUndo?.Invoke();
                e.Handled = true;
            }
        }

        // ---------- Keyboard navigation ----------
        // The item the arrow keys move from (last clicked or keyed to).
        private int _cursorIndex = -1;

        // ScrollViewer consumes arrow keys in its own KeyDown, so navigation runs in Preview.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            switch (key)
            {
                case Key.F1:
                    RequestKeyboardHelp?.Invoke();
                    e.Handled = true;
                    return;
                case Key.F6:
                    FocusNextFence((mods & ModifierKeys.Shift) != 0 ? -1 : 1);
                    e.Handled = true;
                    return;
                case Key.F2:
                    // F2 renames the selected item; with nothing (or several) selected, the fence.
                    var sel = ItemsSource.Where(i => i.IsSelected).ToList();
                    if (sel.Count == 1) RenameItem(sel[0]);
                    else Rename_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.Escape:
                    SelectOnly(null);
                    Pickets.Services.FencesInFront.Exit();
                    e.Handled = true;
                    return;
                case Key.Tab when (mods & ModifierKeys.Control) != 0 && HasTabs:
                    int dir = (mods & ModifierKeys.Shift) != 0 ? -1 : 1;
                    SwitchTab((_model.ActiveTab + dir + _model.Tabs.Count) % _model.Tabs.Count);
                    e.Handled = true;
                    return;
                case Key.Space when mods == ModifierKeys.None:
                    QuickLookSelected();
                    e.Handled = true;
                    return;
                case Key.Back:
                case Key.Left when mods == ModifierKeys.Alt:
                    if (IsInSubfolder) { PortalUp(); e.Handled = true; }
                    return;
            }

            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
            if (IsMonitor)
            {
                if (mods == ModifierKeys.None && HandleMonitorKey(key)) e.Handled = true;
                return;
            }
            if (ItemsSource.Count == 0) return;

            int count = ItemsSource.Count;
            int cur = _cursorIndex >= 0 && _cursorIndex < count ? _cursorIndex : -1;
            int cols = IsListView ? 1 : Math.Max(1, (int)(Scroller.ViewportWidth / TileWidth));
            int next = key switch
            {
                Key.Right => cur < 0 ? 0 : Math.Min(count - 1, cur + 1),
                Key.Left => cur < 0 ? 0 : Math.Max(0, cur - 1),
                Key.Down => cur < 0 ? 0 : Math.Min(count - 1, cur + cols),
                Key.Up => cur < 0 ? 0 : Math.Max(0, cur - cols),
                Key.Home => 0,
                Key.End => count - 1,
                _ => NextByLetter(key, cur)
            };
            if (next < 0) return;

            // Shift extends the selection; otherwise the keyed-to item becomes the selection.
            if ((mods & ModifierKeys.Shift) == ModifierKeys.Shift) ItemsSource[next].IsSelected = true;
            else SelectOnly(ItemsSource[next]);

            _cursorIndex = next;
            (Items.ItemContainerGenerator.ContainerFromIndex(next) as FrameworkElement)?.BringIntoView();
            FocusTile(next);
            e.Handled = true;
        }

        // Typing a letter or digit jumps to the next item whose name starts with it.
        private int NextByLetter(Key key, int cur)
        {
            char c;
            if (key >= Key.A && key <= Key.Z) c = (char)('a' + (key - Key.A));
            else if (key >= Key.D0 && key <= Key.D9) c = (char)('0' + (key - Key.D0));
            else return -1;

            int count = ItemsSource.Count;
            for (int step = 1; step <= count; step++)
            {
                int i = (Math.Max(cur, -1) + step) % count;
                var name = ItemsSource[i].DisplayName;
                if (name.Length > 0 && char.ToLowerInvariant(name[0]) == c) return i;
            }
            return -1;
        }

        // ---------- Marquee (rubber-band) selection ----------
        private bool _marqueeActive;
        private bool _marqueeAdditive;
        private bool _marqueeMoved;
        private Point _marqueeStart;

        private void Scroller_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Clicks that land on an item are handled by the item itself. Monitors have nothing to select.
            if (IsMonitor || FindItem(e.OriginalSource as DependencyObject) != null) return;

            Scroller.Focus();
            _marqueeAdditive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (!_marqueeAdditive) SelectOnly(null);

            _marqueeActive = true;
            _marqueeMoved = false;
            _marqueeStart = e.GetPosition(MarqueeLayer);
            UpdateMarquee(_marqueeStart);
            MarqueeRect.Visibility = Visibility.Visible;
            Scroller.CaptureMouse();
        }

        private void Scroller_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_marqueeActive) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }

            var cur = e.GetPosition(MarqueeLayer);
            if (!_marqueeMoved &&
                (Math.Abs(cur.X - _marqueeStart.X) > 3 || Math.Abs(cur.Y - _marqueeStart.Y) > 3))
                _marqueeMoved = true;

            UpdateMarquee(cur);
            SelectWithinMarquee();
        }

        private void Scroller_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndMarquee();

        private void EndMarquee()
        {
            if (!_marqueeActive) return;
            _marqueeActive = false;
            if (Scroller.IsMouseCaptured) Scroller.ReleaseMouseCapture();
            MarqueeRect.Visibility = Visibility.Collapsed;
        }

        private void UpdateMarquee(Point cur)
        {
            double x = Math.Min(cur.X, _marqueeStart.X);
            double y = Math.Min(cur.Y, _marqueeStart.Y);
            Canvas.SetLeft(MarqueeRect, x);
            Canvas.SetTop(MarqueeRect, y);
            MarqueeRect.Width = Math.Abs(cur.X - _marqueeStart.X);
            MarqueeRect.Height = Math.Abs(cur.Y - _marqueeStart.Y);
        }

        private void SelectWithinMarquee()
        {
            var box = new Rect(Canvas.GetLeft(MarqueeRect), Canvas.GetTop(MarqueeRect),
                               MarqueeRect.Width, MarqueeRect.Height);

            for (int i = 0; i < ItemsSource.Count; i++)
            {
                if (Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                Rect b;
                try { b = c.TransformToVisual(MarqueeLayer).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight)); }
                catch { continue; }

                if (box.IntersectsWith(b)) ItemsSource[i].IsSelected = true;
                else if (!_marqueeAdditive) ItemsSource[i].IsSelected = false;
            }
        }

        private static FenceItem? FindItem(DependencyObject? start)
        {
            while (start != null)
            {
                if (start is FrameworkElement fe && fe.DataContext is FenceItem item)
                    return item;
                start = VisualTreeHelper.GetParent(start);
            }
            return null;
        }

        // WPF DragEventArgs explicitly (avoid WinForms ambiguity)
        private void FenceWindow_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            if (_model.Collapsed) TempExpand(true); // let the user drop into a rolled-up fence
            FenceWindow_DragOver(sender, e);
        }

        private void FenceWindow_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            // Tiles from a fence move (to this fence, or within it); files from elsewhere copy.
            if (e.Data.GetDataPresent(ItemPathsFormat) && IsRealIcon)
                e.Effects = System.Windows.DragDropEffects.Move;
            else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                e.Effects = System.Windows.DragDropEffects.Copy;
            else
                e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void FenceWindow_Drop(object sender, System.Windows.DragEventArgs e)
        {
            // Tiles dragged from a normal fence: reorder within the same fence, or take them over.
            // (Portal tiles are files from elsewhere; they go through the file-drop path below.)
            if (IsRealIcon && _dragSource is { IsPortal: false } &&
                e.Data.GetData(ItemPathsFormat) is string[] fencePaths && fencePaths.Length > 0)
            {
                e.Handled = true;
                if (ReferenceEquals(_dragSource, this))
                    ReorderItems(fencePaths, FindItem(e.OriginalSource as DependencyObject));
                else
                    AssignToThisFence(fencePaths);
                return;
            }

            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;

            if (!IsRealIcon)
            {
                // A single folder dropped on a portal: offer to show that folder here.
                if (paths.Length == 1 && Directory.Exists(paths[0]))
                {
                    PromptPortalFolderDrop(paths[0]);
                    return;
                }

                // Files: copy them into the folder being shown. Dropped folders are skipped
                // for safety (no recursive copies by accident).
                foreach (var p in paths)
                {
                    try
                    {
                        if (Directory.Exists(p)) continue;
                        var dest = Path.Combine(_currentFolder, Path.GetFileName(p));
                        if (!File.Exists(dest)) File.Copy(p, dest);
                    }
                    catch { /* ignore */ }
                }
                ReloadItems();
                return;
            }

            // Real fence: own the ACTUAL desktop items and render them as tiles. Items already
            // on the desktop are assigned as-is (moved out of whatever fence held them). Items
            // from elsewhere aren't desktop items yet, so we ask how to bring them onto the
            // desktop first (move the real file, or drop a shortcut).
            var onDesktop = new List<string>();  // desktop paths ready to assign now
            var offDesktop = new List<string>(); // source paths needing the user's decision
            foreach (var p in paths)
            {
                if (Pickets.Services.DesktopItems.IsOnDesktop(p)) onDesktop.Add(p);
                else offDesktop.Add(p);
            }

            if (onDesktop.Count > 0) AssignToThisFence(onDesktop);

            if (offDesktop.Count > 0)
                PromptOffDesktopDrop(offDesktop);
        }

        // Route an assignment through MainWindow so the items leave any other fence and get
        // persisted; fall back to a local add if the app hasn't wired the callback.
        private void AssignToThisFence(IReadOnlyList<string> desktopPaths)
        {
            if (RequestAssignItems != null) RequestAssignItems(this, desktopPaths);
            else AddItems(desktopPaths);
        }

        private ContextMenu? _dropMenu;

        private void PromptPortalFolderDrop(string folder)
        {
            var menu = _dropMenu = new ContextMenu
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
            };
            var show = new MenuItem { Header = $"Show “{new DirectoryInfo(folder).Name}” in this portal" };
            show.Click += (_, __) => RetargetPortal(folder);
            menu.Items.Add(show);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Cancel" });
            menu.IsOpen = true;
        }

        /// <summary>Ask (via a menu at the drop point) how to bring off-desktop items onto the
        /// desktop, then assign the resulting real desktop items to this fence.</summary>
        private void PromptOffDesktopDrop(List<string> sources)
        {
            string userDesktop = Pickets.Services.Sandbox.UserDesktop;
            string what = sources.Count == 1 ? $"\"{Path.GetFileName(sources[0])}\"" : $"{sources.Count} items";

            var menu = _dropMenu = new ContextMenu
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
            };

            var move = new MenuItem { Header = $"Move {what} to the desktop" };
            move.Click += (_, __) => CompleteOffDesktopDrop(sources, userDesktop, asShortcut: false);

            var link = new MenuItem { Header = "Create a shortcut on the desktop" };
            link.Click += (_, __) => CompleteOffDesktopDrop(sources, userDesktop, asShortcut: true);

            menu.Items.Add(move);
            menu.Items.Add(link);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Cancel" });
            menu.IsOpen = true;
        }

        /// <summary>"Notes.lnk", or "Notes (2).lnk" and so on when that name is taken, so a drop
        /// never overwrites (or takes over) something already on the desktop.</summary>
        internal static string UniqueDesktopPath(string folder, string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
            var path = Path.Combine(folder, fileName);
            for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
                path = Path.Combine(folder, $"{name} ({n}){ext}");
            return path;
        }

        private void CompleteOffDesktopDrop(List<string> sources, string userDesktop, bool asShortcut)
        {
            var assigned = new List<string>();
            foreach (var src in sources)
            {
                try
                {
                    if (asShortcut)
                    {
                        // Reference the item in place with a .lnk on the real desktop.
                        var linkPath = UniqueDesktopPath(userDesktop, $"{Path.GetFileNameWithoutExtension(src)}.lnk");
                        ShellLink.CreateShortcut(linkPath, src);
                        assigned.Add(linkPath);
                    }
                    else
                    {
                        // Physically move the real file onto the desktop.
                        if (Directory.Exists(src)) continue; // don't relocate whole folders
                        var dest = UniqueDesktopPath(userDesktop, Path.GetFileName(src));
                        File.Move(src, dest);
                        assigned.Add(dest);
                    }
                }
                catch { /* ignore */ }
            }

            if (assigned.Count > 0) AssignToThisFence(assigned);
        }

        // ---------- Context menu actions ----------

        // The ✎ button opens the full management menu (rename, sort, icon size,
        // transparency, delete) so everything is reachable without right-clicking.
        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || FenceMenu is null) return;
            RefreshMenuChecks();
            FenceMenu.PlacementTarget = fe;
            FenceMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            FenceMenu.IsOpen = true;
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var prompt = new InputDialog("Rename Fence", "Enter a new name for this fence:", _model.Name)
            {
                Owner = this
            };
            if (prompt.ShowDialog() == true)
            {
                var newName = prompt.Value.Trim();
                if (string.IsNullOrWhiteSpace(newName) ||
                    string.Equals(newName, _model.Name, StringComparison.OrdinalIgnoreCase))
                    return;

                var invalid = Path.GetInvalidFileNameChars();
                if (newName.IndexOfAny(invalid) >= 0)
                {
                    MessageBox.Show("That name contains invalid characters for a folder. Please choose a different name.",
                                    "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Renaming only changes the fence's display label. Portals never touch the
                // mirrored folder, and real-icon fences have no backing folder at all.
                _model.Name = newName;
                TitleText.Text = _model.IsPortal ? "🗁 " + _model.Name : _model.Name;
                UpdateAccessibleName();
                FenceRenamed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            // Portals open the folder they're showing; real-icon fences open the Desktop.
            string folder = _model.IsPortal
                ? _currentFolder
                : Pickets.Services.Sandbox.UserDesktop;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch { /* ignore */ }
        }

        private void AddSystemShortcuts_Click(object sender, RoutedEventArgs e)
        {
            if (IsRealIcon)
            {
                // Assign the well-known special desktop items (This PC, Recycle Bin, …) to this
                // fence as tiles. They render straight from their shell:::{CLSID} monikers.
                var monikers = new[]
                {
                    "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // This PC
                    "shell:::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", // Control Panel
                    "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", // Network
                    "shell:::{645FF040-5081-101B-9F08-00AA002F954E}", // Recycle Bin
                };
                AssignToThisFence(monikers);
                MessageBox.Show("Added system items to this fence.",
                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                SetWatcherEnabled(false);
                int count = SystemShortcuts.AddToFolder(_model.FolderPath);
                ReloadItems();
                MessageBox.Show($"Added {count} system shortcut(s).",
                    "Pickets", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                SetWatcherEnabled(true);
            }
        }

        private void Transparency_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && double.TryParse(Convert.ToString(mi.Tag), System.Globalization.NumberStyles.Float,
                                                         System.Globalization.CultureInfo.InvariantCulture, out double alpha))
            {
                _model.BackgroundOpacity = Math.Clamp(alpha, 0.0, 1.0);
                ApplyBackground();
                Changed?.Invoke(this, EventArgs.Empty);  // persist transparency
            }
        }

        private void TitleBar_ContextMenuOpening(object sender, ContextMenuEventArgs e) => RefreshMenuChecks();

        // Tick the active option in each submenu.
        private void RefreshMenuChecks()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            CheckByTag(SortMenu, _model.Sort.ToString());
            CheckByTag(ViewMenu, _model.View.ToString());
            CheckByTag(IconSizeMenu, _model.IconSize.ToString());
            CheckByTag(ColorMenu, _model.AccentColor ?? "");
            CheckByTag(TitleSizeMenu, _model.TitleFontSize.ToString(inv));
            CheckByTag(TransparencyMenu, _model.BackgroundOpacity.ToString("0.00", inv));
            MiChangeFolder.Visibility = MiPortalFilter.Visibility = _model.IsPortal ? Visibility.Visible : Visibility.Collapsed;
            // System monitors: which readings to show, and nothing about files.
            var itemsOnly = IsMonitor ? Visibility.Collapsed : Visibility.Visible;
            MiMonitorAdd.Visibility = MiMonitorEdit.Visibility = IsMonitor ? Visibility.Visible : Visibility.Collapsed;
            if (IsMonitor) FillAddTileMenu(MiMonitorAdd);
            MiOpenFolder.Visibility = SortMenu.Visibility = MiSystemShortcuts.Visibility = itemsOnly;
            IconSizeMenu.Header = IsMonitor ? "Tile size" : "Icon size";
            // A portal can hold a whole folder's worth of files, so "Open all" is for fences only.
            MiOpenAll.Visibility = IsRealIcon && ItemsSource.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MiOpenAll.Header = HasTabs ? $"Open all in “{_model.Tabs[_model.ActiveTab].Name}”" : "Open all";
            MiCloseAll.Visibility = MiOpenAll.Visibility;
            MiCloseAll.Header = HasTabs ? $"Close all in “{_model.Tabs[_model.ActiveTab].Name}”" : "Close all";
            MiLock.IsChecked = _model.Locked;
            MiAutoHeight.IsChecked = _model.AutoHeight;
            MiAddTab.Visibility = IsRealIcon ? Visibility.Visible : Visibility.Collapsed;
            bool hasMedia = !string.IsNullOrWhiteSpace(_model.BackgroundMedia);
            MiRemoveBackground.IsEnabled = BgFitMenu.IsEnabled = BgDimMenu.IsEnabled = hasMedia;
            MiMuteBackground.IsEnabled = hasMedia && IsVideo(_model.BackgroundMedia!);
            MiMuteBackground.IsChecked = _model.BackgroundMuted;
            CheckByTag(BgFitMenu, _model.BackgroundFit.ToString());
            CheckByTag(BgDimMenu, _model.BackgroundDim.ToString("0.00", inv));
            MiGlass.IsChecked = _model.Glass;
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            var tag = Convert.ToString(mi.Tag);
            _model.AccentColor = string.IsNullOrEmpty(tag) ? null : tag;
            ApplyBackground();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (TryParseColor(_model.AccentColor, out var current))
                dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            _model.AccentColor = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            ApplyBackground();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void TitleSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi &&
                double.TryParse(Convert.ToString(mi.Tag), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                _model.TitleFontSize = size;
                ApplyBackground();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static void CheckByTag(MenuItem? parent, string activeTag)
        {
            if (parent is null) return;
            foreach (var obj in parent.Items)
                if (obj is MenuItem mi)
                    mi.IsChecked = string.Equals(Convert.ToString(mi.Tag), activeTag, StringComparison.Ordinal);
        }

        // ✕ closes the fence but keeps it (and its items); it stays closed until reopened.
        private void Close_Click(object sender, RoutedEventArgs e) => CloseFence();

        public void CloseFence()
        {
            _model.Closed = true;
            ClearSelection(); // a closed fence's selection mustn't ride along with Delete elsewhere
            Hide();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>True when the user closed this fence with ✕ (it's kept, just not shown).</summary>
        public bool IsClosedByUser => _model.Closed;

        /// <summary>Show a fence the user had closed.</summary>
        public void Reopen()
        {
            _model.Closed = false;
            Show();
            EnsureBottomZOrder();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ---------- Used by the main window's fence list ----------
        public string? AccentColor => _model.AccentColor;
        public bool IsCollapsed => _model.Collapsed;
        public string FolderPath => _model.FolderPath;
        public int ItemCount => IsPortal ? ItemsSource.Count : _model.ItemPaths.Count;

        /// <summary>Same as Delete Fence… in the fence menu (MainWindow confirms).</summary>
        public void RequestDelete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Help the user find this fence: lift it above all windows for a moment and
        /// pulse its border.</summary>
        public void Locate()
        {
            if (!IsVisible) Show();
            SetRaised(true);

            var original = RootBorder.BorderBrush;
            var pulse = new SolidColorBrush(MediaColor.FromRgb(0x3B, 0x82, 0xF6));
            RootBorder.BorderBrush = pulse;
            RootBorder.BorderThickness = new Thickness(3);
            pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                From = MediaColor.FromRgb(0x3B, 0x82, 0xF6),
                To = MediaColor.FromRgb(0x93, 0xC5, 0xFD),
                Duration = TimeSpan.FromMilliseconds(300),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3)
            });

            var done = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            done.Tick += (_, __) =>
            {
                done.Stop();
                RootBorder.BorderBrush = original;
                RootBorder.BorderThickness = new Thickness(1);
                SetRaised(false);
            };
            done.Start();
        }

        private void DeleteFence_Click(object sender, RoutedEventArgs e)
        {
            // MainWindow owns the single confirm + folder handling (portal-aware).
            DeleteRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
