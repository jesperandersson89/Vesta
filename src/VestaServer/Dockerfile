# Vesta relay image. For RELAY OPERATORS — app developers connect to an existing relay
# (e.g. Atrium's managed one) and never need this. See docs/operating-a-relay.md.
#
# Build context MUST be the repository root (VestaServer references VestaCore):
#   docker build -f src/VestaServer/Dockerfile -t vesta-server .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
ARG VERSION=0.0.0-dev

COPY Directory.Build.props .editorconfig ./
COPY src/VestaCore/VestaCore.csproj src/VestaCore/
COPY src/VestaServer/VestaServer.csproj src/VestaServer/
RUN dotnet restore src/VestaServer/VestaServer.csproj

COPY src/VestaCore/ src/VestaCore/
COPY src/VestaServer/ src/VestaServer/
RUN dotnet publish src/VestaServer/VestaServer.csproj -c Release -o /app/publish \
    --no-restore -p:Version=${VERSION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.title="vesta-server" \
      org.opencontainers.image.description="Vesta relay server. For relay operators; app developers connect to an existing relay (e.g. Atrium) and do not need this image." \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app/publish .

# The aspnet image runs as non-root `app` and listens on 8080 (ASPNETCORE_HTTP_PORTS=8080).
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "VestaServer.dll"]
