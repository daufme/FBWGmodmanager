using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FbwgModManager
{
    /// <summary>
    /// 安装/卸载：只做两件事 —— 写出 www/js/zzmod.js、在 index.html 插入带标记的一行。
    /// 移除后 index.html 与原始文件字节一致（前提：原始文件未被改动过）。
    /// </summary>
    public static class GameModder
    {
        public const string ModBasename = "zzmod.js";
        public const string MarkBegin = "<!-- FBWG-MOD -->";
        public const string MarkEnd = "<!-- /FBWG-MOD -->";

        public static readonly string MarkLine =
            MarkBegin + "<script src=\"js/" + ModBasename + "\"></script>" + MarkEnd;

        private static readonly Regex DataMainRx =
            new Regex(@"<script[^>]*data-main[^>]*>\s*</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BodyEndRx =
            new Regex("</body>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string WwwOf(string gamePath)
        {
            return Path.Combine(gamePath, "resources", "app", "www");
        }

        // ── 编码 / BOM 保留 ─────────────────────────────────────────────────
        private static string ReadTextPreserveBom(string path, out bool hasBom)
        {
            var bytes = File.ReadAllBytes(path);
            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var enc = new UTF8Encoding(false, false);        // 无 BOM，非法字节替换
            return enc.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }

        private static void WriteTextPreserveBom(string path, string text, bool hasBom)
        {
            var enc = new UTF8Encoding(hasBom, false);
            File.WriteAllText(path, text, enc);
        }

        // ── 插入自动加载行 ──────────────────────────────────────────────────
        public static List<string> InsertMarker(string www)
        {
            var log = new List<string>();
            var idx = Path.Combine(www, "index.html");
            if (!File.Exists(idx))
                throw new InvalidOperationException("找不到 " + idx);

            bool hasBom;
            var html = ReadTextPreserveBom(idx, out hasBom);

            if (html.IndexOf(MarkBegin, StringComparison.Ordinal) >= 0)
            {
                log.Add("自动加载行已存在，跳过插入");
                return log;
            }

            // 必须晚于 require.js（mod 依赖 window.requirejs）。放在 data-main 那个
            // script 标签之后即可 —— 此时 init.js 可能还没执行完，但 mod 内部会轮询等待。
            int pos;
            string where;
            var m = DataMainRx.Match(html);
            if (m.Success)
            {
                pos = m.Index + m.Length;
                where = "data-main 脚本标签之后";
            }
            else
            {
                var m2 = BodyEndRx.Match(html);
                pos = m2.Success ? m2.Index : html.Length;
                where = "</body> 之前";
            }

            // 以换行开头、不带结尾换行；卸载时连同这个换行一起删掉，做到字节级还原
            var ins = "\n" + new string(' ', 8) + MarkLine;
            WriteTextPreserveBom(idx, html.Substring(0, pos) + ins + html.Substring(pos), hasBom);
            log.Add("已在 index.html 插入自动加载行（" + where + "）");
            return log;
        }

        // ── 移除自动加载行 ──────────────────────────────────────────────────
        public static List<string> RemoveMarker(string www)
        {
            var log = new List<string>();
            var idx = Path.Combine(www, "index.html");
            if (!File.Exists(idx))
            {
                log.Add("找不到 index.html，跳过");
                return log;
            }

            bool hasBom;
            var html = ReadTextPreserveBom(idx, out hasBom);

            var pattern = @"\r?\n?[ \t]*" + Regex.Escape(MarkBegin) +
                          @".*?" + Regex.Escape(MarkEnd);
            var rx = new Regex(pattern, RegexOptions.Singleline | RegexOptions.Compiled);
            var matches = rx.Matches(html);
            if (matches.Count == 0)
            {
                log.Add("index.html 里没有自动加载行，无需移除");
                return log;
            }

            var newHtml = rx.Replace(html, "");
            WriteTextPreserveBom(idx, newHtml, hasBom);
            log.Add("已从 index.html 移除自动加载行（" + matches.Count + " 处）");
            return log;
        }

        // ── 安装 / 卸载 ─────────────────────────────────────────────────────
        public static List<string> Install(GameDef game, InstallInfo info)
        {
            var log = new List<string>();
            var www = WwwOf(info.Path);
            var jsDir = Path.Combine(www, "js");
            if (!Directory.Exists(jsDir))
                throw new InvalidOperationException("找不到目录：" + jsDir);

            var dst = Path.Combine(jsDir, ModBasename);
            var content = ModScript.MOD_JS.Replace("\r\n", "\n");
            File.WriteAllText(dst, content, new UTF8Encoding(false, false));
            log.Add("已写入 " + dst + "（" + new FileInfo(dst).Length + " 字节）");

            log.AddRange(InsertMarker(www));
            return log;
        }

        public static List<string> Uninstall(GameDef game, InstallInfo info)
        {
            var log = new List<string>();
            var www = WwwOf(info.Path);

            log.AddRange(RemoveMarker(www));

            var dst = Path.Combine(www, "js", ModBasename);
            if (File.Exists(dst))
            {
                File.Delete(dst);
                log.Add("已删除 " + dst);
            }
            else
            {
                log.Add(dst + " 不存在，跳过");
            }
            return log;
        }
    }
}