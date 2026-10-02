using System;

namespace FbwgModManager
{
    public static class CliScan
    {
        public static int Run()
        {
            Console.WriteLine(new string('=', 96));
            Console.WriteLine("Steam 定位");

            var d = SteamFinder.Discover();
            foreach (var n in d.Notes) Console.WriteLine("  " + n);
            Console.WriteLine("  Steam 根：{0} 个   游戏库：{1} 个",
                              d.Roots.Count, d.Libraries.Count);
            foreach (var l in d.Libraries) Console.WriteLine("      " + l);

            var found = SteamFinder.FindInstalled(d.Libraries);

            Console.WriteLine();
            Console.WriteLine("游戏扫描：找到 {0} / 7 作", found.Count);
            Console.WriteLine();
            Console.WriteLine("  {0,-13} {1,-9} {2}", "key", "appid", "安装路径");
            foreach (var g in GameCatalog.Games)
            {
                InstallInfo info;
                if (!found.TryGetValue(g.Key, out info))
                    Console.WriteLine("  {0,-13} {1,-9} {2}", g.Key, "-", "未安装");
                else
                    Console.WriteLine("  {0,-13} {1,-9} {2}", g.Key, info.AppId ?? "-", info.Path);
            }

            Console.WriteLine();
            Console.WriteLine("内置 mod 代码：{0} 字符（七个游戏共用，不需要外部文件）",
                              ModScript.MOD_JS.Length);
            return 0;
        }
    }
}