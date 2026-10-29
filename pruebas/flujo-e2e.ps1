# Prueba de punta a punta de NUR-TRICENTER a través del API Gateway (Módulo 5).
#
# Recorre el flujo completo SOLO por el Gateway (http://localhost:5000). Entre los
# microservicios no hay ninguna llamada HTTP: cada paso espera a que el evento llegue por
# RabbitMQ al servicio que debe reaccionar (Outbox del productor -> exchange
# nurtricenter.events -> cola del consumidor -> inbox).
#
# Uso (con el sistema levantado por scripts/levantar.ps1):
#   powershell -ExecutionPolicy Bypass -File pruebas/flujo-e2e.ps1
param(
    [string]$Gateway = "http://localhost:5000",
    [int]$TimeoutSegundos = 90
)

$ErrorActionPreference = "Stop"
$script:fallos = 0

function Escribir-Paso($texto) { Write-Host ""; Write-Host "== $texto" -ForegroundColor Cyan }
function Ok($texto) { Write-Host "   [OK] $texto" -ForegroundColor Green }
function Falla($texto) { Write-Host "   [FALLA] $texto" -ForegroundColor Red; $script:fallos++ }

# Invoca la API y devuelve @{ Status; Body } sin lanzar en 4xx/5xx.
function Invocar([string]$Metodo, [string]$Ruta, [string]$Token, $Cuerpo, [string]$Base = $Gateway) {
    $headers = @{}
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    $parametros = @{ Method = $Metodo; Uri = "$Base$Ruta"; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Cuerpo) {
        $parametros["ContentType"] = "application/json; charset=utf-8"
        $parametros["Body"] = [System.Text.Encoding]::UTF8.GetBytes(($Cuerpo | ConvertTo-Json -Depth 10))
    }
    try {
        $respuesta = Invoke-WebRequest @parametros
        $cuerpo = $null
        if ($respuesta.Content) { try { $cuerpo = $respuesta.Content | ConvertFrom-Json } catch { $cuerpo = $respuesta.Content } }
        return @{ Status = [int]$respuesta.StatusCode; Body = $cuerpo }
    } catch [System.Net.WebException] {
        $status = 0
        $texto = $null
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            $lector = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            $texto = $lector.ReadToEnd()
        }
        return @{ Status = $status; Body = $texto }
    }
}

# Reintenta la condición hasta que devuelva algo distinto de $null/$false o se agote el tiempo.
function Esperar-Hasta([string]$Descripcion, [scriptblock]$Condicion) {
    $limite = (Get-Date).AddSeconds($TimeoutSegundos)
    while ((Get-Date) -lt $limite) {
        $resultado = & $Condicion
        if ($resultado) { Ok "$Descripcion"; return $resultado }
        Start-Sleep -Seconds 2
    }
    Falla "$Descripcion (no ocurrió en $TimeoutSegundos s)"
    return $null
}

function Login([string]$Usuario, [string]$Password) {
    $r = Invocar POST "/auth/login" $null @{ usuario = $Usuario; password = $Password }
    if ($r.Status -ne 200) { throw "Login de '$Usuario' falló con $($r.Status)" }
    return $r.Body.accessToken
}

function Id($cuerpo) { if ($cuerpo -is [string]) { return $cuerpo.Trim('"') } elseif ($cuerpo.id) { return $cuerpo.id } else { return $cuerpo } }

$hoy = (Get-Date).ToString("yyyy-MM-dd")

# ----------------------------------------------------------------------------------------
Escribir-Paso "0. Gateway y Service Discovery"
Esperar-Hasta "Gateway responde /health" { (Invocar GET "/health" $null $null).Status -eq 200 } | Out-Null
$consul = Invoke-RestMethod "http://localhost:8500/v1/agent/services"
$nombres = $consul.PSObject.Properties | ForEach-Object { $_.Value.Service } | Sort-Object -Unique
Write-Host "   Servicios registrados en Consul: $($nombres -join ', ')"
foreach ($s in "api-gateway", "pacientes", "produccion", "ms-contratos", "ms-logistica", "ms-catering", "ms-plan-nutricional") {
    if ($nombres -contains $s) { Ok "Consul conoce '$s'" } else { Falla "Consul no conoce '$s'" }
}

