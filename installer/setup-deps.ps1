param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'
$reqFile = Join-Path $InstallDir 'host\requirements.txt'
$venvDir = Join-Path $InstallDir '.venv'
$venvPy = Join-Path $venvDir 'Scripts\python.exe'
$logFile = Join-Path $env:TEMP 'agentdeck-setup-deps.log'

function Find-SystemPython {
    $candidates = @()
    $cmd = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $localPy = Join-Path $env:LOCALAPPDATA 'Programs\Python'
    if (Test-Path $localPy) {
        $candidates += (Get-ChildItem $localPy -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | ForEach-Object { $_.FullName })
    }
    $pfPy = Join-Path $env:ProgramFiles 'Python*'
    $candidates += (Get-ChildItem $pfPy -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | ForEach-Object { $_.FullName })

    foreach ($exe in $candidates) {
        if (-not (Test-Path -LiteralPath $exe)) { continue }
        try {
            $ver = & $exe -c "import sys; print(f'{sys.version_info[0]}.{sys.version_info[1]}')" 2>$null
            if ($ver -match '^3\.(1[1-9]|[2-9]\d)$') {
                return $exe
            }
        } catch {}
    }
    return $null
}

try {
    $sysPy = Find-SystemPython
    if (-not $sysPy) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) {
            throw 'Python 3.11+ is required and winget was not found.'
        }
        "Installing Python 3.12 via winget..." | Out-File -FilePath $logFile -Encoding utf8
        & $winget.Source install --id Python.Python.3.12 -e --silent --accept-package-agreements --accept-source-agreements *>> $logFile
        $sysPy = Find-SystemPython
        if (-not $sysPy) {
            throw 'Python 3.12 installation did not complete. Install Python 3.12 manually and re-run.'
        }
    }

    if (-not (Test-Path -LiteralPath $venvPy)) {
        & $sysPy -m venv $venvDir *>> $logFile
    }
    if (Test-Path -LiteralPath $reqFile) {
        & $venvPy -m pip install --upgrade --no-warn-script-location -r $reqFile *>> $logFile
    }
}
catch {
    $_ | Out-File -FilePath $logFile -Append -Encoding utf8
    exit 1
}
