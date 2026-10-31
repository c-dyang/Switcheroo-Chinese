using System;
using System.Text.RegularExpressions;

namespace Switcheroo.Core.Highlighting
{
    public class HighlightMatcher
    {
        public bool IsMatch(HighlightRule rule, string processTitle, string windowTitle, string className)
        {
            if (rule == null || string.IsNullOrEmpty(rule.Argument)) return false;
            if (!rule.Enabled) return false;

            switch (rule.MatchType)
            {
                case MatchType.ProcessName:
                    // Case-insensitive exact match
                    return string.Equals(processTitle, rule.Argument, StringComparison.OrdinalIgnoreCase);

                case MatchType.WindowClass:
                    return string.Equals(className, rule.Argument, StringComparison.OrdinalIgnoreCase);

                case MatchType.WindowTitleRegex:
                    var regex = rule.GetRegex();
                    if (regex != null)
                    {
                        return regex.IsMatch(windowTitle ?? "");
                    }
                    return false;
            }
            return false;
        }

        /// <summary>规则是否命中且动作等于指定动作。</summary>
        public bool IsMatchAction(HighlightRule rule, RuleAction action, string processTitle, string windowTitle, string className)
        {
            return rule != null && rule.Action == action && IsMatch(rule, processTitle, windowTitle, className);
        }
    }
}
