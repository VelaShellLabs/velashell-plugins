using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 运行容器。
/// <para>
/// 底部那条<b>等效命令</b>不是装饰:面板实际发的是 <c>POST /containers/create</c> 加
/// <c>/start</c>,而用户脑子里的模型是 <c>docker run</c>。把两者并排摆出来,
/// 一是让人能核对自己填的东西,二是能直接复制到终端里去验证。
/// </para>
/// </summary>
public sealed partial class RunContainerForm : PanelForm
{
    private readonly TextField _name;
    private readonly ChoiceField _network;
    private readonly ChoiceField _restart;
    private readonly ToggleField _detach;
    private readonly ToggleField _autoRemove;
    private readonly ToggleField _tty;
    private readonly ToggleField _privileged;
    private readonly TextField _command;
    private readonly TextField _workdir;

    /// <summary>建表单。</summary>
    /// <param name="image">镜像引用。</param>
    /// <param name="imageDetail">镜像的小字(平台、大小)。</param>
    /// <param name="networks">可选网络。</param>
    public RunContainerForm(string image, string imageDetail, IEnumerable<string> networks)
    {
        Image = image;
        ImageDetail = imageDetail;
        // 第一格是只读的源镜像:这张表单从镜像页、命令面板、总览都点得进来,
        // 而"我正要跑的是哪一个"不该只出现在最底下那条等效命令里。
        Fields.Add(new TextField("源镜像")
        {
            Value = image,
            Hint = imageDetail,
            ReadOnly = true
        });
        _name = new("容器名称") { Hint = "留空由 Docker 生成", Placeholder = "my-service" };
        Ports = new("端口映射")
        {
            KeyPlaceholder = "8080",
            ValuePlaceholder = "80",
            Separator = "→",
            AddLabel = "+ 添加端口"
        };
        Volumes = new("卷挂载")
        {
            KeyPlaceholder = "/srv/data 或 卷名",
            ValuePlaceholder = "/data",
            Separator = "→",
            AddLabel = "+ 添加挂载"
        };
        Env = new("环境变量")
        {
            KeyPlaceholder = "KEY",
            ValuePlaceholder = "value",
            AddLabel = "+ 添加变量",
            // 一屏十几条环境变量手敲不现实,而它们几乎总是已经躺在某个 .env 里。
            ImportLabel = "从 .env 导入"
        };
        _network = new("网络") { Value = "bridge" };
        foreach (var network in networks)
        {
            _network.Options.Add(new(network, network));
        }
        if (_network.Options.Count == 0)
        {
            _network.Options.Add(new("bridge", "bridge"));
        }
        _restart = new("重启策略") { Value = "unless-stopped" };
        foreach (var policy in new[] { "no", "on-failure", "always", "unless-stopped" })
        {
            _restart.Options.Add(new(policy, policy));
        }
        _detach = new("后台运行  -d") { Value = true, Description = "创建后立即启动并放到后台。" };
        _autoRemove = new("退出即删除  --rm") { Description = "容器一退出就连同可写层一起删掉。" };
        _tty = new("分配 TTY  -t") { Description = "给它一个伪终端。" };
        _privileged = new("特权模式  --privileged")
        {
            Danger = true,
            Description = "容器能做几乎任何宿主能做的事 —— 只在你确实需要时打开。"
        };
        _command = new("覆盖命令") { Hint = "留空用镜像默认", Placeholder = "nginx -g 'daemon off;'" };
        _workdir = new("工作目录") { Hint = "留空用镜像默认" };
        ExtraArgs = new("额外参数")
        {
            Hint = "与在终端里手敲同权,不做引用",
            Placeholder = "--cap-add NET_ADMIN --dns 1.1.1.1"
        };

        Watch(_name);
        Watch(Ports);
        Watch(Volumes);
        Watch(Env);
        Watch(_network);
        Watch(_restart);
        Watch(_detach);
        Watch(_autoRemove);
        Watch(_tty);
        Watch(_privileged);
        Watch(_command);
        Watch(_workdir);
        Watch(ExtraArgs);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "运行容器";

    /// <inheritdoc />
    public override string Icon => "Icon.play";

    /// <inheritdoc />
    public override string ConfirmLabel => "运行容器";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.play";

    /// <inheritdoc />
    public override string FooterHint => "面板实际调用 POST /containers/create 与 /start";

    /// <summary>镜像引用。</summary>
    public string Image { get; }

    /// <summary>镜像的小字。</summary>
    public string ImageDetail { get; }

    /// <summary>端口映射。</summary>
    public PairListField Ports { get; }

    /// <summary>卷挂载。</summary>
    public PairListField Volumes { get; }

    /// <summary>环境变量。</summary>
    public PairListField Env { get; }

    /// <summary>额外参数(只在等效命令里体现)。</summary>
    public TextField ExtraArgs { get; }

    /// <summary>容器名。</summary>
    public string ContainerName => _name.Value.Trim();

    /// <summary>后台运行。</summary>
    public bool Detach => _detach.Value;

    /// <summary>把表单变成一条 create 请求。</summary>
    public ContainerCreateRequest ToRequest()
    {
        Dictionary<string, PortBindingRequest[]> bindings = [];
        Dictionary<string, object> exposed = [];
        foreach (var row in Ports.Filled)
        {
            var containerPort = row.Value.Trim();
            var key = containerPort.Contains('/', StringComparison.Ordinal) ? containerPort : $"{containerPort}/tcp";
            bindings[key] = [new() { HostPort = row.Key.Trim() }];
            exposed[key] = new object();
        }
        string[] binds = [.. Volumes.Filled.Select(r => $"{r.Key.Trim()}:{r.Value.Trim()}")];
        string[] env = [.. Env.Filled.Select(r => $"{r.Key.Trim()}={r.Value.Trim()}")];
        var command = _command.Value.Trim();
        return new()
        {
            Image = Image,
            Env = env.Length > 0 ? env : null,
            Cmd = command.Length > 0 ? SplitArguments(command) : null,
            WorkingDir = _workdir.Value.Trim() is { Length: > 0 } dir ? dir : null,
            Tty = _tty.Value,
            ExposedPorts = exposed.Count > 0 ? exposed : null,
            HostConfig = new()
            {
                PortBindings = bindings.Count > 0 ? bindings : null,
                Binds = binds.Length > 0 ? binds : null,
                RestartPolicy = _autoRemove.Value ? null : new() { Name = _restart.Value },
                AutoRemove = _autoRemove.Value,
                Privileged = _privileged.Value,
                NetworkMode = _network.Value
            }
        };
    }

    /// <inheritdoc />
    public override bool Validate()
    {
        if (ContainerName.Length > 0 && !NamePattern().IsMatch(ContainerName))
        {
            _name.Error = "只能包含字母、数字与 _ . -,且必须以字母或数字开头。";
            return false;
        }
        foreach (var row in Ports.Filled)
        {
            if (!int.TryParse(row.Key.Trim(), out var host) || host is < 1 or > 65535)
            {
                Ports.Error = $"宿主端口 {row.Key} 不是一个合法端口。";
                return false;
            }
            var container = row.Value.Trim().Split('/')[0];
            if (!int.TryParse(container, out var inner) || inner is < 1 or > 65535)
            {
                Ports.Error = $"容器端口 {row.Value} 不是一个合法端口。";
                return false;
            }
        }
        foreach (var row in Volumes.Filled)
        {
            if (!row.Value.Trim().StartsWith('/'))
            {
                Volumes.Error = $"容器内路径要用绝对路径:{row.Value}";
                return false;
            }
        }
        if (_autoRemove.Value && _restart.Value != "no")
        {
            // Docker 自己会拒掉这个组合,但等它拒不如现在就说清楚。
            FormError = "--rm 与重启策略互斥:退出即删除的容器没法被重启。把重启策略改成 no,或关掉 --rm。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        var sb = new StringBuilder("docker run");
        if (_detach.Value)
        {
            sb.Append(" -d");
        }
        if (_autoRemove.Value)
        {
            sb.Append(" --rm");
        }
        if (_tty.Value)
        {
            sb.Append(" -t");
        }
        if (_privileged.Value)
        {
            sb.Append(" --privileged");
        }
        if (ContainerName.Length > 0)
        {
            sb.Append(" --name ").Append(ContainerName);
        }
        foreach (var row in Ports.Filled)
        {
            sb.Append(" -p ").Append(row.Key.Trim()).Append(':').Append(row.Value.Trim());
        }
        foreach (var row in Volumes.Filled)
        {
            sb.Append(" -v ").Append(row.Key.Trim()).Append(':').Append(row.Value.Trim());
        }
        foreach (var row in Env.Filled)
        {
            sb.Append(" -e ").Append(row.Key.Trim()).Append('=').Append(row.Value.Trim());
        }
        if (_network.Value.Length > 0)
        {
            sb.Append(" --network ").Append(_network.Value);
        }
        if (!_autoRemove.Value)
        {
            sb.Append(" --restart ").Append(_restart.Value);
        }
        if (_workdir.Value.Trim().Length > 0)
        {
            sb.Append(" -w ").Append(_workdir.Value.Trim());
        }
        if (ExtraArgs.Value.Trim().Length > 0)
        {
            sb.Append(' ').Append(ExtraArgs.Value.Trim());
        }
        sb.Append(' ').Append(Image);
        if (_command.Value.Trim().Length > 0)
        {
            sb.Append(' ').Append(_command.Value.Trim());
        }
        CommandPreview = "POST /containers/create  +  POST /containers/{id}/start";
        CommandNote = $"等价于  {sb}";
    }

    /// <summary>
    /// 按 shell 的规矩把一条命令切成 argv:双引号与单引号内的空格不算分隔。
    /// </summary>
    internal static string[] SplitArguments(string command)
    {
        List<string> parts = [];
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }
        return [.. parts];
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NamePattern();
}
