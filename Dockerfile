# ── ETAPA 1: Builder (compila el código) ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder
WORKDIR /src

# Copiar solo el archivo de proyecto primero (mejor cache de capas)
COPY MsApiGateway/MsApiGateway.csproj MsApiGateway/

# Restaurar dependencias (cacheado si no cambia el .csproj)
RUN dotnet restore "MsApiGateway/MsApiGateway.csproj"

# Copiar el resto del código fuente
COPY MsApiGateway/ MsApiGateway/

# Compilar y publicar en modo Release
RUN dotnet publish "MsApiGateway/MsApiGateway.csproj" \
    -c Release \
    --no-restore \
    -o /app/publish

# ── ETAPA 2: Runtime (solo lo necesario para ejecutar) ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copiar SOLO el compilado desde la etapa builder
COPY --from=builder /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MsApiGateway.dll"]
