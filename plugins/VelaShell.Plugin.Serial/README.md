# VelaShell 串口插件

RS-232 / USB 转串口终端。以 SDK 的**终端协议能力**(`IProtocolTerminal`)接入宿主:
连接页多出一个「串口」页签,用它建的会话打开的是普通终端标签 ——
VT 引擎、回滚、搜索、会话日志、会话录制、ZMODEM 全部是宿主的既有实现,插件只负责
"把字节搬过去"、端口枚举,以及串口独有的那几件事(换行归一化、本地回显、发送节流、
Break 与 DTR/RTS)。

| | |
| --- | --- |
| id | `velashell.serial` |
| 装载模式 | 进程内(协议能力的硬性要求) |
| 激活时机 | `onProtocol:velashell.serial` —— 用户点到串口页签才装载 |
| 依赖 | `System.IO.Ports`(MIT) |
| 最低 SDK | 1.5.0(用到 `NoEndpoint` / `DynamicChoice` / `HostKind`) |

## 连接表单

主表单是"连不连得上"的参数,「高级选项」里是"连上了但看着不对"的那些 ——
后者用户不会一上来就调,但出问题时必须找得到。

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| 串口设备 | — | **可刷新的下拉**,带友好名(`USB-SERIAL CH340 (COM3)`);也可以直接手输 |
| 波特率 | 115200 | 表里是常用值,可手输 —— 250000(Marlin)、76800、1500000 都不在标准表上 |
| 数据位 / 停止位 / 校验 | 8 / 1 / 无 | 即 8N1 |
| 流控制 | 无 | RTS/CTS(硬件)、XON/XOFF(软件)、两者 |
| 回车键发送 | CR | LF / CR LF 可选 |
| 打开时置 DTR / RTS | 开 / 开 | 高级。RTS 在流控制取 RTS/CTS 时自动隐藏(那时归驱动) |
| 收到裸 CR 补 LF | 关 | 高级。**输出每行盖在上一行上**时打开它 |
| 收到裸 LF 补 CR | 关 | 高级。**输出呈阶梯状**时打开它 |
| 本地回显 | 关 | 高级。对着从不回显的设备才需要开 |
| 发送延时(字符 / 行) | 0 / 0 | 高级。给没有流控、粘贴配置会丢字符的老设备用 |

用户名/口令两栏由 `ProtocolFeatures.NoCredentials` 收起(登录发生在带内),
端口那一栏由 `ProtocolFeatures.NoEndpoint` 收起(串口的目标不是 `host:port`)。
设备名占的是"主机"那一格 —— 与 PuTTY 把 *Host Name* 换成 *Serial line* 是同一个取舍:
那一格本来就表示"连到哪儿",最近连接列表与会话名也因此显示得出设备。

## 命令面板里的三件事

串口有几个动作**没法用键盘表达** —— Break 是线路状态而不是字符,DTR/RTS 更是两根独立的
控制线。它们注册成命令(Ctrl+P / Ctrl+K):

| 命令 | 用途 |
| --- | --- |
| 串口:发送 BREAK 信号 | Cisco ROMMON 口令恢复、打断 U-Boot 自动启动、内核 SysRq |
| 串口:脉冲 DTR(复位开发板) | Arduino 自动复位电路、ESP32 的 EN 脚 |
| 串口:切换 DTR / RTS | 需要把某根线**保持**在某个电平时 |

作用对象是**最近一次收到输入的那条串口会话**(用户要按 Break,总是先在那个标签里敲过东西)。
宿主目前没有把"当前聚焦的终端标签"开放给插件,所以只能这样推断;每条命令都会把
实际作用的端口名写进插件日志。

## 三处最容易漏、且漏了极难定位的地方

都有专门的单测钉着(`tests/VelaShell.Plugin.Serial.Tests`)。

1. **跨块的裸 CR**。设备发完 `"hello\r"` 就不说话是常态,所以块尾那个悬空的 CR 不能扣下来等下一块
   —— 那一行会永远不出现。于是必须**先补 LF**,再记下"下一块若以 LF 开头就吞掉它",
   否则一次 CRLF 被读取切开就变成一个空行。
