import React, { useState, useEffect, useRef, useCallback, useMemo, createContext, useContext } from "react";
import { api, isTauri } from "./api.js";
import splashImg from "./assets/splash.png";
import splashArrive from "./assets/aemeath-arrive.gif";
import splashType from "./assets/aemeath-type.gif";
import splashScore from "./assets/aemeath-score.gif";
import bgVideo2 from "./assets/bg2.mp4";
import avatarImg from "./assets/avatar.png";
import mood01 from "./assets/moods/mood-01.webp";
import mood02 from "./assets/moods/mood-02.webp";
import mood03 from "./assets/moods/mood-03.webp";
import mood04 from "./assets/moods/mood-04.webp";
import mood05 from "./assets/moods/mood-05.webp";
import mood06 from "./assets/moods/mood-06.webp";
import mood07 from "./assets/moods/mood-07.webp";
import mood08 from "./assets/moods/mood-08.webp";
import mood09 from "./assets/moods/mood-09.webp";
import { useGlassPointer, useScrollDegrade } from "./Glass.jsx";
import { currentLang, LANGS, setLang, shouldPromptLang, markBooted, isBooted, isLangPromptEnabled, setLangPromptEnabled } from "./i18n.js";
import LanguagePrompt from "./LanguagePrompt.jsx";
import { t as tr } from "./i18n.js";
import "./styles/ui.css";

// 由 vite define 从 package.json 注入，别再手写
const VER = "v" + __APP_VERSION__;

/* 品牌字：逐字母做渐变流动 + 轻微浮动，纯 CSS 动画，不加 rAF */
const BRAND = "AEMEATH";
function BrandWord({ className = "" }) {
  return (
    <span className={`brand-word ${className}`} aria-label={BRAND}>
      {BRAND.split("").map((c, i) => (
        <span key={i} style={{ "--i": i }} aria-hidden="true">
          {c === " " ? "\u00a0" : c}
        </span>
      ))}
    </span>
  );
}

/* ═══════════ Toast ═══════════ */
const ToastCtx = createContext(null);
function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const idRef = useRef(0);
  const toast = useCallback((message, tone = "ok") => {
    const id = ++idRef.current;
    setToasts((prev) => [...prev, { id, message, tone }]);
    setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), 2400);
  }, []);
  return (
    <ToastCtx.Provider value={{ toast }}>
      {children}
      <div className="toasts">
        {toasts.map((t) => (
          <div key={t.id} className={`toast toast--${t.tone}`}>
            <span className="toast__bar" /><span>{t.message}</span>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}
const useToast = () => useContext(ToastCtx);

/* ═══════════ 音乐播放器（挂在 App 层，切页不断） ═══════════ */
const MUSIC_DB = "xr-music";
const MUSIC_STORE = "tracks";

function musicDB() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(MUSIC_DB, 1);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(MUSIC_STORE)) {
        db.createObjectStore(MUSIC_STORE, { keyPath: "id" });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function musicAll() {
  try {
    const db = await musicDB();
    return await new Promise((resolve) => {
      const rq = db.transaction(MUSIC_STORE, "readonly").objectStore(MUSIC_STORE).getAll();
      rq.onsuccess = () => resolve(rq.result || []);
      rq.onerror = () => resolve([]);
    });
  } catch { return []; }
}

async function musicPut(rec) {
  try {
    const db = await musicDB();
    await new Promise((resolve) => {
      const tx = db.transaction(MUSIC_STORE, "readwrite");
      tx.objectStore(MUSIC_STORE).put(rec);
      tx.oncomplete = resolve;
      tx.onerror = resolve;
    });
  } catch {}
}

async function musicDel(id) {
  try {
    const db = await musicDB();
    await new Promise((resolve) => {
      const tx = db.transaction(MUSIC_STORE, "readwrite");
      tx.objectStore(MUSIC_STORE).delete(id);
      tx.oncomplete = resolve;
      tx.onerror = resolve;
    });
  } catch {}
}

const MusicCtx = createContext(null);

function MusicProvider({ children }) {
  const audioRef = useRef(null);
  const fadeRef = useRef(null);
  const [tracks, setTracks] = useState([]);
  const [idx, setIdx] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [visible, setVisibleState] = useState(() => {
    try { return localStorage.getItem("xr.player") !== "0"; } catch { return true; }
  });
  const [volume, setVolume] = useState(() => {
    const v = parseFloat(localStorage.getItem("xr.vol") || "");
    return Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0.45;
  });
  const [cur, setCur] = useState(0);
  const [dur, setDur] = useState(0);
  const volRef = useRef(volume);

  const setVisible = useCallback((v) => {
    setVisibleState(v);
    try { localStorage.setItem("xr.player", v ? "1" : "0"); } catch {}
  }, []);

  const fadeTo = useCallback((to, ms = 1600) => {
    const a = audioRef.current; if (!a) return;
    clearInterval(fadeRef.current);
    const from = a.volume, steps = Math.max(1, Math.round(ms / 60));
    let i = 0;
    fadeRef.current = setInterval(() => {
      i += 1;
      a.volume = Math.min(1, Math.max(0, from + (to - from) * (i / steps)));
      if (i >= steps) clearInterval(fadeRef.current);
    }, 60);
  }, []);

  // 启动时读回导入过的本地音乐
  useEffect(() => {
    let alive = true;
    (async () => {
      const saved = await musicAll();
      if (!alive || !saved.length) return;
      setTracks((prev) => prev.concat(
        saved.map((r) => ({ id: r.id, name: r.name, url: URL.createObjectURL(r.blob) }))
      ));
    })();
    return () => { alive = false; };
  }, []);

  // 启动一律静音待命：音乐由用户点播放键才响，不自动开声
  useEffect(() => {
    const a = audioRef.current;
    if (!a) return;
    try { a.pause(); a.currentTime = 0; } catch {}
    setPlaying(false);
  }, []);

  const playAt = useCallback(async (i) => {
    const a = audioRef.current;
    const t = tracks[i];
    if (!a || !t) return;
    if (a.getAttribute("src") !== t.url) {
      a.src = t.url;
      setCur(0);
      setDur(0);
      try { a.load(); } catch {}
    }
    setIdx(i);
    try { a.volume = 0; await a.play(); setPlaying(true); fadeTo(volRef.current); } catch {}
  }, [tracks, fadeTo]);

  const toggle = useCallback(async () => {
    const a = audioRef.current; if (!a) return;
    if (playing) { clearInterval(fadeRef.current); a.pause(); setPlaying(false); }
    else await playAt(idx);
  }, [playing, idx, playAt]);

  const next = useCallback(() => {
    if (tracks.length < 2) return;
    playAt((idx + 1) % tracks.length);
  }, [idx, tracks.length, playAt]);

  const prev = useCallback(() => {
    if (tracks.length < 2) return;
    playAt((idx - 1 + tracks.length) % tracks.length);
  }, [idx, tracks.length, playAt]);

  const seek = useCallback((t) => {
    const a = audioRef.current;
    if (!a) return;
    const d = Number.isFinite(a.duration) ? a.duration : 0;
    if (d <= 0) return;
    const pos = Math.min(Math.max(0, t), d);
    a.currentTime = pos;
    setCur(pos);
  }, []);

  const changeVolume = (v) => {
    const val = Math.min(1, Math.max(0, v));
    setVolume(val); volRef.current = val;
    localStorage.setItem("xr.vol", String(val));
    const a = audioRef.current;
    if (a && playing) { clearInterval(fadeRef.current); a.volume = val; }
  };

  const importFiles = useCallback(async (fileList) => {
    const added = [];
    for (const f of Array.from(fileList || [])) {
      if (!f.type.startsWith("audio/")) continue;
      const id = "u" + Date.now() + Math.random().toString(36).slice(2, 7);
      const name = f.name.replace(/\.[^.]+$/, "");
      await musicPut({ id, name, blob: f });
      added.push({ id, name, url: URL.createObjectURL(f) });
    }
    if (added.length) setTracks((prev) => prev.concat(added));
    return added.length;
  }, []);

  const removeTrack = useCallback(async (id) => {
    setTracks((prev) => {
      const i = prev.findIndex((t) => t.id === id);
      if (i < 0) return prev;
      const nt = prev.filter((t) => t.id !== id);
      setIdx((cur) => (cur >= nt.length ? Math.max(0, nt.length - 1) : cur > i ? cur - 1 : cur));
      if (nt.length === 0) {
        const a = audioRef.current;
        if (a) { try { a.pause(); a.removeAttribute("src"); a.load(); } catch {} }
        setPlaying(false);
        setCur(0);
        setDur(0);
      }
      return nt;
    });
    await musicDel(id);
  }, []);

  // 不再自动播放：只有用户点播放键/点歌才出声

  return (
    <MusicCtx.Provider value={{
      tracks, idx, playing, visible, volume, cur, dur,
      toggle, next, prev, playAt, seek, changeVolume, importFiles, removeTrack, setVisible,
    }}>
      {children}
      <audio ref={audioRef} loop={tracks.length === 1} preload="metadata" autoPlay={false}
        onTimeUpdate={(e) => {
          const t = e.currentTarget.currentTime || 0;
          setCur((prev) => (Math.abs(prev - t) < 0.2 ? prev : t));
        }}
        onLoadedMetadata={(e) => setDur(e.currentTarget.duration || 0)}
        onDurationChange={(e) => setDur(e.currentTarget.duration || 0)}
        onEnded={() => { if (tracks.length > 1) next(); }} />
    </MusicCtx.Provider>
  );
}
const useMusic = () => useContext(MusicCtx);

/* ═══════════ 播放器界面 ═══════════ */
function fmtTime(sec) {
  const n = Number(sec);
  if (!Number.isFinite(n) || n <= 0) return "0:00";
  const m = Math.floor(n / 60);
  const ss = Math.floor(n % 60);
  return `${m}:${String(ss).padStart(2, "0")}`;
}

function MusicPlayer() {
  const {
    tracks, idx, playing, visible, volume, cur, dur,
    toggle, next, prev, playAt, seek, changeVolume, importFiles, removeTrack, setVisible,
  } = useMusic();
  const { toast } = useToast();
  const [showList, setShowList] = useState(false);
  const [closing, setClosing] = useState(false);
  const [peek, setPeek] = useState(false);
  const fileRef = useRef(null);
  const track = tracks[idx] || tracks[0];

  // 关掉时先播「收进收纳袋」动画，再真正卸载
  const closePlayer = () => {
    if (closing) return;
    setClosing(true);
    setTimeout(() => { setVisible(false); setClosing(false); }, 360);
  };

  // 没导入任何音乐时不占屏幕：只留小圆钮，点了才展开（避免常驻卡片压住正文）
  const collapsed = visible && !peek && tracks.length === 0 && !playing;

  // 关掉后留一个小入口，随时能再打开
  if (!visible || collapsed) {
    return (
      <button className="player-open" onClick={() => { setVisible(true); setPeek(true); }} title={tr("打开音乐播放器")}>
        {I.note(15)}
      </button>
    );
  }

  return (
    <div className={`player ${closing ? "player--put" : ""}`}>
      <div className="player__head">
        <span className="player__art">{I.note(16)}</span>
        <span className="player__meta">
          <b title={track?.name}>{track?.name || tr("未导入音乐")}</b>
          <em>{tracks.length > 1 ? `${idx + 1} / ${tracks.length}` : tracks.length === 1 ? tr("本地导入") : tr("点下方 + 导入")}</em>
        </span>
        <button className="player__x" onClick={closePlayer} title={tr("关闭播放器")}>
          {I.close(12)}
        </button>
      </div>

      <div className="player__seek">
        <span className="player__time">{fmtTime(cur)}</span>
        <input className="range player__bar" type="range"
          min="0" max={dur > 0 ? dur : 0} step="0.1"
          value={dur > 0 ? Math.min(cur, dur) : 0}
          disabled={dur <= 0}
          onChange={(e) => seek(Number(e.target.value))}
          title={tr("拖动调整进度")} />
        <span className="player__time">{fmtTime(dur)}</span>
      </div>

      <div className="player__ctrl">
        <button className="player__btn" onClick={prev} disabled={tracks.length < 2} title={tr("上一首")}>{I.prev(14)}</button>
        <button className="player__play" onClick={toggle} disabled={!tracks.length} title={tracks.length ? (playing ? tr("暂停") : tr("播放")) : tr("先导入音乐")}>
          {playing ? I.pause(16) : I.play(16)}
        </button>
        <button className="player__btn" onClick={next} disabled={tracks.length < 2} title={tr("下一首")}>{I.next(14)}</button>
        <input className="range player__vol" type="range" min="0" max="1" step="0.01"
          value={volume} onChange={(e) => changeVolume(Number(e.target.value))} title={tr("音量")} />
      </div>

      <div className="player__foot">
        <button className="player__add" onClick={() => fileRef.current?.click()}>{tr("+ 导入音乐")}</button>
        <button className="player__list" onClick={() => setShowList((v) => !v)}>
          {showList ? tr("收起") : `${tr("列表 ")}${tracks.length}`}
        </button>
      </div>

      {showList && (
        <div className="player__tracks">
          {tracks.map((t, i) => (
            <div className={`player__track ${i === idx ? "is-on" : ""}`} key={t.id}>
              <button className="player__tname" onClick={() => playAt(i)}>{t.name}</button>
              {!t.builtin && (
                <button className="player__del" onClick={() => removeTrack(t.id)} title={tr("移除")}>×</button>
              )}
            </div>
          ))}
          <p className="player__hint">{tr("导入的音乐会保存在本机，重启后还在")}</p>
        </div>
      )}

      <input ref={fileRef} type="file" accept="audio/*" multiple hidden
        onChange={async (e) => {
          const n = await importFiles(e.target.files);
          e.target.value = "";
          if (n) toast(`${tr("已导入 ")}${n}${tr(" 首")}`);
          else toast(tr("没有可导入的音频文件"), "err");
        }} />
    </div>
  );
}

/* ═══════════ 图标 ═══════════ */
const S = (p, s) => ({ width: s, height: s, viewBox: "0 0 24 24", ...p });
const I = {
  bolt: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M13 2 3 14h7l-1 8 10-12h-7l1-8z"/></svg>,
  play: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M8 5.5v13l11-6.5z"/></svg>,
  pause: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="5" width="4" height="14" rx="1.4"/><rect x="14" y="5" width="4" height="14" rx="1.4"/></svg>,
  shield: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3 4 7v6c0 5 3.5 7.5 8 8 4.5-.5 8-3 8-8V7z"/><path d="m9 12 2 2 4-4"/></svg>,
  gear: (s = 16) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="3.2"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9v.09a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>,
  cart: (s = 16) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><circle cx="9" cy="20" r="1.4"/><circle cx="18" cy="20" r="1.4"/><path d="M2 3h3l2.4 11.2a2 2 0 0 0 2 1.6h7.6a2 2 0 0 0 2-1.6L21 7H6"/></svg>,
  trash: (s = 15) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M3 6h18"/><path d="M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2"/><path d="M6 6v13a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V6"/></svg>,
  sun: (s = 13) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/></svg>,
  moon: (s = 13) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5z"/></svg>,
  close: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>,
  min: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M5 12h14"/></svg>,
  home: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M3 10.5 12 3l9 7.5"/><path d="M5.5 9.5V20h13V9.5"/><path d="M9.5 20v-6h5v6"/></svg>,
  plug: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M9 3v6"/><path d="M15 3v6"/><path d="M6 9h12v3a6 6 0 0 1-12 0z"/><path d="M12 18v3"/></svg>,
  bot: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><rect x="4" y="8" width="16" height="12" rx="3.5"/><path d="M12 4v4"/><circle cx="9" cy="14" r="1.2" fill="currentColor" stroke="none"/><circle cx="15" cy="14" r="1.2" fill="currentColor" stroke="none"/><path d="M9.5 17.5h5"/></svg>,
  scale: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v18"/><path d="M5 7h14"/><path d="M5 7 2 14h6z"/><path d="M19 7l-3 7h6z"/></svg>,
  palette: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3a9 9 0 1 0 0 18c1.4 0 2-.9 2-1.8 0-1.6-1.6-2.2-1.6-3.4 0-1 .8-1.8 2-1.8H17a4 4 0 0 0 4-4c0-3.9-4-7-9-7z"/><circle cx="8" cy="9.5" r="1.1" fill="currentColor" stroke="none"/><circle cx="12" cy="7" r="1.1" fill="currentColor" stroke="none"/><circle cx="16" cy="9" r="1.1" fill="currentColor" stroke="none"/></svg>,
  terminal: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="4" width="18" height="16" rx="3"/><path d="m7.5 10 2.5 2.5L7.5 15"/><path d="M12.5 15h4"/></svg>,
  copy: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="11" height="11" rx="2.5"/><path d="M15 5.5A2.5 2.5 0 0 0 12.5 3H6a2.5 2.5 0 0 0-2.5 2.5V13"/></svg>,
  note: (s = 16) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M9 18V5l10-2v13"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="16.5" cy="16" r="2.5"/></svg>,
  prev: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M6 5h2v14H6z"/><path d="M20 5v14l-10-7z"/></svg>,
  next: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M16 5h2v14h-2z"/><path d="M4 5v14l10-7z"/></svg>,
  book: (s = 17) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M4 4.5A1.5 1.5 0 0 1 5.5 3H11v17H5.5A1.5 1.5 0 0 1 4 18.5z"/><path d="M20 4.5A1.5 1.5 0 0 0 18.5 3H13v17h5.5A1.5 1.5 0 0 0 20 18.5z"/></svg>,
  arrow: (s = 14) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12h14"/><path d="m13 6 6 6-6 6"/></svg>,
  refresh: (s = 15) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 5v6h-6"/></svg>,
};

function Toggle({ on, onChange, disabled }) {
  return (
    <button className={`sw ${on ? "is-on" : ""}`} disabled={disabled} onClick={() => !disabled && onChange(!on)} aria-pressed={on}>
      <span className="sw__track" /><span className="sw__knob" />
    </button>
  );
}

/* 昼夜切换：玻璃滑块 + 太阳/月亮 */
function ThemeSwitch({ theme, onToggle }) {
  const dark = theme === "dark";
  return (
    <button
      className="themesw"
      onClick={onToggle}
      aria-pressed={dark}
      title={dark ? tr("切到浅色") : tr("切到深色")}
    >
      <span className="themesw__ico themesw__sun">{I.sun(12)}</span>
      <span className="themesw__ico themesw__moon">{I.moon(12)}</span>
      <span className="themesw__knob">{dark ? I.moon(12) : I.sun(12)}</span>
    </button>
  );
}

function Seg({ options, value, onChange, disabled }) {
  return (
    <div className={`seg ${disabled ? "is-disabled" : ""}`}>
      {options.map((o) => (
        <button key={o.value} type="button" disabled={disabled}
          className={value === o.value ? "is-on" : ""}
          onClick={() => !disabled && onChange(o.value)}>{o.label}</button>
      ))}
    </div>
  );
}

/* ═══════════ 背景 ═══════════ */
/* 背景：视频（默认 miku）/ 自己导入的图片 / 纯色 */
function VideoBg({ bg, custom }) {
  const mode = bg?.mode || "video";
  if (mode === "solid") {
    return <div className="bgv bgv--solid" style={{ background: bg?.color || "#171226" }} aria-hidden="true" />;
  }
  if (mode === "image" && custom) {
    return (
      <div className="bgv" aria-hidden="true">
        <img className="bgv__img" src={custom} alt="" />
        <div className="bgv__veil" />
      </div>
    );
  }
  return (
    <div className="bgv" aria-hidden="true">
      <video src={bgVideo2} autoPlay muted loop playsInline preload="auto" />
      <div className="bgv__veil" />
    </div>
  );
}

/* 自定义背景图存 IndexedDB（localStorage 放不下几 MB 的图） */
const BG_DB = "xr-bg";
function bgDb() {
  return new Promise((res, rej) => {
    const r = indexedDB.open(BG_DB, 1);
    r.onupgradeneeded = () => { try { r.result.createObjectStore("img"); } catch {} };
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}
async function bgPut(v) {
  const db = await bgDb();
  return new Promise((res, rej) => {
    const tx = db.transaction("img", "readwrite");
    tx.objectStore("img").put(v, "custom");
    tx.oncomplete = () => res(true);
    tx.onerror = () => rej(tx.error);
  });
}
async function bgGet() {
  try {
    const db = await bgDb();
    return await new Promise((res, rej) => {
      const tx = db.transaction("img", "readonly");
      const q = tx.objectStore("img").get("custom");
      q.onsuccess = () => res(q.result || null);
      q.onerror = () => rej(q.error);
    });
  } catch { return null; }
}
async function bgDel() {
  try {
    const db = await bgDb();
    const tx = db.transaction("img", "readwrite");
    tx.objectStore("img").delete("custom");
  } catch {}
}

const MOODS = [mood01, mood02, mood03, mood04, mood05, mood06, mood07, mood08, mood09];
const pick = (arr) => arr[Math.floor(Math.random() * arr.length)];

function Pet() {
  const [art] = useState(() => pick(MOODS));
  return (
    <div className="pet" aria-hidden="true">
      <div className="pet__frame"><img src={art} alt="" draggable="false" /></div>
    </div>
  );
}

/* ═══════════ 开屏 ═══════════ */
// 启动页只用动图（GIF），每次随机抽一张，保证是动的
const SPLASH_ART = [splashArrive, splashType, splashScore];

function Splash({ onDone }) {
  const [art] = useState(() => pick(SPLASH_ART));
  useEffect(() => { const t = setTimeout(onDone, 2800); return () => clearTimeout(t); }, [onDone]);
  return (
    <div className="splash">
      <div className="splash__inner">
        <div className="splash__art">
          <span className="splash__halo" />
          <img src={art} alt="" draggable="false" />
        </div>
        <div className="splash__sub">CS2 Bot Plugin Manager</div>
        <div className="splash__bar"><i /></div>
      </div>
    </div>
  );
}

/* ═══════════ 更新公告 ═══════════ */
const CHANGELOG = [
  {
    v: "v1.1.2",
    date: "2026-11-14",
    tag: tr("当前版本"),
    items: [
      "修复：冻结时间人机不切刀（bot 不在 Utilities.GetPlayers 里，改成逐槽扫描）",
      "修复：.d 丢刀会多复制一把（去掉了重复的 drop 调用）",
    ],
  },
  {
    v: "v1.1.1",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "修复：冻结时间人机切刀不生效（bot 过滤条件写错，之前一个都没匹配上）",
      "修复：聊天框输入 .d 丢刀没反应",
    ],
  },
  {
    v: "v1.1.0",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "新增：聊天框输入 .d 直接丢刀，完美平台习惯；先切刀再丢，不会误丢主武器",
      "更新：人机段位浮动范围从 3000 调到 9000，分数分布更散",
      "更新：冻结时间人机切刀 / 下蹲插件增加运行诊断，便于确认是否真的触发",
    ],
  },
  {
    v: "v1.0.9",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "修复：MapRanks 原生发送库的 CS2 接口偏移，记分板重新显示全部人机的段位，不再只有自己",
      "新增：人机段位围绕你自己的分数随机分布，默认上下浮动 3000 分，可在 MapRanks.json 用 bot_rating_spread 调整",
      "更新：MapRanks 配置新增 bot_rating_near_human 开关，关闭后回到全域随机",
      "更新：库存插件保留你实测手套有效的版本，安装修复套件不会把它换回官方 3.8.2",
    ],
  },
  {
    v: "v1.0.8",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "修复：把 XP 过载徽章合并进 MapRanks，安装修复时清除旧 PremierBadgeMvp DLL，避免重复写段位字段",
      "修复：移除插件页独立 XP 徽章设置，段位与 XP 徽章现在只保留 MapRanks 一个入口",
      "更新：四份 botprofile.vpk 基于最新版 1.4.9 参数，使用 1969 个中文名字；其余 VPK 条目保持不变",
      "更新：InventorySimulator 升至官方 3.8.2；击杀图标更新至 1.0.26，机器人每回合重新随机图标包",
      "新增：准备阶段人机动作插件，冻结时间随机短暂切刀或下蹲；设置中可永久关闭启动语言询问",
      "更新：自制插件源码集中整理到 plugin-src，按雪绒维护 / 上游维护分类",
    ],
  },
  {
    v: "v1.0.7",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "更新：主体包升级至 CS2BotImprover 1.4.9，三档 VPK 使用新版参数并沿用旧版对应的中文人机名",
      "更新：难度档案按新版从旧版的 1969 个调整为 1711 个；安装清单显示主体包版本",
    ],
  },
  {
    v: "v1.0.6",
    date: "2026-11-14",
    tag: tr("历史"),
    items: [
      "新增：设置里加「静音受击音效」开关 —— 一键静音自己被爆头、受伤、死亡时的音效，关闭后自动还原，不改动任何 VPK",
      "更新：69 个静音音效已用官方编译器逐个编译并内嵌进 exe，不需要 CS2 Workshop Tools，也不会碰你的自定义音效",
      "修复：安装套件恢复为 1.0.4 稳定版，不再包含自行替换的 CounterStrikeSharp 1.0.380",
    ],
  },
  {
    v: "v1.0.5",
    date: "2026-11-13",
    tag: tr("历史"),
    items: [
      "修复：CS2 11/12 更新后插件全部失效 —— CounterStrikeSharp 升到 1.0.380，这是官方当天适配 1.41.9.7 的版本，不用降级游戏",
    ],
  },
  {
    v: "v1.0.4",
    date: "2026-11-12",
    tag: tr("历史"),
    items: [
      "修复：滚轮时整屏突然变亮 —— 滚动降级态的底色比玻璃态白一档，现在按合成亮度对齐并加了 140ms 过渡",
      "修复：背景 miku 跑偏、只显示一角 —— 模糊滤镜和居中位移同时挂在视频上把定位打乱了，改用 inset 定尺寸",
    ],
  },
  {
    v: "v1.0.3",
    date: "2026-11-12",
    tag: tr("历史"),
    items: [
      "修复：整体卡顿 —— 滚动容器逐帧 transform、每张卡片各挂一个 SVG 位移滤镜、全屏背景模糊链，三处叠在一起把帧率吃光了",
      "更新：滚动时自动撤掉背景采样、停手 140ms 恢复；卡片悬停不再位移，按钮点击更跟手",
    ],
  },
  {
    v: "v1.0.2",
    date: "2026-11-12",
    tag: tr("历史"),
    items: [
      "新增：日语 / 英语界面。每次启动和每次更新后默认日语，并弹窗询问是否切换为中文",
      "新增：启动表情包扩到 12 张，开屏主图、三张小表情和右下角贴纸每次随机",
      "更新：液态玻璃改用边缘折射位移贴图，卡片底色恢复通透，边缘有放大光晕并跟随鼠标",
      "修复：「写入 VPK」按钮被右下角贴纸挡住，移到卡片顶部常驻可见",
      "更新：缩短切页与卡片入场时长，统一 hover / 按压反馈，补齐减少动效降级",
    ],
  },
  {
    v: "v1.0.1",
    date: "2026-11-12",
    tag: tr("历史"),
    items: [
      "修复：人机页整页白屏 —— 起枪编辑器引用了一个不存在的变量，渲染时直接抛错把整棵树带崩",
      "新增：页面错误边界。以后哪一页再出错只显示那一页的错误信息和重试按钮，不再整窗白屏",
    ],
  },
  {
    v: "v1.0.0",
    date: "2026-11-12",
    tag: tr("历史"),
    items: [
      "新增：人机起枪编辑器 —— 直接改 overrides/botprofile.vpk 里 8 个武器栏目（SniperPro / RiflePro / Scoper / SniperPure / Duelist / Rusher / Fastshot / Camper），删一把枪就没人机再起它，改完能预览动了哪几行",
      "新增：液态玻璃界面，设置里可一键开关",
      "新增：启动界面换成爱弥斯动图",
      "修复：MVP 音效包删除失败仍提示成功 —— 现在逐个目录校验，被占用会告诉你具体路径",
      "修复：人机难度切换被 gamemode 配置覆盖，进图后失效",
      "修复：皮肤教程定位不到按钮、卡住不动",
      "更新：插件清单与安装链路同步；管理真人优先分数与 ELO 存档，重开不重置",
    ],
  },
];

