using System.Globalization;

namespace VelaShell.Plugin.Mongo;

/// <summary>
/// 插件自带的文案表。插件的国际化由自己负责(SDK 只给 <c>context.Host.Locale</c> 与
/// <c>LocaleChanged</c> 事件),因此这些词条随插件走 —— 它们本就是 MongoDB 的领域词汇。
/// <para>
/// 只带简体中文与英文两套(与 Redis / S3 插件同口径):缺哪种语言就回落英文,而不是显示一个键名。
/// </para>
/// <para>
/// <b>形状与 Redis 插件不同:一行同时写两种语言</b>(<c>(键, 英文, 中文)</c>)。
/// 词条按功能区拆进各自的 <c>Loc.*.cs</c> 分部文件 —— 二十多块界面各管一张表,
/// 改查询编辑器的人不必和改 GridFS 的人抢同一个文件;而"两种语言的键集必须一致"
/// 这件事由形状本身保证,不再靠单测事后去抓。
/// </para>
/// </summary>
/// <param name="locale">宿主当前语言(如 <c>zh-Hans</c>、<c>en</c>)。</param>
public sealed partial class Loc(string locale)
{
    /// <summary>当前是不是中文表(少数地方要按语言挑数字或单位的写法)。</summary>
    public bool IsChinese { get; } = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取一条文案;未收录的键原样返回(方便一眼看出漏了哪条)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string this[string key] =>
        (IsChinese ? Tables.Chinese : Tables.English).TryGetValue(key, out string? value) ? value : key;

    /// <summary>取一条文案(索引器的具名形式,便于在表达式里连用)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string Get(string key) => this[key];

    /// <summary>取一条带占位符的文案并格式化。</summary>
    /// <param name="key">文案键。</param>
    /// <param name="args">占位参数。</param>
    /// <returns>格式化后的文案。</returns>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>全部文案键。**供单测用**(查重键、查空文案)。</summary>
    public static IReadOnlyCollection<string> AllKeys => Tables.English.Keys;

    /// <summary>
    /// 合成好的两本字典。放在嵌套类里是**必须的**:各功能区的表是分散在十几个分部文件里的静态字段,
    /// 而分部类的静态字段初始化顺序是未指定的 —— 直接写成 Loc 自己的静态字段,
    /// 就可能在某张表还是 null 时去合并它。嵌套类在第一次查词时才初始化,那时 Loc 的全部静态字段早已就位。
    /// </summary>
    private static class Tables
    {
        /// <summary>全部功能区的表。新加一块界面就在这里挂一张 —— 漏挂的后果是那块界面满屏键名,一眼就看得出来。</summary>
        private static readonly (string Key, string En, string Zh)[][] All =
        [
            CoreTexts,
            WorkspaceTexts,
            CollectionTexts,
            QueryTexts,
            PipelineTexts,
            DocumentEditorTexts,
            GridFsTexts,
            DesignTexts,
            ObjectsTexts,
            MonitorTexts,
            ProfilerTexts,
            UsersTexts,
            NewCollectionTexts,
            TransferTexts,
            StatesTexts,
            ConnectionTexts
        ];

        public static readonly Dictionary<string, string> English = Build(static row => row.En);
        public static readonly Dictionary<string, string> Chinese = Build(static row => row.Zh);

        /// <summary>
        /// 把各张表合成一本字典。**重复键直接抛** —— 那会让整个插件不可用,所以由单测在构建期先撞上,
        /// 并且这里给出一句说得清是哪个键的话。
        /// </summary>
        private static Dictionary<string, string> Build(Func<(string Key, string En, string Zh), string> pick)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string Key, string En, string Zh)[] table in All)
            {
                foreach ((string Key, string En, string Zh) row in table)
                {
                    if (!map.TryAdd(row.Key, pick(row)))
                    {
                        throw new InvalidOperationException($"Duplicate localization key: {row.Key}");
                    }
                }
            }
            return map;
        }
    }
}
