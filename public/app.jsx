// 雪绒人机管理器 — 主应用
// React 18 + Babel Standalone，零构建

const { useState, useEffect, useRef, useCallback, createContext, useContext } = React;

// ═══════════════════════════════════════════
// Toast 通知系统
// ═══════════════════════════════════════════
const ToastCtx = createContext(null);

function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const idRef = useRef(0);

  const dismiss = useCallback((id) => {
    setToasts(prev => prev.map(t => t.id === id ? { ...t, out: true } : t));
    setTimeout(() => setToasts(prev => prev.filter(t => t.id !== id)), 220);
  }, []);

  const toast = useCallback((message, tone = "pink") => {
    const id = ++idRef.current;
    setToasts(prev => [...prev, { id, message, tone }]);
    setTimeout(() => dismiss(id), 2600);
  }, [dismiss]);

  return (
    <ToastCtx.Provider value={{ toast }}>
      {children}
      <div className="toast-stack">
        {toasts.map(t => (
          <div key={t.id} className={`toast glass glass-strong ${t.out ? "toast--out" : ""} toast--${t.tone}`}>
            <span>{t.tone === "green" ? "✓" : t.tone === "red" ? "✕" : "◆"}</span>
            <span>{t.message}</span>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}
const useToast = () => useContext(ToastCtx);

// ═══════════════════════════════════════════
// API 封装
// ═══════════════════════════════════════════
const api = {
  async get(url) {
    const r = await fetch(url);
    return r.json();
  },
  async post(url, body) {
    const r = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body || {}),
    });
    return r.json();
  },
  state: () => api.get("/api/state"),
  detectPath: () => api.post("/api/detect-path"),
  setPath: (p) => api.post("/api/set-path", { path: p }),
  togglePlugin: (id, enabled) => api.post("/api/toggle-plugin", { pluginId: id, enabled }),
  install: (cs2Path) => api.post("/api/install", { cs2Path }),
  uninstall: () => api.post("/api/uninstall"),
  updates: () => api.get("/api/updates"),
  difficulty: (d) => api.post("/api/difficulty", { difficulty: d }),
  premier: (cfg) => api.post("/api/premier", cfg),
  killfeed: (style) => api.post("/api/killfeed", { style }),
  onboarded: () => api.post("/api/onboarded"),
};

// ═══════════════════════════════════════════
// 图标组件（SVG）
// ═══════════════════════════════════════════
const I = {
  chevron: (s = 20) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9"/></svg>,
  back: (s = 20) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><polyline points="15 18 9 12 15 6"/></svg>,
  check: (s = 16) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"/></svg>,
  refresh: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>,
  download: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>,
  folder: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>,
  gear: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>,
  bolt: (s = 20) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M13 2L3 14h7l-1 8 10-12h-7l1-8z"/></svg>,
  skull: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="9" cy="12" r="1"/><circle cx="15" cy="12" r="1"/><path d="M8 20v2h8v-2"/><path d="M12.5 2L12 4"/><path d="M16 4l-2 2"/><path d="M9 2l2 2"/><path d="M12 16v4"/><path d="M12 6a6 6 0 0 0-6 6c0 2 .5 3.5 1.5 4.5C8 18 10 18 12 18s4 0 4.5-1.5C17.5 15.5 18 14 18 12a6 6 0 0 0-6-6z"/></svg>,
  target: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="10"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="2"/></svg>,
  book: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>,
  trash: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>,
  github: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="currentColor"><path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"/></svg>,
  rocket: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z"/><path d="M12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z"/><path d="M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0"/><path d="M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5"/></svg>,
  plug: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 22v-5"/><path d="M9 8V2"/><path d="M15 8V2"/><path d="M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8z"/></svg>,
  close: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>,
  help: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>,
  shield: (s = 18) => <svg width={s} height={s} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>,
};

// ═══════════════════════════════════════════
// 开关组件
// ═══════════════════════════════════════════
function Toggle({ on, onChange, disabled, tone = "pink" }) {
  return (
    <button
      className={`toggle ${on ? "is-on" : ""} toggle--${tone}`}
      disabled={disabled}
      onClick={() => !disabled && onChange(!on)}
      aria-pressed={on}
    >
      <span className="toggle__track"></span>
      <span className="toggle__knob"></span>
    </button>
  );
}

