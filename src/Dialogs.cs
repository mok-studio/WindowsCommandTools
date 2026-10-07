// ---------------------------------------------------------------------------
//  Dialogs.cs — 扁平化对话框
//    · ConfirmDialog   高危命令二次确认
//    · ColorPicker     自绘调色板（不弹系统对话框，观感统一）
//    · ThemeWindow     主题：8 套预设 + 自定义颜色 + 本地背景图 + 独立不透明度 + 毛玻璃
//    · SettingsWindow  编码 / 字号 / 确认策略
//    · HelpWindow      快捷键与用法
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WindowsCommandTools
{
    internal abstract class FlatDialog : Window
    {
        protected StackPanel Body;
        protected StackPanel Footer;
        private Border _shell;

        /// <summary>
        /// 外壳四周留给投影的透明边距。
        ///
        /// 为什么必须有：投影是画在圆角外壳"外面"的，如果外壳铺满整个窗口，
        /// 投影就会被窗口边界硬切，在圆角外留下一圈灰色的方形残影 ——
        /// 看上去像是"用灰色块假装圆角、原来的方角没擦干净"。
        /// 留出这块透明边距后，投影才能在窗口内自然淡出。
        /// </summary>
        private const double ShadowRoom = 26;

        /// <summary>width / height 指的是"外壳"的尺寸，窗口会自动加上投影边距。</summary>
        protected FlatDialog(string title, double width, double height)
        {
            Title = title;
            Width = width + ShadowRoom * 2;
            Height = height + ShadowRoom * 2;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = Fonts.Ui;
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            _shell = new Border();
            _shell.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            _shell.BorderThickness = new Thickness(1);
            _shell.SetResourceReference(Border.BackgroundProperty, "SurfaceSolidBrush");
            _shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            DropShadowEffect sh = new DropShadowEffect();
            sh.BlurRadius = 26; sh.ShadowDepth = 5; sh.Direction = 270; sh.Opacity = 0.24;
            sh.Color = Color.FromRgb(0, 0, 0);
            _shell.Effect = sh;

            Grid root = new Grid();
            root.Margin = new Thickness(16);
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions[0].Height = GridLength.Auto;
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions[2].Height = GridLength.Auto;

            // 标题栏
            Grid head = new Grid();
            head.Margin = new Thickness(4, 0, 0, 12);
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions[1].Width = GridLength.Auto;
            TextBlock t = Ui.Text(title, 15, "TextBrush", FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(t);
            Button close = Ui.IconBtn("close", Loc.T("关闭"), delegate { Close(); });
            close.Width = 28; close.Height = 28;
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            head.MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
            root.Children.Add(head);
            Grid.SetRow(head, 0);

            Body = Ui.V();
            root.Children.Add(Body);
            Grid.SetRow(Body, 1);

            Footer = Ui.H();
            Footer.HorizontalAlignment = HorizontalAlignment.Right;
            Footer.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(Footer);
            Grid.SetRow(Footer, 2);

            _shell.Child = root;

            // 外壳外面再包一层，留出投影需要的透明空间（见 ShadowRoom 的说明）
            Grid shellHost = new Grid();
            shellHost.Margin = new Thickness(ShadowRoom);
            shellHost.Children.Add(_shell);
            Content = shellHost;

            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
        }
    }

    // -----------------------------------------------------------------------
    //  高危确认
    // -----------------------------------------------------------------------
    internal sealed class ConfirmDialog : FlatDialog
    {
        private bool _confirmed;

        private ConfirmDialog(Window owner, string title, string message, string command, int danger)
            : base(title, 560, 300)
        {
            if (owner != null) Owner = owner;

            StackPanel head = Ui.H();
            Border iconBox = new Border();
            iconBox.Width = 38; iconBox.Height = 38;
            iconBox.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            iconBox.SetResourceReference(Border.BackgroundProperty, danger >= 2 ? "DangerSoftBrush" : "WarningSoftBrush");
            iconBox.Child = Icons.Create("warning", 21, danger >= 2 ? "DangerBrush" : "WarningBrush", 1.8);
            head.Children.Add(iconBox);
            StackPanel ht = Ui.V();
            ht.Margin = new Thickness(12, 0, 0, 0);
            TextBlock t1 = Ui.Text(danger >= 2 ? Loc.T("这是一个高危操作") : Loc.T("这个操作会改动系统"), 14.5,
                danger >= 2 ? "DangerBrush" : "WarningBrush", FontWeights.SemiBold);
            ht.Children.Add(t1);
            TextBlock t2 = Ui.Wrap(message, 12, "TextDimBrush");
            t2.MaxWidth = 420;
            t2.Margin = new Thickness(0, 4, 0, 0);
            ht.Children.Add(t2);
            head.Children.Add(ht);
            Body.Children.Add(head);

            Border box = new Border();
            box.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            box.Padding = new Thickness(12);
            box.Margin = new Thickness(0, 14, 0, 0);
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            box.BorderThickness = new Thickness(1);
            TextBlock code = Ui.Wrap(command, 12.5, "TextBrush");
            code.FontFamily = Fonts.Mono;
            box.Child = code;
            Body.Children.Add(box);

            TextBlock hint = Ui.Wrap(Loc.T("请再次确认命令内容无误。点击「确认执行」后命令会立即开始运行。"), 11.5, "TextFaintBrush");
            hint.Margin = new Thickness(0, 12, 0, 0);
            Body.Children.Add(hint);

            Button cancel = Ui.Btn(Loc.T("取消"), "GhostButton", delegate { _confirmed = false; Close(); });
            cancel.MinWidth = 90;
            Footer.Children.Add(cancel);
            Button ok = Ui.Btn(Loc.T("确认执行"), danger >= 2 ? "DangerButton" : "PrimaryButton",
                delegate { _confirmed = true; Close(); });
            ok.MinWidth = 110;
            ok.Margin = new Thickness(8, 0, 0, 0);
            Footer.Children.Add(ok);
            Loaded += delegate { ok.Focus(); };
        }

        public static bool Show(Window owner, string title, string message, string command, int danger)
        {
            ConfirmDialog d = new ConfirmDialog(owner, title, message, command, danger);
            d.ShowDialog();
            return d._confirmed;
        }
    }

    // -----------------------------------------------------------------------
    //  调色板
    // -----------------------------------------------------------------------
    internal sealed class ColorPicker : FlatDialog
    {
        public Color Selected;
        private bool _ok;
        private TextBox _hex;
        private Slider _r, _g, _b;
        private bool _sync;

        private Border _preview;
        private TextBlock _previewText;
        private TextBlock _previewSub;
        private Border _previewChip;
        private TextBlock _previewChipText;
        private StackPanel _recentHost;
        private readonly List<Border> _swatches = new List<Border>();

        /// <summary>最近用过的颜色（本次运行内共享，最多 8 个）。</summary>
        private static readonly List<string> Recent = new List<string>();

        private static readonly string[] PaletteColor = new string[] {
            "#EF4444","#F97316","#F59E0B","#EAB308","#84CC16","#22C55E","#10B981","#14B8A6",
            "#06B6D4","#0EA5E9","#3B82F6","#6366F1","#8B5CF6","#A855F7","#D946EF","#EC4899",
            "#DC2626","#EA580C","#D97706","#65A30D","#16A34A","#0D9488","#0284C7","#2563EB",
            "#4F46E5","#7C3AED","#9333EA","#C026D3","#DB2777","#E11D48","#BE123C","#9F1239"
        };

        private static readonly string[] PaletteNeutral = new string[] {
            "#FFFFFF","#F9FAFB","#F3F4F6","#E5E7EB","#D1D5DB","#9CA3AF","#6B7280","#4B5563",
            "#374151","#1F2937","#111827","#000000",
            "#FEF2F2","#FFF7ED","#FFFBEB","#F0FDF4","#ECFDF5","#EFF6FF","#F5F3FF","#FDF2F8"
        };

        private ColorPicker(Window owner, string title, Color initial)
            : base(title, 520, 620)
        {
            if (owner != null) Owner = owner;
            Selected = initial;

            // ---------- 实时预览：这个颜色当背景 / 当前景色分别好不好看 ----------
            _preview = new Border();
            _preview.Height = 96;
            _preview.Margin = new Thickness(0, 0, 0, 2);
            _preview.BorderThickness = new Thickness(1);
            _preview.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            _preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            Grid pv = new Grid();
            pv.Margin = new Thickness(16, 0, 16, 0);
            StackPanel pvLeft = Ui.V();
            pvLeft.VerticalAlignment = VerticalAlignment.Center;
            _previewText = Ui.Text(Loc.T("示例文字 Aa 123"), 14, null, FontWeights.SemiBold);
            pvLeft.Children.Add(_previewText);
            _previewSub = Ui.Text(Loc.T("次要文字 · 可读性预览"), 11.5, null);
            _previewSub.Margin = new Thickness(0, 4, 0, 0);
            pvLeft.Children.Add(_previewSub);
            pv.Children.Add(pvLeft);

            _previewChip = new Border();
            _previewChip.Padding = new Thickness(15, 7, 15, 7);
            _previewChip.HorizontalAlignment = HorizontalAlignment.Right;
            _previewChip.VerticalAlignment = VerticalAlignment.Center;
            _previewChip.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            _previewChipText = Ui.Text(Loc.T("主按钮"), 12, null, FontWeights.SemiBold);
            _previewChip.Child = _previewChipText;
            pv.Children.Add(_previewChip);

            _preview.Child = pv;
            Body.Children.Add(_preview);

            // ---------- 最近使用 ----------
            _recentHost = Ui.V();
            Body.Children.Add(_recentHost);

            // ---------- 调色板 ----------
            Body.Children.Add(PaletteLabel(Loc.T("彩色")));
            WrapPanel pc = new WrapPanel();
            foreach (string hex in PaletteColor) pc.Children.Add(MakeSwatch(hex));
            Body.Children.Add(pc);

            Body.Children.Add(PaletteLabel(Loc.T("中性 / 浅色")));
            WrapPanel pn = new WrapPanel();
            foreach (string hex in PaletteNeutral) pn.Children.Add(MakeSwatch(hex));
            Body.Children.Add(pn);

            Body.Children.Add(Ui.HairLine());

            // ---------- 精确调整 ----------
            Grid g = new Grid();
            g.Margin = new Thickness(0, 8, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel sliders = Ui.V();
            _r = AddChannel(sliders, "R");
            _g = AddChannel(sliders, "G");
            _b = AddChannel(sliders, "B");
            g.Children.Add(sliders);

            StackPanel hexBox = Ui.V();
            hexBox.Margin = new Thickness(16, 0, 0, 0);
            hexBox.Children.Add(Ui.Text(Loc.T("十六进制"), 11.5, "TextFaintBrush"));
            _hex = new TextBox();
            _hex.Width = 104;
            _hex.FontFamily = Fonts.Mono;
            _hex.Margin = new Thickness(0, 5, 0, 6);
            _hex.ToolTip = Loc.T("可以直接输入 #RRGGBB");
            _hex.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            _hex.TextChanged += delegate
            {
                if (_sync) return;
                string s = _hex.Text.Trim();
                if (s.Length == 6 || (s.Length == 7 && s[0] == '#'))
                {
                    Set(ColorUtil.Parse(s, Selected));
                }
            };
            hexBox.Children.Add(_hex);
            TextBlock nowText = Ui.Text("", 11, "TextFaintBrush");
            nowText.Tag = "nowhex";
            hexBox.Children.Add(nowText);
            Grid.SetColumn(hexBox, 1);
            g.Children.Add(hexBox);

            Body.Children.Add(g);

            Button cancel = Ui.Btn(Loc.T("取消"), "GhostButton", delegate { _ok = false; Close(); });
            cancel.MinWidth = 84;
            Footer.Children.Add(cancel);
            Button ok = Ui.Btn(Loc.T("确定"), "PrimaryButton", delegate { _ok = true; Close(); });
            ok.MinWidth = 96;
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.IsDefault = true;
            Footer.Children.Add(ok);

            RefreshRecent();
            Set(initial);
        }

        // ------------------------------------------------------------------

        private static TextBlock PaletteLabel(string text)
        {
            TextBlock t = Ui.Text(text, 11.5, "TextFaintBrush", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 12, 0, 6);
            return t;
        }

        private Border MakeSwatch(string hex)
        {
            Color c = ColorUtil.Parse(hex, Colors.Gray);
            Border sw = new Border();
            sw.Width = 28;
            sw.Height = 28;
            sw.Margin = new Thickness(0, 0, 7, 7);
            sw.Background = ColorUtil.Brush(c);
            sw.Cursor = Cursors.Hand;
            sw.ToolTip = hex;
            sw.BorderThickness = new Thickness(2);
            sw.Tag = hex;
            sw.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            sw.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            Color captured = c;
            sw.MouseLeftButtonUp += delegate { Set(captured); };
            sw.MouseEnter += delegate { sw.Opacity = 0.72; };
            sw.MouseLeave += delegate { sw.Opacity = 1.0; };
            _swatches.Add(sw);
            return sw;
        }

        private void RefreshRecent()
        {
            _recentHost.Children.Clear();
            if (Recent.Count == 0) return;
            _recentHost.Children.Add(PaletteLabel(Loc.T("最近使用")));
            WrapPanel w = new WrapPanel();
            foreach (string hex in Recent)
            {
                Color c = ColorUtil.Parse(hex, Colors.Gray);
                Border sw = new Border();
                sw.Width = 22;
                sw.Height = 22;
                sw.Margin = new Thickness(0, 0, 6, 6);
                sw.Background = ColorUtil.Brush(c);
                sw.Cursor = Cursors.Hand;
                sw.ToolTip = hex;
                sw.BorderThickness = new Thickness(1);
                sw.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
                sw.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
                Color captured = c;
                sw.MouseLeftButtonUp += delegate { Set(captured); };
                w.Children.Add(sw);
            }
            _recentHost.Children.Add(w);
        }

        private static void Remember(string hex)
        {
            hex = (hex ?? "").ToUpperInvariant();
            if (hex.Length == 0) return;
            Recent.Remove(hex);
            Recent.Insert(0, hex);
            while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
        }

        private static Brush ContrastBrush(Color c)
        {
            return ColorUtil.Luminance(c) < 0.55
                ? Brushes.White
                : ColorUtil.Brush(Color.FromRgb(0x11, 0x18, 0x27));
        }

        private Slider AddChannel(StackPanel host, string name)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 6);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;
            TextBlock lab = Ui.Text(name, 11.5, "TextFaintBrush");
            lab.VerticalAlignment = VerticalAlignment.Center;
            lab.Width = 14;
            g.Children.Add(lab);
            Slider sl = new Slider();
            sl.Minimum = 0; sl.Maximum = 255;
            sl.Margin = new Thickness(6, 0, 8, 0);
            sl.ValueChanged += delegate { if (!_sync) FromSliders(); };
            Grid.SetColumn(sl, 1);
            g.Children.Add(sl);
            TextBlock val = Ui.Text("0", 11, "TextDimBrush");
            val.Width = 30;
            val.VerticalAlignment = VerticalAlignment.Center;
            val.TextAlignment = TextAlignment.Right;
            val.Tag = "val";
            Grid.SetColumn(val, 2);
            g.Children.Add(val);
            host.Children.Add(g);
            sl.Tag = val;
            return sl;
        }

        private void FromSliders()
        {
            Color c = Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value);
            Set(c);
        }

        private void Set(Color c)
        {
            _sync = true;
            Selected = c;

            Brush bg = ColorUtil.Brush(c);
            Brush fg = ContrastBrush(c);
            _preview.Background = bg;
            _previewText.Foreground = fg;
            _previewSub.Foreground = fg;
            _previewSub.Opacity = 0.72;
            _previewChip.Background = fg;
            _previewChipText.Foreground = bg;

            _r.Value = c.R; _g.Value = c.G; _b.Value = c.B;
            UpdateLabel(_r); UpdateLabel(_g); UpdateLabel(_b);
            string hex = ColorUtil.Hex(c);
            _hex.Text = hex;

            FrameworkElement nowHex = Ui.FindByTag(this, "nowhex");
            TextBlock nowText = nowHex as TextBlock;
            if (nowText != null)
                nowText.Text = Loc.T("对比度 ") + (ColorUtil.Luminance(c) < 0.55
                    ? Loc.T("偏暗（建议配浅色文字）") : Loc.T("偏亮（建议配深色文字）"));

            // 选中的色块加一圈强调色描边
            foreach (Border sw in _swatches)
            {
                string tag = sw.Tag as string;
                bool on = tag != null && string.Equals(tag, hex, StringComparison.OrdinalIgnoreCase);
                if (on) sw.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                else sw.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            }
            _sync = false;
        }

        private static void UpdateLabel(Slider s)
        {
            TextBlock t = s.Tag as TextBlock;
            if (t != null) t.Text = ((int)s.Value).ToString(CultureInfo.InvariantCulture);
        }


        public static bool Pick(Window owner, string title, Color initial, out Color result)
        {
            ColorPicker p = new ColorPicker(owner, title, initial);
            p.ShowDialog();
            result = p.Selected;
            if (p._ok) Remember(ColorUtil.Hex(p.Selected));
            return p._ok;
        }
    }

    //  主题设置窗口
    // -----------------------------------------------------------------------
    internal sealed class ThemeWindow : FlatDialog
    {
        private readonly Theme _original;
        private Theme _working;
        private readonly MainWindow _main;
        private WrapPanel _presetWrap;
        private TextBlock _sampleText;
        private StackPanel _colorHost;
        private Button _undoAllButton;
        private TextBlock _imgPathText;

        public ThemeWindow(MainWindow main)
            : base(Loc.T("主题与外观"), 860, 720)
        {
            _main = main;
            _original = Themes.Current.Clone();
            _working = Themes.Current.Clone();

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel inner = Ui.V();
            inner.Margin = new Thickness(0, 0, 10, 0);
            sv.Content = inner;
            sv.MaxHeight = 596;
            Body.Children.Add(sv);

            // ---------- 预设 ----------
            inner.Children.Add(SectionTitle(Loc.T("预设主题"), "theme"));
            _presetWrap = new WrapPanel();
            _presetWrap.Margin = new Thickness(0, 8, 0, 4);
            foreach (Theme t in Themes.Presets())
            {
                Theme captured = t;
                _presetWrap.Children.Add(BuildPresetCard(t, delegate
                {
                    Theme nt = captured.Clone();
                    nt.BackgroundImage = _working.BackgroundImage;
                    nt.ImageOpacity = _working.ImageOpacity;
                    nt.ImageStretch = _working.ImageStretch;
                    nt.ImageAlignment = _working.ImageAlignment;
                    _working = nt;
                    Themes.Set(_working, false);
                    RefreshAll();
                }));
            }
            inner.Children.Add(_presetWrap);

            // ---------- 分模式主题 ----------
            StackPanel shellThemeRow = Ui.H();
            shellThemeRow.Margin = new Thickness(0, 10, 0, 0);
            CheckBox sepTheme = new CheckBox();
            sepTheme.Content = Loc.T("为每个模式使用独立主题");
            sepTheme.IsChecked = _main != null && _main.Settings.SeparateThemePerShell;
            sepTheme.Checked += delegate
            {
                if (_main == null) return;
                _main.Settings.SeparateThemePerShell = true;
                _main.Settings.Save();
                Themes.OnShellChanged(_main.Shell);
            };
            sepTheme.Unchecked += delegate
            {
                if (_main == null) return;
                _main.Settings.SeparateThemePerShell = false;
                _main.Settings.Save();
                // CMD 那套保留在 theme-cmd.json 里，下次勾选原样恢复
                Themes.Set(Themes.Current, false);
            };
            shellThemeRow.Children.Add(sepTheme);
            inner.Children.Add(shellThemeRow);

            TextBlock sepNote = Ui.Wrap(Loc.T("勾选后，CMD 和 PowerShell 各用一套主题：切到哪个模式就应用哪套。取消勾选则两种模式都用 PowerShell 那套；CMD 那套会保留下来，重新勾选时按原样恢复。"), 11, "TextFaintBrush");
            sepNote.Margin = new Thickness(0, 0, 0, 4);
            inner.Children.Add(sepNote);

            // ---------- 主界面控件宽度 ----------
            StackPanel freeWidthRow = Ui.H();
            freeWidthRow.Margin = new Thickness(0, 12, 0, 0);
            CheckBox freeWidth = new CheckBox();
            freeWidth.Content = Loc.T("主界面自由控件宽度");
            freeWidth.IsChecked = _main != null && _main.Settings.FreePanelWidth;
            freeWidth.ToolTip = Loc.T("关闭时，三个板块的宽度按下面的滑块固定；打开后可以直接用鼠标拖动板块之间的分隔条来改宽度，拉出来的宽度会记住。");
            freeWidth.Checked += delegate
            {
                if (_main == null) return;
                _main.Settings.FreePanelWidth = true;
                _main.ApplyPanelWidths();
                // 多进程窗口也在用同一套宽度设置，勾上就立即生效，不必等它的轮询
                if (MultiProc.Win != null) MultiProc.Win.ApplyPanelWidths();
            };
            freeWidth.Unchecked += delegate
            {
                if (_main == null) return;
                _main.Settings.FreePanelWidth = false;
                _main.ApplyPanelWidths();
                if (MultiProc.Win != null) MultiProc.Win.ApplyPanelWidths();
            };
            freeWidthRow.Children.Add(freeWidth);
            inner.Children.Add(freeWidthRow);

            TextBlock freeNote = Ui.Wrap(Loc.T("关闭（默认）时宽度由下面的滑块决定；打开后可直接拖动板块之间的分隔条，宽度会保存下来。"), 11, "TextFaintBrush");
            freeNote.Margin = new Thickness(0, 2, 0, 4);
            inner.Children.Add(freeNote);

            // 固定宽度时用这两个滑块调（自由拖动模式下它们只是显示当前宽度）
            inner.Children.Add(SliderRow(Loc.T("命令库栏宽度"), 180, 460, _main != null ? _main.Settings.SidebarWidth : 268, "0 px", delegate (double v)
            {
                if (_main == null) return;
                _main.Settings.SidebarWidth = v;
                _main.ApplyPanelWidths();
            }));
            inner.Children.Add(SliderRow(Loc.T("参数面板宽度"), 240, 560, _main != null ? _main.Settings.RightPanelWidth : 340, "0 px", delegate (double v)
            {
                if (_main == null) return;
                _main.Settings.RightPanelWidth = v;
                _main.ApplyPanelWidths();
            }));

            // ---------- 自定义颜色 ----------
            Grid colorHead = new Grid();
            colorHead.Margin = new Thickness(0, 14, 0, 0);
            colorHead.ColumnDefinitions.Add(new ColumnDefinition());
            colorHead.ColumnDefinitions.Add(new ColumnDefinition());
            colorHead.ColumnDefinitions[1].Width = GridLength.Auto;
            StackPanel colorTitle = SectionTitle(Loc.T("自定义颜色"), "layers");
            colorTitle.Margin = new Thickness(0);
            colorHead.Children.Add(colorTitle);
            _undoAllButton = Ui.Btn(Loc.T("全部撤销修改"), "GhostButton", delegate { UndoAllColors(); });
            _undoAllButton.FontSize = 11.5;
            _undoAllButton.Padding = new Thickness(10, 4, 10, 4);
            _undoAllButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_undoAllButton, 1);
            colorHead.Children.Add(_undoAllButton);
            inner.Children.Add(colorHead);
            _colorHost = Ui.V();
            _colorHost.Margin = new Thickness(0, 8, 0, 4);
            inner.Children.Add(_colorHost);

            // ---------- 背景图 ----------
            inner.Children.Add(SectionTitle(Loc.T("背景图片"), "image"));
            StackPanel imgRow = Ui.H();
            imgRow.Margin = new Thickness(0, 8, 0, 4);
            imgRow.Children.Add(Ui.Btn(Loc.T("选择本地图片…"), "SoftButton", delegate { PickImage(); }));
            Button clearImg = Ui.Btn(Loc.T("清除图片"), "GhostButton", delegate
            {
                _working.BackgroundImage = "";
                Themes.Set(_working, false);
                RefreshAll();
            });
            clearImg.Margin = new Thickness(8, 0, 0, 0);
            imgRow.Children.Add(clearImg);

            ComboBox stretch = new ComboBox();
            stretch.Width = 116;
            stretch.Margin = new Thickness(8, 0, 0, 0);
            stretch.Items.Add(Loc.T("填充裁切"));
            stretch.Items.Add(Loc.T("完整显示"));
            stretch.Items.Add(Loc.T("拉伸铺满"));
            stretch.Items.Add(Loc.T("原始大小"));
            stretch.SelectedIndex = StretchIndex(_working.ImageStretch);
            stretch.SelectionChanged += delegate
            {
                string[] vals = new string[] { "uniformToFill", "uniform", "fill", "none" };
                if (stretch.SelectedIndex >= 0)
                {
                    _working.ImageStretch = vals[stretch.SelectedIndex];
                    Themes.Set(_working, false);
                }
            };
            imgRow.Children.Add(stretch);
            inner.Children.Add(imgRow);

            _imgPathText = Ui.Wrap("", 11, "TextFaintBrush");
            _imgPathText.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(_imgPathText);

            inner.Children.Add(SliderRow(Loc.T("图片不透明度"), 0, 1, _working.ImageOpacity, "0 %", delegate (double v)
            {
                _working.ImageOpacity = v;
                Themes.Set(_working, false);
            }));

            // ---------- 面板与毛玻璃 ----------
            inner.Children.Add(SectionTitle(Loc.T("面板与毛玻璃"), "grid"));
            inner.Children.Add(SliderRow(Loc.T("面板不透明度"), 0.1, 1, _working.PanelOpacity, "0 %", delegate (double v)
            {
                _working.PanelOpacity = v;
                Themes.Set(_working, false);
            }));
            inner.Children.Add(SectionTitle(Loc.T("玻璃与圆角"), "sliders"));
            inner.Children.Add(SliderRow(Loc.T("毛玻璃模糊半径"), 0, 60, _working.BlurRadius, "0 px", delegate (double v)
            {
                _working.BlurRadius = v;
                Themes.Set(_working, false);
                _main.UpdateBackdrop();
            }));
            inner.Children.Add(SliderRow(Loc.T("圆角大小"), 0, 22, _working.CornerRadius, "0 px", delegate (double v)
            {
                _working.CornerRadius = v;
                Themes.Set(_working, false);
            }));
            TextBlock radiusHint = Ui.Wrap(Loc.T("这一个滑块控制全界面：窗口、面板、按钮、输入框、徽标、复选框都会一起变，")
                + Loc.T("下面四个方块就是四档圆角的实际效果。"), 11, "TextFaintBrush");
            radiusHint.Margin = new Thickness(0, 6, 0, 0);
            inner.Children.Add(radiusHint);
            inner.Children.Add(RadiusDemo());

            StackPanel toggles = Ui.H();
            toggles.Margin = new Thickness(0, 8, 0, 4);
            CheckBox frosted = new CheckBox();
            frosted.Content = Loc.T("启用毛玻璃（背景透过面板时模糊）");
            frosted.IsChecked = _working.Frosted;
            frosted.Checked += delegate { _working.Frosted = true; Themes.Set(_working, false); };
            frosted.Unchecked += delegate { _working.Frosted = false; Themes.Set(_working, false); };
            toggles.Children.Add(frosted);

            CheckBox grad = new CheckBox();
            grad.Content = Loc.T("渐变背景");
            grad.Margin = new Thickness(20, 0, 0, 0);
            grad.IsChecked = _working.Gradient;
            grad.Checked += delegate { _working.Gradient = true; Themes.Set(_working, false); };
            grad.Unchecked += delegate { _working.Gradient = false; Themes.Set(_working, false); };
            toggles.Children.Add(grad);
            inner.Children.Add(toggles);

            // ---------- 预览 ----------
            inner.Children.Add(SectionTitle(Loc.T("预览"), "search"));
            Border preview = new Border();
            preview.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            preview.Padding = new Thickness(14);
            preview.Margin = new Thickness(0, 8, 0, 0);
            preview.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            preview.BorderThickness = new Thickness(1);
            StackPanel pv = Ui.V();
            _sampleText = Ui.Text(Loc.T("命令输出预览 · ipconfig /all — 192.168.1.100"), 12.5, "TextBrush");
            _sampleText.FontFamily = Fonts.Mono;
            pv.Children.Add(_sampleText);
            TextBlock pv2 = Ui.Text(Loc.T("次要文字 次要文字 · 强调色文字"), 11.5, "TextDimBrush");
            pv2.Margin = new Thickness(0, 4, 0, 0);
            pv.Children.Add(pv2);
            StackPanel chips = Ui.H();
            chips.Margin = new Thickness(0, 8, 0, 0);
            chips.Children.Add(Ui.Btn(Loc.T("主按钮"), "PrimaryButton", null));
            Button ghost = Ui.Btn(Loc.T("次按钮"), "GhostButton", null);
            ghost.Margin = new Thickness(8, 0, 0, 0);
            chips.Children.Add(ghost);
            Border bad = MainWindow.MakeBadge(Loc.T("徽标"), "AccentSoftBrush", "AccentBrush");
            bad.VerticalAlignment = VerticalAlignment.Center;
            bad.Margin = new Thickness(8, 0, 0, 0);
            chips.Children.Add(bad);
            pv.Children.Add(chips);
            preview.Child = pv;
            inner.Children.Add(preview);

            // ---------- 底部按钮 ----------
            Button export = Ui.Btn(Loc.T("导出主题…"), "GhostButton", delegate { ExportTheme(); });
            export.Margin = new Thickness(0, 0, 6, 0);
            Footer.Children.Add(export);
            Button import = Ui.Btn(Loc.T("导入主题…"), "GhostButton", delegate { ImportTheme(); });
            import.Margin = new Thickness(0, 0, 6, 0);
            Footer.Children.Add(import);
            Button reset = Ui.Btn(Loc.T("恢复默认预设"), "GhostButton", delegate
            {
                Theme t = Themes.PresetByName("浅色扁平");
                t.BackgroundImage = _working.BackgroundImage;
                t.ImageOpacity = _working.ImageOpacity;
                _working = t;
                Themes.Set(_working, false);
                RefreshAll();
            });
            reset.Margin = new Thickness(0, 0, 6, 0);
            Footer.Children.Add(reset);
            Button ok = Ui.Btn(Loc.T("保存"), "PrimaryButton", delegate
            {
                Themes.Set(_working, true);
                DialogResult = true;
                Close();
            });
            ok.MinWidth = 96;
            ok.Margin = new Thickness(0, 0, 0, 0);
            Footer.Children.Add(ok);

            RefreshAll();
            Closed += delegate
            {
                if (DialogResult != true) Themes.Set(_original, true);
            };
        }

        private static int StretchIndex(string s)
        {
            if (s == "uniformToFill") return 0;
            if (s == "uniform") return 1;
            if (s == "fill") return 2;
            if (s == "none") return 3;
            return 0;
        }

        private static StackPanel SectionTitle(string text, string icon)
        {
            StackPanel sp = Ui.H();
            sp.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(Icons.Create(icon, 14, "AccentBrush", 1.6));
            TextBlock t = Ui.Text("  " + text, 13, "TextBrush", FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            return sp;
        }

        private UIElement BuildPresetCard(Theme t, Action onClick)
        {
            Border card = new Border();
            card.Width = 116;
            card.Height = 74;
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            card.Margin = new Thickness(0, 0, 10, 10);
            card.Cursor = Cursors.Hand;
            card.BorderThickness = new Thickness(1.5);
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            card.Background = ColorUtil.Brush(t.CBackground);

            Grid g = new Grid();
            g.Margin = new Thickness(10);
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[1].Height = GridLength.Auto;
            StackPanel bars = Ui.V();
            bars.VerticalAlignment = VerticalAlignment.Center;
            Border b1 = new Border();
            b1.Height = 16;
            b1.Width = 62;
            b1.HorizontalAlignment = HorizontalAlignment.Left;
            b1.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            b1.Background = ColorUtil.Brush(ColorUtil.WithAlpha(t.CSurface, 0.95));
            bars.Children.Add(b1);
            StackPanel line = Ui.H();
            line.Margin = new Thickness(0, 6, 0, 0);
            Border dot = new Border();
            dot.Width = 26; dot.Height = 8;
            dot.SetResourceReference(Border.CornerRadiusProperty, "MicroCornerRadius");
            dot.Background = ColorUtil.Brush(t.CAccent);
            line.Children.Add(dot);
            Border dot2 = new Border();
            dot2.Width = 40; dot2.Height = 8;
            dot2.SetResourceReference(Border.CornerRadiusProperty, "MicroCornerRadius");
            dot2.Margin = new Thickness(5, 0, 0, 0);
            dot2.Background = ColorUtil.Brush(ColorUtil.WithAlpha(t.CText, 0.35));
            line.Children.Add(dot2);
            bars.Children.Add(line);
            g.Children.Add(bars);

            TextBlock name = Ui.Text(Loc.T(t.Name), 11.5, null, FontWeights.SemiBold);
            name.Foreground = ColorUtil.Brush(t.CText);
            name.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(name, 1);
            g.Children.Add(name);

            card.Child = g;
            card.ToolTip = Loc.T("点击应用「{0}」", Loc.T(t.Name));
            card.MouseLeftButtonUp += delegate { onClick(); };
            card.MouseEnter += delegate { card.Opacity = 0.85; };
            card.MouseLeave += delegate { card.Opacity = 1.0; };
            return card;
        }

        // ==================================================================
        //  自定义颜色
        // ==================================================================

        /// <summary>一个可编辑的颜色项：怎么读、怎么写、以及"原本是什么值"（用来判断有没有改过）。</summary>
        private sealed class ColorDef
        {
            public string Label;
            public Func<string> Get;
            public Action<string> Set;
            public Func<string> Original;

            public ColorDef(string label, Func<string> get, Action<string> set, Func<string> original)
            {
                Label = label; Get = get; Set = set; Original = original;
            }

            public bool Changed
            {
                get
                {
                    try
                    {
                        return !string.Equals(Norm(Get()), Norm(Original()), StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                }
            }

            private static string Norm(string s)
            {
                return (s ?? "").Trim();
            }
        }

        /// <summary>每个颜色卡片注册一个刷新闭包，改完值只更新自己，不重建整棵树（否则输入框会失焦）。</summary>
        private readonly List<Action> _cardUpdaters = new List<Action>();

        private void RefreshAll()
        {
            _colorHost.Children.Clear();
            _cardUpdaters.Clear();

            ColorDef[] ui = new ColorDef[] {
                Def(Loc.T("强调色"), delegate { return _working.Accent; }, delegate (string v) { _working.Accent = v; }, delegate { return _original.Accent; }),
                Def(Loc.T("窗口背景"), delegate { return _working.Background; }, delegate (string v) { _working.Background = v; }, delegate { return _original.Background; }),
                Def(Loc.T("渐变第二色"), delegate { return _working.Background2; }, delegate (string v) { _working.Background2 = v; }, delegate { return _original.Background2; }),
                Def(Loc.T("面板底色"), delegate { return _working.Surface; }, delegate (string v) { _working.Surface = v; }, delegate { return _original.Surface; }),
                Def(Loc.T("边框"), delegate { return _working.Border; }, delegate (string v) { _working.Border = v; }, delegate { return _original.Border; })
            };
            ColorDef[] text = new ColorDef[] {
                Def(Loc.T("主文字"), delegate { return _working.Text; }, delegate (string v) { _working.Text = v; }, delegate { return _original.Text; }),
                Def(Loc.T("次要文字"), delegate { return _working.TextDim; }, delegate (string v) { _working.TextDim = v; }, delegate { return _original.TextDim; })
            };
            ColorDef[] cons = new ColorDef[] {
                Def(Loc.T("控制台背景"), delegate { return _working.ConsoleBg; }, delegate (string v) { _working.ConsoleBg = v; }, delegate { return _original.ConsoleBg; }),
                Def(Loc.T("控制台文字"), delegate { return _working.ConsoleText; }, delegate (string v) { _working.ConsoleText = v; }, delegate { return _original.ConsoleText; })
            };
            ColorDef[] state = new ColorDef[] {
                Def(Loc.T("危险色"), delegate { return _working.Danger; }, delegate (string v) { _working.Danger = v; }, delegate { return _original.Danger; }),
                Def(Loc.T("成功色"), delegate { return _working.Success; }, delegate (string v) { _working.Success = v; }, delegate { return _original.Success; }),
                Def(Loc.T("警告色"), delegate { return _working.Warning; }, delegate (string v) { _working.Warning = v; }, delegate { return _original.Warning; })
            };

            Grid two = new Grid();
            two.ColumnDefinitions.Add(new ColumnDefinition());
            two.ColumnDefinitions.Add(new ColumnDefinition());
            two.ColumnDefinitions[1].Width = new GridLength(1.05, GridUnitType.Star);

            StackPanel left = Ui.V();
            left.Margin = new Thickness(0, 0, 8, 0);
            left.Children.Add(GroupCard(Loc.T("界面配色"), "layers", ui));
            left.Children.Add(GroupCard(Loc.T("控制台"), "terminal", cons));
            two.Children.Add(left);

            StackPanel right = Ui.V();
            right.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(GroupCard(Loc.T("文字"), "type", text));
            right.Children.Add(GroupCard(Loc.T("状态色"), "alert", state));
            Grid.SetColumn(right, 1);
            two.Children.Add(right);

            _colorHost.Children.Add(two);

            TextBlock tip = Ui.Wrap(Loc.T("点色块或名字选颜色，也可以直接改右边的十六进制值。")
                + Loc.T("改过的项会出现 ↺ 按钮，点它可以把这一项单独还原。"), 11, "TextFaintBrush");
            tip.Margin = new Thickness(0, 8, 0, 0);
            _colorHost.Children.Add(tip);

            if (_imgPathText != null)
            {
                _imgPathText.Text = string.IsNullOrEmpty(_working.BackgroundImage)
                    ? Loc.T("未选择图片。选择后可以单独调节图片的浓淡，和面板不透明度互不影响。")
                    : Loc.T("当前： ") + _working.BackgroundImage;
            }
        }

        private static ColorDef Def(string label, Func<string> get, Action<string> set, Func<string> original)
        {
            return new ColorDef(label, get, set, original);
        }

        private UIElement GroupCard(string title, string icon, ColorDef[] defs)
        {
            Border card = new Border();
            card.Padding = new Thickness(12, 10, 12, 12);
            card.Margin = new Thickness(0, 0, 0, 10);
            card.BorderThickness = new Thickness(1);
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            card.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");

            StackPanel sp = Ui.V();
            StackPanel head = Ui.H();
            StackPanel hi = Ui.H();
            hi.Children.Add(Icons.Create(icon, 12, "AccentBrush", 1.6));
            TextBlock ht = Ui.Text("  " + title, 12, "TextBrush", FontWeights.SemiBold);
            ht.VerticalAlignment = VerticalAlignment.Center;
            hi.Children.Add(ht);
            head.Children.Add(hi);
            head.Margin = new Thickness(0, 0, 0, 8);
            sp.Children.Add(head);

            foreach (ColorDef d in defs) sp.Children.Add(ColorCard(d));
            card.Child = sp;
            return card;
        }

        /// <summary>一个颜色项：色块 + 名称 + 可编辑的十六进制 + 撤销按钮。整行可点。</summary>
        private UIElement ColorCard(ColorDef def)
        {
            Border row = new Border();
            row.Padding = new Thickness(6, 4, 4, 4);
            row.Margin = new Thickness(0, 2, 0, 2);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
            row.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[3].Width = GridLength.Auto;

            Border swatch = new Border();
            swatch.Width = 24; swatch.Height = 24;
            swatch.BorderThickness = new Thickness(1);
            swatch.VerticalAlignment = VerticalAlignment.Center;
            swatch.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            swatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            swatch.Background = ColorUtil.Brush(ColorUtil.Parse(def.Get(), Colors.Gray));
            g.Children.Add(swatch);

            TextBlock lab = Ui.Text(def.Label, 12.5, "TextBrush");
            lab.VerticalAlignment = VerticalAlignment.Center;
            lab.Margin = new Thickness(10, 0, 8, 0);
            Grid.SetColumn(lab, 1);
            g.Children.Add(lab);

            TextBox hex = new TextBox();
            hex.Width = 86;
            hex.FontFamily = Fonts.Mono;
            hex.FontSize = 11.5;
            hex.Text = def.Get();
            hex.VerticalAlignment = VerticalAlignment.Center;
            hex.HorizontalAlignment = HorizontalAlignment.Right;
            hex.ToolTip = Loc.T("可以直接输入 #RRGGBB，回车生效");
            hex.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            Grid.SetColumn(hex, 2);
            g.Children.Add(hex);

            Button undo = new Button();
            undo.Content = "↺";
            undo.SetResourceReference(FrameworkElement.StyleProperty, "FlatButtonBase");
            undo.Padding = new Thickness(6, 0, 6, 0);
            undo.FontSize = 13;
            undo.Width = 28;
            undo.Margin = new Thickness(4, 0, 0, 0);
            undo.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            undo.ToolTip = Loc.T("撤销这一项的修改");
            undo.Visibility = Visibility.Collapsed;
            Grid.SetColumn(undo, 3);
            g.Children.Add(undo);

            row.Child = g;

            // ---- 三个组件共用的"写入"逻辑 ----
            Action<string> write = delegate (string hexValue)
            {
                def.Set(hexValue);
                swatch.Background = ColorUtil.Brush(ColorUtil.Parse(hexValue, Colors.Gray));
                if (!string.Equals(hex.Text, hexValue, StringComparison.OrdinalIgnoreCase)) hex.Text = hexValue;
                Themes.Set(_working, false);
            };

            Action pick = delegate
            {
                Color cur;
                if (ColorPicker.Pick(this, Loc.T("选择颜色 — ") + def.Label,
                        ColorUtil.Parse(def.Get(), Colors.Gray), out cur))
                {
                    write(ColorUtil.Hex(cur));
                }
            };

            swatch.MouseLeftButtonUp += delegate { pick(); };
            lab.MouseLeftButtonUp += delegate { pick(); };
            lab.Cursor = Cursors.Hand;

            hex.GotFocus += delegate { row.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); };
            hex.LostFocus += delegate
            {
                row.BorderThickness = new Thickness(0);
                string s = hex.Text.Trim();
                if (s.Length == 6 || (s.Length == 7 && s[0] == '#'))
                {
                    Color parsed = ColorUtil.Parse(s, ColorUtil.Parse(def.Get(), Colors.Gray));
                    if (!string.Equals(ColorUtil.Hex(parsed), def.Get(), StringComparison.OrdinalIgnoreCase))
                        write(ColorUtil.Hex(parsed));
                    return;
                }
                hex.Text = def.Get();
            };
            hex.KeyDown += delegate (object s2, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; row.Focus(); }
            };

            undo.Click += delegate
            {
                write(def.Original());
                UpdateCards();
            };

            row.MouseEnter += delegate { row.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush"); };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };

            // 刷新这一项（改过就露出 ↺，同时同步色块和文字）
            Action refresh = delegate
            {
                string cur = def.Get();
                swatch.Background = ColorUtil.Brush(ColorUtil.Parse(cur, Colors.Gray));
                if (!hex.IsKeyboardFocusWithin && !string.Equals(hex.Text, cur, StringComparison.OrdinalIgnoreCase))
                    hex.Text = cur;
                undo.Visibility = def.Changed ? Visibility.Visible : Visibility.Collapsed;
            };
            _cardUpdaters.Add(refresh);
            refresh();

            return row;
        }


        private void UpdateCards()
        {
            foreach (Action a in _cardUpdaters)
            {
                try { a(); } catch { }
            }
            RefreshUndoAllButton();
        }

        private void RefreshUndoAllButton()
        {
            if (_undoAllButton == null) return;
            bool any = false;
            foreach (Action a in _cardUpdaters) { any = true; break; }
            _undoAllButton.Visibility = any && AnyChanged() ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool AnyChanged()
        {
            try
            {
                return !string.Equals(_working.Accent, _original.Accent, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Background, _original.Background, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Background2, _original.Background2, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Surface, _original.Surface, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Border, _original.Border, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Text, _original.Text, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.TextDim, _original.TextDim, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.ConsoleBg, _original.ConsoleBg, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.ConsoleText, _original.ConsoleText, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Danger, _original.Danger, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Success, _original.Success, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_working.Warning, _original.Warning, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void UndoAllColors()
        {
            _working.Accent = _original.Accent;
            _working.Background = _original.Background;
            _working.Background2 = _original.Background2;
            _working.Surface = _original.Surface;
            _working.Border = _original.Border;
            _working.Text = _original.Text;
            _working.TextDim = _original.TextDim;
            _working.ConsoleBg = _original.ConsoleBg;
            _working.ConsoleText = _original.ConsoleText;
            _working.Danger = _original.Danger;
            _working.Success = _original.Success;
            _working.Warning = _original.Warning;
            Themes.Set(_working, false);
            UpdateCards();
        }

        /// <summary>圆角示例块 —— 用 DynamicResource 绑到主题的四个圆角档位，拖滑块时会实时变化。</summary>
        private static UIElement RadiusDemo()
        {
            string[] names = new string[] { Loc.T("面板"), Loc.T("控件"), Loc.T("小元素"), Loc.T("细节") };
            string[] res = new string[] { "PanelCornerRadius", "ControlCornerRadius", "SmallCornerRadius", "MicroCornerRadius" };
            StackPanel row = Ui.H();
            row.Margin = new Thickness(0, 8, 0, 0);
            for (int i = 0; i < names.Length; i++)
            {
                StackPanel sp = Ui.V();
                sp.Margin = new Thickness(0, 0, 12, 0);
                Border box = new Border();
                box.Width = 66; box.Height = 42;
                box.BorderThickness = new Thickness(1);
                box.SetResourceReference(Border.CornerRadiusProperty, res[i]);
                box.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                box.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                sp.Children.Add(box);
                TextBlock t = Ui.Text(names[i], 11, "TextFaintBrush");
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Margin = new Thickness(0, 5, 0, 0);
                sp.Children.Add(t);
                row.Children.Add(sp);
            }
            return row;
        }

        private UIElement SliderRow(string label, double min, double max, double value, string unit,
            Action<double> onChange)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 8, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(132);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = new GridLength(62);

            TextBlock lab = Ui.Text(label, 12, "TextDimBrush");
            lab.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(lab);

            TextBlock val = Ui.Text("", 11.5, "TextFaintBrush");
            val.FontFamily = Fonts.Mono;
            val.VerticalAlignment = VerticalAlignment.Center;
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 2);
            g.Children.Add(val);

            string fmt = unit == "0 %" ? "0 %" : unit;
            bool percent = unit == "0 %";

            Slider s = new Slider();
            s.Minimum = min;
            s.Maximum = max;
            s.Value = value;
            s.Margin = new Thickness(8, 0, 10, 0);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);

            EventHandler update = delegate
            {
                val.Text = percent
                    ? Math.Round(s.Value * 100).ToString(CultureInfo.InvariantCulture) + " %"
                    : Math.Round(s.Value).ToString(CultureInfo.InvariantCulture) + " px";
            };
            s.ValueChanged += delegate
            {
                update(null, null);
                onChange(s.Value);
            };
            update(null, null);
            return g;
        }

        private void PickImage()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = Loc.T("选择背景图片");
            dlg.Filter = Loc.T("图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|所有文件 (*.*)|*.*");
            if (dlg.ShowDialog(this) == true)
            {
                _working.BackgroundImage = dlg.FileName;
                Themes.Set(_working, false);
                RefreshAll();
            }
        }

        private void ExportTheme()
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Title = Loc.T("导出主题");
            dlg.Filter = Loc.T("主题文件 (*.json)|*.json");
            dlg.FileName = "cmdtoolbox-theme.json";
            if (dlg.ShowDialog(this) == true)
            {
                try { Json.WriteToFile(dlg.FileName, _working.ToJson()); }
                catch (Exception ex) { MessageBox.Show(this, Loc.T("导出失败：") + ex.Message, Loc.T("错误")); }
            }
        }

        private void ImportTheme()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = Loc.T("导入主题");
            dlg.Filter = Loc.T("主题文件 (*.json)|*.json|所有文件 (*.*)|*.*");
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    Dictionary<string, object> o = Json.AsObj(Json.ParseFile(dlg.FileName));
                    if (o == null) { MessageBox.Show(this, Loc.T("文件内容不是有效的主题。"), Loc.T("错误")); return; }
                    Theme t = Theme.FromJson(o);
                    _working = t;
                    Themes.Set(_working, false);
                    RefreshAll();
                }
                catch (Exception ex) { MessageBox.Show(this, Loc.T("导入失败：") + ex.Message, Loc.T("错误")); }
            }
        }
    }

    // -----------------------------------------------------------------------
    //  设置
    // -----------------------------------------------------------------------
    internal sealed class SettingsWindow : FlatDialog
    {
        private Action _adminNoteRefresh;
        private CheckBox _runAsAdminBox;
        private Action _multiNoteRefresh;
        private CheckBox _strictBox;
        private TextBlock _strictNote;

        private void RefreshStrictNote()
        {
            if (_strictNote == null || _strictBox == null) return;
            _strictNote.Text = _strictBox.IsChecked == true
                ? Loc.T("已开启：只提示语法上合法的参数，不列出已经用过的参数。")
                : Loc.T("已关闭（默认）：列出全部参数，用过的标灰显示「已使用」。");
        }
        private TextBlock _adminNote;

        /// <summary>对话框内容用 Grid/StackPanel 搭出来，这里递归找带 Tag 的元素。</summary>

        public SettingsWindow(MainWindow main)
            : base(Loc.T("设置"), 660, 900)
        {
            if (main != null) Owner = main;
            AppSettings s = main.Settings;

            // ---------- 界面语言 ----------
            Body.Children.Add(Label(Loc.T("界面语言"),
                Loc.T("切换后界面立即刷新，不需要重启。选「默认」时跟随系统语言：系统是中文就用简体中文，否则用 English。")
                + Loc.T("命令库的说明文字目前只有中文。")));

            StackPanel langRow = Ui.H();
            langRow.Margin = new Thickness(0, 2, 0, 4);
            ComboBox langBox = new ComboBox();
            langBox.Width = 240;
            langBox.Items.Add(Loc.T("默认（跟随系统）"));
            langBox.Items.Add(Loc.T("简体中文"));
            langBox.Items.Add("English");
            langBox.SelectedIndex = s.Language == "zh" ? 1 : (s.Language == "en" ? 2 : 0);
            langBox.SelectionChanged += delegate
            {
                if (langBox.SelectedIndex < 0) return;
                string id = langBox.SelectedIndex == 1 ? "zh" : (langBox.SelectedIndex == 2 ? "en" : "auto");
                if (id == s.Language) return;
                s.Language = id;
                s.Save();
                Loc.Apply(id);
                // 关掉设置窗口 -> 用新语言重建主界面 -> 再把设置窗口开回来
                Close();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (main != null) main.RebuildUi();
                    if (main != null) new SettingsWindow(main).ShowDialog();
                }));
            };
            langRow.Children.Add(langBox);

            TextBlock langNote = Ui.Text("", 11.5, "TextFaintBrush");
            langNote.VerticalAlignment = VerticalAlignment.Center;
            langNote.Margin = new Thickness(12, 0, 0, 0);
            langNote.Text = Loc.IsEnglish ? "Current: English" : Loc.T("当前：简体中文");
            langRow.Children.Add(langNote);
            Body.Children.Add(langRow);

            Body.Children.Add(Label(Loc.T("默认命令行工具"), Loc.T("程序启动时默认使用哪个工具（默认是 PowerShell）。运行时可以随时用标题栏的开关切换：Ctrl+1 用 CMD，Ctrl+2 用 PowerShell；也可以用 cmd> / ps> 前缀临时指定单条命令。")));
            StackPanel shells = Ui.H();
            shells.Margin = new Thickness(0, 0, 0, 4);
            CheckBox defCmd = new CheckBox();
            defCmd.Content = Loc.T("默认以 CMD 启动（不勾选则用 PowerShell）");
            defCmd.IsChecked = string.Equals(s.DefaultShell, "cmd", StringComparison.OrdinalIgnoreCase);
            defCmd.Checked += delegate { s.DefaultShell = "cmd"; };
            defCmd.Unchecked += delegate { s.DefaultShell = "ps"; };
            shells.Children.Add(defCmd);

            CheckBox preferPwsh = new CheckBox();
            preferPwsh.Content = Loc.T("PowerShell 优先用 PowerShell 7（pwsh）");
            preferPwsh.Margin = new Thickness(22, 0, 0, 0);
            preferPwsh.IsChecked = s.PreferPwsh;
            preferPwsh.Checked += delegate { s.PreferPwsh = true; };
            preferPwsh.Unchecked += delegate { s.PreferPwsh = false; };
            shells.Children.Add(preferPwsh);
            Body.Children.Add(shells);

            TextBlock psNote = Ui.Wrap(Loc.T("本机检测：") + (CommandExecutor.PwshAvailable()
                ? Loc.T("已安装 pwsh，勾选后会优先使用它。")
                : Loc.T("未安装 pwsh，勾选后会自动回退到系统自带的 Windows PowerShell 5.1。"))
                + Loc.T("  当前宿主：") + System.IO.Path.GetFileName(CommandExecutor.ResolvePowerShell(s.PreferPwsh)),
                11.5, "TextFaintBrush");
            psNote.Margin = new Thickness(0, 0, 0, 4);
            Body.Children.Add(psNote);

            TextBlock defNote = Ui.Wrap(Loc.T("这两项只决定「启动时」用哪个工具。标题栏上的 CMD / PowerShell 开关和运行按钮旁的下拉只影响本次运行，不会改动这里的设置。"), 11, "TextFaintBrush");
            defNote.Margin = new Thickness(0, 0, 0, 4);
            Body.Children.Add(defNote);

            CheckBox cross = new CheckBox();
            cross.Content = Loc.T("在 CMD 里跑了 PowerShell 命令（或反过来）失败时，提示这是另一个工具的命令");
            cross.IsChecked = s.CrossShellHint;
            cross.Margin = new Thickness(0, 2, 0, 4);
            cross.Checked += delegate { s.CrossShellHint = true; };
            cross.Unchecked += delegate { s.CrossShellHint = false; };
            Body.Children.Add(cross);

            Body.Children.Add(Label(Loc.T("启动权限"),
                Loc.T("程序默认以普通权限启动，需要管理员的命令会单独提示并支持一键提权重启。")
                + Loc.T("打开下面这项后，每次启动都会先弹出 UAC 请求管理员权限。")));

            StackPanel adminRow = Ui.H();
            adminRow.Margin = new Thickness(0, 0, 0, 4);
            CheckBox runAsAdmin = new CheckBox();
            runAsAdmin.Content = Loc.T("默认以管理员权限启动（UAC）");
            runAsAdmin.IsChecked = s.RunAsAdmin;
            runAsAdmin.Checked += delegate
            {
                s.RunAsAdmin = true;
                if (_adminNoteRefresh != null) _adminNoteRefresh();
            };
            runAsAdmin.Unchecked += delegate
            {
                s.RunAsAdmin = false;
                if (_adminNoteRefresh != null) _adminNoteRefresh();
            };
            adminRow.Children.Add(runAsAdmin);
            Body.Children.Add(adminRow);

            _adminNote = Ui.Wrap("", 11.5, "TextFaintBrush");
            _adminNote.Margin = new Thickness(0, 0, 0, 4);
            Body.Children.Add(_adminNote);

            _runAsAdminBox = runAsAdmin;
            _adminNoteRefresh = delegate
            {
                bool admin = Elevation.IsAdministrator();
                string t;
                if (admin)
                    t = Loc.T("当前已经是管理员身份，这项设置不会产生额外影响。");
                else if (_runAsAdminBox.IsChecked == true)
                    t = Loc.T("下次启动时会弹出 UAC 请求提权；如果点了「否」，程序仍会以普通权限继续运行。")
                      + Loc.T("重启程序后生效 —— 本次会话改不了当前进程的权限。");
                else
                    t = Loc.T("当前是普通权限。需要管理员的命令仍会单独提示，可以随时一键提权重启。");
                _adminNote.Text = t;
            };
            _adminNoteRefresh();

            Body.Children.Add(Label(Loc.T("多命令执行"),
                Loc.T("打开输入框旁边的「多命令」开关后，单行输入框会变成一块多行文本区，")
                + Loc.T("可以把一整段命令粘进去一次执行 —— 就像在记事本里编辑好再执行。")
                + Loc.T("整段文本交给同一个 shell，所以 cd、变量、管道在行与行之间是连着的。")));

            StackPanel multiRow = Ui.H();
            multiRow.Margin = new Thickness(0, 0, 0, 4);
            CheckBox multiPersist = new CheckBox();
            multiPersist.Content = Loc.T("多命令执行持久生效");
            multiPersist.IsChecked = s.MultiCommandPersist;
            multiPersist.Checked += delegate
            {
                s.MultiCommandPersist = true;
                if (main != null) main.UpdateMultiToggle();
                if (_multiNoteRefresh != null) _multiNoteRefresh();
            };
            multiPersist.Unchecked += delegate
            {
                s.MultiCommandPersist = false;
                if (main != null) main.UpdateMultiToggle();
                if (_multiNoteRefresh != null) _multiNoteRefresh();
            };
            multiRow.Children.Add(multiPersist);
            Body.Children.Add(multiRow);

            TextBlock multiNote = Ui.Wrap("", 11.5, "TextFaintBrush");
            multiNote.Margin = new Thickness(0, 0, 0, 6);
            Body.Children.Add(multiNote);

            Slider multiHeight = new Slider();
            multiHeight.Minimum = 80;
            multiHeight.Maximum = 400;
            multiHeight.Value = s.MultiCommandHeight;
            multiHeight.Width = 260;
            multiHeight.HorizontalAlignment = HorizontalAlignment.Left;
            TextBlock multiHeightVal = Ui.Text("", 11.5, "TextFaintBrush");

            Grid heightRow = new Grid();
            heightRow.Margin = new Thickness(0, 2, 0, 4);
            heightRow.ColumnDefinitions.Add(new ColumnDefinition());
            heightRow.ColumnDefinitions[0].Width = new GridLength(132);
            heightRow.ColumnDefinitions.Add(new ColumnDefinition());
            heightRow.ColumnDefinitions.Add(new ColumnDefinition());
            heightRow.ColumnDefinitions[2].Width = GridLength.Auto;
            TextBlock hl = Ui.Text(Loc.T("多行输入区高度"), 12, "TextDimBrush");
            hl.VerticalAlignment = VerticalAlignment.Center;
            heightRow.Children.Add(hl);
            multiHeight.Margin = new Thickness(8, 0, 10, 0);
            Grid.SetColumn(multiHeight, 1);
            heightRow.Children.Add(multiHeight);
            multiHeightVal.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(multiHeightVal, 2);
            heightRow.Children.Add(multiHeightVal);
            Body.Children.Add(heightRow);

            multiHeight.ValueChanged += delegate
            {
                s.MultiCommandHeight = Math.Round(multiHeight.Value);
                multiHeightVal.Text = ((int)s.MultiCommandHeight).ToString(CultureInfo.InvariantCulture) + " px";
                if (main != null) main.ApplyMultiHeight();
            };
            multiHeightVal.Text = ((int)s.MultiCommandHeight).ToString(CultureInfo.InvariantCulture) + " px";

            Action refreshMultiNote = delegate
            {
                multiNote.Text = multiPersist.IsChecked == true
                    ? Loc.T("已开启：多命令模式会一直保持，直到你手动关掉开关。")
                    : Loc.T("已关闭（默认）：多命令模式只生效一次，执行完自动回到单行输入框。");
            };
            _multiNoteRefresh = refreshMultiNote;
            refreshMultiNote();

            // ---------- 多进程执行（与「多命令执行」并排的另一节） ----------
            Body.Children.Add(Label(Loc.T("多进程执行"),
                Loc.T("并发跑多条命令，每条有自己的输出区，可以单独停止。")
                + Loc.T("打开输入区旁边的「多进程」开关后，会打开一个独立的多进程窗口：")
                + Loc.T("每条各起一个独立进程，输出互不干扰，退出码也分别显示，随时可以只停其中一条。")));

            Slider multiProcHeight = new Slider();
            // 下限 180：窗口里行数多的时候靠内部滚动，不再压缩每行的高度
            multiProcHeight.Minimum = 180;
            multiProcHeight.Maximum = 600;
            multiProcHeight.Value = s.MultiProcHeight;
            multiProcHeight.Width = 260;
            multiProcHeight.HorizontalAlignment = HorizontalAlignment.Left;
            TextBlock multiProcHeightVal = Ui.Text("", 11.5, "TextFaintBrush");

            Grid mpHeightRow = new Grid();
            mpHeightRow.Margin = new Thickness(0, 2, 0, 4);
            mpHeightRow.ColumnDefinitions.Add(new ColumnDefinition());
            mpHeightRow.ColumnDefinitions[0].Width = new GridLength(132);
            mpHeightRow.ColumnDefinitions.Add(new ColumnDefinition());
            mpHeightRow.ColumnDefinitions.Add(new ColumnDefinition());
            mpHeightRow.ColumnDefinitions[2].Width = GridLength.Auto;
            TextBlock mpLabel = Ui.Text(Loc.T("多进程窗口高度"), 12, "TextDimBrush");

            // ---- 任务行数上限：ComboBox 里给几个常用档，0 = 不限制 ----
            StackPanel rowLimitRow = Ui.H();
            rowLimitRow.Margin = new Thickness(0, 8, 0, 0);
            rowLimitRow.Children.Add(Ui.Text(Loc.T("任务行数上限"), 12, "TextDimBrush"));
            ComboBox rowLimit = new ComboBox();
            rowLimit.Width = 150;
            rowLimit.Margin = new Thickness(10, 0, 0, 0);
            int[] rowVals = new int[] { 1, 2, 3, 4, 6, 8, 12, 16, 0 };
            string[] rowTexts = new string[] { "1", "2", "3", "4", "6", "8", "12", "16", Loc.T("不限制") };
            int rowSel = 5;
            for (int i = 0; i < rowVals.Length; i++)
            {
                rowLimit.Items.Add(rowTexts[i]);
                if (rowVals[i] == s.MultiProcRowLimit) rowSel = i;
            }
            rowLimit.SelectedIndex = rowSel;
            rowLimit.SelectionChanged += delegate
            {
                if (rowLimit.SelectedIndex < 0) return;
                s.MultiProcRowLimit = rowVals[rowLimit.SelectedIndex];
            };
            rowLimitRow.Children.Add(rowLimit);
            Body.Children.Add(rowLimitRow);

            mpLabel.VerticalAlignment = VerticalAlignment.Center;
            mpHeightRow.Children.Add(mpLabel);
            multiProcHeight.Margin = new Thickness(8, 0, 10, 0);
            Grid.SetColumn(multiProcHeight, 1);
            mpHeightRow.Children.Add(multiProcHeight);
            multiProcHeightVal.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(multiProcHeightVal, 2);
            mpHeightRow.Children.Add(multiProcHeightVal);
            Body.Children.Add(mpHeightRow);

            multiProcHeight.ValueChanged += delegate
            {
                s.MultiProcHeight = Math.Round(multiProcHeight.Value);
                multiProcHeightVal.Text = ((int)s.MultiProcHeight).ToString(CultureInfo.InvariantCulture) + " px";
                // 面板展开着的时候立刻生效，不用关掉设置窗口才看得到效果
                if (main != null) main.ApplyMultiProcHeight();
            };
            multiProcHeightVal.Text = ((int)s.MultiProcHeight).ToString(CultureInfo.InvariantCulture) + " px";

            TextBlock mpNote = Ui.Wrap(Loc.T("多进程窗口默认是关着的，点输入区旁边的「多进程」开关即可打开；")
                + Loc.T("关掉的是窗口本身，正在跑的任务不会停。")
                + Loc.T("每条任务的命令和输出只在本次运行期间保留，不会写进设置文件。"), 11.5, "TextFaintBrush");
            mpNote.Margin = new Thickness(0, 0, 0, 6);
            Body.Children.Add(mpNote);


            Body.Children.Add(Label(Loc.T("提示行为"), Loc.T("控制参数提示列出多少内容。命令库 JSON 里 nodes 的书写顺序就是提示顺序，引擎不二次排序。")));

            CheckBox strictOrder = new CheckBox();
            strictOrder.Content = Loc.T("按语法顺序限制提示参数");
            strictOrder.IsChecked = s.StrictSyntaxOrder;
            strictOrder.Margin = new Thickness(0, 2, 0, 2);
            strictOrder.ToolTip = Loc.T("打开后只列出当前语法位置合法的参数：必填的位置参数还没填时，只提示它；已经用过且不能重复的参数不再列出。可以避免选出拼起来跑不通的命令。关闭时所有参数都会列出，用过的标灰显示「已使用」。");
            strictOrder.Checked += delegate
            {
                s.StrictSyntaxOrder = true;
                if (main != null) main.ApplyStrictOrder();
                RefreshStrictNote();
            };
            strictOrder.Unchecked += delegate
            {
                s.StrictSyntaxOrder = false;
                if (main != null) main.ApplyStrictOrder();
                RefreshStrictNote();
            };
            Body.Children.Add(strictOrder);

            TextBlock strictNote = Ui.Wrap("", 11.5, "TextFaintBrush");
            strictNote.Margin = new Thickness(0, 0, 0, 2);
            Body.Children.Add(strictNote);
            _strictNote = strictNote;
            _strictBox = strictOrder;
            RefreshStrictNote();

            Body.Children.Add(Label(Loc.T("控制台提示"), Loc.T("控制台底部那块帮助条，写着快捷键和命令库条数。钉在面板底部不随输出滚动，长输出时占掉一点可视高度，不需要可以关掉。")));

            CheckBox helpBox = new CheckBox();
            helpBox.Content = Loc.T("在控制台底部显示帮助条");
            helpBox.IsChecked = s.ShowConsoleHelp;
            helpBox.Margin = new Thickness(0, 2, 0, 4);
            helpBox.Checked += delegate { s.ShowConsoleHelp = true; if (main != null) main.RebuildConsoleHelp(); };
            helpBox.Unchecked += delegate { s.ShowConsoleHelp = false; if (main != null) main.RebuildConsoleHelp(); };
            Body.Children.Add(helpBox);

            Body.Children.Add(Label(Loc.T("输出编码"), Loc.T("命令输出可能是 GBK（中文系统默认）也可能是 UTF-8（git / python 等）。自动模式会逐行智能判断，基本不会出现乱码。")));
            ComboBox enc = new ComboBox();
            enc.Items.Add(Loc.T("自动识别（推荐）"));
            enc.Items.Add("UTF-8");
            enc.Items.Add(Loc.T("系统 OEM（中文为 GBK）"));
            enc.Items.Add("GBK / CP936");
            enc.SelectedIndex = s.OutputEncoding == "utf8" ? 1 : s.OutputEncoding == "oem" ? 2 : s.OutputEncoding == "gbk" ? 3 : 0;
            enc.SelectionChanged += delegate
            {
                string[] vals = new string[] { "auto", "utf8", "oem", "gbk" };
                if (enc.SelectedIndex >= 0) s.OutputEncoding = vals[enc.SelectedIndex];
            };
            Body.Children.Add(enc);

            Body.Children.Add(Label(Loc.T("控制台字号"), ""));
            Slider font = new Slider();
            font.Minimum = 10; font.Maximum = 22; font.Value = s.FontSize;
            TextBlock fontVal = Ui.Text(((int)s.FontSize) + " px", 11.5, "TextFaintBrush");
            fontVal.FontFamily = Fonts.Mono;
            font.ValueChanged += delegate
            {
                s.FontSize = font.Value;
                fontVal.Text = ((int)font.Value) + " px";
            };
            Body.Children.Add(font);
            Body.Children.Add(fontVal);

            Body.Children.Add(Label(Loc.T("安全确认"), ""));
            CheckBox danger = new CheckBox();
            danger.Content = Loc.T("执行高危命令（格式化 / 删除 / 关机 / 改引导等）前弹出确认框");
            danger.IsChecked = s.ConfirmDanger;
            danger.Margin = new Thickness(0, 4, 0, 4);
            danger.Checked += delegate { s.ConfirmDanger = true; };
            danger.Unchecked += delegate { s.ConfirmDanger = false; };
            Body.Children.Add(danger);

            CheckBox lv1 = new CheckBox();
            lv1.Content = Loc.T("执行会改动系统的命令（注册表 / 服务 / 权限等）前也弹确认框");
            lv1.IsChecked = s.ConfirmLevel1;
            lv1.Margin = new Thickness(0, 4, 0, 4);
            lv1.Checked += delegate { s.ConfirmLevel1 = true; };
            lv1.Unchecked += delegate { s.ConfirmLevel1 = false; };
            Body.Children.Add(lv1);

            CheckBox scroll = new CheckBox();
            scroll.Content = Loc.T("输出时自动滚动到底部");
            scroll.IsChecked = s.AutoScroll;
            scroll.Margin = new Thickness(0, 4, 0, 4);
            scroll.Checked += delegate { s.AutoScroll = true; };
            scroll.Unchecked += delegate { s.AutoScroll = false; };
            Body.Children.Add(scroll);

            TextBlock dataInfo = Ui.Wrap("", 11, "TextFaintBrush");
            dataInfo.Margin = new Thickness(0, 4, 0, 4);
            dataInfo.Text = Loc.T(AppPaths.ModeDescription) + "\n" + AppPaths.UserDataDirectory;
            Body.Children.Add(dataInfo);
            Body.Children.Add(Label(Loc.T("数据位置"), Loc.T("命令库扩展目录（放 JSON 即可扩展命令库）：") + AppPaths.CommandsDirectory
                + Loc.T("\n个人配置与历史：") + AppPaths.UserDataDirectory));

            Button open = Ui.Btn(Loc.T("打开配置目录"), "GhostButton", delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.UserDataDirectory); }
                catch { }
            });
            open.Margin = new Thickness(0, 0, 6, 0);
            Footer.Children.Add(open);

            Button cancel = Ui.Btn(Loc.T("取消"), "GhostButton", delegate { DialogResult = false; Close(); });
            cancel.MinWidth = 84;
            Footer.Children.Add(cancel);

            Button ok = Ui.Btn(Loc.T("保存"), "PrimaryButton", delegate
            {
                s.Save();
                DialogResult = true;
                Close();
            });
            ok.MinWidth = 96;
            ok.Margin = new Thickness(8, 0, 0, 0);
            Footer.Children.Add(ok);
        }

        /// <summary>
        /// 小节标题。长说明收进右侧的 ? 悬停提示，正文只留一句话速览 ——
        /// 原来每节都是三四行灰字，整页读起来很累。
        /// </summary>
        private static UIElement Label(string title, string desc)
        {
            StackPanel sp = Ui.V();
            sp.Margin = new Thickness(0, 18, 0, 6);

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel h = Ui.H();
            Border bar = new Border();
            bar.Width = 3;
            bar.Height = 14;
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            bar.SetResourceReference(Border.CornerRadiusProperty, "MicroCornerRadius");
            h.Children.Add(bar);
            TextBlock tt = Ui.Text("  " + title, 13, "TextBrush", FontWeights.SemiBold);
            tt.VerticalAlignment = VerticalAlignment.Center;
            h.Children.Add(tt);
            head.Children.Add(h);

            if (!string.IsNullOrEmpty(desc))
            {
                Border q = new Border();
                q.Width = 17;
                q.Height = 17;
                q.VerticalAlignment = VerticalAlignment.Center;
                q.Cursor = Cursors.Help;
                q.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
                q.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
                TextBlock qt = Ui.Text("?", 11, "TextDimBrush", FontWeights.SemiBold);
                qt.HorizontalAlignment = HorizontalAlignment.Center;
                qt.VerticalAlignment = VerticalAlignment.Center;
                q.Child = qt;
                ToolTipService.SetToolTip(q, desc);
                ToolTipService.SetInitialShowDelay(q, 180);
                Grid.SetColumn(q, 1);
                head.Children.Add(q);
            }
            sp.Children.Add(head);

            if (!string.IsNullOrEmpty(desc))
            {
                TextBlock brief = Ui.Wrap(Brief(desc, 48), 11, "TextFaintBrush");
                brief.Margin = new Thickness(11, 3, 0, 0);
                sp.Children.Add(brief);
            }
            return sp;
        }

        /// <summary>取说明的第一句、最多 max 字，作为一行速览。</summary>
        private static string Brief(string desc, int max)
        {
            string s = desc.Replace("\n", " ").Trim();
            int cut = -1;
            char[] stops = new char[] { '\u3002', '\uff1b', '.', ';' };
            foreach (char c in stops)
            {
                int i = s.IndexOf(c);
                if (i >= 0 && (cut < 0 || i < cut)) cut = i;
            }
            if (cut >= 0 && cut + 1 <= max) s = s.Substring(0, cut + 1);
            if (s.Length > max) s = s.Substring(0, max - 1) + "\u2026";
            return s;
        }
    }

    // -----------------------------------------------------------------------
    //  帮助
    // -----------------------------------------------------------------------
    internal sealed class HelpWindow : FlatDialog
    {
        public HelpWindow(MainWindow main)
            : base(Loc.T("使用帮助"), 640, 620)
        {
            if (main != null) Owner = main;

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.MaxHeight = 790;
            StackPanel sp = Ui.V();
            sp.Margin = new Thickness(0, 0, 12, 0);

            Add(sp, Loc.T("核心用法"), null);
            Add(sp, Loc.T("1. 在左侧命令库里点一条命令，右侧会显示它的用途和可填写的参数表单。"), null);
            Add(sp, Loc.T("2. 在命令行里输入命令名，敲空格之后会自动提示下一个可用的参数。"), null);
            Add(sp, Loc.T("3. Tab 补全当前候选，↑↓ 选择候选，Enter 执行。"), null);
            Add(sp, Loc.T("4. 参数会按当前输入的层级变化：例如 netsh → wlan → show → profiles 会逐级提示。"), null);
            Add(sp, Loc.T("5. 已经用过的参数会自动标注「已使用」，不会重复干扰提示。"), null);

            Add(sp, Loc.T("快捷键"), null);
            Add(sp, Loc.T("Ctrl+K  聚焦顶部搜索框，全文检索命令库"), "kbd");
            Add(sp, Loc.T("Ctrl+L  清空输出控制台"), "kbd");
            Add(sp, Loc.T("Ctrl+T  打开主题设置"), "kbd");
            Add(sp, Loc.T("Tab     补全 / 应用选中的候选"), "kbd");
            Add(sp, Loc.T("↑ ↓     在候选中上下移动；输入框为空时 ↑ 调出上一条命令"), "kbd");
            Add(sp, Loc.T("Enter   执行命令（若已用 ↑↓ 选中候选，则先补全）"), "kbd");
            Add(sp, Loc.T("Esc     关闭候选列表 / 清空输入"), "kbd");
            Add(sp, Loc.T("F5      重新执行上一条命令"), "kbd");
            Add(sp, Loc.T("F1      打开本帮助"), "kbd");

            Add(sp, Loc.T("工作目录"), null);
            Add(sp, Loc.T("cd / pushd / popd 由工具箱自己维护，切换目录后下一条命令仍在该目录执行，状态栏会实时显示当前路径。"), null);

            Add(sp, Loc.T("管理员权限"), null);
            Add(sp, Loc.T("工具箱默认以普通权限启动。需要管理员的命令会在标题栏和确认框里提示，点标题栏的「以管理员重启」即可提权重启（会弹出系统 UAC 确认）。"), null);

            Add(sp, Loc.T("扩展命令库"), null);
            Add(sp, Loc.T("在程序目录的 commands 子目录里放入符合格式的 JSON 文件，重启后即可扩展或覆盖命令库。内置命令库已经打包在 exe 内部。"), null);

            Add(sp, Loc.T("中文乱码"), null);
            Add(sp, Loc.T("输出编码默认为自动识别：逐行尝试 UTF-8，失败则回退到系统 OEM 代码页（中文系统为 GBK）。如果某个工具仍然乱码，可以在设置里手动指定编码。"), null);

            sv.Content = sp;
            Body.Children.Add(sv);

            Button ok = Ui.Btn(Loc.T("知道了"), "PrimaryButton", delegate { Close(); });
            ok.MinWidth = 96;
            Footer.Children.Add(ok);
        }

        private static void Add(StackPanel host, string text, string kind)
        {
            if (kind == null && !text.StartsWith(" ")) 
            {
                TextBlock h = Ui.Text(text, 13, "TextBrush", FontWeights.SemiBold);
                h.Margin = new Thickness(0, 12, 0, 4);
                host.Children.Add(h);
                return;
            }
            TextBlock t = Ui.Wrap(text, 12, kind == "kbd" ? "TextDimBrush" : "TextDimBrush");
            t.Margin = new Thickness(kind == "kbd" ? 8 : 0, 2, 0, 2);
            if (kind == "kbd") t.FontFamily = Fonts.Mono;
            host.Children.Add(t);
        }
    }
}
