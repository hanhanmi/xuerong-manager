import React, { useState, useEffect, useRef, useCallback, useMemo, createContext, useContext } from "react";
import { api, isTauri } from "./api.js";
import splashImg from "./assets/splash.png";
import bgmSrc from "./assets/bgm.mp3";
import bgVideo from "./assets/bg.mp4";
import avatarImg from "./assets/avatar.png";
import petImg from "./assets/pet.png";
import "./styles/tokens.css";
import "./styles/app.css";

const VER = "v2.9.2";

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
const BUILTIN_TRACK = { id: "builtin", name: "小小奇迹", url: bgmSrc, builtin: true };

function MusicProvider({ children }) {
  const audioRef = useRef(null);
  const fadeRef = useRef(null);
  const [tracks, setTracks] = useState([BUILTIN_TRACK]);
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
    if (id === "builtin") return;
    setTracks((prev) => {
      const i = prev.findIndex((t) => t.id === id);
      if (i < 0) return prev;
      const nt = prev.filter((t) => t.id !== id);
      setIdx((cur) => (cur >= nt.length ? Math.max(0, nt.length - 1) : cur > i ? cur - 1 : cur));
      return nt;
    });
    await musicDel(id);
  }, []);

  // 首次进入自动播放（被拦就等第一次点击）
  useEffect(() => {
    const a = audioRef.current; if (!a) return;
    let started = false;
    (async () => {
      try { a.volume = 0; await a.play(); started = true; setPlaying(true); fadeTo(volRef.current); }
      catch {
        const kick = async () => {
          document.removeEventListener("pointerdown", kick);
          if (started) return;
          try { a.volume = 0; await a.play(); setPlaying(true); fadeTo(volRef.current); } catch {}
        };
        document.addEventListener("pointerdown", kick, { once: true });
      }
    })();
    return () => clearInterval(fadeRef.current);
  }, [fadeTo]);

  return (
    <MusicCtx.Provider value={{
      tracks, idx, playing, visible, volume, cur, dur,
      toggle, next, prev, playAt, seek, changeVolume, importFiles, removeTrack, setVisible,
    }}>
      {children}
      <audio ref={audioRef} src={bgmSrc} loop={tracks.length === 1} preload="auto"
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
  const fileRef = useRef(null);
  const track = tracks[idx] || tracks[0];

  // 关掉时先播「收进收纳袋」动画，再真正卸载
  const closePlayer = () => {
    if (closing) return;
    setClosing(true);
    setTimeout(() => { setVisible(false); setClosing(false); }, 360);
  };

  // 关掉后留一个小入口，随时能再打开
  if (!visible) {
    return (
      <button className="player-open" onClick={() => setVisible(true)} title="打开音乐播放器">
        {I.note(15)}
      </button>
    );
  }

  return (
    <div className={`player ${closing ? "player--put" : ""}`}>
      <div className="player__head">
        <span className="player__art">{I.note(16)}</span>
        <span className="player__meta">
          <b title={track?.name}>{track?.name || "未选择"}</b>
          <em>{tracks.length > 1 ? `${idx + 1} / ${tracks.length}` : "内置音乐"}</em>
        </span>
        <button className="player__x" onClick={closePlayer} title="关闭播放器">
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
          title="拖动调整进度" />
        <span className="player__time">{fmtTime(dur)}</span>
      </div>

      <div className="player__ctrl">
        <button className="player__btn" onClick={prev} disabled={tracks.length < 2} title="上一首">{I.prev(14)}</button>
        <button className="player__play" onClick={toggle} title={playing ? "暂停" : "播放"}>
          {playing ? I.pause(16) : I.play(16)}
        </button>
        <button className="player__btn" onClick={next} disabled={tracks.length < 2} title="下一首">{I.next(14)}</button>
        <input className="range player__vol" type="range" min="0" max="1" step="0.01"
          value={volume} onChange={(e) => changeVolume(Number(e.target.value))} title="音量" />
      </div>

      <div className="player__foot">
        <button className="player__add" onClick={() => fileRef.current?.click()}>+ 导入音乐</button>
        <button className="player__list" onClick={() => setShowList((v) => !v)}>
          {showList ? "收起" : `列表 ${tracks.length}`}
        </button>
      </div>

      {showList && (
        <div className="player__tracks">
          {tracks.map((t, i) => (
            <div className={`player__track ${i === idx ? "is-on" : ""}`} key={t.id}>
              <button className="player__tname" onClick={() => playAt(i)}>{t.name}</button>
              {!t.builtin && (
                <button className="player__del" onClick={() => removeTrack(t.id)} title="移除">×</button>
              )}
            </div>
          ))}
          <p className="player__hint">导入的音乐会保存在本机，重启后还在</p>
        </div>
      )}

      <input ref={fileRef} type="file" accept="audio/*" multiple hidden
        onChange={async (e) => {
          const n = await importFiles(e.target.files);
          e.target.value = "";
          if (n) toast(`已导入 ${n} 首`);
          else toast("没有可导入的音频文件", "err");
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
function VideoBg() {
  return (
    <div className="bgv" aria-hidden="true">
      <video src={bgVideo} autoPlay muted loop playsInline preload="auto" />
      <div className="bgv__veil" />
    </div>
  );
}

function Pet() {
  return (
    <div className="pet" aria-hidden="true">
      <div className="pet__frame"><img src={petImg} alt="" draggable="false" /></div>
    </div>
  );
}

/* ═══════════ 开屏 ═══════════ */
function Splash({ onDone }) {
  useEffect(() => { const t = setTimeout(onDone, 2400); return () => clearTimeout(t); }, [onDone]);
  return (
    <div className="splash">
      <div className="splash__inner">
        <div className="splash__logo"><img src={splashImg} alt="" /></div>
        <div className="splash__title">雪绒人机管理器</div>
        <div className="splash__sub">CS2 Bot Plugin Manager</div>
        <div className="splash__dots"><span /><span /><span /></div>
      </div>
    </div>
  );
}

/* ═══════════ 更新公告 ═══════════ */
const CHANGELOG = [
  {
    v: "v2.9.2",
    date: "2026-11-03",
    tag: "当前版本",
    items: [
      "修复：下载到 99% 突然换源、进度条归零重下",
      "下载支持断点续传，换源时从已下载位置继续，不再从头开始",
      "停滞判定从 20 秒放宽到 30 秒，尾部稍慢不再误判",
      "尾部误差 0.5% 以内直接视为下载完成，不白跑一趟",
    ],
  },
  {
    v: "v2.9.1",
    date: "2026-11-03",
    tag: "修复",
    items: [
      "修复：导入的本地音乐点了没反应（CSP 拦截 blob 音频）",
      "新增：播放进度条，可拖动跳转，两侧显示已播 / 总时长",
      "新增：页面滑到顶/底再继续滑动时有弹性拉伸回弹",
      "新增：关闭播放器时「收进收纳袋」动画，打开时再取出来",
      "优化：图标目录扫描加 5 秒缓存，设置操作不再重复扫 1593 个文件",
      "优化：命令页搜索不再每次按键重建 68 条数据",
      "优化：播放进度只在变化超过 0.2 秒时刷新界面",
    ],
  },
  {
    v: "v2.9.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "音乐播放器重做：可导入本地音乐、切歌、查看列表",
      "导入的歌曲保存在本机，重启后还在，可随时移除",
      "播放器挪到内容区左下角，带独立关闭按钮，关掉后留小音符入口",
      "修复：下载失败提示里的按钮几乎看不见",
    ],
  },
  {
    v: "v2.8.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "命令库从 15 条扩到 68 条",
      "按联机 / 人机 / 规则 / 显示 / 插件五类分组",
      "支持关键词搜索，输入 status、联机、难度都能过滤",
      "补上 status、connect、sv_lan、kick、say 等联机常用指令",
    ],
  },
  {
    v: "v2.7.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "自动更新支持国内加速镜像：官方不通会自动换 4 个源重试",
      "下载停滞 20 秒自动判定超时并切换下一个源，不再卡死",
      "全部源都失败时给出直链，可一键用浏览器下载",
      "进度条显示当前正在用哪个下载源",
    ],
  },
  {
    v: "v2.6.0",
    date: "2026-11-03",
    tag: "修复",
    items: [
      "修复：点「立即更新」弹「Windows 找不到文件」，装不上",
      "自动更新不再经过命令提示窗口，直接拉起安装向导",
      "教程重写为 10 步，补上难度切换、换肤登录、击杀图标的具体操作",
      "难度页说明：先点选档位再应用，切换后必须重开游戏",
    ],
  },
  {
    v: "v2.5.0",
    date: "2026-11-03",
    tag: "修复",
    items: [
      "卸载改为彻底删除，游戏目录里不再堆积备份（此前已堆积 854MB）",
      "卸载自动还原被覆盖的官方原文件，Steam 不再提示「需要更新下载内容」",
      "安装时把官方原文件快照到 %LOCALAPPDATA%\\XueRongCS2\\pristine（游戏目录外）",
      "清单校验区分官方文件与插件文件，未安装时显示「未安装（干净状态）」",
      "安装失败时给出权限提示，不再只报一行英文错误",
    ],
  },
  {
    v: "v2.4.1",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "内置更新仓库，装完直接能检查更新，不用手填任何链接",
      "更新源输入框改为可选覆盖，留空即用内置地址",
    ],
  },
  {
    v: "v2.4.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "新增一键自动更新：点「立即更新」自动下载并静默安装",
      "下载过程显示进度条与已下载体积，装完自动重启",
      "更新源收进「高级」折叠区，填一次即可，不用再手动贴链接",
      "下载完整性校验：拦截错误页与半截文件",
      "新增 THIRD-PARTY.md，完整标注第三方组件出处与授权",
    ],
  },
  {
    v: "v2.3.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "新增软件内检查更新：设置页填更新源即可",
      "支持 GitHub Releases（github:用户名/仓库名），不用自己写 JSON",
      "也支持自定义 JSON 地址，带版本号 / 下载地址 / 更新说明",
      "启动时静默检查一次，有新版本会在侧栏设置项亮小红点",
      "有更新时一键打开发布页下载",
    ],
  },
  {
    v: "v2.2.0",
    date: "2026-11-03",
    tag: "功能",
    items: [
      "新增「只开换肤」模式：总览页一键切换，只加载库存换肤",
      "换肤模式下人机 AI / 饰品随机 / 规则 / 徽章全部停用，难度不受影响",
      "切换肤会自动把 core.json 的 FollowCS2ServerGuidelines 设为 false",
      "换肤模式下点「安装 / 修复套件」不会把人机插件重新打开",
      "修复：停用框架插件时会误把所有人机插件重新启用",
    ],
  },
  {
    v: "v2.1.2",
    date: "2026-11-03",
    tag: "修复",
    items: [
      "修复：换难度后游戏里仍显示「高」（bot_difficulty 没跟着改）",
      "难度现在同时改 overrides 档位文件 + bot_difficulty / custom_bot_difficulty",
      "新增：设置页「兼容性 → 击杀图标资源覆盖」开关，可一键停用图标挂载排查故障",
      "新增：图标覆盖开关状态灯，随时看当前是启用还是停用",
    ],
  },
  {
    v: "v2.1.1",
    date: "2026-11-02",
    tag: "功能",
    items: [
      "新增内置交互式教程：左侧栏「教程」页，9 步引导",
      "教程里每个按钮都能直接跳到对应页面、复制命令、重新检测",
      "教程进度自动记住，设置页可随时重看",
      "侧栏未读教程会显示小红点",
    ],
  },
  {
    v: "v2.1.0",
    date: "2026-11-02",
    tag: "功能",
    items: [
      "桌面 / 任务栏 / 安装包图标换成新形象",
      "侧边栏标题改为彩色「这里是菜单喵」",
      "新增本公告弹窗，可在设置里再次打开",
      "修复：难度切换不生效（现在真的会换 overrides 档位文件）",
      "修复：清单显示 51/55 却提示全部通过（现在准确 55/55）",
      "外观页新增「保持默认即可，不要修改」提示",
    ],
  },
  {
    v: "v2.0.1",
    date: "2026-11-02",
    tag: "修复",
    items: [
      "修复：调设置时反复弹出命令行黑框",
      "修复：界面卡顿、容易未响应",
      "修复：关闭插件后清单误报「缺失」",
      "清理：废弃插件的无效配置写入",
    ],
  },
  {
    v: "v2.0.0",
    date: "2026-11-02",
    tag: "大版本",
    items: [
      "模组包换成中文人机名 · 全难度 · 含 Premier 徽章完整版",
      "界面重构：侧边栏 7 页 + 页面转场",
      "新增「规则」页：加时 / 友伤 / 叠人 / 连跳 / C4 / 燃烧瓶",
      "新增「击杀图标」：22 套皮肤包直接选",
      "新增「Premier 徽章 + 动态 MVP」完整参数",
      "复刻 Panel 的清单校验（55 项，含图标资源与挂载行）",
    ],
  },
];

