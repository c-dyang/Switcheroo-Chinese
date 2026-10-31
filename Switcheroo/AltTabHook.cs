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
        private const int VK_ALT = 0x12;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private readonly KeyboardKey _leftShiftKey = new KeyboardKey(Keys.LShiftKey);
        private readonly KeyboardKey _rightShiftKey = new KeyboardKey(Keys.RShiftKey);
        private readonly KeyboardKey _ctrlKey = new KeyboardKey(Keys.LControlKey);
        private readonly KeyboardKey _altKey = new KeyboardKey(Keys.LMenu);

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly LowLevelKeyboardHook _lowLevelKeyboardHook;

        // 内存环形缓冲：钩子里绝不能做文件 IO（会拖慢回调 → 触发 LowLevelHooksTimeout）
        private readonly List<string> _trace = new List<string>(64);
        // 本次 Alt+Tab 手势里还需要吞掉随后的 Alt 抬起（否则贴边类应用会把"Alt 单击"当成自己的快捷键）
        private bool _pendingAltUp;

        public AltTabHook()
        {
            _lowLevelKeyboardHook = new LowLevelKeyboardHook();
            _lowLevelKeyboardHook.MessageIntercepted += OnMessageIntercepted;
            _lowLevelKeyboardHook.StartHook();
        }

        /// <summary>
        /// WH_KEYBOARD_LL 回调：必须在 LowLevelHooksTimeout（默认 300ms）内返回 —— 只做廉价判定，
        /// 重活全部 BeginInvoke 到 UI 线程；文件日志写到内存环形缓冲，由 Tab/Alt 事件触发时才落盘。
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
            var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // ---- Alt 键：本次手势里随后的抬起必须吞掉 ----
            if (vk == VK_ALT || vk == VK_LMENU || vk == VK_RMENU)
            {
                if (isUp && _pendingAltUp)
                {
                    handled = true;
                    _pendingAltUp = false;
                    Trace(vk, msg, true, "swallow-alt-up");
                    FlushTrace("Alt up swallowed (gesture completed)");
                }
                return;
            }

            if (vk != (int) Keys.Tab)
            {
                return;
            }

            if (handled)
            {
                return;
            }

            var altDown = IsKeyDown(_altKey);
            if (!altDown)
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

            // Tab 按下与抬起都吞；按下时同时标记"随后的 Alt 抬起也要吞"
            handled = true;
            Trace(vk, msg, true, isDown ? "swallow-tab-down" : "swallow-tab-up");

            if (!isDown)
            {
                return;
            }

            _pendingAltUp = true;
            FlushTrace("Alt+Tab intercepted");

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

        private void Trace(int vk, int msg, bool swallowed, string note)
        {
            if (_trace.Count >= 64)
            {
                _trace.RemoveRange(0, 32);
            }
            _trace.Add("vk=0x" + vk.ToString("X2") + " msg=0x" + msg.ToString("X") + (swallowed ? " SWALLOWED" : "") + " " + note);
        }

        /// <summary>把环形缓冲落盘（只在 Tab / Alt 结束时调用，不在普通键路径上做 IO）。</summary>
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
