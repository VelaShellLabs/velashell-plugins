# VelaShell RDP 插件

在 VelaShell 标签页内连接和操作 Windows 远程桌面。插件使用 SDK 的工作台能力（`IWorkspaceProvider`），复用宿主的连接配置、加密凭据、会话树、标签页及证书信任提示，不调用外部 `mstsc`，也不嵌入 Windows ActiveX。

| 项目 | 说明 |
| --- | --- |
| 插件 ID / 连接类型 | `velashell.rdp` |
| 装载模式 | `inProcess`，工作台控件必须与宿主进程共享 Avalonia |
| 激活时机 | `onWorkspace:velashell.rdp`，选中连接页签或打开连接时惰性激活 |
| 默认端口 | `3389` |
| 最低 SDK | `2.0.4`；实际构建依赖见根目录 [Directory.Packages.props](../../Directory.Packages.props) |
| RDP 引擎 | 正式 NuGet `Devolutions.IronRdp 2026.10.19` |
| 分发资产 | Windows、macOS、Linux 的 x64 / arm64 原生库 |
| 插件许可 | MIT，见 [Licenses/VelaShell-LICENSE](Licenses/VelaShell-LICENSE) |

> 三个平台的原生库随插件分发，不等于三平台运行均已验收。目前验证集中在 Windows x64 的原生调用、SDK 装载、环回协议测试和 Headless 界面测试；真实 Windows 桌面整链及其他平台仍需实机验证。

## 功能

- 在宿主标签页中显示远程桌面，支持适应窗口和物理像素 1:1 本地缩放。
- 配置主机、端口、域、账号密码、初始桌面分辨率与连接超时；高级设置可显式启用文本剪贴板与只读 Windows 盘符映射。
- 通过 TLS + NLA/CredSSP 认证；当前使用密码 NTLM，不提供 Kerberos、SSO 或智能卡认证。
- 物理键盘扫描码、重复按键、左右修饰键、扩展键、鼠标拖动、双轴滚轮与 X1/X2 按钮。
- 通过工具栏发送 Ctrl+Alt+Del，主动断开或显式重连。
- 失焦、鼠标捕获丢失、窗口停用及断线时释放输入状态。
- 复用宿主的 SSH 跳板会话与本地端口转发。
- 跟随宿主主题及语言切换，更新描述符时保留同一个 provider，不关闭现有文档。

当前**不包含**音频、打印机重定向、图片/文件剪贴板、RD Gateway、RemoteApp、多显示器、远端动态分辨率及 IME/Unicode 输入。本地缩放不会改变远端桌面分辨率。文本剪贴板与静态磁盘映射需要下述原生扩展，尚不等同于 mstsc 完整功能。

### 重定向扩展进度（2026-10-29）

已接入固定版本 IronRDP 原生扩展：

