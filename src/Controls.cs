// ---------------------------------------------------------------------------
//  Controls.cs — 毛玻璃面板、矢量图标、扁平控件工厂、样式表加载
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WPath = System.Windows.Shapes.Path;

namespace WindowsCommandTools
{
    // -----------------------------------------------------------------------
    //  字体
    // -----------------------------------------------------------------------
    public static class Fonts
    {
        public static readonly FontFamily Ui = new FontFamily("Microsoft YaHei UI, Segoe UI, SimSun");
        public static readonly FontFamily Mono = PickMono();

        private static FontFamily PickMono()
        {
            string[] candidates = new string[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Sarasa Mono SC", "Courier New" };
            List<string> installed = new List<string>();
            try
            {
                foreach (FontFamily f in Fonts2.SystemFontFamilies) installed.Add(f.Source);
            }
            catch { }
            foreach (string c in candidates)
            {
                foreach (string i in installed)
                {
                    if (string.Equals(i, c, StringComparison.OrdinalIgnoreCase))
                        return new FontFamily(c + ", Consolas, Courier New");
                }
            }
            return new FontFamily("Consolas, Courier New");
        }
    }

    // 只是为了避免与上面 Fonts 类名冲突的小包装
    internal static class Fonts2
    {
        public static System.Collections.IEnumerable SystemFontFamilies
        {
            get { return System.Windows.Media.Fonts.SystemFontFamilies; }
        }
    }

    // -----------------------------------------------------------------------
    //  矢量图标（24x24 描边风格，全部用简单路径，保证渲染可靠）
    // -----------------------------------------------------------------------
    public static class Icons
    {
        private static readonly Dictionary<string, string[]> Map = BuildMap();

