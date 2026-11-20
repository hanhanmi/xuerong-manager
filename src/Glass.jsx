import React, { useEffect } from "react";

// The glass surface is rendered with CSS. Keeping SVG displacement filters out
// avoids shipping an unused CPU rasterization path in Chromium/WebView2.

/*
 * 悬停高光。
 * 旧实现用全局 pointermove + elementFromPoint 每帧算位置：elementFromPoint 会强制
 * 同步布局，鼠标一划就卡，点按钮也发钝。现在改成 pointerover/out 事件委托，
 * 只切一个 class，零 layout、零 rAF；位置相关的高光改回静态 CSS。
 */
export function useGlassPointer() {
  useEffect(() => {
    const pick = (node) => (node instanceof Element ? node.closest(".card, .navpill") : null);
    const positions = new WeakMap();
    const updateLight = (el, e) => {
      let rect = positions.get(el);
      if (!rect) {
        rect = el.getBoundingClientRect();
        positions.set(el, rect);
      }
      el.style.setProperty("--gx", `${e.clientX - rect.left}px`);
      el.style.setProperty("--gy", `${e.clientY - rect.top}px`);
    };
    const onOver = (e) => {
      if (document.documentElement.dataset.glass === "off") return;
      const el = pick(e.target);
      if (!el) return;
      // 进入时只量一次；移动时复用边界，只更新高光位置变量。
      positions.set(el, el.getBoundingClientRect());
      updateLight(el, e);
      el.classList.add("is-lit");
    };
    const onMove = (e) => {
      if (document.documentElement.dataset.glass === "off") return;
      const el = pick(e.target);
      if (el?.classList.contains("is-lit")) updateLight(el, e);
    };
    const onOut = (e) => {
      const el = pick(e.target);
      if (!el) return;
      const to = e.relatedTarget;
      if (to instanceof Node && el.contains(to)) return;
      el.classList.remove("is-lit");
    };
    document.addEventListener("pointerover", onOver, { passive: true });
    document.addEventListener("pointermove", onMove, { passive: true });
    document.addEventListener("pointerout", onOut, { passive: true });
    return () => {
      document.removeEventListener("pointerover", onOver);
      document.removeEventListener("pointermove", onMove);
      document.removeEventListener("pointerout", onOut);
    };
  }, []);
}

/*
 * 滚动期间把玻璃降级成实色。
 * backdrop-filter 对滚动容器里的每个元素每帧都要重新采样背景，
 * 卡片一多就必然掉帧。滚动时临时撤掉，停手 140ms 再恢复 —— 滚动中肉眼看不出。
 */
export function useScrollDegrade(ref, active) {
  useEffect(() => {
    if (!active) return;
    const el = ref.current;
    if (!el) return;
    const root = document.documentElement;
    let timer = 0;
    const onScroll = () => {
      if (!root.classList.contains("is-scrolling")) root.classList.add("is-scrolling");
      clearTimeout(timer);
      timer = setTimeout(() => root.classList.remove("is-scrolling"), 140);
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      el.removeEventListener("scroll", onScroll);
      clearTimeout(timer);
      root.classList.remove("is-scrolling");
    };
  }, [ref, active]);
}