- Windows x64 静态 RDPDR 初始盘映射：配置格式为 `D:\;E:\`，默认关闭、默认只读；只接受完整逻辑盘根，不接受文件夹、UNC 或动态热插拔。Rust 后端保留根句柄、执行相对路径约束和只读检查；网络会话泵每 50ms 排出延迟文件 I/O 完成。
- 文本 CLIPRDR：只在远程桌面控件取得焦点时同步文本；Ctrl+V 的按键包会先等待本地剪贴板公告 ACK，再发送给远端。远程文本回写只在焦点仍属于当前会话时执行，失焦、窗口停用、断线和重新挂载会使旧回调失效。图片、文件剪贴板仍不支持。
- 原生补丁和 `native/Build-Native.ps1` 固定到 IronRDP 提交 `1f69e124e6bf85608efce3495c12eaf5f6b5a95d`；Windows x64 Release 测试库 SHA-256 为 `100EC7E145C4CEFD77D5311B972248BC5C28D351954B751EFDB2DADABBBA8B64`。构建脚本要求新临时目录，不覆盖已有源码。

协议状态机仍保留 2 MiB 文本、64 KiB 控制/格式列表、1024 格式及 12 条出站 FIFO 限制；溢出显式故障并清零队列。

本次正确性回归为 **152/152 通过，零失败/跳过**（含状态机、设置、原生 ABI 和 Headless Ctrl+V 顺序、失焦及重复文本用例，不含手动性能分类），关闭 runtime-async 的非增量构建为零警告、零错误。原生静态通道测试 **10/10 通过**，含 16 MiB 分片重组边界用例。原生 Rust Release 构建保留一条上游 `unfulfilled_lint_expectations` 警告；这不影响插件托管构建，但仍需上游自行修正。真实远端 Windows 桌面、磁盘 I/O、剪贴板互操作和吞吐仍未验收。

## 安装与连接

1. 在 VelaShell 的插件管理页选择“安装 .vpx”，安装构建或发布得到的插件包。
2. 新建连接，选择 **RDP** 页签，填写 Windows 主机、端口、用户名和密码。
3. 需要时填写域，在“高级”页调整初始尺寸、超时和本地缩放。
4. 点击连接。证书未通过系统校验时，先核实宿主提示中的目标、证书和 SHA-256 指纹，再决定是否信任。
5. 桌面打开后，点击画面取得键盘焦点；通过工具栏切换缩放、发送 Ctrl+Alt+Del、断开或重连。

远端需要已启用 RDP、允许该账号远程登录，并能够通过配置的网络路径访问。仅支持不需要降级的 TLS + NLA 连接；不自动更改服务器或本机安全策略。

本地未提供签名密钥时生成的是**未签名开发包**。安装时的签名提示与是否信任发布者由宿主处理，不要把“能安装”理解为来源已经验证。

### 账号格式

| 形式 | 用户名字段 | 域字段 |
| --- | --- | --- |
| 独立域字段 | `user` | `DOMAIN` |
| 域前缀 | `DOMAIN\user` | 留空，或填写相同的 `DOMAIN` |
| UPN | `user@domain.example` | 必须留空 |
| Windows 本地账户 | `user` | Windows 计算机名 |

冲突的域写法会作为配置错误拒绝，而不是反复弹登录框。`.` 域的真实互操作尚未验证；插件不会根据 IP 或 FQDN 猜测 Windows NetBIOS 计算机名。接受域/UPN 语法也不代表支持 Kerberos 或保证特定服务器策略下能通过 NTLM 登录。

### 连接表单

| 字段 / 设置键 | 默认值 | 说明 |
| --- | --- | --- |
| 主机 | — | Windows 主机名或 IP；端口单独填写 |
| 端口 | `3389` | 范围 1–65535 |
| 用户名 / 密码 | — | 复用宿主凭据流程，不另存一套插件连接库 |
| 域 `domain` | 空 | 可选，账号格式见上表 |
| SSH 跳板 `jumpSession` | 空（直连） | 选择宿主已保存的 SSH 配置 |
| 桌面宽度 `desktopWidth` | `1280` | 高级，范围 320–7680 像素 |
| 桌面高度 `desktopHeight` | `720` | 高级，范围 200–4320 像素 |
| 本地缩放 `scaleMode` | `fit` | 高级；`fit` 适应窗口，`actual` 为物理像素 1:1，可滚动查看 |
| 连接超时 `connectTimeout` | `15000` | 高级，1000–120000 毫秒，覆盖连接握手与首次激活 |
| 已信任指纹 `trustedThumbprint` | 空 | 隐藏字段，由宿主确认后保存 SHA-256 指纹，不是跳过校验开关 |
| `redirectClipboard` | `false` | 高级；仅焦点窗口的文本剪贴板，要求原生扩展 |
| `redirectDrives` | 空 | 高级；完整盘符根列表，如 `D:\;E:\`，空值关闭 |
| `drivesReadOnly` | `true` | 高级；共享磁盘默认只读 |

高分辨率会增加原生解码、双缓冲与界面位图的内存占用。界面将托管帧直接复制到锁定的位图，不保留额外的 UI 全帧数组；两份托管帧与一份位图在 1080p / 4K 下分别约占 23.73 / 94.92 MiB（RGBA32，不含对齐、原生图像、图形后端及重分配峰值）。插件限制协商尺寸与单帧大小，不是整个进程或多会话总内存的硬上限，也不能完全约束第三方原生解码器的内部峰值。

视图移除或尚未创建时继续处理远端协议，但不生成托管画面快照；重新挂载视图会请求最新完整画面。像素变化不重复刷新工具栏或触发布局测量。会话自然终止后自动释放协议与帧缓冲资源，不必等用户关闭故障标签页；已显示的最后画面可保留至更换会话或移除视图。

这些优化不等于零拷贝或视频硬解码。当前 IronRDP 绑定未配置现代 bitmap codec / RDPGFX，不提供 FPS 或 GPU 解码开关；宿主 GPU 合成也不等于 RDP 硬解。CPU 像素路径的 synthetic 测量不能证明真实远端的稳定 60 / 120 FPS。

### SSH 跳板

选择“经 SSH 会话抵达”后，由宿主建立 SSH 连接及本地转发。插件只连接请求中的本地端点，但 TLS 主机名和 NLA 身份使用 `Tunnel.TargetHost` 的真实 Windows 目标；不会将回环地址当作服务器身份。

SSH 凭据、跳板指纹与隧道关闭均由宿主负责，插件不重复实现 SSH。该功能是 TCP 转发，不是 RD Gateway。

## 证书、安全与凭据

### 首次信任

未保存指纹时，证书链及主机名通过系统校验才会直接接受。验证失败时，插件先拒绝握手，再抛出 `ProtocolCertificateTrustException`，由宿主展示证书信息；用户确认后将 SHA-256 指纹写入连接配置并重新连接。

已保存指纹时必须匹配该证书，证书变化即使通过系统校验也需要重新核实。插件不会无条件接受证书、静默更新指纹、降级到 legacy RDP/TLS-only，或自动跟随服务器重定向。

- **首次打开连接**：可进入宿主的证书确认流程。
- **“测试连接”**：当前测试路径不提供证书确认交互。遇到未信任证书时，应通过正常连接流程核实，而不是关闭验证。
- **标签页重连**：若证书需要确认或已经变化，关闭标签页，再从连接配置重新打开核实。
- **Visual Studio 在异常处暂停**：开启“引发时中断”时会先停在 `ProtocolCertificateTrustException`。继续执行后宿主才能显示确认框；需要时只取消该异常的“引发时中断”，不要关闭全部 CLR 异常检查。

信任前通过服务器端或其他可信渠道核对指纹。不能仅因为目标使用 RDP、证书是自签或提示挡住了登录就直接接受。

### 数据与运行边界

- 凭据持久化复用宿主；插件仅为当前会话和显式重连保留最小内存副本，不保留整个含密码的连接请求。
- 密码不写普通 Storage、日志、环境变量、命令行或界面模型。可控口令缓冲在释放时清零，但不可变托管字符串与第三方原生副本不能保证可靠擦除。
- 断开或关闭标签页**不是注销 Windows 用户**。“测试连接”也会实际握手登录后断开，可能创建或附加远程 Windows 会话，不能保证远端无副作用。
- 插件运行于宿主进程，第三方原生错误可能影响宿主，不提供进程级崩溃隔离。
- 宿主和操作系统保留键优先。Ctrl+N、Ctrl+W、Ctrl+Tab 等宿主组合可能不会传给远端，可在宿主设置中改键或解绑；Ctrl+Alt+Del 使用专用按钮。
- 输入队列过载时清理待发送输入并释放按键，当前会话禁用后续输入，显式重连后恢复；不会静默继续半个组合键。
- 服务器光标形状可显示，但不会跨平台移动操作系统指针位置。

## 实现结构与维护注意

| 文件 / 目录 | 职责 |
| --- | --- |
| [RdpPlugin.cs](RdpPlugin.cs) | 惰性注册、描述符与语言切换 |
| [RdpSettings.cs](RdpSettings.cs) | 声明式字段、范围校验与账号语法解析 |
| [RdpWorkspaceProvider.cs](RdpWorkspaceProvider.cs) | 首次连接、异常本地化、停用和正在打开的会话清理 |
| [RdpWorkspaceDocument.cs](RdpWorkspaceDocument.cs) | 文档状态、无视图释放、断开/重连与旧代事件隔离 |
| [Engine/](Engine/) | TLS/NLA 握手、协议帧、串行会话、托管画面双缓冲及原生对象所有权 |
| [Ui/](Ui/) | Avalonia 画布、缩放与坐标、键鼠输入、主题和本地化 |
| [测试工程](../../tests/VelaShell.Plugin.Rdp.Tests/) | 原生加载、环回安全、生命周期、帧缓冲、Headless 与打包回归 |

维护时不要破坏以下约束：

1. `OpenAsync` 返回前必须完成首次桌面激活。宿主“测试”会执行 Open→Dispose，不能依赖 `CreateView()` 才建连或清理。
2. 网络读取只有一个拥有者，原生状态与网络写串行处理；FFI 借用不跨 `await`，资源按所有权确定性释放。
3. UI 通过同步租约将托管像素直接复制到锁定的位图，目标宽高、stride 和容量必须与租约匹配。发布和尺寸变化不等待 UI，单待处理通知与有界双缓冲防止积压；退休缓冲在消费者归还时清零。无视图时跳过快照，控制唤醒独立于输入队列；协议泵退出后才释放原生对象与帧缓冲，不能自等待或在写入期间清零。
4. **连接表单集合必须以 SDK 元素数组为目标类型。** `IReadOnlyList<T>` 目标上的集合表达式可能生成插件程序集内的包装类型，宿主保留表单便会阻止插件 ALC 回收。保留 `RdpSettings.Declare` 的数组返回类型及 `Choices` 的数组目标转换；保留表单的卸载测试覆盖此问题。
5. 语言切换重注册沿用同一个 provider，不能为换文案替换实现并关闭已有文档。

## 构建、测试与打包

在插件仓库根目录执行，使用仓库 `global.json` 对应的 .NET SDK：

```bash
dotnet build plugins/VelaShell.Plugin.Rdp -c Debug
dotnet test tests/VelaShell.Plugin.Rdp.Tests -c Debug

