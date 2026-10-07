# WindowsCommandTools

**This project was produced by DeepSeek Harness — thanks to the AI agent for the convenience it provides!**

> **Visual panels instead of memorizing command syntax.**
> CMD and PowerShell made simple: type a command and the next parameter is suggested, parameters can be filled in as a form, and risky operations ask once more.

**English** · [**简体中文**](README.md)

---

## What it is

A Windows desktop tool that removes three everyday annoyances:

1. **Can't remember parameters** — type `ping ` and it tells you what can come next, no docs needed
2. **Can't remember commands** — 445 common CMD / PowerShell commands built in, browsable by category, one click to insert
3. **Not sure which shell to use** — type `ping` in PowerShell and it says "this is a CMD command"; one click switches over

A single exe. **No runtime to install** (.NET Framework 4.8 ships with Windows).

---

## Features

| | |
|---|---|
| **Two engines** | One-click switch between CMD and PowerShell; when a command belongs to the other shell it says so and offers to switch |
| **Smart parameter hints** | Type a command plus a space → parameters, enum values and file paths are listed in syntax order; Tab completes |
| **Visual parameter form** | Selecting a command builds a form; fill it in and run — no syntax to assemble |
| **Multi-command** | Turn the input box into a multi-line editor and run a whole script at once |
| **Multi-process** | A separate window runs N tasks in their own processes, with separate output, per-task stop, and tabs |
| **Schedule / loop** | Run once at a given time, or repeat at an interval |
| **Themes** | 8 presets plus custom colors, frosted glass, adjustable corner radius; CMD and PowerShell can each keep their own theme |
| **Bilingual UI** | Switch the interface language; the command library itself stays Chinese |
| **Portable mode** | Drop a `WCTdata` folder next to the exe; change nothing and it writes nothing |

---

## Getting started

### Just run it

1. Download `WindowsCommandTools.exe`
2. Double-click — **no installation**

### Portable mode

To keep settings next to the exe, create a folder named `WCTdata` **in the same directory**:

```
WindowsCommandTools.exe
WCTdata\            ← this folder means portable mode
```

Without it, settings go to `%APPDATA%\WindowsCommandTools`.

> **Change nothing and it writes nothing.** The program only creates `WCTdata` and saves when a setting actually differs from its default; set everything back and the extra files are removed.

---

## The interface

Three columns:

```
┌───────────┬─────────────────────────────┬──────────────┐
│ Library   │  Command line                │  Hints       │
│ Categories│  ────────────────────────   │  Form        │
│ Search    │  Schedule …  Multi  Multi-proc│  Details     │
│ Commands  │  ────────────────────────   │              │
│           │  Console (scrollable)        │              │
│           │  Help strip pinned at bottom │              │
└───────────┴─────────────────────────────┴──────────────┘
```

- **Left**: `Common / Favorites / History` plus categories; the search box searches across all of them
- **Middle**: input box, runtime toolbar, output console
- **Right**: three tabs — hints, visual form, command details

### How hints work

```
ipconfig          → "ipconfig belongs to CMD" (when you are in PowerShell)
ping              → first card is "(Switch to CMD) ping"; click it to switch
ping 1.1.1.1 -    → only switches starting with -
ping -n           → asks for "count"
netsh wlan show   → suggests profiles / interfaces / …
```

The rule is simple: **hint order equals the order the parameters are written in the command library JSON**. The engine never re-sorts, so what you write is what you see.

### Multi-process

Click **Multi-process** on the right of the hint row to open a separate window:

- Left library / middle task rows / right hints, same sources as the main window
- `+` / `−` in the title bar add or remove task rows (configurable limit, 8 by default)
- Each row can run in CMD or PowerShell independently; clicking a "(Switch to CMD) xxx" card switches **that row only**, leaving the window mode alone
- Output tabs can be **dragged to reorder** or **detached into their own window** from the right-click menu

### Schedule / loop

The left side of the row below the input box:

```
🕐 [Schedule] [+30s] [Loop] [5s] [Stop]        [Multi-line] [Multi-process]
```

- **Schedule**: `HH:mm` / `HH:mm:ss`, or a relative value like `+30s`, `+5m`
- **Loop**: `5s` / `1m` / `2h`; if the previous round is still running the round is skipped rather than queued

---

## Keyboard

| Key | Action |
|---|---|
| `Tab` | Complete the current suggestion |
| `↑` `↓` | Pick a suggestion |
| `Enter` | Run |
| `Ctrl+Enter` | Run the whole script in multi-command mode |
| `Ctrl+L` | Clear the console |
| `Ctrl+K` | Focus the search box |
| `F1` | Help |

A prefix switches the engine for a single line:

```
cmd> ipconfig        run this one in CMD
ps> Get-Process      run this one in PowerShell
```

---

## Settings

