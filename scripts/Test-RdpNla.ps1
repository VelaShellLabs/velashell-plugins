[CmdletBinding()]
param(
    [ValidateSet('both', 'negotiate', 'ntlm')]
    [string]$Authentication = 'both',
    [switch]$IncludeNegativeTests
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

foreach ($name in @('VELA_RDP_TEST_NLA_TARGET', 'VELA_RDP_TEST_USER', 'VELA_RDP_TEST_PASS')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required environment variable $name is missing. Configure the repository Actions secret."
    }
}
if ($env:VELA_RDP_TEST_NLA_TARGET -notmatch '^([^:/\s]+):([0-9]{1,5})$') {
    throw 'VELA_RDP_TEST_NLA_TARGET must be a DNS name or IPv4 address followed by :port (1-65535).'
}
$targetHost = $Matches[1]
if ([int]$Matches[2] -lt 1 -or [int]$Matches[2] -gt 65535) {
    throw 'VELA_RDP_TEST_NLA_TARGET port must be between 1 and 65535.'
}

# GitHub masks console output, but not uploaded files. Redact before persisting diagnostics.
$redactions = @(
    $env:VELA_RDP_TEST_NLA_TARGET
    $targetHost
    $env:VELA_RDP_TEST_USER
    $env:VELA_RDP_TEST_PASS
) | ForEach-Object {
    $_
    [System.Security.SecurityElement]::Escape($_)
    [Uri]::EscapeDataString($_)
} | Sort-Object -Unique | Sort-Object Length -Descending

function Protect-Log([string]$Text) {
    foreach ($value in $redactions) {
        $Text = $Text.Replace($value, '[REDACTED]', [StringComparison]::Ordinal)
    }
    return $Text
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'tests/VelaShell.Plugin.Rdp.Tests/VelaShell.Plugin.Rdp.Tests.csproj'
$results = Join-Path $repoRoot 'artifacts/rdp-nla'
New-Item -ItemType Directory -Force $results | Out-Null
$cases = @(
    if ($Authentication -ne 'ntlm') { 'NlaAuthenticatesAndReachesTheDesktop' }
    if ($Authentication -ne 'negotiate') { 'ManagedNtlmAlsoAuthenticatesAndReachesTheDesktop' }
    if ($IncludeNegativeTests) {
        'TlsOnlyIsRefusedByAServerThatRequiresNla'
        'NlaWrongPasswordIsReportedAsAnAuthenticationFailure'
    }
)
$summary = @(
    '## Live RDP NLA results'
    ''
    '| Test | Result |'
    '| --- | --- |'
)
$failed = $false
foreach ($case in $cases) {
    Write-Host "Running $case"
    $savedTrx = Join-Path $results "$case.trx"
    if (Test-Path -LiteralPath $savedTrx) { Remove-Item -LiteralPath $savedTrx }
    $rawDirectory = Join-Path ([IO.Path]::GetTempPath()) ("vela-rdp-nla-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $rawDirectory | Out-Null
    $rawTrx = Join-Path $rawDirectory 'result.trx'
    $passed = $false
    try {
        & dotnet test $project --no-build --no-restore --nologo `
            --filter "FullyQualifiedName=VelaShell.Plugin.Rdp.Tests.RdpNlaInteropTests.$case" `
            --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=result.trx' `
            --results-directory $rawDirectory 2>&1 |
            ForEach-Object { Protect-Log $_.ToString() } |
            Tee-Object -FilePath (Join-Path $results "$case.log")
        $testExitCode = $LASTEXITCODE
        if (Test-Path -LiteralPath $rawTrx) {
            $raw = Get-Content -LiteralPath $rawTrx -Raw
            $report = [xml]::new()
            $report.LoadXml($raw)
            $counters = $report.TestRun.ResultSummary.Counters
            $passed = $testExitCode -eq 0 -and $counters.total -eq '1' -and $counters.passed -eq '1'
            Protect-Log $raw | Set-Content -LiteralPath $savedTrx
        }
        if (-not $passed) {
            Write-Host "::error::$case failed, was skipped, or did not produce exactly one passing result."
        }
    }
    finally {
        # Only the unique temporary directory created for this test contains unredacted results.
        Remove-Item -LiteralPath $rawDirectory -Recurse -Force
    }
    $status = if ($passed) { 'Passed' } else { 'Failed (see log/TRX)' }
    $summary += "| $case | $status |"
    if (-not $passed) { $failed = $true }
}
$summary | Set-Content -LiteralPath (Join-Path $results 'summary.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($failed) { throw 'Live RDP NLA validation failed. See the per-test diagnostic results.' }