function Announcement({ onClose }) {
  return (
    <div className="modal" onClick={onClose}>
      <div className="modal__card" onClick={(e) => e.stopPropagation()}>
        <div className="modal__glow" />
        <header className="modal__head">
          <div>
            <h2>更新公告</h2>
            <p>雪绒人机管理器 · {VER}</p>
          </div>
          <button className="modal__x" onClick={onClose} aria-label="关闭">{I.close(14)}</button>
        </header>
        <div className="modal__body">
          {CHANGELOG.map((c) => (
            <section className="rel" key={c.v}>
              <div className="rel__head">
                <b>{c.v}</b>
                <span className="rel__tag">{c.tag}</span>
                <em>{c.date}</em>
              </div>
              <ul>
                {c.items.map((it, i) => <li key={i}>{it}</li>)}
              </ul>
            </section>
          ))}
        </div>
        <footer className="modal__foot">
          <button className="btn btn--soft btn--block" onClick={onClose}>知道啦喵</button>
        </footer>
      </div>
    </div>
  );
}

/* ═══════════ 侧边栏 ═══════════ */
const NAV = [
  { id: "home", label: "总览", icon: "home", desc: "启动与状态" },
  { id: "plugins", label: "插件", icon: "plug", desc: "安装与开关" },
  { id: "bots", label: "人机", icon: "bot", desc: "难度与物品" },
  { id: "rules", label: "规则", icon: "scale", desc: "对局规则" },
  { id: "look", label: "外观", icon: "palette", desc: "徽章与图标" },
  { id: "commands", label: "命令", icon: "terminal", desc: "控制台速查" },
  { id: "guide", label: "教程", icon: "book", desc: "跟着做一遍" },
  { id: "settings", label: "设置", icon: "gear", desc: "路径与外观" },
];

function Sidebar({ view, onGo, state, manifest, guideSeen, hasUpdate }) {
  const okCount = manifest ? manifest.present : 0;
  const total = manifest ? manifest.total : 0;
  return (
    <aside className="side">
      <div className="side__brand">
        <b className="side__title side__title--lg">这里是菜单喵</b>
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
          <span>{manifest ? `${okCount}/${total} 项就位` : "检测中…"}</span>
        </div>
      </div>
    </aside>
  );
}

