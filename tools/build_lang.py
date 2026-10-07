# -*- coding: utf-8 -*-
"""生成 src/LangTable.cs：把 tools/i18n/*.json 的译文合并进 C# 字典。

用法：python tools/build_lang.py
会打印覆盖率报告：源码里还有哪些中文没有译文（这些会回退成中文显示）。
"""
import io
import json
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
I18N = os.path.join(ROOT, "tools", "i18n")

# ---------------------------------------------------------------- 手工补充
# 脚本抓不到的、或提取之后才新增的文案。
EXTRA = {
    # ---- 语言选项本身 ----
    "默认（跟随系统）": "Default (System)",
    "简体中文": "Simplified Chinese",
    "界面语言": "Language",
    "切换后界面立即刷新，不需要重启。选「默认」时跟随系统语言：系统是中文就用简体中文，否则用 English。":
        "Switches immediately, no restart needed. \"Default\" follows the system language: "
        "Simplified Chinese on Chinese systems, English otherwise. ",
    "命令库的说明文字目前只有中文。":
        "Command library descriptions are currently Chinese only.",
    "已切换到 ": "Switched to ",
    " 模式": " mode",
    "（宿主 ": " (host: ",

    # ---- 多命令执行 ----
    "多命令": "Multi-line",
    "多命令 · 开": "Multi-line · ON",
    "多命令执行": "Multi-line execution",
    "多命令执行持久生效": "Keep multi-line mode on",
    "多行输入区高度": "Multi-line box height",
    "打开输入框旁边的「多命令」开关后，单行输入框会变成一块多行文本区，":
        "Turn on the Multi-line switch next to the input box and the single-line field becomes a multi-line editor. ",
    "可以把一整段命令粘进去一次执行 —— 就像在记事本里编辑好再执行。":
        "Paste a whole script and run it in one go — like editing in Notepad and then executing. ",
    "整段文本交给同一个 shell，所以 cd、变量、管道在行与行之间是连着的。":
        "The entire text goes to a single shell, so cd, variables and pipelines carry across lines.",
    "已开启：多命令模式会一直保持，直到你手动关掉开关。":
        "On: multi-line mode stays until you switch it off.",
    "已关闭（默认）：多命令模式只生效一次，执行完自动回到单行输入框。":
        "Off (default): multi-line mode applies once, then the single-line input returns.",
    "打开多命令执行：输入框变成多行文本区，可以把一整段命令粘进去一次执行。高度可在设置里调。":
        "Enable multi-line execution: the input becomes a multi-line editor for pasting a whole script. Height is configurable in Settings.",
    "多命令执行只生效一次，执行后会自动回到单行输入框（可在设置里改成持久生效）。点这里立即关闭。":
        "Multi-line mode applies once and then returns to the single-line input (enable persistence in Settings). Click to turn it off now.",
    "多命令执行已开启（持久生效）。点这里回到单行输入框。":
        "Multi-line mode is on (persistent). Click to go back to the single-line input.",

    # ---- 带占位符的（先翻译再 string.Format）----
    "共 {0} 条": "Total {0} items",
    "共 {0} 条匹配": "{0} matches",
    "共 {0} 条 · {1}": "{0} items · {1}",
    "{0} 个候选": "{0} suggestions",
    "点击应用「{0}」": "Click to apply \"{0}\"",
    "输出已保存到 {0}": "Output saved to {0}",
    "保存失败：{0}": "Save failed: {0}",
    "路径格式不正确：{0}": "Invalid path: {0}",
    "启动失败：{0}": "Failed to start: {0}",
    "已取消：{0}": "Cancelled: {0}",

    # ---- 8 套预设主题 ----
    "浅色扁平": "Light flat",
    "深色青绿": "Dark teal",
    "午夜紫": "Midnight purple",
    "深海蓝": "Deep blue",
    "暖阳橙": "Warm orange",
    "森林绿": "Forest green",
    "极简灰白": "Minimal gray",
    "高对比暗黑": "High contrast dark",

    # ---- 15 个命令库分类 ----
    "常用": "Common",
    "收藏": "Favorites",
    "历史": "History",
    "文件与目录": "Files & folders",
    "磁盘与系统修复": "Disks & repair",
    "网络与连接": "Network",
    "系统信息与管理": "System info",
    "电源与计划任务": "Power & tasks",
    "命令解释器与批处理": "Shell & batch",
    "第三方与常用工具": "Third-party",
    "进程、服务与作业": "Processes & services",
    "网络与共享": "Network & sharing",
    "事件日志、性能与 CIM": "Events & CIM",
    "磁盘、分区与卷": "Disks & volumes",
    "管道与数据处理": "Pipeline & data",
    "脚本、模块与语言": "Scripting & modules",
    "远程、用户与计划任务": "Remote & users",
    "未分类": "Uncategorized",

    # ---- 双引擎 ----
    "CMD（命令提示符）": "CMD (Command Prompt)",

    # ---- 控制台帮助条 ----
    "控制台提示": "Console hint bar",
    "控制台底部那块帮助条，写着快捷键和命令库条数。钉在面板底部不随输出滚动，长输出时占掉一点可视高度，不需要可以关掉。":
        "The help strip at the bottom of the console showing shortcuts and the library size. It is pinned to the bottom of the panel and does not scroll with output; it costs a little height, so turn it off if you do not need it.",
    "在控制台底部显示帮助条": "Show the help strip at the bottom of the console",

    # ---- 建议卡片徽标（Suggestion.Badge）----
    "命令": "Command",
    "子命令": "Subcommand",
    "开关": "Switch",
    "参数": "Parameter",
    "值": "Value",
    "取值": "Choice",
    "语法": "Syntax",
    "提示": "Hint",

    # ---- 右侧提示区 + 建议徽标（最后补充）----
    "输入命令名": "Type a command name",
    "直接输入命令的第一个字母即可看到候选，Tab 补全、↑↓ 选择、Enter 执行。": "Start typing the first letters of a command to see suggestions. Tab completes, ↑↓ picks, Enter runs.",
    "当前是 ": "Currently ",
    "切换到 ": "Switch to ",
    "命令库中没有 ": "Not in the command library: ",
    "仍然可以直接执行它。参数提示仅对命令库中已有的命令生效。": "You can still run it directly. Parameter hints only work for commands that are in the library.",
    "你是不是想输入 ": "Did you mean ",
    "请输入 ": "Enter ",
    "没有更多可用参数": "No more parameters available",
    "可以直接执行，或按 Enter 运行。": "You can run it as-is, or press Enter.",
    "Tab 补全当前项，↑↓ 选择，Enter 直接执行。": "Tab completes the current item, ↑↓ picks, Enter runs it directly.",
    "管道常用 · ": "Common after a pipe · ",
    "管道后面可以接这些命令": "These commands can follow a pipe",
    "也可以用 ↑↓ 继续找其它命令；Tab 补全，Enter 执行整行。": "Use ↑↓ to look for other commands; Tab completes, Enter runs the whole line.",
    "(切换到 ": "(Switch to ",
    "没有完全匹配的命令": "No exact match",
    "按 Enter 仍会按原样执行你输入的内容。": "Pressing Enter still runs exactly what you typed.",
    "目录": "Folder",
    "文件": "File",
    "文件 · ": "File · ",

    # ---- 设置窗口 + 多进程设置说明（最后一批）----
    "主界面自由控件宽度": "Free panel width on the main window",
    "关闭时，三个板块的宽度按下面的滑块固定；打开后可以直接用鼠标拖动板块之间的分隔条来改宽度，拉出来的宽度会记住。": "When off, the three panels use the fixed widths from the sliders below. When on, you can drag the dividers between panels, and the widths you drag out are remembered.",
    "关闭（默认）时宽度由下面的滑块决定；打开后可直接拖动板块之间的分隔条，宽度会保存下来。": "Off (default) uses the sliders below; on lets you drag the dividers and keeps the widths.",
    "命令库栏宽度": "Command library width",
    "参数面板宽度": "Parameter panel width",
    "并发跑多条命令，每条有自己的输出区，可以单独停止。": "Run several commands at once, each with its own output area that you can stop separately.",
    "打开输入区旁边的「多进程」开关后，会打开一个独立的多进程窗口：": "The Multi-process button next to the input opens a separate multi-process window:",
    "每条各起一个独立进程，输出互不干扰，退出码也分别显示，随时可以只停其中一条。": "Each task gets its own process, output never mixes, exit codes are shown per task, and you can stop any one of them at any time.",
    "多进程窗口高度": "Multi-process window height",
    "多进程窗口默认是关着的，点输入区旁边的「多进程」开关即可打开；": "The multi-process window starts closed; open it with the Multi-process button next to the input.",
    "关掉的是窗口本身，正在跑的任务不会停。": "This closes the window only; running tasks keep going.",
    "每条任务的命令和输出只在本次运行期间保留，不会写进设置文件。": "A task's command and output live only for this run and are never written to the settings file.",

    # ---- 定时/循环 + 多进程窗口对齐（本轮新增）----
    "多进程 · 正在运行": "Multi-process · running",
    "定时": "Schedule",
    "定时执行：打开后在输入框里输入命令，按「运行」不再立即执行，而是排到上面那个时间点。": "Scheduled run: with this on, pressing Run does not execute immediately; the command is queued for the time above.",
    "定时时间：HH:mm 或 HH:mm:ss（今天已经过了就顺延到明天）；也可以写相对时间，如 +30s、+5m。": "Time: HH:mm or HH:mm:ss (rolls over to tomorrow if already past); a relative value like +30s or +5m also works.",
    "循环": "Loop",
    "循环执行：打开后按「运行」，这条命令会立刻执行一次，之后按上面的间隔反复执行。": "Loop: with this on, Run executes once immediately and then repeats at the interval above.",
    "循环间隔：如 5s、1m、2h（不带单位按秒算，最短 1 秒）。": "Interval: e.g. 5s, 1m, 2h (a bare number means seconds; minimum 1 second).",
    "停止所有定时 / 循环（如果当前这一轮是调度器发起的，也会一起结束）。": "Stop all schedule/loop jobs (if the current round was started by the scheduler, it is ended too).",
    "定时 · 开": "Schedule · on",
    "循环 · 开": "Loop · on",
    "已开启定时 + 循环：下次「运行」先按时间执行一次，之后按间隔循环": "Schedule + loop are on: the next Run fires once at the time, then repeats at the interval",
    "已开启定时：下次「运行」按上面的时间执行一次": "Schedule is on: the next Run fires once at the time above",
    "已开启循环：下次「运行」按上面的间隔反复执行": "Loop is on: the next Run repeats at the interval above",
    "每 ": "Every ",
    " 执行中 · 第 ": " running · round ",
    " 次": "",
    "已排定 ": "Scheduled for ",
    " 执行": "",
    "（共 ": "(",
    " 个排定）": " scheduled)",
    "[定时] 时间填得不对：": "[Schedule] Invalid time: ",
    "　—— 请填 HH:mm / HH:mm:ss，或 +30s、+5m 这样的相对时间。": "  — use HH:mm / HH:mm:ss, or a relative value like +30s or +5m.",
    "[定时] 间隔填得不对：": "[Schedule] Invalid interval: ",
    "　—— 请填 5s、1m、2h 这样的间隔（最短 1 秒）。": "  — use an interval like 5s, 1m or 2h (minimum 1 second).",
    "[定时] 这条命令已经在排定里，已按新设置更新（不会重复叠加）。": "[Schedule] That command is already scheduled; it was updated in place (no duplicates).",
    "[定时] 已开始循环执行（每 ": "[Schedule] Loop started (every ",
    "[定时] 已排定 ": "[Schedule] Scheduled for ",
    " 执行：": ": ",
    "[定时] 上一轮还在运行，跳过这次循环。": "[Schedule] The previous round is still running; skipping this one.",
    "[定时] 当前有命令在运行，等它结束后再执行排定的命令。": "[Schedule] A command is running; the scheduled one will run once it finishes.",
    "[定时] 第 ": "[Schedule] Round ",
    " 轮：": ": ",
    "已停止定时 / 循环，并结束了当前这一轮命令。": "Schedule/loop stopped, and the current round was ended.",
    "已停止定时 / 循环。": "Schedule/loop stopped.",
    " 条任务（": " task(s) (",
    "拖动可以左右调整标签顺序；标签太多时标签条可以横向滚动；右键可以弹出为独立窗口。": "Drag to reorder tabs; the tab strip scrolls when there are many; right-click to detach one into its own window.",
    "拖动可以改命令库栏的宽度（在设置里打开「自由控件宽度」后可用）": "Drag to resize the command library (enable Free panel width in Settings first)",
    "拖动可以改参数提示栏的宽度（在设置里打开「自由控件宽度」后可用）": "Drag to resize the parameter panel (enable Free panel width in Settings first)",
    "窗口里默认用哪个控制台执行（打开时跟随主窗口模式；每一行还能单独指定）": "Which console the window uses (follows the main window on open; each row can override it)",
    "窗口里的任务默认用 CMD 执行": "Tasks in this window run in CMD",
    "窗口里的任务默认用 PowerShell 执行": "Tasks in this window run in PowerShell",
    "在任务行里输入命令并运行，输出会显示在这里（每条任务一个标签页）。": "Type a command in a task row and run it; output shows here (one tab per task).",
    "在某一行的输入框里敲命令，这里会显示它的用途和当前语法位置上可用的参数。": "Type a command in any row and its purpose plus the parameters valid at the current syntax position appear here.",
    "当前位置有 ": "There are ",
    " 个候选，点一条就能填进命令行。": " candidates here — click one to insert it.",
    "当前位置没有更多候选。": "No more candidates at this position.",
    "点一下把 ": "Click to insert ",
    " 填进命令行": " into the command line",
    "点这里把这条任务的输出调到前面（标签页或它自己的输出窗口）": "Click to bring this task's output to the front (its tab or its own window)",
    "这一行用哪个控制台执行": "Which console this row uses",
    "跟随窗口模式（": "Follow window mode (",
    "这一行用 CMD 执行": "This row runs in CMD",
    "这一行用 PowerShell 执行": "This row runs in PowerShell",
    "也可以在输入框里用 cmd> / ps> 前缀临时指定单独一条命令": "You can also prefix a single command with cmd> / ps> in the input box",
    "输出在它自己的窗口里": "Output is in its own window",
    "输出在下面的标签页": "Output is in the tabs below",
    "这一行单独用 ": "This row uses ",
    " 执行（点这里可以改回跟随窗口模式）": " (click to go back to following the window mode)",
    "跟随窗口模式：当前 ": "Follows window mode: currently ",
    "（点这里可以单独指定这一行的控制台）": " (click to pick a console just for this row)",
    "把这个输出合并回总窗口的标签页（也可以右键标签选「合并回标签」）": "Merge this output back into the combined window's tabs (or right-click the tab and choose Merge back into a tab)",

    # ---- 多进程（本轮新增）----
    "任务 ": "Task ",
    "[多命令]": "[Multi-command]",
    "第 ": "Row ",
    " 条任务：": " task: ",
    "拖动可以左右调整标签顺序；往外拖出可以弹出成独立窗口；右键有更多操作。": "Drag to reorder tabs; drag a tab out to detach it into its own window; right-click for more.",
    "正在运行": "Running",
    "未运行": "Not running",
    "已结束 · 退出码 0": "Finished · exit code 0",
    "已结束 · 退出码 ": "Finished · exit code ",
    "完成 · 退出码 0": "Done · exit code 0",
    "[多进程] 任务结束：": "[Multi-process] Task finished: ",
    "[多进程] 这条任务正在运行，输出未清空。": "[Multi-process] This task is still running, so its output was not cleared.",
    "[多进程] 当前设置是「每个任务一个独立窗口」，不会合并回标签页。可以在设置里打开「合并命令行窗口」。": "[Multi-process] The setting is one window per task, so it will not merge back into a tab. You can turn on Merge command windows in Settings.",
    "弹出为独立窗口": "Detach into its own window",
    "合并回标签": "Merge back into a tab",
    "关闭这个输出窗口": "Close this output window",
    "停止这条任务": "Stop this task",
    "运行这条任务": "Run this task",
    "多进程：还没有任务行。点这里打开多进程窗口，可以同时跑多条命令。": "Multi-process: no task rows yet. Click to open the multi-process window and run several commands at once.",
    "多进程：当前没有任务在运行（共 ": "Multi-process: nothing running (out of ",
    " 条任务行）。点这里查看各任务状态。": " task rows). Click to see each task's status.",
    "多进程：正在运行 ": "Multi-process: running ",
    " 条任务（共 ": " task(s) (out of ",
    " 条）。点这里查看各任务状态。": "). Click to see each task's status.",
    "多进程任务（按创建顺序）": "Multi-process tasks (in creation order)",
    "（未提交命令）": "(no command submitted)",
    "打开多进程窗口": "Open the multi-process window",
    "全部运行": "Run all",
    "全部停止": "Stop all",
    "多进程执行": "Multi-process",
    "每条任务各起一个进程，输出分开显示，可单独停止": "Each task gets its own process, keeps its own output, and can be stopped on its own",
    "增加一条任务行": "Add a task row",
    "减少最后一条任务行（正在运行的那条不会被删掉）": "Remove the last task row (a running one is never removed)",
    "把所有填写了命令的任务一起跑起来": "Run every task that has a command filled in",
    "停止全部正在运行的任务（连同各自的子进程）": "Stop all running tasks, including their child processes",
    "清空所有任务的输出区（正在运行的任务不会停止）": "Clear every task's output area (running tasks are not stopped)",
    "合并窗口": "Merge windows",
    "关闭窗口（正在运行的任务会继续跑）": "Close the window (running tasks keep going)",
    "在某一行的输入框里敲命令时，右侧会同步提示它的参数": "Type a command in any row and its parameters appear on the right",
    "Enter 运行  ·  Tab 补全  ·  ↑↓ 选候选": "Enter run  ·  Tab complete  ·  ↑↓ pick a candidate",
    "拖动可以调整任务行和输出区的高度分配": "Drag to change how height is split between the task rows and the output area",
    "把当前标签弹出为独立窗口": "Detach this tab into its own window",
    "把所有输出合并回标签": "Merge all output back into tabs",
    "关闭所有独立输出窗口": "Close all detached output windows",
    "搜索命令…": "Search commands…",
    "在某一行的输入框里敲命令，这里会显示它的用途和当前可用的参数。": "Type a command in any row and its purpose and available parameters show up here.",
    "语法：": "Syntax: ",
    "输入这条任务要执行的命令；单行模式按 Enter 直接运行，多命令模式按 Ctrl+Enter": "Type the command for this task; press Enter in single-line mode, or Ctrl+Enter in multi-command mode",
    "这一行切成多命令形式：单行输入框变成多行文本区，一整段脚本一次执行": "Turn this row into multi-command form: the single-line box becomes a multi-line text area and the whole script runs at once",
    "多命令形式：可以粘贴一整段脚本，Ctrl+Enter 一次执行": "Multi-command form: paste a whole script and press Ctrl+Enter to run it at once",
    "[多进程] 这条任务已经在运行了。": "[Multi-process] This task is already running.",
    "[多进程] 请先输入要执行的命令。": "[Multi-process] Enter a command first.",
    "[多进程] 这条是工具箱自己的内建命令，请在主输入框里执行。": "[Multi-process] That is the toolbox's own built-in command; run it in the main input box.",
    "[多进程] 这条命令属于高危操作，为安全起见请回到主输入框执行（那里有二次确认）。": "[Multi-process] That command is high-risk; for safety, run it in the main input box where there is a second confirmation.",
    "[多进程] 已发送停止信号，正在结束这条任务的进程树…": "[Multi-process] Stop signal sent; ending this task's process tree…",
    "已经到任务行数上限（在设置里可以调大「多进程任务行数上限」）。": "Task row limit reached (you can raise Max task rows in Settings).",
    "多进程": "Multi-process",
    "最后一条任务正在运行，先停止它再减少任务行。": "The last task row is running; stop it before removing the row.",
    "当前设置是「每个任务一个独立窗口」：每条任务的输出显示在它自己的窗口里。\\n点某一行标签（或任务行右边的状态文字）可以把那个窗口调到前面。": "The setting is one window per task: each task's output shows in its own window.\\nClick a row's status text to bring that window to the front.",
    "输出显示在独立窗口里": "Output in its own window",
    "输出显示在下面的标签页里": "Output in the tabs below",
    "点这里打开这条任务的输出窗口": "Click to open this task's output window",
    "合并模式：标签已吸回。想单独看这条任务的输出，可以右键标签选「弹出为独立窗口」，或在设置里关掉「合并命令行窗口」。": "Merging is on, so the tab snapped back. To see this task's output separately, right-click the tab and choose Detach into its own window, or turn off Merge command windows in Settings.",
    "停止": "Stop",
    "停止这条任务（连同它拉起的子进程一起结束），不影响其它任务": "Stop this task (together with any child processes it started) without affecting the others",
    "合并窗口 · 开": "Merge windows · on",
    "合并窗口 · 关": "Merge windows · off",
    "当前所有任务的输出集中在标签页里。点这里改成每条任务一个独立窗口（也可以用标签右键菜单临时弹出）。": "All tasks currently share one tabbed window. Click to switch to one window per task (you can also detach a tab from its right-click menu).",
    "当前每条任务的输出都在独立窗口里。点这里改成集中在标签页里显示。": "Each task currently has its own window. Click to show them all in tabs instead.",
    " 行 / 上限 ": " rows / limit ",
    "命令输入": "Command",
    " 行命令": " row command",
    "多进程 · 开": "Multi-process · on",
    "多进程窗口已打开（关闭窗口时正在运行的任务会继续跑）。点这里隐藏窗口。": "The multi-process window is open (closing it leaves running tasks going). Click to hide it.",
    "打开多进程窗口：可以同时跑多条命令，每条有自己的输出，可以单独停止。": "Open the multi-process window: run several commands at once, each with its own output that you can stop separately.",
    "[多进程] 输出过长，已截断前半部分。": "[Multi-process] Output was too long; the first half was truncated.",
    "这条任务的输出已经在独立窗口里显示": "This task's output already shows in its own window",
    "把这个输出合并回总窗口的标签页": "Merge this output back into the combined window's tabs",
    "当前设置是「每个任务一个独立窗口」，合并回总窗口后不显示标签页": "The setting is one window per task, so merging back will not show tabs",
    "关闭这个输出窗口（任务本身不会被停止）": "Close this output window (the task itself keeps running)",
    "[多进程] 输出窗口已关闭，任务仍在继续运行。重新打开标签即可继续查看。": "[Multi-process] Output window closed; the task is still running. Reopen its tab to keep watching.",

    # ---- 多进程 ----
    "任务行数上限": "Max task rows",
    "不限制": "Unlimited",
    "合并命令行窗口": "Merge command windows",
    "启用时，多进程各任务的输出集中在一个总窗口，用顶部分页标签切换；标签可以左右拖动排序，往外拖会吸回来。关闭后每个任务一个独立窗口，手动拖动也不会合并。":
        "When on, all task output lives in one window with tabs across the top; tabs can be dragged to reorder, and dragging one out snaps it back. When off, each task gets its own window and dragging never merges them.",
    "总窗口的标签可以左右拖动排序；合并启用时标签拖出去会吸回来，关闭后才能真正分离成独立窗口。":
        "Tabs in the combined window can be dragged to reorder. While merging is on, a tab dragged out snaps back; turn merging off to detach it for real.",

    # ---- 提示行为 / 严格语法顺序 ----
    "提示行为": "Suggestions",
    "控制参数提示列出多少内容。命令库 JSON 里 nodes 的书写顺序就是提示顺序，引擎不二次排序。":
        "Controls how much the parameter hints list. The order of nodes in the command library JSON is the hint order; the engine never re-sorts.",
    "按语法顺序限制提示参数": "Restrict suggestions to valid syntax order",
    "打开后只列出当前语法位置合法的参数：必填的位置参数还没填时，只提示它；已经用过且不能重复的参数不再列出。可以避免选出拼起来跑不通的命令。关闭时所有参数都会列出，用过的标灰显示「已使用」。":
        "When on, only parameters that are valid at the current syntax position are listed: while a required positional parameter is still empty, only that one is shown, and parameters already used (and not repeatable) are dropped. This avoids picking combinations that will not run. When off, every parameter is listed and used ones are greyed out as \"used\".",
    "已开启：只提示语法上合法的参数，不列出已经用过的参数。":
        "On: only syntactically valid parameters are shown; already-used ones are dropped.",
    "已关闭（默认）：列出全部参数，用过的标灰显示「已使用」。":
        "Off (default): all parameters are listed and used ones are greyed out.",
    "对比度 ": "Contrast ",
    "偏暗（建议配浅色文字）": "dark (use light text)",
    "偏亮（建议配深色文字）": "light (use dark text)",

    # ---- 分模式主题 / 启动工具 ----
    "为每个模式使用独立主题": "Use a separate theme per mode",
    "勾选后，CMD 和 PowerShell 各用一套主题：切到哪个模式就应用哪套。取消勾选则两种模式都用 PowerShell 那套；CMD 那套会保留下来，重新勾选时按原样恢复。":
        "When checked, CMD and PowerShell each keep their own theme and switching modes applies the matching one. When unchecked both modes use the PowerShell theme; the CMD theme is kept and restored if you re-enable this.",
    "这两项只决定「启动时」用哪个工具。标题栏上的 CMD / PowerShell 开关和运行按钮旁的下拉只影响本次运行，不会改动这里的设置。":
        "These two options only decide which tool starts with the app. The CMD / PowerShell switch in the title bar and the dropdown next to Run affect this session only and never change these settings.",
    "默认以 CMD 启动（不勾选则用 PowerShell）": "Start in CMD by default (PowerShell when unchecked)",
    "本次运行始终用 CMD": "Use CMD for this session",
    "本次运行始终用 PowerShell": "Use PowerShell for this session",
    "在设置里修改「启动时的默认工具」…": "Change the startup tool in Settings…",

    # ---- 便携 / 安装模式 ----
    "便携模式：配置、主题、历史都保存在程序同级的 WCTdata 目录，拷走文件夹即可带走全部状态。":
        "Portable mode: settings, theme and history live in the data folder next to the program — copy the folder and everything comes with it.",
    "安装模式：配置、主题、历史保存在 %APPDATA%\\WindowsCommandTools。":
        "Installed mode: settings, theme and history live in %APPDATA%\\WindowsCommandTools.",
    "数据位置": "Data location",

    # ---- 提权 / 崩溃提示 ----
    "已取消提权请求，本次以普通权限启动。":
        "Elevation was cancelled; starting with standard privileges.",
    "提权启动失败（": "Elevation failed (",
    "），本次以普通权限启动。": "); starting with standard privileges.",
    "(未知异常)": "(unknown exception)",
    "发生未知错误。": "An unknown error occurred.",
    "WindowsCommandTools 出错": "WindowsCommandTools error",

    # ---- 深层诊断（用户在控制台里能看到）----
    "已有命令正在运行": "A command is already running",
    "未找到内嵌样式表 ui.styles.xaml": "Embedded stylesheet ui.styles.xaml not found",
    "样式表解析结果不是 ResourceDictionary": "Stylesheet did not parse into a ResourceDictionary",
    "当前：简体中文": "Current: Simplified Chinese",
    "当前是普通权限": "Standard privileges",
    "\n个人配置与历史：": "\nPersonal config and history: ",
    "这条命令通常需要管理员权限，以当前普通权限运行可能会失败。\n可以先用管理员身份重启工具箱，再执行这条命令。":
        "This command usually needs administrator privileges and may fail with standard privileges.\n"
        "Restart the toolbox as administrator first, then run the command.",
}


