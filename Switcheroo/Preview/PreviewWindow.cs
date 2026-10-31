using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switcheroo.Preview
{
    /// <summary>
    /// 缩略图预览窗口。
    ///
    /// 设计要点（均为实测/开源实现验证过的）：
    /// 1) DWM 缩略图必须注册到**本窗口自己的顶层 HWND**（HwndHost 子窗口不合成）；
    /// 2) rcDestination 用**物理像素**，窗口定位走 SetWindowPos（WPF 的 Left/Top 是 DIP，跨屏会漂）；
    /// 3) 等比“铺满并裁切”（cover），不留黑边；
    /// 4) **最小化窗口**没有可渲染的客户区 ⇒ 用 SnapshotCache 里的最后一帧兜底；
    /// 5) 分组时把组内多个窗口铺成网格（DWM 支持多个 thumbnail 注册到同一 destination）。
    /// </summary>
    public class PreviewWindow : Window
    {
        private const int DWM_TNP_RECTDESTINATION = 0x1;
        private const int DWM_TNP_RECTSOURCE = 0x2;
        private const int DWM_TNP_OPACITY = 0x4;
        private const int DWM_TNP_VISIBLE = 0x8;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SWP_HIDEWINDOW = 0x0080;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private readonly Canvas _canvas;
        private readonly List<IntPtr> _thumbs = new List<IntPtr>();
        private IntPtr _hwnd = IntPtr.Zero;

        public PreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = false;   // 分层窗口上 DWM 不合成缩略图
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;

            _canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };
            Content = _canvas;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try { _hwnd = new WindowInteropHelper(this).Handle; }
            catch { }
        }

        /// <summary>显示一组窗口的预览（1 个 = 单图，多个 = 网格）。位置与尺寸均为**物理像素**。</summary>
        public void ShowPreview(IntPtr[] sources, int leftPx, int topPx, int widthPx, int heightPx)
        {
            try
            {
                if (sources == null || sources.Length == 0)
                {
                    HidePreview();
                    return;
                }
                if (_hwnd == IntPtr.Zero)
                {
                    Show();
                    _hwnd = new WindowInteropHelper(this).Handle;
                }

                int wantW = Math.Max(80, widthPx);
                int wantH = Math.Max(60, heightPx);

                // 第一次：先按目标尺寸摆一次，用来测量"窗口坐标 vs 客户区坐标"的边框偏移
                SetWindowPos(_hwnd, HWND_TOPMOST, leftPx, topPx, wantW, wantH,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);

                RECT wr, cr;
                var clientOrigin = new POINT { X = 0, Y = 0 };
                int frameW = 0, frameH = 0, offX = 0, offY = 0;
                if (GetWindowRect(_hwnd, out wr) && GetClientRect(_hwnd, out cr) &&
                    ClientToScreen(_hwnd, ref clientOrigin))
                {
                    frameW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
                    frameH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);
                    offX = clientOrigin.X - wr.Left;
                    offY = clientOrigin.Y - wr.Top;
                }

                // 第二次：把客户区精确摆到目标矩形（否则缩略图会整体偏移几像素）
                SetWindowPos(_hwnd, HWND_TOPMOST,
                    leftPx - offX, topPx - offY,
                    wantW + Math.Max(0, frameW), wantH + Math.Max(0, frameH),
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);

                BuildCells(sources, wantW, wantH);
            }
            catch (Exception ex)
            {
                try { Switcheroo.Core.DiagLog.Error("Preview.ShowPreview", ex); } catch { }
            }
        }

        public void HidePreview()
        {
            try
            {
                ReleaseThumbs();
                _canvas.Children.Clear();
                // 注意：窗口可能是用 SetWindowPos(SWP_SHOWWINDOW) 显示的，WPF 的 IsVisible 不一定同步，
                // 所以这里无条件 Hide() + SWP_HIDEWINDOW 双保险，避免预览窗留在屏幕上不消失。
                if (_hwnd != IntPtr.Zero)
                {
                    SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE);
                }
                Hide();
            }
            catch
            {
            }
        }

        private void ReleaseThumbs()
        {
            foreach (var t in _thumbs)
            {
                try { DwmUnregisterThumbnail(t); } catch { }
            }
            _thumbs.Clear();
        }

        private double DpiScale()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        private void BuildCells(IntPtr[] sources, int wpx, int hpx)
        {
            ReleaseThumbs();
            _canvas.Children.Clear();

            int n = sources.Length;
            if (n > 6) n = 6;

            int cols = n <= 1 ? 1 : n <= 2 ? 2 : n <= 4 ? 2 : 3;
            int rows = (n + cols - 1) / cols;
            const int Gap = 2;

            double scale = DpiScale();

            // 末列/末行吸收整除余数，避免右边和下边出现 1~2px 的空隙
            var colW = new int[cols];
            var colX = new int[cols];
            int baseW = (wpx - Gap * (cols - 1)) / cols;
            for (int c = 0; c < cols; c++)
            {
                colW[c] = c == cols - 1 ? wpx - Gap * (cols - 1) - baseW * (cols - 1) : baseW;
                colX[c] = c == 0 ? 0 : colX[c - 1] + colW[c - 1] + Gap;
            }
            var rowH = new int[rows];
            var rowY = new int[rows];
            int baseH = (hpx - Gap * (rows - 1)) / rows;
            for (int r = 0; r < rows; r++)
            {
                rowH[r] = r == rows - 1 ? hpx - Gap * (rows - 1) - baseH * (rows - 1) : baseH;
                rowY[r] = r == 0 ? 0 : rowY[r - 1] + rowH[r - 1] + Gap;
            }

            for (int i = 0; i < n; i++)
            {
                int cx = i % cols;
                int cy = i / cols;
                int cellW = colW[cx];
                int cellH = rowH[cy];
                if (cellW <= 4 || cellH <= 4) continue;

                int left = colX[cx];
                int top = rowY[cy];
                var src = sources[i];

                bool shown = false;
                if (src != IntPtr.Zero && !IsIconic(src))
                {
                    IntPtr thumb;
                    if (DwmRegisterThumbnail(_hwnd, src, out thumb) == 0 && thumb != IntPtr.Zero)
                    {
                        _thumbs.Add(thumb);
                        ApplyCell(thumb, left, top, cellW, cellH);
                        shown = true;
                    }
                }

                if (!shown)
                {
                    // 最小化（或注册失败）⇒ 用切换前的最后一帧快照兜底
                    var snap = SnapshotCache.TryGet(src);
                    if (snap != null)
                    {
                        var img = new Image
                        {
                            Source = snap,
                            Stretch = Stretch.UniformToFill,
                            Width = cellW / scale,
                            Height = cellH / scale
                        };
                        Canvas.SetLeft(img, left / scale);
                        Canvas.SetTop(img, top / scale);
                        _canvas.Children.Add(img);
                    }
                }
            }
        }

        /// <summary>把缩略图铺满整格并裁切（cover），避免出现黑边。</summary>
        private void ApplyCell(IntPtr thumb, int left, int top, int w, int h)
        {
            try
            {
                PSIZE src;
                var rcSource = new RECT();
                int flags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY;

                if (DwmQueryThumbnailSourceSize(thumb, out src) == 0 && src.x > 0 && src.y > 0)
                {
                    double srcRatio = (double) src.x / src.y;
                    double dstRatio = (double) w / h;
                    if (srcRatio > dstRatio)
                    {
                        int cropW = (int) Math.Round(src.y * dstRatio);
                        if (cropW < 1) cropW = 1;
                        int x = (src.x - cropW) / 2;
                        rcSource = new RECT { Left = x, Top = 0, Right = x + cropW, Bottom = src.y };
                    }
                    else if (srcRatio < dstRatio)
                    {
                        int cropH = (int) Math.Round(src.x / dstRatio);
                        if (cropH < 1) cropH = 1;
                        int y = (src.y - cropH) / 2;
                        rcSource = new RECT { Left = 0, Top = y, Right = src.x, Bottom = y + cropH };
                    }
                    else
                    {
                        rcSource = new RECT { Left = 0, Top = 0, Right = src.x, Bottom = src.y };
                    }
                    flags |= DWM_TNP_RECTSOURCE;
                }

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = flags,
                    rcDestination = new RECT { Left = left, Top = top, Right = left + w, Bottom = top + h },
                    rcSource = rcSource,
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = true
                };
                int hr = DwmUpdateThumbnailProperties(thumb, ref props);
                if (hr != 0)
                {
                    Switcheroo.Core.DiagLog.Info("Preview: update failed hr=0x" + hr.ToString("X8"));
                }
            }
            catch (Exception ex)
            {
                try { Switcheroo.Core.DiagLog.Error("Preview.ApplyCell", ex); } catch { }
            }
        }
    }
}
