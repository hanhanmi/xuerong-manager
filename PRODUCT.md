# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Tauri 2 + Rust 后端 + React 18 + Vite 前端，编译为 Windows NSIS 安装包（69.74 MB exe）。73MB 离线插件包内嵌在 exe resources 中，完全自包含。同时保留 Node 服务降级模式（浏览器运行），通过 `api.js` 适配层自动切换。

## Users

CS2 玩家与社区服主，在本地 Windows 机器上管理人机插件。主要操作：检测 CS2 路径、一键安装插件包、开关单个插件、配置 XP 勋章与击杀图标。使用频率：每周数次的快速查看与调整。

## Product Purpose

让 CS2 人机插件的安装与管理像桌面应用一样简单：双击启动、自动检测路径、一键安装、可视化开关，无需手动复制文件或编辑配置。

## Positioning

基于 CS2-Bot-Improver 生态，提供统一的粉色磨砂玻璃管理界面。管理 16 个插件模块：Metamod + CounterStrikeSharp（框架层）、BotController/BotHider/BotVision（原生 C++ 插件）、9 个 C# 托管插件（BotAI/BotAimImprover/BotBuy 等）、PremierShowcase2（XP 勋章段位伪造）、Killfeed Icons（击杀图标皮肤切换）。

## Decisions (用户确认)

1. **技术栈**：Tauri 2 + React，编译为 exe 安装包（当前先交付 Node 服务版，预留迁移接口）
2. **插件来源**：本地离线包优先（快、可靠），同时提供 GitHub 更新检查与拉取按钮
3. **XP 勋章**：对应 PremierShowcase2 插件（段位徽章/XP/Rating 伪造），闪电图标即其 XP 表现，作为独立管理模块
4. **配色**：浅色粉色系磨砂玻璃，主色 #FF5A8A

## Operating Context

CS2 安装目录（通常 Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo）。插件文件部署到 addons/、overrides/、cfg/ 目录。安装包为离线 zip (73MB)。

## Capabilities and Constraints

- 自动检测 CS2 安装路径（读取 Steam libraryfolders.vdf）
- 一键安装离线包（PowerShell Expand-Archive + 复制 + gameinfo.gi 补丁）
- 插件开关：重命名 .dll ↔ .dll.off
- GitHub 更新检查（api.github.com/repos/.../releases/latest）
- PremierShowcase2 XP 勋章配置（Rating、XP 倍率、闪电图标、徽章样式）
- Killfeed Icons 皮肤切换（18 套 txz01–txz18）
- 新手引导（5 步，高亮 + 弹窗）
- Toast 通知

## Brand Commitments

名称：雪绒人机管理器。标志：粉底白字「雪」monogram。配色：粉色系磨砂玻璃（主色 #FF5A8A）。视觉语言：Apple Liquid Glass。

## Evidence on Hand

- 离线安装包：`../CS2人机增强助手/resources/CS2BotImprover.zip` (73MB)
- 开屏图：`public/assets/splash.png`（源文件 `../启动软件开屏的小图片.png`）
- 插件改动说明：`../CS2插件与配置改动说明.md`
- 完整源码参考：`../CS2人机管理器-完整源码.md`
- 现有 Panel 源码（Liquid Glass 设计语言）：`../../cs2插件/work/bot_improver/CS2-Bot-Improver-1.4.6/Panel/`
- FOLIA 设计参考包：`../folia-major-0.7.25.zip`（已解压至 `.folia-ref/`）

## Product Principles

1. 零门槛启动：双击 .bat 即用，无需编译或安装依赖
2. 粉色玻璃一致性：所有界面遵循统一的磨砂玻璃材质与粉色 token
3. 安装可靠优先：本地离线包安装为主，GitHub 更新为辅
4. 功能可视化：每个插件都有开关、描述与状态指示
5. 新手友好：首次使用有分步引导，每个功能有悬浮说明
