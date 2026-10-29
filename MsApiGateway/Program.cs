using System.Text;
using System.Threading.RateLimiting;
using Consul;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MsApiGateway.Configuracion;
using MsApiGateway.Seguridad;
using MsApiGateway.ServiceDiscovery;
using RouteConfig = Yarp.ReverseProxy.Configuration.RouteConfig;
using ClusterConfig = Yarp.ReverseProxy.Configuration.ClusterConfig;
using DestinationConfig = Yarp.ReverseProxy.Configuration.DestinationConfig;
using RouteMatch = Yarp.ReverseProxy.Configuration.RouteMatch;
using InMemoryConfigProvider = Yarp.ReverseProxy.Configuration.InMemoryConfigProvider;
using IProxyConfigProvider = Yarp.ReverseProxy.Configuration.IProxyConfigProvider;

var builder = WebApplication.CreateBuilder(args);

var consulAddress = builder.Configuration.GetValue<string>("ServiceDiscovery:ConsulAddress") ?? "http://localhost:8500";
builder.Services.AddSingleton<IConsulClient>(_ => new ConsulClient(c => c.Address = new Uri(consulAddress)));

// --- Seguridad: el Gateway emite y valida el JWT ---------------------------------------
// POST /auth/login devuelve un token firmado (HS256) con el claim "rol". Todas las rutas
// proxeadas exigen ese token (política "autenticado"); además, cada microservicio lo vuelve
// a validar con el mismo secreto/issuer/audience, para que nadie pueda saltarse el Gateway
// llamando directo al puerto de un servicio.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("Falta la sección Jwt en appsettings.");
if (string.IsNullOrWhiteSpace(jwt.Secret))
    throw new InvalidOperationException("Falta configurar Jwt:Secret (variable de entorno Jwt__Secret).");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            RoleClaimType = "rol",
            NameClaimType = "name",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy("autenticado", policy => policy.RequireAuthenticatedUser()));
builder.Services.AddSingleton<EmisorDeTokens>();

// --- Rate limiting -----------------------------------------------------------------------
// "login" frena ataques de fuerza bruta contra /auth/login; "general" protege a los
// microservicios de ráfagas de un mismo cliente (se aplica por IP).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anonimo",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("general", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anonimo",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// --- Rutas y clusters de YARP --------------------------------------------------------
// Las RUTAS salen de la sección "Servicios" de appsettings: cada microservicio se expone
// bajo su prefijo (/pacientes/**, /catering/**, ...) y el prefijo se quita antes de
// reenviar. Los DESTINOS de cada cluster NO están escritos aquí: se resuelven en tiempo
// real contra Consul (Service Discovery) mediante ActualizadorDeDestinosBackgroundService,
// que pregunta cada N segundos qué instancias están saludables y actualiza el proxy.
var servicios = builder.Configuration.GetSection("Servicios").Get<List<ServicioDestino>>()
    ?? throw new InvalidOperationException("Falta la sección Servicios en appsettings.");

var rutas = servicios.Select(s => new RouteConfig
{
    RouteId = s.RouteId,
    ClusterId = s.ClusterId,
    AuthorizationPolicy = "autenticado",
    RateLimiterPolicy = "general",
    Match = new RouteMatch { Path = $"{s.Prefijo}/{{**catch-all}}" },
    Transforms = new[] { new Dictionary<string, string> { ["PathRemovePrefix"] = s.Prefijo } },
}).ToList();

var clustersIniciales = servicios
    .Select(s => new ClusterConfig { ClusterId = s.ClusterId, Destinations = new Dictionary<string, DestinationConfig>() })
    .ToList();

var proveedorDeConfiguracion = new InMemoryConfigProvider(rutas, clustersIniciales);
builder.Services.AddSingleton(proveedorDeConfiguracion);
builder.Services.AddSingleton<IProxyConfigProvider>(proveedorDeConfiguracion);
builder.Services.AddSingleton<IReadOnlyList<RouteConfig>>(rutas);
builder.Services.AddSingleton<IReadOnlyList<ServicioDestino>>(servicios);
builder.Services.AddReverseProxy();

builder.Services.AddHostedService<ActualizadorDeDestinosBackgroundService>();
builder.Services.AddHostedService<RegistroGatewayEnConsul>();

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Endpoint informativo para confirmar rápidamente, en la demo, que el Gateway está arriba
// y cómo están repartidas las rutas entre los microservicios.
app.MapGet("/", (IReadOnlyList<ServicioDestino> destinos) => Results.Ok(new
{
    servicio = "NurTricenter API Gateway",
    descripcion = "Enruta por prefijo hacia cada microservicio; los destinos se resuelven dinámicamente vía Consul (Service Discovery). Todas las rutas exigen el JWT de POST /auth/login.",
    consulUi = "http://localhost:8500/ui",
    rabbitMqUi = "http://localhost:15672",
    login = "POST /auth/login { \"usuario\": \"admin\", \"password\": \"admin123\" }",
    rutas = destinos.Select(d => $"{d.Prefijo}/{{**ruta}} -> {d.Nombre} (resuelto vía Consul)"),
}));

// Lo consulta Consul (Service Discovery) para saber si el Gateway sigue vivo.
app.MapGet("/health", () => Results.Ok(new { estado = "Healthy" }));

app.MapPost("/auth/login", (SolicitudLogin solicitud, EmisorDeTokens emisor) =>
{
    var usuario = UsuariosDemo.Validar(solicitud.Usuario, solicitud.Password);
    if (usuario is null)
        return Results.Unauthorized();

    var (token, expiraEn) = emisor.Emitir(usuario);
    return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiraEn, usuario = usuario.Usuario, rol = usuario.Rol });
}).RequireRateLimiting("login");

app.MapReverseProxy();

app.Run();

public sealed record SolicitudLogin(string Usuario, string Password);
