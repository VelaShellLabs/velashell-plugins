using System.Collections.ObjectModel;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>表单里的一个字段。</summary>
public abstract class FormField(string label) : ObservableObject
{

    /// <summary>标签。</summary>
    public string Label { get; } = label;

    /// <summary>标签右侧的灰色提示。</summary>
    public string? Hint { get; init; }

    /// <summary>字段下方的校验错误;为空表示没问题。</summary>
    public string? Error
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有没有校验错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);
}

/// <summary>一行文本。</summary>
public sealed class TextField(string label) : FormField(label)
{
    /// <summary>值。</summary>
    public string Value
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                Error = null;
                Changed?.Invoke();
            }
        }
    } = "";

    /// <summary>占位文字。</summary>
    public string Placeholder { get; init; } = "";

    /// <summary>是不是等宽(路径、id、命令用等宽)。</summary>
    public bool Mono { get; init; } = true;

    /// <summary>只读(源镜像、当前名称这类"给你看清楚"的字段)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>值变了。</summary>
    public event Action? Changed;
}

/// <summary>一个开关。</summary>
public sealed class ToggleField(string label) : FormField(label)
{
    /// <summary>值。</summary>
    public bool Value
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>开关下面那行说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>打开这一项是危险的(特权模式之类)。</summary>
    public bool Danger { get; init; }

    /// <summary>值变了。</summary>
    public event Action? Changed;
}

/// <summary>
/// 下拉 / 分段 / 单选列表里的一个选项。
/// <para>
/// 是类而不是 record:选中态要由界面**看得见**,而看得见就意味着这一项自己得会发通知。
/// 换成"在模板里拿选项的值跟字段的值比一比"也行,但那需要一条多值绑定 ——
/// 把这一位状态放在选项自己身上,模板里就只剩一句 <c>Classes.picked</c>。
/// </para>
/// </summary>
/// <param name="value">值。</param>
/// <param name="label">显示文字。</param>
/// <param name="description">补充说明(单选列表才显示)。</param>
/// <param name="enabled">能不能选。</param>
/// <param name="disabledReason">不能选的原因。</param>
public sealed class ChoiceOption(string value, string label, string description = "", bool enabled = true,
    string disabledReason = "") : ObservableObject
{

    /// <summary>值。</summary>
    public string Value { get; } = value;

    /// <summary>显示文字。</summary>
    public string Label { get; } = label;

    /// <summary>补充说明(单选列表才显示)。</summary>
    public string Description { get; } = description;

    /// <summary>能不能选。</summary>
    public bool Enabled { get; } = enabled;

    /// <summary>不能选的原因。</summary>
    public string DisabledReason { get; } = disabledReason;

    /// <summary>选中的是不是这一项。</summary>
    public bool Picked
    {
        get;
        internal set => SetField(ref field, value);
    }
}

/// <summary>一组互斥选项(分段控件或下拉)。</summary>
public sealed class ChoiceField : FormField
{

    /// <summary>建一组互斥选项。</summary>
    public ChoiceField(string label) : base(label)
    {
        SelectCommand = new(p =>
        {
            if (p is ChoiceOption { Enabled: true } option)
            {
                Value = option.Value;
            }
        });
        // 选项常常是建好字段之后才一条条加进来的(有些还要看 swarm 开没开),
        // 那时候 Value 可能已经设过了 —— 加一条就得跟着把选中态对一遍。
        Options.CollectionChanged += (_, _) => SyncPicked();
    }

    /// <summary>选项。</summary>
    public ObservableCollection<ChoiceOption> Options { get; } = [];

    /// <summary>当前值。</summary>
    public string Value
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                SyncPicked();
                OnPropertyChanged(nameof(SelectedLabel));
                Changed?.Invoke();
            }
        }
    } = "";

    private void SyncPicked()
    {
        foreach (var option in Options)
        {
            option.Picked = option.Value == Value;
        }
    }

    /// <summary>当前值对应的显示文字。</summary>
    public string SelectedLabel => Options.FirstOrDefault(o => o.Value == Value)?.Label ?? Value;

    /// <summary>用分段控件呈现(选项少时)还是下拉(选项多时)。</summary>
    public bool AsSegments { get; init; }

    /// <summary>选中变了。</summary>
    public event Action? Changed;

    /// <summary>选一个。</summary>
    public RelayCommand SelectCommand { get; }
}

/// <summary>带说明的单选列表(重启策略那种)。</summary>
public sealed class RadioListField : FormField
{

    /// <summary>建一个单选列表。</summary>
    public RadioListField(string label) : base(label)
    {
        SelectCommand = new(p =>
        {
            if (p is ChoiceOption { Enabled: true } option)
            {
                Value = option.Value;
            }
        });
        Options.CollectionChanged += (_, _) => SyncPicked();
    }

    /// <summary>选项。</summary>
    public ObservableCollection<ChoiceOption> Options { get; } = [];

