using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Switcheroo.Core.Highlighting;
using Switcheroo.Properties;

namespace Switcheroo.Highlighting
{
    public class HighlightService
    {
        private static readonly XmlSerializer _serializer = new XmlSerializer(typeof(List<HighlightRule>));
        
        private readonly HighlightMatcher _matcher = new HighlightMatcher();

        public List<HighlightRule> Rules { get; private set; }
        public event EventHandler RulesChanged;

        public HighlightService()
        {
            LoadRules();
        }

        private void LoadRules()
        {
            var data = Settings.Default.HighlightRules;
            if (string.IsNullOrEmpty(data))
            {
                Rules = new List<HighlightRule>();
                RefreshHookSnapshot(Rules);
                return;
            }

            try
            {
                using (var reader = new StringReader(data))
                {
                    Rules = (List<HighlightRule>)_serializer.Deserialize(reader);
                }
                RefreshHookSnapshot(Rules);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading highlight rules: " + ex.Message);
                Rules = new List<HighlightRule>();
                RefreshHookSnapshot(Rules);
            }
        }

        public void SaveRules()
        {
            try
            {
                using (var writer = new StringWriter())
                {
                    _serializer.Serialize(writer, Rules);
                    Settings.Default.HighlightRules = writer.ToString();
                    Settings.Default.Save();
                }
                RefreshHookSnapshot(Rules);
                RulesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error saving highlight rules: " + ex.Message);
            }
        }

        public HighlightRule FindMatch(string processTitle, string windowTitle, string className)
        {
            foreach (var rule in Rules)
            {
                if (_matcher.IsMatch(rule, processTitle, windowTitle, className))
                {
                    return rule;
                }
            }
            return null;
        }

        /// <summary>按动作查找命中的规则（Hide / PassThrough / Pin / SkipInList / Highlight）。</summary>
        public HighlightRule FindActionMatch(RuleAction action, string processTitle, string windowTitle, string className)
        {
            foreach (var rule in Rules)
            {
                if (_matcher.IsMatchAction(rule, action, processTitle, windowTitle, className))
                {
                    return rule;
                }
            }
            return null;
        }

        /// <summary>该窗口是否应被规则从列表里拿掉（Hide / SkipInList）。</summary>
        public bool ShouldHide(string processTitle, string windowTitle, string className)
        {
            return FindActionMatch(RuleAction.Hide, processTitle, windowTitle, className) != null
                   || FindActionMatch(RuleAction.SkipInList, processTitle, windowTitle, className) != null;
        }

        public bool IsRulePinned(string processTitle, string windowTitle, string className)
        {
            return FindActionMatch(RuleAction.Pin, processTitle, windowTitle, className) != null;
        }

        public bool IsPassThrough(string processTitle, string windowTitle, string className)
        {
            return FindActionMatch(RuleAction.PassThrough, processTitle, windowTitle, className) != null;
        }

        // ---- 供 LL 钩子使用的静态快照（钩子里不能碰设置/反序列化） ----
        private static List<HighlightRule> _passThroughRules = new List<HighlightRule>();
        private static readonly HighlightMatcher StaticMatcher = new HighlightMatcher();

        public static void RefreshHookSnapshot(List<HighlightRule> rules)
        {
            var list = new List<HighlightRule>();
            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r.Action == RuleAction.PassThrough && r.Enabled) list.Add(r);
                }
            }
            _passThroughRules = list;
        }

        /// <summary>钩子回调里判断当前前台窗口是否命中 PassThrough 规则（快照可为空）。</summary>
        public static bool StaticIsPassThrough(string processTitle, string windowTitle, string className)
        {
            var rules = _passThroughRules;
            if (rules == null || rules.Count == 0) return false;
            foreach (var r in rules)
            {
                if (StaticMatcher.IsMatchAction(r, RuleAction.PassThrough, processTitle, windowTitle, className)) return true;
            }
            return false;
        }

        public void AddRule(HighlightRule rule)
        {
            Rules.Add(rule);
            SaveRules();
        }

        public void RemoveRule(HighlightRule rule)
        {
            Rules.Remove(rule);
            SaveRules();
        }

        public void MoveRuleUp(HighlightRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index > 0)
            {
                Rules.RemoveAt(index);
                Rules.Insert(index - 1, rule);
                SaveRules();
            }
        }

        public void MoveRuleDown(HighlightRule rule)
        {
            int index = Rules.IndexOf(rule);
            if (index >= 0 && index < Rules.Count - 1)
            {
                Rules.RemoveAt(index);
                Rules.Insert(index + 1, rule);
                SaveRules();
            }
        }
    }
}
