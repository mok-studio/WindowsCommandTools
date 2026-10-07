// ---------------------------------------------------------------------------
//  MainWindow.cs — 主窗口外壳
//    · 无边框窗口 + WindowChrome（保留系统的拖动 / 双击最大化 / 边缘缩放 / 贴靠）
//    · 背景层（纯色 / 渐变 / 本地图片，图片不透明度独立可调）
//    · 三栏布局：左=命令库，中=输出控制台，右=参数提示与表单
//    · 状态栏：当前目录、运行状态、退出码、耗时、编码
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace WindowsCommandTools
{
    public partial class MainWindow : Window
    {
        // ---- 核心对象 ----
        internal CommandLibrary Lib;
        internal AppSettings Settings;
        internal HistoryStore History;
        internal CommandExecutor Exec;

        // ---- 布局 ----
        private Grid _root;
        internal Grid BackdropGrid;
        private Image _bgImage;
        private Grid _uiLayer;
        internal Border TitleBar;
        internal Grid BodyGrid;

        // ---- 输出控制台 ----
        internal RichTextBox Output;
        private readonly Queue<OutputChunk> _pending = new Queue<OutputChunk>();
        private DispatcherTimer _flushTimer;
        private int _paragraphCount;
        private const int MaxParagraphs = 6000;

        // ---- 状态栏 ----
        private TextBlock _statusDir;
        private TextBlock _statusRun;
        private TextBlock _statusInfo;
        private TextBlock _statusAdmin;
        private ProgressBar _busyBar;

        // ---- 输入区 ----
        internal TextBox Input;
        internal Border SuggestPanel;
        internal ListBox SuggestList;
        internal StackPanel SuggestHost;
        internal bool SuggestVisible;
        internal bool Navigated;
        internal ColumnDefinition ColLib;
        internal ColumnDefinition ColRight;
        private GridSplitter _splitLeft;
        private GridSplitter _splitRight;

        internal Grid InputHost;
        internal Button RunButton;
        private Button _stopButton;
        private Grid _titleSearchHost;

        // ---- 运行状态 ----
        internal string SessionDirectory;
        internal string LastCommand = "";
        internal bool IsAdmin;
        private Stopwatch _runWatch;
        private DispatcherTimer _clockTimer;
        internal string PendingAutoRun;
        internal string PendingInput;
        /// <summary>启动时想告诉用户的一句话（例如提权被取消），加载完成后显示到控制台。</summary>
        internal string PendingNotice;
        /// <summary>启动时直接进多命令模式。</summary>
        internal bool PendingMulti;
        /// <summary>启动时直接展开多进程面板（--multiproc）。</summary>
        internal bool PendingMultiProc;
        /// <summary>--multiproc-run：展开面板后立刻把预填的命令跑起来（截图 / 自动化验证用）。</summary>
        internal bool PendingMultiProcAutoRun;

        // ---- 双引擎状态 ----
        /// <summary>全局当前使用的 shell。</summary>
        internal ShellKind Shell = ShellKind.PowerShell;
        /// <summary>临时覆盖：只影响下一条命令，执行完自动清空。</summary>
        internal ShellKind? TempShell;
        /// <summary>PowerShell 是否优先使用 pwsh（没有则自动回退 5.1）。</summary>
        internal bool PreferPwsh;
        internal Button ShellCmdButton;
        internal Button ShellPsButton;
        internal Border TempShellChip;
        internal TextBlock TempShellText;
        internal Border CrossShellBanner;
        internal TextBlock CrossShellText;
        internal Button CrossShellSwitchButton;
        internal CmdSpec CrossShellTarget;

        // ==================================================================

        public MainWindow()
        {
            Lib = CommandLibrary.Load();
            Settings = AppSettings.Load();
            Themes.Settings = Settings;
            Suggester.StrictOrder = Settings.StrictSyntaxOrder;
            History = HistoryStore.Load();
            Exec = new CommandExecutor();

            IsAdmin = Elevation.IsAdministrator();
            SessionDirectory = Settings.LastDirectory;
            if (string.IsNullOrEmpty(SessionDirectory) || !Directory.Exists(SessionDirectory))
                SessionDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 默认启动为 PowerShell；设置里可以改成「默认以 CMD 启动」
            Shell = Shells.Parse(Settings.DefaultShell);
            PreferPwsh = Settings.PreferPwsh;

            Title = "WindowsCommandTools";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 980;
            MinHeight = 620;
            Width = Settings.WindowWidth;
            Height = Settings.WindowHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Fonts.Ui;
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;

            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = 46;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            BuildRoot();
            TryLoadIcon();
            Themes.Changed += OnThemeChanged;
            UpdateBackdrop();

            _flushTimer = new DispatcherTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(55);
            _flushTimer.Tick += delegate { FlushOutput(); };
            _flushTimer.Start();

            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromMilliseconds(400);
            _clockTimer.Tick += delegate { TickClock(); };

            Exec.Output += OnProcessOutput;
            Exec.Finished += OnProcessFinished;

            SourceInitialized += OnSourceInitialized;
            StateChanged += delegate
            {
                UpdateMaxButton();
                RefreshWindowCorners();
            };
            Closing += OnClosing;
            PreviewKeyDown += OnWindowKey;

            Loaded += delegate
            {
                Input.Focus();
                UpdateShellUi();
                if (!string.IsNullOrEmpty(PendingInput))
                {
                    string t = PendingInput;
                    PendingInput = null;
                    Input.Text = t;
                    Input.CaretIndex = Input.Text.Length;
                    Input.Focus();
                    UpdateSuggestions();
                }
                if (!string.IsNullOrEmpty(PendingAutoRun))
                {
                    Input.Text = PendingAutoRun;
                    PendingAutoRun = null;
                    Dispatcher.BeginInvoke(new Action(delegate { ExecuteCurrent(); }));
                }
                if (PendingMulti) { PendingMulti = false; SetMultiCommand(true); }
                // 多进程面板：--multiproc 时启动就展开（并把预填的命令放进各任务行）
                if (PendingMultiProc)
                {
                    PendingMultiProc = false;
                    SetMultiProc(true);
                    ApplyPendingMultiProcCmds();
                    if (PendingMultiProcAutoRun)
                    {
                        PendingMultiProcAutoRun = false;
                        // 延后一拍：等布局排完再跑，输出的自动滚动才落到位
                        Dispatcher.BeginInvoke(new Action(delegate { MultiProcRunAll(); }));
                    }
                }
                if (Settings.Maximized) WindowState = WindowState.Maximized;
                if (!string.IsNullOrEmpty(PendingNotice))
                {
                    AppendSystem(PendingNotice);
                    PendingNotice = null;
                }
                if (!string.IsNullOrEmpty(FlatStyles.LastError))
                {
                    AppendSystem(Loc.T("样式表加载警告：") + FlatStyles.LastError);
                }
            };
        }

        // ==================================================================
        //  根布局
        // ==================================================================

        private void BuildRoot()
        {
            _root = new Grid();
            _root.SetResourceReference(Grid.BackgroundProperty, "WindowBgBrush");

            // ---- 背景层：所有毛玻璃面板都从这里取样 ----
            BackdropGrid = new Grid();
            BackdropGrid.SetResourceReference(Grid.BackgroundProperty, "WindowBgBrush");
            _bgImage = new Image();
            _bgImage.Stretch = Stretch.UniformToFill;
            _bgImage.IsHitTestVisible = false;
            RenderOptions.SetBitmapScalingMode(_bgImage, BitmapScalingMode.HighQuality);
            BackdropGrid.Children.Add(_bgImage);
            _root.Children.Add(BackdropGrid);

            // ---- 界面层 ----
            _uiLayer = new Grid();
            _uiLayer.RowDefinitions.Add(new RowDefinition());
            _uiLayer.RowDefinitions[0].Height = new GridLength(46);
            _uiLayer.RowDefinitions.Add(new RowDefinition());
            _uiLayer.RowDefinitions.Add(new RowDefinition());
            _uiLayer.RowDefinitions[2].Height = GridLength.Auto;
            _root.Children.Add(_uiLayer);

            GlassPanel.Backdrop = BackdropGrid;

            BuildTitleBar();
            _uiLayer.Children.Add(TitleBar);
            Grid.SetRow(TitleBar, 0);

            BodyGrid = new Grid();
            BodyGrid.Margin = new Thickness(12, 0, 12, 0);
            // 五列：命令库 | 间隔 | 主区(输出) | 间隔 | 参数面板
            ColumnDefinition colLib = new ColumnDefinition();
            colLib.Width = new GridLength(Settings.SidebarCollapsed ? 0 : Settings.SidebarWidth);
            ColumnDefinition colGap1 = new ColumnDefinition();
            colGap1.Width = new GridLength(10);
            ColumnDefinition colMain = new ColumnDefinition();
            colMain.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition colGap2 = new ColumnDefinition();
            colGap2.Width = new GridLength(10);
            ColumnDefinition colRight = new ColumnDefinition();
            colRight.Width = new GridLength(Settings.RightPanelWidth);
            BodyGrid.ColumnDefinitions.Add(colLib);
            BodyGrid.ColumnDefinitions.Add(colGap1);
            BodyGrid.ColumnDefinitions.Add(colMain);
            BodyGrid.ColumnDefinitions.Add(colGap2);
            BodyGrid.ColumnDefinitions.Add(colRight);
            ColLib = colLib;
            ColRight = colRight;

            // 板块之间的分隔条。只有在主题里打开「主界面自由控件宽度」时才显示，
            // 平时隐藏并把宽度交给设置里那两个滑块。
            _splitLeft = MakeSplitter(colLib);
            BodyGrid.Children.Add(_splitLeft);
            Grid.SetColumn(_splitLeft, 1);
            _splitRight = MakeSplitter(colRight);
            BodyGrid.Children.Add(_splitRight);
            Grid.SetColumn(_splitRight, 3);

            _uiLayer.Children.Add(BodyGrid);
            Grid.SetRow(BodyGrid, 1);

            BuildSidebar();
            BuildCenter();
            BuildRightPanel();
            BuildStatusBar();

            _uiLayer.Children.Add(StatusBarHost);
            Grid.SetRow(StatusBarHost, 2);

            Content = _root;
        }

        // ==================================================================
        //  标题栏
        // ==================================================================

        private Button _maxButton;

        private void BuildTitleBar()
        {
            TitleBar = new Border();
            TitleBar.Background = Brushes.Transparent;
            TitleBar.BorderThickness = new Thickness(0, 0, 0, 1);
            TitleBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            TitleBar.Padding = new Thickness(14, 0, 8, 0);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            // 左：图标 + 标题 + 权限徽标
            StackPanel left = Ui.H();
            left.VerticalAlignment = VerticalAlignment.Center;
            Border logo = new Border();
            logo.Width = 28; logo.Height = 28;
            logo.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            logo.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            logo.Child = Icons.Create("terminal", 17, "OnAccentBrush", 1.8);
            left.Children.Add(logo);
            TextBlock t = Ui.Text("WindowsCommandTools", 14.5, "TextBrush", FontWeights.SemiBold);
            t.Margin = new Thickness(10, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(t);

            TextBlock sub = Ui.Text(Loc.T("命令可视化面板"), 11.5, "TextFaintBrush");
            sub.Margin = new Thickness(10, 1, 0, 0);
            sub.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(sub);

            Border adminBadge = MakeBadge(IsAdmin ? Loc.T("管理员") : Loc.T("普通权限"),
                IsAdmin ? "AccentSoftBrush" : "HoverOverlayBrush",
                IsAdmin ? "AccentBrush" : "TextFaintBrush");
            adminBadge.Margin = new Thickness(12, 0, 0, 0);
            adminBadge.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(adminBadge);
            _adminBadge = adminBadge;
            g.Children.Add(left);
            Grid.SetColumn(left, 0);

            // 中：引擎切换开关 + 全局命令搜索
            Grid mid = new Grid();
            mid.Margin = new Thickness(20, 0, 20, 0);
            mid.VerticalAlignment = VerticalAlignment.Center;
            mid.ColumnDefinitions.Add(new ColumnDefinition());
            mid.ColumnDefinitions[0].Width = GridLength.Auto;
            mid.ColumnDefinitions.Add(new ColumnDefinition());
            mid.ColumnDefinitions.Add(new ColumnDefinition());
            mid.ColumnDefinitions[2].Width = GridLength.Auto;

            mid.Children.Add(BuildShellSwitch());
            Grid.SetColumn(mid.Children[0], 0);

            Grid searchHost = new Grid();
            searchHost.MinWidth = 240;
            searchHost.Margin = new Thickness(14, 0, 0, 0);
            searchHost.VerticalAlignment = VerticalAlignment.Center;
            searchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            _titleSearch = new TextBox();
            _titleSearch.Height = 32;
            _titleSearch.Padding = new Thickness(32, 6, 10, 6);
            _titleSearch.FontSize = 12.5;
            _titleSearch.TextChanged += OnTitleSearchChanged;
            _titleSearch.PreviewKeyDown += OnTitleSearchKey;
            searchHost.Children.Add(_titleSearch);
            Grid iconHost = new Grid();
            iconHost.Width = 30;
            iconHost.HorizontalAlignment = HorizontalAlignment.Left;
            iconHost.IsHitTestVisible = false;
            UIElement si = Icons.Create("search", 14, "TextFaintBrush", 1.7);
            iconHost.Children.Add(si);
            searchHost.Children.Add(iconHost);
            _titleSearchPlaceholder = Ui.Text(Loc.T("搜索命令、用途或标签…（Ctrl+K）"), 12.5, "TextFaintBrush");
            _titleSearchPlaceholder.Margin = new Thickness(36, 0, 0, 0);
            _titleSearchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _titleSearchPlaceholder.IsHitTestVisible = false;
            searchHost.Children.Add(_titleSearchPlaceholder);
            mid.Children.Add(searchHost);
            Grid.SetColumn(searchHost, 1);

            mid.Children.Add(BuildTempShellChip());
            Grid.SetColumn(mid.Children[2], 2);

            // 关键：标题栏这 46px 在 WindowChrome 里整块是「标题栏」，
            // 落在里面的鼠标事件会被当成拖动窗口吃掉 —— 按钮点不动、搜索框点不进去。
            // 必须显式声明这一块要做命中测试（空白处仍然可以拖动窗口）。
            WindowChrome.SetIsHitTestVisibleInChrome(mid, true);
            _titleSearchHost = searchHost;
            WindowChrome.SetIsHitTestVisibleInChrome(searchHost, true);

            g.Children.Add(mid);
            Grid.SetColumn(mid, 1);

            // 右：功能按钮 + 窗口按钮
            StackPanel right = Ui.H();
            right.VerticalAlignment = VerticalAlignment.Center;
            WindowChrome.SetIsHitTestVisibleInChrome(right, true);

            if (!IsAdmin)
            {
                Button elev = Ui.Btn(Loc.T("以管理员重启"), "ChipButton", delegate { RestartElevated(); });
                elev.Height = 28;
                // 和其它标题栏控件一样垂直居中：原来底部多留了 6px，整块看着偏高
                elev.VerticalAlignment = VerticalAlignment.Center;
                elev.Margin = new Thickness(0, 0, 6, 0);
                elev.ToolTip = Loc.T("重新以管理员身份启动，系统会弹出用户账户控制确认");
                UIElement shield = Icons.Create("shield", 13, "AccentBrush", 1.6);
                StackPanel sp = Ui.H();
                sp.Children.Add(shield);
                TextBlock tb = Ui.Text(Loc.T("  以管理员重启"), 12, "TextDimBrush");
                sp.Children.Add(tb);
                elev.Content = sp;
                right.Children.Add(elev);
            }

            Button themeBtn = Ui.IconBtn("theme", Loc.T("主题与外观（配色 / 背景图 / 毛玻璃）"), delegate { OpenThemeWindow(); });
            themeBtn.Margin = new Thickness(2, 0, 2, 0);
            right.Children.Add(themeBtn);

            Button helpBtn = Ui.IconBtn("bulb", Loc.T("使用帮助"), delegate { ShowHelp(); });
            helpBtn.Margin = new Thickness(2, 0, 2, 0);
            right.Children.Add(helpBtn);

            Button setBtn = Ui.IconBtn("settings", Loc.T("设置"), delegate { OpenSettings(); });
            setBtn.Margin = new Thickness(2, 0, 6, 0);
            right.Children.Add(setBtn);

            Button minBtn = Ui.Btn(null, "WindowButton", delegate { WindowState = WindowState.Minimized; });
            minBtn.Content = Icons.Create("min", 15, "TextDimBrush", 1.6);
            minBtn.ToolTip = Loc.T("最小化");
            right.Children.Add(minBtn);

            _maxButton = Ui.Btn(null, "WindowButton", delegate { ToggleMaximize(); });
            _maxButton.Content = Icons.Create("max", 13, "TextDimBrush", 1.6);
            _maxButton.ToolTip = Loc.T("最大化 / 还原");
            right.Children.Add(_maxButton);

            Button closeBtn = Ui.Btn(null, "CloseWindowButton", delegate { Close(); });
            closeBtn.Content = Icons.Create("close", 15, "TextDimBrush", 1.6);
            closeBtn.ToolTip = Loc.T("关闭");
            right.Children.Add(closeBtn);

            g.Children.Add(right);
            Grid.SetColumn(right, 2);

            TitleBar.Child = g;
        }

        private Border _adminBadge;
        private TextBox _titleSearch;
        private TextBlock _titleSearchPlaceholder;

        internal static Border MakeBadge(string text, string bgKey, string fgKey)
        {
            Border b = new Border();
            b.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            b.Padding = new Thickness(7, 2, 7, 2);
            if (bgKey != null) b.SetResourceReference(Border.BackgroundProperty, bgKey);
            TextBlock t = Ui.Text(text, 11, fgKey, FontWeights.SemiBold);
            b.Child = t;
            return b;
        }

        private void UpdateMaxButton()
        {
            if (_maxButton == null) return;
            _maxButton.Content = Icons.Create(WindowState == WindowState.Maximized ? "restore" : "max",
                13, "TextDimBrush", 1.6);
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        // ==================================================================
        //  图标
        // ==================================================================

        private void TryLoadIcon()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (!n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream st = asm.GetManifestResourceStream(n))
                    {
                        if (st != null)
                        {
                            Icon = BitmapFrame.Create(st, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        // ==================================================================
        //  背景层
        // ==================================================================

        internal void UpdateBackdrop()
        {
            Theme t = Themes.Current;

            if (t.Gradient)
            {
                LinearGradientBrush lg = new LinearGradientBrush();
                lg.StartPoint = new Point(0, 0);
                lg.EndPoint = new Point(1, 1);
                lg.GradientStops.Add(new GradientStop(t.CBackground, 0));
                lg.GradientStops.Add(new GradientStop(ColorUtil.Mix(t.CBackground, t.CBackground2, 1.0), 1));
                lg.Freeze();
                BackdropGrid.Background = lg;
            }
            else
            {
                BackdropGrid.Background = ColorUtil.Brush(t.CBackground);
            }

            _bgImage.Opacity = t.ImageOpacity;
            _bgImage.Stretch = ParseStretch(t.ImageStretch);
            _bgImage.HorizontalAlignment = ParseAlignH(t.ImageAlignment);
            _bgImage.VerticalAlignment = ParseAlignV(t.ImageAlignment);

            if (!string.IsNullOrEmpty(t.BackgroundImage) && File.Exists(t.BackgroundImage))
            {
                try
                {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.UriSource = new Uri(t.BackgroundImage, UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    _bgImage.Source = bi;
                }
                catch
                {
                    _bgImage.Source = null;
                }
            }
            else
            {
                _bgImage.Source = null;
            }
        }

        internal static Stretch ParseStretch(string s)
        {
            if (string.Equals(s, "uniform", StringComparison.OrdinalIgnoreCase)) return Stretch.Uniform;
            if (string.Equals(s, "fill", StringComparison.OrdinalIgnoreCase)) return Stretch.Fill;
            if (string.Equals(s, "none", StringComparison.OrdinalIgnoreCase)) return Stretch.None;
            return Stretch.UniformToFill;
        }

        internal static HorizontalAlignment ParseAlignH(string s)
        {
            if (s == null) return HorizontalAlignment.Center;
            if (s.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0) return HorizontalAlignment.Left;
            if (s.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0) return HorizontalAlignment.Right;
            return HorizontalAlignment.Center;
        }

        internal static VerticalAlignment ParseAlignV(string s)
        {
            if (s == null) return VerticalAlignment.Center;
            if (s.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0) return VerticalAlignment.Top;
            if (s.IndexOf("bottom", StringComparison.OrdinalIgnoreCase) >= 0) return VerticalAlignment.Bottom;
            return VerticalAlignment.Center;
        }

        private void OnThemeChanged()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                UpdateBackdrop();
                RefreshGlassPanels();
                RefreshWindowCorners();
            }));
        }

        private void RefreshGlassPanels()
        {
            UpdateGlass(_sidebarPanel);
            UpdateGlass(_centerPanel);
            UpdateGlass(_rightPanel);
        }

        private void UpdateGlass(DependencyObject root)
        {
            if (root == null) return;
            GlassPanel gp = root as GlassPanel;
            if (gp != null) { gp.UpdateLayers(); return; }
            Visual v = root as Visual;
            if (v == null) return;
            int n = VisualTreeHelper.GetChildrenCount(v);
            for (int i = 0; i < n; i++) UpdateGlass(VisualTreeHelper.GetChild(v, i));
        }

        // ==================================================================
        //  状态栏
        // ==================================================================

        internal Border StatusBarHost;

        private void BuildStatusBar()
        {
            StatusBarHost = new Border();
            StatusBarHost.BorderThickness = new Thickness(0, 1, 0, 0);
            StatusBarHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            StatusBarHost.Padding = new Thickness(16, 0, 16, 0);
            StatusBarHost.Height = 30;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            StackPanel left = Ui.H();
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(Icons.Create("folder", 13, "TextFaintBrush", 1.5));
            _statusDir = Ui.Text("", 11.5, "TextDimBrush");
            _statusDir.Margin = new Thickness(6, 0, 0, 0);
            _statusDir.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(_statusDir);
            g.Children.Add(left);
            Grid.SetColumn(left, 0);

            StackPanel mid = Ui.H();
            mid.VerticalAlignment = VerticalAlignment.Center;
            mid.HorizontalAlignment = HorizontalAlignment.Center;
            _busyBar = new ProgressBar();
            _busyBar.Width = 90;
            _busyBar.Height = 3;
            _busyBar.IsIndeterminate = true;
            _busyBar.Visibility = Visibility.Collapsed;
            _busyBar.VerticalAlignment = VerticalAlignment.Center;
            _busyBar.Margin = new Thickness(0, 0, 10, 0);
            mid.Children.Add(_busyBar);
            _statusRun = Ui.Text(Loc.T("就绪"), 11.5, "TextFaintBrush");
            _statusRun.VerticalAlignment = VerticalAlignment.Center;
            mid.Children.Add(_statusRun);
            g.Children.Add(mid);
            Grid.SetColumn(mid, 1);

            StackPanel right = Ui.H();
            right.VerticalAlignment = VerticalAlignment.Center;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            _statusInfo = Ui.Text("", 11.5, "TextFaintBrush");
            _statusInfo.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(_statusInfo);
            _statusAdmin = Ui.Text("", 11.5, "TextFaintBrush");
            _statusAdmin.Margin = new Thickness(14, 0, 0, 0);
            _statusAdmin.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(_statusAdmin);
            g.Children.Add(right);
            Grid.SetColumn(right, 2);

            StatusBarHost.Child = g;
            UpdateStatus();
        }

        internal void UpdateStatus()
        {
            if (_statusDir == null) return;
            _statusDir.Text = SessionDirectory;
            _statusDir.ToolTip = SessionDirectory;
            string enc = Settings.OutputEncoding == "auto"
                ? Loc.T("自动(") + CommandExecutor.OemEncoding().WebName + ")"
                : Settings.OutputEncoding;
            ShellKind eff = EffectiveShell;
            string host = eff == ShellKind.PowerShell
                ? Path.GetFileName(CommandExecutor.ResolvePowerShell(PreferPwsh))
                : "cmd.exe";
            _statusInfo.Text = Loc.T("命令库 CMD ") + Lib.CountOf(ShellKind.Cmd)
                + Loc.T(" 条 · PowerShell ") + Lib.CountOf(ShellKind.PowerShell)
                + Loc.T(" 条  ·  编码 ") + enc + Loc.T("  ·  历史 ") + History.Entries.Count + Loc.T(" 条");
            _statusAdmin.Text = Loc.T("当前 ") + Shells.Display(eff) + "（" + host + "）  ·  "
                + (IsAdmin ? Loc.T("管理员模式") : Loc.T("普通权限"));
        }

        internal void SetRunState(string text, bool running)
        {
            if (_statusRun == null) return;
            _statusRun.Text = text;
            _busyBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            RunButton.IsEnabled = !running;
            RunButton.Content = running ? Loc.T("运行中…") : Loc.T("运行");
            if (_stopButton != null) _stopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running)
            {
                _runWatch = Stopwatch.StartNew();
                _clockTimer.Start();
            }
            else
            {
                _clockTimer.Stop();
            }
        }

        private void TickClock()
        {
            if (_runWatch == null || !Exec.IsRunning) return;
            if (_statusRun != null)
                _statusRun.Text = Loc.T("运行中 ") + (_runWatch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
        }

        // ==================================================================
        //  输出控制台
        // ==================================================================

        private void OnProcessOutput(OutputChunk chunk)
        {
            lock (_pending) { _pending.Enqueue(chunk); }
        }

        private void FlushOutput()
        {
            List<OutputChunk> batch = new List<OutputChunk>();
            lock (_pending)
            {
                while (_pending.Count > 0 && batch.Count < 400) batch.Add(_pending.Dequeue());
            }
            if (batch.Count == 0) return;
            foreach (OutputChunk c in batch)
            {
                string key = c.IsSystem ? "ConsoleSysBrush" : (c.IsError ? "ConsoleErrBrush" : null);
                AppendLine(c.Text, key, c.IsSystem ? FontWeights.SemiBold : FontWeights.Normal);
                // 留一份错误输出，命令结束后用来判断「是不是另一个工具的命令」
                if (c.IsError && _runErrText.Length < 8192) _runErrText.AppendLine(c.Text);
            }
            if (Settings.AutoScroll) OutputScrollToEnd();
        }

        internal void AppendLine(string text, string brushKey, FontWeight weight)
        {
            if (Output == null) return;
            Paragraph p = new Paragraph();
            p.Margin = new Thickness(0);
            p.LineHeight = Math.Max(15, Settings.FontSize * 1.42);
            Run r = new Run(text == null ? "" : text);
            r.FontWeight = weight;
            if (brushKey != null) r.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            p.Inlines.Add(r);
            Output.Document.Blocks.Add(p);
            _paragraphCount++;
            while (_paragraphCount > MaxParagraphs && Output.Document.Blocks.Count > 0)
            {
                Output.Document.Blocks.Remove(Output.Document.Blocks.FirstBlock);
                _paragraphCount--;
            }
        }

        internal void AppendSystem(string text)
        {
            AppendLine(text, "ConsoleSysBrush", FontWeights.Normal);
        }

        internal void AppendEcho(string text)
        {
            if (Output == null) return;
            Paragraph p = new Paragraph();
            p.Margin = new Thickness(0, 6, 0, 2);
            Run arrow = new Run("❯ ");
            arrow.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            arrow.FontWeight = FontWeights.Bold;
            Run cmd = new Run(text);
            cmd.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            cmd.FontWeight = FontWeights.SemiBold;
            p.Inlines.Add(arrow);
            p.Inlines.Add(cmd);
            Output.Document.Blocks.Add(p);
            _paragraphCount++;
            if (Settings.AutoScroll) OutputScrollToEnd();
        }

        internal void OutputScrollToEnd()
        {
            if (Output != null) Output.ScrollToEnd();
        }

        internal void ClearOutput()
        {
            if (Output == null) return;
            Output.Document.Blocks.Clear();
            _paragraphCount = 0;
        }

        // ==================================================================
        //  窗口消息：保证无边框窗口最大化时不会盖住任务栏
        // ==================================================================

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
            ApplyWindowCorners();
        }

        /// <summary>
        /// 切换界面语言后整体重建界面。
        ///
        /// 界面完全是代码搭出来的，BuildRoot() 又是幂等的，所以直接推倒重来最简单可靠；
        /// 只需要把用户看得见的状态（控制台内容、输入内容、当前目录、当前引擎）搬过去。
        /// </summary>
        internal void RebuildUi()
        {
            string consoleText = "";
            try
            {
                if (Output != null && Output.Document != null)
                {
                    TextRange r = new TextRange(Output.Document.ContentStart, Output.Document.ContentEnd);
                    consoleText = r.Text;
                }
            }
            catch { }

            string inputText = Input != null ? Input.Text : "";
            string multiText = MultiInput != null ? MultiInput.Text : "";
            bool multiOn = UseMultiCommand;
            // 多进程面板：控件本身会被 BuildCenter 原样搬回新布局，
            // 这里只要记住展开状态，重建后恢复即可（正在跑的命令不受影响）。
            bool multiProcOn = UseMultiProc;
            string dir = SessionDirectory;
            ShellKind shell = Shell;
            CmdSpec spec = _currentSpec;

            _root.Children.Clear();
            BuildRoot();
            Content = _root;

            SessionDirectory = dir;
            Shell = shell;
            if (Input != null)
            {
                Input.Text = inputText;
                Input.CaretIndex = inputText.Length;
            }
            if (MultiInput != null) MultiInput.Text = multiText;
            SetTempShell(null);
            UseMultiCommand = false;
            if (multiOn) SetMultiCommand(true);
            UseMultiProc = false;
            if (multiProcOn) SetMultiProc(true);

            if (consoleText.Length > 0)
            {
                string[] lines = consoleText.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i == lines.Length - 1 && lines[i].Length == 0) break;
                    AppendLine(lines[i], null, FontWeights.Normal);
                }
            }
            if (spec != null) SetCurrentSpec(spec, false);
            else ShowEmptyRight();

            UpdateShellUi();
            PopulateCategories();
            RefreshCommandList();
            UpdateStatus();
            if (Input != null && !UseMultiCommand) Input.Focus();
        }

        /// <summary>
        /// 给窗口加圆角：Windows 11 走 DWM 原生圆角（带抗锯齿），更早的系统走区域裁剪。
        /// 半径沿用主题里的「圆角大小」，让窗口和里面的面板保持一致。
        /// </summary>
        private GridSplitter MakeSplitter(ColumnDefinition col)
        {
            GridSplitter sp = new GridSplitter();
            sp.Width = 10;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Stretch;
            sp.Background = Brushes.Transparent;
            sp.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            sp.Visibility = Visibility.Collapsed;
            sp.Cursor = Cursors.SizeWE;
            sp.DragCompleted += delegate
            {
                try
                {
                    if (col == ColLib) Settings.SidebarWidth = Math.Max(160, ColLib.ActualWidth);
                    else Settings.RightPanelWidth = Math.Max(220, ColRight.ActualWidth);
                }
                catch { }
            };
            return sp;
        }

        /// <summary>
        /// 按设置应用三个板块的宽度。
        /// 主题里「主界面自由控件宽度」打开时显示分隔条可以拖；关闭时用固定宽度。
        /// </summary>
        internal void ApplyPanelWidths()
        {
            try
            {
                if (ColLib != null)
                    ColLib.Width = Settings.SidebarCollapsed ? new GridLength(0) : new GridLength(Settings.SidebarWidth);
                if (ColRight != null) ColRight.Width = new GridLength(Settings.RightPanelWidth);
                bool free = Settings.FreePanelWidth;
                if (_splitLeft != null)
                    _splitLeft.Visibility = (free && !Settings.SidebarCollapsed) ? Visibility.Visible : Visibility.Collapsed;
                if (_splitRight != null)
                    _splitRight.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        internal void ApplyWindowCorners()
        {
            Theme t = Themes.Current;
            bool dark = false;
            try { dark = ColorUtil.Luminance(t.CBackground) < 0.5; }
            catch { }
            WindowFx.ApplyRoundedCorners(this, Math.Max(t.CornerRadius, 8), dark);
        }

        /// <summary>窗口尺寸 / 最大化状态变化后重算圆角。</summary>
        internal void RefreshWindowCorners()
        {
            Theme t = Themes.Current;
            bool dark = false;
            try { dark = ColorUtil.Luminance(t.CBackground) < 0.5; }
            catch { }
            WindowFx.RefreshCorners(this, Math.Max(t.CornerRadius, 8), dark);
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                MONITORINFO info = new MONITORINFO();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork;
                    RECT mon = info.rcMonitor;
                    mmi.ptMaxPosition.x = work.left - mon.left;
                    mmi.ptMaxPosition.y = work.top - mon.top;
                    mmi.ptMaxSize.x = work.right - work.left;
                    mmi.ptMaxSize.y = work.bottom - work.top;
                    mmi.ptMinTrackSize.x = (int)MinWidth;
                    mmi.ptMinTrackSize.y = (int)MinHeight;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // ==================================================================
        //  快捷键
        // ==================================================================

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.K)
            {
                _titleSearch.Focus();
                _titleSearch.SelectAll();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.L)
            {
                ClearOutput();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.T)
            {
                OpenThemeWindow();
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key == Key.D1 || e.Key == Key.NumPad1))
            {
                SetShell(ShellKind.Cmd, true);
                e.Handled = true;
                return;
            }
            if (ctrl && (e.Key == Key.D2 || e.Key == Key.NumPad2))
            {
                SetShell(ShellKind.PowerShell, true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5)
            {
                if (!string.IsNullOrEmpty(LastCommand))
                {
                    Input.Text = LastCommand;
                    ExecuteCurrent();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F1)
            {
                ShowHelp();
                e.Handled = true;
            }
        }

        // ==================================================================
        //  关闭
        // ==================================================================

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                StopAllSchedules();   // 定时/循环的定时器要先收掉，否则关窗后还在跑
                if (Exec.IsRunning) Exec.Cancel();
                // 多进程面板里正在跑的任务也要一起收掉，否则关窗后会留下孤儿进程
                if (MultiProcAnyRunning()) CommandExecutor.CancelAll();
                // 主目录是"没设置过"的等价状态，别把它当成用户改过的值写进去 ——
                // 否则只是开关一次程序就会凭空多出一个 settings.json。
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Settings.LastDirectory = string.Equals(SessionDirectory, homeDir, StringComparison.OrdinalIgnoreCase)
                    ? "" : SessionDirectory;
                Settings.WindowWidth = Width;
                Settings.WindowHeight = Height;
                Settings.Maximized = WindowState == WindowState.Maximized;
                Settings.Save();
                History.Save();
                Themes.Save();
            }
            catch { }
        }
    }
}

