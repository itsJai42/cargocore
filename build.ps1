param([string]$DeployTo)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
foreach ($project in @('Client/jai-trucking.Client.csproj','Server/jai-trucking.Server.csproj')) {
    dotnet publish $project -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project. Deployment left untouched." }
}
$stage = Join-Path $PSScriptRoot 'package-stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath 'fxmanifest.lua','trailer.cfg','route.cfg' -Destination $stage -Force
foreach ($part in @('Client','Server')) {
    $framework = if ($part -eq 'Client') { 'net452' } else { 'netstandard2.0' }
    $destination = Join-Path $stage "$part/bin/Release/$framework/publish"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path "$part/bin/Release/$framework/publish/*.dll" -Destination $destination -Force
}
# Avoid deleting dist: an existing server resource junction points here.
New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
Copy-Item -Path "$stage/*" -Destination 'dist' -Recurse -Force
$oldSymbols = Join-Path $PSScriptRoot 'dist/Server/bin/Release/netstandard2.0/publish/jai-trucking.Server.net.pdb'
if (Test-Path -LiteralPath $oldSymbols) { Remove-Item -LiteralPath $oldSymbols -Force }
if ($DeployTo) {
    New-Item -ItemType Directory -Path $DeployTo -Force | Out-Null
    Copy-Item -Path 'dist/*' -Destination $DeployTo -Recurse -Force
}
Write-Host 'Build and package complete. Run refresh then ensure jai-trucking in FXServer.'