        private static Dictionary<string, string[]> BuildMap()
        {
            Dictionary<string, string[]> m = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            m["terminal"] = new string[] {
                "M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z",
                "M7 10l2.4 2.4L7 14.8", "M12.5 15H17" };
            m["folder"] = new string[] {
                "M3 7a2 2 0 0 1 2-2h3.6l2 2.5H19a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" };
            m["disk"] = new string[] {
                "M12 3.5c4.4 0 8 1.2 8 2.7v11.6c0 1.5-3.6 2.7-8 2.7s-8-1.2-8-2.7V6.2c0-1.5 3.6-2.7 8-2.7z",
                "M4 6.2c0 1.5 3.6 2.7 8 2.7s8-1.2 8-2.7",
                "M4 12c0 1.5 3.6 2.7 8 2.7s8-1.2 8-2.7" };
            m["network"] = new string[] {
                "M12 3.2a8.8 8.8 0 1 0 0 17.6 8.8 8.8 0 0 0 0-17.6z",
                "M3.2 12h17.6",
                "M12 3.2c2.4 2.6 3.7 5.5 3.7 8.8s-1.3 6.2-3.7 8.8c-2.4-2.6-3.7-5.5-3.7-8.8S9.6 5.8 12 3.2z" };
            m["system"] = new string[] {
                "M4 5h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z",
                "M9.5 20h5", "M12 17v3" };
            m["power"] = new string[] {
                "M12 3.5v8", "M7.4 6.6a7 7 0 1 0 9.2 0" };
            m["script"] = new string[] {
                "M6.5 3h7.5l4 4v13a1 1 0 0 1-1 1h-10.5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z",
                "M14 3v4h4", "M9 13h6", "M9 16.5h4" };
            m["apps"] = new string[] {
                "M4 4h6.5v6.5H4z", "M13.5 4H20v6.5h-6.5z", "M4 13.5h6.5V20H4z", "M13.5 13.5H20V20h-6.5z" };
            m["star"] = new string[] {
                "M12 3.6l2.6 5.4 5.9.8-4.3 4.1 1.1 5.9L12 17l-5.3 2.8 1.1-5.9-4.3-4.1 5.9-.8z" };
            m["history"] = new string[] {
                "M3.6 12a8.4 8.4 0 1 0 2.6-6.1", "M3.4 4.4v4.4h4.4", "M12 8v4.6l3.1 1.8" };
            m["search"] = new string[] {
                "M11 4.2a6.8 6.8 0 1 0 0 13.6 6.8 6.8 0 0 0 0-13.6z", "M16 16l4.6 4.6" };
            m["play"] = new string[] { "M7.5 4.8l11 7.2-11 7.2z" };
            m["stop"] = new string[] { "M6.5 6.5h11v11h-11z" };
            m["trash"] = new string[] {
                "M4 7h16", "M9.5 7V3.8h5V7", "M6.5 7l1 13.2h9L17.5 7", "M10.5 11v5.5", "M13.5 11v5.5" };
            m["copy"] = new string[] { "M9 9h10.5v10.5H9z", "M5 15.5V4.5h10.5" };
            m["save"] = new string[] {
                "M5 4h11l3 3v13H5z", "M9 4v6h6V4", "M8 20v-6h8v6" };
            m["settings"] = new string[] {
                "M4 7h5", "M13 7h7", "M4 12h9", "M17 12h3", "M4 17h5", "M13 17h7",
                "M11 5v4", "M15 10v4", "M11 15v4" };
            m["theme"] = new string[] {
                "M12 3.4c1.8 2.5 5.6 6.4 5.6 9.6a5.6 5.6 0 0 1-11.2 0c0-3.2 3.8-7.1 5.6-9.6z" };
            m["shield"] = new string[] {
                "M12 3.2l7 2.8v5.4c0 4.2-2.9 7.8-7 9-4.1-1.2-7-4.8-7-9V6z" };
            m["warning"] = new string[] {
                "M12 4.2l8.4 15.2H3.6z", "M12 9.6v4.4", "M12 16.6v0.6" };
            m["info"] = new string[] {
                "M12 3.4a8.6 8.6 0 1 0 0 17.2 8.6 8.6 0 0 0 0-17.2z", "M12 11v6", "M12 7.6v0.6" };
            m["bulb"] = new string[] {
                "M9.5 18.5h5", "M10.5 21h3",
                "M12 3.2a6 6 0 0 0-3.4 10.9v2.4h6.8v-2.4A6 6 0 0 0 12 3.2z" };
            m["book"] = new string[] {
                "M4 5.5A2.5 2.5 0 0 1 6.5 3H18v18H6.5A2.5 2.5 0 0 1 4 18.5z", "M18 3v18", "M7.5 8h6" };
            m["close"] = new string[] { "M6.5 6.5l11 11", "M17.5 6.5l-11 11" };
            m["min"] = new string[] { "M6.5 12h11" };
            m["max"] = new string[] { "M6.5 6.5h11v11h-11z" };
            m["restore"] = new string[] { "M8 8.5h10.5V19H8z", "M5.5 15.5V5h10" };
            m["chevronDown"] = new string[] { "M6.5 9.5l5.5 5.5 5.5-5.5" };
            m["chevronRight"] = new string[] { "M9.5 6l6 6-6 6" };
            m["chevronLeft"] = new string[] { "M14.5 6l-6 6 6 6" };
            m["plus"] = new string[] { "M12 5.5v13", "M5.5 12h13" };
            m["minus"] = new string[] { "M5.5 12h13" };
            m["check"] = new string[] { "M5 12.6l4.6 4.6L19 7.2" };
            m["refresh"] = new string[] {
                "M19.6 12a7.6 7.6 0 1 1-2.2-5.4", "M20 3.8v4.4h-4.4" };
            m["clock"] = new string[] {
                "M12 3.4a8.6 8.6 0 1 0 0 17.2 8.6 8.6 0 0 0 0-17.2z", "M12 7v5.4l3.4 2" };
            m["pin"] = new string[] {
                "M9 3.5h6l-1 6 3.5 3.5H5.5L9 9.5z", "M12 13v7.5" };
            m["image"] = new string[] {
                "M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z",
                "M4 16l4.5-4.5 4 4 3-3L20 17", "M8.8 9.4a1.4 1.4 0 1 0 0-.1z" };
            m["layers"] = new string[] {
                "M12 3.5l8 4.2-8 4.2-8-4.2z", "M4 12.2l8 4.2 8-4.2", "M4 16.4l8 4.2 8-4.2" };
            m["grid"] = new string[] {
                "M4 4h16v16H4z", "M4 10h16", "M4 15h16", "M10 4v16" };
            m["code"] = new string[] {
                "M9 7l-5 5 5 5", "M15 7l5 5-5 5" };
            m["list"] = new string[] {
                "M8 6.5h12", "M8 12h12", "M8 17.5h12", "M4.2 6.5h0.6", "M4.2 12h0.6", "M4.2 17.5h0.6" };
            m["send"] = new string[] { "M4 12l16-8-6 16-3-6z" };
            m["lightning"] = new string[] { "M13.5 3l-7 10.5h5L10.5 21l7-10.5h-5z" };
            m["file"] = new string[] {
                "M6.5 3h7.5l4 4v14H6.5z", "M14 3v4h4" };
            // 多进程：两层叠在一起的窗口（上层略小、往右下错开），底下一层有一条内容线
            m["taskStack"] = new string[] {
                "M3.5 6.5h11v10h-11z",
                "M8.5 3.5h12v9.5",
                "M6 12.5h6" };
            m["wrench"] = new string[] {
                "M15.5 3.5a5 5 0 0 0-4.6 7L4 17.4V20h2.6l6.9-6.9a5 5 0 0 0 6.1-6.5l-3 3-2.4-2.4 3-3a5 5 0 0 0-1.7-.7z" };
            return m;
        }