const CH_TYPES = [
  { key: "all", label: tr("全部") },
  { key: "fix", label: tr("修复") },
  { key: "add", label: tr("新增") },
  { key: "upd", label: tr("更新") },
];

function chType(text) {
  if (/^(修复|修好|解决|纠正|撤销)/.test(text)) return "fix";
  if (/^(新增|加入|加了|支持)/.test(text)) return "add";
  if (/^(更新|升级|换成|回退|同步|优化|精简|移除|删|关闭|调整|合并|重写|对齐|固定|改为|改成)/.test(text)) return "upd";
  if (/(修复|解决|不再|避免|防止|崩溃|闪退|失效|丢失|报错|卡死|残留|未生效)/.test(text)) return "fix";
  if (/(新增|一键|支持|加入|显示)/.test(text)) return "add";
  return "upd";
}

const CH_LABEL = { fix: tr("修复"), add: tr("新增"), upd: tr("更新") };

/* ═══════════ 启动教程弹窗 ═══════════ */
function TutorialPopup({ onClose, startTour }) {
  const [noMore, setNoMore] = useState(false);
  const close = () => onClose(noMore);
  return (
    <div className="modal" onClick={close}>
      <div className="modal__card" onClick={(e) => e.stopPropagation()}>
        <div className="modal__glow" />
        <header className="modal__head">
          <div>
            <h2>{tr("新手向导")}</h2>
            <p>{tr("每次启动问一次 · AEMEATH").replace("AEMEATH", BRAND)} {VER}</p>
          </div>
          <button className="modal__x" onClick={close} aria-label={tr("关闭")}>{I.close(14)}</button>
        </header>
        <div className="modal__body">
          <p className="note" style={{ marginTop: 0 }}>
            {tr("选一个功能，界面会自动跳到那一页，高亮真实按钮并在它上方弹出说明气泡，一步步跟着点就行。")}
          </p>
          <div className="tourgrid tourgrid--modal">
            {TOURS.map((t) => (
              <button className="tourcard" key={t.id}
                onClick={() => { close(); startTour(t.id); }}>
                <span className="tourcard__ico">{I[t.icon] ? I[t.icon](19) : I.book(19)}</span>
                <b>{t.title}</b>
                <em>{t.desc}</em>
                <span className="tourcard__foot">{t.steps.length} {tr("步 · 开始向导 →")}</span>
              </button>
            ))}
          </div>
        </div>
        <footer className="modal__foot">
          <label className="modal__check">
            <input type="checkbox" checked={noMore} onChange={(e) => setNoMore(e.target.checked)} />
            <span>{tr("永久关闭教程弹窗（换成新版本仍会再弹一次）")}</span>
          </label>
          <button className="btn btn--soft btn--block" onClick={close}>{tr("先不用，稍后再看")}</button>
        </footer>
      </div>
    </div>
  );
}

function Announcement({ onClose }) {
  const [noMore, setNoMore] = useState(false);
  const close = () => onClose(noMore);

  return (
    <div className="modal" onClick={close}>
      <div className="modal__card welcome" onClick={(e) => e.stopPropagation()}>
        <button className="modal__x welcome__x" onClick={close} aria-label={tr("关闭")}>{I.close(14)}</button>
        <div className="welcome__glow" />

        <BrandWord className="welcome__brand" />

        <h2 className="welcome__title">
          <span className="welcome__title-txt">{tr("欢迎使用")}</span>
          <span className="welcome__caret" />
        </h2>
        <p className="welcome__sub">{tr("所有功能都可以在这里操作。")}</p>

        <div className="welcome__tips">
          <div className="welcome__tip">
            <b>{tr("一键启动")}</b>
            <span>{tr("总览里点「插件模式」，自动挂载 Metamod 进游戏")}</span>
          </div>
          <div className="welcome__tip">
            <b>{tr("装完先自检")}</b>
            <span>{tr("插件页有清单校验，缺文件一眼能看出来")}</span>
          </div>
          <div className="welcome__tip">
            <b>{tr("出问题点诊断")}</b>
            <span>{tr("设置页生成诊断包，丢给电脑上的 AI 就能查")}</span>
          </div>
        </div>

        <button className="btn btn--primary btn--block welcome__go" onClick={close}>
          {tr("开始使用")}
        </button>
        <label className="modal__check welcome__check">
          <input type="checkbox" checked={noMore} onChange={(e) => setNoMore(e.target.checked)} />
          <span>{tr("以后不再自动弹（换新版本仍会再弹一次）")}</span>
        </label>
        <div className="welcome__foot">{BRAND} · {VER}</div>
      </div>
    </div>
  );
}

/* ═══════════ 侧边栏 ═══════════ */
const NAV = [
  { id: "home", label: tr("总览"), icon: "home", desc: tr("启动与状态") },
  { id: "plugins", label: tr("插件"), icon: "plug", desc: tr("安装与开关") },
  { id: "bots", label: tr("人机"), icon: "bot", desc: tr("难度与物品") },
  { id: "rules", label: tr("规则"), icon: "scale", desc: tr("对局规则") },
  { id: "music", label: tr("音乐"), icon: "note", desc: tr("MVP 自定义音乐") },
  { id: "commands", label: tr("命令"), icon: "terminal", desc: tr("控制台速查") },
  { id: "guide", label: tr("教程"), icon: "book", desc: tr("跟着做一遍") },
  { id: "settings", label: tr("设置"), icon: "gear", desc: tr("路径与外观") },
];

function Sidebar({ view, onGo, state, manifest, guideSeen, hasUpdate }) {
  const okCount = manifest ? manifest.present : 0;
  const total = manifest ? manifest.total : 0;
  return (
    <aside className="side">
      <div className="side__brand">
        <b className="side__title side__title--lg">{tr("这里是菜单喵")}</b>
      </div>

      <nav className="side__nav">
        {NAV.map((n) => (
          <button key={n.id} className={`side__item ${view === n.id ? "is-on" : ""}`} onClick={() => onGo(n.id)}>
            <span className="side__ico">{I[n.icon](17)}</span>
            <span className="side__txt"><b>{n.label}</b><em>{n.desc}</em></span>
            {n.id === "guide" && !guideSeen && <span className="side__new" />}
            {n.id === "settings" && hasUpdate && <span className="side__new" />}
            <span className="side__glow" />
          </button>
        ))}
      </nav>

      <div className="side__foot">
        <div className="side__stat">
          <span className={`dot ${manifest?.ok ? "dot--ok" : manifest ? "dot--warn" : "dot--idle"}`} />
          <span>{manifest ? `${okCount}/${total}${tr(" 项就位")}` : tr("检测中…")}</span>
        </div>
      </div>
    </aside>
  );
}

