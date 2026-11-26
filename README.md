# VelaShell 第一方插件

> 当前版本 **2.2.1** · SDK **2.1.5**

[VelaShell](https://github.com/joesdu/VelaShell) 官方维护的插件,一个解决方案管起来:
Redis、MongoDB、S3、Telnet、串口、Docker 面板、RDP 远程桌面、VNC 远程桌面。

几个仓库各管一摊,别串:

| 仓库 | 管什么 |
| --- | --- |
| [joesdu/VelaShell](https://github.com/joesdu/VelaShell) | 主程序(宿主) |
| [VelaShell/velashell-plugin-sdk](https://github.com/VelaShell/velashell-plugin-sdk) | 契约程序集、测试替身与构建包(同版本、同一次发布):`VelaShell.PluginSdk`、`VelaShell.PluginSdk.Testing`、`VelaShell.PluginSdk.Build` |
| [VelaShell/velashell-plugin-cli](https://github.com/VelaShell/velashell-plugin-cli) | 命令行:`vela-plugin` |
| [VelaShell/velashell-plugin-templates](https://github.com/VelaShell/velashell-plugin-templates) | `dotnet new` 模板:`VelaShell.Plugin.Templates` |
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
| `plugins/` | 八个插件,一个子目录一个 csproj(见 [plugins/README.md](plugins/README.md)) |
| `tests/` | 每个插件一个测试工程(MSTest + `VelaShell.PluginSdk.Testing` 替身) |
| `build/PluginBundle.proj` | 批量出 `.vpx`;并把可分发插件收成 `plugins/` 布局(只用于 CI 体检) |
| `scripts/Set-Version.ps1` | 把发行版本号写进仓库里所有落点(发版时由流水线自动跑) |
| `Directory.Packages.props` | **中央包管理**:所有 NuGet 版本只在这一处 |

## 开发

```bash
dotnet build VelaShell.Plugins.slnx
dotnet test  VelaShell.Plugins.slnx -c Debug
```

本仓库不出 NuGet 包、不做强名称签名(那是 SDK 仓库的事),本地构建不需要任何密钥,
`-c Debug` 也只是跑测试的习惯而非硬性要求。唯一用到密钥的地方是给 `.vpx` 签名
(见下面的[签名密钥](#签名密钥)),不传就是未签名包,本地开发够用。

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

### 要用 SDK 里还没发布的契约

等 SDK 发版。SDK 的版本是 `Directory.Packages.props` 里 `VelaShell.PluginSdk.Build` 与
`VelaShell.PluginSdk.Testing` 两行的字面量(同一个号),包只从 nuget.org 来 —— 本仓库没有
`nuget.config`、没有本地源,也没有能用 `-p:` 覆盖的 SDK 版本属性。包发布之后把这两行一起抬上去；
目标宿主的 Avalonia/AvaloniaEdit 版本则由本仓库的 `VelaAvaloniaVersion` /
`VelaAvaloniaEditVersion` 与中央包版本共同维护。不要在 sdk 仓库 `dotnet pack` 本地包来凑
(本地包未签名、版本号也不归下游定,见 sdk 仓库的 AGENTS.md)。

## 版本号:一趟统一列车

本仓库的插件**同上同下** —— 一次 Release,所有插件都是那个版本号。Release 标签是唯一
的输入,`scripts/Set-Version.ps1` 把它写进三处:

| 落点 | 决定什么 |
| --- | --- |
| `Directory.Build.props` 的 `VelaPluginsVersion` | 程序集版本 |
| `README.md` 版本横幅 | 给人看的 |
| 各 `plugins/*/plugin.json` 的 `version` | **`.vpx` 文件名与宿主里显示的插件版本** |

最后那一处最容易漏:打包器出的是 `<id>-<plugin.json 的 version>.vpx`,与 MSBuild 那边的
`VelaPluginsVersion` **毫无关系** —— 只改前者的话,发 1.4.0 出来的仍旧叫
`velashell.redis-0.1.0.vpx`。脚本会动态枚举 `plugins/` 下的每个 `plugin.json`,
新增插件时不用回来改它。

统一列车的代价是没改过的插件也跟着涨版本,用户那边会看到一次"更新";换来的是
"这台机器上装的是哪一批插件"只有一个答案 —— 排查问题时不必逐个去问版本。

这个号只回答"这一批插件是哪次发布出去的";发出去的资产是每个插件各自的 `.vpx`,
不再有整批打包的 zip。

它与 SDK 版本(`VelaSdkVersion`)是两回事:SDK 发 1.5.0 不代表插件必须跟着发,
插件发 1.4.1 也不代表契约动了。

## 发布

**在 GitHub 上发布 Release**(标签形如 `v1.4.0`),流水线会:

1. 把版本号写进仓库(`scripts/Set-Version.ps1`),因此产物永远与标签一致;
2. 全量测试 + 构建;
3. 打包并**逐个核对签名**(未签名或签名不自洽就直接失败,不会挂上去);
4. 产出并挂到该 Release 的下载列表:
   - **每个插件一份 `.vpx`(已签名)** —— 从 Release 下载后即可手工安装;
   - `SHA256SUMS.txt`;
5. 把同一批 `.vpx` **推进插件商店**(`publish-market` 任务,见下文「推进插件商店」);
6. 开一个 `chore/version-<版本>` 的 PR 把版本号回写 `main`,等你手动合。

### 推进插件商店

`publish-market` 任务用商店的**可信发布**:不存任何商店密钥,由 GitHub 为这次运行签发 OIDC 令牌,
商店核对令牌来自本仓库的 `release.yml`、环境 `market`。包推进去后照常进隔离区检测,通过后自动发布。
做法与第三方插件作者一样,见 velashell-docs 的
[打包与发布 §5.4](https://github.com/VelaShell/velashell-docs/blob/main/zh/templates/publishing.md)。

启用(一次性):

1. 每个插件的**首个版本**先登录商店手动上传(可信发布不认领新 id);
2. 商店「我的插件 → 可信发布」登记:仓库 `VelaShell/velashell-plugins`、工作流 `release.yml`、环境 `market`,
   「同时登记到」勾上全部插件;
3. 仓库 Settings → Environments → `market` → Deployment branches and tags 选 Selected branches and tags,
   添加**标签**规则 `[0-9]*.[0-9]*.[0-9]*`(本仓库的发版标签是不带 `v` 的 `2.1.9` 这种;写成 `v*` 会把所有发版都挡住);
4. 仓库变量 `MARKET_TRUSTED_PUBLISHING` 设为 `true`。没设时该任务直接跳过,Release 资产不受影响。
   可选变量 `MARKET_URL`(默认 `https://market.easilynet.top`)、`MARKET_OIDC_AUDIENCE`(默认 `velashell-market`)。

新增插件时重复第 1、2 步。重跑同一标签是安全的:已发布的版本商店返回 409,任务当成功处理。

本地也可以先跑一遍:

```powershell
pwsh scripts/Set-Version.ps1 1.5.0            # 落盘
pwsh scripts/Set-Version.ps1 1.5.0 -Check     # 只报告(CI 每次 push/PR 都跑这个)
```

### 签名密钥

`.vpx` 一律签名 —— 它是用户手工安装与插件商店分发的形态,没有签名,"这个包确实来自我们"
就无从谈起。密钥是 `vela-plugin keygen` 生成的 P-256 PKCS#8 PEM 私钥。

仓库机密 **`KEY_PEM_FILE`** 里存这份 PEM 的 **base64**。生成它(PowerShell,直接进剪贴板,
不落文件也不进终端回滚):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\Users\Joe\OneDrive\文件\密码\velashell.pem')) | Set-Clipboard
```

然后 GitHub → Settings → Secrets and variables → Actions → New repository secret,
名字填 `KEY_PEM_FILE`,内容直接粘贴(是一整行,没有换行)。

粘之前想确认没贴错,可以就地验一次往返 —— 解出来的第一行应当是 `-----BEGIN PRIVATE KEY-----`:

```powershell
[Text.Encoding]::ASCII.GetString([Convert]::FromBase64String((Get-Clipboard))).Split("`n")[0]
```

> ⚠️ 别用 `certutil -encode`:它产出的是带页眉页脚与换行的 PEM 式 base64,
> 流水线里的 `[Convert]::FromBase64String` 读不了。要的是**裸 base64,单行**。

缺这个机密时发布会**直接失败**并给出这段说明 —— 未签名的 `.vpx` 与签过的在文件名、大小、
构建日志上都看不出区别,不能让它悄悄发出去。CI(`ci.yml`)则是**可选**:仓库自己的
push/PR 拿得到机密就顺带签一次,验证签名链路;来自 fork 的 PR 拿不到,不签名照常跑。

本地想出签名包:

```bash
dotnet build build/PluginBundle.proj -c Release -t:PackAllVpx -p:VelaSigningKey=<你的 key.pem>
```

不传 `-p:VelaSigningKey` 就是未签名包,本地开发够用。

## 与宿主的硬约束

**Avalonia 版本必须与宿主一致**。版本号由目标宿主发布线拥有：本仓库的
`VelaAvaloniaVersion` 与 `VelaAvaloniaEditVersion` 分别声明 `12.2.0` 与 `12.0.0`，
并由 `Directory.Build.targets` 同时核对测试宿主的中央包版本和项目解析出的实际引用。
构建 UI 的插件必须直接引用 `Avalonia`（以及需要时的 `Avalonia.AvaloniaEdit`），并设置
`ExcludeAssets="runtime"`；宿主提供运行时程序集，插件只携带编译与 AXAML 资产。
版本漂移会在构建期报 `VELAP1000`/`VELAP1001`，而不是等用户装上插件才炸。

## 许可

AGPL-3.0-only,与主仓库一致。商业授权见主仓库的 `LICENSE-COMMERCIAL.md`。
