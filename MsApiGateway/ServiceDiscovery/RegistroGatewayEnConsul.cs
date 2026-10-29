using System.Net;
using System.Net.Sockets;
using Consul;

namespace MsApiGateway.ServiceDiscovery;

/// <summary>
/// Registra el propio Gateway en Consul al arrancar (con health check a GET /health) y lo da
/// de baja al apagarse, para que en la UI de Consul se vea el sistema completo.
/// Si ServiceDiscovery:Registro:Host está vacío se usa la IP del contenedor en la red Docker.
/// </summary>
public sealed class RegistroGatewayEnConsul : IHostedService
{
    private readonly IConsulClient _consulClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RegistroGatewayEnConsul> _logger;
    private string? _registroId;

    public RegistroGatewayEnConsul(IConsulClient consulClient, IConfiguration configuration, ILogger<RegistroGatewayEnConsul> logger)
    {
        _consulClient = consulClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("ServiceDiscovery:Registro:Habilitado", false))
            return;

        var nombre = _configuration.GetValue("ServiceDiscovery:Registro:Nombre", "api-gateway")!;
        var puerto = _configuration.GetValue("ServiceDiscovery:Registro:Puerto", 8080);
        var host = _configuration.GetValue<string>("ServiceDiscovery:Registro:Host");
        if (string.IsNullOrWhiteSpace(host))
            host = ObtenerIpLocal();

        _registroId = $"{nombre}-{Dns.GetHostName()}";
        var registro = new AgentServiceRegistration
        {
            ID = _registroId,
            Name = nombre,
            Address = host,
            Port = puerto,
            Tags = new[] { "nurtricenter", "gateway" },
            Check = new AgentServiceCheck
            {
                HTTP = $"http://{host}:{puerto}/health",
                Interval = TimeSpan.FromSeconds(10),
                Timeout = TimeSpan.FromSeconds(5),
                DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1),
            },
        };

        try
        {
            await _consulClient.Agent.ServiceRegister(registro, cancellationToken);
            _logger.LogInformation("[ServiceDiscovery] Gateway registrado en Consul como '{Id}' ({Host}:{Puerto}).", _registroId, host, puerto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServiceDiscovery] No se pudo registrar el Gateway en Consul.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registroId is null)
            return;

        try
        {
            await _consulClient.Agent.ServiceDeregister(_registroId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServiceDiscovery] No se pudo dar de baja el Gateway en Consul.");
        }
    }

    private static string ObtenerIpLocal() =>
        Dns.GetHostAddresses(Dns.GetHostName())
            .First(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .ToString();
}