def load_en():
    d = {}
    if not os.path.isdir(I18N):
        return d
    for name in sorted(os.listdir(I18N)):
        if not name.startswith("A") and not name.startswith("B") and not name.startswith("C"):
            continue
        if not name.endswith(".json") or name == "B-dialogs.json":
            continue
        path = os.path.join(I18N, name)
        if name.endswith("-dialogs.json") or name.startswith("A-") or name.startswith("C-"):
            continue
        try:
            obj = json.load(io.open(path, encoding="utf-8"))
        except Exception as e:
            print("  跳过 %s：%s" % (name, e))
            continue
        n = 0
        for k, v in obj.items():
            if v and v.strip():
                d[k] = v
                n += 1
        print("  读入 %-14s %3d 条" % (name, n))
    return d


def csharp(s):
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "\\r")


print("=== 读入译文 ===")
en = load_en()
print("  分片合计 %d 条" % len(en))

overridden = 0
for k, v in EXTRA.items():
    if k in en and en[k] != v:
        overridden += 1
    en[k] = v
print("  手工补充/覆盖 %d 条（其中覆盖译文 %d 条）" % (len(EXTRA), overridden))

# ---------------------------------------------------------------- 写 C#
lines = []
for k in sorted(en, key=lambda x: (-len(x), x)):
    lines.append('            d["%s"] = "%s";' % (csharp(k), csharp(en[k])))

