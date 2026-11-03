# 第三方组件与授权说明

> **结论先说**：本项目的**管理器本体代码**由作者自行编写；但**安装包内含大量第三方编译产物**（插件 DLL、框架、游戏资源覆盖），这些不是原创，且带有强约束的开源授权。
>
> 本项目**不是完全原创**。任何对外分发（含发给朋友）都必须遵守下表的授权条款。

---

## 一、随安装包分发的第三方组件

以下组件被打包进 `resources/CS2BotImprover.zip`，安装时解压到玩家的 `game\csgo\` 目录。

### 1. 运行框架

| 组件 | 版本 | 原项目 | 地址 | 授权 |
|---|---|---|---|---|
| Metamod:Source | 2.0.0-git1483 | alliedmodders/metamod-source | https://github.com/alliedmodders/metamod-source | GPL-3.0 |
| CounterStrikeSharp | 1.0.378 | roflmuffin/CounterStrikeSharp | https://github.com/roflmuffin/CounterStrikeSharp | GPL-3.0 |
| 0Harmony | 2.x | pardeike/Harmony | https://github.com/pardeike/Harmony | MIT |

### 2. 人机插件（托管插件，CounterStrikeSharp 平台）

| 插件 | 上游项目 / 作者 | 地址 | 授权 |
|---|---|---|---|
| BotAI | CS2-BotAI（fork 自 Austin） | https://github.com/ed0ard/CS2-BotAI | AGPL-3.0 |
| BotAimImprover | CS2-Bullseye-Bot | https://github.com/ed0ard/CS2-Bullseye-Bot | AGPL-3.0 |
| BotBuy | CS2-Bot-Buy | https://github.com/ed0ard/CS2-Bot-Buy | AGPL-3.0 |
| BotRandomizer | CS2-Bot-Randomizer | https://github.com/ed0ard/CS2-Bot-Randomizer | AGPL-3.0 |
| NadeSystem | CS2-Bot-NadeSystem | https://github.com/ed0ard/CS2-Bot-NadeSystem | AGPL-3.0 |
| BotState | CS2-Smarter-Bot | https://github.com/ed0ard/CS2-Smarter-Bot | AGPL-3.0 |
| RoundDamageRecap | LBTV-CS2-Bot-Enhancer（YuGeYu） | https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer | AGPL-3.0 |
| BotControllerImpl / BotHiderImpl | 依赖 CS2-Lib（ianlucas） | https://github.com/ianlucas/cs2-lib | AGPL-3.0 / MIT |

### 3. 原生插件（Metamod 平台，C++）

| 插件 | 作者 | 地址 | 授权 |
|---|---|---|---|
| BotController | XBribo | https://github.com/XBribo/CS2-Bot-Controller | 见上游 |
| BotHider | XBribo | https://github.com/XBribo/CS2-Bot-Hider | 见上游 |
| BotVision | XBribo | https://github.com/XBribo/CS2-Bot-Vision | 见上游 |

### 4. 上游整合包

| 项目 | 地址 | 授权 |
|---|---|---|
| CS2-Bot-Improver（整包基础） | https://github.com/ed0ard/CS2-Bot-Improver | **AGPL-3.0** |

> 上游 `README.md` 的 Credits 段落是本文件的主要依据。
> 其许可证声明：整体 AGPL-3.0，**但 `Panel/` 子目录单独采用 PolyForm Strict 1.0.0**。

### 5. 来源未标注的组件（需补充）

以下三个插件来自使用者自行整合的版本，包内**没有附带作者信息、仓库地址或 LICENSE**：

| 组件 | 说明 | 授权 |
|---|---|---|
| `InventorySimulator` | 本地库存/皮肤模拟（客户端插件） | **未知，待补充** |
| `Killfeed_Icons` | 击杀提示栏图标替换 | **未知，待补充** |
| `PremierBadgeMvp` | XP 过载徽章 + 动态 MVP 名片 | **未知，待补充** |

⚠️ **这是当前最大的合规缺口。** 在公开分发前，必须确认这三者的来源与授权，或替换为授权明确的替代品。若无法确认，建议只在私下小范围使用，不要公开发布。

### 6. 游戏资源覆盖

| 内容 | 说明 | 来源 |
|---|---|---|
| `overrides/*/botprofile.vpk` | 人机名称表（中文人名） | 由上游 CS2-Bot-Improver 的 botprofile 修改而来 |
| `panorama/images/icons/equipment/*.vsvg_c` | 1593 个击杀图标资源 | 随 Killfeed_Icons 插件分发，来源同上「未标注」 |

---

## 二、仅作参考、未分发代码的部分

| 项目 | 用途 | 授权 | 是否分发 |
|---|---|---|---|
| CS2-Bot-Improver **Panel v1.4.7** | 逆向其 `validate_files` 行为，参考「硬编码清单 + 存在性检查」的判定模型、状态灯语义、`.bak` 备份命名 | **PolyForm Strict 1.0.0** | **否**。未复制其源码，未分发其 exe，仅参考行为并独立重写 |

⚠️ PolyForm Strict 明确禁止：再分发、修改、创作衍生作品。本项目的做法是「观察其公开可执行文件的外部行为，独立实现同类功能」。这一做法在著作权法上通常不构成侵权（思想/方法不受保护，表达受保护），但**如果对方主张，存在争议空间**。公开发布前建议注明此参考关系。

---

## 三、本项目自行编写的部分

以下**全部为原创代码**，不含上游源码：

| 文件 / 目录 | 内容 | 行数 |
|---|---|---|
| `src-tauri/src/lib.rs` | 全部 Rust 后端：插件清单校验、规则引擎、难度切换、徽章配置、击杀图标、换肤模式、更新检查与下载、安装/卸载 | ~2400 |
| `src/App.jsx` | 全部界面：8 个页面、交互式教程、公告弹窗、进度条 | ~1300 |
| `src/api.js` | 前后端调用适配层 | ~200 |
| `src/styles/*.css` | 全部样式：侧边栏布局、磨砂玻璃、转场动画、组件样式 | ~1500 |
| `src-tauri/tauri.conf.json` | 打包配置 | — |
| `使用教程-v2.1.0.md`、`更新公告-*.md`、`云更新配置指南.md` | 全部文档 | — |

**没有从上游复制任何源代码。** 判定逻辑、cfg 写入策略、清单结构均为按需求独立实现。

---

## 四、授权合规要点（重要）

### AGPL-3.0 的义务

因为安装包内含 AGPL-3.0 组件（CS2-Bot-Improver 生态的插件 DLL），对外分发时你**必须**：

1. **提供对应源码**：附上上游仓库地址，或书面承诺可提供源码
2. **保留版权声明与许可证文本**：不能删除插件内的版权信息
3. **同样以 AGPL-3.0 授权**这些组件的修改版本（如果你改了它们的代码）
4. **网络服务条款**：如果你把它做成在线服务，用户有权获取源码

**最省事的做法**：把这个仓库设为 Public，README 里写清上游地址。这样「提供源码」的义务自动满足。

### 管理器本体可以是 MIT

管理器本体（Rust + React 代码）与插件是**独立程序**，通过文件系统交互，属于「聚合（mere aggregation）」，不构成衍生作品。因此本体可以采用 MIT，插件保持各自的 AGPL。

### Panel 的边界

- ✅ 可以参考它的功能设计、交互逻辑、判定模型
- ❌ 不能复制它的源码
- ❌ 不能把它的 exe 打进你的安装包
- ⚠️ 参考行为本身有争议，公开发布建议注明

---

## 五、免责声明

- 本项目仅用于**离线人机对局**与**好友私人服务器**
- 不适用于 Valve 官方匹配、FACEIT 等第三方竞技平台
- 使用 `-insecure` 启动会关闭 VAC，**不要在官匹使用**
- 因使用本项目导致的账号处罚、数据丢失，作者不承担责任

---

*本文件最后更新：2026-11-03*
