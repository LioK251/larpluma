param([switch]$Check, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    if ($Check) {
        & $dotnet run --project Checks/Larpluma.Checks.csproj -- --output=artifacts/build-checks
        if ($LASTEXITCODE) { throw 'Checks failed.' }
    }
    & $dotnet restore GreenLuma-Manager.csproj -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    & $dotnet publish GreenLuma-Manager.csproj --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    $executable = Join-Path $OutputDirectory 'Larpluma.exe'
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'GreenLuma-Manager.exe') -Destination $executable -Force
    Remove-Item -LiteralPath (Join-Path $OutputDirectory 'GreenLuma-Manager.exe')
    Copy-Item -LiteralPath 'LICENSE','README.md' -Destination $OutputDirectory -Force
    if ($Check) {
        & (Join-Path $PSScriptRoot 'smoke.ps1') -Executable $executable
        & (Join-Path $PSScriptRoot 'smoke.ps1') -Executable $executable -CreamInstaller
    }
    Get-FileHash -LiteralPath $executable -Algorithm SHA256 | Format-List
} finally { Pop-Location }
