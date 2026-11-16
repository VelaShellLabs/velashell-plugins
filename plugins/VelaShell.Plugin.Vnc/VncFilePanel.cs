using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using VelaShell.PluginSdk;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// The file-transfer dialog (UltraVNC file transfer over the desktop's own connection): a remote folder listing with
/// up / refresh / new folder / upload in the 36px toolbar, download / rename / delete for the selection, and a 24px status
/// line with progress and Cancel. Files dropped onto the panel upload into the open folder. One operation runs at a time,
/// as the protocol allows. Confirmations and names are asked inline (no dialogs), like the remote-keys confirmations.
/// </summary>
internal sealed class VncFilePanel : Border
{
    private static readonly System.Buffers.SearchValues<char> InvalidNameChars = System.Buffers.SearchValues.Create("\\/:*?\"<>|");
    private static readonly Geometry FolderIcon = Geometry.Parse("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z");
    private static readonly Geometry FileIcon = Geometry.Parse("M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4");
    private static readonly Geometry DriveIcon = Geometry.Parse("M22 12H2 M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z M6 16h.01 M10 16h.01");
    private static readonly Geometry UpIcon = Geometry.Parse("M5 12l7-7 7 7 M12 19V5");
    private static readonly Geometry RefreshIcon = Geometry.Parse("M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5");
    private static readonly Geometry FolderPlusIcon = Geometry.Parse("M12 10v6 M9 13h6 M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z");
    private static readonly Geometry UploadIcon = Geometry.Parse("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M17 8l-5-5-5 5 M12 3v12");
    private static readonly Geometry DownloadIcon = Geometry.Parse("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3");
    private static readonly Geometry PencilIcon = Geometry.Parse("M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5l4 4");
    private static readonly Geometry TrashIcon = Geometry.Parse("M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2 M10 11v6 M14 11v6");
    private static readonly Geometry CloseIcon = Geometry.Parse("M18 6 6 18 M6 6l12 12");
    private readonly IVncFileTransfer _files;
    private readonly IPluginContext _context;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _path = new() { Name = "VncFilesPath", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.PrefixCharacterEllipsis };
    private readonly ListBox _list = new() { Name = "VncFilesList", SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { Name = "VncFilesStatus", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 3, MinHeight = 3, IsVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _up, _refresh, _newFolder, _upload, _close, _download, _rename, _delete;
    private readonly Button _cancel = new() { Name = "VncFilesCancel", IsVisible = false, Padding = new Thickness(8, 0), Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Border _prompt = new() { Name = "VncFilesPrompt", IsVisible = false, Padding = new Thickness(10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly TextBlock _promptText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _promptInput = new() { Name = "VncFilesPromptInput", Height = 26, MinHeight = 26, Padding = new Thickness(6, 3) };
    private readonly Button _promptOk = new() { Name = "VncFilesPromptOk", Padding = new Thickness(12, 3) };
    private readonly Button _promptCancel = new() { Name = "VncFilesPromptCancel", Padding = new Thickness(12, 3) };
    private TaskCompletionSource<string?>? _pending;
    private CancellationTokenSource? _transfer;
    private Loc _loc;
    private IReadOnlyList<VncRemoteEntry> _entries = [];
    private bool _granted, _opened, _disposed;

    internal VncFilePanel(IVncFileTransfer files, IPluginContext context, Loc loc, IVncLocalFiles localFiles)
    {
        _files = files;
        _context = context;
        _loc = loc;
        LocalFiles = localFiles;
        Name = "VncFilePanel";
        ClipToBounds = true;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Token(this, BackgroundProperty, "VelaBgSurface");
        Token(this, BorderBrushProperty, "VelaBorderSecondary");
        Token(this, BoxShadowProperty, "VelaShadowWindow");

        _up = IconButton("VncFilesUp", UpIcon, () => _ = NavigateAsync(Directory is { } d ? ParentOf(d) : null));
        _refresh = IconButton("VncFilesRefresh", RefreshIcon, () => _ = _granted ? NavigateAsync(Directory) : OpenAsync(retry: true));
        _newFolder = IconButton("VncFilesNewFolder", FolderPlusIcon, () => _ = NewFolderAsync());
        _upload = IconButton("VncFilesUpload", UploadIcon, () => _ = PickAndUploadAsync());
        _close = IconButton("VncFilesClose", CloseIcon, () => CloseRequested?.Invoke());
        _download = IconButton("VncFilesDownload", DownloadIcon, () => _ = DownloadSelectedAsync());
        _rename = IconButton("VncFilesRename", PencilIcon, () => _ = RenameSelectedAsync());
        _delete = IconButton("VncFilesDelete", TrashIcon, () => _ = DeleteSelectedAsync());
        _cancel.Click += (_, _) => _transfer?.Cancel();
        Token(_cancel, TemplatedControlBackground, "VelaBgHover");
        Token(_cancel, TemplatedControlForeground, "VelaTextPrimary");
        Token(_cancel, TemplatedControlFontSize, "VelaFontSize11");

        _close.Width = 27;
        _close.Height = 27;
        _close.CornerRadius = new CornerRadius(0);
        var header = new DockPanel { Height = 28, LastChildFill = true };
        DockPanel.SetDock(_close, Dock.Right);
        header.Children.Add(_close);
        var titleIcon = new VncGlyph { Data = FolderIcon, Width = 15, Height = 15, RestToken = "VelaAccent", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(titleIcon, Dock.Left);
        TitleBar = new Border
        {
            Name = "VncFilesTitleBar",
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new DockPanel { Children = { titleIcon, _title } }
        };
        header.Children.Add(TitleBar);
        Token(_title, TextBlock.ForegroundProperty, "VelaTextPrimary");
        Token(_title, TextBlock.FontSizeProperty, "VelaFontSize13");
        var toolbar = new Border
        {
            Height = 36,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _up, _refresh, _newFolder, _upload } }
        };
        Token(toolbar, BorderBrushProperty, "VelaBorderPrimary");

        var pathBar = new DockPanel { Height = 26, LastChildFill = true };
        var pathButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _download, _rename, _delete } };
        DockPanel.SetDock(pathButtons, Dock.Right);
        pathBar.Children.Add(pathButtons);
        pathBar.Children.Add(_path);
        Token(_path, TextBlock.ForegroundProperty, "VelaTextMuted");
        Token(_path, TextBlock.FontFamilyProperty, "VelaUiMonoFont");
        Token(_path, TextBlock.FontSizeProperty, "VelaFontSize11");
        var pathBorder = new Border { Child = pathBar, BorderThickness = new Thickness(0, 1, 0, 1) };
        Token(pathBorder, BorderBrushProperty, "VelaBorderPrimary");

        _promptInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Answer(_promptInput.Text ?? ""); e.Handled = true; }
            else if (e.Key == Key.Escape) { Answer(null); e.Handled = true; }
        };
        _promptOk.Click += (_, _) => Answer(_promptInput.IsVisible ? _promptInput.Text ?? "" : "");
        _promptCancel.Click += (_, _) => Answer(null);
        Token(_promptText, TextBlock.ForegroundProperty, "VelaTextPrimary");
        Token(_promptText, TextBlock.FontSizeProperty, "VelaFontSize12");
        Token(_promptOk, TemplatedControlBackground, "VelaAccent");
        Token(_promptOk, TemplatedControlForeground, "VelaAccentForeground");
        Token(_prompt, BackgroundProperty, "VelaBgSurface");
        Token(_prompt, BorderBrushProperty, "VelaBorderPrimary");
        _prompt.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _promptText, _promptInput,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _promptCancel, _promptOk } }
            }
        };

        // Avalonia builds the template with null data while a container is being reset.
        _list.ItemTemplate = new FuncDataTemplate<object?>((item, _) => item is null ? new Grid() : Row(item));
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(MinHeightProperty, 28d), new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(10, 0)) }
        });
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => _ = OpenSelectedAsync();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = OpenSelectedAsync(); e.Handled = true; }
            else if (e.Key == Key.Back) { _ = NavigateAsync(Directory is { } d ? ParentOf(d) : null); e.Handled = true; }
            else if (e.Key == Key.Delete) { _ = DeleteSelectedAsync(); e.Handled = true; }
        };

        var footer = new Grid { Height = 24, ColumnDefinitions = [with("*,Auto")] };
        footer.Children.Add(_status);
        Grid.SetColumn(_cancel, 1);
        footer.Children.Add(_cancel);
        var footerBorder = new Border { Child = new Panel { Children = { footer, _progress } }, BorderThickness = new Thickness(0, 1, 0, 0) };
        Token(footerBorder, BorderBrushProperty, "VelaBorderPrimary");
        Token(_status, TextBlock.FontSizeProperty, "VelaFontSize11");
        Token(_progress, TemplatedControlForeground, "VelaAccent");

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(pathBorder, Dock.Top);
        DockPanel.SetDock(_prompt, Dock.Top);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(toolbar);
        layout.Children.Add(pathBorder);
        layout.Children.Add(_prompt);
        layout.Children.Add(footerBorder);
        layout.Children.Add(_list);
        Child = layout;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        ApplyLocale(loc);
        UpdateButtons();
    }

    private static readonly AvaloniaProperty TemplatedControlBackground = Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty;
    private static readonly AvaloniaProperty TemplatedControlForeground = Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty;
    private static readonly AvaloniaProperty TemplatedControlFontSize = Avalonia.Controls.Primitives.TemplatedControl.FontSizeProperty;

    internal IVncLocalFiles LocalFiles { get; set; }
    internal Control TitleBar { get; }
    internal Action? CloseRequested { get; set; }
    internal string? Directory { get; private set; }
    internal IReadOnlyList<object> Items => _list.ItemsSource as IReadOnlyList<object> ?? [];
    internal string StatusText => _status.Text ?? "";
    internal bool IsBusy { get; private set; }
    internal bool PromptVisible => _prompt.IsVisible;
    internal Task? Operation { get; private set; }

    private static void Token(AvaloniaObject target, AvaloniaProperty property, string token) =>
        target[!property] = new DynamicResourceExtension(token);

    private static Button IconButton(string name, Geometry icon, Action click)
    {
        var button = new Button
        {
            Name = name,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Content = new VncGlyph { Data = icon, Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        VncChrome.AddIconButtonStyles(button, "VelaTextSecondary", "VelaTextPrimary");
        button.Click += (_, _) => click();
        return button;
    }

    private static Grid Row(object item)
    {
        (Geometry icon, string name, string detail, string? tip) = item switch
        {
            VncRemoteDrive drive => (DriveIcon, drive.Path, "", null),
            VncRemoteEntry { IsDirectory: true } folder => (FolderIcon, folder.Name, "", Modified(folder)),
            VncRemoteEntry file => (FileIcon, file.Name, FormatSize(file.Size), Modified(file)),
            _ => (FileIcon, item.ToString() ?? "", "", null)
        };
        var glyph = new VncGlyph { Data = icon, Width = 14, Height = 14, RestToken = item is VncRemoteEntry { IsDirectory: false } ? "VelaTextMuted" : "VelaAccent", VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
        var size = new TextBlock { Text = detail, VerticalAlignment = VerticalAlignment.Center };
        Token(label, TextBlock.ForegroundProperty, "VelaTextPrimary");
        Token(label, TextBlock.FontSizeProperty, "VelaFontSize12");
        Token(size, TextBlock.ForegroundProperty, "VelaTextMuted");
        Token(size, TextBlock.FontSizeProperty, "VelaFontSize11");
        var row = new Grid { Height = 28, ColumnDefinitions = [with("Auto,*,Auto")], Background = Brushes.Transparent };
        row.Children.Add(glyph);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        if (tip is not null) { ToolTip.SetTip(row, tip); }
        return row;
    }

    private static string? Modified(VncRemoteEntry entry) =>
        entry.Modified is { } time ? time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : null;

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.CurrentCulture) + " GB"
    };

    /// <summary>The parent of a Windows-style folder path ("C:\a\b\" → "C:\a\"); null above a drive root (the drive list).</summary>
    internal static string? ParentOf(string directory)
    {
        string trimmed = directory.TrimEnd('\\');
        int cut = trimmed.LastIndexOf('\\');
        return cut < 0 ? null : trimmed[..(cut + 1)];
    }

    internal static bool IsValidName(string name) =>
        name.Trim().Length > 0 && name.AsSpan().IndexOfAny(InvalidNameChars) < 0 && name is not ("." or "..");

    internal void ApplyLocale(Loc loc)
    {
        _loc = loc;
        _title.Text = loc["FilesTitle"];
        _path.Text = Directory ?? loc["FilesDrives"];
        ToolTip.SetTip(_up, loc["FilesUp"]);
        ToolTip.SetTip(_refresh, loc["FilesRefresh"]);
        ToolTip.SetTip(_newFolder, loc["FilesNewFolder"]);
        ToolTip.SetTip(_upload, loc["FilesUpload"]);
        ToolTip.SetTip(_close, loc["FilesClose"]);
        ToolTip.SetTip(_download, loc["FilesDownload"]);
        ToolTip.SetTip(_rename, loc["FilesRename"]);
        ToolTip.SetTip(_delete, loc["FilesDelete"]);
        _cancel.Content = loc["FilesCancel"];
        _promptCancel.Content = loc["FilesCancel"];
        _promptInput.PlaceholderText = loc["FilesNamePlaceholder"];
    }

    internal void FocusDialog() => _ = _close.Focus();

    // ------------------------------------------------------------ status

    private void ShowStatus(string text, bool error = false)
    {
        _status.Text = text;
        Token(_status, TextBlock.ForegroundProperty, error ? "VelaError" : "VelaTextSecondary");
        ToolTip.SetTip(_status, text);
    }

    private void ShowListing()
    {
        int count = _list.ItemsSource is IReadOnlyList<object> items ? items.Count : 0;
        ShowStatus(Directory is not null && count == 0 ? _loc["FilesEmpty"] : _loc.Format("FilesItems", count));
    }

    private void SetBusy(bool busy, bool transfer = false)
    {
        IsBusy = busy;
        _list.IsEnabled = !busy;
        _cancel.IsVisible = busy && transfer;
        _progress.IsVisible = busy && transfer;
        if (!busy) { _progress.Value = 0; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = _granted && !IsBusy && !_prompt.IsVisible;
        object? selected = _list.SelectedItem;
        _up.IsEnabled = ready && Directory is not null;
        _refresh.IsEnabled = !IsBusy && !_prompt.IsVisible;
        _newFolder.IsEnabled = ready && Directory is not null;
        _upload.IsEnabled = ready && Directory is not null;
        _download.IsEnabled = ready && selected is VncRemoteEntry { IsDirectory: false };
        _rename.IsEnabled = ready && selected is VncRemoteEntry;
        _delete.IsEnabled = ready && selected is VncRemoteEntry;
    }

    private string Describe(Exception ex, string subject) => ex switch
    {
        VncFileTransferException { Error: VncFileTransferError.Unsupported } => _loc["FilesUnsupported"],
        VncFileTransferException { Error: VncFileTransferError.Timeout } => _loc["FilesErrorTimeout"],
        VncFileTransferException { Error: VncFileTransferError.Unavailable } => _loc.Format("FilesErrorUnavailable", subject),
        VncFileTransferException { Error: VncFileTransferError.Failed } => _loc.Format("FilesErrorFailed", subject),
        VncFileTransferException { Error: VncFileTransferError.Disconnected } => _loc["FilesErrorDisconnected"],
        VncFileTransferException { Error: VncFileTransferError.Denied } => _loc["FilesDenied"],
        IOException or UnauthorizedAccessException => _loc.Format("FilesErrorLocal", ex.Message),
        _ => ex.Message
    };

    /// <summary>Runs one operation: buttons off, failures shown in the status line (and logged), cancellation reported.</summary>
    private Task RunAsync(Func<CancellationToken, Task> action, string subject, bool transfer = false)
    {
        if (IsBusy || _disposed) { return Task.CompletedTask; }
        SetBusy(true, transfer);
        _transfer = new CancellationTokenSource();
        Operation = Core(_transfer.Token);
        return Operation;

        async Task Core(CancellationToken ct)
        {
            try { await action(ct); }
            catch (OperationCanceledException) { if (!_disposed) { ShowStatus(_loc["FilesCancelled"]); } }
            catch (Exception ex)
            {
                _context.Log.Warn($"VNC file transfer: {ex.GetType().Name}: {ex.Message}");
                if (!_disposed) { ShowStatus(Describe(ex, subject), error: true); }
            }
            finally
            {
                _transfer?.Dispose();
                _transfer = null;
                if (!_disposed) { SetBusy(false); }
            }
        }
    }

    // ------------------------------------------------------------ navigation

    /// <summary>First showing: ask for access, then list the drives (a single drive — LibVNC's "C:" — opens directly).</summary>
    internal Task OpenAsync(bool retry = false)
    {
        if ((_opened && !retry) || IsBusy) { return Operation ?? Task.CompletedTask; }
        _opened = true;
        ShowStatus(_loc["FilesRequesting"]);
        return RunAsync(async ct =>
        {
            _granted = await _files.RequestAccessAsync(ct);
            if (!_granted) { ShowStatus(_loc["FilesDenied"], error: true); return; }
            await LoadAsync(null, enterSingleDrive: true, ct);
        }, "");
    }

    private Task NavigateAsync(string? directory) => RunAsync(ct => LoadAsync(directory, enterSingleDrive: false, ct), directory ?? "");

    private async Task LoadAsync(string? directory, bool enterSingleDrive, CancellationToken ct)
    {
        ShowStatus(_loc["FilesLoading"]);
        IReadOnlyList<object> items;
        if (directory is null)
        {
            IReadOnlyList<VncRemoteDrive> drives = await _files.ListDrivesAsync(ct);
            if (enterSingleDrive && drives.Count == 1)
            {
                await LoadAsync(drives[0].Path, false, ct);
                return;
            }
            _entries = [];
            items = [.. drives];
        }
        else
        {
            _entries = await _files.ListAsync(directory, ct);
            items = [.. _entries];
        }

        Directory = directory;
        _path.Text = directory ?? _loc["FilesDrives"];
        _list.ItemsSource = items;
        ShowListing();
    }

    private Task OpenSelectedAsync() => _list.SelectedItem switch
    {
        _ when IsBusy || !_granted => Task.CompletedTask,
        VncRemoteDrive drive => NavigateAsync(drive.Path),
        VncRemoteEntry { IsDirectory: true } folder when Directory is { } dir => NavigateAsync(dir + folder.Name + "\\"),
        VncRemoteEntry { IsDirectory: false } => DownloadSelectedAsync(),
        _ => Task.CompletedTask
    };

    // ------------------------------------------------------------ inline prompt

    /// <summary>Shows the inline prompt; resolves to the entered text ("" for a plain confirmation) or null when cancelled.</summary>
    private Task<string?> AskAsync(string text, string okLabel, string? initial)
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<string?>();
        _promptText.Text = text;
        _promptOk.Content = okLabel;
        _promptInput.IsVisible = initial is not null;
        _promptInput.Text = initial ?? "";
        _prompt.IsVisible = true;
        UpdateButtons();
        if (initial is not null)
        {
            _ = _promptInput.Focus();
            int dot = initial.LastIndexOf('.');
            _promptInput.SelectionStart = 0;
            _promptInput.SelectionEnd = dot > 0 ? dot : initial.Length;
        }
        return _pending.Task;
    }

    internal void Answer(string? value)
    {
        if (_pending is not { } pending) { return; }
        if (value is not null && _promptInput.IsVisible && !IsValidName(value))
        {
            ShowStatus(_loc["FilesInvalidName"], error: true);
            return;
        }
        _pending = null;
        _prompt.IsVisible = false;
        UpdateButtons();
        _ = pending.TrySetResult(value?.Trim());
    }

    // ------------------------------------------------------------ operations

    private async Task NewFolderAsync()
    {
        if (Directory is not { } dir || IsBusy) { return; }
        if (await AskAsync(_loc["FilesNewFolder"], _loc["FilesOk"], "") is not { Length: > 0 } name) { return; }
        await RunAsync(async ct =>
        {
            await _files.CreateDirectoryAsync(dir + name, ct);
            await LoadAsync(dir, false, ct);
        }, name);
    }

    private async Task RenameSelectedAsync()
    {
        if (Directory is not { } dir || _list.SelectedItem is not VncRemoteEntry entry || IsBusy) { return; }
        if (await AskAsync(_loc["FilesRename"], _loc["FilesOk"], entry.Name) is not { Length: > 0 } name || name == entry.Name) { return; }
        await RunAsync(async ct =>
        {
            await _files.RenameAsync(dir + entry.Name, dir + name, ct);
            await LoadAsync(dir, false, ct);
        }, entry.Name);
    }

    private async Task DeleteSelectedAsync()
    {
        if (Directory is not { } dir || _list.SelectedItem is not VncRemoteEntry entry || IsBusy) { return; }
        string question = _loc.Format(entry.IsDirectory ? "FilesConfirmDeleteFolder" : "FilesConfirmDelete", entry.Name);
        if (await AskAsync(question, _loc["FilesDelete"], null) is null) { return; }
        await RunAsync(async ct =>
        {
            await _files.DeleteAsync(dir + entry.Name, ct);
            await LoadAsync(dir, false, ct);
        }, entry.Name);
    }

    private async Task DownloadSelectedAsync()
    {
        if (Directory is not { } dir || _list.SelectedItem is not VncRemoteEntry { IsDirectory: false } entry || IsBusy) { return; }
        VncLocalTarget? target;
        try { target = await LocalFiles.PickSaveAsync(_loc["FilesDownload"], entry.Name); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowStatus(_loc.Format("FilesErrorLocal", ex.Message), error: true); return; }
        if (target is null) { return; }

        await RunAsync(async ct =>
        {
            ShowStatus(_loc.Format("FilesDownloading", entry.Name, 0));
            var progress = new Progress<long>(done =>
            {
                int percent = entry.Size > 0 ? (int)Math.Min(100, done * 100 / entry.Size) : 0;
                _progress.Value = percent;
                if (IsBusy) { ShowStatus(_loc.Format("FilesDownloading", entry.Name, percent)); }
            });
            bool complete = false;
            try
            {
                await using (Stream output = await target.OpenWrite())
                {
                    _ = await _files.DownloadAsync(dir + entry.Name, output, progress, ct);
                }
                complete = true;
            }
            finally
            {
                if (!complete)
                {
                    try { await target.Delete(); } catch { /* a partial file the user can remove */ }
                }
            }
            ShowStatus(_loc.Format("FilesDownloaded", entry.Name));
        }, entry.Name, transfer: true);
    }

    private async Task PickAndUploadAsync()
    {
        if (Directory is null || IsBusy) { return; }
        IReadOnlyList<VncLocalFile> files;
        try { files = await LocalFiles.PickOpenAsync(_loc["FilesUpload"]); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowStatus(_loc.Format("FilesErrorLocal", ex.Message), error: true); return; }
        await UploadAsync(files, 0);
    }

    /// <summary>
    /// Uploads into the open folder, one file after another. Existing names are confirmed once for the batch and deleted
    /// first: an UltraVNC server would otherwise resume onto the old file.
    /// </summary>
    internal async Task UploadAsync(IReadOnlyList<VncLocalFile> files, int skippedFolders)
    {
        if (IsBusy || !_granted) { return; }
        if (Directory is not { } dir) { ShowStatus(_loc["FilesPickFolder"], error: true); return; }
        if (files.Count == 0)
        {
            if (skippedFolders > 0) { ShowStatus(_loc.Format("FilesFoldersSkipped", skippedFolders), error: true); }
            return;
        }

        var existing = new HashSet<string>(_entries.Where(static e => !e.IsDirectory).Select(static e => e.Name), StringComparer.OrdinalIgnoreCase);
        // Names are compared case-insensitively for the replace question (a Windows server treats "Readme.md" and
        // "README.md" as one file and would resume onto it), but exactly for folders and for what must be deleted:
        // on a Unix server (LibVNC) those are two different files.
        var exact = new HashSet<string>(_entries.Where(static e => !e.IsDirectory).Select(static e => e.Name), StringComparer.Ordinal);
        var folders = new HashSet<string>(_entries.Where(static e => e.IsDirectory).Select(static e => e.Name), StringComparer.Ordinal);
        List<VncLocalFile> uploads = [.. files.Where(f => !folders.Contains(f.Name))];
        int conflicts = uploads.Count(f => existing.Contains(f.Name));
        if (conflicts > 0 && await AskAsync(_loc.Format("FilesConfirmOverwrite", conflicts), _loc["FilesReplace"], null) is null) { return; }

        await RunAsync(async ct =>
        {
            for (int i = 0; i < uploads.Count; i++)
            {
                VncLocalFile file = uploads[i];
                int index = i + 1;
                await using Stream input = await file.OpenRead();
                long length = input.CanSeek ? input.Length : file.Length;
                ShowStatus(_loc.Format("FilesUploading", file.Name, index, uploads.Count, 0));
                var progress = new Progress<long>(done =>
                {
                    int percent = length > 0 ? (int)Math.Min(100, done * 100 / length) : 100;
                    _progress.Value = percent;
                    if (IsBusy) { ShowStatus(_loc.Format("FilesUploading", file.Name, index, uploads.Count, percent)); }
                });
                if (exact.Contains(file.Name)) { await _files.DeleteAsync(dir + file.Name, ct); }
                else if (existing.Contains(file.Name))
                {
                    // Only the case differs: on Windows this deletes the old file, on a Unix server there is nothing to delete.
                    try { await _files.DeleteAsync(dir + file.Name, ct); }
                    catch (VncFileTransferException failure) when (failure.Error == VncFileTransferError.Failed) { }
                }
                await _files.UploadAsync(input, length, file.Modified, dir + file.Name, progress, ct);
            }
            await LoadAsync(dir, false, ct);
            string done = _loc.Format("FilesUploaded", uploads.Count);
            int skipped = skippedFolders + files.Count - uploads.Count;
            ShowStatus(skipped > 0 ? done + " " + _loc.Format("FilesFoldersSkipped", skipped) : done);
        }, uploads.Count == 1 ? uploads[0].Name : dir, transfer: true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _granted && !IsBusy && Directory is not null && e.DataTransfer.TryGetFiles()?.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items) { return; }
        try
        {
            (List<VncLocalFile> files, int folders) = await StorageLocalFiles.FromDropAsync(items);
            await UploadAsync(files, folders);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(_loc.Format("FilesErrorLocal", ex.Message), error: true);
        }
    }

    /// <summary>The view is going away: a running transfer is cancelled (and aborted on the server).</summary>
    internal void Close()
    {
        _disposed = true;
        _pending?.TrySetResult(null);
        try { _transfer?.Cancel(); } catch (ObjectDisposedException) { }
    }
}
