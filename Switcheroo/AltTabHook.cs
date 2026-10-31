/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * https://github.com/coezbek/switcheroo
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 * 
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ManagedWinapi;
using ManagedWinapi.Hooks;

namespace Switcheroo
{
    public delegate void AltTabHookEventHandler(object sender, AltTabHookEventArgs args);

    public class AltTabHookEventArgs : EventArgs
    {
        public bool CtrlDown { get; set; }
        public bool ShiftDown { get; set; }
        public bool Handled { get; set; }
    }

    public class AltTabHook : IDisposable
    {
        public event AltTabHookEventHandler Pressed;

        private const int CtrlKey = 11;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private readonly KeyboardKey _leftShiftKey = new KeyboardKey(Keys.LShiftKey);
        private readonly KeyboardKey _rightShiftKey = new KeyboardKey(Keys.RShiftKey);
        private readonly KeyboardKey _ctrlKey = new KeyboardKey(Keys.LControlKey);
        private readonly KeyboardKey _altKey = new KeyboardKey(Keys.LMenu);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly LowLevelKeyboardHook _lowLevelKeyboardHook;

        // 内存环形缓冲：钩子回调里绝不做文件 IO（IO 会拖慢回调 → 触发 LowLevelHooksTimeout）
        private readonly List<string> _trace = new List<string>(96);

        // 这些前台窗口收到裸 Alt 按下会自己执行命令（AIMP 贴边模式的托盘控制条 → 弹「关于」）。
        // 只对这些类抑制 Alt，而且按下/抬起成对吞 —— 只吞抬起会让系统认为 Alt 一直按着。
        private static readonly string[] AltReactiveForegroundClasses = { "TAIMPTrayControl" };
        private bool _altDownSuppressed;

        public AltTabHook()
        {
            _lowLevelKeyboardHook = new LowLevelKeyboardHook();
            _lowLevelKeyboardHook.MessageIntercepted += OnMessageIntercepted;
            _lowLevelKeyboardHook.StartHook();
        }

        /// <summary>
        /// WH_KEYBOARD_LL 回调。硬约束：必须在 LowLevelHooksTimeout（默认 300ms）内返回。
        /// 本版只做两件事：① 吞掉 Alt+Tab 的 Tab（按下+抬起）② 把所有键事件记进内存环形缓冲。
        /// **不碰 Alt 键** —— 吞掉 Alt 抬起会让系统认为 Alt 一直被按住，后续按键全被当成 Alt 组合。
        /// </summary>
        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (keyboardMessage == null)
            {
                return;
            }

            var vk = keyboardMessage.VirtualKeyCode;
            var msg = keyboardMessage.Message;
            var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            // 全键记录（内存，无 IO）：漏给前台的键在 trace 里没有 SWALLOWED 标记
            Trace(vk, msg, false, "pass");

            // ---- Alt：仅对"收到裸 Alt 就会自己执行命令"的前台窗口做抑制，且成对吞 ----
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5)
            {
                if (!handled)
                {
                    if (isDown && ShouldSuppressAltDown())
                    {
                        handled = true;
                        _altDownSuppressed = true;
                        MarkLastSwallowed();
                    }
                    else if (!isDown && _altDownSuppressed)
                    {
                        handled = true;
                        _altDownSuppressed = false;
                        MarkLastSwallowed();
                    }
                }
                return;
            }

            if (handled)
            {
                return;
            }

            if (vk != (int) Keys.Tab)
            {
                return;
            }

            if (!IsKeyDown(_altKey))
            {
                return;
            }

            bool hookEnabled;
            try { hookEnabled = Switcheroo.Properties.Settings.Default.AltTabHook; }
            catch { hookEnabled = false; }
            if (!hookEnabled)
            {
                return;
            }

            // 只吞 Tab（按下与抬起都吞）；Alt 原样放过，避免"Alt 卡住"
            handled = true;
            MarkLastSwallowed();

            if (!isDown)
            {
                return;
            }

            FlushTrace("Alt+Tab intercepted, fg=" + ForegroundClass());

            var shiftDown = IsKeyDown(_leftShiftKey) || IsKeyDown(_rightShiftKey);
            var ctrlDown = IsKeyDown(_ctrlKey);
            var args = new AltTabHookEventArgs {ShiftDown = shiftDown, CtrlDown = ctrlDown};

            var dispatcher = System.Windows.Application.Current == null
                ? null
                : System.Windows.Application.Current.Dispatcher;

            if (dispatcher == null)
            {
                OnPressed(args);
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try { OnPressed(args); }
                catch (Exception ex)
                {
                    try { Switcheroo.Core.DiagLog.Error("AltTabHook handler failed", ex); } catch { }
                }
            }));
        }

        private static bool IsKeyDown(KeyboardKey keyboardKey)
        {
            return (keyboardKey.AsyncState & 32768) != 0;
        }

        private AltTabHookEventArgs OnPressed(AltTabHookEventArgs args)
        {
            var handler = Pressed;
            if (handler != null)
            {
                handler(this, args);
            }
            return args;
        }

        private static string ForegroundClass()
        {
            try
            {
                var h = GetForegroundWindow();
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                return "0x" + h.ToInt64().ToString("X") + ":" + sb;
            }
            catch
            {
                return "?";
            }
        }

        private static bool ShouldSuppressAltDown()
        {
            try
            {
                var h = GetForegroundWindow();
                var sb = new StringBuilder(256);
                if (GetClassName(h, sb, 256) == 0)
                {
                    return false;
                }
                var cls = sb.ToString();
                foreach (var c in AltReactiveForegroundClasses)
                {
                    if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void Trace(int vk, int msg, bool swallowed, string note)
        {
            if (_trace.Count >= 96)
            {
                _trace.RemoveRange(0, 48);
            }
            _trace.Add("0x" + vk.ToString("X2") + "/m" + msg.ToString("X")
                       + (swallowed ? "*SW" : "") + " " + note);
        }

        private void MarkLastSwallowed()
        {
            if (_trace.Count == 0)
            {
                return;
            }
            _trace[_trace.Count - 1] = _trace[_trace.Count - 1] + "*SW";
        }

        /// <summary>把环形缓冲落盘（只在 Alt+Tab 命中时调用，不在普通键路径上做 IO）。</summary>
        private void FlushTrace(string headline)
        {
            try
            {
                if (_trace.Count == 0)
                {
                    return;
                }
                Switcheroo.Core.DiagLog.Info("Hook: " + headline + " | trace: " + string.Join(" -> ", _trace.ToArray()));
                _trace.Clear();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_lowLevelKeyboardHook != null)
            {
                _lowLevelKeyboardHook.Dispose();
            }
        }
    }
}
