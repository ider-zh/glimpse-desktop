[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$OutputName = 'portable')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$outputPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot $OutputName))
if (-not $outputPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish destination must be within the repository artifacts folder.'
}
$running = @(Get-Process -Name 'Glimpse.UI','Glimpse.Worker' -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($outputPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw 'Publish destination is running. Close that build or choose a different -OutputName before publishing.' }
if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $repoRoot
try {
    Invoke-Dotnet publish src/Glimpse.UI -c $Configuration -r win-x64 --self-contained true -o $outputPath --nologo
    Invoke-Dotnet publish src/Glimpse.Worker -c $Configuration -r win-x64 --self-contained true -o (Join-Path $outputPath 'Worker') --nologo
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/USER_GUIDE.md') -Destination (Join-Path $outputPath '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/DEPENDENCIES.md') -Destination $outputPath
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'Worker/Glimpse.Worker.exe'))) { throw 'Worker missing from portable package.' }
    $zipPath = Join-Path $artifactRoot 'Glimpse-Desktop-win-x64.zip'
    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -Force
    Copy-Item -LiteralPath $zipPath -Destination (Join-Path $artifactRoot 'Glimpse-SessionReader-win-x64.zip') -Force
    Write-Output "Portable folder: $outputPath"
    Write-Output "Portable archive: $zipPath"
}
finally { Pop-Location }