# ----------------------------------------------------------------------------------------
Escribir-Paso "1. Seguridad"
$r = Invocar GET "/pacientes/api/pacientes" $null $null
if ($r.Status -eq 401) { Ok "Sin token -> 401 en el Gateway" } else { Falla "Sin token devolvió $($r.Status)" }

$admin = Login "admin" "admin123"
$nutri = Login "nutricionista" "nutri123"
$paciente = Login "paciente" "paciente123"
$cocina = Login "cocina" "cocina123"
$repartidor = Login "repartidor" "repartidor123"
Ok "Login de admin, nutricionista, paciente, cocina y repartidor"

$r = Invocar POST "/contratos/api/servicios" $paciente @{ tipo = "EVALUACION"; nombre = "x"; precio = 1; moneda = "BOB" }
if ($r.Status -eq 403) { Ok "Rol paciente creando catálogo -> 403" } else { Falla "Rol paciente creando catálogo devolvió $($r.Status)" }

$r = Invocar GET "/api/pacientes" $null $null "http://localhost:5144"
if ($r.Status -eq 401) { Ok "Llamada directa a ms-pacientes sin token -> 401 (no se puede saltar el Gateway)" } else { Falla "Llamada directa sin token devolvió $($r.Status)" }

$r = Invocar POST "/auth/login" $null @{ usuario = "admin"; password = "incorrecta" }
if ($r.Status -eq 401) { Ok "Credenciales incorrectas -> 401" }
elseif ($r.Status -eq 429) { Ok "Credenciales incorrectas -> 429 (rate limiting del login: más de 10 intentos por minuto)" }
else { Falla "Credenciales incorrectas devolvió $($r.Status)" }

# ----------------------------------------------------------------------------------------
Escribir-Paso "2. Catálogo de servicios (ms-contratos)"
# Idempotente: ms-contratos admite un solo servicio activo por tipo, así que solo se crean los que faltan.
$activos = @((Invocar GET "/contratos/api/servicios" $admin $null).Body | Where-Object { $_.activo } | ForEach-Object { $_.tipo })
foreach ($s in @(
        @{ tipo = "EVALUACION"; nombre = "Evaluación corporal"; precio = 150; moneda = "BOB" },
        @{ tipo = "ASESORAMIENTO"; nombre = "Asesoramiento nutricional"; precio = 300; moneda = "BOB" },
        @{ tipo = "CATERING"; nombre = "Catering 15 días"; precio = 1500; moneda = "BOB" })) {
    if ($activos -contains $s.tipo) { Ok "Servicio $($s.tipo) ya existía"; continue }
    $r = Invocar POST "/contratos/api/servicios" $admin $s
    if ($r.Status -in 200, 201) { Ok "Servicio $($s.tipo) creado" }
    else { Falla "Crear servicio $($s.tipo) devolvió $($r.Status): $($r.Body)" }
}

# ----------------------------------------------------------------------------------------
Escribir-Paso "3. paciente.registrado: ms-pacientes -> ms-contratos / ms-produccion"
$ci = "{0}" -f (Get-Random -Minimum 1000000 -Maximum 9999999)
$r = Invocar POST "/pacientes/api/pacientes" $admin @{
    nombre = "Ana"; apellido = "Pérez"; fechaNacimiento = "1990-05-15"; email = "ana.$ci@mail.com"
    direccion = "Av. Banzer 123"; telefono = "70000000"; nutricionistaId = [guid]::NewGuid(); nroIdentificacion = $ci
}
if ($r.Status -ne 201) { throw "Registrar paciente devolvió $($r.Status): $($r.Body)" }
$pacienteId = Id $r.Body
Ok "Paciente registrado: $pacienteId (CI $ci)"

# ms-contratos solo contrata para pacientes de su réplica: el ASESORAMIENTO prospera
# cuando llegó paciente.registrado.
Esperar-Hasta "ms-contratos recibió paciente.registrado (contrata ASESORAMIENTO)" {
    $c = Invocar POST "/contratos/api/contratos" $admin @{ pacienteId = $pacienteId; tipoServicio = "ASESORAMIENTO"; fechaInicio = $hoy }
    $c.Status -eq 200
} | Out-Null