        public static string[] Get(string name)
        {
            string[] v;
            if (Map.TryGetValue(name, out v)) return v;
            return Map["terminal"];
        }

        public static UIElement Create(string name, double size, string brushKey, double thickness)
        {
            WPath p = new WPath();
            p.Data = BuildGeometry(Get(name));
            // thickness 按「最终渲染出来的像素粗细」解释，这样不同尺寸的图标观感一致
            double boxScale = size / IconBox;
            if (boxScale < 0.01) boxScale = 1.0;
            p.StrokeThickness = thickness / boxScale;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Fill = null;
            p.SnapsToDevicePixels = false;
            if (brushKey != null) p.SetResourceReference(Shape.StrokeProperty, brushKey);

            Canvas canvas = new Canvas();
            canvas.Width = IconBox;
            canvas.Height = IconBox;
            canvas.Children.Add(p);

            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Child = canvas;
            return vb;
        }

        /// <summary>图标统一坐标系：24×24 的盒子，内容缩放到 20×20 以内并居中。</summary>
        private const double IconBox = 24.0;
        private const double IconContent = 20.0;

        /// <summary>
        /// 把一组路径合并成一个几何，并把内容缩放到标准大小、居中到 24×24 盒子里。
        /// 这样「请求图标宽度 N」得到的视觉大小在各个图标之间是一致的 —— 否则
        /// 图形本身只占 24 单位中的 16 单位时，图标会明显偏小（之前就是这么偏小并被压扁的）。
        /// </summary>
        private static Geometry BuildGeometry(string[] parts)
        {
            GeometryGroup gg = new GeometryGroup();
            foreach (string d in parts)
            {
                try { gg.Children.Add(Geometry.Parse(d)); }
                catch { }
            }
            Rect b = gg.Bounds;
            if (b.IsEmpty || (b.Width < 0.01 && b.Height < 0.01)) return gg;

            double span = Math.Max(b.Width, b.Height);
            double scale = IconContent / Math.Max(span, 0.01);
            if (scale > 8.0) scale = 8.0;   // 极扁的图标（例如一条横线）不要放得过大

            TransformGroup tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(scale, scale));
            double cx = b.X + b.Width / 2.0;
            double cy = b.Y + b.Height / 2.0;
            tg.Children.Add(new TranslateTransform(IconBox / 2.0 - cx * scale, IconBox / 2.0 - cy * scale));
            gg.Transform = tg;
            return gg;
        }