/* ═══════════ 一键诊断（总览 / 设置共用） ═══════════ */
function Diagnose({ toast }) {
  const [open, setOpen] = useState(false);
  const [note, setNote] = useState("");
  const [dump, setDump] = useState(false);
  const [busy, setBusy] = useState(false);
  const [res, setRes] = useState(null);

  const run = async () => {
    setBusy(true);
    try {
      const r = await api.packDiagnostics(note, dump);
      if (r?.error) toast(r.error, "err");
      else { setRes(r); toast(tr("诊断包已生成")); }
    } catch (e) {
      toast(String(e).split("\n")[0] || tr("打包失败"), "err");
    }
    setBusy(false);
  };

  const copyPath = async () => {
    if (!res?.path) return;
    try { await navigator.clipboard.writeText(res.path); toast(tr("路径已复制")); }
    catch { toast(tr("复制失败"), "err"); }
  };

  return (
    <section className="card">
      <div className="card__head">
        <span className="card__title">{tr("诊断")}</span>
        <span className="pill">{tr("一键诊断")}</span>
      </div>
      <p className="note">
        {tr("把日志、加载配置、崩溃记录打成一个压缩包，直接丢给你电脑上的 AI（Claude / ChatGPT / Copilot 都行）就能查。不用自己找文件，本机用户名和 SteamID 会自动打码。")}
      </p>

      {!open && !res && (
        <button className="btn btn--soft btn--block" onClick={() => setOpen(true)}>
          {I.shield(14)} {tr("打包诊断日志")}
        </button>
      )}

      {open && !res && (
        <div className="diag">
          <textarea
            className="diag__input"
            rows={3}
            maxLength={2000}
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder={tr("什么时候报的错？报错窗口写了什么？装之前干过什么？（可留空，但写了更好查）")}
          />
          <label className="diag__check">
            <input type="checkbox" checked={dump} onChange={(e) => setDump(e.target.checked)} />
            <span>
              {tr("附带最近一次崩溃转储")}
              <em>{tr("最多 48 MB，能定位崩在哪个模块；网慢可以不勾")}</em>
            </span>
          </label>
          <div className="grid2">
            <button className="btn btn--ghost" disabled={busy} onClick={() => { setOpen(false); setNote(""); }}>{tr("取消")}</button>
            <button className="btn btn--primary" disabled={busy} onClick={run}>
              {busy ? tr("正在收集…") : tr("生成压缩包")}
            </button>
          </div>
        </div>
      )}

      {res && (
        <div className="diag__out">
          <div className="diag__file">
            <b>{res.name}</b>
            <span>{(res.size / 1048576).toFixed(2)} MB · {res.included?.length || 0} {tr("项")}</span>
          </div>
          <div className="grid2">
            <button className="btn btn--primary" onClick={() => api.openFolder(res.dir)}>{tr("打开所在文件夹")}</button>
            <button className="btn btn--ghost" onClick={copyPath}>{tr("复制路径")}</button>
          </div>
          {res.findings?.length > 0 && (
            <ul className="diag__find">
              {res.findings.map((f, i) => <li key={i}>{f}</li>)}
            </ul>
          )}
          {res.skipped?.length > 0 && (
            <details className="diag__more">
              <summary>{tr("跳过的内容（")}{res.skipped.length}）</summary>
              <ul>{res.skipped.map((s, i) => <li key={i}><code>{s}</code></li>)}</ul>
            </details>
          )}
          <button className="linkbtn" onClick={() => { setRes(null); setOpen(false); setNote(""); setDump(false); }}>
            <span><b>{tr("重新打包")}</b><em>{tr("换了插件状态后再生成一份")}</em></span><i>↻</i>
          </button>
        </div>
      )}
    </section>
  );
}

/* ═══════════ 页：总览 ═══════════ */
function Home({ state, meta, refresh, softRefresh, toast }) {
  const [busy, setBusy] = useState("");
  const [core, setCore] = useState(null);
  const path = state.cs2Path || state.autoDetectedPath || "";
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const m = meta.manifest;

  const launch = async (mode) => {
    if (!path) { toast(tr("请先在设置里选择 CS2 目录"), "err"); return; }
    const r = await api.launchCs2(mode);
    if (r.success) toast(r.message); else toast(r.error || tr("启动失败"), "err");
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("总览")}</h1>
        <p>{tr("一键启动 · 状态校验 · 快速修复")}</p>
      </header>

      <section className="card card--hero">
        <div className="hero__row">
          <div className={`hero__dot ${m?.clean ? "is-idle" : m?.ok ? "is-ok" : m ? "is-bad" : ""}`} />
          <div className="hero__txt">
            <b>
              {!m ? tr("正在校验…")
                : m.clean ? tr("未安装（干净状态）")
                : m.ok ? tr("插件全部就位")
                : `${tr("缺 ")}${m.modMissing ?? m.missing.length}${tr(" 项插件文件")}`}
            </b>
            <span>
              {!m ? tr("读取安装清单")
                : m.clean ? `${m.total}${tr(" 项检查，其中 ")}${m.officialTotal}${tr(" 项是 Steam 原文件（gameinfo.gi 和 14 个 gamemode 配置），不需要删；其余 ")}${m.modTotal + 4}${tr(" 项是插件 / 资源，安装套件后会有。")}`
                : `${tr("插件 ")}${m.modPresent}/${m.modTotal}${tr(" · 官方文件 ")}${m.officialPresent}/${m.officialTotal}${tr(" · 图标 ")}${m.iconCount || 0}${tr(" 个")}`}
            </span>
          </div>
          <button className="btn btn--ghost btn--sm" onClick={() => refresh()}>{I.refresh(14)} {tr("重检")}</button>
        </div>
        {m?.misplaced && (
          <div className="alert alert--warn">
            <b>{tr("解压位置错误")}</b>
            <code>{m.misplaced}</code>
          </div>
        )}
        {m && !m.ok && !m.clean && m.missing.length > 0 && (
          <details className="alert alert--bad">
            <summary>{tr("缺失明细（")}{m.missing.length}）</summary>
            <ul>{m.missing.slice(0, 30).map((x, i) => <li key={i}><code>{x}</code></li>)}</ul>
          </details>
        )}
        {m && m.clean && (
          <p className="note">
            {m.total}{tr(" 项检查，其中 ")}{m.officialTotal}{tr(" 项是 Steam 原文件（gameinfo.gi 和 14 个 gamemode 配置），不需要删；其余 ")}{m.modTotal + 4}{tr(" 项是插件 / 资源，安装套件后会有。")}
          </p>
        )}
      </section>

      <section data-tour="launch" className="card">
        <div className="card__head"><span className="card__title">{I.play(15)} {tr("启动游戏")}</span></div>
        <div className="launch">
          <button className="launch-card launch-card--main" onClick={() => launch("insecure")}>
            <span className="launch-card__ico">{I.bolt(20)}</span>
            <span className="launch-card__t">{tr("插件模式")}</span>
            <span className="launch-card__d">{tr("挂载 Metamod · 人机增强生效")}</span>
          </button>
          <button className="launch-card" onClick={() => launch("normal")}>
            <span className="launch-card__ico launch-card__ico--calm">{I.shield(20)}</span>
            <span className="launch-card__t">{tr("官方原版")}</span>
            <span className="launch-card__d">{tr("摘掉挂载 · 纯原版")}</span>
          </button>
        </div>
      </section>

      <section data-tour="install" className="card">
        <div className="card__head"><span className="card__title">{tr("维护")}</span></div>
        <div className="grid2">
          <button className="btn btn--soft" disabled={busy === "install"} onClick={async () => {
            if (busy) return;
            setBusy("install");
            setCore(null);
            const r = await api.install(path);
            setBusy("");
            if (!r.success) return toast(r.error || tr("安装失败"), "err");
            await refresh();
            toast(`${tr("安装完成，")}${r.installedCount}${tr(" 项就绪")}`);
            setCore(r);
            if (r.coreFailed?.length) toast(`${tr("核心文件校验未通过：")}${r.coreFailed.join("；")}`, "err");
            else if (r.coreItems?.length) toast(`${tr("核心文件 ")}${r.coreItems.length}/${r.coreItems.length}${tr(" 校验通过")}`);
          }}>{busy === "install" ? tr("安装中…") : tr("安装 / 修复套件")}</button>
          <button className="btn btn--ghost" onClick={async () => {
            const r = await api.applyConfigs();
            if (r.success) { await refresh(); toast(tr("配置已重新下发")); } else toast(r.error || tr("失败"), "err");
          }}>{tr("重新下发配置")}</button>
        </div>
        <button className="btn btn--danger btn--block" style={{ marginTop: 10 }} onClick={async () => {
          if (!confirm(tr("彻底删除全部插件？\n\n会直接删掉 addons / overrides / 图标资源，不留备份。\n被覆盖的官方原文件会自动还原，Steam 不会要求重新下载。\n\n删除后要恢复，重新点「安装 / 修复套件」即可。"))) return;
          const r = await api.uninstall();
          if (r.error) return toast(r.error, "err");
          await refresh();
          if (r.success) {
            const n = (r.removed || []).length;
            toast(`${tr("已彻底删除 ")}${n}${tr(" 项，还原 ")}${r.restored ?? 0}${tr(" 个官方文件")}`);
            if (r.needsVerify) {
              toast(tr("没有官方文件快照，请在 Steam 里验证一次游戏文件完整性"), "err");
            }
          } else {
            toast(`${tr("部分失败：")}${(r.failed || []).join("; ")}`, "err");
          }
        }}>{I.trash(13)} {tr("彻底删除插件")}</button>

        {core && (
          <details className={`alert ${core.coreOk ? "alert--ok" : "alert--bad"}`} open={!core.coreOk}>
            <summary>
              {tr("核心文件校验")} {core.coreOk
                ? `${tr("全部通过（")}${core.coreItems?.length || 0}${tr(" 项）")}`
                : `${core.coreFailed?.length || 0}${tr(" 项异常")}`}
            </summary>
            <ul className="core-list">
              {(core.coreItems || []).map((it, i) => (
                <li key={i} className={it.ok ? "is-ok" : "is-bad"}>
                  <b>{it.name}</b>
                  <code>{it.note}</code>
                </li>
              ))}
            </ul>
          </details>
        )}
      </section>


      <section data-tour="skinweb" className="card card--tight">
        <button className="linkbtn" onClick={() => api.openUrl("https://inventory.cstrike.app")}>
          {I.cart(18)}
          <span><b>{tr("打开皮肤库存")}</b><em>inventory.cstrike.app</em></span>
          <i>↗</i>
        </button>
      </section>

      <section className="card card--tight">
        <div className="kv">
          <span>{tr("游戏目录")}</span><code className="kv__path">{path || tr("未设置")}</code>
        </div>
        <div className="kv">
          <span>{tr("框架")}</span><b>{ready ? tr("Metamod + CounterStrikeSharp 就绪") : tr("未安装")}</b>
        </div>
      </section>
    </div>
  );
}

/* ═══════════ 页：插件 ═══════════ */
function Plugins({ state, meta, refresh, refreshState, patchMeta, toast }) {
  const P = state.plugins || {};
  const cat = state.pluginCatalog || [];
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const [busy, setBusy] = useState("");

  const toggle = async (id, on) => {
    setBusy(id);
    const r = await api.togglePlugin(id, on);
    setBusy("");
    if (r.success) { await refreshState(); toast(on ? tr("已开启") : tr("已关闭")); }
    else toast(r.error || tr("操作失败"), "err");
  };

  const groups = [
    { key: "framework", label: tr("框架"), desc: tr("缺一不可，常驻") },
    { key: "ai", label: tr("AI 增强"), desc: tr("控制层 + 决策层") },
    { key: "visual", label: tr("外观展示"), desc: tr("徽章 · 图标 · 饰品") },
    { key: "feature", label: tr("功能模块"), desc: tr("附加能力") },
  ];

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("插件")}</h1>
        <p>{tr("清单校验 + 单项开关 · 运行中禁止改动")}</p>
      </header>

      <section data-tour="plugins-batch" className="card">
        <div className="card__head">
          <span className="card__title">{tr("批量")}</span>
          <span className="pill">{meta.manifest ? `${meta.manifest.present}/${meta.manifest.total}` : "—"}</span>
        </div>
        <div className="grid2">
          <button className="btn btn--soft" onClick={async () => {
            const r = await api.setAllPlugins(true);
            await refreshState(); toast(r.success ? tr("已全部开启") : r.error || tr("失败"));
          }}>{tr("全部开启")}</button>
          <button className="btn btn--ghost" onClick={async () => {
            const r = await api.setAllPlugins(false);
            await refreshState(); toast(r.success ? tr("已全部关闭") : r.error || tr("失败"));
          }}>{tr("全部关闭")}</button>
        </div>
      </section>

      <section className="card">
        {groups.map((g, gi) => {
          const list = cat.filter((p) => p.category === g.key);
          if (!list.length) return null;
          return (
            <div className="pluggroup" key={g.key}>
              <div className="pluggroup__head">
                <b>{g.label}</b>
                <span>{g.desc}</span>
              </div>
              <div className="rows">
                {list.map((p) => {
                  const st = P[p.id] || {};
                  return (
                    <div className="row" key={p.id}>
                      <div className="row__txt">
                        <b>{p.name}{p.alwaysOn && <i className="tag tag--lock">{tr("常驻")}</i>}</b>
                        <span>{p.desc}</span>
                      </div>
                      <Toggle on={!!st.enabled} disabled={!ready || p.alwaysOn || busy === p.id}
                        onChange={(v) => toggle(p.id, v)} />
                    </div>
                  );
                })}
              </div>
            </div>
          );
        })}
      </section>

      <BadgeCards meta={meta} patchMeta={patchMeta} toast={toast} />
    </div>
  );
}

/* ═══════════ 自定义难度 ═══════════ */
const TUNE_FIELDS = [
  { key: "skill", label: tr("技术水平"), min: 20, max: 100, step: 1, unit: "", hint: tr("综合枪法、听声、判断。真人 45–75") },
  { key: "aggression", label: tr("侵略性"), min: 0, max: 100, step: 1, unit: "", hint: tr("主动找人打的倾向。太高就全员前压，40–60 最像人") },
  { key: "reaction", label: tr("反应时间"), min: 0, max: 0.6, step: 0.01, unit: "s", hint: tr("看到人到开始动。真人 0.15–0.30 秒，0 就是外挂") },
  { key: "attackDelay", label: tr("开火延迟"), min: 0, max: 0.4, step: 0.01, unit: "s", hint: tr("反应过来到扣扳机。真人 0.05–0.15 秒") },
  { key: "teamwork", label: tr("团队配合"), min: 0, max: 100, step: 1, unit: "", hint: tr("共享信息、跟着队友走。70–90 比较自然") },
  { key: "aimError", label: tr("瞄准误差"), min: 0, max: 0.6, step: 0.01, unit: "", hint: tr("0 = 弹无虚发，0.2–0.3 才会打偏、需要拉枪") },
];

const TUNE_PRESETS = [
  { id: "human", label: tr("更像真人"), v: { skill: 62, aggression: 45, reaction: 0.22, attackDelay: 0.10, teamwork: 82, aimError: 0.28 } },
  { id: "balance", label: tr("平衡"), v: { skill: 66, aggression: 55, reaction: 0.18, attackDelay: 0.08, teamwork: 80, aimError: 0.22 } },
  { id: "hard", label: tr("硬核"), v: { skill: 78, aggression: 70, reaction: 0.11, attackDelay: 0.05, teamwork: 75, aimError: 0.12 } },
];

function BotTune({ toast, disabled }) {
  const [p, setP] = useState(null);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    api.getBotProfile().then((r) => { if (r && !r.error) setP(r); }).catch(() => {});
  }, []);

  const apply = async (vals) => {
    setBusy(true);
    const r = await api.setBotProfile(vals);
    setBusy(false);
    if (r.error) return toast(r.error, "err");
    setP((prev) => ({ ...prev, ...vals }));
    toast(r.message || tr("已写入"));
  };

  const setField = (k, v) => setP((prev) => ({ ...prev, [k]: v }));

  if (!p) return null;

  return (
    <section className="card">
      <div className="card__head">
        <span className="card__title">{tr("自定义难度")}</span>
        <span className="pill">{p.bots ? `${p.bots}${tr(" 个 bot")}` : tr("直接改 vpk")}</span>
      </div>
      <p className="note">
        {tr("直接改写")} <code>overrides/botprofile.vpk</code> {tr("里的参数，不走档位替换。改完重开游戏生效。")}
      </p>

      <div className="tune-presets">
        {TUNE_PRESETS.map((ps) => (
          <button key={ps.id} className="btn btn--ghost" disabled={disabled || busy}
            onClick={() => apply({ ...ps.v })}>{ps.label}</button>
        ))}
      </div>

      <button className="btn btn--soft btn--block" style={{ marginTop: 8 }}
        onClick={() => setOpen((v) => !v)}>
        {open ? tr("收起参数") : tr("逐项调参")}
      </button>

      {open && (
        <div className="tune">
          {TUNE_FIELDS.map((f) => (
            <div className="tune__row" key={f.key}>
              <div className="tune__head">
                <span>{f.label}</span>
                <b>{typeof p[f.key] === "number" ? (f.step < 1 ? Number(p[f.key]).toFixed(2) : p[f.key]) : "—"}{f.unit}</b>
              </div>
              <input className="range" type="range" min={f.min} max={f.max} step={f.step}
                value={p[f.key] ?? f.min}
                onChange={(e) => setField(f.key, Number(e.target.value))} />
              <em>{f.hint}</em>
            </div>
          ))}
          <button className="btn btn--primary btn--block" disabled={busy} onClick={() => apply(p)}>
            {busy ? tr("写入中…") : tr("写入游戏")}
          </button>
        </div>
      )}
    </section>
  );
}

