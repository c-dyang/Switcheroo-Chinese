using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Switcheroo.Preview
{
    /// <summary>
    /// 独立的缩略图预览窗口。
    /// 为什么不放在 ToolTip 里：ToolTip 是分层/透明弹窗（AllowsTransparency），
    /// DWM 缩略图在分层窗口上不会被合成，现象就是"什么也不显示且没有异常"。
    /// 所以用一个**非分层、不抢焦点**的顶层窗口来承载 DwmPreview。
    /// </summary>
    public class PreviewWindow : Window
    {
        private readonly DwmPreview _preview;

        public PreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = false; // 必须非分层，否则 DWM 缩略图不合成
            Background = Brushes.Black;
            Width = 360;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.Manual;

            _preview = new DwmPreview();
            Content = new Border
            {
                Background = Brushes.Black,
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Child = _preview
            };
        }

        /// <summary>在指定屏幕位置显示某个窗口的实时缩略图。</summary>
        public void ShowFor(IntPtr hwnd, double left, double top, double width, double height)
        {
            try
            {
                Width = Math.Max(120, width);
                Height = Math.Max(80, height);
                Left = left;
                Top = top;
                if (!IsVisible) Show();
                _preview.SourceHwnd = hwnd;
            }
            catch
            {
            }
        }

        public void HidePreview()
        {
            try
            {
                _preview.SourceHwnd = IntPtr.Zero;
                if (IsVisible) Hide();
            }
            catch
            {
            }
        }
    }
}
