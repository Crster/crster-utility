# Crster Utility

Crster Utility is a native Windows desktop utility suite built with WinUI 3 and .NET 10. It combines everyday productivity tools, screen capture, media recording, local notes and todos, and AI-assisted workflows in one application.

## Features

- **Chat** — Use the Secretary, Smart, and Technician AI personalities for conversation, memory, notebook and todo actions, web-assisted answers, and guided troubleshooting.
- **Cody** — Work with an agentic coding workspace that can inspect project files, use a terminal, and perform approved local operations.
- **Notebook** — Create notes with attachments, Markdown editing, AI-assisted writing, and generated passwords or keys.
- **Todos** — Organize, search, categorize, complete, and schedule todo items.
- **Snapshots** — Capture and edit screenshots using a configurable global shortcut, with optional mouse-cursor capture.
- **Recordings** — Record the screen with optional microphone and system-audio tracks.
- **Artist** — Generate and edit images through the configured AI provider.
- **Caffeine** — Keep the computer active when needed.
- **Settings and tray support** — Configure the database location, AI provider, models, location, shortcuts, microphone, and startup behavior. The app can remain available from the Windows notification area.

## Requirements

- Windows 11, build 22621 or later
- .NET 10 SDK
- Visual Studio with WinUI 3 / Windows App SDK support, or the matching .NET CLI workload
- A microphone is only required for microphone-enabled recordings.

The application targets `net10.0-windows10.0.26100.0` and supports `x86`, `x64`, and `ARM64` builds. The minimum supported Windows platform version is `10.0.22621.0`.

## AI provider setup

Crster Utility uses an OpenAI-compatible API. On first use, open **Settings** and configure:

1. The provider base URL.
2. The API key.
3. The embedding, low-cost, high-cost, and Artist models available to that provider.

The API key is entered through the app's password field and should not be committed to source control. AI features that require a provider include Chat, Cody, Artist, AI-assisted notebook actions, semantic notebook search, and some Secretary tools.

## Build and run

Restore and build the app for the desired platform:

```powershell
dotnet restore .\App\App.csproj
dotnet build .\App\App.csproj --configuration Debug --property:Platform=x64
```

For the supported local x64 workflow, `run-app.ps1` cleans and builds the project, copies package assets into the loose-package output, registers the generated package, and launches it:

```powershell
.\run-app.ps1
.\run-app.ps1 -Configuration Release
```

The script targets the `x64` platform and requires permission to register an AppX package. To build another architecture, use the project or Visual Studio packaging workflow directly.

## Packaging

The project uses single-project MSIX tooling. Packaging is configured for `x86` and `x64` bundles, with publish profiles for `win-x86`, `win-x64`, and `win-arm64`. App Installer metadata is generated only when a production HTTP or HTTPS `AppInstallerUri` is supplied to the build.

The current manifest version is `1.0.27.0`. Production package output is written to `Installer\CrsterUtility` with the stable `CrsterUtility` artifact name.

## Project layout

| Path | Purpose |
| --- | --- |
| `App/Pages` | Main application pages and workflows |
| `App/Services` | AI, storage, capture, recording, input, and utility services |
| `App/Models` | Application and persistence models |
| `App/Assets/Monaco` | Bundled Monaco editor assets used by Notebook and Cody |
| `App/Properties/PublishProfiles` | Architecture-specific publish profiles |
| `Installer` | App Installer and generated package artifacts |
| `run-app.ps1` | Build, register, and launch the local x64 package |

## Dependencies

The application uses Microsoft Windows App SDK, Win2D, Easy Windows Terminal Control, LiteDB, NAudio, OpenAI, Markdig, Cronos, Microsoft.CodeAnalysis.CSharp, Vortice.Direct3D11, and related Windows SDK packages.

## Attribution

The window icon is attributed to [Smashicons on Flaticon](https://www.flaticon.com/free-icons/robot).
