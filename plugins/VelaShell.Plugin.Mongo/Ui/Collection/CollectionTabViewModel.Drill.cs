using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 网格钻入(Navicat 式):双击对象 / 数组单元格,网格换成那一层的子表;面包屑与 Backspace 往回走。
/// <para>
/// 网格绑的是 <see cref="GridColumns" /> / <see cref="GridRows" /> / <see cref="GridSelectedRow" />:
/// 不钻入时它们就是本页文档那一套,钻入时换成子表。树视图、JSON 卡片与检查器始终看文档那一层 ——
/// 检查器讲的仍是子表所属的那份文档。
/// </para>
/// </summary>
internal sealed partial class CollectionTabViewModel
{
    private ObservableCollection<CollectionRow> _drillRows = [];
    private CollectionRow? _drillSelected;
    private IReadOnlyList<CollectionRow> _drillSelectedRows = [];
    private readonly List<string> _drillHistory = [];
    private bool _swappingGrid;

    /// <summary>正在看某个对象 / 数组的子表。</summary>
    public bool IsDrilled => Drill is not null;

    /// <summary>当前这一层;没钻入为 <see langword="null" />。</summary>
    public GridDrill? Drill { get; private set; }

    /// <summary>网格的列。</summary>
    public IReadOnlyList<CollectionColumn> GridColumns => Drill?.Columns ?? Columns;

    /// <summary>网格的行。</summary>
    public ObservableCollection<CollectionRow> GridRows => Drill is null ? Rows : _drillRows;

    /// <summary>网格选中的行(钻入时是子表里的那一行;检查器仍看它所属的文档)。</summary>
    public CollectionRow? GridSelectedRow
    {
        get => Drill is null ? _selectedRow : _drillSelected;
        set
        {
            // 换数据源的那一刻 ListBox 会把选中清成 null 推回来 —— 那不是用户的选择。
            if (_swappingGrid)
            {
                return;
            }
            if (Drill is null)
            {
                SelectedRow = value;
                return;
            }
            CollectionRow? previous = _drillSelected;
            if (ReferenceEquals(previous, value))
            {
                return;
            }
            _drillSelected = value;
            UpdateCurrentCell(previous, value);
            RaisePropertyChanged();
        }
    }

    /// <summary>网格里多选的行(由视图在选中变化时写入)。</summary>
    public IReadOnlyList<CollectionRow> GridSelectedRows
    {
        get => Drill is null ? SelectedRows : _drillSelectedRows.Count > 0 ? _drillSelectedRows : _drillSelected is { } one ? [one] : [];
        set
        {
            if (_swappingGrid)
            {
                return;
            }
            if (Drill is null)
            {
                SelectedRows = value;
            }
            else
            {
                _drillSelectedRows = value;
            }
        }
    }

