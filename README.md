# Crster Utility

Crster Utility is a native Windows desktop toolbox for everyday productivity, capture, local data, and AI-assisted work. It is built with WinUI 3 and .NET 10 and runs as a packaged Windows App SDK application.

## What it does

The application is organized into the following pages:

- **Chat** — Chat with the Secretary, Smart, or Technician personality. Conversations can use local notebook and todo data, configurable tools, weather/location context, and the configured OpenAI-compatible provider.
- **Cody** — An agentic coding workspace with a file tree, Monaco editor, terminal, workspace context, attachments, and an optional Cody chat panel. Operations that affect the local workspace are surfaced for approval where appropriate.
- **Notebook** — Store searchable Markdown notes, attach files and images, paste clipboard content, use formatting helpers, generate secrets/passwords, and ask the configured AI provider to improve selected writing. Notebook search asks the low-cost model for keyword patterns and matches them locally.
- **Todos** — Create, search, categorize, complete, and schedule tasks.
- **Snapshots** — Capture the screen from the page or a global shortcut, optionally including the mouse pointer. Captured images can be edited, annotated, copied, saved, and processed with AI-assisted actions.
- **Recordings** — Record the screen to MP4 with optional microphone input and system audio. A recording toolbar remains available while the main window is hidden.
- **Tools** — Run **Caffeine**, which keeps the computer active by periodically moving the pointer, scrolling, or switching tabs. Moving the pointer at least 100 pixels from its last automated position stops it.

The window can be minimized to the Windows notification area. Optional startup registration and global shortcuts are controlled in Settings. The package also exposes a Copilot-key app extension when registered by Windows.

## Requirements

- Windows 11 version 22H2 (build 22621) or later
- .NET 10 SDK
- A Windows App SDK/WinUI 3-capable Visual Studio installation, or the equivalent .NET CLI build environment
- A microphone only when microphone recording is required
- Graphics-capture and microphone permissions for the corresponding capture features

The project targets `net10.0-windows10.0.26100.0`, has a minimum platform version of `10.0.22621.0`, and defines `x86`, `x64`, and `ARM64` platforms. The local launch script uses `x64`.

## First-run setup

On first launch, choose a writable database folder and configure an OpenAI-compatible provider:

1. Enter the provider base URL, including the API version path when required (for example, `https://api.openai.com/v1`). Only `http` and `https` URLs are accepted.
2. Enter the provider API key.
3. After the provider is reachable, select the available models for low-cost requests and high-cost requests.

The same settings are available later from **Settings**. Changing the provider clears the selected models. AI features are optional, but Chat, Cody, AI writing assistance, keyword search, and some tools require a working provider and suitable model configuration.

### Privacy and stored data

The application sends prompts, selected files/images, and tool context to the OpenAI-compatible endpoint configured by the user. Review that provider's retention and privacy policy before using private content.

The configured database folder contains `CrsterUtility.db`, a LiteDB database containing application settings, notes, todos, attachments, chat state, and related local data. Changing the folder copies the database and leaves the original as a backup. The bootstrap file at `%USERPROFILE%\crster\utility\setting.ini` contains the database path, provider URL, and API key needed to open the database; protect this file and do not commit or share it.

## Build

From the repository root, restore and build a selected platform:

```powershell
dotnet restore .\App\App.csproj
dotnet build .\App\App.csproj --configuration Debug --property:Platform=x64
```

The solution can also be built with:

```powershell
dotnet build .\CrsterUtility.slnx --configuration Debug --property:Platform=x64
```

Build another supported architecture by changing the `Platform` property to `x86` or `ARM64`.

## Run locally

`run-app.ps1` is the supported local x64 workflow. It stops an existing app process, cleans and rebuilds the project, copies package assets beside the generated manifest, registers the loose MSIX package, and launches it:

```powershell
.\run-app.ps1
.\run-app.ps1 -Configuration Release
```

The script requires permission to register an AppX package. It intentionally targets the output at `App\bin\x64\<Configuration>\...\win-x64` and does not launch ARM64 or x86 builds.

## Package and publish

The project uses single-project MSIX tooling. Publish profiles are provided for self-contained `win-x86`, `win-x64`, and `win-arm64` builds:

```powershell
dotnet publish .\App\App.csproj --configuration Release --property:Platform=x64 --runtime win-x64
```

MSIX bundle configuration currently covers `x86` and `x64`; the ARM64 publish profile is available for an architecture-specific package. Production package artifacts are written below `Installer\CrsterUtility`. App Installer metadata is generated only when a production `AppInstallerUri` is supplied to the build. The manifest version in this source tree is `1.0.28.0`.

## Repository layout

| Path | Purpose |
| --- | --- |
| `App/Pages` | Main feature pages and page workflows |
| `App/Controls` | Reusable UI controls, including Cody chat and Monaco integration |
| `App/Services` | AI clients, persistence, capture, recording, input, startup, tray, and utility services |
| `App/Models` | Domain, settings, attachment, chat, and persistence models |
| `App/Windows` | Main window, first-run setup, snapshot editor, and recording toolbar windows |
| `App/Assets/Monaco` | Bundled Monaco editor runtime assets |
| `App/Properties/PublishProfiles` | `win-x86`, `win-x64`, and `win-arm64` publish profiles |
| `Installer` | Packaging and generated installer output |
| `run-app.ps1` | Clean, build, register, and launch script for local x64 development |
| `CrsterUtility.slnx` | Solution entry point |

## Main dependencies

The application uses Microsoft Windows App SDK/WinUI 3, Windows SDK build tools, Win2D, Easy Windows Terminal Control, LiteDB, NAudio, the OpenAI .NET client, Markdig, Cronos, Microsoft.CodeAnalysis.CSharp, Vortice.Direct3D11, and Windows Runtime interop libraries.

There is currently no separate automated test project in the repository. A successful `dotnet build` is the baseline validation for changes; capture, recording, provider, and packaged-launch behavior should also be checked manually on Windows.

## Attribution

The window icon is attributed to [Smashicons on Flaticon](https://www.flaticon.com/free-icons/robot).
