# MCP Bridge Dependencies

This directory is part of the public repository structure.

Only this README.md file is committed to GitHub. All dependency binaries, MSI installers and the FilesystemMcp package remain local and are excluded by the root .gitignore.

## Required local files

Place the following files in this directory:

    Dependencies/
    |-- FilesystemMcp/
    |   |-- package.json
    |   |-- dist/
    |   `-- node_modules/
    |-- supergateway.exe
    |-- uv.exe
    |-- uvx.exe
    |-- node-v26.13.0-x64.msi
    `-- tailscale-setup.msi

FilesystemMcp must contain its built dist directory and production node_modules.

FilesystemMcp-engine.json is generated automatically during dependency validation.

## Important

- Keep the filenames unchanged.
- Do not commit executable files or MSI installers.
- Do not upload dependency binaries to GitHub.
- The complete dependency bundle is still embedded in the release installer.

## Build

Run this command from the repository root:

    .\scripts\Build-Installer.ps1

The installer is created under Work\Build\installer.