# VelaShell.Plugin.Mongo —— MongoDB 工作台

> id `velashell.mongo` · 进程内装载 · 命令面板入口(`contributes.commands`,与 Docker 面板同一个路数) · `minSdkVersion` 2.0.2

照设计稿 `velashell-plugin-mongo.pen`(仓库根,24 个画板)实现的 MongoDB 客户端:
Navicat 式的对象树与大图标工具栏,补齐 Compass 的聚合管道、Schema 分析、索引建议与 GridFS;
网格 / 树 / JSON 三视图共用一份数据,所有编辑先进暂存区,提交前可预览将执行的命令。

设计取证与取舍记录在 velashell-docs 的 `zh/host/MongoDB工作台插件设计.md`(英文镜像
`en/host/mongodb-workbench-plugin-design.md`)。本文件只记**在这个目录里改代码**要知道的事。

## 目录

| 位置 | 内容 |
| --- | --- |
| `MongoPlugin.cs` | 入口:注册「MongoDB: 打开 MongoDB 工作台」命令,按下开文档面板 |
| `MongoSettings.cs` | 连接设置的字符串键与解析(已保存的连接按这些键存,对话框按这些键读写) |
| `Core/` | `MongoConnection`:驱动客户端 + 服务器身份 + 权限摘要 + 心跳延迟 + 对象目录;`MongoProfile`(已保存的连接与它的存储)、`MongoConnector`(连、测、把失败翻成人话)、`SshTunnel`(经宿主的 SSH 会话转发)、`ConnectionProbe`(连接串预览与逐步测试) |
| `Bson/` | BSON 的类型识别与配色、网格/内联/多行三种文本形态、mongosh 字面量解析、类型化编辑、点路径 |
| `Shell/` | mongosh 语句解析与执行、补全、代码导出;`MongoVocabulary` 是阶段/运算符/方法的双语词汇表 |
| `Staging/` | 网格的暂存区:字段级修改、新增、删除,乐观并发提交与撤销 |
| `Analysis/` | 执行计划解析、Schema 抽样分析、$jsonSchema 生成与客户端预检、索引建议、查询形状 |
| `Transfer/` | 导出(JSON / CSV / XLSX / BSON 转储 / Shell 脚本)、导入解析、跨连接复制 |
| `Ui/Shell/` | 工作台外壳:工具栏、对象树(根上是全部已保存的连接)、对象标签、覆盖层、状态条、写护栏;`MongoSession` 是一条连着的连接(标签页拿到的 `IMongoWorkspace`),`ConnectionStateTab*` 是连接中 / 连接失败的占位标签 |
| `Ui/<分区>/` | 各对象标签页与对话框:Collection、Query、Pipeline、GridFs、Design、Objects、Monitor、Profiler、Users、Transfer、Dialogs(含新建 / 编辑连接 `ConnectionDialog*`) |
| `Ui/Editor/` | `CodeEditor`(AvaloniaEdit 封装:高亮、补全弹层、诊断波浪线、整行标记) |
| `Ui/MongoStyles.axaml` / `MongoTheme.axaml` | 全插件共用的控件主题与样式类;150 个 lucide 图标几何 |
| `Syntax/` | mongosh 与扩展 JSON 的高亮定义(只管"哪段是什么角色",颜色来自宿主令牌) |

## 与宿主的分工

- **入口**:命令面板(Ctrl+P)里的「MongoDB: 打开 MongoDB 工作台」,开一个文档面板;面板已开着就把它带到眼前。
  MongoDB **不**出现在宿主的新建连接窗口与会话树里。
- **宿主画的**:窗口标题栏、文档标签条(这一个「MongoDB」面板标签)。其余都在面板里:56px 工具栏、对象树、
  对象标签、新建 / 编辑连接对话框(设计稿 10)、连接中 / 连接失败的占位卡片(设计稿 22)、24px 状态条
  (当前连接 · 范围 · 当前标签的状态,`WorkspaceTab.StatusText`)、证书不受信任时的「信任此证书并重连」。
- **连接由插件自己管**:已保存的连接进插件存储(`MongoProfileStore`,键 `connections`),**口令进宿主的加密密钥库**
  (`ISecretsApi`,按连接 id),不写进连接串、不写进插件存储。SSH 跳板经宿主里已保存的 SSH 连接转发
  (`ISessionsApi.OpenAsync` + `IRemoteTunnelApi.OpenTcpAsync`,本机回环上起转发端口)—— 插件一行 SSH 代码都不写,
  也碰不到跳板机的凭据;几条连接走同一台跳板时共用一条会话,只关本插件开的那条。
- **数据传输的"目标连接"**:本插件此刻开着的连接(`MongoSessionRegistry`)或手填一条连接串。