    /// <summary>面包屑:<c>文档 #3 › items › [2] › attrs</c>。</summary>
    public IReadOnlyList<DrillCrumb> DrillCrumbs { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>面包屑右侧的小字(<c>数组 · 4 项</c>)。</summary>
    public string DrillSummary => Drill?.Summary(Loc) ?? "";

    /// <summary>面包屑左侧的「‹」。</summary>
    public RelayCommand DrillBackCommand { get; private set; } = null!;

    /// <summary>点面包屑的某一段。</summary>
    public RelayCommand<DrillCrumb> DrillToCommand { get; private set; } = null!;

    private void InitializeDrillCommands()
    {
        DrillBackCommand = new(DrillBack, () => Drill is not null);
        DrillToCommand = new(crumb =>
        {
            if (Drill is not { } drill || crumb.IsLast)
            {
                return;
            }
            // 往回跳:历史里比目标更深的都不要了。
            _ = _drillHistory.RemoveAll(p => p.Length >= crumb.Path.Length);
            if (crumb.Path.Length == 0)
            {
                ExitDrill(drill.Path);
            }
            else
            {
                _ = Show(drill.Root, crumb.Path, focusFrom: drill.Path);
            }
        });
    }

    /// <summary>
    /// 钻进一格(双击 / Enter / 右键「展开」):对象与数组才进得去。
    /// 返回 false 表示这格不是容器,调用方照常走内联编辑。
    /// </summary>
    internal bool DrillInto(CollectionCell cell)
    {
        if (!cell.IsContainer)
        {
            return false;
        }
        CancelGridEdits();
        if (Drill is { } current)
        {
            _drillHistory.Add(current.Path);
        }
        return Show(cell.Row.Root, cell.Path, focusFrom: null);
    }

    /// <summary>回上一层(Backspace / Alt+← / 「‹」);已在最外层时什么也不做。</summary>
    internal void DrillBack()
    {
        if (Drill is not { } drill)
        {
            return;
        }
        CancelGridEdits();
        if (_drillHistory.Count > 0)
        {
            string previous = _drillHistory[^1];
            _drillHistory.RemoveAt(_drillHistory.Count - 1);
            if (Show(drill.Root, previous, focusFrom: drill.Path))
            {
                return;
            }
        }
        ExitDrill(drill.Path);
    }

    /// <summary>退出钻入,回到本页文档;选中回到那份文档、当前列回到钻进去时的那一列。</summary>
    internal void ExitDrill(string? focusFrom = null)
    {
        if (Drill is not { } drill)
        {
            return;
        }
        CancelGridEdits();
        _drillHistory.Clear();
        _swappingGrid = true;
        try
        {
            Drill = null;
            _drillRows = [];
            _drillSelected = null;
            _drillSelectedRows = [];
            RaiseGridChanged();
        }
        finally
        {
            _swappingGrid = false;
        }
        string? first = focusFrom?.Split('.')[0];
        if (first is not null && Columns.FirstOrDefault(c => c.Name == first) is { } column)
        {
            _currentColumn = column;
            RaisePropertyChanged(nameof(CurrentColumn));
        }
        CollectionRow? root = Rows.Contains(drill.Root) ? drill.Root : _selectedRow;
        _selectedRow = null;
        SelectedRow = root;
        RaisePropertyChanged(nameof(GridSelectedRow));
    }

    /// <summary>
    /// 换到 <paramref name="root" /> 里 <paramref name="path" /> 那一层。<paramref name="focusFrom" /> 是刚离开的那一层的路径:
    /// 往回走时用它把选中落回"刚才是从哪一行、哪一列钻进去的"。
    /// </summary>
    private bool Show(CollectionRow root, string path, string? focusFrom)
    {
        IReadOnlyList<CollectionColumn>? previous = Drill is { } old && old.Path == path ? old.Columns : null;
        var drill = GridDrill.Create(this, root, path, previous, out List<CollectionRow> rows);
        if (drill is null)
        {
            return false;
        }
        int keepIndex = Drill is { } same && same.Path == path ? (_drillSelected?.Number ?? 0) : 0;
        string? keepColumn = _currentColumn?.Name;
        _swappingGrid = true;
        try
        {
            Drill = drill;
            _drillRows = [with(rows)];
            _drillSelected = null;
            _drillSelectedRows = [];
            RaiseGridChanged();
        }
        finally
        {
            _swappingGrid = false;
        }
        if (!ReferenceEquals(_selectedRow, root))
        {
            SelectedRow = root;
        }

        // 往回走:从 focusFrom 里认出刚才那一行(数组下标)与那一列(下一段字段名)。
        CollectionRow? select = null;
        CollectionColumn? column = null;
        if (focusFrom is not null && focusFrom.Length > path.Length && focusFrom.StartsWith(path + ".", StringComparison.Ordinal))
        {
            string[] rest = focusFrom[(path.Length + 1)..].Split('.');
            int at = 0;
            if (drill.IsArray && int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                select = rows.FirstOrDefault(r => r.Number == index);
                at = 1;
            }
            if (at < rest.Length)
            {
                column = drill.Columns.FirstOrDefault(c => !c.IsElementValue && c.Name == rest[at]);
            }
            else if (drill.IsArray)
            {
                column = drill.Columns.FirstOrDefault(static c => c.IsElementValue);
            }
        }
        select ??= rows.FirstOrDefault(r => r.Number == keepIndex) ?? rows.FirstOrDefault();
        column ??= drill.Columns.FirstOrDefault(c => c.Name == keepColumn)
                   ?? drill.Columns.FirstOrDefault(static c => c.Name != "_id")
                   ?? drill.Columns.FirstOrDefault();
        _currentColumn = column;
        RaisePropertyChanged(nameof(CurrentColumn));
        GridSelectedRow = select;
        if (select is not null)
        {
            RowAdded?.Invoke(select);
        }
        return true;
    }

    /// <summary>暂存区变了:子表按所属文档的新版本重建(元素可能多了、少了),停在原来的下标上。</summary>
    private void RefreshDrill()
    {
        if (Drill is not { } drill)
        {
            return;
        }
        if (!Rows.Contains(drill.Root))
        {
            ExitDrill();
            return;
        }
        // 形状没变(元素数、列都一样)就原地重算各行 —— 改一格不该把子表整个换掉,选中、焦点、滚动位置都留着。
        var fresh = GridDrill.Create(this, drill.Root, drill.Path, drill.Columns, out _);
        if (fresh is not null && fresh.IsArray == drill.IsArray && fresh.Count == drill.Count
            && fresh.Columns.Select(static c => (c.Name, c.IsElementValue)).SequenceEqual(drill.Columns.Select(static c => (c.Name, c.IsElementValue))))
        {
            foreach (CollectionRow row in _drillRows)
            {
                row.Recompute();
            }
            return;
        }
        if (!Show(drill.Root, drill.Path, focusFrom: null))
        {
            // 容器本身没了(撤销了新加的字段、被改成了标量):退到最近一层还在的祖先,都没了就回文档。
            string path = drill.Path;
            while (path.LastIndexOf('.') is var dot and > 0)
            {
                path = path[..dot];
                if (Show(drill.Root, path, focusFrom: null))
                {
                    return;
                }
            }
            ExitDrill();
        }
    }

    /// <summary>重查 / 翻页之后:按 <c>_id</c>(新增行按暂存键)找回那份文档,接着看同一层;找不到就退出。</summary>
    private void ReattachDrill()
    {
        if (Drill is not { } drill)
        {
            return;
        }
        CollectionRow? root = drill.Root.Insert is { } insert
            ? Rows.FirstOrDefault(r => ReferenceEquals(r.Insert, insert))
            : Rows.FirstOrDefault(r => drill.Root.Id is { } id && id.Equals(r.Id));
        if (root is null || !Show(root, drill.Path, focusFrom: null))
        {
            ExitDrill();
        }
    }

    /// <summary>底栏「+」在子表里:数组末尾加一个元素;对象里加一个字段。</summary>
    private void AddDrillRow(GridDrill drill)
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        if (drill.IsArray)
        {
            InsertArrayElement(drill.Root, drill.Path);
            if (Drill is { } now && _drillRows.LastOrDefault() is { } last)
            {
                GridSelectedRow = last;
                _currentColumn = now.Columns.FirstOrDefault(static c => c.IsElementValue) ?? _currentColumn;
                RaisePropertyChanged(nameof(CurrentColumn));
                RowAdded?.Invoke(last);
            }
            return;
        }
        PromptAddField(drill.Root, drill.Path);
    }