body = "\n".join(lines)

TEMPLATE = '''// ---------------------------------------------------------------------------
//  LangTable.cs — 界面英文翻译表【由 tools/build_lang.py 自动生成，请勿手改】
//
//  键 = 界面代码里的中文原文；值 = English。
//  查不到就回退中文原文，所以漏翻只会显示中文，不会显示空白。
//
//  要改译文：编辑 tools/i18n/*.json（或 build_lang.py 里的 EXTRA），
//  然后重新运行 python tools/build_lang.py。
//
//  共 %d 条。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace WindowsCommandTools
{
    internal static class LangTable
    {
        /// <summary>中文 → English。</summary>
        public static readonly Dictionary<string, string> En = Build();

        private static Dictionary<string, string> Build()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
%s
            return d;
        }
    }
}
''' % (len(en), body)

out = os.path.join(SRC, "LangTable.cs")
io.open(out, "w", encoding="utf-8", newline="").write(TEMPLATE)
print("\n已生成 %s（%d 条）" % (out, len(en)))

# ---------------------------------------------------------------- 覆盖率
STR = re.compile(r'"((?:[^"\\]|\\.)*)"')
CJK = re.compile(r'[\u4e00-\u9fff]')
FILES = ["MainWindow.cs", "MainPanels.cs", "Dialogs.cs", "ShellUi.cs", "Controls.cs",
         "Exec.cs", "Program.cs", "MultiCmd.cs", "Lang.cs"]

