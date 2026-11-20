/*
 * 把 App.jsx 里所有用户可见中文文案包成 t("原文")。
 * 用 Babel AST 拿精确区间后做文本替换（不用 generator），
 * 这样原文件排版、注释、格式一点不变，也不会误伤：
 *   正则字面量（chType 的分类）、import 路径、CSS 类名、逻辑比较。
 *
 * 运行：node tools/i18n_scan.mjs
 * 产物：src/App.jsx（就地改写 + .bak_i18n 备份）、tools/i18n_keys.json
 */
import fs from "node:fs";
import { parse } from "@babel/parser";

const SRC = "src/App.jsx";
const BACKUP = "src/App.jsx.bak_i18n";
const KEYS = "tools/i18n_keys.json";
const HAN = /[\u4e00-\u9fff]/;

let src = fs.readFileSync(SRC, "utf8");
const ast = parse(src, { sourceType: "module", plugins: ["jsx"] });

const q = (s) => JSON.stringify(s);
const escTpl = (s) => s.replace(/\\/g, "\\\\").replace(/`/g, "\\`").replace(/\$\{/g, "\\${");

function walk(node, parent = null, pk = "", visit = null) {
  if (!node || typeof node !== "object") return;
  if (Array.isArray(node)) { for (const x of node) walk(x, parent, pk, visit); return; }
  if (typeof node.type === "string") {
    if (visit) visit(node, parent, pk);
  }
  for (const k of Object.keys(node)) {
    if (["loc", "start", "end", "extra", "leadingComments", "trailingComments", "innerComments", "tokens", "comments"].includes(k)) continue;
    const v = node[k];
    if (v && typeof v === "object") walk(v, node, k, visit);
  }
}

/* ── 1. CHANGELOG 的 items 全文保留中文：chType 靠中文前缀分类 ── */
const skip = [];
walk(ast, null, "", (n) => {
  if (n.type !== "VariableDeclarator") return;
  if (n.id?.type !== "Identifier" || n.id.name !== "CHANGELOG") return;
  const arr = n.init;
  if (!arr || arr.type !== "ArrayExpression") return;
  for (const entry of arr.elements) {
    if (!entry || entry.type !== "ObjectExpression") continue;
    for (const p of entry.properties) {
      if (p.type !== "ObjectProperty") continue;
      const name = p.key?.name ?? p.key?.value;
      if (name !== "items" || p.value?.type !== "ArrayExpression") continue;
      for (const it of p.value.elements) {
        if (it && it.type === "StringLiteral") skip.push([it.start, it.end]);
      }
    }
  }
});
const inSkip = (s) => skip.some(([a, b]) => s >= a && s < b);

/* ── 2. 收集替换区间 ── */
const edits = [];
const keys = new Set();
const parentStats = {};

walk(ast, null, "", (n, parent, pk) => {
  if (n.type === "StringLiteral") {
    if (parent?.type === "ImportDeclaration" && pk === "source") return;
    if (parent?.type === "CallExpression" && parent.callee?.type === "Identifier" && parent.callee.name === "tr") return;
    if (parent?.type === "ObjectProperty" && pk === "key" && !parent.computed) return;
    if (parent?.type === "JSXAttribute" && pk === "name") return;
    if (!HAN.test(n.value)) return;
    if (inSkip(n.start)) return;
    parentStats[parent?.type || "?"] = (parentStats[parent?.type || "?"] || 0) + 1;
    // JSX 属性原来是 title="中文"，换成表达式必须自带花括号
    const asJsxAttr = parent?.type === "JSXAttribute" && pk === "value";
    const expr = `tr(${q(n.value)})`;
    edits.push({ start: n.start, end: n.end, text: asJsxAttr ? `{${expr}}` : expr });
    keys.add(n.value);
    return;
  }

  if (n.type === "TemplateLiteral") {
    const hasHan = n.quasis.some((x) => HAN.test(x.value.cooked ?? x.value.raw));
    if (!hasHan || inSkip(n.start)) return;
    let s = "`";
    n.quasis.forEach((qu, i) => {
      const cooked = qu.value.cooked ?? qu.value.raw;
      if (cooked) {
        if (HAN.test(cooked)) { s += "${tr(" + q(cooked) + ")}"; keys.add(cooked); }
        else s += escTpl(cooked);
      }
      if (i < n.expressions.length) {
        const e = n.expressions[i];
        s += "${" + src.slice(e.start, e.end) + "}";
      }
    });
    s += "`";
    edits.push({ start: n.start, end: n.end, text: s, covers: true });
    return;
  }

  if (n.type === "JSXText") {
    if (!HAN.test(n.value) || inSkip(n.start)) return;
    const raw = n.value;
    const lead = raw.match(/^\s*/)[0];
    const trail = raw.match(/\s*$/)[0];
    const core = raw.slice(lead.length, raw.length - trail.length);
    if (!HAN.test(core)) return;
    edits.push({ start: n.start, end: n.end, text: lead + "{tr(" + q(core) + ")}" + trail });
    keys.add(core);
  }
});

/* 外层模板重建后，丢掉落在它内部的字符串替换 */
const covers = edits.filter((e) => e.covers && !inSkip(e.start));
const picked = edits.filter((e) => !e.covers && !covers.some((c) => e.start >= c.start && e.end <= c.end));
const all = [...picked, ...covers].sort((a, b) => b.start - a.start || b.end - a.end);

let out = src;
let lastStart = Infinity;
let applied = 0;
for (const e of all) {
  if (e.end > lastStart) continue;
  out = out.slice(0, e.start) + e.text + out.slice(e.end);
  lastStart = e.start;
  applied++;
}

/* ── 3. 注入 t 的 import ── */
if (!/import\s*\{\s*t\s+as\s+tr\s*\}\s*from\s*"\.\/i18n\.js"/.test(out)) {
  const anchor = 'import LanguagePrompt from "./LanguagePrompt.jsx";';
  if (!out.includes(anchor)) throw new Error("找不到 i18n import 锚点");
  out = out.replace(anchor, anchor + '\nimport { t as tr } from "./i18n.js";');
}

fs.writeFileSync(BACKUP, src, "utf8");
fs.writeFileSync(SRC, out, "utf8");
fs.writeFileSync(KEYS, JSON.stringify([...keys].sort(), null, 1), "utf8");

console.log("替换区间:", applied, "| 唯一文案:", keys.size, "| CHANGELOG items 跳过:", skip.length);
console.log("StringLiteral 父节点分布:", JSON.stringify(parentStats));
