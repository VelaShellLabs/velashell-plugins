using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合设计 · 索引页下半部分的「新建索引」编辑器(设计稿 07,Navicat 的设计表做法)。
/// <para>
/// 编辑器开着时索引表末尾多一行「新建」草稿,跟着表单实时变(键、类型、属性、预估大小),
/// 下半部分整宽分三栏:字段表(序号 / 字段 / 排序 / ESR / 抽样类型 / 上移下移删除)、类型与选项、命令预览与预估。
/// 原先挤在右侧 340px 面板里,字段行一窄就点错 —— 摊开之后每一列都有自己的位置。
/// </para>
/// </summary>
internal sealed partial class DesignTabViewModel
{
    private string _newKind = "normal";
    private bool _newUnique;
    private bool _newSparse;
    private bool _newHidden;
    private bool _newTtl;
    private bool _newPartial;
    private string _newPartialText = "{ }";
    private bool _nameEdited;
    private bool _settingName;

    /// <summary>编辑器开着(下半部分从索引建议换成编辑器,索引表收矮、末尾挂草稿行)。</summary>
    public bool IsCreateOpen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasDraft));
            }
        }
    }

    /// <summary>索引表末尾的「新建」草稿行;还没有字段时为 <see langword="null" />。</summary>
    public IndexRow? DraftIndex
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasDraft));
            }
        }
    }

    /// <summary>草稿行可见(创建期间表里挂的是「构建中」那一行,草稿让位)。</summary>
    public bool HasDraft => IsCreateOpen && DraftIndex is not null && !IsCreating;

    /// <summary>编辑器底栏左侧那句「将在 shop.orders 上执行 createIndexes」。</summary>
    public string NewTarget => Loc.Format("Design_EditorTarget", Namespace);

    /// <summary>键字段行。</summary>
    public ObservableCollection<NewKeyRow> NewKeys { get; } = [];

    /// <summary>索引类型分段:<c>normal / text / 2dsphere / hashed / wildcard</c>。</summary>
    public string NewKind
    {
        get => _newKind;
        set
        {
            if (SetProperty(ref _newKind, value ?? "normal"))
            {
                foreach (NewKeyRow row in NewKeys)
                {
                    row.Kind = _newKind;
                }
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>唯一。</summary>
    public bool NewUnique
    {
        get => _newUnique;
        set
        {
            if (SetProperty(ref _newUnique, value))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>稀疏。</summary>
    public bool NewSparse
    {
        get => _newSparse;
        set
        {
            if (SetProperty(ref _newSparse, value))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>创建后隐藏(灰度验证:先建好,观察没问题再放开给优化器)。</summary>
    public bool NewHidden
    {
        get => _newHidden;
        set
        {
            if (SetProperty(ref _newHidden, value))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>TTL。</summary>
    public bool NewTtl
    {
        get => _newTtl;
        set
        {
            if (SetProperty(ref _newTtl, value))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>TTL 秒数。</summary>
    public string NewTtlSeconds
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                RaisePropertyChanged(nameof(NewTtlHuman));
                RecomputeNewIndex();
            }
        }
    } = "86400";

    /// <summary>TTL 秒数换算成人话。</summary>
    public string NewTtlHuman => long.TryParse(NewTtlSeconds, out long s) ? "= " + Duration(s) : "";

    /// <summary>部分索引。</summary>
    public bool NewPartial
    {
        get => _newPartial;
        set
        {
            if (SetProperty(ref _newPartial, value))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>partialFilterExpression 的文本(mongosh 写法)。</summary>
    public string NewPartialText
    {
        get => _newPartialText;
        set
        {
            if (SetProperty(ref _newPartialText, value ?? ""))
            {
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>索引名(默认按键自动生成;手改过就不再跟着变)。</summary>
    public string NewName
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? "") && !_settingName)
            {
                _nameEdited = field.Length > 0;
                RaisePropertyChanged(nameof(NewNameHint));
                RecomputeNewIndex();
            }
        }
    } = "";

    /// <summary>名称右侧的小字:自动生成 / 已自定义。</summary>
    public string NewNameHint => _nameEdited ? Loc["Design_NameCustom"] : Loc["Design_NameAuto"];

    /// <summary>预估框第一行(<c>预计大小 ≈ 140 MB · 构建约 3 分钟</c>)。</summary>
    public string NewImpactTitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>预估框说明。</summary>
    public string NewImpactDetail
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>命令预览。</summary>
    public string NewCommand
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>表单错误(创建按钮因此不可用);没有为空。</summary>
    public string NewError
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasNewError));
                CreateIndexCommand.RaiseCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>有表单错误。</summary>
    public bool HasNewError => NewError.Length > 0;

    /// <summary>正在创建(按钮转圈)。</summary>
    public bool IsCreating
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasDraft));
                CreateIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>抽样到的字段(字段下拉的选项)。</summary>
    public IReadOnlyList<FieldOption> FieldOptions
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                foreach (NewKeyRow row in NewKeys)
                {
                    row.RefreshSwatch();
                }
            }
        }
    } = [];

    /// <summary>打开面板(空白表单)。</summary>
    public RelayCommand OpenCreateCommand { get; private set; } = null!;

    /// <summary>关闭面板。</summary>
    public RelayCommand CloseCreateCommand { get; private set; } = null!;

    /// <summary>添加一个字段行。</summary>
    public RelayCommand AddKeyCommand { get; private set; } = null!;

    /// <summary>删除一个字段行。</summary>
    public RelayCommand<NewKeyRow> RemoveKeyCommand { get; private set; } = null!;

    /// <summary>字段行上移一位(键序前移)。</summary>
    public RelayCommand<NewKeyRow> MoveKeyUpCommand { get; private set; } = null!;

    /// <summary>字段行下移一位。</summary>
    public RelayCommand<NewKeyRow> MoveKeyDownCommand { get; private set; } = null!;

    /// <summary>创建索引。</summary>
    public AsyncCommand CreateIndexCommand { get; private set; } = null!;

    private void InitializeNewIndex()
    {
        OpenCreateCommand = new(() => OpenCreatePanel(null, null));
        CloseCreateCommand = new(() => IsCreateOpen = false);
        AddKeyCommand = new(() => AddKey("", 1, ""));
        RemoveKeyCommand = new(row =>
        {
            _ = NewKeys.Remove(row);
            RenumberKeys();
            RecomputeNewIndex();
        });
        MoveKeyUpCommand = new(row => MoveKey(NewKeys.IndexOf(row), NewKeys.IndexOf(row) - 1));
        MoveKeyDownCommand = new(row => MoveKey(NewKeys.IndexOf(row), NewKeys.IndexOf(row) + 1));
        CreateIndexCommand = new(CreateIndexAsync, () => !IsCreating && NewError.Length == 0 && NewKeys.Any(static k => k.Field.Length > 0));
    }

    /// <summary>
    /// 打开面板。从建议卡片的「创建此索引」进来时带着建议键与查询形状(字段行的 等值 / 排序 / 范围 标签由形状给);
    /// 从工具行进来是一行空字段。
    /// </summary>
    internal void OpenCreatePanel(BsonDocument? key, EsrShape? shape)
    {
        NewKeys.Clear();
        _newKind = "normal";
        RaisePropertyChanged(nameof(NewKind));
        _newUnique = _newSparse = _newHidden = _newTtl = _newPartial = false;
        RaisePropertiesChanged(nameof(NewUnique), nameof(NewSparse), nameof(NewHidden), nameof(NewTtl), nameof(NewPartial));
        _newPartialText = "{ }";
        RaisePropertyChanged(nameof(NewPartialText));
        _nameEdited = false;
        RaisePropertyChanged(nameof(NewNameHint));
        if (key is { ElementCount: > 0 })
        {
            foreach (BsonElement element in key)
            {
                int direction = element.Value.IsNumeric && element.Value.ToDouble() < 0 ? -1 : 1;
                AddKey(element.Name, direction, RoleText(shape?.RoleOf(element.Name)), recompute: false);
            }
        }
        else
        {
            AddKey("", 1, "", recompute: false);
        }
        IsCreateOpen = true;
        RecomputeNewIndex();
        // 字段下拉、键长估算都靠抽样;还没分析过就在后台抽一次(不切页)。
        if (Schema is null && !IsAnalyzing)
        {
            _ = AnalyzeAsync();
        }
    }

    /// <summary>加一行字段。</summary>
    internal void AddKey(string field, int direction, string role, bool recompute = true)
    {
        var row = new NewKeyRow(field, direction, role.Length > 0 ? role : GuessRole(field), OptionOf, RecomputeNewIndex, DirectionLabel)
        {
            Kind = _newKind
        };
        NewKeys.Add(row);
        RenumberKeys();
        if (recompute)
        {
            RecomputeNewIndex();
        }
    }

    /// <summary>拖动排序:把一行从 <paramref name="from" /> 挪到 <paramref name="to" />。</summary>
    internal void MoveKey(int from, int to)
    {
        if (from < 0 || from >= NewKeys.Count || to < 0 || to >= NewKeys.Count || from == to)
        {
            return;
        }
        NewKeys.Move(from, to);
        RenumberKeys();
        RecomputeNewIndex();
    }

    private void RenumberKeys()
    {
        for (int i = 0; i < NewKeys.Count; i++)
        {
            NewKeys[i].Number = i + 1;
            NewKeys[i].IsFirst = i == 0;
            NewKeys[i].IsLast = i == NewKeys.Count - 1;
        }
    }

    /// <summary>键值 → 方向下拉上的字(<c>1  升序</c> / <c>-1  降序</c> / <c>text</c>)。</summary>
    internal string DirectionLabel(BsonValue value) =>
        value.IsNumeric
            ? Loc[value.ToDouble() < 0 ? "Design_DirDesc" : "Design_DirAsc"]
            : value.ToString() ?? "";

    /// <summary>字段 → 抽样到的那一项;抽样里没有为 <see langword="null" />。</summary>
    private FieldOption? OptionOf(string field) => FieldOptions.FirstOrDefault(f => f.Path == field);

    /// <summary>ESR 角色 → 小标签文字。</summary>
    private string RoleText(string? role) => role switch
    {
        "E" => Loc["Design_RoleEquality"],
        "S" => Loc["Design_RoleSort"],
        "R" => Loc["Design_RoleRange"],
        _ => ""
    };

    /// <summary>
    /// 手加的字段没有查询形状可依,就按抽样猜一个角色:少量取值的字符串 / 布尔像等值条件,
    /// 数值与日期像范围条件。猜不出就不标 —— 标错比不标更误导。
    /// </summary>
    private string GuessRole(string field)
    {
        if (Schema?[field] is not { } profile)
        {
            return "";
        }
        BsonKind kind = profile.DominantKind;
        if (kind is BsonKind.Boolean || (kind is BsonKind.String or BsonKind.ObjectId && SchemaAnalyzer.IsLowCardinality(profile)))
        {
            return Loc["Design_RoleEquality"];
        }
        return BsonKinds.IsNumeric(kind) || kind == BsonKind.Date ? Loc["Design_RoleRange"] : "";
    }

    /// <summary>当前表单 → 键模式。</summary>
    internal BsonDocument NewKeyDocument()
    {
        var key = new BsonDocument();
        foreach (NewKeyRow row in NewKeys)
        {
            string field = row.Field.Trim();
            if (field.Length == 0)
            {
                continue;
            }
            if (_newKind == "wildcard" && !field.EndsWith("$**", StringComparison.Ordinal))
            {
                field = field == "*" ? "$**" : field + ".$**";
            }
            _ = key.Set(field, row.DirectionValue);
        }
        if (key.ElementCount == 0 && _newKind == "wildcard")
        {
            key["$**"] = 1;
        }
        return key;
    }

    /// <summary>当前表单 → 选项(不含 key / name)。表单不合法时抛 <see cref="FormatException" />,消息即提示。</summary>
    internal BsonDocument NewOptionsDocument(BsonDocument key)
    {
        var options = new BsonDocument();
        if (_newUnique)
        {
            if (_newKind is "text" or "hashed" or "wildcard" or "2dsphere")
            {
                throw new FormatException(Loc["Design_ErrUniqueKind"]);
            }
            options["unique"] = true;
        }
        if (_newSparse)
        {
            options["sparse"] = true;
        }
        if (_newTtl)
        {
            if (key.ElementCount != 1 || _newKind != "normal")
            {
                throw new FormatException(Loc["Design_ErrTtlSingle"]);
            }
            if (!long.TryParse(NewTtlSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds < 0)
            {
                throw new FormatException(Loc["Design_ErrTtlSeconds"]);
            }
            options["expireAfterSeconds"] = seconds <= int.MaxValue ? (BsonValue)(int)seconds : seconds;
        }
        if (_newPartial)
        {
            if (_newSparse)
            {
                throw new FormatException(Loc["Design_ErrSparsePartial"]);
            }
            BsonDocument partial;
            try
            {
                partial = ShellJson.ParseDocument(_newPartialText);
            }
            catch (ShellJsonException ex)
            {
                throw new FormatException(Loc.Format("Design_ErrPartial", ex.Message));
            }
            if (partial.ElementCount == 0)
            {
                throw new FormatException(Loc["Design_ErrPartialEmpty"]);
            }
            options["partialFilterExpression"] = partial;
        }
        if (_newHidden)
        {
            options["hidden"] = true;
        }
        return options;
    }

    /// <summary>表单任何改动后:重算名称、命令预览、预估与错误。</summary>
    private void RecomputeNewIndex()
    {
        BsonDocument key = NewKeyDocument();
        string auto = key.ElementCount == 0 ? "" : IndexAdvisor.DefaultName(key);
        if (!_nameEdited)
        {
            _settingName = true;
            NewName = auto;
            _settingName = false;
        }
        string error = "";
        BsonDocument options = [];
        if (key.ElementCount == 0)
        {
            error = Loc["Design_ErrNoFields"];
        }
        else
        {
            try
            {
                options = NewOptionsDocument(key);
            }
            catch (FormatException ex)
            {
                error = ex.Message;
            }
        }
        if (error.Length == 0 && Indexes.FirstOrDefault(i => i.Name == NewName.Trim()) is { } clash)
        {
            error = Loc.Format("Design_ErrNameExists", clash.Name);
        }
        if (_nameEdited && NewName.Trim() != auto && NewName.Trim().Length > 0)
        {
            options["name"] = NewName.Trim();
        }
        NewError = error;
        NewCommand = key.ElementCount == 0
            ? ""
            : $"{ShellRef}.createIndex(\n  {BsonText.Literal(key)}" + (options.ElementCount > 0 ? $",\n  {BsonText.Literal(options)}" : "") + "\n)";
        long size = UpdateImpact(key);
        DraftIndex = key.ElementCount == 0 ? null : Draft(key, options, size);
        CreateIndexCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>草稿行:与表里的行同一个模型,大小列写预估,使用列写「待创建」。</summary>
    private IndexRow Draft(BsonDocument key, BsonDocument options, long size)
    {
        string name = NewName.Trim().Length > 0 ? NewName.Trim() : IndexAdvisor.DefaultName(key);
        var spec = new BsonDocument { { "key", key }, { "name", name } };
        _ = spec.Merge(options, overwriteExistingElements: false);
        IndexForm form = IndexAdvisor.ShapeOf(spec);
        return new IndexRow
        {
            Name = name,
            Spec = spec,
            Keys = [.. IndexAdvisor.DisplayKeys(spec).Select(static k => new IndexKeyChip(k.Field, k.Value))],
            Form = form,
            TypeText = FormText(form),
            Attributes = Attributes(spec, 0),
            SizeText = "≈ " + BsonText.Bytes(size)
        };
    }

    /// <summary>
    /// 预估:大小 ≈ 文档数 ×(各键字段平均值长 + 每项开销),构建耗时按数据量粗估;
    /// 再写一句当前版本的构建方式(4.4 起的优化构建只在首尾短暂持排他锁)。返回预估大小(字节)。
    /// </summary>
    private long UpdateImpact(BsonDocument key)
    {
        long count = _indexStats?.Count ?? _stats?.Count ?? 0;
        long data = _indexStats?.Size ?? _stats?.Size ?? 0;
        double keyBytes = 0;
        foreach (BsonElement element in key)
        {
            string field = element.Name.Replace(".$**", "", StringComparison.Ordinal);
            keyBytes += Schema?[field] is { ScalarCount: > 0 } profile ? profile.AverageValueBytes : 12;
        }
        long size = IndexAdvisor.EstimateSize(count, keyBytes);
        double seconds = IndexAdvisor.EstimateBuildSeconds(count, data);
        string time = seconds < 60
            ? Loc.Format("Design_Seconds", Math.Max(1, (int)Math.Ceiling(seconds)))
            : Loc.Format("Design_Minutes", Math.Ceiling(seconds / 60).ToString("0", CultureInfo.InvariantCulture));
        NewImpactTitle = Loc.Format("Design_ImpactTitle", BsonText.Bytes(size), time);
        int major = Workspace.Connection.Server.Major;
        string detail = major is 0 or >= 5 || Workspace.Connection.Server.Version.StartsWith("4.4", StringComparison.Ordinal)
            ? Loc["Design_ImpactOptimized"]
            : Loc["Design_ImpactLegacy"];
        if (_newPartial)
        {
            detail += " " + Loc["Design_ImpactPartial"];
        }
        NewImpactDetail = detail;
        return size;
    }

    /// <summary>
    /// 创建索引:过写护栏 → 生产连接确认 → <c>createIndexes</c>。构建期间表里先挂一行"构建中",
    /// 每秒从 <c>currentOp</c> 刷进度;命令返回(建好或失败)后整页重载。
    /// </summary>
    private async Task CreateIndexAsync()
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        BsonDocument key = NewKeyDocument();
        BsonDocument options;
        try
        {
            options = NewOptionsDocument(key);
        }
        catch (FormatException ex)
        {
            NewError = ex.Message;
            return;
        }
        string name = NewName.Trim().Length > 0 ? NewName.Trim() : IndexAdvisor.DefaultName(key);
        if (Workspace.Guard.ConfirmWrites || Workspace.Guard.IsProduction)
        {
            bool ok = await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Design_CreateTitle"],
                Message = Loc.Format("Design_CreateBody", name, Namespace),
                ConfirmLabel = Loc["Design_CreateIndex"],
                IconKey = "Mongo.key-round",
                Danger = false,
                Facts = [new(Loc["Design_ColKey"], BsonText.Literal(key)), new(Loc["Design_Estimate"], NewImpactTitle)],
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? CollectionName : null
            }).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }
        var spec = new BsonDocument { { "key", key }, { "name", name } };
        _ = spec.Merge(options, overwriteExistingElements: false);
        IsCreating = true;
        var placeholder = new IndexRow
        {
            Name = name,
            Spec = spec,
            Keys = [.. IndexAdvisor.DisplayKeys(spec).Select(static k => new IndexKeyChip(k.Field, k.Value))],
            Form = IndexAdvisor.ShapeOf(spec),
            TypeText = FormText(IndexAdvisor.ShapeOf(spec)),
            Attributes = Attributes(spec, 0)
        };
        ApplyProgress(placeholder, 0);
        Indexes.Add(placeholder);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        Task progress = TrackCreateAsync(placeholder, poll.Token);
        try
        {
            _ = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "createIndexes", CollectionName },
                { "indexes", new BsonArray { spec } }
            }, Lifetime).ConfigureAwait(true);
            Workspace.Toast(new()
            {
                Title = Loc.Format("Design_Created", name),
                Detail = _newHidden ? Loc["Design_CreatedHidden"] : null,
                Kind = ToastKind.Success
            });
            IsCreateOpen = false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
        finally
        {
            poll.Cancel();
            try
            {
                await progress.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            IsCreating = false;
            _ = Indexes.Remove(placeholder);
        }
        await LoadIndexesAsync().ConfigureAwait(true);
    }

    /// <summary>构建期间刷占位行的进度。</summary>
    private async Task TrackCreateAsync(IndexRow placeholder, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(700, token).ConfigureAwait(true);
            Dictionary<string, double> progress = await LoadBuildProgressAsync().ConfigureAwait(true);
            if (progress.TryGetValue(placeholder.Name, out double p))
            {
                ApplyProgress(placeholder, p);
            }
        }
    }

    /// <summary>抽样分析完成后:刷新字段下拉、重算预估与字段行的角色。</summary>
    private void OnSchemaForNewIndex(AnalyzedSchema schema)
    {
        FieldOptions =
        [
            .. schema.Fields
                .Where(static f => f.DominantKind is not BsonKind.Missing)
                .Select(static f => new FieldOption(f.Path, f.DominantKind))
        ];
        foreach (NewKeyRow row in NewKeys.Where(static r => !r.HasRole))
        {
            row.Role = GuessRole(row.Field);
        }
        RecomputeNewIndex();
    }
}