// ═══════════════════════════════════════════
// Segmented 选择器
// ═══════════════════════════════════════════
function Segmented({ items, value, onChange }) {
  return (
    <div className="seg">
      {items.map(it => (
        <button
          key={it.value}
          className={`seg__item ${value === it.value ? "is-active" : ""}`}
          onClick={() => onChange(it.value)}
        >
          {it.label}
        </button>
      ))}
    </div>
  );
}

// ═══════════════════════════════════════════
// 可折叠卡片（平滑展开/收起）
// ═══════════════════════════════════════════
function Collapsible({ title, subtitle, defaultOpen = false, right, children, badge }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className={`collapsible glass ${open ? "is-open" : ""}`}>
      <button className="collapsible__head" onClick={() => setOpen(!open)}>
        <span className="card__title">
          {title}
          {badge}
        </span>
        <span className="collapsible__right">
          {right}
          <span className="collapsible__chev">{I.chevron()}</span>
        </span>
      </button>
      <div className="collapsible__body-wrap">
        <div className="collapsible__body-inner">
          <div className="collapsible__body">
            {subtitle && <div className="card__subtitle">{subtitle}</div>}
            {children}
          </div>
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 开屏动画
// ═══════════════════════════════════════════
function Splash({ onDone }) {
  useEffect(() => {
    const t = setTimeout(onDone, 3200);
    return () => clearTimeout(t);
  }, [onDone]);
  return (
    <div className="splash">
      <div className="splash__inner">
        <div className="splash__logo">
          <img src="assets/splash.png" alt="雪绒" />
        </div>
        <div className="splash__title">雪绒人机管理器</div>
        <div className="splash__sub">CS2 Bot Plugin Manager</div>
        <div className="splash__dots">
          <span className="splash__dot"></span>
          <span className="splash__dot"></span>
          <span className="splash__dot"></span>
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 标题栏
// ═══════════════════════════════════════════
function TitleBar({ onHelp, view, onBack }) {
  return (
    <div className="titlebar glass" style={{ borderRadius: 0, borderTop: "none", borderLeft: "none", borderRight: "none" }}>
      <div className="titlebar__left">
        <div className="titlebar__logo">雪</div>
        <span className="titlebar__title">雪绒人机管理器</span>
        <span className="titlebar__version">v1.0.0</span>
      </div>
      <div className="titlebar__right">
        {view !== "main" && (
          <button className="tl-btn" onClick={onBack} title="返回主页">{I.back()}</button>
        )}
        <button className="tl-btn" onClick={onHelp} title="新手引导">{I.help()}</button>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 主页
// ═══════════════════════════════════════════
function Home({ state, onNav, onTogglePlugin, onInstall, onSetPath, onDetectPath, onSetDifficulty }) {
  const { toast } = useToast();
  const cs2Path = state.cs2Path || state.autoDetectedPath || "";
  const pathSet = !!state.cs2Path;
  const installedCount = Object.values(state.installedMap || {}).filter(Boolean).length;
  const enabledCount = Object.entries(state.plugins || {}).filter(([id, p]) => p.enabled).length;
  const totalPlugins = (state.pluginCatalog || []).filter(p => !p.alwaysOn).length;

  return (
    <div className="shell">
      {/* 状态栏 */}
      <div className="statusbar glass">
        <div className="statusbar__cell">
          <span className={`dot ${pathSet ? "dot--green" : "dot--red"} dot--pulse`}></span>
          <span>{pathSet ? "CS2 路径已设置" : "未设置 CS2 路径"}</span>
        </div>
        <div className="statusbar__cell">
          <span className={`dot ${installedCount > 0 ? "dot--pink" : "dot--gray"}`}></span>
          <span>已安装 {installedCount} / 14 项</span>
        </div>
        <div className="statusbar__cell">
          <span className="dot dot--green"></span>
          <span>{enabledCount} 个插件运行中</span>
        </div>
        <div className="statusbar__cell">
          <span className="dot dot--gray"></span>
          <span>难度: {state.difficulty || "Medium"}</span>
        </div>
      </div>

      {/* 安装卡片 */}
      <div className="card glass">
        <div className="card__head">
          <span className="card__title">{I.download(20)} <span>安装与路径</span></span>
          {installedCount > 0 && <span className="plugin-row__badge plugin-row__badge--core">已安装</span>}
        </div>
        <div className="card__body">
          <div className="path-input">
            <input
              className="path-input__field"
              placeholder="CS2 安装路径（如 C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive）"
              value={cs2Path}
              onChange={(e) => {}}
              readOnly
            />
            <button className="btn btn-secondary" onClick={async () => {
              const r = await api.detectPath();
              if (r.path) {
                const res = await api.setPath(r.path);
                if (res.success) { onSetPath(r.path); toast("检测到 CS2 路径", "green"); }
                else toast(res.error || "未检测到", "red");
              } else toast("未检测到 CS2 安装路径", "red");
            }}>{I.refresh()} 自动检测</button>
          </div>
          <div style={{ display: "flex", gap: 10 }}>
            <button
              className="btn btn-primary"
              onClick={async () => {
                if (!cs2Path) { toast("请先设置或检测 CS2 路径", "red"); return; }
                if (!state.cs2Path && state.autoDetectedPath) {
                  const r = await api.setPath(state.autoDetectedPath);
                  if (!r.success) { toast(r.error, "red"); return; }
                }
                toast("正在安装插件包…");
                const r = await api.install(cs2Path);
                if (r.success) { toast(`安装成功，${r.installedCount} 项已就绪`, "green"); onInstall(); }
                else toast(r.error || "安装失败", "red");
              }}
            >{I.download()} 一键安装插件包</button>
            <button className="btn btn-ghost" onClick={onNav.bind(null, "plugins")}>{I.plug()} 管理插件</button>
          </div>
          {!pathSet && (
            <div className="card__subtitle">
              ⚠ 尚未设置 CS2 安装路径。点击「自动检测」或手动输入路径后即可安装。
            </div>
          )}
        </div>
      </div>

      {/* 难度选择 */}
      <div className="card glass">
        <div className="card__head">
          <span className="card__title">{I.target(20)} <span>难度等级</span></span>
        </div>
        <div className="card__body">
          <div className="card__subtitle">选择 bot 的整体难度，影响瞄准、反应与战术</div>
          <Segmented
            items={[
              { value: "Low", label: "简单" },
              { value: "Medium", label: "中等" },
              { value: "High", label: "困难" },
            ]}
            value={state.difficulty || "Medium"}
            onChange={async (d) => { await api.difficulty(d); onSetDifficulty(d); toast(`难度已切换为 ${d}`, "pink"); }}
          />
        </div>
      </div>

      {/* 导航入口 */}
      <div className="section-label">功能模块</div>
      <button className="nav-block glass" onClick={onNav.bind(null, "plugins")}>
        <span className="nav-block__icon">{I.plug(20)}</span>
        <span className="nav-block__text">
          <span className="nav-block__title">插件管理</span>
          <span className="nav-block__sub">开关 14 个人机插件，配置行为参数</span>
        </span>
        <span className="nav-block__chev">{I.chevron()}</span>
      </button>

      <button className="nav-block glass" onClick={onNav.bind(null, "premier")}>
        <span className="nav-block__icon">{I.bolt(20)}</span>
        <span className="nav-block__text">
          <span className="nav-block__title">XP 勋章 / 段位展示</span>
          <span className="nav-block__sub">PremierShowcase2 — 段位徽章、XP、闪电图标</span>
        </span>
        <span className="nav-block__chev">{I.chevron()}</span>
      </button>

      <button className="nav-block glass" onClick={onNav.bind(null, "killfeed")}>
        <span className="nav-block__icon">{I.skull(20)}</span>
        <span className="nav-block__text">
          <span className="nav-block__title">击杀图标皮肤</span>
          <span className="nav-block__sub">Killfeed Icons — 22 套击杀提示栏图标皮肤</span>
        </span>
        <span className="nav-block__chev">{I.chevron()}</span>
      </button>

      <button className="nav-block glass" onClick={onNav.bind(null, "updates")}>
        <span className="nav-block__icon">{I.github(20)}</span>
        <span className="nav-block__text">
          <span className="nav-block__title">更新检查</span>
          <span className="nav-block__sub">从 GitHub 检查插件最新版本</span>
        </span>
        <span className="nav-block__chev">{I.chevron()}</span>
      </button>

      <button className="nav-block glass" onClick={onNav.bind(null, "settings")}>
        <span className="nav-block__icon">{I.gear(20)}</span>
        <span className="nav-block__text">
          <span className="nav-block__title">设置</span>
          <span className="nav-block__sub">路径、卸载、关于</span>
        </span>
        <span className="nav-block__chev">{I.chevron()}</span>
      </button>
    </div>
  );
}

// ═══════════════════════════════════════════
// 插件管理页
// ═══════════════════════════════════════════
function PluginsPage({ state, onToggle, onBack }) {
  const { toast } = useToast();
  const [filter, setFilter] = useState("all");
  const catalog = state.pluginCatalog || [];

  const categories = [
    { value: "all", label: "全部" },
    { value: "framework", label: "框架" },
    { value: "native", label: "原生插件" },
    { value: "managed", label: "托管插件" },
    { value: "feature", label: "功能模块" },
  ];

  const filtered = filter === "all" ? catalog : catalog.filter(p => p.category === filter);

  return (
    <div className="subpage">
      <div className="subpage__head">
        <button className="subpage__back" onClick={onBack}>{I.back()}</button>
        <span className="subpage__title">插件管理</span>
      </div>

      <Segmented items={categories} value={filter} onChange={setFilter} />

      {filtered.map(p => {
        const st = state.plugins?.[p.id] || { enabled: false, installed: false };
        const installed = state.installedMap?.[p.id] ?? st.installed;
        return (
          <div key={p.id} className="plugin-row glass">
            <div className={`plugin-row__icon ${
              p.alwaysOn ? "plugin-row__icon--core" :
              installed ? "plugin-row__icon--installed" : "plugin-row__icon--off"
            }`}>
              {p.category === "framework" ? I.shield() :
               p.category === "native" ? I.gear() :
               p.tier === "combat" ? I.target() :
               p.tier === "special" ? I.bolt() : I.plug()}
            </div>
            <div className="plugin-row__text">
              <span className="plugin-row__name">
                {p.name}
                <span className={`plugin-row__badge plugin-row__badge--${p.tier}`}>{p.tier === "core" ? "核心" : p.tier === "combat" ? "战斗" : p.tier === "feature" ? "功能" : p.tier === "control" ? "控制" : p.tier === "special" ? "特殊" : ""}</span>
              </span>
              <span className="plugin-row__desc">{p.desc}</span>
            </div>
            <div className="plugin-row__status">
              {p.alwaysOn ? <span style={{ color: "var(--c-pink)" }}>常驻</span> :
               installed ? (st.enabled ? <span style={{ color: "var(--c-green)" }}>运行中</span> : <span>已关闭</span>) :
               <span>未安装</span>}
            </div>
            <Toggle
              on={p.alwaysOn || st.enabled}
              disabled={p.alwaysOn || !installed}
              tone={p.alwaysOn ? "pink" : "green"}
              onChange={async (en) => {
                const r = await api.togglePlugin(p.id, en);
                if (r.success) { onToggle(p.id, en); toast(`${p.name} ${en ? "已开启" : "已关闭"}`, en ? "green" : "pink"); }
                else toast(r.error, "red");
              }}
            />
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════
// XP 勋章模块页
// ═══════════════════════════════════════════
function PremierPage({ state, onPremier, onBack }) {
  const { toast } = useToast();
  const cfg = state.premierConfig || { rating: 15800, xpBoost: 1.5, lightningIcon: true, badgeStyle: "gold" };
  const pluginOn = state.plugins?.PremierShowcase2?.enabled;

  return (
    <div className="subpage">
      <div className="subpage__head">
        <button className="subpage__back" onClick={onBack}>{I.back()}</button>
        <span className="subpage__title">XP 勋章 / 段位展示</span>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">{I.bolt(20)} PremierShowcase2 插件</span>
          <Toggle
            on={pluginOn}
            disabled={!state.installedMap?.PremierShowcase2}
            tone="pink"
            onChange={async (en) => {
              const r = await api.togglePlugin("PremierShowcase2", en);
              if (r.success) { toast(`PremierShowcase2 ${en ? "已开启" : "已关闭"}`, en ? "green" : "pink"); }
              else toast(r.error, "red");
            }}
          />
        </div>
        <div className="card__subtitle">
          伪造 Premier 段位徽章、XP 经验值与 Rating。开启后 bot 对战时会显示自定义段位信息，闪电图标即 XP 表现。
        </div>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">勋章预览</span>
        </div>
        <div className="xp-preview">
          <div className={`xp-badge ${cfg.lightningIcon ? "xp-badge--lightning" : ""}`}>
            {!cfg.lightningIcon && "XP"}
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <span style={{ fontSize: 18, fontWeight: 700, color: "var(--text-primary)" }}>Rating {cfg.rating?.toLocaleString()}</span>
            <span style={{ fontSize: 13, color: "var(--text-tertiary)" }}>XP 倍率 ×{cfg.xpBoost}</span>
            <span style={{ fontSize: 13, color: "var(--c-pink)" }}>{cfg.badgeStyle === "gold" ? "金色徽章" : cfg.badgeStyle === "silver" ? "银色徽章" : "青铜徽章"}</span>
          </div>
        </div>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">Rating 设置</span>
        </div>
        <div className="settings-row">
          <div className="settings-row__text">
            <span className="settings-row__label">Premier Rating</span>
            <span className="settings-row__hint">伪造的段位分数（0–30000）</span>
          </div>
          <div className="num-stepper">
            <button className="num-stepper__btn" onClick={async () => {
              const v = Math.max(0, cfg.rating - 100);
              await api.premier({ rating: v });
              onPremier({ rating: v });
            }}>−</button>
            <span className="num-stepper__val">{cfg.rating?.toLocaleString()}</span>
            <button className="num-stepper__btn" onClick={async () => {
              const v = Math.min(30000, cfg.rating + 100);
              await api.premier({ rating: v });
              onPremier({ rating: v });
            }}>+</button>
          </div>
        </div>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">XP 倍率</span>
        </div>
        <div className="settings-row">
          <div className="settings-row__text">
            <span className="settings-row__label">经验值倍率</span>
            <span className="settings-row__hint">每场获得 XP 的倍率（0.5–5.0）</span>
          </div>
          <Segmented
            items={[
              { value: 1, label: "×1" },
              { value: 1.5, label: "×1.5" },
              { value: 2, label: "×2" },
              { value: 3, label: "×3" },
              { value: 5, label: "×5" },
            ]}
            value={cfg.xpBoost}
            onChange={async (v) => { await api.premier({ xpBoost: v }); onPremier({ xpBoost: v }); toast(`XP 倍率设为 ×${v}`, "pink"); }}
          />
        </div>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">闪电图标</span>
        </div>
        <div className="settings-row">
          <div className="settings-row__text">
            <span className="settings-row__label">显示 XP 闪电图标</span>
            <span className="settings-row__hint">在段位徽章上叠加闪电特效</span>
          </div>
          <Toggle
            on={cfg.lightningIcon}
            tone="pink"
            onChange={async (en) => { await api.premier({ lightningIcon: en }); onPremier({ lightningIcon: en }); }}
          />
        </div>
        <div className="settings-row">
          <div className="settings-row__text">
            <span className="settings-row__label">徽章样式</span>
            <span className="settings-row__hint">金色 / 银色 / 青铜</span>
          </div>
          <Segmented
            items={[
              { value: "gold", label: "金色" },
              { value: "silver", label: "银色" },
              { value: "bronze", label: "青铜" },
            ]}
            value={cfg.badgeStyle}
            onChange={async (v) => { await api.premier({ badgeStyle: v }); onPremier({ badgeStyle: v }); }}
          />
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 击杀图标皮肤页
// ═══════════════════════════════════════════
const KILLFEED_STYLES = [
  { id: "txz01", name: "默认" }, { id: "txz02", name: "极简" },
  { id: "txz03", name: "霓虹粉" }, { id: "txz04", name: "像素" },
  { id: "txz05", name: "军用" }, { id: "txz06", name: "赛博" },
  { id: "txz07", name: "金色" }, { id: "txz08", name: "暗影" },
  { id: "txz09", name: "红色风暴" }, { id: "txz10", name: "冰蓝" },
  { id: "txz11", name: "毒液" }, { id: "txz12", name: "皇家" },
  { id: "txz13", name: "幽灵" }, { id: "txz14", name: "熔岩" },
  { id: "txz15", name: "森林" }, { id: "txz16", name: "沙漠" },
  { id: "txz17", name: "深空" }, { id: "txz18", name: "樱花" },
];

function KillfeedPage({ state, onBack }) {
  const { toast } = useToast();
  const current = state.killfeedStyle || "txz03";
  const pluginOn = state.plugins?.KillfeedIcons?.enabled;

  return (
    <div className="subpage">
      <div className="subpage__head">
        <button className="subpage__back" onClick={onBack}>{I.back()}</button>
        <span className="subpage__title">击杀图标皮肤</span>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">{I.skull(20)} Killfeed Icons</span>
          <Toggle
            on={pluginOn}
            disabled={!state.installedMap?.KillfeedIcons}
            tone="pink"
            onChange={async (en) => {
              const r = await api.togglePlugin("KillfeedIcons", en);
              if (r.success) toast(`击杀图标 ${en ? "已开启" : "已关闭"}`, en ? "green" : "pink");
              else toast(r.error, "red");
            }}
          />
        </div>
        <div className="card__subtitle">
          替换击杀提示栏的图标皮肤包。选择一套主题，击杀时显示对应风格的图标。
        </div>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">选择皮肤</span>
          <span className="card__subtitle" style={{ fontSize: 13 }}>当前: {KILLFEED_STYLES.find(s => s.id === current)?.name || current}</span>
        </div>
        <div className="killfeed-grid">
          {KILLFEED_STYLES.map(s => (
            <button
              key={s.id}
              className={`killfeed-cell ${current === s.id ? "is-active" : ""}`}
              onClick={async () => {
                const r = await api.killfeed(s.id);
                if (r.success) toast(`击杀图标已切换为 ${s.name}`, "pink");
              }}
            >
              <span style={{ fontSize: 20 }}>{I.skull(22)}</span>
              <span>{s.name}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 更新检查页
// ═══════════════════════════════════════════
function UpdatesPage({ onBack }) {
  const { toast } = useToast();
  const [updates, setUpdates] = useState(null);
  const [loading, setLoading] = useState(true);

  const load = async () => {
    setLoading(true);
    try {
      const r = await api.updates();
      setUpdates(r.updates);
    } catch (e) {
      toast("更新检查失败", "red");
    }
    setLoading(false);
  };

  useEffect(() => { load(); }, []);

  return (
    <div className="subpage">
      <div className="subpage__head">
        <button className="subpage__back" onClick={onBack}>{I.back()}</button>
        <span className="subpage__title">更新检查</span>
        <button className="btn btn-secondary" style={{ marginLeft: "auto" }} onClick={load} disabled={loading}>
          {I.refresh()} {loading ? "检查中…" : "重新检查"}
        </button>
      </div>

      <div className="card glass">
        <div className="card__head">
          <span className="card__title">{I.github(20)} GitHub 源</span>
        </div>
        <div className="card__subtitle">
          从 GitHub 检查插件框架的最新发布版本。如有更新，可点击前往下载。
        </div>
      </div>

      {loading && <div className="card glass"><div className="empty">正在从 GitHub 获取最新版本信息…</div></div>}

      {updates && updates.map(u => (
        <div key={u.name} className="card glass">
          <div className="card__head">
            <span className="card__title">{u.name}</span>
            {u.available ? (
              <span className="plugin-row__badge plugin-row__badge--special">{u.latestVersion}</span>
            ) : (
              <span className="plugin-row__badge" style={{ background: "rgba(142,142,147,0.12)", color: "var(--c-mid-gray)" }}>无法获取</span>
            )}
          </div>
          <div className="card__body">
            {u.available ? (
              <>
                <div className="card__subtitle">
                  发布时间: {new Date(u.publishedAt).toLocaleDateString("zh-CN")}
                </div>
                {u.notes && <div className="card__subtitle selectable" style={{ maxHeight: 80, overflow: "auto" }}>{u.notes}</div>}
                <button className="btn btn-primary" onClick={() => window.open(u.releaseUrl, "_blank")}>
                  {I.github()} 前往下载页
                </button>
              </>
            ) : (
              <div className="card__subtitle">无法连接 GitHub（{u.error}）。可能是网络问题或仓库为私有。</div>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}

// ═══════════════════════════════════════════
// 设置页
// ═══════════════════════════════════════════
function SettingsPage({ state, onBack }) {
  const { toast } = useToast();
  return (
    <div className="subpage">
      <div className="subpage__head">
        <button className="subpage__back" onClick={onBack}>{I.back()}</button>
        <span className="subpage__title">设置</span>
      </div>

      <div className="card glass">
        <div className="card__head"><span className="card__title">{I.folder(20)} CS2 安装路径</span></div>
        <div className="path-input">
          <input className="path-input__field" value={state.cs2Path || state.autoDetectedPath || ""} readOnly />
          <button className="btn btn-secondary" onClick={async () => {
            const r = await api.detectPath();
            if (r.path) { await api.setPath(r.path); toast("路径已更新", "green"); }
            else toast("未检测到 CS2", "red");
          }}>{I.refresh()} 重新检测</button>
        </div>
      </div>

      <div className="card glass">
        <div className="card__head"><span className="card__title">{I.trash(20)} 卸载</span></div>
        <div className="card__subtitle">
          卸载所有人机插件。原有 addons 目录会被备份到 game/xuerong_uninstall_* 目录。
        </div>
        <button className="btn btn-secondary" style={{ color: "var(--c-red)" }} onClick={async () => {
          if (!confirm("确认卸载所有人机插件？原有文件会备份。")) return;
          toast("正在卸载…");
          const r = await api.uninstall();
          if (r.success) toast("已卸载，备份已创建", "green");
          else toast(r.error, "red");
        }}>{I.trash()} 卸载插件</button>
      </div>

      <div className="card glass">
        <div className="card__head"><span className="card__title">{I.shield(20)} 关于</span></div>
        <div className="card__body">
          <div className="settings-row">
            <span className="settings-row__label">版本</span>
            <span className="settings-row__hint">{state.version}</span>
          </div>
          <div className="settings-row">
            <span className="settings-row__label">安装时间</span>
            <span className="settings-row__hint">{state.installDate ? new Date(state.installDate).toLocaleString("zh-CN") : "未安装"}</span>
          </div>
          <div className="settings-row">
            <span className="settings-row__label">设计</span>
            <span className="settings-row__hint">粉色磨砂玻璃 · FOLIA 卡片式</span>
          </div>
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 新手引导
// ═══════════════════════════════════════════
const ONBOARD_STEPS = [
  { selector: ".titlebar", title: "欢迎使用雪绒人机管理器", body: "这里是 CS2 人机插件管理器。粉色磨砂玻璃界面，所有功能一目了然。让我们快速了解每个功能。" },
  { selector: ".statusbar", title: "状态栏", body: "顶部状态栏实时显示 CS2 路径状态、已安装插件数、运行中插件数和当前难度。绿色脉冲点表示一切正常。" },
  { selector: ".card", title: "安装与路径", body: "第一步：点击「自动检测」找到 CS2 安装目录，然后点「一键安装插件包」将所有人机插件部署到游戏中。" },
  { selector: ".seg", title: "难度等级", body: "选择 bot 的整体难度。简单适合休闲，困难适合练枪。随时可以切换。" },
  { selector: ".nav-block", title: "功能模块", body: "下方是各功能入口：插件管理（开关单个插件）、XP 勋章（段位伪造）、击杀图标（皮肤切换）、更新检查、设置。" },
];

function Onboard({ onClose }) {
  const [step, setStep] = useState(0);
  const [rect, setRect] = useState(null);
  const popRef = useRef(null);

  useEffect(() => {
    const target = document.querySelector(ONBOARD_STEPS[step].selector);
    if (target) {
      target.scrollIntoView({ behavior: "smooth", block: "center" });
      setTimeout(() => {
        const r = target.getBoundingClientRect();
        setRect({ top: r.top, left: r.left, width: r.width, height: r.height });
      }, 400);
    }
  }, [step]);

  const isLast = step === ONBOARD_STEPS.length - 1;
  const highlightStyle = rect ? {
    top: rect.top - 4, left: rect.left - 4, width: rect.width + 8, height: rect.height + 8,
  } : { display: "none" };

  const popStyle = rect ? (() => {
    const popW = 320, popH = 180;
    let top = rect.bottom + 12;
    let left = rect.left;
    if (top + popH > window.innerHeight - 20) top = rect.top - popH - 12;
    if (left + popW > window.innerWidth - 20) left = window.innerWidth - popW - 20;
    if (left < 20) left = 20;
    return { top, left };
  })() : { display: "none" };

  return (
    <div className="onboard-scrim">
      <div className="onboard-highlight" style={highlightStyle}></div>
      <div className="onboard-pop glass glass-strong" ref={popRef} style={popStyle}>
        <div className="onboard-pop__title">{ONBOARD_STEPS[step].title}</div>
        <div className="onboard-pop__body">{ONBOARD_STEPS[step].body}</div>
        <div className="onboard-pop__actions">
          <span className="onboard-pop__step">{step + 1} / {ONBOARD_STEPS.length}</span>
          <div className="onboard-pop__btns">
            <button className="btn btn-ghost" onClick={onClose}>跳过</button>
            {!isLast ? (
              <button className="btn btn-primary" onClick={() => setStep(step + 1)}>下一步</button>
            ) : (
              <button className="btn btn-primary" onClick={onClose}>完成</button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════
// 主应用
// ═══════════════════════════════════════════
function App() {
  const [showSplash, setShowSplash] = useState(true);
  const [state, setState] = useState(null);
  const [view, setView] = useState("main");
  const [showOnboard, setShowOnboard] = useState(false);
  const { toast } = useToast();

  useEffect(() => {
    api.state().then(s => {
      setState(s);
      if (!s.onboarded) {
        setTimeout(() => setShowOnboard(true), 600);
      }
    }).catch(e => toast("加载状态失败: " + e.message, "red"));
  }, []);

  const refresh = useCallback(async () => {
    const s = await api.state();
    setState(s);
  }, []);

  if (showSplash) return <Splash onDone={() => setShowSplash(false)} />;
  if (!state) return <div className="app"><div className="empty" style={{ paddingTop: 100 }}>加载中…</div></div>;

  const updateState = (patch) => setState(prev => ({ ...prev, ...patch }));

  return (
    <div className="app">
      <TitleBar
        view={view}
        onBack={() => setView("main")}
        onHelp={() => setShowOnboard(true)}
      />
      {view === "main" && (
        <Home
          state={state}
          onNav={setView}
          onTogglePlugin={(id, en) => setState(prev => ({ ...prev, plugins: { ...prev.plugins, [id]: { ...prev.plugins[id], enabled: en } } }))}
          onInstall={refresh}
          onSetPath={(p) => updateState({ cs2Path: p })}
          onDetectPath={() => refresh()}
          onSetDifficulty={(d) => updateState({ difficulty: d })}
        />
      )}
      {view === "plugins" && <PluginsPage state={state} onToggle={(id, en) => setState(prev => ({ ...prev, plugins: { ...prev.plugins, [id]: { ...prev.plugins[id], enabled: en } } }))} onBack={() => setView("main")} />}
      {view === "premier" && <PremierPage state={state} onPremier={(cfg) => updateState({ premierConfig: { ...state.premierConfig, ...cfg } })} onBack={() => setView("main")} />}
      {view === "killfeed" && <KillfeedPage state={state} onBack={() => setView("main")} />}
      {view === "updates" && <UpdatesPage onBack={() => setView("main")} />}
      {view === "settings" && <SettingsPage state={state} onBack={() => setView("main")} />}
      {showOnboard && (
        <Onboard onClose={async () => {
          setShowOnboard(false);
          await api.onboarded();
          setState(prev => ({ ...prev, onboarded: true }));
        }} />
      )}
    </div>
  );
}

// 渲染
ReactDOM.createRoot(document.getElementById("root")).render(
  <ToastProvider><App /></ToastProvider>
);
