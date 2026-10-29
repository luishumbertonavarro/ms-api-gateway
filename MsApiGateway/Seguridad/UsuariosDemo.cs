namespace MsApiGateway.Seguridad;

/// <summary>
/// Usuarios de demostración, uno por rol del sistema. En producción vendrían de un
/// Identity Provider; para la defensa basta con este catálogo en memoria.
/// </summary>
public static class UsuariosDemo
{
    public sealed record UsuarioDemo(string Usuario, string Password, string Rol);

    public static readonly IReadOnlyList<UsuarioDemo> Todos = new[]
    {
        new UsuarioDemo("admin", "admin123", Roles.Admin),
        new UsuarioDemo("nutricionista", "nutri123", Roles.Nutricionista),
        new UsuarioDemo("paciente", "paciente123", Roles.Paciente),
        new UsuarioDemo("cocina", "cocina123", Roles.Cocina),
        new UsuarioDemo("repartidor", "repartidor123", Roles.Repartidor),
    };

    public static UsuarioDemo? Validar(string usuario, string password) =>
        Todos.FirstOrDefault(u => u.Usuario == usuario && u.Password == password);
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Nutricionista = "nutricionista";
    public const string Paciente = "paciente";
    public const string Cocina = "cocina";
    public const string Repartidor = "repartidor";
}
