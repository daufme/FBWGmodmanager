(function boot() {
  'use strict';

  var MAIN = ['invincible', 'timer', 'gems'];
  var ALL = MAIN.slice();

  if (window.__MOD_FBWG__) {
    console.warn('[mod] 已安装，重复粘贴已忽略。MOD.toggles() / MOD.all() / MOD.info() 可用。');
    if (window.MOD && window.MOD.toggles) window.MOD.toggles();
    return;
  }

  var CFG = { invincible: true, timer: true, gems: true };

  function say() { console.warn.apply(console, ['[mod]'].concat([].slice.call(arguments))); }
  var out = say;

  function findPhaser() {
    try { if (window.requirejs && requirejs.defined && requirejs.defined('Phaser')) return requirejs('Phaser'); } catch (e) {}
    try {
      var reg = requirejs.s.contexts._.defined;
      for (var k in reg) { var v = reg[k]; if (v && v.GAMES && v.GAMES.length) return v; }
    } catch (e) {}
    return window.Phaser || null;
  }

  var Phaser = findPhaser();
  if (!Phaser || !Phaser.GAMES || !Phaser.GAMES[0]) {
    boot.tries = (boot.tries || 0) + 1;
    if (boot.tries > 600) {
      console.warn('[mod] 等待游戏就绪超时（60s）已放弃。请确认本文件被加载在游戏页面内。');
      return;
    }
    setTimeout(boot, 100);
    return;
  }
  var G = Phaser.GAMES[0];

  var undo = [];
  function remember(fn) { undo.push(fn); }

  function protoOwning(inst, name) {
    if (!inst) return null;
    var p = Object.getPrototypeOf(inst);
    while (p) {
      if (Object.prototype.hasOwnProperty.call(p, name)) return p;
      p = Object.getPrototypeOf(p);
    }
    return null;
  }

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

  function targetTime(levelData) {
    if (!levelData) return null;
    var t = (G.settings && G.settings.controls === 'single' && levelData.mobileTime) || levelData.time;
    return (typeof t === 'number' && t > 0) ? t : null;
  }

  function hardenMake(orig) {
    return function () {
      if (CFG.invincible) return false;
      return orig.apply(this, arguments);
    };
  }
  function hardenInstance(ch) {
    if (!ch) return;
    ['liquidTouched', 'kill', '_doKill'].forEach(function (k) {
      if (typeof ch[k] === 'function') patchSlot(ch, k, hardenMake);
    });
  }

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

    pin('diamonds', 'gems', function (v, lv) {
      var t = lv && lv.totalDiamonds;
      return (typeof t === 'number' && isFinite(t)) ? t : 999999;
    });
    pin('silverDiamond', 'gems', function (v) {
      return Math.max((typeof v === 'number' && isFinite(v)) ? v : 0, 1);
    });
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

  function attach() {
    var L = G.level;
    if (!L || !L.levelState) return false;

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

    var clock = L.ui && L.ui.clock;
    if (clock) {
      patchSlot(clock, 'getElapsedSeconds', clockMake);
      var clockProto = protoOwning(clock, 'getElapsedSeconds');
      if (clockProto) patchSlot(clockProto, 'getElapsedSeconds', clockMake);
    }

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
  var timer = setInterval(tick, 800);
  remember(function () { clearInterval(timer); });

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