/* ═══════════ 页：总览 ═══════════ */
function Home({ state, meta, refresh, softRefresh, toast }) {
  const [busy, setBusy] = useState("");
  const path = state.cs2Path || state.autoDetectedPath || "";
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const m = meta.manifest;

  const launch = async (mode) => {
    if (!path) { toast("请先在设置里选择 CS2 目录", "err"); return; }
    const r = await api.launchCs2(mode);
    if (r.success) toast(r.message); else toast(r.error || "启动失败", "err");
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>总览</h1>
        <p>一键启动 · 状态校验 · 快速修复</p>
      </header>

      <section className="card card--hero">
        <div className="hero__row">
          <div className={`hero__dot ${m?.clean ? "is-idle" : m?.ok ? "is-ok" : m ? "is-bad" : ""}`} />
          <div className="hero__txt">
            <b>
              {!m ? "正在校验…"
                : m.clean ? "未安装（干净状态）"
                : m.ok ? "插件全部就位"
                : `缺 ${m.modMissing ?? m.missing.length} 项插件文件`}
            </b>
            <span>
              {!m ? "读取安装清单"
                : m.clean ? `55 项检查里，${m.officialTotal} 项是游戏原文件（本就存在），${m.modTotal + 4} 项是插件文件`
                : `插件 ${m.modPresent}/${m.modTotal} · 官方文件 ${m.officialPresent}/${m.officialTotal} · 图标 ${m.iconCount || 0} 个`}
            </span>
          </div>
          <button className="btn btn--ghost btn--sm" onClick={() => refresh()}>{I.refresh(14)} 重检</button>
        </div>
        {m?.misplaced && (
          <div className="alert alert--warn">
            <b>解压位置错误</b>
            <code>{m.misplaced}</code>
          </div>
        )}
        {m && !m.ok && !m.clean && m.missing.length > 0 && (
          <details className="alert alert--bad">
            <summary>缺失明细（{m.missing.length}）</summary>
            <ul>{m.missing.slice(0, 30).map((x, i) => <li key={i}><code>{x}</code></li>)}</ul>
          </details>
        )}
        {m && m.clean && (
          <p className="note">
            这 55 项里，{m.officialTotal} 项（gameinfo.gi 和 14 个 gamemode 配置）是 Steam 自带的原文件，永远都在，不需要删；
            另外 {m.modTotal + 4} 项是插件文件，装完才会有。点下面的「安装 / 修复套件」开始装。
          </p>
        )}
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">运行模式</span>
          <span className={`pill ${meta.skinMode?.enabled ? "pill--ok" : ""}`}>
            {meta.skinMode?.enabled ? "只开换肤" : "完整人机"}
          </span>
        </div>
        <Seg value={meta.skinMode?.enabled ? "skin" : "full"}
          options={[{ value: "full", label: "完整人机" }, { value: "skin", label: "只开换肤" }]}
          onChange={async (v) => {
            const r = await api.setSkinMode(v === "skin");
            if (r.error) return toast(r.error, "err");
            await softRefresh();
            toast(r.message || "已切换");
          }} />
        <p className="note">
          {meta.skinMode?.enabled
            ? "当前只加载库存换肤（InventorySimulator）。人机 AI、饰品随机、规则、徽章全部停用，难度不受影响。"
            : "完整模式：人机 AI + 饰品随机 + 规则 + 徽章全开。只想换肤就去切「只开换肤」。"}
        </p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">{I.play(15)} 启动游戏</span></div>
        <div className="launch">
          <button className="launch-card launch-card--main" onClick={() => launch("insecure")}>
            <span className="launch-card__ico">{I.bolt(20)}</span>
            <span className="launch-card__t">插件模式</span>
            <span className="launch-card__d">挂载 Metamod · 人机增强生效</span>
          </button>
          <button className="launch-card" onClick={() => launch("normal")}>
            <span className="launch-card__ico launch-card__ico--calm">{I.shield(20)}</span>
            <span className="launch-card__t">官方原版</span>
            <span className="launch-card__d">摘掉挂载 · 纯原版</span>
          </button>
        </div>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">兼容性</span>
          <span className={`pill ${meta.iconOverlay?.enabled ? "pill--ok" : "pill--bad"}`}>
            {meta.iconOverlay?.enabled ? "已启用" : "已停用"}
          </span>
        </div>
        <div className="row">
          <div className="row__txt">
            <b>击杀图标资源覆盖</b>
            <span>1593 个图标文件 + gameinfo 挂载行。进游戏报 D3D 错误或主界面闪退时，先关掉它再试</span>
          </div>
          <Toggle on={!!meta.iconOverlay?.enabled} onChange={async (v) => {
            const r = await api.setIconOverlay(v);
            if (r.error) return toast(r.error, "err");
            await softRefresh();
            toast(v ? "图标覆盖已启用" : "图标覆盖已停用，图标会回到原版");
          }} />
        </div>
        <p className="note">
          关掉之后击杀图标全部失效，但人机增强、徽章、规则不受影响。判断是不是它的问题，就靠这个开关。
        </p>
      </section>


      <section className="card">
        <div className="card__head"><span className="card__title">维护</span></div>
        <div className="grid2">
          <button className="btn btn--soft" disabled={busy === "install"} onClick={async () => {
            setBusy("install");
            const r = await api.install(path);
            setBusy("");
            if (r.success) { await refresh(); toast(`安装完成，${r.installedCount} 项就绪`); }
            else toast(r.error || "安装失败", "err");
          }}>{busy === "install" ? "安装中…" : "安装 / 修复套件"}</button>
          <button className="btn btn--ghost" onClick={async () => {
            const r = await api.applyConfigs();
            if (r.success) { await refresh(); toast("配置已重新下发"); } else toast(r.error || "失败", "err");
          }}>重新下发配置</button>
        </div>
        <button className="btn btn--danger btn--block" style={{ marginTop: 10 }} onClick={async () => {
          if (!confirm("彻底删除全部插件？\n\n会直接删掉 addons / overrides / 图标资源，不留备份。\n被覆盖的官方原文件会自动还原，Steam 不会要求重新下载。\n\n删除后要恢复，重新点「安装 / 修复套件」即可。")) return;
          const r = await api.uninstall();
          if (r.error) return toast(r.error, "err");
          await refresh();
          if (r.success) {
            const n = (r.removed || []).length;
            toast(`已彻底删除 ${n} 项，还原 ${r.restored ?? 0} 个官方文件`);
            if (r.needsVerify) {
              toast("没有官方文件快照，请在 Steam 里验证一次游戏文件完整性", "err");
            }
          } else {
            toast(`部分失败：${(r.failed || []).join("; ")}`, "err");
          }
        }}>{I.trash(13)} 彻底删除插件</button>
      </section>

      <section className="card card--tight">
        <button className="linkbtn" onClick={() => api.openUrl("https://inventory.cstrike.app")}>
          {I.cart(18)}
          <span><b>打开皮肤库存</b><em>inventory.cstrike.app</em></span>
          <i>↗</i>
        </button>
      </section>

      <section className="card card--tight">
        <div className="kv">
          <span>游戏目录</span><code className="kv__path">{path || "未设置"}</code>
        </div>
        <div className="kv">
          <span>框架</span><b>{ready ? "Metamod + CounterStrikeSharp 就绪" : "未安装"}</b>
        </div>
      </section>
    </div>
  );
}

/* ═══════════ 页：插件 ═══════════ */
function Plugins({ state, meta, refresh, refreshState, toast }) {
  const P = state.plugins || {};
  const cat = state.pluginCatalog || [];
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const [busy, setBusy] = useState("");

  const toggle = async (id, on) => {
    setBusy(id);
    const r = await api.togglePlugin(id, on);
    setBusy("");
    if (r.success) { await refreshState(); toast(on ? "已开启" : "已关闭"); }
    else toast(r.error || "操作失败", "err");
  };

  const groups = [
    { key: "framework", label: "框架", desc: "缺一不可，常驻" },
    { key: "ai", label: "AI 增强", desc: "控制层 + 决策层" },
    { key: "visual", label: "外观展示", desc: "徽章 · 图标 · 饰品" },
    { key: "feature", label: "功能模块", desc: "附加能力" },
  ];

  return (
    <div className="page">
      <header className="page__head">
        <h1>插件</h1>
        <p>清单校验 + 单项开关 · 运行中禁止改动</p>
      </header>

      <section className="card">
        <div className="card__head">
          <span className="card__title">批量</span>
          <span className="pill">{meta.manifest ? `${meta.manifest.present}/${meta.manifest.total}` : "—"}</span>
        </div>
        <div className="grid2">
          <button className="btn btn--soft" onClick={async () => {
            const r = await api.setAllPlugins(true);
            await refreshState(); toast(r.success ? "已全部开启" : r.error || "失败");
          }}>全部开启</button>
          <button className="btn btn--ghost" onClick={async () => {
            const r = await api.setAllPlugins(false);
            await refreshState(); toast(r.success ? "已全部关闭" : r.error || "失败");
          }}>全部关闭</button>
        </div>
      </section>

      {groups.map((g) => {
        const list = cat.filter((p) => p.category === g.key);
        if (!list.length) return null;
        return (
          <section className="card" key={g.key}>
            <div className="card__head">
              <span className="card__title">{g.label}</span>
              <span className="card__hint">{g.desc}</span>
            </div>
            <div className="rows">
              {list.map((p) => {
                const st = P[p.id] || {};
                return (
                  <div className="row" key={p.id}>
                    <div className="row__txt">
                      <b>{p.name}{p.alwaysOn && <i className="tag tag--lock">常驻</i>}</b>
                      <span>{p.desc}</span>
                    </div>
                    <Toggle on={!!st.enabled} disabled={!ready || p.alwaysOn || busy === p.id}
                      onChange={(v) => toggle(p.id, v)} />
                  </div>
                );
              })}
            </div>
          </section>
        );
      })}
    </div>
  );
}