/* ═══════════ 页：人机 ═══════════ */
const BOT_ITEMS = [
  { key: "weapons", label: tr("武器皮肤"), desc: "bot_randomizer weapons" },
  { key: "knives", label: tr("刀"), desc: "bot_randomizer knives" },
  { key: "gloves", label: tr("手套"), desc: "bot_randomizer gloves" },
  { key: "stickers", label: tr("贴纸"), desc: "bot_randomizer stickers" },
  { key: "charms", label: tr("挂件"), desc: "bot_randomizer charms" },
  { key: "agents", label: tr("人物模型"), desc: "bot_randomizer agents" },
  { key: "music", label: tr("音乐盒"), desc: "bot_randomizer music" },
  { key: "profiles", label: tr("身份伪装"), desc: tr("BotHider 原生插件开关") },
];

function Bots({ state, meta, refresh, refreshState, patchMeta, toast }) {
  const items = meta.items || {};
  const pre = meta.presets || {};
  const ready = !!(state.installed?.metamod && state.installed?.css);

  const setItem = async (k, v) => {
    const r = await api.setBotItem(k, v);
    if (r.error) return toast(r.error, "err");
    if (r.items) patchMeta({ items: r.items });
    toast(v ? tr("已开启") : tr("已关闭"));
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("人机")}</h1>
        <p>{tr("难度 · 饰品随机 · 瞄准与投掷物预设")}</p>
      </header>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("难度档位")}</span></div>
        <Seg disabled={!ready}
          value={state.difficulty || "Medium"}
          options={[
            { value: "Low", label: tr("低") },
            { value: "Medium", label: tr("中") },
            { value: "High", label: tr("高") },
          ].filter((o) => !meta.difficulty?.available?.length || meta.difficulty.available.includes(o.value))}
          onChange={async (v) => {
            const r = await api.setDifficulty(v);
            if (r.error) return toast(r.error, "err");
            await refreshState();
            toast(`${tr("难度已切到 ")}${v === "Low" ? tr("低") : v === "Medium" ? tr("中") : tr("高")}${tr("（已写入 overrides + gamemode cfg，重启 CS2 生效）")}`);
          }} />
        <p className="note">
          {tr("切换会同时改三处：")}<code>overrides/botprofile.vpk</code>、<code>xuerong_manager.cfg</code>{tr("、\n          以及所有")} <code>gamemode_*.cfg</code> {tr("里的")} <code>bot_difficulty</code> / <code>custom_bot_difficulty</code>{tr("。\n          改完必须完全退出 CS2 再启动，进图时游戏模式 cfg 才不会把难度覆盖回去。")}
        </p>
        <p className="note">
          {tr("难度档位本质就是换一份")} <code>botprofile.vpk</code>{tr("。真正决定人机性格、瞄准参数和起枪的是 VPK 里的栏目，\n          想精确控制就用下面的「人机起枪」直接改，改完能预览动了哪几行。")}
        </p>
      </section>

      <BotTune toast={toast} disabled={!ready} />

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("Bot 物品")}</span><span className="card__hint">{tr("写进 xuerong_manager.cfg")}</span></div>
        <div className="rows">
          {BOT_ITEMS.map((it) => (
            <div className="row" key={it.key}>
              <div className="row__txt"><b>{it.label}</b><span>{it.desc}</span></div>
              <Toggle on={!!items[it.key]} disabled={!ready} onChange={(v) => setItem(it.key, v)} />
            </div>
          ))}
        </div>
        <p className="note">{tr("bot_randomizer 是 CSS 命令，需在游戏内")} <code>exec my_bot_normal_config</code> {tr("或由管理器写入后开一局生效。")}</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("瞄准模式")}</span></div>
        <Seg disabled={!ready}
          value={pre.aim || "mixed"}
          options={[{ value: "head", label: tr("锁头") }, { value: "mixed", label: tr("混合") }, { value: "body", label: tr("锁身") }]}
          onChange={async (v) => { const r = await api.setAim(v); if (r.error) return toast(r.error, "err"); patchMeta({ presets: { ...pre, aim: r.aim || v } }); toast(`${tr("瞄准：")}${v}`); }} />
        <p className="note">{tr("对应游戏内")} <code>bot_aim head|mixed|body</code>。</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("投掷物强度")}</span></div>
        <Seg disabled={!ready}
          value={pre.nades || "normal"}
          options={[{ value: "off", label: tr("关") }, { value: "less", label: tr("少") }, { value: "normal", label: tr("正常") }, { value: "more", label: tr("多") }, { value: "max", label: tr("满") }]}
          onChange={async (v) => { const r = await api.setNades(v); if (r.error) return toast(r.error, "err"); patchMeta({ presets: { ...pre, nades: r.nades || v } }); toast(`${tr("投掷物：")}${v}`); }} />
        <p className="note">{tr("对应游戏内")} <code>bot_nades off|less|normal|more|max</code>。</p>
      </section>
      <WeaponEditor toast={toast} disabled={!ready} />
    </div>
  );
}

/* ═══════════ 页：规则 ═══════════ */
function Rules({ state, meta, refresh, patchMeta, toast }) {
  const r = meta.rules || {};
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const [c4, setC4] = useState(r.c4Timer ?? 50);
  const [mol, setMol] = useState(r.molotovMultiplier ?? 0.5);

  useEffect(() => { if (r.c4Timer != null) setC4(r.c4Timer); }, [r.c4Timer]);
  useEffect(() => { if (r.molotovMultiplier != null) setMol(r.molotovMultiplier); }, [r.molotovMultiplier]);

  const set = async (k, v) => {
    const res = await api.setRule(k, v);
    if (res.error) return toast(res.error, "err");
    if (res.rules) patchMeta({ rules: res.rules });
    toast(tr("规则已更新"));
  };

  const ROWS = [
    { key: "overtime", label: tr("加时赛"), desc: tr("打平后进入加时 · mp_overtime_enable") },
    { key: "friendly_fire", label: tr("友军伤害"), desc: tr("队友之间互相造成伤害 · mp_friendlyfire") },
    { key: "boosting", label: tr("允许叠人"), desc: tr("关掉则敌人不可互相穿过 · mp_solid_enemies") },
    { key: "bhop", label: tr("连跳"), desc: tr("空中连续跳跃不掉速 · sv_enablebunnyhopping") },
  ];

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("规则")}</h1>
        <p>{tr("写进三套预设 cfg + 管理器 cfg，开一局就生效")}</p>
      </header>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("对局规则")}</span></div>
        <div className="rows">
          {ROWS.map((row) => (
            <div className="row" key={row.key}>
              <div className="row__txt"><b>{row.label}</b><span>{row.desc}</span></div>
              <Toggle on={!!r[row.key === "friendly_fire" ? "friendlyFire" : row.key]} disabled={!ready}
                onChange={(v) => set(row.key, v)} />
            </div>
          ))}
        </div>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("C4 倒计时")}</span><span className="pill">{c4}s</span></div>
        <input className="range" type="range" min="10" max="120" step="5" value={c4}
          onChange={(e) => setC4(Number(e.target.value))}
          onPointerUp={async () => {
            const res = await api.setC4Timer(c4);
            if (res.error) return toast(res.error, "err");
            if (res.rules) patchMeta({ rules: res.rules });
            toast(`${tr("C4 倒计时 ")}${c4}${tr(" 秒")}`);
          }} />
        <p className="note">{tr("默认 40 秒，范围 10–120。写 mp_c4timer。")}</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("燃烧瓶伤害倍率")}</span><span className="pill">×{mol.toFixed(2)}</span></div>
        <input className="range" type="range" min="0.1" max="3" step="0.05" value={mol}
          onChange={(e) => setMol(Number(e.target.value))}
          onPointerUp={async () => {
            const res = await api.setMolotovMultiplier(mol);
            if (res.error) return toast(res.error, "err");
            if (res.rules) patchMeta({ rules: res.rules });
            toast(`${tr("燃烧瓶 ×")}${mol.toFixed(2)}`);
          }} />
        <p className="note">{tr("按原版 40 点伤害换算，同时写 inferno_damage 与 inferno_damage_ct。")}</p>
      </section>

      <button className="btn btn--ghost btn--block" onClick={async () => {
        const res = await api.resetRules();
        if (res.error) return toast(res.error, "err");
        if (res.rules) patchMeta({ rules: res.rules });
        toast(tr("规则已恢复默认"));
      }}>{tr("恢复默认规则")}</button>
    </div>
  );
}


/* ═══════════ 页面错误边界：单页崩了不白屏 ═══════════ */
class PageBoundary extends React.Component {
  constructor(props) { super(props); this.state = { err: null }; }
  static getDerivedStateFromError(err) { return { err }; }
  componentDidCatch(err, info) { try { console.error("[page]", err, info); } catch {} }
  render() {
    if (!this.state.err) return this.props.children;
    return (
      <div className="page">
        <header className="page__head"><h1>{tr("这一页出错了")}</h1><p>{tr("其他页面不受影响，可以切走或重试")}</p></header>
        <section className="card">
          <div className="card__head"><span className="card__title">{tr("错误信息")}</span></div>
          <p className="note" style={{ marginTop: 0, wordBreak: "break-all" }}>
            {String(this.state.err?.message || this.state.err)}
          </p>
          <button className="btn btn--soft" onClick={() => this.setState({ err: null })}>{tr("重试这一页")}</button>
        </section>
      </div>
    );
  }
}

/* ═══════════ 人机起枪编辑器（直接改 botprofile.vpk） ═══════════ */
const WEAPON_LABEL = {
  awp: "AWP", ssg08: "SSG08", scar20: "SCAR-20", g3sg1: "G3SG1",
  ak47: "AK-47", m4a1: "M4A4", m4a1_silencer: "M4A1-S", aug: "AUG", sg556: "SG553",
  famas: "FAMAS", galilar: "Galil", deagle: tr("沙鹰"), tec9: "Tec-9", fiveseven: "57",
  p250: "P250", elite: tr("双枪"), p90: "P90", mp9: "MP9", mac10: "MAC-10",
  mp7: "MP7", mp5sd: "MP5-SD", ump45: "UMP-45", bizon: tr("PP-野牛"),
  xm1014: "XM1014", nova: "Nova", mag7: "MAG-7", sawedoff: tr("截短霰弹"),
  negev: "Negev", m249: "M249",
};

function WeaponEditor({ toast, disabled }) {
  const [d, setD] = useState(null);
  const [draft, setDraft] = useState([]);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState("");

  const load = useCallback(async () => {
    const r = await api.vpkWeaponsRead();
    setD(r);
    if (r?.ok) setDraft(r.templates.map((t) => ({ name: t.name, weapons: [...t.weapons] })));
  }, []);
  useEffect(() => { load(); }, [load]);

  const orig = useMemo(() => {
    const m = {};
    (d?.templates || []).forEach((t) => { m[t.name] = t.weapons; });
    return m;
  }, [d]);

  const changes = useMemo(() => {
    const out = [];
    draft.forEach((t) => {
      const before = orig[t.name] || [];
      const removed = before.filter((w) => !t.weapons.includes(w));
      const added = t.weapons.filter((w) => !before.includes(w));
      if (removed.length || added.length) out.push({ name: t.name, removed, added });
    });
    return out;
  }, [draft, orig]);

  const edit = (name, fn) => setDraft((list) => list.map((t) => (t.name === name ? { ...t, weapons: fn(t.weapons) } : t)));

  const banFromAll = (weapons) => {
    setDraft((list) => list.map((t) => ({ ...t, weapons: t.weapons.filter((w) => !weapons.includes(w)) })));
    toast(`${tr("已从所有栏目移除 ")}${weapons.map((w) => WEAPON_LABEL[w] || w).join(" / ")}`, "ok");
  };

  const reset = () => {
    setDraft((d?.templates || []).map((t) => ({ name: t.name, weapons: [...t.weapons] })));
    toast(tr("已还原为当前 VPK 里的内容"), "ok");
  };

  const apply = async () => {
    if (!changes.length) return toast(tr("没有改动"), "err");
    setBusy(true);
    try {
      const r = await api.vpkWeaponsApply(draft);
      if (r?.ok === false) return toast(r.message || tr("写入失败"), "err");
      toast(r.message || tr("已写入"), "ok");
      await load();
    } finally { setBusy(false); }
  };

  if (!d) return null;
  if (!d.ok) {
    return (
      <section className="card">
        <div className="card__head"><span className="card__title">{tr("人机起枪")}</span></div>
        <p className="note" style={{ marginTop: 0 }}>{d.message || tr("读取失败")}</p>
      </section>
    );
  }

  return (
    <section className="card">
      <div className="card__head wphead">
        <span className="card__title">{tr("人机起枪")}</span>
        <button
          className="btn btn--primary btn--sm wpapply"
          disabled={disabled || busy || !changes.length || d.cs2Running}
          onClick={apply}
        >
          {busy ? tr("写入中…") : changes.length ? `${tr("写入 VPK（")}${changes.length}${tr(" 项）")}` : tr("没有改动")}
        </button>
        <span className="card__hint">{tr("直接改 overrides/botprofile.vpk ·")} {d.botCount} {tr("个人机共用这")} {d.templates.length} {tr("个栏目")}</span>
        <button className="btn btn--ghost btn--sm" onClick={load}>{tr("刷新")}</button>
      </div>
      <p className="note" style={{ marginTop: 0 }}>
        {tr("每个栏目是一类人机的武器优先级。删掉一把枪，这一类人机就不再起它；顺序就是买枪优先顺序。")}
        {d.cs2Running ? tr(" CS2 正在运行，先退出才能写入。") : ""}
      </p>

      <div className="wpquick">
        <span>{tr("一键禁：")}</span>
        <button className="btn btn--ghost btn--sm" onClick={() => banFromAll(["awp", "ssg08", "scar20", "g3sg1"])}>{tr("全部狙击枪")}</button>
        <button className="btn btn--ghost btn--sm" onClick={() => banFromAll(["negev", "m249"])}>{tr("机枪")}</button>
        <button className="btn btn--ghost btn--sm" onClick={() => banFromAll(["xm1014", "nova", "mag7", "sawedoff"])}>{tr("霰弹枪")}</button>
        <button className="btn btn--ghost btn--sm" onClick={reset}>{tr("还原改动")}</button>
      </div>

      <div className="wplist">
        {draft.map((t) => {
          const isOpen = open === t.name;
          const before = orig[t.name] || [];
          const removed = before.filter((w) => !t.weapons.includes(w));
          const added = t.weapons.filter((w) => !before.includes(w));
          return (
            <div className={`wprow ${isOpen ? "is-open" : ""}`} key={t.name}>
              <button className="wprow__head" onClick={() => setOpen(isOpen ? "" : t.name)}>
                <b>{t.name}</b>
                <span className="wprow__meta">
                  {t.weapons.length} {tr("把")}
                  {removed.length > 0 && <i className="wpdiff wpdiff--del">-{removed.length}</i>}
                  {added.length > 0 && <i className="wpdiff wpdiff--add">+{added.length}</i>}
                </span>
                <span className="wprow__arrow">{isOpen ? tr("收起") : tr("编辑")}</span>
              </button>
              {isOpen && (
                <div className="wprow__body">
                  <div className="wpcells">
                    {t.weapons.map((w, idx) => (
                      <span className="wcell" key={w + idx}>
                        <i>{idx + 1}</i>
                        {WEAPON_LABEL[w] || w}
                        <button onClick={() => edit(t.name, (list) => list.filter((x) => x !== w))} title={tr("移除")}>×</button>
                      </span>
                    ))}
                    {t.weapons.length === 0 && <span className="note" style={{ margin: 0 }}>{tr("这个栏目已清空，人机会退回默认随机买枪。")}</span>}
                  </div>
                  <div className="wpadd">
                    <select
                      value=""
                      onChange={(e) => {
                        const w = e.target.value;
                        if (!w) return;
                        edit(t.name, (list) => [...list, w]);
                      }}
                    >
                      <option value="">{tr("+ 添加武器…")}</option>
                      {(d.allWeapons || [])
                        .filter((w) => !t.weapons.includes(w))
                        .map((w) => <option key={w} value={w}>{WEAPON_LABEL[w] || w}</option>)}
                    </select>
                    <span className="note" style={{ margin: 0 }}>{tr("第 1 把是首选，越靠前越优先")}</span>
                  </div>
                </div>
              )}
            </div>
          );
        })}
      </div>

      {changes.length > 0 && (
        <div className="wppreview">
          <b>{tr("将改动")} {changes.length} {tr("个栏目（写回 VPK 的 Template 块）")}</b>
          {changes.map((c) => (
            <div className="wppreview__line" key={c.name}>
              <span>{c.name}</span>
              {c.removed.length > 0 && <em className="wpdiff wpdiff--del">{tr("删")} {(c.removed.map((w) => WEAPON_LABEL[w] || w)).join("、")}</em>}
              {c.added.length > 0 && <em className="wpdiff wpdiff--add">{tr("加")} {(c.added.map((w) => WEAPON_LABEL[w] || w)).join("、")}</em>}
            </div>
          ))}
        </div>
      )}

      <div className="wpfoot">
        <span className="note" style={{ margin: 0 }}>
          {d.vpkPath} {tr("· 改动前会自动留一份 .bak_before_weapons")}
        </span>
        <span className="wpfoot__state">
          {changes.length ? `${tr("已改 ")}${changes.length}${tr(" 个栏目，点右上角写入")}` : tr("尚无改动")}
        </span>
      </div>
    </section>
  );
}

