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
    /// 窗口缩略图：用 PrintWindow(PW_RENDERFULLCONTENT) 抓一张位图并缓存。
    /// 只对 DWM 之外也能拍到的窗口有效；拍不到就返回 null（不抛异常）。
    /// 仅在用户悬停时按需取图，不在枚举阶段批量抓取（那会拖慢首次弹出）。
    /// </summary>
    public static class ThumbnailProvider
    {
        private const int PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static readonly Dictionary<IntPtr, BitmapSource> Cache = new Dictionary<IntPtr, BitmapSource>();
        private static readonly Dictionary<IntPtr, DateTime> CacheAt = new Dictionary<IntPtr, DateTime>();

        public static BitmapSource Get(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                BitmapSource cached;
                DateTime at;
                if (Cache.TryGetValue(hwnd, out cached) && CacheAt.TryGetValue(hwnd, out at)
                    && (DateTime.UtcNow - at).TotalSeconds < 10)
                {
                    return cached;
                }

                RECT r;
                if (!GetWindowRect(hwnd, out r)) return null;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0 || w > 4000 || h > 4000) return null;

                // 限制源尺寸，避免 4K 窗口抓大图
                const int MaxW = 640;
                double scale = w > MaxW ? (double) MaxW / w : 1.0;
                int bw = Math.Max(1, (int) (w * scale));
                int bh = Math.Max(1, (int) (h * scale));

                BitmapSource result = null;
                using (var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try
                        {
                            ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                        if (!ok) return null;
                    }
                    IntPtr hbmp = bmp.GetHbitmap();
                    try
                    {
                        result = Imaging.CreateBitmapSourceFromHBitmap(
                            hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        result.Freeze();
                    }
                    finally
                    {
                        DeleteObject(hbmp);
                    }
                }

                if (result != null)
                {
                    if (Cache.Count > 64)
                    {
                        Cache.Clear();
                        CacheAt.Clear();
                    }
                    Cache[hwnd] = result;
                    CacheAt[hwnd] = DateTime.UtcNow;
                }
                return result;
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