/* ═══════════ 页：人机 ═══════════ */
const BOT_ITEMS = [
  { key: "weapons", label: "武器皮肤", desc: "bot_randomizer weapons" },
  { key: "knives", label: "刀", desc: "bot_randomizer knives" },
  { key: "gloves", label: "手套", desc: "bot_randomizer gloves" },
  { key: "stickers", label: "贴纸", desc: "bot_randomizer stickers" },
  { key: "charms", label: "挂件", desc: "bot_randomizer charms" },
  { key: "agents", label: "人物模型", desc: "bot_randomizer agents" },
  { key: "music", label: "音乐盒", desc: "bot_randomizer music" },
  { key: "profiles", label: "身份伪装", desc: "BotHider 原生插件开关" },
];

function Bots({ state, meta, refresh, refreshState, patchMeta, toast }) {
  const items = meta.items || {};
  const pre = meta.presets || {};
  const ready = !!(state.installed?.metamod && state.installed?.css);

  const setItem = async (k, v) => {
    const r = await api.setBotItem(k, v);
    if (r.error) return toast(r.error, "err");
    if (r.items) patchMeta({ items: r.items });
    toast(v ? "已开启" : "已关闭");
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>人机</h1>
        <p>难度 · 饰品随机 · 瞄准与投掷物预设</p>
      </header>

      <section className="card">
        <div className="card__head"><span className="card__title">难度档位</span></div>
        <Seg disabled={!ready}
          value={state.difficulty || "Medium"}
          options={[
            { value: "Low", label: "低" },
            { value: "Medium", label: "中" },
            { value: "High", label: "高" },
          ].filter((o) => !meta.difficulty?.available?.length || meta.difficulty.available.includes(o.value))}
          onChange={async (v) => {
            const r = await api.setDifficulty(v);
            if (r.error) return toast(r.error, "err");
            await refreshState();
            toast(`难度已切到 ${v === "Low" ? "低" : v === "Medium" ? "中" : "高"}（已写入 overrides）`);
          }} />
        <p className="note">
          实际动作是把 <code>overrides/{state.difficulty || "Medium"}/botprofile.vpk</code> 复制到
          <code>overrides/botprofile.vpk</code>，gameinfo 挂的就是这个文件。首次改动会留一份 <code>botprofile.vpk.bak</code>。
        </p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">Bot 物品</span><span className="card__hint">写进 xuerong_manager.cfg</span></div>
        <div className="rows">
          {BOT_ITEMS.map((it) => (
            <div className="row" key={it.key}>
              <div className="row__txt"><b>{it.label}</b><span>{it.desc}</span></div>
              <Toggle on={!!items[it.key]} disabled={!ready} onChange={(v) => setItem(it.key, v)} />
            </div>
          ))}
        </div>
        <p className="note">bot_randomizer 是 CSS 命令，需在游戏内 <code>exec my_bot_normal_config</code> 或由管理器写入后开一局生效。</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">瞄准模式</span></div>
        <Seg disabled={!ready}
          value={pre.aim || "mixed"}
          options={[{ value: "head", label: "锁头" }, { value: "mixed", label: "混合" }, { value: "body", label: "锁身" }]}
          onChange={async (v) => { const r = await api.setAim(v); if (r.error) return toast(r.error, "err"); patchMeta({ presets: { ...pre, aim: r.aim || v } }); toast(`瞄准：${v}`); }} />
        <p className="note">对应游戏内 <code>bot_aim head|mixed|body</code>。</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">投掷物强度</span></div>
        <Seg disabled={!ready}
          value={pre.nades || "normal"}
          options={[{ value: "off", label: "关" }, { value: "less", label: "少" }, { value: "normal", label: "正常" }, { value: "more", label: "多" }, { value: "max", label: "满" }]}
          onChange={async (v) => { const r = await api.setNades(v); if (r.error) return toast(r.error, "err"); patchMeta({ presets: { ...pre, nades: r.nades || v } }); toast(`投掷物：${v}`); }} />
        <p className="note">对应游戏内 <code>bot_nades off|less|normal|more|max</code>。</p>
      </section>
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
    toast("规则已更新");
  };

  const ROWS = [
    { key: "overtime", label: "加时赛", desc: "打平后进入加时 · mp_overtime_enable" },
    { key: "friendly_fire", label: "友军伤害", desc: "队友之间互相造成伤害 · mp_friendlyfire" },
    { key: "boosting", label: "允许叠人", desc: "关掉则敌人不可互相穿过 · mp_solid_enemies" },
    { key: "bhop", label: "连跳", desc: "空中连续跳跃不掉速 · sv_enablebunnyhopping" },
  ];

  return (
    <div className="page">
      <header className="page__head">
        <h1>规则</h1>
        <p>写进三套预设 cfg + 管理器 cfg，开一局就生效</p>
      </header>

      <section className="card">
        <div className="card__head"><span className="card__title">对局规则</span></div>
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
        <div className="card__head"><span className="card__title">C4 倒计时</span><span className="pill">{c4}s</span></div>
        <input className="range" type="range" min="10" max="120" step="5" value={c4}
          onChange={(e) => setC4(Number(e.target.value))}
          onPointerUp={async () => {
            const res = await api.setC4Timer(c4);
            if (res.error) return toast(res.error, "err");
            if (res.rules) patchMeta({ rules: res.rules });
            toast(`C4 倒计时 ${c4} 秒`);
          }} />
        <p className="note">默认 40 秒，范围 10–120。写 mp_c4timer。</p>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">燃烧瓶伤害倍率</span><span className="pill">×{mol.toFixed(2)}</span></div>
        <input className="range" type="range" min="0.1" max="3" step="0.05" value={mol}
          onChange={(e) => setMol(Number(e.target.value))}
          onPointerUp={async () => {
            const res = await api.setMolotovMultiplier(mol);
            if (res.error) return toast(res.error, "err");
            if (res.rules) patchMeta({ rules: res.rules });
            toast(`燃烧瓶 ×${mol.toFixed(2)}`);
          }} />
        <p className="note">按原版 40 点伤害换算，同时写 inferno_damage 与 inferno_damage_ct。</p>
      </section>

      <button className="btn btn--ghost btn--block" onClick={async () => {
        const res = await api.resetRules();
        if (res.error) return toast(res.error, "err");
        if (res.rules) patchMeta({ rules: res.rules });
        toast("规则已恢复默认");
      }}>恢复默认规则</button>
    </div>
  );
}

