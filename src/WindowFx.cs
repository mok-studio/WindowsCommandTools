// ---------------------------------------------------------------------------
//  WindowFx.cs — 窗口级的视觉效果
//
//  无边框窗口（WindowStyle=None + WindowChrome + AllowsTransparency=false）
//  默认是四四方方的直角，和里面一堆圆角面板放在一起很突兀。
//  这里做两件事：
//    1. Windows 11（Build 22000+）用 DWM 原生圆角，边缘带抗锯齿，效果最好；
//    2. 更早的系统退回 SetWindowRgn 自绘圆角区域（有锯齿，但至少是圆的）。
//
//  注意：AllowsTransparency=true 的分层窗口（各个对话框）不能用 DWM 圆角 ——
//  它们本来就是靠 WPF 自己画圆角外壳 + 阴影的，这里会跳过。
// ---------------------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WindowsCommandTools
{
    internal static class WindowFx
    {
        // ---- DWM 属性号（Win11 SDK 里的值） ----
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // 20H1 之前
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;

        private const int DWMWCP_DEFAULT = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        /// <summary>DWMWA_COLOR_NONE：让 DWM 不要画那圈强调色描边。</summary>
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        private static int _build = -1;

        // ==================================================================
        //  系统版本
        // ==================================================================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW info);

        /// <summary>
        /// 取真实系统 Build 号。
        /// 不能用 Environment.OSVersion —— 没有 manifest 声明支持 Win10 时它一律报 6.2.9200。
        /// </summary>
        public static int WindowsBuild()
        {
            if (_build >= 0) return _build;
            _build = 0;
            try
            {
                RTL_OSVERSIONINFOW v = new RTL_OSVERSIONINFOW();
                v.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                if (RtlGetVersion(ref v) == 0) _build = (int)v.dwBuildNumber;
            }
            catch { }
            return _build;
        }

        public static bool IsWindows11OrLater()
        {
            return WindowsBuild() >= 22000;
        }

        // ==================================================================
        //  P/Invoke
        // ==================================================================

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
            int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;

        // ==================================================================
        //  对外接口
        // ==================================================================

        /// <summary>窗口句柄还没建好或句柄无效时返回 Zero。</summary>
        private static IntPtr HandleOf(Window w)
        {
            try
            {
                if (w == null) return IntPtr.Zero;
                return new WindowInteropHelper(w).Handle;
            }
            catch { return IntPtr.Zero; }
        }

        private static bool IsLayered(IntPtr hwnd)
        {
            try { return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0; }
            catch { return false; }
        }

        /// <summary>
        /// 给无边框窗口加上圆角。
        /// radiusDip 只在旧系统回退时用到（DWM 圆角由系统按 DPI 自己定）。
        /// </summary>
        public static void ApplyRoundedCorners(Window w, double radiusDip, bool darkCaption)
        {
            IntPtr hwnd = HandleOf(w);
            if (hwnd == IntPtr.Zero) return;

            // 分层窗口（AllowsTransparency=true 的对话框）自己画圆角，不能也不需要用 DWM
            if (IsLayered(hwnd)) return;

            if (IsWindows11OrLater())
            {
                int pref = DWMWCP_ROUND;
                int hr = SafeDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, pref);
                if (hr == 0)
                {
                    // 窗口一旦有自定义区域，DWM 就不画圆角了 —— 确保清掉
                    try { SetWindowRgn(hwnd, IntPtr.Zero, true); } catch { }
                    // 去掉那圈强调色描边，保持扁平风格
                    SafeDwm(hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
                    ApplyCaptionMode(hwnd, darkCaption);
                    return;
                }
                // DWM 失效（远程桌面、关闭了合成等）→ 走回退
            }
            ApplyRegionCorners(w, hwnd, radiusDip);
            ApplyCaptionMode(hwnd, darkCaption);
        }

        /// <summary>窗口尺寸或最大化状态变化后重新应用（旧系统的区域圆角要跟着重算）。</summary>
        public static void RefreshCorners(Window w, double radiusDip, bool darkCaption)
        {
            IntPtr hwnd = HandleOf(w);
            if (hwnd == IntPtr.Zero) return;
            if (IsLayered(hwnd)) return;

            if (IsWindows11OrLater())
            {
                // 最大化时 DWM 自动变直角；还原后自动恢复圆角，这里只要同步标题栏明暗
                ApplyCaptionMode(hwnd, darkCaption);
                return;
            }

            if (w.WindowState == WindowState.Maximized)
            {
                try { SetWindowRgn(hwnd, IntPtr.Zero, true); } catch { }
                return;
            }
            ApplyRegionCorners(w, hwnd, radiusDip);
            ApplyCaptionMode(hwnd, darkCaption);
        }

        // ==================================================================
        //  实现细节
        // ==================================================================

        private static int SafeDwm(IntPtr hwnd, int attr, int value)
        {
            try
            {
                int v = value;
                return DwmSetWindowAttribute(hwnd, attr, ref v, 4);
            }
            catch { return -1; }
        }

        /// <summary>让系统画的标题栏按钮 / 阴影跟着主题明暗走。</summary>
        private static void ApplyCaptionMode(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            int hr = SafeDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, v);
            if (hr != 0) SafeDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, v);
        }

        /// <summary>旧系统回退：用 GDI 圆角区域裁剪整个窗口。有锯齿，但比直角强。</summary>
        private static void ApplyRegionCorners(Window w, IntPtr hwnd, double radiusDip)
        {
            if (radiusDip <= 0.5)
            {
                try { SetWindowRgn(hwnd, IntPtr.Zero, true); } catch { }
                return;
            }
            if (w.WindowState == WindowState.Maximized)
            {
                try { SetWindowRgn(hwnd, IntPtr.Zero, true); } catch { }
                return;
            }

            double scale = 1.0;
            try
            {
                PresentationSource src = PresentationSource.FromVisual(w);
                if (src != null && src.CompositionTarget != null)
                    scale = src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            if (scale <= 0) scale = 1.0;

            int wpx, hpx;
            try
            {
                wpx = (int)Math.Round(w.ActualWidth * scale);
                hpx = (int)Math.Round(w.ActualHeight * scale);
            }
            catch { return; }
            if (wpx <= 2 || hpx <= 2) return;

            int r = (int)Math.Round(radiusDip * scale);
            if (r < 1) r = 1;
            if (r * 2 > wpx) r = wpx / 2;
            if (r * 2 > hpx) r = hpx / 2;

            // CreateRoundRectRgn 的 right/bottom 是"右下角外沿"，要比像素数大 1
            IntPtr hRgn = CreateRoundRectRgn(0, 0, wpx + 1, hpx + 1, r * 2, r * 2);
            if (hRgn == IntPtr.Zero) return;
            try
            {
                // SetWindowRgn 成功后区域归系统所有，不能再 DeleteObject
                if (SetWindowRgn(hwnd, hRgn, true) == 0) DeleteObject(hRgn);
            }
            catch { try { DeleteObject(hRgn); } catch { } }
        }
    }
}
