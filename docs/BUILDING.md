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

## Product version

The single source of truth for the MCP Bridge product version is the `<Version>` property in the repository-root `Directory.Build.props`.

Change that value once when preparing a release. MSBuild uses it for the application's generated assembly version metadata, the application displays the informational assembly version, and `Build-Installer.ps1` passes the same value to Inno Setup for the installer metadata and output filename.

Do not add a separate product version to `McpBridge.csproj` or `installer/MCPBridge.iss`. The fixed `1.0.0.0` in `src/McpBridge/app.manifest` is a Windows manifest identity, not the product release version.