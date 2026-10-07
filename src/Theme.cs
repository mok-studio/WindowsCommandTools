// ---------------------------------------------------------------------------
//  Theme.cs — 主题系统
//
//  需求对应关系：
//    · 「多种颜色的个性化主题，两个主题也作为工具箱预设」 → Presets() 内置 8 套
//    · 「自定义颜色」        → Theme 的每个颜色都可以被主题设置窗口改写
//    · 「上传本地图像作为背景」 → BackgroundImage + ImageStretch + ImageAlignment
//    · 「板块可以使用毛玻璃特效」 → Frosted + BlurRadius（见 Controls.cs 的 GlassPanel）
//    · 「图像不透明度和板块的不透明度独立」 → ImageOpacity 与 PanelOpacity 完全分开
//  所有颜色通过 Application.Resources 暴露成动态资源，改一个值界面立刻刷新。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace WindowsCommandTools
{
    public static class ColorUtil
    {
        public static Color Parse(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                string s = hex.Trim();
                if (s.StartsWith("#")) s = s.Substring(1);
                if (s.Length == 3)
                {
                    s = new string(new char[] { s[0], s[0], s[1], s[1], s[2], s[2] });
                }
                if (s.Length == 6)
                {
                    byte r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return Color.FromRgb(r, g, b);
                }
                if (s.Length == 8)
                {
                    byte a = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte r = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte g = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte b = byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return fallback;
        }

        public static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2", CultureInfo.InvariantCulture)
                       + c.G.ToString("X2", CultureInfo.InvariantCulture)
                       + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static Color WithAlpha(Color c, double alpha)
        {
            int a = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, alpha)) * 255.0);
            return Color.FromArgb((byte)a, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromRgb(
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Lighten(Color c, double t) { return Mix(c, Colors.White, t); }
        public static Color Darken(Color c, double t) { return Mix(c, Colors.Black, t); }

        public static double Luminance(Color c)
        {
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }

        public static SolidColorBrush Brush(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Brush(string hex, Color fallback, double alpha)
        {
            return Brush(WithAlpha(Parse(hex, fallback), alpha));
        }
    }

    public sealed class Theme
    {
        public string Name = "浅色扁平";
        public bool IsLight = true;

        // 强调色
        public string Accent = "#3B6FF5";

        // 背景
        public string Background = "#F3F5FA";
        public string Background2 = "#E7EDF9";
        public bool Gradient;
        public string BackgroundImage = "";
        public double ImageOpacity = 0.35;     // 背景图不透明度（独立于面板）
        public string ImageStretch = "uniformToFill";  // uniformToFill | uniform | fill | none
        public string ImageAlignment = "center";       // center | top | bottom | left | right ...

        // 面板 / 毛玻璃
        public string Surface = "#FFFFFF";
        public double PanelOpacity = 0.86;     // 面板不透明度（独立于背景图）
        public bool Frosted = true;
        public double BlurRadius = 22;
        public double CornerRadius = 12;
        public double BorderOpacity = 0.70;

        // 文字
        public string Text = "#1B2333";
        public string TextDim = "#5A6579";
        public string TextFaint = "#93A0B5";
        public string Border = "#DCE3F0";

        // 语义色
        public string Danger = "#E5484D";
        public string Success = "#2E9E5B";
        public string Warning = "#D98A0B";

        // 控制台
        public string ConsoleBg = "#FBFCFE";
        public string ConsoleText = "#22303F";

        public Color CAccent { get { return ColorUtil.Parse(Accent, Colors.RoyalBlue); } }
        public Color CBackground { get { return ColorUtil.Parse(Background, Colors.WhiteSmoke); } }
        public Color CBackground2 { get { return ColorUtil.Parse(Background2, Colors.Gainsboro); } }
        public Color CSurface { get { return ColorUtil.Parse(Surface, Colors.White); } }
        public Color CText { get { return ColorUtil.Parse(Text, Colors.Black); } }
        public Color CTextDim { get { return ColorUtil.Parse(TextDim, Colors.Gray); } }
        public Color CTextFaint { get { return ColorUtil.Parse(TextFaint, Colors.LightGray); } }
        public Color CBorder { get { return ColorUtil.Parse(Border, Colors.Gainsboro); } }
        public Color CDanger { get { return ColorUtil.Parse(Danger, Colors.Red); } }
        public Color CSuccess { get { return ColorUtil.Parse(Success, Colors.Green); } }
        public Color CWarning { get { return ColorUtil.Parse(Warning, Colors.Orange); } }
        public Color CConsoleBg { get { return ColorUtil.Parse(ConsoleBg, Colors.Black); } }
        public Color CConsoleText { get { return ColorUtil.Parse(ConsoleText, Colors.White); } }

        public Theme Clone()
        {
            Theme t = new Theme();
            t.Name = Name; t.IsLight = IsLight;
            t.Accent = Accent;
            t.Background = Background; t.Background2 = Background2; t.Gradient = Gradient;
            t.BackgroundImage = BackgroundImage; t.ImageOpacity = ImageOpacity;
            t.ImageStretch = ImageStretch; t.ImageAlignment = ImageAlignment;
            t.Surface = Surface; t.PanelOpacity = PanelOpacity; t.Frosted = Frosted;
            t.BlurRadius = BlurRadius; t.CornerRadius = CornerRadius; t.BorderOpacity = BorderOpacity;
            t.Text = Text; t.TextDim = TextDim; t.TextFaint = TextFaint; t.Border = Border;
            t.Danger = Danger; t.Success = Success; t.Warning = Warning;
            t.ConsoleBg = ConsoleBg; t.ConsoleText = ConsoleText;
            return t;
        }

        public Dictionary<string, object> ToJson()
        {
            Dictionary<string, object> o = Json.NewObj();
            o["name"] = Name; o["isLight"] = IsLight;
            o["accent"] = Accent;
            o["background"] = Background; o["background2"] = Background2; o["gradient"] = Gradient;
            o["backgroundImage"] = BackgroundImage; o["imageOpacity"] = ImageOpacity;
            o["imageStretch"] = ImageStretch; o["imageAlignment"] = ImageAlignment;
            o["surface"] = Surface; o["panelOpacity"] = PanelOpacity; o["frosted"] = Frosted;
            o["blurRadius"] = BlurRadius; o["cornerRadius"] = CornerRadius; o["borderOpacity"] = BorderOpacity;
            o["text"] = Text; o["textDim"] = TextDim; o["textFaint"] = TextFaint; o["border"] = Border;
            o["danger"] = Danger; o["success"] = Success; o["warning"] = Warning;
            o["consoleBg"] = ConsoleBg; o["consoleText"] = ConsoleText;
            return o;
        }

        public static Theme FromJson(Dictionary<string, object> o)
        {
            Theme t = new Theme();
            if (o == null) return t;
            t.Name = Json.Str(o, "name", t.Name);
            t.IsLight = Json.Bool(o, "isLight", t.IsLight);
            t.Accent = Json.Str(o, "accent", t.Accent);
            t.Background = Json.Str(o, "background", t.Background);
            t.Background2 = Json.Str(o, "background2", t.Background2);
            t.Gradient = Json.Bool(o, "gradient", t.Gradient);
            t.BackgroundImage = Json.Str(o, "backgroundImage", t.BackgroundImage);
            t.ImageOpacity = Json.Dbl(o, "imageOpacity", t.ImageOpacity);
            t.ImageStretch = Json.Str(o, "imageStretch", t.ImageStretch);
            t.ImageAlignment = Json.Str(o, "imageAlignment", t.ImageAlignment);
            t.Surface = Json.Str(o, "surface", t.Surface);
            t.PanelOpacity = Json.Dbl(o, "panelOpacity", t.PanelOpacity);
            t.Frosted = Json.Bool(o, "frosted", t.Frosted);
            t.BlurRadius = Json.Dbl(o, "blurRadius", t.BlurRadius);
            t.CornerRadius = Json.Dbl(o, "cornerRadius", t.CornerRadius);
            t.BorderOpacity = Json.Dbl(o, "borderOpacity", t.BorderOpacity);
            t.Text = Json.Str(o, "text", t.Text);
            t.TextDim = Json.Str(o, "textDim", t.TextDim);
            t.TextFaint = Json.Str(o, "textFaint", t.TextFaint);
            t.Border = Json.Str(o, "border", t.Border);
            t.Danger = Json.Str(o, "danger", t.Danger);
            t.Success = Json.Str(o, "success", t.Success);
            t.Warning = Json.Str(o, "warning", t.Warning);
            t.ConsoleBg = Json.Str(o, "consoleBg", t.ConsoleBg);
            t.ConsoleText = Json.Str(o, "consoleText", t.ConsoleText);
            return t;
        }
    }

    public static class Themes
    {
        public const string AccentKey = "AccentBrush";
        private static Theme _current;

        /// <summary>当前生效主题属于哪个引擎（勾了"独立主题"时才有意义）。</summary>
        private static ShellKind _activeShell = ShellKind.PowerShell;

        /// <summary>CMD 模式那套主题的落盘路径。PowerShell 那套沿用原来的 theme.json。</summary>
        public static string CmdThemeFile
        {
            get { return Path.Combine(AppPaths.UserDataDirectory, "theme-cmd.json"); }
        }

        /// <summary>由 MainWindow 注入，用来读"是否分模式用主题"这个设置。</summary>
        public static AppSettings Settings = new AppSettings();

        public static event Action Changed;

        public static Theme Current
        {
            get
            {
                if (_current == null) _current = Presets()[0];
                return _current;
            }
        }

        /// <summary>
        /// 引擎切换时调用。勾了「为每个模式使用独立主题」就换成该引擎自己那套；
        /// 没勾就保持 PowerShell 那套不动。
        /// </summary>
        public static void OnShellChanged(ShellKind shell)
        {
            _activeShell = shell;
            try
            {
                if (!Settings.SeparateThemePerShell) return;
                if (shell == ShellKind.Cmd)
                {
                    Theme cmd = LoadFrom(CmdThemeFile);
                    if (cmd != null) Set(cmd, false);
                }
                else
                {
                    Theme ps = LoadFrom(AppPaths.ThemeFile);
                    if (ps != null) Set(ps, false);
                }
            }
            catch { }
        }

        /// <summary>写盘时按"当前生效的是哪个引擎"决定写哪个文件。</summary>
        private static string TargetFile()
        {
            if (Settings.SeparateThemePerShell && _activeShell == ShellKind.Cmd) return CmdThemeFile;
            return AppPaths.ThemeFile;
        }

        private static Theme LoadFrom(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                Dictionary<string, object> o = Json.AsObj(Json.ParseFile(file));
                if (o == null) return null;
                return Theme.FromJson(o);
            }
            catch { return null; }
        }

        public static List<Theme> Presets()
        {
            List<Theme> list = new List<Theme>();

            Theme t1 = new Theme();
            t1.Name = "浅色扁平"; t1.IsLight = true;
            t1.Accent = "#3B6FF5"; t1.Background = "#F3F5FA"; t1.Background2 = "#E4EBF9";
            t1.Surface = "#FFFFFF"; t1.Text = "#1B2333"; t1.TextDim = "#5A6579"; t1.TextFaint = "#93A0B5";
            t1.Border = "#DDE4F1"; t1.ConsoleBg = "#FBFCFE"; t1.ConsoleText = "#22303F";
            t1.Frosted = true; t1.PanelOpacity = 0.88;
            list.Add(t1);

            Theme t2 = new Theme();
            t2.Name = "深色青绿"; t2.IsLight = false;
            t2.Accent = "#14B8A6"; t2.Background = "#0D1417"; t2.Background2 = "#132026";
            t2.Surface = "#16242A"; t2.Text = "#E6F1F2"; t2.TextDim = "#95A9AE"; t2.TextFaint = "#5F7378";
            t2.Border = "#24363D"; t2.ConsoleBg = "#0A1013"; t2.ConsoleText = "#CFE6E6";
            t2.Danger = "#FF6B6B"; t2.Success = "#3FD68A"; t2.Warning = "#F0B429";
            t2.Frosted = true; t2.PanelOpacity = 0.80;
            list.Add(t2);

            Theme t3 = new Theme();
            t3.Name = "午夜紫"; t3.IsLight = false;
            t3.Accent = "#8B5CF6"; t3.Background = "#100E1A"; t3.Background2 = "#1A1530";
            t3.Surface = "#1B1830"; t3.Text = "#EDEAF7"; t3.TextDim = "#A79FC4"; t3.TextFaint = "#6E6690";
            t3.Border = "#2C2748"; t3.ConsoleBg = "#0C0A14"; t3.ConsoleText = "#DCD6F5";
            t3.Danger = "#FF6B81"; t3.Success = "#4ADE80"; t3.Warning = "#FBBF24";
            t3.Frosted = true; t3.PanelOpacity = 0.78;
            list.Add(t3);

            Theme t4 = new Theme();
            t4.Name = "深海蓝"; t4.IsLight = false;
            t4.Accent = "#3B82F6"; t4.Background = "#0A1220"; t4.Background2 = "#101D33";
            t4.Surface = "#13203A"; t4.Text = "#E3EDF9"; t4.TextDim = "#93A8C6"; t4.TextFaint = "#5D7291";
            t4.Border = "#1F3055"; t4.ConsoleBg = "#070D18"; t4.ConsoleText = "#CFE0F5";
            t4.Danger = "#F87171"; t4.Success = "#34D399"; t4.Warning = "#FBBF24";
            t4.Frosted = true; t4.PanelOpacity = 0.80;
            list.Add(t4);

            Theme t5 = new Theme();
            t5.Name = "暖阳橙"; t5.IsLight = true;
            t5.Accent = "#F97316"; t5.Background = "#FFF7F0"; t5.Background2 = "#FFE9D6";
            t5.Surface = "#FFFFFF"; t5.Text = "#2E2115"; t5.TextDim = "#7A6650"; t5.TextFaint = "#B39C82";
            t5.Border = "#F2DFC9"; t5.ConsoleBg = "#FFFBF7"; t5.ConsoleText = "#3A2A1B";
            t5.Danger = "#DC2626"; t5.Success = "#16A34A"; t5.Warning = "#D97706";
            t5.Frosted = true; t5.PanelOpacity = 0.88;
            list.Add(t5);

            Theme t6 = new Theme();
            t6.Name = "森林绿"; t6.IsLight = true;
            t6.Accent = "#2F8F4E"; t6.Background = "#F1F8F3"; t6.Background2 = "#DDEFE3";
            t6.Surface = "#FFFFFF"; t6.Text = "#16261A"; t6.TextDim = "#4E6B55"; t6.TextFaint = "#8AA793";
            t6.Border = "#D5E7DA"; t6.ConsoleBg = "#FAFDFB"; t6.ConsoleText = "#1D3324";
            t6.Frosted = true; t6.PanelOpacity = 0.88;
            list.Add(t6);

            Theme t7 = new Theme();
            t7.Name = "极简灰白"; t7.IsLight = true;
            t7.Accent = "#4B5563"; t7.Background = "#F6F6F7"; t7.Background2 = "#E9E9EC";
            t7.Surface = "#FFFFFF"; t7.Text = "#1F2328"; t7.TextDim = "#5C636E"; t7.TextFaint = "#9AA1AC";
            t7.Border = "#E1E3E8"; t7.ConsoleBg = "#FCFCFD"; t7.ConsoleText = "#25292F";
            t7.Frosted = false; t7.PanelOpacity = 0.94; t7.Gradient = false;
            list.Add(t7);

            Theme t8 = new Theme();
            t8.Name = "高对比暗黑"; t8.IsLight = false;
            t8.Accent = "#FFD400"; t8.Background = "#000000"; t8.Background2 = "#0C0C0C";
            t8.Surface = "#121212"; t8.Text = "#FFFFFF"; t8.TextDim = "#C9C9C9"; t8.TextFaint = "#8A8A8A";
            t8.Border = "#2E2E2E"; t8.ConsoleBg = "#000000"; t8.ConsoleText = "#F2F2F2";
            t8.Danger = "#FF4D4D"; t8.Success = "#00E676"; t8.Warning = "#FFC400";
            t8.Frosted = false; t8.PanelOpacity = 1.0;
            list.Add(t8);

            return list;
        }

        public static Theme PresetByName(string name)
        {
            foreach (Theme t in Presets())
            {
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t.Clone();
            }
            return Presets()[0].Clone();
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(AppPaths.ThemeFile))
                {
                    Dictionary<string, object> o = Json.AsObj(Json.ParseFile(AppPaths.ThemeFile));
                    if (o != null)
                    {
                        _current = Theme.FromJson(o);
                        // 分模式用主题时，如果启动引擎是 CMD，就换成 CMD 那套
                        if (Settings.SeparateThemePerShell)
                        {
                            Theme cmd = LoadFrom(CmdThemeFile);
                            if (cmd != null) _current = cmd;
                        }
                        return;
                    }
                }
            }
            catch { }
            _current = Presets()[0].Clone();
        }

        /// <summary>
        /// 落盘到"当前生效引擎"对应的文件。
        /// 和设置一样：**只在主题偏离默认预设时才写**，全是默认就把旧文件删掉，
        /// 保证没动过外观的用户磁盘上不会多出文件。
        /// </summary>
        public static void Save()
        {
            try
            {
                string file = TargetFile();
                if (IsDefaultTheme(Current))
                {
                    if (File.Exists(file)) File.Delete(file);
                    return;
                }
                if (!AppPaths.EnsureUserDataDirectory()) return;
                Json.WriteToFile(file, Current.ToJson());
            }
            catch { }
        }

        /// <summary>是不是和第一套预设（浅色扁平）完全一样。</summary>
        public static bool IsDefaultTheme(Theme t)
        {
            try
            {
                return Json.Write(t.ToJson()) == Json.Write(Presets()[0].ToJson());
            }
            catch { return false; }
        }

        public static void Set(Theme t, bool save)
        {
            _current = t;
            Apply();
            if (save) Save();
        }

        public static void NotifyChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        /// <summary>把主题写进 Application.Resources，界面通过 DynamicResource 自动刷新。</summary>
        public static void Apply()
        {
            if (Application.Current == null) return;
            Theme t = Current;
            ResourceDictionary r = Application.Current.Resources;

            Color accent = t.CAccent;
            Color accentHover = t.IsLight ? ColorUtil.Darken(accent, 0.12) : ColorUtil.Lighten(accent, 0.14);
            Color accentPress = t.IsLight ? ColorUtil.Darken(accent, 0.24) : ColorUtil.Lighten(accent, 0.26);

            r["AccentBrush"] = ColorUtil.Brush(accent);
            r["AccentHoverBrush"] = ColorUtil.Brush(accentHover);
            r["AccentPressedBrush"] = ColorUtil.Brush(accentPress);
            r["AccentSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0.16));
            r["AccentGhostBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0.09));
            r["OnAccentBrush"] = ColorUtil.Brush(ColorUtil.Luminance(accent) > 0.62 ? ColorUtil.Darken(accent, 0.85) : Colors.White);

            r["WindowBgBrush"] = ColorUtil.Brush(t.CBackground);
            r["WindowBg2Brush"] = ColorUtil.Brush(t.CBackground2);

            r["SurfaceBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CSurface, t.PanelOpacity));
            r["SurfaceSolidBrush"] = ColorUtil.Brush(t.CSurface);
            r["SurfaceHoverBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(
                t.IsLight ? ColorUtil.Darken(t.CSurface, 0.04) : ColorUtil.Lighten(t.CSurface, 0.06), t.PanelOpacity));
            r["SurfaceSunkenBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(
                t.IsLight ? ColorUtil.Darken(t.CSurface, 0.02) : ColorUtil.Darken(t.CSurface, 0.25),
                Math.Min(1.0, t.PanelOpacity + 0.10)));

            r["TextBrush"] = ColorUtil.Brush(t.CText);
            r["TextDimBrush"] = ColorUtil.Brush(t.CTextDim);
            r["TextFaintBrush"] = ColorUtil.Brush(t.CTextFaint);
            r["BorderBrushSoft"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CBorder, t.BorderOpacity));
            r["BorderBrushHard"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CBorder, Math.Min(1.0, t.BorderOpacity + 0.25)));
            r["HoverOverlayBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(
                t.IsLight ? Colors.Black : Colors.White, 0.055));
            r["PressedOverlayBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(
                t.IsLight ? Colors.Black : Colors.White, 0.11));

            r["DangerBrush"] = ColorUtil.Brush(t.CDanger);
            r["DangerSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CDanger, 0.14));
            r["SuccessBrush"] = ColorUtil.Brush(t.CSuccess);
            r["SuccessSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CSuccess, 0.14));
            r["WarningBrush"] = ColorUtil.Brush(t.CWarning);
            r["WarningSoftBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CWarning, 0.16));

            r["ConsoleBgBrush"] = ColorUtil.Brush(t.CConsoleBg);
            r["ConsoleTextBrush"] = ColorUtil.Brush(t.CConsoleText);
            r["ConsoleDimBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CConsoleText, 0.55));
            r["ConsoleSelBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(accent, 0.30));

            bool consoleDark = ColorUtil.Luminance(t.CConsoleBg) < 0.5;
            r["ConsoleErrBrush"] = ColorUtil.Brush(consoleDark ? Color.FromRgb(0xFF, 0x8A, 0x8A) : Color.FromRgb(0xC0, 0x2A, 0x2A));
            r["ConsoleSysBrush"] = ColorUtil.Brush(consoleDark ? Color.FromRgb(0x7A, 0xD0, 0xFF) : Color.FromRgb(0x1D, 0x6F, 0xB8));
            r["ConsoleOkBrush"] = ColorUtil.Brush(consoleDark ? Color.FromRgb(0x86, 0xE0, 0x9A) : Color.FromRgb(0x1E, 0x7A, 0x3C));
            r["ConsoleEchoBrush"] = ColorUtil.Brush(ColorUtil.WithAlpha(t.CConsoleText, 0.62));

            // ---- 圆角：统一的比例尺 ----
            // 以前只有毛玻璃面板跟着「圆角大小」变，按钮、徽标、输入框全是写死的数字，
            // 一调圆角整个界面就不协调。这里由同一个基准算出一整套，代码和样式表都引用它们。
            double baseR = t.CornerRadius;
            double controlR = Clamp(baseR * 0.62, 4, baseR);      // 按钮 / 输入框 / 列表项
            double smallR = Clamp(baseR * 0.42, 3, controlR);     // 徽标 / 色块 / 复选框
            double microR = Clamp(baseR * 0.20, 1, smallR);       // 进度条 / 小圆点
            r["PanelCornerRadius"] = new CornerRadius(baseR);
            r["ControlCornerRadius"] = new CornerRadius(controlR);
            r["SmallCornerRadius"] = new CornerRadius(smallR);
            r["MicroCornerRadius"] = new CornerRadius(microR);
            r["CornerRadiusValue"] = baseR;
            r["ControlRadiusValue"] = controlR;
            r["SmallRadiusValue"] = smallR;
            r["MicroRadiusValue"] = microR;
            r["BlurRadiusValue"] = t.BlurRadius;

            NotifyChanged();
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
