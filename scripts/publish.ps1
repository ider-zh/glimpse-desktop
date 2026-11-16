[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$outputPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'portable'))
if (-not $outputPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish destination must be within the repository artifacts folder.'
}
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
    $zipPath = Join-Path $artifactRoot 'Glimpse-SessionReader-win-x64.zip'
    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -Force
    Write-Output "Portable folder: $outputPath"
    Write-Output "Portable archive: $zipPath"
}
finally { Pop-Location }