2. **拔线必须归一成 EOF**。USB 转串口被拔掉时驱动层一路抛 `IOException` / `UnauthorizedAccessException`;
   抛给宿主只会得到一个红色异常框,而归一成 EOF 才能走到标签页那条"已断开 + 可重连"的既有路径上。
3. **端口由读线程自己关**。读取带 250ms 超时轮询,关闭时不需要去唤醒一个阻塞中的读 ——
   而"唤醒阻塞中的读"正是 dotnet/runtime [#20362](https://github.com/dotnet/runtime/issues/20362)
   (`Close()` 在硬件流控卡住时永久阻塞)与 [#44952](https://github.com/dotnet/runtime/issues/44952)
   两个至今 open 的 issue 的来源。

## 为什么不用 `DataReceived`

`SerialPort.DataReceived` 在 115200 以上会丢字节
([#106631](https://github.com/dotnet/runtime/issues/106631),至今 open)。
也不用 `BaseStream.ReadAsync`:它在 Windows 上**不响应取消令牌**
([#30850](https://github.com/dotnet/runtime/issues/30850),微软自己提的,Future 里程碑),
Unix 上响应 —— 一个平台间行为不一致的地基,换来的只是省一条线程。
一条会话一条读线程在这里是合适的:同时开着的串口数以个位计。

## 端口枚举:三平台三套做法

| 平台 | 清单来源 | 友好名 |
| --- | --- | --- |
| Windows | `SerialPort.GetPortNames()`(注册表 `SERIALCOMM`) | SetupAPI(设备管理器同款),失败只是没有描述 |
| Linux | `/dev/serial/by-id/` + `ttyUSB* / ttyACM* / ttyAMA* / ttyS* / rfcomm*` | by-id 链接名自带厂商型号;否则取驱动名 |
| macOS | **只列 `/dev/cu.*`** | 设备名本身 |

三条各有一个非做不可的细节:

- **Windows 必须自己排序**。微软文档明写 `GetPortNames` 的返回顺序未定义,而字符串序下
  `COM10` 排在 `COM2` 前面。
- **Linux 必须过滤 `ttyS*`**。8250 驱动会无条件注册 `ttyS0..ttyS31`,绝大多数背后没有硬件;
  不过滤的话下拉里是三十多个假端口,真的那个反而找不着。判据是
  `/sys/class/tty/<名>/device` 是否存在。
- **macOS 绝不能列 `/dev/tty.*`**。那是 dial-in 设备,打开时会**阻塞等待 DCD 载波** ——
  对着一个没接线的适配器打开它,进程永久挂起,而用户看到的是"点了连接没反应"。
  `/dev/cu.*` 是 call-out,立即打开。

## 打包

`System.IO.Ports` 带 `runtimes/<rid>/native/`(Linux 的 `.so`、macOS 的 `.dylib`)。
插件是 RID 无关的动态装载工程,构建时那一层会**原样落进输出目录**,再由
`GetVelaPluginPayload` 一并收进发行包 —— 宿主的 `PluginAssemblyLoadContext` 经
`AssemblyDependencyResolver.ResolveUnmanagedDllToPath` 从插件自己的 `deps.json` 解析它。
少了那一层的表现是 Linux/macOS 上运行期 `PlatformNotSupportedException`,
而在 Windows 开发机上**永远测不出来**(调研文档 §五.2 记的正是这条)。

## 已知边界

- **无法自动化端到端验证**:串口没有环回可用(Telnet 有 `TcpListener`,S3 有环回 HTTP 服务器)。
  真正的字节路径靠 `FakeSerialPort` 替身钉住;端到端要真硬件,或
  `socat -d -d pty,raw,echo=0 pty,raw,echo=0`(Linux/macOS)、com0com(Windows)造一对虚拟串口。
- **XON/XOFF 与 ZMODEM 互斥**:软件流控会吃掉线上的 0x11 / 0x13。表单的说明里写了,
  但插件不会替用户拦 —— 有些设备只有软件流控可选。
- **回车改写作用于整条出方向流**:`WriteAsync` 只有一个入口,分不出"用户按的回车"与
  "粘贴内容 / ZMODEM 帧"。默认的 CR 不改写任何字节,所以这个代价只在用户主动打开时才发生。
