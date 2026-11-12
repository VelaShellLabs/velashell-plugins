using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpVirtualFileTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void OleDescriptorsAndStreamAcquisitionDoNotReadContent()
    {
        int reads = 0;
        var progress = new RdpClipboardTransfer();
        using var files = new RdpVirtualFiles([new("folder", true, 0), new("folder\\data.bin", false, 4), new("other", false, 0)],
            (_, offset, count, _) => { reads++; return Task.FromResult(new byte[] { 1, 2, 3, 4 }.AsSpan((int)offset, (int)count).ToArray()); }, progress, default);
        using var data = new WindowsVirtualFileDataObject(files);
        nint unknown = Marshal.GetComInterfaceForObject(data, typeof(IVirtualFileDataObject));
        STGMEDIUM descriptor = default, stream = default;
        nint buffer = Marshal.AllocCoTaskMem(16), fetched = Marshal.AllocCoTaskMem(8);
        try
        {
            GetData get = Method<GetData>(unknown, 3);
            FORMATETC format = WindowsVirtualFileDataObject.Format(WindowsVirtualFileDataObject.DescriptorFormat, TYMED.TYMED_HGLOBAL);
            Marshal.ThrowExceptionForHR(get(unknown, ref format, out descriptor));
            Assert.AreEqual(TYMED.TYMED_HGLOBAL, descriptor.tymed);
            nint memory = WindowsVirtualFileClipboard.Native.GlobalLock(descriptor.unionmember);
            byte[] descriptors = new byte[4 + 3 * 592];
            try { Marshal.Copy(memory, descriptors, 0, descriptors.Length); }
            finally { _ = WindowsVirtualFileClipboard.Native.GlobalUnlock(descriptor.unionmember); }
            Assert.AreSequenceEqual([.. files.Files], [.. RdpClipboardCodec.DecodeFiles(descriptors)]);
            format = WindowsVirtualFileDataObject.Format(WindowsVirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, 1);
            Marshal.ThrowExceptionForHR(get(unknown, ref format, out stream));
            Assert.AreEqual(0, reads);
            Assert.IsNull(progress.Snapshot);
            Read read = Method<Read>(stream.unionmember, 3);
            Marshal.ThrowExceptionForHR(read(stream.unionmember, buffer, 3, fetched));
            Assert.AreEqual(3, Marshal.ReadInt32(fetched));
            Assert.AreEqual(1, reads);
            byte[] bytes = new byte[3];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, bytes);
            Seek seek = Method<Seek>(stream.unionmember, 5);
            Marshal.ThrowExceptionForHR(seek(stream.unionmember, 1, 0, fetched));
            Marshal.ThrowExceptionForHR(read(stream.unionmember, buffer, 3, fetched));
            Assert.AreEqual(4L, progress.Snapshot!.Bytes);
            files.Dispose();
            Assert.IsLessThan(0, read(stream.unionmember, buffer, 1, fetched));
            Assert.AreEqual(2, reads);
        }
        finally
        {
            if (descriptor.unionmember != 0)
            {
                ReleaseStgMedium(ref descriptor);
            }

            if (stream.unionmember != 0)
            {
                ReleaseStgMedium(ref stream);
            }

            _ = Marshal.Release(unknown);
            Marshal.FreeCoTaskMem(buffer);
            Marshal.FreeCoTaskMem(fetched);
        }
    }

    [TestMethod]
    [DataRow("Performed DropEffect", false)]
    [DataRow("Logical Performed DropEffect", false)]
    [DataRow("Paste Succeeded", true)]
    [DataRow("TargetCLSID", true)]
    [OSCondition(OperatingSystems.Windows)]
    public void NativeShellFeedbackDoesNotEndOperationOrCloseConsumerStreams(string name, bool release)
    {
        var progress = new RdpClipboardTransfer();
        using var files = new RdpVirtualFiles([new("data", false, 2)], static (_, _, count, _) => Task.FromResult(Enumerable.Repeat((byte)3, (int)count).ToArray()), progress, default);
        using var data = new WindowsVirtualFileDataObject(files);
        nint unknown = Marshal.GetComInterfaceForObject(data, typeof(IVirtualFileDataObject));
        Guid iid = typeof(IDataObjectAsyncCapability).GUID;
        nint capability = 0;
        STGMEDIUM stream = default, feedback = default;
        int size = name == "TargetCLSID" ? 16 : 4;
        nint memory = WindowsVirtualFileClipboard.Native.GlobalAlloc(0x42, (nuint)size);
        nint buffer = Marshal.AllocCoTaskMem(1), read = Marshal.AllocCoTaskMem(4);
        try
        {
            Assert.AreEqual(0, Marshal.QueryInterface(unknown, in iid, out capability));
            data.StartOperation(null);
            FORMATETC contents = WindowsVirtualFileDataObject.Format(WindowsVirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, 0);
            Marshal.ThrowExceptionForHR(data.GetData(ref contents, out stream));
            files.Retire();
            nint address = WindowsVirtualFileClipboard.Native.GlobalLock(memory);
            Marshal.WriteInt32(address, 1);
            _ = WindowsVirtualFileClipboard.Native.GlobalUnlock(memory);
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            FORMATETC format = WindowsVirtualFileDataObject.Format(unchecked((short)WindowsVirtualFileClipboard.Native.RegisterClipboardFormat(name)), TYMED.TYMED_HGLOBAL);
            Marshal.ThrowExceptionForHR(Method<SetData>(unknown, 7)(unknown, ref format, ref medium, release ? 1 : 0));
            if (release)
            {
                memory = 0;
            }

            Assert.IsFalse(files.Cancelled);
            data.InOperation(out bool active);
            Assert.IsTrue(active);
            Assert.AreEqual(name == "Paste Succeeded" ? "Completed" : "Transferring", progress.Snapshot!.State);
            Marshal.ThrowExceptionForHR(Method<GetData>(unknown, 3)(unknown, ref format, out feedback));
            address = WindowsVirtualFileClipboard.Native.GlobalLock(feedback.unionmember);
            try { Assert.AreEqual(1, Marshal.ReadInt32(address)); }
            finally { _ = WindowsVirtualFileClipboard.Native.GlobalUnlock(feedback.unionmember); }
            Marshal.ThrowExceptionForHR(Method<Read>(stream.unionmember, 3)(stream.unionmember, buffer, 1, read));
            data.EndOperation(0, null, 1);
            Assert.IsFalse(files.Cancelled, "A successful EndOperation cannot close a separately owned stream.");
            Marshal.ThrowExceptionForHR(Method<Read>(stream.unionmember, 3)(stream.unionmember, buffer, 1, read));
            Assert.AreEqual(3, Marshal.ReadByte(buffer));
            Assert.AreEqual("Completed", progress.Snapshot!.State);
            Assert.AreEqual(2L, progress.Snapshot.Bytes);
            if (!release)
            {
                Assert.IsGreaterThanOrEqualTo((nuint)size, WindowsVirtualFileClipboard.Native.GlobalSize(memory));
            }
        }
        finally
        {
            if (stream.unionmember != 0)
            {
                ReleaseStgMedium(ref stream);
            }

            if (feedback.unionmember != 0)
            {
                ReleaseStgMedium(ref feedback);
            }

            if (capability != 0)
            {
                _ = Marshal.Release(capability);
            }

            _ = Marshal.Release(unknown);
            if (memory != 0)
            {
                _ = WindowsVirtualFileClipboard.Native.GlobalFree(memory);
            }

            Marshal.FreeCoTaskMem(buffer); Marshal.FreeCoTaskMem(read);
        }
    }

    [TestMethod]
    public async Task DeliveryWithoutShellFeedbackFinishesAtAllBytesAndSurvivesEofAndDisposal()
    {
        var progress = new RdpClipboardTransfer();
        using var files = new RdpVirtualFiles([new("directory", true, 0), new("empty", false, 0), new("a", false, 2), new("b", false, 3)],
            static (_, _, count, _) => Task.FromResult(new byte[count]), progress, default);
        _ = await files.ReadAsync(1, 0, 1);
        _ = await files.ReadAsync(3, 0, 3);
        Assert.AreEqual("Transferring", progress.Snapshot!.State);
        _ = await files.ReadAsync(2, 0, 2);
        Assert.AreEqual("Delivered", progress.Snapshot.State);
        Assert.AreEqual(5L, progress.Snapshot.Bytes);
        _ = await files.ReadAsync(2, 2, 1);
        _ = await files.ReadAsync(3, 0, 3);
        files.Dispose();
        Assert.AreEqual("Delivered", progress.Snapshot.State);
        Assert.AreEqual(5L, progress.Snapshot.Bytes);
    }

    [TestMethod]
    public async Task EmptyCopyCompletesWithoutContentRequestsAndNewOperationResetsProgress()
    {
        var progress = new RdpClipboardTransfer();
        using var files = new RdpVirtualFiles([new("directory", true, 0), new("empty", false, 0)],
            static (_, _, _, _) => throw new AssertFailedException("Zero-byte content must not request a block."), progress, default);
        files.DescriptorsDelivered();
        Assert.AreEqual("Delivered", progress.Snapshot!.State);
        files.BeginOperation();
        Assert.AreEqual("Delivered", progress.Snapshot!.State);
        files.EndOperation(0, 1);
        _ = await files.ReadAsync(1, 0, 1);
        Assert.AreEqual("Completed", progress.Snapshot.State);
        files.BeginOperation();
        Assert.AreEqual("Delivered", progress.Snapshot.State);
        Assert.AreEqual(0L, progress.Snapshot.Bytes);
        files.EndOperation(0, 0);
        Assert.AreEqual("Cancelled", progress.Snapshot.State);
        files.ConfirmShellCompletion();
        Assert.AreEqual("Cancelled", progress.Snapshot.State);
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80004004), "Cancelled")]
    [DataRow(unchecked((int)0x80004005), "Failed")]
    public void ShellFailureKeepsItsTerminalStateAfterLateProgress(int result, string state)
    {
        var progress = new RdpClipboardTransfer();
        using var files = new RdpVirtualFiles([new("data", false, 2)], static (_, _, _, _) => Task.FromResult(new byte[2]), progress, default);
        files.BeginOperation();
        files.EndOperation(result, 0);
        progress.Sent(0, 0, 2, "data", "Delivered");
        progress.Retry();
        files.ConfirmShellCompletion();
        Assert.AreEqual(state, progress.Snapshot!.State);
        Assert.IsTrue(files.Cancelled);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void NativeFormatProbesReturnHresultsWithoutManagedExceptionsOrTakingRejectedOwnership()
    {
        using var files = new RdpVirtualFiles([new("data", false, 1)], (_, _, _, _) => Task.FromResult(new byte[] { 3 }), new(), default);
        using var data = new WindowsVirtualFileDataObject(files);
        nint unknown = Marshal.GetComInterfaceForObject(data, typeof(IVirtualFileDataObject));
        nint memory = WindowsVirtualFileClipboard.Native.GlobalAlloc(0x42, 4);
        int exceptions = 0;
        void observed(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) { if (e.Exception is COMException) { exceptions++; } }
        AppDomain.CurrentDomain.FirstChanceException += observed;
        try
        {
            FORMATETC format = WindowsVirtualFileDataObject.Format(1, TYMED.TYMED_HGLOBAL);
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            Assert.AreEqual(unchecked((int)0x80040064), Method<SetData>(unknown, 7)(unknown, ref format, ref medium, 1));
            Assert.IsGreaterThanOrEqualTo((nuint)4, WindowsVirtualFileClipboard.Native.GlobalSize(memory));
            Assert.AreEqual(unchecked((int)0x80040064), Method<GetData>(unknown, 3)(unknown, ref format, out _));
            format.cfFormat = WindowsVirtualFileDataObject.DescriptorFormat;
            format.dwAspect = DVASPECT.DVASPECT_ICON;
            Assert.AreEqual(unchecked((int)0x8004006B), Method<GetData>(unknown, 3)(unknown, ref format, out _));
            Assert.AreEqual(0, exceptions);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observed;
            _ = Marshal.Release(unknown);
            _ = WindowsVirtualFileClipboard.Native.GlobalFree(memory);
        }
    }

    [TestMethod]
    public void LargeStreamReadsStayChunkBoundedAndRetirementPreservesTheActiveStream()
    {
        int maximum = 0, calls = 0, released = 0;
        int length = RdpClipboardFiles.ChunkSize * 3 + 7;
        using var files = new RdpVirtualFiles([new("large", false, length)], (_, _, count, _) =>
        {
            maximum = Math.Max(maximum, (int)count); calls++;
            return Task.FromResult(Enumerable.Repeat((byte)23, (int)count).ToArray());
        }, new(), default, () => released++);
        using var stream = new VirtualFileStream(files, 0);
        files.Retire();
        Assert.AreEqual(0, calls);
        nint buffer = Marshal.AllocCoTaskMem(length + 10), read = Marshal.AllocCoTaskMem(4);
        try
        {
            Assert.AreEqual(1, stream.Read(buffer, length + 10, read));
            Assert.AreEqual(length, Marshal.ReadInt32(read));
            Assert.AreEqual(23, Marshal.ReadByte(buffer, length - 1));
            Assert.AreEqual(4, calls);
            Assert.AreEqual(RdpClipboardFiles.ChunkSize, maximum);
            Assert.AreEqual(0, released);
            stream.Dispose();
            Assert.AreEqual(1, released);
            Assert.IsLessThan(0, stream.Read(buffer, 1, read));
        }
        finally { Marshal.FreeCoTaskMem(buffer); Marshal.FreeCoTaskMem(read); }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ClipboardBusyRetriesAreBoundedAndCancellationStopsThem()
    {
        int calls = 0;
        await WindowsVirtualFileClipboard.RetryClipboardAsync(() =>
        {
            if (++calls < 3)
            {
                throw new COMException("Busy", unchecked((int)0x800401D0));
            }

            return Task.CompletedTask;
        }, default);
        Assert.AreEqual(3, calls);
        calls = 0;
        _ = await Assert.ThrowsAsync<COMException>(() => WindowsVirtualFileClipboard.RetryClipboardAsync(() =>
        { calls++; throw new COMException("Busy", unchecked((int)0x800401D0)); }, default));
        Assert.AreEqual(8, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsVirtualFileClipboard.RetryClipboardAsync(() => throw new AssertFailedException("Must not retry after cancellation."), cancelled.Token));
    }

    [TestMethod]
    public async Task DisconnectCancelsBlockedContentAndRejectsLaterReads()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var files = new RdpVirtualFiles([new("data", false, 10)], async (_, _, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, new(), stop.Token);
        Task<byte[]> pending = files.ReadAsync(0, 0, 10);
        await entered.Task;
        stop.Cancel();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => files.ReadAsync(0, 0, 1));
    }

    [TestMethod]
    public void VirtualDescriptorsRejectUnsafePathsAndTransferLimitBeforeReading()
    {
        static Task<byte[]> NoRead(uint index, ulong offset, uint length, CancellationToken ct) => throw new AssertFailedException("Metadata must not read content.");
        _ = Assert.Throws<InvalidDataException>(static () => new RdpVirtualFiles([new("..\\escape", false, 1)], NoRead, new(), default));
        _ = Assert.Throws<InvalidDataException>(static () => new RdpVirtualFiles([new("large", false, RdpClipboardFiles.MaxTransferBytes + 1)], NoRead, new(), default));
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetData(nint self, ref FORMATETC format, out STGMEDIUM medium);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetData(nint self, ref FORMATETC format, ref STGMEDIUM medium, int release);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Read(nint self, nint buffer, int count, nint fetched);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Seek(nint self, long move, int origin, nint position);
    // STGMEDIUM.pUnkForRelease requires runtime COM object marshalling, unsupported by LibraryImport.
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
