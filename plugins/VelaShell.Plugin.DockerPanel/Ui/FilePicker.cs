using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 本地文件选择。
/// <para>
/// SDK 没有这个能力,因为它对隔离进程的插件没法给 —— 但 Docker 面板是
/// <c>hostMode: inProcess</c>(隧道要一条活的字节流,跨不了进程),所以它就在宿主的
/// Avalonia 里,能直接问 <see cref="TopLevel" /> 要 <see cref="IStorageProvider" />。
/// </para>
/// <para>
/// 挂在这里而不是让视图模型自己去爬可视树:视图模型不该知道自己被画在哪儿,
/// 而"当前顶层窗口"是只有视图答得上来的问题。
/// </para>
/// </summary>
public static class FilePicker
{
    private static Func<TopLevel?>? _accessor;

    /// <summary>视图挂上来的时候把自己的顶层窗口交出来。</summary>
    public static void Attach(Func<TopLevel?> accessor) => _accessor = accessor;

    /// <summary>能不能弹选择器(视图还没挂上、或者宿主没给顶层窗口时不能)。</summary>
    public static bool IsAvailable => _accessor?.Invoke()?.StorageProvider is { CanOpen: true };

    /// <summary>选一个本地文件读。取消返回 <see langword="null" />。</summary>
    public static async Task<IStorageFile?> PickOpenAsync(string title)
    {
        if (_accessor?.Invoke()?.StorageProvider is not { } storage)
        {
            return null;
        }
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new()
        {
            Title = title,
            AllowMultiple = false
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0] : null;
    }

    /// <summary>选一个本地路径写。取消返回 <see langword="null" />。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="suggestedName">建议文件名。</param>
    /// <param name="extension">默认扩展名,不带点。</param>
    public static async Task<IStorageFile?> PickSaveAsync(string title, string suggestedName, string? extension = null)
    {
        return _accessor?.Invoke()?.StorageProvider is not { } storage
            ? null
            : await storage.SaveFilePickerAsync(new()
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true
            }).ConfigureAwait(true);
    }
}