        public static UIElement CreateFilled(string name, double size, string brushKey)
        {
            Canvas canvas = new Canvas();
            canvas.Width = 24;
            canvas.Height = 24;
            foreach (string d in Get(name))
            {
                WPath p = new WPath();
                try { p.Data = Geometry.Parse(d); }
                catch { continue; }
                p.Fill = null;
                if (brushKey != null) p.SetResourceReference(Shape.FillProperty, brushKey);
                canvas.Children.Add(p);
            }
            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Stretch = Stretch.Uniform;
            vb.Child = canvas;
            return vb;
        }
    }

    // -----------------------------------------------------------------------
    //  毛玻璃面板
    //
    //  原理：面板自己不去模糊「自己的内容」，而是把「背景层」(Backdrop) 用
    //  VisualBrush 按自己在背景层坐标系里的位置取样，贴在面板底部再套一层
    //  GaussianBlur，最后压一层半透明的主题色。这样得到的就是真正的
    //  「背景透过磨砂玻璃」效果，而且和背景图的不透明度完全独立。
    //
    //  重要：本控件绝不能放在 Backdrop 内部，否则会形成 VisualBrush 自引用死循环。
    // -----------------------------------------------------------------------
    public class GlassPanel : ContentControl
    {
        /// <summary>被取样的背景层，由主窗口在启动时赋值。</summary>
        public static FrameworkElement Backdrop;

        private Rectangle _blurRect;
        private Border _tint;

        public GlassPanel()
        {
            SnapsToDevicePixels = true;
            FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(UIElement.ClipToBoundsProperty, true);

            FrameworkElementFactory rect = new FrameworkElementFactory(typeof(Rectangle));
            rect.Name = "PART_Blur";
            grid.AppendChild(rect);

            FrameworkElementFactory tint = new FrameworkElementFactory(typeof(Border));
            tint.Name = "PART_Tint";
            grid.AppendChild(tint);

            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ContentPresenter));
            grid.AppendChild(host);

