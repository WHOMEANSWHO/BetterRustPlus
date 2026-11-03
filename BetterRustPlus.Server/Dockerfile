FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY nuget.config ./
COPY packages-local/ packages-local/
COPY BetterRustPlus.Server/BetterRustPlus.Server.csproj BetterRustPlus.Server/
COPY BetterRustPlus.Server/Core/BetterRustPlus.Alerts.csproj BetterRustPlus.Server/Core/
RUN dotnet restore BetterRustPlus.Server/BetterRustPlus.Server.csproj
COPY BetterRustPlus.Server/ BetterRustPlus.Server/
RUN dotnet publish BetterRustPlus.Server/BetterRustPlus.Server.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
# curl is used only for the local liveness probe, without ingest credentials.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5088 \
    DOTNET_EnableDiagnostics=0
EXPOSE 5088
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 3 http://127.0.0.1:5088/health > /dev/null || exit 1
ENTRYPOINT ["dotnet", "BetterRustPlus.Server.dll"]
