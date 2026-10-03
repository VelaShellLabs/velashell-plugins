using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Mongo;

/// <summary>
/// MongoDB 会话标签页上的图标:lucide 的 <c>leaf</c>(设计稿 00 封面与标签页上就是它)。
/// <para>
/// 用 lucide 的叶子而不是 MongoDB 官方商标:后者是注册商标,插件随包分发一份商标路径
/// 需要另行取得许可;叶子与设计稿一致,在一排标签里也足够认得出来。描边、视框 24。
/// </para>
/// </summary>
internal static class MongoIcon
{
    /// <summary>lucide <c>leaf</c> 的路径。</summary>
    public const string PathData =
        "M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10Z M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12";

    /// <summary>交给宿主的那份图标。</summary>
    public static readonly PluginIcon Tab = PluginIcon.Stroked(PathData);
}
