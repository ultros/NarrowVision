[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$IdentityName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Publisher,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublisherDisplayName,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'),
    [string]$MakeAppxPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (!$MakeAppxPath) {
    $sdkRoot = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Windows Kits\10\bin'
    $MakeAppxPath = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if (!$MakeAppxPath) { throw 'Install the Windows SDK with MakeAppx.exe, or pass MakeAppxPath.' }

$payload = Join-Path $OutputDirectory 'msix-payload'
& (Join-Path $PSScriptRoot 'Publish-Payload.ps1') -OutputDirectory $payload
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'store/assets') -Filter '*.png' |
    Copy-Item -Destination $assets

[xml]$project = Get-Content -LiteralPath (Join-Path $root 'NovusVision.csproj') -Raw
$version = [version]$project.Project.PropertyGroup.Version
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'store/AppxManifest.xml') -Raw
$manifest.Package.Identity.Name = $IdentityName
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Identity.Version = "$($version.ToString(3)).0"
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Save((Join-Path $payload 'AppxManifest.xml'))

$package = Join-Path $OutputDirectory 'NovusVision-win-x64.msix'
& $MakeAppxPath pack /d $payload /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx could not build the MSIX.' }
Get-Item -LiteralPath $package
