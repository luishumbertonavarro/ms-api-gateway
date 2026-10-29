# Composición del sistema NUR-TRICENTER

Este documento describe cómo está armado el sistema: qué piezas hay, dónde vive el código de cada una, cómo se despliegan y qué componentes comparten. Los patrones (Outbox, Inbox, DLQ, JWT) y el catálogo de eventos se explican en [`ARQUITECTURA.md`](ARQUITECTURA.md).

## 1. Vista de despliegue

Todo corre en Docker, en una red bridge compartida llamada `nurtricenter_net`. `ms-api-gateway/docker-compose.sistema.yml` incluye el compose de infraestructura y el de cada microservicio, así que el sistema completo se levanta con un solo comando.

```mermaid
flowchart TB
    subgraph Cliente
        U[Postman / App / pruebas/flujo-e2e.ps1]
    end
    subgraph Infraestructura["Infraestructura (ms-api-gateway/docker-compose.yml)"]
        GW["api-gateway<br/>.NET 8 + YARP<br/>:5000"]
        CS[("consul<br/>:8500")]
        MQ[("rabbitmq<br/>:5672 / :15672")]
    end
    subgraph Katering
        PAC["pacientes_api<br/>.NET 8 :5144"] --- PACDB[("pacientes_db<br/>SQL Server :1433")]
        PRO["produccion_api<br/>.NET 8 :5234"] --- PRODB[("produccion_db<br/>SQL Server :1434")]
    end
    subgraph Elvis
        CON["contratos_api<br/>.NET 10 :5030"] --- CONDB[("contratos_db<br/>Postgres 16 :5435")]
        LOG["logistica_api<br/>.NET 10 :5060"] --- LOGDB[("logistica_db<br/>Postgres 16 :5436")]
    end
    subgraph Luis
        CAT["catering_api ×1..3<br/>FastAPI :8020-8022"] --- CATDB[("catering_db<br/>Postgres 15 :5433")]
        PLA["plan_nutricional_api<br/>FastAPI :8010"] --- PLADB[("plan_nutricional_db<br/>Postgres 15 :5434")]
    end
    U -->|HTTP + JWT| GW
    GW --> PAC & PRO & CON & LOG & CAT & PLA
    PAC & PRO & CON & LOG & CAT & PLA <-->|eventos| MQ
    GW & PAC & PRO & CON & LOG & CAT & PLA -.registro + /health.-> CS
```

### Contenedores

| Contenedor | Imagen | Puerto host → contenedor | Qué hace |
|---|---|---|---|
| `consul` | `hashicorp/consul:1.20` (modo `-dev`) | 8500 → 8500 | Registro de servicios y health checks. Tiene UI |
| `rabbitmq` | `rabbitmq:3.13-management` | 5672 → 5672, 15672 → 15672 | Broker. Exchange topic `nurtricenter.events`, una cola por servicio y sus DLQ |
| `api-gateway` | `nurtricenter/api-gateway:1.0.0` | 5000 → 8080 | Punto de entrada único: login, JWT, rate limiting y proxy hacia los servicios |
| `pacientes_api` + `pacientes_db` | `nurtricenter/ms-pacientes-evaluacion:2.0.0` + `mssql/server:2022` | 5144 → 8080, 1433 → 1433 | BC1, Pacientes y Evaluación |
| `produccion_api` + `produccion_db` | `nurtricenter/ms-produccion-alimentos:2.0.0` + `mssql/server:2022` | 5234 → 8080, 1434 → 1433 | BC5, Producción de Alimentos |
| `contratos_api` + `contratos_db` | `nurtricenter/ms-contratos-facturacion:2.0.0` + `postgres:16` | 5030 → 8080, 5435 → 5432 | BC3, Contratos y Facturación |
| `logistica_api` + `logistica_db` | `nurtricenter/ms-logistica-entrega:2.0.0` + `postgres:16` | 5060 → 8080, 5436 → 5432 | BC6, Logística de Entrega. Volumen `logistica_evidencias` para fotos y firmas |
| `catering_api` (1 a 3 réplicas) + `catering_db` + `catering_cache` + `catering_migrate` | `betonavarro/ms-catering:0.2.0` + `postgres:15` + `redis:7` | 8020-8022 → 8000, 5433 → 5432 | BC4, Gestión de Catering. `catering_migrate` corre las migraciones de Alembic una vez y termina |
| `plan_nutricional_api` + `plan_nutricional_db` | `betonavarro/ms-plan-nutricional:2.0.0` + `postgres:15` | 8010 → 8000, 5434 → 5432 | BC2, Planificación Nutricional |

