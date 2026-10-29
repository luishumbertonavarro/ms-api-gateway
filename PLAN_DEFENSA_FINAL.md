# Plan de trabajo: Defensa Final Módulo 5 (NUR-TRICENTER)

Este es el checklist de lo que pide `Defensa_Final_Modulo_5.txt`:

1. API Gateway.
2. Service Discovery.
3. Integración de los 6 microservicios.
4. Seguridad en los endpoints.
5. Transactional Outbox.
6. Comunicación interna asíncrona por un broker.

En cada repo se trabaja en la rama `feature/integracion-modulo5`.

## Decisiones

- **RabbitMQ para toda la comunicación entre microservicios.**
  - Exchange topic `nurtricenter.events`, con routing keys en la forma `entidad.participio`.
  - No hay HTTP entre servicios. El único HTTP es cliente → gateway → servicio.
- **Consul para el service discovery.** Cada servicio se registra con un health check en `/health`. El gateway (YARP) lee de Consul las instancias sanas y balancea entre ellas.
- **El JWT lo emite el gateway** en `POST /auth/login` y lo valida en el borde. Los 6 servicios lo vuelven a validar:
  - Algoritmo HS256 con secreto compartido.
  - Issuer `nurtricenter-gateway` y audience `nurtricenter-api`.
  - Claim `rol`.
- **Roles:** `admin`, `nutricionista`, `paciente`, `cocina`, `repartidor`.
- **6 microservicios.** BC1 Pacientes y BC2 Evaluación se fusionaron en `ms-pacientes-evaluacion`.

| Servicio | Repo | Nombre en Consul | Prefijo en el gateway |
|---|---|---|---|
| Pacientes y Evaluación | katering/ms-pacientes-evaluacion | `pacientes` | `/pacientes` |
| Producción de Alimentos | katering/ms-produccion-alimentos | `produccion` | `/produccion` |
| Contratos y Facturación | elvis/ms-contratos-facturacion-modulo2 | `ms-contratos` | `/contratos` |
| Logística de Entrega | elvis/ms-logistica-entrega-modulo4 | `ms-logistica` | `/logistica` |
| Gestión de Catering | luis/Repo-ms-catering | `ms-catering` | `/catering` |
| Planificación Nutricional | luis/Repo-ms-plan-nutricional | `ms-plan-nutricional` | `/plan-nutricional` |

---

## Fase 0: Documentación
- [x] `PLAN_DEFENSA_FINAL.md` (este archivo)
- [x] `documentacion/ARQUITECTURA.md`: arquitectura actualizada y catálogo de eventos implementado

## Fase 1: Infraestructura y API Gateway (`ms-api-gateway`)
- [x] `docker-compose.yml` con consul, rabbitmq (management) y gateway, en la red externa `nurtricenter_net`
- [x] `docker-compose.sistema.yml` que usa `include:` para levantar la infraestructura y los 6 servicios
- [x] `.env.example` y `scripts/levantar.ps1`
- [x] Sección `Servicios` en appsettings; rutas y clusters generados desde esa lista
- [x] `POST /auth/login` que emite el JWT, con usuarios demo por rol
- [x] Validación del JWT en todas las rutas proxeadas (política `autenticado`)
- [x] `/health` y auto-registro del gateway en Consul
- [x] Rate limiting en `/auth/login`, CORS y README

## Fase 2: Katering (pacientes-evaluacion, produccion-alimentos)
- [x] JWT del gateway (issuer, audience y `RoleClaimType = "rol"`); se eliminan `AuthController` y `UsuariosDemo`
- [x] Roles nuevos en los `[Authorize]`
- [x] Registro en Consul con host y puerto configurables (ya no `host.docker.internal`)
- [x] Publisher con JSON camelCase
- [x] `paciente.registrado` con `nroIdentificacion` y `contacto`
- [x] Nuevo evento `evaluacion.registrada` (Pacientes)
- [x] Nuevo evento `paquetes.listos` (Producción), alineado con el pact de Logística
- [x] Consumidor RabbitMQ genérico con dead-letter queue e inbox idempotente (`EventosProcesados`)
- [x] `PacientesApiClient` deja de registrarse en DI: no hay HTTP entre servicios en ejecución (la clase se conserva solo para los tests Pact del Módulo 4)
- [x] `docker-compose.yml` por servicio (SQL Server + api)

