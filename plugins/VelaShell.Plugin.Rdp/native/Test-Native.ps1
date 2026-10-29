param([Parameter(Mandatory)][string]$Library)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'RDP native verification requires a Windows x64 process.'
}
$path = (Resolve-Path -LiteralPath $Library).Path
if (-not ('VelaRdpAbiVersion' -as [type])) {
    Add-Type '[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)] public delegate uint VelaRdpAbiVersion();'
}
$handle = [Runtime.InteropServices.NativeLibrary]::Load($path)
try {
    $exports = @(
        'VelaClipboard_abi_version', 'VelaClipboard_attach', 'VelaClipboard_next', 'VelaClipboard_send',
        'VelaRdpdr_poll', 'WindowsRdpdrConfig_new', 'WindowsRdpdrConfig_destroy',
        'WindowsRdpdrConfig_add_drive', 'ClientConnector_with_static_channel_windows_rdpdr'
    )
    foreach ($name in $exports) {
        $null = [Runtime.InteropServices.NativeLibrary]::GetExport($handle, $name)
    }
    $address = [Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'VelaClipboard_abi_version')
    $version = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($address, [VelaRdpAbiVersion])
    if ($version.Invoke() -ne 1) { throw 'Unsupported RDP clipboard ABI; expected version 1.' }
    $address = [IntPtr]::Zero
    if (-not [Runtime.InteropServices.NativeLibrary]::TryGetExport($handle, 'VelaClipboard_fault_isolation_version', [ref]$address)) {
        throw 'RDP extension lacks clipboard fault isolation. Rebuild native/vela_clipboard.rs; ABI 1 alone is insufficient.'
    }
    $isolation = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($address, [VelaRdpAbiVersion])
    if ($isolation.Invoke() -ne 1) { throw 'Unsupported RDP clipboard fault-isolation version; expected version 1.' }
} finally {
    [Runtime.InteropServices.NativeLibrary]::Free($handle)
}
Get-FileHash -LiteralPath $path -Algorithm SHA256
