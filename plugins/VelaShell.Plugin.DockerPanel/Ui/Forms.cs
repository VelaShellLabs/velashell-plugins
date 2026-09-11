using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>重命名容器。</summary>
public sealed partial class RenameContainerForm : PanelForm
{
    private readonly TextField _current;
    private readonly TextField _next;

    /// <summary>建表单。</summary>
    public RenameContainerForm(string currentName, string composeProject)
    {
        ComposeWarning = composeProject;
        _current = new("当前名称") { Value = currentName, ReadOnly = true };
        _next = new("新名称") { Value = currentName, Placeholder = "只能是字母、数字与 _ . -" };
        Fields.Add(_current);
        Watch(_next);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "重命名容器";

    /// <inheritdoc />
    public override string Icon => "Icon.pencil";

    /// <inheritdoc />
    public override string ConfirmLabel => "重命名";

    /// <inheritdoc />
    public override string FooterHint => "改名不重启容器";

    /// <summary>新名字。</summary>
    public string NewName => _next.Value;

    /// <summary>
    /// compose 管着的容器改名后,compose 会认为那个服务不存在 —— 这句话必须说在前面。
    /// </summary>
    public string ComposeWarning => field.Length > 0
        ? $"这个容器由 compose 项目 {field} 管理。改名后 compose 会认为服务不存在,下次 up -d 会再建一个。要长期生效,请改 compose.yaml 里的 container_name。"
        : "";

    /// <inheritdoc />
    // 这句话原来只算不显示:表单壳上压根没有放它的地方,
    // 于是给一个 compose 管着的容器改名,界面上一句提醒都没有。
    public override string Notice => ComposeWarning;

    /// <inheritdoc />
    public override bool Validate()
    {
        var value = _next.Value.Trim();
        if (value.Length == 0)
        {
            _next.Error = "名字不能为空。";
            return false;
        }
        if (!NamePattern().IsMatch(value))
        {
            _next.Error = "只能包含字母、数字与 _ . -,且必须以字母或数字开头。";
            return false;
        }
        if (value == _current.Value)
        {
            _next.Error = "和当前名字一样。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"POST /containers/{_current.Value}/rename?name={_next.Value.Trim()}";
        CommandNote = $"等价于  docker rename {_current.Value} {_next.Value.Trim()}";
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NamePattern();
}

/// <summary>修改重启策略。</summary>
public sealed class RestartPolicyForm : PanelForm
{
    private readonly RadioListField _policy;
    private readonly TextField _retries;

    /// <summary>建表单。</summary>
    public RestartPolicyForm(string current, int maxRetries)
    {
        _policy = new("策略") { Value = current };
        _policy.Options.Add(new("no", "no", "容器退出后就停在那里。"));
        _policy.Options.Add(new("on-failure", "on-failure", "退出码非 0 才重启,可限次数。"));
        _policy.Options.Add(new("always", "always", "daemon 重启后也拉起 —— 手动停过的也会被拉起。"));
        _policy.Options.Add(new("unless-stopped", "unless-stopped", "同 always,但你手动停过的不会被拉起。"));
        _retries = new("最大重试次数") { Value = maxRetries.ToString(), Hint = "仅 on-failure 有意义" };
        Fields.Add(_policy);
        Watch(_retries);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "修改重启策略";

    /// <inheritdoc />
    public override string Icon => "Icon.refresh-cw";

    /// <inheritdoc />
    public override string ConfirmLabel => "保存";

    /// <inheritdoc />
    public override string FooterHint => "立即生效,不重启容器";

    /// <summary>选中的策略。</summary>
    public string Policy => _policy.Value;

    /// <summary>最大重试次数。</summary>
    public int MaxRetries => int.TryParse(_retries.Value, out var n) ? Math.Max(0, n) : 0;

    /// <inheritdoc />
    public override bool Validate()
    {
        if (_policy.Value == "on-failure" && !int.TryParse(_retries.Value.Trim(), out _))
        {
            _retries.Error = "要一个整数。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"POST /containers/{{id}}/update   RestartPolicy.Name = {_policy.Value}";
        CommandNote = $"等价于  docker update --restart {_policy.Value}{(_policy.Value == "on-failure" ? $":{MaxRetries}" : "")} <容器>";
    }
}

/// <summary>给镜像打标签。</summary>
public sealed class TagImageForm : PanelForm
{
    private readonly string _sourceId;
    private readonly TextField _repository;
    private readonly TextField _tag;

    /// <summary>建表单。</summary>
    public TagImageForm(string sourceId, string sourceReference)
    {
        _sourceId = sourceId;
        Fields.Add(new TextField("源镜像") { Value = sourceReference, ReadOnly = true });
        _repository = new("新仓库") { Value = "", Placeholder = "registry.internal/acme/api" };
        _tag = new("新标签") { Value = "latest" };
        Watch(_repository);
        Watch(_tag);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "打标签";

    /// <inheritdoc />
    public override string Icon => "Docker.tag";

    /// <inheritdoc />
    public override string ConfirmLabel => "打标签";

    /// <inheritdoc />
    public override string ConfirmIcon => "Docker.tag";

    /// <inheritdoc />
    public override string FooterHint => "标签只是指针 —— 磁盘不会多占一份";

    /// <summary>目标仓库。</summary>
    public string Repository => _repository.Value.Trim();

    /// <summary>目标标签。</summary>
    public string Tag => _tag.Value.Trim();

    /// <inheritdoc />
    public override bool Validate()
    {
        if (Repository.Length == 0)
        {
            _repository.Error = "仓库名不能为空。";
            return false;
        }
        if (Repository.Contains(' ', StringComparison.Ordinal))
        {
            _repository.Error = "仓库名里不能有空格。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"POST /images/{Humanize.ShortId(_sourceId)}/tag?repo={Repository}&tag={Tag}";
        CommandNote = $"等价于  docker tag {Humanize.ShortId(_sourceId)} {Repository}:{Tag}";
    }
}

/// <summary>新建卷。</summary>
public sealed class CreateVolumeForm : PanelForm
{
    private readonly TextField _name;
    private readonly ChoiceField _driver;

    /// <summary>建表单。</summary>
    public CreateVolumeForm()
    {
        _name = new("名称") { Placeholder = "pg-backup", Hint = "创建后不可改" };
        _driver = new("驱动") { Value = "local", AsSegments = true };
        _driver.Options.Add(new("local", "local"));
        _driver.Options.Add(new("nfs", "nfs(经 local 驱动选项)"));
        Watch(_name);
        Watch(_driver);
        Options = new("驱动选项")
        {
            Hint = "留空即用默认本地目录 /var/lib/docker/volumes",
            KeyPlaceholder = "type",
            ValuePlaceholder = "nfs",
            AddLabel = "+ 添加选项"
        };
        Labels = new("标签") { KeyPlaceholder = "backup.retention", ValuePlaceholder = "30d" };
        Watch(Options);
        Watch(Labels);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "新建卷";

    /// <inheritdoc />
    public override string Icon => "Docker.database";

    /// <inheritdoc />
    public override string ConfirmLabel => "新建卷";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.plus";

    /// <summary>驱动选项。</summary>
    public PairListField Options { get; }

    /// <summary>标签。</summary>
    public PairListField Labels { get; }

    /// <summary>卷名。</summary>
    public string Name => _name.Value.Trim();

    /// <summary>驱动(nfs 也是 local 驱动 + 选项)。</summary>
    public string Driver => "local";

    /// <summary>驱动选项字典。</summary>
    public Dictionary<string, string>? DriverOptions =>
        Options.Filled.ToDictionary(r => r.Key.Trim(), r => r.Value.Trim()) is { Count: > 0 } map ? map : null;

    /// <summary>标签字典。</summary>
    public Dictionary<string, string>? LabelMap =>
        Labels.Filled.ToDictionary(r => r.Key.Trim(), r => r.Value.Trim()) is { Count: > 0 } map ? map : null;

    /// <inheritdoc />
    public override bool Validate()
    {
        if (Name.Length == 0)
        {
            _name.Error = "卷名不能为空。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = "POST /volumes/create";
        CommandNote = $"等价于  docker volume create {Name}";
    }
}

/// <summary>新建网络。</summary>
public sealed class CreateNetworkForm : PanelForm
{
    private readonly TextField _name;
    private readonly ChoiceField _driver;
    private readonly TextField _subnet;
    private readonly TextField _gateway;
    private readonly ToggleField _internal;
    private readonly ToggleField _attachable;
    private readonly ToggleField _ipv6;

    /// <summary>建表单。</summary>
    /// <param name="swarmActive">远端 swarm 是否 active —— 决定 overlay 能不能选。</param>
    public CreateNetworkForm(bool swarmActive)
    {
        _name = new("名称") { Placeholder = "edge-dmz" };
        _driver = new("驱动") { Value = "bridge", AsSegments = true };
        _driver.Options.Add(new("bridge", "bridge"));
        _driver.Options.Add(new("macvlan", "macvlan"));
        _driver.Options.Add(new("ipvlan", "ipvlan"));
        // overlay 直接置灰而不是让用户去撞一条 daemon 的错误。
        _driver.Options.Add(new("overlay", "overlay", "", swarmActive,
            swarmActive ? "" : "需要 swarm 模式,当前主机 Swarm: inactive"));
        _subnet = new("子网") { Placeholder = "172.28.0.0/16", Hint = "留空由 Docker 自动分配" };
        _gateway = new("网关") { Placeholder = "172.28.0.1" };
        _internal = new("internal") { Description = "不给这个网络出外网的能力。" };
        _attachable = new("attachable") { Value = true, Description = "允许 docker run --network 事后接入。" };
        _ipv6 = new("启用 IPv6") { Description = "需要 daemon 开了 ipv6。" };
        Watch(_name);
        Watch(_driver);
        Watch(_subnet);
        Watch(_gateway);
        Watch(_internal);
        Watch(_attachable);
        Watch(_ipv6);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "新建网络";

    /// <inheritdoc />
    public override string Icon => "Icon.network";

    /// <inheritdoc />
    public override string ConfirmLabel => "新建网络";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.plus";

    /// <summary>网络名。</summary>
    public string Name => _name.Value.Trim();

    /// <summary>驱动。</summary>
    public string Driver => _driver.Value;

    /// <summary>子网。</summary>
    public string Subnet => _subnet.Value.Trim();

    /// <summary>网关。</summary>
    public string Gateway => _gateway.Value.Trim();

    /// <summary>是不是内部网络。</summary>
    public bool Internal => _internal.Value;

    /// <summary>能不能事后接入。</summary>
    public bool Attachable => _attachable.Value;

    /// <summary>启不启用 IPv6。</summary>
    public bool EnableIPv6 => _ipv6.Value;

    /// <inheritdoc />
    public override bool Validate()
    {
        if (Name.Length == 0)
        {
            _name.Error = "网络名不能为空。";
            return false;
        }
        if (Subnet.Length > 0 && !Subnet.Contains('/', StringComparison.Ordinal))
        {
            _subnet.Error = "子网要带掩码位数,如 172.28.0.0/16。";
            return false;
        }
        if (Gateway.Length > 0 && Subnet.Length == 0)
        {
            _gateway.Error = "给了网关就要一并给子网,否则 Docker 不知道它属于哪一段。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        var sb = new StringBuilder("docker network create -d ").Append(Driver);
        if (Subnet.Length > 0)
        {
            sb.Append(" --subnet ").Append(Subnet);
        }
        if (Gateway.Length > 0)
        {
            sb.Append(" --gateway ").Append(Gateway);
        }
        if (Internal)
        {
            sb.Append(" --internal");
        }
        if (Attachable)
        {
            sb.Append(" --attachable");
        }
        if (EnableIPv6)
        {
            sb.Append(" --ipv6");
        }
        sb.Append(' ').Append(Name.Length > 0 ? Name : "<名称>");
        CommandPreview = "POST /networks/create";
        CommandNote = $"等价于  {sb}";
    }
}

/// <summary>把容器接入网络。</summary>
public sealed class ConnectNetworkForm : PanelForm
{
    private readonly TextField _aliases;

    /// <summary>建表单。</summary>
    /// <param name="network">目标网络的描述。</param>
    /// <param name="candidates">候选容器(id, 名字, 状态, 能不能选, 不能选的原因)。</param>
    public ConnectNetworkForm(string network,
        IEnumerable<(string Id, string Name, string Meta, bool Enabled, string Reason)> candidates)
    {
        Fields.Add(new TextField("目标网络") { Value = network, ReadOnly = true });
        Containers = new("选择容器") { Placeholder = "过滤未接入的容器…" };
        foreach ((var id, var name, var meta, var enabled, var reason) in candidates)
        {
            Containers.Items.Add(new(id, name, meta, enabled, reason));
        }
        Containers.ApplyFilter();
        Fields.Add(Containers);
        _aliases = new("网络别名") { Hint = "逗号分隔", Placeholder = "db, primary" };
        Watch(_aliases);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "接入容器到网络";

    /// <inheritdoc />
    public override string Icon => "Icon.plug";

    /// <inheritdoc />
    public override string ConfirmLabel => "接入";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.plug";

    /// <inheritdoc />
    public override string FooterHint => "接入后立即生效,不重启容器";

    /// <summary>候选容器。</summary>
    public SelectListField Containers { get; }

    /// <summary>选中的容器 id。</summary>
    public string[] SelectedIds => [.. Containers.SelectedItems.Select(i => i.Id)];

    /// <summary>别名。</summary>
    public string[] Aliases =>
        [.. _aliases.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <inheritdoc />
    public override bool Validate()
    {
        if (SelectedIds.Length == 0)
        {
            Containers.Error = "至少选一个容器。";
            return false;
        }
        if (SelectedIds.Length > 1 && Aliases.Length > 0)
        {
            // 同一个别名指向多个容器,DNS 解析结果就成了随机的 —— 那不是用户想要的。
            _aliases.Error = "别名只能给单个容器 —— 多个容器共用一个别名会让 DNS 解析变成随机的。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = "POST /networks/{id}/connect";
        CommandNote = "等价于  docker network connect [--alias …] <网络> <容器>";
    }
}

/// <summary>
/// 把容器提交成镜像。
/// <para>
/// 这条路专治"手改到能跑了,别再让我复现一遍" —— 但它做出来的镜像没有 Dockerfile,
/// 半年后没人说得清里面有什么。所以表单要把这句话说在脸上,而不是藏在文档里。
/// </para>
/// </summary>
public sealed class CommitContainerForm : PanelForm
{
    private readonly string _containerName;
    private readonly TextField _repository;
    private readonly TextField _tag;
    private readonly TextField _comment;
    private readonly ToggleField _pause;

    /// <summary>建表单。</summary>
    public CommitContainerForm(string containerName, string sourceImage)
    {
        _containerName = containerName;
        Fields.Add(new TextField("源容器") { Value = containerName, ReadOnly = true });
        Fields.Add(new TextField("当前镜像") { Value = sourceImage, ReadOnly = true });
        _repository = new("新仓库") { Value = "", Placeholder = "registry.internal/acme/api" };
        _tag = new("新标签") { Value = "latest" };
        _comment = new("提交说明") { Value = "", Placeholder = "改了什么 —— 这是以后唯一的线索" };
        _pause = new("提交期间暂停容器")
        {
            Value = true,
            Description = "默认开着,与 docker commit 一致。关掉的话,一个正在写文件的进程会被拍成写到一半的样子。"
        };
        Fields.Add(_repository);
        Fields.Add(_tag);
        Fields.Add(_comment);
        Fields.Add(_pause);
        Watch(_repository);
        Watch(_tag);
        Watch(_comment);
        Watch(_pause);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "提交为镜像";

    /// <inheritdoc />
    public override string Icon => "Docker.box";

    /// <inheritdoc />
    public override string ConfirmLabel => "提交";

    /// <inheritdoc />
    public override string ConfirmIcon => "Docker.box";

    /// <inheritdoc />
    public override string FooterHint => "提交出来的镜像没有 Dockerfile —— 一次性救急可以,别当成构建流程";

    /// <summary>目标仓库。</summary>
    public string Repository => _repository.Value.Trim();

    /// <summary>目标标签。</summary>
    public string Tag => _tag.Value.Trim();

    /// <summary>提交说明。</summary>
    public string Comment => _comment.Value.Trim();

    /// <summary>提交期间是否暂停。</summary>
    public bool Pause => _pause.Value;

    /// <inheritdoc />
    public override bool Validate()
    {
        if (Repository.Length == 0)
        {
            _repository.Error = "仓库名不能为空 —— 留空会得到一个没有标签的悬空镜像。";
            return false;
        }
        if (Repository.Contains(' ', StringComparison.Ordinal))
        {
            _repository.Error = "仓库名里不能有空格。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        CommandPreview = $"POST /commit?container={_containerName}&repo={Repository}&tag={Tag}&pause={(Pause ? 1 : 0)}";
        CommandNote = "等价于  docker commit " + (Pause ? "" : "-p=false ") +
                      (Comment.Length > 0 ? $"-m {Sh.Quote(Comment)} " : "") +
                      $"{_containerName} {Repository}:{Tag}";
    }
}

/// <summary>
/// exec 会话的用户与 shell。
/// <para>
/// 这两样在 <c>exec</c> 建立时就定死了,改不了活着的那一个会话 ——
/// 所以这张表单确认之后必然伴随一次重连,而不是"下次生效"。
/// </para>
/// </summary>
public sealed class ExecUserForm : PanelForm
{
    private readonly TextField _user;
    private readonly ChoiceField _shell;

    /// <summary>建表单。</summary>
    public ExecUserForm(string currentUser, string currentShell)
    {
        _user = new("用户")
        {
            Value = currentUser,
            Placeholder = "留空 = 镜像里 USER 指定的那个",
            Hint = "可以是名字或 uid,也可以写 uid:gid"
        };
        _shell = new("Shell") { Value = currentShell, AsSegments = true };
        _shell.Options.Add(new("/bin/bash", "bash", "多数发行版镜像都有。"));
        _shell.Options.Add(new("/bin/sh", "sh", "精简镜像(alpine / distroless)只有它。"));
        _shell.Options.Add(new("/bin/ash", "ash", "busybox 系。"));
        Watch(_user);
        Watch(_shell);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "切换用户";

    /// <inheritdoc />
    public override string Icon => "Docker.users";

    /// <inheritdoc />
    public override string ConfirmLabel => "重新连接";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.refresh-cw";

    /// <inheritdoc />
    public override string FooterHint => "会结束当前 exec 会话并重开一个";

    /// <summary>用户。</summary>
    public string User => _user.Value.Trim();

    /// <summary>shell 的绝对路径。</summary>
    public string Shell => _shell.Value;

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        var user = User.Length > 0 ? $"&User={User}" : "";
        CommandPreview = $"POST /containers/{{id}}/exec   Cmd=[{Shell}]{user}";
        CommandNote = "等价于  docker exec -it " + (User.Length > 0 ? $"-u {User} " : "") + $"<容器> {Shell}";
    }
}

/// <summary>exec 会话的工作目录。</summary>
public sealed class ExecWorkingDirForm : PanelForm
{
    private readonly TextField _dir;

    /// <summary>建表单。</summary>
    public ExecWorkingDirForm(string current)
    {
        _dir = new("工作目录")
        {
            Value = current,
            Placeholder = "留空 = 镜像里 WORKDIR 指定的那个",
            Hint = "绝对路径;目录不存在时 exec 会直接失败"
        };
        Watch(_dir);
        UpdatePreview();
    }

    /// <inheritdoc />
    public override string Title => "工作目录";

    /// <inheritdoc />
    public override string Icon => "Icon.folder";

    /// <inheritdoc />
    public override string ConfirmLabel => "重新连接";

    /// <inheritdoc />
    public override string ConfirmIcon => "Icon.refresh-cw";

    /// <inheritdoc />
    public override string FooterHint => "会结束当前 exec 会话并重开一个";

    /// <summary>工作目录。</summary>
    public string WorkingDir => _dir.Value.Trim();

    /// <inheritdoc />
    public override bool Validate()
    {
        if (WorkingDir.Length > 0 && !WorkingDir.StartsWith('/'))
        {
            _dir.Error = "要绝对路径 —— exec 没有「当前目录」可以相对。";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    protected override void UpdatePreview()
    {
        var dir = WorkingDir.Length > 0 ? $"&WorkingDir={WorkingDir}" : "";
        CommandPreview = $"POST /containers/{{id}}/exec   Cmd=[shell]{dir}";
        CommandNote = "等价于  docker exec -it " + (WorkingDir.Length > 0 ? $"-w {WorkingDir} " : "") + "<容器> <shell>";
    }
}
