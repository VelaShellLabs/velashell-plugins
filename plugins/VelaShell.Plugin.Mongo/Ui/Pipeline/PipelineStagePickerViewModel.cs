using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>阶段搜索列表里的一项。</summary>
/// <param name="Name">阶段名(<c>$lookup</c>)。</param>
/// <param name="Short">短说明(<c>关联集合</c>)。</param>
/// <param name="Description">完整一句(词汇表的说明)。</param>
internal sealed record PipelineStageChoice(string Name, string Short, string Description);

/// <summary>
/// 「添加阶段 · 输入 $ 可搜索 30+ 个阶段」的搜索弹层,也是阶段卡片上运算符下拉的那张列表。
/// 一份列表两处用:新加与改运算符面对的是同一个问题("我要哪个阶段"),理应是同一个答案。
/// </summary>
internal sealed class PipelineStagePickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<PipelineStageChoice> _all;
    private string _query = "";
    private IReadOnlyList<PipelineStageChoice> _items;
    private PipelineStageChoice? _selected;

    /// <summary>构造。</summary>
    /// <param name="loc">文案。</param>
    public PipelineStagePickerViewModel(Loc loc)
    {
        Loc = loc;
        _all =
        [
            .. MongoVocabulary.Stages.Select(entry =>
            {
                string key = "Pipe_Desc_" + entry.Name.TrimStart('$');
                string shortText = loc[key] == key ? loc["Pipe_Desc_custom"] : loc[key];
                return new PipelineStageChoice(entry.Name, shortText, entry.Describe(loc.IsChinese));
            })
        ];
        _items = _all;
        _selected = _all.FirstOrDefault();
    }

    /// <summary>文案。</summary>
    public Loc Loc { get; }

    /// <summary>选中后回调(由视图挂上:新加阶段,或者给某张卡片换运算符)。</summary>
    public Action<string>? Picked { get; set; }

    /// <summary>搜索词(<c>$lo</c>、<c>lookup</c>、<c>关联</c> 都能搜到 <c>$lookup</c>)。</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? ""))
            {
                Filter();
            }
        }
    }

    /// <summary>过滤后的列表。</summary>
    public IReadOnlyList<PipelineStageChoice> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>当前选中。</summary>
    public PipelineStageChoice? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>重置(每次打开弹层)。</summary>
    public void Reset(string? current = null)
    {
        Query = "";
        Selected = _all.FirstOrDefault(c => c.Name == current) ?? _all.FirstOrDefault();
    }

    /// <summary>上下移动选中。</summary>
    public void Move(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }
        int index = _selected is null ? -1 : IndexOf(_selected);
        Selected = _items[Math.Clamp(index + delta, 0, _items.Count - 1)];
    }

    /// <summary>确认选中项。</summary>
    public void Accept(PipelineStageChoice? choice = null)
    {
        if ((choice ?? _selected) is { } picked)
        {
            Picked?.Invoke(picked.Name);
        }
    }

    private int IndexOf(PipelineStageChoice choice)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], choice))
            {
                return i;
            }
        }
        return -1;
    }

    private void Filter()
    {
        string q = _query.Trim().TrimStart('$');
        if (q.Length == 0)
        {
            Items = _all;
        }
        else
        {
            // 名字前缀匹配排前面,其余(名字包含、说明包含)随后 —— 敲 "$s" 先给 $sort / $set 而不是 $densify。
            var prefix = _all.Where(c => c.Name.TrimStart('$').StartsWith(q, StringComparison.OrdinalIgnoreCase));
            var rest = _all.Where(c => !c.Name.TrimStart('$').StartsWith(q, StringComparison.OrdinalIgnoreCase)
                                       && (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                           || c.Short.Contains(q, StringComparison.OrdinalIgnoreCase)
                                           || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
            Items = [.. prefix, .. rest];
        }
        Selected = _items.FirstOrDefault();
    }
}