/* ═══════════ 外观卡片（嵌在插件页） ═══════════ */
function BadgeCards({ meta, patchMeta, toast }) {
  return (
    <section className="card">
      <div className="card__head">
        <span className="card__title">{tr("击杀图标")}</span>
        <span className="card__hint">{tr("游戏内指令")}</span>
      </div>
      <p className="note" style={{ marginTop: 0 }}>
        {tr("进游戏后按 ~ 打开控制台，先发")} <code>!weaponkill</code>{tr("，再发")} <code>!编号</code>{tr("，图标立刻换。")}
      </p>
      <button className="btn btn--soft btn--block" onClick={async () => {
        try { await navigator.clipboard.writeText("!weaponkill"); toast(tr("已复制 !weaponkill")); }
        catch { toast(tr("复制失败，手动输入"), "err"); }
      }}>{I.copy(13)} {tr("复制 !weaponkill")}</button>
      <button className="linkbtn" style={{ marginTop: 8 }} onClick={() => api.openUrl("https://inventory.cstrike.app")}>
        {I.cart(18)}
        <span><b>{tr("打开皮肤库存")}</b><em>inventory.cstrike.app</em></span>
        <i>↗</i>
      </button>
    </section>
  );
}

/* ═══════════ 页：MVP 音乐（v1.3） ═══════════ */
const MUSIC_SWITCHES = [
  { key: "Enabled", label: tr("启用插件"), desc: tr("关掉则完全不替换 MVP 音乐") },
  { key: "IncludeBots", label: tr("人机拿 MVP 也放"), desc: tr("默认关：只有真人拿 MVP 才播") },
  { key: "MuteVanillaOnMvp", label: tr("播时压掉官方音乐"), desc: tr("默认关，避免影响别人的音量") },
  { key: "AllowPlayersToAdjust", label: tr("允许玩家自己调音量"), desc: tr("聊天框 !mvpvol") },
];

