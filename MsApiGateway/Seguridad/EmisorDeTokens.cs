using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MsApiGateway.Seguridad;

/// <summary>
/// Firma el JWT (HS256) que el cliente usa en todas las rutas. El rol viaja en el claim
/// "rol", que es el que leen los microservicios .NET (RoleClaimType) y los de Python.
/// </summary>
public sealed class EmisorDeTokens
{
    private readonly JwtOptions _opciones;

    public EmisorDeTokens(IOptions<JwtOptions> opciones) => _opciones = opciones.Value;

    public (string Token, DateTime ExpiraEn) Emitir(UsuariosDemo.UsuarioDemo usuario)
    {
        var expiraEn = DateTime.UtcNow.AddMinutes(_opciones.ExpiracionMinutos);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, usuario.Usuario),
            new Claim(JwtRegisteredClaimNames.Name, usuario.Usuario),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("rol", usuario.Rol),
        };

        var credenciales = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opciones.Secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opciones.Issuer,
            audience: _opciones.Audience,
            claims: claims,
            expires: expiraEn,
            signingCredentials: credenciales);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiraEn);
    }
}