## 与设计稿 / DESIGN.md 的偏离(刻意的)

| 设计稿 | 实现 | 理由 |
| --- | --- | --- |
| 10「新建连接 · MongoDB」 | 照稿实现在插件里(`ConnectionDialog*`,1020 × 760):左 700 表单(基本 / 服务器 / 认证 / 安全通道)、右 320 侧栏(连接字符串 / 测试结果 / 发现的成员 / 安全策略)、页脚「测试连接 · 取消 · 仅保存 · 保存并连接」。稿外加了一个默认收起的「高级」(appName、超时、每页行数、抽样数、扩展 JSON 模式、直连、显示系统库);主机行前的 ⠿ 只是记号,不能拖动排序;SRV 形态不能走 SSH 跳板(卡片上写明原因) | 稿外那些调优项不决定连不连得上,但总得有个地方改;SRV 经 DNS 解析出一组成员,一条转发只通得到其中一台 |
| (稿外)系统库 | admin / config / local 默认不列(`showSystemDatabases`,高级选项);对象树表头的眼睛按钮在本次会话里切换;连接里点名的默认库照列 | Navicat 的默认;日常几乎不进这三个库,列着只会把用户库往下挤 |
| 10 的「分组」 | 对话框里一栏(可选已有分组,也可新写);对象树里分了组的连接挂在可收起的组名节下面,不分组的在最上面 | — |
| 01 / 13 对象树根上只画了一条连接 | 根上是**全部**已保存的连接(Navicat 的习惯):灰点没连、绿点连着、红点没连上;双击连上,连着的那条下面挂库;右键里有连接 / 断开 / 编辑 / 复制 / 删除 | 连接由插件自己管之后,树根就是连接列表 |
| 14 执行计划时编辑器收矮到 194 | 不动分界:切到执行计划页,编辑器与结果区的高度照用户拖过的分隔条 | 结果页与执行计划页是同一块面板的两个页签;自动收矮再在切回时重置成默认值,用户拖好的高度就白拖了 |
| 01 对象树的分组文件夹右键只有刷新 | 「集合 / 视图 / GridFS 存储桶」分组(及其中对象、库行)右键可新建对应对象;桶行右键可上传文件 / 文件夹、删除存储桶 | 不打开对象列表也能就地新建;新建桶原先只能从已打开的桶标签里进 |
| 会话标签上的连接名(「mongo-inner-01」) | 宿主标签条上是一个「MongoDB」面板;连接名写在对象树根、对象标签与状态条上 | 一个面板装着全部连接(与 Docker 面板一样);对象标签按连接分开,同名集合在两条连接里是两个标签 |
| 22「连接中 / 连接失败」两张卡 | 照稿实现在插件里:连接时先开一个占位标签(「正在连接 X」+ 取消),连上了换成默认库的对象列表,没连上就原地变成「无法连接 X」+ 等宽字的原因 + 编辑连接 / 关闭标签页 / 重新连接;证书不受信任时多一个「信任此证书并重连」(只信这一张的指纹)。连接**中途断开**时内容区顶部一条横幅 | 证书信任原先是宿主的流程,连接自己管之后由插件给 |
| 图表色 chart1–4 | `MongoChart1–4`(插件私有资源,按明暗各一套) | 宿主令牌里没有四个互相可区分的分类色;Info / Accent / Warning 各有语义(延迟告警是 Warning),拿来当分类色会被读成"告警" |
| BSON 色板 bsonOid / bsonStr / … | 直接用宿主的 `VelaShellBlue / Cyan / Green / Yellow / Magenta`、`VelaWarning`、`VelaTextTertiary` | 逐色等于 VelaDark 下的终端色,换主题时跟着走,而不是写死 Dracula 色值 |
| `warningDim / errorDim / successDim / infoDim` | `{ui:Dim VelaWarning, 0.12}` 等:从宿主令牌按固定透明度现算,换肤时就地改色 | 宿主只有 VelaAccentDim 与终端色那一族的 Dim;拿 VelaShellYellowDim 顶替的话,橙字压在黄底上 |
| 面板标签的 MongoDB 图标 | lucide `leaf`(描边) | 与设计稿一致;官方商标是注册商标,不随包分发 |

## 交互约定(稿外,照 Navicat)

- **钻入**:网格(`GridDrill` + `CollectionTabViewModel.Drill.cs`)与查询结果网格(`ResultGrid` 的层栈)里双击 / Enter 对象或数组单元格,
  换成那一层的子表,面包屑 + Backspace 回退。集合网格的子表行与所属文档共用暂存身份,单元格路径是绝对路径 ——
  所以暂存区、提交、撤销一行没改。网格绑的是 `GridColumns / GridRows / GridSelectedRow`,树 / JSON / 检查器仍看文档那一层。