function fmtSize(n) {
  const b = Number(n) || 0;
  if (b >= 1048576) return `${(b / 1048576).toFixed(2)} MB`;
  if (b >= 1024) return `${(b / 1024).toFixed(0)} KB`;
  return `${b} B`;
}
function fmtDelta(v, hasPrev) {
  if (!hasPrev || v == null) return { t: "—", cls: "" };
  const n = Number(v);
  if (n === 0) return { t: tr("— 无变化"), cls: "" };
  return n > 0
    ? { t: `▲ +${fmtSize(n)}`, cls: "up" }
    : { t: `▼ -${fmtSize(-n)}`, cls: "down" };
}
function relative(s) {
  if (!s) return "—";
  const d = new Date(String(s).replace(/-/g, "/"));
  if (isNaN(d.getTime())) return s;
  const diff = (Date.now() - d.getTime()) / 1000;
  if (diff < 600) return tr("刚刚");
  if (diff < 3600) return `${Math.floor(diff / 60)}${tr(" 分钟前")}`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}${tr(" 小时前")}`;
  if (diff < 86400 * 7) return `${Math.floor(diff / 86400)}${tr(" 天前")}`;
  return s;
}

function Music({ toast }) {
  const [d, setD] = useState(null);
  const [busy, setBusy] = useState("");
  const [log, setLog] = useState("");
  const [sel, setSel] = useState("");
  const [confirmDel, setConfirmDel] = useState("");
  const [delInput, setDelInput] = useState("");
  const [showMake, setShowMake] = useState(false);
  const [form, setForm] = useState({ pack: "", label: "", input: "", start: 0, duration: 8, fade: 1.5, steamId: "" });

  const load = useCallback(async () => {
    const r = await api.mvpState();
    if (!r.ok) { setD(r); return; }
    setD(r);
    setForm((f) => ({ ...f, steamId: f.steamId || r.steamId || "" }));
    if (!sel && r.packs?.length) setSel(r.packs[0].pack);
  }, [sel]);
  useEffect(() => { load(); }, []); // eslint-disable-line

  const cfg = d?.config || {};
  const packs = d?.packs || [];
  const env = d?.env || {};

  const run = async (key, fn, okMsg) => {
    setBusy(key);
    setLog("");
    try {
      const r = await fn();
      setLog(r?.message || r?.log || (r?.ok ? okMsg : r?.error || JSON.stringify(r)));
      if (r?.ok === false) toast(r.message || r.error || tr("操作失败"), "err");
      else if (okMsg) toast(okMsg);
      await load();
      return r;
    } finally { setBusy(""); }
  };

  const saveCfg = (patch) => run("cfg", () => api.mvpSetConfig(patch), tr("已保存"));

  const pickAudio = async () => {
    try {
      const { open } = await import("@tauri-apps/plugin-dialog");
      const f = await open({ multiple: false, filters: [{ name: tr("音频"), extensions: ["mp3", "wav", "flac", "m4a", "ogg", "aac"] }] });
      if (f) setForm((x) => ({ ...x, input: f }));
    } catch { toast(tr("文件选择不可用"), "err"); }
  };
  const pickZip = async () => {
    try {
      const { open } = await import("@tauri-apps/plugin-dialog");
      const fs = await open({ multiple: true, filters: [{ name: tr("音效包"), extensions: ["zip"] }] });
      if (!fs || !fs.length) return;
      await run("import", () => api.mvpImportPack(Array.isArray(fs) ? fs : [fs]), tr("导入完成"));
    } catch { toast(tr("文件选择不可用"), "err"); }
  };
  const pickVsnd = async () => {
    try {
      const { open } = await import("@tauri-apps/plugin-dialog");
      const fs = await open({ multiple: true, filters: [{ name: tr("已编译音效"), extensions: ["vsnd_c"] }] });
      if (!fs || !fs.length || !form.pack) return toast(tr("先填包名再选文件"), "err");
      await run("place", () => api.mvpPlacePack(form.pack, Array.isArray(fs) ? fs : [fs]), tr("已放置"));
    } catch { toast(tr("文件选择不可用"), "err"); }
  };

  const doMake = async () => {
    if (!form.pack.trim()) return toast(tr("包名不能空"), "err");
    if (!form.input) return toast(tr("先选音频文件"), "err");
    setLog(tr("切片 → 编译 → 部署，可能要 10-60 秒…"));
    await run("make", () => api.mvpMakePack({
      pack: form.pack.trim(), label: form.label.trim(), input: form.input,
      start: Number(form.start) || 0, duration: Number(form.duration) || 8,
      fade: Number(form.fade) || 0, steamId: form.steamId.trim(),
    }), tr("制作完成"));
  };

  const doDelete = async () => {
    if (delInput !== confirmDel) return toast(tr("包名输入不一致"), "err");
    const p = confirmDel;
    const r = await run("del", () => api.mvpDeletePack(p), tr("已删除"));
    if (r?.ok !== false) { setConfirmDel(""); setDelInput(""); setSel(""); }
  };

  const doShare = async (all) => {
    const list = all ? [] : (sel ? [sel] : []);
    if (!all && !list.length) return toast(tr("先选一个包"), "err");
    await run("share", () => api.mvpSharePack(list, all), tr("已导出到桌面"));
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("MVP 音乐")}</h1>
        <p>{tr("只替换回合 MVP 那一段 · 制作 / 查看 / 分享 / 导入")}</p>
      </header>

      <section data-tour="music-status" className="card">
        <div className="card__head">
          <span className="card__title">{tr("状态")}</span>
          <span className={`pill ${d?.installed ? "pill--ok" : "pill--bad"}`}>{d?.installed ? tr("已安装 v1.3.0") : tr("未安装")}</span>
        </div>
        <p className="note" style={{ marginTop: 0 }}>
          {tr("音效包")} <b>{packs.length}</b> {tr("个 · 共")} <b>{fmtSize(d?.totalBytes)}</b> {tr("· 默认音量")} <b>{Math.round((cfg.DefaultVolume ?? 1) * 100)}%</b>
          {d?.defaultPack ? `${tr(" · 默认包 ")}${d.defaultPack}` : ""}
        </p>
        <p className="note">
          {tr("本机 SteamID：")}<b className="mono">{d?.mySteamId || tr("未识别")}</b>
          <button className="btn btn--ghost" style={{ marginLeft: 8, padding: "2px 8px", fontSize: 10 }} onClick={() => load()}>{tr("刷新")}</button>
        </p>
        <div className="grid3">
          <button className="btn btn--soft" disabled={busy === "install"} onClick={() => run("install", api.mvpInstall, tr("安装完成"))}>
            {busy === "install" ? "…" : tr("安装 / 更新")}</button>
          <button className="btn btn--soft" disabled={busy === "uninstall"} onClick={() => run("uninstall", api.mvpUninstall, tr("已卸载"))}>
            {busy === "uninstall" ? "…" : tr("卸载")}</button>
          <button className="btn btn--ghost" onClick={() => api.mvpOpenPacksDir()}>{tr("打开音效目录")}</button>
        </div>
        <p className="note">
          {tr("CS2 创意工坊工具：")}<b className={env.resourcecompiler ? "ok" : "bad"}>{env.resourcecompiler ? tr("已装") : tr("缺失")}</b>
          {" · "}ffmpeg：<b className={env.ffmpeg ? "ok" : "bad"}>{env.ffmpeg ? tr("已就绪") : tr("未找到")}</b>
          {env.resourcecompiler ? "" : tr(" · 制作功能不可用，但可以导入别人做好的包")}
        </p>
      </section>

      <section data-tour="music-packs" className="card">
        <div className="card__head">
          <span className="card__title">{tr("音效包")}</span>
          <span className="pill">{tr("按更新时间倒序")}</span>
        </div>
        {packs.length === 0 && <p className="note">{tr("还没有音效包。做一个新的，或导入朋友发来的 zip。")}</p>}
        {packs.length > 0 && (
          <div className="packtable">
            {packs.map((p) => {
              const delta = fmtDelta(p.deltaBytes, p.previousSizeBytes != null);
              const fresh = p.updatedAt && (Date.now() - new Date(String(p.updatedAt).replace(/-/g, "/")).getTime()) < 600000;
              return (
                <div className={`packrow ${sel === p.pack ? "is-on" : ""} ${p.isMine ? "is-mine" : ""}`} key={p.pack} onClick={() => setSel(p.pack)}>
                  <div className="packrow__main">
                    <b className="mono">{p.isMine && <i className="tag tag--mine">{tr("你的")}</i>}{p.pack}{p.isDefault ? <i className="tag">{tr("默认")}</i> : ""}</b>
                    <em>{p.label || "—"} · {p.fileCount} {tr("个音效")}</em>
                  </div>
                  <div className="packrow__meta">
                    <span className={fresh ? "ok" : ""}>
                      {p.updatedAt || "—"} {p.updatedAt ? `(${relative(p.updatedAt)})` : ""}
                      {fresh && <i className="tag tag--fresh">{tr("刚刚更新")}</i>}
                    </span>
                    {p.suggestBind && d?.mySteamId && (
                      <button className="btn btn--ghost" style={{ padding: "2px 7px", fontSize: 10 }}
                        onClick={(e) => { e.stopPropagation(); run("bind", () => api.mvpSetSteamId(d.mySteamId, p.pack), tr("已绑定到你的账号")); }}>
                        {tr("建议绑定你：")}{String(d.mySteamId).slice(0, 9)}…
                      </button>
                    )}
                    <span>{fmtSize(p.sizeBytes)} <b className={delta.cls}>{delta.t}</b></span>
                    <span className={p.steamIds?.length ? "" : "bad"}>
                      {p.steamIds?.length ? p.steamIds.join(", ") : tr("未绑定")}
                    </span>
                    <span className={p.backupSynced ? "ok" : "bad"}>{p.backupSynced ? tr("已同步") : tr("未同步")}</span>
                  </div>
                  <div className="packrow__ops">
                    <button className="btn btn--ghost" onClick={(e) => { e.stopPropagation(); setSel(p.pack); doShare(false); }}>{tr("分享")}</button>
                    <button className="btn btn--ghost" onClick={(e) => { e.stopPropagation(); saveCfg({ DefaultPack: p.pack }); }}>{tr("设为默认")}</button>
                    <button className="btn btn--ghost btn--danger" onClick={(e) => { e.stopPropagation(); setConfirmDel(p.pack); setDelInput(""); }}>{tr("删除")}</button>
                  </div>
                </div>
              );
            })}
          </div>
        )}
        <div className="grid3" style={{ marginTop: 10 }}>
          <button data-tour="music-make" className="btn btn--primary" onClick={() => setShowMake((v) => !v)}>{showMake ? tr("收起制作") : tr("制作新音效包")}</button>
          <button className="btn btn--soft" disabled={busy === "import"} onClick={pickZip}>{tr("导入音效包")}</button>
          <button className="btn btn--soft" disabled={busy === "share"} onClick={() => doShare(true)}>{tr("分享全部")}</button>
        </div>
      </section>

      {confirmDel && (
        <section className="card card--danger">
          <div className="card__head"><span className="card__title">{tr("确认删除「")}{confirmDel}」</span></div>
          <p className="note">{tr("会同时清掉主音效目录、备份目录、两处编译中转目录，并把配置里绑到这个包的 SteamID 全部移除。")}</p>
          <p className="note">{tr("输入包名")} <code>{confirmDel}</code> {tr("才能确认：")}</p>
          <input className="input" value={delInput} onChange={(e) => setDelInput(e.target.value)} placeholder={confirmDel} />
          <div className="grid2" style={{ marginTop: 8 }}>
            <button className="btn btn--ghost" onClick={() => setConfirmDel("")}>{tr("取消")}</button>
            <button className="btn btn--danger" disabled={delInput !== confirmDel || busy === "del"} onClick={doDelete}>{tr("确认删除")}</button>
          </div>
        </section>
      )}

      {showMake && (
        <section className="card">
          <div className="card__head"><span className="card__title">{tr("制作音效包")}</span></div>
          <div className="grid2">
            <div className="field">
              <label>{tr("包名（英文/数字）")}</label>
              <input className="input" value={form.pack} onChange={(e) => setForm({ ...form, pack: e.target.value })} placeholder="me" />
            </div>
            <div className="field">
              <label>{tr("显示名（可空）")}</label>
              <input className="input" value={form.label} onChange={(e) => setForm({ ...form, label: e.target.value })} placeholder={tr("我的MVP")} />
            </div>
          </div>
          <div className="field">
            <label>{tr("音频文件")}</label>
            <button className="btn btn--ghost btn--block" onClick={pickAudio}>{form.input || tr("选择 mp3 / wav / flac…")}</button>
          </div>
          <div className="grid3">
            <div className="field"><label>{tr("起点秒")}</label><input className="input" type="number" min="0" step="0.5" value={form.start} onChange={(e) => setForm({ ...form, start: e.target.value })} /></div>
            <div className="field"><label>{tr("时长（4~12）")}</label><input className="input" type="number" min="4" max="12" step="0.5" value={form.duration} onChange={(e) => setForm({ ...form, duration: e.target.value })} /></div>
            <div className="field"><label>{tr("淡出秒")}</label><input className="input" type="number" min="0" max="5" step="0.5" value={form.fade} onChange={(e) => setForm({ ...form, fade: e.target.value })} /></div>
          </div>
          <div className="field">
            <label>{tr("绑定 SteamID（留空跳过；进服 !mvpwhoami 可查）")}</label>
            <div className="grid2">
              <input className="input" value={form.steamId} onChange={(e) => setForm({ ...form, steamId: e.target.value })} placeholder="7656119…" />
              <button className="btn btn--ghost" onClick={async () => {
                const r = await api.mvpGetSteamId();
                if (!r.ok) return toast(r.message, "err");
                setForm((f) => ({ ...f, steamId: r.steamId }));
                toast(tr("已读取当前 Steam 账号"));
              }}>{tr("读取我的 SteamID")}</button>
            </div>
          </div>
          <div className="grid2">
            <button className="btn btn--primary" disabled={busy === "make" || !env.resourcecompiler} onClick={doMake}>
              {busy === "make" ? tr("处理中…") : tr("切片 → 编译 → 部署")}</button>
            <button className="btn btn--soft" onClick={pickVsnd}>{tr("放置已编译的 .vsnd_c")}</button>
          </div>
          {!env.resourcecompiler && (
            <p className="note">
              <b>{tr("没有编译器时的三条路：")}</b><br />
              {tr("A) 只是进服玩 → 点「导入音效包」把开服的人给你的 zip 拖进来。")}<br />
              {tr("B) 想有自己的歌 → 把「歌 + 起点 + 时长 + SteamID」发给开服的人帮你编译。")}<br />
              {tr("C) 自己编译 → Steam 库筛选「工具」安装 Counter-Strike 2 Workshop Tools（约 2 GB）。")}
            </p>
          )}
        </section>
      )}

      <section data-tour="music-rules" className="card">
        <div className="card__head"><span className="card__title">{tr("播放规则")}</span></div>
        <div className="rows">
          {MUSIC_SWITCHES.map((s) => (
            <div className="row" key={s.key}>
              <div className="row__txt"><b>{s.label}</b><span>{s.desc}</span></div>
              <Toggle on={cfg[s.key] === true} onChange={(v) => saveCfg({ [s.key]: v })} />
            </div>
          ))}
          <div className="row">
            <div className="row__txt"><b>{tr("路人拿 MVP")}</b><span>{tr("没绑定音效包的人")}</span></div>
            <Seg value={cfg.UnboundMvp || "silent"} options={[{ value: "silent", label: tr("静音") }, { value: "default", label: tr("放默认包") }]}
              onChange={(v) => saveCfg({ UnboundMvp: v })} />
          </div>
          <div className="row">
            <div className="row__txt"><b>{tr("默认音量")}</b><span>{tr("玩家没自己调过时的音量")}</span></div>
            <input className="range" type="range" min="0" max="1" step="0.05" style={{ width: 140 }}
              value={cfg.DefaultVolume ?? 1} onChange={(e) => saveCfg({ DefaultVolume: Number(e.target.value) })} />
          </div>
        </div>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("SteamID 绑定")}</span></div>
        <div className="grid2">
          <input className="input" id="mvp-sid" placeholder={tr("7656119…（17 位）")} />
          <button className="btn btn--soft" onClick={async () => {
            const sid = document.getElementById("mvp-sid")?.value?.trim();
            if (!sid) return toast(tr("先填 SteamID"), "err");
            if (!sel) return toast(tr("先在上面选一个包"), "err");
            await run("bind", () => api.mvpSetSteamId(sid, sel), tr("已绑定"));
          }}>{tr("绑定到选中的包")}</button>
        </div>
        {Object.entries(cfg.Packs || {}).map(([sid, pk]) => (
          <div className="row" key={sid}>
            <div className="row__txt"><b className="mono">{sid}</b><span>→ {pk}{packs.some((p) => p.pack === pk) ? "" : tr(" · 包已丢失")}</span></div>
            <button className="btn btn--ghost" onClick={() => run("unbind", () => api.mvpRemoveSteamId(sid), tr("已解绑"))}>{tr("解绑")}</button>
          </div>
        ))}
      </section>

      {log && <pre className="logbox">{log}</pre>}

      <section data-tour="music-tips" className="card">
        <div className="card__head"><span className="card__title">{tr("必须先做两件事")}</span></div>
        <p className="note" style={{ marginTop: 0 }}>
          <b>{tr("1. 在游戏里卸下音乐盒")}</b>{tr("：设置 → 音乐 → 音乐盒 → 卸下。否则官方 MVP 音乐会叠上来。")}
        </p>
        <p className="note">
          <b>{tr("2. 装完/做完完全退出 CS2 再启动。")}</b> {tr("验证：控制台")} <code>{tr("play \"sounds/mymvp/packs/包名/mvpanthem.vsnd\"")}</code> {tr("有声音 → 聊天框")} <code>!mvpwhoami</code> {tr("显示包名 → 打一回合拿 MVP。")}
        </p>
      </section>
    </div>
  );
}


/* ═══════════ 交互式向导：高亮按钮 + 上方气泡 ═══════════ */
const TOURS = [
  {
    id: "quick", icon: "bolt", title: tr("第一次用：装好并进游戏"),
    desc: tr("确认路径 → 安装套件 → 插件模式启动，顺带看一眼装了什么"),
    steps: [
      {
        view: "settings", sel: '[data-tour="path"]', title: tr("先确认游戏路径"),
        body: tr("选到游戏根目录这一层（…\\Counter-Strike Global Offensive），不是 game\\csgo。点「自动检测」一般就能找到。"),
        tip: tr("路径错了后面全白做。"),
      },
      {
        view: "settings", sel: '[data-tour="inventory"]', title: tr("看清安装的是哪个包"),
        body: tr("这里列出「安装 / 修复套件」实际解压的 CS2BotImprover.zip、包大小、构建时间，以及包里每个插件的来源、版本、安装时间。展开明细可以逐项核对。"),
        tip: tr("以后怀疑某个插件版本不对，来这里看。"),
      },
      {
        view: "home", sel: '[data-tour="install"]', title: tr("安装 / 修复套件"),
        body: tr("把上面那个 zip 解压，整体覆盖到 game/csgo 的 addons、overrides、cfg、backup、panorama。第一次装、游戏更新后插件失效，都点这个。"),
        tip: tr("操作前必须完全退出 CS2；装完会保留你自己的音乐配置。"),
      },
      {
        view: "home", sel: '[data-tour="launch"]', title: tr("用「插件模式」启动"),
        body: tr("只有插件模式会挂载 Metamod，人机增强、徽章、换肤才生效。打官匹就点右边的「官方原版」，把挂载摘掉。"),
        tip: tr("两种模式别混用。"),
      },
    ],
  },
  {
    id: "music", icon: "note", title: tr("自定义 MVP 音乐"),
    desc: tr("做一首自己的 MVP 音乐，绑到你的 SteamID，回合结束就播"),
    steps: [
      {
        view: "music", sel: '[data-tour="music-tips"]', title: tr("先做两件事"),
        body: tr("① Steam 库 → 工具 → 装「Counter-Strike 2 Workshop Tools」（约 2 GB），没有它编译不出音效。② 游戏设置里卸下音乐盒，不然会和官方 MVP 音乐叠着放。"),
        tip: tr("这两件不做，后面做了包也听不到。"),
      },
      {
        view: "music", sel: '[data-tour="music-status"]', title: tr("看状态和环境"),
        body: tr("这里显示插件版本、音效包数量、默认音量、本机 SteamID，以及创意工坊工具和 ffmpeg 是否就位。没装插件就先点「安装 / 更新」。"),
        tip: tr("SteamID 自动从本机 Steam 读取，不用手抄。"),
      },
      {
        view: "music", sel: '[data-tour="music-make"]', title: tr("做新音效包"),
        body: tr("点开后选音频文件，填包名、起始秒、截取时长、淡出秒数，SteamID 已自动填好。管理器会切音频 → 编译 .vsnd_c → 同时部署到主目录和备份目录。"),
        tip: tr("也可以点「放置已编译的 .vsnd_c」，直接丢别人做好的文件。"),
      },
      {
        view: "music", sel: '[data-tour="music-packs"]', title: tr("音效包、分享与导入"),
        body: tr("表格里能看到每个包的更新时间、大小、绑定了哪些 SteamID。点「分享」导出 zip 发给朋友；点「导入音效包」选 zip，同名包直接覆盖，和原版拖进 bat 一样。"),
        tip: tr("「你的」标记表示这个包绑了本机账号。"),
      },
      {
        view: "music", sel: '[data-tour="music-rules"]', title: tr("播放规则"),
        body: tr("启用插件、人机拿 MVP 也放、压掉官方音乐、允许玩家调音量、路人 MVP 策略、默认音量都在这里改。改完立即写进插件配置。"),
        tip: tr("想只让自己听，把「人机拿 MVP 也放」关掉。"),
      },
    ],
  },
  {
    id: "killfeed", icon: "plug", title: tr("击杀图标"),
    desc: tr("选图标包、确认挂载、游戏内即时切换"),
    steps: [
      {
        view: "plugins", sel: '[data-tour="plugins-batch"]', title: tr("插件开关在这里"),
        body: tr("「插件」页能单项开关每个插件，也能一键全开全关。CS2 正在运行时开关会禁用，先退游戏再改。"),
        tip: tr("下面的外观卡片也归这一页管。"),
      },
      {
        view: "plugins", sel: '[data-tour="killfeed"]', title: tr("选击杀图标包"),
        body: tr("这里列出全部图标包，点一下就切换。右上角「已挂载」表示 1593 个图标资源和 gameinfo 挂载行都在位；显示「未挂载」时图标不会变。"),
        tip: tr("进游戏报 D3D 错误或主界面闪退，去总览关掉「击杀图标资源覆盖」再试。"),
      },
      {
        view: "plugins", sel: '[data-tour="killfeed"]', title: tr("游戏里实时换"),
        body: tr("管理器里选的是默认值。进游戏后聊天框先发 !weaponkill 打开选包菜单，再发 !编号（1–18）立刻生效。这个卡片上的「复制游戏内指令」按钮能直接抄走。"),
        tip: tr("不用重开游戏。"),
      },
    ],
  },
  {
    id: "skin", icon: "cart", title: tr("皮肤与手套"),
    desc: tr("库存插件、只开换肤模式、去网站配装"),
    steps: [
      {
        view: "home", sel: '[data-tour="mode"]', title: tr("完整人机 / 只开换肤"),
        body: tr("「完整人机」= 人机 AI + 饰品随机 + 规则 + 徽章全开。「只开换肤」只加载库存插件（InventorySimulator），只想玩皮肤时用，排障时也很好用。"),
        tip: tr("难度和规则不受影响，切回去插件不会丢。"),
      },
      {
        view: "home", sel: '[data-tour="skinweb"]', title: tr("皮肤不在管理器里改"),
        body: tr("点「打开皮肤库存」去 inventory.cstrike.app，用你玩 CS2 的那个账号登录，在那里配枪皮、手套、探员，进游戏才会显示。"),
        tip: tr("登录错账号 = 改了不生效，这是最常见的问题。"),
      },
      {
        view: "settings", sel: '[data-tour="inventory"]', title: tr("确认库存插件版本"),
        body: tr("展开安装清单，找到 InventorySimulator，可查看本包使用的官方 3.8.2 版本、来源和构建时间。"),
        tip: tr("皮肤突然全没了，先来这里确认插件是不是被换成了别的版本。"),
      },
    ],
  },
];

function Guide({ startTour }) {
  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("教程")}</h1>
        <p>{tr("点一个功能，我带你把按钮挨个按一遍")}</p>
      </header>

      <section className="card">
        <p className="note" style={{ marginTop: 0 }}>
          {tr("不是让你读步骤。点「开始向导」之后，界面会跳到对应页面，高亮那个按钮并在它上方弹出说明气泡，一步步跟着点就行。找不到按钮可以直接跳过这一步，随时能退出。")}
        </p>
        <div className="tourgrid">
          {TOURS.map((t) => (
            <button className="tourcard" key={t.id} onClick={() => startTour(t.id)}>
              <span className="tourcard__ico">{I[t.icon] ? I[t.icon](19) : I.book(19)}</span>
              <b>{t.title}</b>
              <em>{t.desc}</em>
              <span className="tourcard__foot">{t.steps.length} {tr("步 · 开始向导 →")}</span>
            </button>
          ))}
        </div>
      </section>

      <p className="note">{tr("四个向导互相独立，学哪个点哪个；重开软件也不会丢进度之外的数据。")}</p>
    </div>
  );
}

/* 全局浮层：高亮目标 + 贴着按钮上方的气泡 */
function TourOverlay({ tourId, stepIndex, setStep, onClose, view, go }) {
  const t = TOURS.find((x) => x.id === tourId);
  const step = t?.steps[stepIndex];
  const [rect, setRect] = useState(null);
  const [popH, setPopH] = useState(180);
  const popRef = useRef(null);

  useEffect(() => {
    if (step?.view && step.view !== view) go(step.view);
  }, [tourId, stepIndex, step?.view, view, go]);

  useEffect(() => {
    if (!step) return;
    setRect(null);
    let tries = 0;
    let frames = 0;
    let found = false;
    let cancelled = false;
    let raf = 0;
    const measure = () => {
      if (cancelled) return;
      const el = document.querySelector(step.sel);
      if (el) {
        if (!found) {
          found = true;
          // 不用 smooth：滚动动画期间 rect 一直在变，气泡会跟着乱跳
          try { el.scrollIntoView({ block: "center", behavior: "auto" }); } catch {}
        }
        const r = el.getBoundingClientRect();
        setRect({ top: r.top, left: r.left, width: r.width, height: r.height });
        frames += 1;
        if (frames < 90) raf = requestAnimationFrame(measure);
      } else if (tries++ < 300) {
        // 切页后 React 挂载 + 入场动画期间可能一时找不到，持续等
        raf = requestAnimationFrame(measure);
      } else {
        setRect(null);
      }
    };
    // 立即开始，靠 rAF 持续跟踪动画后的最终坐标
    raf = requestAnimationFrame(measure);
    return () => { cancelled = true; cancelAnimationFrame(raf); };
  }, [tourId, stepIndex, view, step?.sel]);

  useEffect(() => {
    if (popRef.current) setPopH(popRef.current.offsetHeight || 180);
  }, [rect, stepIndex, tourId]);

  if (!t || !step) return null;
  // 切页动画期间不画错位的高亮框/气泡；找不到按钮时给出口，别把人卡死
  if (!rect) return (
    <div className="tour-wait tour-wait--miss">
      <span>{tr("正在定位按钮… 如果这个按钮不在当前页面，点下面的按钮跳过")}</span>
      <div className="tour-wait__ops">
        <button className="btn btn--ghost btn--sm" onClick={onClose}>{tr("退出向导")}</button>
        {stepIndex + 1 < t.steps.length && (
          <button className="btn btn--soft btn--sm" onClick={() => setStep(stepIndex + 1)}>{tr("跳过这步")}</button>
        )}
      </div>
    </div>
  );

  const W = 340;
  const vw = typeof window !== "undefined" ? window.innerWidth : 1280;
  const vh = typeof window !== "undefined" ? window.innerHeight : 800;
  const left = rect ? Math.min(Math.max(12, rect.left + rect.width / 2 - W / 2), vw - W - 12) : 12;
  let top = rect ? rect.top - popH - 14 : 60;
  let below = false;
  if (top < 10) {
    top = rect ? rect.top + rect.height + 14 : 60;
    below = true;
  }
  if (top + popH > vh - 10) top = Math.max(10, vh - popH - 10);
  const last = stepIndex === t.steps.length - 1;

  return (
    <>
      {rect && (
        <div className="tour-hl" style={{
          top: Math.max(2, rect.top - 5), left: Math.max(2, rect.left - 5),
          width: rect.width + 10, height: rect.height + 10,
        }} />
      )}
      <div className={`tour-pop ${below ? "tour-pop--below" : ""}`} ref={popRef} style={{ left, top, width: W }}>
        <div className="tour-pop__head">
          <span>{t.title} {tr("· 第")} {stepIndex + 1} / {t.steps.length} {tr("步")}</span>
          <button onClick={onClose} aria-label={tr("退出向导")}>×</button>
        </div>
        <h3>{step.title}</h3>
        <p>{step.body}</p>
        {step.tip && <div className="tour-pop__tip">{step.tip}</div>}
        <div className="tour-pop__foot">
          <button className="btn btn--ghost" disabled={stepIndex === 0} onClick={() => setStep(stepIndex - 1)}>{tr("上一步")}</button>
          {last
            ? <button className="btn btn--primary" onClick={onClose}>{tr("完成")}</button>
            : <button className="btn btn--primary" onClick={() => setStep(stepIndex + 1)}>{tr("下一步")}</button>}
        </div>
        {!rect && <div className="tour-pop__miss">{tr("正在定位按钮…")}</div>}
      </div>
    </>
  );
}


/* ═══════════ 页：命令 ═══════════ */
const CMD_GROUPS = [
  {
    key: "net",
    label: tr("联机 / 服务器"),
    items: [
      { cmd: "status", desc: tr("看服务器状态、所有玩家、IP 和端口") },
      { cmd: "connect 192.168.1.10:27015", desc: tr("直连服务器，把 IP 换成房主的") },
      { cmd: "disconnect", desc: tr("断开当前服务器") },
      { cmd: "reconnect", desc: tr("重连上一次进的服务器") },
      { cmd: "sv_lan 1", desc: tr("切成局域网模式，同路由器的人更容易连上") },
      { cmd: "sv_password 1234", desc: tr("给房间设密码") },
      { cmd: "sv_password \"\"", desc: tr("取消房间密码") },
      { cmd: tr("hostname 我的房间"), desc: tr("改房间名，服务器列表里显示这个") },
      { cmd: "map de_dust2", desc: tr("换图，会重开一局") },
      { cmd: "changelevel de_mirage", desc: tr("换图，保留比分和金钱") },
      { cmd: "mp_restartgame 1", desc: tr("1 秒后重开当前这局") },
      { cmd: "mp_warmup_end", desc: tr("立刻结束热身，直接开打") },
      { cmd: tr("kick 玩家名"), desc: tr("踢人，房主才能用") },
      { cmd: tr("say 大家好"), desc: tr("全体发言") },
      { cmd: tr("say_team 集合 B 点"), desc: tr("队内发言") },
    ],
  },
  {
    key: "bot",
    label: tr("人机控制"),
    items: [
      { cmd: "bot_add", desc: tr("加一个 bot，自动平衡队伍") },
      { cmd: "bot_add_t", desc: tr("给 T 方加一个 bot") },
      { cmd: "bot_add_ct", desc: tr("给 CT 方加一个 bot") },
      { cmd: "bot_kick", desc: tr("踢掉场上所有 bot") },
      { cmd: "bot_kill", desc: tr("杀死所有 bot") },
      { cmd: "bot_stop 1", desc: tr("冻结 bot 移动，调试用") },
      { cmd: "bot_stop 0", desc: tr("解除冻结") },
      { cmd: "bot_difficulty 3", desc: tr("bot 难度 0–5，数字越大越强") },
      { cmd: "bot_quota 10", desc: tr("场上保持多少个 bot") },
      { cmd: "bot_quota_mode fill", desc: tr("补满模式：人不够时自动补 bot") },
      { cmd: "bot_mimic 1", desc: tr("bot 模仿你的动作") },
      { cmd: "bot_place", desc: tr("在准星位置放一个 bot") },
      { cmd: "bot_crouch 1", desc: tr("所有 bot 蹲下") },
      { cmd: "bot_zombie 1", desc: tr("bot 不移动也不还手") },
      { cmd: "bot_dont_shoot 1", desc: tr("bot 不开枪") },
    ],
  },
  {
    key: "rule",
    label: tr("对局规则"),
    items: [
      { cmd: "mp_freezetime 5", desc: tr("开局冻结时间，秒") },
      { cmd: "mp_roundtime 1.92", desc: tr("每回合时长，分钟") },
      { cmd: "mp_maxrounds 24", desc: tr("最多打多少回合") },
      { cmd: "mp_startmoney 800", desc: tr("初始金钱") },
      { cmd: "mp_buytime 20", desc: tr("购买时间，秒") },
      { cmd: "mp_buy_anywhere 1", desc: tr("允许在任意位置购买") },
      { cmd: "mp_friendlyfire 0", desc: tr("友军伤害，0 关 1 开") },
      { cmd: "mp_autoteambalance 0", desc: tr("自动平衡队伍，人机局建议关") },
      { cmd: "mp_limitteams 0", desc: tr("队伍人数差限制，0 = 不限") },
      { cmd: "mp_c4timer 40", desc: tr("C4 倒计时，秒") },
      { cmd: "mp_overtime_enable 1", desc: tr("打平后进加时") },
      { cmd: "mp_halftime 1", desc: tr("半场换边") },
      { cmd: "mp_solid_enemies 0", desc: tr("敌人可否互相穿过，0 = 可穿 = 能叠人") },
      { cmd: "mp_solid_teammates 2", desc: tr("队友碰撞，2 = 可叠") },
      { cmd: "mp_drop_knife_enable 1", desc: tr("允许丢刀") },
      { cmd: "mp_spectators_max 4", desc: tr("最多几个观战") },
    ],
  },
  {
    key: "show",
    label: tr("显示 / 调试"),
    items: [
      { cmd: "cl_showfps 1", desc: tr("左上角显示帧率") },
      { cmd: "cq_netgraph 1", desc: tr("显示网络参数图（丢包、延迟）") },
      { cmd: "cl_showpos 1", desc: tr("显示坐标和移动速度") },
      { cmd: "sensitivity 2", desc: tr("鼠标灵敏度") },
      { cmd: "cl_crosshairsize 3", desc: tr("准星大小") },
      { cmd: "cl_radar_scale 0.35", desc: tr("雷达缩放，越小看得越远") },
      { cmd: "voice_scale 0.5", desc: tr("队友语音音量") },
      { cmd: "sv_cheats 1", desc: tr("开作弊模式，noclip 和 god 需要它") },
      { cmd: "noclip", desc: tr("穿墙飞行（要 sv_cheats 1）") },
      { cmd: "god", desc: tr("无敌（要 sv_cheats 1）") },
      { cmd: "demoui", desc: tr("录像播放控制台") },
    ],
  },
  {
    key: "plugin",
    label: tr("插件命令"),
    items: [
      { cmd: "exec my_bot_normal_config", desc: tr("加载常规人机预设，规则和饰品随机全在这份里") },
      { cmd: "exec my_bot_ffa_config", desc: tr("加载死斗预设") },
      { cmd: "exec my_bot_rush_config", desc: tr("加载 rush 预设") },
      { cmd: "exec xuerong_manager", desc: tr("只应用管理器里设的规则，不动预设") },
      { cmd: "bot_aim head", desc: tr("人机瞄准锁头（mixed / body）") },
      { cmd: "bot_nades max", desc: tr("投掷物拉满（off / less / normal / more / max）") },
      { cmd: "bot_randomizer weapons 1", desc: tr("开 bot 武器皮肤（knives/gloves/stickers/charms/agents/music 同理）") },
      { cmd: "css_badge_mvp status", desc: tr("查看 XP 徽章状态（段位由 MapRanks 单独管理）") },
      { cmd: "css_mapranks_status", desc: tr("查看 bot 段位与动态 MVP 状态") },
      { cmd: "!weaponkill", desc: tr("打开击杀图标选包菜单") },
      { cmd: "!3", desc: tr("选第 3 套击杀图标，编号 1–18") },
    ],
  },
];

// 扁平化 + 分类标签只算一次（68 条，搜索时不再重建）
const ALL_CMDS = CMD_GROUPS.flatMap((g) => g.items.map((i) => ({ ...i, group: g.key })));
const CMD_TABS = [{ key: "all", label: `${tr("全部 ")}${ALL_CMDS.length}` }]
  .concat(CMD_GROUPS.map((g) => ({ key: g.key, label: g.label })));

function Commands({ toast }) {
  const [tab, setTab] = useState("all");
  const [q, setQ] = useState("");

  const list = useMemo(() => {
    const kw = q.trim().toLowerCase();
    if (!kw && tab === "all") return ALL_CMDS;
    return ALL_CMDS.filter((i) => {
      if (tab !== "all" && i.group !== tab) return false;
      if (!kw) return true;
      return i.cmd.toLowerCase().includes(kw) || i.desc.toLowerCase().includes(kw);
    });
  }, [tab, q]);
  const all = ALL_CMDS;
  const tabs = CMD_TABS;

  const copy = async (cmd) => {
    try { await navigator.clipboard.writeText(cmd); toast(`${tr("已复制 ")}${cmd}`); }
    catch { toast(tr("复制失败，手动输入"), "err"); }
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("命令")}</h1>
        <p>{tr("共")} {all.length} {tr("条，点一下复制，进游戏控制台粘贴")}</p>
      </header>

      <section className="card">
        <div className="seg seg--wrap">
          {tabs.map((t) => (
            <button key={t.key} type="button"
              className={tab === t.key ? "is-on" : ""}
              onClick={() => setTab(t.key)}>{t.label}</button>
          ))}
        </div>
        <input className="input" style={{ marginTop: 10 }} value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder={tr("搜命令或说明，比如 status / 联机 / 难度")} />
      </section>

      <section className="card">
        {list.length === 0 ? (
          <p className="note" style={{ margin: 0 }}>{tr("没找到匹配的命令，换个关键词试试。")}</p>
        ) : (
          <div className="cmdlist">
            {list.map((c) => (
              <button className="cmd" key={c.cmd} onClick={() => copy(c.cmd)}>
                <code>{c.cmd}</code>
                <span>{c.desc}</span>
                <i>{I.copy(13)}</i>
              </button>
            ))}
          </div>
        )}
      </section>

      <p className="note">
        {tr("控制台默认按")} <code>~</code> {tr("打开。没反应的话，Steam 库右键 CS2 → 属性 → 启动选项加")} <code>-console</code>{tr("；\n        管理器用「插件模式」启动时已经自动带上了。")}
      </p>
      <p className="note">
        {tr("标着「要 sv_cheats 1」的命令，先发")} <code>sv_cheats 1</code> {tr("再发它，否则无效。\n        联机时房主才需要开 cheats，加入的人不用。")}
      </p>
    </div>
  );
}

/* ═══════════ 设置页：安装清单（安装 / 修复套件到底装了什么） ═══════════ */
function fmtInvSize(n) {
  if (!n) return "—";
  return n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${(n / 1024).toFixed(0)} KB`;
}

