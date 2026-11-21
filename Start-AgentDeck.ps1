param(
    [switch]$Foreground,
    [switch]$Stop,
    [switch]$Desktop,
    [switch]$Demo
)

$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$pythonPath = 'D:\AgentDeck-tools\venv\Scripts\python.exe'
$pythonwPath = 'D:\AgentDeck-tools\venv\Scripts\pythonw.exe'
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    $pythonPath = Join-Path $projectPath '.venv\Scripts\python.exe'
    $pythonwPath = Join-Path $projectPath '.venv\Scripts\pythonw.exe'
    if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
        $pythonPath = (Get-Command python.exe -ErrorAction Stop).Source
        $pythonwCmd = Get-Command pythonw.exe -ErrorAction SilentlyContinue
        $pythonwPath = if ($pythonwCmd) { $pythonwCmd.Source } else { $pythonPath }
    }
}
if (-not (Test-Path -LiteralPath $pythonwPath -PathType Leaf)) {
    $pythonwPath = $pythonPath
}
$dataPath = if (Test-Path -LiteralPath 'D:\') { 'D:\AgentDeck-data' } else {
    Join-Path $env:LOCALAPPDATA 'AgentDeck'
}
$cliPath = Join-Path $projectPath 'host\agentdeck.py'
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$common = @($cliPath, '--data', $dataPath)
if ($Demo) { $common += '--demo' }
if ($Stop) {
    & $pythonPath @common '--quit'
    exit $LASTEXITCODE
}
$optionsForRun = $common + '--allow-shutdown'
if ($Foreground) {
    & $pythonPath @optionsForRun
    exit $LASTEXITCODE
}
$logRoot = if ($Demo) { Join-Path $dataPath 'demo' } else { $dataPath }
function Open-Desktop {
    if (-not $Desktop) { return }
    $windowSource = Join-Path $projectPath 'host\Desktop.cs'
    $layout = Join-Path $projectPath 'host\Window.xaml'
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $rootExe = Join-Path $projectPath 'AgentDeck.exe'
    $needsBuild = -not (Test-Path -LiteralPath $rootExe) -or ((Get-Item -LiteralPath $windowSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $rootExe).LastWriteTimeUtc)
    if ($needsBuild) {
        $references = @('System.Core.dll', 'System.Net.Http.dll', 'System.Security.dll',
            'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll') |
            ForEach-Object { '/reference:' + (Join-Path $framework $_) }
        $references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') |
            ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
        $buildPath = Join-Path $logRoot ('AgentDeck-build-' + $PID + '.exe')
        & (Join-Path $framework 'csc.exe') /nologo /target:winexe /optimize+ /codepage:65001 "/out:$buildPath" @references $windowSource
        if ($LASTEXITCODE -or -not (Test-Path -LiteralPath $buildPath)) {
            throw 'Failed to compile AgentDeck.exe.'
        }
        Move-Item -LiteralPath $buildPath -Destination $rootExe -Force
    }
    $windowArguments = @(('"' + $logRoot + '"'), ('"' + $layout + '"'))
    Start-Process -FilePath $rootExe -ArgumentList $windowArguments -WorkingDirectory $projectPath
}
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$alive = Join-Path $logRoot 'bridge-alive.json'
if (Test-Path -LiteralPath $alive) {
    $record = Get-Content -Raw -LiteralPath $alive | ConvertFrom-Json
    $existing = Get-CimInstance Win32_Process -Filter "ProcessId=$($record.pid)"
    if ($existing -and $existing.CommandLine -and $existing.CommandLine.Contains($cliPath)) {
        Open-Desktop
        return
    }
}
$arguments = foreach ($item in $optionsForRun) { '"' + $item + '"' }
$child = Start-Process -FilePath $pythonwPath -ArgumentList $arguments -WorkingDirectory $projectPath -WindowStyle Hidden -PassThru
for ($retry = 0; $retry -lt 25; $retry++) {
    if ($child.HasExited) { throw ('Bridge exited unexpectedly. Check ' + (Join-Path $logRoot 'bridge-errors.log')) }
    if (Test-Path -LiteralPath $alive) {
        $record = Get-Content -Raw -LiteralPath $alive | ConvertFrom-Json
        if ($record.pid -eq $child.Id) { Open-Desktop; return }
        $worker = Get-CimInstance Win32_Process -Filter "ProcessId=$($record.pid)"
        if ($worker -and $worker.ParentProcessId -eq $child.Id) { Open-Desktop; return }
    }
    Start-Sleep -Milliseconds 200
}
