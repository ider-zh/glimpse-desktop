[CmdletBinding()]
param([switch]$InteractiveWindows)
$ErrorActionPreference = 'Stop'
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location (Join-Path $PSScriptRoot '..')
try {
    Invoke-Dotnet build Glimpse.slnx -c Release --nologo
    Invoke-Dotnet test tests/Glimpse.Tests -c Release --no-build --nologo
    Invoke-Dotnet run --project tools/Glimpse.Benchmarks -c Release --no-build
    $env:DOTNET_ROOT = Split-Path (Get-Command dotnet).Source
    $workerPath = Join-Path $PWD 'src/Glimpse.Worker/bin/Release/net10.0-windows10.0.19041.0/Glimpse.Worker.exe'
    Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --ipc $workerPath
    Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --worker-tests
    Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --provider-tests
    Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --login-terminal-tests
    Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --agent-tests
    if ($InteractiveWindows) {
        Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build
        Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --reader
        Invoke-Dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --ocr-corpus
    }
}
finally { Pop-Location }
