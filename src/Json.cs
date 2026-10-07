// ---------------------------------------------------------------------------
//  Json.cs — 零依赖的极简 JSON 解析器 / 序列化器
//  为什么不用 Newtonsoft.Json：本工具要编译成单个 exe，不引入任何第三方依赖，
//  也不需要目标机器安装任何运行时包。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsCommandTools
{
    public static class Json
    {
        // ---------------- 解析 ----------------

        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length)
                throw new FormatException("JSON 末尾存在多余字符（位置 " + i + "）");
            return v;
        }

        public static object ParseFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(DecodeText(bytes));
        }

        /// <summary>把字节流解码成字符串：优先 UTF-8（带/不带 BOM），失败则退回 GBK。</summary>
        public static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException) { }
            try { return Encoding.GetEncoding(936).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                // 容忍 BOM
                if (c == '\uFEFF') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 非法字面量，位置 " + i);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new FormatException("JSON 对象的键必须是字符串，位置 " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new FormatException("JSON 缺少冒号，位置 " + i);
                i++;
                object val = ParseValue(s, ref i);
                map[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException("JSON 对象中出现意外字符 '" + s[i] + "'，位置 " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 数组中出现意外字符 '" + s[i] + "'，位置 " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JSON 字符串未闭合");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("JSON 转义未结束");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整");
                        string hex = s.Substring(i, 4);
                        sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("JSON 未知转义 \\" + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("JSON 非法值，位置 " + start);
            string num = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON 数字格式错误：" + num);
            return d;
        }

        // ---------------- 便捷取值 ----------------

        public static Dictionary<string, object> AsObj(object o) { return o as Dictionary<string, object>; }
        public static List<object> AsArr(object o) { return o as List<object>; }

        public static object Get(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            return o.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> o, string key, string def)
        {
            object v = Get(o, key);
            if (v == null) return def;
            string s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(Dictionary<string, object> o, string key, bool def)
        {
            object v = Get(o, key);
            if (v == null) return def;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null)
            {
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return def;
        }

        public static int Int(Dictionary<string, object> o, string key, int def)
        {
            object v = Get(o, key);
            if (v == null) return def;
            if (v is double) return (int)Math.Round((double)v);
            if (v is string)
            {
                double d;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return (int)Math.Round(d);
            }
            return def;
        }

        public static double Dbl(Dictionary<string, object> o, string key, double def)
        {
            object v = Get(o, key);
            if (v == null) return def;
            if (v is double) return (double)v;
            if (v is string)
            {
                double d;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return def;
        }

        public static List<string> StrList(Dictionary<string, object> o, string key)
        {
            List<string> result = new List<string>();
            List<object> arr = AsArr(Get(o, key));
            if (arr == null) return result;
            foreach (object item in arr)
            {
                if (item == null) continue;
                string s = item as string;
                result.Add(s != null ? s : Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            return result;
        }

        // ---------------- 序列化 ----------------

        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        public static void WriteToFile(string path, object value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Write(value), new UTF8Encoding(false));
        }

        private static void Indent(StringBuilder sb, int level)
        {
            sb.Append('\n');
            for (int k = 0; k < level; k++) sb.Append("  ");
        }

        private static void WriteValue(StringBuilder sb, object v, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is int) { sb.Append(((int)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is long) { sb.Append(((long)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                    sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }

            Dictionary<string, object> map = v as Dictionary<string, object>;
            if (map != null)
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, level + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    WriteValue(sb, kv.Value, level + 1);
                }
                Indent(sb, level);
                sb.Append('}');
                return;
            }

            System.Collections.IEnumerable seq = v as System.Collections.IEnumerable;
            if (seq != null)
            {
                List<object> items = new List<object>();
                foreach (object item in seq) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                bool firstItem = true;
                foreach (object item in items)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Indent(sb, level + 1);
                    WriteValue(sb, item, level + 1);
                }
                Indent(sb, level);
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 构造辅助 ----------------

        public static Dictionary<string, object> NewObj() { return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }

        public static Dictionary<string, object> Put(Dictionary<string, object> o, string key, object value)
        {
            o[key] = value;
            return o;
        }
    }
}