| Group | Options |
|---|---|
| Language | System / 简体中文 / English |
| Default tool | "Start in CMD by default" (unchecked = PowerShell); "Prefer pwsh 7" |
| Startup privilege | "Start as administrator (UAC)", off by default |
| Console hint | Show or hide the bottom help strip, on by default |
| Multi-command | Whether the mode persists, multi-line height |
| Multi-process | Task row limit (8 by default, unlimited allowed), window height |
| Hint behaviour | "Restrict suggestions to valid syntax order", off by default |
| Output encoding | Auto-detect / UTF-8 / OEM code page |
| Console font size | 10 – 20 |
| Safety | Second confirmation for high-risk and modifying commands |
| Data location | Whether you are portable or installed, and where |

> **Note**: the `CMD / PowerShell` switch in the title bar and the dropdown next to Run affect **this session only**. They never change which tool starts with the app.

---

## Building from source

Only the .NET Framework compiler that ships with Windows is needed — **no Visual Studio, no network**.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The build script:

1. Validates `commands\cmd\*.json` and `commands\ps\*.json`
2. Compiles everything into one exe with `csc.exe` (`/langversion:5`)
3. Copies the command library into `dist\commands\`
4. Runs the built-in self-test (**158 checks** covering the hint engine, cross-shell detection, real process execution, encoding, persistence and more)
5. Verifies the exe's file properties

The result is `dist\WindowsCommandTools.exe`.

After changing translations or the icon:

```powershell
python tools\build_lang.py      # regenerate src\LangTable.cs
python tools\make_res.py        # regenerate build\app.res
```

---

## Project layout

```
cmdtools\
├─ build.ps1              one-shot build (validate → compile → self-test → verify)
├─ README.md              Chinese readme
├─ README.en.md           this file
├─ LICENSE                AGPL-3.0
│
├─ src\                   all C# sources (code-only WPF, no XAML build step)
│   ├─ Program.cs           entry point, CLI switches, built-in self-test
│   ├─ MainWindow.cs        window shell, draggable dividers, rounded corners
│   ├─ MainPanels.cs        the three columns and the console
│   ├─ MultiProc.cs         multi-process window
│   ├─ MultiProcView.cs     output views, hand-drawn tab strip, detached windows
│   ├─ Schedule.cs          schedule / loop scheduler
│   ├─ Suggest.cs           the hint engine (the core)
│   ├─ Exec.cs              process execution, settings persistence, elevation
│   ├─ Library.cs           command library loading, path handling
│   ├─ Controls.cs          UI helpers and custom controls
│   ├─ Theme.cs             themes and palettes
│   ├─ Dialogs.cs           settings / theme / colour picker dialogs
│   ├─ Lang.cs / LangTable.cs   UI translation
│   └─ Icons.cs / WindowFx.cs / Json.cs / MultiCmd.cs / ShellUi.cs
│
├─ commands\              command library (extend it yourself; read at startup)
│   ├─ cmd\                 7 files, 175 CMD commands
│   └─ ps\                  8 files, 270 PowerShell commands
│
├─ ui\
│   ├─ styles.xaml          WPF styles, parsed at runtime
│   └─ app.res              icon + version info (committed, so no SDK is needed)
│
└─ tools\                 build scripts
    ├─ build_lang.py        generates the translation table
    ├─ make_res.py          generates app.res
    ├─ verify_res.py        independent app.res verifier
    ├─ make_icon.py         generates the icon
    ├─ app.rc               equivalent hand-written .rc (cross-checked against rc.exe)
    └─ i18n\                translation sources
```

---

## FAQ

**Do I need to install .NET?**
No. Windows 10 / 11 include .NET Framework 4.8.

**Only 445 commands — can I add my own?**
Yes. Drop JSON files into `commands\cmd\` or `commands\ps\`; copy the format of the existing ones and restart the app.

**PowerShell says `ping` is a CMD command — why?**
Deliberate. Click "Switch and re-run" on the banner, or the "(Switch to CMD) ping" card.

**Will it block dangerous commands?**
By default it only asks for **confirmation**, it never silently blocks. You can turn the confirmation off.

**Why is the hint order not what I expected?**
The order is exactly the order parameters are written in the command library JSON; the engine never re-sorts. That makes it predictable — change the JSON to change the order.

**What does it leave on disk?**
Nothing by default. Settings are only saved once you change one, into `WCTdata` (portable) or `%APPDATA%\WindowsCommandTools` (installed).

---

## Known limitations

- The UAC elevation flow depends on the secure desktop and cannot be automated; only argument assembly and persistence are unit-tested
- In very narrow windows a few English titles are truncated with an ellipsis (controls take priority)
- Killing the process without a normal close means pending settings changes are not saved

---

## License

[AGPL-3.0](LICENSE)

Free to use, modify and distribute — but **if you offer it to others as a network service, you must publish your changes**.