/* ═══════════ 页：外观 ═══════════ */
function Look({ state, meta, refresh, patchMeta, toast }) {
  const badge = meta.badge?.config || {};
  const kf = meta.killfeed || {};
  const ready = !!(state.installed?.metamod && state.installed?.css);
  const [busyPack, setBusyPack] = useState("");
  const installed = meta.badge?.installed;

  const patch = async (obj) => {
    const res = await api.setBadge(obj);
    if (res.error) return toast(res.error, "err");
    if (res.config) patchMeta({ badge: { installed: meta.badge?.installed, config: res.config } });
    toast("徽章配置已写入");
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>外观</h1>
        <p>XP 过载徽章 · 动态 MVP 名片 · 击杀图标</p>
      </header>

      <div className="hintbar">
        <b>保持默认即可，不要修改</b>
        <span>下面这些参数已经是调好的状态。没有明确需求就别动，改错了会让徽章或 MVP 名片不显示。</span>
      </div>

      <section className="card">
        <div className="card__head">
          <span className="card__title">XP 过载徽章</span>
          <span className={`pill ${installed ? "pill--ok" : "pill--bad"}`}>{installed ? "已安装" : "未安装"}</span>
        </div>
        <div className="row">
          <div className="row__txt"><b>启用徽章</b><span>m_nPersonaDataXpTrailLevel · 1~8 档配色</span></div>
          <Toggle on={!!badge.EnableXpBadge} disabled={!installed}
            onChange={(v) => patch({ EnableXpBadge: v })} />
        </div>
        <div className="slider-row">
          <span>档位下限</span>
          <input className="range" type="range" min="1" max="8" step="1" value={badge.XpMin ?? 1}
            onChange={(e) => patch({ XpMin: Number(e.target.value) })} />
          <b>{badge.XpMin ?? 1}</b>
        </div>
        <div className="slider-row">
          <span>档位上限</span>
          <input className="range" type="range" min="1" max="8" step="1" value={badge.XpMax ?? 8}
            onChange={(e) => patch({ XpMax: Number(e.target.value) })} />
          <b>{badge.XpMax ?? 8}</b>
        </div>
        <div className="slider-row">
          <span>自己档位</span>
          <input className="range" type="range" min="1" max="8" step="1" value={badge.SelfXp ?? 7}
            onChange={(e) => patch({ SelfXp: Number(e.target.value) })} />
          <b>{badge.SelfXp ?? 7}</b>
        </div>
        <div className="seg-line">
          <span>分配方式</span>
          <Seg value={badge.XpMode || "slot"} disabled={!installed}
            options={[{ value: "slot", label: "按槽位" }, { value: "random", label: "随机" }]}
            onChange={(v) => patch({ XpMode: v })} />
        </div>
        <div className="row">
          <div className="row__txt"><b>也给真人写</b><span>XpToHumans · 关掉则只有 bot 有徽章</span></div>
          <Toggle on={badge.XpToHumans !== false} disabled={!installed}
            onChange={(v) => patch({ XpToHumans: v })} />
        </div>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">动态 MVP 名片</span></div>
        <div className="row">
          <div className="row__txt"><b>启用动态 MVP</b><span>m_iCompetitiveRankType ≥ 11 触发播报</span></div>
          <Toggle on={!!badge.EnableMvp} disabled={!installed}
            onChange={(v) => patch({ EnableMvp: v })} />
        </div>
        <div className="slider-row">
          <span>段位类型</span>
          <input className="range" type="range" min="1" max="18" step="1" value={badge.RankType ?? 11}
            onChange={(e) => patch({ RankType: Number(e.target.value) })} />
          <b>{badge.RankType ?? 11}</b>
        </div>
        <div className="row">
          <div className="row__txt"><b>也给真人写 RankType</b><span>会隐藏真人的 Premier 闪电徽章，默认关</span></div>
          <Toggle on={!!badge.WriteRankTypeToHumans} disabled={!installed}
            onChange={(v) => patch({ WriteRankTypeToHumans: v })} />
        </div>
        <p className="note">RankType ≥ 11 才触发动态名片；写真人会顶掉他的 Premier 徽章，两者互斥。</p>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">击杀图标</span>
          <span className={`pill ${kf.mounted ? "pill--ok" : "pill--bad"}`}>{kf.mounted ? "已挂载" : "未挂载"}</span>
        </div>
        <div className="packs">
          {(kf.packs || []).map((p) => (
            <button key={p} className={`pack ${kf.current === p ? "is-on" : ""}`} disabled={busyPack === p}
              onClick={async () => {
                setBusyPack(p);
                const r = await api.setKillfeed(p);
                setBusyPack("");
                if (r.success) {
                  const kf2 = await api.getKillfeed().catch(() => null);
                  if (kf2 && !kf2.error) patchMeta({ killfeed: kf2 });
                  toast(`已切换 ${p}`);
                } else toast(r.error || "失败", "err");
              }}>{busyPack === p ? "…" : p}</button>
          ))}
        </div>
        <p className="note">
          图标资源 {kf.iconCount || 0} 个。游戏内聊天框发 <code>!weaponkill</code> 再发 <code>!编号</code> 才会立刻生效。
        </p>
        <button className="btn btn--soft btn--block" onClick={async () => {
          try { await navigator.clipboard.writeText("!weaponkill"); toast("已复制 !weaponkill"); }
          catch { toast("复制失败，手动输入", "err"); }
        }}>{I.copy(13)} 复制游戏内指令</button>
      </section>

      <section className="card card--tight">
        <button className="linkbtn" onClick={() => api.openUrl("https://inventory.cstrike.app")}>
          {I.cart(18)}
          <span><b>打开皮肤库存</b><em>inventory.cstrike.app</em></span>
          <i>↗</i>
        </button>
      </section>
    </div>
  );
}

/* ═══════════ 页：交互式教程 ═══════════ */
const GUIDE = [
  {
    t: "先完全退出 CS2",
    d: "所有装插件、改配置、换难度的操作，只要 CS2 在跑就会失败——正在加载的 dll 被系统锁住了。",
    pts: [
      "任务栏右键 CS2 → 退出，别只点 ×（Steam 里可能还在后台）",
      "不确定就先点下面的「重新检测状态」",
    ],
    action: { label: "重新检测状态", kind: "refresh" },
  },
  {
    t: "确认游戏路径",
    d: "管理器得知道游戏装在哪。选到游戏根目录这一层，不是 game/csgo。",
    pts: [
      "路径长这样：D:/apps/steam/steamapps/common/Counter-Strike Global Offensive",
      "设置页有「自动检测」和「浏览…」两个按钮",
    ],
    action: { label: "去设置页", kind: "go", target: "settings" },
  },
  {
    t: "安装 / 修复套件",
    d: "把整套插件解压进游戏，同时自动备份会被覆盖的原文件。第一次装、或者游戏更新后失效，都点这个。",
    pts: [
      "装完状态灯会从红变绿",
      "去「插件」页确认：插件 36/36、官方文件 15/15",
      "解压大约 30–60 秒，别以为卡死了",
    ],
    action: { label: "去总览页安装", kind: "go", target: "home" },
  },
  {
    t: "想只换肤？切到「只开换肤」",
    d: "总览页顶部有个「运行模式」，可以在「完整人机」和「只开换肤」之间切。",
    pts: [
      "只开换肤 = 只加载库存换肤插件，人机 AI、饰品随机、徽章全部停用",
      "难度和规则不受影响，切过去照样能调",
      "想恢复人机增强，切回「完整人机」即可，插件不会丢",
    ],
    action: { label: "去总览页切换", kind: "go", target: "home" },
  },
  {
    t: "用插件模式启动",
    d: "只有「插件模式」进游戏，插件才生效。官方原版会把挂载摘掉，插件不加载。",
    pts: [
      "插件模式 = cs2.exe -steam -insecure -console",
      "打官匹就用官方原版，别混用",
    ],
    action: { label: "去总览页启动", kind: "go", target: "home" },
  },
  {
    t: "调整难度",
    d: "难度不是一个数字，是三份不同的 botprofile.vpk。切换时会把对应档位复制成 overrides/botprofile.vpk，同时改 bot_difficulty。",
    pts: [
      "先在「低 / 中 / 高」三个档位里点选一个，再点应用，不要直接拖",
      "切换后必须重开游戏才生效，当前这局不会变",
      "改的时候 CS2 要完全关着，否则会提示先退游戏",
    ],
    action: { label: "去人机页", kind: "go", target: "bots" },
  },
  {
    t: "修改对局规则",
    d: "加时、友伤、叠人、连跳、C4 倒计时、燃烧瓶倍率，六项都是真写进 cfg 的。",
    pts: [
      "同时写管理器 cfg 和三套预设，不会被冲掉",
      "「允许叠人」是反的：开 = mp_solid_enemies 0",
      "改完下一局生效，不用重开游戏",
    ],
    action: { label: "去规则页", kind: "go", target: "rules" },
  },
  {
    t: "外观：保持默认",
    d: "XP 过载徽章、动态 MVP 名片的参数已经调好了，没有明确需求就别动，改错了徽章会不显示。",
    pts: [
      "击杀图标在软件里选的只是默认值",
      "游戏内聊天框先发 !weaponkill，再发 !编号（1–18）才会立刻生效",
      "右上角状态灯要显示「已挂载」，否则图标全不显示",
    ],
    action: { label: "去外观页", kind: "go", target: "look" },
  },
  {
    t: "换肤：走链接，先登录账号",
    d: "皮肤不在管理器里改。点「打开皮肤库存」直达库存网站，在那里配装。",
    pts: [
      "必须先登录你玩 CS2 用的那个账号，否则改了不生效",
      "登录后再改皮肤，进游戏才会显示",
      "换肤插件要开着（只开换肤模式下默认就是开的）",
    ],
    action: { label: "打开皮肤库存", kind: "url", url: "https://inventory.cstrike.app" },
  },
  {
    t: "出问题了怎么办",
    d: "记住三个按钮就够了：重新下发配置、彻底删除插件、安装 / 修复套件。",
    pts: [
      "插件突然全失效 → 重新下发配置",
      "不想玩了 → 彻底删除插件（直接删，不保留备份）",
      "删了想装回来 → 安装 / 修复套件",
    ],
    action: { label: "查看更新公告", kind: "notice" },
  },
];

