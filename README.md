# WindowMenu

WindowMenu is a Windows system-tray utility that opens an action menu for a
window when you Alt+right-click its top-right corner.

## Requirements

- Windows 10 or newer
- .NET 8 SDK

## Build and run

From this folder, run:

```powershell
dotnet run --project .\WindowMenu.csproj
```

Or build it first:

```powershell
dotnet build .\WindowMenu.csproj --configuration Release
```

NuGet dependencies are restored by the .NET SDK during the build. Some actions
involving protected processes may require running the application as
Administrator; the application does not need elevation for normal use.

## Features

- Window positioning, visibility, priority, title, audio, and process actions
- aim bot in multiplayer powerpoint, Word, and Excel lobbies
- Window locking, focus, and on-close actions
- Process network and performance views
- infinite ammo (bottomless clip)
- Optional integrations with tools installed separately
- god mode (only working in calculator and paint)

The application is Windows-specific and uses Windows Forms and Windows APIs.
