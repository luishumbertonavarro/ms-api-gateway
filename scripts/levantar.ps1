# Levanta el sistema completo NUR-TRICENTER (infraestructura + 6 microservicios).
# Uso:  ./scripts/levantar.ps1            -> levanta todo
#       ./scripts/levantar.ps1 -Bajar     -> detiene y elimina los contenedores
param([switch]$Bajar)

$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

if ($Bajar) {
    docker compose -f docker-compose.sistema.yml down
    exit $LASTEXITCODE
}

docker network inspect nurtricenter_net *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creando la red nurtricenter_net..."
    docker network create nurtricenter_net | Out-Null
}

docker compose -f docker-compose.sistema.yml up -d --build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Gateway:   http://localhost:5000"
Write-Host "Consul:    http://localhost:8500/ui"
Write-Host "RabbitMQ:  http://localhost:15672 (guest/guest)"