function Guide({ go, meta, refresh, toast, openNotice }) {
  const [step, setStep] = useState(() => {
    const n = Number(localStorage.getItem("xr.guide.step") || 0);
    return Number.isFinite(n) ? Math.min(Math.max(n, 0), GUIDE.length - 1) : 0;
  });
  useEffect(() => { try { localStorage.setItem("xr.guide.step", String(step)); } catch {} }, [step]);
  useEffect(() => { try { localStorage.setItem("xr.guide.seen", "1"); } catch {} }, []);

  const cur = GUIDE[step];
  const last = step === GUIDE.length - 1;
  const pct = ((step + 1) / GUIDE.length) * 100;

  const run = async () => {
    const a = cur.action;
    if (a.kind === "go") { go(a.target); return; }
    if (a.kind === "notice") { openNotice(); return; }
    if (a.kind === "url") { api.openUrl(a.url); return; }
    if (a.kind === "refresh") {
      await refresh();
      const m = meta.manifest;
      toast(m ? (m.ok ? "全部就位，可以启动" : `还缺 ${m.missing.length} 项`) : "检测完成",
            m && !m.ok ? "err" : "ok");
      return;
    }
    if (a.kind === "copy") {
      try { await navigator.clipboard.writeText(a.text); toast("已复制，进游戏粘贴"); }
      catch { toast("复制失败，手动输入 " + a.text, "err"); }
    }
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>教程</h1>
        <p>十步走完，这套东西就全通了</p>
      </header>

      <section className="card guide">
        <div className="guide__bar"><i style={{ width: `${pct}%` }} /></div>

        <div className="guide__dots">
          {GUIDE.map((g, i) => (
            <button key={i} className={`guide__dot ${i === step ? "is-on" : ""} ${i < step ? "is-done" : ""}`}
              onClick={() => setStep(i)} title={g.t}>{i + 1}</button>
          ))}
        </div>

        <div className="guide__head">
          <span className="guide__no">{String(step + 1).padStart(2, "0")}</span>
          <div>
            <h2>{cur.t}</h2>
            <p>{cur.d}</p>
          </div>
        </div>

        <ul className="guide__pts">
          {cur.pts.map((x, i) => <li key={i}>{x}</li>)}
        </ul>

        <button className="btn btn--soft btn--block" onClick={run}>
          {cur.action.label} {I.arrow(13)}
        </button>

        <div className="guide__nav">
          <button className="btn btn--ghost" disabled={step === 0} onClick={() => setStep(step - 1)}>上一步</button>
          {last
            ? <button className="btn btn--soft" onClick={() => { setStep(0); toast("看完啦喵，去总览启动吧"); go("home"); }}>完成，去启动</button>
            : <button className="btn btn--soft" onClick={() => setStep(step + 1)}>下一步</button>}
        </div>
      </section>

      <p className="note">随时可以从左边栏回到这一页；进度会记住，下次打开接着看。</p>
    </div>
  );
}

/* ═══════════ 页：命令 ═══════════ */
const CMD_GROUPS = [
  {
    key: "net",
    label: "联机 / 服务器",
    items: [
      { cmd: "status", desc: "看服务器状态、所有玩家、IP 和端口" },
      { cmd: "connect 192.168.1.10:27015", desc: "直连服务器，把 IP 换成房主的" },
      { cmd: "disconnect", desc: "断开当前服务器" },
      { cmd: "reconnect", desc: "重连上一次进的服务器" },
      { cmd: "sv_lan 1", desc: "切成局域网模式，同路由器的人更容易连上" },
      { cmd: "sv_password 1234", desc: "给房间设密码" },
      { cmd: "sv_password \"\"", desc: "取消房间密码" },
      { cmd: "hostname 我的房间", desc: "改房间名，服务器列表里显示这个" },
      { cmd: "map de_dust2", desc: "换图，会重开一局" },
      { cmd: "changelevel de_mirage", desc: "换图，保留比分和金钱" },
      { cmd: "mp_restartgame 1", desc: "1 秒后重开当前这局" },
      { cmd: "mp_warmup_end", desc: "立刻结束热身，直接开打" },
      { cmd: "kick 玩家名", desc: "踢人，房主才能用" },
      { cmd: "say 大家好", desc: "全体发言" },
      { cmd: "say_team 集合 B 点", desc: "队内发言" },
    ],
  },
  {
    key: "bot",
    label: "人机控制",
    items: [
      { cmd: "bot_add", desc: "加一个 bot，自动平衡队伍" },
      { cmd: "bot_add_t", desc: "给 T 方加一个 bot" },
      { cmd: "bot_add_ct", desc: "给 CT 方加一个 bot" },
      { cmd: "bot_kick", desc: "踢掉场上所有 bot" },
      { cmd: "bot_kill", desc: "杀死所有 bot" },
      { cmd: "bot_stop 1", desc: "冻结 bot 移动，调试用" },
      { cmd: "bot_stop 0", desc: "解除冻结" },
      { cmd: "bot_difficulty 3", desc: "bot 难度 0–5，数字越大越强" },
      { cmd: "bot_quota 10", desc: "场上保持多少个 bot" },
      { cmd: "bot_quota_mode fill", desc: "补满模式：人不够时自动补 bot" },
      { cmd: "bot_mimic 1", desc: "bot 模仿你的动作" },
      { cmd: "bot_place", desc: "在准星位置放一个 bot" },
      { cmd: "bot_crouch 1", desc: "所有 bot 蹲下" },
      { cmd: "bot_zombie 1", desc: "bot 不移动也不还手" },
      { cmd: "bot_dont_shoot 1", desc: "bot 不开枪" },
    ],
  },
  {
    key: "rule",
    label: "对局规则",
    items: [
      { cmd: "mp_freezetime 5", desc: "开局冻结时间，秒" },
      { cmd: "mp_roundtime 1.92", desc: "每回合时长，分钟" },
      { cmd: "mp_maxrounds 24", desc: "最多打多少回合" },
      { cmd: "mp_startmoney 800", desc: "初始金钱" },
      { cmd: "mp_buytime 20", desc: "购买时间，秒" },
      { cmd: "mp_buy_anywhere 1", desc: "允许在任意位置购买" },
      { cmd: "mp_friendlyfire 0", desc: "友军伤害，0 关 1 开" },
      { cmd: "mp_autoteambalance 0", desc: "自动平衡队伍，人机局建议关" },
      { cmd: "mp_limitteams 0", desc: "队伍人数差限制，0 = 不限" },
      { cmd: "mp_c4timer 40", desc: "C4 倒计时，秒" },
      { cmd: "mp_overtime_enable 1", desc: "打平后进加时" },
      { cmd: "mp_halftime 1", desc: "半场换边" },
      { cmd: "mp_solid_enemies 0", desc: "敌人可否互相穿过，0 = 可穿 = 能叠人" },
      { cmd: "mp_solid_teammates 2", desc: "队友碰撞，2 = 可叠" },
      { cmd: "mp_drop_knife_enable 1", desc: "允许丢刀" },
      { cmd: "mp_spectators_max 4", desc: "最多几个观战" },
    ],
  },
  {
    key: "show",
    label: "显示 / 调试",
    items: [
      { cmd: "cl_showfps 1", desc: "左上角显示帧率" },
      { cmd: "cq_netgraph 1", desc: "显示网络参数图（丢包、延迟）" },
      { cmd: "cl_showpos 1", desc: "显示坐标和移动速度" },
      { cmd: "sensitivity 2", desc: "鼠标灵敏度" },
      { cmd: "cl_crosshairsize 3", desc: "准星大小" },
      { cmd: "cl_radar_scale 0.35", desc: "雷达缩放，越小看得越远" },
      { cmd: "voice_scale 0.5", desc: "队友语音音量" },
      { cmd: "sv_cheats 1", desc: "开作弊模式，noclip 和 god 需要它" },
      { cmd: "noclip", desc: "穿墙飞行（要 sv_cheats 1）" },
      { cmd: "god", desc: "无敌（要 sv_cheats 1）" },
      { cmd: "demoui", desc: "录像播放控制台" },
    ],
  },
  {
    key: "plugin",
    label: "插件命令",
    items: [
      { cmd: "exec my_bot_normal_config", desc: "加载常规人机预设，规则和饰品随机全在这份里" },
      { cmd: "exec my_bot_ffa_config", desc: "加载死斗预设" },
      { cmd: "exec my_bot_rush_config", desc: "加载 rush 预设" },
      { cmd: "exec xuerong_manager", desc: "只应用管理器里设的规则，不动预设" },
      { cmd: "bot_aim head", desc: "人机瞄准锁头（mixed / body）" },
      { cmd: "bot_nades max", desc: "投掷物拉满（off / less / normal / more / max）" },
      { cmd: "bot_randomizer weapons 1", desc: "开 bot 武器皮肤（knives/gloves/stickers/charms/agents/music 同理）" },
      { cmd: "css_badge_mvp status", desc: "查看 XP 徽章和 MVP 名片当前状态" },
      { cmd: "css_badge_mvp rt 11", desc: "直接改 RankType，≥11 触发动态 MVP" },
      { cmd: "!weaponkill", desc: "打开击杀图标选包菜单" },
      { cmd: "!3", desc: "选第 3 套击杀图标，编号 1–18" },
    ],
  },
];

