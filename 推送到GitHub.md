# 推送到 GitHub（三步）

本地仓库已经建好并提交了（分支 `master`，2 个提交，2.87 MB，101 个文件）。
我没有你的 GitHub 登录态，推不上去，你执行下面三步即可。

---

## 1. 在 GitHub 建空仓库

https://github.com/new

- **Repository name**：`xuerong-manager`（随便起）
- 选 **Public**（私有仓库也能用，但 Releases 的 API 需要 token，麻烦）
- **不要**勾选 "Add a README file" / "Add .gitignore" / "Choose a license"（本地已经有了，勾了会冲突）
- 点 Create repository

---

## 2. 关联远程并推送

远程地址和分支已经配好了，直接执行这一条：

```bash
cd "C:\Users\ASUS\Downloads\雪绒bot管理器\雪绒人机管理器"

git remote add origin https://github.com/你的用户名/xuerong-manager.git
git branch -M main
git push -u origin main
```

第一次推会弹浏览器让你登录 GitHub 授权，点同意即可。

---

## 3. 发第一个 Release（自动更新靠这个）

仓库页面右侧 → **Releases** → **Create a new release**

| 字段 | 填什么 |
|---|---|
| Choose a tag | `v2.4.1`（点 "Create new tag"） |
| Release title | `v2.4.1` |
| Describe this release | 直接复制 `更新公告` 里的内容 |
| Attach binaries | 拖入 `雪绒人机管理器_2.4.1_x64-setup.exe` |

点 **Publish release**。

**不用再填更新源了** —— v2.4.1 已经把 `github:hanhanmi/xuerong-manager` 写进程序。

以后每次发版只要重复第 3 步（改 tag 号 + 换安装包），所有装了软件的人打开就会看到红点提示。

---

## 常见问题

**推不上去 / 403**
GitHub 从 2021 年起不能用账号密码推送，需要 Personal Access Token：
Settings → Developer settings → Personal access tokens → Tokens (classic) → Generate new token → 勾 `repo` → 复制 token。
推送时用户名填 GitHub 用户名，密码位置粘贴 token。

**`remote origin already exists`**
先 `git remote remove origin` 再重新 add。

**`failed to push some refs`**
远程仓库建的时候勾了 README，先 `git pull --rebase origin main` 再推。

**仓库体积**
已排除 `node_modules`、`dist`、`src-tauri/target`、插件包 zip、背景音乐/视频。当前 2.87 MB，很干净。

---

## 仓库里现在有什么

```
README.md              项目介绍 + 原创声明 + 第三方致谢
THIRD-PARTY.md         ★ 第三方组件完整清单与授权（必读）
LICENSE                MIT（仅覆盖作者编写的代码）
.gitignore             排除依赖、产物、版权素材
云更新配置指南.md       更新源配置说明
使用教程-v2.1.0.md      文字版教程
src/                   React 前端（原创）
src-tauri/             Rust 后端（原创）+ 图标
```

**不在仓库里**（需自行准备，见 README「从源码构建」）：
`src-tauri/resources/CS2BotImprover.zip`、`src/assets/bgm.mp3`、`src/assets/bg.mp4`
