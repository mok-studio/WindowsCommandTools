// ---------------------------------------------------------------------------
//  Program.cs — 程序入口
//    默认：启动图形界面
//    命令行模式（用于自动化验证，不弹窗口）：
//      --selftest              跑全部内置自检，输出 PASS/FAIL，退出码 0/1
//      --list                  列出两个命令库的分类与条数
//      --suggest "<命令行>"     打印该输入下的参数提示（可加 --shell ps --caret N）
//      --export-lib <目录>      把内置命令库导出成可编辑的 JSON
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace WindowsCommandTools
{
    public sealed class App : Application
    {
        public static bool SuppressCloseSave;

        [STAThread]
        public static int Main(string[] args)
        {
            CliOptions opt = CliOptions.Parse(args);

            // 数据目录必须最先定下来 —— 后面所有 AppPaths 访问都依赖它
            if (opt.Portable) AppPaths.ForcePortable(true);
            if (!string.IsNullOrEmpty(opt.DataDir)) AppPaths.SetDataDirectory(opt.DataDir);

            if (opt.SelfTest || opt.List || opt.Suggest != null || opt.ExportLib != null)
            {
                // 本程序编译为 GUI 子系统（双击不弹黑框），因此默认没有控制台。
                // 命令行模式下把父进程给的标准句柄重新接上，输出才能被捕获。
                Cli.ReopenStdio();
                Loc.Apply(string.IsNullOrEmpty(opt.Lang) ? AppSettings.Load().Language : opt.Lang);
                if (opt.SelfTest) return SelfTest.Run(opt.OutFile);
                if (opt.List) return SelfTest.List();
                if (opt.Suggest != null) return SelfTest.Suggest(opt.Suggest, opt.Caret, opt.ShellName);
                return SelfTest.ExportLib(opt.ExportLib);
            }

            // "默认以管理员权限启动"：设置里打开后，启动时先请求一次 UAC。
            // 已经在管理员身份下就直接往下走，不会来回重启（判断的是当前进程令牌）。
            string elevateNote = null;
            if (!opt.Elevated && !Elevation.IsAdministrator())
            {
                AppSettings prefs = AppSettings.Load();
                if (prefs.RunAsAdmin)
                {
                    string err = Elevation.RelaunchElevated(args);
                    if (err == null) return 0;            // 已经交给提权后的新进程
                    if (err == "cancelled") elevateNote = Loc.T("已取消提权请求，本次以普通权限启动。");
                    else elevateNote = Loc.T("提权启动失败（") + err + Loc.T("），本次以普通权限启动。");
                }
            }

            App app = new App();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };
            app.DispatcherUnhandledException += delegate (object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                LogCrash(e.Exception);
                e.Handled = true;
            };

            // 界面语言要在建界面之前定下来，所有 Loc.T() 才会取到正确的语言
            Loc.Apply(string.IsNullOrEmpty(opt.Lang) ? AppSettings.Load().Language : opt.Lang);

            Themes.Settings = AppSettings.Load();
            Themes.Load();
            // 分模式用主题时，启动引擎对应的那套才该生效
            Themes.OnShellChanged(Themes.Settings.DefaultShell == "cmd"
                ? ShellKind.Cmd : ShellKind.PowerShell);
            FlatStyles.Load();
            Themes.Apply();

            MainWindow w = new MainWindow();
            w.PendingAutoRun = opt.Run;
            w.PendingInput = opt.Input;
            w.PendingNotice = elevateNote;
            w.PendingMulti = opt.Multi;
            // 多进程面板：--multiproc 展开面板，--multiproc-cmd 预填各任务行
            // （多进程模式下 --input 是留给主输入框的，这里两边互不干扰）
            w.PendingMultiProc = opt.MultiProc;
            w.PendingMultiProcAutoRun = opt.MultiProcAutoRun;
            if (opt.MultiProc && opt.MultiProcCmds.Count > 0)
                w.PendingMultiProcCmds = opt.MultiProcCmds.ToArray();
            w.ApplyPanelWidths();
            if (!string.IsNullOrEmpty(opt.ShellName)) w.SetShell(Shells.Parse(opt.ShellName), false);
            if (!string.IsNullOrEmpty(opt.Cwd) && Directory.Exists(opt.Cwd)) w.SessionDirectory = opt.Cwd;

            try
            {
                app.Run(w);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                return 3;
            }
            return 0;
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                AppPaths.EnsureUserDataDirectory();
                string path = Path.Combine(AppPaths.UserDataDirectory, "crash.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    (ex == null ? Loc.T("(未知异常)") : ex.ToString()) + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            try
            {
                MessageBox.Show(ex == null ? Loc.T("发生未知错误。") : ex.Message,
                    Loc.T("WindowsCommandTools 出错"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }

    internal sealed class CliOptions
    {
        public bool SelfTest;
        public bool List;
        public string Suggest;
        public int Caret = -1;
        public string ExportLib;
        public string Run;
        public string Cwd;
        public string OutFile;
        public string Input;
        public string ShellName;
        /// <summary>提权后的子进程会带上这个标记，避免反复请求 UAC。</summary>
        public bool Elevated;
        /// <summary>临时覆盖界面语言（auto / zh / en），只影响本次运行，不改设置。</summary>
        public string Lang;
        /// <summary>启动时直接打开多命令（多行）输入模式。</summary>
        public bool Multi;
        /// <summary>启动时直接展开多进程面板。</summary>
        public bool MultiProc;
        /// <summary>--multiproc-run：展开面板后立刻把预填的命令跑起来（截图 / 自动化验证用）。</summary>
        public bool MultiProcAutoRun;
        /// <summary>--multiproc-cmd "命令"：预填到多进程面板的任务行（可重复，最多 4 条）。</summary>
        public List<string> MultiProcCmds = new List<string>();
        /// <summary>--portable：强制便携模式。
        /// --data &lt;目录&gt;：把设置/主题/历史放到指定目录。</summary>
        public bool Portable;
        public string DataDir;

        public static CliOptions Parse(string[] args)
        {
            CliOptions o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--selftest") o.SelfTest = true;
                else if (a == "--elevated") o.Elevated = true;
                else if (a == "--lang" && i + 1 < args.Length) o.Lang = args[++i];
                else if (a == "--multi") o.Multi = true;
                else if (a == "--multiproc") o.MultiProc = true;
                else if (a == "--multiproc-run") { o.MultiProc = true; o.MultiProcAutoRun = true; }
                else if (a == "--multiproc-cmd" && i + 1 < args.Length)
                {
                    // 一个参数里可以用 ; 分隔多条，省得命令行上写一长串 --multiproc-cmd
                    foreach (string one in args[++i].Split(';'))
                    {
                        string t = one.Trim();
                        if (t.Length > 0) o.MultiProcCmds.Add(t);
                    }
                }
                else if (a == "--portable") o.Portable = true;
                else if (a == "--data" && i + 1 < args.Length) o.DataDir = args[++i];
                else if (a == "--list") o.List = true;
                else if (a == "--suggest" && i + 1 < args.Length) o.Suggest = args[++i];
                else if (a == "--caret" && i + 1 < args.Length)
                {
                    int c;
                    if (int.TryParse(args[++i], out c)) o.Caret = c;
                }
                else if (a == "--shell" && i + 1 < args.Length) o.ShellName = args[++i];
                else if (a == "--export-lib" && i + 1 < args.Length) o.ExportLib = args[++i];
                else if (a == "--out" && i + 1 < args.Length) o.OutFile = args[++i];
                else if (a == "--run" && i + 1 < args.Length) o.Run = args[++i];
                else if (a == "--input" && i + 1 < args.Length) o.Input = args[++i];
                else if (a == "--cwd" && i + 1 < args.Length) o.Cwd = args[++i];
            }
            return o;
        }
    }

    /// <summary>GUI 子系统的程序默认没有控制台，命令行模式需要手动接回父进程的标准句柄。</summary>
    internal static class Cli
    {
        /// <summary>设置控制台编码；GUI 子系统下没有真实控制台，会报「句柄无效」，此时静默忽略。</summary>
        public static void TrySetConsoleEncoding()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }
        }

        public static void ReopenStdio()
        {
            try
            {
                Stream s = Console.OpenStandardOutput();
                if (s != null)
                {
                    StreamWriter w = new StreamWriter(s, new UTF8Encoding(false));
                    w.AutoFlush = true;
                    Console.SetOut(w);
                }
            }
            catch { }
            try
            {
                Stream e = Console.OpenStandardError();
                if (e != null)
                {
                    StreamWriter w = new StreamWriter(e, new UTF8Encoding(false));
                    w.AutoFlush = true;
                    Console.SetError(w);
                }
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------------
    //  无界面自检
    // -----------------------------------------------------------------------
    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;
        private static readonly StringBuilder _log = new StringBuilder();

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; _log.AppendLine("  PASS  " + name); }
            else { _fail++; _log.AppendLine("  FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "   → " + detail)); }
        }

        private static void Section(string title)
        {
            _log.AppendLine("");
            _log.AppendLine("=== " + title + " ===");
        }

        private static int Finish(string outFile)
        {
            _log.AppendLine("");
            _log.AppendLine("----------------------------------------------------------");
            _log.AppendLine("结果：通过 " + _pass + " 项，失败 " + _fail + " 项");
            string text = _log.ToString();
            try { Console.Out.Write(text); Console.Out.Flush(); }
            catch { }
            if (!string.IsNullOrEmpty(outFile))
            {
                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outFile, text, new UTF8Encoding(false));
                }
                catch { }
            }
            return _fail == 0 ? 0 : 1;
        }

        public static int List()
        {
            Cli.TrySetConsoleEncoding();
            CommandLibrary lib = CommandLibrary.Load();
            Console.WriteLine("命令库共 " + lib.Count + " 条命令，" + lib.Categories.Count + " 个分类：");
            foreach (ShellKind shell in new ShellKind[] { ShellKind.Cmd, ShellKind.PowerShell })
            {
                Console.WriteLine("  [" + Shells.Display(shell) + "]  " + lib.CountOf(shell) + " 条");
                foreach (CmdCategory c in lib.Categories)
                {
                    if (c.Shell != shell) continue;
                    Console.WriteLine(string.Format("     {0,-24} {1,3} 条   (来源 {2})",
                        CatName(c), c.Commands.Count, c.Source));
                }
            }
            foreach (string w in lib.LoadWarnings) Console.WriteLine("  警告: " + w);
            return 0;
        }

        private static string CatName(CmdCategory c)
        {
            string n = c.Name;
            int i = n.IndexOf('\0');
            return i < 0 ? n : n.Substring(0, i);
        }

        public static int Suggest(string line, int caret, string shellName)
        {
            Cli.TrySetConsoleEncoding();
            CommandLibrary lib = CommandLibrary.Load();
            ShellKind shell = string.IsNullOrEmpty(shellName) ? ShellKind.PowerShell : Shells.Parse(shellName);
            if (caret < 0) caret = line.Length;
            SuggestResult r = Suggester.Compute(lib, line, caret, Environment.CurrentDirectory, shell);
            Console.WriteLine("引擎   : " + Shells.Display(shell));
            Console.WriteLine("输入   : " + line);
            Console.WriteLine("光标   : " + caret);
            Console.WriteLine("面包屑 : " + (r.TrailText.Length == 0 ? "(命令名位置)" : r.TrailText));
            Console.WriteLine("提示   : " + r.HintTitle);
            if (r.OtherShellSpec != null) Console.WriteLine("跨工具 : " + r.OtherShellHint);
            Console.WriteLine("候选   : " + r.Items.Count + " 个");
            foreach (Suggestion s in r.Items)
            {
                Console.WriteLine(string.Format("   [{0}] {1}{2}  — {3}",
                    s.Badge, s.Display,
                    s.ValueHint.Length > 0 ? " " + s.ValueHint : "",
                    s.Desc));
            }
            return 0;
        }

        public static int ExportLib(string directory)
        {
            Cli.TrySetConsoleEncoding();
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                int n = 0;
                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (!res.StartsWith("lib.", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = res.Split('.');
                    if (parts.Length < 4) continue;
                    string shell = parts[1];
                    string rel = res.Substring("lib.".Length + shell.Length + 1);
                    string dir = Path.Combine(directory, shell);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    using (Stream st = asm.GetManifestResourceStream(res))
                    {
                        if (st == null) continue;
                        using (FileStream fs = File.Create(Path.Combine(dir, rel)))
                        {
                            byte[] buf = new byte[8192];
                            int read;
                            while ((read = st.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, read);
                        }
                    }
                    n++;
                }
                Console.WriteLine("已导出 " + n + " 个命令库文件到 " + directory);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("导出失败：" + ex.Message);
                return 1;
            }
        }

        public static int Run(string outFile)
        {
            Cli.TrySetConsoleEncoding();

            // ---------------- 1. 命令库 ----------------
            Section("命令库完整性");
            CommandLibrary lib = CommandLibrary.Load();
            Check("命令库已加载", lib.Count > 0, "共 " + lib.Count + " 条");
            Check("CMD 命令库非空", lib.CountOf(ShellKind.Cmd) > 50, "共 " + lib.CountOf(ShellKind.Cmd) + " 条");
            Check("PowerShell 命令库非空", lib.CountOf(ShellKind.PowerShell) > 50,
                "共 " + lib.CountOf(ShellKind.PowerShell) + " 条");
            Check("分类数 ≥ 10", lib.Categories.Count >= 10, "共 " + lib.Categories.Count + " 个");
            Check("加载无警告", lib.LoadWarnings.Count == 0, string.Join(" | ", lib.LoadWarnings.ToArray()));

            int badField = 0, badNode = 0, badKind = 0, badHint = 0, badForm = 0, badExample = 0;
            string firstBad = "";
            Dictionary<string, int> dup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CmdSpec s in lib.All)
            {
                if (s.Name.Length == 0 || s.Title.Length == 0 || s.Desc.Length == 0 ||
                    s.Usage.Length == 0 || s.Nodes.Count == 0)
                {
                    badField++; if (firstBad.Length == 0) firstBad = s.Name;
                }
                if (s.Examples.Count < 1) badExample++;
                foreach (FormField f in s.Form)
                {
                    if (f.Type != "text" && f.Type != "number" && f.Type != "select" &&
                        f.Type != "flag" && f.Type != "path") badForm++;
                    if (f.Type == "select" && f.Options.Count == 0) badForm++;
                }
                ValidateNodes(s, s.Nodes, ref badNode, ref badKind, ref badHint);
                string k = Shells.Id(s.Shell) + ":" + s.Name;
                int c;
                dup.TryGetValue(k, out c);
                dup[k] = c + 1;
            }
            Check("每个命令都有 name/title/desc/usage/nodes", badField == 0,
                badField + " 条不完整，例如 " + firstBad);
            Check("每个命令至少有 1 条示例", badExample == 0, badExample + " 条缺示例");
            Check("每个参数节点都有 token", badNode == 0, badNode + " 个节点缺 token");
            Check("参数 kind 取值合法", badKind == 0, badKind + " 个节点 kind 非法");
            Check("option/value 都带 valueHint", badHint == 0, badHint + " 个节点缺 valueHint");
            Check("表单字段类型合法", badForm == 0, badForm + " 个字段非法");

            int dupCount = 0;
            string dupNames = "";
            foreach (KeyValuePair<string, int> kv in dup)
            {
                if (kv.Value > 1)
                {
                    dupCount++;
                    if (dupNames.Length < 140) dupNames += kv.Key + "(" + kv.Value + ") ";
                }
            }
            Check("同引擎内命令名唯一", dupCount == 0, dupCount + " 个重名：" + dupNames);

            int totalNodes = 0, deepTrees = 0, aliased = 0;
            foreach (CmdSpec s in lib.All)
            {
                totalNodes += s.NodeCount;
                if (MaxDepth(s.Nodes, 0) >= 2) deepTrees++;
                if (s.Aliases.Count > 0) aliased++;
            }
            _log.AppendLine("  统计：命令 " + lib.Count + " 条（CMD " + lib.CountOf(ShellKind.Cmd)
                + " / PowerShell " + lib.CountOf(ShellKind.PowerShell) + "），参数节点 " + totalNodes
                + " 个，多级子命令树 " + deepTrees + " 条，带别名的命令 " + aliased + " 条");

            // ---------------- 2. 分词 ----------------
            Section("命令行分词");
            List<Token> tk = Lexer.Tokenize("ping -n 4 \"a b\" c");
            Check("引号内不切分", tk.Count == 5, "实际 " + tk.Count);
            if (tk.Count == 5)
            {
                Check("引号被剥离", tk[3].Bare == "a b", "实际 " + tk[3].Bare);
                Check("普通词正确", tk[0].Bare == "ping" && tk[2].Bare == "4", "");
            }
            List<Token> tk2 = Lexer.Tokenize("ipconfig /all  ");
            Check("尾部空格不产生空 token", tk2.Count == 2, "实际 " + tk2.Count);

            // ---------------- 3. CMD 提示引擎 ----------------
            Section("CMD 提示引擎");
            Expect(lib, ShellKind.Cmd, "ip", "ipconfig");
            Expect(lib, ShellKind.Cmd, "pin", "ping");
            ExpectContains(lib, ShellKind.Cmd, "ping ", "-t");
            ExpectAllPrefix(lib, ShellKind.Cmd, "ping -", "-");
            ExpectHintContains(lib, ShellKind.Cmd, "ping -n ", "次数");
            ExpectContains(lib, ShellKind.Cmd, "netsh wlan show ", "profiles");
            ExpectContains(lib, ShellKind.Cmd, "sc config start= ", "disabled");
            ExpectSpell(lib, ShellKind.Cmd, "ipconfg", "ipconfig");
            ExpectEmptyItems(lib, ShellKind.Cmd, "notarealcommand123 ");

            Section("CMD 提示顺序");
            ExpectFirst(lib, ShellKind.Cmd, "ping ", "<目标主机>");
            ExpectFirst(lib, ShellKind.Cmd, "xcopy ", "<源>");
            ExpectFirst(lib, ShellKind.Cmd, "sc config ", "<服务名>");
            ExpectFirst(lib, ShellKind.Cmd, "netstat ", "-a");
            ExpectFirst(lib, ShellKind.Cmd, "sc ", "query");
            ExpectContains(lib, ShellKind.Cmd, "powercfg /change ", "monitor-timeout-ac");

            // ---------------- 4. PowerShell 提示引擎 ----------------
            Section("PowerShell 提示引擎");
            ExpectFirst(lib, ShellKind.PowerShell, "Get-Ch", "Get-ChildItem");
            ExpectFirst(lib, ShellKind.PowerShell, "Get-ChildItem ", "<路径>");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem ", "-Recurse");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem -Rec", "-Recurse");
            ExpectContains(lib, ShellKind.PowerShell, "Get-Service ", "-Name");
            ExpectFirst(lib, ShellKind.PowerShell, "Get-Process ", "<进程名>");
            ExpectContains(lib, ShellKind.PowerShell, "Get-Process ", "-Id");
            ExpectSpell(lib, ShellKind.PowerShell, "Get-ChildIten", "Get-ChildItem");
            Expect(lib, ShellKind.PowerShell, "ls", "Get-ChildItem");
            Expect(lib, ShellKind.PowerShell, "gci", "Get-ChildItem");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem ", "-Verbose");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem ", "-ErrorAction");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem -ErrorAction ", "SilentlyContinue");

            Section("管道与跨工具");
            ExpectContains(lib, ShellKind.PowerShell, "Get-Process | Where-Object ", "-Property");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem | Select-Object ", "-Property");
            ExpectContains(lib, ShellKind.PowerShell, "Get-ChildItem | Sort-Object ", "-Descending");
            ExpectContains(lib, ShellKind.PowerShell, "Get-Process | ", "Where-Object");
            CrossShell(lib, ShellKind.Cmd, "Get-Process", ShellKind.PowerShell);
            CrossShell(lib, ShellKind.PowerShell, "netsh", ShellKind.Cmd);
            CrossShell(lib, ShellKind.PowerShell, "powercfg", ShellKind.Cmd);
            NoCrossShell(lib, ShellKind.Cmd, "ipconfig");

            // ---------------- 5. 补全插入 ----------------
            Section("补全插入语义");
            string nl; int nc;
            Suggester.ApplyInsert("ipconf", 6, "ipconfig", true, out nl, out nc);
            Check("半截词补全 → 替换", nl == "ipconfig " && nc == 9, "实际 [" + nl + "] caret=" + nc);
            Suggester.ApplyInsert("ping 1.1.1.1", 12, "-t", false, out nl, out nc);
            Check("完整参数后 → 追加新词", nl == "ping 1.1.1.1 -t ", "实际 [" + nl + "]");
            Suggester.ApplyInsert("Get-Ch", 6, "Get-ChildItem", true, out nl, out nc);
            Check("PowerShell 半截词补全", nl == "Get-ChildItem ", "实际 [" + nl + "]");
            string w = Suggester.CurrentWord("ping 1.1.1.1", 12);
            Check("CurrentWord 取到完整词", w == "1.1.1.1", "实际 [" + w + "]");

            // ---------------- 6. 临时切换前缀与管道切分 ----------------
            Section("临时切换前缀");
            ShellKind sk = ShellKind.PowerShell;
            string stripped = MainWindow.StripShellPrefix("cmd> ipconfig /all", ref sk);
            Check("cmd> 前缀被识别", sk == ShellKind.Cmd && stripped == "ipconfig /all",
                "实际 " + sk + " [" + stripped + "]");
            sk = ShellKind.Cmd;
            stripped = MainWindow.StripShellPrefix("ps> Get-Process", ref sk);
            Check("ps> 前缀被识别", sk == ShellKind.PowerShell && stripped == "Get-Process",
                "实际 " + sk + " [" + stripped + "]");
            sk = ShellKind.PowerShell;
            stripped = MainWindow.StripShellPrefix("Get-Process", ref sk);
            Check("无前缀时保持不变", sk == ShellKind.PowerShell && stripped == "Get-Process", "");
            Check("管道检测：引号内竖线不算", Suggester.LastPipeBefore("echo \"a|b\" | x", 14) == 11,
                "实际 " + Suggester.LastPipeBefore("echo \"a|b\" | x", 14));

            // ---------------- 7. 跨工具报错识别 ----------------
            Section("跨工具报错识别");
            string cmdErr = "'Get-Process' 不是内部或外部命令，也不是可运行的程序或批处理文件。";
            string psErr = "Get-Process : 无法将“Get-Process”项识别为 cmdlet、函数、脚本文件或可运行程序的名称。";
            Check("识别 cmd 的「不是内部或外部命令」", ShellErrors.LooksLikeCmdUnknown(cmdErr), "");
            Check("识别 PowerShell 的「无法将…识别为 cmdlet」", ShellErrors.LooksLikePowerShellUnknown(psErr), "");
            Check("两者都算「不认识这个命令」", ShellErrors.IsUnknownCommandError(cmdErr)
                && ShellErrors.IsUnknownCommandError(psErr), "");
            Check("从中文 PowerShell 报错里抽出命令名",
                ShellErrors.ExtractUnknownName(psErr) == "Get-Process",
                "实际 [" + ShellErrors.ExtractUnknownName(psErr) + "]");
            Check("从 cmd 报错里抽出命令名",
                ShellErrors.ExtractUnknownName(cmdErr) == "Get-Process",
                "实际 [" + ShellErrors.ExtractUnknownName(cmdErr) + "]");
            Check("普通错误不会被误判", !ShellErrors.IsUnknownCommandError("拒绝访问。"), "");

            // ---------------- 8. JSON ----------------
            Section("JSON 解析与序列化");
            Dictionary<string, object> jo = Json.NewObj();
            jo["s"] = "中文\"引号\" 与 \\ 反斜杠\n换行";
            jo["n"] = 42.5;
            jo["b"] = true;
            List<object> ja = new List<object>();
            ja.Add("x"); ja.Add(1);
            jo["a"] = ja;
            jo["nil"] = null;
            string js = Json.Write(jo);
            Dictionary<string, object> back = Json.AsObj(Json.Parse(js));
            Check("字符串往返一致", back != null && Json.Str(back, "s", "") == "中文\"引号\" 与 \\ 反斜杠\n换行", "");
            Check("数字往返一致", back != null && Math.Abs(Json.Dbl(back, "n", 0) - 42.5) < 1e-9, "");
            Check("布尔往返一致", back != null && Json.Bool(back, "b", false), "");
            Check("数组往返一致", back != null && Json.AsArr(Json.Get(back, "a")).Count == 2, "");
            bool threw = false;
            try { Json.Parse("{bad json}"); } catch { threw = true; }
            Check("非法 JSON 会抛异常", threw, "");
            string bomText = Json.DecodeText(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D });
            Check("BOM 被剥离", bomText == "{}", "实际 [" + bomText + "]");

            // ---------------- 9. 中文编码 ----------------
            Section("中文编码自动识别");
            byte[] gbk = Encoding.GetEncoding(936).GetBytes("系统找不到指定的路径。");
            byte[] utf8 = new UTF8Encoding(false).GetBytes("已更新 1 个项目。");
            Check("GBK 输出正确解码", CommandExecutor.DecodeLine(gbk, null) == "系统找不到指定的路径。",
                CommandExecutor.DecodeLine(gbk, null));
            Check("UTF-8 输出正确解码", CommandExecutor.DecodeLine(utf8, null) == "已更新 1 个项目。",
                CommandExecutor.DecodeLine(utf8, null));
            Check("ANSI 转义被清理", CommandExecutor.StripAnsi("\u001b[31mred\u001b[0m") == "red", "");

            // ---------------- 10. 真实执行 ----------------
            Section("真实命令执行（CMD 端到端）");
            int code0;
            string out1 = CommandExecutor.RunSync("echo CMD_OK", null, 8000, out code0);
            Check("CMD echo 有输出", out1.IndexOf("CMD_OK", StringComparison.Ordinal) >= 0, "实际 [" + out1.Trim() + "]");
            string out2 = CommandExecutor.RunSync("echo 中文测试", null, 8000);
            Check("CMD 中文回显不乱码", out2.IndexOf("中文测试", StringComparison.Ordinal) >= 0, "实际 [" + out2.Trim() + "]");
            int code7;
            CommandExecutor.RunSync("exit 7", null, 8000, out code7);
            Check("CMD 退出码原样透传 (7)", code7 == 7, "实际 " + code7);

            Section("真实命令执行（PowerShell 端到端）");
            string host = CommandExecutor.ResolvePowerShell(false);
            Check("找到 PowerShell 宿主", !string.IsNullOrEmpty(host), host);
            _log.AppendLine("  宿主：" + host + "   pwsh 可用：" + CommandExecutor.PwshAvailable());
            int pcode;
            string p1 = CommandExecutor.RunSync("Write-Output PS_OK", null, 25000, out pcode, ShellKind.PowerShell, false);
            Check("PowerShell 有输出", p1.IndexOf("PS_OK", StringComparison.Ordinal) >= 0, "实际 [" + p1.Trim() + "]");
            Check("PowerShell 退出码为 0", pcode == 0, "实际 " + pcode);
            string p2 = CommandExecutor.RunSync("Write-Output '中文测试'", null, 25000, out pcode, ShellKind.PowerShell, false);
            Check("PowerShell 中文不乱码", p2.IndexOf("中文测试", StringComparison.Ordinal) >= 0, "实际 [" + p2.Trim() + "]");
            CommandExecutor.RunSync("exit 9", null, 25000, out pcode, ShellKind.PowerShell, false);
            Check("PowerShell 退出码原样透传 (9)", pcode == 9, "实际 " + pcode);
            string p3 = CommandExecutor.RunSync("(Get-Date).Year", null, 25000, out pcode, ShellKind.PowerShell, false);
            Check("PowerShell 能执行真实表达式", p3.Trim().Length == 4, "实际 [" + p3.Trim() + "]");
            string p4 = CommandExecutor.RunSync("Get-ChildItem | Measure-Object | Select-Object -ExpandProperty Count",
                null, 25000, out pcode, ShellKind.PowerShell, false);
            Check("PowerShell 管道可用", p4.Trim().Length > 0, "实际 [" + p4.Trim() + "]");

            Section("跨工具失败检测（真实执行）");
            string crossOut = CommandExecutor.RunSync("Get-Process", null, 25000, out pcode, ShellKind.Cmd, false);
            Check("在 CMD 里跑 PowerShell 命令确实报错",
                ShellErrors.IsUnknownCommandError(crossOut), "实际 [" + crossOut.Trim() + "]");
            Check("能从真实报错里抽出 Get-Process",
                ShellErrors.ExtractUnknownName(crossOut) == "Get-Process",
                "实际 [" + ShellErrors.ExtractUnknownName(crossOut) + "]");
            Check("抽出的名字能在 PowerShell 库里找到",
                lib.FindInOther(ShellKind.Cmd, ShellErrors.ExtractUnknownName(crossOut)) != null, "");
            Check("netsh 在 PowerShell 库里没有、在 CMD 库里有",
                lib.FindInOther(ShellKind.PowerShell, "netsh") != null, "");

            // ---------------- 11. 设置持久化 ----------------
            Section("设置持久化");
            string backup = null;
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    backup = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
            }
            catch { }
            try
            {
                AppSettings s = new AppSettings();
                s.DefaultShell = "cmd";
                s.PreferPwsh = true;
                s.RunAsAdmin = true;
                s.CrossShellHint = false;
                s.FontSize = 15.5;
                s.ConfirmLevel1 = true;
                s.MultiCommandPersist = true;
                s.MultiCommandHeight = 210;
                s.Language = "en";
                s.Save();

                AppSettings back2 = AppSettings.Load();
                Check("默认引擎能存能读", back2.DefaultShell == "cmd", "实际 " + back2.DefaultShell);
                Check("优先 pwsh 能存能读", back2.PreferPwsh, "");
                Check("默认管理员启动能存能读", back2.RunAsAdmin, "");
                Check("跨工具提示能存能读", !back2.CrossShellHint, "");
                Check("字号能存能读", Math.Abs(back2.FontSize - 15.5) < 1e-6, "实际 " + back2.FontSize);
                Check("改动确认能存能读", back2.ConfirmLevel1, "");
                Check("多命令持久生效能存能读", back2.MultiCommandPersist, "");
                Check("多行高度能存能读", Math.Abs(back2.MultiCommandHeight - 210) < 1e-6, "实际 " + back2.MultiCommandHeight);
                Check("语言设置能存能读", back2.Language == "en", "实际 " + back2.Language);

                AppSettings def = AppSettings.Load();
                Check("默认值：不以管理员启动", new AppSettings().RunAsAdmin == false, "");
                Check("默认值：默认引擎是 PowerShell", new AppSettings().DefaultShell == "ps", "");
                _log.AppendLine("  设置文件：" + AppPaths.SettingsFile);
            }
            catch (Exception ex)
            {
                Check("设置读写不抛异常", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (backup != null) File.WriteAllText(AppPaths.SettingsFile, backup, new UTF8Encoding(false));
                    else if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                }
                catch { }
            }

            Section("提权参数拼接");
            Check("无参数", Elevation.JoinArgs(new string[0]) == "", "");
            Check("普通参数", Elevation.JoinArgs(new string[] { "--shell", "cmd" }) == "--shell cmd", "");
            Check("带空格的参数加引号",
                Elevation.JoinArgs(new string[] { "--input", "ping 1.1.1.1" }) == "--input \"ping 1.1.1.1\"", "");
            Check("提权标记可解析", CliOptions.Parse(new string[] { "--elevated" }).Elevated, "");
            Check("不提权时标记为假", !CliOptions.Parse(new string[] { "--shell", "ps" }).Elevated, "");
            Check("提权标记不会被当成普通参数",
                CliOptions.Parse(new string[] { "--elevated", "--shell", "cmd" }).ShellName == "cmd", "");

            // ---------------- 12. 高危识别 ----------------
            Section("高危命令识别");
            Check("format 判为高危", DangerCheck("format D: /q") == 2, "");
            Check("diskpart 判为高危", DangerCheck("diskpart") == 2, "");
            Check("rd /s 判为高危", DangerCheck("rd /s /q C:\\temp") == 2, "");
            Check("Clear-Disk 判为高危", DangerCheck("Clear-Disk -Number 1 -RemoveData") == 2, "");
            Check("Format-Volume 判为高危", DangerCheck("Format-Volume -DriveLetter D") == 2, "");
            Check("dir 判为安全", DangerCheck("dir /a") == 0, "");
            Check("Get-Process 判为安全", DangerCheck("Get-Process") == 0, "");

            // ---------------- 13. 界面语言 ----------------
            Section("界面语言");
            Check("解析 auto", Loc.Parse("auto") == Lang.Auto, "");
            Check("解析 zh", Loc.Parse("zh") == Lang.Zh, "");
            Check("解析 en", Loc.Parse("en") == Lang.En, "");
            Check("解析未知值回退 auto", Loc.Parse("klingon") == Lang.Auto, "");
            Check("Id 往返 zh", Loc.Id(Lang.Zh) == "zh", "");
            Check("Id 往返 en", Loc.Id(Lang.En) == "en", "");
            Check("Id 往返 auto", Loc.Id(Lang.Auto) == "auto", "");

            Loc.Set(Lang.Zh);
            Check("中文模式下 T() 原样返回", Loc.T("预设主题") == "预设主题", "实际 " + Loc.T("预设主题"));
            Check("中文模式下 IsEnglish 为假", !Loc.IsEnglish, "");

            Loc.Set(Lang.En);
            Check("英文模式下 IsEnglish 为真", Loc.IsEnglish, "");
            Check("英文模式下能查到译文", Loc.T("预设主题") == "Presets", "实际 [" + Loc.T("预设主题") + "]");
            Check("英文模式下没译文就回退中文",
                Loc.T("这个字符串肯定没有译文xyz") == "这个字符串肯定没有译文xyz", "");
            Check("带参数的 T() 能格式化", Loc.T("共 {0} 条", 5) == "Total 5 items",
                "实际 [" + Loc.T("共 {0} 条", 5) + "]");
            Check("预设主题名有译文", Loc.T("浅色扁平") != "浅色扁平", "");
            Check("命令库分类有译文", Loc.T("网络与连接") != "网络与连接", "");
            Check("语言选项本身有译文", Loc.T("简体中文") == "Simplified Chinese", "");
            Check("英文下「默认」显示 System", Loc.T("默认（跟随系统）") == "Default (System)",
                "实际 [" + Loc.T("默认（跟随系统）") + "]");
            Check("报错匹配模式没被翻译（否则跨工具检测会失效）",
                Loc.T("不是内部或外部命令") == "不是内部或外部命令",
                "实际 [" + Loc.T("不是内部或外部命令") + "]");
            Check("主题查找键没被翻译", Loc.T("浅色扁平") != "浅色扁平", "（显示名翻译、键仍是中文）");
            _log.AppendLine("  翻译表共 " + LangTable.En.Count + " 条");
            Loc.Set(Lang.Zh);

            // ---------------- 14. 多命令执行 ----------------
            Section("多命令执行");
            ShellKind ms = ShellKind.PowerShell;
            string multi = MainWindow.StripShellPrefixMulti("cmd> ipconfig\r\ndir\r\nnetstat -a", ref ms);
            Check("多行逐行剥离 cmd> 前缀", ms == ShellKind.Cmd, "实际 " + ms);
            Check("多行内容正确", multi == "ipconfig\ndir\nnetstat -a", "实际 [" + multi.Replace("\n", "|") + "]");
            Check("没有前缀时原样保留",
                MainWindow.StripShellPrefixMulti("echo a\necho b", ref ms) == "echo a\necho b", "");
            Check("单行仍然走老逻辑",
                MainWindow.StripShellPrefixMulti("ps> Get-Process", ref ms) == "Get-Process", "");

            AppSettings ms2 = new AppSettings();
            Check("多命令持久生效默认关闭", !ms2.MultiCommandPersist, "");
            Check("多行高度有默认值", ms2.MultiCommandHeight >= 80, "实际 " + ms2.MultiCommandHeight);
            Check("语言默认 auto", ms2.Language == "auto", "");
            _log.AppendLine("  多行高度默认 " + ms2.MultiCommandHeight + " px");

            Section("便携模式");
            Check("数据目录非空", !string.IsNullOrEmpty(AppPaths.UserDataDirectory), AppPaths.UserDataDirectory);
            Check("命令库目录在 exe 同级",
                AppPaths.CommandsDirectory.StartsWith(AppPaths.ExeDirectory), AppPaths.CommandsDirectory);
            _log.AppendLine("  当前模式：" + Loc.T(AppPaths.ModeDescription));
            _log.AppendLine("  数据目录：" + AppPaths.UserDataDirectory);
            bool portableNow = AppPaths.IsPortable;
            bool dataBesideExe = AppPaths.UserDataDirectory.StartsWith(
                AppPaths.ExeDirectory, StringComparison.OrdinalIgnoreCase);
            Check("便携判定与数据目录位置一致（便携↔exe同级，安装↔%APPDATA%）",
                portableNow == dataBesideExe,
                (portableNow ? "便携" : "安装") + " 但目录在 " + (dataBesideExe ? "exe 同级" : "%APPDATA%"));
            Check("非便携时数据目录不在 exe 同级（避免污染程序目录）",
                portableNow || !dataBesideExe, AppPaths.UserDataDirectory);

            // ---------------- 16. 跨工具候选必须能真的切换工具 ----------------
            Section("跨工具候选的切换标记");
            SuggestResult xs1 = Suggester.Compute(lib, "ping", 4, Environment.CurrentDirectory, ShellKind.PowerShell);
            Check("[PS] ping 被判为跨工具", xs1.OtherShellSpec != null, "OtherShellSpec 为空");
            Check("[PS] 首个候选带 SwitchShell 标记",
                xs1.Items.Count > 0 && xs1.Items[0].SwitchShell,
                xs1.Items.Count == 0 ? "无候选" : "SwitchShell=false —— 点了只会插文本，不会切工具");
            Check("[PS] 指向 CMD",
                xs1.Items.Count > 0 && xs1.Items[0].SwitchToShell == ShellKind.Cmd,
                xs1.Items.Count == 0 ? "无候选" : xs1.Items[0].SwitchToShell.ToString());

            SuggestResult xs2 = Suggester.Compute(lib, "Get-Process", 11, Environment.CurrentDirectory, ShellKind.Cmd);
            Check("[CMD] Get-Process 被判为跨工具", xs2.OtherShellSpec != null, "OtherShellSpec 为空");
            Check("[CMD] 首个候选带 SwitchShell 标记",
                xs2.Items.Count > 0 && xs2.Items[0].SwitchShell,
                xs2.Items.Count == 0 ? "无候选" : "SwitchShell=false");
            Check("[CMD] 指向 PowerShell",
                xs2.Items.Count > 0 && xs2.Items[0].SwitchToShell == ShellKind.PowerShell,
                xs2.Items.Count == 0 ? "无候选" : xs2.Items[0].SwitchToShell.ToString());

            // 同工具内的普通候选不能被误标（否则点一下就把工具切走了）
            SuggestResult xs3 = Suggester.Compute(lib, "Get-Ch", 6, Environment.CurrentDirectory, ShellKind.PowerShell);
            Check("同工具候选没有 SwitchShell 标记",
                xs3.Items.Count > 0 && !xs3.Items[0].SwitchShell, "普通候选被误标了 SwitchShell");

            // ---------------- 17. 按语法顺序限制提示参数 ----------------
            Section("按语法顺序限制提示参数");
            bool savedStrict = Suggester.StrictOrder;

            // 关闭（默认）状态：所有参数都列出，顺序与命令库一致
            Suggester.StrictOrder = false;
            SuggestResult off1 = Suggester.Compute(lib, "ping ", 5, Environment.CurrentDirectory, ShellKind.Cmd);
            Check("[关] ping 仍列出多个候选", off1.Items.Count > 3, "实际 " + off1.Items.Count + " 个");
            Check("[关] 首个仍是 <目标主机>",
                off1.Items.Count > 0 && off1.Items[0].Display == "<目标主机>",
                off1.Items.Count == 0 ? "无候选" : "实际 " + off1.Items[0].Display);
            bool hasOptionOff = false;
            foreach (Suggestion s in off1.Items) { if (s.Display == "-t") { hasOptionOff = true; break; } }
            Check("[关] 开关 -t 也在列表里（不隐藏）", hasOptionOff, "");

            // 打开状态：必填位置参数没填时，只提示它
            Suggester.StrictOrder = true;
            SuggestResult on1 = Suggester.Compute(lib, "ping ", 5, Environment.CurrentDirectory, ShellKind.Cmd);
            Check("[开] ping 只提示 <目标主机>", on1.Items.Count == 1, "实际 " + on1.Items.Count + " 个");
            Check("[开] 且就是 <目标主机>",
                on1.Items.Count == 1 && on1.Items[0].Display == "<目标主机>",
                on1.Items.Count == 1 ? on1.Items[0].Display : "无候选");

            SuggestResult on2 = Suggester.Compute(lib, "ping 1.1.1.1 ", 12, Environment.CurrentDirectory, ShellKind.Cmd);
            bool hasOptionOn = false;
            foreach (Suggestion s in on2.Items) { if (s.Display == "-t") { hasOptionOn = true; break; } }
            Check("[开] 填完位置参数后开关重新出现", hasOptionOn, "实际 " + on2.Items.Count + " 个候选");

            SuggestResult on3 = Suggester.Compute(lib, "xcopy ", 6, Environment.CurrentDirectory, ShellKind.Cmd);
            Check("[开] xcopy 只提示必填的 <源>",
                on3.Items.Count == 1 && on3.Items[0].Display == "<源>",
                on3.Items.Count == 0 ? "无候选" : "实际 " + on3.Items.Count + " 个，首个 " + on3.Items[0].Display);

            // PowerShell 侧同样成立
            SuggestResult on4 = Suggester.Compute(lib, "Get-ChildItem ", 14, Environment.CurrentDirectory, ShellKind.PowerShell);
            Check("[开] PowerShell 命令仍有候选（位置参数可选）", on4.Items.Count > 3, "实际 " + on4.Items.Count + " 个");

            Suggester.StrictOrder = savedStrict;

            // ---------------- 17. 环境 ----------------
            Section("运行环境");
            _log.AppendLine("  .NET Framework 版本：" + Environment.Version);
            _log.AppendLine("  系统：" + Environment.OSVersion);
            _log.AppendLine("  OEM 代码页：CP" + NativeMethods.GetOEMCP());
            _log.AppendLine("  是否管理员：" + Elevation.IsAdministrator());
            _log.AppendLine("  配置目录：" + AppPaths.UserDataDirectory);

            return Finish(outFile);
        }

        private static int DangerCheck(string s)
        {
            Type t = typeof(CommandLibrary).Assembly.GetType("WindowsCommandTools.MainWindow");
            System.Reflection.MethodInfo mi = t.GetMethod("DangerPattern",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            return (int)mi.Invoke(null, new object[] { s });
        }

        private static void ValidateNodes(CmdSpec s, List<ParamNode> nodes, ref int badNode, ref int badKind, ref int badHint)
        {
            foreach (ParamNode n in nodes)
            {
                if (n.Token.Length == 0) badNode++;
                string k = n.Kind;
                if (k != "flag" && k != "option" && k != "value" && k != "subcommand" && k != "syntax") badKind++;
                if ((n.IsOption || n.IsValue) && n.ValueHint.Length == 0 && n.Enum.Count == 0) badHint++;
                ValidateNodes(s, n.Children, ref badNode, ref badKind, ref badHint);
            }
        }

        private static int MaxDepth(List<ParamNode> nodes, int depth)
        {
            int max = depth;
            foreach (ParamNode n in nodes)
            {
                if (n.Children.Count == 0) continue;
                int d = MaxDepth(n.Children, depth + 1);
                if (d > max) max = d;
            }
            return max;
        }

        // ---- 断言辅助 ----
        private static void Expect(CommandLibrary lib, ShellKind shell, string line, string expectedFirst)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = r.Items.Count > 0 &&
                string.Equals(r.Items[0].Display, expectedFirst, StringComparison.OrdinalIgnoreCase);
            Check("[" + Shells.Display(shell) + "] 输入 \"" + line + "\" 首选 " + expectedFirst, ok,
                r.Items.Count == 0 ? "无候选" : "实际 " + r.Items[0].Display);
        }

        private static void ExpectFirst(CommandLibrary lib, ShellKind shell, string line, string expected)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = r.Items.Count > 0 &&
                string.Equals(r.Items[0].Display, expected, StringComparison.OrdinalIgnoreCase);
            Check("[" + Shells.Display(shell) + "] \"" + line + "\" 首个候选是 " + expected, ok,
                r.Items.Count == 0 ? "无候选" : "实际首个 " + r.Items[0].Display);
        }

        private static void ExpectContains(CommandLibrary lib, ShellKind shell, string line, string token)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = false;
            foreach (Suggestion s in r.Items)
            {
                if (s.Display.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) { ok = true; break; }
            }
            Check("[" + Shells.Display(shell) + "] \"" + line + "\" 提示包含 " + token, ok,
                r.Items.Count == 0 ? "无候选" : "候选 " + r.Items.Count + " 个，首个 " + r.Items[0].Display);
        }

        private static void ExpectAllPrefix(CommandLibrary lib, ShellKind shell, string line, string prefix)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = r.Items.Count > 0;
            foreach (Suggestion s in r.Items)
            {
                if (!s.Display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
            }
            Check("[" + Shells.Display(shell) + "] \"" + line + "\" 只提示 " + prefix + " 开头", ok,
                r.Items.Count == 0 ? "无候选" : "首个 " + r.Items[0].Display);
        }

        private static void ExpectHintContains(CommandLibrary lib, ShellKind shell, string line, string text)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = r.HintTitle.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            Check("[" + Shells.Display(shell) + "] \"" + line + "\" 提示值占位 " + text, ok, "实际提示 [" + r.HintTitle + "]");
        }

        private static void ExpectSpell(CommandLibrary lib, ShellKind shell, string line, string expected)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            bool ok = false;
            foreach (Suggestion s in r.Items)
            {
                if (string.Equals(s.Display, expected, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            }
            Check("[" + Shells.Display(shell) + "] 拼写纠错 \"" + line + "\" → " + expected, ok,
                r.Items.Count == 0 ? "无候选" : "首个 " + r.Items[0].Display);
        }

        private static void ExpectEmptyItems(CommandLibrary lib, ShellKind shell, string line)
        {
            SuggestResult r = Suggester.Compute(lib, line, line.Length, Environment.CurrentDirectory, shell);
            Check("[" + Shells.Display(shell) + "] 未知命令 \"" + line.Trim() + "\" 不硬造候选", r.Items.Count <= 1,
                "候选 " + r.Items.Count + " 个");
        }

        private static void CrossShell(CommandLibrary lib, ShellKind shell, string cmd, ShellKind expectOwner)
        {
            SuggestResult r = Suggester.Compute(lib, cmd, cmd.Length, Environment.CurrentDirectory, shell);
            bool ok = r.OtherShellSpec != null && r.OtherShellSpec.Shell == expectOwner;
            Check("在 " + Shells.Display(shell) + " 模式下输入 \"" + cmd + "\" 会提示它是 "
                + Shells.Display(expectOwner) + " 命令", ok,
                r.OtherShellSpec == null ? "没有识别出来" : "识别成了 " + Shells.Display(r.OtherShellSpec.Shell));
        }

        private static void NoCrossShell(CommandLibrary lib, ShellKind shell, string cmd)
        {
            SuggestResult r = Suggester.Compute(lib, cmd, cmd.Length, Environment.CurrentDirectory, shell);
            Check("在 " + Shells.Display(shell) + " 模式下输入 \"" + cmd + "\" 不报跨工具提示",
                r.OtherShellSpec == null, "被误判了");
        }
    }
}