// 扁平化 + 分类标签只算一次（68 条，搜索时不再重建）
const ALL_CMDS = CMD_GROUPS.flatMap((g) => g.items.map((i) => ({ ...i, group: g.key })));
const CMD_TABS = [{ key: "all", label: `全部 ${ALL_CMDS.length}` }]
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
    try { await navigator.clipboard.writeText(cmd); toast(`已复制 ${cmd}`); }
    catch { toast("复制失败，手动输入", "err"); }
  };

  return (
    <div className="page">
      <header className="page__head">
        <h1>命令</h1>
        <p>共 {all.length} 条，点一下复制，进游戏控制台粘贴</p>
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
          placeholder="搜命令或说明，比如 status / 联机 / 难度" />
      </section>

      <section className="card">
        {list.length === 0 ? (
          <p className="note" style={{ margin: 0 }}>没找到匹配的命令，换个关键词试试。</p>
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
        控制台默认按 <code>~</code> 打开。没反应的话，Steam 库右键 CS2 → 属性 → 启动选项加 <code>-console</code>；
        管理器用「插件模式」启动时已经自动带上了。
      </p>
      <p className="note">
        标着「要 sv_cheats 1」的命令，先发 <code>sv_cheats 1</code> 再发它，否则无效。
        联机时房主才需要开 cheats，加入的人不用。
      </p>
    </div>
  );
}

/* ═══════════ 页：设置 ═══════════ */
function Settings({ state, meta, refresh, softRefresh, toast, bg, setBg, go, openNotice, updateInfo, setUpdateInfo }) {
  const { visible: playerVisible, setVisible: setPlayerVisible } = useMusic();
  const [prog, setProg] = useState(null);
  const [dlError, setDlError] = useState(null);

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
        <h1>设置</h1>
        <p>路径 · 背景 · 音乐 · 作者</p>
      </header>

      <section className="card author">
        <div className="author__ring"><img src={avatarImg} alt="" /></div>
        <div>
          <div className="author__name">噗噗咩咩</div>
          <div className="author__sub">作者</div>
        </div>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">游戏路径</span></div>
        <code className="kv__path">{path || "未设置"}</code>
        <div className="grid2" style={{ marginTop: 10 }}>
          <button className="btn btn--soft" onClick={async () => {
            const r = await api.detectPath();
            if (r.path) { await api.setPath(r.path); await refresh(); toast("已自动检测"); }
            else toast("未检测到 CS2", "err");
          }}>自动检测</button>
          <button className="btn btn--soft" onClick={async () => {
            const p = await api.pickFolder();
            if (!p) return;
            const r = await api.setPath(p);
            if (r.success) { await refresh(); toast("路径已更新"); } else toast(r.error || "路径无效", "err");
          }}>浏览…</button>
        </div>
        <div className="row" style={{ marginTop: 12 }}>
          <div className="row__txt"><b>模式</b><span>插件模式需挂载 metamod 搜索路径</span></div>
          <Seg value={meta.mode?.current || "bots"} disabled={meta.mode?.cs2Running}
            options={[{ value: "bots", label: "插件" }, { value: "online", label: "官方" }]}
            onChange={async (v) => {
              const r = await api.setMode(v);
              if (r.error) return toast(r.error, "err");
              await softRefresh(); toast(v === "bots" ? "已切到插件模式" : "已切回官方模式");
            }} />
        </div>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">背景显示</span></div>
        <div className="slider-row">
          <span>模糊</span>
          <input className="range" type="range" min="0" max="24" step="1" value={bg.blur}
            onChange={(e) => setBg({ ...bg, blur: Number(e.target.value) })} />
          <b>{bg.blur}px</b>
        </div>
        <div className="slider-row">
          <span>遮罩</span>
          <input className="range" type="range" min="15" max="92" step="1" value={bg.veil}
            onChange={(e) => setBg({ ...bg, veil: Number(e.target.value) })} />
          <b>{bg.veil}%</b>
        </div>
        <p className="note">看不清文字就把遮罩调高；想看视频就把模糊调到 0。</p>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">背景音乐</span>
          <span className={`pill ${playerVisible ? "pill--ok" : ""}`}>{playerVisible ? "播放器已显示" : "已隐藏"}</span>
        </div>
        <div className="row">
          <div className="row__txt">
            <b>显示播放器</b>
            <span>左下角那个卡片。可以导入本地音乐、切歌、调音量</span>
          </div>
          <Toggle on={playerVisible} onChange={(v) => setPlayerVisible(v)} />
        </div>
        <p className="note">关掉后左下角会留一个小音符按钮，点一下就能重新打开。</p>
      </section>

      <section className="card">
        <div className="card__head">
          <span className="card__title">软件更新</span>
          <span className={`pill ${updateInfo?.hasUpdate ? "pill--bad" : "pill--ok"}`}>
            {prog ? "更新中" : updateInfo?.hasUpdate ? `有新版本 ${updateInfo.latest}` : "已是最新"}
          </span>
        </div>

        <div className="kv"><span>当前版本</span><b>v{VER.replace(/^v/, "")}</b></div>
        {updateInfo?.latest && <div className="kv"><span>最新版本</span><b>{updateInfo.latest}</b></div>}

        {prog ? (
          <div className="prog">
            <div className="prog__bar"><i style={{ width: `${prog.percent || 0}%` }} /></div>
            <div className="prog__meta">
              <span>
                {prog.phase === "installing"
                  ? "下载完成，正在安装…程序会自动关闭"
                  : `正在下载${prog.source ? `（${prog.source}）` : ""}`}
              </span>
              <b>
                {(prog.downloaded / 1048576).toFixed(1)}
                {prog.total ? ` / ${(prog.total / 1048576).toFixed(1)}` : ""} MB
              </b>
            </div>
          </div>
        ) : (
          <div className="grid2" style={{ marginTop: 10 }}>
            <button className="btn btn--soft" onClick={async () => {
              const r = await api.checkAppUpdate();
              if (r.error) { setUpdateInfo(null); return toast(r.error, "err"); }
              setUpdateInfo(r);
              toast(r.hasUpdate ? `发现新版本 ${r.latest}` : "已是最新版本");
            }}>检查更新</button>
            <button className="btn btn--primary" disabled={!updateInfo?.hasUpdate || !updateInfo?.url}
              onClick={async () => {
                setProg({ percent: 0, downloaded: 0, total: 0, phase: "downloading" });
                setDlError(null);
                try {
                  const r = await api.downloadUpdate(updateInfo.url, updateInfo.latest || "");
                  if (r && r.error) { setProg(null); setDlError(updateInfo.url); }
                } catch (e) {
                  setProg(null);
                  setDlError(updateInfo.url);
                  const msg = typeof e === "string" ? e.split("\n")[0] : "下载失败";
                  toast(msg, "err");
                }
              }}>立即更新</button>
          </div>
        )}

        {dlError && (
          <div className="dlfail">
            <div className="dlfail__title">自动下载失败</div>
            <div className="dlfail__text">
              自动下载会依次尝试 GitHub 官方和几个国内加速镜像，这次全部没通。
              点下面按钮用浏览器下载，装完一样是升级到最新版。
            </div>
            <code className="dlfail__url">{dlError}</code>
            <div className="dlfail__actions">
              <button className="btn btn--primary" onClick={() => { api.openUrl(dlError); }}>用浏览器下载</button>
              <button className="btn btn--light" onClick={async () => {
                try { await navigator.clipboard.writeText(dlError); toast("链接已复制"); }
                catch { toast("复制失败", "err"); }
              }}>复制链接</button>
            </div>
          </div>
        )}

        {updateInfo?.notes && (
          <p className="note" style={{ whiteSpace: "pre-wrap" }}>{updateInfo.notes}</p>
        )}

        <details className="adv">
          <summary>高级：更新源</summary>
          <label className="field">
            <input className="input" defaultValue={meta.updateSource?.source || ""}
              placeholder="github:hanhanmi/xuerong-manager（默认）"
              onBlur={async (e) => {
                const v = e.target.value.trim();
                if (v === (meta.updateSource?.source || "")) return;
                await api.setUpdateSource(v);
                await softRefresh();
                toast(v ? "更新源已保存" : "已清空更新源");
              }} />
          </label>
          <p className="note">
            填 <code>github:owner/repo</code> 走 GitHub Releases；也可填 JSON 地址。填一次就行，之后不用再管。
          </p>
        </details>
      </section>

      <section className="card">
        <div className="card__head"><span className="card__title">维护</span></div>
        <button className="btn btn--soft btn--block" onClick={async () => {
          const r = await api.applyConfigs();
          if (r.success) { await refresh(); toast("配置已重新下发"); } else toast(r.error || "失败", "err");
        }}>重新下发配置</button>
        <div className="kv" style={{ marginTop: 10 }}><span>版本</span><b>{VER}</b></div>
        <div className="kv"><span>清单</span><b>{meta.manifest ? `${meta.manifest.present}/${meta.manifest.total}` : "—"}</b></div>
        <div className="grid2" style={{ marginTop: 8 }}>
          <button className="btn btn--ghost" onClick={() => go("guide")}>重看教程</button>
          <button className="btn btn--ghost" onClick={openNotice}>更新公告</button>
        </div>
      </section>
    </div>
  );
}

