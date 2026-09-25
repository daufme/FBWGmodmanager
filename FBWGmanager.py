#!/usr/bin/env python3
import os
import queue
import re
import string
import sys
import threading
import time
import tkinter as tk
from tkinter import ttk, scrolledtext, messagebox

#  mod 代码内置在此
MOD_JS = r'''/* 
 *  Fireboy & Watergirl 系列通用控制台 MOD
 *  适用：Fairy Tales / Elements / and Friends / 1 Forest / 2 Light / 3 Ice / 4 Crystal
 *
 *    invincible   角色无敌
 *    timer        不超时（结算时间必定达标 + 计时到目标就停住）
 *    gems         无视宝石数要求，必定判定收集满
 *
 *  1) 管理器自动加载（推荐）
 *     用 FBWGmanager.py 安装后，启动游戏即自动生效；控制台只用来临时改开关。
 *  2) 控制台直接装载
 *     启动游戏 → Ctrl+Shift+F12 打开 DevTools → Console → 把本文件整个粘进去 → 回车。
 *     在菜单里粘贴也可以：脚本会轮询等待关卡出现，进入关卡时自动接管，换关也会重新接管。
 *     改动只存在于内存，关掉游戏即完全恢复。
 *
 * 命令
 *    MOD.info()                 当前游戏 ID、是否已接管、判定值 vs 真实值
 *    MOD.toggles()              查看三项开关
 *    MOD.all() / MOD.none()     一键全开 / 全关
 *    MOD.on('timer') / MOD.off('gems')
 *    MOD.uninstall()            还原本会话的全部改动（含判定访问器与轮询）
 */

(function boot() {
  'use strict';

  var MAIN = ['invincible', 'timer', 'gems'];
  var ALL = MAIN.slice();

  if (window.__MOD_FBWG__) {
    console.warn('[mod] 已安装，重复粘贴已忽略。MOD.toggles() / MOD.all() / MOD.info() 可用。');
    if (window.MOD && window.MOD.toggles) window.MOD.toggles();
    return;
  }

  /* 运行时开关：读取方每次调用都会重新查，所以切换即时生效 */
  var CFG = { invincible: true, timer: true, gems: true };

  function say() { console.warn.apply(console, ['[mod]'].concat([].slice.call(arguments))); }
  var out = say;

  /* ── 拿 Phaser 与游戏实例（不依赖具体模块名） ── */
  function findPhaser() {
    try { if (window.requirejs && requirejs.defined && requirejs.defined('Phaser')) return requirejs('Phaser'); } catch (e) {}
    try {
      var reg = requirejs.s.contexts._.defined;
      for (var k in reg) { var v = reg[k]; if (v && v.GAMES && v.GAMES.length) return v; }
    } catch (e) {}
    return window.Phaser || null;
  }

  /* 作为 <script> 被自动加载时，本脚本可能在 init.js（bundle）执行完之前就运行，
     那时 Phaser 模块与游戏实例都还不存在 —— 所以轮询等待，而不是直接放弃。 */
  var Phaser = findPhaser();
  if (!Phaser || !Phaser.GAMES || !Phaser.GAMES[0]) {
    boot.tries = (boot.tries || 0) + 1;
    if (boot.tries > 600) {                    // 约 60 秒
      console.warn('[mod] 等待游戏就绪超时（60s）已放弃。请确认本文件被加载在游戏页面内。');
      return;
    }
    setTimeout(boot, 100);
    return;
  }
  var G = Phaser.GAMES[0];

  var undo = [];
  function remember(fn) { undo.push(fn); }

  /* 沿原型链找「真正拥有该方法」的原型（跨子类生效的关键） */
  function protoOwning(inst, name) {
    if (!inst) return null;
    var p = Object.getPrototypeOf(inst);
    while (p) {
      if (Object.prototype.hasOwnProperty.call(p, name)) return p;
      p = Object.getPrototypeOf(p);
    }
    return null;
  }

  /* 替换 target[name]；幂等（已打过补丁就跳过） */
  function patchSlot(target, name, make) {
    if (!target || typeof target[name] !== 'function') return false;
    var orig = target[name];
    if (orig.__modPatched) return false;
    var neo = make(orig);
    neo.__modPatched = true;
    target[name] = neo;
    remember(function () { target[name] = orig; });
    return true;
  }

  /* ── 目标时间：与各作 ProgressModel.update 里的表达式一致 ── */
  function targetTime(levelData) {
    if (!levelData) return null;
    var t = (G.settings && G.settings.controls === 'single' && levelData.mobileTime) || levelData.time;
    return (typeof t === 'number' && t > 0) ? t : null;
  }

  /* ── 1) 无敌 ── */
  function hardenMake(orig) {
    return function () {
      if (CFG.invincible) return false;         // 液体致死 / 撞墙 / 出界 全部短路
      return orig.apply(this, arguments);
    };
  }
  function hardenInstance(ch) {
    if (!ch) return;
    ['liquidTouched', 'kill', '_doKill'].forEach(function (k) {
      if (typeof ch[k] === 'function') patchSlot(ch, k, hardenMake);
    });
  }

  /* ── 2) 计时：到目标时间就停住 ── */
  function clockMake(orig) {
    return function () {
      var t = orig.apply(this, arguments);
      if (!CFG.timer) return t;
      if (!isFinite(t) || t < 0) return 0;
      var lv = this.game && this.game.level;
      var target = targetTime(lv && lv.levelData);
      return target ? Math.min(t, target) : t;
    };
  }

  /* ── 3) 结算判定：装访问器，把真实值中立化 ──
   * 访问器总是装上，由 CFG 在【读取时】决定是否强制 —— 开关因此是即时的。
   * 必须 enumerable：ProgressModel 会把 levelState 直接赋给 data.best 并 JSON 序列化。
   * 内部标记走 WeakSet，绝不在 levelState 上挂属性（否则会被写进存档）。
   */
  var pinnedStates = new WeakSet();

  function pinLevelState(level) {
    var ls = level && level.levelState;
    if (!ls || pinnedStates.has(ls)) return;
    pinnedStates.add(ls);

    function pin(key, flag, forced) {
      var had = Object.prototype.hasOwnProperty.call(ls, key);
      var desc = null;
      try { if (had) desc = Object.getOwnPropertyDescriptor(ls, key); } catch (e) {}
      var store = ls[key];
      try {
        Object.defineProperty(ls, key, {
          configurable: true,
          enumerable: true,
          get: function () { return CFG[flag] ? forced(store, level) : store; },
          set: function (v) { store = v; }
        });
      } catch (e) { out('pin 失败:', key, e && e.message); return; }
      remember(function () {
        try {
          // 先摘掉访问器，再以【当前最新值】写回普通数据属性 ——
          // 不能还原 pin 时刻的描述符，那会丢掉引擎之后写入的真实值
          delete ls[key];
          if (had && desc) {
            Object.defineProperty(ls, key, {
              value: store,
              writable: desc.writable !== false,
              enumerable: desc.enumerable !== false,
              configurable: desc.configurable !== false
            });
          } else {
            ls[key] = store;
          }
          pinnedStates.delete(ls);
        } catch (e) {}
      });
    }

    // 各作星级规则都只比较 state.diamonds >= data.totalDiamonds
    pin('diamonds', 'gems', function (v, lv) {
      var t = lv && lv.totalDiamonds;
      return (typeof t === 'number' && isFinite(t)) ? t : 999999;
    });
    // puzzle 关的第三项要求 silverDiamond > 0
    pin('silverDiamond', 'gems', function (v) {
      return Math.max((typeof v === 'number' && isFinite(v)) ? v : 0, 1);
    });
    // dark 关与普通关的时间项要求 time <= 目标时间
    pin('time', 'timer', function (v, lv) {
      var t = targetTime(lv && lv.levelData);
      if (typeof v !== 'number' || !isFinite(v)) return t || 0;
      return t ? Math.min(v, t) : v;
    });
  }

  function finishMake(orig) {
    return function () {
      try { pinLevelState(this); } catch (e) { out('pin 出错:', e && e.message); }
      return orig.apply(this, arguments);
    };
  }

  /* ── 接管：从当前关卡实例反推一切 ── */
  function attach() {
    var L = G.level;
    if (!L || !L.levelState) return false;

    // 角色：先处理当前实例（被 bind 成自有属性），再处理「拥有 liquidTouched 的原型」，
    // 这样 Friends 系列的各种角色子类会一起生效
    var chars = [];
    if (L.pers1) chars.push(L.pers1);
    if (L.pers2) chars.push(L.pers2);
    if (L.friends && L.friends.length) chars = chars.concat(L.friends);
    chars.forEach(hardenInstance);
    var charProto = chars.length ? protoOwning(chars[0], 'liquidTouched') : null;
    if (charProto) {
      ['liquidTouched', 'kill', '_doKill'].forEach(function (k) {
        if (typeof charProto[k] === 'function') patchSlot(charProto, k, hardenMake);
      });
    }

    // 时钟
    var clock = L.ui && L.ui.clock;
    if (clock) {
      patchSlot(clock, 'getElapsedSeconds', clockMake);
      var clockProto = protoOwning(clock, 'getElapsedSeconds');
      if (clockProto) patchSlot(clockProto, 'getElapsedSeconds', clockMake);
    }

    // 结算
    patchSlot(L, 'gameFinish', finishMake);
    var levelProto = protoOwning(L, 'gameFinish');
    if (levelProto) patchSlot(levelProto, 'gameFinish', finishMake);

    pinLevelState(L);
    return true;
  }

  var attachedOnce = false;
  function tick() {
    if (G.level && attach()) {
      if (!attachedOnce) {
        attachedOnce = true;
        say('已接管关卡（游戏 ID：' + ((G.gameConfig && G.gameConfig.id) || '?') + '）');
        window.MOD.info();
      }
    }
  }

  attach();
  var timer = setInterval(tick, 800);            // 轮询：在菜单粘贴也能等到关卡出现；换关自动重接管
  remember(function () { clearInterval(timer); });

  /* ── 对外接口 ── */
  function setFeature(key, value) {
    if (ALL.indexOf(key) < 0) {
      say('未知功能：' + key + '（可用：' + ALL.join(' / ') + '）');
      return false;
    }
    CFG[key] = !!value;
    say((CFG[key] ? '✔ 已开启 ' : '✘ 已关闭 ') + key);
    return true;
  }

  window.MOD = {
    cfg: CFG,
    game: function () { return G; },
    level: function () { return G.level; },

    on: function (k) { return setFeature(k, true); },
    off: function (k) { return setFeature(k, false); },
    set: function (k, v) { return setFeature(k, v); },
    all: function () { MAIN.forEach(function (k) { CFG[k] = true; }); say('一键开启三项：' + MAIN.join(' / ')); },
    none: function () { MAIN.forEach(function (k) { CFG[k] = false; }); say('已关闭三项修改'); },

    toggles: function () {
      say('功能开关：\n' + ALL.map(function (k) {
        return '  ' + (CFG[k] ? '[开]' : '[关]') + '  ' + k;
      }).join('\n') + '\n  MOD.all() 全开 / MOD.none() 全关 / MOD.off("键") 单独关');
    },

    info: function () {
      var gc = G.gameConfig || {};
      var L = G.level;
      var lines = [
        '游戏 ID   : ' + (gc.id || '?') + '   (type=' + (gc.type || '?') + ')',
        '页面      : ' + ((typeof location !== 'undefined' && location.href)
                            ? location.href.split('/').slice(-3).join('/') : '?'),
        '已接管    : ' + (pinnedStates.has(L && L.levelState) ? '是（本关）' : '否')
      ];
      if (L) {
        var d = L.levelData || {};
        var p1 = L.pers1 && L.pers1.data, p2 = L.pers2 && L.pers2.data;
        var realGems = (p1 ? p1.diamonds : 0) + (p2 ? p2.diamonds : 0);
        lines.push('关卡      : id=' + d.id + ' type=' + d.type + ' 目标=' + targetTime(d) + 's');
        lines.push('显示用时  : ' + (L.ui && L.ui.clock ? L.ui.clock.getElapsedSeconds() : '?'));
        lines.push('真实宝石  : ' + realGems + '/' + L.totalDiamonds);
        lines.push('判定宝石  : ' + (L.levelState ? L.levelState.diamonds : '?') +
                   '   判定银钻: ' + (L.levelState ? L.levelState.silverDiamond : '?') +
                   '   判定时间: ' + (L.levelState ? L.levelState.time : '?'));
        lines.push('存活      : ' + !((L.pers1 && L.pers1.isDead) || (L.pers2 && L.pers2.isDead)));
      } else {
        lines.push('关卡      : 不在关卡内（进入关卡时自动接管）');
      }
      say(lines.join('\n'));
    },

    uninstall: function () {
      while (undo.length) { try { undo.pop()(); } catch (e) {} }
      delete window.__MOD_FBWG__;
      delete window.MOD;
      say('已卸载：全部改动已还原（含判定访问器与轮询）');
    }
  };
  window.__MOD_FBWG__ = true;

  say('安装完成。游戏 ID：' + ((G.gameConfig && G.gameConfig.id) || '?') +
      (G.level ? '' : '（当前在菜单，进入关卡时自动接管）'));
  window.MOD.toggles();
})();
'''

