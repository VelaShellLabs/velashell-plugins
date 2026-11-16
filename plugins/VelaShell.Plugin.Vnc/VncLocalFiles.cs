using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VelaShell.Plugin.Vnc;

/// <summary>A local file to upload: picked, or dropped onto the file panel.</summary>
internal sealed record VncLocalFile(string Name, long Length, DateTimeOffset Modified, Func<Task<Stream>> OpenRead);

/// <summary>Where a download goes: the file the save picker returned. <see cref="Delete"/> removes a partial download.</summary>
internal sealed record VncLocalTarget(string Name, Func<Task<Stream>> OpenWrite, Func<Task> Delete);

/// <summary>The local side of file transfer: pickers, and turning dropped items into files. A seam for headless tests.</summary>
internal interface IVncLocalFiles
{
    Task<IReadOnlyList<VncLocalFile>> PickOpenAsync(string title);
    Task<VncLocalTarget?> PickSaveAsync(string title, string suggestedName);
}

/// <summary>
/// The host window's <see cref="IStorageProvider"/>. The plugin runs in process (<c>hostMode: inProcess</c>), so it can
/// use Avalonia's pickers directly, as the Docker panel does; the files touched are only the ones the user picks.
/// </summary>
internal sealed class StorageLocalFiles(Func<TopLevel?> topLevel) : IVncLocalFiles
{
    public async Task<IReadOnlyList<VncLocalFile>> PickOpenAsync(string title)
    {
        if (topLevel()?.StorageProvider is not { CanOpen: true } storage) { return []; }
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new() { Title = title, AllowMultiple = true });
        var files = new List<VncLocalFile>(picked.Count);
        foreach (IStorageFile file in picked) { files.Add(await FromStorageFileAsync(file)); }
        return files;
    }

    public async Task<VncLocalTarget?> PickSaveAsync(string title, string suggestedName)
    {
        if (topLevel()?.StorageProvider is not { CanSave: true } storage) { return null; }
        IStorageFile? file = await storage.SaveFilePickerAsync(new() { Title = title, SuggestedFileName = suggestedName, ShowOverwritePrompt = true });
        return file is null ? null : new VncLocalTarget(file.Name, file.OpenWriteAsync, file.DeleteAsync);
    }

    /// <summary>Files from a drop; folders are counted, not uploaded (UltraVNC file transfer moves single files).</summary>
    internal static async Task<(List<VncLocalFile> Files, int Folders)> FromDropAsync(IEnumerable<IStorageItem> items)
    {
        var files = new List<VncLocalFile>();
        int folders = 0;
        foreach (IStorageItem item in items)
        {
            if (item is IStorageFile file) { files.Add(await FromStorageFileAsync(file)); }
            else { folders++; }
        }
        return (files, folders);
    }

    private static async Task<VncLocalFile> FromStorageFileAsync(IStorageFile file)
    {
        StorageItemProperties properties = await file.GetBasicPropertiesAsync();
        return new VncLocalFile(file.Name, (long)(properties.Size ?? 0), properties.DateModified ?? DateTimeOffset.Now, file.OpenReadAsync);
    }
}
