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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private System.Windows.Threading.DispatcherTimer _bannerWatch;
        private HashSet<string> _lastAimpWindows = new HashSet<string>();

        private LowLevelKeyboardHook _lowLevelKeyboardHook;

        // 钩子看门狗：LL 钩子被系统旁路（LowLevelHooksTimeout）时键会静默漏给前台程序。
        // 判据 = 系统有键盘输入、但我们 60 秒没收到任何键事件 ⇒ 重装钩子并记日志。
        private System.Windows.Threading.DispatcherTimer _hookWatchdog;
        private DateTime _lastHookKeyUtc = DateTime.UtcNow;
        private uint _lastInputTick;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // 内存环形缓冲：钩子回调里绝不做文件 IO（IO 会拖慢回调 → 触发 LowLevelHooksTimeout）
        private readonly List<string> _trace = new List<string>(96);

        // 这些前台窗口收到裸 Alt 按下会自己执行命令（AIMP 贴边模式的托盘控制条 → 弹「关于」）。
        // 只对这些类抑制 Alt，而且按下/抬起成对吞 —— 只吞抬起会让系统认为 Alt 一直按着。
        // AIMP（迷你播放模块 / 主窗）对"Alt 按下后失去前台"敏感：只要切换器抢走前台，它 128ms 后
        // 就弹「关于」（TAIMPAboutForm + 子窗口 TAIMPAboutBanner）。实测吞/放 Alt 都无法避免，
        // 唯一稳定做法是在这些前台窗口下把 Alt+Tab 让回系统处理。
        private static readonly string[] PassThroughForegroundClasses =
        {
            "TAIMPTrayControl",
            "TAIMPMainForm"
        };

        // 需要"成对吞掉 Alt"的前台窗口类；当前为空（AIMP 走 PassThrough 让路，无需抑制 Alt）
        private static readonly string[] AltReactiveForegroundClasses = new string[0];
        private bool _altDownSuppressed;
        // 钩子自己维护的 Alt 状态：一旦某次 Alt 被吞，GetAsyncKeyState 不再可靠
        private bool _altIsDown;

        public AltTabHook()
        {
            _lowLevelKeyboardHook = new LowLevelKeyboardHook();
            _lowLevelKeyboardHook.MessageIntercepted += OnMessageIntercepted;
            _lowLevelKeyboardHook.StartHook();
            StartHookWatchdog();

            // 诊断：扫描 AIMP 的全部窗口（顶层 + 子窗口），记录类名出现/消失的准确时刻与当时前台窗口。
            // 注意必须在应用自身进程里跑（本程序在用户的交互式会话里）；从 ssh/session 0 派生的进程
            // 看不到交互桌面上的窗口，EnumWindows 会是空的。
            try
            {
                _bannerWatch = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _bannerWatch.Tick += delegate
                {
                    try
                    {
                        var now = ScanAimpWindows();
                        foreach (var w in now)
                        {
                            if (!_lastAimpWindows.Contains(w))
                            {
                                Switcheroo.Core.DiagLog.Info("AIMPWIN APPEARED " + w + " | fg=" + ForegroundClass());
                            }
                        }
                        foreach (var w in _lastAimpWindows)
                        {
                            if (!now.Contains(w))
                            {
                                Switcheroo.Core.DiagLog.Info("AIMPWIN GONE " + w + " | fg=" + ForegroundClass());
                            }
                        }
                        _lastAimpWindows = now;
                    }
                    catch { }
                };
                _bannerWatch.Start();
            }
            catch { }
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

            _lastHookKeyUtc = DateTime.UtcNow;

            // 全键记录（内存，无 IO）：漏给前台的键在 trace 里没有 SWALLOWED 标记
            Trace(vk, msg, false, "pass");

            // ---- 让路：这些前台窗口下不接管 Alt+Tab（交回系统），避免触发对方自己的菜单/关于逻辑 ----
            if (vk == (int) Keys.Tab && IsPassThroughForeground())
            {
                Trace(vk, msg, false, "passthrough-fg");
                return;
            }

            // ---- Alt：保留成对抑制能力（当前名单为空即完全不介入） ----
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5)
            {
                _altIsDown = isDown;
                if (!handled)
                {
                    if (isDown && ShouldSuppressAltDown())
                    {
                        handled = true;
                        _altDownSuppressed = true;
                        MarkLastSwallowed();
                    }
                    else if (!isDown && (_altDownSuppressed || ShouldSuppressAltDown()))
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

            var altDown = IsKeyDown(_altKey) || _altIsDown;
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

        /// <summary>扫 AIMP 的全部窗口（顶层 + 子窗口），返回 "TOP/CHILD 类名 hwnd title" 集合。</summary>
        private static HashSet<string> ScanAimpWindows()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    var sb = new StringBuilder(256);
                    GetClassName(h, sb, 256);
                    var cls = sb.ToString();
                    if (cls.StartsWith("TAIMP", StringComparison.OrdinalIgnoreCase))
                    {
                        var tb = new StringBuilder(128);
                        GetWindowText(h, tb, 128);
                        set.Add("TOP " + cls + " 0x" + h.ToInt64().ToString("X") + " title='" + tb + "'");
                        EnumChildWindows(h, delegate(IntPtr c, IntPtr l2)
                        {
                            var cb = new StringBuilder(256);
                            GetClassName(c, cb, 256);
                            var cc = cb.ToString();
                            if (cc.StartsWith("TAIMP", StringComparison.OrdinalIgnoreCase))
                            {
                                var tb2 = new StringBuilder(128);
                                GetWindowText(c, tb2, 128);
                                set.Add("CHILD " + cc + " 0x" + c.ToInt64().ToString("X") + " title='" + tb2 + "'");
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return set;
        }

        private static string ForegroundClass()        {
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

        private static bool IsPassThroughForeground()
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
                foreach (var c in PassThroughForegroundClasses)
                {
                    if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0)
                {
                    return false;
                }
                var name = GetProcessNameCached((int) pid);

                // ① 用户规则（规则动作 = PassThrough）：静态快照，钩子里不做设置读取/反序列化
                bool useRules;
                try { useRules = Switcheroo.Properties.Settings.Default.UseWindowRules; }
                catch { useRules = true; }
                if (useRules)
                {
                    var tb = new StringBuilder(256);
                    GetWindowText(h, tb, 256);
                    if (Switcheroo.Highlighting.HighlightService.StaticIsPassThrough(name, tb.ToString(), cls))
                    {
                        return true;
                    }
                }

                // ② 进程级"让路名单"（用户维护，托盘菜单可移除）：名单为空时零开销，不查进程名
                var list = Switcheroo.Properties.Settings.Default.PassThroughProcesses;
                if (list == null || list.Count == 0 || string.IsNullOrEmpty(name))
                {
                    return false;
                }
                foreach (var p in list)
                {
                    if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase))
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

        private static readonly Dictionary<int, KeyValuePair<string, DateTime>> ProcessNameCache =
            new Dictionary<int, KeyValuePair<string, DateTime>>();

        /// <summary>
        /// pid → 进程名（带缓存）。钩子回调里不能每次 OpenProcess：缓存 3 秒，超 256 条直接清空。
        /// </summary>
        private static string GetProcessNameCached(int pid)
        {
            KeyValuePair<string, DateTime> entry;
            if (ProcessNameCache.TryGetValue(pid, out entry) &&
                (DateTime.UtcNow - entry.Value).TotalSeconds < 3)
            {
                return entry.Key;
            }
            string name = null;
            try
            {
                name = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (ProcessNameCache.Count > 256)
            {
                ProcessNameCache.Clear();
            }
            ProcessNameCache[pid] = new KeyValuePair<string, DateTime>(name, DateTime.UtcNow);
            return name;
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
            try
            {
                if (_hookWatchdog != null) _hookWatchdog.Stop();
                if (_bannerWatch != null) _bannerWatch.Stop();
            }
            catch
            {
            }
            if (_lowLevelKeyboardHook != null)
            {
                _lowLevelKeyboardHook.Dispose();
            }
        }

        // ---------------- 钩子看门狗 ----------------

        private void StartHookWatchdog()
        {
            try
            {
                bool enabled;
                try { enabled = Switcheroo.Properties.Settings.Default.HookWatchdogEnabled; }
                catch { enabled = true; }
                if (!enabled) return;

                _hookWatchdog = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(60)
                };
                _hookWatchdog.Tick += delegate { CheckHookAlive(); };
                _hookWatchdog.Start();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 系统有键盘输入、但本钩子 60 秒内一个键事件都没收到 ⇒ 判定被系统旁路，重装钩子。
        /// 只在「确有输入」时触发，且重装本身开销极小，误判代价可忽略。
        /// </summary>
        private void CheckHookAlive()
        {
            try
            {
                var li = new LASTINPUTINFO();
                li.cbSize = (uint) Marshal.SizeOf(typeof (LASTINPUTINFO));
                bool got = GetLastInputInfo(ref li);
                bool inputAdvanced = got && li.dwTime != _lastInputTick;
                _lastInputTick = li.dwTime;

                // 刚启动的 2 分钟内不判（避免误报）
                if (!inputAdvanced || (DateTime.UtcNow - _lastHookKeyUtc).TotalSeconds < 60) return;

                Switcheroo.Core.DiagLog.Info("HOOK STALL: 系统有键盘输入但 60s 未收到任何键事件 -> 重装钩子");
                ReinstallHook();
            }
            catch
            {
            }
        }

        private void ReinstallHook()
        {
            try
            {
                if (_lowLevelKeyboardHook != null)
                {
                    try
                    {
                        _lowLevelKeyboardHook.MessageIntercepted -= OnMessageIntercepted;
                        _lowLevelKeyboardHook.Dispose();
                    }
                    catch
                    {
                    }
                }
                _lowLevelKeyboardHook = new LowLevelKeyboardHook();
                _lowLevelKeyboardHook.MessageIntercepted += OnMessageIntercepted;
                _lowLevelKeyboardHook.StartHook();
                _lastHookKeyUtc = DateTime.UtcNow;
                Switcheroo.Core.DiagLog.Info("HOOK REINSTALLED");
            }
            catch (Exception ex)
            {
                Switcheroo.Core.DiagLog.Error("HOOK REINSTALL FAILED", ex);
            }
        }
    }
}