- **列宽**:集合网格、查询结果、管道输出各自接了列头拖把;其余用 `ColumnDefinitions` 对齐的表一律挂 `ui:TableColumns.Header/Row`
  (同名一组,`Fixed` 排除勾选框之类的列)。双击拖把按内容自适应,公共零件在 `ColumnFit`。
- **面板分隔条**:`GridSplitter.vsplit / .hsplit`(MongoStyles),放在宽 / 高为 0 的那一列 / 行里,左右(上下)各探出 3px;
  线由两侧面板自己的边框画。
- **数据区右侧面板可收起**:集合网格的文档检查器与 JSON 视图的大纲,底栏右下角那颗按钮切换(`ui:SidePanel.Collapsed`
  挂在「内容 | 分隔条 | 面板」三列的 Grid 上,收起时最后一列归零、展开回到原宽度);新开的集合标签记着上次的选择。
- **检查器**(`DocInspectorView`):对象 / 数组整行可点着展开收起(按下第一下就翻,`ClickCount` 2 不再翻回);
  编辑框失焦即提交(焦点进了类型菜单、日历这些弹出层不算),解析不了的留着红框;
  选中行只在 `ListBox.flat:focus-within` 时着色(`CollectionStyles`)。
- **日期的内联编辑**:`InlineValueEditor.IsDate` 时编辑框右边是日历按钮,弹 `DatePickFlyout`(月历 + 时刻框 +「现在」/「应用」);
  日期不再给"现在"那一条文字候选。设计稿没有月历这一块,外观按令牌自定:`Calendar` / `CalendarItem` /
  `CalendarDayButton` / `CalendarButton` 四个**按类型作键**的主题(MongoStyles),一格 30×26、行距 28,
  今天 = `VelaAccent` 描边、选中 = `VelaAccentDim` 底;时刻框与两颗按钮是 `MongoTextBox` / `MongoOutlineButton` / `MongoPillButton`。
  文档编辑器里的日历按钮弹的也是同一套(插件里任何 `new Calendar()` 都自动用它)。
- **筛选框的 Enter**:补全弹层开着时是「接受补全」,关着才是「查找」。筛选框在编辑区上另挂了隧道处理器,
  而同一元素上的隧道处理器按**注册的逆序**调用 —— 它比 `CodeEditor` 自己的先拿到 Enter,所以要先看 `CodeEditor.IsCompletionOpen`。
- **新建索引**(设计稿 07b,`DesignIndexesView` + `DesignTabViewModel.NewIndex.cs`):Navicat 设计表的做法 —— 点「新建索引」后索引页下半部分
  换成整宽编辑器(字段表 / 类型与选项 / 命令预览与预估),索引表末尾钉一行「新建」草稿(`DraftIndex`,与表里的行同一个 `IndexRow` 模型,
  不进 `Indexes`,所以重名检查、删除、隐藏都碰不到它)。字段表的行距是 28,`DesignIndexesView.RowPitch` 跟着它算拖动落点;
  上移 / 下移按钮走 `MoveKeyUpCommand / MoveKeyDownCommand`,与拖动是同一个 `MoveKey`。编辑器开着时索引表的滚动区
  (`ScrollViewer#TableScroll.compact`)最多 180 高。
- **查询的执行目标**(`QueryTabViewModel.Target.cs`):工具行下行的「连接 ▾ 数据库 ▾」。标签的连接定死在构造里,
  所以切换连接是 `MongoWorkspaceViewModel.ReplaceTab` 原位换一个新连接上的查询标签(`CarryOver` 带过文本、未保存基线、光标、
  maxTimeMS);没连着的连接走 `ConnectAsync(entry, quiet: true)`(不开占位标签、不动树的选中、不开对象列表)。
  诊断的第二段(`CheckCollectionsAsync`,异步)查 `db.集合` 在生效的库里有没有,没有就是一条 Warning;
  脚本里建删了集合(`ChangesCatalog`)时 `ForgetCollections` 丢掉那个库的缓存。
- **GridFS 拖放区可点**:`DropZone` 的 Tapped 走 `UploadFilesCommand`;源头在框里那两个链接按钮里时不再重复弹(它们自己处理)。
- **服务器监控**(`MonitorTabView`):两行图表 `*` : `*` 分高度,中间 `hsplit` 可拖;两张柱状图 `MinSlots` = 满窗口的柱数
  (`ChartSlots`),刚开始采样时柱子靠右、保持最终宽度。存储 Top 的集合名列是 `Width="Auto" SharedSizeGroup="StorageName"`
  (各行对齐、默认放下最长的名字)再挂 `ui:TableColumns` 组 `storage`(拖宽、双击自适应);名字是两个 `Run`(库名淡色 + 集合名),
  两个 `Run` 必须写在同一行,中间的换行会被画成一个空格。
