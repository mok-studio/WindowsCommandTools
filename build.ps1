<#
    build.ps1 — WindowsCommandTools 构建脚本
    ------------------------------------------------------------------
    只用 Windows 自带的 .NET Framework 4.8 编译器（csc.exe），
    不需要安装 .NET SDK、不需要 Visual Studio、不需要联网。

    产物：dist\WindowsCommandTools.exe —— 单文件、双击即用、无需任何运行时。

    用法：
      pwsh -File build.ps1               构建并自检
      pwsh -File build.ps1 -SelfTestOnly 只跑自检
      pwsh -File build.ps1 -Run          构建后启动界面
#>
[CmdletBinding()]
param(
    [switch]$SelfTestOnly,
    [switch]$Run,
    [switch]$KeepBuild,
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "    $text" -ForegroundColor Yellow }

$exeName = 'WindowsCommandTools.exe'

# ---------------------------------------------------------------- 编译器定位
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
if (-not (Test-Path $csc)) {
    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $csc = Join-Path $fw 'csc.exe'
    $wpf = Join-Path $fw 'WPF'
}
if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 编译器 csc.exe，请确认系统已安装 .NET Framework 4.x" }

$exePath = Join-Path $root (Join-Path $OutDir $exeName)

# ---------------------------------------------------------------- 只自检
if ($SelfTestOnly) {
    if (-not (Test-Path $exePath)) { throw "还没构建：$exePath 不存在" }
    & $exePath --selftest
    exit $LASTEXITCODE
}

Write-Step "构建 WindowsCommandTools"
Write-Ok "编译器 $csc"
Write-Ok "输出   $exePath"

# ---------------------------------------------------------------- 清理
$buildDir = Join-Path $root 'build'
if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root $OutDir) | Out-Null

# ---------------------------------------------------------------- 命令库 JSON 预检
Write-Step "校验命令库 JSON"
$resources = @()
$totalCmd = 0
foreach ($shell in @(@{ dir = 'cmd'; label = 'CMD'; res = 'cmd' },
                     @{ dir = 'ps';  label = 'PowerShell'; res = 'ps' })) {
    $shellDir = Join-Path $root (Join-Path 'commands' $shell.dir)
    $files = @(Get-ChildItem (Join-Path $shellDir '*.json') -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($files.Count -eq 0) { Write-Warn2 "commands\$($shell.dir) 下没有 JSON"; continue }
    $sub = 0
    foreach ($f in $files) {
        try {
            $j = Get-Content -Raw -Encoding UTF8 $f.FullName | ConvertFrom-Json
            $n = @($j.commands).Count
            $sub += $n
            Write-Ok ("{0,-8} {1,-26} {2,3} 条  ({3})" -f $shell.label, $f.Name, $n, $j.category)
        }
        catch {
            throw "命令库文件 $($f.Name) 不是合法 JSON：$($_.Exception.Message)"
        }
        $resources += "/resource:$($f.FullName),lib.$($shell.res).$($f.Name)"
    }
    $totalCmd += $sub
    Write-Ok "$($shell.label) 合计 $sub 条命令"
}
Write-Ok "命令库总计 $totalCmd 条"

# ---------------------------------------------------------------- 资源
$stylePath = Join-Path $root 'ui\styles.xaml'
if (-not (Test-Path $stylePath)) { throw "缺少样式表 ui\styles.xaml" }
$resources += "/resource:$stylePath,ui.styles.xaml"
# Win32 资源：图标 + VERSIONINFO（资源管理器「属性 → 详细信息」里的文件说明就来自这里）。
# app.res 由 tools/make_res.py 从 ui\app.ico 生成后固化进仓库，
# 所以构建本身仍然只用系统自带的 csc.exe，不需要 Python 或 rc.exe。
# 要改图标或版本信息：python tools/make_res.py --out ui\app.res
$resPath = Join-Path $root 'ui\app.res'
$icoPath = Join-Path $root 'ui\app.ico'
$hasRes = Test-Path $resPath
$hasIcon = Test-Path $icoPath
if ($hasRes) { Write-Ok "Win32 资源 ui\app.res（图标 + 文件说明）" }
else { Write-Warn2 "缺少 ui\app.res：将退回只用图标，文件属性里不会有说明" }

# ---------------------------------------------------------------- 源码
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "src 目录下没有 .cs 源码" }
Write-Ok "源码 $($sources.Count) 个文件"

