#Requires -Version 7.0
<#
.SYNOPSIS
    把本仓库的发行版本号写进所有落点。

.DESCRIPTION
    这里说的"版本"是**这一批第一方插件的发行版本**(VelaPluginsVersion),不是 SDK 版本,
    也不是任何单个插件的版本:

      Directory.Build.props   <VelaPluginsVersion>   —— 程序集版本 + 分发包文件名的默认值
      README.md               版本横幅                —— 给人看的,过期了会被照着抄

    单个插件的版本在各自的 plugin.json 里,各自演进,本脚本**不碰** —— Redis 发 0.2.0
    与 Telnet 无关,把它们绑在一起只会逼出一堆无意义的空版本。

    发版流水线在解析出 Release 标签之后**第一件事**就是跑本脚本
    (见 .github/workflows/release.yml),因此产物永远与标签一致,与仓库里当时提交了
    什么无关;发布成功后由 sync-main 任务开一个 PR 把改动回写 main,让仓库自己也保持诚实。

    也可以本地先跑一遍再提交,那样发版时脚本就是个空操作。

.PARAMETER Version
    目标版本,SemVer(1.5.0 或 1.5.0-preview.1)。

.PARAMETER Check
    只报告不落盘;有任何一处不同步就以退出码 1 结束。CI 用它做"仓库是否已同步"的体检。

.EXAMPLE
    pwsh scripts/Set-Version.ps1 1.5.0

.EXAMPLE
    pwsh scripts/Set-Version.ps1 1.5.0 -Check
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Version,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' 不是合法 SemVer。用 1.5.0 或 1.5.0-preview.1 这种形式。"
}

$root = Split-Path -Parent $PSScriptRoot

# ── 落点清单 ────────────────────────────────────────────────────────────────
# 每一项:文件、正则、替换串。正则务必**锚定到唯一的上下文**,别用裸版本号 ——
# README 里 SDK 版本与插件版本长得一模一样,认错了会把 SDK 的号也改掉。
$targets = @(
    @{
        Path        = 'Directory.Build.props'
        Pattern     = '(?<pre><VelaPluginsVersion[^>]*>)[^<]+(?<post></VelaPluginsVersion>)'
        Replacement = "`${pre}$Version`${post}"
        What        = 'VelaPluginsVersion'
    },
    @{
        Path        = 'README.md'
        Pattern     = '(?<pre>>\s*当前版本\s*\*\*)[^*]+(?<post>\*\*)'
        Replacement = "`${pre}$Version`${post}"
        What        = 'README 版本横幅'
    }
)

$drift = @()
foreach ($target in $targets) {
    $path = Join-Path $root $target.Path
    if (-not (Test-Path $path)) { throw "落点文件不存在:$($target.Path)(脚本与仓库结构脱节了)" }

    $original = Get-Content -Raw $path
    $updated = [regex]::Replace($original, $target.Pattern, $target.Replacement)

    if ($updated -eq $original -and $original -notmatch $target.Pattern) {
        # 正则一处都没匹配上 = 文件结构变了,而不是"已经是目标版本"。这两种情况
        # 结果都是"没有改动",但含义天差地别,不区分的话 -Check 会给出假绿灯。
        throw "在 $($target.Path) 里找不到 $($target.What) 的落点(正则没匹配上)。改了文件结构就要同步改本脚本。"
    }

    if ($updated -eq $original) { continue }   # 已经是目标版本

    $drift += "  $($target.Path) —— $($target.What)"
    if (-not $Check) {
        # 不带 BOM 写回:仓库里这两个文件本来就是无 BOM 的,写回时加上会让 diff 多一行噪声。
        [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
        Write-Host "已更新 $($target.Path) —— $($target.What) → $Version"
    }
}

if ($drift.Count -eq 0) {
    Write-Host "版本号已经是 $Version,无需改动。"
    exit 0
}

if ($Check) {
    Write-Host "以下落点与 $Version 不同步:"
    $drift | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "跑 `pwsh scripts/Set-Version.ps1 $Version` 修正。"
    exit 1
}

Write-Host "完成:$($drift.Count) 处已同步到 $Version。"
