param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $verFile = Join-Path $root 'host\version.py'
    $match = Select-String -Path $verFile -Pattern 'APP_VERSION\s*=\s*"([^"]+)"'
    $Version = if ($match) { $match.Matches[0].Groups[1].Value } else { '0.6.0' }
}

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.Core.dll','System.Net.Http.dll','System.Security.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Xaml.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$refs += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fw ('WPF\' + $_)) }

$exeOut = Join-Path $root 'AgentDeck.exe'
$desktopCs = Join-Path $root 'host\Desktop.cs'
$icoPath = Join-Path $root 'assets\logo.ico'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$exeOut") + $refs + @($desktopCs)
if (Test-Path -LiteralPath $icoPath) { $cscArgs += "/win32icon:$icoPath" }
& (Join-Path $fw 'csc.exe') @cscArgs
if ($LASTEXITCODE) { throw 'csc.exe compilation failed.' }

$valProc = Start-Process -FilePath $exeOut -ArgumentList @(
    'D:\AgentDeck-data', (Join-Path $root 'host\Window.xaml'), '--validate'
) -Wait -PassThru
if ($valProc.ExitCode) { throw 'AgentDeck.exe --validate failed.' }

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw 'Inno Setup ISCC.exe not found.' }

Write-Output "Building AgentDeck installer v$Version using $iscc..."
& $iscc "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot 'AgentDeck.iss')
if ($LASTEXITCODE) { throw 'ISCC.exe failed.' }
$setupOut = Join-Path $root "dist\AgentDeck-Setup-$Version.exe"
Write-Output "Built installer: $setupOut ($((Get-Item $setupOut).Length) bytes)"
