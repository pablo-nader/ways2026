using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;

namespace Ways.Infrastructure.MercadoPago;

/// <summary>
/// El <c>state</c> de OAuth: DataProtection (autenticado y cifrado) con el vencimiento dentro del propio payload, comparado
/// contra <see cref="IRelojDelSistema"/>. No usa <c>ITimeLimitedDataProtector</c> para no sumar un paquete y para que el
/// vencimiento lo controle el reloj inyectado. La clave de protección sale de la base (ver <c>AddDataProtection</c>): un
/// redeploy durante los diez minutos de vigencia no invalida el flujo.
/// </summary>
public sealed class ProtectorDeEstadoOAuth(IDataProtectionProvider proveedor, IRelojDelSistema reloj)
    : IProtectorDeEstadoOAuth
{
    public const string Proposito = "Ways.MercadoPago.OAuthState";

    private readonly IDataProtector _protector = proveedor.CreateProtector(Proposito);

    private sealed record Sobre(EstadoOAuth Estado, DateTimeOffset Vence);

    public string Proteger(EstadoOAuth estado) =>
        _protector.Protect(JsonSerializer.Serialize(
            new Sobre(estado, reloj.Ahora.Add(ServicioDeConexionMercadoPago.VigenciaDelEstado))));

    public EstadoOAuth? Desproteger(string valor)
    {
        try
        {
            var sobre = JsonSerializer.Deserialize<Sobre>(_protector.Unprotect(valor));

            return sobre is { Estado: not null } && sobre.Vence > reloj.Ahora ? sobre.Estado : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
