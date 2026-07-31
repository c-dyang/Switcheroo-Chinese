/*
 * Switcheroo - The incremental-search task switcher for Windows.
 * PinyinHelper - 中文标题拼音转换（用于拼音搜索/筛选）
 *
 * 基于 Microsoft.International.Converters.PinYinConverter（微软官方，net20 兼容）。
 * 带静态缓存：同一汉字只转换一次，避免窗口列表枚举时反复转换拖慢。
 * 注意：该库不处理多音字，取第一个读音（常见场景可接受）。
 */

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace Switcheroo
{
    public static class PinyinHelper
    {
        private static readonly Dictionary<char, string> Cache = new Dictionary<char, string>();
        private static readonly object LockObj = new object();

        /// <summary>
        /// 全拼（小写，无声调）。非汉字：字母数字保留，其他字符跳过。
        /// 例："哔哩哔哩" → "bilibili"
        /// </summary>
        public static string GetFull(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                sb.Append(GetCharPinyin(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 首字母（小写）。非汉字：字母数字保留，其他字符跳过。
        /// 例："哔哩哔哩" → "blbl"
        /// </summary>
        public static string GetInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                var py = GetCharPinyin(c);
                if (py.Length > 0)
                {
                    sb.Append(py[0]);
                }
                else if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 单个字符的拼音（小写，无声调）；非汉字返回空串或自身（ASCII 字母数字）。
        /// 注意：汉字（如"哔"）的 char.IsLetterOrDigit 为 true，必须先判汉字再判 ASCII，
        /// 否则汉字会被 ASCII 分支拦下返回空串、永远轮不到 PinYinConverter 转换。
        /// </summary>
        private static string GetCharPinyin(char c)
        {
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                // 汉字：查拼音缓存
                lock (LockObj)
                {
                    if (Cache.TryGetValue(c, out var cached)) return cached;
                }

                string result = "";
                try
                {
                    var cc = new ChineseChar(c);
                    if (cc.Pinyins != null)
                    {
                        foreach (var p in cc.Pinyins)
                        {
                            if (!string.IsNullOrEmpty(p))
                            {
                                foreach (var ch in p)
                                {
                                    if (char.IsLetter(ch)) result += char.ToLowerInvariant(ch);
                                }
                                break; // 取第一个读音（多音字简化）
                            }
                        }
                    }
                }
                catch
                {
                    result = ""; // 转换失败按无拼音处理
                }

                lock (LockObj)
                {
                    if (!Cache.ContainsKey(c)) Cache[c] = result;
                }
                return result;
            }

            // 非汉字：ASCII 字母数字保留（小写），其他字符跳过
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                return char.ToLowerInvariant(c).ToString();
            }
            return "";
        }
    }
}
