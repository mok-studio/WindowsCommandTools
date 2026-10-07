// ---------------------------------------------------------------------------
//  Exec.cs — 命令执行引擎 + 设置/历史持久化
//
//  关键点：
//   1. 中文 Windows 的 cmd 输出是 GBK(OEM 代码页)，而部分工具（git / python）输出
//      UTF-8。这里逐行做「严格 UTF-8 试解码，失败则回退 OEM 代码页」的智能判断，
//      所以中文不会变成乱码。
//   2. 输出是流式的（边跑边显示），长命令不会假死。
//   3. 可以随时「停止」，会连子进程一起杀掉（taskkill /T /F）。
//   4. 会话工作目录独立维护，cd 之后下一条命令仍在同一目录，体验跟真正的 cmd 一致。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WindowsCommandTools
{
    public sealed class OutputChunk
    {
        public string Text = "";
        public bool IsError;
        public bool IsSystem;   // 工具箱自己打印的信息
    }

    public sealed class ExecResult
    {
        public int ExitCode;
        public long ElapsedMs;
        public bool Cancelled;
        public string Error;
    }

    // -----------------------------------------------------------------------
    //  ExecOne —— 一个「进程槽」的公共部分
    //
    //  多进程改造的取舍：
    //    原来的 CommandExecutor 只管一个进程，所有状态字段直接摊在自己身上。要让多条
    //    命令同时跑，最省事也最不容易出错的做法是「抽出槽位」：进程句柄、两条输出泵
    //    线程、取消标记、退出码、计时器全部搬进一个基类，CommandExecutor 退化成
    //    「槽位 0 的薄封装」，多进程面板用槽位 1..N。
    //
    //  这样做的两个好处：
    //    1. 主控制台那条路径（Exec.Start / IsRunning / Cancel / Output / Finished）
    //       调用点和行为一个字都不用改，自检全部照旧 —— 只是实现搬了个家；
    //    2. 多进程面板直接复用同一套智能解码、CLIXML 还原、taskkill 取消逻辑，
    //       不必再写第二份「读进程输出」的代码（两份实现迟早会跑偏）。
    //
    //  事件带不带 Id 的区别：
    //    Output / Finished       老事件，只在「主通道」(Id 0) 上触发，行为同改造前；
    //    OutputId / FinishedId   新事件，任何槽位都触发并带上槽位号，
    //                            多进程面板据此把输出分流到各自任务的输出区。
    // -----------------------------------------------------------------------

    public class ExecOne
    {
        internal Process Proc;
        internal Thread OutThread;
        internal Thread ErrThread;
        private readonly object _lock = new object();
        internal volatile bool Cancelled;
        private Stopwatch _watch;
        internal int ExitCodeValue = -1;
        private int _pending;
        private int _exitCode = -1;

        /// <summary>槽位号：0 = 主控制台，1..N = 多进程面板的各个任务。</summary>
        public int Id;

        public event Action<OutputChunk> Output;
        public event Action<ExecResult> Finished;
        public event Action<int, OutputChunk> OutputId;
        public event Action<int, ExecResult> FinishedId;

        public bool IsRunning
        {
            get
            {
                Process p = Proc;
                if (p == null) return false;
                try { return !p.HasExited; }
                catch { return false; }
            }
        }

        public int ProcessId
        {
            get
            {
                Process p = Proc;
                if (p == null) return 0;
                try { return p.Id; }
                catch { return 0; }
            }
        }

        /// <summary>上一次运行的退出码；还没跑过是 -1。</summary>
        public int LastExitCode
        {
            get { return ExitCodeValue; }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 启动一个进程并接管它的标准输出 / 标准错误。
        /// 同一个槽位同一时刻只允许有一个进程：已经在跑就抛异常，由调用方决定怎么提示
        /// （主控制台原来就是靠这个异常提示「已有命令正在运行」的）。
        /// </summary>
        public void Execute(string command, string workingDirectory, Encoding forced, ShellKind shell, bool preferPwsh)
        {
            lock (_lock)
            {
                if (IsRunning) throw new InvalidOperationException(Loc.T("已有命令正在运行"));
            }

            string cwd = workingDirectory;
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
                cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ProcessStartInfo psi = new ProcessStartInfo();
            if (shell == ShellKind.PowerShell)
            {
                psi.FileName = CommandExecutor.ResolvePowerShell(preferPwsh);
                psi.Arguments = CommandExecutor.BuildPowerShellArgs(command);
            }
            else
            {
                psi.FileName = Environment.GetEnvironmentVariable("COMSPEC");
                if (string.IsNullOrEmpty(psi.FileName)) psi.FileName = "cmd.exe";
                // /d 跳过 AutoRun，/s 让 cmd 原样取用引号内的命令串
                psi.Arguments = "/d /s /c \"" + command + "\"";
            }
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;

            Process p = new Process();
            p.StartInfo = psi;
            Cancelled = false;
            _exitCode = -1;
            ExitCodeValue = -1;
            _pending = 2;
            _watch = Stopwatch.StartNew();

            // 先登记再启动：这样 UI 线程随时问 IsRunning 都能拿到真实句柄
            Proc = p;
            p.Start();

            // 每个槽位有自己独立的两条输出泵，读的是各自进程的重定向管道，
            // 所以多条命令的输出天然不会串到别人的输出区里。
            OutThread = new Thread(delegate () { Pump(p.StandardOutput.BaseStream, false, forced); });
            OutThread.IsBackground = true;
            ErrThread = new Thread(delegate () { Pump(p.StandardError.BaseStream, true, forced); });
            ErrThread.IsBackground = true;
            OutThread.Start();
            ErrThread.Start();
        }

        /// <summary>往这个槽位的进程标准输入写一行（主控制台的 stdin 输入行用）。</summary>
        public void Send(string text)
        {
            Process p = Proc;
            if (p == null) return;
            try
            {
                p.StandardInput.Write(text);
                p.StandardInput.Write("\r\n");
                p.StandardInput.Flush();
            }
            catch { }
        }

        /// <summary>往这个槽位的进程标准输入写原始内容，不补换行。</summary>
        public void SendRawText(string text)
        {
            Process p = Proc;
            if (p == null) return;
            try
            {
                p.StandardInput.Write(text);
                p.StandardInput.Flush();
            }
            catch { }
        }

        /// <summary>
        /// 停止这个槽位的进程：先 taskkill /T /F 整棵进程树，再兜底 Kill 一次。
        /// 为什么要 /T：命令常常会拉起子进程（robocopy、脚本、启动器…），只杀父进程
        /// 会留下孤儿进程继续占着管道，输出迟迟不结束，用户看着就像「停不掉」。
        /// </summary>
        public void Stop()
        {
            Process p = Proc;
            if (p == null) return;
            Cancelled = true;
            int pid = 0;
            try { pid = p.Id; } catch { }
            try
            {
                if (pid > 0) CommandExecutor.TaskKillTree(pid);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); }
            catch { }
            try { p.StandardInput.Close(); }
            catch { }
        }

        /// <summary>找可用的 PowerShell 宿主：优先 pwsh（PowerShell 7），不存在时回退系统自带的 5.1。</summary>
        public static string ResolvePowerShell(bool preferPwsh)
        {
            if (preferPwsh)
            {
                string found = FindOnPath("pwsh.exe");
                if (found != null) return found;
            }
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(ps)) return ps;
            string onPath = FindOnPath("powershell.exe");
            return onPath != null ? onPath : "powershell.exe";
        }

        public static bool PwshAvailable()
        {
            return FindOnPath("pwsh.exe") != null;
        }

        private static string FindOnPath(string exe)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;
                foreach (string dir in path.Split(';'))
                {
                    if (dir.Trim().Length == 0) continue;
                    try
                    {
                        string full = Path.Combine(dir.Trim(), exe);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 用 -EncodedCommand 传脚本：Base64(UTF-16LE) 完全避开了引号转义问题，
        /// 命令里带单引号、双引号、&amp;、| 都不会被 PowerShell 的解析器二次解释。
        /// 首行统一输出编码为 UTF-8，末尾把执行结果映射成进程退出码，便于判断失败。
        /// </summary>
        public static string BuildPowerShellArgs(string command)
        {
            // 注意：[Console]::OutputEncoding 在标准输出被重定向成管道时会抛「句柄无效」，
            // 所以必须包在 try/catch 里 —— 否则 PowerShell 会把错误以 CLIXML 写到 stderr，
            // 混进正常输出里。设置失败也没关系，逐行智能解码会按 GBK 处理。
            string script = string.Join("\r\n", new string[] {
                // 首次使用模块时 PowerShell 会往 stderr 写「Preparing modules for first use.」进度记录，
                // 关掉进度流，否则会混进输出里。
                "$ProgressPreference = 'SilentlyContinue'",
                "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }",
                "$global:LASTEXITCODE = 0",
                command,
                "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }",
                "if ($?) { exit 0 } else { exit 1 }"
            });
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + b64;
        }

        // ------------------------------------------------------------------

        private void Pump(Stream stream, bool isError, Encoding forced)
        {
            byte[] buf = new byte[4096];
            List<byte> line = new List<byte>(256);
            try
            {
                while (true)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == 0x0A)
                        {
                            Emit(line, isError, forced);
                            line.Clear();
                        }
                        else
                        {
                            line.Add(b);
                            if (line.Count > 1024 * 512) { Emit(line, isError, forced); line.Clear(); }
                        }
                    }
                }
            }
            catch { }
            if (line.Count > 0) Emit(line, isError, forced);

            if (Interlocked.Decrement(ref _pending) == 0) Done();
        }

        /// <summary>
        /// 两条输出管道都结束了才会走到这里 —— 这时进程基本已经退出。
        /// 必须再 WaitForExit 确认一次才能拿到真实退出码，否则会读到 -1
        /// （读取时机早于进程退出的老问题）。
        /// </summary>
        private void Done()
        {
            try
            {
                Process p = Proc;
                if (p != null && !p.WaitForExit(5000)) p.Kill();
                if (p != null) _exitCode = p.ExitCode;
            }
            catch { }

            ExitCodeValue = _exitCode;
            Stopwatch w = _watch;
            if (w != null) w.Stop();

            ExecResult r = new ExecResult();
            r.ExitCode = _exitCode;
            r.ElapsedMs = w == null ? 0 : w.ElapsedMilliseconds;
            r.Cancelled = Cancelled;

            // 老事件只在主通道触发，保持主控制台的行为不变
            Action<ExecResult> h = Finished;
            if (h != null) h(r);

            Action<int, ExecResult> h2 = FinishedId;
            if (h2 != null) h2(Id, r);
        }

        private void Emit(List<byte> bytes, bool isError, Encoding forced)
        {
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == 0x0D) bytes.RemoveAt(bytes.Count - 1);
            byte[] arr = bytes.ToArray();
            string text = CommandExecutor.StripAnsi(CommandExecutor.DecodeLine(arr, forced));
            if (text.Length == 0) { EmitLine("", isError); return; }

            // PowerShell 在 stderr 被重定向时会把错误流序列化成 CLIXML，
            // 这里先去掉标记行，再把 <S S="Error">…</S> 还原成人能读的纯文本。
            if (text.StartsWith("#< CLIXML")) return;
            if (text.IndexOf("<Objs", StringComparison.Ordinal) >= 0)
            {
                string decoded = CommandExecutor.DecodeClixml(text);
                string[] lines = decoded.Replace("\r\n", "\n").Split('\n');
                if (lines.Length == 0) return;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Trim().Length == 0 && i != lines.Length - 1) continue;
                    EmitLine(lines[i], isError);
                }
                return;
            }
            EmitLine(text, isError);
        }

        private void EmitLine(string text, bool isError)
        {
            OutputChunk chunk = new OutputChunk();
            chunk.Text = text;
            chunk.IsError = isError;
            // 老事件只在主通道触发，多进程面板走带 Id 的新事件
            if (Id == 0)
            {
                Action<OutputChunk> h = Output;
                if (h != null) h(chunk);
            }
            Action<int, OutputChunk> h2 = OutputId;
            if (h2 != null) h2(Id, chunk);
        }

        /// <summary>
        /// 把 PowerShell 的 CLIXML 错误流还原成纯文本。
        /// CLIXML 里换行写成 _x000A_、回车写成 _x000D_，尖括号写成 &amp;lt; / &amp;gt;。
        /// </summary>
        public static string DecodeClixml(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.IndexOf("<Objs", StringComparison.Ordinal) < 0) return "";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (true)
            {
                int a = raw.IndexOf("<S S=\"", i, StringComparison.Ordinal);
                if (a < 0) break;
                int tag = raw.IndexOf('>', a);
                if (tag < 0) break;
                int close = raw.IndexOf("</S>", tag, StringComparison.Ordinal);
                if (close < 0) break;
                sb.Append(UnescapeClixml(raw.Substring(tag + 1, close - tag - 1)));
                i = close + 4;
            }
            if (sb.Length == 0) return "";
            string text = sb.ToString();
            text = text.Replace("_x000D_", "").Replace("_x000A_", "\n").Replace("_x0009_", "\t");
            return text.TrimEnd();
        }

        private static string UnescapeClixml(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        }

        public static string StripAnsi(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\u001b') < 0) return s;
            return Regex.Replace(s, "\u001b\\[[0-9;?]*[ -/]*[@-~]", "");
        }

        /// <summary>逐行智能解码：严格 UTF-8 能解通就用 UTF-8，否则用系统 OEM 代码页（中文系统为 GBK）。</summary>
        public static string DecodeLine(byte[] bytes, Encoding forced)
        {
            if (forced != null) return forced.GetString(bytes);
            bool hasHigh = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80) { hasHigh = true; break; }
            }
            if (!hasHigh) return Encoding.ASCII.GetString(bytes);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException) { }
            try { return OemEncoding().GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        private static Encoding _oem;
        public static Encoding OemEncoding()
        {
            if (_oem != null) return _oem;
            int cp = 0;
            try { cp = NativeMethods.GetOEMCP(); } catch { }
            if (cp <= 0)
            {
                try { cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage; }
                catch { cp = 936; }
            }
            try { _oem = Encoding.GetEncoding(cp); }
            catch { _oem = Encoding.GetEncoding(936); }
            return _oem;
        }

        public static Encoding EncodingForInput()
        {
            return OemEncoding();
        }

        /// <summary>把设置里的编码名解析成 Encoding；"auto" 返回 null 表示智能判断。</summary>
        public static Encoding ResolveEncoding(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "auto") return null;
            try
            {
                if (name == "utf8") return new UTF8Encoding(false);
                if (name == "oem") return OemEncoding();
                if (name == "gbk") return Encoding.GetEncoding(936);
                return Encoding.GetEncoding(name);
            }
            catch { return null; }
        }

        /// <summary>同步执行并捕获输出（用于内部探测与自检）。</summary>
        public static string RunSync(string command, string cwd, int timeoutMs)
        {
            int code;
            return RunSync(command, cwd, timeoutMs, out code, ShellKind.Cmd, false);
        }

        public static string RunSync(string command, string cwd, int timeoutMs, out int exitCode)
        {
            return RunSync(command, cwd, timeoutMs, out exitCode, ShellKind.Cmd, false);
        }

        public static string RunSync(string command, string cwd, int timeoutMs, out int exitCode,
            ShellKind shell, bool preferPwsh)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                if (shell == ShellKind.PowerShell)
                {
                    psi.FileName = ResolvePowerShell(preferPwsh);
                    psi.Arguments = BuildPowerShellArgs(command);
                }
                else
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/d /s /c \"" + command + "\"";
                }
                psi.WorkingDirectory = string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : cwd;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                MemoryStream ms = new MemoryStream();
                Thread t = new Thread(delegate ()
                {
                    try { p.StandardOutput.BaseStream.CopyTo(ms); } catch { }
                });
                t.IsBackground = true;
                t.Start();
                Thread te = new Thread(delegate ()
                {
                    try { p.StandardError.BaseStream.CopyTo(ms); } catch { }
                });
                te.IsBackground = true;
                te.Start();
                if (!p.WaitForExit(timeoutMs <= 0 ? 8000 : timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return "";
                }
                t.Join(500);
                te.Join(500);
                try { exitCode = p.ExitCode; } catch { }
                return DecodeLine(ms.ToArray(), null);
            }
            catch { return ""; }
        }
    }

    // -----------------------------------------------------------------------
    //  CommandExecutor —— 多进程执行器
    //
    //  对外仍然是「一个执行器」：
    //    · 槽位 0（主通道）就是原来那个单进程接口 —— Start / IsRunning / Cancel /
    //      SendInput / SendRaw / Output / Finished 全部保留，主控制台和自检不受影响；
    //    · 槽位 1..SlotCount-1 给多进程面板用，每个槽位有独立的进程、独立的重定向
    //      管道、独立的取消和退出码，互不干扰。
    //
    //  槽位数组是静态的：面板的任务行绑定固定的槽位号，不挂在窗口实例上 ——
    //  切换语言会整体重建主界面，静态槽位能让正在跑的命令活过那次重建。
    // -----------------------------------------------------------------------
    public sealed class CommandExecutor
    {
        /// <summary>
        /// 0 = 主控制台，1..SlotCount-1 = 多进程窗口的各条任务。
        ///
        /// = 1 + MultiProcWindow.MaxRows（16）：多进程窗口允许用户用 + / - 增删任务行，
        /// 设置里的行数上限（默认 8）最大就是 16，所以这里按 16 行预分配。
        /// 写成一个 const 表达式而不是字面量 17，是为了让「行数上限」只有一个真源：
        /// 以后要放宽上限，只改 MultiProcWindow.MaxRows 这一个常量即可。
        /// 槽位本身很轻（一个进程句柄 + 两个线程字段的壳），17 个占不了什么资源，
        /// 换来的是「槽位数组是静态只读的、永远不用加锁」。
        /// </summary>
        /// <summary>
        /// **建议**的槽位上限，只用来给界面一个默认值 —— 槽位实际是按需增长的，
        /// 真正的上限只受内存限制（见 Slot(int)）。
        /// </summary>
        public const int SuggestedSlotCount = 1 + MultiProcWindow.MaxRows;

        /// <summary>兼容旧名字。</summary>
        public const int SlotCount = SuggestedSlotCount;

        // 槽位按需增长：行数「不限制」时不能靠预分配数组。
        // 用 List + 锁，主线程取槽位、后台线程收尾都可能碰它。
        private static readonly List<ExecOne> _slots = CreateSlots();
        private static readonly object _slotLock = new object();

        /// <summary>主通道：原来的单进程接口就是它。</summary>
        private readonly ExecOne _main = _slots[0];

        private static List<ExecOne> CreateSlots()
        {
            List<ExecOne> all = new List<ExecOne>();
            // 预建到"建议上限"，之后按需增长
            while (all.Count < SuggestedSlotCount) all.Add(NewSlot(all.Count));
            return all;
        }

        private static ExecOne NewSlot(int id)
        {
            ExecOne s = new ExecOne();
            s.Id = id;
            return s;
        }

        /// <summary>取一个槽位（界面绑定用）。越界返回 null，调用方要判空。</summary>
        /// <summary>
        /// 取一个槽位，**按需增长**。越界（负数）返回 null，调用方要判空。
        /// 行数「不限制」时这里会一直往后长，所以没有硬上限。
        /// </summary>
        public static ExecOne Slot(int id)
        {
            if (id < 0) return null;
            lock (_slotLock)
            {
                while (_slots.Count <= id) _slots.Add(NewSlot(_slots.Count));
                return _slots[id];
            }
        }

        /// <summary>当前已经用到的槽位数量（界面用来显示"共 N 条"）。</summary>
        public static int SlotCreated
        {
            get { lock (_slotLock) { return _slots.Count; } }
        }

        /// <summary>停掉所有槽位里的进程（关窗时调用，避免留下孤儿进程）。</summary>
        public static void CancelAll()
        {
            ExecOne[] snap;
            lock (_slotLock) { snap = _slots.ToArray(); }
            for (int i = 0; i < snap.Length; i++)
            {
                try { snap[i].Stop(); }
                catch { }
            }
        }

        /// <summary>
        /// 结束整棵进程树（taskkill /T /F）。
        /// Stop() 只结束直接子进程；ping -t、diskpart 这类会派生子进程，
        /// 需要连树一起杀，否则会留下孤儿进程。
        /// </summary>
        public static void TaskKillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill",
                    "/T /F /PID " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process pro = Process.Start(psi);
                if (pro != null) pro.WaitForExit(4000);
            }
            catch { }
        }

        // ---- 静态工具方法：原来都在 CommandExecutor 上，重构后移到了 ExecOne。
        //      这里逐个转发，保证界面层 30 多处 CommandExecutor.Xxx() 不用改。 ----

        public static string ResolvePowerShell(bool preferPwsh) { return ExecOne.ResolvePowerShell(preferPwsh); }
        public static bool PwshAvailable() { return ExecOne.PwshAvailable(); }
        public static string BuildPowerShellArgs(string command) { return ExecOne.BuildPowerShellArgs(command); }
        public static string DecodeClixml(string raw) { return ExecOne.DecodeClixml(raw); }
        public static string StripAnsi(string s) { return ExecOne.StripAnsi(s); }
        public static string DecodeLine(byte[] bytes, Encoding forced) { return ExecOne.DecodeLine(bytes, forced); }
        public static Encoding OemEncoding() { return ExecOne.OemEncoding(); }
        public static Encoding EncodingForInput() { return ExecOne.EncodingForInput(); }
        public static Encoding ResolveEncoding(string name) { return ExecOne.ResolveEncoding(name); }
        public static string RunSync(string command, string cwd, int timeoutMs)
        { return ExecOne.RunSync(command, cwd, timeoutMs); }
        public static string RunSync(string command, string cwd, int timeoutMs, out int exitCode)
        { return ExecOne.RunSync(command, cwd, timeoutMs, out exitCode); }
        public static string RunSync(string command, string cwd, int timeoutMs, out int exitCode,
            ShellKind shell, bool preferPwsh)
        { return ExecOne.RunSync(command, cwd, timeoutMs, out exitCode, shell, preferPwsh); }
        public static bool LooksLikeCmdUnknown(string text) { return ShellErrors.LooksLikeCmdUnknown(text); }
        public static bool LooksLikePowerShellUnknown(string text) { return ShellErrors.LooksLikePowerShellUnknown(text); }
        public static bool IsUnknownCommandError(string text) { return ShellErrors.IsUnknownCommandError(text); }
        public static string ExtractUnknownName(string text) { return ShellErrors.ExtractUnknownName(text); }

        // ---- 主通道的老接口：签名和行为都保持不变 ----

        public event Action<OutputChunk> Output
        {
            add { _main.Output += value; }
            remove { _main.Output -= value; }
        }

        public event Action<ExecResult> Finished
        {
            add { _main.Finished += value; }
            remove { _main.Finished -= value; }
        }

        public bool IsRunning
        {
            get { return _main.IsRunning; }
        }

        public int ProcessId
        {
            get { return _main.ProcessId; }
        }

        public void Start(string command, string workingDirectory, Encoding forced)
        {
            Start(command, workingDirectory, forced, ShellKind.Cmd, false);
        }

        public void Start(string command, string workingDirectory, Encoding forced, ShellKind shell, bool preferPwsh)
        {
            _main.Execute(command, workingDirectory, forced, shell, preferPwsh);
        }

        public void SendInput(string text)
        {
            _main.Send(text);
        }

        public void SendRaw(string text)
        {
            _main.SendRawText(text);
        }

        public void Cancel()
        {
            _main.Stop();
        }
    }

    // ---------------------------------------------------------------------------
    //  跨工具识别：cmd / PowerShell 互相不认识的命令，报错长什么样
    //
    //  当你在 cmd 模式下执行 Get-Process，cmd 会说「'Get-Process' 不是内部或外部命令」；
    //  反过来在 PowerShell 里执行 ipconfig 之外的 cmd 内建命令（例如 dir /s），
    //  PowerShell 会说「无法将"dir"项识别为 cmdlet」。
    //  这里把这两类报错认出来，并把命令名抽出来，交给界面提示用户切换工具。
    // ---------------------------------------------------------------------------
    public static class ShellErrors
    {
        private static readonly string[] CmdUnknown = new string[] {
            "不是内部或外部命令",                                   // 中文 cmd
            "is not recognized as an internal or external command",  // 英文 cmd
            "命令语法不正确",                                        // dir /s 在 PS 里被当参数时的报错
        };

        private static readonly string[] PsUnknown = new string[] {
            "无法将",                                                // 「无法将"xxx"项识别为 cmdlet」
            "项识别为 cmdlet",
            "is not recognized as the name of a cmdlet",
            "CommandNotFoundException",
            "The term '"
        };

        public static bool LooksLikeCmdUnknown(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string p in CmdUnknown)
            {
                if (text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static bool LooksLikePowerShellUnknown(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string p in PsUnknown)
            {
                if (text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>是不是「不认识这个命令」这一类报错。</summary>
        public static bool IsUnknownCommandError(string text)
        {
            return LooksLikeCmdUnknown(text) || LooksLikePowerShellUnknown(text);
        }

        /// <summary>从报错文本里抽出那个不被识别的命令名；抽不到返回空串。</summary>
        public static string ExtractUnknownName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // 中文 PowerShell：无法将“Get-Process”项识别为 cmdlet
            int a = text.IndexOf('“');
            int b = text.IndexOf('”');
            if (a >= 0 && b > a) return text.Substring(a + 1, b - a - 1).Trim();
            // 英文两者都用单引号：'Get-Process' is not recognized / The term 'x' is not recognized
            int c = text.IndexOf('\'');
            if (c >= 0)
            {
                int d = text.IndexOf('\'', c + 1);
                if (d > c) return text.Substring(c + 1, d - c - 1).Trim();
            }
            return "";
        }
    }

    // ---------------------------------------------------------------------------
    //  设置
    // ---------------------------------------------------------------------------

    public sealed class AppSettings
    {
        public string ThemeName = "浅色扁平";
        public string OutputEncoding = "auto";      // auto | utf8 | oem | gbk
        public double FontSize = 13.0;
        public string FontFamily = "Consolas";
        public bool ConfirmDanger = true;
        public bool ConfirmLevel1 = false;
        public bool AutoScroll = true;
        public bool SaveHistory = true;
        public int MaxHistory = 400;
        public string LastDirectory = "";
        public double WindowWidth = 1320;
        public double WindowHeight = 840;
        public bool Maximized;
        public bool SidebarCollapsed;
        public double SidebarWidth = 268;
        public double RightPanelWidth = 340;

        // ---- 多进程窗口自己的面板宽度 ----
        // 和主窗口完全独立：改一个不会动另一个。
        // 0 = 还没设过；首次打开多进程窗口时跟随主窗口当时的宽度，之后各记各的。
        public double MpSidebarWidth;
        public double MpRightPanelWidth;

        /// <summary>
        /// 控制台底部那块帮助（WindowsCommandTools · Tab 补全 · ↑↓ 选择候选 …）。
        /// 默认开启；关掉后控制台只留输出，可视高度更大。
        /// </summary>
        public bool ShowConsoleHelp = true;

        /// <summary>
        /// 多进程窗口下方输出区的高度（拖动分隔条写回）。
        /// 0 或负数 = 还没设过，界面用自己的默认值。
        /// 注意别和 MultiProcHeight 混：那个是"多进程窗口"的整体高度。
        /// </summary>
        public double MpOutputHeight = 300;

        // ---- 双引擎相关 ----
        /// <summary>默认以哪个 shell 启动："ps"（默认）或 "cmd"。</summary>
        public string DefaultShell = "ps";
        /// <summary>PowerShell 优先使用 PowerShell 7（pwsh.exe），没有则自动回退 5.1。</summary>
        public bool PreferPwsh;
        /// <summary>检测到「命令属于另一个工具」时给出提示。</summary>
        public bool CrossShellHint = true;
        /// <summary>启动时自动请求管理员权限（UAC）。默认关闭。</summary>
        public bool RunAsAdmin;

        // ---- 多命令执行 ----
        /// <summary>多命令模式是否持久生效。默认关闭：只生效一次，执行完回到单行输入框。</summary>
        public bool MultiCommandPersist;
        /// <summary>多行输入区高度（像素）。</summary>
        public double MultiCommandHeight = 150;
        /// <summary>多行输入区里保留的内容，下次勾选还在。</summary>
        public string MultiCommandText = "";

        // ---- 多进程执行 ----
        /// <summary>
        /// 多进程面板高度（像素），默认 220。
        /// 面板展开时占这么高，下面的控制台相应变矮 —— 所以这个值直接决定
        /// 「4 条任务各自的输出区能看几行」。设置里的滑块范围是 180~600，
        /// 也可以直接拖面板下沿的分隔条（拖完会写回这里）。
        /// 下限取 180 而不是更小：再矮下去 4 条任务的输出区就只剩一条缝，没法看。
        /// 任务内容本身不持久化（进程跑完就没了），这里只存高度。
        /// </summary>
        public double MultiProcHeight = 220;

        // ---- 界面语言 ----
        /// <summary>界面语言："auto"（跟随系统）/"zh"（简体中文）/"en"（English）。</summary>
        public string Language = "auto";

        /// <summary>
        /// 按语法顺序限制提示参数。默认关闭。
        /// 关闭：所有参数都列出，已用过的标灰提示「已使用」。
        /// 打开：只列出当前语法位置**合法**的参数，避免选出无法执行的组合。
        /// </summary>
        public bool StrictSyntaxOrder;

        /// <summary>
        /// 主界面自由控件宽度。默认关闭。
        /// 关闭：三个板块的宽度由 SidebarWidth / RightPanelWidth 固定；
        /// 打开：可以用鼠标拖动板块之间的分隔条，宽度写回上面两个字段。
        /// </summary>
        public bool FreePanelWidth;

        /// <summary>
        /// 多进程任务行数上限。默认 8；**0 表示不限制**。最少 1 行。
        /// 行数多时窗口内滚动，不压缩每行的输出空间。
        /// </summary>
        public int MultiProcRowLimit = 8;

        /// <summary>
        /// 合并命令行窗口。默认启用。
        /// 启用：多进程各任务的输出集中在一个总窗口，用顶部分页标签切换；
        ///       标签拖出去会吸回来。
        /// 关闭：每个任务一个独立窗口，手动拖动也不会触发合并。
        /// </summary>
        public bool MergeCommandWindows = true;

        // ---- 主题 ----
        /// <summary>是否为每种模式（CMD / PowerShell）各用一套独立主题。默认关闭。</summary>
        public bool SeparateThemePerShell;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    Dictionary<string, object> o = Json.AsObj(Json.ParseFile(AppPaths.SettingsFile));
                    if (o != null) return FromJson(o);
                }
            }
            catch { }
            return new AppSettings();
        }

        private static AppSettings FromJson(Dictionary<string, object> o)
        {
            AppSettings s = new AppSettings();
            s.ThemeName = Json.Str(o, "themeName", s.ThemeName);
            s.OutputEncoding = Json.Str(o, "outputEncoding", s.OutputEncoding);
            s.FontSize = Json.Dbl(o, "fontSize", s.FontSize);
            s.FontFamily = Json.Str(o, "fontFamily", s.FontFamily);
            s.ConfirmDanger = Json.Bool(o, "confirmDanger", s.ConfirmDanger);
            s.ConfirmLevel1 = Json.Bool(o, "confirmLevel1", s.ConfirmLevel1);
            s.AutoScroll = Json.Bool(o, "autoScroll", s.AutoScroll);
            s.SaveHistory = Json.Bool(o, "saveHistory", s.SaveHistory);
            s.MaxHistory = Json.Int(o, "maxHistory", s.MaxHistory);
            s.LastDirectory = Json.Str(o, "lastDirectory", s.LastDirectory);
            s.WindowWidth = Json.Dbl(o, "windowWidth", s.WindowWidth);
            s.WindowHeight = Json.Dbl(o, "windowHeight", s.WindowHeight);
            s.Maximized = Json.Bool(o, "maximized", s.Maximized);
            s.SidebarCollapsed = Json.Bool(o, "sidebarCollapsed", s.SidebarCollapsed);
            s.SidebarWidth = Json.Dbl(o, "sidebarWidth", s.SidebarWidth);
            s.RightPanelWidth = Json.Dbl(o, "rightPanelWidth", s.RightPanelWidth);
            s.MpSidebarWidth = Json.Dbl(o, "mpSidebarWidth", s.MpSidebarWidth);
            s.MpRightPanelWidth = Json.Dbl(o, "mpRightPanelWidth", s.MpRightPanelWidth);
            s.ShowConsoleHelp = Json.Bool(o, "showConsoleHelp", s.ShowConsoleHelp);
            s.MpOutputHeight = Json.Dbl(o, "mpOutputHeight", s.MpOutputHeight);
            s.DefaultShell = Json.Str(o, "defaultShell", s.DefaultShell);
            s.PreferPwsh = Json.Bool(o, "preferPwsh", s.PreferPwsh);
            s.CrossShellHint = Json.Bool(o, "crossShellHint", s.CrossShellHint);
            s.RunAsAdmin = Json.Bool(o, "runAsAdmin", s.RunAsAdmin);
            s.MultiCommandPersist = Json.Bool(o, "multiCommandPersist", s.MultiCommandPersist);
            s.MultiCommandHeight = Json.Dbl(o, "multiCommandHeight", s.MultiCommandHeight);
            s.MultiCommandText = Json.Str(o, "multiCommandText", s.MultiCommandText);
            s.MultiProcHeight = Json.Dbl(o, "multiProcHeight", s.MultiProcHeight);
            s.Language = Json.Str(o, "language", s.Language);
            s.SeparateThemePerShell = Json.Bool(o, "separateThemePerShell", s.SeparateThemePerShell);
            s.StrictSyntaxOrder = Json.Bool(o, "strictSyntaxOrder", s.StrictSyntaxOrder);
            s.FreePanelWidth = Json.Bool(o, "freePanelWidth", s.FreePanelWidth);
            s.MultiProcRowLimit = Json.Int(o, "multiProcRowLimit", s.MultiProcRowLimit);
            s.MergeCommandWindows = Json.Bool(o, "mergeCommandWindows", s.MergeCommandWindows);
            return s;
        }

        /// <summary>把当前设置序列化出来（Save 和 IsDefault 共用）。</summary>
        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> o = Json.NewObj();
                o["themeName"] = ThemeName;
                o["outputEncoding"] = OutputEncoding;
                o["fontSize"] = FontSize;
                o["fontFamily"] = FontFamily;
                o["confirmDanger"] = ConfirmDanger;
                o["confirmLevel1"] = ConfirmLevel1;
                o["autoScroll"] = AutoScroll;
                o["saveHistory"] = SaveHistory;
                o["maxHistory"] = MaxHistory;
                o["lastDirectory"] = LastDirectory;
                o["windowWidth"] = WindowWidth;
                o["windowHeight"] = WindowHeight;
                o["maximized"] = Maximized;
                o["sidebarCollapsed"] = SidebarCollapsed;
                o["sidebarWidth"] = SidebarWidth;
                o["rightPanelWidth"] = RightPanelWidth;
            o["mpSidebarWidth"] = MpSidebarWidth;
            o["mpRightPanelWidth"] = MpRightPanelWidth;
            o["showConsoleHelp"] = ShowConsoleHelp;
            o["mpOutputHeight"] = MpOutputHeight;
                o["defaultShell"] = DefaultShell;
                o["preferPwsh"] = PreferPwsh;
                o["crossShellHint"] = CrossShellHint;
                o["runAsAdmin"] = RunAsAdmin;
                o["multiCommandPersist"] = MultiCommandPersist;
                o["multiCommandHeight"] = MultiCommandHeight;
                o["multiCommandText"] = MultiCommandText;
                // 必须进 ToJson：IsDefault() 是靠「当前设置 vs 默认设置」的 JSON 比对实现的，
                // 漏掉这个字段会让「只改过多进程面板高度」的设置被判成默认值而被删掉。
                o["multiProcHeight"] = MultiProcHeight;
            o["language"] = Language;
            o["separateThemePerShell"] = SeparateThemePerShell;
            o["strictSyntaxOrder"] = StrictSyntaxOrder;
            o["freePanelWidth"] = FreePanelWidth;
            o["multiProcRowLimit"] = MultiProcRowLimit;
            o["mergeCommandWindows"] = MergeCommandWindows;
            return o;
        }

        /// <summary>是不是全是默认值（是的话不需要写文件）。</summary>
        public bool IsDefault()
        {
            try
            {
                return Json.Write(ToJson()) == Json.Write(new AppSettings().ToJson());
            }
            catch { return false; }
        }

        /// <summary>
        /// 落盘。**只有与默认值不同才写** —— 全是默认值时反而会把已有文件删掉。
        /// 这样"没动过设置"的用户磁盘上不会多出任何文件，便携模式下尤其重要。
        /// </summary>
        public void Save()
        {
            try
            {
                if (IsDefault())
                {
                    if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                    return;
                }
                if (!AppPaths.EnsureUserDataDirectory()) return;
                Json.WriteToFile(AppPaths.SettingsFile, ToJson());
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------------
    //  历史记录与收藏
    // ---------------------------------------------------------------------------

    public sealed class HistoryEntry
    {
        public string Command = "";
        public string Directory = "";
        public int ExitCode;
        public DateTime Time = DateTime.Now;
    }

    public sealed class HistoryStore
    {
        public List<HistoryEntry> Entries = new List<HistoryEntry>();
        public List<string> Favorites = new List<string>();

        private const int MaxFavorites = 200;

        public static HistoryStore Load()
        {
            HistoryStore h = new HistoryStore();
            try
            {
                if (File.Exists(AppPaths.HistoryFile))
                {
                    Dictionary<string, object> o = Json.AsObj(Json.ParseFile(AppPaths.HistoryFile));
                    if (o != null)
                    {
                        List<object> arr = Json.AsArr(Json.Get(o, "entries"));
                        if (arr != null)
                        {
                            foreach (object item in arr)
                            {
                                Dictionary<string, object> eo = Json.AsObj(item);
                                if (eo == null) continue;
                                HistoryEntry e = new HistoryEntry();
                                e.Command = Json.Str(eo, "command", "");
                                e.Directory = Json.Str(eo, "directory", "");
                                e.ExitCode = Json.Int(eo, "exitCode", 0);
                                string t = Json.Str(eo, "time", "");
                                DateTime dt;
                                if (DateTime.TryParse(t, out dt)) e.Time = dt;
                                if (e.Command.Length > 0) h.Entries.Add(e);
                            }
                        }
                        h.Favorites = Json.StrList(o, "favorites");
                    }
                }
            }
            catch { }
            return h;
        }

        public void Save()
        {
            try
            {
                // 没有历史也没有收藏就别留文件
                if (Entries.Count == 0 && Favorites.Count == 0)
                {
                    if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile);
                    return;
                }
                if (!AppPaths.EnsureCachesDirectory()) return;
                Dictionary<string, object> o = Json.NewObj();
                List<object> arr = new List<object>();
                int start = Math.Max(0, Entries.Count - 400);
                for (int i = start; i < Entries.Count; i++)
                {
                    Dictionary<string, object> eo = Json.NewObj();
                    eo["command"] = Entries[i].Command;
                    eo["directory"] = Entries[i].Directory;
                    eo["exitCode"] = Entries[i].ExitCode;
                    eo["time"] = Entries[i].Time.ToString("yyyy-MM-dd HH:mm:ss");
                    arr.Add(eo);
                }
                o["entries"] = arr;
                o["favorites"] = Favorites;
                Json.WriteToFile(AppPaths.HistoryFile, o);
            }
            catch { }
        }

        public void Add(string command, string directory, int exitCode, int max)
        {
            if (string.IsNullOrEmpty(command)) return;
            command = command.Trim();
            if (command.Length == 0) return;
            // 连续重复的命令只保留一条
            if (Entries.Count > 0 && Entries[Entries.Count - 1].Command == command)
            {
                Entries[Entries.Count - 1].Time = DateTime.Now;
                Entries[Entries.Count - 1].ExitCode = exitCode;
                return;
            }
            HistoryEntry e = new HistoryEntry();
            e.Command = command;
            e.Directory = directory;
            e.ExitCode = exitCode;
            e.Time = DateTime.Now;
            Entries.Add(e);
            while (Entries.Count > max) Entries.RemoveAt(0);
        }

        public bool IsFavorite(string command)
        {
            foreach (string f in Favorites)
            {
                if (string.Equals(f, command, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public void ToggleFavorite(string command)
        {
            if (string.IsNullOrEmpty(command)) return;
            for (int i = 0; i < Favorites.Count; i++)
            {
                if (string.Equals(Favorites[i], command, StringComparison.OrdinalIgnoreCase))
                {
                    Favorites.RemoveAt(i);
                    return;
                }
            }
            Favorites.Add(command.Trim());
            while (Favorites.Count > MaxFavorites) Favorites.RemoveAt(0);
        }

        public List<HistoryEntry> Recent(int count, string filter)
        {
            List<HistoryEntry> result = new List<HistoryEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = Entries.Count - 1; i >= 0 && result.Count < count; i--)
            {
                HistoryEntry e = Entries[i];
                if (seen.Contains(e.Command)) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    e.Command.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                seen.Add(e.Command);
                result.Add(e);
            }
            return result;
        }
    }

    // ---------------------------------------------------------------------------
    //  管理员权限
    // ---------------------------------------------------------------------------

    public static class Elevation
    {
        public static bool IsAdministrator()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>以管理员身份重新启动本程序。</summary>
        public static bool RestartAsAdmin(string extraArgs, string workingDirectory)
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                string args = "--cwd \"" + workingDirectory + "\"";
                if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs;
                psi.Arguments = args;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 用管理员权限重新启动自己，命令行参数原样带过去。
        /// 返回 null 表示成功；否则返回给用户看的失败原因
        /// （用户在 UAC 弹窗上点"否"是最常见的情况，要区别对待）。
        /// </summary>
        public static string RelaunchElevated(string[] args)
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                // 带上 --elevated 标记：万一提权后拿到的仍然不是管理员令牌，
                // 子进程也不会再请求一次，避免无限重启。
                string joined = JoinArgs(args);
                psi.Arguments = joined.Length == 0 ? "--elevated" : "--elevated " + joined;
                psi.WorkingDirectory = Environment.CurrentDirectory;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = ERROR_CANCELLED，用户在 UAC 对话框上点了"否"
                if (ex.NativeErrorCode == 1223) return "cancelled";
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>把参数数组拼回命令行，含空格的参数加引号。</summary>
        public static string JoinArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.IndexOf(' ') >= 0 || a.IndexOf('\t') >= 0)
                {
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                }
                else sb.Append(a);
            }
            return sb.ToString();
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern int GetOEMCP();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern int GetACP();
    }
}
