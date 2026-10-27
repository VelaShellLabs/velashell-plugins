using System.Reflection;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpDocumentTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TestPluginContext Context() => new() { PluginId = "velashell.rdp" };

    [TestMethod]
    public async Task OpenWaitsForActivationThenDisposeWithoutViewClearsCredentials()
    {
        using var context = Context();
        var entered = Signal();
        var activate = Signal();
        var session = new IntegrationSession();
        var factory = new IntegrationFactory(async (_, _, token) =>
        {
            entered.SetResult();
            await activate.Task.WaitAsync(token);
            return session;
        });
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        Task<IWorkspaceDocument> open = provider.OpenAsync(RdpSettingsTests.Request());
        await entered.Task.WaitAsync(Deadline);
        Assert.IsFalse(open.IsCompleted);
        activate.SetResult();
        IWorkspaceDocument document = await open.WaitAsync(Deadline);
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        await document.DisposeAsync();
        await document.DisposeAsync();
        Assert.AreEqual(1, session.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => factory.Credentials!.GetPasswordForNative());
        IReadOnlyList<string> storedKeys = await context.Storage.GetKeysAsync();
        Assert.IsEmpty(storedKeys);
        Assert.IsEmpty(context.CollectingLog.Entries);
    }

    [TestMethod]
    public async Task TunnelTransportIdentityAndDomainAreSeparatedAndNoRequestIsRetained()
    {
        using var context = Context();
        var factory = new IntegrationFactory((_, _, _) => Task.FromResult<IRdpSession>(new IntegrationSession()));
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var request = RdpSettingsTests.Request(("domain", "DOMAIN")) with
        {
            Host = "127.0.0.1", Port = 45123, Username = "user", Tunnel = new("windows.example", 3389, "bastion")
        };
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(request);
        Assert.AreEqual("127.0.0.1", factory.Options!.TransportHost);
        Assert.AreEqual(45123, factory.Options.TransportPort);
        Assert.AreEqual("windows.example", factory.Options.TargetHost);
        Assert.AreEqual(3389, factory.Options.TargetPort);
        Assert.AreEqual("DOMAIN", factory.Credentials!.Domain);
        Assert.AreEqual("user", factory.Credentials.Username);
        Assert.Contains("windows.example:3389", document.Endpoint);
        foreach (Type type in new[] { typeof(RdpWorkspaceDocument), typeof(RdpWorkspaceProvider) })
            Assert.IsFalse(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(f => f.FieldType == typeof(WorkspaceConnectRequest)));
        Assert.AreEqual(nameof(RdpCredentials), factory.Credentials.ToString());
    }

    [TestMethod]
    public async Task ReconnectDisposesOldBeforeConnectAndIgnoresCapturedOldEvent()
    {
        using var context = Context();
        var old = new IntegrationSession();
        var next = new IntegrationSession();
        int connects = 0;
        var factory = new IntegrationFactory((_, _, _) =>
        {
            if (++connects == 1) return Task.FromResult<IRdpSession>(old);
            Assert.AreEqual(1, old.DisposeCount);
            return Task.FromResult<IRdpSession>(next);
        });
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        Action<RdpSessionStatus>? captured = old.CapturedStatusHandler;
        await document.ReconnectAsync();
        captured?.Invoke(new(RdpSessionState.Faulted, "private-test-secret"));
        Assert.AreSame(next, document.Session);
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        await document.DisconnectAsync();
        Assert.IsNull(document.Session);
        Assert.AreEqual(1, next.DisposeCount);
        Assert.AreEqual("Disconnected", document.Status.Message);
    }

    [TestMethod]
    public async Task CloseCancelsBlockedReconnectAndCannotResurrectSession()
    {
        using var context = Context();
        var entered = Signal();
        int connects = 0;
        var factory = new IntegrationFactory(async (_, _, token) =>
        {
            if (++connects == 1) return new IntegrationSession();
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("Cancelled reconnect returned.");
        });
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        Task reconnect = document.ReconnectAsync();
        await entered.Task.WaitAsync(Deadline);
        await document.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.ThrowsAsync<OperationCanceledException>(() => reconnect);
        Assert.IsNull(document.Session);
        Assert.AreEqual(ProtocolSessionState.Closed, document.Status.State);
        Assert.ThrowsExactly<ObjectDisposedException>(() => factory.Credentials!.GetPasswordForNative());
    }

    [TestMethod]
    public async Task DisconnectInterruptsBlockedReconnectBeforeWaitingForOperationGate()
    {
        using var context = Context();
        var entered = Signal();
        int connects = 0;
        var factory = new IntegrationFactory(async (_, _, token) =>
        {
            if (++connects == 1) return new IntegrationSession();
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("Cancelled reconnect returned.");
        });
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        Task reconnect = document.ReconnectAsync();
        await entered.Task.WaitAsync(Deadline);
        await document.DisconnectAsync().WaitAsync(Deadline);
        await Assert.ThrowsAsync<OperationCanceledException>(() => reconnect);
        Assert.IsNull(document.Session);
        Assert.AreEqual("Disconnected", document.Status.Message);
    }

    [TestMethod]
    public async Task LocaleUpdatesOpenedDocumentAndCannotDeadlockWithCloseCallback()
    {
        using var context = Context();
        var factory = new IntegrationFactory((_, _, _) => Task.FromResult<IRdpSession>(new IntegrationSession()));
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        var localeEntered = Signal();
        var releaseLocale = Signal();
        document.LocaleChanged += () =>
        {
            localeEntered.TrySetResult();
            releaseLocale.Task.GetAwaiter().GetResult();
        };
        Task locale = Task.Run(() => provider.UpdateLocale(new("zh-Hans")));
        await localeEntered.Task.WaitAsync(Deadline);
        try
        {
            Assert.AreEqual("已连接", document.Status.Message);
            // Must complete while the locale subscriber is blocked: neither monitor may be held by it.
            await document.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        finally { releaseLocale.TrySetResult(); }
        await locale.WaitAsync(Deadline);
        Assert.AreEqual("已关闭", document.Status.Message);
    }

    [TestMethod]
    public async Task ShutdownCallbackDoesNotBlockOnTeardownAndNewOpenIsRejected()
    {
        using var context = Context();
        var disposing = Signal();
        var release = Signal();
        var session = new IntegrationSession { OnDispose = async () => { disposing.SetResult(); await release.Task; } };
        var factory = new IntegrationFactory((_, _, _) => Task.FromResult<IRdpSession>(session));
        var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        await provider.OpenAsync(RdpSettingsTests.Request());
        Task shutdown = Task.Run(context.RequestShutdown);
        try
        {
            await disposing.Task.WaitAsync(Deadline);
            await shutdown.WaitAsync(Deadline);
            Exception? rejected = null;
            try { await provider.OpenAsync(RdpSettingsTests.Request()); }
            catch (Exception ex) { rejected = ex; }
            Assert.IsTrue(rejected is OperationCanceledException or ObjectDisposedException);
            Assert.AreEqual(1, factory.ConnectCount);
        }
        finally { release.TrySetResult(); }
        await provider.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task OpenCleanupFailureStillDrainsOpenCounterAndClearsCredentials()
    {
        using var context = Context();
        var entered = Signal();
        var proceed = Signal();
        var session = new IntegrationSession { OnDispose = () => throw new IOException("dispose failure") };
        var cancelled = Signal();
        var factory = new IntegrationFactory(async (_, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await proceed.Task;
            return session;
        });
        var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        Task<IWorkspaceDocument> open = provider.OpenAsync(RdpSettingsTests.Request());
        await entered.Task.WaitAsync(Deadline);
        Task close = provider.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(Deadline);
        proceed.SetResult();
        await Assert.ThrowsExactlyAsync<IOException>(() => open);
        await close.WaitAsync(Deadline);
        Assert.ThrowsExactly<ObjectDisposedException>(() => factory.Credentials!.GetPasswordForNative());
    }

    [TestMethod]
    public async Task StatusChangingDuringSubscriptionIsReadRatherThanAssumedConnected()
    {
        using var context = Context();
        var factory = new IntegrationFactory((_, _, _) => Task.FromResult<IRdpSession>(
            new IntegrationSession { ChangeToDisconnectedOnSubscribe = true }));
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.AreEqual("Disconnected", document.Status.Message);
        await document.ReconnectAsync();
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.AreEqual("Disconnected", document.Status.Message);
    }

    [TestMethod]
    public async Task ReconnectCertificateChangeIsNotTrustedOrLeakedAndLocaleRefreshesIt()
    {
        using var context = Context();
        int connects = 0;
        var factory = new IntegrationFactory((_, _, _) => ++connects == 1
            ? Task.FromResult<IRdpSession>(new IntegrationSession())
            : throw new ProtocolCertificateTrustException("private-test-secret", "subject", "issuer", DateTimeOffset.UtcNow.AddDays(1), new string('A', 64), "Changed"));
        await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
        var document = (RdpWorkspaceDocument)await provider.OpenAsync(RdpSettingsTests.Request());
        await Assert.ThrowsExactlyAsync<ProtocolCertificateTrustException>(() => document.ReconnectAsync());
        Assert.Contains("Close this tab", document.Status.Message!);
        Assert.IsFalse(document.Status.Message!.Contains("private-test-secret", StringComparison.Ordinal));
        Assert.AreEqual("", factory.Options!.TrustedSha256);
        provider.UpdateLocale(new("zh-Hans"));
        Assert.Contains("重新打开", document.Status.Message!);
        Assert.IsEmpty(context.CollectingLog.Entries);
    }

    [TestMethod]
    public async Task InitialCertificateFailureIsLocalizedWithoutChangingTrustDetails()
    {
        using var context = Context();
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddDays(1);
        string pin = new('A', 64);
        var factory = new IntegrationFactory((_, _, _) => throw new ProtocolCertificateTrustException(
            "private-test-secret", "CN=server", "CN=issuer", expires, pin, "RemoteCertificateNameMismatch"));
        await using var provider = new RdpWorkspaceProvider(context, new("zh-Hans"), factory);
        ProtocolCertificateTrustException actual = await Assert.ThrowsExactlyAsync<ProtocolCertificateTrustException>(
            () => provider.OpenAsync(RdpSettingsTests.Request()));
        Assert.AreEqual("需要确认 RDP 服务器证书。", actual.Message);
        Assert.AreEqual("CN=server", actual.Subject);
        Assert.AreEqual("CN=issuer", actual.Issuer);
        Assert.AreEqual(expires, actual.ExpiresOn);
        Assert.AreEqual(pin, actual.Thumbprint);
        Assert.AreEqual("RemoteCertificateNameMismatch", actual.PolicyErrors);
        Assert.IsNull(actual.InnerException);
    }

    [TestMethod]
    public async Task InvalidConfigurationNeverCallsNativeFactoryOrRewritesFieldHint()
    {
        using var context = Context();
        var factory = new IntegrationFactory((_, _, _) => throw new AssertFailedException("Invalid input reached native boundary."));
        await using var provider = new RdpWorkspaceProvider(context, new("zh-Hans"), factory);
        ProtocolConnectionException failure = await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(
            () => provider.OpenAsync(RdpSettingsTests.Request(("desktopWidth", "0"))));
        Assert.Contains("desktopWidth", failure.Message);
        Assert.AreEqual(0, factory.ConnectCount);
    }

    [TestMethod]
    public async Task AuthenticationNetworkUnsupportedAndCancellationKeepTheirCategories()
    {
        foreach (Exception expected in new Exception[]
        {
            new ProtocolAuthenticationException("auth"), new ProtocolConnectionException("network"),
            new ProtocolUnsupportedException("unsupported"), new OperationCanceledException()
        })
        {
            using var context = Context();
            var factory = new IntegrationFactory((_, _, _) => throw expected);
            await using var provider = new RdpWorkspaceProvider(context, new("en"), factory);
            Exception? actual = null;
            try { await provider.OpenAsync(RdpSettingsTests.Request()); }
            catch (Exception ex) { actual = ex; }
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.GetType(), actual.GetType());
            Assert.IsNull(actual.InnerException);
            Assert.IsFalse(actual.Message.Contains("private-test-secret", StringComparison.Ordinal));
            Assert.ThrowsExactly<ObjectDisposedException>(() => factory.Credentials!.GetPasswordForNative());
        }
    }

    private sealed class IntegrationFactory(Func<RdpConnectionOptions, RdpCredentials, CancellationToken, Task<IRdpSession>> connect) : IRdpSessionFactory
    {
        public RdpConnectionOptions? Options { get; private set; }
        public RdpCredentials? Credentials { get; private set; }
        public int ConnectCount { get; private set; }
        public Task<IRdpSession> ConnectAsync(RdpConnectionOptions options, RdpCredentials credentials, CancellationToken cancellationToken = default)
        {
            Options = options;
            Credentials = credentials;
            ConnectCount++;
            return connect(options, credentials, cancellationToken);
        }
    }

    private sealed class IntegrationSession : IRdpSession
    {
        private Action<RdpSessionStatus>? _statusChanged;
        public Action<RdpSessionStatus>? CapturedStatusHandler { get; private set; }
        public bool ChangeToDisconnectedOnSubscribe { get; init; }
        public Func<Task>? OnDispose { get; init; }
        public int DisposeCount { get; private set; }
        public RdpSessionStatus Status { get; private set; } = new(RdpSessionState.Connected);
        public RdpFrameInfo FrameInfo => new(1, 1, 4, 1);
        public RdpPointer Pointer => new(RdpPointerKind.Default);
        public event Action? FrameAvailable { add { } remove { } }
        public event Action<RdpPointer>? PointerChanged { add { } remove { } }
        public event Action<RdpSessionStatus>? StatusChanged
        {
            add
            {
                _statusChanged += value;
                CapturedStatusHandler = value;
                if (ChangeToDisconnectedOnSubscribe) Status = new(RdpSessionState.Disconnected);
            }
            remove { _statusChanged -= value; }
        }
        public bool TryCopyFrame(Memory<byte> destination, out RdpFrameInfo frame) { frame = FrameInfo; return false; }
        public ValueTask SendInputAsync(RdpInput input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            Status = new(RdpSessionState.Closed);
            if (OnDispose is not null) await OnDispose();
        }
    }
}
