# Build context: repo root (worktree). Builds only the load-test sim server.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY tests/loadtest/OpcUaSimServer/OpcUaSimServer.csproj ./sim/
RUN dotnet restore sim/OpcUaSimServer.csproj
COPY tests/loadtest/OpcUaSimServer/ ./sim/
RUN dotnet publish sim/OpcUaSimServer.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 4840
ENTRYPOINT ["dotnet", "OpcUaSimServer.dll"]
