/*
 * 生成 src/i18n-dict.js
 *  - tools/i18n_keys.json        : 编号 -> 中文原文（i18n_scan.mjs 产出）
 *  - tools/i18n_[1-4].tsv        : 编号 | 日本語 | English
 *  - tools/i18n_changelog.tsv    : 日本語 | English（按 CHANGELOG items 顺序）
 * 运行：node tools/i18n_dict.mjs
 */
import fs from "node:fs";

const read = (p) => fs.readFileSync(p, "utf8").replace(/^\uFEFF/, "");
// 译文表里写的 \n 是排版用的字面换行，渲染到 JSX 会变成可见的 "\n"，统一收成空格
const clean = (s) => s.replace(/\\n\s*/g, " ");
const keys = JSON.parse(read("tools/i18n_keys.json"));

const trans = new Map();
for (const f of ["tools/i18n_1.tsv", "tools/i18n_2.tsv", "tools/i18n_3.tsv", "tools/i18n_4.tsv"]) {
  const lines = read(f).split(/\r?\n/);
  lines.forEach((line, i) => {
    if (!line.trim()) return;
    const p = line.split("|");
    if (p.length !== 3) throw new Error(`${f}:${i + 1} 字段数 ${p.length}（应为 3）: ${line.slice(0, 60)}`);
    const id = Number(String(p[0]).replace(/^\uFEFF/, "").trim());
    if (!Number.isInteger(id) || id < 0 || id >= keys.length) throw new Error(`${f}:${i + 1} 非法编号 ${p[0]}`);
    if (trans.has(id)) throw new Error(`编号重复: ${id}`);
    trans.set(id, [clean(p[1]), clean(p[2])]);
  });
}
const missing = keys.map((_, i) => i).filter((i) => !trans.has(i));
if (missing.length) throw new Error(`译文缺失编号: ${missing.join(",")}`);

const DICT = {};
keys.forEach((k, i) => {
  const [ja, en] = trans.get(i);
  DICT[k] = { ja: ja || k, en: en || k };
});

/* CHANGELOG 的 items 在 App.jsx 里保留中文（chType 靠前缀分类），
   显示时去掉前缀再查字典，所以这里补一份去前缀的 key。 */
const app = read("src/App.jsx");
const block = app.match(/const CHANGELOG = \[([\s\S]*?)\n\];/);
if (!block) throw new Error("找不到 CHANGELOG 区块");
const items = [...block[1].matchAll(/^\s*"((?:修复|新增|更新)：[^"]*)",?\s*$/gm)]
  .map((m) => m[1].replace(/^(修复|新增|更新)[：:]\s*/, ""));
const clLines = read("tools/i18n_changelog.tsv").split(/\r?\n/).filter((l) => l.trim());
if (clLines.length !== items.length) {
  throw new Error(`CHANGELOG 译文行数 ${clLines.length} 与条目数 ${items.length} 不一致`);
}
clLines.forEach((line, i) => {
  const p = line.split("|");
  if (p.length !== 2) throw new Error(`i18n_changelog.tsv:${i + 1} 字段数 ${p.length}`);
  DICT[items[i]] = { ja: clean(p[0]), en: clean(p[1]) };
});

const out = "// 由 tools/i18n_dict.mjs 生成，请勿手改。\n" +
  "export const DICT = " + JSON.stringify(DICT, null, 2) + ";\n";
fs.writeFileSync("src/i18n-dict.js", out, "utf8");
console.log("字典条目:", Object.keys(DICT).length, "| 主文案:", keys.length, "| 公告:", items.length);
console.log("公告配对示例:", items[0], "=>", DICT[items[0]].ja.slice(0, 24) + "…");