missing = {}
total = 0
for f in FILES:
    p = os.path.join(SRC, f)
    if not os.path.exists(p):
        continue
    text = io.open(p, encoding="utf-8").read()
    if f == "Program.cs":
        i = text.find("internal static class SelfTest")
        if i > 0:
            text = text[:i]
    for line in text.split("\n"):
        if line.strip().startswith("//"):
            continue
        for m in STR.finditer(line):
            s = (m.group(1).replace('\\"', '"')
                 .replace('\\n', '\n').replace('\\r', '\r').replace('\\t', '\t'))
            if not CJK.search(s):
                continue
            total += 1
            if s not in en:
                missing[s] = missing.get(s, 0) + 1

print("\n=== 覆盖率 ===")
print("  源码中文字面量 %d 处，其中未翻译 %d 种" % (total, len(missing)))
if missing:
    print("  未翻译的（会回退成中文显示）：")
    for s, n in sorted(missing.items(), key=lambda x: -x[1])[:40]:
        print("    x%-3d %s" % (n, s[:78]))
    with io.open(os.path.join(ROOT, "build", "untranslated.txt"), "w",
                 encoding="utf-8", newline="\n") as fh:
        for s, n in sorted(missing.items(), key=lambda x: -x[1]):
            fh.write("%d\t%s\n" % (n, s))
    print("  （完整清单 build/untranslated.txt）")
