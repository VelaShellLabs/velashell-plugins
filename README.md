# VelaShell 第一方插件

> 当前版本 **1.4.0** · SDK **1.4.0**

[VelaShell](https://github.com/joesdu/VelaShell) 官方维护的插件,一个解决方案管起来:
Redis、S3、Telnet,外加示例插件 HelloWorld。

三个仓库各管一摊,别串:

| 仓库 | 管什么 |
| --- | --- |
| [joesdu/VelaShell](https://github.com/joesdu/VelaShell) | 主程序(宿主) |
| [joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain) | 插件 SDK 与工具链:契约程序集、测试替身、构建包、`vela-plugin`、`dotnet new` 模板 |
| **本仓库** | 第一方插件本身(AI 插件除外,见下) |

本仓库的插件与第三方插件走**完全同一条路径** —— 从 nuget.org 引用
`VelaShell.PluginSdk.Build`,没有任何"仓库内特权"。所以第一方插件天然是 SDK 包的
第一个用户:包坏了我们自己先撞上,而不是等插件作者来报。

> **AI 插件 `velashell.ai` 不在这里**:它住在主仓库的 `plugins/` 下,随主程序同仓构建、
> 同版发布 —— 它借宿主的 AvaloniaEdit 作输入框,只能进程内装载,而进程内装载要求编译时
> 引用的 Avalonia 与宿主逐字同版;面板还要跟着宿主的主题、语言、字体走。分仓的话每改一行
> UI 都要"发一次 Release → 回主仓库抬 pin → 才看得到效果"。详见主仓库 `plugins/README.md`。

## 仓库里有什么

| 目录 | 内容 |
| --- | --- |
| `plugins/` | 四个插件,一个子目录一个 csproj(见 [plugins/README.md](plugins/README.md)) |
| `tests/` | 每个插件一个测试工程(MSTest + `VelaShell.PluginSdk.Testing` 替身) |
| `build/PluginBundle.proj` | 发布期把可分发插件收成 `velashell-plugins-<版本>.zip`,并批量出 `.vpx` |
| `scripts/Set-Version.ps1` | 把发行版本号写进仓库里所有落点(发版时由流水线自动跑) |
| `Directory.Packages.props` | **中央包管理**:所有 NuGet 版本只在这一处 |

## 开发

```bash
dotnet build VelaShell.Plugins.slnx
dotnet test  VelaShell.Plugins.slnx -c Debug
```

本仓库不出 NuGet 包、不做强名称签名(那是工具链仓库的事),所以 Release 构建
不需要任何密钥,`-c Debug` 也只是跑测试的习惯而非硬性要求。

### 改插件时立刻在真实宿主里看到效果

构建后插件输出会镜像到 `artifacts/plugins/<目录名>/`。要直接铺进本机 VelaShell,
指一下应用目录即可:

```powershell
$env:VELASHELL_DEV_APP_DIR = 'G:\VelaShell\src\VelaShell\bin\Debug\net11.0'
dotnet build plugins/VelaShell.Plugin.Redis
```

反向也行 —— 在主仓库跑
`pwsh scripts/Fetch-Plugins.ps1 -FromPluginsRepo G:\velashell-plugins`,
由宿主主动来取,不用改这边任何设置。

### 出一个 .vpx(手工安装 / 插件市场的源)

```bash
dotnet build plugins/VelaShell.Plugin.Redis -t:PackVpx     # 落 bin/vpx/*.vpx
```

`PackVpx` 与打包器都来自 `VelaShell.PluginSdk.Build` 包 —— 与第三方插件用的是同一个。

### 联调一个还没发布的 SDK

在工具链仓库 `dotnet pack ... -p:VelaSdkVersion=1.5.0-dev -o artifacts/nuget`,
然后打开本仓库 [`nuget.config`](nuget.config) 里那条注释掉的本地源,再
`dotnet build -p:VelaSdkVersion=1.5.0-dev`。用完记得把本地源注释回去再提交。

## 两个版本号,别混

- **插件自己的版本**在各自的 `plugin.json` 里,各插件独立演进,决定 `.vpx` 文件名
  与宿主看到的插件版本。
- **本仓库的发行版本**(`Directory.Build.props` 的 `VelaPluginsVersion`)只回答
  "这一批插件是哪次发布出去的"。主仓库按它 pin(`VelaPluginsBundleVersion`),
  从本仓库的 Release 资产里取那一版 zip。

它与 SDK 版本(`VelaSdkVersion`)也是两回事:SDK 发 1.5.0 不代表插件必须跟着发,
插件发 1.4.1 也不代表契约动了。

## 发布

**在 GitHub 上发布 Release**(标签形如 `v1.4.0`),流水线会:

1. 把版本号写进仓库(`scripts/Set-Version.ps1`),因此产物永远与标签一致;
2. 全量测试 + 构建;
3. 产出并挂到该 Release:
   - `velashell-plugins-<版本>.zip` —— 包内布局就是安装包 `plugins/` 那一层,主仓库下载解开即可;
   - 每个插件一份 `.vpx` —— 供用户手工安装 / 作插件市场的源;
   - `SHA256SUMS.txt`;
4. 开一个 `chore/version-<版本>` 的 PR 把版本号回写 `main`,等你手动合。

本地也可以先跑一遍:

```powershell
pwsh scripts/Set-Version.ps1 1.5.0            # 落盘
pwsh scripts/Set-Version.ps1 1.5.0 -Check     # 只报告(CI 每次 push/PR 都跑这个)
```

## 与宿主的硬约束

**Avalonia 版本必须与宿主一致**。这个版本号的权威在工具链仓库:
`VelaShell.PluginSdk.Build` 用精确区间 `[x.y.z]` 锁给插件工程,
`VelaShell.PluginSdk` 再把同一个值经 `buildTransitive` 导出成
`VelaSdkPinnedAvaloniaVersion`。本仓库的 `VelaAvaloniaVersion` 只服务于测试工程
(测试宿主要扮演装载方),由 `Directory.Build.targets` 的 `VerifyAvaloniaMatchesSdk`
在构建期与 SDK 锁的值核对 —— 漂了当场报 `VELAP1000`,而不是等用户装上插件才炸。

## 许可

AGPL-3.0-only,与主仓库一致。商业授权见主仓库的 `LICENSE-COMMERCIAL.md`。