## Fase 3: Elvis (contratos-facturacion, logistica-entrega)
- [x] Outbox (`OutboxMessages`) y relay a RabbitMQ; `OutboxIntegrationEventPublisher` reemplaza al logger
- [x] Contratos publica `servicio.contratado`, `factura.emitida`, `contrato.cancelado` y `contrato.completado`
- [x] Contratos consume `paciente.registrado`, `plan_nutricional.creado` y `evaluacion.registrada`
- [x] Logística publica `ruta.optimizada`, `entrega.confirmada` e `incidencia_entrega.registrada`
- [x] Logística consume `paquetes.listos`
- [x] Se elimina `IntegracionController` en ambos (HTTP entre servicios)
- [x] JWT y `[Authorize]` por rol
- [x] Registro en Consul y `/health`
- [x] Migraciones al arranque, `Dockerfile` y `docker-compose.yml` (Postgres + api)

## Fase 4: Luis (catering, plan-nutricional)
- [x] `security.py` valida la audience
- [x] `PacienteGatewayHttp` no se usa en ejecución (se conserva solo para los tests Pact); documentado en `EVENTOS_INTEGRACION.md` §7
- [x] Catering consume `incidencia_entrega.registrada` y agrega un día de reposición
- [x] Se corrige `EVENTOS_INTEGRACION.md` §3 (Outbox)

## Fase 5: Verificación
- [x] Sistema completo arriba (17 contenedores) con `docker-compose.sistema.yml`
- [x] Consul: 7 servicios en passing. RabbitMQ: 6 colas con consumidor, bindings según el catálogo y DLQ vacías
- [x] Seguridad: 401 sin token (en el gateway y directo al servicio), 403 con rol sin permiso, 429 por rate limiting del login
- [x] Flujo extremo a extremo por eventos (`pruebas/flujo-e2e.ps1`): 30/30 verificaciones
- [x] Demo del Outbox: con RabbitMQ detenido el evento quedó pendiente; al levantarlo se publicó y ms-contratos creó la réplica
- [x] Balanceo: `--scale catering_api=3`, 3 instancias en Consul y el gateway repartió 30 peticiones (9/13/8)
- [x] Tests de los 6 repos: pacientes 55, producción 74, contratos 113, logística 156 (incluye Testcontainers), catering 259, plan-nutricional 310

## Incoherencias en los repos de Elvis (resueltas)

Se registraron siguiendo su protocolo (`CLAUDE.md`) y se resolvieron a pedido del grupo; la fase del Módulo 5 quedó documentada en `docs/DESIGN.md` §21 de cada repo:

- `ms-contratos-facturacion-modulo2/docs/INCOHERENCIAS.md`:
  - INC-6 (opción B): `SISTEMA.md` §10/§11 describen la fase sin broker.
  - INC-7 (opción A): contratos de §9 vs. lo que viaja por RabbitMQ.
- `ms-logistica-entrega-modulo4/docs/INCOHERENCIAS.md`:
  - INC-3 (opción B): la misma situación de §10/§11.
  - INC-4 (opción C): coordenadas por defecto cuando BC4 no geolocaliza.

## Entregables de documentación

- [x] `documentacion/ARQUITECTURA.md`: requisitos, patrones y catálogo de eventos
- [x] `documentacion/COMPOSICION_DEL_SISTEMA.md`: contenedores, repos, capas y configuración
- [x] `documentacion/FLUJO_PRESENTACION.md`: guion de la defensa con comandos
- [x] `UNIVERSIDAD NUR -.docx` actualizado a 6 microservicios y nuevo Context Map
- [x] Contratos: crear un servicio duplicado en el catálogo responde 409 `C15_SERVICIO_TIPO_YA_ACTIVO` (antes 500)
