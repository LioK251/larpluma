param([string]$Executable = 'dist/Larpluma.exe', [switch]$CreamInstaller)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$smokeData = Join-Path $projectRoot ('artifacts/package-smoke-' + [Guid]::NewGuid().ToString('N'))
$previousData = $env:LARPLUMA_DATA_DIR
$smokeProcess = $null
try {
    $env:LARPLUMA_DATA_DIR = $smokeData
    $exe = if ([IO.Path]::IsPathRooted($Executable)) { $Executable } else { Join-Path $projectRoot $Executable }
    if ($CreamInstaller) {
        $fixtureSteam = Join-Path $smokeData 'Steam'
        New-Item -ItemType Directory -Path $fixtureSteam -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $fixtureSteam 'Steam.exe') -Value 'Preview fixture; never executed.'
        @{ UnlockMethod = 1; SteamUnlocker = 0; SteamPath = $fixtureSteam } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $smokeData 'config.json') -Encoding UTF8
    }
    $smokeProcess = Start-Process -FilePath $exe -ArgumentList '--preview','--smoke' -WindowStyle Hidden -PassThru
    if (-not $smokeProcess.WaitForExit(25000)) { throw 'Packaged startup timed out.' }
    if ($smokeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $smokeData 'smoke-pass.txt'))) {
        throw "Packaged startup failed; inspect $smokeData"
    }
    Write-Output 'PASS: self-contained Larpluma.exe startup, window, and embedded resources.'
} finally {
    $env:LARPLUMA_DATA_DIR = $previousData
    if ($smokeProcess) {
        if (-not $smokeProcess.HasExited) { Stop-Process -Id $smokeProcess.Id }
        $smokeProcess.Dispose()
    }
}
