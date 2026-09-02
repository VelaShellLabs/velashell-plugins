using System.Collections.ObjectModel;
using System.Text;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 解码链上的一个选项(分段控件里的一格)。
/// <para>
/// <b>未内置的解码器照样出现在这一行</b>,只是点不动并说明原因 —— 灰一个按钮不告诉原因,
/// 用户会以为是自己没配对;而把它整个藏起来,用户会以为这个客户端压根不认识 Zstd。
/// </para>
/// </summary>
public sealed class RedisCodecOption : ObservableObject
{
    /// <summary>短标签(格式名,不翻译)。</summary>
    public required string Label { get; init; }

    /// <summary>本插件是否内置这个解码器。</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>当前是否选中。</summary>
    public bool IsOn
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>不可用时的悬停说明。</summary>
    public string Tip
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>解压环节的取值;这一格属于反序列化时为 null。</summary>
    internal RedisCompression? Compression { get; init; }

    /// <summary>反序列化环节的取值;这一格属于解压时为 null。</summary>
    internal RedisSerialization? Serialization { get; init; }
}

/// <summary>
/// 值的解码链:<c>原始字节 → 解压 → 反序列化 → 视图</c>。
/// <para>
/// 这一段界面回答的是"我看到的这段东西,是怎么从服务端那串字节变过来的",
/// 并且把结论落到一个可操作的判断上:<b>这条链可不可逆</b> —— 可逆才允许编辑并写回。
/// </para>
/// <para>
/// 打开一个键时会**按魔数试着自动选**,但只有"真的解开了"才作数:一段以 <c>1f 8b</c>
/// 开头却解不开的字节不会被标成 GZip,而是老老实实留在转义形态并说明识别到了什么。
/// 认得出 ≠ 解得开,这两件事分开说。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>解压之后、反序列化之前的字节。视图与"能不能当文本看"都基于它。</summary>
    private byte[] _plainBytes = [];

    /// <summary>反序列化的转储文本;未启用反序列化时为空串。</summary>
    private string _dumpText = string.Empty;

    /// <summary>解压环节。</summary>
    public ObservableCollection<RedisCodecOption> CompressionOptions { get; } = [];

    /// <summary>反序列化环节。</summary>
    public ObservableCollection<RedisCodecOption> SerializationOptions { get; } = [];

    /// <summary>当前解压环节。</summary>
    public RedisCompression Compression
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            SyncCodecFlags();
        }
    } = RedisCompression.None;

    /// <summary>当前反序列化环节。</summary>
    public RedisSerialization Serialization
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            SyncCodecFlags();
        }
    } = RedisSerialization.None;

    /// <summary>切换解压环节。</summary>
    public AsyncCommand<RedisCodecOption?> UseCompressionCommand { get; private set; } = null!;

    /// <summary>切换反序列化环节。</summary>
    public AsyncCommand<RedisCodecOption?> UseSerializationCommand { get; private set; } = null!;

    /// <summary>切到 JSON 视图(缩进排版;保存时把编辑框里的文本原样写回)。</summary>
    public AsyncCommand UseJsonFormatCommand { get; private set; } = null!;

    /// <summary>
    /// 把编辑框里这段文本重新缩进排版。
    /// <para>
    /// 作用在**草稿**上而不是服务端现值:用户改了一半、缩进乱了想理一理,
    /// 这一下不该把他的改动丢掉。不是合法 JSON 就原样不动,并把解析错误说出来 ——
    /// 不猜、也不"尽力格式化"出一段谁也认不出的东西。
    /// </para>
    /// </summary>
    public AsyncCommand FormatValueCommand { get; private set; } = null!;

    /// <summary>
    /// 把当前这段值写到文件。
    /// <para>
    /// 写的是**服务端的原始字节**(解压前),不是编辑框里那段渲染 —— "下载"这个词在
    /// 用户脑子里是"把这个键里的东西原样拿出来",而不是"把我现在看到的排版存一份"。
    /// 路径直接报到状态条上,不静默写到某个猜出来的地方。
    /// </para>
    /// </summary>
    public AsyncCommand DownloadValueCommand { get; private set; } = null!;

    private Task FormatValueAsync()
    {
        if (!RedisValueCodec.TryPrettyJson(StringDraft, out string pretty, out string? error))
        {
            StatusMessage = Loc.Format("Redis_JsonInvalid", error ?? string.Empty);
            return Task.CompletedTask;
        }
        StringDraft = pretty;
        return Task.CompletedTask;
    }

    private async Task DownloadValueAsync()
    {
        if (Selected?.Key is not { } key || _valueBytes.Length == 0)
        {
            return;
        }
        // 文件名从键名来,非法字符换成下划线:键名里的冒号在 Windows 上是不能进路径的。
        string safe = new([.. key.Display.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')]);
        string path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "redis-export",
            safe.Length > 120 ? safe[..120] : safe);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllBytesAsync(path, _valueBytes).ConfigureAwait(true);
            StatusMessage = Loc.Format("Redis_ValueSaved", key.Display,
                _valueBytes.Length.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " B", path);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Saving value of '{key.Display}' failed.", ex);
        }
    }

    /// <summary>当前是 JSON 视图。</summary>
    public bool IsJsonFormat => ValueFormat == RedisValueFormat.Json;

    /// <summary>
    /// 整条链是否可逆 —— 这是"能不能编辑"的唯一依据。
    /// <para>解压两个方向都做得到(BCL 的三种);反序列化只做得到读,所以一旦启用就转只读。
    /// 十六进制是排版不是表示,同样只读。</para>
    /// </summary>
    public bool IsChainReversible =>
        RedisValueCodec.IsReversible(Compression)
        && Serialization == RedisSerialization.None
        && ValueFormat != RedisValueFormat.Hex;

    /// <summary>链状态短标签(值工具条右侧那一格)。</summary>
    public string ChainStateLabel => IsChainReversible ? Loc["Redis_ChainReversible"] : Loc["Redis_ChainReadOnly"];

    /// <summary>链的可读描述(<c>GZip → UTF-8 → JSON</c>)。</summary>
    public string ChainDescription
    {
        get
        {
            var parts = new List<string>(3);
            if (Compression != RedisCompression.None)
            {
                parts.Add(RedisValueCodec.Label(Compression));
            }
            if (Serialization != RedisSerialization.None)
            {
                parts.Add(RedisValueCodec.Label(Serialization));
            }
            parts.Add(ValueFormat switch
            {
                RedisValueFormat.Json => "JSON",
                RedisValueFormat.Hex => "Hex",
                RedisValueFormat.Escaped => "\\xNN",
                _ => "UTF-8"
            });
            return string.Join(" → ", parts);
        }
    }

    /// <summary>
    /// 解码链的说明行。按重要性给一句:解码失败 &gt; 未内置 &gt; 不可逆的原因 &gt; 可逆的确认。
    /// </summary>
    public string ChainNotice
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasChainNotice));
            // 老名字仍是界面与测试的入口:两边同源,免得说明行出现两套互相矛盾的话。
            RaisePropertyChanged(nameof(ValueFormatNotice));
            RaisePropertyChanged(nameof(HasValueFormatNotice));
        }
    } = string.Empty;

    /// <summary>有说明要显示。</summary>
    public bool HasChainNotice => ChainNotice.Length > 0;

    private void InitializeDecode()
    {
        foreach (RedisCompression option in RedisValueCodec.Compressions)
        {
            CompressionOptions.Add(new()
            {
                Label = option == RedisCompression.None ? Loc["Redis_CodecNone"] : RedisValueCodec.Label(option),
                IsAvailable = RedisValueCodec.IsAvailable(option),
                IsOn = option == RedisCompression.None,
                Tip = RedisValueCodec.IsAvailable(option)
                    ? string.Empty
                    : Loc.Format("Redis_CodecNotBundled", RedisValueCodec.Label(option)),
                Compression = option
            });
        }
        foreach (RedisSerialization option in RedisValueCodec.Serializations)
        {
            SerializationOptions.Add(new()
            {
                Label = option == RedisSerialization.None ? Loc["Redis_CodecNone"] : RedisValueCodec.Label(option),
                IsAvailable = RedisValueCodec.IsAvailable(option),
                IsOn = option == RedisSerialization.None,
                Tip = RedisValueCodec.IsAvailable(option)
                    ? string.Empty
                    : Loc.Format("Redis_CodecNotBundled", RedisValueCodec.Label(option)),
                Serialization = option
            });
        }
        UseCompressionCommand = new(option => ApplyCompressionAsync(option?.Compression));
        UseSerializationCommand = new(option => ApplySerializationAsync(option?.Serialization));
        UseJsonFormatCommand = new(() => SwitchValueFormatAsync(RedisValueFormat.Json));
        FormatValueCommand = new(FormatValueAsync, () => IsStringSelected && CanEditString);
        DownloadValueCommand = new(DownloadValueAsync, () => IsStringSelected);
    }

    private Task ApplyCompressionAsync(RedisCompression? option)
    {
        if (option is not { } compression || compression == Compression)
        {
            return Task.CompletedTask;
        }
        if (!RedisValueCodec.IsAvailable(compression))
        {
            // 点不动的那一格再点一次(键盘/脚本):把原因说出来,而不是无声无息。
            StatusMessage = Loc.Format("Redis_CodecNotBundled", RedisValueCodec.Label(compression));
            return Task.CompletedTask;
        }
        Compression = compression;
        RefreshDecodedValue(resetDraft: true);
        return Task.CompletedTask;
    }

    private Task ApplySerializationAsync(RedisSerialization? option)
    {
        if (option is not { } serialization || serialization == Serialization)
        {
            return Task.CompletedTask;
        }
        if (!RedisValueCodec.IsAvailable(serialization))
        {
            StatusMessage = Loc.Format("Redis_CodecNotBundled", RedisValueCodec.Label(serialization));
            return Task.CompletedTask;
        }
        Serialization = serialization;
        RefreshDecodedValue(resetDraft: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 打开一个新键时按魔数试选一次。
    /// <para>
    /// <b>只有真的解开了才作数。</b>一段以 <c>1f 8b</c> 开头却解不开的字节(截断了、
    /// 或者压根只是巧合)如果被标成 GZip,用户看到的就是一句"解码失败"和一个空编辑框;
    /// 而它其实是一段可以照常按转义编辑的普通字节。
    /// </para>
    /// </summary>
    private void AutoSelectCodec(byte[] raw)
    {
        Compression = RedisCompression.None;
        Serialization = RedisSerialization.None;
        _detected = string.Empty;

        RedisCompression compression = RedisValueCodec.DetectCompression(raw);
        if (compression != RedisCompression.None)
        {
            _detected = RedisValueCodec.Label(compression);
        }
        byte[] plain = raw;
        if (compression != RedisCompression.None
            && RedisValueCodec.IsAvailable(compression)
            && RedisValueCodec.TryDecompress(raw, compression, out byte[] decompressed, out _))
        {
            Compression = compression;
            plain = decompressed;
        }

        RedisSerialization serialization = RedisValueCodec.DetectSerialization(plain);
        if (serialization == RedisSerialization.None)
        {
            return;
        }
        _detected = _detected.Length > 0
            ? $"{_detected} + {RedisValueCodec.Label(serialization)}"
            : RedisValueCodec.Label(serialization);
        if (RedisValueCodec.IsAvailable(serialization)
            && RedisValueCodec.TryDeserialize(plain, serialization, out _, out _))
        {
            Serialization = serialization;
        }
    }

    /// <summary>识别到但未必解开了的格式名(说明行里那句"识别到 X")。</summary>
    private string _detected = string.Empty;

    /// <summary>
    /// 按当前链把 <see cref="_valueBytes" /> 重新解一遍,刷新显示文本、可编辑性与说明行。
    /// </summary>
    /// <param name="resetDraft">是否把编辑框也重置成新解出来的文本(切链时要,切视图时不要)。</param>
    private void RefreshDecodedValue(bool resetDraft)
    {
        var problems = new List<string>(2);
        _plainBytes = _valueBytes;
        if (Compression != RedisCompression.None)
        {
            if (RedisValueCodec.TryDecompress(_valueBytes, Compression, out byte[] plain, out string? error))
            {
                _plainBytes = plain;
            }
            else
            {
                _plainBytes = [];
                problems.Add(Loc.Format("Redis_CodecFailed", RedisValueCodec.Label(Compression), error ?? string.Empty));
            }
        }

        _dumpText = string.Empty;
        if (Serialization != RedisSerialization.None)
        {
            if (RedisValueCodec.TryDeserialize(_plainBytes, Serialization, out string dump, out string? error))
            {
                _dumpText = dump;
            }
            else
            {
                problems.Add(Loc.Format("Redis_CodecFailed", RedisValueCodec.Label(Serialization), error ?? string.Empty));
            }
        }

        CanUseTextFormat = RedisValueText.IsTextSafe(_plainBytes);
        if (ValueFormat == RedisValueFormat.Text && !CanUseTextFormat)
        {
            ValueFormat = RedisValueFormat.Escaped;
        }
        // JSON 视图对不是合法 JSON 的内容退化成原样文本,并把解析错误挂到说明行上。
        if (ValueFormat == RedisValueFormat.Json
            && !RedisValueCodec.TryPrettyJson(Encoding.UTF8.GetString(_plainBytes), out _, out string? jsonError)
            && jsonError is not null)
        {
            problems.Add(Loc.Format("Redis_JsonInvalid", jsonError));
        }

        StringValue = _dumpText.Length > 0 ? _dumpText : RedisValueText.Render(_plainBytes, ValueFormat);
        if (resetDraft)
        {
            StringDraft = StringValue;
        }
        ChainNotice = BuildChainNotice(problems);
        RaiseChainState();
    }

    private string BuildChainNotice(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            return string.Join("  ·  ", problems);
        }
        if (!CanUseTextFormat && ValueFormat == RedisValueFormat.Escaped)
        {
            // 老口径保留:这段字节为什么不是「文本」形态,一句话说清。
            return _detected.Length > 0
                ? $"{Loc.Format("Redis_CodecDetected", _detected)}  ·  {Loc["Redis_BinaryValue"]}"
                : Loc["Redis_BinaryValue"];
        }
        if (ValueFormat == RedisValueFormat.Hex)
        {
            return Loc["Redis_HexReadOnly"];
        }
        if (Serialization != RedisSerialization.None)
        {
            return Loc.Format("Redis_ChainBlocked", RedisValueCodec.Label(Serialization), Loc["Redis_ChainReadOnly"]);
        }
        return Compression != RedisCompression.None
            ? Loc.Format("Redis_ChainExplain", ChainDescription)
            : string.Empty;
    }

    /// <summary>链一变,分段控件的选中态与所有派生标签一起刷新。</summary>
    private void SyncCodecFlags()
    {
        foreach (RedisCodecOption option in CompressionOptions)
        {
            option.IsOn = option.Compression == Compression;
        }
        foreach (RedisCodecOption option in SerializationOptions)
        {
            option.IsOn = option.Serialization == Serialization;
        }
        RaiseChainState();
    }

    private void RaiseChainState()
    {
        RaisePropertyChanged(nameof(IsChainReversible));
        RaisePropertyChanged(nameof(ChainStateLabel));
        RaisePropertyChanged(nameof(ChainDescription));
        RaisePropertyChanged(nameof(CanEditString));
        SaveStringCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 保存路径:编辑框里的文本 → 按视图解回字节 → 按同一条链压回去。
    /// <para>压不回去就**不写** —— 写一段没压过的裸字节进去,下一个读它的进程会在很远的地方失败。</para>
    /// </summary>
    /// <param name="bytes">要写进服务端的字节。</param>
    /// <returns>可以写。</returns>
    private bool TryEncodeForWrite(out byte[] bytes)
    {
        bytes = [];
        if (!TryEncodeDraft(out byte[] plain))
        {
            return false;
        }
        if (!RedisValueCodec.TryCompress(plain, Compression, out byte[] raw, out string? error))
        {
            StatusMessage = Loc.Format("Redis_CodecFailed", RedisValueCodec.Label(Compression), error ?? string.Empty);
            return false;
        }
        bytes = raw;
        return true;
    }
}