function PluginInventoryCard({ toast }) {
  const [inv, setInv] = useState(null);
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState("plugins");

  const load = useCallback(async () => {
    try {
      const r = await api.getPluginInventory();
      setInv(r && r.ok ? r : { ok: false, error: r?.error || tr("读取失败") });
    } catch (e) {
      setInv({ ok: false, error: String(e) });
    }
  }, []);
  useEffect(() => { load(); }, [load]);

  const list = inv?.ok ? (tab === "plugins" ? inv.plugins : inv.components) : [];

  return (
    <section className="card" data-tour="inventory">
      <div className="card__head">
        <span className="card__title">{tr("安装清单")}</span>
        <span className={`pill ${inv?.ok ? "pill--ok" : ""}`}>
          {inv?.ok ? `${inv.pluginCount}${tr(" 插件 / ")}${inv.componentCount}${tr(" 组件")}` : inv ? tr("读取失败") : tr("读取中…")}
        </span>
      </div>

      {inv?.ok ? (
        <>
          <div className="invsrc">
            <div className="invsrc__name">{inv.source.name}</div>
            <code className="kv__path">{inv.source.path}</code>
          </div>
          {inv.source.bundleVersion && <div className="kv"><span>{tr("主体包版本")}</span><b>{inv.source.bundleVersion}</b></div>}
          <div className="kv"><span>{tr("包大小 / 条目")}</span><b>{fmtInvSize(inv.source.size)} · {inv.source.entries} {tr("个文件")}</b></div>
          <div className="kv"><span>{tr("包内构建时间")}</span><b>{inv.source.builtAt || inv.source.packedAt || "—"}</b></div>
          <div className="kv"><span>{tr("覆盖到")}</span><b>{tr("game/csgo 下的")} {inv.installTargets.join(" / ")}</b></div>
          <p className="note">
            {tr("总览页点「安装 / 修复套件」，就是把上面这个 zip 解压，整体覆盖到游戏目录的这 5 个位置；\n            随后自动修补 gameinfo.gi、保留音乐配置、只留一份备份。下表是这个包里装的所有东西。")}
          </p>

          <div className="grid2" style={{ marginTop: 10 }}>
            <button className="btn btn--soft" onClick={() => setOpen((v) => !v)}>
              {open ? tr("收起明细") : `${tr("查看全部 ")}${inv.pluginCount + inv.componentCount}${tr(" 项")}`}
            </button>
            <button className="btn btn--ghost" onClick={() => { load(); toast(tr("清单已刷新")); }}>{tr("刷新清单")}</button>
          </div>

          {open && (
            <div className="invlist">
              <div className="invlist__tabs">
                <button className={tab === "plugins" ? "is-on" : ""} onClick={() => setTab("plugins")}>{tr("插件")} {inv.pluginCount}</button>
                <button className={tab === "components" ? "is-on" : ""} onClick={() => setTab("components")}>{tr("组件")} {inv.componentCount}</button>
              </div>
              {list.map((p) => (
                <div className="invitem" key={p.name + p.kind}>
                  <div className="invitem__top">
                    <b>{p.displayName || p.name}</b>
                    <span className={`tag ${p.installed ? "tag--ok" : ""}`}>{p.installed ? tr("已安装") : tr("未安装")}</span>
                    <em className="mono">{p.name}</em>
                  </div>
                  <div className="invitem__meta">
                    <span>{tr("版本")} <b>{p.version || "—"}</b></span>
                    <span>{tr("文件")} <b>{p.files}</b></span>
                    <span>{tr("大小")} <b>{fmtInvSize(p.size)}</b></span>
                    <span>{tr("构建")} <b>{p.builtAt || "—"}</b></span>
                    <span>{tr("安装")} <b>{p.installedAt || "—"}</b></span>
                  </div>
                  <div className="invitem__src">{tr("来源：")}{p.source || tr("整合包内置")}</div>
                  {p.desc && <div className="invitem__desc">{p.desc}</div>}
                  {p.configPath && <code className="invitem__cfg">{p.configPath}</code>}
                </div>
              ))}
            </div>
          )}
        </>
      ) : (
        <p className="note">{inv ? inv.error : tr("正在读取离线安装包…")}</p>
      )}
    </section>
  );
}