- **有未保存修改的标签**:标签上是橙点,鼠标移到标签上换成 ×(`MongoWorkspaceView` 里 `Ellipse.dirty` / `Button.tabclose` 两组样式,
  别再给它们绑 `IsVisible` —— 本地值会压过 `:pointerover` 样式,× 就永远出不来,有修改的标签就关不掉了)。
  点 × 或 Ctrl+W 都先过 `WorkspaceTab.ConfirmCloseAsync`:「放弃未保存的修改?」,确认才关。
- **代码里建的右键菜单**一律走 `MenuKit.Command`:普通项不设 `Foreground` —— 哪怕设成 `null`,本地值也会压过宿主
  `ContextMenu MenuItem` 样式,文字透明到悬停才露出来。
- **绑定不穿过可空的中段**:`{Binding Dialog.Title}`、`{Binding Editor.HasError}` 这类路径在中段为 null 时
  (没开对话框、格子不在编辑)每一处都报一条绑定错误,宿主调试输出里刷屏。可空的那一段换成数据上下文
  (`DataContext="{Binding Dialog}"` + 子元素 `x:DataType`),或在模型上给一个扁平属性(`CollectionCell.HasEditorError`);
  挂在别的控件下的子视图(`CodeEditor` 的补全弹层)显式 `DataContext = null`,别继承宿主视图的视图模型。
  `BindingHygieneTests` 把主要界面与全部对话框走一遍,绑定错误一条都不许有。
- **停用不押在 UI 线程上**:宿主退出时 UI 线程正同步等着插件停用(限时 2 秒),面板的 `Closed` 在线程池上触发。
  所以停用先放连接与跳板转发(`MongoWorkspaceViewModel.ReleaseConnectionsAsync`,不碰界面、任意线程),
  关面板只等一小会儿,界面那一半(`Dispose`)只在 UI 线程上做(不在就投递)。`ShutdownTests` 守着这条路。

## 已知限制

| 现象 | 原因 / 现状 |
| --- | --- |
| 代码区行高约 15–16px,设计稿是 18px | AvaloniaEdit 不提供行高设置,行高由字体度量决定 |
| 查询编辑器的 code lens(▷ 运行 · 执行计划 …)画在叠加层上 | AvaloniaEdit 没有在两行之间插入装饰行的机制:语句上一行是空行时画在空行里,否则画在语句首行行尾 |
| 管道卡片右上角的「→ N 份文档」是抽样计数 | 预览基于前 N 份输入文档(底栏写明);设计稿画的是全集合数 —— 每次改阶段都对全集合计数代价太高 |
| 「事件」里没有「用户登录」 | serverStatus / `$currentOp` 推断不出登录事件 |
| profiler 的 slowms / sampleRate 是整个 mongod 实例的设置 | MongoDB 的语义如此;控件的提示里写明了 |
| 「未使用 N 天」徽章至少要 `$indexStats` 观察满 7 天 | 重启后计数清零,短于 7 天不下结论 |
| 设计稿底部那行状态栏 | 面板自己画一条 24px 状态条:当前连接 · 范围 · 当前标签的状态(待提交数等并在标签的那一段里);没有「UTF-8」那一格 | 面板没有宿主状态栏可用(那是会话标签的);编码对 MongoDB 无意义 |
| 时序集合不能「创建后立即编辑验证规则」 | 服务器不允许给时序集合设 validator,对话框里这一项置灰 |
| 弹出层(补全、右键菜单、类型菜单)不进 headless 截图 | 它们是独立的顶层窗口;接线由单测覆盖 |

## 开发

```bash
dotnet build plugins/VelaShell.Plugin.Mongo
dotnet test tests/VelaShell.Plugin.Mongo.Tests
```

打真实 MongoDB 的测试按仓库惯例**按环境早退**:本机没有 `127.0.0.1:27017` 时报 Inconclusive。
地址可用 `VELASHELL_MONGO_TEST_URI` 改。截图测试(`ScreenshotTests` 与各分区的 `*Tests`)在设了
`MONGO_SCREENSHOT_DIR` 时把每一屏落成 PNG,用来与设计稿逐屏对照;库默认用 `shop`(与设计稿的数据同形,
`MONGO_SCREENSHOT_DB` 可改)。灌数据的脚本是 `tests/VelaShell.Plugin.Mongo.Tests/seed-shop.js`(头注写了用法)。
