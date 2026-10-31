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
using System.Windows;
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
        private const int AltKey = 32;
        private const int CtrlKey = 11;
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

        public AltTabHook()
        {
            _lowLevelKeyboardHook = new LowLevelKeyboardHook();
            _lowLevelKeyboardHook.MessageIntercepted += OnMessageIntercepted;
            _lowLevelKeyboardHook.StartHook();
        }

        /// <summary>
        /// WH_KEYBOARD_LL 回调：**必须在 LowLevelHooksTimeout（默认 300ms）内返回**，否则系统会
        /// 静默旁路本钩子，后续按键直接漏给前台程序（表现为"别的程序收到了不属于它的键"）。
        /// ⇒ 这里只做廉价判定 + 立刻吞键，真正的窗口枚举与 UI 全部丢给 UI 线程异步执行。
        /// </summary>
        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
            {
                return;
            }

            if (keyboardMessage.VirtualKeyCode != (int) Keys.Tab)
            {
                return;
            }

            // 廉价判定：Alt 必须按下（GetAsyncKeyState，无阻塞）
            if (!IsKeyDown(_altKey))
            {
                return;
            }

            // 钩子被用户在选项里关掉时完全不要介入，避免把 Alt+Tab 吃掉
            bool hookEnabled;
            try { hookEnabled = Settings.Default.AltTabHook; } catch { hookEnabled = false; }
            if (!hookEnabled)
            {
                return;
            }

            var isDown = keyboardMessage.Message == WM_KEYDOWN || keyboardMessage.Message == WM_SYSKEYDOWN;
            var isUp = keyboardMessage.Message == WM_KEYUP || keyboardMessage.Message == WM_SYSKEYUP;

            // Tab 按下与抬起都当场吞掉：漏给前台程序的 Tab 会被当成"应用自己的按键"
            handled = true;

            if (!isDown)
            {
                return;
            }

            var shiftDown = IsKeyDown(_leftShiftKey) || IsKeyDown(_rightShiftKey);
            var ctrlDown = IsKeyDown(_ctrlKey);
            var args = new AltTabHookEventArgs {ShiftDown = shiftDown, CtrlDown = ctrlDown};

            try
            {
                Switcheroo.Core.DiagLog.Info("Hook: Alt+Tab intercepted (shift=" + shiftDown + " ctrl=" + ctrlDown + ")");
            }
            catch
            {
            }

            // 重活（窗口枚举 / 布局 / 图标）交给 UI 线程排队，钩子回调立即返回
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
                try
                {
                    OnPressed(args);
                }
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

        public void Dispose()
        {
            if (_lowLevelKeyboardHook != null)
            {
                _lowLevelKeyboardHook.Dispose();
            }
        }
    }
}
