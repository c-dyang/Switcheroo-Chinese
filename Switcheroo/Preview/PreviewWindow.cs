using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Switcheroo.Preview
{
    /// <summary>
    /// 缩略图预览窗口：把目标窗口的 DWM 缩略图**直接注册到本窗口自己的 HWND** 上。
    ///
    /// 为什么不用 HwndHost 子窗口（实测无效、且无异常）：
    /// DWM 缩略图只会合成到**顶层窗口的 surface** 上；用一个 CreateWindowEx 出来的
    /// WS_CHILD 子窗口做 destination，结果就是"什么都不显示"。
    /// 开源参考：miguelo96/WindowScatter 的 ThumbnailManager、dremin/RetroBar 的 TaskThumbnail
    /// —— 两者都用"宿主窗口句柄 + 物理像素 rcDestination + 布局变化时重刷"。
    ///
    /// 另外两个必须照抄的点：
    /// 1) rcDestination 用**物理像素**（WPF 的 ActualWidth 是 DIP，要乘 DPI 缩放）；
    /// 2) 内容区必须保持透明/不绘制，否则 WPF 的画面会把缩略图盖住。
    /// </summary>
    public class PreviewWindow : Window
    {
        private const int DWM_TNP_RECTDESTINATION = 0x1;
        private const int DWM_TNP_OPACITY = 0x4;
        private const int DWM_TNP_VISIBLE = 0x8;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PSIZE
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _thumb = IntPtr.Zero;

        public PreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = false;   // 分层窗口上 DWM 不合成缩略图
            Background = Brushes.Black;   // 没有缩略图时的底色
            Width = 360;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.Manual;

            // 内容必须完全透明，缩略图才不会被 WPF 画面盖住
            Content = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Switcheroo.Core.DiagLog.Info("Preview: window hwnd=0x" + _hwnd.ToInt64().ToString("X"));
            }
            catch
            {
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            ApplyThumbnail();
        }

        /// <summary>在指定位置显示某个窗口的实时缩略图。</summary>
        public void ShowFor(IntPtr sourceHwnd, double left, double top, double width, double height)
        {
            try
            {
                Width = Math.Max(160, width);
                Height = Math.Max(100, height);
                Left = left;
                Top = top;
                if (!IsVisible) Show();
                if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
                Register(sourceHwnd);
            }
            catch (Exception ex)
            {
                try { Switcheroo.Core.DiagLog.Error("Preview.ShowFor", ex); } catch { }
            }
        }

        public void HidePreview()
        {
            try
            {
                Unregister();
                if (IsVisible) Hide();
            }
            catch
            {
            }
        }

        private void Unregister()
        {
            try
            {
                if (_thumb != IntPtr.Zero) DwmUnregisterThumbnail(_thumb);
            }
            catch
            {
            }
            _thumb = IntPtr.Zero;
        }

        private void Register(IntPtr sourceHwnd)
        {
            Unregister();
            try
            {
                if (_hwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero || sourceHwnd == _hwnd) return;
                if (IsIconic(sourceHwnd))
                {
                    Switcheroo.Core.DiagLog.Info("Preview: source minimized, skip 0x" + sourceHwnd.ToInt64().ToString("X"));
                    return;
                }

                IntPtr thumb;
                int hr = DwmRegisterThumbnail(_hwnd, sourceHwnd, out thumb);
                if (hr != 0 || thumb == IntPtr.Zero)
                {
                    Switcheroo.Core.DiagLog.Info("Preview: DwmRegisterThumbnail failed hr=0x" + hr.ToString("X8")
                                                 + " src=0x" + sourceHwnd.ToInt64().ToString("X"));
                    return;
                }
                _thumb = thumb;
                ApplyThumbnail();
            }
            catch (Exception ex)
            {
                try { Switcheroo.Core.DiagLog.Error("Preview.Register", ex); } catch { }
            }
        }

        private void ApplyThumbnail()
        {
            if (_thumb == IntPtr.Zero) return;
            try
            {
                // WPF 尺寸是 DIP，rcDestination 要物理像素
                double scale = 1.0;
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                }
                catch { }

                int wpx = Math.Max(1, (int) (ActualWidth * scale));
                int hpx = Math.Max(1, (int) (ActualHeight * scale));

                // 按源窗口比例做等比缩放并居中（避免拉伸变形）
                PSIZE src;
                int left = 0, top = 0, right = wpx, bottom = hpx;
                if (DwmQueryThumbnailSourceSize(_thumb, out src) == 0 && src.x > 0 && src.y > 0)
                {
                    double dstRatio = (double) wpx / hpx;
                    double srcRatio = (double) src.x / src.y;
                    if (srcRatio > dstRatio)
                    {
                        int h = (int) (wpx / srcRatio);
                        top = (hpx - h) / 2;
                        bottom = top + h;
                    }
                    else if (srcRatio < dstRatio)
                    {
                        int w = (int) (hpx * srcRatio);
                        left = (wpx - w) / 2;
                        right = left + w;
                    }
                }

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = true
                };
                int hr = DwmUpdateThumbnailProperties(_thumb, ref props);
                Switcheroo.Core.DiagLog.Info("Preview: applied dest=" + wpx + "x" + hpx
                                             + " (dpi=" + scale.ToString("F2") + ") hr=0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                try { Switcheroo.Core.DiagLog.Error("Preview.ApplyThumbnail", ex); } catch { }
            }
        }
    }
}
