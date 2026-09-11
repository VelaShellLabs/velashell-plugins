# plugins/ —— 第一方插件

本目录存放官方维护的插件,每个插件一个子目录(独立 csproj)。
SDK 契约与开发文档在工具链仓库
[VelaShellLabs/velashell-plugin-cli](https://github.com/VelaShellLabs/velashell-plugin-cli)
([开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)、[SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md))。

## 现有插件

| 目录 | id | 随包分发 | 装载模式 | 说明 |
| --- | --- | --- | --- | --- |
| [VelaShell.Plugin.DockerPanel](VelaShell.Plugin.DockerPanel/) | `velashell.dockerpanel` | 是 | 进程内 | 远端 Docker 管理面板:容器 / 镜像 / 卷 / 网络 / Compose,含实时统计、日志、容器内文件编辑与内置终端 |
| [VelaShell.Plugin.Redis](VelaShell.Plugin.Redis/) | `velashell.redis` | 是 | 进程内 | Redis 客户端:键浏览、类型化查看与编辑、命令执行 |
| [VelaShell.Plugin.S3](VelaShell.Plugin.S3/) | `velashell.s3` | 是 | 进程内 | S3 兼容对象存储:协议 + 桶管理器 + 对象检视器(协议能力域的首个使用者) |
| [VelaShell.Plugin.Serial](VelaShell.Plugin.Serial/) | `velashell.serial` | 是 | 进程内 | RS-232 / USB 转串口终端:端口热插拔枚举、换行归一化、发送节流、Break 与 DTR/RTS |
| [VelaShell.Plugin.Telnet](VelaShell.Plugin.Telnet/) | `velashell.telnet` | 是 | 进程内 | RFC 854 Telnet 终端:选项协商 + NAWS + 8 位透明(**终端**协议能力的首个使用者) |

装载模式由 `plugin.json` 的 `hostMode` 决定(`isolated` / `inProcess`,默认进程内)。
隔离插件跑在独立的 `VelaShell.PluginHost` 进程里(实现在主仓库),崩溃不波及宿主;
S3 与 Redis 因为**协议能力只在进程内可用**必须进程内装载 —— 协议是宿主反向调用插件的
高频通道,隔离进程的 RPC 只承载插件→宿主方向(清单校验会直接拒绝 protocols + isolated 的组合);
Telnet 与串口同理。Docker 面板的理由是另一条:它要把一个原生 Avalonia 控件挂进主窗口标签区
(控件无法跨进程嵌入),而且 `IRemoteTunnelApi` 交给它的是一条**活的 `Stream`**,
隔离进程里明确不可用。

> **Docker 面板要 SDK ≥ 2.0.0**(`plugin.json` 里钉了 `minSdkVersion`)。它说的是
> Docker Engine 的 HTTP API,而这条 API 的载体是远端的一个 unix socket ——
> 要的是到那个 socket 的**裸字节双工流**(`IRemoteTunnelApi`)。SDK 1.1 的远程执行只有
> "整段 UTF-8 解码"与"按 `\n` 切行"两种**文本**形态,承载不了分块传输、tar 归档流与
> `exec` 的多路复用帧:UTF-8 解码把非法字节换成 U+FFFD(不可逆),按行切分在 `0x0A`
> 处把一帧劈成两半 —— 那不是慢一点,是**数据静默损坏**。`apiLevel` 表达不了这一档
> (它只在**破坏性**变更时才动),所以另钉 `minSdkVersion`。
>
> 它也是本仓库**唯一直接引 Avalonia.\* 包**的插件(`Avalonia.AvaloniaEdit`,用于
> compose.yaml / .env 的语法高亮)。这一条有个反直觉的后果,见下一节最后那段。

> **串口插件要 SDK ≥ 1.5.0**。它是连接表单三件新面的驱动者与首个使用者:
> `ProtocolFeatures.NoEndpoint`(收起端口栏)、`ProtocolSettingKind.DynamicChoice` +
> `IProtocolChoiceSource`(候选项在表单打开时现取 —— USB 转串口是热插拔设备)、
> 以及 `AllowsCustomValue` / `HostKind`(可编辑下拉;主机那一栏也能做成下拉)。
> 它的 `plugin.json` 里因此有一条 `minSdkVersion`:老宿主上这些成员根本不存在,
> 不声明就是运行期 `MissingMethodException`。

> **AI 插件不在这里**:`velashell.ai` 住在主仓库 [joesdu/VelaShell](https://github.com/joesdu/VelaShell)
> 的 `plugins/` 下,随主程序同仓构建、同版发布。理由见那边的 `plugins/README.md` ——
> 它借宿主的 AvaloniaEdit 作输入框(隔离进程里没有这个程序集),因此只能进程内装载,
> 而进程内装载又要求它编译时引用的 Avalonia 与宿主逐字同版;加上面板要跟着宿主的主题、
> 语言、字体走,UI 改动几乎每次都同时落在两侧。分仓的话每改一行 UI 都要"发一次 Release
> → 回主仓库抬 pin → 才看得到效果"。

## 每个插件的 csproj 长什么样

拆库之后,第一方插件与第三方插件走**完全同一条路径**:

```xml
<ItemGroup>
  <PackageReference Include="VelaShell.PluginSdk.Build" />
</ItemGroup>
```

就这一行 —— 契约程序集、与宿主版本一致的 Avalonia(含 AXAML 编译器)、`plugin.json`
进输出目录、清单编译期校验、`dotnet build -t:PackVpx`,全都随这个包到位。
版本号不写在这里:本仓库开了**中央包管理**,所有 NuGet 版本集中在根
[`Directory.Packages.props`](../Directory.Packages.props)。

**不要**在插件里声明 `Avalonia` 的 `PackageReference`:SDK 包已经用精确区间
`[x.y.z]` 锁死了它(必须与宿主一致,否则跨 ALC 的控件类型对不上),自己再写一条
只会引来版本漂移。同理也不要写 `ExcludeAssets="runtime"` —— SDK 包的
`VelaExcludeSharedRuntimeAssets` 已经按装载器的判定口径(`VelaShell.PluginSdk`
与 `Avalonia*` 前缀)把共享程序集的运行时资产排掉了。

### 例外:确实需要某个 `Avalonia.*` 包时,`ExcludeAssets="runtime"` 必须自己写

上一段那条"不要写 `ExcludeAssets`"的前提是**插件不直接引 Avalonia 包** ——
前四个插件都不引,SDK 包处理它自己那条引用就够了。

DockerPanel 要 `Avalonia.AvaloniaEdit` 做语法高亮,于是撞上了另一面:
`VelaExcludeSharedRuntimeAssets` 排得掉 `Avalonia.AvaloniaEdit` 自己的运行时资产,
**排不掉它带进来的传递依赖**。去掉那条 `ExcludeAssets`,
`AvaloniaEdit → Avalonia → MicroCom.Runtime` 里的 `MicroCom.Runtime.dll`
就会落进插件目录(2026-09-11 并库时实测,构建 0 错 0 警告,包照常打出来)。

它不以 `Avalonia` 打头,因此**两道防线同时漏掉它**:装载器的共享前缀判定不认它
(插件会加载自己那一份,与宿主的 Avalonia COM 互操作分属两个类型标识),
CI 那条泄漏体检原本也只认 `Avalonia*` 与 `VelaShell.PluginSdk.dll`。
体检已经把 `MicroCom.Runtime.dll` 补进去了,但**规矩仍然是显式写**:

```xml
<PackageReference Include="Avalonia.AvaloniaEdit" ExcludeAssets="runtime" />
```

本目录的 `Directory.Build.props/targets` 只额外做三件仓库自己的事:
`VelaPluginShip`(是否随应用分发)、构建后镜像到 `artifacts/plugins/<目录名>/`
与本机宿主、以及发布期的 `GetVelaPluginPayload`。

## 分发

"随包分发"由 csproj 的 `<VelaPluginShip>` 控制(默认 `true`)。设成 `false` 的插件
本机构建仍会镜像到 `artifacts/plugins/`(以及 `VELASHELL_DEV_APP_DIR` 指定的应用目录),
装载起来验证插件系统没问题,但它不会被收进分发布局 —— 给开发者读的范例用这一档。
(当前五个插件都是 `true`;示例插件 HelloWorld 已于 2026-08 移除。)

[`build/PluginBundle.proj`](../build/PluginBundle.proj) 的 `Bundle` 目标把 `VelaPluginShip=true`
的插件收成安装包 `plugins/` 那一层的布局:它不再作为 Release 资产上传,只在 CI 与发布流水线里
用来体检布局(尤其是共享程序集有没有漏进去)。一个可分发插件都收不到时直接失败,
不会悄悄放过一个空布局。发出去的是每个插件各自的 `.vpx`。

## 版本号:别手改 plugin.json 的 version

本仓库是一趟**统一发布列车** —— 一次 Release,所有插件同上一个版本号,由
[`scripts/Set-Version.ps1`](../scripts/Set-Version.ps1) 从 Release 标签写进
`Directory.Build.props`、README 横幅,以及**每个 `plugin.json` 的 `version`**。

所以新增插件时 `plugin.json` 里那个 `version` 填什么都行(填 `0.1.0` 即可),
下一次发版会被覆盖掉;**别为了"发个新版 Redis"去手改它** —— 改了也只会在下次发版时
被标签里的版本盖回去,徒增一次无意义的 diff。

要点在于:`.vpx` 的文件名是 `<id>-<plugin.json 的 version>.vpx`,
与 MSBuild 的 `VelaPluginsVersion` 毫无关系。两处必须一起写,只写一处就会出现
"发了 1.4.0,包却叫 velashell.redis-0.1.0.vpx"。

## 新建插件

1. 挑一个形态最接近的现有插件复制成新目录(终端类看 Telnet,面板类看 DockerPanel),
   改 csproj 中的 `<VelaPluginId>` 与 `plugin.json`;
2. 新依赖的版本加进根 `Directory.Packages.props`(中央包管理,csproj 里不写 `Version=`);
3. 把项目与它的测试工程加入 `VelaShell.Plugins.slnx` 的 `/plugins/`、`/tests/` 文件夹;
4. `dotnet build plugins/VelaShell.Plugin.<名字>` —— 输出自动镜像到
   `artifacts/plugins/<目录名>/`;想让本机 VelaShell 直接装载,构建前设
   `VELASHELL_DEV_APP_DIR` 指向应用目录。
   目录名 = 插件 id 把点换成短横(`velashell.ai` → `velashell-ai`):macOS 的 `codesign`
   会把 `.app` 内带点号的目录当成嵌套 bundle 而签名失败。目录名不参与任何逻辑,
   宿主是枚举子目录后从 `plugin.json` 读 id。

写自己的插件(不进本仓库)不必复制目录 —— 用模板更快:

```bash
dotnet new install VelaShell.Plugin.Templates
dotnet new velaplugin-ui -n MyPlugin --publisher acme --authorName "Your Name"
```
