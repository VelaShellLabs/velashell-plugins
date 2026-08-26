# plugins/ —— 第一方插件

本目录存放官方维护的插件,每个插件一个子目录(独立 csproj)。
SDK 契约与开发文档在工具链仓库
[joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain)
(`docs/dev-guide.md`、`docs/sdk-reference.md`)。

## 现有插件

| 目录 | id | 随包分发 | 装载模式 | 说明 |
| --- | --- | --- | --- | --- |
| [VelaShell.Plugin.HelloWorld](VelaShell.Plugin.HelloWorld/) | `velashell.hello-world` | 否 | 隔离进程 | 官方示例:SDK 各能力的最小用法 |
| [VelaShell.Plugin.Redis](VelaShell.Plugin.Redis/) | `velashell.redis` | 是 | 进程内 | Redis 客户端:键浏览、类型化查看与编辑、命令执行 |
| [VelaShell.Plugin.S3](VelaShell.Plugin.S3/) | `velashell.s3` | 是 | 进程内 | S3 兼容对象存储:协议 + 桶管理器 + 对象检视器(协议能力域的首个使用者) |
| [VelaShell.Plugin.Serial](VelaShell.Plugin.Serial/) | `velashell.serial` | 是 | 进程内 | RS-232 / USB 转串口终端:端口热插拔枚举、换行归一化、发送节流、Break 与 DTR/RTS |
| [VelaShell.Plugin.Telnet](VelaShell.Plugin.Telnet/) | `velashell.telnet` | 是 | 进程内 | RFC 854 Telnet 终端:选项协商 + NAWS + 8 位透明(**终端**协议能力的首个使用者) |

装载模式由 `plugin.json` 的 `hostMode` 决定(`isolated` / `inProcess`,默认进程内)。
隔离插件跑在独立的 `VelaShell.PluginHost` 进程里(实现在主仓库),崩溃不波及宿主;
S3 与 Redis 因为**协议能力只在进程内可用**必须进程内装载 —— 协议是宿主反向调用插件的
高频通道,隔离进程的 RPC 只承载插件→宿主方向(清单校验会直接拒绝 protocols + isolated 的组合);
Telnet 与串口同理。

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

本目录的 `Directory.Build.props/targets` 只额外做三件仓库自己的事:
`VelaPluginShip`(是否随应用分发)、构建后镜像到 `artifacts/plugins/<目录名>/`
与本机宿主、以及发布期的 `GetVelaPluginPayload`。

## 分发

"随包分发"由 csproj 的 `<VelaPluginShip>` 控制(默认 `true`)。示例插件设 `false`:
本机构建仍会镜像到 `artifacts/plugins/`(以及 `VELASHELL_DEV_APP_DIR` 指定的应用目录),
装载起来验证插件系统没问题,但它不会被收进分发布局 ——
它是给开发者读的范例,不是给用户装的功能。

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

## 规划中(尚未创建)

- **串口插件**(`velashell.serial`):与 Telnet 同为终端协议能力的使用者;
  依赖 `System.IO.Ports`,要处理三平台端口枚举与 `Close()` 死锁。
  连接对话框里的「串口」页签在它落地前保持禁用占位。
- **容器管理插件**:基于远程执行能力封装 docker/podman 常用操作
  (已有独立仓库原型 `joesdu/VelaShell.Plugin.DockerPanel`,尚未并入本仓库)。

## 新建插件

1. 复制 `VelaShell.Plugin.HelloWorld/` 为新目录,改 csproj 中的 `<VelaPluginId>` 与 `plugin.json`;
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
