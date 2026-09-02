using System.Globalization;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 多选与批量操作。
/// <para>
/// 选中集按**键名**存,不挂在行对象上:列表每来一页 <c>SCAN</c> 就整份重排,展开一条分组行
/// 也会重排 —— 挂在行上的勾会在这些时刻莫名其妙地消失,而用户根本关联不到原因。
/// </para>
/// <para>
/// 批量删除走「危」档:逐次确认即可,不要求手打确认串(那是「毁」档的规格)。
/// 用 <c>UNLINK</c> 而不是 <c>DEL</c> —— 理由与单键删除同一条。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>已勾选的键。</summary>
    private readonly HashSet<RedisKeyName> _checkedKeys = [];

    /// <summary>
    /// 多选模式。
    /// <para>做成一个显式的模式而不是"按住 Ctrl 点":这一栏的行本来就要响应单击(看详情),
    /// 让同一次点击既选中又勾选,迟早会出现"我只是想看看它,怎么被勾上了"。</para>
    /// </summary>
    public bool IsMultiSelect
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasCheckedKeys));
            if (!field)
            {
                ClearChecked();
            }
        }
    }

    /// <summary>有勾选的键(批量条据此占位置)。</summary>
    public bool HasCheckedKeys => IsMultiSelect && _checkedKeys.Count > 0;

    /// <summary>勾选的键数。</summary>
    public int CheckedCount => _checkedKeys.Count;

    /// <summary>批量条左侧的摘要。</summary>
    public string BatchSummary =>
        Loc.Format("Redis_BatchSelected", _checkedKeys.Count.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>进入/退出多选模式。</summary>
    public AsyncCommand ToggleMultiSelectCommand { get; private set; } = null!;

    /// <summary>勾选 / 取消勾选一行(列表模板里的复选框绑它)。</summary>
    public AsyncCommand<RedisKeyRow?> ToggleCheckedCommand { get; private set; } = null!;

    /// <summary>复制勾选的键名到剪贴板(一行一个)。</summary>
    public AsyncCommand CopyKeyNamesCommand { get; private set; } = null!;

    /// <summary>批量删除勾选的键。</summary>
    public AsyncCommand BatchDeleteCommand { get; private set; } = null!;

    /// <summary>
    /// 请视图把一段文本放进剪贴板。
    /// <para>剪贴板挂在 <c>TopLevel</c> 上,视图模型够不着它 —— 也不该够得着:
    /// 一个能在测试里静默写系统剪贴板的视图模型是个惊喜,不是个特性。</para>
    /// </summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>勾选 / 取消勾选一行(视图上的复选框)。</summary>
    /// <param name="row">键行;分组行会把它底下**已扫到的**成员整批勾上。</param>
    public void ToggleChecked(RedisKeyRow? row)
    {
        if (row is null)
        {
            return;
        }
        if (row.Key is { } key)
        {
            if (!_checkedKeys.Remove(key))
            {
                _checkedKeys.Add(key);
            }
        }
        else
        {
            // 分组行:整批勾上/取下。范围限于**已扫描到的**那些 —— 一条写着 20 481 的分组行
            // 底下大部分键还没扫到,让一次点击去删没见过的键是危险的。
            string prefix = row.Display[..^1];
            var members = _scanned.Keys
                .Where(candidate => candidate.Display.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            bool allChecked = members.Count > 0 && members.TrueForAll(_checkedKeys.Contains);
            foreach (RedisKeyName member in members)
            {
                if (allChecked)
                {
                    _checkedKeys.Remove(member);
                }
                else
                {
                    _checkedKeys.Add(member);
                }
            }
        }
        SyncCheckedFlags();
    }

    /// <summary>清空勾选。</summary>
    public void ClearChecked()
    {
        _checkedKeys.Clear();
        SyncCheckedFlags();
    }

    /// <summary>把勾选态刷到当前这批行上,并通知批量条。</summary>
    private void SyncCheckedFlags()
    {
        foreach (RedisKeyRow row in Rows)
        {
            row.IsChecked = row.Key is { } key && _checkedKeys.Contains(key);
        }
        RaisePropertyChanged(nameof(HasCheckedKeys));
        RaisePropertyChanged(nameof(CheckedCount));
        RaisePropertyChanged(nameof(BatchSummary));
        CopyKeyNamesCommand.RaiseCanExecuteChanged();
        BatchDeleteCommand.RaiseCanExecuteChanged();
    }

    private void InitializeBatch()
    {
        ToggleMultiSelectCommand = new(() =>
        {
            IsMultiSelect = !IsMultiSelect;
            return Task.CompletedTask;
        });
        ToggleCheckedCommand = new(row =>
        {
            ToggleChecked(row);
            return Task.CompletedTask;
        });
        CopyKeyNamesCommand = new(CopyKeyNamesAsync, () => _checkedKeys.Count > 0);
        BatchDeleteCommand = new(BatchDeleteAsync, () => CanWrite && _checkedKeys.Count > 0);
    }

    private Task CopyKeyNamesAsync()
    {
        if (_checkedKeys.Count == 0)
        {
            StatusMessage = Loc["Redis_BatchNothing"];
            return Task.CompletedTask;
        }
        // 键名用**显示形式**(转义后):它能直接粘进控制台,也能粘进代码里当字面量。
        CopyRequested?.Invoke(this, string.Join('\n', _checkedKeys.Select(key => key.Display).Order(StringComparer.Ordinal)));
        StatusMessage = Loc.Format("Redis_BatchCopied", _checkedKeys.Count.ToString("N0", CultureInfo.CurrentCulture));
        return Task.CompletedTask;
    }

    private async Task BatchDeleteAsync()
    {
        if (_checkedKeys.Count == 0)
        {
            StatusMessage = Loc["Redis_BatchNothing"];
            return;
        }
        List<RedisKeyName> targets = [.. _checkedKeys];
        // 确认框里列前几条真实键名:一个只写着"3 个键"的框,用户没法核对自己勾对了没有。
        string preview = string.Join('\n', targets.Take(6).Select(key => key.Display))
                         + (targets.Count > 6 ? $"\n… +{targets.Count - 6}" : string.Empty);
        bool confirmed = await Confirmation.AskAsync(
            Loc.Format("Redis_BatchDeleteTitle", targets.Count.ToString("N0", CultureInfo.CurrentCulture)),
            Loc["Redis_BatchDeleteBody"],
            preview,
            Loc["Redis_BatchDelete"],
            Loc["Redis_Cancel"],
            destructive: true).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await GuardedAsync("UNLINK", async () =>
        {
            long removed = await _connection.DeleteAsync(targets).ConfigureAwait(true);
            ClearChecked();
            SelectedRow = null;
            await ScanAsync(restart: true).ConfigureAwait(true);
            StatusMessage = Loc.Format("Redis_BatchDeleted", removed.ToString("N0", CultureInfo.CurrentCulture));
        }).ConfigureAwait(true);
    }

    /// <summary>导出面板要的键集合:勾了就用勾的,没勾就用当前已扫到的全部。</summary>
    internal IReadOnlyList<RedisKeyName> KeysForTransfer(bool selectedOnly) =>
        selectedOnly && _checkedKeys.Count > 0 ? [.. _checkedKeys] : [.. _scanned.Keys];
}
