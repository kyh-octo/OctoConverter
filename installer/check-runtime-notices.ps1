[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Get-Content -LiteralPath (Join-Path $PublishDirectory 'OctoConverter.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($runtime.runtimeOptions.includedFrameworks)
if ($frameworks.Count -ne 2 -or @($frameworks | Where-Object { $_.name -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') -or $_.version -ne '10.0.11' }).Count) {
    throw 'Bundled runtime changed. Update version-specific original license notices and this release check before packaging.'
}
$required = @('LICENSE', 'THIRD-PARTY-NOTICES.md') + @(
    'Microsoft.NETCore.App-LICENSE.TXT', 'Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT',
    'Microsoft.WindowsDesktop.App-LICENSE', 'wpf-LICENSE.TXT', 'wpf-THIRD-PARTY-NOTICES.TXT',
    'winforms-LICENSE.TXT', 'winforms-THIRD-PARTY-NOTICES.TXT'
) | ForEach-Object {
    if ($_ -in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) { $_ }
    else { 'Licenses\dotnet-10.0.11\' + $_ }
}
foreach ($relative in $required) {
    $source = Join-Path $root $relative
    $published = Join-Path $PublishDirectory $relative
    if (-not (Test-Path -LiteralPath $published -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) {
        throw "Required original license notice missing or modified: $relative"
    }
}
Write-Host 'Verified bundled .NET 10.0.11 original license notices.'