    /// <summary>当前值。</summary>
    public string Value
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                SyncPicked();
                OnPropertyChanged(nameof(SelectedValue));
            }
        }
    } = "";

    private void SyncPicked()
    {
        foreach (var option in Options)
        {
            option.Picked = option.Value == Value;
        }
    }

    /// <summary>当前值(绑定用的别名,方便模板里做相等判断)。</summary>
    public string SelectedValue => Value;

    /// <summary>选一个。</summary>
    public RelayCommand SelectCommand { get; }
}

/// <summary>键值行(端口、卷、环境变量、驱动选项共用)。</summary>
public sealed class PairRow(string key, string value) : ObservableObject
{
    /// <summary>左侧。</summary>
    public string Key
    {
        get;
        set => SetField(ref field, value);
    } = key;

    /// <summary>右侧。</summary>
    public string Value
    {
        get;
        set => SetField(ref field, value);
    } = value;

    /// <summary>第三格(协议、只读标记);不用时为空。</summary>
    public string Extra { get; set; } = "";
}

/// <summary>可增删的键值列表。</summary>
public sealed class PairListField : FormField
{
    /// <summary>建一个键值列表。</summary>
    public PairListField(string label) : base(label)
    {
        AddCommand = new(_ => Rows.Add(new("", "")));
        RemoveCommand = new(p =>
        {
            if (p is PairRow row)
            {
                Rows.Remove(row);
            }
        });
        // 增删行、以及行里任一格的改动,都要把"等效命令"重算一遍 ——
        // 那条预览存在的全部意义就是让用户核对自己填的东西,它一旦滞后就成了误导。
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<PairRow>() ?? [])
            {
                added.PropertyChanged += OnRowChanged;
            }
            foreach (var removed in e.OldItems?.OfType<PairRow>() ?? [])
            {
                removed.PropertyChanged -= OnRowChanged;
            }
            Changed?.Invoke();
        };
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Changed?.Invoke();

    /// <summary>
    /// 从一段 <c>.env</c> 文本导入(设计稿 06 号板那个「从 .env 导入」)。
    /// <para>
    /// 只认最朴素的那一档:<c>KEY=VALUE</c>,跳过空行与 <c>#</c> 注释,剥掉值两边的引号。
    /// <b>不</b>做变量插值与多行值 —— dotenv 的方言各家不同,面板猜错一次就等于
    /// 悄悄改了用户的配置。认不出来的行原样跳过,并把跳过的条数报出来。
    /// </para>
    /// </summary>
    public (int Imported, int Skipped) ImportDotEnv(string text)
    {
        var imported = 0;
        var skipped = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            // export FOO=bar 也很常见。
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                skipped++;
                continue;
            }
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            // 同名覆盖:.env 里后写的赢,这里也一样。
            if (Rows.FirstOrDefault(r => r.Key == key) is { } existing)
            {
                existing.Value = value;
            }
            else
            {
                Rows.Add(new(key, value));
            }
            imported++;
        }
        // 空的占位行留着没意义,导完清一遍。
        foreach (var blank in Rows.Where(r => r.Key.Length == 0 && r.Value.Length == 0).ToList())
        {
            Rows.Remove(blank);
        }
        return (imported, skipped);
    }

    /// <summary>任一行增删或改动。</summary>
    public event Action? Changed;

    /// <summary>行。</summary>
    public ObservableCollection<PairRow> Rows { get; } = [];

    /// <summary>左侧占位。</summary>
    public string KeyPlaceholder { get; init; } = "";

    /// <summary>右侧占位。</summary>
    public string ValuePlaceholder { get; init; } = "";

    /// <summary>两格之间的符号(“→” / “=” / “:”)。</summary>
    public string Separator { get; init; } = "=";

    /// <summary>“+ 添加”的文字。</summary>
    public string AddLabel { get; init; } = "+ 添加";

    /// <summary>「从 .env 导入」那个按钮的文字;留空表示这一组不提供导入。</summary>
    public string ImportLabel { get; init; } = "";

    /// <summary>这一组支不支持导入。</summary>
    public bool CanImport => ImportLabel.Length > 0;

    /// <summary>
    /// 弹一个文件对话框选 <c>.env</c> 并导入。结果经 <see cref="ImportReport" /> 报给界面。
    /// </summary>
    public RelayCommand ImportCommand => field ??= new(async _ =>
    {
        var file =
            await FilePicker.PickOpenAsync("选一个 .env 文件").ConfigureAwait(true);
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync().ConfigureAwait(true);
        (var imported, var skipped) = ImportDotEnv(text);
        ImportReport = skipped == 0
            ? $"已导入 {imported} 条"
            : $"已导入 {imported} 条 · 跳过 {skipped} 行(不是 KEY=VALUE)";
        OnPropertyChanged(nameof(ImportReport));
    });

    /// <summary>上一次导入的结果;没导过时为空。</summary>
    public string ImportReport { get; private set; } = "";

    /// <summary>添加一行。</summary>
    public RelayCommand AddCommand { get; }

    /// <summary>删掉一行。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>非空行(两格都填了才算)。</summary>
    public IEnumerable<PairRow> Filled =>
        Rows.Where(r => r.Key.Trim().Length > 0 && r.Value.Trim().Length > 0);
}

