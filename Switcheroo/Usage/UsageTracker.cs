using System;
using System.Collections.Generic;
using System.Text;
using Switcheroo.Properties;

namespace Switcheroo.Usage
{
    /// <summary>
    /// 使用频次/最近使用统计（用于切换列表排序）。
    /// 存储格式刻意用最笨的文本：<c>进程名|次数|lastUsedTicks</c> 每行一条，
    /// 避免引入 JSON 依赖；读写都在 UI 线程，条目量级只有几十。
    /// </summary>
    public static class UsageTracker
    {
        private sealed class Entry
        {
            public int Count;
            public long LastTicks;
        }

        private static readonly Dictionary<string, Entry> Data =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static bool _dirty;
        private static DateTime _lastSaveUtc = DateTime.UtcNow;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var raw = Settings.Default.UsageStats;
                if (string.IsNullOrEmpty(raw)) return;
                foreach (var line in raw.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    var parts = t.Split('|');
                    if (parts.Length < 3) continue;
                    int count;
                    long ticks;
                    if (!int.TryParse(parts[1], out count)) continue;
                    if (!long.TryParse(parts[2], out ticks)) continue;
                    Data[parts[0]] = new Entry { Count = count, LastTicks = ticks };
                }
            }
            catch
            {
            }
        }

        /// <summary>记录一次切换（进程维度）。</summary>
        public static void Record(string processTitle)
        {
            if (string.IsNullOrEmpty(processTitle)) return;
            try
            {
                EnsureLoaded();
                Entry e;
                if (!Data.TryGetValue(processTitle, out e))
                {
                    e = new Entry();
                    Data[processTitle] = e;
                }
                if (e.Count < 1000000) e.Count++;
                e.LastTicks = DateTime.UtcNow.Ticks;
                _dirty = true;

                // 最多每 30 秒落盘一次，避免每次切换都写设置文件
                if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= 30)
                {
                    Save();
                }
            }
            catch
            {
            }
        }

        public static long GetLastTicks(string processTitle)
        {
            try
            {
                EnsureLoaded();
                Entry e;
                return Data.TryGetValue(processTitle, out e) ? e.LastTicks : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static int GetCount(string processTitle)
        {
            try
            {
                EnsureLoaded();
                Entry e;
                return Data.TryGetValue(processTitle, out e) ? e.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void Save()
        {
            if (!_dirty) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in Data)
                {
                    sb.Append(kv.Key).Append('|').Append(kv.Value.Count).Append('|')
                      .Append(kv.Value.LastTicks).Append('\n');
                }
                Settings.Default.UsageStats = sb.ToString();
                Settings.Default.Save();
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
            }
            catch
            {
            }
        }
    }
}
