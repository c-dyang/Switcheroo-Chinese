using System;
using System.Xml.Serialization;
using System.Text.RegularExpressions;

namespace Switcheroo.Core.Highlighting
{
    public enum MatchType
    {
        ProcessName,
        WindowTitleRegex,
        WindowClass
    }

    /// <summary>
    /// 规则动作：一条规则 = 匹配方式 + 动作。默认 Highlight 保持与旧版高亮规则完全兼容
    /// （旧 XML 里没有 Action 元素 → 反序列化取 0 = Highlight）。
    /// </summary>
    public enum RuleAction
    {
        Highlight = 0,   // 着色 / 标记（旧行为）
        Hide = 1,        // 完全不进切换列表（取代 BlacklistedProcesses）
        PassThrough = 2, // 该窗口在前台时不接管 Alt+Tab，交回系统切换器
        Pin = 3,         // 置顶到列表最前
        SkipInList = 4   // 排除出列表但不禁用（用于 shell/工具窗口，取代硬编码类表）
    }

    [Serializable]
    public class HighlightRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }

        public MatchType MatchType { get; set; }

        /// <summary>规则动作，默认 Highlight（兼容旧规则）。</summary>
        public RuleAction Action { get; set; }

        /// <summary>临时停用一条规则而不删除。</summary>
        public bool Enabled { get; set; } = true;

        private string _argument;
        public string Argument
        {
            get { return _argument; }
            set
            {
                if (_argument != value)
                {
                    _argument = value;
                    _cachedRegex = null;
                }
            }
        }

        // Visuals
        public string ColorHex { get; set; } // #RRGGBB or #AARRGGBB
        public string Marker { get; set; } // Text/Emoji

        [XmlIgnore]
        private Regex _cachedRegex;

        public Regex GetRegex()
        {
            if (_cachedRegex == null && MatchType == MatchType.WindowTitleRegex && !string.IsNullOrEmpty(Argument))
            {
                try
                {
                    _cachedRegex = new Regex(Argument, RegexOptions.IgnoreCase);
                }
                catch
                {
                    // Invalid regex, ignore or handle gracefully
                }
            }
            return _cachedRegex;
        }

        public HighlightRule() { }
    }
}