[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'),
    [string]$SignToolPath,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if ([bool]$SignToolPath -ne [bool]$SigningCertificateThumbprint) {
    throw 'Provide both SignToolPath and SigningCertificateThumbprint for a signed MSI.'
}

$root = Split-Path -Parent $PSScriptRoot
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$payload = Join-Path $OutputDirectory 'msi-payload'
& (Join-Path $PSScriptRoot 'Publish-Payload.ps1') -OutputDirectory $payload -SingleFile

function Sign-ReleaseFile([string]$Path) {
    if (!$SignToolPath) { return }
    & $SignToolPath sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path." }
    & $SignToolPath verify /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $Path." }
}
Sign-ReleaseFile (Join-Path $payload 'NovusVision.exe')

$license = 'Copyright 2026 Novus Mercatura. CC BY 4.0.' + [Environment]::NewLine + [Environment]::NewLine +
    (Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw)
$license = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$license = [regex]::Replace($license, '[^\u0000-\u007F]', { param($m) '\u' + [int][char]$m.Value + '?' })
$license = [regex]::Replace($license, '\r?\n', '\par ')
'{\rtf1\ansi{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $license + '}' |
    Set-Content -LiteralPath (Join-Path $payload 'License.rtf') -Encoding ascii

[xml]$project = Get-Content -LiteralPath (Join-Path $root 'NovusVision.csproj') -Raw
$version = [version]$project.Project.PropertyGroup.Version
$msi = Join-Path $OutputDirectory 'NovusVision-win-x64.msi'
Push-Location (Join-Path $PSScriptRoot 'installer')
try {
    dotnet build NovusVision.wixproj -c Release "-p:PayloadDir=$payload" "-p:InstallerVersion=$($version.ToString(3))" "-p:OutputPath=$OutputDirectory"
    if ($LASTEXITCODE -ne 0) { throw 'WiX MSI build failed.' }
} finally {
    Pop-Location
}

Move-Item -LiteralPath (Join-Path $OutputDirectory 'NovusVision.msi') -Destination $msi -Force
Sign-ReleaseFile $msi
Get-Item -LiteralPath $msi