# ---------------------------------------------------------------- 编译
Write-Step "编译（C# 5 / .NET Framework 4.8 / WPF 纯代码）"
$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/langversion:5'
    '/platform:x64'
    '/optimize+'
    '/debug-'
    '/warn:4'
    '/nostdlib-'
    '/out:' + $exePath
    "/r:$wpf\PresentationFramework.dll"
    "/r:$wpf\PresentationCore.dll"
    "/r:$wpf\WindowsBase.dll"
    "/r:$fw\System.Xaml.dll"
    "/r:$fw\System.Core.dll"
)
# /win32res 和 /win32icon 互斥：优先用 res（它里面已经含图标了）
if ($hasRes) { $cscArgs += "/win32res:$resPath" }
elseif ($hasIcon) { $cscArgs += "/win32icon:$icoPath" }

$all = $cscArgs + $resources + $sources
$output = & $csc $all 2>&1
$warnings = @($output | Where-Object { $_ -match ': warning ' })
$errors   = @($output | Where-Object { $_ -match ': error ' })

foreach ($w in $warnings) { Write-Warn2 $w }
if ($errors.Count -gt 0) {
    Write-Host ""
    foreach ($e in $errors) { Write-Host $e -ForegroundColor Red }
    throw "编译失败：$($errors.Count) 个错误"
}
if (-not (Test-Path $exePath)) { throw "编译没有产出 exe" }

$size = (Get-Item $exePath).Length
Write-Ok ("编译成功：{0:N0} 字节（{1:N0} KB）" -f $size, ($size / 1KB))

# ---------------------------------------------------------------- 随包命令库
foreach ($shell in @('cmd', 'ps')) {
    $src = Join-Path $root (Join-Path 'commands' $shell)
    $dst = Join-Path $root (Join-Path $OutDir (Join-Path 'commands' $shell))
    if (-not (Test-Path $src)) { continue }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Get-ChildItem (Join-Path $src '*.json') | ForEach-Object { Copy-Item $_.FullName $dst -Force }
}
Write-Ok "命令库已复制到 $OutDir\commands\（可自行编辑扩展）"

# ---------------------------------------------------------------- 便携模式
# exe 同级只要存在 data\ 目录，程序就自动进入便携模式：
# 设置 / 主题 / 历史全部写在 data\ 里，不碰 %APPDATA%，整个文件夹拷走即可。
$dataDir = Join-Path $root (Join-Path $OutDir 'data')
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$portableReadme = @"
WindowsCommandTools 便携版
============================================================

这个文件夹就是完整的便携版，整个拷到 U 盘 / 任意目录都能直接用。

目录说明
--------
  WindowsCommandTools.exe   主程序。命令库（445 条）、界面样式、图标、版本信息
                            全部内嵌在这一个文件里，单独拿走它也能正常运行。
  WCTdata\                  存在这个目录 = 便携模式。
                            设置、主题、历史都写在这里，不会碰 %APPDATA%，
                            也不会在宿主机上留下别的东西。
                            删掉这个目录就变回"安装模式"（数据存 %APPDATA%）。
  commands\                 可选。放在这里的 JSON 会覆盖 / 扩展内置命令库，
                            删掉不影响使用。
    cmd\                    CMD 命令库
    ps\                     PowerShell 命令库

命令行参数
----------
  --portable            强制便携模式（即使没有 data\ 目录）
  --data <目录>         把数据目录指定到任意位置
  --lang auto|zh|en     临时切换界面语言，只影响本次运行
  --shell cmd|ps        指定启动引擎（默认 PowerShell）
  --multi               启动即打开多命令（多行）模式
  --selftest            跑 139 项内置自检

"@
[System.IO.File]::WriteAllText((Join-Path $dataDir 'README.txt'), $portableReadme, (New-Object System.Text.UTF8Encoding $false))
Write-Ok "便携模式已启用：$OutDir\WCTdata\ 存在即自动生效"

# ---------------------------------------------------------------- 自检
Write-Step "运行内置自检"
$report = Join-Path $buildDir 'selftest.txt'
& $exePath --selftest --out $report | Out-Null
$code = $LASTEXITCODE
if (Test-Path $report) { Get-Content $report -Encoding UTF8 | ForEach-Object { Write-Host $_ } }
else { Write-Warn2 "自检报告未生成" }
if ($code -ne 0) { throw "自检未通过（退出码 $code）" }

Write-Step "核对文件属性"
$vi = (Get-Item $exePath).VersionInfo
Write-Ok ("文件说明：{0}" -f $vi.FileDescription)
Write-Ok ("产品名称：{0}   版本：{1}" -f $vi.ProductName, $vi.FileVersion)
if ([string]::IsNullOrWhiteSpace($vi.FileDescription)) {
    Write-Warn2 "文件说明是空的 —— 检查 ui\app.res 是否存在、是否含 VERSIONINFO"
}

Write-Host ""
Write-Host "构建完成：$exePath" -ForegroundColor Green
Write-Host "双击即可运行，无需安装任何运行时。" -ForegroundColor Green

if (-not $KeepBuild -and (Test-Path $buildDir)) { Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue }

if ($Run) { Start-Process $exePath }
exit 0
