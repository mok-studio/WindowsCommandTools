// ---------------------------------------------------------------------------
//  MultiProc.cs — 多进程执行（独立二级窗口）
//
//  改造前后：
//    · 以前「多进程」是主界面里一块可折叠的内嵌面板（4 行固定，每行一个矮输出区），
//      折叠状态下用户不知道输出是什么、也读不了几行 —— 这是被抱怨的根源；
//    · 现在它是一个**独立窗口**：主界面提示行的「多进程」开关负责打开/关闭它，
//      窗口可以鼠标缩放、可以最大化，里面行数可增可减，行多了窗口内部滚动，
//      不压缩每行的显示空间。
//
//  这个文件里有三部分：
//    1. MultiProcTask / MultiProc —— 全局多进程状态（任务行、槽位绑定、输出队列、
//       输出窗口）。**故意做成 static**：换语言会整体重建主界面，静态状态能让正在跑的
//       任务和已经攒下的输出活过那次重建（老代码的槽位就是这么活下来的）。
//    2. MultiProcWindow —— 独立窗口：顶部工具条 + 可滚动的任务行 + 分页标签输出区
//       + 左侧命令库 + 右侧参数说明（敲命令时实时提示，复用 Suggester.Compute）。
//    3. MainWindow 的那几个 internal 方法 —— 给 MainPanels.cs 接线用，只有一个调用点。
//
//  隔离是怎么做的（关键，和老实现一致）：
//    每条任务固定绑定 Exec.cs 里 CommandExecutor 的一个槽位（1..N，0 是主控制台），
//    槽位自带独立的 Process、独立的重定向管道、独立的输出泵线程、独立的取消标记和
//    退出码。所以「输出串台」「停 A 把 B 也停了」从结构上就不会发生。
//
//  命令行窗口的标签：
//    所有任务的输出集中在一个总窗口里，用顶部分页标签切换。标签可以左右拖动重排、
//    标签多了标签条自己横向滚动。**「拖动合并窗口」这一整套已经删掉**：
//      · 拖到标签条外面不再弹窗、也不再「吸回」；
//      · 右上角那个「合并窗口 · 开/关」开关删掉了（AppSettings.MergeCommandWindows
//        字段保留但这里不再读它）；
//      · 弹出独立窗口只走标签的右键菜单（弹出为独立窗口 / 合并回标签 / 关闭这个输出窗口），
//        分离出来的输出窗口自己标题栏上也有「合并回标签」。
//
//  控制台（shell）：
//    窗口打开那一刻跟随主窗口当前的 EffectiveShell（PS / CMD），窗口标题栏上有分段开关
//    可以随时在窗口内切换；**每一行还可以单独指定控制台**（行右边那个 PS/CMD 小按钮），
//    不指定就跟随窗口模式。行下面的灰行最左侧用 PS> / CMD> 明示这一行实际用哪个控制台。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace WindowsCommandTools
{
    /// <summary>一条任务的状态色（绿 = 正在运行，灰 = 已停止/没跑过）。</summary>
    internal enum MultiProcTint
    {
        Faint = 0,   // 灰：没跑过 / 已停止
        Running = 1, // 绿/强调色：正在运行
        Ok = 2,      // 成功
        Fail = 3     // 失败
    }

    /// <summary>一条任务行用到的全部控件和运行状态。</summary>
    internal sealed class MultiProcTask
    {
        /// <summary>创建序号（从 1 开始，等于绑定的槽位号）。</summary>
        public int Id;
        /// <summary>这个任务专属的执行槽位。</summary>
        public ExecOne Slot;

        // ---- 任务行上的控件 ----
        public Border RowRoot;
        public TextBlock RowNo;
        public TextBox Input;
        public Button RunBtn;
        public Button MultiBtn;
        /// <summary>行右边的「这一行用哪个控制台」小按钮（PS / CMD）。</summary>
        public Button ShellBtn;
        public TextBlock ShellLabel;
        /// <summary>灰行最左侧的 PS&gt; / CMD&gt; 提示符。</summary>
        public TextBlock ShellText;
        /// <summary>灰行里的状态文案（就绪 / 运行中… / 完成 · 退出码 0）。</summary>
        public TextBlock Status;
        /// <summary>灰行右侧的输出位置提示（输出在下方标签页 / 输出在独立窗口里）。</summary>
        public TextBlock InlineOut;
        public Border InlineBox;
        /// <summary>灰行背景当前用的画笔键（避免每 90ms 重复 SetResourceReference）。</summary>
        public string InlineBrushKey;
        /// <summary>小按钮上当前显示的控制台短名（避免每 90ms 重复刷）。</summary>
        public string ShellShown;

        /// <summary>
        /// 这一行单独指定的执行控制台（null = 跟随窗口模式）。
        /// 和主窗口的「临时切换」语义一致：只影响这一行，随时可以改回「跟随窗口」。
        /// </summary>
        public ShellKind? RowShell;

        /// <summary>这一行是不是「多命令」形式（多行文本区）。</summary>
        public bool Multi;
        /// <summary>用户提交过的命令原文（标签摘要、状态菜单都用它）。</summary>
        public string LastCmd = "";

        /// <summary>本地记录的「是否在跑」。槽位的 IsRunning 是权威值，这里用于界面判断。</summary>
        public bool Running;
        /// <summary>本次运行的开始时间，用来算耗时。</summary>
        public DateTime StartedAt;

        // ---- 输出 ----
        /// <summary>输出块记录。视图换绑（标签弹出 / 合并回去）时用它重画，内容不会丢。</summary>
        public List<OutputChunk> ViewChunks = new List<OutputChunk>();
        /// <summary>已经写进「当前视图」的块数。=-1 表示需要整块重画。</summary>
        public int OutSync = -1;
        /// <summary>记录被裁过，下一批输出要整块重画。</summary>
        public bool NeedsRebuild;
        /// <summary>当前绑定到这块输出的视图（总窗口里的那块，或者独立小窗口里的那块）。</summary>
        public MultiProcOutputView OutputView;
        /// <summary>分离出去的输出窗口（合并模式下也可以有，标签上会带一个 ↗ 标记）。</summary>
        public OutputWindow OutputWindow;

        // ---- 输出先入队，再由定时器批量刷进界面 ----
        // 直接在每个输出行上操作 TextBox 会让长命令（例如 ping -t）把 UI 线程压死，
        // 攒一批再刷既流畅又不会丢内容。
        public readonly Queue<OutputChunk> Queue = new Queue<OutputChunk>();
        public readonly object QueueLock = new object();
    }

    // =======================================================================
    //  全局多进程状态
    // =======================================================================
    internal static class MultiProc
    {
        /// <summary>第一次打开多进程窗口时的默认行数。</summary>
        private const int DefaultRows = 4;

        /// <summary>主线程上定时把输出队列刷进界面用。</summary>
        private static DispatcherTimer _timer;

        /// <summary>任务行按创建顺序排列 —— 标签页和状态菜单都按这个顺序展示。</summary>
        public static readonly List<MultiProcTask> Tasks = new List<MultiProcTask>();
        /// <summary>主界面（语言切换会换实例，每次重建时用最新那个）。</summary>
        public static MainWindow Host;

        /// <summary>
        /// 当前设置。多进程的输出视图 / 输出窗口不是 MainWindow 的 partial，
        /// 拿不到 MainWindow.Settings 这个实例字段，统一从这里取（Host 为空时给一份默认值，
        /// 只是为了让字号、自动滚动这些读起来不用到处判空）。
        /// </summary>
        public static AppSettings Settings
        {
            get { return Host == null ? _fallbackSettings : Host.Settings; }
        }

        private static readonly AppSettings _fallbackSettings = new AppSettings();
        /// <summary>多进程窗口（惰性创建，关闭只是隐藏，任务和输出都留着）。</summary>
        public static MultiProcWindow Win;
        /// <summary>行数上限（来自设置，0 = 不限制，但硬上限是 MultiProcWindow.MaxRows）。</summary>
        public static int RowLimit = 8;
        /// <summary>当前标签选中的任务。</summary>
        public static MultiProcTask Selected;

        // ==================================================================
        //  控制台（shell）选择
        // ==================================================================

        /// <summary>
        /// 一条任务实际用哪个控制台执行：**行上单独指定的优先**，
        /// 没指定就跟随多进程窗口的模式；窗口还没建时退回主窗口的当前模式。
        /// </summary>
        public static ShellKind ShellForTask(MultiProcTask t)
        {
            if (t != null && t.RowShell.HasValue) return t.RowShell.Value;
            if (Win != null) return Win.WindowShell;
            if (Host != null) return Host.EffectiveShell;
            return ShellKind.PowerShell;
        }

        /// <summary>灰行最左侧那个提示符：PS&gt; / CMD&gt;。</summary>
        public static string PromptOf(ShellKind shell)
        {
            return shell == ShellKind.Cmd ? "CMD>" : "PS>";
        }

        /// <summary>一行上显示的控制台短名（小按钮用）。</summary>
        public static string ShellShort(ShellKind shell)
        {
            return shell == ShellKind.Cmd ? "CMD" : "PS";
        }

        /// <summary>标签的兜底右键菜单（无父级，可以挂到任何标签或输出窗口上）。</summary>
        private static ContextMenu _tabMenu;

        // ==================================================================
        //  设置同步
        // ==================================================================

        /// <summary>把设置里的「行数上限」同步到运行状态。改设置后调用。</summary>
        public static void ApplySettings()
        {
            // 「合并命令行窗口」（AppSettings.MergeCommandWindows）已经不再参与判断：
            // 输出永远显示在总窗口的标签页里，弹出独立窗口走标签右键菜单。
            // 字段本身保留在 AppSettings 里（旧配置文件读进来不报错），只是没人读它了。
            RowLimit = Host == null ? 8 : Host.Settings.MultiProcRowLimit;
            if (Win == null) return;
            Win.NotifySettingsChanged();
        }

        /// <summary>按设置的硬上限算行数（0 = 不限制 → 取编译期上限）。</summary>
        public static int ConfiguredRows()
        {
            int want = RowLimit;
            if (want <= 0) return MultiProcWindow.MaxRows;
            if (want > MultiProcWindow.MaxRows) want = MultiProcWindow.MaxRows;
            if (want < 1) want = 1;
            return want;
        }

        /// <summary>还能再加行吗？（设置上限 + 硬上限双重封顶）</summary>
        public static bool CanAddRow()
        {
            return Tasks.Count < ConfiguredRows();
        }

        // ==================================================================
        //  派生出来的显示文本（标签 / 状态菜单 / 状态按钮共用）
        // ==================================================================

        public static string BrushOf(MultiProcTint tint)
        {
            if (tint == MultiProcTint.Running) return "AccentBrush";
            if (tint == MultiProcTint.Ok) return "SuccessBrush";
            if (tint == MultiProcTint.Fail) return "DangerBrush";
            return "TextFaintBrush";
        }

        public static MultiProcTint TintOf(MultiProcTask t)
        {
            if (t == null) return MultiProcTint.Faint;
            if (t.Running || (t.Slot != null && t.Slot.IsRunning)) return MultiProcTint.Running;
            if (t.Slot == null) return MultiProcTint.Faint;
            int code = t.Slot.LastExitCode;
            if (code < 0) return MultiProcTint.Faint;
            return code == 0 ? MultiProcTint.Ok : MultiProcTint.Fail;
        }

        /// <summary>标签上的一行摘要：行号 + 命令摘要（+ 多命令标记）。</summary>
        public static string TabLabel(MultiProcTask t)
        {
            if (t == null) return "";
            string name = Loc.T("任务 ") + t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string cmd = Summarize(t.LastCmd);
            if (cmd.Length > 0) name += " · " + cmd;
            if (t.Multi) name += " " + Loc.T("[多命令]");
            return name;
        }

        public static string TabTooltip(MultiProcTask t)
        {
            if (t == null) return "";
            ShellKind sh = ShellForTask(t);
            string s = Loc.T("第 ") + t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + Loc.T(" 条任务（") + Shells.Display(sh) + Loc.T("）：") + StatusText(t);
            if (t.LastCmd.Length > 0) s += "\n" + t.LastCmd;
            s += "\n" + Loc.T("拖动可以左右调整标签顺序；标签太多时标签条可以横向滚动；右键可以弹出为独立窗口。");
            return s;
        }

        /// <summary>状态文字：运行中 / 已结束（退出码 N）。</summary>
        public static string StatusText(MultiProcTask t)
        {
            if (t == null) return "";
            if (t.Running || (t.Slot != null && t.Slot.IsRunning)) return Loc.T("正在运行");
            if (t.Slot == null || t.Slot.LastExitCode < 0) return Loc.T("未运行");
            if (t.Slot.LastExitCode == 0) return Loc.T("已结束 · 退出码 0");
            return Loc.T("已结束 · 退出码 ") + t.Slot.LastExitCode;
        }

        /// <summary>把一条命令压成标签上能看的一行。</summary>
        private static string Summarize(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string s = cmd.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0)
                s = s.Replace("  ", " ");
            if (s.Length > 28) s = s.Substring(0, 28) + "…";
            return s;
        }

        // ==================================================================
        //  任务行的增删
        // ==================================================================

        public static MultiProcTask AddTask()
        {
            int slot = Tasks.Count + 1;
            ExecOne one = CommandExecutor.Slot(slot);
            if (one == null) return null;   // 超出编译期槽位数：设置已经封顶，正常到不了这里

            MultiProcTask t = new MultiProcTask();
            t.Id = slot;
            t.Slot = one;
            Tasks.Add(t);

            if (one != null)
            {
                ExecOne captured = one;
                captured.OutputId += OnTaskOutput;
                captured.FinishedId += OnTaskFinished;
            }
            EnsureTimer();
            if (Win != null) Win.OnTasksChanged(true);
            RefreshStatusButton();
            return t;
        }

        /// <summary>去掉最后一条任务行（正在运行的不肯删）。</summary>
        public static bool RemoveLastTask()
        {
            if (Tasks.Count <= 1) return false;
            MultiProcTask t = Tasks[Tasks.Count - 1];
            if (t.Running || (t.Slot != null && t.Slot.IsRunning)) return false;
            DropTask(t);
            return true;
        }

        public static void DropTask(MultiProcTask t)
        {
            if (t == null) return;
            if (t.Slot != null)
            {
                ExecOne captured = t.Slot;
                captured.OutputId -= OnTaskOutput;
                captured.FinishedId -= OnTaskFinished;
            }
            if (t.OutputWindow != null) t.OutputWindow.CloseForRemoval();
            if (t.OutputView != null) t.OutputView.Detach();
            if (Selected == t) Selected = null;
            Tasks.Remove(t);
            if (Win != null) Win.OnTasksChanged(false);
            RefreshStatusButton();
        }

        /// <summary>行数变化后重排行号（增删中间行时才需要，目前只在删尾时用到）。</summary>
        public static void ResetSelection()
        {
            if (Tasks.Count == 0) { Selected = null; return; }
            if (Selected == null || !Tasks.Contains(Selected)) Selected = Tasks[0];
        }

        // ==================================================================
        //  输出视图的绑定 / 换绑
        // ==================================================================

        /// <summary>取出这一行任务的输出视图（总窗口里那块，或者分离窗口里那块）。</summary>
        public static MultiProcOutputView ViewOf(MultiProcTask t)
        {
            if (t == null) return null;
            if (t.OutputWindow != null) return t.OutputWindow.View;
            if (t.OutputView != null && t.OutputView.Task == t) return t.OutputView;
            return null;
        }

        /// <summary>
        /// 换绑输出视图：先把已有视图上的内容重画一遍（换绑后内容一个字都不能少），
        /// 然后决定这一行的输出以后写到哪里。
        /// </summary>
        public static void Rebind(MultiProcTask t, bool ensureInline)
        {
            if (t == null || Win == null) return;

            if (ensureInline && t.OutputWindow == null)
            {
                t.OutputView = Win.HostFor(t);
                if (t.OutputView != null) t.OutSync = -1;
            }
            else if (!ensureInline && t.OutputView != null)
            {
                // 输出改由独立窗口显示：把总窗口里那块腾出来
                MultiProcOutputView v = t.OutputView;
                t.OutputView = null;
                if (v.Task == t) v.Detach();
                t.OutSync = -1;
                if (v.Task == null) v.Box.Clear();
            }

            MultiProcOutputView cur = ViewOf(t);
            if (cur != null) MultiProcOutputView.RebuildView(t, cur);
        }

        /// <summary>把某条任务的输出同步到所有正在显示它的视图上。</summary>
        public static void SyncViews(MultiProcTask t)
        {
            if (t == null) return;
            if (t.OutputWindow != null) t.OutputWindow.SyncOutput();
            if (t.OutputView != null && t.OutputView.Task == t)
            {
                // 没被选中的那块不用刷，切过去时会整块重画
                if (Selected == t) MultiProcOutputView.Sync(t, t.OutputView);
            }
        }

        /// <summary>给一条任务写一行系统提示（走输出队列，保证顺序）。</summary>
        public static void Write(MultiProcTask t, string text, bool system)
        {
            if (t == null) return;
            OutputChunk c = new OutputChunk();
            c.Text = text;
            c.IsSystem = system;
            lock (t.QueueLock) { t.Queue.Enqueue(c); }
        }

        // ==================================================================
        //  槽位回调（可能来自任意输出泵线程）
        // ==================================================================

        /// <summary>槽位回吐输出：按槽位号分流到对应的任务队列。</summary>
        private static void OnTaskOutput(int id, OutputChunk chunk)
        {
            MultiProcTask t = TaskOf(id);
            if (t == null) return;
            // 跑完之后（槽位已清空）迟到的输出直接丢掉，免得混进下一次运行
            if (!t.Running && (t.Slot == null || !t.Slot.IsRunning)) return;
            lock (t.QueueLock) { t.Queue.Enqueue(chunk); }
        }

        private static void OnTaskFinished(int id, ExecResult r)
        {
            MultiProcTask t = TaskOf(id);
            if (t == null) return;
            t.Running = false;
            long ms = 0;
            try { ms = (long)(DateTime.Now - t.StartedAt).TotalMilliseconds; }
            catch { }

            // 回 UI 线程：这里可能已经是进程结束后的异步回调
            Dispatcher d = Win == null ? null : Win.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(new Action(delegate
            {
                FlushTask(t);
                string s;
                if (r.Cancelled) s = Loc.T("已停止");
                else if (r.ExitCode == 0) s = Loc.T("完成 · 退出码 0");
                else s = Loc.T("退出码 ") + r.ExitCode;
                s += "  ·  " + (ms / 1000.0).ToString("0.00") + Loc.T(" 秒");

                MultiProc.Write(t, Loc.T("[多进程] 任务结束：") + s, true);
                FlushTask(t);
                if (Win != null) Win.SetTaskStatus(t, s, MultiProc.BrushOf(TintOf(t)));
                RefreshStatusButton();
            }));
        }

        public static MultiProcTask TaskOf(int id)
        {
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t != null && t.Id == id) return t;
            }
            return null;
        }

        // ==================================================================
        //  输出刷新
        // ==================================================================

        private static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(90);
            _timer.Tick += delegate { Flush(); };
            _timer.Start();
        }

        /// <summary>把队列里的输出批量刷进各自视图，并顺势刷新状态按钮。</summary>
        /// <summary>
        /// 每 90ms 一轮：把队列里的输出刷进视图，并且**每次都重建一遍标签**。
        /// 标签上的状态色点、行按钮的「运行/停止」都靠这一下来跟上任务状态 ——
        /// 以前只在「有新输出」时才顺带刷新，于是任务一跑完输出没了、刷新也停了，
        /// 色点永远停在「还在跑」的蓝色（这个坑真踩过）。
        /// </summary>
        public static void Flush()
        {
            for (int i = 0; i < Tasks.Count; i++) FlushTask(Tasks[i]);
            if (Win != null)
            {
                Win.RefreshTabsAndRows();
                Win.RefreshInlineHintsPublic();
            }
            RefreshStatusButton();
        }

        private static void FlushTask(MultiProcTask t)
        {
            if (t == null) return;
            List<OutputChunk> batch = new List<OutputChunk>();
            lock (t.QueueLock)
            {
                while (t.Queue.Count > 0 && batch.Count < 600) batch.Add(t.Queue.Dequeue());
            }
            if (batch.Count == 0) return;

            if (t.ViewChunks == null) t.ViewChunks = new List<OutputChunk>();
            for (int i = 0; i < batch.Count; i++) t.ViewChunks.Add(batch[i]);
            MultiProcOutputView.TrimKept(t);
            SyncViews(t);
            if (Win != null) Win.RefreshTaskUi(t);
        }

        // ==================================================================
        //  窗口级操作（菜单 / 状态按钮 / MainPanels 接线都走这里）
        // ==================================================================

        public static void Open(bool forceNew)
        {
            EnsureTimer();
            if (Win == null)
            {
                Win = new MultiProcWindow(Host);
                for (int i = 0; i < DefaultRows; i++)
                {
                    MultiProcTask t = AddTask();
                    if (t == null) break;
                }
                Selected = Tasks.Count > 0 ? Tasks[0] : null;
                Win.RebuildRows();
            }
            // 「打开时默认跟随主窗口模式」：窗口本来没显示（=一次真正的打开动作）时，
            // 把窗口的控制台模式同步成主窗口当前的 EffectiveShell（PS / CMD）。
            // 窗口已经开着就不动 —— 用户在窗口里切过的模式不能被悄悄改掉。
            if (!Win.IsVisible) Win.SyncShellFromHost();
            Win.Show();
            if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
            Win.Activate();
            Win.RefreshTexts();
            RefreshStatusButton();
            DumpHwnd();
        }

        /// <summary>主界面那个「多进程」开关：开 = 打开窗口，关 = 隐藏窗口（任务继续跑）。</summary>
        public static bool IsOpen
        {
            get { return Win != null && Win.IsVisible; }
        }

        /// <summary>
        /// 测试辅助：把多进程窗口的 HWND 写到 mpdp.txt。
        /// 自动化脚本（tools/mp-interact.ps1）靠它精确找到这个窗口，不用按标题猜。
        /// 找不到窗口 / 写文件失败都只是静默跳过，不影响正常使用。
        /// </summary>
        private static void DumpHwnd()
        {
            try
            {
                if (Win == null) return;
                System.Windows.Interop.WindowInteropHelper h =
                    new System.Windows.Interop.WindowInteropHelper(Win);
                if (h.Handle == IntPtr.Zero) return;
                System.IO.File.WriteAllText("mpdp.txt",
                    h.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public static void CloseWindow()
        {
            if (Win == null) return;
            Win.Hide();
            RefreshStatusButton();
        }

        /// <summary>还有任务在跑吗？（关主窗口时要据此决定是否停掉）</summary>
        public static bool AnyRunning()
        {
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t != null && t.Slot != null && t.Slot.IsRunning) return true;
            }
            return false;
        }

        public static int RunningCount()
        {
            int n = 0;
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t != null && (t.Running || (t.Slot != null && t.Slot.IsRunning))) n++;
            }
            return n;
        }

        public static void RunAll()
        {
            if (Win == null) return;
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t == null || t.Slot == null) continue;
                if (t.Slot.IsRunning) continue;
                if (t.Input == null) continue;
                string txt = t.Input.Text == null ? "" : t.Input.Text.Trim();
                if (txt.Length == 0) continue;   // 空行直接跳过，不打扰用户
                Win.RunTask(t);
            }
        }

        public static void StopAll()
        {
            if (Win == null) return;
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t == null || t.Slot == null) continue;
                if (t.Slot.IsRunning) Win.StopTask(t);
            }
        }

        public static void ClearAll()
        {
            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                if (t == null) continue;
                if (t.Slot != null && t.Slot.IsRunning)
                {
                    // 正在跑的任务内容别清：清了马上又被新输出填回来，看着像没生效
                    Write(t, Loc.T("[多进程] 这条任务正在运行，输出未清空。"), true);
                    continue;
                }
                if (t.ViewChunks != null) t.ViewChunks.Clear();
                t.OutSync = -1;
                t.NeedsRebuild = true;
                if (t.OutputView != null)
                {
                    t.OutputView.Box.Clear();
                    t.OutSync = 0;
                    t.NeedsRebuild = false;
                }
                if (t.OutputWindow != null)
                {
                    t.OutputWindow.View.Box.Clear();
                    t.OutSync = 0;
                    t.NeedsRebuild = false;
                }
            }
            if (Win != null) Win.RefreshAllTabs();
        }

        // ==================================================================
        //  标签相关的动作
        // ==================================================================

        /// <summary>选中某条任务：合并模式下切标签，独立窗口模式下把那个窗口激活。</summary>
        public static void Activate(MultiProcTask t)
        {
            if (t == null) return;
            Selected = t;
            if (Win != null && Win.IsVisible)
            {
                if (t.OutputWindow != null)
                {
                    // 已经有独立窗口了：直接把它拉到前面
                    if (Win.WindowState != WindowState.Minimized)
                    {
                        if (t.OutputWindow.WindowState == WindowState.Minimized)
                            t.OutputWindow.WindowState = WindowState.Normal;
                        t.OutputWindow.Activate();
                    }
                    Win.ShowHostFor(t);
                }
                else
                {
                    Win.ShowHostFor(t);
                }
            }
            else
            {
                Open(false);
                if (Win != null) Win.ShowHostFor(t);
            }
            if (Win != null) Win.RefreshAllTabs();
        }

        /// <summary>把一条任务的输出弹出成独立窗口。</summary>
        public static MultiProcOutputView PopOut(MultiProcTask t)
        {
            if (t == null) return null;
            if (t.OutputWindow != null)
            {
                if (t.OutputWindow.WindowState == WindowState.Minimized)
                    t.OutputWindow.WindowState = WindowState.Normal;
                t.OutputWindow.Activate();
                return t.OutputWindow.View;
            }
            if (t.OutputView != null) t.OutputView.Detach();
            t.OutputView = null;
            t.OutSync = -1;

            OutputWindow w = new OutputWindow(t);
            MultiProcOutputView.RebuildView(t, w.View);
            w.Show();
            t.OutputWindow = w;
            if (Win != null)
            {
                Win.RefreshAllTabs();
                Win.RebuildInlineOutput();
            }
            RefreshStatusButton();
            return w.View;
        }

        /// <summary>把一条任务的输出合并回总窗口的标签页。</summary>
        public static void MergeTask(MultiProcTask t)
        {
            if (t == null) return;
            if (t.OutputWindow != null)
            {
                OutputWindow w = t.OutputWindow;
                t.OutputWindow = null;
                w.CloseForRemoval();
            }
            Open(false);
            Selected = t;
            if (Win != null)
            {
                Win.ShowHostFor(t);
                Rebind(t, true);
                Win.RebuildInlineOutput();
                Win.RefreshAllTabs();
            }
            RefreshStatusButton();
        }

        /// <summary>标签的兜底右键菜单（拖拽手势不好发现，这里给一份能点的）。</summary>
        public static ContextMenu TabMenu(MultiProcTask t)
        {
            if (t == null) return null;
            if (_tabMenu == null) _tabMenu = new ContextMenu();

            ContextMenu m = new ContextMenu();
            MenuItem head = new MenuItem();
            head.Header = TabLabel(t);
            head.IsEnabled = false;
            m.Items.Add(head);
            m.Items.Add(new Separator());

            if (t.OutputWindow == null)
            {
                MenuItem pop = new MenuItem();
                pop.Header = Loc.T("弹出为独立窗口");
                pop.Click += delegate { PopOut(t); };
                m.Items.Add(pop);

                MenuItem merge = new MenuItem();
                merge.Header = Loc.T("合并回标签");
                merge.IsEnabled = false;   // 本来就在标签里
                m.Items.Add(merge);
            }
            else
            {
                MenuItem pop = new MenuItem();
                pop.Header = Loc.T("弹出为独立窗口");
                pop.IsEnabled = false;
                m.Items.Add(pop);

                MenuItem merge = new MenuItem();
                merge.Header = Loc.T("合并回标签");
                merge.Click += delegate { MergeTask(t); };
                m.Items.Add(merge);

                MenuItem close = new MenuItem();
                close.Header = Loc.T("关闭这个输出窗口");
                close.Click += delegate
                {
                    OutputWindow w = t.OutputWindow;
                    t.OutputWindow = null;
                    if (w != null) w.CloseForRemoval();
                    Rebind(t, true);
                    if (Win != null)
                    {
                        Win.RebuildInlineOutput();
                        Win.RefreshAllTabs();
                    }
                };
                m.Items.Add(close);
            }

            m.Items.Add(new Separator());
            MenuItem run = new MenuItem();
            run.Header = t.Running ? Loc.T("停止这条任务") : Loc.T("运行这条任务");
            run.Click += delegate
            {
                if (Win != null) Win.ToggleTask(t);
            };
            run.IsEnabled = Win != null;
            m.Items.Add(run);

            return m;
        }

        // ==================================================================
        //  主界面上的状态按钮
        // ==================================================================

        private static Button _statusButton;
        private static TextBlock _statusCount;
        private static StackPanel _statusStack;
        private static string _statusIconBrush = "";

        /// <summary>主界面提示行上的状态按钮：悬浮看运行数，点开看各任务状态。</summary>
        internal static UIElement BuildStatusButton()
        {
            EnsureTimer();
            Button b = new Button();
            b.SetResourceReference(FrameworkElement.StyleProperty, "ChipButton");
            b.Height = 28;
            b.Padding = new Thickness(8, 0, 10, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Click += delegate { OpenStatusMenu(b); };

            StackPanel sp = Ui.H();
            sp.Children.Add(StatusIcon("TextFaintBrush"));
            _statusStack = sp;

            TextBlock n = Ui.Text("", 11.5, "TextDimBrush", FontWeights.SemiBold);
            n.VerticalAlignment = VerticalAlignment.Center;
            n.Margin = new Thickness(6, 0, 0, 0);
            n.FontFamily = Fonts.Mono;
            sp.Children.Add(n);

            b.Content = sp;
            _statusButton = b;
            _statusCount = n;
            _statusIconBrush = "";
            RefreshStatusButton();
            return b;
        }

        private static UIElement StatusIcon(string brushKey)
        {
            UIElement ic = Icons.Create("taskStack", 14, brushKey, 1.5);
            ic.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            return ic;
        }

        /// <summary>刷新状态按钮（运行数 / 提示 / 灰化）。窗口重建时会自动挂到新按钮上。</summary>
        internal static void RefreshStatusButton()
        {
            // 每次 BuildStatusButton 都新建按钮，这里只更新当前那个。
            // 注意不能对同一个 Children 索引做替换赋值（set_Item）—— 会抛
            // 「指定的索引已经在使用」，而且定时器里抛就是启动即崩。
            if (_statusButton == null || _statusStack == null) return;
            int running = RunningCount();
            int total = Tasks.Count;

            if (_statusCount != null)
                _statusCount.Text = total == 0 ? "" : running.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string brush = running > 0 ? "SuccessBrush" : "TextDimBrush";
            _statusButton.SetResourceReference(Control.ForegroundProperty, brush);

            string tip;
            if (total == 0)
                tip = Loc.T("多进程：还没有任务行。点这里打开多进程窗口，可以同时跑多条命令。");
            else if (running == 0)
                tip = Loc.T("多进程：当前没有任务在运行（共 ") + total
                    + Loc.T(" 条任务行）。点这里查看各任务状态。");
            else
                tip = Loc.T("多进程：正在运行 ") + running + Loc.T(" 条任务（共 ") + total
                    + Loc.T(" 条）。点这里查看各任务状态。");
            _statusButton.ToolTip = tip;

            // 图标跟着状态走：没任务在跑就整块灰掉。
            // 换图标必须 replace 掉原来那个元素 —— 直接给 Children[0] 赋值会因为
            // 「该索引已被占用」抛 ArgumentException（这个坑真踩过，而且是在
            // DispatcherTimer 的 tick 里抛，表现为启动即崩）。状态没变就不动。
            string iconBrush = running > 0 ? "SuccessBrush" : "TextFaintBrush";
            if (!string.Equals(iconBrush, _statusIconBrush, StringComparison.Ordinal))
            {
                _statusIconBrush = iconBrush;
                if (_statusStack.Children.Count > 0) _statusStack.Children.RemoveAt(0);
                _statusStack.Children.Insert(0, StatusIcon(iconBrush));
            }
        }

        /// <summary>把状态按钮挂到当前主窗口实例上（重建界面后会换成新按钮）。</summary>
        private static void OpenStatusMenu(Button anchor)
        {
            if (Tasks.Count == 0)
            {
                Open(false);
                return;
            }
            ContextMenu m = new ContextMenu();
            m.PlacementTarget = anchor;
            m.Placement = PlacementMode.Bottom;

            MenuItem head = new MenuItem();
            head.Header = Loc.T("多进程任务（按创建顺序）");
            head.IsEnabled = false;
            m.Items.Add(head);
            m.Items.Add(new Separator());

            for (int i = 0; i < Tasks.Count; i++)
            {
                MultiProcTask t = Tasks[i];
                MenuItem mi = new MenuItem();

                StackPanel row = Ui.H();
                Ellipse dot = new Ellipse();
                dot.Width = 8; dot.Height = 8;
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Margin = new Thickness(0, 0, 0, 0);
                dot.SetResourceReference(Shape.FillProperty, BrushOf(TintOf(t)));
                row.Children.Add(dot);

                string cmd = Summarize(t.LastCmd);
                if (cmd.Length == 0) cmd = Loc.T("（未提交命令）");
                TextBlock txt = Ui.Text(
                    t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + cmd
                    + "   · " + StatusText(t), 12, "TextBrush");
                txt.VerticalAlignment = VerticalAlignment.Center;
                txt.Margin = new Thickness(8, 0, 0, 0);
                txt.MaxWidth = 420;
                txt.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(txt);
                mi.Header = row;

                MultiProcTask captured = t;
                mi.Click += delegate { Activate(captured); };
                m.Items.Add(mi);
            }

            m.Items.Add(new Separator());
            MenuItem openWin = new MenuItem();
            openWin.Header = Loc.T("打开多进程窗口");
            openWin.Click += delegate { Open(false); };
            m.Items.Add(openWin);

            MenuItem runAll = new MenuItem();
            runAll.Header = Loc.T("全部运行");
            runAll.Click += delegate { RunAll(); };
            m.Items.Add(runAll);

            MenuItem stopAll = new MenuItem();
            stopAll.Header = Loc.T("全部停止");
            stopAll.IsEnabled = RunningCount() > 0;
            stopAll.Click += delegate { StopAll(); };
            m.Items.Add(stopAll);

            m.IsOpen = true;
        }
    }

    // =======================================================================
    //  多进程窗口
    // =======================================================================
    internal sealed class MultiProcWindow : Window
    {
        /// <summary>
        /// 任务行数的编译期硬上限。Exec.cs 的 CommandExecutor.SlotCount 就是用它算出来的
        /// （1 + MaxRows），所以这里改大，槽位数会跟着变大，不需要改别的地方。
        /// 取 16 的理由：设置里「任务行数上限」下拉的最大档位就是 16。
        /// </summary>
        public const int MaxRows = 16;
        private readonly MainWindow _owner;
        private readonly List<MultiProcTask> _tasks = MultiProc.Tasks;

        // ---- 窗口自己的控制台模式（打开时跟随主窗口，窗口内可切换） ----
        /// <summary>这个窗口默认用哪个控制台；每行可以单独覆盖（MultiProcTask.RowShell）。</summary>
        private ShellKind _shell = ShellKind.PowerShell;
        private Button _shellCmdBtn;
        private Button _shellPsBtn;

        // ---- 顶部工具条上的文字（换语言时要重刷） ----
        private TextBlock _title;
        private TextBlock _sub;
        private TextBlock _rowInfo;
        private Button _addBtn;
        private Button _delBtn;
        private Button _runAllBtn;
        private Button _stopAllBtn;
        private Button _clearBtn;
        private Button _maxBtn;

        // ---- 中部 ----
        /// <summary>最后聚焦过的任务行（命令库往哪一行的输入框里填）。</summary>
        private MultiProcTask _focusTask;

        /// <summary>当前正在编辑的任务行：优先最后聚焦的那行，否则当前选中的标签行。</summary>
        private MultiProcTask EditTask()
        {
            if (_focusTask != null && _tasks.Contains(_focusTask) && _focusTask.Input != null) return _focusTask;
            MultiProcTask t = MultiProc.Selected;
            if (t != null && t.Input != null) return t;
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i].Input != null) return _tasks[i];
            }
            return null;
        }

        /// <summary>命令库往哪一行的输入框里填：当前编辑行（最后聚焦的那一行）。</summary>
        private TextBox ActiveInputBox()
        {
            MultiProcTask t = EditTask();
            return t == null ? null : t.Input;
        }
        private TextBlock _rowPrompt;

        private ScrollViewer _rowScroll;
        private StackPanel _rowHost;
        private MultiProcTabStrip _strip;
        private Grid _midGrid;
        private ListBox _suggestList;
        private Border _suggestPanel;
        private StackPanel _suggestHost;
        private bool _suggestVisible;
        private int _suggestNav = -1;
        private SuggestResult _lastSuggest;

        // ---- 跨工具候选（「(切换到 CMD) ping」）点了之后的待执行行 ----
        /// <summary>刚被跨工具候选切过控制台的那一行（双击时要跑的就是它）。</summary>
        private MultiProcTask _crossPendingTask;
        private DateTime _crossPendingAt = DateTime.MinValue;
        /// <summary>双击判定窗口：WPF 双击事件之间只隔几十毫秒，700ms 足够且不会误伤。</summary>
        private const double CrossPendingMs = 700;

        // ---- 输出 ----
        private Grid _outGrid;
        private Border _outShell;
        private TextBlock _outHint;
        private readonly Dictionary<MultiProcTask, MultiProcOutputView> _hosts =
            new Dictionary<MultiProcTask, MultiProcOutputView>();

        // ---- 左右两栏的宽度（主题里「自由控件宽度」打开时可以拖分隔条） ----
        private ColumnDefinition _colLib;
        private ColumnDefinition _colRight;
        private GridSplitter _splitLib;
        private GridSplitter _splitRight;
        /// <summary>正在拖分隔条：此时别去同步宽度，否则会跟 GridSplitter 打架。</summary>
        private bool _splitDragging;
        private bool _appliedFree;
        private double _appliedLibW = -1;
        private double _appliedRightW = -1;

        // ---- 任务行区 / 输出区之间的横向分隔条（拖动改输出区高度） ----
        private GridSplitter _splitOut;
        private bool _outSplitDragging;
        /// <summary>输出区高度下限（像素）：再矮就看不到几行输出了。</summary>
        private const double OutMinHeight = 120;
        /// <summary>从没设过高度时的初始值，约等于以前 1:1 分给人的那个观感。</summary>
        private const double OutDefaultHeight = 300;
        /// <summary>分隔条自己占的高度（和 GridSplitter.Height 保持一致）。</summary>
        private const double OutSplitterHeight = 7;
        /// <summary>任务行区的保底高度（和 RowDefinitions[1].MinHeight 保持一致）。</summary>
        private const double RowAreaMinHeight = 170;
        /// <summary>
        /// 输出区高度。存在设置里（多进程窗口自己的一份，和主窗口无关），
        /// 0 = 还没设过 → OutDefaultHeight。
        /// 注意别和 Settings.MultiProcHeight 混：那个是**多进程窗口的整体高度**
        /// （设置窗口里的「多进程窗口高度」滑块 180..600 → 窗口高 MultiProcHeight+400），
        /// 各管各的，互不覆盖。
        /// </summary>
        private double MpOutHeightSetting
        {
            get { return _owner == null ? 0 : _owner.Settings.MpOutputHeight; }
            set { if (_owner != null) _owner.Settings.MpOutputHeight = value; }
        }

        // ---- 左：命令库 ----
        private TextBox _libSearch;
        private ListBox _libList;
        private TextBlock _libInfo;
        /// <summary>分类筛选 chip（常用 / 收藏 / 历史 + 各命令分类），和主窗口左栏同一个东西。</summary>
        private ListBox _catList;
        /// <summary>和 _catList 一一对应的分类原始键（Loc.T 之后的显示名，比对时用它）。</summary>
        private readonly List<string> _catKeys = new List<string>();
        /// <summary>这排 chip 是按哪种语言建出来的（换语言要重列，否则分类名对不上）。</summary>
        private Lang _catLang;

        // ---- 右：参数提示 / 参数表单 / 命令详情（三个页签，和主窗口一致） ----
        private TabControl _rightTabs;
        private TextBlock _rightTitle;
        private TextBlock _rightBody;
        private TextBlock _rightCmd;
        /// <summary>命令名下面那行（标题 · 分类 · 来源），规格对齐主窗口「命令详情」的副标题。</summary>
        private TextBlock _rightSub;
        /// <summary>提示引擎算出来的「当前位置」文案（和主窗口右侧一致）。</summary>
        private TextBlock _rightHint;
        private StackPanel _rightParams;

        /// <summary>「参数表单」页的宿主（和主窗口 _formHost 同规格）。</summary>
        private StackPanel _formHost;
        /// <summary>表单底部那个「将执行的命令」预览框。</summary>
        private TextBox _formPreview;
        private readonly List<FormField> _formFields = new List<FormField>();
        private readonly List<FrameworkElement> _formEditors = new List<FrameworkElement>();
        /// <summary>用户在表单里改过东西：此时别被提示引擎自动重算的 spec 覆盖掉。</summary>
        private bool _formDirty;
        /// <summary>表单当前对应的命令（用来判断要不要重建）。</summary>
        private CmdSpec _formSpec;

        /// <summary>「命令详情」页的宿主。</summary>
        private StackPanel _detailHost;

        private bool _suppressSuggest;
        private bool _closingToHide;

        public MultiProcWindow(MainWindow owner)
        {
            _owner = owner;
            // 打开那一刻默认跟随主窗口的模式（PS / CMD）；窗口内还能再切。
            _shell = owner == null ? ShellKind.PowerShell : owner.EffectiveShell;
            Title = Loc.T("多进程执行");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 1000;
            MinHeight = 520;
            Width = 1220;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Fonts.Ui;
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = 44;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            BuildUi();
            ApplyPanelWidths();

            SourceInitialized += delegate { WindowFx.ApplyRoundedCorners(this, 10, !Themes.Current.IsLight); };
            StateChanged += delegate { RefreshMaxIcon(); };
            Closing += OnClosing;
            PreviewKeyDown += OnWindowKey;
            Themes.Changed += OnThemeChanged;
        }

        // ==================================================================
        //  界面构建
        // ==================================================================

        private void BuildUi()
        {
            Border shell = new Border();
            shell.BorderThickness = new Thickness(1);
            shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushHard");
            shell.SetResourceReference(Border.BackgroundProperty, "WindowBgBrush");

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions[0].Height = GridLength.Auto;
            root.RowDefinitions.Add(new RowDefinition());

            root.Children.Add(BuildTitleBar());
            Grid.SetRow(root.Children[0], 0);

            Grid body = new Grid();
            body.Margin = new Thickness(10, 8, 10, 8);
            _colLib = new ColumnDefinition();
            _colLib.Width = new GridLength(LibWidth());
            ColumnDefinition colGap1 = new ColumnDefinition();
            colGap1.Width = GridLength.Auto;
            ColumnDefinition colMain = new ColumnDefinition();
            colMain.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition colGap2 = new ColumnDefinition();
            colGap2.Width = GridLength.Auto;
            _colRight = new ColumnDefinition();
            _colRight.Width = new GridLength(RightWidth());
            body.ColumnDefinitions.Add(_colLib);
            body.ColumnDefinitions.Add(colGap1);
            body.ColumnDefinitions.Add(colMain);
            body.ColumnDefinitions.Add(colGap2);
            body.ColumnDefinitions.Add(_colRight);

            // 左：命令库
            // 面板底色 / 边框 / 圆角键和主窗口那两个侧栏（MainWindow 的 GlassPanel）完全一致：
            // GlassPanel 的 tint 层用的就是 SurfaceBrush + BorderBrushSoft + PanelCornerRadius。
            // 多进程窗口没有背景层（GlassPanel.Backdrop 是主窗口的静态背景），所以这里用
            // 普通 Border 套同样的三个资源键 —— 主题一换两边一起变。
            Border left = new Border();
            left.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            left.BorderThickness = new Thickness(1);
            left.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            left.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            left.Child = BuildLibrary();
            body.Children.Add(left);
            Grid.SetColumn(left, 0);

            _splitLib = MakeSplitter(true);
            body.Children.Add(_splitLib);
            Grid.SetColumn(_splitLib, 1);

            // 中：任务行 + 输出
            UIElement mid = BuildCenter();
            body.Children.Add(mid);
            Grid.SetColumn(mid, 2);

            _splitRight = MakeSplitter(false);
            body.Children.Add(_splitRight);
            Grid.SetColumn(_splitRight, 3);

            // 右：参数说明（面板规格同左栏，也和主窗口右侧的参数面板一致）
            Border right = new Border();
            right.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            right.BorderThickness = new Thickness(1);
            right.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            right.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            right.Child = BuildRightPanel();
            body.Children.Add(right);
            Grid.SetColumn(right, 4);

            root.Children.Add(body);
            Grid.SetRow(body, 1);

            shell.Child = root;
            Content = shell;
        }

        // ==================================================================
        //  左右两栏宽度：**多进程窗口自己的值**（和主窗口完全独立）
        //
        //  以前这里直接读写主窗口的 SidebarWidth / RightPanelWidth，
        //  于是拖多进程窗口的分隔条，主窗口也跟着变（用户报的「宽度串台」）。
        //  现在读写 Settings.MpSidebarWidth / MpRightPanelWidth：
        //    0 = 还没设过 → 取主窗口那一刻的宽度当初始值并写回，之后就各记各的。
        //  设置窗口里那两个滑块仍然只改主窗口，两边互不影响。
        // ==================================================================

        /// <summary>命令库栏宽度（下限 160，和主窗口一致）。0 = 还没设过 → 跟主窗口要初值。</summary>
        private double LibWidth()
        {
            if (_owner == null) return 268;
            double w = _owner.Settings.MpSidebarWidth;
            if (w <= 0)
            {
                w = _owner.Settings.SidebarWidth;     // 第一次打开：用主窗口当前的宽度
                if (w < 160) w = 160;
                _owner.Settings.MpSidebarWidth = w;   // 写回：初值只取这一次
            }
            return w < 160 ? 160 : w;
        }

        /// <summary>参数说明栏宽度（下限 220，和主窗口一致）。0 = 还没设过 → 跟主窗口要初值。</summary>
        private double RightWidth()
        {
            if (_owner == null) return 344;
            double w = _owner.Settings.MpRightPanelWidth;
            if (w <= 0)
            {
                w = _owner.Settings.RightPanelWidth;
                if (w < 220) w = 220;
                _owner.Settings.MpRightPanelWidth = w;
            }
            return w < 220 ? 220 : w;
        }

        private bool FreeWidth()
        {
            return _owner != null && _owner.Settings.FreePanelWidth;
        }

        /// <summary>
        /// 按设置应用左右两栏宽度：主题里「自由控件宽度」打开时显示分隔条可以拖，
        /// 关闭时隐藏分隔条、用固定宽度（写法参考主窗口 MainWindow.ApplyPanelWidths）。
        /// 宽度取的是多进程窗口自己的 MpSidebarWidth / MpRightPanelWidth（0 = 跟主窗口要初值）。
        /// </summary>
        public void ApplyPanelWidths()
        {
            try
            {
                bool free = FreeWidth();
                double lw = LibWidth();
                double rw = RightWidth();
                if (_colLib != null && !_splitDragging) _colLib.Width = new GridLength(lw);
                if (_colRight != null && !_splitDragging) _colRight.Width = new GridLength(rw);
                if (_splitLib != null) _splitLib.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
                if (_splitRight != null) _splitRight.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
                _appliedFree = free;
                _appliedLibW = lw;
                _appliedRightW = rw;
            }
            catch { }
        }

        /// <summary>
        /// 90ms 一轮的轻量同步：设置窗口里改了「自由控件宽度」时，多进程窗口没有接线也能立刻跟上。
        /// 注意：设置里那两个**宽度滑块只管主窗口**（写 SidebarWidth / RightPanelWidth），
        /// 多进程窗口读的是 Mp* 两个字段，所以这里不会因为拖主窗口的分隔条而跟着变。
        /// 只在值真的变了才动手，拖动分隔条期间完全不动。
        /// </summary>
        private void SyncPanelWidths()
        {
            if (_splitDragging) return;
            bool free = FreeWidth();
            double lw = LibWidth();
            double rw = RightWidth();
            if (free != _appliedFree || Math.Abs(lw - _appliedLibW) > 0.5 || Math.Abs(rw - _appliedRightW) > 0.5)
                ApplyPanelWidths();
        }

        /// <summary>
        /// 分隔条：拖动只改**多进程窗口自己**的宽度（MpSidebarWidth / MpRightPanelWidth），
        /// 松手后写回设置。这里坚决不碰主窗口的 SidebarWidth / RightPanelWidth，
        /// 也不调主窗口的 ApplyPanelWidths() —— 那正是「拖一个窗口另一个跟着动」的根源。
        /// </summary>
        private GridSplitter MakeSplitter(bool leftSide)
        {
            GridSplitter sp = new GridSplitter();
            sp.Width = 7;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Stretch;
            sp.Background = Brushes.Transparent;
            sp.ResizeDirection = GridResizeDirection.Columns;
            sp.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            sp.Cursor = Cursors.SizeWE;
            sp.Visibility = Visibility.Collapsed;
            sp.ToolTip = leftSide
                ? Loc.T("拖动可以改命令库栏的宽度（在设置里打开「自由控件宽度」后可用）")
                : Loc.T("拖动可以改参数提示栏的宽度（在设置里打开「自由控件宽度」后可用）");
            sp.DragStarted += delegate { _splitDragging = true; };
            sp.DragCompleted += delegate
            {
                _splitDragging = false;
                try
                {
                    if (_owner == null) return;
                    if (leftSide)
                    {
                        if (_colLib != null) _owner.Settings.MpSidebarWidth = Math.Max(160, _colLib.ActualWidth);
                    }
                    else
                    {
                        if (_colRight != null) _owner.Settings.MpRightPanelWidth = Math.Max(220, _colRight.ActualWidth);
                    }
                    _appliedLibW = LibWidth();
                    _appliedRightW = RightWidth();
                }
                catch { }
            };
            return sp;
        }

        private UIElement BuildTitleBar()
        {
            Border bar = new Border();
            bar.Height = 44;
            bar.Padding = new Thickness(14, 0, 8, 0);
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            bar.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            StackPanel left = Ui.H();
            left.VerticalAlignment = VerticalAlignment.Center;
            Border logo = new Border();
            logo.Width = 26; logo.Height = 26;
            logo.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            logo.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            logo.Child = Icons.Create("taskStack", 15, "OnAccentBrush", 1.7);
            left.Children.Add(logo);

            _title = Ui.Text(Loc.T("多进程执行"), 14, "TextBrush", FontWeights.SemiBold);
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Margin = new Thickness(9, 0, 0, 0);
            left.Children.Add(_title);

            _sub = Ui.Text(Loc.T("每条任务各起一个进程，输出分开显示，可单独停止"), 11.5, "TextFaintBrush");
            _sub.VerticalAlignment = VerticalAlignment.Center;
            _sub.Margin = new Thickness(10, 1, 0, 0);
            left.Children.Add(_sub);
            g.Children.Add(left);
            Grid.SetColumn(left, 0);

            // 中：行数 + 增删 + 运行/停止/清空
            StackPanel mid = Ui.H();
            mid.VerticalAlignment = VerticalAlignment.Center;
            mid.HorizontalAlignment = HorizontalAlignment.Left;
            mid.Margin = new Thickness(18, 0, 0, 0);
            WindowChrome.SetIsHitTestVisibleInChrome(mid, true);

            _addBtn = Ui.Btn(null, "IconButton", delegate { AddRow(); });
            _addBtn.Content = Icons.Create("plus", 15, "TextDimBrush", 1.8);
            _addBtn.Width = 28; _addBtn.Height = 28;
            _addBtn.ToolTip = Loc.T("增加一条任务行");
            mid.Children.Add(_addBtn);

            _delBtn = Ui.Btn(null, "IconButton", delegate { DelRow(); });
            _delBtn.Content = Icons.Create("minus", 15, "TextDimBrush", 1.8);
            _delBtn.Width = 28; _delBtn.Height = 28;
            _delBtn.Margin = new Thickness(4, 0, 0, 0);
            _delBtn.ToolTip = Loc.T("减少最后一条任务行（正在运行的那条不会被删掉）");
            mid.Children.Add(_delBtn);

            _rowInfo = Ui.Text("", 11.5, "TextFaintBrush");
            _rowInfo.VerticalAlignment = VerticalAlignment.Center;
            _rowInfo.Margin = new Thickness(10, 0, 0, 0);
            mid.Children.Add(_rowInfo);

            _runAllBtn = Ui.Btn(Loc.T("全部运行"), "SoftButton", delegate { MultiProc.RunAll(); });
            _runAllBtn.Height = 28;
            _runAllBtn.FontSize = 11.5;
            _runAllBtn.Padding = new Thickness(12, 0, 12, 0);
            _runAllBtn.Margin = new Thickness(14, 0, 0, 0);
            _runAllBtn.ToolTip = Loc.T("把所有填写了命令的任务一起跑起来");
            mid.Children.Add(_runAllBtn);

            _stopAllBtn = Ui.Btn(Loc.T("全部停止"), "GhostButton", delegate { MultiProc.StopAll(); });
            _stopAllBtn.Height = 28;
            _stopAllBtn.FontSize = 11.5;
            _stopAllBtn.Padding = new Thickness(12, 0, 12, 0);
            _stopAllBtn.Margin = new Thickness(6, 0, 0, 0);
            _stopAllBtn.ToolTip = Loc.T("停止全部正在运行的任务（连同各自的子进程）");
            mid.Children.Add(_stopAllBtn);

            _clearBtn = Ui.Btn(Loc.T("清空输出"), "GhostButton", delegate { MultiProc.ClearAll(); });
            _clearBtn.Height = 28;
            _clearBtn.FontSize = 11.5;
            _clearBtn.Padding = new Thickness(12, 0, 12, 0);
            _clearBtn.Margin = new Thickness(6, 0, 0, 0);
            _clearBtn.ToolTip = Loc.T("清空所有任务的输出区（正在运行的任务不会停止）");
            mid.Children.Add(_clearBtn);

            g.Children.Add(mid);
            Grid.SetColumn(mid, 1);

            // 右：窗口内的控制台切换 + 窗口按钮
            StackPanel right = Ui.H();
            right.VerticalAlignment = VerticalAlignment.Center;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            WindowChrome.SetIsHitTestVisibleInChrome(right, true);

            right.Children.Add(BuildShellSwitch());

            Button min = Ui.IconBtn("min", Loc.T("最小化"), delegate { WindowState = WindowState.Minimized; });
            min.Width = 32; min.Height = 28;
            right.Children.Add(min);

            Button max = Ui.IconBtn("max", Loc.T("最大化 / 还原"), delegate
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            });
            max.Width = 32; max.Height = 28;
            _maxBtn = max;
            right.Children.Add(max);

            Button close = Ui.IconBtn("close", Loc.T("关闭窗口（正在运行的任务会继续跑）"), delegate { Close(); });
            close.Width = 32; close.Height = 28;
            right.Children.Add(close);

            g.Children.Add(right);
            Grid.SetColumn(right, 2);

            bar.Child = g;
            bar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); }
                catch { }
            };
            return bar;
        }

        private static bool HasButtonAncestor(DependencyObject d)
        {
            int guard = 0;
            while (d != null && guard++ < 40)
            {
                if (d is Button) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        // ==================================================================
        //  窗口内的控制台切换（分段开关，样式对齐主窗口标题栏那个）
        // ==================================================================

        /// <summary>窗口内的控制台模式；每一行的默认值。</summary>
        public ShellKind WindowShell { get { return _shell; } }

        /// <summary>
        /// 打开窗口时把模式同步成主窗口当前的 EffectiveShell。
        /// 只同步「没有单独指定控制台」的行 —— 行上显式选过的保持不动。
        /// </summary>
        public void SyncShellFromHost()
        {
            if (_owner == null) return;
            SetWindowShell(_owner.EffectiveShell);
        }

        /// <summary>切换窗口的控制台模式：命令库（含分类）、参数提示、灰行提示符都跟着走。</summary>
        public void SetWindowShell(ShellKind shell)
        {
            bool changed = _shell != shell;
            _shell = shell;
            ApplyShellSegmentState();
            if (!changed) return;
            // 分类是按控制台分的（CMD 的分类和 PS 的分类不是同一批），换模式要重列
            PopulateCategories();
            FillLibrary();
            RefreshTexts();
            UpdateSuggestions();
        }

        /// <summary>
        /// 窗口内的 [CMD | PS] 分段开关。
        /// 视觉规格和主窗口标题栏那个（MainWindow.BuildShellSwitch / MakeSegment）**逐项对齐**：
        /// 圆角键 ControlCornerRadius、底色 SurfaceSunkenBrush、边框 BorderBrushSoft、内边距 3、
        /// 分段按钮 13/4 内边距 · FontSize 12.5 · MinWidth 54、选中 AccentBrush + OnAccentBrush + SemiBold、
        /// 未选中透明底 + TextDimBrush + Normal。文字短（CMD / PS）但控件规格一样，所以两边长得一样高一样宽。
        /// 想改规格请两边一起改，别只改这一处。
        /// </summary>
        private UIElement BuildShellSwitch()
        {
            Border outer = new Border();
            outer.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            outer.BorderThickness = new Thickness(1);
            outer.Padding = new Thickness(3);
            outer.VerticalAlignment = VerticalAlignment.Center;
            outer.Margin = new Thickness(0, 0, 8, 0);
            outer.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            outer.ToolTip = Loc.T("窗口里默认用哪个控制台执行（打开时跟随主窗口模式；每一行还能单独指定）");

            StackPanel seg = Ui.H();
            _shellCmdBtn = MakeShellSegment("CMD", delegate { SetWindowShell(ShellKind.Cmd); });
            _shellPsBtn = MakeShellSegment("PS", delegate { SetWindowShell(ShellKind.PowerShell); });
            _shellCmdBtn.ToolTip = Loc.T("窗口里的任务默认用 CMD 执行");
            _shellPsBtn.ToolTip = Loc.T("窗口里的任务默认用 PowerShell 执行");
            seg.Children.Add(_shellCmdBtn);
            seg.Children.Add(_shellPsBtn);
            outer.Child = seg;

            // 标题栏在 WindowChrome 里整块当标题栏用，不声明的话点了没反应
            WindowChrome.SetIsHitTestVisibleInChrome(outer, true);
            WindowChrome.SetIsHitTestVisibleInChrome(seg, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_shellCmdBtn, true);
            WindowChrome.SetIsHitTestVisibleInChrome(_shellPsBtn, true);
            ApplyShellSegmentState();
            return outer;
        }

        /// <summary>规格必须和 MainWindow.MakeSegment 一模一样（内边距 / 字号 / 最小宽度）。</summary>
        private static Button MakeShellSegment(string text, RoutedEventHandler onClick)
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

        private void ApplyShellSegmentState()
        {
            ApplyShellSegment(_shellCmdBtn, _shell == ShellKind.Cmd);
            ApplyShellSegment(_shellPsBtn, _shell == ShellKind.PowerShell);
        }

        /// <summary>
        /// 选中态 / 未选中态的画刷与字重，和 MainWindow.ApplySegmentState 完全一致。
        /// 未选中是**透明底**（不是 HoverOverlayBrush）：主窗口那边未选中的 CMD 只是一段灰字，
        /// 多进程窗口以前给它垫了一层灰底，所以两边看着不像同一个控件。
        /// </summary>
        private static void ApplyShellSegment(Button b, bool active)
        {
            if (b == null) return;
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

        /// <summary>这一行实际用哪个控制台：行上单独指定优先，否则窗口模式。</summary>
        private ShellKind ShellOfRow(MultiProcTask t)
        {
            if (t != null && t.RowShell.HasValue) return t.RowShell.Value;
            return _shell;
        }

        // ==================================================================
        //  中：任务行 + 输出
        // ==================================================================

        private UIElement BuildCenter()
        {
            _midGrid = new Grid();
            _midGrid.RowDefinitions.Add(new RowDefinition());
            _midGrid.RowDefinitions[0].Height = GridLength.Auto;
            _midGrid.RowDefinitions.Add(new RowDefinition());
            _midGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            _midGrid.RowDefinitions.Add(new RowDefinition());
            _midGrid.RowDefinitions[2].Height = GridLength.Auto;
            _midGrid.RowDefinitions.Add(new RowDefinition());
            // 输出区高度 = 设置里的 MpOutputHeight（下面的分隔条可以拖，拖完写回），
            // 任务行那一格吃剩下的空间。以前两格都是 1:1 的 Star，比例存不下来；
            // 输出区绝对不能是 Auto：里面的输出框一长就自己撑高，会把任务行挤没。
            _midGrid.RowDefinitions[3].Height = new GridLength(OutHeightNominal());

            // ---------- 当前编辑行 ----------
            Grid hint = new Grid();
            hint.Margin = new Thickness(2, 0, 2, 6);
            hint.ColumnDefinitions.Add(new ColumnDefinition());
            hint.ColumnDefinitions.Add(new ColumnDefinition());
            hint.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel hl = Ui.H();
            _rowPrompt = Ui.Text("", 11.5, "TextDimBrush", FontWeights.SemiBold);
            _rowPrompt.VerticalAlignment = VerticalAlignment.Center;
            hl.Children.Add(_rowPrompt);
            TextBlock note = Ui.Text(Loc.T("在某一行的输入框里敲命令时，右侧会同步提示它的参数"), 11, "TextFaintBrush");
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Margin = new Thickness(10, 0, 0, 0);
            hl.Children.Add(note);
            hint.Children.Add(hl);

            TextBlock hint2 = Ui.Text(Loc.T("Enter 运行  ·  Tab 补全  ·  ↑↓ 选候选"), 11, "TextFaintBrush");
            hint2.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint2, 1);
            hint.Children.Add(hint2);

            _midGrid.Children.Add(hint);
            Grid.SetRow(hint, 0);

            // ---------- 可滚动的任务行 ----------
            _rowScroll = new ScrollViewer();
            _rowScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _rowScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _rowScroll.Padding = new Thickness(0, 0, 4, 0);
            _rowHost = Ui.V();
            _rowScroll.Content = _rowHost;
            _midGrid.Children.Add(_rowScroll);
            Grid.SetRow(_rowScroll, 1);

            // ---------- 提示下拉（浮在任务行上面） ----------
            StackPanel host = new StackPanel();
            _suggestList = new ListBox();
            _suggestList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _suggestList.ItemContainerStyle = TryStyle("FlatListBoxItem");
            _suggestList.MaxHeight = 300;
            _suggestList.MouseDoubleClick += delegate { ApplySuggestion(_suggestList.SelectedIndex, true); };
            _suggestList.PreviewMouseLeftButtonUp += delegate { ApplySuggestion(_suggestList.SelectedIndex, false); };

            _suggestPanel = new Border();
            _suggestPanel.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            _suggestPanel.BorderThickness = new Thickness(1);
            _suggestPanel.Padding = new Thickness(4);
            _suggestPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceSolidBrush");
            _suggestPanel.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            Ui.AddSoftShadow(_suggestPanel, 0.20, 4, 20);
            _suggestPanel.Child = _suggestList;
            _suggestPanel.VerticalAlignment = VerticalAlignment.Top;
            _suggestPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _suggestPanel.MaxHeight = 320;
            _suggestPanel.Visibility = Visibility.Collapsed;
            host.Children.Add(_suggestPanel);
            host.VerticalAlignment = VerticalAlignment.Top;
            host.IsHitTestVisible = true;
            Panel.SetZIndex(host, 20);
            _suggestHost = host;
            _midGrid.Children.Add(host);
            // 提示下拉挂在「任务行区域」这一格里（纵向跨满任务行 + 输出区），
            // 而不是以前那个 0 高的专用行：0 高的格子会把子元素排成 0 高，
            // 下拉框永远看不见（Tab / ↑↓ 选候选也就跟着变成瞎选）。
            Grid.SetRow(host, 1);
            Grid.SetRowSpan(host, 3);
            _rowScroll.ScrollChanged += delegate
            {
                // 提示下拉跟着任务行一起滚，始终贴着当前输入框下沿
                PositionSuggest();
            };

            // ---------- 任务行与输出之间的分隔条（纵向拖动 = 改输出区高度） ----------
            GridSplitter sp = new GridSplitter();
            sp.Height = OutSplitterHeight;
            sp.HorizontalAlignment = HorizontalAlignment.Stretch;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Background = Brushes.Transparent;
            sp.ResizeDirection = GridResizeDirection.Rows;
            sp.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            sp.Cursor = Cursors.SizeNS;
            sp.ToolTip = Loc.T("上下拖动可以调整任务行区和下面输出区的高度（拖完会记住）");
            sp.DragStarted += delegate { _outSplitDragging = true; };
            sp.DragCompleted += delegate
            {
                _outSplitDragging = false;
                ApplyOutputHeight(true);   // 以拖完的实际高度为准：夹到上下限 + 写回设置
            };
            _splitOut = sp;
            _midGrid.Children.Add(sp);
            Grid.SetRow(sp, 2);

            // ---------- 输出区（合并模式 = 标签页） ----------
            BuildOutputArea();

            // 任务行最小高度：行数多的时候窗口内部滚动，不压缩每行的显示空间
            _rowScroll.MinHeight = 150;
            _midGrid.RowDefinitions[1].MinHeight = RowAreaMinHeight;
            _midGrid.RowDefinitions[3].MinHeight = OutMinHeight;

            // 窗口变大变小 / 行数变化导致可用高度变了：输出区跟着夹一次，
            // 免得窗口被拖矮之后输出区还占着老高的位置、或者被压成 0 高。
            _midGrid.SizeChanged += delegate { ApplyOutputHeight(false); };
            ApplyOutputHeight(false);

            return _midGrid;
        }

        // ==================================================================
        //  输出区高度（任务行区与输出区之间的那条分隔条）
        // ==================================================================

        /// <summary>设置里的输出区高度；0（还没设过）时用默认值。</summary>
        private double OutHeightNominal()
        {
            double h = MpOutHeightSetting;
            return h <= 0 ? OutDefaultHeight : h;
        }

        /// <summary>任务行区 + 输出区一共能用多高（拿不到布局尺寸时退回窗口高度估算）。</summary>
        private double OutHeightAvail()
        {
            double avail = 0;
            try { if (_midGrid != null) avail = _midGrid.ActualHeight; }
            catch { }
            if (avail <= 0) avail = ActualHeight > 0 ? ActualHeight - 84 : 600;  // 减去标题栏与上下留白
            return avail;
        }

        /// <summary>
        /// 输出区高度的上限：不超过可用高度的 70%，并且给任务行区留够 RowAreaMinHeight。
        /// 还要扣掉上面那行提示（Auto 高）和分隔条本身占的高度 —— 否则窗口被拖到最矮时
        /// 几行加起来会超出可见区域，输出区最下面一截会被裁掉。
        /// 两者取小的那个，再兜底到下限，保证不会算出「上限比下限还小」。
        /// </summary>
        private double OutHeightMax()
        {
            double avail = OutHeightAvail();
            double max = avail * 0.7;
            double head = 0;
            try
            {
                if (_midGrid != null && _midGrid.RowDefinitions.Count > 0)
                    head = _midGrid.RowDefinitions[0].ActualHeight;   // 第 0 行：当前行命令的提示行
            }
            catch { }
            double room = avail - RowAreaMinHeight - OutSplitterHeight - head;
            if (room < max) max = room;
            if (max < OutMinHeight) max = OutMinHeight;
            return max;
        }

        /// <summary>把高度夹进 [OutMinHeight, OutHeightMax()]。</summary>
        private double ClampOutHeight(double h)
        {
            if (h < OutMinHeight) h = OutMinHeight;
            double max = OutHeightMax();
            if (h > max) h = max;
            return h;
        }

        /// <summary>
        /// 把输出区高度落到行定义上。
        /// save = false：按设置值（窗口尺寸变化时也走这里，保证随窗口收放）。
        /// save = true ：按拖完的实际高度，夹好之后写回设置 —— 只有拖动结束才写。
        /// 落完之后任务行那一格恢复成 Star：它吃剩余空间，行数多时自己滚动，不挤压输出区。
        /// </summary>
        private void ApplyOutputHeight(bool save)
        {
            try
            {
                if (_midGrid == null || _midGrid.RowDefinitions.Count < 4) return;
                double h = save ? _midGrid.RowDefinitions[3].ActualHeight : OutHeightNominal();
                h = ClampOutHeight(h);
                if (save) MpOutHeightSetting = h;
                _midGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                if (!_outSplitDragging) _midGrid.RowDefinitions[3].Height = new GridLength(h);
            }
            catch { }
        }

        private void BuildOutputArea()
        {
            Grid outWrap = new Grid();
            outWrap.RowDefinitions.Add(new RowDefinition());
            outWrap.RowDefinitions[0].Height = GridLength.Auto;
            outWrap.RowDefinitions.Add(new RowDefinition());

            _strip = new MultiProcTabStrip();
            _strip.OnSelect = delegate (MultiProcTask t)
            {
                // 点标签 = 接下来多半在操作这一行：命令库也填到这一行
                _focusTask = t;
                MultiProc.Selected = t;
                ShowHostFor(t);
            };
            _strip.OnMoveRequested = delegate (MultiProcTask t, int target) { MoveTab(t, target); };
            // 标签条空白处右键也给一份菜单（标签本身各自带一份）
            FrameworkElement stripRoot = _strip.Root as FrameworkElement;
            if (stripRoot != null) stripRoot.ContextMenu = BuildStripMenu();
            outWrap.Children.Add(_strip.Root);            Grid.SetRow(_strip.Root, 0);

            _outShell = new Border();
            _outShell.BorderThickness = new Thickness(1);
            _outShell.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            _outShell.SetResourceReference(Border.BackgroundProperty, "ConsoleBgBrush");
            _outShell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");

            _outGrid = new Grid();
            _outShell.Child = _outGrid;

            _outHint = Ui.Text(Loc.T("在任务行里输入命令并运行，输出会显示在这里（每条任务一个标签页）。"), 12, "TextFaintBrush");
            _outHint.Margin = new Thickness(14, 12, 14, 12);
            _outHint.TextWrapping = TextWrapping.Wrap;
            _outGrid.Children.Add(_outHint);

            outWrap.Children.Add(_outShell);
            Grid.SetRow(_outShell, 1);

            _midGrid.Children.Add(outWrap);
            Grid.SetRow(outWrap, 3);
        }

        private ContextMenu BuildStripMenu()
        {
            ContextMenu m = new ContextMenu();
            MenuItem pop = new MenuItem();
            pop.Header = Loc.T("把当前标签弹出为独立窗口");
            pop.Click += delegate
            {
                MultiProcTask t = MultiProc.Selected;
                if (t != null) MultiProc.PopOut(t);
            };
            m.Items.Add(pop);

            MenuItem mergeAll = new MenuItem();
            mergeAll.Header = Loc.T("把所有输出合并回标签");
            mergeAll.Click += delegate { MergeAllBack(); };
            m.Items.Add(mergeAll);

            m.Items.Add(new Separator());
            MenuItem closeAll = new MenuItem();
            closeAll.Header = Loc.T("关闭所有独立输出窗口");
            closeAll.Click += delegate { CloseAllOutputWindows(); };
            m.Items.Add(closeAll);
            return m;
        }

        // ==================================================================
        //  左：命令库
        // ==================================================================

        private UIElement BuildLibrary()
        {
            Grid g = new Grid();
            // 内边距和主窗口左侧栏一样是 (10,10,8,10)：栏目宽度相同时，
            // 两边内容区的左右起点与可用宽度就完全相同，不会一边宽一边窄。
            g.Margin = new Thickness(10, 10, 8, 10);
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[0].Height = GridLength.Auto;
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions[1].Height = GridLength.Auto;
            g.RowDefinitions.Add(new RowDefinition());

            StackPanel head = Ui.V();
            Grid headRow = new Grid();
            headRow.ColumnDefinitions.Add(new ColumnDefinition());
            headRow.ColumnDefinitions.Add(new ColumnDefinition());
            headRow.ColumnDefinitions[1].Width = GridLength.Auto;
            StackPanel tb = Ui.H();
            // 标题规格对齐主窗口 BuildSidebar：图标 15 / 1.6，文字 13 SemiBold TextBrush
            tb.Children.Add(Icons.Create("book", 15, "AccentBrush", 1.6));
            TextBlock tl = Ui.Text(Loc.T("命令库"), 13, "TextBrush", FontWeights.SemiBold);
            tl.TextTrimming = TextTrimming.CharacterEllipsis;
            tl.Margin = new Thickness(7, 0, 0, 0);
            tl.VerticalAlignment = VerticalAlignment.Center;
            tb.Children.Add(tl);
            headRow.Children.Add(tb);

            _libInfo = Ui.Text("", 11, "TextFaintBrush");
            _libInfo.VerticalAlignment = VerticalAlignment.Center;
            _libInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_libInfo, 1);
            headRow.Children.Add(_libInfo);
            head.Children.Add(headRow);

            // ---- 分类筛选 chip：和主窗口左栏 BuildSidebar 里那排完全一致 ----
            // 常用 / 收藏 / 历史 + 当前控制台的各命令分类；点一下就只列这一类。
            // 视觉规格（FlatListBox + ChipItem + WrapPanel 换行 + 无纵向滚动条 +
            // 外边距 0,10,0,6）逐项照抄主窗口，选中态由 ChipItem 样式统一提供。
            _catList = new ListBox();
            _catList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _catList.ItemContainerStyle = TryStyle("ChipItem");
            _catList.SelectionChanged += delegate { OnCategoryChanged(); };
            _catList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            _catList.Background = Brushes.Transparent;
            _catList.Margin = new Thickness(0, 10, 0, 6);
            ScrollViewer.SetVerticalScrollBarVisibility(_catList, ScrollBarVisibility.Disabled);
            head.Children.Add(_catList);
            head.Children.Add(Ui.HairLine());

            Grid searchHost = new Grid();
            searchHost.Margin = new Thickness(0, 8, 0, 4);
            _libSearch = new TextBox();
            // 输入框规格对齐主窗口标题栏那个全局搜索框：高 32 / 内边距 (32,6,10,6) / 12.5 号
            _libSearch.Height = 32;
            _libSearch.Padding = new Thickness(32, 6, 10, 6);
            _libSearch.FontSize = 12.5;
            _libSearch.TextChanged += delegate { FillLibrary(); };
            searchHost.Children.Add(_libSearch);
            Grid iconHost = new Grid();
            iconHost.Width = 30;
            iconHost.HorizontalAlignment = HorizontalAlignment.Left;
            iconHost.IsHitTestVisible = false;
            iconHost.Children.Add(Icons.Create("search", 14, "TextFaintBrush", 1.7));
            searchHost.Children.Add(iconHost);
            TextBlock ph = Ui.Text(Loc.T("搜索命令…"), 12.5, "TextFaintBrush");
            ph.Margin = new Thickness(32, 0, 0, 0);
            ph.VerticalAlignment = VerticalAlignment.Center;
            ph.IsHitTestVisible = false;
            searchHost.Children.Add(ph);
            _libSearch.TextChanged += delegate
            {
                ph.Visibility = string.IsNullOrEmpty(_libSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            head.Children.Add(searchHost);
            g.Children.Add(head);
            Grid.SetRow(head, 0);

            _libList = new ListBox();
            _libList.SetResourceReference(FrameworkElement.StyleProperty, "FlatListBox");
            _libList.ItemContainerStyle = TryStyle("CardItem");
            _libList.Margin = new Thickness(0, 4, 0, 0);
            _libList.SelectionChanged += delegate
            {
                if (_libList.SelectedIndex < 0) return;
                FrameworkElement fe = _libList.SelectedItem as FrameworkElement;
                CmdSpec s = fe == null ? null : fe.Tag as CmdSpec;
                if (s == null) return;
                // 右栏「参数表单 / 命令详情」两个页签跟着这条命令走
                SetSpecTabs(s);
                TextBox target = ActiveInputBox();
                if (target != null)
                {
                    // 追加而不是覆盖：用户已经在输入框里写了半条命令时，点命令库不该把它清掉
                    string cur = target.Text ?? "";
                    int insertAt = target.CaretIndex;
                    if (insertAt < 0) insertAt = 0;
                    if (insertAt > cur.Length) insertAt = cur.Length;
                    _suppressSuggest = true;
                    target.Text = cur.Substring(0, insertAt) + s.Name + " " + cur.Substring(insertAt);
                    _suppressSuggest = false;
                    target.CaretIndex = insertAt + s.Name.Length + 1;
                    target.Focus();
                    UpdateSuggestions();
                }
                else
                {
                    // 一条任务行都没有（理论上不会）：至少把说明显示在右栏
                    ShowSpec(s);
                }
            };            g.Children.Add(_libList);
            Grid.SetRow(_libList, 2);

            PopulateCategories();
            FillLibrary();
            return g;
        }

        // ==================================================================
        //  左栏的分类筛选 chip（行为对齐主窗口 MainPanels：PopulateCategories /
        //  AddCategoryChip / OnCategoryChanged / RefreshCommandList）
        // ==================================================================

        /// <summary>重列分类 chip：常用 / 收藏 / 历史 + 当前控制台自己的分类。</summary>
        private void PopulateCategories()
        {
            if (_catList == null || _owner == null) return;
            _catKeys.Clear();
            _catList.Items.Clear();
            _catLang = Loc.Effective;

            AddCategoryChip(Loc.T("常用"), "lightning");
            AddCategoryChip(Loc.T("收藏"), "star");
            AddCategoryChip(Loc.T("历史"), "history");

            // 只列当前控制台（窗口模式）的分类，和主窗口按 Shell 过滤一致
            foreach (CmdCategory c in _owner.Lib.Categories)
            {
                if (c.Shell != _shell) continue;
                AddCategoryChip(c.Name, c.Icon);
            }

            if (_catList.Items.Count > 0) _catList.SelectedIndex = 0;
        }

        /// <summary>一枚分类 chip：图标 12 / 文字 12，和主窗口 AddCategoryChip 同规格。</summary>
        private void AddCategoryChip(string rawName, string icon)
        {
            if (_catList == null) return;
            _catKeys.Add(rawName);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel sp = Ui.H();
            Viewbox vb = Icons.Create(icon, 12, "TextDimBrush", 1.6) as Viewbox;
            if (vb != null) sp.Children.Add(vb);
            TextBlock t = Ui.Text(" " + MainWindow.CatDisplayName(rawName), 12, null);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(5, 0, 0, 0);
            sp.Children.Add(t);
            g.Children.Add(sp);

            _catList.Items.Add(g);
        }

        /// <summary>换分类了：有搜索词就先清掉搜索（清空会触发列表刷新），否则直接重列。</summary>
        private void OnCategoryChanged()
        {
            if (_catList == null || _catList.SelectedIndex < 0) return;
            if (_libSearch != null && (_libSearch.Text ?? "").Trim().Length > 0)
            {
                _libSearch.Text = "";
                return;
            }
            FillLibrary();
        }

        /// <summary>当前选中的分类键（没选中时退回「常用」）。</summary>
        private string CurrentCategoryKey()
        {
            if (_catList == null || _catList.SelectedIndex < 0 || _catList.SelectedIndex >= _catKeys.Count)
                return Loc.T("常用");
            return _catKeys[_catList.SelectedIndex];
        }

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

        /// <summary>
        /// 按「搜索词 + 当前分类」重列命令库。行为对齐主窗口 MainPanels.RefreshCommandList：
        /// 有搜索词就跨分类搜；没搜索词才看分类（常用 / 收藏 / 历史 / 具体某个分类）。
        /// </summary>
        private void FillLibrary()
        {
            if (_libList == null || _owner == null) return;
            _libList.Items.Clear();

            string q = _libSearch == null ? "" : (_libSearch.Text ?? "").Trim();
            string key = CurrentCategoryKey();
            int n = 0;

            // ---- 搜索优先：无视分类，在当前控制台的命令库里搜 ----
            if (q.Length > 0)
            {
                List<CmdSpec> hits = _owner.Lib.Search(_shell, q, 120);
                for (int i = 0; i < hits.Count; i++)
                {
                    _libList.Items.Add(BuildLibraryItem(hits[i]));
                    n++;
                }
                if (_libInfo != null)
                    _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" 条匹配");
                if (n == 0)
                    AddLibraryHint(Loc.T("当前 ") + Shells.Display(_shell) + Loc.T(" 命令库里没有匹配的命令。"));
                return;
            }

            // ---- 常用：常用 CMD / PowerShell 命令各一份清单 ----
            if (key == Loc.T("常用"))
            {
                string[] names = _shell == ShellKind.Cmd ? CommonNames : CommonPsNames;
                for (int i = 0; i < names.Length && n < 80; i++)
                {
                    CmdSpec s = _owner.Lib.Find(_shell, names[i]);
                    if (s == null) continue;
                    _libList.Items.Add(BuildLibraryItem(s));
                    n++;
                }
                if (_libInfo != null)
                    _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" 条");
                if (n == 0)
                    AddLibraryHint(Loc.T("当前 ") + Shells.Display(_shell) + Loc.T(" 命令库里还没有常用命令。"));
                return;
            }

            // ---- 收藏：点一条就填进当前编辑行的输入框 ----
            if (key == Loc.T("收藏"))
            {
                List<string> favs = _owner.History == null ? null : _owner.History.Favorites;
                if (favs != null)
                {
                    for (int i = 0; i < favs.Count; i++)
                    {
                        AddLibraryCommandCard(favs[i], Loc.T("点击填入当前行的命令行"), "star");
                        n++;
                    }
                }
                if (_libInfo != null)
                    _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" 条");
                if (n == 0) AddLibraryHint(Loc.T("还没有收藏。在历史记录里点 ☆ 可以收藏常用命令。"));
                return;
            }

            // ---- 历史：最近跑过的命令（带收藏按钮） ----
            if (key == Loc.T("历史"))
            {
                List<HistoryEntry> recent = _owner.History == null ? null : _owner.History.Recent(60, "");
                if (recent != null)
                {
                    for (int i = 0; i < recent.Count; i++)
                    {
                        AddLibraryHistoryCard(recent[i]);
                        n++;
                    }
                }
                if (_libInfo != null)
                    _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" 条");
                if (n == 0) AddLibraryHint(Loc.T("还没有执行过命令。"));
                return;
            }

            // ---- 具体某个分类：只列这一类里的命令 ----
            foreach (CmdCategory c in _owner.Lib.Categories)
            {
                if (c.Shell != _shell) continue;
                if (!string.Equals(c.Name, key, StringComparison.Ordinal)) continue;
                for (int i = 0; i < c.Commands.Count; i++)
                {
                    _libList.Items.Add(BuildLibraryItem(c.Commands[i]));
                    n++;
                }
                if (_libInfo != null)
                    _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + Loc.T(" 条 · ") + MainWindow.CatSource(c.Name);
                return;
            }

            if (_libInfo != null)
                _libInfo.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Loc.T(" 条");
        }

        /// <summary>分类下没内容时的一行灰字提示（对齐主窗口 AddEmptyHint）。</summary>
        private void AddLibraryHint(string text)
        {
            TextBlock t = Ui.Wrap(text, 12, "TextFaintBrush");
            t.Margin = new Thickness(10, 14, 10, 10);
            _libList.Items.Add(t);
        }

        /// <summary>
        /// 「收藏」等分类里的简单卡片：整行可点，点了把命令填进当前编辑行。
        /// 版式对齐主窗口 AddSpecialCard / BuildSimpleCard（图标 15 + 12.5 等宽文字 + 说明）。
        /// 注意 Tag 放的是命令原文（不是 CmdSpec），所以选中处理里必须判类型。
        /// </summary>
        private void AddLibraryCommandCard(string command, string desc, string icon)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());

            g.Children.Add(Icons.Create(icon, 15, "TextFaintBrush", 1.6));
            StackPanel sp = Ui.V();
            sp.Margin = new Thickness(9, 0, 0, 0);
            TextBlock t = Ui.Text(command, 12.5, "TextBrush");
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
            wrap.Tag = command;
            string captured = command;
            wrap.MouseLeftButtonUp += delegate { FillRowInput(captured); };
            _libList.Items.Add(wrap);
        }

        /// <summary>「历史」分类里的一条：命令 + 时间 + 收藏星标（对齐主窗口 AddHistoryCard）。</summary>
        private void AddLibraryHistoryCard(HistoryEntry e)
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
            HistoryEntry captured = e;
            star.Click += delegate
            {
                if (_owner == null || _owner.History == null) return;
                _owner.History.ToggleFavorite(captured.Command);
                _owner.History.Save();
                FillLibrary();
            };
            Grid.SetColumn(star, 1);
            g.Children.Add(star);

            Border wrap = new Border();
            wrap.Child = g;
            wrap.Background = Brushes.Transparent;
            wrap.Cursor = Cursors.Hand;
            string cmd = e.Command;
            wrap.MouseLeftButtonUp += delegate { FillRowInput(cmd); };
            _libList.Items.Add(wrap);
        }

        /// <summary>
        /// 命令库列表项。ListBox 直接装 CmdSpec 会把 ToString() 打出来
        /// （屏幕上就是一串 WindowsCommandTools.CmdSpec），所以必须自己画。
        /// 命令名与说明都塞进 Tag，选中时不用再去反查。
        ///
        /// 视觉规格逐项对齐主窗口 MainPanels.AddCommandCard：
        /// 命令行 13.5 SemiBold 等宽 TextBrush / 说明 11.5 TextDimBrush / 右侧同样的三枚徽标。
        /// 列表项的内边距与选中底色由 CardItem 样式统一提供（两边用的是同一个样式键），
        /// 所以这里不再自己加 Padding，免得两边厚薄不一。
        /// </summary>
        private static UIElement BuildLibraryItem(CmdSpec s)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            StackPanel left = Ui.V();
            TextBlock name = Ui.Text(s.Name, 13.5, "TextBrush", FontWeights.SemiBold);
            name.FontFamily = Fonts.Mono;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(name);
            if (!string.IsNullOrEmpty(s.Title))
            {
                TextBlock t = Ui.Text(s.Title, 11.5, "TextDimBrush");
                t.Margin = new Thickness(0, 2, 0, 0);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                left.Children.Add(t);
            }
            g.Children.Add(left);

            StackPanel badges = Ui.H();
            badges.VerticalAlignment = VerticalAlignment.Top;
            if (s.Admin) badges.Children.Add(MpBadge(Loc.T("管理"), "AccentSoftBrush", "AccentBrush"));
            if (s.Danger >= 2) badges.Children.Add(MpBadge(Loc.T("高危"), "DangerSoftBrush", "DangerBrush"));
            else if (s.Danger == 1) badges.Children.Add(MpBadge(Loc.T("改动"), "WarningSoftBrush", "WarningBrush"));
            Grid.SetColumn(badges, 1);
            g.Children.Add(badges);

            g.Tag = s;
            g.ToolTip = s.Name + (s.Title.Length > 0 ? "  ·  " + s.Title : "")
                + (s.Desc.Length > 0 ? "\n" + s.Desc : "");
            return g;
        }

        /// <summary>
        /// 和主窗口命令卡片同规格的小徽标（管理 / 高危 / 改动）。
        /// 直接借 MainWindow.MakeBadge 画底子，再按 MainPanels.Badge 的尺寸收紧，
        /// 这样徽标的圆角、字号、配色和主窗口一模一样，不会两处各写一套慢慢跑偏。
        /// </summary>
        private static Border MpBadge(string text, string bgKey, string fgKey)
        {
            Border b = MainWindow.MakeBadge(text, bgKey, fgKey);
            b.Margin = new Thickness(4, 0, 0, 0);
            b.Padding = new Thickness(5, 1, 5, 1);
            return b;
        }

        // ==================================================================
        //  右：参数提示 / 参数表单 / 命令详情
        //
        //  主窗口右栏是三个页签（MainPanels.BuildRightPanel / BuildForm /
        //  BuildDetails）。这三个页的实现是 MainWindow 的私有方法，多进程窗口
        //  用不了，所以这里按同一套数据源（CommandLibrary 的 CmdSpec）和同一套
        //  视觉规格重写一份，保证两边内容与观感一致。
        // ==================================================================

        private UIElement BuildRightPanel()
        {
            // 外边距和主窗口右侧面板那层 Grid 一样是 (8,10,10,10)：
            // 页签标题行与内容区的左右起点、可用宽度都和主窗口对齐。
            Grid host = new Grid();
            host.Margin = new Thickness(8, 10, 10, 10);

            _rightTabs = new TabControl();

            TabItem t1 = new TabItem();
            t1.Header = Loc.T("参数提示");
            t1.Content = BuildSuggestTab();

            TabItem t2 = new TabItem();
            t2.Header = Loc.T("参数表单");
            _formHost = Ui.V();
            ScrollViewer fsv = new ScrollViewer();
            fsv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            fsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            fsv.Content = _formHost;
            t2.Content = fsv;

            TabItem t3 = new TabItem();
            t3.Header = Loc.T("命令详情");
            _detailHost = Ui.V();
            ScrollViewer dsv = new ScrollViewer();
            dsv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            dsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            dsv.Content = _detailHost;
            t3.Content = dsv;

            _rightTabs.Items.Add(t1);
            _rightTabs.Items.Add(t2);
            _rightTabs.Items.Add(t3);
            host.Children.Add(_rightTabs);

            SetRightPanel(null);
            ShowEmptyForm();
            ShowEmptyDetail();
            return host;
        }

        /// <summary>「参数提示」页：现有的那一块（当前命令 / 当前位置 / 可用参数）。</summary>
        private UIElement BuildSuggestTab()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            StackPanel sp = Ui.V();
            // 分区标题规格对齐主窗口：13 SemiBold TextBrush
            _rightTitle = Ui.Text(Loc.T("参数提示"), 13, "TextBrush", FontWeights.SemiBold);
            sp.Children.Add(_rightTitle);

            // 命令名 + 副标题：规格对齐主窗口「命令详情」里那组（16 Bold 等宽 / 11.5 TextFaint）
            _rightCmd = Ui.Text("", 16, "TextBrush", FontWeights.Bold);
            _rightCmd.FontFamily = Fonts.Mono;
            _rightCmd.Margin = new Thickness(0, 8, 0, 0);
            _rightCmd.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(_rightCmd);

            _rightSub = Ui.Wrap("", 11.5, "TextFaintBrush");
            _rightSub.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(_rightSub);

            _rightBody = Ui.Wrap("", 12, "TextDimBrush");
            _rightBody.LineHeight = 19;
            _rightBody.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(_rightBody);

            // 和主窗口一样：把提示引擎算出来的「当前位置」摆出来
            // （例如「ping 的第 2 个参数」），换行显示，别撑破右栏。
            _rightHint = Ui.Wrap("", 11.5, "AccentBrush");
            _rightHint.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(_rightHint);

            sp.Children.Add(Ui.HairLine());
            _rightParams = Ui.V();
            _rightParams.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(_rightParams);

            sv.Content = sp;
            return sv;
        }

        /// <summary>点命令库某一条时，把它的说明直接显示在右栏（不用先跑一遍提示引擎）。</summary>
        private void ShowSpec(CmdSpec s)
        {
            if (s == null || _owner == null) return;
            SetSpecTabs(s);
            // 用「当前编辑行实际会用的控制台」算，和 UpdateSuggestions 一致
            // （这一行单独指定过 CMD 的时候，提示也要按 CMD 算）
            SuggestResult r = Suggester.Compute(_owner.Lib, s.Name + " ", (s.Name + " ").Length,
                _owner.SessionDirectory, ShellOfRow(EditTask()));
            _lastSuggest = r;
            SetRightPanel(r);
        }

        /// <summary>右栏两个「命令级」页签（参数表单 + 命令详情）一起换成这条命令。</summary>
        private void SetSpecTabs(CmdSpec s)
        {
            BuildForm(s);
            BuildDetails(s);
        }

        // ==================================================================
        //  「参数表单」页（对齐主窗口 MainPanels.BuildForm 及配套方法）
        // ==================================================================

        private void ShowEmptyForm()
        {
            if (_formHost == null) return;
            _formHost.Children.Clear();
            _formFields.Clear();
            _formEditors.Clear();
            _formDirty = false;
            TextBlock t = Ui.Wrap(Loc.T("选中一条命令后，这里会生成可填写的参数表单，填完即可一键执行，不用记参数写法。"),
                12, "TextFaintBrush");
            t.Margin = new Thickness(0, 6, 0, 0);
            _formHost.Children.Add(t);
        }

        private void BuildForm(CmdSpec s)
        {
            if (_formHost == null) return;
            _formHost.Children.Clear();
            _formFields.Clear();
            _formEditors.Clear();
            _formDirty = false;
            _formSpec = s;

            if (s == null) { ShowEmptyForm(); return; }

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
                _formHost.Children.Add(BuildPreviewBox(s));
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
            _formHost.Children.Add(BuildPreviewBox(s));
        }

        private FrameworkElement BuildFieldEditor(FormField f)
        {
            if (f.Type == "flag")
            {
                CheckBox cb = new CheckBox();
                cb.Content = f.Label;
                cb.IsChecked = string.Equals(f.Value, "true", StringComparison.OrdinalIgnoreCase);
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
                box.PreviewTextInput += delegate (object sender, TextCompositionEventArgs e)
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

        /// <summary>表单底部的预览框：将执行的命令 + 填入 / 立即执行 / 重置。</summary>
        private Border BuildPreviewBox(CmdSpec s)
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
                FillRowInput(_formPreview.Text);
            }));
            Button run = Ui.Btn(Loc.T("立即执行"), "PrimaryButton", delegate
            {
                string cmd = _formPreview.Text;
                if (!string.IsNullOrEmpty(cmd))
                {
                    FillRowInput(cmd);
                    RunActiveRow();
                }
            });
            run.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(run);
            Button reset = Ui.Btn(Loc.T("重置"), "GhostButton", delegate
            {
                if (_formSpec != null) BuildForm(_formSpec);
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
            if (_formPreview == null || _formSpec == null) return;
            _formPreview.Text = ComposeFormCommand(_formSpec, _formFields, _formEditors);
        }

        /// <summary>把表单当前的值拼成一条完整命令（和主窗口 MainPanels.ComposeFormCommand 同规则）。</summary>
        private static string ComposeFormCommand(CmdSpec s, List<FormField> fields, List<FrameworkElement> editors)
        {
            if (s == null) return "";
            List<string> parts = new List<string>();
            foreach (string piece in SplitTokens(s.Name)) parts.Add(piece);

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

        // ==================================================================
        //  「命令详情」页（对齐主窗口 MainPanels.BuildDetails / AddNodeTree）
        // ==================================================================

        private void ShowEmptyDetail()
        {
            if (_detailHost == null) return;
            _detailHost.Children.Clear();
            TextBlock t = Ui.Wrap(Loc.T("选择左侧命令库中的命令，或直接在输入框里输入命令，这里会显示用途、语法和真实示例。"),
                12, "TextFaintBrush");
            t.Margin = new Thickness(0, 6, 0, 0);
            _detailHost.Children.Add(t);
        }

        private void BuildDetails(CmdSpec s)
        {
            if (_detailHost == null) return;
            _detailHost.Children.Clear();
            if (s == null) { ShowEmptyDetail(); return; }

            StackPanel head = Ui.H();
            TextBlock nm = Ui.Text(s.Name, 16, "TextBrush", FontWeights.Bold);
            nm.FontFamily = Fonts.Mono;
            head.Children.Add(nm);
            if (s.Admin)
            {
                Border b = MpBadge(Loc.T("需要管理员"), "AccentSoftBrush", "AccentBrush");
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Margin = new Thickness(8, 0, 0, 0);
                head.Children.Add(b);
            }
            if (s.Danger >= 1)
            {
                Border b = MpBadge(s.Danger >= 2 ? Loc.T("高危") : Loc.T("会改动系统"), "DangerSoftBrush", "DangerBrush");
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
                    tags.Children.Add(MpBadge(tag, "HoverOverlayBrush", "TextDimBrush"));
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
                    row.MouseLeftButtonUp += delegate { FillRowInput(captured); };
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
                Border kind = MpBadge(NodeKindLabel(n), KindBackground(NodeSuggestKind(n)), KindForeground(NodeSuggestKind(n)));
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

        /// <summary>把文字填进「当前编辑行」的输入框（表单 / 详情里的示例都走这里）。</summary>
        private void FillRowInput(string text)
        {
            MultiProcTask t = EditTask();
            if (t == null || t.Input == null) return;
            _suppressSuggest = true;
            t.Input.Text = text ?? "";
            _suppressSuggest = false;
            t.Input.CaretIndex = t.Input.Text.Length;
            t.Input.Focus();
            UpdateSuggestions();
        }

        /// <summary>执行「当前编辑行」（表单页的「立即执行」）。</summary>
        private void RunActiveRow()
        {
            MultiProcTask t = EditTask();
            if (t != null) ToggleTask(t);
        }

        /// <summary>用一条推荐结果刷新右侧说明区（命令 / 说明 / 当前位置 / 可用参数）。</summary>
        private void SetRightPanel(SuggestResult r)
        {
            if (_rightParams == null) return;
            _rightParams.Children.Clear();

            CmdSpec spec = r == null ? null : r.Spec;
            if (spec == null)
            {
                _rightCmd.Text = "";
                if (_rightSub != null) _rightSub.Text = "";
                if (_rightHint != null) _rightHint.Text = "";
                _rightBody.Text = Loc.T("在某一行的输入框里敲命令，这里会显示它的用途和当前语法位置上可用的参数。");
                return;
            }

            _rightCmd.Text = spec.Name;
            if (_rightSub != null)
            {
                _rightSub.Text = spec.Title + "  ·  " + spec.Category + Loc.T("  ·  来源 ") + spec.Source;
            }
            _rightBody.Text = spec.Desc;
            if (_rightHint != null) _rightHint.Text = r.HintTitle;

            if (spec.Usage.Length > 0)
            {
                // 语法块规格对齐主窗口「命令详情」里的那个用法框：
                // SurfaceSunkenBrush 底 + ControlCornerRadius + 内边距 10 + 12 号等宽 TextBrush
                Border usageBox = new Border();
                usageBox.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
                usageBox.Padding = new Thickness(10);
                usageBox.Margin = new Thickness(0, 0, 0, 10);
                usageBox.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
                TextBlock usage = Ui.Wrap(Loc.T("语法：") + spec.Usage, 12, "TextBrush");
                usage.FontFamily = Fonts.Mono;
                usageBox.Child = usage;
                _rightParams.Children.Add(usageBox);
            }

            List<ParamNode> level = r.CurrentLevel;
            if (level == null || level.Count == 0) level = spec.Nodes;
            int shown = 0;
            for (int i = 0; i < level.Count && shown < 40; i++)
            {
                ParamNode node = level[i];
                if (node == null) continue;
                _rightParams.Children.Add(BuildParamRow(node));
                shown++;
            }
            if (shown == 0 && r.Items.Count > 0)
            {
                for (int i = 0; i < r.Items.Count && i < 40; i++)
                    _rightParams.Children.Add(BuildSuggestionRow(r.Items[i]));
            }

            TextBlock foot = Ui.Wrap(r.Items.Count > 0
                ? Loc.T("当前位置有 ") + r.Items.Count + Loc.T(" 个候选，点一条就能填进命令行。")
                : Loc.T("当前位置没有更多候选。"), 11, "TextFaintBrush");
            foot.Margin = new Thickness(0, 6, 0, 0);
            _rightParams.Children.Add(foot);
        }

        /// <summary>
        /// 把右栏里的一行包成「点一下填进命令行」的可点区域（悬浮时才有底色）。
        /// 内边距 / 圆角 / 悬浮底色对齐主窗口列表项用的 CardItem 样式
        /// （11,8 内边距 · ControlCornerRadius · HoverOverlayBrush），
        /// 让右栏这些可点参数和主窗口右栏那份列表摸起来是一样的。
        /// </summary>
        private Border ClickableRow(UIElement content, string token, string tip)
        {
            Border b = new Border();
            b.Padding = new Thickness(11, 8, 11, 8);
            b.Margin = new Thickness(0, 2, 0, 0);
            b.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            b.Background = Brushes.Transparent;
            b.Child = content;
            if (!string.IsNullOrEmpty(token))
            {
                b.Cursor = Cursors.Hand;
                b.ToolTip = tip;
                string ins = token;
                b.MouseLeftButtonUp += delegate { InsertToken(ins); };
                b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush"); };
                b.MouseLeave += delegate
                {
                    b.ClearValue(Border.BackgroundProperty);
                    b.Background = Brushes.Transparent;
                };
            }
            return b;
        }

        /// <summary>
        /// 右栏的「参数」一行：规格和主窗口 MainPanels.BuildParamRow 一模一样 ——
        /// 左边一枚类别徽标（子命令/参数/值/语法/开关），右边参数名 12.5 SemiBold 等宽
        /// + 取值提示 11 Accent 等宽 + 说明 11.5 TextDimBrush。
        /// 区别只在多进程这边整行可以点一下填进当前行的输入框。
        /// </summary>
        private UIElement BuildParamRow(ParamNode n)
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
            SuggestKind k = NodeSuggestKind(n);
            badge.SetResourceReference(Border.BackgroundProperty, KindBackground(k));
            TextBlock bt = Ui.Text(NodeKindLabel(n), 10.5, KindForeground(k), FontWeights.SemiBold);
            bt.HorizontalAlignment = HorizontalAlignment.Center;
            bt.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = bt;
            g.Children.Add(badge);

            StackPanel text = Ui.V();
            text.Margin = new Thickness(9, 0, 0, 0);
            StackPanel head = Ui.H();
            TextBlock tok = Ui.Text(n.Token, 12.5, "TextBrush", FontWeights.SemiBold);
            tok.FontFamily = Fonts.Mono;
            head.Children.Add(tok);
            if (!string.IsNullOrEmpty(n.ValueHint))
            {
                TextBlock vh = Ui.Text("  " + n.ValueHint, 11, "AccentBrush");
                vh.FontFamily = Fonts.Mono;
                vh.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(vh);
            }
            text.Children.Add(head);
            TextBlock d = Ui.Wrap(n.Desc + (n.Enum.Count > 0 ? Loc.T("（取值：") + string.Join(" / ", n.Enum.ToArray()) + "）" : ""),
                11.5, "TextDimBrush");
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            string token = n.Token.Length > 0 ? n.Token + " " : "";
            return ClickableRow(g, token,
                n.Token.Length > 0 ? Loc.T("点一下把 ") + n.Token + Loc.T(" 填进命令行") : "");
        }

        /// <summary>右栏里「候选」一行：规格同主窗口主提示列表（徽标 + 13 号等宽 + 说明）。</summary>
        private UIElement BuildSuggestionRow(Suggestion s)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            Border badge = new Border();
            badge.MinWidth = 40; badge.Height = 19;
            badge.Padding = new Thickness(7, 2, 7, 2);
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
            tok.TextTrimming = TextTrimming.CharacterEllipsis;
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
                Grid.SetColumn(chev, 2);
                g.Children.Add(chev);
            }

            string token = string.IsNullOrEmpty(s.Insert) ? s.Display : s.Insert;
            return ClickableRow(g, token, Loc.T("点一下把 ") + token.Trim() + Loc.T(" 填进命令行"));
        }

        /// <summary>参数的类别（和主窗口 MainPanels.NodeSuggestKind 同一套判定）。</summary>
        private static SuggestKind NodeSuggestKind(ParamNode n)
        {
            if (n.IsSubCommand) return SuggestKind.SubCommand;
            if (n.IsOption) return SuggestKind.Option;
            if (n.IsValue) return SuggestKind.Value;
            if (n.IsSyntax) return SuggestKind.Syntax;
            return SuggestKind.Flag;
        }

        /// <summary>徽标上的类别文字（和主窗口 MainPanels.NodeKindLabel 一致）。</summary>
        private static string NodeKindLabel(ParamNode n)
        {
            if (n.IsSubCommand) return Loc.T("子命令");
            if (n.IsOption) return Loc.T("参数");
            if (n.IsValue) return Loc.T("值");
            if (n.IsSyntax) return Loc.T("语法");
            return Loc.T("开关");
        }

        // ==================================================================
        //  任务行
        // ==================================================================

        /// <summary>任务行变了（增 / 删）：重建行的视觉树，按当前合并模式决定输出落在哪。</summary>
        public void OnTasksChanged(bool added)
        {
            if (added)
            {
                MultiProcTask t = _tasks[_tasks.Count - 1];
                if (MultiProc.Selected == null) MultiProc.Selected = t;
            }
            RebuildRows();
        }

        /// <summary>重建所有任务行（换语言 / 增删行 / 设置变化时调用）。</summary>
        public void RebuildRows()
        {
            if (_rowHost == null) return;
            _rowHost.Children.Clear();
            for (int i = 0; i < _tasks.Count; i++)
            {
                MultiProcTask t = _tasks[i];
                if (t.RowRoot == null) t.RowRoot = BuildRow(t);
                _rowHost.Children.Add(t.RowRoot);
                RefreshTaskUi(t);
            }
            RefreshTexts();
            RebuildInlineOutput();
            RefreshAllTabs();
        }

        private Border BuildRow(MultiProcTask t)
        {
            MultiProcTask captured = t;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(24);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[3].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[4].Width = GridLength.Auto;

            t.RowNo = Ui.Text(t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                12, "TextFaintBrush", FontWeights.SemiBold);
            t.RowNo.FontFamily = Fonts.Mono;
            t.RowNo.VerticalAlignment = VerticalAlignment.Top;
            t.RowNo.Margin = new Thickness(0, 7, 0, 0);
            g.Children.Add(t.RowNo);

            t.Input = new TextBox();
            t.Input.FontFamily = Fonts.Mono;
            t.Input.FontSize = 12.5;
            t.Input.Padding = new Thickness(9, 5, 9, 5);
            t.Input.Margin = new Thickness(0, 0, 6, 0);
            t.Input.VerticalContentAlignment = VerticalAlignment.Center;
            t.Input.TextWrapping = TextWrapping.NoWrap;
            t.Input.AcceptsReturn = false;
            t.Input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            t.Input.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            t.Input.Height = 30;
            t.Input.ToolTip = Loc.T("输入这条任务要执行的命令；单行模式按 Enter 直接运行，多命令模式按 Ctrl+Enter");
            t.Input.PreviewKeyDown += OnRowInputKey;
            t.Input.TextChanged += delegate { OnRowInputChanged(captured); };
            t.Input.SelectionChanged += delegate { OnRowInputChanged(captured); };
            t.Input.GotFocus += delegate { SetActiveRow(captured); };
            t.Input.PreviewMouseDown += delegate { SetActiveRow(captured); };
            Grid.SetColumn(t.Input, 1);
            g.Children.Add(t.Input);

            t.RunBtn = Ui.Btn(Loc.T("运行"), "SoftButton", delegate { ToggleTask(captured); });
            t.RunBtn.Height = 30;
            t.RunBtn.MinWidth = 56;
            t.RunBtn.FontSize = 11.5;
            t.RunBtn.Padding = new Thickness(10, 0, 10, 0);
            t.RunBtn.Margin = new Thickness(0, 0, 6, 0);
            t.RunBtn.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(t.RunBtn, 2);
            g.Children.Add(t.RunBtn);

            t.MultiBtn = Ui.Btn(Loc.T("多命令"), "GhostButton", delegate { ToggleMulti(captured); });
            t.MultiBtn.Height = 30;
            t.MultiBtn.FontSize = 11.5;
            t.MultiBtn.Padding = new Thickness(10, 0, 10, 0);
            t.MultiBtn.Margin = new Thickness(0, 0, 6, 0);
            t.MultiBtn.VerticalAlignment = VerticalAlignment.Top;
            t.MultiBtn.ToolTip = Loc.T("这一行切成多命令形式：单行输入框变成多行文本区，一整段脚本一次执行");
            Grid.SetColumn(t.MultiBtn, 3);
            g.Children.Add(t.MultiBtn);

            // ---- 这一行单独指定执行控制台（PS / CMD，和主窗口的「临时切换」同一语义） ----
            t.ShellBtn = new Button();
            t.ShellBtn.SetResourceReference(FrameworkElement.StyleProperty, "ChipButton");
            t.ShellBtn.Height = 30;
            t.ShellBtn.MinWidth = 62;
            t.ShellBtn.Padding = new Thickness(9, 0, 7, 0);
            t.ShellBtn.FontSize = 11.5;
            t.ShellBtn.VerticalAlignment = VerticalAlignment.Top;
            t.ShellBtn.Click += delegate { OpenRowShellMenu(captured, t.ShellBtn); };
            StackPanel shellSp = Ui.H();
            shellSp.VerticalAlignment = VerticalAlignment.Center;
            t.ShellLabel = Ui.Text("PS", 11.5, "TextDimBrush", FontWeights.SemiBold);
            t.ShellLabel.FontFamily = Fonts.Mono;
            t.ShellLabel.VerticalAlignment = VerticalAlignment.Center;
            shellSp.Children.Add(t.ShellLabel);
            UIElement caret = Icons.Create("chevronDown", 11, "TextFaintBrush", 1.6);
            caret.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            caret.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 0, 0));
            shellSp.Children.Add(caret);
            t.ShellBtn.Content = shellSp;
            Grid.SetColumn(t.ShellBtn, 4);
            g.Children.Add(t.ShellBtn);

            // ---- 执行框下面那一行灰字：最左侧 PS> / CMD> + 状态 ----
            // 「就绪 / 运行中 / 完成 · 退出码 N」原来在行的最右边，现在整块搬到这里：
            // 一行只看一个地方就知道「用哪个控制台、跑到哪一步了」。
            t.InlineBox = new Border();
            t.InlineBox.Margin = new Thickness(24, 4, 0, 12);
            t.InlineBox.Padding = new Thickness(10, 5, 10, 5);
            t.InlineBox.BorderThickness = new Thickness(1);
            t.InlineBox.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            t.InlineBox.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
            t.InlineBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            t.InlineBox.Cursor = Cursors.Hand;
            t.InlineBox.MouseLeftButtonUp += delegate { OnInlineBoxClick(captured); };
            t.InlineBox.ToolTip = Loc.T("点这里把这条任务的输出调到前面（标签页或它自己的输出窗口）");

            StackPanel line = Ui.H();
            line.VerticalAlignment = VerticalAlignment.Center;

            t.ShellText = Ui.Text("PS>", 11.5, "AccentBrush", FontWeights.SemiBold);
            t.ShellText.FontFamily = Fonts.Mono;
            t.ShellText.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(t.ShellText);

            t.Status = Ui.Text(Loc.T("就绪"), 11.5, "TextFaintBrush");
            t.Status.VerticalAlignment = VerticalAlignment.Center;
            t.Status.Margin = new Thickness(6, 0, 0, 0);
            t.Status.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(t.Status);

            t.InlineOut = Ui.Text("", 11, "TextFaintBrush");
            t.InlineOut.VerticalAlignment = VerticalAlignment.Center;
            t.InlineOut.Margin = new Thickness(12, 0, 0, 0);
            t.InlineOut.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(t.InlineOut);

            t.InlineBox.Child = line;

            StackPanel row = Ui.V();
            row.Margin = new Thickness(0, 0, 0, 6);
            row.Children.Add(g);
            row.Children.Add(t.InlineBox);

            Border b = new Border();
            b.Padding = new Thickness(6, 5, 6, 5);
            b.Margin = new Thickness(0, 0, 0, 6);
            b.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            b.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
            b.BorderThickness = new Thickness(1);
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            b.Child = row;
            return b;
        }

        private void SetActiveRow(MultiProcTask t)
        {
            if (t == null) return;
            // 「编辑焦点」和「显示哪个标签页」是两件事：这里只动前者，
            // 命令库、参数提示、灰行提示符都跟着这一行走。
            bool changed = t != _focusTask;
            _focusTask = t;
            if (changed) RefreshTexts();
            UpdateSuggestions();
        }

        // ==================================================================
        //  这一行的执行控制台（和主窗口的「临时切换」同一语义）
        // ==================================================================

        /// <summary>这一行的控制台菜单：跟随窗口模式 / 单独指定 CMD / 单独指定 PowerShell。</summary>
        private void OpenRowShellMenu(MultiProcTask t, Button anchor)
        {
            if (t == null || anchor == null) return;
            MultiProcTask captured = t;
            ContextMenu m = new ContextMenu();
            m.PlacementTarget = anchor;
            m.Placement = PlacementMode.Bottom;

            MenuItem head = new MenuItem();
            head.Header = Loc.T("这一行用哪个控制台执行");
            head.IsEnabled = false;
            m.Items.Add(head);
            m.Items.Add(new Separator());

            MenuItem follow = new MenuItem();
            follow.Header = Loc.T("跟随窗口模式（") + Shells.Display(_shell) + "）";
            follow.IsChecked = !t.RowShell.HasValue;
            follow.Click += delegate { SetRowShell(captured, null); };
            m.Items.Add(follow);

            m.Items.Add(new Separator());

            MenuItem useCmd = new MenuItem();
            useCmd.Header = Loc.T("这一行用 CMD 执行");
            useCmd.InputGestureText = "cmd>";
            useCmd.IsChecked = t.RowShell.HasValue && t.RowShell.Value == ShellKind.Cmd;
            useCmd.Click += delegate { SetRowShell(captured, ShellKind.Cmd); };
            m.Items.Add(useCmd);

            MenuItem usePs = new MenuItem();
            usePs.Header = Loc.T("这一行用 PowerShell 执行");
            usePs.InputGestureText = "ps>";
            usePs.IsChecked = t.RowShell.HasValue && t.RowShell.Value == ShellKind.PowerShell;
            usePs.Click += delegate { SetRowShell(captured, ShellKind.PowerShell); };
            m.Items.Add(usePs);

            m.Items.Add(new Separator());
            MenuItem note = new MenuItem();
            note.Header = Loc.T("也可以在输入框里用 cmd> / ps> 前缀临时指定单独一条命令");
            note.IsEnabled = false;
            m.Items.Add(note);

            m.IsOpen = true;
        }

        /// <summary>把「正在编辑的行」切到某一行（命令库 / 参数提示都跟着它）。</summary>
        public void FocusRow(MultiProcTask t)
        {
            if (t == null) return;
            SetActiveRow(t);
            try
            {
                if (t.Input != null) t.Input.Focus();
            }
            catch { }
        }

        /// <summary>设置这一行的执行控制台（null = 跟随窗口模式）。</summary>
        public void SetRowShell(MultiProcTask t, ShellKind? shell)
        {
            if (t == null) return;
            t.RowShell = shell;
            RefreshTaskUi(t);
            RefreshInlineHints();
            if (t == EditTask()) UpdateSuggestions();
            RefreshAllTabs();
        }

        /// <summary>切换这一行的多命令形式。</summary>
        private void ToggleMulti(MultiProcTask t)
        {
            if (t == null || t.Input == null) return;
            t.Multi = !t.Multi;
            if (t.Multi)
            {
                double h = _owner == null ? 150 : _owner.Settings.MultiCommandHeight;
                if (h < 72) h = 72;
                if (h > 600) h = 600;
                t.Input.AcceptsReturn = true;
                t.Input.TextWrapping = TextWrapping.NoWrap;
                t.Input.VerticalContentAlignment = VerticalAlignment.Top;
                t.Input.Height = h;
                t.Input.MinHeight = 72;
                t.Input.ToolTip = Loc.T("多命令形式：可以粘贴一整段脚本，Ctrl+Enter 一次执行");
            }
            else
            {
                t.Input.AcceptsReturn = false;
                t.Input.VerticalContentAlignment = VerticalAlignment.Center;
                t.Input.ClearValue(FrameworkElement.HeightProperty);
                t.Input.Height = 30;
                t.Input.MinHeight = 0;
                t.Input.ToolTip = Loc.T("输入这条任务要执行的命令；单行模式按 Enter 直接运行，多命令模式按 Ctrl+Enter");
            }
            RefreshTaskUi(t);
            RefreshAllTabs();
        }

        /// <summary>键盘：单行 Enter 运行；多命令 Enter 换行、Ctrl+Enter 运行；候选导航沿用主界面那套。</summary>
        private void OnRowInputKey(object sender, KeyEventArgs e)
        {
            TextBox box = sender as TextBox;
            if (box == null) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            MultiProcTask t = TaskOfInput(box);
            if (t == null) return;

            if (e.Key == Key.Down || (ctrl && e.Key == Key.N))
            {
                if (_suggestList != null && _suggestList.Items.Count > 0)
                {
                    SetSuggestVisible(true);
                    int i = _suggestList.SelectedIndex + 1;
                    if (i >= _suggestList.Items.Count) i = 0;
                    _suggestList.SelectedIndex = i;
                    _suggestList.ScrollIntoView(_suggestList.Items[i]);
                    _suggestNav = i;
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Up || (ctrl && e.Key == Key.P))
            {
                if (_suggestList != null && _suggestList.Items.Count > 0 && _suggestVisible)
                {
                    int i = _suggestList.SelectedIndex - 1;
                    if (i < 0) i = _suggestList.Items.Count - 1;
                    _suggestList.SelectedIndex = i;
                    _suggestList.ScrollIntoView(_suggestList.Items[i]);
                    _suggestNav = i;
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Tab)
            {
                if (_suggestList != null && _suggestList.Items.Count > 0)
                {
                    int idx = _suggestList.SelectedIndex >= 0 ? _suggestList.SelectedIndex : 0;
                    ApplySuggestion(idx, false);
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_suggestVisible) SetSuggestVisible(false);
                else box.Text = "";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (_suggestVisible && _suggestNav >= 0 && _suggestList != null && _suggestList.SelectedIndex >= 0)
                {
                    ApplySuggestion(_suggestList.SelectedIndex, false);
                    e.Handled = true;
                    return;
                }
                if (t.Multi && !ctrl)
                {
                    // 多命令模式下 Enter 是换行（记事本习惯），运行用 Ctrl+Enter 或点「运行」
                    return;
                }
                ToggleTask(t);
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.Space)
            {
                UpdateSuggestions();
                if (_suggestList != null && _suggestList.Items.Count > 0) SetSuggestVisible(true);
                e.Handled = true;
            }
        }

        private MultiProcTask TaskOfInput(TextBox box)
        {
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i].Input == box) return _tasks[i];
            }
            return null;
        }

        // ==================================================================
        //  运行 / 停止
        // ==================================================================

        public void ToggleTask(MultiProcTask t)
        {
            if (t == null || t.Slot == null) return;
            if (t.Slot.IsRunning || t.Running) StopTask(t);
            else RunTask(t);
        }

        public void RunTask(MultiProcTask t)
        {
            if (t == null || t.Slot == null) return;
            if (t.Slot.IsRunning)
            {
                MultiProc.Write(t, Loc.T("[多进程] 这条任务已经在运行了。"), true);
                return;
            }
            string text = t.Input == null || t.Input.Text == null ? "" : t.Input.Text.Trim();
            if (text.Length == 0)
            {
                MultiProc.Write(t, Loc.T("[多进程] 请先输入要执行的命令。"), true);
                MultiProc.Flush();
                return;
            }

            // 这一行实际用哪个控制台：行上单独指定的优先，否则窗口模式
            // （其它输入框一致：cmd> / ps> 前缀仍可临时覆盖单独这一条）
            ShellKind shell = ShellOfRow(t);
            string cmd = MainWindow.StripShellPrefixMulti(text, ref shell);
            if (cmd.Length == 0) return;

            // 工具箱自己的内建命令（cls / exit / settings…）走主输入框：
            // 它们动的是整个界面（清屏、关窗、弹设置窗口），在并行任务里没有意义。
            string low = cmd.Trim().ToLowerInvariant();
            if (low == "cls" || low == "clear" || low == "clear-host" || low == "exit"
                || low == "quit" || low == "help" || low == "theme" || low == "settings")
            {
                MultiProc.Write(t, Loc.T("[多进程] 这条是工具箱自己的内建命令，请在主输入框里执行。"), true);
                MultiProc.Flush();
                return;
            }

            // 危险命令仍然拦一下：多进程是批量执行的，误触代价太大。
            // 想跑就走主输入框，那里有完整的确认流程。
            int danger = MainWindow.DangerPattern(cmd);
            CmdSpec spec = _owner == null ? null : _owner.Lib.Find(shell, FirstWord(cmd));
            if (spec != null && spec.Danger > danger) danger = spec.Danger;
            if (danger >= 2)
            {
                MultiProc.Write(t, Loc.T("[多进程] 这条命令属于高危操作，为安全起见请回到主输入框执行（那里有二次确认）。"), true);
                MultiProc.Flush();
                return;
            }

            ClearTaskOutput(t);
            t.LastCmd = cmd;
            MultiProc.Write(t, "❯ " + cmd + "     [" + Shells.Display(shell) + "]", true);
            try
            {
                Encoding forced = CommandExecutor.ResolveEncoding(_owner == null ? "auto" : _owner.Settings.OutputEncoding);
                t.Running = true;
                t.StartedAt = DateTime.Now;
                t.Slot.Execute(cmd, _owner == null ? null : _owner.SessionDirectory, forced, shell, PreferPwsh());
            }
            catch (Exception ex)
            {
                t.Running = false;
                MultiProc.Write(t, Loc.T("启动失败：") + ex.Message, true);
            }
            MultiProc.Flush();
            RefreshTaskUi(t);
            RefreshAllTabs();
            MultiProc.RefreshStatusButton();
        }

        private bool PreferPwsh()
        {
            return _owner != null && _owner.PreferPwsh;
        }

        public void StopTask(MultiProcTask t)
        {
            if (t == null || t.Slot == null) return;
            if (!t.Slot.IsRunning) return;
            MultiProc.Write(t, Loc.T("[多进程] 已发送停止信号，正在结束这条任务的进程树…"), true);
            // 停止只作用于这个槽位自己的进程，不会波及其它任务
            t.Slot.Stop();
            MultiProc.Flush();
            RefreshTaskUi(t);
            RefreshAllTabs();
            MultiProc.RefreshStatusButton();
        }

        private static string FirstWord(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            // cmd> / ps> 前缀先剥掉
            if (s.Length > 4 && (s.StartsWith("cmd>", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("ps>", StringComparison.OrdinalIgnoreCase)))
                s = s.Substring(4).Trim();
            while (s.Length > 0 && (s[0] == '"' || s[0] == '\''))
            {
                int close = s.IndexOf(s[0], 1);
                if (close < 0) break;
                return s.Substring(1, close - 1);
            }
            int sp = s.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            return sp < 0 ? s : s.Substring(0, sp);
        }

        private static void ClearTaskOutput(MultiProcTask t)
        {
            if (t == null) return;
            lock (t.QueueLock) { t.Queue.Clear(); }
            if (t.ViewChunks == null) t.ViewChunks = new List<OutputChunk>();
            else t.ViewChunks.Clear();
            t.OutSync = 0;
            t.NeedsRebuild = false;
            if (t.OutputView != null) t.OutputView.Box.Clear();
            if (t.OutputWindow != null) t.OutputWindow.View.Box.Clear();
        }

        // ==================================================================
        //  增删行
        // ==================================================================

        private void AddRow()
        {
            if (!MultiProc.CanAddRow())
            {
                MessageBox.Show(this,
                    Loc.T("已经到任务行数上限（在设置里可以调大「多进程任务行数上限」）。"),
                    Loc.T("多进程"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MultiProc.AddTask();
            RefreshTexts();
        }

        private void DelRow()
        {
            if (_tasks.Count <= 1) return;
            MultiProcTask last = _tasks[_tasks.Count - 1];
            if (last.Running || (last.Slot != null && last.Slot.IsRunning))
            {
                MessageBox.Show(this, Loc.T("最后一条任务正在运行，先停止它再减少任务行。"),
                    Loc.T("多进程"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MultiProc.RemoveLastTask();
            RefreshTexts();
        }

        // ==================================================================
        //  输出区：合并模式 / 独立窗口
        // ==================================================================

        /// <summary>取出总窗口里这一行的输出视图（不存在就建一个并绑定）。</summary>
        public MultiProcOutputView HostFor(MultiProcTask t)
        {
            if (t == null || _outGrid == null) return null;
            MultiProcOutputView v;
            if (_hosts.TryGetValue(t, out v)) return v;
            v = new MultiProcOutputView();
            v.Box.Margin = new Thickness(1);
            _hosts[t] = v;
            _outGrid.Children.Add(v.Box);
            v.Attach(t);
            return v;
        }

        /// <summary>把某条任务的输出视图显示出来，其余隐藏。</summary>
        public void ShowHostFor(MultiProcTask t)
        {
            if (t == null || _outGrid == null) return;
            MultiProc.Selected = t;
            MultiProc.Rebind(t, true);
            RebuildInlineOutput();
            ShowOnly(t);
            if (_strip != null) _strip.Refresh();
        }

        private void ShowOnly(MultiProcTask t)
        {
            foreach (KeyValuePair<MultiProcTask, MultiProcOutputView> kv in _hosts)
            {
                if (kv.Value == null) continue;
                bool show = kv.Key == t && t.OutputWindow == null;
                kv.Value.Box.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_outHint != null) _outHint.Visibility = Visibility.Visible;
            bool anyShown = false;
            foreach (KeyValuePair<MultiProcTask, MultiProcOutputView> kv in _hosts)
            {
                if (kv.Value != null && kv.Value.Box.Visibility == Visibility.Visible) { anyShown = true; break; }
            }
            if (anyShown) _outHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>重排输出区（标签条 + 各视图绑定）。输出永远在标签页里，没有第二种模式。</summary>
        public void RebuildInlineOutput()
        {
            if (_outGrid == null) return;

            if (_strip != null) _strip.Root.Visibility = Visibility.Visible;

            MultiProc.ResetSelection();
            MultiProcTask sel = MultiProc.Selected;
            if (sel != null) MultiProc.Rebind(sel, true);
            foreach (KeyValuePair<MultiProcTask, MultiProcOutputView> kv in _hosts)
            {
                kv.Value.Box.Visibility = Visibility.Collapsed;
            }
            ShowOnly(sel);
            if (_strip != null) _strip.Rebuild(_tasks, IndexOfTask(MultiProc.Selected));
            RefreshInlineHints();
        }

        private int IndexOfTask(MultiProcTask t)
        {
            if (t == null) return -1;
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i] == t) return i;
            }
            return -1;
        }

        /// <summary>
        /// 刷新每行下面那一行灰字：最左侧是 PS&gt; / CMD&gt;（这一行实际用的控制台），
        /// 中间是状态（就绪 / 运行中… / 完成 · 退出码 N），右边是输出现在在哪。
        /// 状态文案本身由 SetTaskStatus 写；这里只管提示符和输出位置。
        /// </summary>
        private void RefreshInlineHints()
        {
            for (int i = 0; i < _tasks.Count; i++)
            {
                MultiProcTask t = _tasks[i];
                if (t.InlineBox == null || t.ShellText == null || t.InlineOut == null) continue;

                t.ShellText.Text = MultiProc.PromptOf(ShellOfRow(t));

                string where = t.OutputWindow != null
                    ? Loc.T("输出在它自己的窗口里")
                    : Loc.T("输出在下面的标签页");
                t.InlineOut.Text = t.LastCmd.Length > 0 ? "  ·  " + where : where;

                string brush = t.OutputWindow != null ? "AccentGhostBrush" : "HoverOverlayBrush";
                if (!string.Equals(brush, t.InlineBrushKey, StringComparison.Ordinal))
                {
                    t.InlineBrushKey = brush;
                    t.InlineBox.SetResourceReference(Border.BackgroundProperty, brush);
                }
            }
        }

        private void OnInlineBoxClick(MultiProcTask t)
        {
            if (t == null) return;
            MultiProc.Activate(t);
        }

        internal void RefreshAllTabs()
        {
            if (_strip != null) _strip.Refresh();
            MultiProc.RefreshStatusButton();
        }

        /// <summary>90ms 一轮的状态刷新：标签、各行按钮与状态文字、输出占位提示。</summary>
        public void RefreshTabsAndRows()
        {
            SyncPanelWidths();
            if (_strip != null) _strip.Refresh();
            for (int i = 0; i < _tasks.Count; i++) SetTaskStatus(_tasks[i], null, null);
            RefreshInlineHints();
        }

        /// <summary>给静态控制器调用的「刷新输出占位提示」入口。</summary>
        public void RefreshInlineHintsPublic()
        {
            RefreshInlineHints();
        }

        // ==================================================================
        //  标签动作
        // ==================================================================

        private void MoveTab(MultiProcTask t, int target)
        {
            int from = IndexOfTask(t);
            if (from < 0 || target < 0 || target >= _tasks.Count || from == target) return;
            _tasks.RemoveAt(from);
            if (target > _tasks.Count) target = _tasks.Count;
            _tasks.Insert(target, t);
            // 顺序变了，选中的那个标签要按「任务」跟着走，而不是还按下标算：
            // 否则拖一下之后高亮的标签和下面显示的输出来自不同的任务。
            if (_strip != null)
            {
                int sel = IndexOfTask(MultiProc.Selected);
                if (sel >= 0) _strip.SelectedIndex = sel;
            }
            RefreshAllTabs();
        }

        private void MergeAllBack()
        {
            for (int i = 0; i < _tasks.Count; i++)
            {
                MultiProcTask t = _tasks[i];
                if (t.OutputWindow != null)
                {
                    OutputWindow w = t.OutputWindow;
                    t.OutputWindow = null;
                    if (w != null) w.CloseForRemoval();
                }
            }
            RebuildInlineOutput();
            RefreshAllTabs();
            RefreshInlineHints();
        }

        private void CloseAllOutputWindows()
        {
            for (int i = 0; i < _tasks.Count; i++)
            {
                MultiProcTask t = _tasks[i];
                if (t.OutputWindow != null)
                {
                    OutputWindow w = t.OutputWindow;
                    t.OutputWindow = null;
                    if (w != null) w.CloseForRemoval();
                }
            }
            RebuildInlineOutput();
            RefreshAllTabs();
            RefreshInlineHints();
        }

        /// <summary>设置变了（行数上限 / 自由控件宽度）：重排界面。</summary>
        public void NotifySettingsChanged()
        {
            OnSettingsChanged();
        }

        private void OnSettingsChanged()
        {
            // 行数上限调小不删已有行（用户可能正在跑），只在「加行」时生效
            _tasks.Clear();
            _tasks.AddRange(MultiProc.Tasks);
            ApplyPanelWidths();
            // 输出区高度也跟着设置走：设置窗口里改了 MpOutputHeight（或窗口大小）
            // 之后走 NotifySettingsChanged() 这条路就能立刻生效，外面不用再接新接口。
            ApplyOutputHeight(false);
            RebuildRows();
            RefreshInlineHints();
        }

        // ==================================================================
        //  参数提示
        // ==================================================================

        private void OnRowInputChanged(MultiProcTask t)
        {
            if (_suppressSuggest) return;
            // 只记「正在编辑哪一行」：不抢 MultiProc.Selected —— 那是「现在显示哪个标签页」，
            // 抢过来的话下面正显示着的那块输出就不再同步了（表现为输出卡住不动）。
            _focusTask = t;
            UpdateSuggestions();
            if (_strip != null) _strip.Refresh();
        }

        /// <summary>
        /// 按「当前编辑行 + 行内光标」算提示 —— 和主窗口 MainPanels.UpdateSuggestions 用的是
        /// 同一套引擎（Suggester.Compute + SuggestResult.Items / CurrentLevel / HintTitle），
        /// 所以在任意一行的输入框里敲命令，右栏和候选下拉都会像主窗口那样跟着光标所在语法位置走。
        /// 多命令行里也算光标当前所在的那一段，而不是把整段文本当成一行。
        /// </summary>
        private void UpdateSuggestions()
        {
            if (_owner == null || _suggestList == null) return;
            MultiProcTask t = EditTask();
            string text = t == null || t.Input == null ? "" : (t.Input.Text ?? "");
            int caret = t == null || t.Input == null ? 0 : t.Input.CaretIndex;
            if (caret < 0) caret = 0;
            if (caret > text.Length) caret = text.Length;

            string line = LineAt(text, caret);
            int inLine = caret - LineStart(text, caret);
            if (inLine < 0) inLine = 0;
            if (inLine > line.Length) inLine = line.Length;

            // 用「这一行实际会用的控制台」算提示：行上单独指定了就用它，否则跟随窗口模式
            SuggestResult r = Suggester.Compute(_owner.Lib, line, inLine, _owner.SessionDirectory, ShellOfRow(t));
            _lastSuggest = r;

            // 右栏「参数表单 / 命令详情」跟着当前命令走（表单被用户改过时先别覆盖，
            // 和主窗口 MainPanels.UpdateSuggestions 里那个 _formDirty 判断一致）
            CmdSpec sp = r.Spec;
            if (sp != null && sp != _formSpec && !_formDirty) SetSpecTabs(sp);

            if (_suggestList != null)
            {
                _suggestList.Items.Clear();
                if (line.Trim().Length == 0 || r.Items.Count == 0)
                {
                    SetSuggestVisible(false);
                }
                else
                {
                    int n = 0;
                    for (int i = 0; i < r.Items.Count; i++)
                    {
                        _suggestList.Items.Add(BuildSuggestionItem(r.Items[i]));
                        if (++n >= 60) break;
                    }
                    SetSuggestVisible(true);
                    _suggestList.SelectedIndex = -1;
                    _suggestNav = -1;
                }
            }
            SetRightPanel(r);
        }

        private void SetSuggestVisible(bool visible)
        {
            _suggestVisible = visible;
            if (_suggestPanel != null)
                _suggestPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) PositionSuggest();
        }

        /// <summary>
        /// 把候选下拉摆到「当前编辑行」的输入框下沿（浮在任务行 / 输出区上面）。
        /// host 挂在任务行那一格里，所以纵坐标是相对于行区域的偏移；
        /// 行区域自己滚动时 TranslatePoint 已经把偏移算进去了。
        /// </summary>
        private void PositionSuggest()
        {
            if (_suggestHost == null) return;
            double y = 0;
            try
            {
                MultiProcTask t = EditTask();
                if (t != null && t.RowRoot != null)
                    y = t.RowRoot.TranslatePoint(new Point(0, 0), _rowScroll).Y;
                y += 34;   // 输入框高度 + 一点缝，贴在输入框下面
                if (y < 0) y = 0;
            }
            catch { y = 0; }
            try { _suggestHost.Margin = new Thickness(0, y, 0, 0); } catch { }
        }

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
                Grid.SetColumn(chev, 2);
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

        /// <summary>
        /// 点候选卡片。
        ///
        /// 普通候选：整词替换插进当前编辑行，execute=true（双击）时再跑这一行。
        ///
        /// 跨工具候选（在 PowerShell 模式的行里敲 ping，卡片写作「(切换到 CMD) ping」）：
        /// 多进程窗口**不能**像主窗口那样切整个窗口的模式 —— 用户要的是「这一行改用另一个
        /// 终端」，也就是等价于点这一行右边的「这一行用 CMD 执行」。所以这里走
        /// SetRowShell(这一行, 目标)，窗口右上角的模式开关一动不动，别的行也不受影响。
        /// </summary>
        private void ApplySuggestion(int index, bool execute)
        {
            DateTime now = DateTime.Now;
            bool recentCross = _crossPendingTask != null
                && (now - _crossPendingAt).TotalMilliseconds < CrossPendingMs;

            // 双击跨工具候选时，事件顺序是：单击(false) → 双击(true) → 再冒一次单击(false)。
            // 第一下已经把这一行切好、命令也填好了，此时候选列表已按新控制台重算过，
            // 再拿 index 去猜会插错东西（实测会把新列表里的第一个参数补进输入框）；
            // 所以这里只认「刚才那一行」，双击把它跑起来，尾巴上那一下继续吞掉。
            if (execute && recentCross)
            {
                MultiProcTask pending = _crossPendingTask;
                _crossPendingAt = now;   // 续期：压住双击尾巴上冒出来的那一次 MouseUp
                if (pending != null) ToggleTask(pending);
                return;
            }
            if (!execute && recentCross) return;   // 双击过程中重复冒出来的那一下「单击」

            if (index < 0 || _lastSuggest == null) return;
            if (index >= _lastSuggest.Items.Count) return;
            Suggestion s = _lastSuggest.Items[index];
            if (s.Kind == SuggestKind.Hint) return;

            if (s.SwitchShell)
            {
                MultiProcTask t = EditTask();
                if (t == null) return;
                // 1) 先按旧列表整词替换把命令填进这一行（此刻 _lastSuggest 还是旧控制台的）
                ApplyInsertWord(s.Insert);
                // 2) 只切「这一行」的控制台：窗口模式、其它行都不动
                SetRowShell(t, s.SwitchToShell);
                // 3) 记下这一行：双击要跑的就是它；紧接着的那次 MouseUp 也要靠它吞掉
                _crossPendingTask = t;
                _crossPendingAt = now;
                if (execute) ToggleTask(t);
                return;
            }

            ApplyInsert(s.Insert);
            if (execute)
            {
                MultiProcTask t = EditTask();
                if (t != null) ToggleTask(t);
            }
        }

        /// <summary>右栏里点一条参数 / 候选：填进当前编辑行。</summary>
        private void InsertToken(string insert)
        {
            ApplyInsert(insert);
        }

        /// <summary>
        /// 把一段文本按提示引擎的规则插进当前编辑行（该替换半截词就替换，否则插到光标处），
        /// Tab 补全、双击候选、右栏点参数都走这里，行为保证一致。
        /// </summary>
        private bool ApplyInsert(string insert)
        {
            return ApplyInsertCore(insert, false);
        }

        /// <summary>强制整词替换地插入（跨工具候选用：用户可能只敲了半截的 "pin"）。</summary>
        private bool ApplyInsertWord(string insert)
        {
            return ApplyInsertCore(insert, true);
        }

        private bool ApplyInsertCore(string insert, bool forceReplaceWord)
        {
            if (insert == null) return false;
            MultiProcTask t = EditTask();
            if (t == null || t.Input == null) return false;

            string txt = t.Input.Text ?? "";
            int caret = t.Input.CaretIndex;
            if (caret < 0) caret = 0;
            if (caret > txt.Length) caret = txt.Length;
            int lineStart = LineStart(txt, caret);
            string line = LineAt(txt, caret);
            int inLine = caret - lineStart;
            if (inLine < 0) inLine = 0;
            if (inLine > line.Length) inLine = line.Length;

            string word = Suggester.CurrentWord(line, inLine);
            bool replaceWord = forceReplaceWord
                || (_lastSuggest != null && Suggester.AnyStartsWith(_lastSuggest.Items, word));
            string newLine;
            int newCaret;
            Suggester.ApplyInsert(line, inLine, insert, replaceWord, out newLine, out newCaret);

            int lineEnd = lineStart + line.Length;
            string updated = txt.Substring(0, lineStart) + newLine + txt.Substring(lineEnd);
            _suppressSuggest = true;
            t.Input.Text = updated;
            _suppressSuggest = false;
            int pos = lineStart + newCaret;
            if (pos < 0) pos = 0;
            if (pos > updated.Length) pos = updated.Length;
            t.Input.CaretIndex = pos;
            t.Input.Focus();
            _suggestNav = -1;
            UpdateSuggestions();
            return true;
        }

        private static int LineStart(string text, int caret)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (caret < 0) caret = 0;
            if (caret > text.Length) caret = text.Length;
            int probe = caret > 0 ? caret - 1 : 0;
            int nl = text.LastIndexOf('\n', probe);
            return nl >= 0 ? nl + 1 : 0;
        }

        /// <summary>光标所在那一行（不含行尾的换行符）。</summary>
        private static string LineAt(string text, int caret)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int start = LineStart(text, caret);
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            if (end < start) end = start;
            return text.Substring(start, end - start);
        }

        // ==================================================================
        //  状态刷新
        // ==================================================================

        /// <summary>刷新一条任务行的按钮、状态文字、输出占位提示。</summary>
        public void RefreshTaskUi(MultiProcTask t)
        {
            SetTaskStatus(t, null, null);
        }

        /// <summary>
        /// 刷新一条任务行。text 不为空时用指定文字覆盖状态（任务结束那一下的
        /// 「完成 · 退出码 0 · 0.42 秒」就是这么写进去的），但按钮的「运行 / 停止」
        /// 始终按实时状态算 —— 否则任务结束后按钮会一直停在「停止」。
        /// </summary>
        public void SetTaskStatus(MultiProcTask t, string text, string brushKey)
        {
            if (t == null || t.Slot == null) return;
            bool running = t.Running || t.Slot.IsRunning;

            if (t.RunBtn != null)
            {
                t.RunBtn.Content = running ? Loc.T("停止") : Loc.T("运行");
                t.RunBtn.ToolTip = running
                    ? Loc.T("停止这条任务（连同它拉起的子进程一起结束），不影响其它任务")
                    : Loc.T("运行这条任务");
                if (running) t.RunBtn.SetResourceReference(Control.BackgroundProperty, "DangerSoftBrush");
                else t.RunBtn.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
            }
            if (t.MultiBtn != null)
            {
                t.MultiBtn.FontWeight = t.Multi ? FontWeights.SemiBold : FontWeights.Normal;
                if (t.Multi)
                {
                    t.MultiBtn.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                    t.MultiBtn.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
                }
                else
                {
                    t.MultiBtn.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                    t.MultiBtn.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
                }
            }

            // 行右边那个 PS / CMD 小按钮：显示这一行实际会用的控制台。
            // 单独指定过的用强调色（一眼能看出「这行和别的不一样」），跟随窗口的是灰的。
            if (t.ShellBtn != null && t.ShellLabel != null)
            {
                ShellKind sh = ShellOfRow(t);
                bool over = t.RowShell.HasValue;
                string shown = MultiProc.ShellShort(sh) + (over ? "*" : "");
                if (!string.Equals(shown, t.ShellShown, StringComparison.Ordinal))
                {
                    t.ShellShown = shown;
                    t.ShellLabel.Text = shown;
                    if (over)
                    {
                        t.ShellBtn.SetResourceReference(Control.BackgroundProperty, "AccentGhostBrush");
                        t.ShellBtn.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                    }
                    else
                    {
                        t.ShellBtn.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                        t.ShellBtn.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
                    }
                }
                t.ShellBtn.ToolTip = over
                    ? Loc.T("这一行单独用 ") + Shells.Display(sh) + Loc.T(" 执行（点这里可以改回跟随窗口模式）")
                    : Loc.T("跟随窗口模式：当前 ") + Shells.Display(sh) + Loc.T("（点这里可以单独指定这一行的控制台）");
            }

            if (t.Status != null)
            {
                if (text != null)
                {
                    t.Status.Text = text;
                    t.Status.SetResourceReference(TextBlock.ForegroundProperty,
                        brushKey == null ? "TextDimBrush" : brushKey);
                }
                else if (running)
                {
                    t.Status.Text = Loc.T("运行中…");
                    t.Status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                }
                else if (t.Slot.LastExitCode >= 0)
                {
                    int code = t.Slot.LastExitCode;
                    t.Status.Text = code == 0 ? Loc.T("完成 · 退出码 0") : Loc.T("退出码 ") + code;
                    t.Status.SetResourceReference(TextBlock.ForegroundProperty,
                        code == 0 ? "SuccessBrush" : "DangerBrush");
                }
                else
                {
                    t.Status.Text = Loc.T("就绪");
                    t.Status.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
                }
            }
            if (t.OutputWindow != null) t.OutputWindow.RefreshState();
        }

        // ==================================================================
        //  文本 / 提示刷新（换语言、行数变化时调用）
        // ==================================================================

        /// <summary>换语言 / 行数变化时调用：按钮文案和状态都按新语言重刷。</summary>
        public void RefreshTexts()
        {
            if (_title != null) _title.Text = Loc.T("多进程执行");
            if (_sub != null) _sub.Text = Loc.T("每条任务各起一个进程，输出分开显示，可单独停止");
            if (_runAllBtn != null) _runAllBtn.Content = Loc.T("全部运行");
            if (_stopAllBtn != null) _stopAllBtn.Content = Loc.T("全部停止");
            if (_clearBtn != null) _clearBtn.Content = Loc.T("清空输出");
            // 左栏分类 chip 是按语言查出来的显示名，换语言必须重列
            // （否则「常用 / 收藏 / 历史」这三个键既显示的还是旧语言，也比对不上）
            if (_catLang != Loc.Effective) PopulateCategories();
            // 右栏三个页签的标题同理（它们是 new TabItem + Loc.T 出来的，不重刷就还是旧语言）
            if (_rightTabs != null && _rightTabs.Items.Count >= 3)
            {
                ((TabItem)_rightTabs.Items[0]).Header = Loc.T("参数提示");
                ((TabItem)_rightTabs.Items[1]).Header = Loc.T("参数表单");
                ((TabItem)_rightTabs.Items[2]).Header = Loc.T("命令详情");
            }
            if (_rightTitle != null) _rightTitle.Text = Loc.T("参数提示");
            if (_addBtn != null) _addBtn.ToolTip = Loc.T("增加一条任务行");
            if (_delBtn != null) _delBtn.ToolTip = Loc.T("减少最后一条任务行（正在运行的那条不会被删掉）");
            // 窗口内的控制台切换按钮（原来这里是「合并窗口 · 开/关」，那个开关已经删掉）
            ApplyShellSegmentState();
            if (_shellCmdBtn != null) _shellCmdBtn.ToolTip = Loc.T("窗口里的任务默认用 CMD 执行");
            if (_shellPsBtn != null) _shellPsBtn.ToolTip = Loc.T("窗口里的任务默认用 PowerShell 执行");
            if (_rowInfo != null)
            {
                _rowInfo.Text = _tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + Loc.T(" 行 / 上限 ") + MultiProc.ConfiguredRows().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (_addBtn != null) _addBtn.IsEnabled = MultiProc.CanAddRow();
            if (_delBtn != null) _delBtn.IsEnabled = _tasks.Count > 1;
            if (_rowPrompt != null)
            {
                MultiProcTask sel = EditTask();
                _rowPrompt.Text = sel == null ? Loc.T("命令输入") : Loc.T("第 ") + sel.Id
                    + Loc.T(" 行命令" + (sel.Multi ? "（多命令）" : ""));
            }
            for (int i = 0; i < _tasks.Count; i++)
            {
                MultiProcTask t = _tasks[i];
                if (t.RowNo != null) t.RowNo.Text = t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (t.MultiBtn != null) t.MultiBtn.Content = t.Multi ? Loc.T("多命令 · 开") : Loc.T("多命令");
                RefreshTaskUi(t);
            }
            RefreshInlineHints();
            if (_strip != null) _strip.Refresh();
        }

        private void RefreshMaxIcon()
        {
            if (_maxBtn == null) return;
            _maxBtn.Content = Icons.Create(
                WindowState == WindowState.Maximized ? "restore" : "max", 15, "TextDimBrush", 1.6);
        }

        private void OnThemeChanged()
        {
            Dispatcher.BeginInvoke(new Action(delegate { if (_strip != null) _strip.Refresh(); }));
        }

        // ==================================================================
        //  窗口事件
        // ==================================================================

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closingToHide) return;
            // 关窗口 ≠ 停任务：任务继续跑，窗口只是藏起来，随时能从主界面开关拉回来
            e.Cancel = true;
            Hide();
            MultiProc.RefreshStatusButton();
        }

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (e.Key == Key.Escape && _suggestVisible)
            {
                SetSuggestVisible(false);
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.N)
            {
                AddRow();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.L)
            {
                MultiProc.ClearAll();
                e.Handled = true;
            }
        }

        /// <summary>主界面关掉时，由控制器调用：真关（不再 Hide）。</summary>
        public void CloseForShutdown()
        {
            _closingToHide = true;
            CloseAllOutputWindows();
            try { Close(); }
            catch { }
        }

        private Style TryStyle(string key)
        {
            try { return TryFindResource(key) as Style; }
            catch { return null; }
        }
    }

    // =======================================================================
    //  主窗口侧的接线（MainPanels.cs 只需要调这里列出的几个方法）
    // =======================================================================
    public partial class MainWindow
    {
        /// <summary>
        /// 主界面提示行上的「多进程」开关。字段放在这里，MainPanels.cs 直接 new 出来用即可。
        /// 注意：现在 MainPanels 把「多进程开关」和状态图标按钮合并成了一个按钮，
        /// 不再给这个字段赋值 —— 它保持 null，UpdateMultiProcToggle() 会直接早退。
        /// </summary>
        internal Button MultiProcToggle = null;

        /// <summary>
        /// 主界面提示行右边那个状态按钮。MainPanels.cs 里创建一次、加进提示行即可：
        ///     MultiProcStatusHost.Children.Add(BuildMultiProcStatusButton());
        /// </summary>
        internal UIElement BuildMultiProcStatusButton()
        {
            return MultiProc.BuildStatusButton();
        }

        /// <summary>刷新状态按钮（图标灰化 / 运行数 / 悬浮提示）。重复调用没副作用。</summary>
        internal void RefreshMultiProcStatusButton()
        {
            MultiProc.RefreshStatusButton();
        }

        /// <summary>打开多进程窗口（主界面的「多进程」开关 / 菜单都可以调它）。</summary>
        internal void OpenMultiProcWindow()
        {
            MultiProc.Host = this;
            MultiProc.Open(false);
            UpdateMultiProcToggle();
            MultiProc.RefreshStatusButton();
        }

        /// <summary>
        /// 「多进程」开关：开 = 打开独立窗口，关 = 隐藏窗口。
        /// **隐藏不会停止正在跑的任务**，这一点和以前的内嵌面板一致。
        /// </summary>
        internal void SetMultiProc(bool on)
        {
            MultiProc.Host = this;
            MultiProc.ApplySettings();
            if (on) MultiProc.Open(false);
            else MultiProc.CloseWindow();
            UpdateMultiProcToggle();
            MultiProc.RefreshStatusButton();
        }

        /// <summary>多进程窗口现在开着吗（按钮文案据此显示「多进程 · 开」）。</summary>
        internal bool UseMultiProc
        {
            get { return MultiProc.IsOpen; }
            set { MultiProc.Host = this; ApplyMultiProcSettings(); }
        }

        internal void UpdateMultiProcToggle()
        {
            if (MultiProcToggle == null) return;
            bool on = MultiProc.IsOpen;
            MultiProcToggle.Content = on ? Loc.T("多进程 · 开") : Loc.T("多进程");
            MultiProcToggle.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (on)
            {
                MultiProcToggle.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                MultiProcToggle.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
            }
            else
            {
                MultiProcToggle.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                MultiProcToggle.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
            }
            MultiProcToggle.ToolTip = on
                ? Loc.T("多进程窗口已打开（关闭窗口时正在运行的任务会继续跑）。点这里隐藏窗口。")
                : Loc.T("打开多进程窗口：可以同时跑多条命令，每条有自己的输出，可以单独停止。");
        }

        /// <summary>还有任务在跑吗？（主窗口关闭时据此决定要不要提示）</summary>
        internal bool MultiProcAnyRunning()
        {
            return MultiProc.AnyRunning();
        }

        internal void MultiProcRunAll()
        {
            MultiProc.RunAll();
        }

        internal void MultiProcStopAll()
        {
            MultiProc.StopAll();
        }

        internal void MultiProcClearAll()
        {
            MultiProc.ClearAll();
        }

        /// <summary>设置窗口里改了「行数上限 / 合并命令行窗口」后调用：立即生效。</summary>
        internal void ApplyMultiProcSettings()
        {
            MultiProc.Host = this;
            MultiProc.ApplySettings();
            UpdateMultiProcToggle();
        }

        /// <summary>
        /// 设置里的「多进程窗口高度」滑块。现在没有内嵌面板了，
        /// 这个值直接当成多进程窗口的高度用（拖动滑块时窗口立即跟着变高变矮）。
        /// </summary>
        internal void ApplyMultiProcHeight()
        {
            if (MultiProc.Win == null || !MultiProc.Win.IsVisible) return;
            double h = Settings.MultiProcHeight + 400;   // 滑块 180..600 → 窗口 580..1000
            if (h < 560) h = 560;
            if (h > 1000) h = 1000;
            try
            {
                if (MultiProc.Win.WindowState == WindowState.Normal) MultiProc.Win.Height = h;
            }
            catch { }
        }

        /// <summary>
        /// --multiproc 启动：窗口里的任务行预填命令行给的命令（不自动执行）。
        /// </summary>
        internal void ApplyPendingMultiProcCmds()
        {
            string[] cmds = PendingMultiProcCmds;
            PendingMultiProcCmds = null;
            if (cmds == null) return;
            if (MultiProc.Win == null) return;
            MultiProcTask first = null;
            for (int i = 0; i < MultiProc.Tasks.Count && i < cmds.Length; i++)
            {
                if (cmds[i] == null || cmds[i].Length == 0) continue;
                MultiProcTask t = MultiProc.Tasks[i];
                if (t.Input == null) continue;
                t.Input.Text = cmds[i];
                // 光标放到末尾：提示按「这个词已经写完」算，和用户手打一遍的效果一致
                t.Input.CaretIndex = t.Input.Text.Length;
                if (first == null) first = t;
            }
            // 右栏跟着第一条有命令的行显示参数提示（截图 / --multiproc 验证也靠这个）
            MultiProc.Win.FocusRow(first);
        }

        /// <summary>启动时（--multiproc）预填到各任务输入框里的命令。</summary>
        internal string[] PendingMultiProcCmds;
    }
}
