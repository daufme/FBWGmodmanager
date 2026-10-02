using System.Collections.Generic;

namespace FbwgModManager
{
    public sealed class GameDef
    {
        public string Key;
        public string Name;
        public string Folder;
        public string Exe;
        public string Gid;
    }

    public sealed class InstallInfo
    {
        public string Path;
        public string AppId;
        public string Lib;
        public string Source;
    }

    public static class GameCatalog
    {
        public static readonly IReadOnlyList<GameDef> Games = new List<GameDef>
        {
            new GameDef { Key = "forest",     Name = "1  Forest Temple",  Folder = "Fireboy & Watergirl 1 The Forest Temple",  Exe = "FbwgForest.exe",  Gid = "forest" },
            new GameDef { Key = "light",      Name = "2  Light Temple",   Folder = "Fireboy & Watergirl 2 The Light Temple",   Exe = "FbwgLight.exe",   Gid = "light" },
            new GameDef { Key = "ice",        Name = "3  Ice Temple",     Folder = "Fireboy & Watergirl 3 The Ice Temple",     Exe = "FbwgIce.exe",     Gid = "ice" },
            new GameDef { Key = "crystal",    Name = "4  Crystal Temple", Folder = "Fireboy & Watergirl 4 The Crystal Temple", Exe = "FbwgCrystal.exe", Gid = "crystal" },
            new GameDef { Key = "friends",    Name = "and Friends",       Folder = "Fireboy & Watergirl and Friends",          Exe = "FbwgFriends.exe", Gid = "friends" },
            new GameDef { Key = "elements",   Name = "Elements",          Folder = "Fireboy & Watergirl Elements",             Exe = "Fbwg.exe",        Gid = "elements" },
            new GameDef { Key = "fairytales", Name = "Fairy Tales",       Folder = "Fireboy & Watergirl Fairy Tales",          Exe = "Fbwg.exe",        Gid = "fairytales" },
        };

        public static GameDef Get(string key)
        {
            foreach (var g in Games)
                if (g.Key == key) return g;
            return null;
        }
    }
}