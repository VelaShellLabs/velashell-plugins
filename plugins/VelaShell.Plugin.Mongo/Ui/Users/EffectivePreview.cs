using System.Collections.ObjectModel;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 「有效权限预览」:把一组角色(加上角色自己定义的权限)展开成 资源 → 动作 的清单,
/// 并与服务器现状对齐,标出这次改动会多出 / 失去什么。
/// <para>
/// 展开靠服务器(<c>rolesInfo … showPrivileges</c>)而不是客户端维护一张内置角色表:
/// 内置角色的动作随版本变(7.0 加了 listSearchIndexes 一类),自己抄一份迟早过时,
/// 而预览恰恰是让人确认"这次授权到底给了什么"的地方 —— 说错比不说更糟。
/// 勾选连续变化时防抖 150ms,结果按角色缓存,来回勾不会反复打服务器。
/// </para>
/// </summary>
internal sealed class EffectivePreview : ObservableObject, IDisposable
{
    private readonly UsersTabViewModel _owner;
    private CancellationTokenSource? _pending;
    private bool _isBusy;
    private string _message = "";

    /// <summary>构造。</summary>
    public EffectivePreview(UsersTabViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>预览行。</summary>
    public ObservableCollection<EffectiveLine> Lines { get; } = [];

    /// <summary>正在算。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>空态 / 出错时的一句话。</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                RaisePropertyChanged(nameof(HasMessage));
            }
        }
    }

    /// <summary>有没有那句话。</summary>
    public bool HasMessage => _message.Length > 0;

    /// <summary>请求重算(防抖)。</summary>
    /// <param name="originalRoles">服务器现状的角色。</param>
    /// <param name="originalOwn">服务器现状里角色自己定义的权限(用户为空)。</param>
    /// <param name="currentRoles">含未保存改动的角色。</param>
    /// <param name="currentOwn">含未保存改动的自有权限。</param>
    public void Request(
        IReadOnlyCollection<RoleRef> originalRoles,
        IReadOnlyList<Privilege> originalOwn,
        IReadOnlyCollection<RoleRef> currentRoles,
        IReadOnlyList<Privilege> currentOwn)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = RunAsync(originalRoles, originalOwn, currentRoles, currentOwn, cts.Token);
    }

    private async Task RunAsync(
        IReadOnlyCollection<RoleRef> originalRoles,
        IReadOnlyList<Privilege> originalOwn,
        IReadOnlyCollection<RoleRef> currentRoles,
        IReadOnlyList<Privilege> currentOwn,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(true);
            IsBusy = true;
            IReadOnlyDictionary<RoleRef, IReadOnlyList<Privilege>> resolved =
                await _owner.ResolvePrivilegesAsync(originalRoles.Concat(currentRoles), cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EffectiveLine> lines = UserAdmin.EffectiveLines(
                UserAdmin.Aggregate(Expand(originalRoles, originalOwn, resolved)),
                UserAdmin.Aggregate(Expand(currentRoles, currentOwn, resolved)));
            Lines.Clear();
            foreach (EffectiveLine line in lines)
            {
                Lines.Add(line);
            }
            Message = lines.Count == 0 ? _owner.Loc["Users_NoPrivileges"] : "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Lines.Clear();
            Message = _owner.Loc.Format("Users_PreviewFailed", MongoConnector.Describe(ex));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private static IEnumerable<Privilege> Expand(
        IEnumerable<RoleRef> roles,
        IEnumerable<Privilege> own,
        IReadOnlyDictionary<RoleRef, IReadOnlyList<Privilege>> resolved) =>
        own.Concat(roles.SelectMany(r => resolved.TryGetValue(r, out IReadOnlyList<Privilege>? p) ? p : []));

    /// <inheritdoc />
    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
