# Memory

## Project Overview
See @README.md for project overview and @package.json for available npm/pnpm commands for this project.

## Code Style Guidelines
- Use descriptive variable names
- Follow existing patterns in the codebase
- Extract complex conditions into meaningful boolean variables

## Architecture Notes
Add important architectural decisions and patterns here.

## Common Workflows

### GitHub Issues
When handling a GitHub issue, do not work directly on `main`. Create a dedicated branch and an isolated working directory (worktree) for the issue and do the work there. Do not open a PR; when the work is done, ask whether to merge the branch to `main` directly.

### Build with Docker
There is no local dotnet SDK — all builds and tests run in the official SDK container. `--user` keeps build artifacts owned by the host user, and the mounted `.nuget-cache` persists packages between runs.

```bash
# Build the full solution (Debug)
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp/dh -e DOTNET_CLI_HOME=/tmp/dh \
  -e NUGET_PACKAGES=/nuget -e DOTNET_NOLOGO=1 \
  -v "$PWD":/src -v "$PWD/.nuget-cache":/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build OpcBridge.sln -c Debug --nologo

# Run the test suite
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp/dh -e DOTNET_CLI_HOME=/tmp/dh \
  -e NUGET_PACKAGES=/nuget -e DOTNET_NOLOGO=1 \
  -v "$PWD":/src -v "$PWD/.nuget-cache":/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet test tests/OpcBridge.LoadTest/OpcBridge.LoadTest.csproj --nologo
```

Build output lands in `src/*/bin/Debug/net8.0/`; the bridge app is `src/OpcBridge.App/bin/Debug/net8.0/OpcBridge.App.dll`.
