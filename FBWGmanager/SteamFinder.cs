using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;

namespace FbwgModManager
{
    public static class SteamFinder
    {
        public static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (p ?? "").ToLowerInvariant(); }
        }

        private static string CleanSteamPath(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim().Trim('"').Replace('/', '\\');
            if (v.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
                v = Path.GetDirectoryName(v);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        // ── 注册表 ───────────────────────────────────────────────────────────
        public static List<KeyValuePair<string, string>> RegistrySteamPaths()
        {
            var list = new List<KeyValuePair<string, string>>();
            AddReg(list, "HKCU SteamPath", Registry.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath");
            AddReg(list, "HKLM InstallPath(WOW64)", Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddReg(list, "HKLM InstallPath", Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            AddReg(list, "HKCU SteamExe", Registry.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamExe");
            return list;
        }

        private static void AddReg(List<KeyValuePair<string, string>> list,
                                   string label, RegistryKey hive, string subkey, string valueName)
        {
            try
            {
                using (var k = hive.OpenSubKey(subkey))
                {
                    if (k == null) return;
                    var p = CleanSteamPath(k.GetValue(valueName) as string);
                    if (!string.IsNullOrEmpty(p))
                        list.Add(new KeyValuePair<string, string>(label, p));
                }
            }
            catch { /* 忽略：与 Python 版一致 */ }
        }

        // ── 常见路径 + 全盘扫描 ─────────────────────────────────────────────
        public static List<KeyValuePair<string, string>> GuessedSteamPaths()
        {
            var list = new List<KeyValuePair<string, string>>();

            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432" })
            {
                var baseDir = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(baseDir))
                    list.Add(new KeyValuePair<string, string>(
                        "环境变量 " + env, Path.Combine(baseDir, "Steam")));
            }

            foreach (var drive in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            {
                var root = drive + ":\\";
                if (!Directory.Exists(root)) continue;
                foreach (var sub in new[]
                {
                    "Steam",
                    "SteamLibrary",
                    @"Program Files (x86)\Steam",
                    @"Program Files\Steam",
                    @"Games\Steam",
                })
                {
                    var full = Path.Combine(root, sub);
                    list.Add(new KeyValuePair<string, string>("扫描 " + full, full));
                }
            }
            return list;
        }

        // ── libraryfolders.vdf ───────────────────────────────────────────────
        public static List<string> LibraryFolders(string steamRoot)
        {
            var libs = new List<string> { steamRoot };
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return DedupeExisting(libs);

            Dictionary<string, object> tree;
            try { tree = Vdf.Parse(File.ReadAllText(vdf)); }
            catch { return DedupeExisting(libs); }

            object lfObj;
            if (!tree.TryGetValue("libraryfolders", out lfObj))
                tree.TryGetValue("LibraryFolders", out lfObj);

            var lf = lfObj as Dictionary<string, object>;
            if (lf != null)
            {
                foreach (var kv in lf)
                {
                    string p = null;
                    var dict = kv.Value as Dictionary<string, object>;
                    if (dict != null)
                    {
                        object pathObj;
                        if (dict.TryGetValue("path", out pathObj) ||
                            dict.TryGetValue("Path", out pathObj))
                            p = pathObj?.ToString();
                    }
                    else if (kv.Value is string)
                    {
                        p = (string)kv.Value;      // 老格式：编号直接对应路径
                    }

                    if (!string.IsNullOrEmpty(p))
                    {
                        try { libs.Add(Path.GetFullPath(p.Replace('/', '\\'))); }
                        catch { /* 忽略非法路径 */ }
                    }
                }
            }
            return DedupeExisting(libs);
        }

        private static List<string> DedupeExisting(IEnumerable<string> paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
                if (seen.Add(Norm(p))) result.Add(p);
            }
            return result;
        }

        // ── 顶层探测 ─────────────────────────────────────────────────────────
        public sealed class DiscoverResult
        {
            public List<string> Roots;
            public List<string> Libraries;
            public List<string> Notes;
        }

        public static DiscoverResult Discover()
        {
            var r = new DiscoverResult
            {
                Roots = new List<string>(),
                Libraries = new List<string>(),
                Notes = new List<string>(),
            };

            var candidates = new List<KeyValuePair<string, string>>();
            candidates.AddRange(RegistrySteamPaths());
            candidates.AddRange(GuessedSteamPaths());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in candidates)
            {
                var p = kv.Value;
                if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
                if (!Directory.Exists(Path.Combine(p, "steamapps"))) continue;
                if (!seen.Add(Norm(p))) continue;
                r.Roots.Add(p);
                r.Notes.Add(kv.Key + " → " + p);
            }
            if (r.Roots.Count == 0)
                r.Notes.Add("未找到任何 Steam 安装（注册表与常见路径都没命中）");

            var lseen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in r.Roots)
                foreach (var lib in LibraryFolders(root))
                    if (lseen.Add(Norm(lib)))
                        r.Libraries.Add(lib);

            return r;
        }

        // ── 找出安装了哪几作 ─────────────────────────────────────────────────
        public static Dictionary<string, InstallInfo> FindInstalled(List<string> libraries)
        {
            var found = new Dictionary<string, InstallInfo>(StringComparer.OrdinalIgnoreCase);

            var byFolder = new Dictionary<string, GameDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in GameCatalog.Games)
                byFolder[g.Folder.ToLowerInvariant()] = g;

            foreach (var lib in libraries)
            {
                var sa = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(sa)) continue;

                string[] acfs;
                try { acfs = Directory.GetFiles(sa, "appmanifest_*.acf"); }
                catch { acfs = new string[0]; }

                foreach (var fn in acfs)
                {
                    try
                    {
                        var tree = Vdf.Parse(File.ReadAllText(fn));

                        object appObj;
                        if (!tree.TryGetValue("AppState", out appObj)) continue;
                        var app = appObj as Dictionary<string, object>;
                        if (app == null) continue;

                        object dirObj;
                        var installdir =
                            (app.TryGetValue("installdir", out dirObj) ? dirObj?.ToString() : "")
                            ?.Trim() ?? "";
                        if (installdir.Length == 0) continue;

                        GameDef g;
                        if (!byFolder.TryGetValue(installdir.ToLowerInvariant(), out g)) continue;
                        if (found.ContainsKey(g.Key)) continue;

                        var path = Path.Combine(sa, "common", installdir);
                        if (!Directory.Exists(path)) continue;

                        object appIdObj;
                        app.TryGetValue("appid", out appIdObj);

                        found[g.Key] = new InstallInfo
                        {
                            Path = path,
                            AppId = appIdObj?.ToString(),
                            Lib = lib,
                            Source = "appmanifest",
                        };
                    }
                    catch { /* 与 Python 版一致，忽略单个 ACF 解析失败 */ }
                }

                // 兜底：直接在 common/ 下按目录名找
                var common = Path.Combine(sa, "common");
                if (Directory.Exists(common))
                {
                    foreach (var g in GameCatalog.Games)
                    {
                        if (found.ContainsKey(g.Key)) continue;
                        var p = Path.Combine(common, g.Folder);
                        if (Directory.Exists(p))
                            found[g.Key] = new InstallInfo
                            {
                                Path = p,
                                AppId = null,
                                Lib = lib,
                                Source = "目录扫描"
                            };
                    }
                }
            }
            return found;
        }
    }
}