using System.Text;
using System.Text.Json;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>一次 exec 的捕获结果。</summary>
/// <param name="StandardOutput">标准输出。</param>
/// <param name="StandardError">标准错误(**单独一条流**,不并进标准输出)。</param>
/// <param name="ExitCode">退出码。</param>
public readonly record struct ExecCapture(string StandardOutput, string StandardError, int ExitCode)
{
    /// <summary>退出码为 0。</summary>
    public bool IsSuccess => ExitCode == 0;

    /// <summary>给人看的一行失败原因。</summary>
    public string FailureText
    {
        get
        {
            foreach (var candidate in new[] { StandardError, StandardOutput })
            {
                foreach (var line in candidate.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        return trimmed;
                    }
                }
            }
            return $"exit {ExitCode}";
        }
    }
}

/// <summary>一条已经接上的交互式 exec 会话。</summary>
public sealed class DockerExecSession(string execId, Stream stream, DockerClient client, bool tty) : IAsyncDisposable
{
    /// <summary>exec 实例 id(调整窗口大小、取退出码用)。</summary>
    public string ExecId { get; } = execId;

    /// <summary>容器是否分配了 TTY。</summary>
    public bool Tty { get; } = tty;

    /// <summary>把键盘输入写进容器的 stdin。</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>持续读出容器的输出,直到会话结束或取消。</summary>
    public Task ReadAsync(Action<DockerLogLine> onLine, CancellationToken cancellationToken)
    {
        var decoder = new DockerFrameDecoder(Tty, timestamps: false);
        return decoder.ReadAsync(stream, onLine, cancellationToken);
    }

    /// <summary>把原始字节交给调用方(终端控件要的是字节,不是行)。</summary>
    public ValueTask<int> ReadRawAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        stream.ReadAsync(buffer, cancellationToken);

    /// <summary>
    /// 底层的双工流,交给终端视图去泵。
    /// <para>
    /// <b>只在 <see cref="Tty" /> 为真时能这么用。</b> 分配了 TTY 的 exec,daemon 两个方向
    /// 走的都是裸字节;没有 TTY 时输出是 8 字节头的多路复用帧,直接喂给终端会把
    /// <c>[type][0][0][0][len]</c> 这五个字节当成正文画出来。后一种情形请走
    /// <see cref="ReadAsync" />,它有解帧器。
    /// </para>
    /// </summary>
    public Stream Stream => stream;

    /// <summary>调整伪终端的行列。</summary>
    public Task ResizeAsync(int rows, int columns, CancellationToken cancellationToken = default) =>
        client.ResizeExecAsync(ExecId, rows, columns, cancellationToken);

    /// <summary>取退出码(会话结束后才有意义)。</summary>
    public Task<ExecInspectResponse> InspectAsync(CancellationToken cancellationToken = default) =>
        client.InspectExecAsync(ExecId, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

public sealed partial class DockerClient
{
    /// <summary>
    /// 在容器里开一条**交互式** exec 会话。返回的会话握着一条独立的隧道,
    /// 调用方负责释放它 —— 释放即结束远端进程。
    /// </summary>
    /// <param name="containerId">容器 id 或名字。</param>
    /// <param name="command">要执行的命令(argv 形态,不经 shell 解析)。</param>
    /// <param name="tty">是否分配伪终端。</param>
    /// <param name="user">以哪个用户执行;留空用镜像默认。</param>
    /// <param name="workingDir">工作目录;留空用镜像默认。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<DockerExecSession> StartExecAsync(string containerId, string[] command, bool tty,
        string? user = null, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        var created = await CreateExecAsync(containerId, command, tty, attachStdin: true, user, workingDir,
            cancellationToken).ConfigureAwait(false);
        // 劫持端点要独占一条流:HttpClient 的连接池不能用,它没法在同一条连接上
        // 边写 stdin 边读 stdout。
        var stream = await Transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = JsonSerializer.Serialize(new { Detach = false, Tty = tty }, DockerJson.Options);
            (var status, var reason, var hijacked) = await DockerRawHttp
                .PostAsync(stream, $"/exec/{Uri.EscapeDataString(created.Id)}/start", body, upgrade: true, cancellationToken)
                .ConfigureAwait(false);
            return status is not (200 or 101) ? throw new DockerApiException((System.Net.HttpStatusCode)status, $"启动 exec 失败:HTTP {status} {reason}") : new(created.Id, hijacked, this, tty);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 在容器里跑一条命令并把输出**完整收回来**。
    /// <para>
    /// 这是文件浏览(<c>ls</c>)、探测(<c>which</c>)这类"要结果不要交互"的基础件。
    /// 刻意用 <c>Tty=false</c>:只有这样 stdout 与 stderr 才是分开的两条流,
    /// 而"命令失败了"和"命令没有输出"才区分得开。
    /// </para>
    /// </summary>
    public async Task<ExecCapture> ExecCaptureAsync(string containerId, string[] command,
        string? user = null, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        var created = await CreateExecAsync(containerId, command, tty: false, attachStdin: false,
            user, workingDir, cancellationToken).ConfigureAwait(false);
        var stream = await Transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        await using (stream.ConfigureAwait(false))
        {
            var body = JsonSerializer.Serialize(new { Detach = false, Tty = false }, DockerJson.Options);
            (var status, var reason, var hijacked) = await DockerRawHttp
                .PostAsync(stream, $"/exec/{Uri.EscapeDataString(created.Id)}/start", body, upgrade: true, cancellationToken)
                .ConfigureAwait(false);
            if (status is not (200 or 101))
            {
                throw new DockerApiException((System.Net.HttpStatusCode)status, $"启动 exec 失败:HTTP {status} {reason}");
            }
            var decoder = new DockerFrameDecoder(tty: false, timestamps: false);
            await decoder.ReadAsync(hijacked, line =>
            {
                var target = line.Kind == DockerStreamKind.StdErr ? stderr : stdout;
                target.Append(line.Text).Append('\n');
            }, cancellationToken).ConfigureAwait(false);
        }
        var inspect = await InspectExecAsync(created.Id, cancellationToken).ConfigureAwait(false);
        return new(stdout.ToString(), stderr.ToString(), inspect.ExitCode);
    }

    /// <summary>创建一个 exec 实例(还没开始跑)。</summary>
    private Task<ExecCreateResponse> CreateExecAsync(string containerId, string[] command, bool tty, bool attachStdin,
        string? user, string? workingDir, CancellationToken cancellationToken) =>
        PostJsonAsync<ExecCreateResponse>($"/containers/{Uri.EscapeDataString(containerId)}/exec", new
        {
            AttachStdin = attachStdin,
            AttachStdout = true,
            AttachStderr = true,
            Tty = tty,
            Cmd = command,
            User = string.IsNullOrWhiteSpace(user) ? null : user,
            WorkingDir = string.IsNullOrWhiteSpace(workingDir) ? null : workingDir
        }, cancellationToken);

    /// <summary>调整 exec 的伪终端行列。</summary>
    internal Task ResizeExecAsync(string execId, int rows, int columns, CancellationToken cancellationToken = default) =>
        PostAsync($"/exec/{Uri.EscapeDataString(execId)}/resize" +
                  Query(("h", rows.ToString()), ("w", columns.ToString())), null, cancellationToken);

    /// <summary>查 exec 的状态与退出码。</summary>
    internal Task<ExecInspectResponse> InspectExecAsync(string execId, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ExecInspectResponse>($"/exec/{Uri.EscapeDataString(execId)}/json", cancellationToken);
}
