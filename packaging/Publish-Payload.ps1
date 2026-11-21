[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$bundle = $SingleFile.IsPresent.ToString().ToLowerInvariant()
$arguments = @(
    'publish', (Join-Path $root 'NovusVision.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', "-p:PublishSingleFile=$bundle",
    "-p:IncludeNativeLibrariesForSelfExtract=$bundle", '-p:DebugType=None', '-o', $OutputDirectory
)
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'NovusVision publish failed.' }

Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
$assets = Get-Content -LiteralPath (Join-Path $root 'obj/project.assets.json') -Raw | ConvertFrom-Json
$framework = $assets.project.frameworks.PSObject.Properties.Value | Select-Object -First 1
$packageCache = $assets.project.restore.packagesPath
$notices = Join-Path $OutputDirectory 'notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($package in @(
    @{ Name = 'Microsoft.NETCore.App.Runtime.win-x64'; License = 'LICENSE.TXT'; Output = 'dotnet-runtime-license.txt' },
    @{ Name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; License = 'LICENSE'; Output = 'dotnet-desktop-license.txt' }
)) {
    $download = $framework.downloadDependencies | Where-Object name -EQ $package.Name
    $version = (($download.version -split ',')[0]).TrimStart('[').Trim()
    $directory = Join-Path $packageCache (Join-Path $package.Name.ToLowerInvariant() $version)
    Copy-Item -LiteralPath (Join-Path $directory $package.License) -Destination (Join-Path $notices $package.Output)
    $thirdPartyFile = Join-Path $directory 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $thirdPartyFile) {
        Copy-Item -LiteralPath $thirdPartyFile -Destination (Join-Path $notices ($package.Output.Replace('-license', '-notices')))
    }
}