/* ═══════════ 滚动弹性拉伸 ═══════════ */
function useElasticScroll(ref) {
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    let over = 0;
    let settle = 0;
    let raf = 0;
    const paint = () => {
      if (!over) { el.style.transform = ""; return; }
      el.style.transformOrigin = over > 0 ? "center top" : "center bottom";
      el.style.transform = `translateY(${(over * 0.32).toFixed(1)}px) scaleY(${(1 + Math.abs(over) / 2200).toFixed(4)})`;
    };
    const onWheel = (e) => {
      const atTop = el.scrollTop <= 0;
      const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 1;
      const pulling = (atTop && e.deltaY < 0) || (atBottom && e.deltaY > 0);
      if (!pulling) return;
      over += e.deltaY * 0.28;
      over = Math.max(-64, Math.min(64, over));
      if (raf) cancelAnimationFrame(raf);
      raf = requestAnimationFrame(paint);
      clearTimeout(settle);
      settle = setTimeout(() => {
        el.style.transition = "transform .42s cubic-bezier(.34, 1.5, .64, 1)";
        over = 0;
        paint();
        setTimeout(() => { el.style.transition = ""; }, 440);
      }, 90);
    };
    el.addEventListener("wheel", onWheel, { passive: true });
    return () => {
      el.removeEventListener("wheel", onWheel);
      clearTimeout(settle);
      if (raf) cancelAnimationFrame(raf);
    };
  }, [ref]);
}

/* ═══════════ 壳 ═══════════ */
function App() {
  const [splash, setSplash] = useState(true);
  const [state, setState] = useState(null);
  const [meta, setMeta] = useState({});
  const [notice, setNotice] = useState(false);
  const contentRef = useRef(null);
  useElasticScroll(contentRef);
  const [updateInfo, setUpdateInfo] = useState(null);
  const updateChecked = useRef(false);
  const [guideSeen, setGuideSeen] = useState(() => {
    try { return localStorage.getItem("xr.guide.seen") === "1"; } catch { return true; }
  });
  const [view, setView] = useState(() => {
    // 支持 ?view=rules 直接打开某页（调试用，正常启动无参数）
    try {
      const v = new URLSearchParams(location.search).get("view");
      return v && NAV.some((n) => n.id === v) ? v : "home";
    } catch { return "home"; }
  });
  const [dir, setDir] = useState(1);
  const { toast } = useToast();

  const [bg, setBg] = useState(() => {
    try {
      const s = JSON.parse(localStorage.getItem("xr.bg") || "{}");
      return { blur: s.blur ?? 3, veil: s.veil ?? 62 };
    } catch { return { blur: 3, veil: 62 }; }
  });

  // 首次打开（或版本变了）弹一次公告
  useEffect(() => {
    try {
      if (new URLSearchParams(location.search).has("nonotice")) return;
      if (localStorage.getItem("xr.notice") !== VER) setNotice(true);
    } catch {}
  }, []);

  const closeNotice = useCallback(() => {
    setNotice(false);
    try { localStorage.setItem("xr.notice", VER); } catch {}
  }, []);

  useEffect(() => {
    document.documentElement.style.setProperty("--bg-blur", `${bg.blur}px`);
    document.documentElement.style.setProperty("--veil", `${bg.veil / 100}`);
    localStorage.setItem("xr.bg", JSON.stringify(bg));
  }, [bg]);

  const loadMeta = useCallback(async (withManifest = true) => {
    const safe = (p) => p.catch(() => null);
    const jobs = [
      safe(api.getRules()), safe(api.getBotItems()), safe(api.getPresets()),
      safe(api.getBadge()), safe(api.getKillfeed()), safe(api.getMode()), safe(api.getDifficulty()), safe(api.getIconOverlay()), safe(api.getSkinMode()), safe(api.getUpdateSource()),
      withManifest ? safe(api.validateManifest()) : Promise.resolve(undefined),
    ];
    const [rules, items, presets, badge, killfeed, mode, difficulty, iconOverlay, skinMode, updateSrc, manifest] = await Promise.all(jobs);
    setMeta((prev) => ({
      ...prev,
      ...(manifest !== undefined ? { manifest: manifest && !manifest.error ? manifest : null } : {}),
      rules: rules && !rules.error ? rules : {},
      items: items && !items.error ? items : {},
      presets: presets && !presets.error ? presets : {},
      badge: badge && !badge.error ? badge : null,
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
    try { setState(await api.state()); } catch { toast("状态读取失败", "err"); }
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

  // 启动后静默检查一次更新（只在配置了更新源时）
  useEffect(() => {
    const src = meta.updateSource?.source;
    if (!src || updateChecked.current) return;
    updateChecked.current = true;
    api.checkAppUpdate().then((r) => {
      if (r && r.hasUpdate) {
        setUpdateInfo(r);
        toast(`发现新版本 ${r.latest}，去设置页更新`);
      }
    }).catch(() => {});
  }, [meta.updateSource?.source, toast]);

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

  if (splash) return <><VideoBg /><Splash onDone={() => setSplash(false)} /></>;
  if (!state) return <><VideoBg /><div className="loading">加载中…</div></>;

  const pageProps = { state, meta, refresh, softRefresh, refreshState, patchMeta, toast, bg, setBg, go, updateInfo, setUpdateInfo, openNotice: () => setNotice(true) };
  const PAGES = {
    home: <Home {...pageProps} />,
    plugins: <Plugins {...pageProps} />,
    bots: <Bots {...pageProps} />,
    rules: <Rules {...pageProps} />,
    look: <Look {...pageProps} />,
    commands: <Commands {...pageProps} />,
    guide: <Guide {...pageProps} />,
    settings: <Settings {...pageProps} />,
  };

  return (
    <>
      <VideoBg />
      <div className="app">
        <header className="titlebar" data-tauri-drag-region>
          <div className="titlebar__brand" data-tauri-drag-region>
            <span>雪绒人机管理器</span>
            <span className="ver-chip">{VER}</span>
          </div>
          <div className="titlebar__win">
            <button onClick={() => api.minimize()}>{I.min(14)}</button>
            <button className="is-close" onClick={() => api.close()}>{I.close(14)}</button>
          </div>
        </header>

        <div className="shell">
          <Sidebar view={view} onGo={go} state={state} manifest={meta.manifest} guideSeen={guideSeen} hasUpdate={!!updateInfo?.hasUpdate} />
          <main className="content" ref={contentRef}>
            <div className="pager" key={view} style={{ "--dir": dir }}>
              {PAGES[view]}
            </div>
          </main>
        </div>
      </div>
      <Pet />
      <MusicPlayer />
      {notice && <Announcement onClose={closeNotice} />}
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
