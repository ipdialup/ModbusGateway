<#
  Builds the release package: runs the tests, publishes the service and tray app as self-contained
  single-file win-x64 exes, and zips them with the install scripts and INSTALL.md.

    .\build-release.ps1                   # version taken from the Service .csproj
    .\build-release.ps1 -Version 1.0.1    # overrides the version stamped into the exes

  Output: dist\ModbusGateway-v<version>-win-x64.zip
#>
param([string]$Version, [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content "$root\src\ModbusGateway.Service\ModbusGateway.Service.csproj")).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "No version found; pass -Version" }

$name = "ModbusGateway-v$Version-win-x64"
$stage = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"
Remove-Item $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

if (-not $SkipTests) {
    dotnet test "$root\ModbusGateway.sln" -c Release
    if ($LASTEXITCODE) { throw "Tests failed" }
}
foreach ($p in "ModbusGateway.Service", "ModbusGateway.Tray") {
    dotnet publish "$root\src\$p" -c Release -o $stage -p:Version=$Version
    if ($LASTEXITCODE) { throw "Publish of $p failed" }
}
Get-ChildItem $stage -Filter *.pdb | Remove-Item
Copy-Item "$root\install\install.ps1", "$root\install\uninstall.ps1", "$root\install\INSTALL.md" $stage

Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "Built $zip"
Get-ChildItem $stage | Format-Table Name, Length -AutoSize
