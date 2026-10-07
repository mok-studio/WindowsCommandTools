// ---------------------------------------------------------------------------
//  Lang.cs — 界面语言
//
//  三种取值：
//    auto  跟随系统
//    zh    简体中文
//    en    English
//
//  实现方式：**中文原文当键**。界面代码里直接写 Loc.T("设置")，
//  英文模式下查表换成 "Settings"，中文模式下原样返回。
//  查不到就回退成中文原文 —— 漏翻最多显示中文，不会显示空白或乱码。
//
//  翻译表在 LangTable.cs（由 tools/build_lang.py 从 build/en/*.json 生成）。
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WindowsCommandTools
{
    public enum Lang { Auto = 0, Zh = 1, En = 2 }

    internal static class Loc
    {
        private static Lang _setting = Lang.Auto;
        private static bool _resolvedEn;

        /// <summary>当前设置（auto / zh / en）。</summary>
        public static Lang Setting
        {
            get { return _setting; }
        }

        /// <summary>实际生效的语言（auto 已解析成 zh 或 en）。</summary>
        public static Lang Effective
        {
            get
            {
                if (_setting != Lang.Auto) return _setting;
                return SystemPrefersChinese() ? Lang.Zh : Lang.En;
            }
        }

        public static bool IsEnglish
        {
            get { return Effective == Lang.En; }
        }

        /// <summary>系统界面语言是不是中文。auto 模式据此决定。</summary>
        private static bool SystemPrefersChinese()
        {
            try
            {
                CultureInfo c = CultureInfo.CurrentUICulture;
                string two = c.TwoLetterISOLanguageName;
                if (string.Equals(two, "zh", StringComparison.OrdinalIgnoreCase)) return true;
                // 某些精简系统上 TwoLetterISOLanguageName 不准，再看 LCID
                int lcid = c.LCID & 0x3FF;
                return lcid == 0x04;   // 中文的 LCID 主语言号
            }
            catch { return true; }
        }

        /// <summary>从设置字符串解析。</summary>
        public static Lang Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return Lang.Auto;
            if (s.Equals("zh", StringComparison.OrdinalIgnoreCase)) return Lang.Zh;
            if (s.Equals("en", StringComparison.OrdinalIgnoreCase)) return Lang.En;
            return Lang.Auto;
        }

        public static string Id(Lang l)
        {
            if (l == Lang.Zh) return "zh";
            if (l == Lang.En) return "en";
            return "auto";
        }

        /// <summary>应用一个语言设置（不落盘，落盘由调用方负责）。</summary>
        public static void Set(Lang l)
        {
            _setting = l;
            _resolvedEn = IsEnglish;
        }

        public static void Apply(string s)
        {
            Set(Parse(s));
        }

        // ==================================================================
        //  翻译
        // ==================================================================

        /// <summary>把中文界面文案翻成当前语言。翻不到就原样返回。</summary>
        public static string T(string zh)
        {
            if (zh == null) return "";
            if (!IsEnglish) return zh;
            string en;
            if (LangTable.En.TryGetValue(zh, out en) && !string.IsNullOrEmpty(en)) return en;
            return zh;
        }

        /// <summary>先翻译再格式化：Loc.T("共 {0} 条", n)。</summary>
        public static string T(string zh, params object[] args)
        {
            string s = T(zh);
            if (args == null || args.Length == 0) return s;
            try { return string.Format(CultureInfo.InvariantCulture, s, args); }
            catch { return s; }
        }
    }
}
