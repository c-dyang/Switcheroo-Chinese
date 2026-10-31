using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Switcheroo.Preview
{
    /// <summary>
    /// 窗口快照缓存：给**最小化窗口**兜底用。
    ///
    /// 背景（微软官方答复 + 业界做法）：最小化窗口没有客户区，DWM 无法渲染其缩略图，
    /// 任务栏缩略图显示的也是"最小化前的最后一帧"。所以正确做法是**在窗口还可见时抓一帧存起来**，
    /// 等它最小化后贴出来（参考 jcardama/LeopardWM：'captures each window with PrintWindow
    /// right before it leaves the screen and shows that frame when DWM thumbnails are unavailable'）。
    /// </summary>
    public static class SnapshotCache
    {
        private const int PW_RENDERFULLCONTENT = 2;
        private const int MaxEntries = 16;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private static readonly Dictionary<IntPtr, BitmapSource> Cache = new Dictionary<IntPtr, BitmapSource>();
        private static readonly List<IntPtr> Order = new List<IntPtr>();

        /// <summary>抓一帧存入缓存（窗口必须处于可见状态才有意义）。返回是否成功。</summary>
        public static bool Capture(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;

                RECT r;
                if (!GetWindowRect(hwnd, out r)) return false;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 8 || h <= 8 || w > 8000 || h > 8000) return false;

                const int MaxW = 800;
                double scale = w > MaxW ? (double) MaxW / w : 1.0;
                int bw = Math.Max(1, (int) (w * scale));
                int bh = Math.Max(1, (int) (h * scale));

                BitmapSource src = null;
                using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                        finally { g.ReleaseHdc(hdc); }
                        if (!ok) return false;
                    }
                    IntPtr hbmp = bmp.GetHbitmap();
                    try
                    {
                        src = Imaging.CreateBitmapSourceFromHBitmap(
                            hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                    }
                    finally
                    {
                        DeleteObject(hbmp);
                    }
                }

                if (src == null) return false;
                if (!Cache.ContainsKey(hwnd)) Order.Add(hwnd);
                Cache[hwnd] = src;
                while (Order.Count > MaxEntries)
                {
                    var oldest = Order[0];
                    Order.RemoveAt(0);
                    Cache.Remove(oldest);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static BitmapSource TryGet(IntPtr hwnd)
        {
            try
            {
                BitmapSource v;
                return Cache.TryGetValue(hwnd, out v) ? v : null;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
    }
}