            ControlTemplate tpl = new ControlTemplate(typeof(GlassPanel));
            tpl.VisualTree = grid;
            Template = tpl;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _blurRect = GetTemplateChild("PART_Blur") as Rectangle;
            _tint = GetTemplateChild("PART_Tint") as Border;
            if (_tint != null)
            {
                _tint.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                _tint.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
                _tint.SetResourceReference(Border.CornerRadiusProperty, "PanelCornerRadius");
                _tint.BorderThickness = new Thickness(1);
            }
            if (_blurRect != null)
            {
                _blurRect.SetResourceReference(Rectangle.RadiusXProperty, "CornerRadiusValue");
                _blurRect.SetResourceReference(Rectangle.RadiusYProperty, "CornerRadiusValue");
                _blurRect.IsHitTestVisible = false;
            }
            Themes.Changed += OnThemeChanged;
            UpdateLayers();
        }

        private void OnThemeChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateLayers));
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Size s = base.ArrangeOverride(arrangeSize);
            UpdateLayers();
            return s;
        }

        /// <summary>强制刷新（例如背景图换了）。</summary>
        public void UpdateLayers()
        {
            if (_blurRect == null) return;
            Theme t = Themes.Current;

            double radius = t.CornerRadius;
            _blurRect.RadiusX = radius;
            _blurRect.RadiusY = radius;

            if (Backdrop == null || !t.Frosted || t.BlurRadius <= 0.5
                || ActualWidth < 1 || ActualHeight < 1)
            {
                _blurRect.Fill = null;
                _blurRect.Effect = null;
                return;
            }

            try
            {
                GeneralTransform gt = TransformToVisual(Backdrop);
                Rect box = gt.TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
                if (box.Width < 1 || box.Height < 1) { _blurRect.Fill = null; return; }

                VisualBrush vb = new VisualBrush(Backdrop);
                vb.ViewboxUnits = BrushMappingMode.Absolute;
                vb.Viewbox = box;
                vb.Stretch = Stretch.Fill;
                vb.AlignmentX = AlignmentX.Left;
                vb.AlignmentY = AlignmentY.Top;
                _blurRect.Fill = vb;

                BlurEffect blur = new BlurEffect();
                blur.Radius = t.BlurRadius;
                blur.KernelType = KernelType.Gaussian;
                blur.RenderingBias = RenderingBias.Performance;
                _blurRect.Effect = blur;
            }
            catch
            {
                _blurRect.Fill = null;
                _blurRect.Effect = null;
            }
        }
    }

    // -----------------------------------------------------------------------
    //  扁平控件工厂
    // -----------------------------------------------------------------------
    public static class Ui
    {
        public static TextBlock Text(string text, double size, string brushKey)
        {
            return Text(text, size, brushKey, FontWeights.Normal);
        }

        public static TextBlock Text(string text, double size, string brushKey, FontWeight weight)
        {
            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = size;
            tb.FontWeight = weight;
            tb.TextWrapping = TextWrapping.NoWrap;
            if (brushKey != null) tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        public static TextBlock Wrap(string text, double size, string brushKey)
        {
            TextBlock tb = Text(text, size, brushKey);
            tb.TextWrapping = TextWrapping.Wrap;
            return tb;
        }

        public static StackPanel V()
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;
            return sp;
        }

        public static StackPanel H()
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            return sp;
        }

        public static Border Spacer(double w, double h)
        {
            Border b = new Border();
            b.Width = w;
            b.Height = h;
            return b;
        }

        public static Button Btn(string text, string styleKey, RoutedEventHandler onClick)
        {
            Button b = new Button();
            b.Content = text;
            if (styleKey != null) b.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Button IconBtn(string icon, string tooltip, RoutedEventHandler onClick)
        {
            Button b = new Button();
            b.Content = Icons.Create(icon, 17, "TextDimBrush", 1.6);
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            b.ToolTip = tooltip;
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Border Card(UIElement child)
        {
            Border b = new Border();
            b.SetResourceReference(FrameworkElement.StyleProperty, "Card");
            b.Child = child;
            return b;
        }

        /// <summary>
        /// 按 FrameworkElement.Tag 递归查找子元素（取第一个）。
        /// 之前 ColorPicker / ThemeWindow / SettingsWindow 各写了一份一模一样的，
        /// 统一放到这里。注意：窗口 Show() 之前视觉树还没建好，构造期用它找不到东西，
        /// 那种场景应该直接持有控件引用。
        /// </summary>
        public static FrameworkElement FindByTag(DependencyObject root, string tag)
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null && tag.Equals(fe.Tag)) return fe;
                FrameworkElement deep = FindByTag(child, tag);
                if (deep != null) return deep;
            }
            return null;
        }

        public static Border HairLine()
        {
            Border b = new Border();
            b.SetResourceReference(FrameworkElement.StyleProperty, "HairLine");
            return b;
        }

        /// <summary>给元素加一层柔和的投影（扁平设计里只用来表达层级，不表达立体感）。</summary>
        public static void AddSoftShadow(UIElement el, double opacity, double depth, double blur)
        {
            DropShadowEffect e = new DropShadowEffect();
            e.BlurRadius = blur;
            e.ShadowDepth = depth;
            e.Direction = 270;
            e.Opacity = opacity;
            e.Color = Color.FromRgb(0x10, 0x18, 0x28);
            e.RenderingBias = RenderingBias.Performance;
            el.Effect = e;
        }

        public static void SetMargin(FrameworkElement el, double left, double top, double right, double bottom)
        {
            el.Margin = new Thickness(left, top, right, bottom);
        }
    }

    // -----------------------------------------------------------------------
    //  样式表加载
    // -----------------------------------------------------------------------
    public static class FlatStyles
    {
        public static string LastError = "";

        public static void Load()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string text = null;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("styles.xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream st = asm.GetManifestResourceStream(name))
                        {
                            if (st == null) continue;
                            using (StreamReader sr = new StreamReader(st, System.Text.Encoding.UTF8))
                            {
                                text = sr.ReadToEnd();
                            }
                        }
                        break;
                    }
                }
                if (text == null) { LastError = Loc.T("未找到内嵌样式表 ui.styles.xaml"); return; }

                ResourceDictionary dict = XamlReader.Parse(text) as ResourceDictionary;
                if (dict == null) { LastError = Loc.T("样式表解析结果不是 ResourceDictionary"); return; }
                Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }
}
