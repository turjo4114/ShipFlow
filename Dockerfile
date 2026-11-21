# syntax=docker/dockerfile:1

# ---------- Stage 1: restore (cached until csproj changes) ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src
COPY src/ShipFlow.Api/ShipFlow.Api.csproj src/ShipFlow.Api/
RUN dotnet restore src/ShipFlow.Api/ShipFlow.Api.csproj

# ---------- Stage 2: publish ----------
FROM restore AS publish
COPY src/ src/
RUN dotnet publish src/ShipFlow.Api/ShipFlow.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- Stage 3: runtime (small, non-root) ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ShipFlow.Api.dll"]