En total son **17 contenedores** (19 con catering escalado a 3 réplicas). Los puertos directos de los servicios son solo para depurar: sin el JWT del Gateway responden 401.

## 2. Repositorios y responsables

| Repo (carpeta) | Responsable | Microservicio | Rama de trabajo |
|---|---|---|---|
| `ms-api-gateway` | Grupo | API Gateway, infraestructura y documentación | `main` |
| `katering/ms-pacientes-evaluacion` | Katering Cristal Flores Hilari | BC1, Pacientes y Evaluación | `main` |
| `katering/ms-produccion-alimentos` | Katering Cristal Flores Hilari | BC5, Producción de Alimentos | `main` |
| `elvis/ms-contratos-facturacion-modulo2` | Elvis Encinas | BC3, Contratos y Facturación | `main` |
| `elvis/ms-logistica-entrega-modulo4` | Elvis Encinas | BC6, Logística de Entrega | `main` |
| `luis/Repo-ms-catering` | Luis Humberto Navarro Ribeiro | BC4, Gestión de Catering | `master` |
| `luis/Repo-ms-plan-nutricional` | Luis Humberto Navarro Ribeiro | BC2, Planificación Nutricional | `master` |

Para que `docker-compose.sistema.yml` encuentre los repos de los servicios, tienen que estar en esta estructura de carpetas:

```
integracion/
├── ms-api-gateway/
├── katering/   ms-pacientes-evaluacion/, ms-produccion-alimentos/
├── elvis/      ms-contratos-facturacion-modulo2/, ms-logistica-entrega-modulo4/
└── luis/       Repo-ms-catering/, Repo-ms-plan-nutricional/
```

## 3. API Gateway (`ms-api-gateway`)

| Pieza | Archivo | Qué hace |
|---|---|---|
| Rutas | `MsApiGateway/appsettings.json` → `Servicios` | Lista de `{ Nombre en Consul, Prefijo }`. YARP genera una ruta y un cluster por entrada y quita el prefijo antes de reenviar |
| Service Discovery | `ServiceDiscovery/ActualizadorDeDestinosBackgroundService.cs` | Cada 10 s pide a Consul las instancias sanas de cada servicio y actualiza los destinos de YARP en caliente |
| Auto-registro | `ServiceDiscovery/RegistroGatewayEnConsul.cs` | Registra el Gateway en Consul como `api-gateway` |
| Login | `Program.cs` → `POST /auth/login`, `Seguridad/EmisorDeTokens.cs`, `Seguridad/UsuariosDemo.cs` | Valida usuario y contraseña de demo y emite un JWT HS256 con el claim `rol` |
| Autorización | Política `autenticado` en cada ruta | Sin token válido, responde 401 antes de llegar al servicio |
| Rate limiting | Políticas `login` (10/min) y `general` (300/min), por IP | Responde 429 al excederse |

| Prefijo | Servicio en Consul | Ejemplo |
|---|---|---|
| `/pacientes` | `pacientes` | `GET /pacientes/api/pacientes` |
| `/produccion` | `produccion` | `POST /produccion/api/paquetes` |
| `/contratos` | `ms-contratos` | `POST /contratos/api/contratos` |
| `/logistica` | `ms-logistica` | `GET /logistica/api/paquetes?fecha=...` |
| `/catering` | `ms-catering` | `GET /catering/calendarios/paciente/{id}` |
| `/plan-nutricional` | `ms-plan-nutricional` | `POST /plan-nutricional/planes` |

## 4. Los seis microservicios por dentro

Todos siguen arquitectura limpia (Domain ← Application ← Infrastructure ← API) y tienen **su propia base de datos**.

### 4.1 Servicios .NET (Katering y Elvis)

```
<Servicio>.Domain           agregados, value objects, eventos de dominio
<Servicio>.Application      commands/queries (MediatR), contratos de integración,
                            manejadores de eventos entrantes (IManejadorEventoIntegracion)
<Servicio>.Infrastructure   EF Core, repositorios, Outbox, Inbox y mensajería:
    Messaging/RabbitMqConexion.cs              conexión perezosa que se reconecta sola
    Messaging/RabbitMqEventPublisher.cs        publish con publisher confirms
      (RabbitMqPublicador.cs en Elvis)
    Messaging/ConsumidorEventosIntegracion.cs  cola + bindings + DLQ + inbox
    Outbox/OutboxPublisherBackgroundService.cs relay del Outbox
    Inbox/EventoProcesado.cs                   tabla de mensajes ya procesados
<Servicio>.Api / WebApi     controllers, JWT, /health, registro en Consul (ServiceDiscovery/)
```

