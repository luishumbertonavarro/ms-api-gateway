namespace MsApiGateway.Seguridad;

/// <summary>
/// Configuración del JWT que emite el Gateway. Los 6 microservicios validan el token con
/// el mismo secreto, issuer y audience, así que un token sirve para todo el sistema.
/// </summary>
public sealed class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpiracionMinutos { get; set; } = 60;
}
