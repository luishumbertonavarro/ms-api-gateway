using Consul;
using MsApiGateway.Configuracion;
using RouteConfig = Yarp.ReverseProxy.Configuration.RouteConfig;
using ClusterConfig = Yarp.ReverseProxy.Configuration.ClusterConfig;
using DestinationConfig = Yarp.ReverseProxy.Configuration.DestinationConfig;
using InMemoryConfigProvider = Yarp.ReverseProxy.Configuration.InMemoryConfigProvider;

namespace MsApiGateway.ServiceDiscovery;

/// <summary>
/// Consulta a Consul periódicamente (Service Discovery) para saber qué instancias de cada
/// microservicio de la sección "Servicios" están saludables en este momento, y actualiza en
/// caliente los destinos de cada cluster de YARP mediante InMemoryConfigProvider.Update().
/// Si un servicio se cae, su health check en Consul empieza a fallar, deja de aparecer en la
/// consulta y el Gateway deja de enviarle tráfico — sin reiniciar nada ni tocar
/// configuración estática. Con varias réplicas sanas, YARP balancea entre ellas.
/// </summary>
public sealed class ActualizadorDeDestinosBackgroundService : BackgroundService
{
    private readonly IConsulClient _consulClient;
    private readonly InMemoryConfigProvider _configProvider;
    private readonly IReadOnlyList<RouteConfig> _rutas;
    private readonly IReadOnlyList<ServicioDestino> _servicios;
    private readonly TimeSpan _intervalo;
    private readonly ILogger<ActualizadorDeDestinosBackgroundService> _logger;

    public ActualizadorDeDestinosBackgroundService(
        IConsulClient consulClient,
        InMemoryConfigProvider configProvider,
        IReadOnlyList<RouteConfig> rutas,
        IReadOnlyList<ServicioDestino> servicios,
        IConfiguration configuration,
        ILogger<ActualizadorDeDestinosBackgroundService> logger)
    {
        _consulClient = consulClient;
        _configProvider = configProvider;
        _rutas = rutas;
        _servicios = servicios;
        _intervalo = TimeSpan.FromSeconds(configuration.GetValue("ServiceDiscovery:IntervaloSegundos", 30));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primera actualización inmediata: no tiene sentido esperar el intervalo completo
        // antes de poder enrutar tráfico la primera vez que arranca el Gateway.
        await ActualizarDestinosAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalo, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await ActualizarDestinosAsync(stoppingToken);
        }
    }

    private async Task ActualizarDestinosAsync(CancellationToken cancellationToken)
    {
        var nuevosClusters = new List<ClusterConfig>();

        foreach (var servicio in _servicios)
        {
            var destinos = new Dictionary<string, DestinationConfig>();

            try
            {
                var respuesta = await _consulClient.Health.Service(servicio.Nombre, null, true, cancellationToken);

                foreach (var entrada in respuesta.Response)
                {
                    destinos[entrada.Service.ID] = new DestinationConfig
                    {
                        Address = ConstruirDireccion(entrada.Service.Address, entrada.Service.Port),
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServiceDiscovery] No se pudo consultar Consul para el servicio '{Servicio}'.", servicio.Nombre);
            }

            nuevosClusters.Add(new ClusterConfig { ClusterId = servicio.ClusterId, Destinations = destinos });
            _logger.LogInformation("[ServiceDiscovery] {Servicio}: {Cantidad} instancia(s) saludable(s).", servicio.Nombre, destinos.Count);
        }

        _configProvider.Update(_rutas, nuevosClusters);
    }

    // Los servicios .NET y los de Python registran la dirección con o sin esquema
    // ("http://10.0.0.5" o "10.0.0.5"); YARP necesita una URL completa.
    private static string ConstruirDireccion(string direccion, int puerto)
    {
        var conEsquema = direccion.Contains("://") ? direccion : $"http://{direccion}";
        return $"{conEsquema.TrimEnd('/')}:{puerto}";
    }
}
