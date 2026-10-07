// ---------------------------------------------------------------------------
//  MultiCmd.cs — 多命令执行模式
//
//  勾选输入框旁边的「多命令」后，单行输入框就地换成一块多行文本区
//  （高度在设置里可调），可以把一整段脚本粘进去一次执行 ——
//  就像在记事本里编辑好再执行一样。
//
//  整段文本原样交给 cmd / PowerShell，只起一个进程，
//  所以 cd、变量、管道这些上下文在行与行之间是连着的。
//
//  持久性由设置「多命令执行持久生效」控制（默认关闭）：
//    · 关闭：只生效一次，执行完自动回到单行输入框
//    · 打开：一直停留在多行模式，直到手动关掉
// ---------------------------------------------------------------------------
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsCommandTools
{
    public partial class MainWindow
    {
        /// <summary>多行输入框（单行模式下隐藏）。</summary>
        internal TextBox MultiInput;
        /// <summary>当前是不是多命令模式。</summary>
        internal bool UseMultiCommand;
        /// <summary>提示行右侧那个「多命令」开关。</summary>
        internal Button MultiToggle;

        private TextBlock _multiPrompt;
        private readonly System.Collections.Generic.List<Button> _inputRowButtons =
            new System.Collections.Generic.List<Button>();

        // ==================================================================
        //  当前正在编辑的输入框（单行 / 多行统一抽象）
        // ==================================================================

        internal TextBox ActiveEditor
        {
            get { return UseMultiCommand && MultiInput != null ? MultiInput : Input; }
        }

        /// <summary>光标所在那一行的范围（不含行尾的 \r）。</summary>
        private static void LineAt(TextBox tb, out int start, out int end)
        {
            string t = tb.Text ?? "";
            int c = tb.CaretIndex;
            if (c < 0) c = 0;
            if (c > t.Length) c = t.Length;

            start = 0;
            if (t.Length > 0)
            {
                int probe = c > 0 ? c - 1 : 0;
                int nl = t.LastIndexOf('\n', probe);
                if (nl >= 0) start = nl + 1;
            }
            end = t.IndexOf('\n', start);
            if (end < 0) end = t.Length;
            if (end > start && t[end - 1] == '\r') end--;
        }

        /// <summary>光标所在行的文本。</summary>
        internal string EditorLine()
        {
            int s, e;
            LineAt(ActiveEditor, out s, out e);
            return ActiveEditor.Text.Substring(s, e - s);
        }

        /// <summary>光标在这行里的偏移。</summary>
        internal int EditorCaretInLine()
        {
            int s, e;
            LineAt(ActiveEditor, out s, out e);
            int c = ActiveEditor.CaretIndex - s;
            if (c < 0) c = 0;
            if (c > e - s) c = e - s;
            return c;
        }

        /// <summary>用新内容替换光标所在行，并把光标放到行内指定位置。</summary>
        internal void ReplaceEditorLine(string newLine, int caretInLine)
        {
            TextBox tb = ActiveEditor;
            int s, e;
            LineAt(tb, out s, out e);
            string t = tb.Text ?? "";
            string updated = t.Substring(0, s) + newLine + t.Substring(e);
            tb.Text = updated;
            int pos = s + caretInLine;
            if (pos < 0) pos = 0;
            if (pos > updated.Length) pos = updated.Length;
            tb.CaretIndex = pos;
        }

        // ==================================================================
        //  多命令开关
        // ==================================================================

        internal void SetMultiCommand(bool on)
        {
            if (MultiInput == null) return;

            if (on)
            {
                // 首次进入时把上次留下的内容放回来
                if (string.IsNullOrEmpty(MultiInput.Text) && !string.IsNullOrEmpty(Settings.MultiCommandText))
                    MultiInput.Text = Settings.MultiCommandText;

                UseMultiCommand = true;
                MultiInput.Visibility = Visibility.Visible;
                Input.Visibility = Visibility.Collapsed;
                if (_inputPlaceholder != null) _inputPlaceholder.Visibility = Visibility.Collapsed;
                if (_multiPrompt != null)
                {
                    _multiPrompt.VerticalAlignment = VerticalAlignment.Top;
                    _multiPrompt.Margin = new Thickness(13, 12, 0, 0);
                }
                ApplyMultiHeight();
                foreach (Button b in _inputRowButtons) b.VerticalAlignment = VerticalAlignment.Top;

                UpdateMultiToggle();
                MultiInput.Focus();
                MultiInput.CaretIndex = MultiInput.Text.Length;
            }
            else
            {
                // 回单行时把内容记下来，下次勾选还在
                if (MultiInput.Text != null) Settings.MultiCommandText = MultiInput.Text;

                UseMultiCommand = false;
                MultiInput.Visibility = Visibility.Collapsed;
                Input.Visibility = Visibility.Visible;
                if (InputHost != null) InputHost.ClearValue(FrameworkElement.HeightProperty);
                if (_multiPrompt != null)
                {
                    _multiPrompt.VerticalAlignment = VerticalAlignment.Center;
                    _multiPrompt.Margin = new Thickness(13, 0, 0, 0);
                }
                foreach (Button b in _inputRowButtons) b.VerticalAlignment = VerticalAlignment.Center;
                UpdateMultiToggle();
                Input.Focus();
            }
            SetSuggestVisible(false);
            UpdateSuggestions();
        }

        /// <summary>把设置里的高度应用到多行输入区。</summary>
        internal void ApplyMultiHeight()
        {
            if (MultiInput == null) return;
            double h = Settings.MultiCommandHeight;
            if (h < 72) h = 72;
            if (h > 600) h = 600;
            if (UseMultiCommand && InputHost != null) InputHost.Height = h;
            MultiInput.MinHeight = 72;
        }

        internal void UpdateMultiToggle()
        {
            if (MultiToggle == null) return;
            MultiToggle.Content = UseMultiCommand ? Loc.T("多命令 · 开") : Loc.T("多命令");
            MultiToggle.FontWeight = UseMultiCommand ? FontWeights.SemiBold : FontWeights.Normal;
            if (UseMultiCommand)
            {
                MultiToggle.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                MultiToggle.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
            }
            else
            {
                MultiToggle.SetResourceReference(Control.BackgroundProperty, "HoverOverlayBrush");
                MultiToggle.SetResourceReference(Control.ForegroundProperty, "TextDimBrush");
            }
            MultiToggle.ToolTip = UseMultiCommand
                ? (Settings.MultiCommandPersist
                    ? Loc.T("多命令执行已开启（持久生效）。点这里回到单行输入框。")
                    : Loc.T("多命令执行只生效一次，执行后会自动回到单行输入框（可在设置里改成持久生效）。点这里立即关闭。"))
                : Loc.T("打开多命令执行：输入框变成多行文本区，可以把一整段命令粘进去一次执行。高度可在设置里调。");
        }

        /// <summary>执行完一次之后，按持久化设置决定要不要退回单行。</summary>
        internal void AfterMultiCommandRun()
        {
            if (!UseMultiCommand) return;
            if (!Settings.MultiCommandPersist) SetMultiCommand(false);
        }

        // ==================================================================
        //  多行输入框的键盘处理
        // ==================================================================

        private void OnMultiInputKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // 多行模式下 Enter 就是换行（记事本习惯），执行用 Ctrl+Enter 或点「运行」
            if (e.Key == Key.Enter)
            {
                if (ctrl)
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
                if (SuggestVisible && Navigated && SuggestList.SelectedIndex >= 0)
                {
                    ApplySuggestion(SuggestList.SelectedIndex, false);
                    e.Handled = true;
                    return;
                }
                return;   // 交给 TextBox 自己插入换行
            }

            // 其余按键（Tab / ↑↓ / Esc / Ctrl+Space）沿用单行模式那一套
            OnInputKeyDown(sender, e);
        }
    }
}
