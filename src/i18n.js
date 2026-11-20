import { DICT } from "./i18n-dict.js";

/*
 * 轻量三语层。语言选择持久保存在 localStorage；语言询问默认每次冷启动显示，
 * 用户可在弹窗或设置页永久关闭。切语言直接 reload，让模块级文案重新求值。
 */
export const LANGS = [
  { id: "ja", label: "日本語", mark: "あ" },
  { id: "en", label: "English", mark: "A" },
  { id: "zh", label: "简体中文", mark: "中" },
];

const KEY = "xr.lang.session";
const PREF_KEY = "xr.lang.preference";
const PROMPT_KEY = "xr.lang.prompted";
const PROMPT_SUPPRESS_KEY = "xr.lang.prompt.suppress";
const VER_KEY = "xr.lang.version";
const BOOT_KEY = "xr.booted";
const VER = "v" + __APP_VERSION__;

const ss = {
  get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { sessionStorage.setItem(k, v); } catch {} },
  del(k) { try { sessionStorage.removeItem(k); } catch {} },
};

// 版本变了就当第一次启动：语言回日语、询问重新弹
try {
  if (localStorage.getItem(VER_KEY) !== VER) {
    localStorage.setItem(VER_KEY, VER);
    ss.del(KEY);
    ss.del(PROMPT_KEY);
    ss.del(BOOT_KEY);
  }
} catch {}

/* 只有「本次会话内 reload」才沿用刚才选的语言。
   真正的冷启动（包括装完新版本第一次打开）一律回到日语并重新询问，
   不依赖 sessionStorage 会不会被 WebView 意外持久化。 */
function isReloadNav() {
  try {
    const nav = performance.getEntriesByType?.("navigation")?.[0];
    return nav?.type === "reload";
  } catch { return false; }
}
const RELOAD = isReloadNav();

function readLang() {
  try {
    const preferred = localStorage.getItem(PREF_KEY);
    if (LANGS.some((l) => l.id === preferred)) return preferred;
  } catch {}
  const v = ss.get(KEY);
  return LANGS.some((l) => l.id === v) ? v : "ja";
}

let lang = readLang();

export function currentLang() { return lang; }
export function t(s) {
  if (lang === "zh" || typeof s !== "string") return s;
  const row = DICT[s];
  return (row && row[lang]) || s;
}
export function shouldPromptLang() {
  try {
    if (localStorage.getItem(PROMPT_SUPPRESS_KEY) === "1") return false;
  } catch {}
  return RELOAD ? ss.get(PROMPT_KEY) !== "1" : true;
}
export function dismissLangPrompt() { ss.set(PROMPT_KEY, "1"); }
export function isLangPromptEnabled() {
  try { return localStorage.getItem(PROMPT_SUPPRESS_KEY) !== "1"; } catch { return true; }
}
export function setLangPromptEnabled(enabled) {
  try {
    if (enabled) localStorage.removeItem(PROMPT_SUPPRESS_KEY);
    else localStorage.setItem(PROMPT_SUPPRESS_KEY, "1");
  } catch {}
}
export function markBooted() { ss.set(BOOT_KEY, "1"); }
export function isBooted() { return RELOAD && ss.get(BOOT_KEY) === "1"; }

export function setLang(next) {
  if (!LANGS.some((l) => l.id === next)) return;
  lang = next;
  try { localStorage.setItem(PREF_KEY, next); } catch {}
  ss.set(KEY, next);
  ss.set(PROMPT_KEY, "1");
  ss.set(BOOT_KEY, "1");
  // 让模块级常量按新语言重新求值
  window.location.reload();
}
