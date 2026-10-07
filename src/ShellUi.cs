// ---------------------------------------------------------------------------
//  ShellUi.cs — 双引擎界面：全局切换开关、临时切换、跨工具提示条
//
//  三种"用哪个工具执行"的来源，优先级从高到低：
//    1. 命令行前缀       cmd> ipconfig   /   ps> Get-Process
//    2. 临时切换         标题栏开关旁边的下拉选「本次用 CMD/PowerShell」，只影响下一条
//    3. 全局切换         标题栏的 [CMD | PowerShell] 开关
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace WindowsCommandTools
{
    public partial class MainWindow
    {
        /// <summary>这条命令实际会用哪个 shell 执行（临时切换优先）。</summary>
        internal ShellKind EffectiveShell
        {
            get { return TempShell.HasValue ? TempShell.Value : Shell; }
        }

        // ==================================================================
        //  全局切换开关
        // ==================================================================

        private Border BuildShellSwitch()
        {
            Border outer = new Border();
            outer.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            outer.BorderThickness = new Thickness(1);
            outer.Padding = new Thickness(3);
            outer.VerticalAlignment = VerticalAlignment.Center;
            outer.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            ToolTipService.SetToolTip(outer, Loc.T("选择用哪个命令行工具执行（全局切换，Ctrl+1 用 CMD / Ctrl+2 用 PowerShell）"));

            StackPanel seg = Ui.H();
            ShellCmdButton = MakeSegment("CMD", delegate { SetShell(ShellKind.Cmd, true); });
            ShellPsButton = MakeSegment("PowerShell", delegate { SetShell(ShellKind.PowerShell, true); });
            seg.Children.Add(ShellCmdButton);
            seg.Children.Add(ShellPsButton);
            outer.Child = seg;

            // 标题栏在 WindowChrome 里默认整块当标题栏用，鼠标事件会被拖动吃掉。
            // 必须显式声明这里要参与命中测试，否则按钮点了没反应。
            WindowChrome.SetIsHitTestVisibleInChrome(outer, true);
            WindowChrome.SetIsHitTestVisibleInChrome(seg, true);
            WindowChrome.SetIsHitTestVisibleInChrome(ShellCmdButton, true);
            WindowChrome.SetIsHitTestVisibleInChrome(ShellPsButton, true);
            return outer;
        }

        private Button MakeSegment(string text, RoutedEventHandler onClick)
        {
            Button b = new Button();
            b.Content = text;
            b.SetResourceReference(FrameworkElement.StyleProperty, "FlatButtonBase");
            b.Padding = new Thickness(13, 4, 13, 4);
            b.FontSize = 12.5;
            b.MinWidth = 54;
            b.Click += onClick;
            return b;
        }

        private void ApplySegmentState(Button b, bool active)
        {
            try
            {
                if (active)
                {
                    b.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                    b.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
                    b.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
                    b.FontWeight = FontWeights.Normal;
                }
            }
            catch { }
        }

        /// <summary>切换全局 shell。</summary>
        internal void SetShell(ShellKind shell, bool save)
        {
            if (Shell == shell && TempShell == null) { UpdateShellUi(); return; }
            Shell = shell;
            TempShell = null;
            // 注意：标题栏这个开关**只改本次运行的引擎**，不写进"启动默认"。
            // 启动用哪个引擎由设置里的「默认以 CMD 启动」单独决定 ——
            // 两者混在一起会让用户点一下开关就悄悄改掉下次启动的行为。
            UpdateShellUi();
            Themes.OnShellChanged(shell);
            PopulateCategories();
            AppendSystem(Loc.T("已切换到 ") + Shells.DisplayCn(shell) + Loc.T(" 模式")
                + (shell == ShellKind.PowerShell ? Loc.T("（宿主 ") + System.IO.Path.GetFileName(CommandExecutor.ResolvePowerShell(PreferPwsh)) + "）" : ""));
            if (Input != null) Input.Focus();
        }

        // ==================================================================
        //  临时切换
        // ==================================================================

        private Border BuildTempShellChip()
        {
            TempShellChip = new Border();
            TempShellChip.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            TempShellChip.Padding = new Thickness(9, 3, 5, 3);
            TempShellChip.Margin = new Thickness(10, 0, 0, 0);
            TempShellChip.VerticalAlignment = VerticalAlignment.Center;
            TempShellChip.Visibility = Visibility.Collapsed;
            TempShellChip.SetResourceReference(Border.BackgroundProperty, "WarningSoftBrush");
            ToolTipService.SetToolTip(TempShellChip, Loc.T("下一条命令会改用这个工具执行，执行完自动恢复"));

            StackPanel sp = Ui.H();
            TempShellText = Ui.Text("", 11.5, "WarningBrush", FontWeights.SemiBold);
            TempShellText.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(TempShellText);

            Button x = new Button();
            x.Content = "✕";
            x.SetResourceReference(FrameworkElement.StyleProperty, "FlatButtonBase");
            x.Padding = new Thickness(5, 0, 2, 0);
            x.FontSize = 11;
            x.SetResourceReference(Control.ForegroundProperty, "WarningBrush");
            x.ToolTip = Loc.T("取消临时切换");
            x.Click += delegate { SetTempShell(null); };
            sp.Children.Add(x);

            TempShellChip.Child = sp;
            WindowChrome.SetIsHitTestVisibleInChrome(TempShellChip, true);
            WindowChrome.SetIsHitTestVisibleInChrome(x, true);
            return TempShellChip;
        }

        /// <summary>设置临时切换（null = 取消）。</summary>
        internal void SetTempShell(ShellKind? shell)
        {
            TempShell = shell;
            UpdateShellUi();
            if (Input != null) Input.Focus();
        }

        // ==================================================================
        //  统一刷新界面
        // ==================================================================

        /// <summary>设置里改「按语法顺序限制提示参数」后同步给提示引擎。</summary>
        internal void ApplyStrictOrder()
        {
            Suggester.StrictOrder = Settings.StrictSyntaxOrder;
            UpdateSuggestions();
        }

        internal void UpdateShellUi()
        {
            ApplySegmentState(ShellCmdButton, Shell == ShellKind.Cmd);
            ApplySegmentState(ShellPsButton, Shell == ShellKind.PowerShell);

            if (TempShellChip != null)
            {
                if (TempShell.HasValue)
                {
                    TempShellChip.Visibility = Visibility.Visible;
                    TempShellText.Text = Loc.T("本次改用 ") + Shells.Display(TempShell.Value);
                }
                else
                {
                    TempShellChip.Visibility = Visibility.Collapsed;
                }
            }

            if (_inputPlaceholder != null)
            {
                string host = ShellKind.PowerShell == EffectiveShell
                    ? System.IO.Path.GetFileName(CommandExecutor.ResolvePowerShell(PreferPwsh)) : "cmd";
                _inputPlaceholder.Text = EffectiveShell == ShellKind.PowerShell
                    ? Loc.T("输入 PowerShell 命令（") + host + Loc.T("），空格分隔后会自动提示下一个参数…")
                    : Loc.T("输入 CMD 命令（cmd.exe），空格分隔后会自动提示下一个参数…");
            }

            UpdateStatus();
            RefreshCommandList();
        }

        // ==================================================================
        //  跨工具提示条
        // ==================================================================

        internal Border BuildCrossShellBanner()
        {
            CrossShellBanner = new Border();
            CrossShellBanner.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            CrossShellBanner.Padding = new Thickness(12, 8, 10, 8);
            CrossShellBanner.Margin = new Thickness(0, 0, 0, 8);
            CrossShellBanner.BorderThickness = new Thickness(1);
            CrossShellBanner.Visibility = Visibility.Collapsed;
            CrossShellBanner.SetResourceReference(Border.BackgroundProperty, "WarningSoftBrush");
            CrossShellBanner.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            UIElement icon = Icons.Create("bulb", 16, "WarningBrush", 1.9);
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            g.Children.Add(icon);

            StackPanel text = Ui.V();
            text.Margin = new Thickness(10, 0, 0, 0);
            CrossShellText = Ui.Text("", 12.5, "WarningBrush", FontWeights.SemiBold);
            text.Children.Add(CrossShellText);
            TextBlock sub = Ui.Text("", 11, "TextDimBrush");
            sub.Tag = "sub";
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            StackPanel btns = Ui.H();
            btns.VerticalAlignment = VerticalAlignment.Center;
            CrossShellSwitchButton = Ui.Btn(Loc.T("切换并重新执行"), "SoftButton", delegate { CrossShellSwitchAndRun(); });
            CrossShellSwitchButton.FontSize = 12;
            CrossShellSwitchButton.Padding = new Thickness(11, 5, 11, 5);
            btns.Children.Add(CrossShellSwitchButton);

            Button onlyOnce = Ui.Btn(Loc.T("只这一条切过去"), "GhostButton", delegate { CrossShellRunOnce(); });
            onlyOnce.FontSize = 12;
            onlyOnce.Padding = new Thickness(11, 5, 11, 5);
            onlyOnce.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(onlyOnce);

            Button close = Ui.IconBtn("close", Loc.T("关闭提示"), delegate { HideCrossShellBanner(); });
            close.Width = 26; close.Height = 26;
            close.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(close);

            Grid.SetColumn(btns, 2);
            g.Children.Add(btns);

            CrossShellBanner.Child = g;
            return CrossShellBanner;
        }

        /// <summary>显示「这是另一个工具的命令」提示条。</summary>
        internal void ShowCrossShellBanner(CmdSpec other, string command, string reason)
        {
            if (other == null || !Settings.CrossShellHint) return;
            CrossShellTarget = other;
            CrossShellText.Text = other.Name + Loc.T(" 是 ") + Shells.DisplayCn(other.Shell) + Loc.T(" 命令，当前是 ")
                + Shells.DisplayCn(EffectiveShell) + Loc.T(" 模式");
            TextBlock sub = FindTextByTag(CrossShellBanner, "sub");
            if (sub != null)
            {
                sub.Text = reason + Loc.T("   可以一键切过去重新执行它。（") + other.Title + "）";
            }
            CrossShellBanner.Visibility = Visibility.Visible;

            // 临时切换也跟着点出来，方便用户下次自己用
            SetTempShell(other.Shell);
        }

        private static TextBlock FindTextByTag(DependencyObject root, string tag)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(root, i);
                TextBlock tb = c as TextBlock;
                if (tb != null && tag.Equals(tb.Tag)) return tb;
                TextBlock deep = FindTextByTag(c, tag);
                if (deep != null) return deep;
            }
            return null;
        }

        internal void HideCrossShellBanner()
        {
            CrossShellTarget = null;
            if (CrossShellBanner != null) CrossShellBanner.Visibility = Visibility.Collapsed;
        }

        /// <summary>切换到另一个 shell 并把刚才那条命令重新执行一遍。</summary>
        private void CrossShellSwitchAndRun()
        {
            CmdSpec other = CrossShellTarget;
            string cmd = _crossShellCommand;
            HideCrossShellBanner();
            if (other == null || string.IsNullOrEmpty(cmd)) return;
            SetShell(other.Shell, true);
            Input.Text = cmd;
            Input.CaretIndex = cmd.Length;
            ExecuteCurrent();
        }

        private void CrossShellRunOnce()
        {
            CmdSpec other = CrossShellTarget;
            string cmd = _crossShellCommand;
            HideCrossShellBanner();
            if (other == null || string.IsNullOrEmpty(cmd)) return;
            TempShell = other.Shell;
            Input.Text = cmd;
            Input.CaretIndex = cmd.Length;
            ExecuteCurrent();
        }

        internal string _crossShellCommand = "";
    }
}