    /// <summary>工具栏「删除」、底栏「−」、Del:网格钻入时删子表里的元素,否则暂存删除选中的文档。</summary>
    private void DeleteGridSelection()
    {
        if (Drill is { } drill && ViewMode == CollectionViewMode.Grid)
        {
            DeleteDrillRows(drill);
        }
        else
        {
            StageDelete(SelectedRows);
        }
    }

    /// <summary>
    /// 删除子表里选中的元素(数组);对象那一层没有"行"可删,删的是当前格那个字段。
    /// 数组按下标从大到小删 —— 先删小的,后面的下标就全错位了。
    /// </summary>
    private void DeleteDrillRows(GridDrill drill)
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        if (!drill.IsArray)
        {
            if (_drillSelected is { } row && _currentColumn is { } column && row.CellOf(column) is { IsMissing: false } cell)
            {
                DeleteField(row, cell.Path);
            }
            return;
        }
        List<int> indexes = [.. GridSelectedRows.Select(static r => r.Number).Distinct().OrderDescending()];
        if (indexes.Count == 0 || BsonPath.Get(drill.Root.Document, drill.Path) is not BsonArray array)
        {
            return;
        }
        var copy = (BsonArray)array.DeepClone();
        foreach (int index in indexes.Where(i => i >= 0 && i < copy.Count))
        {
            copy.RemoveAt(index);
        }
        _ = Stage(drill.Root, drill.Path, copy);
    }

    private void RaiseGridChanged()
    {
        RaisePropertyChanged(nameof(GridColumns));
        RaisePropertyChanged(nameof(GridRows));
        RaisePropertiesChanged(nameof(IsDrilled), nameof(Drill), nameof(DrillSummary));
        DrillCrumbs = BuildCrumbs();
        DrillBackCommand.RaiseCanExecuteChanged();
    }

    private List<DrillCrumb> BuildCrumbs()
    {
        if (Drill is not { } drill)
        {
            return [];
        }
        string document = drill.Root.Insert is null
            ? Loc.Format("Cw_DrillDoc", drill.Root.NumberText)
            : Loc["Cw_DrillNewDoc"];
        var crumbs = new List<DrillCrumb> { new(document, "", false) };
        string[] segments = drill.Path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            crumbs.Add(new(GridDrill.SegmentLabel(segments[i]), string.Join('.', segments[..(i + 1)]), i == segments.Length - 1));
        }
        return crumbs;
    }

    /// <summary>换层之前收起正在编辑的格子(编辑器挂在旧的行上,换了数据源就再也提交不了)。</summary>
    private void CancelGridEdits()
    {
        if (GridSelectedRow?.Cells is { } cells)
        {
            foreach (CollectionCell cell in cells.Where(static c => c.IsEditing))
            {
                cell.Editor = null;
            }
        }
    }
}
