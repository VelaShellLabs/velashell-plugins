using VelaShell.PluginSdk;

namespace VelaShell.Plugin.S3;

/// <summary>
/// S3 的那个图标:会话标签页、桶管理器与对象检视器两扇工具窗,三处共用同一个实例。
/// </summary>
/// <remarks>
/// <para>
/// 三处共用是刻意的 —— 标签条与标题栏上认的都是「这一扇属于哪个插件」,分开画只会让
/// 同一个插件的三个界面看着像三个来路。
/// </para>
/// <para>
/// <b>为什么是云而不是存储那一类字形。</b>宿主给 SFTP / FTP 的标签画的正是 <c>hard-drive</c>,
/// 而 S3 的标签与它们并排躺在同一条标签条上(三者都是双栏文件浏览器)。再拿硬盘、机箱
/// 一类的字形画 S3,一排标签就只能靠读文字来分了。云答的是「文件在网那头的对象存储里」,
/// 这正是它与前两者的区别 —— 至于自建的 MinIO / Ceph 算不算“云”,对象存储这套模型本就
/// 是云存储带起来的,用户的心智模型在这一边。
/// </para>
/// <para>描边、视框 24:lucide 那套字形的通用规格,报成实心会把它填成一团色块。</para>
/// </remarks>
internal static class S3Icon
{
    /// <summary>交给宿主的那份图标(lucide <c>cloud</c>)。</summary>
    public static readonly PluginIcon Cloud = PluginIcon.Stroked(PathData);

    /// <summary>lucide <c>cloud</c> 的路径数据。</summary>
    public const string PathData = "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z";
}
