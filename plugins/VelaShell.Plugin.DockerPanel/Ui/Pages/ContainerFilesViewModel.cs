using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>编辑器 / 差异视图里的一行。</summary>
/// <param name="Number">显示的行号。</param>
/// <param name="Marker">差异标记(<c>+</c> / <c>-</c> / <c>~</c>;没变为空)。</param>
/// <param name="Text">行内容。</param>
/// <param name="Tone">语气(界面按它给标记与底色上色)。</param>
public readonly record struct EditorLine(string Number, string Marker, string Text, RowTone Tone);

/// <summary>本面板往这个容器里写过的一次。</summary>
/// <param name="At">什么时候。</param>
/// <param name="Path">写了哪个文件。</param>
/// <param name="Summary">改动摘要,如 <c>+2 −1</c> 或 <c>新建</c>。</param>
public readonly record struct FileWriteRecord(DateTimeOffset At, string Path, string Summary)
{
    /// <summary>时间的显示文本。</summary>
    public string TimeText => At.ToLocalTime().ToString(
        At.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss" : "MM-dd HH:mm");

    /// <summary>文件名(路径太长,历史列表里只放名字)。</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>文件树里的一项。</summary>
public sealed class FileEntryItem(ContainerFileEntry entry, string changeMarker) : ObservableObject
{
    /// <summary>底层条目。</summary>
    public ContainerFileEntry Entry { get; } = entry;

    /// <summary>名字。</summary>
    public string Name => Entry.Name;

    /// <summary>绝对路径。</summary>
    public string FullPath => Entry.FullPath;

    /// <summary>是否目录。</summary>
    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>图标资源键。</summary>
    public string Icon => Entry.IsDirectory ? "Icon.folder" : Entry.IsSymlink ? "Icon.link-2" : "Icon.file-text";

    /// <summary>大小文本。</summary>
    public string SizeText => Entry.IsDirectory ? "" : Humanize.Bytes(Entry.Size);

    /// <summary>权限。</summary>
    public string Mode => Entry.Mode;

    /// <summary>修改时间。</summary>
    public string Modified => Entry.Modified;

    /// <summary>符号链接的目标。</summary>
    public string LinkText => Entry.LinkTarget is { Length: > 0 } target ? $"→ {target}" : "";

    /// <summary>是不是软链接(决定要不要画出目标)。</summary>
    public bool IsSymlink => Entry.IsSymlink;

    /// <summary>
    /// 悬停时那行详情:权限、大小、修改时间。
    /// <para>
    /// 这三样 <c>ls</c> 早就解析好了,却一直没有任何地方显示 ——
    /// 树里一行只有 24px,塞不下三列;挂在提示上是它们唯一放得下的位置。
    /// </para>
    /// </summary>
    public string Details => string.Join("  ", new[] { Mode, SizeText, Modified }.Where(s => s.Length > 0));

    /// <summary>相对镜像的变更标记(A/C/D);没变过为空。</summary>
    public string ChangeMarker { get; } = changeMarker;

    /// <summary>有没有变更标记。</summary>
    public bool HasChange => ChangeMarker.Length > 0;

    /// <summary>「只看变更」过滤后是否显示。</summary>
    public bool Visible
    {
        get;
        set => SetField(ref field, value);
    } = true;

    /// <summary>变更标记的语气。</summary>
    public RowTone ChangeTone => ChangeMarker switch
    {
        "A" => RowTone.Ok,
        "D" => RowTone.Danger,
        "C" => RowTone.Warn,
        _ => RowTone.Idle
    };

    // ── 树 ────────────────────────────────────────────────────────

    /// <summary>在树里的层级。根("/")是 0。</summary>
    public int Depth { get; init; }

    /// <summary>缩进像素。每层 13px —— 与折叠箭头同宽,竖着看是一条对齐的线。</summary>
    public double Indent => Depth * 13;

    /// <summary>只有目录才有折叠箭头。文件那一格留空,名字才不会左右跳。</summary>
    public bool HasCaret => IsDirectory;

    /// <summary>展开了没有。</summary>
    public bool Expanded
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(CaretIcon));
            }
        }
    }

    /// <summary>折叠箭头的朝向。</summary>
    public string CaretIcon => Expanded ? "Icon.chevron-down" : "Icon.chevron-right";

    /// <summary>子节点。目录第一次展开时才去列 —— 一上来递归整棵树会把隧道占满。</summary>
    public List<FileEntryItem> Children { get; } = [];

    /// <summary>子节点列过没有。空目录也算列过,否则每次点都会再发一次 exec。</summary>
    public bool ChildrenLoaded { get; set; }

    /// <summary>这一行是不是编辑器里正打开的那个文件。</summary>
    public bool Current
    {
        get;
        set => SetField(ref field, value);
    }
}

