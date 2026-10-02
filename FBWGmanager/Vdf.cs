using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FbwgModManager
{
    /// <summary>
    /// 极简 Valve KeyValues 解析器（libraryfolders.vdf / appmanifest_*.acf 只用到
    /// 字符串与嵌套块）。移植自 Python 版的 parse_vdf()。
    /// </summary>
    public static class Vdf
    {
        private struct Token
        {
            public readonly string Quoted;
            public readonly string Brace;
            public Token(string q, string b) { Quoted = q; Brace = b; }
        }

        private static readonly Regex TokenRx =
            new Regex(@"""((?:[^""\\]|\\.)*)""|([{}])", RegexOptions.Compiled);

        public static Dictionary<string, object> Parse(string text)
        {
            var tokens = new List<Token>();
            foreach (Match m in TokenRx.Matches(text))
            {
                if (m.Groups[1].Success)
                    tokens.Add(new Token(m.Groups[1].Value, null));
                else
                    tokens.Add(new Token(null, m.Groups[2].Value));
            }

            int pos = 0;
            return ParseBlock(tokens, ref pos);
        }

        private static Dictionary<string, object> ParseBlock(List<Token> tokens, ref int pos)
        {
            var node = new Dictionary<string, object>();
            while (pos < tokens.Count)
            {
                var t = tokens[pos];

                if (t.Brace == "}") { pos++; return node; }
                if (t.Brace == "{") { pos++; continue; }

                var key = (t.Quoted ?? "").Replace("\\\\", "\\");
                pos++;
                if (pos >= tokens.Count) break;

                var next = tokens[pos];
                if (next.Brace == "{")
                {
                    pos++;
                    node[key] = ParseBlock(tokens, ref pos);
                }
                else
                {
                    pos++;
                    node[key] = (next.Quoted ?? "").Replace("\\\\", "\\");
                }
            }
            return node;
        }
    }
}