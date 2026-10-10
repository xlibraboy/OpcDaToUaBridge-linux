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

### Run the Logic app (companion web app)

`src/OpcBridge.Logic` is in the solution and builds with the commands above. It needs a running
bridge, finds one on its own (the 8080–8180 sweep, or `Bridge:BaseUrl`), and serves its page on
8090 — rolling to the next free port when that one is taken (the log line names the bound port).
Both run on the host network so discovery reaches the bridge:

```bash
# The bridge (a container or the MSI service; Dockerfile.local is the local one).
# OPCBRIDGE_INSTANCE_LOCK keeps the single-instance lock out of the working tree.
docker run --rm --network host -u "$(id -u):$(id -g)" -e HOME=/tmp/dh -e DOTNET_CLI_HOME=/tmp/dh \
  -e NUGET_PACKAGES=/nuget -e OPCBRIDGE_INSTANCE_LOCK=/tmp/opcbridge-smoke.lock \
  -v "$PWD":/src -v "$PWD/.nuget-cache":/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet src/OpcBridge.App/bin/Debug/net8.0/OpcBridge.App.dll

# The Logic app (build it first: dotnet build src/OpcBridge.Logic -c Debug)
docker run --rm --network host -u "$(id -u):$(id -g)" -e HOME=/tmp/dh -e DOTNET_CLI_HOME=/tmp/dh \
  -e NUGET_PACKAGES=/nuget -v "$PWD":/src -v "$PWD/.nuget-cache":/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet src/OpcBridge.Logic/bin/Debug/net8.0/OpcBridge.Logic.dll
```

Then open http://localhost:8090. `-e Bridge__BaseUrl=http://host:8080` pins a specific bridge
instead of discovery; `Dockerfile.logic` is the same app as a standalone image.

### Build the Android viewer (APK)

`src/OpcBridge.Mobile` targets `net8.0-android` and therefore needs the MAUI Android workload — it is deliberately **not** in `OpcBridge.sln`, so the two commands above stay workload-free. The shared logic lives in `src/OpcBridge.Mobile.Core` (net8.0) and is covered by the test suite. CI builds the APK on `mobile-v*` (`.github/workflows/mobile-release.yml`); locally:

```bash
# One-time: build the toolchain image (SDK + maui-android workload + JDK 17 + Android SDK)
docker build -f Dockerfile.mobile -t opcbridge-mobile-build .

# Build the APK (Release embeds the assemblies; the project signs with the keystore in
# packaging/android, so successive builds install over each other)
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp/dh -e DOTNET_CLI_HOME=/tmp/dh \
  -e NUGET_PACKAGES=/nuget -e JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64 \
  -e ANDROID_HOME=/opt/android-sdk -e ANDROID_SDK_ROOT=/opt/android-sdk \
  -v "$PWD":/src -v "$PWD/.nuget-cache":/nuget -w /src \
  opcbridge-mobile-build \
  dotnet publish src/OpcBridge.Mobile/OpcBridge.Mobile.csproj -f net8.0-android -c Release \
    -p:AndroidPackageFormats=apk -p:EmbedAssembliesIntoApk=true
```

The APK lands under `src/OpcBridge.Mobile/bin/Release/net8.0-android/` (`-Signed.apk`).
A Debug APK must **not** be installed standalone: it expects fast deployment (assemblies
pushed by `dotnet build/install`) and exits on launch with "No assemblies found ... Assuming
this is part of Fast Deployment".
