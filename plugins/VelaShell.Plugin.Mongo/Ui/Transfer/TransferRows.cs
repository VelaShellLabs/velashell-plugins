using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>数据传输的一个目标候选:本插件开着的另一条 MongoDB 会话,或手填的连接串。</summary>
internal sealed class TransferTarget
{
    /// <summary>构造。</summary>
    /// <param name="name">显示名(会话名 / 脱敏后的连接串)。</param>
    /// <param name="connection">连接。</param>
    /// <param name="owned">是不是向导自己开的(手填连接串);是的话向导结束时由它关掉。</param>
    public TransferTarget(string name, MongoConnection connection, bool owned)
    {
        Name = name;
        Connection = connection;
        Owned = owned;
    }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>连接。</summary>
    public MongoConnection Connection { get; }

    /// <summary>向导自己开的连接。</summary>
    public bool Owned { get; }

    /// <summary>环境。</summary>
    public MongoEnvironment Environment => Connection.Settings.Environment;

    /// <summary>端点。</summary>
    public string Endpoint => Connection.Endpoint;
}

/// <summary>传输的一个对象在执行页上的状态。</summary>
internal enum TransferObjectState
{
    /// <summary>排队。</summary>
    Queued,

    /// <summary>传输中。</summary>
    Running,

    /// <summary>完成。</summary>
    Done,

    /// <summary>已跳过。</summary>
    Skipped,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>被停止。</summary>
    Stopped
}

/// <summary>对象表的一行(设计稿 21:对象 / 目标动作 / 进度 / 状态)。</summary>
internal sealed class TransferObjectRow : ObservableObject
{
    private readonly Loc _loc;
    private readonly Action _changed;
    private XferOption _action;
    private long _done;

    /// <summary>构造。</summary>
    public TransferObjectRow(string name, XferObjectKind kind, CollectionInfo? info, IReadOnlyList<XferOption> actions, Loc loc, Action changed)
    {
        Name = name;
        Kind = kind;
        Info = info;
        Actions = actions;
        _loc = loc;
        _changed = changed;
        _action = actions.First(a => (XferAction)a.Value == XferAction.Create);
    }

    /// <summary>名字(桶是桶名)。</summary>
    public string Name { get; }

    /// <summary>种类。</summary>
    public XferObjectKind Kind { get; }

    /// <summary>集合信息。</summary>
    public CollectionInfo? Info { get; }

    /// <summary>表里显示的名字(<c>fs（GridFS）</c>)。</summary>
    public string DisplayName => Kind == XferObjectKind.Bucket ? $"{Name}（GridFS）" : Name;

    /// <summary>图标。</summary>
    public string IconKey => Kind switch
    {
        XferObjectKind.Bucket => "Mongo.hard-drive",
        XferObjectKind.View => "Mongo.eye",
        _ => Info?.Kind == CollectionKind.TimeSeries ? "Mongo.chart-no-axes-column" : "Mongo.table-2"
    };

    /// <summary>图标色。</summary>
    public string IconToken => Kind switch
    {
        XferObjectKind.Bucket => "VelaWarning",
        XferObjectKind.View => "VelaShellMagenta",
        _ => "VelaInfo"
    };

