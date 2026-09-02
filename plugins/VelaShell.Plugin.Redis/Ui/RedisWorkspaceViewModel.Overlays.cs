using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>分段控件里的一格(类型 / 格式 / 范围共用)。</summary>
public sealed class RedisChoice : ObservableObject
{
    /// <summary>显示文本。</summary>
    public required string Label { get; init; }

    /// <summary>取值(类型名 / 格式名)。</summary>
    public required string Value { get; init; }

    /// <summary>是否选中。</summary>
    public bool IsOn
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>
/// 新建键的面板。
/// <para>
/// <b>「新建」这个词在用户脑子里就是「原来没有」</b> —— 所以它一律走不覆盖的路
/// (字符串 <c>SET … NX</c>,其余类型先 <c>EXISTS</c>),撞名时停下来说清,而不是悄悄盖掉。
/// </para>
/// </summary>
public sealed class RedisNewKeyForm : ObservableObject
{
    private readonly Loc _loc;
    private readonly Func<Task> _submit;

    /// <summary>构造。</summary>
    /// <param name="loc">文案表。</param>
    /// <param name="submit">提交回调(由工作台视图模型提供)。</param>
    internal RedisNewKeyForm(Loc loc, Func<Task> submit)
    {
        _loc = loc;
        _submit = submit;
        foreach (string type in (string[])["string", "hash", "list", "set", "zset", "stream"])
        {
            Types.Add(new() { Label = type, Value = type, IsOn = type is "string" });
        }
        UseTypeCommand = new(choice =>
        {
            if (choice is not null)
            {
                TypeName = choice.Value;
            }
            return Task.CompletedTask;
        });
        SubmitCommand = new(_submit, () => KeyName.Trim().Length > 0 && !IsBusy);
        CancelCommand = new(() =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
    }

    /// <summary>面板是否打开。</summary>
    public bool IsOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>正在提交(防重复点击)。</summary>
    public bool IsBusy
    {
        get;
        internal set
        {
            SetProperty(ref field, value);
            SubmitCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>键名。</summary>
    public string KeyName
    {
        get;
        set
        {
            SetProperty(ref field, value);
            SubmitCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>可选类型。</summary>
    public IList<RedisChoice> Types { get; } = [];

    /// <summary>当前类型。</summary>
    public string TypeName
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            foreach (RedisChoice choice in Types)
            {
                choice.IsOn = string.Equals(choice.Value, field, StringComparison.Ordinal);
            }
            RaisePropertyChanged(nameof(NeedsField));
            RaisePropertyChanged(nameof(FieldLabel));
        }
    } = "string";

    /// <summary>这个类型需不需要额外的字段名 / 分值。</summary>
    public bool NeedsField => TypeName is "hash" or "zset" or "stream";

    /// <summary>字段栏的标签(按类型换措辞:哈希是字段名、有序集合是分值)。</summary>
    public string FieldLabel => TypeName switch
    {
        "zset" => _loc["Redis_ColumnScore"],
        "stream" => _loc["Redis_ColumnField"],
        _ => _loc["Redis_ColumnField"]
    };

    /// <summary>字段名 / 分值。</summary>
    public string FieldText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>初始值。</summary>
    public string ValueText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>TTL 输入。</summary>
    public string TtlText
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(TtlPreview));
        }
    } = string.Empty;

    /// <summary>TTL 的实时换算回显(与键详情那一栏同一条规矩:换算给他看)。</summary>
    public string TtlPreview
    {
        get
        {
            if (TtlText.Trim().Length == 0)
            {
                return _loc["Redis_NewKeyTtlHint"];
            }
            if (!RedisTtl.TryParse(TtlText, DateTimeOffset.Now, out TimeSpan ttl))
            {
                return _loc["Redis_TtlInvalid"];
            }
            DateTimeOffset expiry = DateTimeOffset.Now + ttl;
            return _loc.Format("Redis_TtlPreview",
                expiry.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture), RedisTtl.Describe(ttl));
        }
    }

    /// <summary>撞名 / 失败说明。</summary>
    public string Notice
    {
        get;
        internal set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasNotice));
        }
    } = string.Empty;

    /// <summary>有说明要显示。</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>切换类型。</summary>
    public AsyncCommand<RedisChoice?> UseTypeCommand { get; }

    /// <summary>创建。</summary>
    public AsyncCommand SubmitCommand { get; }

    /// <summary>取消。</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>打开面板并复位。</summary>
    /// <param name="seed">键名预填(从当前前缀带过来)。</param>
    internal void Open(string seed)
    {
        KeyName = seed;
        TypeName = "string";
        FieldText = string.Empty;
        ValueText = string.Empty;
        TtlText = string.Empty;
        Notice = string.Empty;
        IsBusy = false;
        IsOpen = true;
    }
}

