// ---------------------------------------------------------------------------
//  Schedule.cs — 主界面：多进程入口合并按钮 + 定时 / 循环执行
//
//  【1】多进程按钮（提示行右侧）
//    以前提示行上有两个东西：「多进程」开关 + 一个灰色状态图标按钮。
//    现在合并成一个按钮：
//      · 没有任务在跑 → 普通灰底，文案「多进程」（图标与文字同为暗色）
//      · 有任务在跑   → 强调色蓝底，文案「多进程 · 正在运行」
//    悬浮提示给出「正在运行 N 条 / 共 M 条任务行」，点击打开多进程窗口。
//
//    刷新方式选「自己挂一个 250ms 的 DispatcherTimer 轮询」而不是往 MultiProc.cs 的
//    90ms 刷新点里挂钩子：那个刷新点正在被另一个 teammate 大改，而轮询只依赖约定好的
//    MultiProcAnyRunning() 一个入口，最不容易被改坏；反正定时 / 循环本来就需要一个
//    心跳定时器，两件事共用一拍，不多花任何代价。状态没变时不碰控件，所以轮询本身
//    不会引起重排。
//
//  【2】定时 / 循环（提示行右侧，与「多命令」「多进程」同一水平线）
//    「定时 + 时间」「循环 + 间隔」「停止」并进提示行（原来自己占一行）。
//    状态小字（「每 5s 执行中 · 第 3 次」「已排定 12:30:00 执行」）放在控制台顶部一条：
//    提示行已经排满控件，状态文字更长（英文尤其明显），放那里会被挤掉；
//    控制台顶部这条没排定时整条 Collapsed 不占高度，有排定时贴着输出区上沿。
//    两个开关只管「模式」：开着的时候按「运行」不再立即执行，而是把这条命令交给这里的
//    调度器（MainPanels.cs 的 RunCommand 顶部有一行分流）。调度器状态是纯数据、不持有
//    控件引用，所以切换语言重建界面之后排定照旧继续跑。
//
//    取舍（都按"简单可靠"选，理由如下）：
//      · 循环 = 开火即跑第一轮，之后每 N 秒一轮（不是先干等 N 秒，反馈更直接）；
//      · 上一轮还没跑完 → 跳过这一轮，不排队。排队会在「长命令 + 短间隔」时越堆越多，
//        而且主控制台只有一个执行槽（槽位 0），本来也塞不下并发；
//      · 同一条命令重复按「运行」→ 只更新那一条排定，不叠加第二个循环；
//      · 单次定时到点撞上正在运行 → 每 2 秒重试直到能跑，避免"排定了却没执行"。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowsCommandTools
{
    /// <summary>
    /// 一条已经交给调度器的命令。纯数据，不持有任何界面控件引用 ——
    /// 所以切换语言整体重建界面（RebuildUi）之后排定照旧有效。
    /// </summary>
    internal sealed class ScheduleJob
    {
        /// <summary>要执行的命令原文。</summary>
        public string Command = "";
        /// <summary>true = 按间隔循环，false = 只执行一次。</summary>
        public bool Loop;
        /// <summary>下一次该跑的时刻（本地时间）。</summary>
        public DateTime Due;
        /// <summary>循环间隔。</summary>
        public TimeSpan Interval = TimeSpan.FromSeconds(5);
        /// <summary>已经跑过几轮。</summary>
        public int Runs;
        /// <summary>上一轮到点时有命令在跑、被跳过了（提示只打一次）。</summary>
        public bool Skipped;
    }

    public partial class MainWindow
    {
        // ==================================================================
        //  需求 1：多进程入口 + 运行状态，合并成一个按钮
        // ==================================================================

        /// <summary>提示行上那个合并后的「多进程」按钮（重建界面会换成新实例）。</summary>
        private Button _mpButton;
        /// <summary>已经画到按钮上的状态（-1 = 还没画过）。状态没变就不动控件。</summary>
        private int _mpShownRunning = -1;
        private int _mpShownRuns = -1;
        private int _mpShownRows = -1;

        /// <summary>
        /// 造出提示行右侧的「多进程」按钮（入口 + 运行状态二合一）。
        /// MainPanels.cs 建一次、加进提示行；语言切换重建界面时会再建一个新的。
        /// </summary>
        internal Button BuildMergedMultiProcButton()
        {
            Button b = Ui.Btn(null, "GhostButton", delegate { OpenMultiProcWindow(); });
            b.FontSize = 11.5;
            b.Padding = new Thickness(11, 4, 11, 4);
            b.VerticalAlignment = VerticalAlignment.Center;
            _mpButton = b;
            _mpShownRunning = -1;
            _mpShownRuns = -1;
            _mpShownRows = -1;
            RefreshMergedMultiProcButton();
            EnsureSchedTimer();
            return b;
        }

        /// <summary>
        /// 按当前状态重画按钮：
        ///   没有任务在跑 → 普通灰底「多进程」（图标和文字一起暗下去）
        ///   有任务在跑   → 强调色蓝底「多进程 · 正在运行」
        /// 每 250ms 被心跳定时器调一次，只在状态真的变了才碰控件。
        /// </summary>
        internal void RefreshMergedMultiProcButton()
        {
            Button b = _mpButton;
            if (b == null) return;

            bool running = MultiProcAnyRunning();
            int runs = MpRunningCount();
            int rows = MpRowCount();
            int flag = running ? 1 : 0;
            if (flag == _mpShownRunning && runs == _mpShownRuns && rows == _mpShownRows) return;
            _mpShownRunning = flag;
            _mpShownRuns = runs;
            _mpShownRows = rows;

            b.Content = MpButtonContent(running);
            b.FontWeight = running ? FontWeights.SemiBold : FontWeights.Normal;
            // 内容换成 StackPanel 之后 UIA / 读屏软件就取不到按钮名了，这里补一个。
            b.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
                running ? Loc.T("多进程 · 正在运行") : Loc.T("多进程"));
            if (running)
            {
                b.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                b.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
            }
            else
            {
                b.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                b.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
            }
            b.ToolTip = MpButtonTip(running, runs, rows);
        }

        /// <summary>按钮内容：图标 + 文案（运行中时整块用强调色上的对比色）。</summary>
        private static StackPanel MpButtonContent(bool running)
        {
            StackPanel sp = Ui.H();
            sp.VerticalAlignment = VerticalAlignment.Center;

            UIElement ic = Icons.Create("taskStack", 13.5, running ? "OnAccentBrush" : "TextFaintBrush", 1.5);
            ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.Children.Add(ic);

            TextBlock t = Ui.Text(running ? Loc.T("多进程 · 正在运行") : Loc.T("多进程"), 11.5,
                running ? "OnAccentBrush" : "TextDimBrush",
                running ? FontWeights.SemiBold : FontWeights.Normal);
            t.Margin = new Thickness(6, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            return sp;
        }

        /// <summary>悬浮提示：正在运行几条 / 一共几行任务。沿用多进程原来的措辞。</summary>
        private static string MpButtonTip(bool running, int runs, int rows)
        {
            if (rows == 0)
                return Loc.T("多进程：还没有任务行。点这里打开多进程窗口，可以同时跑多条命令。");
            if (!running)
                return Loc.T("多进程：当前没有任务在运行（共 ") + rows.ToString(CultureInfo.InvariantCulture)
                    + Loc.T(" 条任务行）。点这里查看各任务状态。");
            return Loc.T("多进程：正在运行 ") + runs.ToString(CultureInfo.InvariantCulture)
                + Loc.T(" 条任务（共 ") + rows.ToString(CultureInfo.InvariantCulture)
                + Loc.T(" 条）。点这里查看各任务状态。");
        }

        /// <summary>
        /// 正在运行的多进程任务数。兜底实现：直接遍历执行器槽位（1..N 是多进程的），
        /// 不依赖 MultiProc.cs 里可能被改动的静态状态。
        /// 如果 mp-align 之后提供了 MultiProcRunningCount()，换掉这个方法体即可。
        /// </summary>
        private static int MpRunningCount()
        {
            int n = 0;
            int total = CommandExecutor.SlotCreated;
            for (int i = 1; i < total; i++)
            {
                ExecOne one = CommandExecutor.Slot(i);
                if (one != null && one.IsRunning) n++;
            }
            return n;
        }

        /// <summary>当前任务行数。暂时读多进程的公开任务列表（行数只有它知道）；
        /// 如果 mp-align 提供了 MultiProcRowCount()，换掉这一行即可。</summary>
        private static int MpRowCount()
        {
            return MultiProc.Tasks.Count;
        }

        // ==================================================================
        //  需求 2：定时 / 循环 —— 界面
        // ==================================================================

        /// <summary>心跳定时器：一拍里跑到点的排定 + 刷新多进程按钮 + 刷新状态小字。</summary>
        private DispatcherTimer _schedTimer;

        /// <summary>所有还活着的排定（定时一次 / 循环多次）。</summary>
        private readonly List<ScheduleJob> _schedJobs = new List<ScheduleJob>();

        /// <summary>「定时」开关（只管模式：开着时按「运行」＝排定执行）。</summary>
        private bool _schedOnceOn;
        /// <summary>「循环」开关（同上）。</summary>
        private bool _schedLoopOn;
        /// <summary>上一次画到按钮上的开关状态（-1 = 还没画过）。</summary>
        private int _schedOnceShown = -1;
        private int _schedLoopShown = -1;

        /// <summary>输入框里的时间 / 间隔文本。存一份是为了重建界面后填回去。</summary>
        private string _schedTimeText = "+30s";
        private string _schedEveryText = "5s";

        private Button _schedOnceBtn;
        private Button _schedLoopBtn;
        private TextBox _schedTimeBox;
        private TextBox _schedEveryBox;
        private Button _schedStopBtn;
        private TextBlock _schedStatus;

        /// <summary>时间 / 间隔输入框的常规宽度（HH:mm:ss 得放得下）。</summary>
        private const double SchedTimeBoxWidth = 74;
        private const double SchedEveryBoxWidth = 48;
        /// <summary>窄窗口下的紧凑宽度（MainPanels.cs 的空间不够时切过去）。</summary>
        private const double SchedTimeBoxCompact = 56;
        private const double SchedEveryBoxCompact = 36;

        /// <summary>正在由调度器触发 RunCommand（这一轮不再被当成"用户按了运行"去排定）。</summary>
        private bool _schedFiring;
        /// <summary>当前正在跑的这一条是调度器发起的（「停止」据此决定要不要一起结束它）。</summary>
        private bool _schedRunActive;
        /// <summary>窗口 Closing 只挂一次（重建界面会再走一遍 BuildScheduleControls）。</summary>
        private bool _schedClosingHooked;

        /// <summary>定时 / 循环任意一个开着 —— MainPanels.cs 的 RunCommand 据此改走调度器。</summary>
        private bool ScheduleArmed
        {
            get { return _schedOnceOn || _schedLoopOn; }
        }

        /// <summary>
        /// 提示行**左侧**那组定时 / 循环控件：「定时 + 时间」「循环 + 间隔」「停止」。
        /// 【位置】以前它自己占 Grid 的第 2 行（提示行下面单独一行），现在并进提示行、
        /// 贴在提示行左端，与右侧的「多命令」「多进程」同一水平线 —— 所以这里返回的是一个
        /// 横向小控件组，由 MainPanels.cs 放进 hintRow 第 0 列（Auto），不再需要自己一行。
        /// 【尺寸】整体按旁边小按钮的规格（FontSize 11.5 / Padding 11,4,11,4）保持一致；
        /// 时间 / 间隔输入框比原来窄（提示行空间有限，英文 Schedule / +30s / Loop / 5s / Stop 更长），
        /// 极窄时还可以由 SetSchedBoxesCompact 再收一档。
        /// 状态小字不在这里 —— 它跟着控制台顶部那条（_schedStatus，MainPanels.cs 里创建）。
        /// </summary>
        internal StackPanel BuildScheduleControls()
        {
            StackPanel left = Ui.H();
            left.VerticalAlignment = VerticalAlignment.Center;

            UIElement ic = Icons.Create("clock", 13, "TextFaintBrush", 1.5);
            ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            ic.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 6, 0));
            left.Children.Add(ic);

            _schedOnceBtn = Ui.Btn(Loc.T("定时"), "GhostButton", delegate { ToggleSchedOnce(); });
            StyleSchedButton(_schedOnceBtn);
            _schedOnceBtn.ToolTip = Loc.T("定时执行：打开后在输入框里输入命令，按「运行」不再立即执行，而是排到上面那个时间点。");
            left.Children.Add(_schedOnceBtn);

            _schedTimeBox = MakeSchedBox(SchedTimeBoxWidth, 5);
            _schedTimeBox.Text = _schedTimeText;
            _schedTimeBox.ToolTip = Loc.T("定时时间：HH:mm 或 HH:mm:ss（今天已经过了就顺延到明天）；也可以写相对时间，如 +30s、+5m。");
            _schedTimeBox.TextChanged += delegate
            {
                _schedTimeText = _schedTimeBox.Text;
                RefreshScheduleUi();
            };
            left.Children.Add(_schedTimeBox);

            _schedLoopBtn = Ui.Btn(Loc.T("循环"), "GhostButton", delegate { ToggleSchedLoop(); });
            StyleSchedButton(_schedLoopBtn);
            _schedLoopBtn.Margin = new Thickness(8, 0, 0, 0);
            _schedLoopBtn.ToolTip = Loc.T("循环执行：打开后按「运行」，这条命令会立刻执行一次，之后按上面的间隔反复执行。");
            left.Children.Add(_schedLoopBtn);

            _schedEveryBox = MakeSchedBox(SchedEveryBoxWidth, 5);
            _schedEveryBox.Text = _schedEveryText;
            _schedEveryBox.ToolTip = Loc.T("循环间隔：如 5s、1m、2h（不带单位按秒算，最短 1 秒）。");
            _schedEveryBox.TextChanged += delegate
            {
                _schedEveryText = _schedEveryBox.Text;
                RefreshScheduleUi();
            };
            left.Children.Add(_schedEveryBox);

            _schedStopBtn = Ui.Btn(Loc.T("停止"), "GhostButton", delegate { StopSchedules(); });
            StyleSchedButton(_schedStopBtn);
            _schedStopBtn.Margin = new Thickness(8, 0, 0, 0);
            _schedStopBtn.IsEnabled = false;
            _schedStopBtn.ToolTip = Loc.T("停止所有定时 / 循环（如果当前这一轮是调度器发起的，也会一起结束）。");
            left.Children.Add(_schedStopBtn);

            _schedOnceShown = -1;
            _schedLoopShown = -1;
            RefreshScheduleUi();
            EnsureSchedTimer();
            HookWindowClosing();
            return left;
        }

        /// <summary>
        /// 窄窗口下的兜底：把时间 / 间隔输入框收窄一档，给右侧「多命令 / 多进程」腾地方。
        /// 由 MainPanels.cs 的 OnHintRowSizeChanged 调用（控件还没建时直接返回）。
        /// </summary>
        internal void SetSchedBoxesCompact(bool compact)
        {
            if (_schedTimeBox != null) _schedTimeBox.Width = compact ? SchedTimeBoxCompact : SchedTimeBoxWidth;
            if (_schedEveryBox != null) _schedEveryBox.Width = compact ? SchedEveryBoxCompact : SchedEveryBoxWidth;
        }

        /// <summary>
        /// 关窗时把定时器全部收掉。挂窗口自己的 Closing（每个窗口实例只挂一次）——
        /// 这样即使 MainWindow.OnClosing 忘了调 StopAllSchedules() 也不会漏掉。
        /// </summary>
        private void HookWindowClosing()
        {
            if (_schedClosingHooked) return;
            _schedClosingHooked = true;
            Closing += delegate { StopAllSchedules(); };
        }

        private static void StyleSchedButton(Button b)
        {
            b.FontSize = 11.5;
            b.Padding = new Thickness(11, 4, 11, 4);
            b.VerticalAlignment = VerticalAlignment.Center;
        }

        private static TextBox MakeSchedBox(double width, double gap)
        {
            TextBox tb = new TextBox();
            tb.Width = width;
            tb.Height = 25;
            tb.FontSize = 11.5;
            tb.FontFamily = Fonts.Mono;
            tb.Padding = new Thickness(8, 1, 8, 1);
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(gap, 0, 0, 0);
            return tb;
        }

        /// <summary>心跳：一拍干三件事。定时器只建一次，重建界面不会叠加。</summary>
        private void EnsureSchedTimer()
        {
            if (_schedTimer != null) return;
            _schedTimer = new DispatcherTimer();
            _schedTimer.Interval = TimeSpan.FromMilliseconds(250);
            _schedTimer.Tick += OnSchedTick;
            _schedTimer.Start();
        }

        private void OnSchedTick(object sender, EventArgs e)
        {
            RunDueSchedules();
            RefreshMergedMultiProcButton();
            RefreshScheduleUi();
        }

        private void ToggleSchedOnce()
        {
            _schedOnceOn = !_schedOnceOn;
            // 打开时给个能直接用的默认值，省得用户对着空框发呆
            if (_schedOnceOn && _schedTimeBox != null && _schedTimeBox.Text.Trim().Length == 0)
                _schedTimeBox.Text = "+30s";
            RefreshScheduleUi();
        }

        private void ToggleSchedLoop()
        {
            _schedLoopOn = !_schedLoopOn;
            if (_schedLoopOn && _schedEveryBox != null && _schedEveryBox.Text.Trim().Length == 0)
                _schedEveryBox.Text = "5s";
            RefreshScheduleUi();
        }

        /// <summary>重画这一行的开关 / 停止按钮 / 状态小字。状态没变就不碰控件。</summary>
        private void RefreshScheduleUi()
        {
            PaintSchedToggle(_schedOnceBtn, _schedOnceOn, Loc.T("定时 · 开"), Loc.T("定时"), ref _schedOnceShown);
            PaintSchedToggle(_schedLoopBtn, _schedLoopOn, Loc.T("循环 · 开"), Loc.T("循环"), ref _schedLoopShown);

            bool active = _schedJobs.Count > 0 || _schedRunActive;
            if (_schedStopBtn != null && _schedStopBtn.IsEnabled != active) _schedStopBtn.IsEnabled = active;

            if (_schedStatus == null) return;
            string text = ScheduleStatusText();
            // 状态小字在控制台顶部那条：没有排定时整条 Collapsed，一点高度都不占
            Visibility want = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_schedStatus.Visibility != want) _schedStatus.Visibility = want;
            if (string.Equals(text, _schedStatus.Text, StringComparison.Ordinal)) return;
            _schedStatus.Text = text;
            _schedStatus.SetResourceReference(TextBlock.ForegroundProperty,
                active ? "AccentBrush" : "TextFaintBrush");
        }

        private static void PaintSchedToggle(Button b, bool on, string onText, string offText, ref int shown)
        {
            if (b == null) return;
            int flag = on ? 1 : 0;
            if (flag == shown) return;
            shown = flag;
            b.Content = on ? onText : offText;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (on)
            {
                b.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                b.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
            }
            else
            {
                b.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                b.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
            }
        }

        /// <summary>状态小字：没排定时显示"下次运行会怎样"，有排定时显示排定 / 第几轮。</summary>
        private string ScheduleStatusText()
        {
            if (_schedJobs.Count == 0)
            {
                if (!_schedOnceOn && !_schedLoopOn) return "";
                if (_schedOnceOn && _schedLoopOn)
                    return Loc.T("已开启定时 + 循环：下次「运行」先按时间执行一次，之后按间隔循环");
                if (_schedOnceOn)
                    return Loc.T("已开启定时：下次「运行」按上面的时间执行一次");
                return Loc.T("已开启循环：下次「运行」按上面的间隔反复执行");
            }

            ScheduleJob last = _schedJobs[_schedJobs.Count - 1];
            string one;
            if (last.Loop)
                one = Loc.T("每 ") + FormatSpan(last.Interval) + Loc.T(" 执行中 · 第 ")
                    + last.Runs.ToString(CultureInfo.InvariantCulture) + Loc.T(" 次");
            else
                one = Loc.T("已排定 ") + last.Due.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Loc.T(" 执行");
            if (_schedJobs.Count > 1)
                one = one + Loc.T("（共 ") + _schedJobs.Count.ToString(CultureInfo.InvariantCulture)
                    + Loc.T(" 个排定）");
            return one;
        }

        // ==================================================================
        //  需求 2：定时 / 循环 —— 调度
        // ==================================================================

        /// <summary>
        /// 按「运行」时走到这里（RunCommand 顶部的分流）：把命令交给调度器，不立即执行。
        /// </summary>
        private void ArmSchedule(string text)
        {
            string cmd = text == null ? "" : text.Trim();
            if (cmd.Length == 0) return;

            string whenText = _schedTimeBox != null ? _schedTimeBox.Text : _schedTimeText;
            string everyText = _schedEveryBox != null ? _schedEveryBox.Text : _schedEveryText;
            _schedTimeText = whenText;
            _schedEveryText = everyText;

            DateTime due = DateTime.Now;
            if (_schedOnceOn)
            {
                if (!ParseWhen(whenText, out due))
                {
                    AppendSystem(Loc.T("[定时] 时间填得不对：") + whenText
                        + Loc.T("　—— 请填 HH:mm / HH:mm:ss，或 +30s、+5m 这样的相对时间。"));
                    return;
                }
            }

            TimeSpan every = TimeSpan.FromSeconds(5);
            if (_schedLoopOn)
            {
                if (!ParseEvery(everyText, out every))
                {
                    AppendSystem(Loc.T("[定时] 间隔填得不对：") + everyText
                        + Loc.T("　—— 请填 5s、1m、2h 这样的间隔（最短 1 秒）。"));
                    return;
                }
            }

            // 同一条命令不叠加：已经有排定就按新设置更新那一条
            for (int i = 0; i < _schedJobs.Count; i++)
            {
                ScheduleJob old = _schedJobs[i];
                if (old == null || !string.Equals(old.Command, cmd, StringComparison.Ordinal)) continue;
                old.Loop = _schedLoopOn;
                old.Interval = every;
                old.Due = due;
                old.Skipped = false;
                if (!_schedLoopOn) old.Runs = 0;
                AppendSystem(Loc.T("[定时] 这条命令已经在排定里，已按新设置更新（不会重复叠加）。"));
                RefreshScheduleUi();
                return;
            }

            ScheduleJob job = new ScheduleJob();
            job.Command = cmd;
            job.Loop = _schedLoopOn;
            job.Interval = every;
            job.Due = due;
            _schedJobs.Add(job);

            if (_schedLoopOn)
                AppendSystem(Loc.T("[定时] 已开始循环执行（每 ") + FormatSpan(every) + Loc.T("）：") + cmd);
            else
                AppendSystem(Loc.T("[定时] 已排定 ") + due.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + Loc.T(" 执行：") + cmd);
            RefreshScheduleUi();
        }

        /// <summary>心跳里跑到点的排定。忙的时候：循环跳过这一轮，单次定时等空下来再跑。</summary>
        private void RunDueSchedules()
        {
            if (_schedFiring) return;              // 正在跑一轮（可能还开着确认对话框），别再进来
            if (_schedRunActive && !Exec.IsRunning) { _schedRunActive = false; RefreshScheduleUi(); }
            if (_schedJobs.Count == 0) return;

            DateTime now = DateTime.Now;
            for (int i = _schedJobs.Count - 1; i >= 0; i--)
            {
                if (i < 0 || i >= _schedJobs.Count) continue;
                ScheduleJob job = _schedJobs[i];
                if (job == null) { _schedJobs.RemoveAt(i); continue; }
                if (job.Due > now) continue;

                bool busy = Exec.IsRunning;
                if (busy)
                {
                    if (job.Loop)
                    {
                        // 跳过这一轮：不排队，免得长命令 + 短间隔时越堆越多
                        job.Due = now + job.Interval;
                        if (!job.Skipped)
                        {
                            job.Skipped = true;
                            AppendSystem(Loc.T("[定时] 上一轮还在运行，跳过这次循环。"));
                        }
                    }
                    else
                    {
                        // 单次定时：等控制台空下来再跑，避免"排定了却没执行"
                        job.Due = now.AddSeconds(2);
                        if (!job.Skipped)
                        {
                            job.Skipped = true;
                            AppendSystem(Loc.T("[定时] 当前有命令在运行，等它结束后再执行排定的命令。"));
                        }
                    }
                    continue;
                }

                job.Skipped = false;
                job.Runs = job.Runs + 1;
                if (job.Loop) job.Due = now + job.Interval;
                string cmd = job.Command;
                int round = job.Runs;
                if (!job.Loop) _schedJobs.RemoveAt(i);

                AppendSystem(Loc.T("[定时] 第 ") + round.ToString(CultureInfo.InvariantCulture)
                    + Loc.T(" 轮：") + cmd);
                FireScheduled(cmd);
            }
            RefreshScheduleUi();
        }

        /// <summary>真正把排定的命令丢给主控制台执行（走的是同一条 RunCommand，安全检查一样生效）。</summary>
        private void FireScheduled(string cmd)
        {
            _schedFiring = true;
            try
            {
                RunCommand(cmd);
            }
            finally
            {
                _schedFiring = false;
            }
            _schedRunActive = Exec.IsRunning;
        }

        /// <summary>「停止」：撤销所有排定；如果当前这一轮是调度器发起的，也一起结束。</summary>
        private void StopSchedules()
        {
            bool hadJobs = _schedJobs.Count > 0;
            _schedJobs.Clear();
            bool killed = false;
            if (_schedRunActive && Exec.IsRunning)
            {
                Exec.Cancel();
                killed = true;
            }
            _schedRunActive = false;
            if (killed) AppendSystem(Loc.T("已停止定时 / 循环，并结束了当前这一轮命令。"));
            else if (hadJobs) AppendSystem(Loc.T("已停止定时 / 循环。"));
            RefreshScheduleUi();
        }

        /// <summary>
        /// 窗口关闭时收掉所有定时器 / 排定（MainWindow.OnClosing 调用）。
        /// 这里只碰状态不碰界面 —— 关窗时控件可能已经不可用了。
        /// </summary>
        internal void StopAllSchedules()
        {
            _schedJobs.Clear();
            _schedRunActive = false;
            _schedFiring = false;
            if (_schedTimer != null) _schedTimer.Stop();
        }

        // ==================================================================
        //  时间 / 间隔解析
        // ==================================================================

        /// <summary>
        /// 解析「什么时候执行」：
        ///   HH:mm / HH:mm:ss  → 今天这个时刻；已经过了就顺延到明天（下一次出现）
        ///   +30s / +5m / +2h  → 相对现在（不带单位按秒算）
        /// 解析失败返回 false，绝不抛异常。
        /// </summary>
        private static bool ParseWhen(string text, out DateTime due)
        {
            due = DateTime.MinValue;
            if (text == null) return false;
            string s = text.Trim();
            if (s.Length == 0) return false;

            if (s[0] == '+')
            {
                TimeSpan span;
                if (!ParseEvery(s.Substring(1), out span)) return false;
                due = DateTime.Now + span;
                return true;
            }

            string[] parts = s.Split(':');
            if (parts.Length != 2 && parts.Length != 3) return false;
            int hour;
            int minute;
            int second = 0;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out second)) return false;
            }
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return false;

            DateTime now = DateTime.Now;
            DateTime today = new DateTime(now.Year, now.Month, now.Day, hour, minute, second);
            due = today > now ? today : today.AddDays(1);
            return true;
        }

        /// <summary>解析间隔：「5s」「1m」「2h」，不带单位按秒算。范围 1 秒 ~ 24 小时。</summary>
        private static bool ParseEvery(string text, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            if (text == null) return false;
            string s = text.Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            double unit = 1.0;
            char last = s[s.Length - 1];
            if (last == 's') s = s.Substring(0, s.Length - 1);
            else if (last == 'm') { unit = 60.0; s = s.Substring(0, s.Length - 1); }
            else if (last == 'h') { unit = 3600.0; s = s.Substring(0, s.Length - 1); }

            double value;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            if (value <= 0) return false;

            double seconds = value * unit;
            if (seconds < 1.0) seconds = 1.0;
            if (seconds > 86400.0) seconds = 86400.0;
            span = TimeSpan.FromSeconds(seconds);
            return true;
        }

        /// <summary>把间隔写成「5s」「2m」「1h」这种好读的形式。</summary>
        private static string FormatSpan(TimeSpan span)
        {
            double seconds = span.TotalSeconds;
            if (seconds >= 3600.0 && Math.Abs(seconds % 3600.0) < 0.001) return TrimNum(seconds / 3600.0) + "h";
            if (seconds >= 60.0 && Math.Abs(seconds % 60.0) < 0.001) return TrimNum(seconds / 60.0) + "m";
            return TrimNum(seconds) + "s";
        }

        private static string TrimNum(double value)
        {
            if (Math.Abs(value - Math.Round(value)) < 0.001)
                return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
