// ---------------------------------------------------------------------------
//  Library.cs — 命令知识库的数据模型、加载、检索（CMD + PowerShell 双引擎）
//
//  数据来源（按优先级从低到高，同名命令后加载的覆盖先加载的）：
//    1. exe 内嵌资源   lib.<shell>.<file>       —— 内置命令库（编译期打包，开箱即用）
//    2. exe 同级目录   commands\<shell>\*.json  —— 随包分发的可编辑命令库
//    3. %APPDATA%\WindowsCommandTools\commands\<shell>\*.json —— 用户自己的扩展命令库
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WindowsCommandTools
{
    /// <summary>当前使用的命令解释器类型。</summary>
    public enum ShellKind
    {
        Cmd,
        PowerShell
    }

    public static class Shells
    {
        public static string Id(ShellKind k)
        {
            return k == ShellKind.Cmd ? "cmd" : "ps";
        }

        public static string Display(ShellKind k)
        {
            return k == ShellKind.Cmd ? "CMD" : "PowerShell";
        }

        public static string DisplayCn(ShellKind k)
        {
            return k == ShellKind.Cmd ? Loc.T("CMD（命令提示符）") : "PowerShell";
        }

        public static ShellKind Other(ShellKind k)
        {
            return k == ShellKind.Cmd ? ShellKind.PowerShell : ShellKind.Cmd;
        }

        public static ShellKind Parse(string id)
        {
            if (string.Equals(id, "ps", StringComparison.OrdinalIgnoreCase)) return ShellKind.PowerShell;
            if (string.Equals(id, "powershell", StringComparison.OrdinalIgnoreCase)) return ShellKind.PowerShell;
            return ShellKind.Cmd;
        }

        /// <summary>提示符前缀，例如 cmd&gt; 或 ps&gt;。</summary>
        public static string Prompt(ShellKind k)
        {
            return k == ShellKind.Cmd ? "cmd>" : "ps>";
        }

        /// <summary>执行该命令的宿主程序（PowerShell 可被设置覆盖为 pwsh）。</summary>
        public static string Host(ShellKind k, bool preferPwsh)
        {
            if (k == ShellKind.Cmd) return "cmd.exe";
            return preferPwsh ? "pwsh.exe" : "powershell.exe";
        }
    }

    public sealed class CmdExample
    {
        public string Cmd = "";
        public string Desc = "";
    }

    public sealed class FormField
    {
        public string Token = "";   // 参数前导符；位置参数为空串
        public string Label = "";
        public string Type = "text"; // text | number | select | flag | path
        public string Value = "";
        public string Hint = "";
        public List<string> Options = new List<string>();
    }

    public sealed class ParamNode
    {
        public string Token = "";
        public string Kind = "flag";   // flag | option | value | subcommand | syntax
        public string Desc = "";
        public string ValueHint = "";
        public bool Repeatable;
        public List<string> Enum = new List<string>();
        public List<ParamNode> Children = new List<ParamNode>();

        public bool IsFlag { get { return string.Equals(Kind, "flag", StringComparison.OrdinalIgnoreCase); } }
        public bool IsOption { get { return string.Equals(Kind, "option", StringComparison.OrdinalIgnoreCase); } }
        public bool IsValue { get { return string.Equals(Kind, "value", StringComparison.OrdinalIgnoreCase); } }
        public bool IsSubCommand { get { return string.Equals(Kind, "subcommand", StringComparison.OrdinalIgnoreCase); } }
        /// <summary>字面量语法片段，例如 cd ..、icacls (OI)、popd &amp;&amp;，原样使用不需要值。</summary>
        public bool IsSyntax { get { return string.Equals(Kind, "syntax", StringComparison.OrdinalIgnoreCase); } }
        public bool TakesValue { get { return IsOption; } }
    }

    public sealed class CmdSpec
    {
        public string Name = "";
        public string Title = "";
        public string Desc = "";
        public string Usage = "";
        public bool Admin;
        public int Danger;
        public List<string> Aliases = new List<string>();   // PowerShell 别名，如 gci / ls
        public List<string> Tags = new List<string>();
        public List<CmdExample> Examples = new List<CmdExample>();
        public List<FormField> Form = new List<FormField>();
        public List<ParamNode> Nodes = new List<ParamNode>();

        // 加载时回填
        public ShellKind Shell = ShellKind.Cmd;
        public string Category = "";
        public string CategoryIcon = "terminal";
        public string Source = "内置";

        public int NodeCount
        {
            get
            {
                int n = 0;
                CountNodes(Nodes, ref n);
                return n;
            }
        }

        private List<ParamNode> _nodesEx;

        /// <summary>
        /// 顶层参数节点：PowerShell 命令会自动补上 13 个通用参数
        /// （-Verbose / -ErrorAction / -WhatIf …），这样命令库 JSON 里不用重复写，
        /// 但输入 `-ErrorAction ` 时依然能提示出 Continue / Stop 这些取值。
        /// </summary>
        public List<ParamNode> NodesEx
        {
            get
            {
                if (_nodesEx == null)
                {
                    _nodesEx = new List<ParamNode>(Nodes);
                    if (Shell == ShellKind.PowerShell)
                    {
                        foreach (ParamNode n in PowerShelCommonNodes())
                        {
                            bool exists = false;
                            foreach (ParamNode own in Nodes)
                            {
                                if (string.Equals(own.Token, n.Token, StringComparison.OrdinalIgnoreCase))
                                {
                                    exists = true;
                                    break;
                                }
                            }
                            if (!exists) _nodesEx.Add(n);
                        }
                    }
                }
                return _nodesEx;
            }
        }

        private static List<ParamNode> _psCommon;

        /// <summary>PowerShell 通用参数（官方 about_CommonParameters 里的那一组）。</summary>
        public static List<ParamNode> PowerShelCommonNodes()
        {
            if (_psCommon != null) return _psCommon;
            List<ParamNode> list = new List<ParamNode>();

            list.Add(Flag("-Verbose", "显示详细过程信息，排查脚本时很有用"));
            list.Add(Flag("-Debug", "显示调试级信息"));
            list.Add(Option("-ErrorAction", "出错时怎么处理", "<动作>",
                new string[] { "Continue", "Stop", "SilentlyContinue", "Ignore", "Inquire" }));
            list.Add(Option("-ErrorVariable", "把错误对象存到指定变量里", "<变量名>", null));
            list.Add(Option("-WarningAction", "警告怎么处理", "<动作>",
                new string[] { "Continue", "Stop", "SilentlyContinue", "Inquire" }));
            list.Add(Option("-WarningVariable", "把警告存到指定变量里", "<变量名>", null));
            list.Add(Option("-InformationAction", "信息流怎么处理", "<动作>",
                new string[] { "Continue", "Stop", "SilentlyContinue", "Ignore", "Inquire" }));
            list.Add(Option("-InformationVariable", "把信息流存到指定变量里", "<变量名>", null));
            list.Add(Option("-OutVariable", "把输出对象同时存到指定变量", "<变量名>", null));
            list.Add(Option("-OutBuffer", "攒够多少条再一次性输出", "<条数>", null));
            list.Add(Option("-PipelineVariable", "把当前对象存到变量供后续管道使用", "<变量名>", null));
            list.Add(Flag("-WhatIf", "只演示会做什么，不真正执行（支持 ShouldProcess 的命令）"));
            list.Add(Flag("-Confirm", "执行前逐个确认（支持 ShouldProcess 的命令）"));

            _psCommon = list;
            return list;
        }

        private static ParamNode Flag(string token, string desc)
        {
            ParamNode n = new ParamNode();
            n.Token = token;
            n.Kind = "flag";
            n.Desc = desc + "（通用参数）";
            return n;
        }

        private static ParamNode Option(string token, string desc, string hint, string[] values)
        {
            ParamNode n = new ParamNode();
            n.Token = token;
            n.Kind = "option";
            n.Desc = desc + "（通用参数）";
            n.ValueHint = hint;
            if (values != null) n.Enum = new List<string>(values);
            return n;
        }

        private static void CountNodes(List<ParamNode> nodes, ref int n)
        {
            if (nodes == null) return;
            foreach (ParamNode p in nodes)
            {
                n++;
                CountNodes(p.Children, ref n);
            }
        }

        /// <summary>名字或别名匹配（不区分大小写）。</summary>
        public bool MatchesName(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (string.Equals(Name, text, StringComparison.OrdinalIgnoreCase)) return true;
            return HasAlias(text);
        }

        public bool HasAlias(string text)
        {
            foreach (string a in Aliases)
            {
                if (string.Equals(a, text, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool HasAliasPrefix(string prefix)
        {
            foreach (string a in Aliases)
            {
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>别名的展示串，例如「别名 gci / ls / dir」。</summary>
        public string AliasText()
        {
            if (Aliases.Count == 0) return "";
            return "别名 " + string.Join(" / ", Aliases.ToArray());
        }
    }

    public sealed class CmdCategory
    {
        public string Name = "";
        public string Icon = "terminal";
        public string Source = "内置";
        public ShellKind Shell = ShellKind.Cmd;
        public List<CmdSpec> Commands = new List<CmdSpec>();
    }

    public sealed class CommandLibrary
    {
        public List<CmdCategory> Categories = new List<CmdCategory>();
        public List<CmdSpec> All = new List<CmdSpec>();

        private readonly Dictionary<string, CmdSpec> _byName =
            new Dictionary<string, CmdSpec>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CmdSpec> _byAlias =
            new Dictionary<string, CmdSpec>(StringComparer.OrdinalIgnoreCase);

        public List<string> LoadWarnings = new List<string>();

        public int Count { get { return All.Count; } }

        public int CountOf(ShellKind shell)
        {
            int n = 0;
            foreach (CmdSpec s in All) if (s.Shell == shell) n++;
            return n;
        }

        private static string Key(ShellKind shell, string name)
        {
            return Shells.Id(shell) + ":" + name;
        }

        // ------------------------------------------------------------------
        //  加载
        // ------------------------------------------------------------------

        public static CommandLibrary Load()
        {
            CommandLibrary lib = new CommandLibrary();
            lib.LoadEmbedded();
            foreach (ShellKind sh in new ShellKind[] { ShellKind.Cmd, ShellKind.PowerShell })
            {
                lib.LoadExternal(Path.Combine(AppPaths.CommandsDirectory, Shells.Id(sh)), sh);
            // 便携模式下额外允许 data\commands\<shell>\ 覆盖
            if (AppPaths.IsPortable)
                lib.LoadExternal(Path.Combine(AppPaths.PortableCommandsDirectory, Shells.Id(sh)), sh);
                lib.LoadExternal(Path.Combine(Path.Combine(AppPaths.UserDataDirectory, "commands"), Shells.Id(sh)), sh);
            }
            lib.BuildIndex();
            return lib;
        }

        private void LoadEmbedded()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            List<string> names = new List<string>(asm.GetManifestResourceNames());
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string res in names)
            {
                // 资源名形如 lib.cmd.01-files.json / lib.ps.ps-01-files.json
                if (!res.StartsWith("lib.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = res.Split('.');
                if (parts.Length < 4) continue;
                ShellKind shell = Shells.Parse(parts[1]);
                try
                {
                    using (Stream st = asm.GetManifestResourceStream(res))
                    {
                        if (st == null) continue;
                        byte[] buf = new byte[st.Length];
                        int read = 0;
                        while (read < buf.Length)
                        {
                            int n = st.Read(buf, read, buf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        ParseCategory(Json.DecodeText(buf), "内置", null, shell);
                    }
                }
                catch (Exception ex)
                {
                    LoadWarnings.Add("内嵌命令库 " + res + " 解析失败：" + ex.Message);
                }
            }
        }

        private void LoadExternal(string directory, ShellKind shell)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.json"); }
            catch (Exception ex) { LoadWarnings.Add("无法列出目录 " + directory + "：" + ex.Message); return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            bool shipped = directory.StartsWith(AppPaths.CommandsDirectory, StringComparison.OrdinalIgnoreCase);
            string source = shipped ? "随包命令库" : "自定义命令库";
            foreach (string file in files)
            {
                try
                {
                    string text = Json.DecodeText(File.ReadAllBytes(file));
                    ParseCategory(text, source, file, shell);
                }
                catch (Exception ex)
                {
                    LoadWarnings.Add("命令库 " + Path.GetFileName(file) + " 解析失败：" + ex.Message);
                }
            }
        }

        private void ParseCategory(string text, string source, string filePath, ShellKind shell)
        {
            Dictionary<string, object> root = Json.AsObj(Json.Parse(text));
            if (root == null) throw new FormatException("根节点不是 JSON 对象");

            // 文件里若显式声明了 shell，以文件声明为准（同一个目录里可以混放）
            string declared = Json.Str(root, "shell", "");
            if (declared.Length > 0) shell = Shells.Parse(declared);

            CmdCategory cat = new CmdCategory();
            cat.Name = Json.Str(root, "category", "未分类");
            cat.Icon = Json.Str(root, "icon", "terminal");
            cat.Source = source;
            cat.Shell = shell;

            List<object> arr = Json.AsArr(Json.Get(root, "commands"));
            if (arr == null) throw new FormatException("缺少 commands 数组");

            foreach (object item in arr)
            {
                Dictionary<string, object> co = Json.AsObj(item);
                if (co == null) continue;
                CmdSpec spec = ParseCommand(co);
                if (string.IsNullOrEmpty(spec.Name)) continue;
                spec.Shell = shell;
                spec.Category = DisplayName(cat.Name);
                spec.CategoryIcon = cat.Icon;
                spec.Source = source;
                cat.Commands.Add(spec);
                All.Add(spec);
            }

            if (cat.Commands.Count > 0)
            {
                cat.Name = cat.Name + "\u0000" + source; // 内部用 \0 分隔，展示时再拆
                Categories.Add(cat);
            }
        }

        private static CmdSpec ParseCommand(Dictionary<string, object> co)
        {
            CmdSpec spec = new CmdSpec();
            spec.Name = Json.Str(co, "name", "").Trim();
            spec.Title = Json.Str(co, "title", "");
            spec.Desc = Json.Str(co, "desc", "");
            spec.Usage = Json.Str(co, "usage", "");
            spec.Admin = Json.Bool(co, "admin", false);
            spec.Danger = Json.Int(co, "danger", 0);
            spec.Aliases = Json.StrList(co, "aliases");
            spec.Tags = Json.StrList(co, "tags");

            List<object> ex = Json.AsArr(Json.Get(co, "examples"));
            if (ex != null)
            {
                foreach (object e in ex)
                {
                    Dictionary<string, object> eo = Json.AsObj(e);
                    if (eo == null) continue;
                    CmdExample ce = new CmdExample();
                    ce.Cmd = Json.Str(eo, "cmd", "");
                    ce.Desc = Json.Str(eo, "desc", "");
                    if (ce.Cmd.Length > 0) spec.Examples.Add(ce);
                }
            }

            List<object> fm = Json.AsArr(Json.Get(co, "form"));
            if (fm != null)
            {
                foreach (object f in fm)
                {
                    Dictionary<string, object> fo = Json.AsObj(f);
                    if (fo == null) continue;
                    FormField ff = new FormField();
                    ff.Token = Json.Str(fo, "token", "");
                    ff.Label = Json.Str(fo, "label", "");
                    ff.Type = Json.Str(fo, "type", "text").ToLowerInvariant();
                    ff.Value = Json.Str(fo, "value", "");
                    ff.Hint = Json.Str(fo, "hint", "");
                    ff.Options = Json.StrList(fo, "options");
                    if (ff.Label.Length == 0) ff.Label = ff.Token;
                    spec.Form.Add(ff);
                }
            }

            spec.Nodes = ParseNodes(Json.AsArr(Json.Get(co, "nodes")));
            return spec;
        }

        private static List<ParamNode> ParseNodes(List<object> arr)
        {
            List<ParamNode> result = new List<ParamNode>();
            if (arr == null) return result;
            foreach (object item in arr)
            {
                Dictionary<string, object> no = Json.AsObj(item);
                if (no == null) continue;
                ParamNode node = new ParamNode();
                node.Token = Json.Str(no, "token", "");
                node.Kind = Json.Str(no, "kind", "flag").ToLowerInvariant();
                node.Desc = Json.Str(no, "desc", "");
                node.ValueHint = Json.Str(no, "valueHint", "");
                node.Repeatable = Json.Bool(no, "repeatable", false);
                node.Enum = Json.StrList(no, "enum");
                node.Children = ParseNodes(Json.AsArr(Json.Get(no, "children")));
                if (node.Token.Length == 0) continue;
                result.Add(node);
            }
            return result;
        }

        private static string DisplayName(string raw)
        {
            if (raw == null) return "";
            int i = raw.IndexOf('\0');
            return i < 0 ? raw : raw.Substring(0, i);
        }

        private void BuildIndex()
        {
            // 同名命令：后加载的（外部命令库）覆盖先加载的（内嵌命令库），并且只保留一份。
            // 覆盖时保持原有位置，这样命令在列表里的顺序不会因为覆盖而跳动。
            List<CmdSpec> unique = new List<CmdSpec>();
            Dictionary<string, int> position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CmdSpec s in All)
            {
                string k = Key(s.Shell, s.Name);
                int p;
                if (position.TryGetValue(k, out p)) unique[p] = s;
                else
                {
                    position[k] = unique.Count;
                    unique.Add(s);
                }
            }
            All = unique;

            _byName.Clear();
            _byAlias.Clear();
            foreach (CmdSpec s in All)
            {
                _byName[Key(s.Shell, s.Name)] = s;
                foreach (string a in s.Aliases)
                {
                    string ak = Key(s.Shell, a);
                    if (!_byAlias.ContainsKey(ak)) _byAlias[ak] = s;
                }
            }

            // 分类列表同样去重：被覆盖掉的那一份从原分类里移除，空分类删除
            foreach (CmdCategory c in Categories)
            {
                List<CmdSpec> keep = new List<CmdSpec>();
                foreach (CmdSpec s in c.Commands)
                {
                    CmdSpec winner;
                    if (_byName.TryGetValue(Key(s.Shell, s.Name), out winner) && ReferenceEquals(winner, s)) keep.Add(s);
                }
                c.Commands = keep;
            }
            Categories.RemoveAll(delegate (CmdCategory c) { return c.Commands.Count == 0; });
        }

        // ------------------------------------------------------------------
        //  查询
        // ------------------------------------------------------------------

        /// <summary>按名字或别名查命令；带路径的写法（.\a.exe、C:\x\ping.exe）也能识别。</summary>
        public CmdSpec Find(ShellKind shell, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim().Trim('"');
            CmdSpec s;
            if (_byName.TryGetValue(Key(shell, n), out s)) return s;
            if (_byAlias.TryGetValue(Key(shell, n), out s)) return s;

            int slash = n.LastIndexOfAny(new char[] { '\\', '/' });
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            if (_byName.TryGetValue(Key(shell, n), out s)) return s;
            if (_byAlias.TryGetValue(Key(shell, n), out s)) return s;
            return null;
        }

        /// <summary>只在另一个 shell 里存在的命令（用于跨工具提示）。</summary>
        public CmdSpec FindInOther(ShellKind shell, string name)
        {
            if (Find(shell, name) != null) return null;   // 本 shell 也有，就不算"只属于另一个"
            return Find(Shells.Other(shell), name);
        }

        public List<CmdSpec> OfShell(ShellKind shell)
        {
            List<CmdSpec> list = new List<CmdSpec>();
            foreach (CmdSpec s in All) if (s.Shell == shell) list.Add(s);
            return list;
        }

        /// <summary>按名称/别名前缀给出候选（用于第一个词的补全）。</summary>
        public List<CmdSpec> ByNamePrefix(ShellKind shell, string prefix, int max)
        {
            List<CmdSpec> hits = new List<CmdSpec>();
            List<CmdSpec> pool = OfShell(shell);
            if (string.IsNullOrEmpty(prefix))
            {
                for (int i = 0; i < pool.Count && hits.Count < max; i++) hits.Add(pool[i]);
                return hits;
            }
            foreach (CmdSpec s in pool)
            {
                if (s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(s);
                    if (hits.Count >= max) return hits;
                }
            }
            if (hits.Count == 0)
            {
                // 别名前缀也算（例如 PS 模式下敲 ls）
                foreach (CmdSpec s in pool)
                {
                    if (s.HasAliasPrefix(prefix)) hits.Add(s);
                    if (hits.Count >= max) break;
                }
            }
            if (hits.Count == 0)
            {
                foreach (CmdSpec s in pool)
                {
                    if (s.Name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits.Add(s);
                        if (hits.Count >= max) return hits;
                    }
                }
            }
            hits.Sort(delegate (CmdSpec a, CmdSpec b)
            {
                bool ap = a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || a.HasAliasPrefix(prefix);
                bool bp = b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || b.HasAliasPrefix(prefix);
                if (ap != bp) return ap ? -1 : 1;
                int c = a.Name.Length.CompareTo(b.Name.Length);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return hits;
        }

        /// <summary>全文检索：名称 &gt; 别名 &gt; 标题 &gt; 标签 &gt; 说明。</summary>
        public List<CmdSpec> Search(ShellKind shell, string query, int max)
        {
            List<CmdSpec> result = new List<CmdSpec>();
            if (string.IsNullOrEmpty(query) || query.Trim().Length == 0) return result;
            string q = query.Trim();
            List<KeyValuePair<int, CmdSpec>> scored = new List<KeyValuePair<int, CmdSpec>>();
            foreach (CmdSpec s in All)
            {
                if (s.Shell != shell) continue;
                int score = Score(s, q);
                if (score > 0) scored.Add(new KeyValuePair<int, CmdSpec>(score, s));
            }
            scored.Sort(delegate (KeyValuePair<int, CmdSpec> a, KeyValuePair<int, CmdSpec> b)
            {
                if (a.Key != b.Key) return b.Key.CompareTo(a.Key);
                return string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < scored.Count && i < max; i++) result.Add(scored[i].Value);
            return result;
        }

        private static int Score(CmdSpec s, string q)
        {
            int score = 0;
            if (s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 1000;
            if (s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += 300;
            else if (s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) score += 200;
            foreach (string a in s.Aliases)
            {
                if (string.Equals(a, q, StringComparison.OrdinalIgnoreCase)) return 900;
                if (a.StartsWith(q, StringComparison.OrdinalIgnoreCase)) { score += 150; break; }
            }
            if (s.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) score += 120;
            foreach (string tag in s.Tags)
            {
                if (tag.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { score += 80; break; }
            }
            if (s.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) score += 40;
            if (s.Usage.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) score += 20;
            return score;
        }
    }

    /// <summary>程序运行时用到的各种路径。</summary>
    public static class AppPaths
    {
        public const string ProductName = "WindowsCommandTools";
        /// <summary>便携模式下存放设置 / 主题 / 历史的目录名。</summary>
        public const string DataFolderName = "WCTdata";
        private const string LegacyName = "CmdToolbox";

        public static string ExeDirectory
        {
            get
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(p)) p = AppDomain.CurrentDomain.BaseDirectory;
                return Path.GetDirectoryName(p);
            }
        }

        /// <summary>随包的命令库覆盖目录（可选，删掉也能跑 —— 内置库已经嵌在 exe 里）。</summary>
        public static string CommandsDirectory
        {
            get { return Path.Combine(ExeDirectory, "commands"); }
        }

        /// <summary>便携模式下额外支持 data\commands 覆盖目录。</summary>
        public static string PortableCommandsDirectory
        {
            get { return Path.Combine(UserDataDirectory, "commands"); }
        }

        // ---- 便携模式 ----
        private static string _dataOverride;   // --data <目录>
        private static int _portable = -1;     // -1 未判定 / 0 否 / 1 是

        /// <summary>命令行 --data &lt;目录&gt;：把数据目录钉到指定位置。</summary>
        public static void SetDataDirectory(string dir)
        {
            if (!string.IsNullOrEmpty(dir)) _dataOverride = dir;
        }

        /// <summary>命令行 --portable：强制便携模式（不看 data\ 目录存不存在）。</summary>
        public static void ForcePortable(bool on)
        {
            _portable = on ? 1 : 0;
        }

        /// <summary>
        /// 是不是便携模式。
        ///
        /// 判定顺序：
        ///   1. --data 指定了目录        -> 是
        ///   2. --portable / 已判定过    -> 用判定结果
        ///   3. exe 同级有 data\ 目录    -> 是（这是分发包的默认形态）
        ///   4. exe 同级有 portable.txt  -> 是（手动标记）
        ///   5. 否则                    -> 否，用 %APPDATA%
        ///
        /// 便携模式下设置、主题、历史全部落在 exe 旁边，拷走整个文件夹即可带走全部状态，
        /// 也不会在宿主机上留下任何东西。
        /// </summary>
        public static bool IsPortable
        {
            get
            {
                if (_dataOverride != null) return true;
                if (_portable >= 0) return _portable == 1;
                try
                {
                    string exe = ExeDirectory;
                    // WCTdata 是正式名字；data 是早期版本的目录名，一并认。
                    if (Directory.Exists(Path.Combine(exe, DataFolderName)) ||
                        Directory.Exists(Path.Combine(exe, "data")) ||
                        File.Exists(Path.Combine(exe, "portable.txt")))
                    {
                        _portable = 1;
                        return true;
                    }
                }
                catch { }
                _portable = 0;
                return false;
            }
        }

        public static string UserDataDirectory
        {
            get
            {
                if (_dataOverride != null) return _dataOverride;
                if (IsPortable) return Path.Combine(ExeDirectory, DataFolderName);
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ProductName);
            }
        }

        /// <summary>给界面看的一句话说明（中文原文，英文由 Loc.T 翻）。</summary>
        public static string ModeDescription
        {
            get
            {
                return IsPortable
                    ? "便携模式：配置、主题、历史都保存在程序同级的 WCTdata 目录，拷走文件夹即可带走全部状态。"
                    : "安装模式：配置、主题、历史保存在 %APPDATA%\\" + ProductName + "。";
            }
        }

        public static string LegacyDataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    LegacyName);
            }
        }

        public static string SettingsFile { get { return Path.Combine(UserDataDirectory, "settings.json"); } }
        public static string ThemeFile { get { return Path.Combine(UserDataDirectory, "theme.json"); } }
        /// <summary>
        /// 历史命令 / 收藏 / 缓存都放在数据目录下的 caches 子目录里，
        /// 和设置、主题分开，便于单独清理或备份。
        /// </summary>
        public static string CachesDirectory
        {
            get { return Path.Combine(UserDataDirectory, "caches"); }
        }

        /// <summary>真正要写缓存之前才建 caches 子目录。</summary>
        public static bool EnsureCachesDirectory()
        {
            try
            {
                if (!EnsureUserDataDirectory()) return false;
                if (!Directory.Exists(CachesDirectory)) Directory.CreateDirectory(CachesDirectory);
                return true;
            }
            catch { return false; }
        }

        public static string HistoryFile
        {
            get { return Path.Combine(CachesDirectory, "history.json"); }
        }
        public static string ThemesDirectory { get { return Path.Combine(UserDataDirectory, "themes"); } }

        /// <summary>
        /// 真正要写文件之前才创建数据目录。
        ///
        /// 刻意不在启动时调用：如果用户什么都没改，程序不应该在磁盘上留下任何东西 ——
        /// 便携模式下这一点尤其重要（插到别人机器上不该产生残留）。
        /// </summary>
        public static bool EnsureUserDataDirectory()
        {
            try
            {
                if (Directory.Exists(UserDataDirectory)) return true;
                Directory.CreateDirectory(UserDataDirectory);
                MigrateLegacy();
                return true;
            }
            catch { return false; /* 无写入权限：退化成内存态，不影响使用 */ }
        }

        /// <summary>把旧版 CmdToolbox 的配置迁移过来，用户不用重新设置主题和历史。</summary>
        private static void MigrateLegacy()
        {
            try
            {
                string old = LegacyDataDirectory;
                if (!Directory.Exists(old)) return;
                foreach (string name in new string[] { "settings.json", "theme.json", "history.json" })
                {
                    string src = Path.Combine(old, name);
                    string dst = Path.Combine(UserDataDirectory, name);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst, false);
                }
            }
            catch { }
        }
    }
}
