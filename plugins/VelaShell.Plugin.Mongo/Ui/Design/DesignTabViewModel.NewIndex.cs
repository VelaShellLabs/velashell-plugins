using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计 · 右侧「新建索引」面板(设计稿 07)。</summary>
internal sealed partial class DesignTabViewModel
{
    private bool _isCreateOpen;
    private string _newKind = "normal";
    private bool _newUnique;
    private bool _newSparse;
    private bool _newHidden;
    private bool _newTtl;
    private string _newTtlSeconds = "86400";
    private bool _newPartial;
    private string _newPartialText = "{ }";
    private string _newName = "";
    private bool _nameEdited;
    private bool _settingName;
    private string _newImpactTitle = "";
    private string _newImpactDetail = "";
    private string _newCommand = "";
    private string _newError = "";
    private bool _isCreating;
    private IReadOnlyList<FieldOption> _fieldOptions = [];

    /// <summary>面板开着。</summary>
    public bool IsCreateOpen
    {
        get => _isCreateOpen;
        set => SetProperty(ref _isCreateOpen, value);
    }

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
        get => _newTtlSeconds;
        set
        {
            if (SetProperty(ref _newTtlSeconds, value ?? ""))
            {
                RaisePropertyChanged(nameof(NewTtlHuman));
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>TTL 秒数换算成人话。</summary>
    public string NewTtlHuman => long.TryParse(_newTtlSeconds, out long s) ? "= " + Duration(s) : "";

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
        get => _newName;
        set
        {
            if (SetProperty(ref _newName, value ?? "") && !_settingName)
            {
                _nameEdited = _newName.Length > 0;
                RaisePropertyChanged(nameof(NewNameHint));
                RecomputeNewIndex();
            }
        }
    }

    /// <summary>名称右侧的小字:自动生成 / 已自定义。</summary>
    public string NewNameHint => _nameEdited ? Loc["Design_NameCustom"] : Loc["Design_NameAuto"];

    /// <summary>预估框第一行(<c>预计大小 ≈ 140 MB · 构建约 3 分钟</c>)。</summary>
    public string NewImpactTitle
    {
        get => _newImpactTitle;
        private set => SetProperty(ref _newImpactTitle, value);
    }

    /// <summary>预估框说明。</summary>
    public string NewImpactDetail
    {
        get => _newImpactDetail;
        private set => SetProperty(ref _newImpactDetail, value);
    }

    /// <summary>命令预览。</summary>
    public string NewCommand
    {
        get => _newCommand;
        private set => SetProperty(ref _newCommand, value);
    }

    /// <summary>表单错误(创建按钮因此不可用);没有为空。</summary>
    public string NewError
    {
        get => _newError;
        private set
        {
            if (SetProperty(ref _newError, value))
            {
                RaisePropertyChanged(nameof(HasNewError));
                CreateIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>有表单错误。</summary>
    public bool HasNewError => _newError.Length > 0;

    /// <summary>正在创建(按钮转圈)。</summary>
    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                CreateIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>抽样到的字段(字段下拉的选项)。</summary>
    public IReadOnlyList<FieldOption> FieldOptions
    {
        get => _fieldOptions;
        private set
        {
            if (SetProperty(ref _fieldOptions, value))
            {
                foreach (NewKeyRow row in NewKeys)
                {
                    row.RefreshSwatch();
                }
            }
        }
    }

    /// <summary>打开面板(空白表单)。</summary>
    public RelayCommand OpenCreateCommand { get; private set; } = null!;

    /// <summary>关闭面板。</summary>
    public RelayCommand CloseCreateCommand { get; private set; } = null!;

    /// <summary>添加一个字段行。</summary>
    public RelayCommand AddKeyCommand { get; private set; } = null!;

    /// <summary>删除一个字段行。</summary>
    public RelayCommand<NewKeyRow> RemoveKeyCommand { get; private set; } = null!;

    /// <summary>创建索引。</summary>
    public AsyncCommand CreateIndexCommand { get; private set; } = null!;

    private void InitializeNewIndex()
    {
        OpenCreateCommand = new(() => OpenCreatePanel(null, null));
        CloseCreateCommand = new(() => IsCreateOpen = false);
        AddKeyCommand = new(() => AddKey("", 1, ""));
        RemoveKeyCommand = new(row =>
        {
            NewKeys.Remove(row);
            RenumberKeys();
            RecomputeNewIndex();
        });
        CreateIndexCommand = new(CreateIndexAsync, () => !_isCreating && _newError.Length == 0 && NewKeys.Any(static k => k.Field.Length > 0));
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
        var row = new NewKeyRow(field, direction, role.Length > 0 ? role : GuessRole(field), TokenOf, RecomputeNewIndex, DirectionLabel)
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
            NewKeys[i].IsFirst = i == 0;
        }
    }

    /// <summary>键值 → 方向下拉上的字(<c>1  升序</c> / <c>-1  降序</c> / <c>text</c>)。</summary>
    internal string DirectionLabel(BsonValue value) =>
        value.IsNumeric
            ? Loc[value.ToDouble() < 0 ? "Design_DirDesc" : "Design_DirAsc"]
            : value.ToString() ?? "";

    /// <summary>字段 → 类型色(抽样里没有就是灰的)。</summary>
    private string TokenOf(string field) =>
        _fieldOptions.FirstOrDefault(f => f.Path == field)?.Token ?? "VelaTextMuted";

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
            key.Set(field, row.DirectionValue);
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
            if (!long.TryParse(_newTtlSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds < 0)
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
        if (error.Length == 0 && Indexes.FirstOrDefault(i => i.Name == _newName.Trim()) is { } clash)
        {
            error = Loc.Format("Design_ErrNameExists", clash.Name);
        }
        if (_nameEdited && _newName.Trim() != auto && _newName.Trim().Length > 0)
        {
            options["name"] = _newName.Trim();
        }
        NewError = error;
        NewCommand = key.ElementCount == 0
            ? ""
            : $"{ShellRef}.createIndex(\n  {BsonText.Literal(key)}" + (options.ElementCount > 0 ? $",\n  {BsonText.Literal(options)}" : "") + "\n)";
        UpdateImpact(key);
        CreateIndexCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 预估:大小 ≈ 文档数 ×(各键字段平均值长 + 每项开销),构建耗时按数据量粗估;
    /// 再写一句当前版本的构建方式(4.4 起的优化构建只在首尾短暂持排他锁)。
    /// </summary>
    private void UpdateImpact(BsonDocument key)
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
        string name = _newName.Trim().Length > 0 ? _newName.Trim() : IndexAdvisor.DefaultName(key);
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
        spec.Merge(options, overwriteExistingElements: false);
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
            await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
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
            Indexes.Remove(placeholder);
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
