using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

public enum ResultadoDeRenovacion
{
    Renovada,

    /// <summary>Ya no estaba activa o ya no le tocaba renovarse cuando se la bloqueó.</summary>
    Omitida,

    RequiereReconexion,

    /// <summary>Falla de Mercado Pago, de red o ilegible localmente: la conexión queda como estaba y se reintenta en la próxima corrida.</summary>
    Fallida
}

public sealed record ResumenDeRenovacion(int Seleccionadas, int Renovadas, int Omitidas, int RequierenReconexion, int Fallidas);

/// <summary>
/// Renueva los tokens de Mercado Pago antes de que venzan, en todos los tenants. Cada renovación rota el refresh token (el
/// anterior deja de servir), así que el orden importa: dentro de una transacción propia y con el contexto del tenant de la
/// conexión se bloquea la fila (<c>FOR UPDATE</c>), se la lee UNA vez después del lock, se llama a Mercado Pago y se guardan los
/// dos tokens nuevos antes del commit.
///
/// La llamada HTTP corre con la fila bloqueada, a propósito: dos procesos que renovaran la misma conexión a la vez gastarían el
/// mismo refresh token y uno perdería la rotación; con el lock el segundo espera, relee la fila ya renovada y la omite. El costo
/// es que una desconexión de esa empresa espera hasta el timeout del cliente HTTP (15 s), y nada más: las lecturas no toman lock.
/// Nunca se llama dos veces a Mercado Pago por la misma conexión en una corrida: la estrategia es sin reintento y cada conexión
/// se procesa una sola vez.
///
/// Un rechazo 4xx (o una cuenta de Mercado Pago distinta de la guardada) deja la conexión en <c>requiere_reconexion</c> y borra
/// el material cifrado; un 5xx, un tiempo agotado o un fallo de red no toca nada. Una conexión que falla no frena a las demás.
/// </summary>
public class ServicioDeRenovacionDeConexionesMercadoPago(
    IServiceScopeFactory alcances,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IOptions<OpcionesDeMercadoPago> opciones,
    IClienteOAuthMercadoPago cliente,
    IAlmacenDeTokensMercadoPago almacen,
    ILogger<ServicioDeRenovacionDeConexionesMercadoPago> log)
{
    /// <summary>Instante a partir del cual una conexión que vence antes o en él tiene que renovarse.</summary>
    public static DateTimeOffset UmbralDeRenovacion(DateTimeOffset ahora, TimeSpan ventana) => ahora + ventana;

    public static bool LeToca(DateTimeOffset accessTokenExpiraEn, DateTimeOffset umbral) => accessTokenExpiraEn <= umbral;

    public async Task<ResumenDeRenovacion> RenovarConexionesPorVencerAsync(DateTimeOffset ahora, CancellationToken ct = default)
    {
        if (!opciones.Value.EstaConfigurado || !almacen.PuedeCifrar)
        {
            log.LogWarning("La renovación de conexiones de Mercado Pago no corre: falta la configuración o la clave maestra.");
            return new ResumenDeRenovacion(0, 0, 0, 0, 0);
        }

        var umbral = UmbralDeRenovacion(ahora, opciones.Value.VentanaDeRenovacion);

        var pendientes = await dbPlataforma.ConexionesMercadoPago
            .Where(c => c.Estado == EstadoConexionMercadoPago.Activa && c.AccessTokenExpiraEn <= umbral)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.IdTenant })
            .ToListAsync(ct);

        int renovadas = 0, omitidas = 0, reconexion = 0, fallidas = 0;
        foreach (var pendiente in pendientes)
        {
            ct.ThrowIfCancellationRequested();

            ResultadoDeRenovacion resultado;
            try
            {
                resultado = await RenovarConexionAsync(pendiente.Id, pendiente.IdTenant, ahora, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(
                    error, "Falló la renovación de la conexión {IdConexion} de Mercado Pago; se reintenta en la próxima corrida.",
                    pendiente.Id);
                resultado = ResultadoDeRenovacion.Fallida;
            }

            switch (resultado)
            {
                case ResultadoDeRenovacion.Renovada: renovadas++; break;
                case ResultadoDeRenovacion.Omitida: omitidas++; break;
                case ResultadoDeRenovacion.RequiereReconexion: reconexion++; break;
                default: fallidas++; break;
            }
        }

        return new ResumenDeRenovacion(pendientes.Count, renovadas, omitidas, reconexion, fallidas);
    }

    /// <summary>Renueva una conexión puntual con el criterio de la corrida: bajo lock y releída, solo si sigue activa y le toca.
    /// Las excepciones inesperadas suben a quien llama; <see cref="RenovarConexionesPorVencerAsync"/> las aísla por conexión.</summary>
    public async Task<ResultadoDeRenovacion> RenovarConexionAsync(
        int idConexion, int idTenant, DateTimeOffset ahora, CancellationToken ct = default)
    {
        var umbral = UmbralDeRenovacion(ahora, opciones.Value.VentanaDeRenovacion);

        await using var alcance = alcances.CreateAsyncScope();
        using var suplantacion = alcance.ServiceProvider.GetRequiredService<ITenantActual>().Suplantar(idTenant);
        var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM conexiones_mercadopago WHERE id_conexion_mercadopago = {idConexion} FOR UPDATE", ct);

            // La única lectura de la fila, después del lock: una desconexión o una renovación que haya commiteado mientras
            // esta transacción esperaba ya se ve acá.
            var conexion = await db.ConexionesMercadoPago
                .FirstOrDefaultAsync(c => c.Id == idConexion && c.Estado == EstadoConexionMercadoPago.Activa, ct);

            if (conexion is null || !LeToca(conexion.AccessTokenExpiraEn, umbral))
            {
                return ResultadoDeRenovacion.Omitida;
            }

            string refreshActual;
            try
            {
                refreshActual = almacen.Descifrar(conexion).RefreshToken;
            }
            catch (ErrorDominio)
            {
                log.LogWarning(
                    "Los tokens de la conexión {IdConexion} de Mercado Pago no se pudieron descifrar; no se renueva.", idConexion);
                return ResultadoDeRenovacion.Fallida;
            }

            TokensDeMercadoPago nuevos;
            try
            {
                nuevos = await cliente.RenovarAsync(refreshActual, ct);
            }
            catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.RenovacionRechazada)
            {
                log.LogWarning(
                    "Mercado Pago rechazó la renovación de la conexión {IdConexion} (empresa {IdEmpresa}): requiere reconexión.",
                    idConexion, conexion.IdEmpresa);
                await MarcarRequiereReconexionAsync(db, transaccion, conexion, ahora, ct);
                return ResultadoDeRenovacion.RequiereReconexion;
            }
            catch (ErrorDominio)
            {
                log.LogWarning(
                    "Mercado Pago no pudo renovar la conexión {IdConexion}; queda como estaba y se reintenta en la próxima corrida.",
                    idConexion);
                return ResultadoDeRenovacion.Fallida;
            }

            if (nuevos.IdUsuario != conexion.IdUsuarioMercadoPago)
            {
                log.LogWarning(
                    "La renovación de la conexión {IdConexion} devolvió otra cuenta de Mercado Pago: requiere reconexión.",
                    idConexion);
                await MarcarRequiereReconexionAsync(db, transaccion, conexion, ahora, ct);
                return ResultadoDeRenovacion.RequiereReconexion;
            }

            var cifrados = almacen.Cifrar(
                conexion.IdTenant, conexion.IdEmpresa, conexion.Id, nuevos.AccessToken, nuevos.RefreshToken);

            conexion.AccessTokenCifrado = cifrados.AccessTokenCifrado;
            conexion.AccessTokenNonce = cifrados.AccessTokenNonce;
            conexion.AccessTokenTag = cifrados.AccessTokenTag;
            conexion.RefreshTokenCifrado = cifrados.RefreshTokenCifrado;
            conexion.RefreshTokenNonce = cifrados.RefreshTokenNonce;
            conexion.RefreshTokenTag = cifrados.RefreshTokenTag;
            conexion.IdClaveMaestra = cifrados.IdClaveMaestra;
            conexion.AccessTokenExpiraEn = ahora.AddSeconds(nuevos.ExpiraEnSegundos);
            conexion.FechaUltimaRenovacion = ahora;
            conexion.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return ResultadoDeRenovacion.Renovada;
        });
    }

    private static async Task MarcarRequiereReconexionAsync(
        IWaysDbContext db, IDbContextTransaction transaccion, ConexionMercadoPago conexion, DateTimeOffset ahora, CancellationToken ct)
    {
        conexion.Estado = EstadoConexionMercadoPago.RequiereReconexion;
        conexion.FechaRequiereReconexion = ahora;
        conexion.UpdatedAt = ahora;
        conexion.AccessTokenCifrado = null;
        conexion.AccessTokenNonce = null;
        conexion.AccessTokenTag = null;
        conexion.RefreshTokenCifrado = null;
        conexion.RefreshTokenNonce = null;
        conexion.RefreshTokenTag = null;

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
    }
}
