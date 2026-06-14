# Agent Guide: CrsterUtility

This guide provides essential context for AI agents working on the `CrsterUtility` codebase.

## Project Overview
`CrsterUtility` is a Windows desktop application built using **WinUI 3** and **.NET 8**. It leverages the **Windows App SDK** to provide a native Windows experience.

## Technical Stack
- **Framework**: .NET 10.0
- **UI Framework**: WinUI 3 (`Microsoft.WindowsAppSDK` 2.1.3)
- **Target Platform**: Windows 10 (Min version 10.0.17763.0, Target 10.0.20348.0)
- **Project Type**: WinExe (Single-project MSIX Packaging)

## Project Structure
- `/App`: The main application project containing:
    - `App.xaml` & `App.xaml.cs`: Application-wide resources and lifecycle management.
    - `MainWindow.xaml` & `MainWindow.xaml.cs`: The primary application window.
    - `Assets/`: Application icons, splash screens, and logos.
    - `Package.appxmanifest`: MSIX packaging configuration.
    - `app.manifest`: Application manifest for OS integration.
    - `Properties/PublishProfiles/`: Configuration for targeting x86, x64, and ARM64 architectures.

## Build and Development
### Target Architectures
The project is configured for multiple architectures:
- `win-x86`
- `win-x64`
- `win-arm64`

### Build Commands
Since this is a .NET project, use the standard .NET CLI:
- **Build**: `dotnet build App/App.csproj`
- **Restore**: `dotnet restore App/App.csproj`

### Publishing
Publish profiles are located in `App/Properties/PublishProfiles/`. The project uses `PublishReadyToRun` and `PublishTrimmed` for non-debug configurations to optimize performance and size.

## Conventions & Patterns
- **UI Pattern**: Follows the standard WinUI 3 pattern where XAML files define the layout and `.xaml.cs` (code-behind) files handle the logic.
- **Naming**: Uses standard C# / .NET naming conventions (PascalCase for classes and methods).
- **Resource Management**: Application assets are managed in the `Assets` folder and declared in the `.csproj`.

## Gotchas & Notes
- **WinUI 3 Specifics**: Be aware that WinUI 3 components reside in the `Microsoft.UI.Xaml` namespace, not `System.Windows` (WPF) or `Windows.UI.Xaml` (UWP).
- **MSIX Packaging**: The project uses `EnableMsixTooling`, meaning it is designed to be packaged as an MSIX app. Changes to permissions or identity should be made in `Package.appxmanifest`.
- **Architectures**: Ensure the correct runtime identifier (`-r`) is used when building or publishing for specific hardware.