MOD_BASENAME = "zzmod.js"                       # 放进 www/js/ 的文件名
MARK_BEGIN = "<!-- FBWG-MOD -->"
MARK_END = "<!-- /FBWG-MOD -->"
MARK_LINE = '%s<script src="js/%s"></script>%s' % (MARK_BEGIN, MOD_BASENAME, MARK_END)

GAMES = [
    dict(key="forest",     name="1  Forest Temple",  folder="Fireboy & Watergirl 1 The Forest Temple",  exe="FbwgForest.exe",  gid="forest"),
    dict(key="light",      name="2  Light Temple",   folder="Fireboy & Watergirl 2 The Light Temple",   exe="FbwgLight.exe",   gid="light"),
    dict(key="ice",        name="3  Ice Temple",     folder="Fireboy & Watergirl 3 The Ice Temple",     exe="FbwgIce.exe",     gid="ice"),
    dict(key="crystal",    name="4  Crystal Temple", folder="Fireboy & Watergirl 4 The Crystal Temple", exe="FbwgCrystal.exe", gid="crystal"),
    dict(key="friends",    name="and Friends",       folder="Fireboy & Watergirl and Friends",          exe="FbwgFriends.exe", gid="friends"),
    dict(key="elements",   name="Elements",          folder="Fireboy & Watergirl Elements",             exe="Fbwg.exe",        gid="elements"),
    dict(key="fairytales", name="Fairy Tales",       folder="Fireboy & Watergirl Fairy Tales",          exe="Fbwg.exe",        gid="fairytales"),
]

