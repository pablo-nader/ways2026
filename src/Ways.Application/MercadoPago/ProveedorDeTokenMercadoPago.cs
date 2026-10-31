using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Entrega el access token del vendedor para llamar a la API de Mercado Pago. No toma locks ni escribe: lee la conexión sin
/// rastrearla, y si el token está por vencer delega en <see cref="ServicioDeRenovacionDeConexionesMercadoPago.RenovarConexionAsync"/>,
/// que ya bloquea la fila, rota los dos tokens y clasifica el rechazo. Después de renovar (con éxito o no) lee la fila de nuevo, así
/// ve la renovación de otro proceso y el estado que haya dejado un rechazo.
///
/// Política ante una renovación fallida: se devuelve el token que ya está guardado solo si todavía le queda más que
/// <see cref="HolguraDeUso"/>; si no, <see cref="CodigosDeErrorDeMercadoPago.NoDisponible"/>. Un token vencido no se entrega nunca. Una
/// falla transitoria dentro del margen no corta las llamadas mientras el token siga vigente, y como el margen (1 día) es mucho menor
/// que la ventana del proceso en segundo plano (30 días), una falla pasajera casi nunca llega a vencerlo.
/// </summary>
public class ProveedorDeTokenMercadoPago(
    IWaysDbContext db,
    ServicioDeConexionMercadoPago conexiones,
    ServicioDeRenovacionDeConexionesMercadoPago renovacion,
    IAlmacenDeTokensMercadoPago almacen,
    IRelojDelSistema reloj,
    IOptions<OpcionesDeMercadoPago> opciones,
    ILogger<ProveedorDeTokenMercadoPago> log) : IProveedorDeTokenMercadoPago
{
    /// <summary>Un token que vence antes que esto desde ahora no se entrega: llegaría vencido a Mercado Pago y su 401 se confundiría
    /// con una revocación.</summary>
    public static readonly TimeSpan HolguraDeUso = TimeSpan.FromMinutes(1);

    /// <summary>El token tiene que renovarse antes de usarse: vence dentro del margen o ya venció.</summary>
    public static bool NecesitaRenovarse(DateTimeOffset accessTokenExpiraEn, DateTimeOffset ahora, TimeSpan margen) =>
        accessTokenExpiraEn <= ahora + margen;

    /// <summary>El token se puede entregar: le queda más que la holgura.</summary>
    public static bool EsUsable(DateTimeOffset accessTokenExpiraEn, DateTimeOffset ahora) =>
        accessTokenExpiraEn > ahora + HolguraDeUso;

    public async Task<TokenDelVendedorMercadoPago> ObtenerAccessTokenAsync(int idEmpresa, CancellationToken ct = default)
    {
        var conexion = ExigirActiva(await LeerAsync(idEmpresa, ct));
        var ahora = reloj.Ahora;

        if (NecesitaRenovarse(conexion.AccessTokenExpiraEn, ahora, opciones.Value.MargenDelToken))
        {
            var resultado = await renovacion.RenovarConexionAsync(conexion.Id, conexion.IdTenant, ahora, ct);
            if (resultado == ResultadoDeRenovacion.RequiereReconexion)
            {
                throw RequiereReconexion();
            }

            if (resultado is not (ResultadoDeRenovacion.Renovada or ResultadoDeRenovacion.Omitida))
            {
                log.LogWarning(
                    "La renovación previa al uso de la conexión {IdConexion} de Mercado Pago terminó en {Resultado}.",
                    conexion.Id, resultado);
            }

            conexion = ExigirActiva(await LeerAsync(idEmpresa, ct));
            if (!EsUsable(conexion.AccessTokenExpiraEn, ahora))
            {
                throw NoDisponible();
            }
        }

        string acceso;
        try
        {
            acceso = almacen.Descifrar(conexion).AccessToken;
        }
        catch (ErrorDominio error)
        {
            log.LogWarning("No se pudo leer el token de la conexión {IdConexion} de Mercado Pago ({Codigo}).", conexion.Id, error.Codigo);
            throw;
        }

        return new TokenDelVendedorMercadoPago(conexion.Id, conexion.IdUsuarioMercadoPago, acceso, conexion.AccessTokenExpiraEn);
    }

    public Task<bool> MarcarTokenRechazadoAsync(TokenDelVendedorMercadoPago token, CancellationToken ct = default) =>
        conexiones.MarcarRequiereReconexionPorRechazoAsync(token.IdConexion, token.ExpiraEn, ct);

    /// <summary>La conexión activa de la empresa; sin activa, la más reciente (para distinguir "nunca conectó" de "hay que reconectar").</summary>
    private Task<ConexionMercadoPago?> LeerAsync(int idEmpresa, CancellationToken ct) =>
        db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .OrderByDescending(c => c.Estado == EstadoConexionMercadoPago.Activa)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

    private static ConexionMercadoPago ExigirActiva(ConexionMercadoPago? conexion) => conexion?.Estado switch
    {
        EstadoConexionMercadoPago.Activa => conexion!,
        EstadoConexionMercadoPago.RequiereReconexion => throw RequiereReconexion(),
        _ => throw new ErrorDominio(
            CodigosDeErrorDeMercadoPago.NoConectada, "La empresa no tiene Mercado Pago conectado.", 409)
    };

    private static ErrorDominio RequiereReconexion() =>
        new(
            CodigosDeErrorDeMercadoPago.RequiereReconexion,
            "La conexión con Mercado Pago de esta empresa dejó de ser válida: hay que reconectarla.",
            409);

    private static ErrorDominio NoDisponible() =>
        new(CodigosDeErrorDeMercadoPago.NoDisponible, "Mercado Pago no está disponible en este momento. Probá de nuevo más tarde.", 503);
}
