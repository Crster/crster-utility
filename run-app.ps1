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

# Stop every running instance of the app so the package can be re-registered cleanly.
Get-Process -Name "CrsterUtility.App" -ErrorAction SilentlyContinue | Stop-Process -Force

# Give the OS a moment to release the executable handle, then verify it is gone.
Start-Sleep -Milliseconds 500
$stillRunning = Get-Process -Name "CrsterUtility.App" -ErrorAction SilentlyContinue
if ($stillRunning) {
    $stillRunning | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (Get-Process -Name "CrsterUtility.App" -ErrorAction SilentlyContinue) {
        throw "The app process is still running and could not be terminated."
    }
}

dotnet clean $projectPath --configuration $Configuration --property:Platform=x64 | Out-Null
dotnet build $projectPath --configuration $Configuration --property:Platform=x64 --no-incremental
if ($LASTEXITCODE -ne 0) {
    throw "The build failed with exit code $LASTEXITCODE."
}

# The loose-package manifest references Assets\..., but the standard build output omits them.
# Copy the project's existing package assets beside the generated manifest for package activation.
if (-not (Test-Path $buildOutputPath)) {
    throw "The build output folder was not produced: $buildOutputPath"
}
$sourceAssetsPath = Join-Path $PSScriptRoot "App\Assets"
Copy-Item -LiteralPath $sourceAssetsPath -Destination (Join-Path $buildOutputPath "Assets") -Recurse -Force

if (-not (Test-Path $appxManifestPath)) {
    throw "The AppX manifest was not produced at: $appxManifestPath"
}

$expectedInstallPath = (Resolve-Path $appxPath).Path.TrimEnd('\')

# Always re-register from the current build output so the running package matches the latest build.
try {
    Add-AppxPackage -Path $appxManifestPath -Register -ForceApplicationShutdown -ErrorAction Stop
}
catch {
    throw "Registering the app package failed: $($_.Exception.Message)"
}

$package = Get-AppxPackage -Name $packageName | Where-Object {
    $_.InstallLocation.TrimEnd('\') -eq $expectedInstallPath
} | Select-Object -First 1

if (-not $package) {
    throw "The app package was not registered from the current build output: $expectedInstallPath"
}

$appActivationId = "shell:AppsFolder\$($package.PackageFamilyName)!App"
Write-Host "Launching registered workspace package: $expectedInstallPath"
Start-Process -FilePath $appActivationId
