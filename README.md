# 喜加一（XiJiaYi）

基于 WPF + .NET 8 开发的现代化 Steam 游戏管理工具（适配 OpenSteamTool 内核）。从制作到发布皆由ChatGPT一手包办，有问题不要问我，我也不懂

## 功能一览
<img width="1755" height="1182" alt="image" src="https://github.com/user-attachments/assets/974bd64d-6764-4c9d-8603-5f63d2de95af" />





### 游戏库
- 已入库游戏自动展示封面与中文名，卡片 / 列表布局自由切换
- 搜索、排序、筛选、批量启用/禁用/删除
- 拖拽 .lua（游戏清单）与 .bin（成就数据）一键导入
- 版本固定：将清单固定到当前安装版本或 Steam 最新版本
- DLC 查询与一键补全入库

### 入库管理
- 按 AppID / 游戏名搜索并入库新游戏
- 多数据源获取 depot 密钥（含本地缓存仓库与远程清单仓库，自动更新）

### 授权
- 从正版账号提取游戏清单与成就数据（Lua / Bin）
- AppTicket / ETicket 授权提取、管理、拖拽导入

### 修改器
- 内置搜索下载：支持中英文游戏名搜索（中文名自动解析）、热门推荐、最新发布、一键下载与管理
- 数据来源：风灵月影官网（flingtrainer.com）

### 成就管理
- 查看 / 解锁 / 回锁已拥有游戏的 Steam 成就

### 在线联机
- 以 SpaceWar(480) 身份启动游戏（内核启动参数 `-onlinefix`），通过 AppID 480 大厅匹配实现好友联机

### 存档管理
- 自动扫描 Steam 本地云存档（userdata/remote）
- 本地备份 / 一键恢复，恢复前自动再次备份
- WebDAV 云端备份（兼容坚果云、Nextcloud、Alist 等），换电脑不丢档
- 完美存档一键替换：ZIP / 文件夹导入，自动修正存档内 Steam 账号 ID（64 位与 3 位自动换算）

### 界面与皮肤
- 纯黑深色 Fluent 风格界面，多套配色与动漫壁纸皮肤
- 启动时自动检查更新（GitHub / Gitee Releases），发现新版本弹窗提示下载

## 环境要求

- Windows 10 / 11（64 位）
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)
- Steam + OpenSteamTool 内核

## 构建

```bash
dotnet publish SteamLuaManager.csproj -c Release -r win-x64 --self-contained false \
  -o out /p:PublishSingleFile=true /p:DebugType=None /p:DebugFullType=None
```

## 致谢（所用开源项目与代码）

本项目基于以下开源项目重构、扩展而来，特此致谢：

| 项目 | 用途 | 许可 |
|---|---|---|
| [Fluent-Steam-Lua](https://github.com/huanyuejue/Fluent-Steam-Lua) | 原始界面与入库/提取基础框架 | zlib |
| [Steam Achievement Manager (SamApi)](https://github.com/gibbed/SteamAchievementManager)（Rick/gibbed） | 成就读写 API（Services/SamApi） | zlib |
| OpenSteamTool | Steam 内核（授权、清单、联机支持） | 见其项目主页 |
| ManifestHub-GUI | 在线联机与清单数据源参考 | 见其项目主页 |
| [iNKORE.UI.WPF.Modern](https://github.com/Kinnara/ModernWpf) | Fluent 风格 UI 控件 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM 框架 | MIT |
| [HtmlAgilityPack](https://html-agility-pack.net/) | HTML 解析 | MIT |
| [Tomlyn](https://github.com/xoofx/Tomlyn) | TOML 配置解析 | BSD-2-Clause |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | 依赖注入 | MIT |

各项目的完整许可文件请见其源码仓库；本仓库内保留的第三方代码（如 `Services/SamApi/LICENSE.txt`）均已随附原始许可声明。

## 声明

本软件仅供学习交流使用，请支持正版游戏。使用本工具产生的任何后果由使用者自行承担。
