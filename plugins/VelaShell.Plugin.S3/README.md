# VelaShell.Plugin.S3

S3 兼容对象存储的官方插件:Amazon S3、MinIO、Ceph RGW、阿里云 OSS、腾讯云 COS、Cloudflare R2 等。

- 设计与取舍:[S3 协议插件化设计](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/S3协议插件化设计.md)
- 协议能力域用法:[开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) §5.13

## 它做了什么

| 面 | 内容 |
| --- | --- |
| 文件管理器 | 实现 SDK 的 `IProtocolFileSystem`,把平的键空间翻译成目录树;宿主的双栏浏览、传输队列、限速、拖放、冲突策略**零改动**复用 |
| 桶管理器 | 概览 + 对象版本 + 未完成分片上传 + **22 项桶配置**(表单 / JSON 双形态编辑器) |
| 对象检视器 | 详情 / 标签 / 权限 / 保留与合法保留 / 存储与加密 / S3 Select / 预签名 URL |
| 标签页图标 | lucide `cloud`(`S3Icon`),会话标签页与两扇工具窗共用。**不用存储那一类字形** —— 宿主给 SFTP / FTP 的标签画的正是 `hard-drive`,而三者并排躺在同一条标签条上 |

S3 协议的 116 个操作里,除 4 个客户端无法或不应调用的(见设计文档 §九)之外全部可达。

## 协议的可选面(SDK 2.1.3)

| 可选面 | 实现了没有 | 为什么 |
| --- | --- | --- |
| `IProtocolChecksum`(服务器端摘要) | **实现**(`S3StoredChecksum`) | 只交服务端**存着的**完整对象校验和:HEAD 带 `x-amz-checksum-mode: ENABLED`,取 `x-amz-checksum-sha256` / `-sha512` / `-sha1` / `-md5`,base64 换成小写十六进制。对象没存这种校验和、分段上传的组合摘要(`x-amz-checksum-type: COMPOSITE` 或值带 `-分段数`)、根 / 桶 / 目录前缀、不存在、没权限(含 KMS 对象缺 `kms:Decrypt`)→ 那一项为 null;`Unknown` 算法整条会话抛 `ProtocolUnsupportedException`。**绝不**为凑一个值把对象读回来算,**也不拿 ETag 充 MD5**(分段上传、SSE-KMS / SSE-C 与不少兼容实现上它都不是 MD5)。一个路径一次 HEAD,按会话的并发设置并行 |
| `IProtocolLastWriteTime`(改修改时间) | **不实现** | 对象的 `LastModified` 是服务端写入时刻,协议上没有改它的操作。唯一的近似是「复制对象自身 + 改元数据」:会重写对象、换掉版本号与 ETag、触发复制 / 生命周期规则,而 `LastModified` 照样是复制那一刻 —— 不等价,不做。不实现,宿主就如实报「设不了修改时间」,目录同步改按大小 / 摘要比较 |
| `RemoteFileEntry.IsSymbolicLink` / `LinkTarget` | 不适用 | S3 没有符号链接;条目一律是「不是链接」(默认值) |

注意本插件**自己上传的对象不带校验和**:`S3ClientFactory` 用 `RequestChecksumCalculation.WHEN_REQUIRED`
(兼容不认 flexible checksums 的 S3 兼容实现),所以这些对象上摘要恒为 null,目录同步对它们回退到大小与修改时间。
摘要只对别的工具带着 SHA-256 等校验和上传的对象有值。

## 文件导览

| 文件 | 说明 |
| --- | --- |
| `S3Plugin.cs` | 入口:注册协议描述(页签、连接表单、右键动作、能力位) |
| `S3ProtocolFileSystem.cs` | 协议实现。内部以 `Guid` 为会话键,宿主的不透明字符串键只在 `IProtocolFileSystem` 的**显式实现**那一层映射 |
| `S3ManagementService.cs` | 文件管理器之外的近百个操作;与文件系统共用同一条会话与同一个客户端 |
| `S3ObjectPath.cs` | 路径 ↔「桶 + 键」的双向翻译 —— 整个后端都架在这层映射上 |
| `S3StoredChecksum.cs` | 对象存着的校验和 → SDK 的小写十六进制摘要;认出并拒掉分段上传的组合摘要 |
| `S3Interop.cs` | AWSSDK 异常 → `VelaS3*` 异常族。**AWSSDK 的类型不越过插件边界** |
| `S3Icon.cs` | 标签页/标题栏图标的路径数据(lucide `cloud`) |
| `S3ConfigKind.cs` | 22 项桶配置的枚举与元数据表;桶管理器的导航由它驱动 |
| `Ui/` | 两个面板(不引 ReactiveUI,理由见设计文档 §5.3) |
| `ThrottledStream.cs` | 宿主同名实现的副本 —— 限速上限仍由宿主统一给出 |

## 开发

`plugins/Directory.Build.targets` 会在构建后把输出镜像到 `src/VelaShell/bin/<配置>/net11.0/plugins/velashell-s3/`,
直接 F5 主程序即可加载。协议页签在**发现期**就出现在连接配置页(不装载本程序集),
用户点到它才触发 `onProtocol:velashell.s3` 惰性激活。

测试:`tests/VelaShell.Plugin.S3.Tests`(环回 S3 服务器会重算 SigV4 并比对 —— 守的是
"客户端配置有没有被正确送上线",详见设计文档 §8.1)。
