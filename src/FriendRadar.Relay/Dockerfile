# Build from the repository root:
#   docker build -f src/FriendRadar.Relay/Dockerfile -t friendradar-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/FriendRadar.Protocol/FriendRadar.Protocol.csproj src/FriendRadar.Protocol/
COPY src/FriendRadar.Relay/FriendRadar.Relay.csproj src/FriendRadar.Relay/
RUN dotnet restore src/FriendRadar.Relay/FriendRadar.Relay.csproj

COPY src/FriendRadar.Protocol/ src/FriendRadar.Protocol/
COPY src/FriendRadar.Relay/ src/FriendRadar.Relay/
RUN dotnet publish src/FriendRadar.Relay/FriendRadar.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The account graph lives here; mount a volume so it survives a container rebuild.
ENV Relay__DataDirectory=/data
ENV ASPNETCORE_HTTP_PORTS=8080
VOLUME ["/data"]
EXPOSE 8080

RUN useradd --system --uid 10001 relay && mkdir -p /data && chown relay /data
USER relay

ENTRYPOINT ["dotnet", "FriendRadar.Relay.dll"]
