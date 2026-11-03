# 雪绒人机管理器

> CS2 离线人机增强的一体化管理工具。图形界面安装插件、调规则、换难度、管徽章、换肤，支持软件内自动更新。

![version](https://img.shields.io/badge/version-2.4.0-ff7aa8)
![platform](https://img.shields.io/badge/platform-Windows%20x64-8f6ae0)
![tauri](https://img.shields.io/badge/Tauri-2-24C8DB)
![license](https://img.shields.io/badge/license-MIT%20(own%20code)-lightgrey)

---

## ⚠️ 先读这一段

**本项目不是完全原创。**

- **管理器本体**（Rust 后端 + React 前端，约 5400 行）由作者自行编写
- **安装包内含的插件、框架、游戏资源**全部来自第三方开源项目，不是原创
- 其中核心生态 **CS2-Bot-Improver 采用 AGPL-3.0**，对分发有强约束

完整的组件清单、原作者、仓库地址、授权类型见 **[THIRD-PARTY.md](THIRD-PARTY.md)**。

**如果你要公开分发，务必先读 THIRD-PARTY.md 第四节（授权合规要点）。**

---

## 功能

| 页面 | 作用 |
|---|---|
| **总览** | 状态灯校验、双模式启动、运行模式切换（完整人机 / 只开换肤）、安装修复、一键卸载 |
| **插件** | 55 项清单校验（51 文件 + 资源目录 + gameinfo 两项 + 配置一项）、分类开关 |
| **人机** | 难度三档（换 overrides VPK + bot_difficulty）、8 项 Bot 物品、瞄准模式、投掷物强度 |
| **规则** | 加时 / 友伤 / 叠人 / 连跳 / C4 倒计时 / 燃烧瓶倍率，写入 cfg 即时生效 |
| **外观** | XP 过载徽章、动态 MVP 名片、22 套击杀图标、皮肤库存直达 |
| **命令** | 15 条高频控制台命令，点一下复制 |
| **教程** | 9 步交互式引导，按钮可直接跳页 / 复制 / 重新检测 |
| **设置** | 路径、模式、背景模糊遮罩、音乐、兼容性开关、软件更新 |

**其它特性**

- 磨砂玻璃界面 + MP4 动态背景 + 背景音乐（可调音量、切页不断）
- 更新公告弹窗、版本更新自动提示
- 软件内检查更新 + 下载进度条 + 静默安装
- 崩溃排查：CS2 运行中拒绝改文件、崩溃转储扫描、安全模式

---

## 安装

### 给普通用户

下载 `雪绒人机管理器_x.y.z_x64-setup.exe`，双击安装。

- 安装到 `%LOCALAPPDATA%\Programs`，**不需要管理员权限**
- 首次运行会提示 SmartScreen「Windows 已保护你的电脑」→ 点「更多信息」→「仍要运行」（未做代码签名，正常现象）
- 装完后在设置页确认游戏路径 → 总览页点「安装 / 修复套件」→「插件模式」启动

### 前置要求

- Windows 10 / 11 x64
- 已安装 CS2（Steam 版本）
- WebView2 运行时（Win11 自带；Win10 一般随 Edge 更新，缺失时安装器会尝试下载）

---

## 从源码构建

```bash
# 依赖：Node 18+、Rust 1.77+、MSVC 工具链
npm install
npx tauri build
```

产物在 `src-tauri/target/release/bundle/nsis/`。

### ⚠️ 构建前必须准备插件包

`src-tauri/resources/CS2BotImprover.zip`（约 81 MB）**不在仓库里**（体积过大，且含第三方二进制）。

自行准备：从上游 CS2-Bot-Improver 的 Release 下载整合包，解压后把 `addons / backup / cfg / gameinfo.gi / overrides / panorama` 重新打成 zip，放到该路径。

没有这个文件，编译能过，但「安装 / 修复套件」会报「未找到离线安装包」。

---

## 目录结构

```
.
├── src/                      # React 前端
│   ├── App.jsx               # 全部界面与页面
│   ├── api.js                # 前后端调用适配
│   ├── assets/               # 背景视频、音乐、头像、宠物图
│   └── styles/               # tokens / app（侧边栏 + 组件）
├── src-tauri/
│   ├── src/lib.rs            # 全部 Rust 后端命令
│   ├── icons/                # 应用图标（含 icon.ico）
│   ├── resources/            # 插件包（不提交，见上）
│   └── tauri.conf.json       # 打包配置
├── THIRD-PARTY.md            # 第三方组件与授权（必读）
├── 云更新配置指南.md          # GitHub Releases 更新配置
├── 使用教程-v2.1.0.md        # 文字版教程
└── LICENSE                   # 本项目代码授权（MIT）
```

---

## 软件内自动更新

设置页填一次更新源（`github:用户名/仓库名`），之后：

1. 启动时静默检查一次，有新版本左侧栏亮小红点
2. 点「检查更新」→ 显示新版本号与更新说明
3. 点「立即更新」→ 从 GitHub Release 拉取安装包，界面显示进度条
4. 下载完成 → 自动静默安装 → 程序自动关闭

详细配置见 **[云更新配置指南.md](云更新配置指南.md)**。

---

## 第三方组件与授权

本项目**安装包内含**以下第三方组件，均非原创：

- **Metamod:Source**（GPL-3.0）、**CounterStrikeSharp**（GPL-3.0）、**0Harmony**（MIT）
- **CS2-Bot-Improver 生态插件**（AGPL-3.0）——BotAI / BotAimImprover / BotBuy / BotRandomizer / NadeSystem / BotState / RoundDamageRecap 等
- **BotController / BotHider / BotVision**（XBribo）
- **InventorySimulator / Killfeed_Icons / PremierBadgeMvp** —— ⚠️ 来源未标注，授权待确认

完整清单（含每个组件的原作者、GitHub 地址、授权类型、是否随包分发）：**[THIRD-PARTY.md](THIRD-PARTY.md)**

### 本项目的授权

**管理器本体代码采用 [MIT](LICENSE)**。

但请注意：MIT **仅覆盖本仓库中作者编写的代码**，不覆盖安装包内的第三方二进制。对外分发含 AGPL 组件的安装包时，仍需履行 AGPL 的源码提供义务。

---

## 免责声明

- 仅用于**离线人机对局**与**好友私人服务器**
- 不适用于 Valve 官方匹配、FACEIT 等第三方竞技平台
- 插件模式使用 `-insecure` 启动，会关闭 VAC，**切勿在官匹使用**
- 因使用本项目导致的账号处罚、数据丢失，作者不承担责任

---

## 致谢

感谢以下项目的作者，没有他们就没有这个工具：

[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) ·
[Metamod:Source](https://github.com/alliedmodders/metamod-source) ·
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) ·
[Harmony](https://github.com/pardeike/Harmony) ·
[cs2-lib](https://github.com/ianlucas/cs2-lib) ·
[XBribo](https://github.com/XBribo) ·
[YuGeYu](https://github.com/YuGeYu) ·
[Tauri](https://github.com/tauri-apps/tauri)