/* ═══════════ 页：设置 ═══════════ */
function Settings({ state, meta, refresh, softRefresh, toast, bg, setBg, go, openNotice, updateInfo, setUpdateInfo, bgImage, setBgImage }) {
  const { visible: playerVisible, setVisible: setPlayerVisible } = useMusic();
  const [prog, setProg] = useState(null);
  const [dlError, setDlError] = useState(null);
  const [askLanguage, setAskLanguage] = useState(() => isLangPromptEnabled());

  // 监听后端下载进度
  useEffect(() => {
    if (!isTauri) return;
    let un = null;
    let alive = true;
    import("@tauri-apps/api/event")
      .then(({ listen }) => listen("update-progress", (e) => { if (alive) setProg(e.payload); }))
      .then((f) => { if (alive) un = f; else try { f(); } catch {} })
      .catch(() => {});
    return () => { alive = false; try { un && un(); } catch {} };
  }, []);

  const { playing, toggle, volume, changeVolume } = useMusic();
  const path = state.cs2Path || state.autoDetectedPath || "";

  return (
    <div className="page">
      <header className="page__head">
        <h1>{tr("设置")}</h1>
        <p>{tr("路径 · 背景 · 音乐 · 外观")}</p>
      </header>

      <section className="card author">
        <div className="author__ring"><img src={avatarImg} alt="" /></div>
      </section>

      <section data-tour="path" className="card">
        <div className="card__head"><span className="card__title">{tr("游戏路径")}</span></div>
        <code className="kv__path">{path || tr("未设置")}</code>
        <div className="grid2" style={{ marginTop: 10 }}>
          <button className="btn btn--soft" onClick={async () => {
            const r = await api.detectPath();
            if (r.path) { await api.setPath(r.path); await refresh(); toast(tr("已自动检测")); }
            else toast(tr("未检测到 CS2"), "err");
          }}>{tr("自动检测")}</button>
          <button className="btn btn--soft" onClick={async () => {
            const p = await api.pickFolder();
            if (!p) return;
            const r = await api.setPath(p);
            if (r.success) { await refresh(); toast(tr("路径已更新")); } else toast(r.error || tr("路径无效"), "err");
          }}>{tr("浏览…")}</button>
        </div>
        <div className="row" style={{ marginTop: 12 }}>
          <div className="row__txt"><b>{tr("模式")}</b><span>{tr("插件模式需挂载 metamod 搜索路径")}</span></div>
          <Seg value={meta.mode?.current || "bots"} disabled={meta.mode?.cs2Running}
            options={[{ value: "bots", label: tr("插件") }, { value: "online", label: tr("官方") }]}
            onChange={async (v) => {
              const r = await api.setMode(v);
              if (r.error) return toast(r.error, "err");
              await softRefresh(); toast(v === "bots" ? tr("已切到插件模式") : tr("已切回官方模式"));
            }} />
        </div>
      </section>

      <PluginInventoryCard toast={toast} />

      <section className="card">
        <div className="card__head">
          <span className="card__title">{tr("界面语言 / 言語 / Language")}</span>
          <span className={`pill pill--ok`}>{LANGS.find((l) => l.id === currentLang())?.label}</span>
        </div>
        <div className="row">
          <div className="row__txt"><b>{tr("当前语言")}</b><span>{tr("语言选择会保存到下次启动")}</span></div>
          <Seg value={currentLang()} options={LANGS.map((l) => ({ value: l.id, label: l.label }))} onChange={setLang} />
        </div>
        <div className="row">
          <div className="row__txt"><b>{tr("启动时询问语言")}</b><span>{tr("关闭后不再显示语言选择弹窗，可在此重新开启")}</span></div>
          <Toggle on={askLanguage} onChange={(enabled) => {
            setLangPromptEnabled(enabled);
            setAskLanguage(enabled);
          }} />
        </div>
      </section>


      <section className="card">
        <div className="card__head">
          <span className="card__title">{tr("运行模式")}</span>
          <span className={`pill ${meta.skinMode?.enabled ? "pill--ok" : ""}`}>
            {meta.skinMode?.enabled ? tr("只开换肤") : tr("完整人机")}
          </span>
        </div>
        <Seg value={meta.skinMode?.enabled ? "skin" : "full"}
          options={[{ value: "full", label: tr("完整人机") }, { value: "skin", label: tr("只开换肤") }]}
          onChange={async (v) => {
            const r = await api.setSkinMode(v === "skin");
            if (r.error) return toast(r.error, "err");
            await softRefresh();
            toast(r.message || tr("已切换"));
          }} />
        <p className="note">
          {meta.skinMode?.enabled
            ? tr("当前只加载库存换肤（InventorySimulator）。人机 AI、饰品随机、规则、徽章全部停用，难度不受影响。")
            : tr("完整模式：人机 AI + 饰品随机 + 规则 + 徽章全开。只想换肤就去切「只开换肤」。")}
        </p>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">{tr("兼容性")}</span>
          <span className={`pill ${meta.iconOverlay?.enabled ? "pill--ok" : "pill--bad"}`}>
            {meta.iconOverlay?.enabled ? tr("已启用") : tr("已停用")}
          </span>
        </div>
        <div className="row">
          <div className="row__txt">
            <b>{tr("击杀图标资源覆盖")}</b>
            <span>{tr("1593 个图标文件 + gameinfo 挂载行。进游戏报 D3D 错误或主界面闪退时，先关掉它再试")}</span>
          </div>
          <Toggle on={!!meta.iconOverlay?.enabled} onChange={async (v) => {
            const r = await api.setIconOverlay(v);
            if (r.error) return toast(r.error, "err");
            await softRefresh();
            toast(v ? tr("图标覆盖已启用") : tr("图标覆盖已停用，图标会回到原版"));
          }} />
        </div>
        <p className="note">
          {tr("关掉之后击杀图标全部失效，但人机增强、徽章、规则不受影响。判断是不是它的问题，就靠这个开关。")}
        </p>
      </section>


      <Diagnose toast={toast} />

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("背景与材质")}</span></div>
        <div className="row">
          <div className="row__txt"><b>{tr("液态玻璃")}</b><span>{tr("卡片和导航换成半透明折射材质；关掉就是普通磨砂，低配机更顺")}</span></div>
          <Toggle on={bg.glass !== false} onChange={(v) => setBg({ ...bg, glass: v })} />
        </div>
        <div className="row">
          <div className="row__txt">
            <b>{tr("背景")}</b>
            <span>{tr("用默认的 Miku 视频、导入自己的图，或者干脆纯色")}</span>
          </div>
          <Seg value={bg.mode || "video"}
            options={[
              { value: "video", label: tr("视频") },
              { value: "image", label: tr("图片") },
            ]}
            onChange={(v) => setBg({ ...bg, mode: v })} />
        </div>

        {(bg.mode || "video") === "image" && (
          <div className="row">
            <div className="row__txt">
              <b>{tr("自定义图片")}</b>
              <span>{bgImage ? tr("已导入，点右侧可以换一张或移除") : tr("支持 jpg / png / webp，建议 1920×1080 以上")}</span>
            </div>
            <div className="row__ops">
              <label className="btn btn--ghost btn--sm">
                {tr("选择…")}
                <input type="file" accept="image/*" hidden onChange={async (e) => {
                  const f = e.target.files?.[0];
                  if (!f) return;
                  if (f.size > 24 * 1024 * 1024) return toast(tr("图片太大，换一张 24MB 以内的"), "err");
                  const url = await new Promise((res) => {
                    const fr = new FileReader();
                    fr.onload = () => res(fr.result);
                    fr.readAsDataURL(f);
                  });
                  await bgPut(url);
                  setBgImage(url);
                  setBg({ ...bg, mode: "image" });
                  toast(tr("背景已更新"));
                }} />
              </label>
              {bgImage && (
                <button className="btn btn--ghost btn--sm" onClick={async () => {
                  await bgDel(); setBgImage(null);
                  setBg({ ...bg, mode: "video" });
                  toast(tr("已移除自定义背景"));
                }}>{tr("移除")}</button>
              )}
            </div>
          </div>
        )}


        <div className="slider-row">
          <span>{tr("背景")}</span>
          <input className="range" type="range" min="0" max="40" step="1" value={bg.blur ?? 0}
            onChange={(e) => setBg({ ...bg, blur: Number(e.target.value) })} />
          <b>{bg.blur ?? 0}px</b>
        </div>
        <div className="slider-row">
          <span>{tr("玻璃")}</span>
          <input className="range" type="range" min="0" max="40" step="1" value={bg.glassBlur ?? 22}
            onChange={(e) => setBg({ ...bg, glassBlur: Number(e.target.value) })} />
          <b>{bg.glassBlur ?? 22}px</b>
        </div>
        <div className="slider-row">
          <span>{tr("遮罩")}</span>
          <input className="range" type="range" min="15" max="92" step="1" value={bg.veil}
            onChange={(e) => setBg({ ...bg, veil: Number(e.target.value) })} />
          <b>{bg.veil}%</b>
        </div>
        <p className="note">{tr("看不清文字就把遮罩调高；想看视频就把模糊调到 0。")}</p>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">{tr("背景音乐")}</span>
          <span className={`pill ${playerVisible ? "pill--ok" : ""}`}>{playerVisible ? tr("播放器已显示") : tr("已隐藏")}</span>
        </div>
        <div className="row">
          <div className="row__txt">
            <b>{tr("显示播放器")}</b>
            <span>{tr("左下角那个卡片。可以导入本地音乐、切歌、调音量")}</span>
          </div>
          <Toggle on={playerVisible} onChange={(v) => setPlayerVisible(v)} />
        </div>
        <p className="note">{tr("关掉后左下角会留一个小音符按钮，点一下就能重新打开。")}</p>
      </section>


      <Diagnose toast={toast} />

      <section className="card">
        <div className="card__head"><span className="card__title">{tr("维护")}</span></div>
        <button className="btn btn--soft btn--block" onClick={async () => {
          const r = await api.applyConfigs();
          if (r.success) { await refresh(); toast(tr("配置已重新下发")); } else toast(r.error || tr("失败"), "err");
        }}>{tr("重新下发配置")}</button>
        <div className="kv" style={{ marginTop: 10 }}><span>{tr("版本")}</span><b>{VER}</b></div>
        <div className="kv"><span>{tr("清单")}</span><b>{meta.manifest ? `${meta.manifest.present}/${meta.manifest.total}` : "—"}</b></div>
        <div className="grid2" style={{ marginTop: 8 }}>
          <button className="btn btn--ghost" onClick={() => go("guide")}>{tr("重看教程")}</button>
          <button className="btn btn--ghost" onClick={openNotice}>{tr("更新公告")}</button>
        </div>
        <button className="btn btn--ghost btn--block" style={{ marginTop: 8 }} onClick={() => {
          try {
            localStorage.removeItem("xr.tutorial.suppress");
            localStorage.removeItem("xr.tutorial.suppressVer");
            localStorage.removeItem("xr.notice.suppress");
            localStorage.removeItem("xr.notice.suppressVer");
          } catch {}
          toast(tr("已恢复：下次启动重新弹教程和公告"));
        }}>{tr("恢复启动弹窗（永久关闭后想再看点这里）")}</button>
      </section>
    </div>
  );
}

/* 曾经这里有个「滚动到底部弹性拉伸」的 hook：给 .content 逐帧写 transform。
   它会让整页所有 backdrop-filter 每帧重新采样背景，滚动直接掉到个位数帧。
   已移除 —— 别再往滚动容器上加 transform / filter，代价是整棵子树。 */

/* ═══════════ 壳 ═══════════ */
function App() {
  const [splash, setSplash] = useState(() => !isBooted());
  const [langPrompt, setLangPrompt] = useState(() => shouldPromptLang());
  const [state, setState] = useState(null);
  const [meta, setMeta] = useState({});
  const [notice, setNotice] = useState(false);
  const [tutorial, setTutorial] = useState(false);
  const contentRef = useRef(null);
  const navRef = useRef(null);
  const [ink, setInk] = useState({ x: 0, w: 0, ready: false });
  useGlassPointer();
  useScrollDegrade(contentRef, !splash && !!state);
  const [updateInfo, setUpdateInfo] = useState(null);
  const updateChecked = useRef(false);
  const [guideSeen, setGuideSeen] = useState(() => {
    try { return localStorage.getItem("xr.guide.seen") === "1"; } catch { return true; }
  });
  // 交互式向导：{ id, step }
  const [tour, setTour] = useState(null);
  const [view, setView] = useState(() => {
    // 支持 ?view=rules 直接打开某页（调试用，正常启动无参数）
    try {
      const v = new URLSearchParams(location.search).get("view");
      return v && NAV.some((n) => n.id === v) ? v : "home";
    } catch { return "home"; }
  });
  const [dir, setDir] = useState(1);
  const { toast } = useToast();

  // 背景：v2 起默认遮罩从 38% 提到 78% —— 旧默认值下视频太抢，玻璃上的字压不住。
  // 老用户存的还是 v1（无 v 字段），这里一次性迁移到新默认，模糊/主题/玻璃开关保持原样。
  const [bg, setBg] = useState(() => {
    const DEF = { blur: 0, glassBlur: 22, veil: 34, mode: "video", color: "#171226", glass: true, webgl: true, v: 3 };
    try {
      const s = JSON.parse(localStorage.getItem("xr.bg") || "null");
      if (!s) return DEF;
      if (s.v !== 3) return { ...DEF, mode: s.mode ?? "video", color: s.color ?? "#171226", glass: s.glass ?? true, webgl: s.webgl !== false };
      return { ...DEF, blur: s.blur ?? 0, glassBlur: s.glassBlur ?? 22, veil: s.veil ?? 34, mode: s.mode ?? "video", color: s.color ?? "#171226", glass: s.glass ?? true, webgl: s.webgl !== false };
    } catch { return DEF; }
  });

  const [bgImage, setBgImage] = useState(null);
  useEffect(() => { bgGet().then((v) => { if (v) setBgImage(v); }); }, []);

  // 主题：light / dark，存本地，切换只换 <html data-theme>
  const [theme, setTheme] = useState(() => {
    try { return localStorage.getItem("xr.theme") === "dark" ? "dark" : "light"; } catch { return "light"; }
  });
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try { localStorage.setItem("xr.theme", theme); } catch {}
  }, [theme]);

  // 每次启动都弹教程 + 公告；勾过「永久关闭」才跳过，但换版本一律重新弹
  useEffect(() => {
    try {
      if (new URLSearchParams(location.search).has("nonotice")) return;
      const suppressed = (key) => {
        try {
          return localStorage.getItem(key) === "1" && localStorage.getItem(key + "Ver") === VER;
        } catch { return false; }
      };
      if (!suppressed("xr.tutorial.suppress")) setTutorial(true);
      if (!suppressed("xr.notice.suppress")) setNotice(true);
    } catch {}
  }, []);

  const saveSuppress = (key, permanent) => {
    try {
      if (permanent) {
        localStorage.setItem(key, "1");
        localStorage.setItem(key + "Ver", VER);
      } else {
        localStorage.removeItem(key);
        localStorage.removeItem(key + "Ver");
      }
    } catch {}
  };

  const closeNotice = useCallback((permanent = false) => {
    setNotice(false);
    saveSuppress("xr.notice.suppress", permanent);
  }, []);

  const closeTutorial = useCallback((permanent = false) => {
    setTutorial(false);
    saveSuppress("xr.tutorial.suppress", permanent);
  }, []);

  useEffect(() => {
    document.documentElement.style.setProperty("--bg-blur", `${bg.blur}px`);
    // 玻璃模糊：只作用在面板自己的 backdrop 上，跟背景视频的模糊分开
    document.documentElement.style.setProperty("--blur", `${bg.glassBlur ?? 22}px`);
    document.documentElement.style.setProperty("--veil", `${bg.veil / 100}`);
    document.documentElement.dataset.glass = bg.glass === false ? "off" : "on";
    localStorage.setItem("xr.bg", JSON.stringify(bg));
  }, [bg]);

  const loadMeta = useCallback(async (withManifest = true) => {
    const safe = (p) => p.catch(() => null);
    const jobs = [
      safe(api.getRules()), safe(api.getBotItems()), safe(api.getPresets()),
      safe(api.getKillfeed()), safe(api.getMode()), safe(api.getDifficulty()), safe(api.getIconOverlay()), safe(api.getSkinMode()), safe(api.getUpdateSource()),
      withManifest ? safe(api.validateManifest()) : Promise.resolve(undefined),
    ];
    const [rules, items, presets, killfeed, mode, difficulty, iconOverlay, skinMode, updateSrc, manifest] = await Promise.all(jobs);
    setMeta((prev) => ({
      ...prev,
      ...(manifest !== undefined ? { manifest: manifest && !manifest.error ? manifest : null } : {}),
      rules: rules && !rules.error ? rules : {},
      items: items && !items.error ? items : {},
      presets: presets && !presets.error ? presets : {},
      killfeed: killfeed && !killfeed.error ? killfeed : {},
      mode: mode && !mode.error ? mode : {},
      difficulty: difficulty && !difficulty.error ? difficulty : {},
      iconOverlay: iconOverlay && !iconOverlay.error ? iconOverlay : {},
      skinMode: skinMode && !skinMode.error ? skinMode : {},
      updateSource: updateSrc && !updateSrc.error ? updateSrc : {},
    }));
  }, []);

  // 局部更新：设置类操作直接把后端返回值合并进来，不重跑全量检测
  const patchMeta = useCallback((patch) => setMeta((prev) => ({ ...prev, ...patch })), []);

  const refreshState = useCallback(async () => {
    try { setState(await api.state()); } catch { toast(tr("状态读取失败"), "err"); }
  }, [toast]);

  // 全量：只在启动、安装/卸载、手动重检时用（会扫描 1593 个图标资源）
  const refresh = useCallback(async () => {
    await refreshState();
    await loadMeta(true);
  }, [refreshState, loadMeta]);

  // 轻量：切插件、改难度、切模式后只刷状态
  const softRefresh = useCallback(async () => {
    await refreshState();
    await loadMeta(false);
  }, [refreshState, loadMeta]);

  useEffect(() => { refresh(); }, [refresh]);

  const go = useCallback((id) => {
    const a = NAV.findIndex((n) => n.id === view);
    const b = NAV.findIndex((n) => n.id === id);
    setDir(b >= a ? 1 : -1);
    setView(id);
    if (id === "guide" && !guideSeen) {
      setGuideSeen(true);
      try { localStorage.setItem("xr.guide.seen", "1"); } catch {}
    }
  }, [view, guideSeen]);

  // 导航指示块：切页/改窗口大小后重新量一次，只在事件里算，不逐帧
  useEffect(() => {
    const nav = navRef.current;
    if (!nav) return;
    const measure = () => {
      const idx = NAV.findIndex((n) => n.id === view);
      const btn = nav.querySelectorAll(".navpill__item")[idx];
      if (!btn) return;
      const nr = nav.getBoundingClientRect();
      const br = btn.getBoundingClientRect();
      setInk({ x: br.left - nr.left + nav.scrollLeft, w: br.width, ready: true });
    };
    measure();
    const t = setTimeout(measure, 60);
    window.addEventListener("resize", measure);
    return () => { clearTimeout(t); window.removeEventListener("resize", measure); };
  }, [view, splash, state]);

  if (splash) return <><VideoBg bg={bg} custom={bgImage} /><Splash onDone={() => { markBooted(); setSplash(false); }} /></>;
  if (!state) return <><VideoBg bg={bg} custom={bgImage} /><div className="loading">{tr("加载中…")}</div></>;
  const pageProps = { state, meta, refresh, softRefresh, refreshState, patchMeta, toast, bg, setBg, bgImage, setBgImage, go, updateInfo, setUpdateInfo, openNotice: () => setNotice(true), startTour: (id) => setTour({ id, step: 0 }) };
  const PAGES = {
    home: <Home {...pageProps} />,
    plugins: <Plugins {...pageProps} />,
    bots: <Bots {...pageProps} />,
    rules: <Rules {...pageProps} />,
    music: <Music {...pageProps} />,
    commands: <Commands {...pageProps} />,
    guide: <Guide {...pageProps} />,
    settings: <Settings {...pageProps} />,
  };

  return (
    <>
      <VideoBg bg={bg} custom={bgImage} />
      <div className="app">
        <header className="titlebar" data-tauri-drag-region>
          <div className="titlebar__brand" data-tauri-drag-region>
            <BrandWord />
            <span className="ver-chip">{VER}</span>
          </div>
          <nav className="navpill" ref={navRef}>
            <span
              className={`navpill__ink ${ink.ready ? "is-ready" : ""}`}
              style={{ transform: `translateX(${ink.x}px)`, width: ink.w }}
            />
            {NAV.map((n) => (
              <button
                key={n.id}
                className={`navpill__item ${view === n.id ? "is-on" : ""}`}
                onClick={() => go(n.id)}
                title={n.desc}
              >
                {n.label}
                {n.id === "guide" && !guideSeen && <span className="navpill__dot" />}
              </button>
            ))}
          </nav>
          <ThemeSwitch theme={theme} onToggle={() => setTheme((t) => (t === "dark" ? "light" : "dark"))} />
          <div className="titlebar__win">
            <button onClick={() => api.minimize()}>{I.min(14)}</button>
            <button className="is-close" onClick={() => api.close()}>{I.close(14)}</button>
          </div>
        </header>

        <div className="shell">
          <main className="content" ref={contentRef}>
            <div className="pager" key={view} style={{ "--dir": dir }}>
              <PageBoundary key={view}>{PAGES[view]}</PageBoundary>
            </div>
          </main>
        </div>
      </div>
      {view === "home" && <Pet />}
      <MusicPlayer />
      {notice && !langPrompt && <Announcement onClose={closeNotice} />}
      {tutorial && !notice && !langPrompt && <TutorialPopup onClose={closeTutorial} startTour={(id) => setTour({ id, step: 0 })} />}
      {tour && (
        <TourOverlay
          tourId={tour.id}
          stepIndex={tour.step}
          setStep={(n) => setTour((t) => ({ ...t, step: n }))}
          onClose={() => setTour(null)}
          view={view}
          go={go}
        />
      )}
      {langPrompt && <LanguagePrompt onClose={() => setLangPrompt(false)} />}
    </>
  );
}

export default function Root() {
  return (
    <ToastProvider>
      <MusicProvider>
        <App />
      </MusicProvider>
    </ToastProvider>
  );
}