    /// <summary>勾选。</summary>
    public bool IsChecked
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(RowOpacity));
                _changed();
            }
        }
    } = true;

    /// <summary>没勾、或跳过的行淡一点。</summary>
    public double RowOpacity => IsChecked ? 1 : 0.5;

    /// <summary>目标动作候选。</summary>
    public IReadOnlyList<XferOption> Actions { get; }

    /// <summary>目标动作。</summary>
    public XferOption Action
    {
        get => _action;
        set
        {
            if (value is not null && SetProperty(ref _action, value))
            {
                RaisePropertiesChanged(nameof(ActionValue), nameof(ActionText), nameof(ActionTone), nameof(NameToken), nameof(Note), nameof(HasNote));
                _changed();
            }
        }
    }

    /// <summary>目标动作的值。</summary>
    public XferAction ActionValue => (XferAction)_action.Value;

    /// <summary>目标动作的字(执行页的小标签)。</summary>
    public string ActionText => _action.Label;

    /// <summary>标签语气:覆盖 = 警告色,追加 / 新建 = info,跳过 = 灰。</summary>
    public string ActionTone => ActionValue switch
    {
        XferAction.Overwrite => "warn",
        XferAction.Skip => "muted",
        _ => "info"
    };

    /// <summary>名字颜色(跳过的灰掉)。</summary>
    public string NameToken => ActionValue == XferAction.Skip ? "VelaTextMuted" : "VelaTextPrimary";

    /// <summary>目标上已有同名对象。</summary>
    public bool ExistsOnTarget
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(Note), nameof(HasNote));
            }
        }
    }

    /// <summary>进度那一格的灰字(<c>目标已存在同名集合</c>)。</summary>
    public string Note => ExistsOnTarget && ActionValue == XferAction.Skip ? _loc["Xfer_ExistsOnTarget"] : "";

    /// <summary>有灰字。</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>总文档数(桶是 files + chunks)。</summary>
    public long Total
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(CountText), nameof(Fraction));
            }
        }
    }

    /// <summary>桶的文件数。</summary>
    public long Files { get; set; }

    /// <summary>桶的数据量(字节)。</summary>
    public long Bytes
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>已传。</summary>
    public long Done
    {
        get => Interlocked.Read(ref _done);
        set
        {
            _ = Interlocked.Exchange(ref _done, value);
            RaisePropertiesChanged(nameof(Done), nameof(CountText), nameof(Fraction));
        }
    }

    /// <summary>加一批(线程池线程上调)。</summary>
    public void Add(long count)
    {
        _ = Interlocked.Add(ref _done, count);
        RaisePropertiesChanged(nameof(Done), nameof(CountText), nameof(Fraction));
    }

    /// <summary>进度 0–1。</summary>
    public double Fraction => Total <= 0
        ? State == TransferObjectState.Done ? 1 : 0
        : Math.Min(1, (double)Done / Total);

    /// <summary>进度条旁的计数(<c>796,112 / 1,284,902</c>、<c>86,410</c>、<c>1,204 个文件 · 18.4 GB</c>)。</summary>
    public string CountText
    {
        get
        {
            if (Kind == XferObjectKind.Bucket && State is TransferObjectState.Queued)
            {
                return _loc.Format("Xfer_BucketCount", BsonText.Grouped(Files), BsonText.Bytes(Bytes));
            }
            if (Kind == XferObjectKind.View)
            {
                return _loc["Xfer_ViewDefinition"];
            }
            return State == TransferObjectState.Running
                ? $"{BsonText.Grouped(Done)} / {BsonText.Grouped(Total)}"
                : State == TransferObjectState.Done ? BsonText.Grouped(Math.Max(Done, 0)) : BsonText.Grouped(Total);
        }
    }

    /// <summary>执行页状态。</summary>
    public TransferObjectState State
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(StatusText), nameof(StatusIcon), nameof(StatusToken), nameof(IsActive), nameof(CountText),
                    nameof(Fraction), nameof(IsDone));
            }
        }
    } = TransferObjectState.Queued;

    /// <summary>传输中(那一行 VelaBgHover 底)。</summary>
    public bool IsActive => State == TransferObjectState.Running;

    /// <summary>完成(进度条绿)。</summary>
    public bool IsDone => State == TransferObjectState.Done;

    /// <summary>执行中(表里显示进度与状态,不显示勾选与下拉)。</summary>
    public bool Running
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(Editing));
            }
        }
    }

    /// <summary>设置阶段(可勾选、可改动作)。</summary>
    public bool Editing => !Running;

    /// <summary>状态字。</summary>
    public string StatusText => State switch
    {
        TransferObjectState.Running => _loc["Xfer_StateRunning"],
        TransferObjectState.Done => _loc["Xfer_StateDone"],
        TransferObjectState.Skipped => _loc["Xfer_StateSkipped"],
        TransferObjectState.Failed => _loc["Xfer_StateFailed"],
        TransferObjectState.Stopped => _loc["Xfer_StateStopped"],
        _ => _loc["Xfer_StateQueued"]
    };

    /// <summary>状态图标。</summary>
    public string StatusIcon => State switch
    {
        TransferObjectState.Running => "Mongo.loader-circle",
        TransferObjectState.Done => "Mongo.circle-check",
        TransferObjectState.Skipped => "Mongo.circle-minus",
        TransferObjectState.Failed => "Mongo.circle-x",
        TransferObjectState.Stopped => "Mongo.octagon-x",
        _ => "Mongo.circle-dashed"
    };

    /// <summary>状态颜色。</summary>
    public string StatusToken => State switch
    {
        TransferObjectState.Running => "VelaAccent",
        TransferObjectState.Done => "VelaStatusConnected",
        TransferObjectState.Failed => "VelaError",
        TransferObjectState.Stopped => "VelaWarning",
        _ => "VelaTextMuted"
    };

    /// <summary>→ 核心层的传输对象。</summary>
    public XferItem ToItem() => new(Name, Kind, Info, ActionValue);
}
