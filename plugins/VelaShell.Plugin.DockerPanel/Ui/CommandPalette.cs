using System.Collections.ObjectModel;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>命令面板里的一条。</summary>
/// <param name="Group">分组标题(动作 / 容器 / 镜像·卷 / 面板命令)。</param>
/// <param name="Title">主文字。</param>
/// <param name="Detail">右侧小字。</param>
/// <param name="Icon">图标资源键。</param>
/// <param name="Tone">语气(破坏性的那几条用危险色)。</param>
/// <param name="Destructive">破坏性:标题带省略号,选中后仍走确认闸门。</param>
/// <param name="Run">执行。</param>
public sealed record PaletteEntry(
    string Group,
    string Title,
    string Detail,
    string Icon,
    RowTone Tone,
    bool Destructive,
    Func<Task> Run);

/// <summary>命令面板里的一行(含分组头的显示状态)。</summary>
public sealed class PaletteItem(PaletteEntry entry) : ObservableObject
{

    /// <summary>底层条目。</summary>
    public PaletteEntry Entry { get; } = entry;

    /// <summary>分组标题。</summary>
    public string Group => Entry.Group;

    /// <summary>主文字。</summary>
    public string Title => Entry.Title;

    /// <summary>右侧小字。</summary>
    public string Detail => Entry.Detail;

    /// <summary>图标。</summary>
    public string Icon => Entry.Icon;

    /// <summary>语气。</summary>
    public RowTone Tone => Entry.Tone;

    /// <summary>这一条上面要不要画分组头。</summary>
    public bool ShowGroup { get; set; }

    /// <summary>分组里有几条(分组头右侧那个数字)。</summary>
    public int GroupCount { get; set; }

    /// <summary>键盘选中的那一条。</summary>
    public bool Active
    {
        get;
        set => SetField(ref field, value);
    }
}

/// <summary>
/// 面板内的命令面板(<c>Ctrl+K</c>)。
/// <para>
/// <b>它在面板里,不劫持宿主的命令面板。</b> 宿主自己有一个 <c>Ctrl+K</c>,
/// 那一个管的是会话、设置、插件;这一个管的是当前这台 Docker 主机上的容器、镜像、卷。
/// 两者搜的是不同的东西,合并成一个只会让两边都变难用。
/// </para>
/// <para>
/// 破坏性的条目标题带省略号,并且**选中后仍然走确认闸门** —— 命令面板是个加速器,
/// 不是绕过闸门的后门。
/// </para>
/// </summary>
public sealed class CommandPalette : ObservableObject
{
    private readonly Func<IReadOnlyList<PaletteEntry>> _collect;
    private List<PaletteEntry> _all = [];
    private int _selectedIndex;

    /// <summary>建面板。</summary>
    /// <param name="collect">打开时去收集当前可用的条目。</param>
    public CommandPalette(Func<IReadOnlyList<PaletteEntry>> collect)
    {
        _collect = collect;
        CloseCommand = new RelayCommand(_ =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
        RunSelectedCommand = new RelayCommand(_ => RunSelectedAsync());
        RunCommand = new RelayCommand(p => p is PaletteItem item ? RunAsync(item) : Task.CompletedTask);
    }

    /// <summary>开着没。</summary>
    public bool IsOpen
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>搜索词。</summary>
    public string Query
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyFilter();
            }
        }
    } = "";

    /// <summary>过滤后的条目。</summary>
    public ObservableCollection<PaletteItem> Items { get; } = [];

    /// <summary>一条都没匹配上。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>当前主机名(面板顶部显示 —— 命令作用在哪台机器上必须写清)。</summary>
    public string HostName { get; private set; } = "";

    /// <summary>关闭。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>执行当前选中的那条。</summary>
    public RelayCommand RunSelectedCommand { get; }

    /// <summary>执行指定的那条(鼠标点击)。</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>打开面板并收集条目。</summary>
    public void Open(string hostName)
    {
        HostName = hostName;
        _all = [.. _collect()];
        Query = "";
        ApplyFilter();
        OnPropertyChanged(nameof(HostName));
        IsOpen = true;
    }

    /// <summary>上下移动选中项。</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        // 环绕:到底了按下一次回到第一条,比停在那里不动更符合预期。
        var next = (_selectedIndex + delta + Items.Count) % Items.Count;
        Select(next);
    }

    /// <summary>
    /// Tab 补全:把选中那条的标题填进搜索框。
    /// <para>
    /// 补的是标题里去掉省略号的部分 —— 省略号代表"还会再问你一次",
    /// 把它补进搜索框只会让下一次匹配失败。
    /// </para>
    /// </summary>
    public void Complete()
    {
        if (Items.Count == 0)
        {
            return;
        }
        Query = Items[_selectedIndex].Title.TrimEnd('…');
    }

    private void Select(int index)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Active = i == index;
        }
        _selectedIndex = index;
    }

    private async Task RunSelectedAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }
        await RunAsync(Items[_selectedIndex]).ConfigureAwait(true);
    }

    private async Task RunAsync(PaletteItem item)
    {
        // 先关面板再执行:被触发的动作多半会自己弹确认闸门或表单,
        // 让命令面板压在它上面既难看也点不到。
        IsOpen = false;
        await item.Entry.Run().ConfigureAwait(true);
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var needle = Query.Trim();
        List<PaletteEntry> matched = needle.Length == 0
            ? [.. _all]
            : [.. _all.Where(e => Matches(e, needle)).OrderByDescending(e => Score(e, needle))];

        string? group = null;
        foreach (var entry in matched)
        {
            var item = new PaletteItem(entry);
            if (entry.Group != group)
            {
                item.ShowGroup = true;
                item.GroupCount = matched.Count(e => e.Group == entry.Group);
                group = entry.Group;
            }
            Items.Add(item);
        }
        OnPropertyChanged(nameof(IsEmpty));
        if (Items.Count > 0)
        {
            Select(0);
        }
    }

    private static bool Matches(PaletteEntry entry, string needle) =>
        entry.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || entry.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 排序权重。标题前缀命中 &gt; 标题包含 &gt; 只在小字里命中。
    /// <para>
    /// 输入 <c>res</c> 时,"重启 nginx-proxy" 该排在"名字里恰好带 res 的某个卷"前面。
    /// </para>
    /// </summary>
    private static int Score(PaletteEntry entry, string needle) =>
        entry.Title.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 3
        : entry.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 2
        : 1;
}