/// <summary>
/// 导入 / 导出面板。
/// <para>
/// 两个方向共用一张表单:它们的字段完全一样(格式、范围、路径),做成两个面板只会让
/// "我刚才是在导入还是导出"变成一个需要看标题才能回答的问题。
/// </para>
/// </summary>
public sealed class RedisTransferForm : ObservableObject
{
    private readonly Loc _loc;

    /// <summary>构造。</summary>
    /// <param name="loc">文案表。</param>
    /// <param name="submit">提交回调。</param>
    internal RedisTransferForm(Loc loc, Func<Task> submit)
    {
        _loc = loc;
        foreach ((string label, string value) in ((string, string)[])
                 [("DUMP + RESTORE", "dump"), ("RESP", "resp"), ("JSONL", "jsonl")])
        {
            Formats.Add(new() { Label = label, Value = value, IsOn = value is "dump" });
        }
        UseFormatCommand = new(choice =>
        {
            if (choice is not null)
            {
                Format = choice.Value;
            }
            return Task.CompletedTask;
        });
        UseScopeCommand = new(choice =>
        {
            SelectedOnly = choice?.Value is "selected";
            return Task.CompletedTask;
        });
        SubmitCommand = new(submit, () => Path.Trim().Length > 0 && !IsBusy);
        CancelCommand = new(() =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
    }

    /// <summary>面板是否打开。</summary>
    public bool IsOpen
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>这一趟是导出(false = 导入)。</summary>
    public bool IsExport
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(SubmitLabel));
            RaisePropertyChanged(nameof(ShowsScope));
            RaisePropertyChanged(nameof(Note));
        }
    } = true;

    /// <summary>标题。</summary>
    public string Title => _loc[IsExport ? "Redis_ExportTitle" : "Redis_ImportTitle"];

    /// <summary>提交按钮文案。</summary>
    public string SubmitLabel => _loc[IsExport ? "Redis_Export" : "Redis_Import"];

    /// <summary>导入没有"范围"可选(范围由文件内容决定)。</summary>
    public bool ShowsScope => IsExport;

    /// <summary>底部那段取舍说明。</summary>
    public string Note => _loc[IsExport ? "Redis_ExportNote" : "Redis_ImportNote"];

    /// <summary>可选格式。</summary>
    public IList<RedisChoice> Formats { get; } = [];

    /// <summary>当前格式(<c>dump</c> / <c>resp</c> / <c>jsonl</c>)。</summary>
    public string Format
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            foreach (RedisChoice choice in Formats)
            {
                choice.IsOn = string.Equals(choice.Value, field, StringComparison.Ordinal);
            }
        }
    } = "dump";

    /// <summary>范围:只导出勾选的键。</summary>
    public bool SelectedOnly
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            foreach (RedisChoice choice in Scopes)
            {
                choice.IsOn = (choice.Value is "selected") == field;
            }
        }
    } = true;

    /// <summary>可选范围。</summary>
    public IList<RedisChoice> Scopes { get; } = [];

    /// <summary>落盘 / 读取路径。</summary>
    public string Path
    {
        get;
        set
        {
            SetProperty(ref field, value);
            SubmitCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>结果 / 失败说明。</summary>
    public string Notice
    {
        get;
        internal set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasNotice));
        }
    } = string.Empty;

    /// <summary>有说明要显示。</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>正在跑。</summary>
    public bool IsBusy
    {
        get;
        internal set
        {
            SetProperty(ref field, value);
            SubmitCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>切换格式。</summary>
    public AsyncCommand<RedisChoice?> UseFormatCommand { get; }

    /// <summary>切换范围。</summary>
    public AsyncCommand<RedisChoice?> UseScopeCommand { get; }

    /// <summary>执行。</summary>
    public AsyncCommand SubmitCommand { get; }

    /// <summary>取消。</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>打开导出面板。</summary>
    /// <param name="checkedCount">已勾选的键数。</param>
    /// <param name="scannedCount">已扫到的键数。</param>
    /// <param name="defaultPath">默认落盘路径。</param>
    internal void OpenExport(int checkedCount, int scannedCount, string defaultPath)
    {
        Scopes.Clear();
        Scopes.Add(new()
        {
            Label = _loc.Format("Redis_ExportScopeSelected", checkedCount.ToString("N0", CultureInfo.CurrentCulture)),
            Value = "selected",
            IsOn = checkedCount > 0
        });
        Scopes.Add(new()
        {
            Label = _loc.Format("Redis_ExportScopeScanned", scannedCount.ToString("N0", CultureInfo.CurrentCulture)),
            Value = "scanned",
            IsOn = checkedCount == 0
        });
        SelectedOnly = checkedCount > 0;
        IsExport = true;
        Path = defaultPath;
        Notice = string.Empty;
        IsBusy = false;
        IsOpen = true;
    }

    /// <summary>打开导入面板。</summary>
    /// <param name="defaultPath">默认读取路径。</param>
    internal void OpenImport(string defaultPath)
    {
        IsExport = false;
        Path = defaultPath;
        Notice = string.Empty;
        IsBusy = false;
        IsOpen = true;
    }
}

/// <summary>面板内的两个覆盖层:新建键、导入 / 导出。</summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>新建键面板。</summary>
    public RedisNewKeyForm NewKey { get; private set; } = null!;

    /// <summary>导入 / 导出面板。</summary>
    public RedisTransferForm Transfer { get; private set; } = null!;

    /// <summary>打开新建键面板。</summary>
    public AsyncCommand NewKeyCommand { get; private set; } = null!;

    /// <summary>打开导出面板。</summary>
    public AsyncCommand OpenExportCommand { get; private set; } = null!;

    /// <summary>打开导入面板。</summary>
    public AsyncCommand OpenImportCommand { get; private set; } = null!;

    private void InitializeOverlays()
    {
        NewKey = new(Loc, CreateKeyAsync);
        Transfer = new(Loc, RunTransferAsync);
        NewKeyCommand = new(() =>
        {
            // 键名预填当前过滤条的前缀:新建的键十有八九和你正在看的那一批同族。
            NewKey.Open(MatchMode == RedisMatchMode.Prefix ? Filter : string.Empty);
            return Task.CompletedTask;
        }, () => CanWrite);
        OpenExportCommand = new(() =>
        {
            Transfer.OpenExport(CheckedCount, MatchedCount, DefaultTransferPath("export"));
            return Task.CompletedTask;
        });
        OpenImportCommand = new(() =>
        {
            Transfer.OpenImport(DefaultTransferPath("export"));
            return Task.CompletedTask;
        }, () => CanWrite);
    }

    /// <summary>导出面板的默认路径:用户目录下一个按连接与库命名的文件。</summary>
    private string DefaultTransferPath(string kind)
    {
        string safe = new([.. Title.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')]);
        string extension = Transfer?.Format switch
        {
            "resp" => "redis",
            "jsonl" => "jsonl",
            _ => "rdbdump"
        };
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "redis-" + kind,
            $"{safe}-db{CurrentDatabase.ToString(CultureInfo.InvariantCulture)}.{extension}");
    }

    private async Task CreateKeyAsync()
    {
        string name = NewKey.KeyName.Trim();
        if (name.Length == 0)
        {
            return;
        }
        // 键名与值都按转义解回字节:二进制安全从"新建"这一步就开始,而不是等到编辑时才补。
        if (!RedisValueText.TryUnescape(name, out byte[] rawName, out string? nameError))
        {
            NewKey.Notice = Loc.Format("Redis_BadEscape", nameError ?? string.Empty);
            return;
        }
        if (!RedisValueText.TryUnescape(NewKey.ValueText, out byte[] rawValue, out string? valueError))
        {
            NewKey.Notice = Loc.Format("Redis_BadEscape", valueError ?? string.Empty);
            return;
        }
        TimeSpan? ttl = null;
        if (NewKey.TtlText.Trim().Length > 0)
        {
            if (!RedisTtl.TryParse(NewKey.TtlText, DateTimeOffset.Now, out TimeSpan parsed))
            {
                NewKey.Notice = Loc["Redis_TtlInvalid"];
                return;
            }
            ttl = parsed;
        }

        var key = new RedisKeyName(rawName);
        NewKey.IsBusy = true;
        try
        {
            await GuardedAsync(NewKey.TypeName is "string" ? "SET" : WriteCommandFor(NewKey.TypeName), async () =>
            {
                bool created = await _connection
                    .CreateKeyAsync(key, NewKey.TypeName, rawValue, NewKey.FieldText.Trim(), ttl)
                    .ConfigureAwait(true);
                if (!created)
                {
                    // 撞名不是错误,是一次"这不是新建"的如实告知:面板留着,话说在面板上。
                    NewKey.Notice = Loc.Format("Redis_NewKeyExists", key.Display);
                    return;
                }
                NewKey.IsOpen = false;
                StatusMessage = Loc.Format("Redis_NewKeyCreated", key.Display);
                // 新键要能立刻看见:按它的完整名字精确扫一次,再选中。
                await JumpToAsync(key.Display).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        finally
        {
            NewKey.IsBusy = false;
        }
    }

    private async Task RunTransferAsync()
    {
        string path = ExpandPath(Transfer.Path);
        if (path.Length == 0)
        {
            Transfer.Notice = Loc["Redis_PathRequired"];
            return;
        }
        Transfer.IsBusy = true;
        try
        {
            if (Transfer.IsExport)
            {
                await RunExportAsync(path).ConfigureAwait(true);
            }
            else
            {
                await RunImportAsync(path).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Transfer.Notice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Transfer failed.", ex);
        }
        finally
        {
            Transfer.IsBusy = false;
        }
    }

    private async Task RunExportAsync(string path)
    {
        IReadOnlyList<RedisKeyName> keys = KeysForTransfer(Transfer.SelectedOnly);
        if (keys.Count == 0)
        {
            Transfer.Notice = Loc["Redis_BatchNothing"];
            return;
        }
        RedisExportFormat format = Transfer.Format switch
        {
            "resp" => RedisExportFormat.RespCommands,
            "jsonl" => RedisExportFormat.Jsonl,
            _ => RedisExportFormat.DumpRestore
        };
        RedisExportResult result = await _connection.ExportAsync(keys, format, path).ConfigureAwait(true);
        string size = result.Bytes switch
        {
            < 1024 => $"{result.Bytes} B",
            < 1024 * 1024 => $"{result.Bytes / 1024.0:0.#} KB",
            _ => $"{result.Bytes / (1024.0 * 1024):0.#} MB"
        };
        string notice = Loc.Format("Redis_ExportDone",
            result.Keys.ToString("N0", CultureInfo.CurrentCulture), size, result.Path);
        if (result.Skipped.Count > 0)
        {
            // 跳过了几条必须说 —— 一个只报"已导出 8 个键"的提示,会让用户以为那 2 个也在里面。
            notice += "  ·  " + Loc.Format("Redis_ExportSkipped",
                result.Skipped.Count.ToString("N0", CultureInfo.CurrentCulture));
        }
        Transfer.Notice = notice;
        StatusMessage = notice;
    }

    private async Task RunImportAsync(string path)
    {
        if (!File.Exists(path))
        {
            Transfer.Notice = Loc.Format("Redis_FileMissing", path);
            return;
        }
        bool confirmed = await Confirmation.AskAsync(
            Loc.Format("Redis_ImportConfirmTitle", Path.GetFileName(path), $"{Endpoint} db{CurrentDatabase}"),
            Loc["Redis_ImportConfirmBody"],
            $"{Endpoint}  db{CurrentDatabase}",
            Loc["Redis_Import"],
            Loc["Redis_Cancel"],
            destructive: true,
            expectedText: IsProduction ? Console.ConfirmationPhrase : null).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        int replayed = 0;
        int failed = 0;
        foreach (string raw in await File.ReadAllLinesAsync(path).ConfigureAwait(true))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            // 每一行都过一次同样的护栏:只读模式下第一行就会被拦下,而不是写到一半才发现。
            string command = RedisCommandLine.TrySplit(line, out IReadOnlyList<string> args, out _) && args.Count > 0
                ? args[0]
                : line;
            RedisCommandVerdict verdict = _connection.Guard.Evaluate(command);
            if (!verdict.Allowed)
            {
                Transfer.Notice = Loc.Format("Redis_BlockedByReadOnly", command);
                break;
            }
            try
            {
                RedisConsoleResult result = await _connection.ExecuteConsoleAsync(line).ConfigureAwait(true);
                if (result.IsError)
                {
                    failed++;
                }
                else
                {
                    replayed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _log.Info($"Import line failed: {ex.Message}");
            }
        }
        string notice = Loc.Format("Redis_ImportDone",
            replayed.ToString("N0", CultureInfo.CurrentCulture),
            failed.ToString("N0", CultureInfo.CurrentCulture));
        Transfer.Notice = notice;
        StatusMessage = notice;
        Transfer.IsOpen = false;
        await ScanAsync(restart: true).ConfigureAwait(true);
    }

    /// <summary>把 <c>~</c> 展开成用户目录 —— 面板上填的是给人看的路径。</summary>
    private static string ExpandPath(string path)
    {
        string trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        return trimmed is ['~', '/' or '\\', ..]
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed[2..])
            : trimmed;
    }
}
