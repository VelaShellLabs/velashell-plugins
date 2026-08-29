# AGENTS.md

> 给 AI 代理与新加入者的操作约定。**动手之前先读完本文件,以及它指向的文档。**

## 一、开工前必读:velashell-docs

VelaShell 生态的**全部文档**集中在一个仓库:
**[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**。
本仓库**不放** `docs/`、`docs-en/` —— 设计手册、开发规范与开发文档都在那边。

**在动任何代码之前**,先把下表中与你要改的部分相关的几篇读掉。跳过这一步直接改,
结果通常是两种:与既有设计冲突,或者重复实现一个已经存在的能力。

| 位置 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | 宿主分层架构与依赖方向、工程化重构蓝图、交互与界面规格、快捷键参考、设置项审计,以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 01–15(进程模型、IPC 协议、权限系统、UI 扩展、威胁模型、路线图)与[进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) | 插件契约 SDK 参考、SDK 仓库的发版流程 |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) | `vela-plugin` 命令行手册、CLI 仓库的发版流程 |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | 插件开发指南、打包与发布、模板仓库的发版流程 |

英文镜像在 [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en),与 `zh/` 同构。
[仓库首页](https://github.com/VelaShellLabs/velashell-docs)有按「我想做什么」组织的快速入口表。

## 二、涉及文档的改动一律同步到 velashell-docs

**这是本文件最重要的一条。**

- 本仓库里**不新建** `docs/`、`docs-en/` 或任何成体系的文档目录。要写文档,去 velashell-docs 开 PR。
- 改了代码,而**行为、接口、配置项、命令行、构建流程或版本纪律**与现有文档对不上时,
  必须**同时**在 velashell-docs 提一个 PR 把文档改过来。两个 PR 在正文里互相引用,一起合。
  只改代码不改文档,等于让文档开始骗人 —— 而文档是别人照抄的。
- velashell-docs 的 `zh/` 与 `en/` 是**互为镜像**的两棵树,文件一一对应。改了中文就要改英文,
  反之亦然。漏一边,两棵树就开始漂。
- velashell-docs 内部的互相引用**一律走相对路径**(如 `../templates/dev-guide.md`),
  不要写回 GitHub 绝对 URL —— 文档集中到一个仓库,消掉的正是那种一改路径就断的跨仓库链接。
- **例外**:留在代码仓库里的少数几份文件不适用上述规则,因为它们服务的是「在这个仓库里写代码」
  这件事,搬走只会离使用场景更远。各仓库的例外清单见下面第三节。

## 三、本仓库:velashell-plugins(第一方插件)

Redis / S3 / Telnet / 串口等第一方插件,以 Release 资产 `velashell-plugins-<版本>.zip` 交付。

### 构建与打包

```bash
dotnet build VelaShell.Plugins.slnx
dotnet build plugins/VelaShell.Plugin.Redis -t:PackVpx     # 单个插件出 bin/vpx/*.vpx
dotnet build build/PluginBundle.proj -c Release -t:PackAllVpx -p:VelaSigningKey=<key.pem>
```

本仓库的插件与第三方插件走**完全同一条路径**:从 nuget.org 引用 `VelaShell.PluginSdk.Build`。
第一方插件因此天然是 SDK 包的第一个用户 —— 包坏了我们自己先撞上,而不是等插件作者来报。
想验一版未发布的 SDK,`-p:VelaSdkVersion=<版本>-dev` 配 `nuget.config` 里的本地源。

### 写插件之前必须读的

- [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) —— 清单、生命周期、能力 API、隔离模式、测试、性能纪律
- [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md) —— 契约表面与能力域一览
- [权限系统](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/06-permission-system.md)与[威胁模型](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/12-security-threat-model.md) —— 申请任何敏感能力前

各插件的设计取证也在 velashell-docs:Redis 见
[`zh/host/Redis客户端插件化调研与设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Redis客户端插件化调研与设计.md),
S3 见 [`zh/host/S3协议插件化设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/S3协议插件化设计.md),
Telnet / 串口见 [`zh/host/Telnet与串口可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Telnet与串口可行性调研.md)。
**改了这些插件的设计取舍,要回写对应那篇。**

### 视觉对齐宿主

插件面板的几何与配色取自宿主的
[`DESIGN.md`](https://github.com/joesdu/VelaShell/blob/main/DESIGN.md),不发明新令牌:
36px 文档头 / 26px 列头 / 28px 行 / 24px 状态条。刻意偏离要在 README 里写明理由。

### 留在本仓库的文档

`README.md`、`LICENSE`,以及各插件目录下的 `README.md`(该插件自己的实现说明与偏离记录)。
