# Contributing

Thanks for taking an interest in MCP Bridge.

The project is maintained in spare time. Contributions should be focused, practical, and easy to review.

## Before opening a pull request

1. Check existing issues to avoid duplicating work.
2. For a substantial change, open an issue first and briefly describe the problem and proposed approach.
3. Keep changes scoped. Avoid unrelated formatting or refactoring in the same pull request.
4. Do not include personal configuration, access tokens, tunnel IDs, logs containing private paths, or bundled third-party binaries.

## Build

On Windows, with the .NET SDK installed:

\`\`\`powershell
dotnet restore MCPBridge.sln
dotnet build MCPBridge.sln --configuration Release
\`\`\`

The application targets .NET Framework 4.8 and x64. Full installer builds have additional payload and Inno Setup requirements; see the installer documentation.

## Pull request checklist

- [ ] The change has a clear purpose and description.
- [ ] The solution builds successfully.
- [ ] User-facing text is updated in both English and Russian where applicable.
- [ ] No secrets, personal paths, generated output, or unrelated binaries are included.
- [ ] Any security impact is described.

Before submitting, make sure you have the right to contribute the code and are comfortable with it being distributed under the project's selected license once published. If you are unsure, discuss it before submitting.