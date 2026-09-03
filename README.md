# 喜加一（XiJiaYi）

基于 WPF + .NET 8 开发的现代高级 Steam 游戏管理与生态工具（深度适配开源 OpenSteamTool 内核）。

从制作到发布皆由 ChatGPT 一手包办，有问题不要问我，我也不懂。

---

## 📸 功能一览

<img width="1755" height="1182" alt="image" src="https://github.com/user-attachments/assets/974bd64d-6764-4c9d-8603-5f63d2de95af" />

---

## ✨ 核心特性

### 🎮 我的游戏库
- **现代卡片视图**：自动抓取高清封面与中文名称，支持大卡片网格与紧凑列表双视图无缝切换。
- **智能检索与筛选**：支持按游戏名、AppID 模糊搜索，支持按已启用/已禁用状态筛选与多维排序。
- **批量管理**：支持一键批量启用、批量禁用、批量删除游戏清单。
- **版本固定**：可锁定到当前已安装版本或 Steam 最新发布版本，防止补丁更新导致游戏无法启动。
- **DLC 补全查询**：卡片菜单一键深度查询该游戏的所有清单 DLC，支持一键补全入库。

### 🔑 D加密授权体系
- **正版号一键授权（本机）**：已拥有正版游戏的账号登录时，一键从 Steam 提取票据并写入注册表，换号免购买直接离线畅玩。
- **提取并导出授权文件（分享好友）**：一键生成独立授权票据文件，自动保存至桌面【喜加一授权导出】目录并自动高亮选中文件，随手拖进聊天软件即可分享。
- **多格式授权导入**：支持 `.txt`（tickets）、`.cw`、`.shiki`、`.json` 等全格式授权文件，一键注入注册表生效。

### 🚀 全局智能拖拽与自动识别补全
- **全窗口拖拽支持**：无需停留在指定页面，在软件任意界面直接拖入文件。
- **授权自动补全入库**：拖入他人发来的授权文件时，软件自动解析 AppID。若游戏尚未入库，自动提示并全自动完成“清单入库 + 授权写入”，一步到位！
- **清单与成就拖入**：直接拖入 `.lua`（游戏清单）或 `.bin`（成就数据）瞬间完成入库。

### 🔥 热门游戏
- **官方热销实时抓取**：直连 Steam 官方热销商品榜，实时拉取前 30 名热门大作。
- **纯游戏智能过滤**：全自动剔除免费游戏、DLC 扩展包、Steam Deck 等硬件外设及实体配件，只收录纯游戏。
- **一键极速入库**：热门榜单中看中哪款，直接点击即可一键加入 Steam 游戏库。

### 📦 入库管理
- **多数据源 Depot 检索**：按 AppID 或名称搜索新游戏，接入本地缓存仓库 V1/V2、ShikiLua 内置库与远程清单仓库，自动同步最新 DepotKey。

### ⚡ 修改器
- **内置搜索下载**：集成风灵月影官方资源，支持中英文双向模糊解析搜索。
- **热门推荐与排行**：实时查看热门修改器榜单，支持一键极速下载、本地版本管理与直接启动运行。

### 🏆 成就管理
- **本地安全读写**：基于开源 SAM API，安全读取并修改已拥有的 Steam 游戏成就。
- **批量与自定义**：支持一键全解锁、一键回锁（重置成就）、单项自定义勾选及成就名称搜索。

### 💾 存档管理
- **本地快照与恢复**：自动扫描各盘符 Steam 远程存档目录（userdata/remote），恢复存档前自动备份当前进度，防止误操作丢档。
- **WebDAV 云端同步**：支持坚果云、Nextcloud、Alist、群晖等任意 WebDAV 服务，跨设备无缝同步游戏进度。

### 🌐 在线联机
- **内核驱动联机**：房主创建房间后打开好友列表，右键好友头像，点击 【邀请加入游戏】；请注意是邀请Spacewar，而不是你当前在游玩的游戏。这时好友会收到一条来自 Spacewar 的邀请弹窗，点击 【加入游戏】，即可进入您的游戏房间！ 好友也可以直接在 Steam 列表右键房主头像，点击【加入游戏】。
正版玩家只要也用 480 模式启动，也能和好友无缝联机。
搜不到/连不上请检查双方游戏版本是否完全一致，版本不一致数据包会对不上。

### 🛠️ 快捷助手与内核生态
- **Steam 快捷助手**：侧边栏快速启动 Steam、快速重启 Steam、免密多账号极速切换。
- **内置开源 OpenSteamTool**：程序资源已内嵌最新开源内核，零网络依赖，断网也能秒速安装。
- **第三方冲突智能修复**：遇到第三方闭源内核残留时自动预警，支持一键安全清理冲突（`steam.cfg`、`stplug-in` 等）并换装开源内核。

---

## 💻 环境要求

- **操作系统**：Windows 10 / 11（64 位）
- **运行环境**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)
- **依赖平台**：Steam 客户端（搭载开源 OpenSteamTool 内核）

---

## 🛠️ 构建指南

```bash
dotnet publish SteamLuaManager.csproj -c Release -r win-x64 --self-contained false \
  -o out /p:PublishSingleFile=true /p:DebugType=None /p:DebugFullType=None
```

---

## 💖 致谢

本项目基于以下优秀的开源项目重构、扩展与整合而来，特此致敬：

| 项目 | 用途说明 | 许可证 |
|---|---|---|
| [Fluent-Steam-Lua](https://github.com/huanyuejue/Fluent-Steam-Lua) | 现代化界面与基础框架参考 | zlib |
| [Steam Achievement Manager (SamApi)](https://github.com/gibbed/SteamAchievementManager) | 本地成就读写支持库 | zlib |
| [OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool) | 开源 Steam 注入内核（清单、授权、联机） | GPL-3.0 |
| [iNKORE.UI.WPF.Modern](https://github.com/Kinnara/ModernWpf) | Windows 11 Fluent 风格 UI 控件库 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 高性能 MVVM 架构支撑 | MIT |
| [HtmlAgilityPack](https://html-agility-pack.net/) | 高性能 HTML 解析提取器 | MIT |
| [Tomlyn](https://github.com/xoofx/Tomlyn) | 现代 TOML 配置文件读写库 | BSD-2-Clause |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | 官方依赖注入容器 | MIT |

---

## ⚠️ 声明

本软件仅供编程学习与技术交流使用，请支持购买 Steam 官方正版游戏。因使用本工具产生的一切后果由使用者自行承担。