# ----------------------------------------------------------------------------------------
Escribir-Paso "4. plan_nutricional.creado: ms-plan-nutricional -> ms-contratos / ms-catering"
$r = Invocar POST "/plan-nutricional/planes" $nutri @{
    paciente_id = $pacienteId; fecha_inicio = $hoy; duracion_dias = 15
    necesidad = @{ calorias = 2000; proteinas = 120; grasas = 60; carbohidratos = 250 }
    recomendacion_texto = "Plan de prueba E2E"
}
if ($r.Status -ne 201) { throw "Crear plan devolvió $($r.Status): $($r.Body)" }
$planId = $r.Body.id
Ok "Plan creado: $planId"

# ----------------------------------------------------------------------------------------
Escribir-Paso "5. servicio.contratado (CATERING): ms-contratos -> ms-catering / ms-produccion / ms-pacientes"
Esperar-Hasta "ms-contratos recibió plan_nutricional.creado (contrata CATERING)" {
    $c = Invocar POST "/contratos/api/contratos" $admin @{ pacienteId = $pacienteId; tipoServicio = "CATERING" }
    $c.Status -eq 200
} | Out-Null

$calendario = Esperar-Hasta "ms-catering creó el calendario de entregas" {
    $c = Invocar GET "/catering/calendarios/paciente/$pacienteId" $admin $null
    if ($c.Status -eq 200) { $c.Body }
}
if ($calendario) {
    Write-Host "   Calendario $($calendario.id): $($calendario.fecha_inicio) a $($calendario.fecha_fin), $($calendario.total_dias) días"
    Esperar-Hasta "ms-catering vinculó el plan nutricional al calendario" {
        $c = Invocar GET "/catering/calendarios/paciente/$pacienteId" $admin $null
        $c.Status -eq 200 -and $c.Body.plan_nutricional_id -eq $planId
    } | Out-Null
}

# ----------------------------------------------------------------------------------------
Escribir-Paso "6. paquetes.listos: ms-produccion -> ms-logistica"
$recetaId = [guid]::NewGuid()
$r = Invocar POST "/produccion/api/ordenes" $cocina @{
    encargadoCocinaId = [guid]::NewGuid(); loteProduccion = "LOTE-E2E"; fecha = $hoy
    items = @(@{ recetaId = $recetaId; cantidadRequerida = 1 })
}
if ($r.Status -ne 201) { throw "Generar orden devolvió $($r.Status): $($r.Body)" }
$ordenId = Id $r.Body
$r = Invocar POST "/produccion/api/paquetes" $cocina @{
    pacienteId = $pacienteId; ordenId = $ordenId; fecha = $hoy
    nombrePaciente = "Ana Pérez"; direccionEntrega = "Av. Banzer 123"; numeroId = $ci
}
if ($r.Status -ne 201) { throw "Armar paquete devolvió $($r.Status): $($r.Body)" }
$paqueteId = Id $r.Body
$r = Invocar POST "/produccion/api/paquetes/$paqueteId/porciones" $cocina @{ recetaId = $recetaId; cantidad = 250 }
$porcionId = Id $r.Body
Invocar POST "/produccion/api/paquetes/$paqueteId/porciones/$porcionId/envasar" $cocina $null | Out-Null
$r = Invocar POST "/produccion/api/paquetes/$paqueteId/entregar" $cocina $null
if ($r.Status -in 200, 204) { Ok "Paquete $paqueteId entregado a logística" } else { Falla "Entregar paquete devolvió $($r.Status): $($r.Body)" }

Esperar-Hasta "ms-logistica registró el paquete (POR_ASIGNAR)" {
    $c = Invocar GET "/logistica/api/paquetes?fecha=$hoy" $admin $null
    $c.Status -eq 200 -and (@($c.Body) | Where-Object { $_.paqueteId -eq $paqueteId })
} | Out-Null

