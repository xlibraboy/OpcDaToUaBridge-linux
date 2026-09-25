#!/bin/sh
# Pause-feature verification rig for issue #5:
#   opcbridge-pause : the main checkout on port 18085 (pause is merged to main)
#   opcua-sim-pause : an OPC UA sim for it to poll
# Both containers join the user-defined opcbridge-pause-net network so the
# bridge can resolve the sim's hostname (container DNS is user-defined only).
# The sim source proves the live worker path: a paused source must drop its
# session, report Paused, and stop producing values until resumed.
set -e
REPO=/home/autoinst578/ProjectDirectory/OpcDaToUaBridge

docker rm -f opcbridge-pause opcua-sim-pause >/dev/null 2>&1 || true
docker network create opcbridge-pause-net >/dev/null 2>&1 || true

# The sim's self-signed cert embeds its container hostname; a recreated container
# gets a new hostname and rejects the old cert (FATAL at boot). Clear it so the
# sim regenerates one for its current identity. The pki files are root-owned
# (the sim container runs as root), so delete them from inside a container.
docker run --rm -v /tmp/cc-sim:/sim mcr.microsoft.com/dotnet/sdk:8.0 \
  rm -rf /sim/OpcUaSimServer/bin/Release/net8.0/pki

docker run -d --name opcua-sim-pause \
  --network opcbridge-pause-net \
  -v /tmp/cc-sim:/sim \
  --workdir /sim/OpcUaSimServer/bin/Release/net8.0 \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet OpcUaSimServer.dll >/dev/null

docker run -d --name opcbridge-pause \
  --network opcbridge-pause-net \
  -p 18085:8080 \
  -u 1000:1000 \
  -v "$REPO":/wt \
  --workdir /wt/src/OpcBridge.App/bin/Debug/net8.0 \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet OpcBridge.App.dll >/dev/null

echo "rig up: bridge http://127.0.0.1:18085, sim opcua-sim-pause"
