namespace MsApiGateway.Configuracion;

/// <summary>
/// Un microservicio detrás del Gateway: su nombre de registro en Consul y el prefijo de URL
/// público con el que se expone. Se lee de la sección "Servicios" de appsettings, que es la
/// única fuente de verdad para las rutas y para el Service Discovery.
/// </summary>
public sealed class ServicioDestino
{
    public string Nombre { get; init; } = string.Empty;
    public string Prefijo { get; init; } = string.Empty;

    public string RouteId => $"{Nombre}-route";
    public string ClusterId => $"{Nombre}-cluster";
}
