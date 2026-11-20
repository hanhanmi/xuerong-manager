import React, { useState } from "react";
import { setLang, dismissLangPrompt, currentLang, isLangPromptEnabled, setLangPromptEnabled } from "./i18n.js";
import mood07 from "./assets/moods/mood-07.webp";
import mood01 from "./assets/moods/mood-01.webp";
import mood05 from "./assets/moods/mood-05.webp";

const ART = [mood07, mood01, mood05];

/*
 * 冷启动时的语言询问，可选择永久关闭。
 * 这里刻意自己带三语文案，不经过 t()：它本来就是用来选语言的。
 */
const COPY = {
  ja: {
    eyebrow: "change my mind へようこそ",
    title: "表示言語を選択",
    body: "画面の表示言語を選べます。選択は次回起動後も保存されます。",
    primary: "简体中文にする",
    primarySub: "切换到中文 · Switch to Chinese",
    keep: "日本語にする",
    keepSub: "Switch to Japanese · 切换到日语",
    en: "English にする",
    enSub: "Switch to English · 切换到英语",
    foot: "この設定は「設定」からいつでも変更できます",
    suppress: "次回から言語選択を表示しない",
  },
  en: {
    eyebrow: "Welcome to change my mind",
    title: "Choose a display language",
    body: "Choose a display language. Your choice will be remembered the next time you start the manager.",
    primary: "简体中文 / Switch to Chinese",
    primarySub: "簡体中文に切り替える · 切换到中文",
    keep: "Use Japanese",
    keepSub: "日本語にする · 切换到日语",
    en: "Use English",
    enSub: "英語にする · 切换到英语",
    foot: "Change this any time in Settings",
    suppress: "Don't ask me again at startup",
  },
  zh: {
    eyebrow: "欢迎使用 change my mind",
    title: "选择界面语言",
    body: "选择管理器的界面语言。你的选择会保存到下次启动。",
    primary: "切换到简体中文",
    primarySub: "簡体中文に切り替える · Switch to Chinese",
    keep: "切换到日语",
    keepSub: "日本語にする · Use Japanese",
    en: "切换到英语",
    enSub: "英語にする · Use English",
    foot: "以后可在设置页更改语言",
    suppress: "以后不再询问语言",
  },
};

export default function LanguagePrompt({ onClose }) {
  const c = COPY[currentLang()] || COPY.ja;
  const [art] = useState(() => ART[Math.floor(Math.random() * ART.length)]);
  const [askAgain, setAskAgain] = useState(() => isLangPromptEnabled());
  const dismiss = () => { dismissLangPrompt(); onClose(); };

  return (
    <div className="modal lang-modal" role="dialog" aria-modal="true">
      <div className="modal__card lang-card">
        <div className="modal__glow" />
        <button className="modal__x lang-x" onClick={dismiss} aria-label="Close / 閉じる / 关闭">×</button>

        <div className="lang-art">
          <img src={art} alt="" draggable="false" />
          <span className="lang-art__ring" />
        </div>

        <div className="lang-eyebrow">{c.eyebrow}</div>
        <h2 className="lang-title">{c.title}</h2>
        <p className="lang-body">{c.body}</p>

        <button className="lang-primary" onClick={() => setLang("zh")}>
          <span className="lang-primary__mark">中</span>
          <span className="lang-primary__txt">
            <b>{c.primary}</b>
            <em>{c.primarySub}</em>
          </span>
          <span className="lang-primary__arrow">→</span>
        </button>

        <div className="lang-secondary">
          <button onClick={() => setLang("ja")}>
            <b>{c.keep}</b><em>{c.keepSub}</em>
          </button>
          <button onClick={() => setLang("en")}>
            <b>{c.en}</b><em>{c.enSub}</em>
          </button>
        </div>

        <label className="lang-remember">
          <input
            type="checkbox"
            checked={!askAgain}
            onChange={(event) => {
              const enabled = !event.target.checked;
              setAskAgain(enabled);
              setLangPromptEnabled(enabled);
            }}
          />
          <span>{c.suppress}</span>
        </label>

        <div className="lang-foot">
          <span>{c.foot}</span>
          <span>日本語 · 简体中文 · English</span>
        </div>
      </div>
    </div>
  );
}
