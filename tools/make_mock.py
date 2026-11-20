"""生成 dist/mock.js + dist/mock.html，用于无头浏览器截图验证（验证完删掉）。"""
import io, os, re, shutil

DIST = "dist"
html = io.open(os.path.join(DIST, "index.html"), encoding="utf-8").read()

MOCK = r"""
(function () {
  var q = new URLSearchParams(location.search);
  function setup() {
  try {
    localStorage.setItem("xr.lang.version", "v1.3.0");
    localStorage.setItem("xr.tutorial.suppress", "1");
    localStorage.setItem("xr.tutorial.suppressVer", "v1.3.0");
    localStorage.setItem("xr.notice.suppress", "1");
    localStorage.setItem("xr.notice.suppressVer", "v1.3.0");
    if (q.get("splash") === "1") sessionStorage.removeItem("xr.booted");
    else sessionStorage.setItem("xr.booted", "1");
    if (q.get("lang")) sessionStorage.setItem("xr.lang.session", q.get("lang"));
    else sessionStorage.removeItem("xr.lang.session");
    if (q.get("nolang") === "1") sessionStorage.setItem("xr.lang.prompted", "1");
    else sessionStorage.removeItem("xr.lang.prompted");
    if (q.get("theme")) localStorage.setItem("xr.theme", q.get("theme"));
    if (q.get("noprompt") === "1") localStorage.setItem("xr.lang.prompt.suppress", "1");
    else localStorage.removeItem("xr.lang.prompt.suppress");
    localStorage.setItem("xr.tutorial.suppress", "1");
    localStorage.setItem("xr.notice.seen", "9999");
    if (q.get("gblur")) localStorage.setItem("xr.bg", JSON.stringify({ blur: 0, glassBlur: parseInt(q.get("gblur"),10), veil: 34, mode: "video", color: "#171226", glass: true, v: 3 }));
    if (q.get("nogl") === "1") localStorage.setItem("xr.bg", JSON.stringify({ blur: 0, glassBlur: 22, veil: 24, mode: "video", color: "#171226", glass: true, webgl: false, v: 3 }));
    if (q.get("glass") === "off") localStorage.setItem("xr.bg", JSON.stringify({ blur: 3, veil: 62, theme: "miku", glass: false }));
  } catch (e) {}
  }
  // 模拟「选完语言后 reload」：冷启动是 navigate，i18n.js 会强制日语
  if (q.get("lang") && !sessionStorage.getItem("__mock_reloaded")) {
    sessionStorage.setItem("__mock_reloaded", "1");
    setup();
    location.reload();
    return;
  }
  setup();

  var weapons = ["ak47","m4a1","m4a1_silencer","awp","ssg08","scar20","g3sg1","deagle","usp_silencer","glock","famas","galilar","mp9","mac10","ump45","p90","negev","m249","xm1014","nova","mag7","sawedoff","fn57","tec9","cz75a","mp7","mp5sd","aug","sg556","bizon"];
  var names = ["SniperPro","RiflePro","Scoper","SniperPure","Duelist","Rusher","Fastshot","Camper"];
  var templates = names.map(function (n, i) { return { name: n, weapons: weapons.slice(i, i + 6) }; });
  var installed = { metamod: true, css: true, botRandomizer: true, botHider: true, mapRanks: true, xueRongChat: true, inventory: true, premierBadge: true, killfeedIcons: true, mvpMusic: true };

  var state = {
    ok: true, installed: installed, cs: "D:\\apps\\steam\\steamapps\\common\\Counter-Strike Global Offensive",
    autoDetectedPath: "D:\\apps\\steam\\steamapps\\common\\Counter-Strike Global Offensive",
    plugins: {}, pluginCatalog: [], difficulty: "Medium", err: null,
  };

  var fixed = {
    get_state: state,
    hitmute_status: { supported: true, enabled: false, partial: false, written: 0, expected: 0, total: 69,
      cs2Path: "D:\\apps\\steam\\steamapps\\common\\Counter-Strike Global Offensive",
      dir: "D:\\apps\\steam\\...\\game\\csgo\\sounds\\player" },
    hitmute_set: { enabled: false, removed: 0, restored: 0, needsRestart: true },
    vpk_weapons_read: { ok: true, botCount: 1993, templates: templates, allWeapons: weapons,
      vpkPath: "D:\\apps\\steam\\...\\overrides\\botprofile.vpk", cs2Running: false },
    validate_manifest: { ok: true, total: 62, present: 62, missing: [], misplaced: null,
      officialTotal: 8, modTotal: 54, modPresent: 54, officialPresent: 8, iconCount: 1593,
      coreItems: [1,2,3], coreFailed: [] },
    get_plugin_inventory: {
      ok: true, pluginCount: 8, componentCount: 4,
      source: { name: "CS2BotImprover.zip", path: "C:\\\\Users\\\\ASUS\\\\Downloads\\\\CS2BotImprover.zip",
        size: 87097000, entries: 2201, builtAt: "2026-11-12 10:24" },
      installTargets: ["addons", "overrides", "cfg", "backup", "panorama"],
      plugins: [{ name: "MapRanks", displayName: "MapRanks", kind: "plugin", installed: true, version: "2.0.0",
        files: 3, size: 240000, builtAt: "2026-11-10", installedAt: "2026-11-12", source: "bundled" }],
      components: [{ name: "Metamod", displayName: "Metamod", kind: "component", installed: true, version: "2.0",
        files: 6, size: 180000, builtAt: "2026-10-01", installedAt: "2026-11-12", source: "bundled" }],
    },
    check_app_update: { hasUpdate: false },
  };

  var cid = 0;
  window.__TAURI_INTERNALS__ = {
    metadata: { currentWindow: { label: "main" }, currentWebview: { label: "main" } },
    transformCallback: function (cb, once) {
      var id = "__mock_cb_" + (++cid);
      window[id] = function (data) { cb && cb(data); if (once) delete window[id]; };
      return id;
    },
    unregisterCallback: function () {},
    convertFileSrc: function (p) { return p; },
    invoke: function (cmd) {
      if (Object.prototype.hasOwnProperty.call(fixed, cmd)) return Promise.resolve(fixed[cmd]);
      if (/^get_|^mvp_|^check_/.test(cmd)) return Promise.resolve({});
      return Promise.resolve({ success: true, ok: true });
    },
  };
  function flag(msg) {
    document.documentElement.setAttribute("data-mockerr", msg);
    var d = document.createElement("pre"); d.id = "mockerr"; d.textContent = msg;
    if (document.body) document.body.appendChild(d);
  }
  if (q.get("scrolling") === "1") {
    document.documentElement.classList.add("is-scrolling");
  }
  if (q.get("settings") === "1") {
    window.addEventListener("load", function () {
      setTimeout(function () {
        var b = Array.from(document.querySelectorAll(".navpill__item"))
          .find(function (x) { return /設定|设置|Settings/.test(x.textContent || ""); });
        if (b) b.click();
      }, 1800);
    });
  }
  if (q.get("switch") === "zh" && !sessionStorage.getItem("__mock_switched")) {
    window.addEventListener("load", function () {
      setTimeout(function () {
        var b = document.querySelector(".lang-primary");
        if (b) { sessionStorage.setItem("__mock_switched", "1"); b.click(); }
      }, 300);
    });
  }
  if (q.get("nofade") === "1") {
    var st = document.createElement("style");
    st.textContent = ".pager{animation:none!important;opacity:1!important;transform:none!important}";
    document.addEventListener("DOMContentLoaded", function () { document.head.appendChild(st); });
  }
  if (q.get("welcome") === "1") {
    localStorage.setItem("xr.tutorial.suppress", "1");
    localStorage.setItem("xr.tutorial.suppressVer", "v1.3.0");
    localStorage.removeItem("xr.notice.suppress");
    localStorage.removeItem("xr.notice.suppressVer");
  }
  if (q.get("dbg") === "1") {
    setTimeout(function () {
      var v = document.querySelector(".bgv video");
      var cs = getComputedStyle(document.documentElement);
      document.title = "DBG blur=" + cs.getPropertyValue("--bg-blur") +
        " veil=" + cs.getPropertyValue("--veil") +
        " glass=" + cs.getPropertyValue("--blur") +
        " vfilter=" + (v ? getComputedStyle(v).filter : "none") +
        " webgl=" + document.documentElement.dataset.webgl;
    }, 2500);
  }
  window.addEventListener("error", function (e) {
    flag("ERROR: " + e.message + " @ " + (e.filename || "") + ":" + (e.lineno || ""));
    console.error("MOCKERR", e.message, e.error && e.error.stack);
  });
  window.addEventListener("unhandledrejection", function (e) {
    flag("REJECT: " + ((e.reason && e.reason.stack) || e.reason));
  });
})();
"""

io.open(os.path.join(DIST, "mock.js"), "w", encoding="utf-8").write(MOCK)
out = html.replace("</head>", '<script src="/mock.js"></script></head>')
io.open(os.path.join(DIST, "mock.html"), "w", encoding="utf-8").write(out)
print("mock.html / mock.js 已生成")
