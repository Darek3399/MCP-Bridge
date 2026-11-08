# Building MCP Bridge

## Repository layout

- Dependencies/README.md documents the required local dependency files.
- Dependencies/ contains local build dependencies. Git tracks only its README.
- Work/Build/bin/ and Work/Build/obj/ contain build output and intermediates.
- Work/Build/publish/win-x64/ contains the published application.
- Work/Build/installer/ contains the release installer.
- Work/Diagnostics/ contains local diagnostic reports.
- Work/Temp/ contains temporary files.

A clean clone does not contain the dependency binaries. Populate Dependencies according to Dependencies/README.md before building the offline installer.

## Build the application

Use Rider or run:

    dotnet build MCPBridge.sln --configuration Release

Build output is redirected to Work/Build.

## Build the complete offline installer

Run:

    .\scripts\Build-Installer.ps1

The resulting installer is:

    Work\Build\installer\MCPBridge-Setup-<version>.exe

All runtime dependencies remain embedded in the installer. They are not committed to GitHub.