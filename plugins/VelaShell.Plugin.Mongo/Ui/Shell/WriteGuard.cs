using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 写护栏:只读开关、写前确认、dropDatabase 禁用、权限置灰。
/// <para>
/// 只读是**客户端**护栏 —— 真正的只读应当落在服务器端的用户权限上(只授 read)。
/// 但"生产连接默认只读、写之前手动解锁"这一步拦下的,恰恰是最常见的那一类事故:
/// 在一个以为是测试库的标签页里批量改了数据。
/// </para>
/// </summary>
internal sealed class WriteGuard : ObservableObject
{
    private bool _isReadOnly;

    /// <summary>按连接设置与探到的权限初始化。</summary>
    /// <param name="settings">连接设置。</param>
    /// <param name="privileges">当前用户的权限。</param>
    public WriteGuard(MongoSettings settings, PrivilegeSummary privileges)
    {
        Environment = settings.Environment;
        _isReadOnly = settings.ReadOnly;
        ConfirmWrites = settings.ConfirmWrites;
        DisableDropDatabase = settings.DisableDropDatabase;
        Privileges = privileges;
    }

    /// <summary>环境标记。</summary>
    public MongoEnvironment Environment { get; }

    /// <summary>是不是生产连接。</summary>
    public bool IsProduction => Environment == MongoEnvironment.Production;

    /// <summary>只读模式(工具栏「读写」开关)。</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (SetProperty(ref _isReadOnly, value))
            {
                RaisePropertyChanged(nameof(IsWritable));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>可写(只读关着)。</summary>
    public bool IsWritable => !_isReadOnly;

    /// <summary>写操作前二次确认(删除、批量更新、dropCollection 要手打名称)。</summary>
    public bool ConfirmWrites { get; }

    /// <summary>不提供 dropDatabase。</summary>
    public bool DisableDropDatabase { get; }

    /// <summary>当前用户的权限。</summary>
    public PrivilegeSummary Privileges { get; private set; }

    /// <summary>只读状态或权限变了(各标签页据此刷新按钮可用性)。</summary>
    public event Action? Changed;

    /// <summary>重探权限之后更新。</summary>
    public void UpdatePrivileges(PrivilegeSummary privileges)
    {
        Privileges = privileges;
        Changed?.Invoke();
    }

    /// <summary>在这个库上能不能写;不能时给出原因文案键。</summary>
    /// <param name="database">库名。</param>
    /// <returns>不能写的原因(文案键);能写为 <see langword="null" />。</returns>
    public string? Check(string database)
    {
        if (_isReadOnly)
        {
            return "Common_ReadOnlyBlocked";
        }
        return Privileges.CanWrite(database) ? null : "Common_NoPrivilege";
    }
}
