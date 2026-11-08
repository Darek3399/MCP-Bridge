# Installer payload

This directory intentionally contains only this README. Runtime packages and third-party installers are kept locally in the ignored Dependencies directory and are never committed to Git.

## Local dependency source

The full installer build expects the following layout:

    Work/
    └── Dependencies/
        ├── FilesystemMcp/
        │   ├── package.json
        │   ├── dist/
        │   └── node_modules/
        ├── FilesystemMcp-engine.json
        ├── supergateway.exe
        ├── uv.exe
        ├── uvx.exe
        ├── node-v26.13.0-x64.msi
        └── tailscale-setup.msi

Run scripts/Build-Installer.ps1 from the repository root. The build validates these inputs and Inno Setup embeds them directly into the release installer. No intermediate payload copy is created under installer/.

The generated offline installer contains the required runtime packages and installers, while the individual third-party files remain outside the Git repository.
