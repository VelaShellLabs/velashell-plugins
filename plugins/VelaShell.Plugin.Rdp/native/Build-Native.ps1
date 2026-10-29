param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [ValidateSet('debug','release')][string]$Profile = 'release'
)
$ErrorActionPreference = 'Stop'
$commit = '1f69e124e6bf85608efce3495c12eaf5f6b5a95d'
$here = $PSScriptRoot
$work = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path $work) { throw 'WorkRoot must be a new directory; existing checkouts are never overwritten.' }
$parent = Split-Path $work -Parent
if (-not (Test-Path $parent -PathType Container)) { throw 'WorkRoot parent must exist.' }
if (-not $IsWindows) { throw 'This script builds the Windows x64 extension only.' }
function Run([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
New-Item -ItemType Directory -Path $work | Out-Null
$source = Join-Path $work 'upstream'
Run 'git' @('clone', '--no-checkout', 'https://github.com/Devolutions/IronRDP.git', $source)
Run 'git' @('-C', $source, 'checkout', '--detach', $commit)
Run 'git' @('-C', $source, 'apply', '--check', (Join-Path $here 'ironrdp-redirection.patch'))
Run 'git' @('-C', $source, 'apply', (Join-Path $here 'ironrdp-redirection.patch'))
Copy-Item (Join-Path $here 'vela_clipboard.rs') (Join-Path $source 'ffi/src/vela_clipboard.rs')
Run 'cargo' @('+1.94.1', 'test', '--locked', '--manifest-path', (Join-Path $source 'ffi/Cargo.toml'), '--target', 'x86_64-pc-windows-msvc', 'vela_clipboard::tests')
$args = @('+1.94.1', 'build', '--locked', '--manifest-path', (Join-Path $source 'ffi/Cargo.toml'), '--target', 'x86_64-pc-windows-msvc')
if ($Profile -eq 'release') { $args += '--release' }
Run 'cargo' $args
$destination = Join-Path $here 'artifacts/win-x64/native'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item (Join-Path $source "target/x86_64-pc-windows-msvc/$Profile/ironrdp.dll") (Join-Path $destination 'DevolutionsIronRdp.dll')
& (Join-Path $here 'Test-Native.ps1') -Library (Join-Path $destination 'DevolutionsIronRdp.dll')