/// <summary>
/// 容器内文件浏览与在线编辑。
/// <para>
/// 列目录走 exec 跑 <c>ls</c>(Engine API 没有列目录这个端点,而 <c>/archive</c>
/// 只能整包取走);读写单个文件走 <c>/archive</c> 的 tar 流 —— 不经 shell,
/// 因此不受登录 shell、引用规则与 locale 的影响。
/// </para>
/// </summary>
public sealed class ContainerFilesViewModel(DockerPanelViewModel shell, string containerId, string containerName)
    : ObservableObject
{
    private readonly Dictionary<string, string> _changes = [];
    private readonly List<FileWriteRecord> _history = [];
    private bool _loaded;
    private string _originalText = "";
    private ContainerFileEntry? _openEntry;

    /// <summary>只看相对镜像有变更的那些条目。</summary>
    public bool ChangedOnly
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyEntryView();
            }
        }
    }

    /// <summary>
    /// 差异视图(编辑 / 差异两态切换)。
    /// <para>
    /// 比的是**这次编辑前后**,不是相对镜像 —— 后者由左边那个 A/C/D 标记表达。
    /// 用户按下保存前想确认的是"我刚改了什么",而不是"这个文件相对镜像改过什么"。
    /// </para>
    /// </summary>
    public bool DiffMode
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(EditMode));
                RebuildEditorLines();
            }
        }
    }

    /// <summary>在编辑态(与 <see cref="DiffMode" /> 互斥)。</summary>
    public bool EditMode => !DiffMode;

    /// <summary>保存后顺带在容器里跑一次重载命令。</summary>
    public bool ReloadAfterSave
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>差异视图里的行。</summary>
    public ObservableCollection<EditorLine> DiffLines { get; } = [];

    /// <summary>本面板往这个容器里写过的记录(最近在前)。</summary>
    public ObservableCollection<FileWriteRecord> History { get; } = [];

    /// <summary>有写入历史。</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>打开的那个文件的属性(大小 / 权限 / 属主 / 修改时间 / 相对镜像)。</summary>
    public ObservableCollection<DetailField> FileProperties { get; } = [];

    /// <summary>光标位置文本。</summary>
    public string CaretText
    {
        get;
        private set => SetField(ref field, value);
    } = "行 1, 列 1";

    /// <summary>已改了几行。</summary>
    public string ModifiedLinesText
    {
        get
        {
            if (!IsModified)
            {
                return "未修改";
            }
            var changed = LineDiff.CountChanged(LineDiff.Compute(_originalText, EditorText));
            return $"已修改 {changed} 行";
        }
    }

    /// <summary>换行符(照原文件的,保存时不改它)。</summary>
    public string LineEnding => _originalText.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : "LF";

    /// <summary>按扩展名猜的语言(只用来在状态条上显示)。</summary>
    public string Language => GuessLanguage(OpenFileName);

    /// <summary>
    /// 这个文件多半需要的重载命令;猜不出来时为空。
    /// <para>
    /// 只覆盖最常改的那几类配置。猜不到就不显示那个勾选框 ——
    /// 给一个会失败的默认命令,比不给更糟。
    /// </para>
    /// </summary>
    public string ReloadCommand => OpenFilePath is not { } path ? "" : path switch
    {
        var p when p.Contains("/nginx/", StringComparison.Ordinal) => "nginx -s reload",
        var p when p.Contains("/postgresql/", StringComparison.Ordinal) => "pg_ctl reload",
        var p when p.Contains("/redis", StringComparison.Ordinal) => "redis-cli CONFIG SET appendonly yes",
        _ => ""
    };

    /// <summary>有没有可用的重载命令。</summary>
    public bool HasReloadCommand => ReloadCommand.Length > 0;

    private static string GuessLanguage(string name)
    {
        var dot = name.LastIndexOf('.');
        var ext = dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
        return ext switch
        {
            "conf" or "cnf" or "ini" => name.Contains("nginx", StringComparison.OrdinalIgnoreCase) ? "nginx" : "ini",
            "yml" or "yaml" => "yaml",
            "json" => "json",
            "sh" or "bash" => "shell",
            "toml" => "toml",
            "xml" => "xml",
            "env" => "dotenv",
            "" => name.StartsWith('.') ? "dotfile" : "text",
            _ => ext
        };
    }

    /// <summary>
    /// 当前目录 —— 树上最后一个被展开或被打开文件所在的那一个。
    /// 上传落在这里,所以它必须跟着树走,而不是自己另记一份。
    /// </summary>
    public string Path
    {
        get;
        private set => SetField(ref field, value);
    } = "/";

    /// <summary>正在读。</summary>
    public bool Busy
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>出错信息。</summary>
    public string Error
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    } = "";

    /// <summary>有没有出错。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>相对镜像变更了几项。</summary>
    public int ChangeCount => _changes.Count;

    /// <summary>变更计数文本。</summary>
    // diff 读失败时不能说"与镜像一致"—— 那是在**断言一件没验证过的事**,
    // 而这一页的读者正要据此决定改哪个文件。读不到就说读不到。
    public string ChangeText => _diffFailed
        ? "读不到相对镜像的变更(A/C/D 标记这一次不可用)"
        : _changes.Count == 0
            ? "与镜像一致"
            : $"相对镜像已变更 {_changes.Count} 项";

    private bool _diffFailed;

    /// <summary>正在编辑的文件路径;没打开时为 <see langword="null" />。</summary>
    public string? OpenFilePath
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HasOpenFile), nameof(OpenFileName), nameof(OpenFileDirectory));
                MarkCurrentFile(value);
            }
        }
    }

    /// <summary>有文件开着。</summary>
    public bool HasOpenFile => OpenFilePath is not null;

    /// <summary>打开的文件名。</summary>
    public string OpenFileName => OpenFilePath is { } p ? p[(p.LastIndexOf('/') + 1)..] : "";

    /// <summary>打开的文件所在目录。</summary>
    public string OpenFileDirectory => OpenFilePath is { } p && p.LastIndexOf('/') > 0 ? p[..(p.LastIndexOf('/') + 1)] : "/";

    /// <summary>编辑器内容。</summary>
    public string EditorText
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText), nameof(ModifiedLinesText));
                RebuildEditorLines();
            }
        }
    } = "";

    /// <summary>改过了没有。</summary>
    public bool IsModified => EditorText != _originalText;

    /// <summary>改动摘要。</summary>
    public string ModifiedText
    {
        get
        {
            if (!IsModified)
            {
                return "未修改";
            }
            var before = _originalText.Split('\n').Length;
            var after = EditorText.Split('\n').Length;
            var delta = after - before;
            return delta switch
            {
                > 0 => $"未保存 · +{delta} 行",
                < 0 => $"未保存 · {delta} 行",
                _ => "未保存"
            };
        }
    }

    /// <summary>打开一个目录或文件。</summary>
    public RelayCommand OpenCommand => field ??= new(p => p is FileEntryItem item
        ? item.IsDirectory ? ToggleAsync(item) : OpenFileAsync(item.FullPath)
        : Task.CompletedTask);

    /// <summary>重新列一次(整棵树丢掉重建,并重新取一次相对镜像的变更)。</summary>
    public RelayCommand RefreshCommand => field ??= new(_ => ReloadTreeAsync());

    /// <summary>关掉编辑器。</summary>
    public RelayCommand CloseFileCommand => field ??= new(_ =>
    {
        OpenFilePath = null;
        EditorText = "";
        _originalText = "";
    });

    /// <summary>撤销未保存的修改。</summary>
    public RelayCommand RevertCommand => field ??= new(_ => EditorText = _originalText);

    /// <summary>保存回容器。</summary>
    public RelayCommand SaveCommand => field ??= new(_ => SaveAsync());

    /// <summary>把一个文件或目录取到本地。</summary>
    public RelayCommand DownloadCommand => field ??= new(p => p is FileEntryItem item
        ? DownloadAsync(item)
        : Task.CompletedTask);

    /// <summary>把本地一个文件传进当前目录。</summary>
    public RelayCommand UploadCommand => field ??= new(_ => UploadAsync());

    /// <summary>能不能弹本地文件对话框(宿主没给顶层窗口时不能)。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    /// <summary>
    /// 取一份到本地。
    /// <para>
    /// 目录只能是 tar —— <c>/archive</c> 交出来的就是一个 tar 流,面板不替用户解包:
    /// 一个几万文件的目录在这里展开,失败到一半留下的半个目录树比什么都不做更糟。
    /// 单个文件则从 tar 里取出来还原成原样,因为用户要的是那个文件,不是一个装着它的盒子。
    /// </para>
    /// </summary>
    private async Task DownloadAsync(FileEntryItem item)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        var suggested = item.IsDirectory ? $"{item.Name}.tar" : item.Name;
        var target = await FilePicker
            .PickSaveAsync(item.IsDirectory ? $"把 {item.Name}/ 存成 tar" : $"保存 {item.Name}",
                suggested, item.IsDirectory ? "tar" : null)
            .ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            await using var archive = await client.DownloadArchiveAsync(containerId, item.FullPath, shell.Lifetime)
                                                     .ConfigureAwait(true);
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            long written;
            if (item.IsDirectory)
            {
                await archive.CopyToAsync(output, shell.Lifetime).ConfigureAwait(true);
                written = output.CanSeek ? output.Length : 0;
            }
            else
            {
                // 单文件走流式解 tar:整份读进内存的话,一个 2 GB 的日志会把宿主拖垮。
                written = await TarUtil.ExtractFirstFileAsync(archive, output, shell.Lifetime).ConfigureAwait(true);
            }
            shell.Feedback.Notify(FeedbackKind.Success, "已取到本地",
                $"{target.Name}{(written > 0 ? $" · {Humanize.Bytes(written)}" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
            shell.Feedback.ReportError("下载文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>切到编辑态。</summary>
    public RelayCommand ShowEditCommand => field ??= new(_ =>
    {
        DiffMode = false;
        return Task.CompletedTask;
    });

    /// <summary>切到差异态。</summary>
    public RelayCommand ShowDiffCommand => field ??= new(_ =>
    {
        DiffMode = true;
        return Task.CompletedTask;
    });

    /// <summary>编辑器报一次光标位置(视图在选区变化时调)。</summary>
    public void ReportCaret(int line, int column) => CaretText = $"行 {line}, 列 {column}";

    /// <summary>按当前文本重算差异行。</summary>
    private void RebuildEditorLines()
    {
        DiffLines.Clear();
        if (!DiffMode)
        {
            return;
        }
        foreach (var line in LineDiff.Compute(_originalText, EditorText))
        {
            (var marker, var tone) = line.Marker switch
            {
                DiffMarker.Added => ("+", RowTone.Ok),
                DiffMarker.Removed => ("−", RowTone.Danger),
                DiffMarker.Changed => ("~", RowTone.Warn),
                _ => (" ", RowTone.Idle)
            };
            // 删除的行在新文里没有行号,显示原文的那个 —— 空着会让人对不上原文件。
            var number = line.Marker == DiffMarker.Removed ? line.OldNumber : line.NewNumber;
            DiffLines.Add(new(number > 0 ? number.ToString() : "", marker, line.Text, tone));
        }
    }

    private void BuildFileProperties()
    {
        FileProperties.Clear();
        if (_openEntry is not { } entry)
        {
            return;
        }
        FileProperties.Add(new("大小", Humanize.Bytes(entry.Size)));
        FileProperties.Add(new("权限", entry.Mode));
        FileProperties.Add(new("属主", entry.Owner));
        FileProperties.Add(new("修改时间", entry.Modified));
        var marker = _changes.GetValueOrDefault(entry.FullPath, "");
        FileProperties.Add(marker switch
        {
            "A" => new("相对镜像", "新增 (A)", RowTone.Ok),
            "C" => new("相对镜像", "已修改 (C)", RowTone.Warn),
            "D" => new("相对镜像", "已删除 (D)", RowTone.Danger),
            _ => new("相对镜像", "与镜像一致")
        });
    }

    private void Record(string path, string summary)
    {
        // 只留在内存里:写入历史是"这次会话里我动过什么"的备忘,
        // 落盘会把它变成一份需要清理、需要考虑隐私的东西,而它不值那个代价。
        _history.Insert(0, new(DateTimeOffset.UtcNow, path, summary));
        while (_history.Count > 20)
        {
            _history.RemoveAt(_history.Count - 1);
        }
        History.Clear();
        foreach (var record in _history)
        {
            History.Add(record);
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>这次改动的摘要,进写入历史(<c>+2 −1</c> 这种)。</summary>
    private string DescribeEdit()
    {
        var diff = LineDiff.Compute(_originalText, EditorText);
        var added = diff.Count(l => l.Marker is DiffMarker.Added or DiffMarker.Changed);
        var removed = diff.Count(l => l.Marker is DiffMarker.Removed or DiffMarker.Changed);
        return _originalText.Length == 0 ? "新建"
            : added == 0 && removed == 0 ? "无改动"
            : $"+{added} −{removed}";
    }

    /// <summary>
    /// 保存后跑一次重载命令。
    /// <para>
    /// 失败**不**回滚文件 —— 文件已经写进去了,那是既成事实;
    /// 假装"保存失败"会让用户以为可以重来一次,而实际上容器里的内容已经变了。
    /// </para>
    /// </summary>
    private async Task RunReloadAsync(DockerClient client)
    {
        var command = ReloadCommand;
        try
        {
            var result = await client
                .ExecCaptureAsync(containerId, ["/bin/sh", "-c", command], cancellationToken: shell.Lifetime)
                .ConfigureAwait(true);
            if (result.ExitCode == 0)
            {
                shell.Feedback.Notify(FeedbackKind.Success, "已重载配置", $"{containerName}:{command}");
                return;
            }
            shell.Feedback.Notify(FeedbackKind.Warning, "文件已写回,但重载失败",
                $"{command} 退出码 {result.ExitCode}\n{result.StandardError.Trim()}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.Notify(FeedbackKind.Warning, "文件已写回,但重载没跑成", $"{command}:{ex.Message}");
        }
    }

    private void ApplyEntryView() => RebuildTree();

    // ── 文件树 ────────────────────────────────────────────────────
    //
    // 设计稿 04 号板的左栏是一棵**常驻**的树,不是"打开文件就消失的目录列表":
    // 改配置这件事本来就是在几个相邻文件之间来回跳,
    // 每跳一次都要先关掉编辑器再重新走一遍目录,是这一页最费手的地方。

    /// <summary>左栏那棵树:已展开的节点摊平成一列(界面绑这个)。</summary>
    public ObservableCollection<FileEntryItem> Tree { get; } = [];

    /// <summary>左栏标题:哪个容器的文件系统。</summary>
    public string TreeTitle => $"{containerName} 文件系统";

    private FileEntryItem? _root;

    /// <summary>展开 / 收起一个目录,并把它记成"当前目录"(上传落到这里)。</summary>
    private async Task ToggleAsync(FileEntryItem item)
    {
        if (!item.IsDirectory)
        {
            return;
        }
        if (item.Expanded)
        {
            item.Expanded = false;
        }
        else
        {
            await ExpandAsync(item).ConfigureAwait(true);
        }
        Path = item.FullPath;
        RebuildTree();
    }

    /// <summary>展开一个目录。第一次展开才去列 —— 一上来递归整棵树会把隧道占满。</summary>
    private async Task ExpandAsync(FileEntryItem item)
    {
        if (item.ChildrenLoaded)
        {
            item.Expanded = true;
            return;
        }
        if (shell.Client is not { } client)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            var entries = await client
                .ListDirectoryAsync(containerId, item.FullPath, shell.Lifetime).ConfigureAwait(true);
            item.Children.Clear();
            // 目录在前、再按名字排:树里最常做的动作是"往下钻",
            // 目录混在文件中间会让每一层都要重新找一遍。
            foreach (var entry in entries
                         .OrderByDescending(e => e.IsDirectory)
                         .ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                item.Children.Add(new(entry, _changes.GetValueOrDefault(entry.FullPath, ""))
                {
                    Depth = item.Depth + 1
                });
            }
            item.ChildrenLoaded = true;
            item.Expanded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>把展开着的部分摊平成一列。</summary>
    private void RebuildTree()
    {
        Tree.Clear();
        if (_root is null)
        {
            return;
        }
        Append(_root);
        return;

        void Append(FileEntryItem node)
        {
            // 目录永远留着 —— 藏掉目录会让"只看变更"变成一个走不进任何子目录的死胡同。
            if (!ChangedOnly || node.IsDirectory || node.HasChange)
            {
                Tree.Add(node);
            }
            if (!node.Expanded)
            {
                return;
            }
            foreach (var child in node.Children)
            {
                Append(child);
            }
        }
    }

    /// <summary>建根节点并展开一层。</summary>
    private async Task BuildTreeAsync()
    {
        _root = new(new("/", "/", true, false, 0, "", "", "", null), "");
        await ExpandAsync(_root).ConfigureAwait(true);
        RebuildTree();
    }

    /// <summary>把树里指向某个路径的那一行标成"正在编辑"。</summary>
    private void MarkCurrentFile(string? path)
    {
        foreach (var node in Tree)
        {
            node.Current = node.FullPath == path;
        }
    }

    /// <summary>在**已展开过**的那部分树里按路径找一个节点。</summary>
    private FileEntryItem? FindNode(string path)
    {
        if (_root is null)
        {
            return null;
        }
        var node = _root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Children.FirstOrDefault(c => c.Name == segment) is not { } child)
            {
                return null;
            }
            node = child;
        }
        return node;
    }

    /// <summary>
    /// 一路展开到某个路径。从抽屉的「挂载」那一节点进来时用 ——
    /// 用户点的是一个挂载点,期待的是"树已经停在那里",而不是"从 / 自己找过去"。
    /// </summary>
    private async Task ExpandToAsync(string path)
    {
        if (_root is null)
        {
            return;
        }
        var node = _root;
        await ExpandAsync(node).ConfigureAwait(true);
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Children.FirstOrDefault(c => c.Name == segment) is not { } child)
            {
                break;
            }
            node = child;
            if (node.IsDirectory)
            {
                await ExpandAsync(node).ConfigureAwait(true);
            }
        }
        if (node.IsDirectory)
        {
            Path = node.FullPath;
        }
        RebuildTree();
    }

    /// <summary>把整棵树丢掉重来。变更标记也重新取一次 —— 它决定了 A/C/D 和「只看变更」。</summary>
    private async Task ReloadTreeAsync()
    {
        await LoadChangesAsync().ConfigureAwait(true);
        var previous = Path;
        _root = null;
        await BuildTreeAsync().ConfigureAwait(true);
        if (previous != "/")
        {
            await ExpandToAsync(previous).ConfigureAwait(true);
        }
    }

    /// <summary>把本地一个文件送进容器当前目录。</summary>
    /// <summary>接住拖进来的那个文件,当成上传到当前目录。</summary>
    public Task UploadDroppedAsync(IStorageFile file) => UploadFileAsync(file);

    private async Task UploadAsync()
    {
        var source = await FilePicker.PickOpenAsync($"上传到 {Path}").ConfigureAwait(true);
        if (source is not null)
        {
            await UploadFileAsync(source).ConfigureAwait(true);
        }
    }

    private async Task UploadFileAsync(IStorageFile source)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        byte[] content;
        await using (var input = await source.OpenReadAsync().ConfigureAwait(true))
        {
            if (input.CanSeek && input.Length > DockerClient.MaxUploadFileBytes)
            {
                Error = $"{source.Name} 有 {Humanize.Bytes(input.Length)},超过单文件上传上限 " +
                        $"{Humanize.Bytes(DockerClient.MaxUploadFileBytes)}。大文件请用 scp 之类的通道。";
                return;
            }
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, shell.Lifetime).ConfigureAwait(true);
            content = buffer.ToArray();
        }
        var target = Path.TrimEnd('/') + "/" + source.Name;
        var exists = FindNode(target) is not null;
        var confirmed = await shell.Confirm.AskAsync(shell.BuildConfirm(new()
        {
            Title = exists ? $"覆盖容器内的 {source.Name}?" : $"上传 {source.Name} 到 {containerName}?",
            Icon = exists ? "Docker.shield-alert" : "Icon.upload",
            // 覆盖已有文件是不可撤销的,要手打确认串;新建一个文件不是,点一下就够。
            Tier = exists ? ConfirmTier.DataLoss : ConfirmTier.Destructive,
            ConfirmWord = "overwrite",
            ConfirmLabel = exists ? "覆盖" : "上传",
            ConfirmIcon = "Icon.upload",
            HostName = "",
            Commands = [$"docker cp {source.Name} {containerName}:{target}"],
            CommandNote = $"{Humanize.Bytes(content.Length)},以 tar 流写入容器可写层,不经过 shell。",
            DataLossHeadline = exists ? "容器里同名的那个文件会被整体覆盖" : "",
            DataLossPoints = exists
                ?
                [
                    "Docker 不做备份,也没有回收站。",
                    "改动只在容器的可写层里:容器一旦被删除或重建,它就没了。"
                ]
                : []
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            await client.WriteFileAsync(containerId, target, content, shell.Lifetime).ConfigureAwait(true);
            shell.Feedback.Notify(FeedbackKind.Success, "已上传", $"{target} · {Humanize.Bytes(content.Length)}");
            await ReloadTreeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
            shell.Feedback.ReportError("上传文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 直接跳到某个目录(卷页"浏览卷内文件"用:它已经知道卷挂在容器的哪儿了,
    /// 让用户从 <c>/</c> 一级一级点下去是白费一遍功夫)。
    /// </summary>
    public async Task GoToAsync(string path)
    {
        await EnsureLoadedAsync().ConfigureAwait(true);
        await ExpandToAsync(path).ConfigureAwait(true);
    }

    /// <summary>第一次进这一页时才加载。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await LoadChangesAsync().ConfigureAwait(true);
        await BuildTreeAsync().ConfigureAwait(true);
    }

    private async Task LoadChangesAsync()
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        try
        {
            var changes = await client.ChangesAsync(containerId, shell.Lifetime).ConfigureAwait(true);
            _changes.Clear();
            foreach (var change in changes)
            {
                _changes[change.Path] = change.Marker;
            }
            _diffFailed = false;
            OnPropertiesChanged(nameof(ChangeCount), nameof(ChangeText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拿不到 diff 不该挡住文件浏览 —— 标记只是锦上添花。
            // 但那一行小字得改口:不能继续说"与镜像一致"。
            _diffFailed = true;
            OnPropertyChanged(nameof(ChangeText));
            shell.Context.Log.Debug($"docker diff failed: {ex.Message}");
        }
    }

    private async Task OpenFileAsync(string path)
    {
        if (shell.Client is not { } client)
        {
            return;
        }
        Busy = true;
        Error = "";
        try
        {
            var bytes = await client.ReadFileAsync(containerId, path, DockerClient.MaxEditableFileBytes, shell.Lifetime)
                                       .ConfigureAwait(true);
            if (LooksBinary(bytes))
            {
                Error = $"{path} 看起来是二进制文件 —— 面板只在线编辑文本。";
                return;
            }
            _originalText = Encoding.UTF8.GetString(bytes);
            EditorText = _originalText;
            OpenFilePath = path;
            // 从树里取元数据,而不是从"当前目录的列表"里 ——
            // 树上点开的文件所在目录,未必是 Path 指着的那一个。
            _openEntry = FindNode(path)?.Entry;
            if (path.LastIndexOf('/') > 0)
            {
                Path = path[..path.LastIndexOf('/')];
            }
            DiffMode = false;
            BuildFileProperties();
            OnPropertiesChanged(nameof(ModifiedLinesText), nameof(LineEnding), nameof(Language),
                nameof(ReloadCommand), nameof(HasReloadCommand));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is DockerApiException api ? api.Message : ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 二进制探测:前 8 KB 里出现 NUL 就当二进制。
    /// <para>
    /// 把一个 ELF 塞进文本编辑器,用户点一次保存就把它毁了 ——
    /// 而"毁了"这件事要等到容器下次启动失败才会被发现。
    /// </para>
    /// </summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private async Task SaveAsync()
    {
        if (shell.Client is not { } client || OpenFilePath is not { } path || !IsModified)
        {
            return;
        }
        var confirmed = await shell.Confirm.AskAsync(shell.BuildConfirm(new()
        {
            Title = $"覆盖容器内的 {OpenFileName}?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "save",
            ConfirmLabel = "写回容器",
            ConfirmIcon = "Icon.save",
            HostName = "",
            Commands = [$"PUT /containers/{containerName}/archive?path={OpenFileDirectory}"],
            CommandNote = "整个文件以 tar 流写回容器可写层,不经过 shell。",
            DataLossHeadline = "原文件会被整体覆盖,无法撤销",
            DataLossPoints =
            [
                "Docker 不做备份,也没有回收站 —— 写回去就是写回去了。",
                "改动只在容器的可写层里:容器一旦被删除或重建,它就没了。",
                "多数服务不会自动重载配置,保存后多半还要 reload 或重启容器。"
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        Busy = true;
        try
        {
            await client.WriteFileAsync(containerId, path, Encoding.UTF8.GetBytes(EditorText), shell.Lifetime)
                        .ConfigureAwait(true);
            Record(path, DescribeEdit());
            _originalText = EditorText;
            OnPropertiesChanged(nameof(IsModified), nameof(ModifiedText), nameof(ModifiedLinesText));
            RebuildEditorLines();
            shell.Feedback.Notify(FeedbackKind.Success, "已写回容器", $"{path} · {Humanize.Bytes(Encoding.UTF8.GetByteCount(EditorText))}");
            await LoadChangesAsync().ConfigureAwait(true);
            if (ReloadAfterSave && HasReloadCommand)
            {
                await RunReloadAsync(client).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.ReportError("写回文件", ex);
        }
        finally
        {
            Busy = false;
        }
    }
}
