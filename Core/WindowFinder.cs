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

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Switcheroo.Core
{
    public class WindowFinder
    {
        // shell / 桌面合成类窗口：它们不是"应用窗口"，列进切换器并被激活，
        // 等于直接去碰 explorer 的 XAML/COM 侧（Win11 的任务栏与桌面都是 XAML 岛）。
        internal static readonly string[] ShellWindowClasses =
        {
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Progman",
            "WorkerW",
            "ApplicationManager_DesktopShellWindow",
            "Windows.UI.Core.CoreWindow",
            "MultitaskingViewFrame",
            "XamlExplorerHostIslandWindow"
        };

        public List<AppWindow> GetWindows()
        {
            var all = AppWindow.AllToplevelWindows.ToList();
            var kept = new List<AppWindow>(all.Count);
            var excluded = new StringBuilder();

            foreach (var w in all)
            {
                string cls = null;
                try { cls = w.ClassName; } catch { }

                if (cls != null && ShellWindowClasses.Contains(cls))
                {
                    if (excluded.Length < 800)
                    {
                        excluded.Append(cls).Append('#').Append(w.ProcessId).Append(' ');
                    }
                    continue;
                }

                bool isTabWindow;
                try { isTabWindow = w.IsAltTabWindow(); } catch { isTabWindow = false; }
                if (isTabWindow)
                {
                    kept.Add(w);
                }
            }

            if (excluded.Length > 0)
            {
                DiagLog.Info("WindowFinder: excluded shell windows -> " + excluded.ToString().TrimEnd());
            }
            DiagLog.Info("WindowFinder: toplevel=" + all.Count + " kept=" + kept.Count);

            return kept;
        }
    }
}
