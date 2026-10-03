using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 最小可观察基类。
/// <para>
/// 刻意**不引 ReactiveUI / CommunityToolkit**:前者会随插件目录分发一整套(ReactiveUI + Splat +
/// DynamicData),而且插件 ALC 里那份 <c>RxApp</c> 与宿主的是两个独立实例,调度器不会自动挂到
/// Avalonia 上;这里要的只是 <see cref="INotifyPropertyChanged" /> 与几个命令类型(与 Redis 插件同一口径)。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>值变化时赋值并通知;返回是否真的变了。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">后备字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名(自动填充)。</param>
    /// <returns>是否变了。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 手动触发一次属性变更通知(派生属性用)。
    /// <para>
    /// 绑定只能在 UI 线程更新。视图模型里不少加载逻辑跑在后台线程(<c>ConfigureAwait(false)</c> 之后),
    /// 统一在这里封送,免得每个调用点都记得 Dispatcher —— 漏掉一处就是一次崩溃而不是一次退化。
    /// </para>
    /// </summary>
    /// <param name="propertyName">属性名。</param>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (PropertyChanged is not { } handler)
        {
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(this, new(propertyName));
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(this, new(propertyName)));
        }
    }

    /// <summary>一次通知多个属性。</summary>
    /// <param name="names">属性名。</param>
    protected void RaisePropertiesChanged(params string[] names)
    {
        foreach (string name in names)
        {
            RaisePropertyChanged(name);
        }
    }
}

/// <summary>无参异步命令。执行期间自动禁用自己,避免重复点击叠加请求。</summary>
/// <param name="execute">命令体。</param>
/// <param name="canExecute">可用性判定;为 null 即恒可用。</param>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>正在执行。</summary>
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter) => await ExecuteAsync().ConfigureAwait(true);

    /// <summary>执行并等它做完(串起几步的代码与单测用)。不可用时直接返回。</summary>
    /// <returns>任务。</returns>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch
        {
            // 命令体自己负责把失败呈现到界面上;这里只保证不把异常抛进 async void 的同步上下文 ——
            // 那会直接崩掉宿主进程。
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>带参异步命令。</summary>
/// <typeparam name="T">参数类型。</typeparam>
/// <param name="execute">命令体。</param>
/// <param name="canExecute">可用性判定。</param>
public sealed class AsyncCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && parameter is T typed && (canExecute?.Invoke(typed) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            await ExecuteAsync(typed).ConfigureAwait(true);
        }
    }

    /// <summary>执行并等它做完(串起几步的代码与单测用)。不可用时直接返回。</summary>
    /// <param name="parameter">参数。</param>
    /// <returns>任务。</returns>
    public async Task ExecuteAsync(T parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter).ConfigureAwait(true);
        }
        catch
        {
            // 同 AsyncCommand:命令体自己呈现失败;这里只保证异常不逃出 async void 的 Execute。
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>同步命令。</summary>
/// <param name="execute">命令体。</param>
/// <param name="canExecute">可用性判定。</param>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>带参同步命令。</summary>
/// <typeparam name="T">参数类型。</typeparam>
/// <param name="execute">命令体。</param>
/// <param name="canExecute">可用性判定。</param>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => parameter is T typed && (canExecute?.Invoke(typed) ?? true);

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (parameter is T typed && CanExecute(parameter))
        {
            execute(typed);
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