# ----------------------------------------------------------------------------------------
Escribir-Paso "7. entrega.confirmada: ms-logistica -> ms-pacientes"
$r = Invocar POST "/logistica/api/repartidores" $admin @{
    nombre = "Repartidor E2E"; telefono = "70100001"; tipoVehiculo = "MOTO"; placa = "E2E-$ci"; capacidadPaquetes = 10
}
$repartidorId = Id $r.Body
$r = Invocar POST "/logistica/api/rutas" $admin @{ fecha = $hoy; repartidorId = $repartidorId; paqueteIds = @($paqueteId) }
if ($r.Status -notin 200, 201) { throw "Crear ruta devolvió $($r.Status): $($r.Body)" }
$rutaId = Id $r.Body
Invocar POST "/logistica/api/rutas/$rutaId/optimizar" $admin @{
    calle = "Depósito Central"; zona = "Centro"; ciudad = "Santa Cruz"; referencia = $null; latitud = -17.78; longitud = -63.18
} | Out-Null
Invocar POST "/logistica/api/rutas/$rutaId/iniciar" $repartidor $null | Out-Null
$ruta = (Invocar GET "/logistica/api/rutas/$rutaId" $repartidor $null).Body
$paradaId = $ruta.paradas[0].paradaId
Ok "Ruta $rutaId en camino (parada $paradaId); evento ruta.optimizada publicado"

# Constancia: PNG de 1x1 (la evidencia es obligatoria, RN-16).
$png = Join-Path $env:TEMP "constancia-e2e.png"
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="))
$codigo = & curl.exe -s -o NUL -w "%{http_code}" -X POST "$Gateway/logistica/api/rutas/$rutaId/paradas/$paradaId/confirmar" `
    -H "Authorization: Bearer $repartidor" -F "Tipo=FOTO" -F "ReceptorNombre=Ana Pérez" -F "Archivo=@$png;type=image/png"
if ($codigo -eq "200") { Ok "Entrega confirmada con constancia" } else { Falla "Confirmar entrega devolvió $codigo" }

Esperar-Hasta "ms-pacientes recibió entrega.confirmada (inbox EventosProcesados)" {
    $c = Invocar GET "/pacientes/api/integracion/eventos-recibidos?cantidad=50" $admin $null
    $c.Status -eq 200 -and (@($c.Body) | Where-Object { $_.routingKey -eq "entrega.confirmada" -and $_.payload.paqueteId -eq $paqueteId })
} | Out-Null

# ----------------------------------------------------------------------------------------
Escribir-Paso "8. evaluacion.registrada: ms-pacientes -> ms-contratos (control adicional, RN-06)"
$r = Invocar POST "/pacientes/api/pacientes" $admin @{
    nombre = "Luis"; apellido = "Rojas"; fechaNacimiento = "1985-01-20"; email = "luis.$ci@mail.com"
    direccion = "Calle Libertad 456"; telefono = "70000001"; nutricionistaId = [guid]::NewGuid(); nroIdentificacion = "9$ci"
}
$pacienteSinCatering = Id $r.Body
Esperar-Hasta "ms-contratos conoce al segundo paciente" {
    (Invocar POST "/contratos/api/contratos" $admin @{ pacienteId = $pacienteSinCatering; tipoServicio = "EVALUACION"; fechaInicio = $hoy }).Status -eq 200
} | Out-Null
$r = Invocar POST "/pacientes/api/pacientes/$pacienteSinCatering/evaluaciones" $nutri @{
    fecha = $hoy; peso = 70.5; cintura = 80; cadera = 95; imc = 23.1; observaciones = "Control E2E"; adherencia = "Alta"; planId = [guid]::NewGuid()
}
if ($r.Status -in 200, 204) { Ok "Evaluación registrada" } else { Falla "Registrar evaluación devolvió $($r.Status): $($r.Body)" }
Esperar-Hasta "ms-contratos generó y facturó el control adicional" {
    $c = Invocar GET "/contratos/api/pacientes/$pacienteSinCatering/controles" $admin $null
    $c.Status -eq 200 -and @($c.Body).Count -ge 1
} | Out-Null

# ----------------------------------------------------------------------------------------
Write-Host ""
if ($script:fallos -eq 0) {
    Write-Host "Flujo E2E completo: todas las verificaciones pasaron." -ForegroundColor Green
    exit 0
}
Write-Host "Flujo E2E con $script:fallos verificación(es) fallida(s)." -ForegroundColor Red
exit 1