/// <summary>可多选列表里的一项。</summary>
public sealed class SelectItem(string id, string label, string meta, bool enabled, string disabledReason = "")
    : ObservableObject
{

    /// <summary>标识。</summary>
    public string Id { get; } = id;

    /// <summary>显示文字。</summary>
    public string Label { get; } = label;

    /// <summary>右侧小字。</summary>
    public string Meta { get; } = meta;

    /// <summary>能不能选。</summary>
    public bool Enabled { get; } = enabled;

    /// <summary>不能选的原因。</summary>
    public string DisabledReason { get; } = disabledReason;

    /// <summary>选中了没有。</summary>
    public bool Selected
    {
        get;
        set
        {
            if (Enabled)
            {
                SetField(ref field, value);
            }
        }
    }
}

/// <summary>带搜索的多选列表。</summary>
public sealed class SelectListField(string label) : FormField(label)
{

    /// <summary>全部项。</summary>
    public ObservableCollection<SelectItem> Items { get; } = [];

    /// <summary>过滤后的项。</summary>
    public ObservableCollection<SelectItem> View { get; } = [];

    /// <summary>搜索词。</summary>
    public string Search
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

    /// <summary>搜索框占位。</summary>
    public string Placeholder { get; init; } = "过滤…";

    /// <summary>已选的项。</summary>
    public IEnumerable<SelectItem> SelectedItems => Items.Where(i => i.Selected);

    /// <summary>重建过滤视图。</summary>
    public void ApplyFilter()
    {
        View.Clear();
        foreach (var item in Items.Where(i =>
                     Search.Length == 0 || i.Label.Contains(Search, StringComparison.OrdinalIgnoreCase)))
        {
            View.Add(item);
        }
    }
}

/// <summary>
/// 一个表单弹窗。
/// <para>
/// 全部复用同一个壳:标题栏 → 主机条 → 内容 → 页脚。主机条一行都不能省 ——
/// 同时开着两台机器时,它是最便宜的保险。
/// </para>
/// </summary>
public abstract class PanelForm : ObservableObject
{

    /// <summary>标题。</summary>
    public abstract string Title { get; }

    /// <summary>标题图标。</summary>
    public virtual string Icon => "Icon.settings";

    /// <summary>确认按钮文字。</summary>
    public abstract string ConfirmLabel { get; }

    /// <summary>确认按钮图标。</summary>
    public virtual string ConfirmIcon => "Docker.check";

    /// <summary>页脚左侧的提示。</summary>
    public virtual string FooterHint => "Esc 取消";

    /// <summary>字段。</summary>
    public ObservableCollection<FormField> Fields { get; } = [];

    /// <summary>“等效命令”里显示的那条请求。</summary>
    public string CommandPreview
    {
        get;
        protected set => SetField(ref field, value);
    } = "";

    /// <summary>请求下面那行等价的命令行。</summary>
    public string CommandNote
    {
        get;
        protected set => SetField(ref field, value);
    } = "";

    /// <summary>有没有命令预览。</summary>
    public bool HasPreview => CommandPreview.Length > 0;

    /// <summary>
    /// 表单顶部的一条提醒。
    /// <para>
    /// 与 <see cref="FormError" /> 不是一回事:错误说的是"这样填不行",
    /// 提醒说的是"这样填可以,但会有一个你未必想要的后果"——
    /// 改一个 compose 管着的容器的名字就属于后者。
    /// </para>
    /// </summary>
    public virtual string Notice => "";

    /// <summary>有没有提醒。</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>整表级别的错误。</summary>
    public string? FormError
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasFormError));
            }
        }
    }

    /// <summary>有没有整表错误。</summary>
    public bool HasFormError => !string.IsNullOrEmpty(FormError);

    /// <summary>
    /// 校验。返回 <see langword="false" /> 时把原因写在字段的 <see cref="FormField.Error" />
    /// 或 <see cref="FormError" /> 上 —— 让用户知道**哪一格**不对,而不是一句"输入有误"。
    /// </summary>
    public virtual bool Validate() => true;

    /// <summary>字段变动后重算命令预览。</summary>
    protected virtual void UpdatePreview()
    {
    }

    /// <summary>把某个字段的变动接到预览上。</summary>
    protected void Watch(TextField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个开关接到预览上。</summary>
    protected void Watch(ToggleField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个选择接到预览上。</summary>
    protected void Watch(ChoiceField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }

    /// <summary>把某个键值列表接到预览上。</summary>
    protected void Watch(PairListField field)
    {
        field.Changed += UpdatePreview;
        Fields.Add(field);
    }
}
