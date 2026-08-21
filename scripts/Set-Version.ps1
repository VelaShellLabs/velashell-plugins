#Requires -Version 7.0
<#
.SYNOPSIS
    把本仓库的发行版本号写进所有落点。

.DESCRIPTION
    这里说的"版本"是**这一批第一方插件的发行版本**,不是 SDK 版本。本仓库是一趟
    **统一发布列车**:一次 Release,所有插件同上一个版本号。落点有三类:

      Directory.Build.props   <VelaPluginsVersion>   —— 程序集版本 + 分发包文件名
      README.md               版本横幅                —— 给人看的,过期了会被照着抄
      plugins/*/plugin.json    "version"              —— **.vpx 文件名与宿主看到的插件版本**

    最后那一类最容易被忘:打包器出的是 <id>-<plugin.json 的 version>.vpx,
    与 MSBuild 那边的 VelaPluginsVersion 毫无关系 —— 不写它,发 1.4.0 出来的仍旧是
    velashell.redis-0.1.0.vpx(2026-08-22 就是这么发出去的)。

    统一列车的代价是没改过的插件也跟着涨版本,用户那边会看到一次"更新";
    换来的是"这台机器上装的是哪一批插件"只有一个答案,排查时不必逐个去问版本。

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

# 每个插件的 plugin.json。**这是 .vpx 文件名与宿主看到的插件版本的唯一来源** ——
# 打包器出的是 <id>-<plugin.json 的 version>.vpx,与 MSBuild 的 VelaPluginsVersion
# 毫无关系。不把它一起写,发 1.4.0 出来的仍旧是 velashell.redis-0.1.0.vpx。
#
# 于是本仓库是一趟**统一发布列车**:一次 Release,所有插件同上一个版本号。
# 代价是没改过的插件也会跟着涨版本(用户那边会看到一次"更新"),换来的是
# "这台机器上装的是哪一批插件"永远只有一个答案 —— 排查问题时不必逐个去问版本。
#
# 动态枚举而不是写死四条:新增插件时没人会记得回来改这个脚本,
# 而漏掉的后果是那个插件的 .vpx 永远停在它初始的版本号上,且不会有任何报错。
foreach ($manifest in Get-ChildItem (Join-Path $root 'plugins') -Directory |
                      ForEach-Object { Join-Path $_.FullName 'plugin.json' } |
                      Where-Object { Test-Path $_ } | Sort-Object) {
    $relative = [IO.Path]::GetRelativePath($root, $manifest).Replace('\', '/')
    $targets += @{
        Path = $relative
        # 只认顶层的 "version" 键。带引号前缀天然排除了 minSdkVersion 这类
        # 以 Version 结尾的键名(它们里面没有 `"version` 这个子串)。
        Pattern     = '(?<pre>"version"\s*:\s*")[^"]+(?<post>")'
        Replacement = "`${pre}$Version`${post}"
        What        = 'plugin.json 的 version(决定 .vpx 文件名)'
    }
}

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

# 显式 exit 0,别靠"脚本正常结束"隐含成功。
# 调用方是 `& ./scripts/Set-Version.ps1 ...` 后面跟一句 if ($LASTEXITCODE) —— 而 .ps1
# **不调用 exit 就根本不会设置 $LASTEXITCODE**,它会原样保留调用方进程里的旧值。
# GitHub 的每个 pwsh 步骤都是全新进程,那里的旧值是 $null,于是 `$LASTEXITCODE -ne 0`
# 求值为真 —— 脚本明明改好了文件,步骤却报 exit code 1。
# 2026-08-22 发 1.0.0 时就是这么红的(此前每次发版 main 已经是目标版本,
# 走的是上面那条 exit 0 的分支,所以一直没露面)。
exit 0
