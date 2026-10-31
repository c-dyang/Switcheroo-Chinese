using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Switcheroo
{
    /// <summary>
    /// 极简 JSON：只服务于"配置导出/导入"——对象只含字符串/布尔/数字/空值，
    /// 不处理数组与嵌套（StringCollection 在导出时被拼成单字符串）。
    /// 之所以不引第三方库：这是 net48 老工程，多一个依赖就多一份安装风险。
    /// </summary>
    internal static class SimpleJson
    {
        public static string Serialize(IDictionary<string, object> dict)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Quote(kv.Key)).Append(':');
                var v = kv.Value;
                if (v == null) sb.Append("null");
                else if (v is bool) sb.Append(((bool) v) ? "true" : "false");
                else if (v is int || v is long) sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
                else if (v is double) sb.Append(((double) v).ToString("R", CultureInfo.InvariantCulture));
                else sb.Append(Quote(v.ToString()));
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static Dictionary<string, object> DeserializeObject(string json)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return result;

            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') throw new FormatException("不是 JSON 对象");
            i++;
            while (true)
            {
                SkipWs(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWs(json, ref i);

                object value;
                if (i < json.Length && json[i] == '"')
                {
                    value = ReadString(json, ref i);
                }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i])) i++;
                    var token = json.Substring(start, i - start);
                    if (token == "true") value = true;
                    else if (token == "false") value = false;
                    else if (token == "null") value = null;
                    else
                    {
                        long l;
                        double d;
                        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) value = l;
                        else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) value = d;
                        else value = token;
                    }
                }
                result[key] = value;
            }
            return result;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("期望字符串");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                sb.Append((char) Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int) c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
