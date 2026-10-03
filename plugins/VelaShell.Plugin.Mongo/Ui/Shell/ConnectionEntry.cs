using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>对象树根上一条连接的状态。</summary>
internal enum ConnectionState
{
    /// <summary>没连(灰点)。</summary>
    Disconnected,

    /// <summary>正在连(占位标签里是「正在连接」卡片)。</summary>
    Connecting,

    /// <summary>连着(绿点,下面挂着库)。</summary>
    Connected,

    /// <summary>没连上(红点,占位标签里是「无法连接」卡片)。</summary>
    Failed
}

/// <summary>
/// 一条已保存的连接在工作台里的样子:对象树的根行、连接状态、连着时的会话、没连上时的原因。
/// </summary>
internal sealed class ConnectionEntry : ObservableObject
{
    private MongoProfile _profile;
    private ConnectionState _state;
    private MongoSession? _session;
    private MongoConnectException? _failure;
    private readonly Loc _loc;

    /// <summary>构造。</summary>
    /// <param name="profile">已保存的连接。</param>
    /// <param name="loc">文案表。</param>
    public ConnectionEntry(MongoProfile profile, Loc loc)
    {
        _profile = profile;
        _loc = loc;
        Root = new TreeNode(NodeKind.Connection, profile.Name, 0, null, this) { IsLoaded = true };
        UpdateRoot();
    }

    /// <summary>已保存的连接(编辑后整条换掉)。</summary>
    public MongoProfile Profile
    {
        get => _profile;
        set
        {
            if (SetProperty(ref _profile, value))
            {
                Root.Name = value.Name;
                UpdateRoot();
                RaisePropertiesChanged(nameof(Name), nameof(Detail));
            }
        }
    }

    /// <summary>对象树的根行。</summary>
    public TreeNode Root { get; }

    /// <summary>显示名。</summary>
    public string Name => _profile.Name;

    /// <summary>状态。</summary>
    public ConnectionState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                UpdateRoot();
                StateChanged?.Invoke(this);
            }
        }
    }

    /// <summary>状态变了(外壳据此刷新底部信息区)。</summary>
    public event Action<ConnectionEntry>? StateChanged;

    /// <summary>连着时的会话。</summary>
    public MongoSession? Session
    {
        get => _session;
        set => SetProperty(ref _session, value);
    }

    /// <summary>没连上的原因。</summary>
    public MongoConnectException? Failure
    {
        get => _failure;
        set => SetProperty(ref _failure, value);
    }

    /// <summary>正在连时可取消。</summary>
    internal CancellationTokenSource? Connecting { get; set; }

    /// <summary>
    /// 卡片副标题(设计稿 22:<c>MongoDB · 10.20.3.21:27017 · 经 bastion-ops</c>)。
    /// 跳板只有 id 时不写名字 —— 名字要问宿主,卡片不值得为它等一次往返。
    /// </summary>
    public string Detail
    {
        get
        {
            MongoSettings settings = _profile.Parsed;
            string target = settings.Topology switch
            {
                MongoTopology.Uri => MongoConnection.RedactUri(_profile.Host),
                MongoTopology.Srv => _profile.Host,
                _ => $"{_profile.Host}:{_profile.Port}"
            };
            return $"MongoDB · {target}";
        }
    }

    /// <summary>根行的色点、标签与右侧小字跟着状态与环境走。</summary>
    private void UpdateRoot()
    {
        Root.DotClass = _state switch
        {
            ConnectionState.Connected => "ok",
            ConnectionState.Connecting => "connecting",
            ConnectionState.Failed => "err",
            _ => "off"
        };
        (Root.Tag, Root.TagClass) = _profile.Environment switch
        {
            MongoEnvironment.Production => (_loc["Mongo_EnvProduction"], "err"),
            MongoEnvironment.Testing => (_loc["Mongo_EnvTesting"], "warn"),
            _ => ((string?)null, "muted")
        };
        if (_state != ConnectionState.Connected)
        {
            Root.Meta = "";
        }
    }
}
