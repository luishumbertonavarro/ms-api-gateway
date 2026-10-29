# MsApiGateway

API Gateway (YARP) con Service Discovery en Consul y seguridad JWT. Es parte del proyecto **NurTricenter** (Diplomado de Arquitectura de Microservicios) y de la defensa final del Módulo 5. Este repo también contiene la infraestructura compartida (Consul y RabbitMQ) y el compose que levanta el sistema completo.

- **Arquitectura y catálogo de eventos:** [`documentacion/ARQUITECTURA.md`](documentacion/ARQUITECTURA.md)
- **Composición del sistema (contenedores, repos, capas):** [`documentacion/COMPOSICION_DEL_SISTEMA.md`](documentacion/COMPOSICION_DEL_SISTEMA.md)
- **Guion de la presentación:** [`documentacion/FLUJO_PRESENTACION.md`](documentacion/FLUJO_PRESENTACION.md)
- **Checklist de la defensa:** [`PLAN_DEFENSA_FINAL.md`](PLAN_DEFENSA_FINAL.md)

## Qué hace

- **Enrutamiento por prefijo** hacia los 6 microservicios. Las rutas salen de la sección `Servicios` de `appsettings.json`, que es la única fuente de verdad. El prefijo se quita antes de reenviar.
- **Service Discovery.** Un `BackgroundService` consulta a Consul cada 10 s qué instancias de cada servicio están sanas y actualiza YARP en caliente. Con varias réplicas, balancea entre ellas. El Gateway también se registra en Consul (`api-gateway`).
- **Seguridad.**
  - `POST /auth/login` emite un JWT HS256 con el rol en el claim `rol`, con issuer `nurtricenter-gateway` y audience `nurtricenter-api`.
  - Todas las rutas proxeadas exigen ese token (política `autenticado`).
  - Cada microservicio vuelve a validar el token y autoriza por rol.
- **Rate limiting.** 10 intentos de login por minuto y por IP, y 300 peticiones por minuto y por IP en las rutas.
- **`GET /health`** para Consul y **`GET /`** con el mapa de rutas.

| Ruta de entrada | Servicio en Consul | Repo |
|---|---|---|
| `/pacientes/**` | `pacientes` | `katering/ms-pacientes-evaluacion` |
| `/produccion/**` | `produccion` | `katering/ms-produccion-alimentos` |
| `/contratos/**` | `ms-contratos` | `elvis/ms-contratos-facturacion-modulo2` |
| `/logistica/**` | `ms-logistica` | `elvis/ms-logistica-entrega-modulo4` |
| `/catering/**` | `ms-catering` | `luis/Repo-ms-catering` |
| `/plan-nutricional/**` | `ms-plan-nutricional` | `luis/Repo-ms-plan-nutricional` |

Usuarios demo:

| Usuario | Contraseña |
|---|---|
| `admin` | `admin123` |
| `nutricionista` | `nutri123` |
| `paciente` | `paciente123` |
| `cocina` | `cocina123` |
| `repartidor` | `repartidor123` |

## Cómo levantarlo

Se espera que los repos estén en esta estructura de carpetas:

```
integracion/
├── ms-api-gateway/
├── katering/ms-pacientes-evaluacion, katering/ms-produccion-alimentos
├── elvis/ms-contratos-facturacion-modulo2, elvis/ms-logistica-entrega-modulo4
└── luis/Repo-ms-catering, luis/Repo-ms-plan-nutricional
```

```powershell
cp .env.example .env                 # opcional: cambiar JWT_SECRET y credenciales
./scripts/levantar.ps1               # crea la red nurtricenter_net y levanta todo
./scripts/levantar.ps1 -Bajar        # detiene el sistema
```

Para levantar solo la infraestructura (Consul, RabbitMQ y Gateway): `docker compose up -d --build`.

| URL | Qué es |
|---|---|
| http://localhost:5000 | API Gateway |
| http://localhost:8500/ui | Consul |
| http://localhost:15672 | RabbitMQ (guest/guest) |

## Pruebas

```powershell
powershell -ExecutionPolicy Bypass -File pruebas/flujo-e2e.ps1
```

El script recorre por el Gateway el flujo completo:

1. Paciente registrado.
2. Contratos.
3. Plan.
4. Calendario de catering.
5. Paquete.
6. Ruta.
7. Entrega confirmada.
8. Control adicional.

En cada paso verifica que el evento correspondiente llegó por RabbitMQ al servicio que debía reaccionar. También comprueba los 401 y 403 de seguridad y el registro de los 7 servicios en Consul.

## Tecnología

.NET 8, ASP.NET Core (Minimal Hosting), Yarp.ReverseProxy 2, Consul, Microsoft.AspNetCore.Authentication.JwtBearer y rate limiting de ASP.NET Core.
