using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Switcheroo.Preview
{
    /// <summary>
    /// DWM 实时缩略图宿主：把一个目标窗口的内容由 DWM 直接合成到本控件里。
    /// 相比 PrintWindow 的好处：**被遮挡 / DWM 合成 / GPU 渲染的窗口也能正确显示**，
    /// 而且不需要截图缓存（DWM 自己刷新）。
    /// 用法：<c>&lt;preview:DwmPreview SourceHwnd="{Binding HWnd}" Width="320" Height="200"/&gt;</c>
    /// </summary>
    public class DwmPreview : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int DWM_TNP_RECTDESTINATION = 0x1;
        private const int DWM_TNP_OPACITY = 0x4;
        private const int DWM_TNP_VISIBLE = 0x8;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out RECT size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        public static readonly DependencyProperty SourceHwndProperty = DependencyProperty.Register(
            "SourceHwnd", typeof (IntPtr), typeof (DwmPreview),
            new PropertyMetadata(IntPtr.Zero, OnSourceHwndChanged));

        public IntPtr SourceHwnd
        {
            get { return (IntPtr) GetValue(SourceHwndProperty); }
            set { SetValue(SourceHwndProperty, value); }
        }

        private IntPtr _hostHwnd = IntPtr.Zero;
        private IntPtr _thumb = IntPtr.Zero;

        private static void OnSourceHwndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = d as DwmPreview;
            if (host != null) host.Refresh();
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            try
            {
                _hostHwnd = CreateWindowEx(0, "static", "",
                    WS_CHILD | WS_VISIBLE, 0, 0,
                    Math.Max(1, (int) ActualWidth), Math.Max(1, (int) ActualHeight),
                    hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                _hostHwnd = IntPtr.Zero;
            }
            Refresh();
            return new HandleRef(this, _hostHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            ReleaseThumbnail();
            try
            {
                if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
            }
            catch
            {
            }
            _hostHwnd = IntPtr.Zero;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateThumbnailRect();
        }

        private void ReleaseThumbnail()
        {
            try
            {
                if (_thumb != IntPtr.Zero)
                {
                    DwmUnregisterThumbnail(_thumb);
                }
            }
            catch
            {
            }
            _thumb = IntPtr.Zero;
        }

        private void Refresh()
        {
            try
            {
                if (_hostHwnd == IntPtr.Zero) return;

                bool enabled;
                try { enabled = Switcheroo.Properties.Settings.Default.ThumbnailPreviewEnabled; }
                catch { enabled = true; }
                if (!enabled)
                {
                    ReleaseThumbnail();
                    return;
                }

                var src = SourceHwnd;
                ReleaseThumbnail();

                if (src == IntPtr.Zero || src == _hostHwnd) return;
                // 最小化窗口没有可合成的内容
                if (IsIconic(src)) return;

                IntPtr thumb;
                if (DwmRegisterThumbnail(_hostHwnd, src, out thumb) == 0 && thumb != IntPtr.Zero)
                {
                    _thumb = thumb;
                    UpdateThumbnailRect();
                }
            }
            catch
            {
            }
        }

        private void UpdateThumbnailRect()
        {
            if (_thumb == IntPtr.Zero) return;
            try
            {
                int w = Math.Max(1, (int) ActualWidth);
                int h = Math.Max(1, (int) ActualHeight);

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT { Left = 0, Top = 0, Right = w, Bottom = h },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = true
                };
                DwmUpdateThumbnailProperties(_thumb, ref props);
            }
            catch
            {
            }
        }
    }
}