# ══════════════════════════ Steam / 游戏定位 ══════════════════════════

def _norm(p):
    return os.path.normcase(os.path.normpath(p))


def _clean_steam_path(v):
    if not v:
        return None
    v = str(v).strip().strip('"').replace("/", "\\")
    if v.lower().endswith("steam.exe"):
        v = os.path.dirname(v)
    return v or None


def registry_steam_paths():
    out = []
    if os.name != "nt":
        return out
    try:
        import winreg
    except ImportError:
        return out
    specs = [
        ("HKCU SteamPath", winreg.HKEY_CURRENT_USER, r"SOFTWARE\Valve\Steam", "SteamPath"),
        ("HKLM InstallPath(WOW64)", winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        ("HKLM InstallPath", winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
        ("HKCU SteamExe", winreg.HKEY_CURRENT_USER, r"SOFTWARE\Valve\Steam", "SteamExe"),
    ]
    for label, hive, key, name in specs:
        try:
            with winreg.OpenKey(hive, key) as k:
                v, _ = winreg.QueryValueEx(k, name)
            p = _clean_steam_path(v)
            if p:
                out.append((label, p))
        except OSError:
            pass
    return out


def guessed_steam_paths():
    cands = []
    for env in ("ProgramFiles(x86)", "ProgramFiles", "ProgramW6432"):
        base = os.environ.get(env)
        if base:
            cands.append(os.path.join(base, "Steam"))
    for drive in string.ascii_uppercase:
        root = "%s:\\" % drive
        if not os.path.isdir(root):
            continue
        for sub in ("Steam", "SteamLibrary", r"Program Files (x86)\Steam",
                    r"Program Files\Steam", r"Games\Steam"):
            cands.append(os.path.join(root, sub))
    return [("扫描 %s" % c, c) for c in cands]


def parse_vdf(text):
    """极简 Valve KeyValues 解析器（libraryfolders.vdf 只用到字符串与嵌套块）"""
    tokens = re.findall(r'"((?:[^"\\]|\\.)*)"|([{}])', text)
    pos = 0

    def parse_block():
        nonlocal pos
        node = {}
        while pos < len(tokens):
            quoted, brace = tokens[pos]
            if brace == "}":
                pos += 1
                return node
            if brace == "{":
                pos += 1
                continue
            key = quoted.replace('\\\\', '\\')
            pos += 1
            if pos >= len(tokens):
                break
            q2, b2 = tokens[pos]
            if b2 == "{":
                pos += 1
                node[key] = parse_block()
            else:
                pos += 1
                node[key] = q2.replace('\\\\', '\\')
        return node

    return parse_block()


def library_folders(steam_root):
    """该 Steam 根下的所有库目录（含根本身）。兼容新旧 libraryfolders.vdf。"""
    libs = [steam_root]
    vdf = os.path.join(steam_root, "steamapps", "libraryfolders.vdf")
    if not os.path.isfile(vdf):
        return libs
    try:
        with open(vdf, encoding="utf-8", errors="replace") as fh:
            tree = parse_vdf(fh.read())
    except Exception:
        return libs
    lf = tree.get("libraryfolders") or tree.get("LibraryFolders") or {}
    for key, val in lf.items():
        if isinstance(val, dict):
            p = val.get("path") or val.get("Path")
        elif isinstance(val, str):
            p = val                       # 老格式：编号直接对应路径
        else:
            p = None
        if p:
            libs.append(os.path.normpath(p.replace("/", "\\")))
    seen, out = set(), []
    for p in libs:
        n = _norm(p)
        if n not in seen and os.path.isdir(p):
            seen.add(n)
            out.append(p)
    return out


def discover_steam():
    notes = []
    roots, seen = [], set()
    for label, p in registry_steam_paths() + guessed_steam_paths():
        if not p or not os.path.isdir(p) or not os.path.isdir(os.path.join(p, "steamapps")):
            continue
        n = _norm(p)
        if n in seen:
            continue
        seen.add(n)
        roots.append(p)
        notes.append("%s → %s" % (label, p))
    if not roots:
        notes.append("未找到任何 Steam 安装（注册表与常见路径都没命中）")
    libs, lseen = [], set()
    for r in roots:
        for lib in library_folders(r):
            n = _norm(lib)
            if n not in lseen:
                lseen.add(n)
                libs.append(lib)
    return roots, libs, notes


def find_installed(libraries):
    """返回 {game_key: {path, appid, lib, source}}"""
    found = {}
    by_folder = {g["folder"].lower(): g for g in GAMES}
    for lib in libraries:
        sa = os.path.join(lib, "steamapps")
        if not os.path.isdir(sa):
            continue
        try:
            for fn in os.listdir(sa):
                if not (fn.startswith("appmanifest_") and fn.lower().endswith(".acf")):
                    continue
                try:
                    with open(os.path.join(sa, fn), encoding="utf-8", errors="replace") as fh:
                        app = parse_vdf(fh.read()).get("AppState") or {}
                except Exception:
                    continue
                installdir = (app.get("installdir") or "").strip()
                g = by_folder.get(installdir.lower())
                if not g or g["key"] in found:
                    continue
                path = os.path.join(sa, "common", installdir)
                if os.path.isdir(path):
                    found[g["key"]] = dict(path=path, appid=app.get("appid"),
                                           lib=lib, source="appmanifest")
        except OSError:
            pass
        common = os.path.join(sa, "common")
        if os.path.isdir(common):
            for g in GAMES:
                if g["key"] in found:
                    continue
                p = os.path.join(common, g["folder"])
                if os.path.isdir(p):
                    found[g["key"]] = dict(path=p, appid=None, lib=lib, source="目录扫描")
    return found


# ══════════════════════════ 安装 / 卸载 ══════════════════════════

def www_of(game_path):
    return os.path.join(game_path, "resources", "app", "www")


def _insert_marker(www):
    """往 index.html 插入自动加载行。已经存在就不再插（避免重复插同一行）。"""
    idx = os.path.join(www, "index.html")
    if not os.path.isfile(idx):
        raise RuntimeError("找不到 %s" % idx)
    with open(idx, encoding="utf-8", errors="replace") as fh:
        html = fh.read()
    if MARK_BEGIN in html:
        return ["自动加载行已存在，跳过插入"]
    # 必须晚于 require.js（mod 依赖 window.requirejs）。放在 data-main 那个 script 标签
    # 之后即可 —— 此时 init.js 可能还没执行完，但 mod 内部会轮询等待游戏就绪。
    m = re.search(r"<script[^>]*data-main[^>]*>\s*</script>", html, re.I)
    if m:
        pos = m.end()
        where = "data-main 脚本标签之后"
    else:
        m2 = re.search(r"</body>", html, re.I)
        pos = m2.start() if m2 else len(html)
        where = "</body> 之前"
    # 以换行开头、不带结尾换行；卸载时连同这个换行一起删掉，做到字节级还原
    ins = "\n%s%s" % (" " * 8, MARK_LINE)
    with open(idx, "w", encoding="utf-8", newline="") as fh:
        fh.write(html[:pos] + ins + html[pos:])
    return ["已在 index.html 插入自动加载行（%s）" % where]


def _remove_marker(www):
    """按标记移除自动加载行；re.sub 默认替换**所有**匹配，重复插入也能清干净。"""
    idx = os.path.join(www, "index.html")
    if not os.path.isfile(idx):
        return ["找不到 index.html，跳过"]
    with open(idx, encoding="utf-8", errors="replace") as fh:
        html = fh.read()
    new, n = re.subn(r"\r?\n?[ \t]*" + re.escape(MARK_BEGIN) + r".*?" + re.escape(MARK_END),
                     "", html, flags=re.S)
    if n == 0:
        return ["index.html 里没有自动加载行，无需移除"]
    with open(idx, "w", encoding="utf-8", newline="") as fh:
        fh.write(new)
    return ["已从 index.html 移除自动加载行（%d 处）" % n]


def install_mod(game, info):
    """安装：写出内置的 mod 代码 + 插入自动加载行"""
    log = []
    www = www_of(info["path"])
    jsdir = os.path.join(www, "js")
    if not os.path.isdir(jsdir):
        raise RuntimeError("找不到目录：%s" % jsdir)
    dst = os.path.join(jsdir, MOD_BASENAME)
    with open(dst, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(MOD_JS.replace("\r\n", "\n"))
    log.append("已写入 %s（%d 字节）" % (dst, os.path.getsize(dst)))
    log.extend(_insert_marker(www))
    return log


def uninstall_mod(game, info):
    """卸载：删 zzmod.js + 移除自动加载行"""
    log = []
    www = www_of(info["path"])
    log.extend(_remove_marker(www))
    dst = os.path.join(www, "js", MOD_BASENAME)
    if os.path.isfile(dst):
        os.remove(dst)
        log.append("已删除 %s" % dst)
    else:
        log.append("%s 不存在，跳过" % dst)
    return log


# ══════════════════════════ GUI ══════════════════════════

class Manager:
    def __init__(self, root):
        self.root = root
        self.q = queue.Queue()
        self.installed = {}
        self.busy = False
        self.selected = GAMES[0]["key"]

        root.title("Fireboy & Watergirl Mod 部署管理器")
        root.geometry("1080x620")

        self._build_toolbar()
        self._build_table()
        self._build_detail()
        self._build_log()

        self.root.after(120, self._drain)
        self.scan()

    def _build_toolbar(self):
        bar = ttk.Frame(self.root, padding=(8, 6))
        bar.pack(fill="x")
        ttk.Button(bar, text="重新探测 Steam 并扫描游戏", command=self.scan).pack(side="left")
        ttk.Separator(bar, orient="vertical").pack(side="left", fill="y", padx=8)
        ttk.Button(bar, text="全部安装", command=self.install_all).pack(side="left")
        ttk.Button(bar, text="全部卸载", command=self.uninstall_all).pack(side="left", padx=4)
        self.summary = ttk.Label(bar, text="")
        self.summary.pack(side="right")

    def _build_table(self):
        cols = ("game", "appid", "path")
        heads = ("游戏", "appid", "安装路径")
        widths = (140, 90, 820)
        self.tree = ttk.Treeview(self.root, columns=cols, show="headings", height=7)
        for c, h, w in zip(cols, heads, widths):
            self.tree.heading(c, text=h)
            self.tree.column(c, width=w, anchor="w" if c != "appid" else "center")
        self.tree.pack(fill="x", padx=8)
        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        for g in GAMES:
            self.tree.insert("", "end", iid=g["key"], values=(g["name"], "-", "未扫描"))

    def _build_detail(self):
        box = ttk.LabelFrame(self.root, text="选中游戏的操作", padding=8)
        box.pack(fill="x", padx=8, pady=6)
        self.detail_title = ttk.Label(box, text="", font=("", 10, "bold"))
        self.detail_title.grid(row=0, column=0, columnspan=8, sticky="w", pady=(0, 6))

        ttk.Button(box, text="安装（自动加载）", command=self.install_selected).grid(row=1, column=0, padx=3)
        ttk.Button(box, text="卸载", command=self.uninstall_selected).grid(row=1, column=1, padx=3)
        ttk.Button(box, text="打开游戏目录", command=self.open_dir).grid(row=1, column=2, padx=3)
        ttk.Button(box, text="复制脚本到剪贴板", command=self.copy_script).grid(row=1, column=3, padx=3)

        self.hint = ttk.Label(box, text="", foreground="#555", justify="left")
        self.hint.grid(row=2, column=0, columnspan=8, sticky="w", pady=(8, 0))

    def _build_log(self):
        box = ttk.LabelFrame(self.root, text="日志", padding=4)
        box.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        self.log = scrolledtext.ScrolledText(box, height=10, wrap="word", state="disabled")
        self.log.pack(fill="both", expand=True)

    # ---- 工具 ----
    def logline(self, msg):
        self.log.configure(state="normal")
        self.log.insert("end", "[%s] %s\n" % (time.strftime("%H:%M:%S"), msg))
        self.log.see("end")
        self.log.configure(state="disabled")

    def game(self, key=None):
        return next(g for g in GAMES if g["key"] == (key or self.selected))

    def _bg(self, fn, *a):
        if self.busy:
            self.logline("上一项操作还在进行，已忽略本次点击")
            return
        self.busy = True

        def run():
            try:
                fn(*a)
            except Exception as e:
                self.q.put(("log", "错误：%s" % e))
            finally:
                self.busy = False

        threading.Thread(target=run, daemon=True).start()

    # ---- 扫描 ----
    def scan(self):
        self._bg(self._scan_worker)

    def _scan_worker(self):
        roots, libs, notes = discover_steam()
        for n in notes:
            self.q.put(("log", n))
        if libs:
            self.q.put(("log", "游戏库（%d 个）：%s" % (len(libs), " ; ".join(libs))))
        self.q.put(("scan", find_installed(libs)))

    def _apply_scan(self, found):
        self.installed = found
        for g in GAMES:
            info = found.get(g["key"])
            self.tree.item(g["key"], values=(g["name"], (info or {}).get("appid") or "-",
                                             info["path"] if info else "未安装"))
        self.summary.configure(text="找到 %d / 7 作" % len(found))
        self.logline("扫描完成：找到 %d 作" % len(found))
        if self.tree.selection():
            self._on_select()

    # ---- 事件 ----
    def _on_select(self, _e=None):
        sel = self.tree.selection()
        if not sel:
            return
        self.selected = sel[0]
        g = self.game()
        info = self.installed.get(self.selected)
        self.detail_title.configure(text=g["name"])
        if not info:
            self.hint.configure(text="未安装（未在 Steam 库中找到）")
            return
        www = www_of(info["path"])
        self.hint.configure(text="安装路径：%s\n"
                                 "将写入：%s\n"
                                 "将修改：%s（插入/移除一行）"
                                 % (info["path"],
                                    os.path.join(www, "js", MOD_BASENAME),
                                    os.path.join(www, "index.html")))

    def _need_selected(self):
        info = self.installed.get(self.selected)
        if not info:
            messagebox.showwarning("未安装", "「%s」未在 Steam 库中找到。" % self.game()["name"])
            return None
        return info

    # ---- 单作 ----
    def install_selected(self):
        if self._need_selected():
            self._bg(self._install_worker, [self.selected])

    def uninstall_selected(self):
        if self._need_selected():
            self._bg(self._uninstall_worker, [self.selected])

    def _install_worker(self, keys):
        for k in keys:
            g = self.game(k)
            for line in install_mod(g, self.installed[k]):
                self.q.put(("log", "%s：%s" % (g["name"], line)))

    def _uninstall_worker(self, keys):
        for k in keys:
            g = self.game(k)
            for line in uninstall_mod(g, self.installed[k]):
                self.q.put(("log", "%s：%s" % (g["name"], line)))

    # ---- 批量 ----
    def install_all(self):
        keys = list(self.installed)
        if not keys:
            messagebox.showwarning("没有可安装的游戏", "扫描结果为空。")
            return
        self._bg(self._install_worker, keys)

    def uninstall_all(self):
        keys = list(self.installed)
        if not keys:
            messagebox.showwarning("没有可卸载的游戏", "扫描结果为空。")
            return
        if not messagebox.askyesno("确认卸载",
                                   "将对 %d 作执行：删除 zzmod.js 并移除 index.html 里那一行。\n继续？" % len(keys)):
            return
        self._bg(self._uninstall_worker, keys)

    def open_dir(self):
        info = self.installed.get(self.selected)
        if info and os.path.isdir(info["path"]):
            os.startfile(info["path"])

    def copy_script(self):
        self.root.clipboard_clear()
        self.root.clipboard_append(MOD_JS)
        self.logline("已把内置 mod 代码（%d 字符）复制到剪贴板，可在游戏控制台里直接粘贴"
                     % len(MOD_JS))

    def _drain(self):
        try:
            while True:
                kind, payload = self.q.get_nowait()
                if kind == "log":
                    self.logline(payload)
                elif kind == "scan":
                    self._apply_scan(payload)
        except queue.Empty:
            pass
        self.root.after(120, self._drain)


# ══════════════════════════ 命令行自检 ══════════════════════════

def cli_scan():
    print("=" * 96)
    print("Steam 定位")
    roots, libs, notes = discover_steam()
    for n in notes:
        print("  " + n)
    print("  Steam 根：%d 个   游戏库：%d 个" % (len(roots), len(libs)))
    for l in libs:
        print("      " + l)

    found = find_installed(libs)
    print("\n游戏扫描：找到 %d / 7 作\n" % len(found))
    print("  %-13s %-9s %s" % ("key", "appid", "安装路径"))
    for g in GAMES:
        info = found.get(g["key"])
        if not info:
            print("  %-13s %-9s %s" % (g["key"], "-", "未安装"))
        else:
            print("  %-13s %-9s %s" % (g["key"], info["appid"] or "-", info["path"]))

    print("\n内置 mod 代码：%d 字符（七个游戏共用，不需要外部文件）" % len(MOD_JS))
    return 0


def main():
    if "--scan" in sys.argv:
        return cli_scan()
    root = tk.Tk()
    try:
        ttk.Style().theme_use("vista")
    except Exception:
        pass
    Manager(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
