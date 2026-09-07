# Build from the repository root:
#   docker build -f src/Wyu2.Relay/Dockerfile -t wyu2-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Wyu2.Protocol/Wyu2.Protocol.csproj src/Wyu2.Protocol/
COPY src/Wyu2.Relay/Wyu2.Relay.csproj src/Wyu2.Relay/
RUN dotnet restore src/Wyu2.Relay/Wyu2.Relay.csproj

COPY src/Wyu2.Protocol/ src/Wyu2.Protocol/
COPY src/Wyu2.Relay/ src/Wyu2.Relay/
RUN dotnet publish src/Wyu2.Relay/Wyu2.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is only here so the compose healthcheck has something to call.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The account graph lives here; mount a volume over it so it survives a container rebuild.
# Presence blobs stay in memory and never reach this directory.
ENV Relay__DataDirectory=/data
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The aspnet image already ships a non-root "app" user. No VOLUME instruction here on purpose:
# it would discard this chown, and compose mounts the volume itself.
RUN mkdir -p /data && chown -R app /data
USER app

ENTRYPOINT ["dotnet", "Wyu2.Relay.dll"]
