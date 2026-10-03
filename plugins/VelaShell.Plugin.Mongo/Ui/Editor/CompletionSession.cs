using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一次打开着的补全:候选项随用户继续打字就地过滤,选中项驱动右侧详情栏。
/// </summary>
public sealed class CompletionSession : ObservableObject
{
    private readonly IReadOnlyList<CompletionItem> _all;
    private CompletionItem? _selected;
    private string _prefix = "";

    /// <summary>构造。</summary>
    /// <param name="set">候选集。</param>
    public CompletionSession(CompletionSet set)
    {
        Set = set;
        _all = set.Items;
        foreach (CompletionItem item in _all)
        {
            Items.Add(item);
        }
        _selected = Items.FirstOrDefault();
    }

    /// <summary>候选集。</summary>
    public CompletionSet Set { get; }

    /// <summary>过滤后的候选项。</summary>
    public ObservableCollection<CompletionItem> Items { get; } = [];

    /// <summary>选中项。</summary>
    public CompletionItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RaisePropertyChanged(nameof(ShowDetail));
            }
        }
    }

    /// <summary>详情栏开着(用户可用 Ctrl+Space 切换)。</summary>
    public bool DetailEnabled { get; set; } = true;

    /// <summary>右侧详情栏显示与否。</summary>
    public bool ShowDetail => DetailEnabled && _selected?.HasDetail == true;

    /// <summary>有没有标题行。</summary>
    public bool HasHeader => !string.IsNullOrEmpty(Set.Header);

    /// <summary>有没有底部提示。</summary>
    public bool HasFooter => !string.IsNullOrEmpty(Set.Footer);

    /// <summary>替换区间起点。</summary>
    public int ReplaceOffset => Set.ReplaceOffset;

    /// <summary>当前替换区间长度(随打字增长)。</summary>
    public int ReplaceLength => Set.ReplaceLength + (_prefix.Length - InitialPrefix.Length);

    /// <summary>打开时光标前那半截词。</summary>
    public string InitialPrefix { get; init; } = "";

    /// <summary>按新的前缀过滤:前缀匹配在前,包含匹配在后;一个都不剩时返回 false(调用方关掉弹层)。</summary>
    public bool Filter(string prefix)
    {
        _prefix = prefix;
        string needle = prefix.TrimStart('"', '\'');
        List<CompletionItem> next =
        [
            .. _all.Where(i => Matches(i.Label, needle, prefix: true)),
            .. _all.Where(i => !Matches(i.Label, needle, prefix: true) && Matches(i.Label, needle, prefix: false))
        ];
        Items.Clear();
        foreach (CompletionItem item in next)
        {
            Items.Add(item);
        }
        Selected = Items.FirstOrDefault();
        return Items.Count > 0;
    }

    private static bool Matches(string label, string needle, bool prefix)
    {
        string bare = label.TrimStart('"', '\'');
        return needle.Length == 0
               || (prefix
                   ? bare.StartsWith(needle, StringComparison.OrdinalIgnoreCase) || label.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                   : bare.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>上下移动选中。</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        int index = _selected is null ? 0 : Items.IndexOf(_selected);
        Selected = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    /// <summary>切换详情栏(Ctrl+Space 再按一次)。</summary>
    public void ToggleDetail()
    {
        DetailEnabled = !DetailEnabled;
        RaisePropertyChanged(nameof(ShowDetail));
    }
}
