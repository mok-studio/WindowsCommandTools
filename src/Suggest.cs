// ---------------------------------------------------------------------------
//  Suggest.cs — 智能参数提示引擎（本工具的核心）
//
//  职责：给定「当前命令行 + 光标位置」，算出下一个该输入什么。
//    · 第 1 个词         → 提示命令名（命令库全文检索 + 拼写纠错）
//    · 后续词            → 沿命令的参数树逐级下行，提示下一级参数 / 子命令 / 取值
//    · 参数需要值的时候  → 提示值占位说明，或枚举候选，或做文件/目录路径补全
//    · 已用过的参数      → 自动灰掉，不再重复提示（repeatable 的除外）
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindowsCommandTools
{
    public sealed class Token
    {
        public string Raw = "";    // 原样（可能带引号）
        public string Bare = "";   // 去掉外层引号
        public int Start;
        public int End;            // 不含
        public bool Quoted;
        public bool OpenQuote;     // 只有左引号没有右引号
    }

    public static class Lexer
    {
        /// <summary>按 cmd 的规则分词：空格分隔，双引号内不切分。</summary>
        public static List<Token> Tokenize(string line)
        {
            List<Token> tokens = new List<Token>();
            if (string.IsNullOrEmpty(line)) return tokens;
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i >= line.Length) break;
                int start = i;
                bool quoted = false;
                bool open = false;
                StringBuilder raw = new StringBuilder();
                while (i < line.Length && (quoted || (line[i] != ' ' && line[i] != '\t')))
                {
                    char c = line[i];
                    if (c == '"')
                    {
                        quoted = !quoted;
                        if (quoted) { if (start == i) { /* 起始引号 */ } }
                        raw.Append(c);
                        i++;
                        continue;
                    }
                    raw.Append(c);
                    i++;
                }
                open = quoted;
                Token t = new Token();
                t.Raw = raw.ToString();
                t.Start = start;
                t.End = i;
                t.OpenQuote = open;
                string bare = t.Raw;
                if (bare.Length >= 2 && bare[0] == '"' && bare[bare.Length - 1] == '"')
                {
                    bare = bare.Substring(1, bare.Length - 2);
                    t.Quoted = true;
                }
                else if (bare.Length >= 1 && bare[0] == '"')
                {
                    bare = bare.Substring(1);
                }
                t.Bare = bare;
                tokens.Add(t);
            }
            return tokens;
        }

        public static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
            if (s.Length >= 1 && s[0] == '"') return s.Substring(1);
            return s;
        }
    }

    public enum SuggestKind
    {
        Command, SubCommand, Flag, Option, Value, EnumValue, PathDir, PathFile, Hint, Syntax
    }

    public sealed class Suggestion
    {
        public string Insert = "";      // 按 Tab/回车 时插入的文本
        public string Display = "";     // 列表中显示的主文本
        public SuggestKind Kind = SuggestKind.Value;
        public string Desc = "";
        public string ValueHint = "";
        public bool Used;

        /// <summary>
        /// 这个候选代表「这条命令属于另一个工具」。
        /// 点它要**真正切换工具**（ApplySuggestion 据此走切换路径），
        /// 而不是像普通候选那样只把文本插进当前模式 —— 只改 Display 文案是没用的。
        /// </summary>
        public bool SwitchShell;

        /// <summary>SwitchShell 为真时，要切到哪个工具。</summary>
        public ShellKind SwitchToShell;
        public string Badge
        {
            get
            {
                switch (Kind)
                {
                    case SuggestKind.Command: return "命令";
                    case SuggestKind.SubCommand: return "子命令";
                    case SuggestKind.Flag: return "开关";
                    case SuggestKind.Option: return "参数";
                    case SuggestKind.Value: return "值";
                    case SuggestKind.EnumValue: return "取值";
                    case SuggestKind.PathDir: return "目录";
                    case SuggestKind.PathFile: return "文件";
                    case SuggestKind.Syntax: return "语法";
                    default: return "提示";
                }
            }
        }
    }

    public sealed class SuggestResult
    {
        public List<Suggestion> Items = new List<Suggestion>();
        public string CommandName = "";
        public CmdSpec Spec;
        public List<ParamNode> CurrentLevel = new List<ParamNode>();
        public ParamNode ValueOwner;
        public List<string> Trail = new List<string>();
        public string StatusText = "";
        public string HintTitle = "";
        public string HintText = "";
        public bool IsCommandPosition;
        public bool Retried;
        public ShellKind Shell = ShellKind.Cmd;
        /// <summary>只有当命令「只属于另一个 shell」时才非空，用于跨工具提示。</summary>
        public CmdSpec OtherShellSpec;
        public string OtherShellHint = "";
        /// <summary>输入里是否用了管道（提示的是管道后那一段）。</summary>
        public bool InPipeline;

        public string TrailText
        {
            get { return Trail.Count == 0 ? "" : string.Join(" › ", Trail.ToArray()); }
        }
    }

    public static class Suggester
    {
        private const int MaxItems = 40;

        /// <summary>
        /// 「按语法顺序限制提示参数」。由界面层注入（引擎不该直接依赖 AppSettings）。
        /// 打开后只列出当前语法位置合法的参数；关闭则全部列出、用过的标灰。
        /// </summary>
        public static bool StrictOrder;

        public static SuggestResult Compute(CommandLibrary lib, string line, int caret, string cwd, ShellKind shell)
        {
            if (line == null) line = "";
            if (caret < 0) caret = 0;
            if (caret > line.Length) caret = line.Length;

            // ---- 0. 管道分段：`Get-Process | Where-Object ` 要提示管道后那一段 ----
            // 只看光标之前最后一个未被引号包住的竖线。
            int pipe = LastPipeBefore(line, caret);
            if (pipe >= 0)
            {
                string seg = line.Substring(pipe + 1);
                int segCaret = caret - pipe - 1;
                SuggestResult pr = ComputeSegment(lib, seg, segCaret, cwd, shell, true);
                pr.InPipeline = true;
                pr.Trail.Insert(0, "|");
                return pr;
            }
            return ComputeSegment(lib, line, caret, cwd, shell, false);
        }

        /// <summary>管道里最常用的命令，输入 `| ` 时优先列出来。</summary>
        private static readonly string[] PipelineFavorites = new string[] {
            "Where-Object", "Select-Object", "ForEach-Object", "Sort-Object", "Group-Object",
            "Measure-Object", "Select-String", "Get-Member", "Format-Table", "Format-List",
            "Out-String", "Out-File", "Out-Null", "Tee-Object", "Export-Csv", "ConvertTo-Json",
            "ConvertTo-Csv", "Compare-Object", "Add-Member", "Out-GridView"
        };

        /// <summary>光标之前最后一个不在引号里的竖线的位置，没有则返回 -1。</summary>
        public static int LastPipeBefore(string line, int caret)
        {
            bool quoted = false;
            int last = -1;
            for (int i = 0; i < caret && i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '|') last = i;
            }
            return last;
        }

        private static SuggestResult ComputeSegment(CommandLibrary lib, string line, int caret, string cwd,
            ShellKind shell, bool inPipeline)
        {
            SuggestResult first = ComputeCore(lib, line, caret, cwd, false, shell, inPipeline);

            // 两段式判定：光标停在某个完整词的末尾时，如果这个词和当前层的任何候选都不构成
            // 前缀关系，说明它不是"正在输入的半截词"，而是一个已经写好的参数
            // （例如 ping 1.1.1.1 后面要接新参数）。这时按"词已完成"重新算一次。
            if (!first.IsCommandPosition && first.CommandName.Length > 0 && !first.Retried)
            {
                string word = CurrentWord(line, caret);
                if (word.Length > 0 && !AnyStartsWith(first.Items, word))
                {
                    SuggestResult second = ComputeCore(lib, line, caret, cwd, true, shell, inPipeline);
                    second.Retried = true;
                    if (second.Items.Count > 0 || first.Items.Count == 0) return second;
                }
            }
            return first;
        }

        public static SuggestResult ComputeCore(CommandLibrary lib, string line, int caret, string cwd,
            bool completeLastWord, ShellKind shell)
        {
            return ComputeCore(lib, line, caret, cwd, completeLastWord, shell, false);
        }

        public static SuggestResult ComputeCore(CommandLibrary lib, string line, int caret, string cwd,
            bool completeLastWord, ShellKind shell, bool inPipeline)
        {
            if (line == null) line = "";
            if (caret < 0) caret = 0;
            if (caret > line.Length) caret = line.Length;

            SuggestResult result = new SuggestResult();
            result.Shell = shell;
            List<Token> tokens = Lexer.Tokenize(line);

            // ---- 1. 判断光标落在哪个词上 ----
            int editIndex = -1;
            int prefixStart = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (caret > tokens[i].Start && caret <= tokens[i].End) { editIndex = i; prefixStart = tokens[i].Start; break; }
            }

            List<Token> completed = new List<Token>();
            string prefix;
            bool prefixQuoted = false;

            if (editIndex >= 0)
            {
                bool wholeWord = completeLastWord && editIndex == tokens.Count - 1 && caret == tokens[editIndex].End;
                int upTo = wholeWord ? editIndex + 1 : editIndex;
                for (int i = 0; i < upTo; i++) completed.Add(tokens[i]);
                prefix = wholeWord ? "" : line.Substring(prefixStart, caret - prefixStart);
                prefixQuoted = prefix.StartsWith("\"");
            }
            else
            {
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (tokens[i].End <= caret) completed.Add(tokens[i]);
                }
                prefix = "";
            }

            string barePrefix = Lexer.Unquote(prefix);

            // ---- 2. 第一个词：提示命令名 ----
            if (completed.Count == 0)
            {
                result.IsCommandPosition = true;
                result.HintTitle = Loc.T("输入命令名");
                result.HintText = Loc.T("直接输入命令的第一个字母即可看到候选，Tab 补全、↑↓ 选择、Enter 执行。");
                AddCommandCandidates(lib, shell, barePrefix, cwd, prefixQuoted, result, inPipeline);
                return result;
            }

            string cmdName = completed[0].Bare;
            result.CommandName = cmdName;
            CmdSpec spec = lib.Find(shell, cmdName);
            result.Spec = spec;

            if (spec == null)
            {
                // 跨工具识别：这条命令只存在于另一个 shell 里
                CmdSpec other = lib.FindInOther(shell, cmdName);
                if (other != null)
                {
                    result.OtherShellSpec = other;
                    result.OtherShellHint = cmdName + " 是 " + Shells.Display(other.Shell) + " 命令";
                    result.HintTitle = cmdName + " 属于 " + Shells.Display(other.Shell);
                    result.HintText = Loc.T("当前是 ") + Shells.Display(shell) + " 模式，执行会失败。可以点提示条一键切换后重新执行。";
                    Suggestion sw = new Suggestion();
                    sw.Insert = cmdName; sw.Display = cmdName; sw.Kind = SuggestKind.Command;
                    sw.SwitchShell = true; sw.SwitchToShell = other.Shell;
                    sw.Desc = "切换到 " + Shells.Display(other.Shell) + " 执行（" + other.Title + "）";
                    result.Items.Add(sw);
                    return result;
                }

                result.HintTitle = Loc.T("命令库中没有 ") + cmdName;
                result.HintText = Loc.T("仍然可以直接执行它。参数提示仅对命令库中已有的命令生效。");
                string near = NearestCommand(lib, shell, cmdName);
                if (near != null)
                {
                    Suggestion s = new Suggestion();
                    s.Insert = near; s.Display = near; s.Kind = SuggestKind.Command;
                    CmdSpec ns = lib.Find(shell, near);
                    s.Desc = "你是不是想输入 " + near + (ns != null && ns.Title.Length > 0 ? "（" + ns.Title + "）" : "") + "？";
                    result.Items.Add(s);
                }
                return result;
            }

            result.Trail.Add(spec.Name);

            // ---- 3. 沿参数树下行 ----
            // PowerShell 命令的顶层用 NodesEx（自带通用参数），这样 `-ErrorAction ` 也能提示取值
            List<ParamNode> level = spec.NodesEx;
            Dictionary<ParamNode, int> used = new Dictionary<ParamNode, int>();
            ParamNode valueOwner = null;
            bool expectingValue = false;

            for (int k = 1; k < completed.Count; k++)
            {
                string t = completed[k].Bare;
                if (expectingValue)
                {
                    expectingValue = false;
                    valueOwner = null;
                    continue;
                }
                ParamNode m = FindLiteral(level, t, used);
                if (m == null)
                {
                    // 谁都不匹配 → 判定为位置参数，占用第一个还没用过的 value 节点
                    ParamNode v = FindUnusedValue(level, used);
                    if (v != null) Bump(used, v);
                    continue;
                }
                Bump(used, m);
                if (m.IsSubCommand)
                {
                    result.Trail.Add(m.Token);
                    if (m.Children.Count > 0) level = m.Children;
                }
                else if (m.IsOption)
                {
                    expectingValue = true;
                    valueOwner = m;
                }
            }

            result.CurrentLevel = level;
            result.ValueOwner = valueOwner;

            // ---- 4. 生成候选 ----
            if (expectingValue && valueOwner != null)
            {
                // 正在输入某个参数的值：优先给出这个参数自带的取值候选（带中文说明）
                foreach (ParamNode child in valueOwner.Children)
                {
                    if (barePrefix.Length > 0 && !child.Token.StartsWith(barePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Suggestion s = new Suggestion();
                    s.Insert = child.Token;
                    s.Display = child.Token;
                    s.Kind = NodeKind(child);
                    if (s.Kind == SuggestKind.Flag || s.Kind == SuggestKind.Option) s.Kind = SuggestKind.EnumValue;
                    s.Desc = child.Desc;
                    s.ValueHint = child.ValueHint;
                    result.Items.Add(s);
                    if (result.Items.Count >= MaxItems) break;
                }

                // 再补上 enum 里没有出现的取值
                if (valueOwner.Enum.Count > 0)
                {
                    foreach (string e in valueOwner.Enum)
                    {
                        if (barePrefix.Length > 0 && !e.StartsWith(barePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        bool dup = false;
                        foreach (Suggestion exist in result.Items)
                        {
                            if (string.Equals(exist.Display, e, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        }
                        if (dup) continue;
                        Suggestion item = new Suggestion();
                        item.Insert = e; item.Display = e; item.Kind = SuggestKind.EnumValue;
                        item.Desc = valueOwner.Desc;
                        result.Items.Add(item);
                    }
                }

                if (result.Items.Count == 0)
                {
                    result.HintTitle = Loc.T("请输入 ") + (valueOwner.ValueHint.Length > 0 ? valueOwner.ValueHint : "值");
                    result.HintText = valueOwner.Token + " — " + valueOwner.Desc;
                }
                else
                {
                    result.HintTitle = valueOwner.Token + " 的取值";
                    result.HintText = valueOwner.Desc + "   （Tab 补全，Enter 直接执行）";
                }
                if (LooksLikePathNode(valueOwner) || LooksLikePathPrefix(barePrefix))
                    AddPathCandidates(barePrefix, cwd, prefixQuoted, false, result, valueOwner);
                return result;
            }

            // 正常位置：列出当前层的候选项
            List<ParamNode> matched = new List<ParamNode>();
            List<ParamNode> fuzzy = new List<ParamNode>();
            foreach (ParamNode node in level)
            {
                int u;
                bool isUsed = used.TryGetValue(node, out u) && u > 0;
                if (isUsed && !node.Repeatable) continue;
                if (barePrefix.Length == 0)
                {
                    matched.Add(node);
                }
                else if (node.Token.StartsWith(barePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(node);
                }
                else if (node.Token.IndexOf(barePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fuzzy.Add(node);
                }
            }
            if (matched.Count == 0) matched.AddRange(fuzzy);

            // 「按语法顺序限制提示参数」（设置里默认关闭）：
            //   打开 -> 只保留当前语法位置**合法**的候选，避免选出跑不通的组合
            //   关闭 -> 保持原样，已用过的参数仍然列出并标灰「已使用」
            if (StrictOrder)
                matched = FilterBySyntaxOrder(spec, matched, used);

            // 提示顺序 = 命令库 JSON 里 nodes 的书写顺序。
            // 数据层已经按 cmd 的书写顺序规范过（位置参数 → 子命令 → 语法片段 → 开关/参数），
            // 这里不再二次排序：这样「你按什么顺序写，就按什么顺序提示」，可预期、可控制。
            foreach (ParamNode node in matched)
            {
                int u;
                bool isUsed = used.TryGetValue(node, out u) && u > 0;
                Suggestion s = new Suggestion();
                s.Insert = node.Token;
                s.Display = node.Token;
                s.Kind = NodeKind(node);
                s.Desc = node.Desc;
                s.ValueHint = node.ValueHint;
                s.Used = isUsed;
                result.Items.Add(s);
                if (result.Items.Count >= MaxItems) break;
            }

            // 位置参数 / 路径补全
            if (LooksLikePathPrefix(barePrefix))
                AddPathCandidates(barePrefix, cwd, prefixQuoted, false, result, null);

            // PowerShell 的通用参数已经并进 NodesEx，这里不需要再补
            if (result.Items.Count == 0)
            {
                ParamNode v = FindUnusedValue(level, used);
                if (v != null)
                {
                    result.HintTitle = Loc.T("请输入 ") + (v.ValueHint.Length > 0 ? v.ValueHint : v.Token);
                    result.HintText = v.Desc;
                }
                else
                {
                    result.HintTitle = Loc.T("没有更多可用参数");
                    result.HintText = Loc.T("可以直接执行，或按 Enter 运行。");
                }
            }
            else
            {
                result.HintTitle = result.TrailText + " 的可用参数";
                result.HintText = Loc.T("Tab 补全当前项，↑↓ 选择，Enter 直接执行。");
            }
            return result;
        }

        // ------------------------------------------------------------------
        //  候选项工具
        // ------------------------------------------------------------------

        private static SuggestKind NodeKind(ParamNode n)
        {
            if (n.IsSubCommand) return SuggestKind.SubCommand;
            if (n.IsOption) return SuggestKind.Option;
            if (n.IsValue) return SuggestKind.Value;
            if (n.IsSyntax) return SuggestKind.Syntax;
            return SuggestKind.Flag;
        }

        /// <summary>提示顺序权重：位置参数 → 子命令 → 语法片段 → 开关/参数（仅供数据规范化脚本参考）</summary>
        /// <summary>
        /// 按语法顺序过滤候选。
        ///
        /// 规则：
        ///   1. 已经用过、且不能重复的参数直接去掉（普通模式下它们是标灰显示）；
        ///   2. 只要还有**必填的位置参数**没填，就只提示这些位置参数 ——
        ///      否则用户可能在 `<源>` 还没给的情况下先选 `-i`，拼出来的命令语法不合法。
        ///
        /// 「必填」的判定：该 value 节点的 token 出现在 usage 里且不在方括号内
        /// （方括号表示可选，比如 `[&lt;间隔&gt;]`）。
        /// </summary>
        private static List<ParamNode> FilterBySyntaxOrder(CmdSpec spec, List<ParamNode> nodes,
            Dictionary<ParamNode, int> used)
        {
            List<ParamNode> keep = new List<ParamNode>();
            foreach (ParamNode n in nodes)
            {
                int u;
                bool isUsed = used.TryGetValue(n, out u) && u > 0;
                if (isUsed && !n.Repeatable) continue;
                keep.Add(n);
            }

            List<ParamNode> required = new List<ParamNode>();
            foreach (ParamNode n in keep)
            {
                int u;
                bool isUsed = used.TryGetValue(n, out u) && u > 0;
                if (!isUsed && IsRequiredValue(spec, n)) required.Add(n);
            }
            if (required.Count > 0) return required;
            return keep;
        }

        /// <summary>这个 value 节点在 usage 里是不是必填（不在方括号内）。</summary>
        private static bool IsRequiredValue(CmdSpec spec, ParamNode n)
        {
            if (spec == null || n == null || !n.IsValue) return false;
            try
            {
                string usage = spec.Usage;
                if (string.IsNullOrEmpty(usage)) return false;
                int at = usage.IndexOf(n.Token, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                int depth = 0;
                for (int k = 0; k < at; k++)
                {
                    if (usage[k] == '[') depth++;
                    else if (usage[k] == ']') { if (depth > 0) depth--; }
                }
                return depth == 0;
            }
            catch { return false; }
        }

        public static int Rank(ParamNode n)
        {
            if (n.IsValue) return 0;
            if (n.IsSubCommand) return 1;
            if (n.IsSyntax) return 2;
            return 3;
        }

        public static bool AnyStartsWith(List<Suggestion> items, string word)
        {
            if (items == null || word == null || word.Length == 0) return false;
            foreach (Suggestion s in items)
            {
                if (s.Kind == SuggestKind.Hint) continue;
                if (s.Insert != null && s.Insert.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>取光标所在的那个词（去掉外层引号）；光标处于空白处时返回空串。</summary>
        public static string CurrentWord(string line, int caret)
        {
            if (string.IsNullOrEmpty(line)) return "";
            if (caret > line.Length) caret = line.Length;
            List<Token> tokens = Lexer.Tokenize(line);
            foreach (Token t in tokens)
            {
                if (caret > t.Start && caret <= t.End)
                    return Lexer.Unquote(line.Substring(t.Start, caret - t.Start));
            }
            return "";
        }

        private static ParamNode FindLiteral(List<ParamNode> level, string text, Dictionary<ParamNode, int> used)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // 先找没有用过的
            foreach (ParamNode n in level)
            {
                if (n.IsValue) continue;
                if (string.Equals(n.Token, text, StringComparison.OrdinalIgnoreCase))
                {
                    int c; used.TryGetValue(n, out c);
                    if (c == 0 || n.Repeatable) return n;
                }
            }
            // 允许重复的再找一遍
            foreach (ParamNode n in level)
            {
                if (n.IsValue) continue;
                if (n.Repeatable && string.Equals(n.Token, text, StringComparison.OrdinalIgnoreCase)) return n;
            }
            return null;
        }

        private static ParamNode FindUnusedValue(List<ParamNode> level, Dictionary<ParamNode, int> used)
        {
            foreach (ParamNode n in level)
            {
                if (!n.IsValue) continue;
                int c; used.TryGetValue(n, out c);
                if (c == 0 || n.Repeatable) return n;
            }
            return null;
        }

        private static void Bump(Dictionary<ParamNode, int> used, ParamNode n)
        {
            int c;
            used.TryGetValue(n, out c);
            used[n] = c + 1;
        }

        // ------------------------------------------------------------------
        //  命令名候选
        // ------------------------------------------------------------------

        private static void AddCommandCandidates(CommandLibrary lib, ShellKind shell, string prefix, string cwd,
            bool quoted, SuggestResult result, bool inPipeline)
        {
            if (LooksLikePathPrefix(prefix))
            {
                AddPathCandidates(prefix, cwd, quoted, true, result, null);
                if (result.Items.Count > 0) return;
            }

            // 刚敲完 `|`：把管道里最常用的命令排到最前面，否则它们埋在 200 多条命令里找不到
            if (inPipeline && prefix.Length == 0 && shell == ShellKind.PowerShell)
            {
                foreach (string name in PipelineFavorites)
                {
                    CmdSpec fav = lib.Find(shell, name);
                    if (fav == null) continue;
                    Suggestion sf = new Suggestion();
                    sf.Insert = fav.Name;
                    sf.Display = fav.Name;
                    sf.Kind = SuggestKind.Command;
                    sf.Desc = "管道常用 · " + fav.Title;
                    result.Items.Add(sf);
                }
                result.HintTitle = Loc.T("管道后面可以接这些命令");
                result.HintText = Loc.T("也可以用 ↑↓ 继续找其它命令；Tab 补全，Enter 执行整行。");
            }

            List<CmdSpec> specs = lib.ByNamePrefix(shell, prefix, MaxItems);
            foreach (CmdSpec s in specs)
            {
                bool already = false;
                foreach (Suggestion exist in result.Items)
                {
                    if (string.Equals(exist.Display, s.Name, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                }
                if (already) continue;
                Suggestion sg = new Suggestion();
                sg.Insert = s.Name;
                sg.Display = s.Name;
                sg.Kind = SuggestKind.Command;
                sg.Desc = s.Title;
                string alias = s.AliasText();
                if (alias.Length > 0) sg.Desc = sg.Desc + "   " + alias;
                if (s.Admin) sg.Desc = "👑 " + sg.Desc;
                if (s.Danger >= 2) sg.Desc = "⚠ " + sg.Desc;
                result.Items.Add(sg);
            }

            // 跨工具识别：输入的第一个词只存在于另一个 shell 里
            if (prefix.Length >= 2)
            {
                CmdSpec other = lib.FindInOther(shell, prefix);
                if (other != null)
                {
                    result.OtherShellSpec = other;
                    result.OtherShellHint = prefix + " 是 " + Shells.Display(other.Shell) + " 命令";
                    result.HintTitle = prefix + " 属于 " + Shells.Display(other.Shell);
                    result.HintText = Loc.T("当前是 ") + Shells.Display(shell)
                        + " 模式，直接执行会失败；下面第一个候选可以切到 "
                        + Shells.Display(other.Shell) + " 并重新执行。";
                    Suggestion sw = new Suggestion();
                    sw.Insert = other.Name;
                    sw.Display = "(切换到 " + Shells.Display(other.Shell) + ") " + other.Name;
                    sw.Kind = SuggestKind.Command;
                    sw.Desc = other.Title + " —— 点它会切换工具并重新执行";
                    sw.SwitchShell = true; sw.SwitchToShell = other.Shell;
                    result.Items.Insert(0, sw);
                    return;
                }
            }

            if (result.Items.Count == 0 && prefix.Length > 0)
            {
                string near = NearestCommand(lib, shell, prefix);
                if (near != null)
                {
                    Suggestion sg = new Suggestion();
                    sg.Insert = near; sg.Display = near; sg.Kind = SuggestKind.Command;
                    CmdSpec ns = lib.Find(shell, near);
                    sg.Desc = "你是不是想输入 " + near + (ns != null ? "（" + ns.Title + "）" : "");
                    result.Items.Add(sg);
                    result.HintTitle = Loc.T("没有完全匹配的命令");
                    result.HintText = Loc.T("按 Enter 仍会按原样执行你输入的内容。");
                }
            }
        }

        // ------------------------------------------------------------------
        //  路径补全
        // ------------------------------------------------------------------

        private static bool LooksLikePathNode(ParamNode n)
        {
            if (n == null) return false;
            string hay = n.Token + " " + n.ValueHint + " " + n.Desc;
            return hay.IndexOf("路径") >= 0 || hay.IndexOf("目录") >= 0 || hay.IndexOf("文件夹") >= 0
                || hay.IndexOf("文件") >= 0 || hay.IndexOf("盘符") >= 0;
        }

        private static bool LooksLikePathPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            if (prefix.IndexOf('\\') >= 0 || prefix.IndexOf('/') >= 0 || prefix.IndexOf(':') >= 0) return true;
            if (prefix.StartsWith(".") || prefix.StartsWith("~")) return true;
            return false;
        }

        private static void AddPathCandidates(string prefix, string cwd, bool quoted, bool executableOnly,
            SuggestResult result, ParamNode owner)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            string p = prefix;
            string dirPart = "";
            int sep = p.LastIndexOfAny(new char[] { '\\', '/' });
            if (sep >= 0)
            {
                dirPart = p.Substring(0, sep + 1);
                p = p.Substring(sep + 1);
            }

            string baseDir = dirPart;
            try { baseDir = Environment.ExpandEnvironmentVariables(baseDir); }
            catch { }
            if (baseDir.Length == 0) baseDir = cwd;
            else if (!Path.IsPathRooted(baseDir)) baseDir = Path.Combine(cwd, baseDir);

            string[] dirs;
            string[] files;
            try
            {
                dirs = Directory.GetDirectories(baseDir, p.Length == 0 ? "*" : p + "*");
                files = Directory.GetFiles(baseDir, p.Length == 0 ? "*" : p + "*");
            }
            catch { return; }

            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                Suggestion s = new Suggestion();
                s.Insert = dirPart + name + "\\";
                s.Display = name + "\\";
                s.Kind = SuggestKind.PathDir;
                s.Desc = "目录";
                result.Items.Add(s);
                if (++added >= 12) break;
            }
            if (added < 12)
            {
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (executableOnly && !IsExecutable(name)) continue;
                    Suggestion s = new Suggestion();
                    s.Insert = (quoted ? dirPart + name + "\"" : dirPart + name);
                    s.Display = name;
                    s.Kind = SuggestKind.PathFile;
                    s.Desc = "文件";
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        s.Desc = "文件 · " + FormatSize(fi.Length);
                    }
                    catch { }
                    result.Items.Add(s);
                    if (++added >= 20) break;
                }
            }
        }

        private static bool IsExecutable(string name)
        {
            string ext = Path.GetExtension(name);
            if (ext == null) return false;
            ext = ext.ToLowerInvariant();
            return ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".com" || ext == ".ps1";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        // ------------------------------------------------------------------
        //  拼写纠错
        // ------------------------------------------------------------------

        private static string NearestCommand(CommandLibrary lib, ShellKind shell, string word)
        {
            if (lib == null || string.IsNullOrEmpty(word) || word.Length < 2) return null;
            string best = null;
            int bestDist = int.MaxValue;
            int limit = word.Length <= 4 ? 1 : 2;
            foreach (CmdSpec s in lib.All)
            {
                if (s.Shell != shell) continue;
                if (Math.Abs(s.Name.Length - word.Length) > limit) continue;
                int d = Levenshtein(word.ToLowerInvariant(), s.Name.ToLowerInvariant(), limit);
                if (d < bestDist) { bestDist = d; best = s.Name; }
            }
            return bestDist <= limit ? best : null;
        }

        private static int Levenshtein(string a, string b, int limit)
        {
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            int[] prev = new int[m + 1];
            int[] cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    cur[j] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (rowMin > limit) return limit + 1;
                int[] tmp = prev; prev = cur; cur = tmp;
            }
            return prev[m];
        }

        // ------------------------------------------------------------------
        //  插入补全文本
        // ------------------------------------------------------------------

        /// <summary>
        /// 把候选插入命令行。
        /// replaceWord=true  → 替换光标所在的整词（补全半截词，例如 ipconf → ipconfig）
        /// replaceWord=false → 在光标处插入一个新词（例如 ping 1.1.1.1 → ping 1.1.1.1 -t）
        /// </summary>
        public static void ApplyInsert(string line, int caret, string insert, bool replaceWord,
            out string newLine, out int newCaret)
        {
            if (line == null) line = "";
            if (caret < 0) caret = 0;
            if (caret > line.Length) caret = line.Length;

            int start = caret;
            int end = caret;
            if (replaceWord)
            {
                List<Token> tokens = Lexer.Tokenize(line);
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (caret > tokens[i].Start && caret <= tokens[i].End)
                    {
                        start = tokens[i].Start;
                        end = tokens[i].End;
                        break;
                    }
                }
            }

            string before = line.Substring(0, start);
            string after = line.Substring(end);

            string text = insert;
            bool trailingSpace = true;
            if (text.EndsWith("\\") || text.EndsWith("/") || text.EndsWith("=")) trailingSpace = false;
            if (text.EndsWith("\"")) trailingSpace = false;
            if (after.StartsWith(" ")) trailingSpace = false;

            bool leadingSpace = false;
            if (!replaceWord)
            {
                if (before.Length > 0 && !before.EndsWith(" ") && !before.EndsWith("\t")) leadingSpace = true;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(before);
            if (leadingSpace) sb.Append(' ');
            sb.Append(text);
            if (trailingSpace) sb.Append(' ');
            int newPos = sb.Length;
            sb.Append(after);

            newLine = sb.ToString();
            newCaret = newPos;
        }

        public static void ApplyInsert(string line, int caret, string insert, out string newLine, out int newCaret)
        {
            ApplyInsert(line, caret, insert, true, out newLine, out newCaret);
        }
    }
}
