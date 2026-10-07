// ---------------------------------------------------------------------------
//  MainPanels.cs — 主窗口的三个面板
//    左：命令库（分类 + 命令卡片）
//    中：命令行输入（实时参数提示下拉） + 输出控制台
//    右：参数提示 / 可视化参数表单 / 命令详情
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsCommandTools
{
    public partial class MainWindow
    {
        internal GlassPanel _sidebarPanel;
        internal GlassPanel _centerPanel;
        internal GlassPanel _rightPanel;

        private ListBox _cmdList;
        private ListBox _catList;
        private TextBlock _listHeader;
        private readonly List<object> _listData = new List<object>();
        private readonly List<string> _catKeys = new List<string>();

        private TextBlock _hintTitle;
        private TextBlock _hintText;
        private TextBlock _inputPlaceholder;

        private TabControl _rightTabs;

        private CmdSpec _currentSpec;
        private bool _formDirty;
        private readonly List<FormField> _formFields = new List<FormField>();
        private readonly List<FrameworkElement> _formEditors = new List<FrameworkElement>();
        private TextBox _formPreview;
        private StackPanel _formHost;

        private static readonly string[] CommonNames = new string[] {
            "ipconfig", "ping", "netstat", "tracert", "nslookup", "tasklist", "taskkill",
            "systeminfo", "dir", "cd", "sfc", "chkdsk", "powercfg", "netsh", "sc", "reg",
            "schtasks", "robocopy", "xcopy", "shutdown", "dism", "wmic", "net", "curl", "git"
        };

        private static readonly string[] CommonPsNames = new string[] {
            "Get-ChildItem", "Set-Location", "Get-Content", "Select-String", "Get-Process",
            "Stop-Process", "Get-Service", "Restart-Service", "Get-NetIPConfiguration",
            "Test-NetConnection", "Get-NetAdapter", "Resolve-DnsName", "Get-ComputerInfo",
            "Get-CimInstance", "Get-WinEvent", "Get-Volume", "Get-Disk", "Get-LocalUser",
            "Get-ScheduledTask", "Where-Object", "Select-Object", "Sort-Object",
            "ForEach-Object", "Measure-Object", "ConvertTo-Json", "Export-Csv",
            "Invoke-WebRequest", "Test-Path", "Copy-Item", "Remove-Item"
        };

        // ==================================================================
        //  左：命令库
        // ==================================================================

        private void BuildSidebar()
        {
            _sidebarPanel = new GlassPanel();
            Grid.SetColumn(_sidebarPanel, 0);
            BodyGrid.Children.Add(_sidebarPanel);

            Grid g = new Grid();
            g.Margin = new Thickness(10, 10, 8, 10);
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[0].Height = GridLength.Auto;
            g.RowDefinitions.Add(new RowDefinition());

            StackPanel head = Ui.V();

            Grid headRow = new Grid();
            headRow.ColumnDefinitions.Add(new ColumnDefinition());
            headRow.ColumnDefinitions.Add(new ColumnDefinition());
            headRow.ColumnDefinitions[1].Width = GridLength.Auto;
            StackPanel titleBox = Ui.H();
            titleBox.Children.Add(Icons.Create("book", 15, "AccentBrush", 1.6));
            TextBlock tl = Ui.Text(Loc.T("命令库"), 13, "TextBrush", FontWeights.SemiBold);
            // 英文 "Command library" 比中文长得多，窄栏时会压到右边的计数上，
            // 加省略号让它在自己的列里截断而不是叠字。
            tl.TextTrimming = TextTrimming.CharacterEllipsis;
            tl.Margin = new Thickness(7, 0, 0, 0);
            tl.VerticalAlignment = VerticalAlignment.Center;
            titleBox.Children.Add(tl);
            headRow.Children.Add(titleBox);
            _listHeader = Ui.Text("", 11, "TextFaintBrush");
            _listHeader.TextTrimming = TextTrimming.CharacterEllipsis;
            _listHeader.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_listHeader, 1);
            headRow.Children.Add(_listHeader);
            head.Children.Add(headRow);

            _catList = new ListBox();
            _catList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _catList.ItemContainerStyle = TryStyle("ChipItem");
            _catList.SelectionChanged += delegate { OnCategoryChanged(); };
            WrapPanel wrap = new WrapPanel();
            _catList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            _catList.Background = Brushes.Transparent;
            _catList.Margin = new Thickness(0, 10, 0, 6);
            ScrollViewer.SetVerticalScrollBarVisibility(_catList, ScrollBarVisibility.Disabled);
            head.Children.Add(_catList);

            head.Children.Add(Ui.HairLine());
            g.Children.Add(head);
            Grid.SetRow(head, 0);

            _cmdList = new ListBox();
            _cmdList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _cmdList.ItemContainerStyle = TryStyle("CardItem");
            _cmdList.Margin = new Thickness(0, 4, 0, 0);
            _cmdList.SelectionChanged += OnCommandSelected;
            _cmdList.MouseDoubleClick += delegate { if (_cmdList.SelectedIndex >= 0) ExecuteCurrent(); };
            g.Children.Add(_cmdList);
            Grid.SetRow(_cmdList, 1);

            _sidebarPanel.Content = g;
            PopulateCategories();
        }

        private Style TryStyle(string key)
        {
            try
            {
                object o = TryFindResource(key);
                return o as Style;
            }
            catch { return null; }
        }

        private void PopulateCategories()
        {
            _catKeys.Clear();
            _catList.Items.Clear();

            AddCategoryChip(Loc.T("常用"), "lightning");
            AddCategoryChip(Loc.T("收藏"), "star");
            AddCategoryChip(Loc.T("历史"), "history");

            // 只列出当前 shell 的分类
            foreach (CmdCategory c in Lib.Categories)
            {
                if (c.Shell != Shell) continue;
                AddCategoryChip(c.Name, c.Icon);
            }

            if (_catList.Items.Count > 0) _catList.SelectedIndex = 0;
        }

        private void AddCategoryChip(string rawName, string icon)
        {
            _catKeys.Add(rawName);
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel sp = Ui.H();
            Viewbox vb = Icons.Create(icon, 12, "TextDimBrush", 1.6) as Viewbox;
            if (vb != null) sp.Children.Add(vb);
            TextBlock t = Ui.Text(" " + CatDisplayName(rawName), 12, null);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(5, 0, 0, 0);
            sp.Children.Add(t);
            g.Children.Add(sp);

            _catList.Items.Add(g);
        }

        internal static string CatDisplayName(string raw)
        {
            if (raw == null) return "";
            int i = raw.IndexOf('\0');
            // 分类名来自命令库 JSON（中文），这里按当前语言翻成显示名
            return Loc.T(i < 0 ? raw : raw.Substring(0, i));
        }

        internal static string CatSource(string raw)
        {
            if (raw == null) return "";
            int i = raw.IndexOf('\0');
            return i < 0 ? "" : raw.Substring(i + 1);
        }

        private void OnCategoryChanged()
        {
            if (_catList.SelectedIndex < 0) return;
            if (_titleSearch != null && _titleSearch.Text.Trim().Length > 0)
            {
                _titleSearch.Text = "";
                return;
            }
            RefreshCommandList();
        }

        private void RefreshCommandList()
        {
            _cmdList.Items.Clear();
            _listData.Clear();
            if (_catList.SelectedIndex < 0) return;

            string key = _catKeys[_catList.SelectedIndex];
            string search = _titleSearch != null ? _titleSearch.Text.Trim() : "";

            if (search.Length > 0)
            {
                List<CmdSpec> hits = Lib.Search(Shell, search, 120);
                foreach (CmdSpec s in hits) AddCommandCard(s, null);
                _listHeader.Text = hits.Count + Loc.T(" 条匹配 · ") + Shells.Display(Shell);
                if (hits.Count == 0) AddEmptyHint(Loc.T("当前 ") + Shells.Display(Shell) + Loc.T(" 命令库里没有匹配的命令。"));
                return;
            }

            if (key == Loc.T("常用"))
            {
                int n = 0;
                foreach (string name in Shell == ShellKind.Cmd ? CommonNames : CommonPsNames)
                {
                    CmdSpec s = Lib.Find(Shell, name);
                    if (s == null) continue;
                    AddCommandCard(s, null);
                    n++;
                }
                _listHeader.Text = n + Loc.T(" 条 · ") + Shells.Display(Shell);
                return;
            }
            if (key == Loc.T("收藏"))
            {
                int n = 0;
                foreach (string fav in History.Favorites)
                {
                    AddSpecialCard(fav, Loc.T("点击填入命令行并执行"), "star", delegate { FillInput(fav); });
                    n++;
                }
                _listHeader.Text = n + Loc.T(" 条");
                if (n == 0) AddEmptyHint(Loc.T("还没有收藏。在历史记录里点 ☆ 可以收藏常用命令。"));
                return;
            }
            if (key == Loc.T("历史"))
            {
                List<HistoryEntry> recent = History.Recent(60, "");
                foreach (HistoryEntry e in recent) AddHistoryCard(e);
                _listHeader.Text = recent.Count + Loc.T(" 条");
                if (recent.Count == 0) AddEmptyHint(Loc.T("还没有执行过命令。"));
                return;
            }

            foreach (CmdCategory c in Lib.Categories)
            {
                if (c.Shell != Shell) continue;
                if (!string.Equals(c.Name, key, StringComparison.Ordinal)) continue;
                foreach (CmdSpec s in c.Commands) AddCommandCard(s, c.Source);
                _listHeader.Text = c.Commands.Count + Loc.T(" 条 · ") + CatSource(c.Name);
                return;
            }
        }

        private void AddEmptyHint(string text)
        {
            TextBlock t = Ui.Wrap(text, 12, "TextFaintBrush");
            t.Margin = new Thickness(10, 14, 10, 10);
            _cmdList.Items.Add(t);
            _listData.Add(null);
        }

        private void AddCommandCard(CmdSpec s, string source)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel left = Ui.V();
            TextBlock name = Ui.Text(s.Name, 13.5, "TextBrush", FontWeights.SemiBold);
            name.FontFamily = Fonts.Mono;
            left.Children.Add(name);
            TextBlock title = Ui.Text(s.Title, 11.5, "TextDimBrush");
            title.Margin = new Thickness(0, 2, 0, 0);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(title);
            g.Children.Add(left);

            StackPanel badges = Ui.H();
            badges.VerticalAlignment = VerticalAlignment.Top;
            if (s.Admin) badges.Children.Add(Badge(Loc.T("管理"), "AccentSoftBrush", "AccentBrush"));
            if (s.Danger >= 2) badges.Children.Add(Badge(Loc.T("高危"), "DangerSoftBrush", "DangerBrush"));
            else if (s.Danger == 1) badges.Children.Add(Badge(Loc.T("改动"), "WarningSoftBrush", "WarningBrush"));
            Grid.SetColumn(badges, 1);
            g.Children.Add(badges);

            g.ToolTip = BuildCommandTooltip(s);
            _cmdList.Items.Add(g);
            _listData.Add(s);
        }

        private static Border Badge(string text, string bg, string fg)
        {
            Border b = MakeBadge(text, bg, fg);
            b.Margin = new Thickness(4, 0, 0, 0);
            b.Padding = new Thickness(5, 1, 5, 1);
            return b;
        }

        private static object BuildCommandTooltip(CmdSpec s)
        {
            StackPanel sp = Ui.V();
            sp.MaxWidth = 420;
            sp.Children.Add(Ui.Text(s.Name + " — " + s.Title, 13, "TextBrush", FontWeights.SemiBold));
            TextBlock u = Ui.Wrap(s.Usage, 12, "AccentBrush");
            u.FontFamily = Fonts.Mono;
            u.Margin = new Thickness(0, 6, 0, 6);
            sp.Children.Add(u);
            sp.Children.Add(Ui.Wrap(s.Desc, 12, "TextDimBrush"));
            sp.Children.Add(Ui.Text(Loc.T("参数 ") + s.NodeCount + Loc.T(" 项  ·  示例 ") + s.Examples.Count + Loc.T(" 条  ·  来源 ") + s.Source,
                11, "TextFaintBrush"));
            ToolTip tt = new ToolTip();
            tt.Content = sp;
            return tt;
        }

        private void AddSpecialCard(string text, string desc, string icon, Action onClick)
        {
            _cmdList.Items.Add(BuildSimpleCard(text, desc, icon, onClick));
            _listData.Add(text);
        }

        private void AddHistoryCard(HistoryEntry e)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel left = Ui.V();
            TextBlock name = Ui.Text(e.Command, 12.5, "TextBrush");
            name.FontFamily = Fonts.Mono;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(name);
            TextBlock title = Ui.Text(e.Time.ToString("MM-dd HH:mm") + Loc.T("  ·  退出码 ") + e.ExitCode, 11, "TextFaintBrush");
            title.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(title);
            g.Children.Add(left);

            Button star = Ui.IconBtn("star", Loc.T("收藏 / 取消收藏"), null);
            star.Width = 26; star.Height = 26;
            star.Click += delegate
            {
                History.ToggleFavorite(e.Command);
                History.Save();
                RefreshCommandList();
            };
            Grid.SetColumn(star, 1);
            g.Children.Add(star);

            Border wrap = new Border();
            wrap.Child = g;
            wrap.Background = Brushes.Transparent;
            wrap.Cursor = Cursors.Hand;
            wrap.MouseLeftButtonUp += delegate { FillInput(e.Command); };
            _cmdList.Items.Add(wrap);
            _listData.Add(e);
        }

        private UIElement BuildSimpleCard(string text, string desc, string icon, Action onClick)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());

            g.Children.Add(Icons.Create(icon, 15, "TextFaintBrush", 1.6));
            StackPanel sp = Ui.V();
            sp.Margin = new Thickness(9, 0, 0, 0);
            TextBlock t = Ui.Text(text, 12.5, "TextBrush");
            t.FontFamily = Fonts.Mono;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(desc)) sp.Children.Add(Ui.Text(desc, 11, "TextFaintBrush"));
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);

            Border wrap = new Border();
            wrap.Child = g;
            wrap.Background = Brushes.Transparent;
            wrap.Cursor = Cursors.Hand;
            wrap.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return wrap;
        }

        private void OnCommandSelected(object sender, SelectionChangedEventArgs e)
        {
            int i = _cmdList.SelectedIndex;
            if (i < 0 || i >= _listData.Count) return;
            object data = _listData[i];
            CmdSpec s = data as CmdSpec;
            if (s != null) SetCurrentSpec(s, true);
        }

        private void FillInput(string text)
        {
            Input.Text = text;
            Input.CaretIndex = Input.Text.Length;
            Input.Focus();
            UpdateSuggestions();
        }

        // ==================================================================
        //  中：输入区 + 控制台
        // ==================================================================

        private void BuildCenter()
        {
            _centerPanel = new GlassPanel();
            Grid.SetColumn(_centerPanel, 2);
            BodyGrid.Children.Add(_centerPanel);

            Grid g = new Grid();
            g.Margin = new Thickness(10, 10, 10, 10);
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[0].Height = GridLength.Auto;   // 0 命令行输入
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[1].Height = GridLength.Auto;   // 1 提示行（多命令 / 多进程 / 定时 / 循环 / 停止）
            // 第 2 行是唯一的 * 行：控制台住在这里，高度有界，
            // 输出超长时由 RichTextBox 自己滚动（以前控制台在 Auto 行里，
            // 内容有多长就撑多长，滚动条永远不出现，帮助块也就没法钉在底部）。
            g.RowDefinitions.Add(new RowDefinition());

            // ---------- 命令行输入 ----------
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions[1].Width = GridLength.Auto;
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions[2].Width = GridLength.Auto;
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions[3].Width = GridLength.Auto;

            InputHost = new Grid();
            Input = new TextBox();
            Input.FontFamily = Fonts.Mono;
            Input.FontSize = 13.5;
            Input.Height = 40;
            Input.Padding = new Thickness(32, 9, 12, 9);
            Input.TextChanged += delegate { UpdateSuggestions(); };
            Input.SelectionChanged += delegate { UpdateSuggestions(); };
            Input.PreviewKeyDown += OnInputKeyDown;
            InputHost.Children.Add(Input);

            TextBlock prompt = Ui.Text("❯", 14, "AccentBrush", FontWeights.Bold);
            prompt.HorizontalAlignment = HorizontalAlignment.Left;
            prompt.VerticalAlignment = VerticalAlignment.Center;
            prompt.Margin = new Thickness(13, 0, 0, 0);
            prompt.IsHitTestVisible = false;
            InputHost.Children.Add(prompt);
            _multiPrompt = prompt;

            // 多命令模式用的多行文本区：默认隐藏，勾选「多命令」后顶替单行输入框
            MultiInput = new TextBox();
            MultiInput.FontFamily = Fonts.Mono;
            MultiInput.FontSize = 13;
            MultiInput.Padding = new Thickness(32, 9, 12, 9);
            MultiInput.AcceptsReturn = true;
            MultiInput.AcceptsTab = true;
            MultiInput.TextWrapping = TextWrapping.NoWrap;
            MultiInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            MultiInput.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            MultiInput.Visibility = Visibility.Collapsed;
            MultiInput.Text = Settings.MultiCommandText ?? "";
            MultiInput.TextChanged += delegate { UpdateSuggestions(); };
            MultiInput.SelectionChanged += delegate { UpdateSuggestions(); };
            MultiInput.PreviewKeyDown += OnMultiInputKeyDown;
            InputHost.Children.Add(MultiInput);

            _inputPlaceholder = Ui.Text(Loc.T("输入 cmd 命令，空格分隔后会自动提示下一个参数…"), 12.5, "TextFaintBrush");
            _inputPlaceholder.Margin = new Thickness(34, 0, 0, 0);
            _inputPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _inputPlaceholder.IsHitTestVisible = false;
            InputHost.Children.Add(_inputPlaceholder);

            row.Children.Add(InputHost);
            Grid.SetColumn(InputHost, 0);

            Grid runHost = new Grid();
            runHost.Margin = new Thickness(8, 0, 0, 0);
            runHost.ColumnDefinitions.Add(new ColumnDefinition());
            runHost.ColumnDefinitions.Add(new ColumnDefinition());
            runHost.ColumnDefinitions[1].Width = GridLength.Auto;

            RunButton = Ui.Btn(Loc.T("运行"), "PrimaryButton", delegate { ExecuteCurrent(); });
            RunButton.Height = 40;
            RunButton.MinWidth = 76;
            RunButton.ToolTip = Loc.T("执行命令（Enter）。Ctrl+Enter 也可强制执行");
            runHost.Children.Add(RunButton);

            Button caret = new Button();
            caret.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            caret.Content = Icons.Create("chevronDown", 13, "OnAccentBrush", 2.0);
            caret.Width = 30;
            caret.Height = 40;
            caret.Padding = new Thickness(0);
            caret.Margin = new Thickness(2, 0, 0, 0);
            caret.ToolTip = Loc.T("临时用另一个工具执行这条命令（只影响下一条）");
            caret.Click += delegate { OpenRunMenu(caret); };
            Grid.SetColumn(caret, 1);
            runHost.Children.Add(caret);

            Grid.SetColumn(runHost, 1);
            row.Children.Add(runHost);

            _stopButton = Ui.Btn(null, "IconActionButton", delegate { StopCurrent(); });
            _stopButton.Content = Icons.Create("stop", 17, "DangerBrush", 2.0);
            _stopButton.Width = 40;
            _stopButton.Height = 40;
            _stopButton.Margin = new Thickness(8, 0, 0, 0);
            _stopButton.Visibility = Visibility.Collapsed;
            _stopButton.ToolTip = Loc.T("停止当前命令（连同子进程一起结束）");
            Grid.SetColumn(_stopButton, 2);
            row.Children.Add(_stopButton);

            Button clearBtn = Ui.Btn(null, "IconActionButton", delegate { ClearOutput(); });
            clearBtn.Content = Icons.Create("trash", 17, "TextDimBrush", 2.0);
            clearBtn.Width = 40;
            clearBtn.Height = 40;
            clearBtn.Margin = new Thickness(8, 0, 0, 0);
            clearBtn.ToolTip = Loc.T("清空输出（Ctrl+L）");
            Grid.SetColumn(clearBtn, 3);
            row.Children.Add(clearBtn);

            _inputRowButtons.Add(RunButton);
            _inputRowButtons.Add(caret);
            _inputRowButtons.Add(_stopButton);
            _inputRowButtons.Add(clearBtn);

            g.Children.Add(row);
            Grid.SetRow(row, 0);

            // ---------- 提示行 ----------
            // 一行三列（都在同一条水平线上）：
            //   列 0（Auto，贴左）：定时 + 时间 · 循环 + 间隔 · 停止
            //   列 1（*  弹性）    ：当前命令的提示文字（「N 个候选」跟着它）
            //   列 2（Auto，贴右）：多命令 · 多进程
            // 用户要求：定时 / 循环「直接上移」到这一行的**左侧**，多任务控件留在右侧不动。
            //
            // 提示文字放中间（而不是塞进定时组前面）的理由：定时组的 x 位置就不会随提示文字长短
            // 左右跳；提示文字本身是次要信息，空间不够时先省略号截断，再由 OnHintRowSizeChanged
            // 依次收掉「N 个候选」、收窄时间 / 间隔框，保证两侧控件都不被挤出面板。
            Grid hintRow = new Grid();
            hintRow.Margin = new Thickness(4, 7, 4, 8);
            hintRow.ClipToBounds = true;
            hintRow.ColumnDefinitions.Add(new ColumnDefinition());
            hintRow.ColumnDefinitions[0].Width = GridLength.Auto;   // 定时 / 循环 / 停止（贴左）
            hintRow.ColumnDefinitions.Add(new ColumnDefinition());  // 提示文字（弹性）
            hintRow.ColumnDefinitions.Add(new ColumnDefinition());
            hintRow.ColumnDefinitions[2].Width = GridLength.Auto;   // 多命令 / 多进程（贴右）

            // 左：定时 / 循环 / 停止（见 Schedule.cs）
            _hintSchedControls = BuildScheduleControls();
            _hintSchedControls.HorizontalAlignment = HorizontalAlignment.Left;
            hintRow.Children.Add(_hintSchedControls);
            Grid.SetColumn(_hintSchedControls, 0);

            // 中：提示标题 + 候选数（标题可截断、候选数可整块收掉）
            Grid hintMiddle = new Grid();
            hintMiddle.ClipToBounds = true;
            hintMiddle.Margin = new Thickness(12, 0, 12, 0);
            hintMiddle.VerticalAlignment = VerticalAlignment.Center;
            hintMiddle.ColumnDefinitions.Add(new ColumnDefinition());
            hintMiddle.ColumnDefinitions.Add(new ColumnDefinition());
            hintMiddle.ColumnDefinitions[1].Width = GridLength.Auto;

            _hintTitle = Ui.Text("", 11.5, "TextDimBrush");
            _hintTitle.VerticalAlignment = VerticalAlignment.Center;
            _hintTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            hintMiddle.Children.Add(_hintTitle);

            _hintText = Ui.Text("", 11, "TextFaintBrush");
            _hintText.VerticalAlignment = VerticalAlignment.Center;
            _hintText.Margin = new Thickness(12, 0, 0, 0);
            _hintText.TextTrimming = TextTrimming.CharacterEllipsis;
            _hintText.MaxWidth = 120;
            Grid.SetColumn(_hintText, 1);
            hintMiddle.Children.Add(_hintText);

            hintRow.Children.Add(hintMiddle);
            Grid.SetColumn(hintMiddle, 1);

            // 右：多命令 + 多进程（位置不变，仍贴右）
            StackPanel hintRight = Ui.H();
            hintRight.VerticalAlignment = VerticalAlignment.Center;
            _hintTaskControls = hintRight;

            MultiToggle = Ui.Btn(Loc.T("多命令"), "GhostButton", delegate { SetMultiCommand(!UseMultiCommand); });
            MultiToggle.FontSize = 11.5;
            MultiToggle.Padding = new Thickness(11, 4, 11, 4);
            MultiToggle.VerticalAlignment = VerticalAlignment.Center;
            hintRight.Children.Add(MultiToggle);

            // 「多进程」入口 + 运行状态**合并成一个按钮**（见 Schedule.cs）：
            //   没有任务在跑 → 普通灰底「多进程」；有任务在跑 → 蓝底「多进程 · 正在运行」。
            // 样式、内边距、对齐方式都跟旁边的「多命令」一致。
            Button mpBtn = BuildMergedMultiProcButton();
            mpBtn.Margin = new Thickness(6, 0, 0, 0);
            hintRight.Children.Add(mpBtn);

            hintRow.Children.Add(hintRight);
            Grid.SetColumn(hintRight, 2);
            hintRow.SizeChanged += OnHintRowSizeChanged;

            g.Children.Add(hintRow);
            Grid.SetRow(hintRow, 1);


            // ---------- 提示下拉 ----------
            SuggestList = new ListBox();
            SuggestList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            SuggestList.ItemContainerStyle = TryStyle("FlatListBoxItem");
            SuggestList.MaxHeight = 330;
            SuggestList.MouseDoubleClick += delegate { ApplySuggestion(SuggestList.SelectedIndex, true); };
            SuggestList.PreviewMouseLeftButtonUp += delegate { ApplySuggestion(SuggestList.SelectedIndex, false); };

            SuggestHost = new StackPanel();

            // 提示下拉做成"窗口内浮层"而不是独立 Popup：
            // 独立 Popup 是另一个顶层窗口，会跟随焦点/捕获状态自行关闭，不如浮层稳定。
            SuggestPanel = new Border();
            SuggestPanel.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            SuggestPanel.BorderThickness = new Thickness(1);
            SuggestPanel.Padding = new Thickness(4);
            SuggestPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceSolidBrush");
            SuggestPanel.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            Ui.AddSoftShadow(SuggestPanel, 0.20, 4, 20);
            SuggestPanel.Child = SuggestList;
            SuggestPanel.VerticalAlignment = VerticalAlignment.Top;
            SuggestPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SuggestPanel.Margin = new Thickness(0, 4, 0, 0);
            SuggestPanel.MaxHeight = 348;
            SuggestPanel.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(SuggestPanel, 20);
            g.Children.Add(SuggestPanel);
            // 提示下拉是覆盖层，行号跟着控制台走（第 2 行现在就是控制台所在的 * 行）
            Grid.SetRow(SuggestPanel, 2);

            // ---------- 输出控制台 ----------
            Border console = new Border();
            console.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            console.BorderThickness = new Thickness(1);
            console.SetResourceReference(Border.BackgroundProperty, "ConsoleBgBrush");
            console.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            Grid consoleGrid = new Grid();
            consoleGrid.RowDefinitions.Add(new RowDefinition());                 // 0 输出（* 行：高度有界 → 内部滚动）
            consoleGrid.RowDefinitions.Add(new RowDefinition());                 // 1 stdin 输入行
            consoleGrid.RowDefinitions[1].Height = GridLength.Auto;
            consoleGrid.RowDefinitions.Add(new RowDefinition());                 // 2 固定在底部的帮助块
            consoleGrid.RowDefinitions[2].Height = GridLength.Auto;

            Output = new RichTextBox();
            Output.IsReadOnly = true;
            Output.IsReadOnlyCaretVisible = false;
            Output.BorderThickness = new Thickness(0);
            Output.Background = Brushes.Transparent;
            Output.Padding = new Thickness(14, 12, 6, 12);
            Output.FontFamily = Fonts.Mono;
            Output.FontSize = Settings.FontSize;
            Output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Output.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // 输出从输出区**顶部**开始往下流：不要让内容在框里垂直居中 / 贴底。
            Output.VerticalContentAlignment = VerticalAlignment.Top;
            Output.SetResourceReference(Control.ForegroundProperty, "ConsoleTextBrush");
            FlowDocument doc = new FlowDocument();
            doc.PagePadding = new Thickness(0);
            doc.LineHeight = Math.Max(15, Settings.FontSize * 1.42);
            doc.FontFamily = Fonts.Mono;
            doc.FontSize = Settings.FontSize;
            Output.Document = doc;
            Output.ContextMenu = BuildConsoleMenu();
            consoleGrid.Children.Add(Output);
            Grid.SetRow(Output, 0);

            // stdin 输入行
            Grid stdinRow = new Grid();
            stdinRow.Margin = new Thickness(8, 0, 8, 8);
            stdinRow.Visibility = Visibility.Collapsed;
            _stdinRow = stdinRow;
            stdinRow.ColumnDefinitions.Add(new ColumnDefinition());
            stdinRow.ColumnDefinitions.Add(new ColumnDefinition());
            stdinRow.ColumnDefinitions[1].Width = GridLength.Auto;
            _stdinBox = new TextBox();
            _stdinBox.FontFamily = Fonts.Mono;
            _stdinBox.FontSize = 12.5;
            _stdinBox.Height = 34;
            _stdinBox.PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    Exec.SendInput(_stdinBox.Text);
                    AppendLine(Loc.T("[输入] ") + _stdinBox.Text, "ConsoleEchoBrush", FontWeights.Normal);
                    _stdinBox.Text = "";
                    e.Handled = true;
                }
            };
            stdinRow.Children.Add(_stdinBox);
            Button sendBtn = Ui.Btn(Loc.T("发送"), "SoftButton", delegate
            {
                Exec.SendInput(_stdinBox.Text);
                AppendLine(Loc.T("[输入] ") + _stdinBox.Text, "ConsoleEchoBrush", FontWeights.Normal);
                _stdinBox.Text = "";
                _stdinBox.Focus();
            });
            sendBtn.Height = 34;
            sendBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(sendBtn, 1);
            stdinRow.Children.Add(sendBtn);
            consoleGrid.Children.Add(stdinRow);
            Grid.SetRow(stdinRow, 1);

            // ---------- 帮助块：钉在控制面板底部，不随输出滚动 ----------
            // 原来这三行是往输出流里 AppendLine 的欢迎语，长输出一滚就看不见了。
            // 现在它是 * 行之外的独立 Auto 行：输出只占上面那块滚动区，
            // 帮助块永远贴着控制面板的下沿；设置里关掉时整块 Collapsed、一点高度都不占。
            _consoleHelp = new Border();
            _consoleHelp.BorderThickness = new Thickness(0, 1, 0, 0);
            _consoleHelp.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            _consoleHelp.Padding = new Thickness(14, 7, 14, 8);
            StackPanel helpText = Ui.V();
            TextBlock help1 = Ui.Text(Loc.T("WindowsCommandTools  ·  用可视化面板代替记忆命令语法"),
                11, "ConsoleSysBrush", FontWeights.SemiBold);
            TextBlock help2 = Ui.Text(Loc.T("命令库 ") + Lib.Count + Loc.T(" 条  ·  输入命令后按空格会自动提示下一个参数"),
                11, "ConsoleEchoBrush");
            TextBlock help3 = Ui.Text(Loc.T("Tab 补全  ·  ↑↓ 选择候选  ·  Enter 执行  ·  Ctrl+L 清屏  ·  F1 帮助"),
                11, "ConsoleEchoBrush");
            help1.TextTrimming = TextTrimming.CharacterEllipsis;
            help2.TextTrimming = TextTrimming.CharacterEllipsis;
            help3.TextTrimming = TextTrimming.CharacterEllipsis;
            help2.Margin = new Thickness(0, 2, 0, 0);
            help3.Margin = new Thickness(0, 2, 0, 0);
            helpText.Children.Add(help1);
            helpText.Children.Add(help2);
            helpText.Children.Add(help3);
            _consoleHelp.Child = helpText;
            consoleGrid.Children.Add(_consoleHelp);
            Grid.SetRow(_consoleHelp, 2);
            RebuildConsoleHelp();

            console.Child = consoleGrid;

            // 控制台上方压一条「这是另一个工具的命令」提示条 + 定时 / 循环的状态小字
            Grid consoleWrap = new Grid();
            consoleWrap.RowDefinitions.Add(new RowDefinition());
            consoleWrap.RowDefinitions[0].Height = GridLength.Auto;
            consoleWrap.RowDefinitions.Add(new RowDefinition());
            consoleWrap.RowDefinitions[1].Height = GridLength.Auto;
            consoleWrap.RowDefinitions.Add(new RowDefinition());
            consoleWrap.Children.Add(BuildCrossShellBanner());
            Grid.SetRow(CrossShellBanner, 0);

            // 定时 / 循环的状态小字（「每 5s 执行中 · 第 3 次」「已排定 12:30:00 执行」）：
            // 提示行已经排满一整排控件，状态文字比控件还长（英文尤其明显），
            // 放这里最省地方 —— 没排定时整条 Collapsed 不占高度；有排定时它贴在输出区上沿，
            // 用户盯着输出看的时候一定看得见（放输出区底部会被新输出挤走）。
            _schedStatus = Ui.Text("", 11, "TextFaintBrush");
            _schedStatus.VerticalAlignment = VerticalAlignment.Center;
            _schedStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            _schedStatus.Margin = new Thickness(14, 0, 14, 6);
            _schedStatus.Visibility = Visibility.Collapsed;
            consoleWrap.Children.Add(_schedStatus);
            Grid.SetRow(_schedStatus, 1);

            consoleWrap.Children.Add(console);
            Grid.SetRow(console, 2);

            g.Children.Add(consoleWrap);
            Grid.SetRow(consoleWrap, 2);
            RefreshScheduleUi();

            // ---------- 欢迎信息 ----------
            AppendWelcome();

            _centerPanel.Content = g;
        }

        internal void SetSuggestVisible(bool visible)
        {
            SuggestVisible = visible;
            if (SuggestPanel != null)
                SuggestPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private Grid _stdinRow;
        private TextBox _stdinBox;

        /// <summary>控制台底部那条固定帮助块（不随输出滚动）。</summary>
        private Border _consoleHelp;
        /// <summary>提示行左侧那一组定时 / 循环 / 停止控件。</summary>
        private StackPanel _hintSchedControls;
        /// <summary>提示行右侧那一组多任务控件（多命令 / 多进程）。</summary>
        private StackPanel _hintTaskControls;
        /// <summary>「N 个候选」上一次可见时的宽度（收起来之后 DesiredSize 会归零，用这个记账）。</summary>
        private double _hintTextWidth;
        /// <summary>时间 / 间隔输入框是否已经收窄（窄窗口兜底用）。</summary>
        private bool _hintCompact;

        /// <summary>
        /// 按 Settings.ShowConsoleHelp 显隐控制台底部那条帮助。
        /// 设置窗口一勾就调、BuildCenter / RebuildUi 之后也会调，所以必须能重复安全调用；
        /// 关掉时整块 Collapsed（不占高度），输出区把空间吃满。
        /// </summary>
        internal void RebuildConsoleHelp()
        {
            if (_consoleHelp == null) return;
            _consoleHelp.Visibility = Settings.ShowConsoleHelp ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 提示行空间不够时的自适应（英文文案最长 —— Multi-process / Schedule / Stop，最先撞）：
        ///   1) 先收掉「N 个候选」这块次要文字（中间的提示标题还能用省略号截断）；
        ///   2) 再收窄时间 / 间隔输入框（74→56、48→36）;
        ///   3) 还不行就只剩标题被挤到 0，两侧控件由 hintRow.ClipToBounds 兜底。
        /// 每级都留 30~40px 回差，避免在阈值附近来回抖。
        /// </summary>
        private void OnHintRowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_hintText == null || _hintSchedControls == null || _hintTaskControls == null) return;
            if (_hintText.Visibility == Visibility.Visible) _hintTextWidth = _hintText.DesiredSize.Width;
            double w = e.NewSize.Width;
            double both = _hintSchedControls.DesiredSize.Width + _hintTaskControls.DesiredSize.Width + 24;

            // 1) 「N 个候选」：中间的提示标题至少留 96px
            double needCount = both + _hintTextWidth + 96;
            if (_hintText.Visibility == Visibility.Visible && w < needCount)
                _hintText.Visibility = Visibility.Collapsed;
            else if (_hintText.Visibility != Visibility.Visible && w > needCount + 30)
                _hintText.Visibility = Visibility.Visible;

            // 2) 时间 / 间隔框收窄：两侧控件 + 标题至少留 60px
            double needBoxes = both + 60;
            if (!_hintCompact && w < needBoxes)
            {
                _hintCompact = true;
                SetSchedBoxesCompact(true);
            }
            else if (_hintCompact && w > needBoxes + 40)
            {
                _hintCompact = false;
                SetSchedBoxesCompact(false);
            }
        }

        private ContextMenu BuildConsoleMenu()
        {
            ContextMenu m = new ContextMenu();
            m.Items.Add(MenuItem(Loc.T("复制选中"), delegate { if (!Output.Selection.IsEmpty) Output.Copy(); }));
            m.Items.Add(MenuItem(Loc.T("复制全部"), delegate { Output.SelectAll(); Output.Copy(); Output.Selection.Select(Output.Document.ContentStart, Output.Document.ContentStart); }));
            m.Items.Add(MenuItem(Loc.T("全选"), delegate { Output.SelectAll(); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem(Loc.T("保存输出到文件…"), delegate { SaveOutput(); }));
            m.Items.Add(MenuItem(Loc.T("清空输出"), delegate { ClearOutput(); }));
            return m;
        }

        private static MenuItem MenuItem(string header, RoutedEventHandler handler)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            if (handler != null) mi.Click += handler;
            return mi;
        }

        private void AppendWelcome()
        {
            // 欢迎 / 帮助文案已经搬到控制台底部那条固定帮助块（_consoleHelp）里，
            // 不再写进输出流 —— 输出区从第一行起就是命令反馈，首行贴着输出区顶部。
            // 这里只剩命令库加载过程中的警告需要让用户看到。
            foreach (string w in Lib.LoadWarnings) AppendLine(Loc.T("警告：") + w, "ConsoleErrBrush", FontWeights.Normal);
        }

        private void SaveOutput()
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Title = Loc.T("保存输出");
            dlg.Filter = Loc.T("文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");
            dlg.FileName = "cmd-output-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    TextRange range = new TextRange(Output.Document.ContentStart, Output.Document.ContentEnd);
                    File.WriteAllText(dlg.FileName, range.Text, new UTF8Encoding(true));
                    AppendSystem(Loc.T("输出已保存到 ") + dlg.FileName);
                }
                catch (Exception ex)
                {
                    AppendSystem(Loc.T("保存失败：") + ex.Message);
                }
            }
        }

        // ==================================================================
        //  提示下拉
        // ==================================================================

        private void UpdateSuggestions()
        {
            if (_inputPlaceholder != null)
                _inputPlaceholder.Visibility = string.IsNullOrEmpty(Input.Text) ? Visibility.Visible : Visibility.Collapsed;

            string line = EditorLine();
            int caret = EditorCaretInLine();
            SuggestResult r = Suggester.Compute(Lib, line, caret, SessionDirectory, EffectiveShell);
            _lastSuggest = r;

            if (_hintTitle != null)
            {
                _hintTitle.Text = r.HintTitle;
                _hintText.Text = line.Trim().Length == 0 ? "" : (r.Items.Count > 0 ? r.Items.Count + Loc.T(" 个候选") : Loc.T("无候选"));
            }

            if (!Settings.OutputEncoding.Equals("never"))
            {
                // 记录当前命令对应的 spec（用于右栏表单/详情）
                CmdSpec sp = r.Spec;
                if (sp != null && sp != _currentSpec && !_formDirty) SetCurrentSpec(sp, false);
            }

            SuggestList.Items.Clear();
            if (line.Trim().Length == 0 || r.Items.Count == 0)
            {
                SetSuggestVisible(false);
            }
            else
            {
                int n = 0;
                foreach (Suggestion s in r.Items)
                {
                    SuggestList.Items.Add(BuildSuggestionItem(s));
                    if (++n >= 60) break;
                }
                SetSuggestVisible(true);
                SuggestList.SelectedIndex = -1;
                Navigated = false;
            }
            UpdateRightPanelSuggest(r);
        }

        internal SuggestResult _lastSuggest;

        private UIElement BuildSuggestionItem(Suggestion s)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            Border badge = new Border();
            badge.MinWidth = 40;
            badge.Padding = new Thickness(7, 2, 7, 2);
            badge.Height = 19;
            badge.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 1, 0, 0);
            badge.SetResourceReference(Border.BackgroundProperty, KindBackground(s.Kind));
            TextBlock bt = Ui.Text(Loc.T(s.Badge), 10.5, KindForeground(s.Kind), FontWeights.SemiBold);
            bt.HorizontalAlignment = HorizontalAlignment.Center;
            bt.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = bt;
            g.Children.Add(badge);

            StackPanel text = Ui.V();
            text.Margin = new Thickness(10, 0, 0, 0);
            StackPanel head = Ui.H();
            TextBlock tok = Ui.Text(s.Display, 13, "TextBrush", FontWeights.SemiBold);
            tok.FontFamily = Fonts.Mono;
            head.Children.Add(tok);
            if (!string.IsNullOrEmpty(s.ValueHint))
            {
                TextBlock vh = Ui.Text("  " + s.ValueHint, 11.5, "AccentBrush");
                vh.FontFamily = Fonts.Mono;
                vh.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(vh);
            }
            if (s.Used)
            {
                TextBlock used = Ui.Text(Loc.T("  已使用"), 10.5, "TextFaintBrush");
                used.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(used);
            }
            text.Children.Add(head);
            if (!string.IsNullOrEmpty(s.Desc))
            {
                TextBlock d = Ui.Text(s.Desc, 11.5, "TextDimBrush");
                d.Margin = new Thickness(0, 2, 0, 0);
                d.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Children.Add(d);
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (s.Kind == SuggestKind.SubCommand)
            {
                UIElement chev = Icons.Create("chevronRight", 13, "TextFaintBrush", 1.6);
                chev.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                Grid.SetColumn(chev as UIElement, 2);
                g.Children.Add(chev);
            }

            return g;
        }

        private static string KindBackground(SuggestKind k)
        {
            switch (k)
            {
                case SuggestKind.Command: return "AccentSoftBrush";
                case SuggestKind.SubCommand: return "AccentGhostBrush";
                case SuggestKind.PathDir: return "SuccessSoftBrush";
                case SuggestKind.PathFile: return "HoverOverlayBrush";
                case SuggestKind.EnumValue: return "WarningSoftBrush";
                case SuggestKind.Syntax: return "HoverOverlayBrush";
                default: return "HoverOverlayBrush";
            }
        }

        private static string KindForeground(SuggestKind k)
        {
            switch (k)
            {
                case SuggestKind.Command: return "AccentBrush";
                case SuggestKind.SubCommand: return "AccentBrush";
                case SuggestKind.PathDir: return "SuccessBrush";
                case SuggestKind.EnumValue: return "WarningBrush";
                case SuggestKind.Syntax: return "TextDimBrush";
                default: return "TextDimBrush";
            }
        }

        private static SuggestKind NodeSuggestKind(ParamNode n)
        {
            if (n.IsSubCommand) return SuggestKind.SubCommand;
            if (n.IsOption) return SuggestKind.Option;
            if (n.IsValue) return SuggestKind.Value;
            if (n.IsSyntax) return SuggestKind.Syntax;
            return SuggestKind.Flag;
        }

        private static string NodeKindLabel(ParamNode n)
        {
            if (n.IsSubCommand) return Loc.T("子命令");
            if (n.IsOption) return Loc.T("参数");
            if (n.IsValue) return Loc.T("值");
            if (n.IsSyntax) return Loc.T("语法");
            return Loc.T("开关");
        }

        private void ApplySuggestion(int index, bool execute)
        {
            if (index < 0 || _lastSuggest == null) return;
            if (index >= _lastSuggest.Items.Count) return;
            Suggestion s = _lastSuggest.Items[index];
            if (s.Kind == SuggestKind.Hint) return;

            // 跨工具候选（比如在 PowerShell 模式下敲 ping，候选写作「(切换到 CMD) ping」）：
            // 点它要**真的切到那个工具**，再把这行命令留下；双击则切过去直接执行。
            // 修复前这里只做 ApplyInsert，于是"切换工具"只活在文案里，命令还是插在当前模式
            // 里执行 —— 用户报的就是这个。
            if (s.SwitchShell)
            {
                ApplyCrossShellSuggestion(s, execute);
                return;
            }

            string newLine;
            int newCaret;
            TextBox ed = ActiveEditor;
            string word = Suggester.CurrentWord(ed.Text, ed.CaretIndex);
            bool replaceWord = Suggester.AnyStartsWith(_lastSuggest.Items, word);
            Suggester.ApplyInsert(EditorLine(), EditorCaretInLine(), s.Insert, replaceWord, out newLine, out newCaret);
            ReplaceEditorLine(newLine, newCaret);
            ed.Focus();
            Navigated = false;
            UpdateSuggestions();
            if (execute) ExecuteCurrent();
        }

        /// <summary>
        /// 点了「(切换到 X) 命令」这类候选。
        ///
        /// 单击：切到目标工具，命令留在输入框里等用户确认（回车执行）。
        /// 双击：切过去并立刻执行 —— 和候选卡片上写的「点它会切换工具并重新执行」一致。
        ///
        /// 走的是 SetShell，也就是标题栏那个开关同一条路径，所以标题栏、状态栏、
        /// 命令库、参数提示会一起跟着变。
        /// </summary>
        private void ApplyCrossShellSuggestion(Suggestion s, bool execute)
        {
            TextBox ed = ActiveEditor;
            if (ed == null) return;

            // 1) 先把命令写进输入框。整词替换：用户敲的可能是半截的 "pin"。
            string newLine;
            int newCaret;
            Suggester.ApplyInsert(EditorLine(), EditorCaretInLine(), s.Insert, true,
                out newLine, out newCaret);
            ReplaceEditorLine(newLine, newCaret);

            // 2) 真正切换工具（save=false：标题栏开关的语义就是"只影响本次运行"）
            // SetShell 内部已经写过一行「已切换到 X 模式（本次运行）」，这里不再重复输出
            SetShell(s.SwitchToShell, false);

            // 3) 切完重新算提示 —— 现在这行命令在新工具下是合法的，会显示它的参数
            Navigated = false;
            ed = ActiveEditor;
            if (ed != null)
            {
                ed.Focus();
                ed.CaretIndex = Math.Min(newCaret, ed.Text.Length);
            }
            UpdateSuggestions();

            // 4) 双击 = 切过去就跑
            if (execute) ExecuteCurrent();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (e.Key == Key.Down || (ctrl && e.Key == Key.N))
            {
                if (SuggestList.Items.Count > 0)
                {
                    SetSuggestVisible(true);
                    int i = SuggestList.SelectedIndex + 1;
                    if (i >= SuggestList.Items.Count) i = 0;
                    SuggestList.SelectedIndex = i;
                    SuggestList.ScrollIntoView(SuggestList.Items[i]);
                    Navigated = true;
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Up || (ctrl && e.Key == Key.P))
            {
                if (SuggestList.Items.Count > 0 && SuggestVisible)
                {
                    int i = SuggestList.SelectedIndex - 1;
                    if (i < 0) i = SuggestList.Items.Count - 1;
                    SuggestList.SelectedIndex = i;
                    SuggestList.ScrollIntoView(SuggestList.Items[i]);
                    Navigated = true;
                    e.Handled = true;
                    return;
                }
                else if (string.IsNullOrEmpty(Input.Text))
                {
                    // 空输入时 ↑ 调出上一条历史
                    List<HistoryEntry> h = History.Recent(1, "");
                    if (h.Count > 0) { Input.Text = h[0].Command; Input.CaretIndex = Input.Text.Length; e.Handled = true; }
                    return;
                }
            }
            if (e.Key == Key.Tab)
            {
                if (SuggestList.Items.Count > 0)
                {
                    int idx = SuggestList.SelectedIndex >= 0 ? SuggestList.SelectedIndex : 0;
                    ApplySuggestion(idx, false);
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (SuggestVisible) { SetSuggestVisible(false); Navigated = false; }
                else { Input.Text = ""; }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (SuggestVisible && Navigated && SuggestList.SelectedIndex >= 0)
                {
                    ApplySuggestion(SuggestList.SelectedIndex, false);
                    e.Handled = true;
                    return;
                }
                ExecuteCurrent();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.Space)
            {
                UpdateSuggestions();
                if (SuggestList.Items.Count > 0) SetSuggestVisible(true);
                e.Handled = true;
            }
        }

        // ==================================================================
        //  执行
        // ==================================================================

        internal void ExecuteCurrent()
        {
            // 定时 / 循环开着时，"运行"只是把命令交给调度器，所以当前有命令在跑也照收
            if (Exec.IsRunning && !ScheduleArmed) return;
            TextBox ed = ActiveEditor;
            string text = ed.Text == null ? "" : ed.Text.Trim();
            if (text.Length == 0) return;
            ed.Text = "";
            SetSuggestVisible(false);
            RunCommand(text);
        }

        internal void StopCurrent()
        {
            if (!Exec.IsRunning) return;
            Exec.Cancel();
            AppendSystem(Loc.T("已发送停止信号，正在结束进程树…"));
        }

        /// <summary>运行按钮右侧的下拉：临时用另一个工具执行 / 全局切换。</summary>
        private void OpenRunMenu(Button anchor)
        {
            ContextMenu m = new ContextMenu();
            m.PlacementTarget = anchor;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

            MenuItem onceCmd = new MenuItem();
            onceCmd.Header = Loc.T("本次用 CMD 执行");
            onceCmd.InputGestureText = "cmd>";
            onceCmd.IsChecked = TempShell == ShellKind.Cmd;
            onceCmd.Click += delegate { SetTempShell(ShellKind.Cmd); };
            m.Items.Add(onceCmd);

            MenuItem oncePs = new MenuItem();
            oncePs.Header = Loc.T("本次用 PowerShell 执行");
            oncePs.InputGestureText = "ps>";
            oncePs.IsChecked = TempShell == ShellKind.PowerShell;
            oncePs.Click += delegate { SetTempShell(ShellKind.PowerShell); };
            m.Items.Add(oncePs);

            if (TempShell.HasValue)
            {
                MenuItem cancel = new MenuItem();
                cancel.Header = Loc.T("取消临时切换");
                cancel.Click += delegate { SetTempShell(null); };
                m.Items.Add(cancel);
            }

            m.Items.Add(new Separator());

            MenuItem alwaysCmd = new MenuItem();
            alwaysCmd.Header = Loc.T("本次运行始终用 CMD");
            alwaysCmd.InputGestureText = "Ctrl+1";
            alwaysCmd.IsChecked = Shell == ShellKind.Cmd;
            alwaysCmd.Click += delegate { SetShell(ShellKind.Cmd, true); };
            m.Items.Add(alwaysCmd);

            MenuItem alwaysPs = new MenuItem();
            alwaysPs.Header = Loc.T("本次运行始终用 PowerShell");
            alwaysPs.InputGestureText = "Ctrl+2";
            alwaysPs.IsChecked = Shell == ShellKind.PowerShell;
            alwaysPs.Click += delegate { SetShell(ShellKind.PowerShell, true); };
            m.Items.Add(alwaysPs);

            m.Items.Add(new Separator());

            MenuItem swap = new MenuItem();
            swap.Header = Loc.T("交换：") + Shells.Display(Shell) + " → " + Shells.Display(Shells.Other(Shell));
            swap.Click += delegate { SetShell(Shells.Other(Shell), true); };
            m.Items.Add(swap);

            MenuItem edit = new MenuItem();
            edit.Header = Loc.T("在设置里修改「启动时的默认工具」…");
            edit.Click += delegate { OpenSettings(); };
            m.Items.Add(edit);

            m.IsOpen = true;
        }

        internal void RunCommand(string text)
        {
            // 定时 / 循环开着：这一次「运行」不立即执行，交给调度器（见 Schedule.cs）。
            // 调度器自己触发的那一轮带 _schedFiring 标记，走下面的正常执行路径。
            if (!_schedFiring && ScheduleArmed)
            {
                ArmSchedule(text);
                return;
            }

            if (Exec.IsRunning)
            {
                AppendSystem(Loc.T("已有命令正在运行，请先停止或等待完成。"));
                return;
            }

            // 1. 命令行前缀可以临时指定用哪个工具：cmd> ipconfig / ps> Get-Process
            //    多行模式下逐行处理（整段交给同一个 shell，所以只认第一个出现的指定）
            ShellKind runShell = EffectiveShell;
            string cmd = StripShellPrefixMulti(text, ref runShell);
            if (cmd.Length == 0) return;
            bool usedTemp = TempShell.HasValue || runShell != Shell;

            // 2. 内建处理（cd / cls / help …）
            string builtin = HandleBuiltin(cmd, runShell);
            if (builtin == "handled")
            {
                if (TempShell.HasValue) SetTempShell(null);
                return;
            }

            // 3. 跨工具预判：这条命令只属于另一个工具，先提醒（但仍然允许执行）
            CmdSpec spec = Lib.Find(runShell, FirstWord(cmd));
            if (spec == null && Settings.CrossShellHint && !usedTemp)
            {
                string otherName = FindCrossShellWord(runShell, cmd);
                CmdSpec other = otherName.Length > 0 ? Lib.FindInOther(runShell, otherName) : null;
                if (other != null)
                {
                    _crossShellCommand = cmd;
                    ShowCrossShellBanner(other, cmd, Loc.T("这条命令在 ") + Shells.Display(runShell) + Loc.T(" 里不存在。"));
                    AppendLine(Loc.T("提示：") + otherName + Loc.T(" 是 ") + Shells.Display(other.Shell)
                        + Loc.T(" 命令，当前是 ") + Shells.Display(runShell) + Loc.T(" 模式，执行大概率会失败。"),
                        "ConsoleSysBrush", FontWeights.Normal);
                }
            }

            int danger = spec != null ? spec.Danger : 0;
            string reason = spec != null ? spec.Title : "";
            int pattern = DangerPattern(cmd);
            if (pattern > danger) { danger = pattern; reason = Loc.T("命令行包含高风险操作"); }

            if (danger >= 2 || (danger == 1 && Settings.ConfirmLevel1))
            {
                if (Settings.ConfirmDanger)
                {
                    bool ok = ConfirmDialog.Show(this, danger >= 2 ? Loc.T("高危操作确认") : Loc.T("改动确认"),
                        danger >= 2
                            ? Loc.T("这个操作可能不可逆，会修改或删除磁盘 / 系统数据。请确认命令内容无误。")
                            : Loc.T("这个操作会修改系统或文件，请确认命令内容无误。"),
                        cmd, danger);
                    if (!ok)
                    {
                        AppendSystem(Loc.T("已取消：") + cmd);
                        return;
                    }
                }
            }

            if (spec != null && spec.Admin && !IsAdmin)
            {
                bool go = ConfirmDialog.Show(this, Loc.T("需要管理员权限"),
                    Loc.T("这条命令通常需要管理员权限，以当前普通权限运行可能会失败。\n可以先用管理员身份重启工具箱，再执行这条命令。"),
                    cmd, 1);
                if (go)
                {
                    RestartElevated(cmd);
                    return;
                }
            }

            History.Add(cmd, SessionDirectory, 0, Settings.MaxHistory);
            LastCommand = cmd;
            RunShellKind = runShell;
            _crossShellCommand = cmd;
            RefreshCommandList();

            AppendEcho(SessionDirectory + "  " + Shells.Prompt(runShell) + " " + cmd.Replace("\n", "\n    ")
                + (usedTemp ? Loc.T("     [临时使用 ") + Shells.Display(runShell) + "]" : ""));
            SetRunState(Loc.T("运行中…"), true);
            _runErrText.Length = 0;
            HideCrossShellBanner();
            try
            {
                Encoding forced = CommandExecutor.ResolveEncoding(Settings.OutputEncoding);
                Exec.Start(cmd, SessionDirectory, forced, runShell, PreferPwsh);
                if (_stdinRow != null) _stdinRow.Visibility = Visibility.Visible;
                // 多命令模式：没开持久化就只生效这一次
                AfterMultiCommandRun();
            }
            catch (Exception ex)
            {
                AppendLine(Loc.T("启动失败：") + ex.Message, "ConsoleErrBrush", FontWeights.Normal);
                SetRunState(Loc.T("启动失败"), false);
            }
            // 临时切换只影响这一条
            if (TempShell.HasValue) SetTempShell(null);
        }

        /// <summary>多行文本逐行剥掉 cmd&gt; / ps&gt; 前缀（只认第一个出现的指定）。</summary>
        internal static string StripShellPrefixMulti(string text, ref ShellKind shell)
        {
            if (text == null) return "";
            if (text.IndexOf('\n') < 0) return StripShellPrefix(text, ref shell);

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            ShellKind chosen = shell;
            bool specified = false;
            for (int i = 0; i < lines.Length; i++)
            {
                ShellKind trial = shell;
                string stripped = StripShellPrefix(lines[i], ref trial);
                if (trial != shell)
                {
                    if (!specified) { chosen = trial; specified = true; }
                    lines[i] = stripped;
                }
            }
            if (specified) shell = chosen;
            return string.Join("\n", lines).Trim();
        }

        /// <summary>在多行文本里逐行找第一个「属于另一个 shell」的命令名；找不到返回空串。</summary>
        internal string FindCrossShellWord(ShellKind shell, string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("#")) continue;
                string w = FirstWord(line);
                if (w.Length == 0) continue;
                if (Lib.Find(shell, w) != null) continue;
                if (Lib.FindInOther(shell, w) != null) return w;
            }
            return "";
        }

        /// <summary>本条命令实际使用的 shell，执行结束后用于判断跨工具提示。</summary>
        internal ShellKind RunShellKind = ShellKind.PowerShell;

        /// <summary>识别并去掉 cmd&gt; / ps&gt; 前缀；没有前缀时 shell 保持不变。</summary>
        internal static string StripShellPrefix(string text, ref ShellKind shell)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.TrimStart();
            string[] cmdPrefix = new string[] { "cmd>", "cmd:", "cmd>" };
            string[] psPrefix = new string[] { "ps>", "ps:", "pwsh>" };
            foreach (string p in psPrefix)
            {
                if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    shell = ShellKind.PowerShell;
                    return t.Substring(p.Length).TrimStart();
                }
            }
            foreach (string p in cmdPrefix)
            {
                if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    shell = ShellKind.Cmd;
                    return t.Substring(p.Length).TrimStart();
                }
            }
            return t;
        }

        private static string FirstWord(string text)
        {
            if (text == null) return "";
            text = text.TrimStart();
            int i = 0;
            while (i < text.Length && text[i] != ' ' && text[i] != '\t') i++;
            return text.Substring(0, i);
        }

        /// <summary>工具箱自己的内建命令（不交给 cmd/PowerShell 执行），返回 handled / none。</summary>
        private string HandleBuiltin(string text, ShellKind shell)
        {
            string t = text.Trim();
            string lower = t.ToLowerInvariant();

            if (lower == "cls" || lower == "clear" || lower == "clear-host")
            {
                ClearOutput();
                return "handled";
            }
            if (lower == "exit" || lower == "quit")
            {
                Close();
                return "handled";
            }
            if (lower == "help" || lower == "?")
            {
                ShowHelp();
                return "handled";
            }
            if (lower == "theme" || lower == Loc.T("主题"))
            {
                OpenThemeWindow();
                return "handled";
            }
            if (lower == "settings" || lower == "option" || lower == Loc.T("设置") || lower == Loc.T("选项"))
            {
                OpenSettings();
                return "handled";
            }
            if (shell == ShellKind.PowerShell)
            {
                if (TryChangeDirectoryPs(t)) return "handled";
                return "none";
            }
            if (lower.StartsWith("cd") || lower.StartsWith("chdir") || lower.StartsWith("pushd") || lower.StartsWith("popd"))
            {
                if (t.IndexOf('&') >= 0 || t.IndexOf('|') >= 0 || t.IndexOf('>') >= 0 || t.IndexOf('<') >= 0)
                    return "none";
                if (TryChangeDirectory(t)) return "handled";
            }
            return "none";
        }

        /// <summary>PowerShell 的切目录命令，同样由工具箱自己维护会话目录。</summary>
        private bool TryChangeDirectoryPs(string line)
        {
            List<Token> tokens = Lexer.Tokenize(line);
            if (tokens.Count == 0) return false;
            string cmd = tokens[0].Bare.ToLowerInvariant();
            bool push = cmd == "push-location" || cmd == "pushd";
            bool pop = cmd == "pop-location" || cmd == "popd";
            if (!push && !pop && cmd != "cd" && cmd != "chdir" && cmd != "set-location" && cmd != "sl") return false;
            if (line.IndexOf('|') >= 0 || line.IndexOf(';') >= 0) return false;
            return ChangeDirectory(tokens, push, pop);
        }

        private readonly Stack<string> _dirStack = new Stack<string>();

        private bool TryChangeDirectory(string line)
        {
            List<Token> tokens = Lexer.Tokenize(line);
            if (tokens.Count == 0) return false;
            string cmd = tokens[0].Bare.ToLowerInvariant();
            if (cmd != "cd" && cmd != "chdir" && cmd != "pushd" && cmd != "popd") return false;
            return ChangeDirectory(tokens, cmd == "pushd", cmd == "popd");
        }

        private bool ChangeDirectory(List<Token> tokens, bool push, bool pop)
        {
            if (pop)
            {
                if (_dirStack.Count == 0) { AppendLine(Loc.T("目录堆栈为空。"), "ConsoleErrBrush", FontWeights.Normal); return true; }
                string prev = _dirStack.Pop();
                if (Directory.Exists(prev)) SessionDirectory = prev;
                AppendLine(SessionDirectory, "ConsoleSysBrush", FontWeights.Normal);
                UpdateStatus();
                return true;
            }

            string target = null;
            for (int i = 1; i < tokens.Count; i++)
            {
                string a = tokens[i].Bare;
                // cmd 的 /d 与 PowerShell 的 -Path / -LiteralPath 都跳过
                if (string.Equals(a, "/d", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(a, "-Path", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(a, "-LiteralPath", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(a, "-PSProvider", StringComparison.OrdinalIgnoreCase)) continue;
                target = a;
                break;
            }

            if (target == null)
            {
                AppendLine(SessionDirectory, "ConsoleSysBrush", FontWeights.Normal);
                return true;
            }

            // PowerShell 支持 ~ 表示用户主目录
            if (target == "~") target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            else if (target.StartsWith("~\\") || target.StartsWith("~/"))
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target.Substring(2));

            if (push) _dirStack.Push(SessionDirectory);

            string candidate;
            try
            {
                candidate = Path.IsPathRooted(target) ? target : Path.Combine(SessionDirectory, target);
                candidate = Path.GetFullPath(candidate);
            }
            catch
            {
                AppendLine(Loc.T("路径格式不正确：") + target, "ConsoleErrBrush", FontWeights.Normal);
                return true;
            }

            if (Directory.Exists(candidate))
            {
                SessionDirectory = candidate;
                Settings.LastDirectory = candidate;
                AppendLine(SessionDirectory, "ConsoleSysBrush", FontWeights.Normal);
                UpdateStatus();
            }
            else
            {
                AppendLine(EffectiveShell == ShellKind.PowerShell
                    ? Loc.T("找不到路径：") + target
                    : Loc.T("系统找不到指定的路径。"), "ConsoleErrBrush", FontWeights.Normal);
            }
            return true;
        }

        /// <summary>不依赖命令库的高危模式识别（CMD 与 PowerShell 两套写法都覆盖）。</summary>
        internal static int DangerPattern(string text)
        {
            string t = " " + text.ToLowerInvariant() + " ";
            string[] level2 = new string[] {
                // ---- CMD ----
                " format ", "format c:", "format d:", "diskpart", " rd /s", "rmdir /s", "del /f", "del /q",
                "del /s", "erase /f", "shutdown", "bcdedit", "cipher /w", "reg delete", "sc delete",
                " vssadmin delete", " wbadmin delete", " fsutil ", "chkdsk /f", "chkdsk /r", " convert ",
                "takeown", "reagentc", "sysprep", " attrib -", "rmdir /q",
                // ---- PowerShell ----
                "clear-disk", "format-volume", "initialize-disk", "remove-partition", "reset-disk",
                "remove-storagepool", "remove-virtualdisk", "clear-content -force", "stop-computer",
                "restart-computer", "remove-localuser", "unregister-scheduledtask", "unregister-pssessionconfiguration",
                "remove-item -recurse", "remove-item -force", "rm -recurse", "remove-itemproperty",
                "set-executionpolicy unrestricted", "set-executionpolicy bypass", "disable-computerrestore",
                "remove-windowsfeature", "disable-windowsoptionalfeature", "clear-recyclebin", "remove-aduser"
            };
            foreach (string p in level2)
            {
                if (t.IndexOf(p) >= 0) return 2;
            }
            string[] level1 = new string[] {
                // ---- CMD ----
                " reg add", " sc config", " sc create", " net user", " net localgroup", " icacls ",
                " cacls ", " attrib ", " powercfg /hibernate off", " schtasks /delete", " schtasks /change",
                " netsh advfirewall", " route add", " route delete", " arp -s", " mklink ", " subst ",
                " compact /c", " dism ", " sfc ", " net share", " taskkill", " stop-service",
                // ---- PowerShell ----
                "new-itemproperty", "set-itemproperty", " new-localuser", "set-localuser", "add-localgroupmember",
                "remove-localgroupmember", " new-netfirewallrule", "set-netfirewallrule", "set-netfirewallprofile",
                "remove-netfirewallrule", " new-smbshare", "remove-smbshare", "register-scheduledtask",
                "set-scheduledtask", "stop-scheduledtask", " new-netipaddress", "set-netipaddress",
                "remove-netipaddress", " new-netroute", "remove-netroute", "set-dnsserveraddress",
                "set-dnsclientserversaddress", "invoke-expression", " iex ", "set-executionpolicy",
                "set-service", "restart-computer", "set-timezone", "new-pssession", "enable-psremoting",
                "register-pssessionconfiguration", "disable-localuser", "enable-localuser",
                "remove-item", "move-item", "rename-item", "set-acl", "stop-process", "remove-service",
                "new-service", "disable-netadapter", "restart-netadapter", "repair-volume", "resize-partition"
            };
            foreach (string p in level1)
            {
                if (t.IndexOf(p) >= 0) return 1;
            }
            return 0;
        }

        private void OnProcessFinished(ExecResult r)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_stdinRow != null) _stdinRow.Visibility = Visibility.Collapsed;
                FlushOutput();
                StringBuilder sb = new StringBuilder();
                if (r.Cancelled) sb.Append(Loc.T("已停止"));
                else sb.Append(Loc.T("完成"));
                sb.Append(Loc.T("  ·  退出码 ")).Append(r.ExitCode);
                sb.Append(Loc.T("  ·  耗时 ")).Append((r.ElapsedMs / 1000.0).ToString("0.00")).Append(Loc.T(" 秒"));
                AppendLine("", null, FontWeights.Normal);
                AppendLine(sb.ToString(), r.ExitCode == 0 ? "ConsoleOkBrush" : "ConsoleErrBrush", FontWeights.SemiBold);
                SetRunState(r.Cancelled ? Loc.T("已停止") : (r.ExitCode == 0 ? Loc.T("完成") : Loc.T("退出码 ") + r.ExitCode), false);
                if (History.Entries.Count > 0)
                {
                    History.Entries[History.Entries.Count - 1].ExitCode = r.ExitCode;
                    History.Save();
                }
                UpdateStatus();
                DetectCrossShellFailure(r);
                Input.Focus();
            }));
        }

        /// <summary>
        /// 跨工具识别：命令在 cmd / PowerShell 里报「不认识这个命令」时，
        /// 如果它恰好是另一个工具的命令，就弹出提示条让用户一键切过去重跑。
        /// </summary>
        private void DetectCrossShellFailure(ExecResult r)
        {
            if (!Settings.CrossShellHint) return;
            if (r.Cancelled) return;
            string err = _runErrText.ToString();
            if (err.Length == 0 && r.ExitCode == 0) return;
            if (!ShellErrors.IsUnknownCommandError(err)) return;

            string unknown = ShellErrors.ExtractUnknownName(err);
            if (unknown.Length == 0) unknown = FirstWord(_crossShellCommand);
            if (unknown.Length == 0) return;

            CmdSpec other = Lib.FindInOther(RunShellKind, unknown);
            if (other == null) return;

            _crossShellCommand = _crossShellCommand.Length > 0 ? _crossShellCommand : LastCommand;
            ShowCrossShellBanner(other, _crossShellCommand,
                Shells.Display(RunShellKind) + Loc.T(" 报告说它不认识 ") + unknown + "：");
            AppendLine(Loc.T("检测到这是 ") + Shells.Display(other.Shell) + Loc.T(" 命令 —— ") + unknown
                + Loc.T(" 属于 ") + Shells.Display(other.Shell) + "（" + other.Title + "）。"
                + Loc.T("可以点上方的按钮切换后重新执行。"), "ConsoleSysBrush", FontWeights.SemiBold);
        }

        internal readonly StringBuilder _runErrText = new StringBuilder();

        // ==================================================================
        //  右：参数提示 / 表单 / 详情
        // ==================================================================

        private void BuildRightPanel()
        {
            _rightPanel = new GlassPanel();
            Grid.SetColumn(_rightPanel, 4);
            BodyGrid.Children.Add(_rightPanel);

            Grid g = new Grid();
            g.Margin = new Thickness(8, 10, 10, 10);

            _rightTabs = new TabControl();

            TabItem t1 = new TabItem();
            t1.Header = Loc.T("参数提示");
            _suggestPanel = BuildSuggestTab();
            t1.Content = _suggestPanel;

            TabItem t2 = new TabItem();
            t2.Header = Loc.T("参数表单");
            _formHost = new StackPanel();
            ScrollViewer fsv = new ScrollViewer();
            fsv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            fsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            fsv.Content = _formHost;
            t2.Content = fsv;

            TabItem t3 = new TabItem();
            t3.Header = Loc.T("命令详情");
            _detailHost = new StackPanel();
            ScrollViewer dsv = new ScrollViewer();
            dsv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            dsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            dsv.Content = _detailHost;
            t3.Content = dsv;

            _rightTabs.Items.Add(t1);
            _rightTabs.Items.Add(t2);
            _rightTabs.Items.Add(t3);
            g.Children.Add(_rightTabs);

            _rightPanel.Content = g;
            ShowEmptyRight();
        }

        private StackPanel _suggestPanel;
        private StackPanel _detailHost;
        private TextBlock _crumbText;
        private ListBox _levelList;

        private StackPanel BuildSuggestTab()
        {
            StackPanel sp = Ui.V();

            _crumbText = Ui.Text("", 11.5, "AccentBrush", FontWeights.SemiBold);
            _crumbText.FontFamily = Fonts.Mono;
            _crumbText.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_crumbText);

            _paramHint = Ui.Wrap("", 11.5, "TextFaintBrush");
            _paramHint.Margin = new Thickness(0, 4, 0, 8);
            sp.Children.Add(_paramHint);

            _levelList = new ListBox();
            _levelList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _levelList.ItemContainerStyle = TryStyle("CardItem");
            _levelList.SelectionChanged += delegate
            {
                if (_levelList.SelectedIndex < 0) return;
                int i = _levelList.SelectedIndex;
                if (i < _levelParams.Count)
                {
                    ParamNode n = _levelParams[i];
                    if (n.IsSyntax || n.IsFlag || n.IsOption || n.IsValue || n.IsSubCommand)
                    {
                        string newLine; int newCaret;
                        string word = Suggester.CurrentWord(Input.Text, Input.CaretIndex);
                        bool replaceWord = _lastSuggest != null && Suggester.AnyStartsWith(_lastSuggest.Items, word);
                        Suggester.ApplyInsert(Input.Text, Input.CaretIndex, n.Token, replaceWord, out newLine, out newCaret);
                        Input.Text = newLine;
                        Input.CaretIndex = Math.Min(newCaret, newLine.Length);
                    }
                    Input.Focus();
                    _levelList.SelectedIndex = -1;
                    UpdateSuggestions();
                }
            };
            sp.Children.Add(_levelList);

            _suggestIntro = Ui.Wrap(Loc.T("在左侧选择一条命令，或在输入框里输入命令，这里会实时显示当前可用的参数。"), 11.5, "TextFaintBrush");
            _suggestIntro.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(_suggestIntro);

            return sp;
        }

        private TextBlock _paramHint;
        private TextBlock _suggestIntro;
        private readonly List<ParamNode> _levelParams = new List<ParamNode>();

        private void ShowEmptyRight()
        {
            if (_crumbText != null) _crumbText.Text = Loc.T("未选择命令");
            if (_paramHint != null) _paramHint.Text = Loc.T("输入命令后自动提示");
            if (_levelList != null) _levelList.Items.Clear();
            if (_detailHost != null)
            {
                _detailHost.Children.Clear();
                TextBlock t = Ui.Wrap(Loc.T("选择左侧命令库中的命令，或直接在输入框里输入命令，这里会显示用途、语法和真实示例。"),
                    12, "TextFaintBrush");
                t.Margin = new Thickness(0, 6, 0, 0);
                _detailHost.Children.Add(t);
            }
            if (_formHost != null)
            {
                _formHost.Children.Clear();
                TextBlock t = Ui.Wrap(Loc.T("选中一条命令后，这里会生成可填写的参数表单，填完即可一键执行，不用记参数写法。"),
                    12, "TextFaintBrush");
                t.Margin = new Thickness(0, 6, 0, 0);
                _formHost.Children.Add(t);
            }
        }

        private void UpdateRightPanelSuggest(SuggestResult r)
        {
            if (_crumbText == null) return;
            _crumbText.Text = string.IsNullOrEmpty(r.TrailText) ? Loc.T("未选择命令") : r.TrailText;
            _paramHint.Text = r.HintTitle;

            _levelList.Items.Clear();
            _levelParams.Clear();
            List<ParamNode> level = r.CurrentLevel;
            if (level != null)
            {
                int n = 0;
                foreach (ParamNode node in level)
                {
                    _levelList.Items.Add(BuildParamRow(node));
                    _levelParams.Add(node);
                    if (++n >= 80) break;
                }
            }
            if (_suggestIntro != null)
                _suggestIntro.Text = level != null && level.Count > 0
                    ? Loc.T("点击任意参数即可填入命令行。")
                    : Loc.T("在左侧选择一条命令，或在输入框里输入命令，这里会实时显示当前可用的参数。");
        }

        private UIElement BuildParamRow(ParamNode node)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());

            Border badge = new Border();
            badge.MinWidth = 40; badge.Height = 19;
            badge.Padding = new Thickness(7, 0, 7, 0);
            badge.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 1, 0, 0);
            SuggestKind k = NodeSuggestKind(node);
            badge.SetResourceReference(Border.BackgroundProperty, KindBackground(k));
            TextBlock bt = Ui.Text(NodeKindLabel(node), 10.5, KindForeground(k), FontWeights.SemiBold);
            bt.HorizontalAlignment = HorizontalAlignment.Center;
            bt.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = bt;
            g.Children.Add(badge);

            StackPanel text = Ui.V();
            text.Margin = new Thickness(9, 0, 0, 0);
            StackPanel head = Ui.H();
            TextBlock tok = Ui.Text(node.Token, 12.5, "TextBrush", FontWeights.SemiBold);
            tok.FontFamily = Fonts.Mono;
            head.Children.Add(tok);
            if (!string.IsNullOrEmpty(node.ValueHint))
            {
                TextBlock vh = Ui.Text("  " + node.ValueHint, 11, "AccentBrush");
                vh.FontFamily = Fonts.Mono;
                vh.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(vh);
            }
            text.Children.Add(head);
            TextBlock d = Ui.Wrap(node.Desc + (node.Enum.Count > 0 ? Loc.T("（取值：") + string.Join(" / ", node.Enum.ToArray()) + "）" : ""),
                11.5, "TextDimBrush");
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            return g;
        }

        // ==================================================================
        //  当前命令 → 表单 / 详情
        // ==================================================================

        internal void SetCurrentSpec(CmdSpec s, bool fromList)
        {
            _currentSpec = s;
            BuildForm(s);
            BuildDetails(s);
            if (fromList && s != null && !Exec.IsRunning)
            {
                string text = s.Name + " ";
                if (Input.Text == null || Input.Text.Trim().Length == 0 || fromList)
                {
                    Input.Text = text;
                    Input.CaretIndex = text.Length;
                    Input.Focus();
                }
                UpdateSuggestions();
            }
        }

        private void BuildForm(CmdSpec s)
        {
            if (_formHost == null) return;
            _formHost.Children.Clear();
            _formFields.Clear();
            _formEditors.Clear();
            _formDirty = false;

            if (s == null) return;

            StackPanel head = Ui.V();
            TextBlock title = Ui.Text(s.Name + "  " + s.Title, 13, "TextBrush", FontWeights.SemiBold);
            head.Children.Add(title);
            TextBlock usage = Ui.Wrap(s.Usage, 11.5, "AccentBrush");
            usage.FontFamily = Fonts.Mono;
            usage.Margin = new Thickness(0, 5, 0, 0);
            head.Children.Add(usage);
            head.Children.Add(Ui.HairLine());
            _formHost.Children.Add(head);

            if (s.Form.Count == 0)
            {
                TextBlock t = Ui.Wrap(Loc.T("这条命令还没有预置参数表单。可以直接在命令行里输入，输入过程中会自动提示可用参数。"),
                    12, "TextFaintBrush");
                t.Margin = new Thickness(0, 4, 0, 8);
                _formHost.Children.Add(t);
                _formHost.Children.Add(BuildPreviewBox(s, null));
                return;
            }

            foreach (FormField f in s.Form)
            {
                _formFields.Add(f);
                FrameworkElement editor = BuildFieldEditor(f);
                _formEditors.Add(editor);

                StackPanel block = Ui.V();
                block.Margin = new Thickness(0, 0, 0, 9);
                if (f.Type != "flag")
                {
                    StackPanel lab = Ui.H();
                    TextBlock lt = Ui.Text(f.Label, 12, "TextDimBrush");
                    lab.Children.Add(lt);
                    if (!string.IsNullOrEmpty(f.Token))
                    {
                        TextBlock tk = Ui.Text("  " + f.Token, 11, "TextFaintBrush");
                        tk.FontFamily = Fonts.Mono;
                        tk.VerticalAlignment = VerticalAlignment.Center;
                        lab.Children.Add(tk);
                    }
                    block.Children.Add(lab);
                }
                editor.Margin = new Thickness(0, f.Type == "flag" ? 0 : 5, 0, 0);
                block.Children.Add(editor);
                if (!string.IsNullOrEmpty(f.Hint))
                {
                    TextBlock h = Ui.Wrap(f.Hint, 11, "TextFaintBrush");
                    h.Margin = new Thickness(0, 4, 0, 0);
                    block.Children.Add(h);
                }
                _formHost.Children.Add(block);
            }

            _formHost.Children.Add(Ui.HairLine());
            _formHost.Children.Add(BuildPreviewBox(s, null));
        }

        private FrameworkElement BuildFieldEditor(FormField f)
        {
            if (f.Type == "flag")
            {
                CheckBox cb = new CheckBox();
                cb.Content = f.Label;
                cb.IsChecked = string.Equals(f.Value, "true", StringComparison.OrdinalIgnoreCase);
                cb.SetResourceReference(FrameworkElement.MarginProperty, "Tag");
                cb.Margin = new Thickness(0);
                cb.Checked += delegate { _formDirty = true; UpdateFormPreview(); };
                cb.Unchecked += delegate { _formDirty = true; UpdateFormPreview(); };
                return cb;
            }
            if (f.Type == "select")
            {
                ComboBox combo = new ComboBox();
                foreach (string o in f.Options) combo.Items.Add(o);
                if (combo.Items.Count > 0)
                {
                    int idx = f.Options.IndexOf(f.Value);
                    combo.SelectedIndex = idx >= 0 ? idx : 0;
                }
                combo.SelectionChanged += delegate { _formDirty = true; UpdateFormPreview(); };
                return combo;
            }
            if (f.Type == "path")
            {
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[1].Width = GridLength.Auto;
                TextBox tb = new TextBox();
                tb.Text = f.Value;
                tb.FontFamily = Fonts.Mono;
                tb.TextChanged += delegate { _formDirty = true; UpdateFormPreview(); };
                g.Children.Add(tb);
                Button browse = Ui.Btn(Loc.T("浏览…"), "GhostButton", null);
                browse.Height = 34;
                browse.Margin = new Thickness(6, 0, 0, 0);
                TextBox captured = tb;
                browse.Click += delegate
                {
                    Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                    dlg.Title = Loc.T("选择文件");
                    dlg.Filter = Loc.T("所有文件 (*.*)|*.*");
                    if (dlg.ShowDialog(this) == true) captured.Text = dlg.FileName;
                };
                Grid.SetColumn(browse, 1);
                g.Children.Add(browse);
                g.Tag = tb;
                return g;
            }

            TextBox box = new TextBox();
            box.Text = f.Value;
            box.FontFamily = Fonts.Mono;
            if (f.Type == "number")
            {
                box.PreviewTextInput += delegate (object s, TextCompositionEventArgs e)
                {
                    foreach (char c in e.Text)
                    {
                        if (!char.IsDigit(c) && c != '-' && c != '.') { e.Handled = true; return; }
                    }
                };
            }
            box.TextChanged += delegate { _formDirty = true; UpdateFormPreview(); };
            return box;
        }

        private Border BuildPreviewBox(CmdSpec s, string unused)
        {
            Border box = new Border();
            box.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            box.Padding = new Thickness(10);
            box.Margin = new Thickness(0, 8, 0, 0);
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            box.BorderThickness = new Thickness(1);

            StackPanel sp = Ui.V();
            sp.Children.Add(Ui.Text(Loc.T("将执行的命令"), 11, "TextFaintBrush"));
            _formPreview = new TextBox();
            _formPreview.IsReadOnly = true;
            _formPreview.FontFamily = Fonts.Mono;
            _formPreview.FontSize = 12;
            _formPreview.TextWrapping = TextWrapping.Wrap;
            _formPreview.MinHeight = 34;
            _formPreview.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(_formPreview);

            StackPanel btns = Ui.H();
            btns.Margin = new Thickness(0, 8, 0, 0);
            btns.Children.Add(Ui.Btn(Loc.T("填入命令行"), "GhostButton", delegate
            {
                FillInput(_formPreview.Text);
            }));
            Button run = Ui.Btn(Loc.T("立即执行"), "PrimaryButton", delegate
            {
                string cmd = _formPreview.Text;
                if (!string.IsNullOrEmpty(cmd)) { FillInput(cmd); ExecuteCurrent(); }
            });
            run.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(run);
            Button reset = Ui.Btn(Loc.T("重置"), "GhostButton", delegate
            {
                if (_currentSpec != null) { BuildForm(_currentSpec); }
            });
            reset.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(reset);
            sp.Children.Add(btns);

            box.Child = sp;
            if (s != null) UpdateFormPreview();
            return box;
        }

        private void UpdateFormPreview()
        {
            if (_formPreview == null || _currentSpec == null) return;
            _formPreview.Text = ComposeFormCommand(_currentSpec, _formFields, _formEditors);
        }

        private static string ComposeFormCommand(CmdSpec s, List<FormField> fields, List<FrameworkElement> editors)
        {
            if (s == null) return "";
            List<string> parts = new List<string>();
            foreach (string piece in SplitTokens(s.Name + " " + (s.Form.Count == 0 ? "" : ""))) parts.Add(piece);

            for (int i = 0; i < fields.Count && i < editors.Count; i++)
            {
                FormField f = fields[i];
                FrameworkElement ed = editors[i];
                string val = null;
                bool flagOn = false;

                CheckBox cb = ed as CheckBox;
                ComboBox combo = ed as ComboBox;
                TextBox tb = ed as TextBox;
                if (cb != null) { flagOn = cb.IsChecked == true; }
                else if (combo != null) { val = combo.SelectedItem as string; }
                else if (tb != null) { val = tb.Text; }
                else
                {
                    TextBox inner = ed.Tag as TextBox;
                    if (inner != null) val = inner.Text;
                }

                if (cb != null)
                {
                    if (flagOn && f.Token.Length > 0) parts.Add(f.Token);
                    continue;
                }
                if (val == null) continue;
                val = val.Trim();
                if (val.Length == 0) continue;
                if (f.Token.Length == 0) parts.Add(QuoteIfNeeded(val));
                else parts.Add(f.Token + " " + QuoteIfNeeded(val));
            }
            return string.Join(" ", parts.ToArray());
        }

        private static IEnumerable<string> SplitTokens(string text)
        {
            foreach (string p in text.Split(' '))
            {
                if (p.Length > 0) yield return p;
            }
        }

        private static string QuoteIfNeeded(string v)
        {
            if (v.IndexOf(' ') >= 0 && v.IndexOf('"') < 0) return "\"" + v + "\"";
            return v;
        }

        private void BuildDetails(CmdSpec s)
        {
            if (_detailHost == null) return;
            _detailHost.Children.Clear();
            if (s == null) return;

            StackPanel head = Ui.H();
            TextBlock nm = Ui.Text(s.Name, 16, "TextBrush", FontWeights.Bold);
            nm.FontFamily = Fonts.Mono;
            head.Children.Add(nm);
            if (s.Admin)
            {
                Border b = Badge(Loc.T("需要管理员"), "AccentSoftBrush", "AccentBrush");
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Margin = new Thickness(8, 0, 0, 0);
                head.Children.Add(b);
            }
            if (s.Danger >= 1)
            {
                Border b = Badge(s.Danger >= 2 ? Loc.T("高危") : Loc.T("会改动系统"), "DangerSoftBrush", "DangerBrush");
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Margin = new Thickness(6, 0, 0, 0);
                head.Children.Add(b);
            }
            _detailHost.Children.Add(head);

            TextBlock sub = Ui.Text(s.Title + "  ·  " + s.Category + Loc.T("  ·  来源 ") + s.Source, 11.5, "TextFaintBrush");
            sub.Margin = new Thickness(0, 3, 0, 0);
            _detailHost.Children.Add(sub);

            Border usageBox = new Border();
            usageBox.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            usageBox.Padding = new Thickness(10);
            usageBox.Margin = new Thickness(0, 10, 0, 10);
            usageBox.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            TextBlock usage = Ui.Wrap(s.Usage, 12, "TextBrush");
            usage.FontFamily = Fonts.Mono;
            usageBox.Child = usage;
            _detailHost.Children.Add(usageBox);

            TextBlock desc = Ui.Wrap(s.Desc, 12, "TextDimBrush");
            desc.LineHeight = 19;
            _detailHost.Children.Add(desc);

            if (s.Tags.Count > 0)
            {
                StackPanel tags = Ui.H();
                tags.Margin = new Thickness(0, 8, 0, 0);
                foreach (string tag in s.Tags)
                {
                    tags.Children.Add(Badge(tag, "HoverOverlayBrush", "TextDimBrush"));
                    Border last = tags.Children[tags.Children.Count - 1] as Border;
                    if (last != null) last.Margin = new Thickness(0, 0, 6, 0);
                }
                tags.Children.Add(new Border());
                _detailHost.Children.Add(tags);
            }

            if (s.Examples.Count > 0)
            {
                _detailHost.Children.Add(Ui.HairLine());
                _detailHost.Children.Add(Ui.Text(Loc.T("示例（点击填入命令行）"), 12, "TextBrush", FontWeights.SemiBold));
                foreach (CmdExample ex in s.Examples)
                {
                    Border row = new Border();
                    row.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
                    row.Padding = new Thickness(10);
                    row.Margin = new Thickness(0, 6, 0, 0);
                    row.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
                    row.Cursor = Cursors.Hand;
                    StackPanel sp = Ui.V();
                    TextBlock code = Ui.Wrap(ex.Cmd, 12, "AccentBrush");
                    code.FontFamily = Fonts.Mono;
                    sp.Children.Add(code);
                    sp.Children.Add(Ui.Wrap(ex.Desc, 11.5, "TextDimBrush"));
                    row.Child = sp;
                    string captured = ex.Cmd;
                    row.MouseLeftButtonUp += delegate { FillInput(captured); };
                    row.MouseEnter += delegate { row.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); };
                    row.MouseLeave += delegate { row.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft"); };
                    row.BorderThickness = new Thickness(1);
                    row.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
                    _detailHost.Children.Add(row);
                }
            }

            if (s.Nodes.Count > 0)
            {
                _detailHost.Children.Add(Ui.HairLine());
                _detailHost.Children.Add(Ui.Text(Loc.T("全部参数（") + s.NodeCount + Loc.T(" 项）"), 12, "TextBrush", FontWeights.SemiBold));
                AddNodeTree(_detailHost, s.Nodes, 0);
            }
        }

        private void AddNodeTree(StackPanel host, List<ParamNode> nodes, int depth)
        {
            foreach (ParamNode n in nodes)
            {
                StackPanel row = Ui.V();
                row.Margin = new Thickness(depth * 12, 4, 0, 0);
                StackPanel head = Ui.H();
                TextBlock tok = Ui.Text(n.Token, 11.5, "TextBrush", FontWeights.SemiBold);
                tok.FontFamily = Fonts.Mono;
                head.Children.Add(tok);
                if (!string.IsNullOrEmpty(n.ValueHint))
                {
                    TextBlock vh = Ui.Text("  " + n.ValueHint, 10.5, "AccentBrush");
                    vh.FontFamily = Fonts.Mono;
                    vh.VerticalAlignment = VerticalAlignment.Center;
                    head.Children.Add(vh);
                }
                Border kind = Badge(NodeKindLabel(n), KindBackground(NodeSuggestKind(n)), KindForeground(NodeSuggestKind(n)));
                kind.VerticalAlignment = VerticalAlignment.Center;
                kind.Margin = new Thickness(6, 0, 0, 0);
                kind.Padding = new Thickness(4, 0, 4, 0);
                head.Children.Add(kind);
                row.Children.Add(head);
                TextBlock d = Ui.Wrap(n.Desc, 11, "TextFaintBrush");
                row.Children.Add(d);
                host.Children.Add(row);
                if (n.Children.Count > 0) AddNodeTree(host, n.Children, depth + 1);
            }
        }

        // ==================================================================
        //  标题栏搜索
        // ==================================================================

        private void OnTitleSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_titleSearchPlaceholder != null)
                _titleSearchPlaceholder.Visibility = string.IsNullOrEmpty(_titleSearch.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            RefreshCommandList();
        }

        private void OnTitleSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _titleSearch.Text = "";
                Input.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && _cmdList != null && _cmdList.Items.Count > 0)
            {
                _cmdList.Focus();
                _cmdList.SelectedIndex = 0;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && _cmdList != null && _cmdList.Items.Count > 0)
            {
                object data = _listData.Count > 0 ? _listData[0] : null;
                CmdSpec s = data as CmdSpec;
                if (s != null)
                {
                    SetCurrentSpec(s, true);
                    Input.Focus();
                }
                e.Handled = true;
            }
        }

        // ==================================================================
        //  弹窗
        // ==================================================================

        private void OpenThemeWindow()
        {
            ThemeWindow w = new ThemeWindow(this);
            w.Owner = this;
            w.ShowDialog();
            RefreshGlassPanels();
            UpdateStatus();
        }

        private void OpenSettings()
        {
            SettingsWindow w = new SettingsWindow(this);
            w.Owner = this;
            if (w.ShowDialog() == true)
            {
                Output.FontSize = Settings.FontSize;
                Output.Document.FontSize = Settings.FontSize;
                Output.Document.LineHeight = Math.Max(15, Settings.FontSize * 1.42);
                Output.FontFamily = new FontFamily(Settings.FontFamily + ", Consolas");
                Output.Document.FontFamily = Output.FontFamily;
                UpdateStatus();
            }
        }

        private void ShowHelp()
        {
            HelpWindow w = new HelpWindow(this);
            w.Owner = this;
            w.ShowDialog();
        }

        private void RestartElevated()
        {
            RestartElevated(null);
        }

        private void RestartElevated(string commandToRun)
        {
            string extra = string.IsNullOrEmpty(commandToRun) ? "" : "--run \"" + commandToRun + "\"";
            if (Elevation.RestartAsAdmin(extra, SessionDirectory))
            {
                App.SuppressCloseSave = true;
                Close();
                Application.Current.Shutdown();
            }
            else
            {
                AppendSystem(Loc.T("提权被取消或失败。"));
            }
        }
    }
}
