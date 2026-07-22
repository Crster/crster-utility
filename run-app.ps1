param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "App\App.csproj"
$packageName = "76371500-163b-4c74-974d-f79d90df175c"
$buildOutputPath = Join-Path $PSScriptRoot "App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$appxManifestPath = Join-Path $buildOutputPath "AppxManifest.xml"
$appxPath = Split-Path $appxManifestPath

Get-Process "CrsterUtility.App" -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet clean $projectPath --configuration $Configuration --property:Platform=x64
dotnet build $projectPath --configuration $Configuration --property:Platform=x64 --no-incremental

# The loose-package manifest references Assets\..., but the standard build output omits them.
# Copy the project's existing package assets beside the generated manifest for package activation.
$sourceAssetsPath = Join-Path $PSScriptRoot "App\Assets"
Copy-Item -LiteralPath $sourceAssetsPath -Destination (Join-Path $buildOutputPath "Assets") -Recurse -Force

if (-not (Test-Path $appxManifestPath)) {
    throw "The AppX manifest was not produced at: $appxManifestPath"
}

$expectedInstallPath = (Resolve-Path $appxPath).Path.TrimEnd('\')
$package = Get-AppxPackage -Name $packageName | Where-Object {
    $_.InstallLocation.TrimEnd('\') -eq $expectedInstallPath
} | Select-Object -First 1

if (-not $package) {
    Add-AppxPackage -Register $appxManifestPath -ForceApplicationShutdown
    $package = Get-AppxPackage -Name $packageName | Where-Object {
        $_.InstallLocation.TrimEnd('\') -eq $expectedInstallPath
    } | Select-Object -First 1
}

if (-not $package) {
    throw "The app package was not registered from the current build output: $expectedInstallPath"
}

$appActivationId = "shell:AppsFolder\$($package.PackageFamilyName)!App"
Write-Host "Launching registered workspace package: $expectedInstallPath"
Start-Process "explorer.exe" -ArgumentList $appActivationId
