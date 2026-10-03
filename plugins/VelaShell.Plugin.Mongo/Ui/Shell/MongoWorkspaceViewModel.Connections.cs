using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 连接的生命周期:读出已保存的连接、连 / 断、新建 / 编辑 / 复制 / 删除、信任证书后重连。
/// 连的过程照宿主「先建标签、后连会话」的习惯:先开一个占位标签(设计稿 22 的「正在连接」卡片),
/// 连上了换成对象列表,没连上就在原地变成「无法连接」卡片 —— 不弹模态框。
/// </summary>
internal sealed partial class MongoWorkspaceViewModel
{
    /// <summary>读出已保存的连接,挂到对象树根上(常用的在上面)。不自动连 —— 连哪条由用户决定。</summary>
    /// <returns>任务。</returns>
    public async Task InitializeAsync()
    {
        IReadOnlyList<MongoProfile> profiles;
        try
        {
            profiles = await Profiles.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Loading saved MongoDB connections failed.", ex);
            Toast(new() { Title = Loc.Format("Conn_LoadFailed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
            profiles = [];
        }
        foreach (MongoProfile profile in profiles
                     .OrderByDescending(static p => p.LastConnectedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(static p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Connections.All(c => c.Profile.Id != profile.Id))
            {
                Connections.Add(Track(new ConnectionEntry(profile, Loc)));
            }
        }
        RaisePropertyChanged(nameof(HasNoConnections));
        RebuildVisible();
        if (SelectedNode is null && Connections.FirstOrDefault() is { } first)
        {
            SelectedNode = first.Root;
        }
    }

    private ConnectionEntry Track(ConnectionEntry entry)
    {
        entry.StateChanged += RefreshDetailsFor;
        return entry;
    }

    /// <summary>新建连接(设计稿 10)。</summary>
    internal void NewConnection() =>
        ShowDialog(new ConnectionDialogViewModel(this, new MongoProfile { Name = NextName("mongo") }, isNew: true));

    /// <summary>编辑一条连接。口令不回填到框里 —— 留空表示不改。</summary>
    /// <param name="entry">连接。</param>
    internal void EditConnection(ConnectionEntry entry) =>
        ShowDialog(new ConnectionDialogViewModel(this, entry.Profile.Clone(), isNew: false));

    /// <summary>复制一条连接(新 id、名字加「副本」,口令一起带过去),在对话框里改完再存。</summary>
    /// <param name="entry">连接。</param>
    internal void DuplicateConnection(ConnectionEntry entry)
    {
        MongoProfile copy = entry.Profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = NextName(Loc.Format("Conn_CopyName", entry.Name));
        copy.LastConnectedAt = null;
        ShowDialog(new ConnectionDialogViewModel(this, copy, isNew: true, passwordFrom: entry.Profile.Id));
    }

    private string NextName(string stem)
    {
        if (Connections.All(c => !string.Equals(c.Name, stem, StringComparison.OrdinalIgnoreCase)))
        {
            return stem;
        }
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem}-{i}";
            if (Connections.All(c => !string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 对话框点了「仅保存」或「保存并连接」:落盘,挂到树上(或换掉旧的那条),要连就连。
    /// 改的是连着的那条时先断开再连 —— 设置变了,旧连接不再代表它。
    /// </summary>
    /// <param name="profile">连接。</param>
    /// <param name="password">口令;<see langword="null" /> = 不改已存的。</param>
    /// <param name="connect">存完就连。</param>
    /// <returns>任务。</returns>
    internal async Task SaveProfileAsync(MongoProfile profile, string? password, bool connect)
    {
        await Profiles.SaveAsync(profile, password).ConfigureAwait(true);
        ConnectionEntry? entry = Connections.FirstOrDefault(c => c.Profile.Id == profile.Id);
        bool wasConnected = entry?.State == ConnectionState.Connected;
        if (entry is null)
        {
            entry = Track(new ConnectionEntry(profile.Clone(), Loc));
            Connections.Insert(0, entry);
            RaisePropertyChanged(nameof(HasNoConnections));
        }
        else
        {
            entry.Profile = profile.Clone();
        }
        RebuildVisible();
        SelectedNode = entry.Root;
        if (connect)
        {
            if (wasConnected)
            {
                await DisconnectAsync(entry).ConfigureAwait(true);
            }
            await ConnectAsync(entry).ConfigureAwait(true);
        }
        else if (wasConnected)
        {
            Toast(new() { Title = Loc["Conn_SavedReconnectHint"], Kind = ToastKind.Info });
        }
    }

    /// <summary>连一条。已连着就选中它;正在连就什么都不做。</summary>
    /// <param name="entry">连接。</param>
    /// <param name="quiet">
    /// 安静地连(查询编辑器切换连接时):不开「正在连接」占位标签、连上后不改对象树的选中、不开对象列表 ——
    /// 调用方自己接着用这条连接;失败只记在 <see cref="ConnectionEntry.Failure" /> 上,由调用方报。
    /// </param>
    /// <returns>任务。</returns>
    internal async Task ConnectAsync(ConnectionEntry entry, bool quiet = false)
    {
        if (entry.State == ConnectionState.Connecting)
        {
            return;
        }
        if (entry.State == ConnectionState.Connected)
        {
            if (!quiet)
            {
                SelectedNode = entry.Root;
            }
            return;
        }
        using var cts = new CancellationTokenSource();
        entry.Connecting = cts;
        entry.Failure = null;
        entry.State = ConnectionState.Connecting;
        ConnectionStateTabViewModel? stateTab = quiet ? null : Activate(new ConnectionStateTabViewModel(this, entry));
        MongoLink link;
        try
        {
            string password = await Profiles.GetPasswordAsync(entry.Profile.Id, cts.Token).ConfigureAwait(true);
            link = await Connector.ConnectAsync(entry.Profile, password, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            entry.State = ConnectionState.Disconnected;
            CloseTabsWhere(t => ReferenceEquals(t, stateTab));
            return;
        }
        catch (Exception ex)
        {
            entry.Failure = ex as MongoConnectException ?? new MongoConnectException(MongoConnector.Describe(ex), ConnectFailureKind.Network, ex);
            entry.State = ConnectionState.Failed;
            Log.Info($"Connecting '{entry.Name}' failed: {entry.Failure.Message}");
            return;
        }
        finally
        {
            entry.Connecting = null;
        }
        if (!Connections.Contains(entry))
        {
            // 连的这段时间里这条连接被删了。
            await link.DisposeAsync().ConfigureAwait(true);
            return;
        }
        CloseTabsWhere(t => ReferenceEquals(t, stateTab));
        await AttachAsync(entry, link, quiet).ConfigureAwait(true);
        entry.Profile.LastConnectedAt = DateTimeOffset.Now;
        try
        {
            await Profiles.SaveAsync(entry.Profile, null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 只是记一下"最近连过",存不下不值得打扰用户。
            Log.Info($"Recording the last connection time failed: {ex.Message}");
        }
    }

    /// <summary>连上之后:建会话、读库列表、展开默认库,开它的对象列表(<paramref name="quiet" /> 时只建会话、读库列表)。</summary>
    /// <param name="entry">连接。</param>
    /// <param name="link">连上的一条。</param>
    /// <param name="quiet">不动对象树的选中、不开对象列表。</param>
    /// <returns>会话。</returns>
    internal async Task<MongoSession> AttachAsync(ConnectionEntry entry, MongoLink link, bool quiet = false)
    {
        var session = new MongoSession(this, entry, link);
        entry.Session = session;
        entry.State = ConnectionState.Connected;
        await session.ReloadTreeAsync().ConfigureAwait(true);
        if (quiet)
        {
            return session;
        }
        string? db = session.DefaultDatabase();
        if (db is not null && session.Root.Children.FirstOrDefault(n => n.Name == db) is { } dbNode)
        {
            SelectedNode = dbNode.Children.FirstOrDefault(static f => f.Folder == FolderKind.Collections)?.Children.FirstOrDefault() ?? dbNode;
        }
        else
        {
            SelectedNode = entry.Root;
        }
        if (db is not null)
        {
            session.OpenObjects(db);
        }
        UpdateCurrentSession();
        return session;
    }

    /// <summary>
    /// 单测与截图用:把一条已经连上的连接挂到树上(不经存储、不建跳板)。
    /// </summary>
    /// <param name="profile">连接。</param>
    /// <param name="connection">连上的连接。</param>
    /// <returns>会话。</returns>
    internal Task<MongoSession> AttachAsync(MongoProfile profile, MongoConnection connection)
    {
        var entry = Track(new ConnectionEntry(profile, Loc));
        Connections.Add(entry);
        RaisePropertyChanged(nameof(HasNoConnections));
        RebuildVisible();
        return AttachAsync(entry, new MongoLink(connection, null));
    }

    /// <summary>断开一条:它的标签先过「有未提交的修改」那一问,任何一个不肯关就不断。正在连就取消。</summary>
    /// <param name="entry">连接。</param>
    /// <returns>是否断开了。</returns>
    internal async Task<bool> DisconnectAsync(ConnectionEntry entry)
    {
        if (entry.State == ConnectionState.Connecting)
        {
            entry.Connecting?.Cancel();
            return true;
        }
        if (entry.Session is not { } session)
        {
            entry.State = ConnectionState.Disconnected;
            entry.Failure = null;
            CloseTabsWhere(t => t is ConnectionStateTabViewModel state && ReferenceEquals(state.Entry, entry));
            return true;
        }
        foreach (WorkspaceTab tab in Tabs.Where(t => ReferenceEquals(t.Owner, session)).ToList())
        {
            if (!tab.CanClose || !await tab.ConfirmCloseAsync().ConfigureAwait(true))
            {
                ActiveTab = tab;
                return false;
            }
        }
        CloseTabsWhere(t => ReferenceEquals(t.Owner, session));
        if (Dialog is { } open && open.Owner is { } owner && ReferenceEquals(owner, session))
        {
            CloseDialog(open);
        }
        entry.Session = null;
        session.Dispose();
        entry.State = ConnectionState.Disconnected;
        if (SelectedNode?.Owner == entry)
        {
            SelectedNode = entry.Root;
        }
        RebuildVisible();
        UpdateCurrentSession();
        await session.Link.DisposeAsync().ConfigureAwait(true);
        Log.Info($"MongoDB connection '{entry.Name}' closed.");
        return true;
    }

    /// <summary>删掉一条连接(连同存着的口令)。连着就先断开。</summary>
    /// <param name="entry">连接。</param>
    /// <returns>任务。</returns>
    internal async Task DeleteConnectionAsync(ConnectionEntry entry)
    {
        if (!await ConfirmAsync(new()
            {
                Title = Loc["Conn_DeleteTitle"],
                Message = Loc.Format("Conn_DeleteBody", entry.Name),
                ConfirmLabel = Loc["Conn_DeleteConfirm"],
                IconKey = "Mongo.trash-2",
                Danger = true
            }).ConfigureAwait(true))
        {
            return;
        }
        if (!await DisconnectAsync(entry).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await Profiles.DeleteAsync(entry.Profile.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
            return;
        }
        CloseTabsWhere(t => t is ConnectionStateTabViewModel state && ReferenceEquals(state.Entry, entry));
        Connections.Remove(entry);
        if (SelectedNode?.Owner == entry)
        {
            SelectedNode = Connections.FirstOrDefault()?.Root;
        }
        RaisePropertyChanged(nameof(HasNoConnections));
        RebuildVisible();
    }

    /// <summary>证书不受信任:记下这张证书的指纹(只信这一张),然后重连。</summary>
    /// <param name="entry">连接。</param>
    /// <returns>任务。</returns>
    internal async Task TrustAndReconnectAsync(ConnectionEntry entry)
    {
        if (entry.Failure?.Certificate is not { } certificate)
        {
            return;
        }
        MongoProfile updated = entry.Profile.Clone();
        updated.Set(MongoSettings.KeyTrustedThumbprint, certificate.Thumbprint);
        try
        {
            await Profiles.SaveAsync(updated, null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
            return;
        }
        entry.Profile = updated;
        entry.State = ConnectionState.Disconnected;
        await ConnectAsync(entry).ConfigureAwait(true);
    }
}
