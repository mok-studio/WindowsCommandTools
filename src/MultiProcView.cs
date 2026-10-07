// ---------------------------------------------------------------------------
//  MultiProcView.cs — 多进程的输出呈现层
//
//  三样东西：
//    · MultiProcOutputView  一块只读输出框，绑定一条任务。同一时刻只属于一个窗口
//      （合并模式在总窗口里，分离模式在自己那个小窗口里），所以不需要跨窗口同步。
//    · MultiProcTabStrip    顶部分页标签条，全部自绘：点选、拖动左右重排，
//      横向溢出时自动出现滚动条（标签太多也不会把右边的标签挤出屏幕）。
//      用自绘而不用 TabControl，是因为「拖动重排」在 TabControl 上要跟它的模板较劲，
//      自绘反而更短更可控。「往外拖出 → 弹出独立窗口 / 吸回合并」这套手势已经删掉，
//      弹出独立窗口改由标签右键菜单提供（见 MultiProc.TabMenu）。
//    · OutputWindow         分离出去的那个输出窗口，可以缩放、可以最大化。
//
//  输出只写一次：
//    任务队列里的每个 OutputChunk 先写进「当前绑定视图」的文本框，同时留一份在
//    task.ViewChunks 里。视图换绑（标签弹出 / 合并回去）时用这份记录重画一次，
//    所以拖来拖去内容一个字都不会丢，也不会出现两个窗口各显示一半的情况。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace WindowsCommandTools
{
    // =======================================================================
    //  输出视图：一块只读文本框 + 绑定状态
    // =======================================================================
    internal sealed class MultiProcOutputView
    {
        /// <summary>保留多少条输出块用于换绑时重画（超出后丢最旧的）。</summary>
        private const int MaxKeptChunks = 600;
        /// <summary>文本框超过这么多字符就截断前半，避免跑一夜把内存吃满。</summary>
        private const int MaxChars = 200000;

        public readonly TextBox Box;

        /// <summary>当前绑定的任务；换绑时用来退订。</summary>
        public MultiProcTask Task;

        public MultiProcOutputView()
        {
            Box = new TextBox();
            Box.IsReadOnly = true;
            Box.IsReadOnlyCaretVisible = false;
            Box.FontFamily = Fonts.Mono;
            Box.FontSize = Math.Max(10.5, MultiProc.Settings.FontSize - 1.5);
            Box.TextWrapping = TextWrapping.NoWrap;
            Box.AcceptsReturn = true;
            Box.Padding = new Thickness(10, 8, 8, 8);
            Box.BorderThickness = new Thickness(0);
            Box.Background = Brushes.Transparent;
            Box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Box.SetResourceReference(Control.ForegroundProperty, "ConsoleTextBrush");
        }

        public void Attach(MultiProcTask t)
        {
            if (Task != null) Task.OutputView = null;
            Task = t;
            if (t == null) { Box.Clear(); return; }
            t.OutputView = this;
            t.ViewChunks = new List<OutputChunk>();
            RebuildView(t, this);
        }

        public void Detach()
        {
            if (Task != null && Task.OutputView == this) Task.OutputView = null;
            Task = null;
        }

        /// <summary>按任务留下的输出记录整块重画（换绑时用）。</summary>
        public static void RebuildView(MultiProcTask t, MultiProcOutputView v)
        {
            if (t == null || v == null) return;
            StringBuilder sb = new StringBuilder();
            AppendChunks(t, sb);
            v.Box.Text = sb.ToString();
            v.Box.CaretIndex = v.Box.Text.Length;
            if (MultiProc.Settings.AutoScroll) v.Box.ScrollToEnd();
        }

        /// <summary>给指定视图补上还没写进去的输出块（光标停在末尾时才自动滚到底）。</summary>
        public static void Sync(MultiProcTask t, MultiProcOutputView v)
        {
            if (t == null || v == null) return;
            int synced = t.OutSync;
            if (synced < 0 || synced > t.ViewChunks.Count) { RebuildView(t, v); t.OutSync = t.ViewChunks.Count; return; }
            int n = t.ViewChunks.Count;
            if (n == synced)
            {
                if (t.NeedsRebuild) { t.NeedsRebuild = false; RebuildView(t, v); t.OutSync = n; }
                return;
            }

            bool atEnd = true;
            try { atEnd = v.Box.CaretIndex >= v.Box.Text.Length; }
            catch { }

            int from = synced;
            if (from < 0) from = 0;
            // 一次补太多说明积压了，直接整块重画，比逐段拼字符串快
            if (n - from > 400 || (t.NeedsRebuild && n - from > 8))
            {
                t.NeedsRebuild = false;
                RebuildView(t, v);
                t.OutSync = n;
                return;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = from; i < n; i++) AppendOne(t.ViewChunks[i], sb);
            v.Box.AppendText(sb.ToString());
            t.OutSync = n;

            if (v.Box.Text.Length > MaxChars)
            {
                // 截断后必须整块重画：文本框里剩下的内容和 chunk 记录对不上了
                string keep = v.Box.Text.Substring(v.Box.Text.Length - MaxChars / 2);
                v.Box.Text = Loc.T("[多进程] 输出过长，已截断前半部分。") + "\r\n" + keep;
                TrimChunksForDisplay(t, v);
            }
            if (MultiProc.Settings.AutoScroll && atEnd) v.Box.ScrollToEnd();
        }

        /// <summary>
        /// 文本框被截断之后，把 chunk 记录裁到「凑出来的长度刚好覆盖文本框」的位置，
        /// 然后整块重画一次。不这么做的话：OutSync 还指着旧的块数，下一批输出会
        /// 被当成新内容再追加一遍，文本框里就会出现重复段落。
        /// </summary>
        private static void TrimChunksForDisplay(MultiProcTask t, MultiProcOutputView v)
        {
            int boxLen = v.Box.Text.Length;
            int total = 0;
            int keepFrom = t.ViewChunks.Count;
            for (int i = t.ViewChunks.Count - 1; i >= 0; i--)
            {
                total += t.ViewChunks[i].Text.Length + 2;
                keepFrom = i;
                if (total >= boxLen - 8) break;
            }
            if (keepFrom > 0)
            {
                t.ViewChunks.RemoveRange(0, keepFrom);
                t.OutSync = 0;
            }
            // 一致地重建一次：此后 OutSync == ViewChunks.Count，两边重新对齐
            RebuildView(t, v);
        }

        private static void AppendChunks(MultiProcTask t, StringBuilder sb)
        {
            if (t == null || t.ViewChunks == null) return;
            for (int i = 0; i < t.ViewChunks.Count; i++) AppendOne(t.ViewChunks[i], sb);
        }

        private static void AppendOne(OutputChunk c, StringBuilder sb)
        {
            if (c == null) return;
            sb.Append(c.Text).Append("\r\n");
        }

        /// <summary>输出块写入后的收尾：把记录裁到上限内。</summary>
        public static void TrimKept(MultiProcTask t)
        {
            if (t == null || t.ViewChunks == null) return;
            int extra = t.ViewChunks.Count - MaxKeptChunks;
            if (extra <= 0) return;
            t.ViewChunks.RemoveRange(0, extra);
            t.OutSync -= extra;
            if (t.OutSync < 0) t.OutSync = 0;
            t.NeedsRebuild = true;
        }

        public static void AppendChunk(MultiProcTask t, OutputChunk c)
        {
            if (t.ViewChunks == null) t.ViewChunks = new List<OutputChunk>();
            t.ViewChunks.Add(c);
            if (t.OutputView != null) Sync(t, t.OutputView);
            TrimKept(t);
        }
    }

    // =======================================================================
    //  分页标签条（自绘）：点选 / 拖动重排 / 横向滚动
    // =======================================================================
    internal sealed class MultiProcTabStrip
    {
        private readonly StackPanel _host;
        private readonly Border _shell;
        private readonly ScrollViewer _scroll;
        private readonly List<MultiProcTask> _tabs = new List<MultiProcTask>();

        public int SelectedIndex = -1;

        /// <summary>点了一下标签（没拖动）。</summary>
        public Action<MultiProcTask> OnSelect;
        /// <summary>把标签拖到了新的位置（参数是任务本身，序号由窗口重排）。</summary>
        public Action<MultiProcTask, int> OnMoveRequested;

        // ---- 拖拽状态 ----
        private const double ClickSlop = 5.0;
        private int _dragIndex = -1;
        private Point _dragStart;
        private Point _dragNow;
        private bool _dragging;
        private int _shownDrop = -2;   // 当前画出来的插入位置
        private int _scrolledTo = -2;  // 已经滚动到可见位置的那个标签序号

        public MultiProcTabStrip()
        {
            _shell = new Border();
            // 不用固定 Height：横向滚动条出现时整条要跟着高一点，
            // 否则滚动条会把标签压掉一截。
            _shell.MinHeight = 34;
            _shell.Padding = new Thickness(6, 4, 6, 0);
            _shell.BorderThickness = new Thickness(0, 0, 0, 1);
            _shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            _shell.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");

            _host = Ui.H();
            _host.ClipToBounds = false;

            // 标签一多就横向滚动：只出横向滚动条，不要纵向的。
            _scroll = new ScrollViewer();
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.CanContentScroll = false;
            _scroll.Focusable = false;
            _scroll.Content = _host;
            _shell.Child = _scroll;

            // 事件挂在标签条壳上（这个类不是 UIElement，本身没有鼠标事件）
            _shell.PreviewMouseLeftButtonDown += OnDown;
            _shell.PreviewMouseMove += OnMove;
            _shell.PreviewMouseLeftButtonUp += OnUp;
            _shell.PreviewMouseWheel += OnWheel;
            _shell.LostMouseCapture += delegate { EndDrag(false); };
        }

        /// <summary>滚轮横着滚标签条（只在真的溢出了才拦截，免得吃掉别的滚动）。</summary>
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_scroll == null) return;
            if (_scroll.ScrollableWidth <= 0.5) return;
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        public UIElement Root { get { return _shell; } }

        public int Count { get { return _tabs.Count; } }

        public MultiProcTask TaskAt(int i)
        {
            if (i < 0 || i >= _tabs.Count) return null;
            return _tabs[i];
        }

        /// <summary>
        /// 重建标签条。注意：tasks 可能就是本类自己的 _tabs（Refresh 走的就是这条路），
        /// 所以必须先把它拷出来再清空 —— 否则 Clear() 之后遍历的是同一个已经被清空的
        /// 列表，标签会全部消失（这个坑真踩过一次）。
        /// </summary>
        public void Rebuild(List<MultiProcTask> tasks, int selected)
        {
            List<MultiProcTask> src = new List<MultiProcTask>();
            if (tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++) src.Add(tasks[i]);
            }

            // 重建会把横向滚动位置冲掉：先记下来，建完再放回去
            double keepOffset = 0;
            try { keepOffset = _scroll.HorizontalOffset; } catch { }

            _host.Children.Clear();
            _tabs.Clear();
            for (int i = 0; i < src.Count; i++) _tabs.Add(src[i]);
            SelectedIndex = selected;
            for (int i = 0; i < _tabs.Count; i++) _host.Children.Add(BuildTab(_tabs[i], i));

            // 选中的标签滚进可见范围。只在「选中的是另一个标签」时才滚：
            // 否则用户手动滚到别处会被硬拽回来。
            if (SelectedIndex != _scrolledTo)
            {
                _scrolledTo = SelectedIndex;
                _shell.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(delegate { ScrollToIndex(_scrolledTo); }));
            }
            else if (keepOffset > 0.5)
            {
                try { _scroll.ScrollToHorizontalOffset(keepOffset); } catch { }
            }
        }

        /// <summary>把第 index 个标签滚到可见范围（标签条溢出时才有意义）。</summary>
        private void ScrollToIndex(int index)
        {
            if (_scroll == null || index < 0 || index >= _host.Children.Count) return;
            FrameworkElement fe = _host.Children[index] as FrameworkElement;
            if (fe == null) return;
            try
            {
                double left = fe.TranslatePoint(new Point(0, 0), _host).X;
                double right = left + fe.ActualWidth;
                double view = _scroll.ViewportWidth;
                if (view <= 0) return;
                double off = _scroll.HorizontalOffset;
                if (left < off) _scroll.ScrollToHorizontalOffset(Math.Max(0, left - 6));
                else if (right > off + view) _scroll.ScrollToHorizontalOffset(right - view + 6);
            }
            catch { }
        }

        /// <summary>
        /// 标签文字 / 状态色点会随任务变化，重建一遍。
        /// 注意两件事：
        ///   1. 这个方法每 90ms 被调用一次（MultiProc.Flush → RefreshTabsAndRows），
        ///      所以先用指纹判断内容有没有变，没变就什么都不做 —— 重建子元素会把
        ///      ScrollViewer 的横向偏移量冲掉，用户手动滚到右边的位置会被立刻弹回最左边
        ///      （横向滚动就是这么被玩坏的，实测踩到过）；
        ///   2. **顺序以 MultiProc.Tasks 为准**：拖动重排改的是那份列表，
        ///      这里要是继续用本类自己那份旧快照，标签看起来就像根本没拖动过。
        /// </summary>
        public void Refresh()
        {
            List<MultiProcTask> live = MultiProc.Tasks;
            string sig = Signature(live);
            if (string.Equals(sig, _lastSig, StringComparison.Ordinal)) return;
            _lastSig = sig;
            Rebuild(live, SelectedIndex);
        }

        /// <summary>标签条内容的指纹：选中项 + 顺序 + 每条任务影响外观的字段。</summary>
        private string Signature(List<MultiProcTask> src)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(SelectedIndex).Append('|');
            if (src != null)
            {
                for (int i = 0; i < src.Count; i++)
                {
                    MultiProcTask t = src[i];
                    if (t == null) { sb.Append("null;"); continue; }
                    sb.Append(t.Id).Append(',')
                      .Append((int)MultiProc.TintOf(t)).Append(',')
                      .Append(t.Multi ? '1' : '0').Append(',')
                      .Append(t.OutputWindow != null ? '1' : '0').Append(',')
                      .Append(t.LastCmd).Append(';');
                }
            }
            return sb.ToString();
        }

        private string _lastSig;

        private UIElement BuildTab(MultiProcTask t, int index)
        {
            bool sel = index == SelectedIndex;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            // 状态色点：绿 = 正在运行，其余按退出码走成功 / 失败 / 灰。
            // 用 Border + Background 画（不是 Ellipse + Fill）：实测 Ellipse 的 Fill
            // 在这个场景里没有跟着画笔键走，屏幕上永远是强调色蓝点，
            // 而 Border.Background 是界面里到处在用的写法，稳定。
            string dotBrush = MultiProc.BrushOf(MultiProc.TintOf(t));
            Border dot = new Border();
            dot.Width = 9;
            dot.Height = 9;
            dot.CornerRadius = new CornerRadius(5);
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(0, 0, 8, 0);
            dot.SetResourceReference(Border.BackgroundProperty, dotBrush);
            g.Children.Add(dot);

            TextBlock txt = Ui.Text(MultiProc.TabLabel(t), 11.5, sel ? "TextBrush" : "TextDimBrush",
                sel ? FontWeights.SemiBold : FontWeights.Normal);
            txt.VerticalAlignment = VerticalAlignment.Center;
            txt.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.MaxWidth = 190;
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);

            if (t.OutputWindow != null)
            {
                // 已经弹出成独立窗口的标签：加一个小标记，免得用户以为它没输出
                TextBlock mark = Ui.Text("↗", 11, "AccentBrush", FontWeights.SemiBold);
                mark.VerticalAlignment = VerticalAlignment.Center;
                mark.Margin = new Thickness(6, 0, 0, 0);
                mark.ToolTip = Loc.T("这条任务的输出已经在独立窗口里显示");
                Grid.SetColumn(mark, 2);
                g.Children.Add(mark);
            }

            Border b = new Border();
            b.Height = 30;
            b.Padding = new Thickness(11, 0, 11, 0);
            b.Margin = new Thickness(0, 0, 4, 0);
            b.Cursor = Cursors.Hand;
            b.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            b.SetResourceReference(Border.BackgroundProperty, sel ? "AccentGhostBrush" : "HoverOverlayBrush");
            if (sel)
            {
                b.BorderThickness = new Thickness(0, 0, 0, 2);
                b.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            }
            b.Child = g;
            b.ToolTip = MultiProc.TabTooltip(t);
            // 右键兜底菜单：拖拽手势不好发现，「弹出为独立窗口 / 合并回标签」得能点出来
            b.ContextMenu = MultiProc.TabMenu(t);
            return b;
        }

        // ==================================================================
        //  鼠标：点选 / 拖动重排
        // ==================================================================

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragIndex = TabIndexAt(e.GetPosition(_shell));
            _dragStart = e.GetPosition(_shell);
            _dragNow = _dragStart;
            _dragging = false;
            _shownDrop = -2;
            if (_dragIndex < 0) return;
            try { Mouse.Capture(_shell); } catch { }
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(true); return; }
            _dragNow = e.GetPosition(_shell);
            if (!_dragging)
            {
                if (Math.Abs(_dragNow.X - _dragStart.X) < ClickSlop && Math.Abs(_dragNow.Y - _dragStart.Y) < ClickSlop)
                    return;
                _dragging = true;
            }
            ShowDropHint();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragIndex < 0) return;
            _dragNow = e.GetPosition(_shell);
            bool moved = _dragging;
            int from = _dragIndex;
            MultiProcTask t = TaskAt(from);
            int target = DropIndex(_dragNow);

            _dragIndex = -1;
            _dragging = false;
            _shownDrop = -2;
            try { if (Mouse.Captured == _shell) Mouse.Capture(null); } catch { }
            if (t == null) return;

            if (!moved)
            {
                // 位移比阈值小 → 当成点选
                SelectedIndex = from;
                if (OnSelect != null) OnSelect(t);
                Refresh();
                return;
            }

            // 拖动只做左右重排：拖到标签条外面不会弹窗、也不会合并，
            // 按最近的落点插回去（弹出独立窗口走右键菜单）。
            if (target >= 0 && target != from && OnMoveRequested != null) OnMoveRequested(t, target);
            Refresh();
            _shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
        }

        private void EndDrag(bool releaseCapture)
        {
            if (releaseCapture)
            {
                try { if (Mouse.Captured == _shell) Mouse.Capture(null); } catch { }
            }
            _dragIndex = -1;
            _dragging = false;
            _shownDrop = -2;
            // 拖动时标签条边框会变成强调色，收尾时要变回去
            try { _shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft"); } catch { }
        }

        private int TabIndexAt(Point p)
        {
            for (int i = 0; i < _host.Children.Count; i++)
            {
                FrameworkElement fe = _host.Children[i] as FrameworkElement;
                if (fe == null) continue;
                double left = 0;
                try { left = fe.TranslatePoint(new Point(0, 0), _shell).X; } catch { }
                if (p.X >= left && p.X <= left + fe.ActualWidth
                    && p.Y >= 0 && p.Y <= fe.ActualHeight) return i;
            }
            return -1;
        }

        /// <summary>松手时标签应该落在第几个位置（0..Count-1）。</summary>
        private int DropIndex(Point p)
        {
            int n = _host.Children.Count;
            if (n == 0) return -1;
            for (int i = 0; i < n; i++)
            {
                FrameworkElement fe = _host.Children[i] as FrameworkElement;
                if (fe == null) continue;
                double left = 0;
                try { left = fe.TranslatePoint(new Point(0, 0), _shell).X; } catch { }
                if (p.X < left + fe.ActualWidth / 2.0) return i;
            }
            return n - 1;
        }

        /// <summary>
        /// 拖动时把「松手会插到哪」表达出来：被拖的标签淡掉 + 标签条边框变强调色。
        /// 现在只有「重排」一种结果，所以提示很固定，不会再出现「吸回 / 弹窗」的歧义。
        /// </summary>
        private void ShowDropHint()
        {
            int idx = DropIndex(_dragNow);
            if (idx == _shownDrop) return;
            _shownDrop = idx;

            for (int i = 0; i < _host.Children.Count; i++)
            {
                FrameworkElement fe = _host.Children[i] as FrameworkElement;
                if (fe == null) continue;
                fe.Opacity = (i == _dragIndex) ? 0.40 : 1.0;
            }
            _shell.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        }
    }

    // =======================================================================
    //  分离出来的输出窗口（可缩放、可最大化）
    // =======================================================================
    internal sealed class OutputWindow : Window
    {
        private readonly MultiProcTask _task;
        private readonly MultiProcOutputView _view;
        private readonly TextBlock _title;
        private readonly TextBlock _status;
        private readonly Ellipse _dot;
        private bool _closing;

        public MultiProcTask Task { get { return _task; } }
        public MultiProcOutputView View { get { return _view; } }

        public OutputWindow(MultiProcTask t)
        {
            _task = t;
            Title = MultiProc.TabLabel(t);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 420;
            MinHeight = 240;
            Width = 720;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Fonts.Ui;
            FontSize = 13;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = 38;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            Border shell = new Border();
            shell.BorderThickness = new Thickness(1);
            shell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushHard");
            shell.SetResourceReference(Border.BackgroundProperty, "SurfaceSolidBrush");

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions[0].Height = GridLength.Auto;
            root.RowDefinitions.Add(new RowDefinition());

            // ---------- 标题栏 ----------
            Grid bar = new Grid();
            bar.Height = 38;
            bar.Margin = new Thickness(10, 0, 0, 0);
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions[0].Width = GridLength.Auto;
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions[2].Width = GridLength.Auto;

            _dot = new Ellipse();
            _dot.Width = 9; _dot.Height = 9;
            _dot.VerticalAlignment = VerticalAlignment.Center;
            _dot.Margin = new Thickness(0, 0, 8, 0);
            _dot.SetResourceReference(Shape.FillProperty, "TextFaintBrush");
            bar.Children.Add(_dot);

            _title = Ui.Text(MultiProc.TabLabel(t), 13, "TextBrush", FontWeights.SemiBold);
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_title, 0);
            bar.Children.Add(_title);

            _status = Ui.Text("", 11.5, "TextFaintBrush");
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(_status, 1);
            bar.Children.Add(_status);

            StackPanel btns = Ui.H();
            btns.VerticalAlignment = VerticalAlignment.Center;
            WindowChrome.SetIsHitTestVisibleInChrome(btns, true);

            Button merge = Ui.Btn(Loc.T("合并回标签"), "ChipButton", delegate { MultiProc.MergeTask(_task); });
            merge.Height = 24;
            merge.FontSize = 11;
            merge.Margin = new Thickness(0, 0, 6, 0);
            merge.ToolTip = Loc.T("把这个输出合并回总窗口的标签页（也可以右键标签选「合并回标签」）");
            btns.Children.Add(merge);

            Button min = Ui.IconBtn("min", Loc.T("最小化"), delegate { WindowState = WindowState.Minimized; });
            min.Width = 30; min.Height = 26;
            btns.Children.Add(min);

            Button max = Ui.IconBtn("max", Loc.T("最大化 / 还原"), delegate
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            });
            max.Width = 30; max.Height = 26;
            btns.Children.Add(max);

            Button close = Ui.IconBtn("close", Loc.T("关闭这个输出窗口（任务本身不会被停止）"), delegate { Close(); });
            close.Width = 30; close.Height = 26;
            btns.Children.Add(close);

            Grid.SetColumn(btns, 2);
            bar.Children.Add(btns);

            // 标题栏空白处拖动窗口；按在按钮上不能拖（否则点「关闭」会变成拖窗口）
            bar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); }
                catch { }
            };
            root.Children.Add(bar);
            Grid.SetRow(bar, 0);

            // ---------- 输出 ----------
            Border outShell = new Border();
            outShell.Margin = new Thickness(8, 0, 8, 8);
            outShell.BorderThickness = new Thickness(1);
            outShell.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
            outShell.SetResourceReference(Border.BackgroundProperty, "ConsoleBgBrush");
            outShell.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
            _view = new MultiProcOutputView();
            outShell.Child = _view.Box;
            root.Children.Add(outShell);
            Grid.SetRow(outShell, 1);

            shell.Child = root;
            Content = shell;

            StateChanged += delegate
            {
                if (max != null) max.Content = Icons.Create(
                    WindowState == WindowState.Maximized ? "restore" : "max", 15, "TextDimBrush", 1.6);
            };
            SourceInitialized += delegate { WindowFx.ApplyRoundedCorners(this, 10, !Themes.Current.IsLight); };
            Closed += OnClosed;
            PreviewKeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };

            _view.Attach(t);
            t.OutputWindow = this;
            RefreshState();
        }

        /// <summary>鼠标事件源是不是落在按钮上（沿视觉树往上找）。</summary>
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

        public void RefreshState()
        {
            if (_task == null) return;
            _title.Text = MultiProc.TabLabel(_task);
            Title = _title.Text;
            _status.Text = MultiProc.StatusText(_task);
            string brush = MultiProc.BrushOf(MultiProc.TintOf(_task));
            _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (_dot != null) _dot.SetResourceReference(Shape.FillProperty, brush);
        }

        public void SyncOutput()
        {
            MultiProcOutputView.Sync(_task, _view);
        }

        /// <summary>任务被删掉时由控制器调用，不再走「关窗」那套善后。</summary>
        public void CloseForRemoval()
        {
            _closing = true;
            _view.Detach();
            try { Close(); } catch { }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_task != null && _task.OutputWindow == this) _task.OutputWindow = null;
            if (_view != null) _view.Detach();
            if (_closing) return;
            // 用户手动关的：任务还在跑就说明「任务还活着，只是不想看输出了」
            if (_task != null && _task.Slot != null && _task.Slot.IsRunning)
                MultiProc.Write(_task, Loc.T("[多进程] 输出窗口已关闭，任务仍在继续运行。重新打开标签即可继续查看。"), true);
        }
    }
}