| | Pacientes y Evaluación | Producción | Contratos | Logística |
|---|---|---|---|---|
| .NET | 8 | 8 | 10 | 10 |
| Base de datos | SQL Server (`NurTricenterPacientes`) | SQL Server (`NurTricenterProduccion`) | Postgres (`contratos_db`) | Postgres (`logistica_db`) |
| Cómo escribe en el Outbox | `IIntegrationEventOutbox` en el handler | `IIntegrationEventOutbox` en el handler | `OutboxIntegrationEventPublisher` (políticas dentro del `UnitOfWork`) | `OutboxIntegrationEventPublisher` |
| Tablas de mensajería | `OutboxMessages`, `EventosProcesados` | `OutboxMessages`, `EventosProcesados`, réplica `PacientesCatering` y `DireccionesEntregaDia` | `outbox_messages`, `eventos_procesados` | `outbox_messages`, `eventos_procesados` |
| Cola | `ms-pacientes.eventos` | `ms-produccion.eventos` | `ms-contratos.eventos` | `ms-logistica.eventos` |
| Roles | admin, nutricionista (escritura de pacientes) | admin, cocina | admin, paciente (facturas), nutricionista (catálogo) | admin, repartidor |
| Tests | 55 | 74 | 113 | 156 (incluye Testcontainers) |

### 4.2 Servicios Python (Luis)

```
src/<paquete>/domain          agregados, value objects, eventos de dominio, puertos
src/<paquete>/application     casos de uso
src/<paquete>/infrastructure
    persistence/              SQLAlchemy async, repositorios, tablas outbox y eventos_procesados
    messaging/outbox_relay.py relay con FOR UPDATE SKIP LOCKED (seguro con varias réplicas)
    messaging/consumidor.py   aio-pika connect_robust + inbox
    messaging/manejadores.py  reacción a cada routing key
    discovery/consul.py       registro en Consul con la IP del contenedor
src/<paquete>/presentation    FastAPI, routers, security.py (JWT del gateway + rol)
```

| | Gestión de Catering | Planificación Nutricional |
|---|---|---|
| Stack | FastAPI, SQLAlchemy async, Alembic | FastAPI, SQLAlchemy async, script SQL de inicio |
| Base de datos | Postgres (`db_catering`) | Postgres |
| Réplicas | Hasta 3 (`--scale catering_api=3`) | 1 |
| Réplica local | `planes_nutricionales_conocidos` (plan vigente por paciente) | `asesoramientos_habilitados` |
| Cola | `ms-catering.eventos` | `ms-plan-nutricional.eventos` |
| Tests | 261 | 310 |

## 5. Configuración compartida

Todos los servicios reciben la misma configuración de infraestructura por variables de entorno, desde su `docker-compose.yml`:

| Qué | .NET | Python |
|---|---|---|
| Broker | `RabbitMq__HostName=rabbitmq`, `RabbitMq__UserName`, `RabbitMq__Password` | `RABBITMQ_URL=amqp://guest:guest@rabbitmq:5672/` |
| Consul | `ServiceDiscovery__ServiceDiscoveryAddress=http://consul:8500`, `ServiceDiscovery__ServiceId`, `ServiceDiscovery__Port=8080`, `ServiceDiscovery__Host=""` (usa la IP del contenedor) | `CONSUL_URL=http://consul:8500`, `SERVICIO_NOMBRE` |
| JWT | `Jwt__Secret`; issuer `nurtricenter-gateway` y audience `nurtricenter-api` en `appsettings.json` | `JWT_SECRET`, `JWT_ISSUER`, `JWT_AUDIENCE`, `AUTH_HABILITADA=true` |
| Migraciones | Katering: siempre al arrancar. Elvis: `Database__MigrarAlArrancar=true` | Catering: contenedor `catering_migrate` (Alembic). Plan: script SQL en el arranque de Postgres |

Los valores por defecto (`JWT_SECRET`, credenciales de RabbitMQ) se pueden cambiar con `ms-api-gateway/.env` (ver `.env.example`).

## 6. Puertos de un vistazo

| Puerto | Qué |
|---|---|
| 5000 | **API Gateway**: la única URL que usa el cliente |
| 8500 | Consul UI |
| 15672 | RabbitMQ Management (guest/guest) |
| 5144 · 5234 · 5030 · 5060 · 8020-8022 · 8010 | Servicios, acceso directo (exigen JWT) |
| 1433 · 1434 · 5433 · 5434 · 5435 · 5436 | Bases de datos |
