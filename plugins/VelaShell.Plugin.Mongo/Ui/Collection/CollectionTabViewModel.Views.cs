using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>树视图与 JSON 卡片:同一页文档的另外两种画法。</summary>
internal sealed partial class CollectionTabViewModel
{
    private readonly HashSet<string> _treeExpanded = [with(StringComparer.Ordinal)];
    private DocTreeRow? _selectedTreeRow;
    private JsonCardViewModel? _focusedCard;

    // ── 树 ───────────────────────────────────────────────────────────────────

    /// <summary>树视图的可见行。</summary>
    public ObservableCollection<DocTreeRow> TreeRows
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>树视图选中的行。</summary>
    public DocTreeRow? SelectedTreeRow
    {
        get => _selectedTreeRow;
        set
        {
            if (SetProperty(ref _selectedTreeRow, value) && value is not null && !ReferenceEquals(value.Row, _selectedRow))
            {
                SelectedRow = value.Row;
            }
        }
    }

    /// <summary>展开 / 收起一行。</summary>
    internal void ToggleTree(DocTreeRow row)
    {
        if (!row.IsExpandable)
        {
            return;
        }
        string key = DocTreeBuilder.ExpansionKey(row.Row, row.Path);
        if (!_treeExpanded.Remove(key))
        {
            _ = _treeExpanded.Add(key);
        }
        RebuildTree();
    }

    /// <summary>展开一份文档的某个路径(及其祖先)—— 截图与"定位到字段"用。</summary>
    internal void ExpandTreePath(CollectionRow row, string path)
    {
        _ = _treeExpanded.Add(DocTreeBuilder.ExpansionKey(row, ""));
        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= segments.Length; i++)
        {
            _ = _treeExpanded.Add(DocTreeBuilder.ExpansionKey(row, string.Join('.', segments[..i])));
        }
        RebuildTree();
    }

    /// <summary>重建树的可见行(保持选中)。只在树视图可见时做实事 —— 网格模式下不必为它付钱。</summary>
    internal void RebuildTree()
    {
        if (_viewMode != CollectionViewMode.Tree)
        {
            return;
        }
        (CollectionRow? keepRow, string? keepPath) = (_selectedTreeRow?.Row, _selectedTreeRow?.Path);
        List<DocTreeRow> rows = DocTreeBuilder.Build(_rows, _treeExpanded, Staging, Loc);
        TreeRows = [with(rows)];
        _selectedTreeRow = rows.FirstOrDefault(r => ReferenceEquals(r.Row, keepRow) && r.Path == keepPath)
                           ?? rows.FirstOrDefault(r => ReferenceEquals(r.Row, _selectedRow) && r.IsDocument);
        RaisePropertyChanged(nameof(SelectedTreeRow));
    }

    // ── JSON 卡片 ─────────────────────────────────────────────────────────────

    /// <summary>JSON 视图的卡片(一份文档一张)。</summary>
    public ObservableCollection<JsonCardViewModel> Cards
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>大纲面板看的那张卡片(编辑中的优先,否则跟着选中行)。</summary>
    public JsonCardViewModel? FocusedCard
    {
        get => _focusedCard;
        set
        {
            if (SetProperty(ref _focusedCard, value))
            {
                value?.RefreshOutline();
                RaisePropertyChanged(nameof(OutlineTitle));
                if (value is not null && !ReferenceEquals(value.Row, _selectedRow))
                {
                    SelectedRow = value.Row;
                }
            }
        }
    }

    /// <summary>大纲头部右侧的「文档 #4」。</summary>
    public string OutlineTitle => _focusedCard is { } card ? Loc.Format("Cw_OutlineDoc", card.Row.NumberText) : "";

    /// <summary>行内类型提示(编辑底栏显示光标所在字段的类型)。</summary>
    public bool InlineTypeHints
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _focusedCard?.UpdateCaret();
            }
        }
    } = true;

    /// <summary>保存前自动格式化(「更新文档」先把文本重排成标准缩进)。</summary>
    public bool FormatOnSave
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>重建卡片(只在 JSON 视图可见时)。正在编辑的卡片保留,免得刷新把用户敲了一半的文本冲掉。</summary>
    internal void RebuildCards()
    {
        if (_viewMode != CollectionViewMode.Json)
        {
            return;
        }
        var editing = Cards.Where(static c => c.IsEditing).ToDictionary(static c => c.Row);
        var cards = new List<JsonCardViewModel>(_rows.Count);
        foreach (CollectionRow row in _rows)
        {
            JsonCardViewModel? kept = editing.GetValueOrDefault(row)
                                      ?? editing.Values.FirstOrDefault(c => c.Row.Id is { } id && id.Equals(row.Id));
            cards.Add(kept is not null && ReferenceEquals(kept.Row, row) ? kept : new JsonCardViewModel(this, row));
        }
        Cards = [with(cards)];
        SyncCardSelection();
    }

    /// <summary>选中行变了:大纲跟着换到那张卡片(除非有卡片正在编辑)。</summary>
    private void SyncCardSelection()
    {
        if (_viewMode != CollectionViewMode.Json)
        {
            return;
        }
        JsonCardViewModel? editing = Cards.FirstOrDefault(static c => c.IsEditing);
        JsonCardViewModel? target = editing ?? Cards.FirstOrDefault(c => ReferenceEquals(c.Row, _selectedRow)) ?? Cards.FirstOrDefault();
        if (!ReferenceEquals(target, _focusedCard))
        {
            _focusedCard = target;
            target?.RefreshOutline();
            RaisePropertiesChanged(nameof(FocusedCard), nameof(OutlineTitle));
        }
    }

    /// <summary>一张卡片进入 / 退出编辑(大纲转去看它)。</summary>
    internal void OnCardEditingChanged(JsonCardViewModel card)
    {
        if (card.IsEditing)
        {
            foreach (JsonCardViewModel other in Cards.Where(c => c.IsEditing && !ReferenceEquals(c, card)).ToList())
            {
                other.CancelEdit();
            }
            FocusedCard = card;
        }
        SyncCardSelection();
        RaisePropertyChanged(nameof(PendingText));
    }
}