# 关闭 runtime-async 做一次严格编译，检查 ref struct / Span 跨 await 问题
dotnet build tests/VelaShell.Plugin.Rdp.Tests -c Debug --no-incremental -p:Features=runtime-async=off

# 只检查 CA1068，不应用格式修改
dotnet format analyzers plugins/VelaShell.Plugin.Rdp/VelaShell.Plugin.Rdp.csproj --verify-no-changes --diagnostics CA1068 --severity info --no-restore

# 本地开发包；不传签名密钥即未签名
dotnet build plugins/VelaShell.Plugin.Rdp -c Release -t:PackVpx
```

产物为 `bin/vpx/velashell.rdp-<plugin.json 中的版本>.vpx`。版本由仓库统一发布列车管理，不为单个插件手改版本号。发布签名使用已有打包流程的 `VelaSigningKey` 参数；本文不创建密钥或发布包。

需要宿主开发内环时，可使用独立 `--data-root` 和 `--dev-root` 挂载插件输出，不要覆盖日常安装和连接配置。构建会镜像插件到仓库 `artifacts/plugins/velashell-rdp/`；设置了 `VELASHELL_DEV_APP_DIR` 时还会复制到指定宿主，运行验证前应确认其值。

### 手动 CPU 像素路径测量

在设置 `VELASHELL_RDP_PIXEL_BENCHMARK=1` 的当前 shell 中，执行：

```bash
dotnet test tests/VelaShell.Plugin.Rdp.Tests -c Debug --no-restore -p:VelaDevAppDir= -p:VELASHELL_DEV_APP_DIR= --filter TestCategory=ManualPerformance --logger "console;verbosity=detailed"
```

未启用时手动用例会报告未执行。测量使用非托管源/目标替身和生产双缓冲，比较明确标识为 reconstructed 的旧中转参考路径与生产直接位图复制；重建参考不能替代修改前保存的旧生产测量。输出包含复制字节、稳态线程分配与 CPU 吞吐。它不运行真实远端解码、Avalonia 位图锁、GPU 上传或屏幕呈现，不是 FPS 基准。常规正确性回归可使用 `--filter TestCategory!=ManualPerformance`，避免将未启用的手动测量混入通过数。

2026-10-27 在 Windows x64 / Debug / .NET 11 RC3 下，每种分辨率预热 32 次并测量 5×128 次，实际旧生产路径与新生产路径的 128 次耗时中位数如下。源与位图均为非托管替身，未测进程内存或真实呈现。

| 分辨率 | 修改前旧生产路径 | 新直接复制路径 |
| --- | ---: | ---: |
| 1280×720 | 80.608 ms | 58.501 ms |
| 1920×1080 | 354.277 ms | 141.477 ms |
| 3840×2160 | 1512.052 ms | 718.298 ms |

前后稳态线程分配均为 72 B/次（包括帧租约对象），不是零分配；复制载荷由三帧降为两帧，4K UI 中转容量减少 31.64 MiB。前后采样非交错且存在机器波动，不能将这些耗时比值推广为真实 RDP 提速倍数或稳定 FPS 保证。

### 原生依赖分发

插件托管桥仍从正式 NuGet 恢复，不引用 SDK 源码项目，也不创建本地 SDK 包。原生重定向扩展单独从固定上游提交构建；`native/artifacts` 是忽略入库的本地产物，缺少它时仍复制 NuGet 的原始 Windows x64 库，开启重定向会明确失败，而不是假装可用。其他五个 RID 当前仍使用 NuGet 原始库，不具备这些新增扩展。

Windows x64 构建扩展需要 Rust MSVC 工具链和 Visual C++ 构建工具：

```powershell
# WorkRoot 必须尚不存在，父目录须存在；默认 Release，使用 --locked 依赖。
./plugins/VelaShell.Plugin.Rdp/native/Build-Native.ps1 -WorkRoot "$env:TEMP/vela-rdp-build-new"
dotnet build plugins/VelaShell.Plugin.Rdp -c Release -t:PackVpx -p:VelaDevAppDir= -p:VELASHELL_DEV_APP_DIR=
```

构建不会修改 NuGet 缓存。`VelaRdpNativeRoot` 可指向另一份按 RID 布局的扩展输出，插件只替换 `win-x64` 对应文件。上游构建目标按构建机操作系统复制资源，因此本工程局部补齐六种桌面 RID：

```text
runtimes/win-x64/native/DevolutionsIronRdp.dll
runtimes/win-arm64/native/DevolutionsIronRdp.dll
runtimes/linux-x64/native/libDevolutionsIronRdp.so
runtimes/linux-arm64/native/libDevolutionsIronRdp.so
runtimes/osx-x64/native/libDevolutionsIronRdp.dylib
runtimes/osx-arm64/native/libDevolutionsIronRdp.dylib
```

不要仅复制入口 DLL；托管桥、`.deps.json`、原生库及 `Licenses/` 都必须随包。SDK、Avalonia、MicroCom.Runtime 由宿主共享，不能复制进插件；移动平台和 `osx-universal` 资源不进入桌面插件包。

上游 MIT、Apache 及第三方 NOTICE 保留在 [Licenses/](Licenses/)，精确来源提交和核实范围见 [IronRDP-SOURCES.txt](Licenses/IronRDP-SOURCES.txt)。保留文本不等于已完成所有静态 Rust 依赖的法律审核。

## 验证状态

- Windows x64：原生 create/call/dispose、正式 SDK ALC 加载、保留表单卸载、TLS 环回、拒绝降级、取消/超时、帧缓冲并发及真实 Skia Headless 输入/画面测试已执行。
- 2026-10-27 性能优化后：完整正确性回归 **102/102 通过，零失败/跳过**（含本机 Loopback、Headless 与现有卸载回归，不含手动性能分类）；关闭 runtime-async 的严格重新构建 **零警告、零错误**，CA1068 定向分析通过。10 项 Session 调度/生命周期用例另在 20 个独立测试进程中重复执行，累计 **200/200 通过**。
- 手动像素测量显式启用时 6/6 全像素校验通过，未启用时 6/6 真实跳过；它们不计入上述 102 项。真实泵用例使用离线测试流及原生图像；现有 ALC 回归不覆盖建立真实远端会话并创建视图后的完整卸载，均不能代替真实远端性能验收。
- 环回探针确认拒绝 legacy RDP/TLS-only，以及未信任或 SHA-256 pin 不匹配的证书；证书拒绝前不发送 NLA 应用数据。
- Release 曾在隐藏全部 Debug 输出后通过 90 项测试，排除读取旧 Debug payload 的情况。该结果属于当时源码快照，不代表未经重新构建的旧 `.vpx` 覆盖后续源码修改。
- 实际宿主已显示 RDP 配置页；首次连接的证书确认框已由用户在调试中确认可见。
- **真实 Windows 登录、完整桌面画面/键鼠/重激活/断网重连，以及 macOS/Linux/arm64 实机运行仍未验收。** 原生文件存在、Headless 通过或证书对话框出现不能替代这些验证。

完整插件开发契约见 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)。
