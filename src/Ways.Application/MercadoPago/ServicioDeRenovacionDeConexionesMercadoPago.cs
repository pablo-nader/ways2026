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
    Fallida,

    /// <summary>Mercado Pago rechazó las credenciales de la aplicación (no el token de la conexión): la fila queda intacta y la
    /// corrida se corta, porque las mismas credenciales fallarían en todas las demás.</summary>
    CredencialesRechazadas,

    /// <summary>Solo de la renovación previa al uso: otra renovación tiene la fila bloqueada, así que no se espera ni se llama a
    /// Mercado Pago; quien llama usa el token guardado si todavía sirve.</summary>
    Ocupada,

    /// <summary>Solo de la renovación previa al uso: un intento reciente falló y el backoff todavía no venció.</summary>
    EnEspera,

    /// <summary>Mercado Pago aceptó la renovación y rotó el refresh token, pero el resultado no llegó a guardarse: el token nuevo
    /// se perdió y la fila conserva el anterior, que ya no sirve. Lo probable es que la conexión necesite reconectarse.</summary>
    RotacionPerdida
}

public sealed record ResumenDeRenovacion(
    int Seleccionadas, int Renovadas, int Omitidas, int RequierenReconexion, int Fallidas, int RotacionesPerdidas = 0,
    bool Interrumpida = false);

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
/// Un 400 <c>invalid_grant</c> (o una cuenta de Mercado Pago distinta de la guardada) deja la conexión en
/// <c>requiere_reconexion</c> y borra el material cifrado; un 5xx, un tiempo agotado o un fallo de red no toca nada y una conexión
/// que falla así no frena a las demás. Cualquier otro 4xx (credenciales de la aplicación) tampoco toca la fila, pero corta la
/// corrida: las mismas credenciales fallarían en todas. Una vez que Mercado Pago aceptó la renovación, el guardado no se cancela
/// por un apagado: la rotación ya se gastó del lado de Mercado Pago.
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

    /// <summary>Un intento fallido reciente frena el siguiente hasta que pase el backoff. Un intento fechado en el futuro (reloj
    /// retrocedido) no frena nada: el backoff nunca puede quedar más largo que el configurado.</summary>
    public static bool EstaEnBackoff(DateTimeOffset? ultimoIntento, DateTimeOffset ahora, TimeSpan backoff) =>
        ultimoIntento is { } intento && intento <= ahora && ahora < intento + backoff;

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

        int renovadas = 0, omitidas = 0, reconexion = 0, fallidas = 0, perdidas = 0;
        var interrumpida = false;
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
                case ResultadoDeRenovacion.RotacionPerdida: perdidas++; break;
                default: fallidas++; break;
            }

            if (resultado == ResultadoDeRenovacion.CredencialesRechazadas)
            {
                log.LogError(
                    "Mercado Pago rechazó las credenciales de la aplicación al renovar la conexión {IdConexion}: se corta la corrida " +
                    "sin tocar las {Restantes} conexiones restantes. Hay que revisar ClientId y ClientSecret.",
                    pendiente.Id, pendientes.Count - pendientes.IndexOf(pendiente) - 1);
                interrumpida = true;
                break;
            }
        }

        return new ResumenDeRenovacion(pendientes.Count, renovadas, omitidas, reconexion, fallidas, perdidas, interrumpida);
    }

    /// <summary>Renueva una conexión puntual con el criterio de la corrida: bajo lock y releída, solo si sigue activa y le toca.
    /// Las excepciones inesperadas suben a quien llama; <see cref="RenovarConexionesPorVencerAsync"/> las aísla por conexión.</summary>
    public Task<ResultadoDeRenovacion> RenovarConexionAsync(
        int idConexion, int idTenant, DateTimeOffset ahora, CancellationToken ct = default) =>
        RenovarAsync(idConexion, idTenant, ahora, null, ct);

    /// <summary>
    /// Renovación previa al uso de una llamada a Mercado Pago. A diferencia de la corrida, no espera el lock (<c>FOR UPDATE SKIP
    /// LOCKED</c>: si otra renovación tiene la fila devuelve <see cref="ResultadoDeRenovacion.Ocupada"/>) y respeta el backoff
    /// guardado, releído bajo el lock: con el token todavía utilizable y un intento fallido más reciente que <paramref name="backoff"/>
    /// devuelve <see cref="ResultadoDeRenovacion.EnEspera"/> sin llamar a Mercado Pago.
    /// </summary>
    public Task<ResultadoDeRenovacion> RenovarAntesDeUsarAsync(
        int idConexion, int idTenant, DateTimeOffset ahora, TimeSpan backoff, CancellationToken ct = default) =>
        RenovarAsync(idConexion, idTenant, ahora, backoff, ct);

    private async Task<ResultadoDeRenovacion> RenovarAsync(
        int idConexion, int idTenant, DateTimeOffset ahora, TimeSpan? backoffPrevio, CancellationToken ct)
    {
        var resultado = await RenovarBajoLockAsync(idConexion, idTenant, ahora, backoffPrevio, ct);
        if (resultado == ResultadoDeRenovacion.RotacionPerdida)
        {
            await RegistrarIntentoAparteAsync(idConexion, idTenant, ahora);
        }

        return resultado;
    }

    private async Task<ResultadoDeRenovacion> RenovarBajoLockAsync(
        int idConexion, int idTenant, DateTimeOffset ahora, TimeSpan? backoffPrevio, CancellationToken ct)
    {
        var umbral = UmbralDeRenovacion(ahora, opciones.Value.VentanaDeRenovacion);

        await using var alcance = alcances.CreateAsyncScope();
        using var suplantacion = alcance.ServiceProvider.GetRequiredService<ITenantActual>().Suplantar(idTenant);
        var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (backoffPrevio is null)
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT 1 FROM conexiones_mercadopago WHERE id_conexion_mercadopago = {idConexion} FOR UPDATE", ct);
            }
            else
            {
                var tomadas = await db.Database
                    .SqlQuery<int>(
                        $"SELECT 1 AS \"Value\" FROM conexiones_mercadopago WHERE id_conexion_mercadopago = {idConexion} FOR UPDATE SKIP LOCKED")
                    .ToListAsync(ct);
                if (tomadas.Count == 0)
                {
                    return ResultadoDeRenovacion.Ocupada;
                }
            }

            // La única lectura de la fila, después del lock: una desconexión o una renovación que haya commiteado mientras
            // esta transacción esperaba ya se ve acá.
            var conexion = await db.ConexionesMercadoPago
                .FirstOrDefaultAsync(c => c.Id == idConexion && c.Estado == EstadoConexionMercadoPago.Activa, ct);

            if (conexion is null || !LeToca(conexion.AccessTokenExpiraEn, umbral))
            {
                return ResultadoDeRenovacion.Omitida;
            }

            if (backoffPrevio is { } backoff
                && ProveedorDeTokenMercadoPago.EsUsable(conexion.AccessTokenExpiraEn, ahora)
                && EstaEnBackoff(conexion.FechaUltimoIntentoRenovacion, ahora, backoff))
            {
                return ResultadoDeRenovacion.EnEspera;
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
                await GuardarIntentoFallidoAsync(db, transaccion, conexion, ahora, ct);
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
            catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.CredencialesRechazadas)
            {
                log.LogWarning(
                    "Mercado Pago rechazó las credenciales de la aplicación al renovar la conexión {IdConexion}; solo se anota el intento.",
                    idConexion);
                await GuardarIntentoFallidoAsync(db, transaccion, conexion, ahora, ct);
                return ResultadoDeRenovacion.CredencialesRechazadas;
            }
            catch (ErrorDominio)
            {
                log.LogWarning(
                    "Mercado Pago no pudo renovar la conexión {IdConexion}; queda como estaba y se reintenta en la próxima corrida.",
                    idConexion);
                await GuardarIntentoFallidoAsync(db, transaccion, conexion, ahora, ct);
                return ResultadoDeRenovacion.Fallida;
            }

            // Desde acá Mercado Pago ya rotó el refresh token: guardar no depende de la cancelación del llamador (un apagado
            // no debe abortar una rotación gastada) y una falla ya no es reintentable.
            try
            {
                if (nuevos.IdUsuario != conexion.IdUsuarioMercadoPago)
                {
                    log.LogWarning(
                        "La renovación de la conexión {IdConexion} devolvió otra cuenta de Mercado Pago: requiere reconexión.",
                        idConexion);
                    await MarcarRequiereReconexionAsync(db, transaccion, conexion, ahora, CancellationToken.None);
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

                await db.SaveChangesAsync(CancellationToken.None);
                await transaccion.CommitAsync(CancellationToken.None);

                return ResultadoDeRenovacion.Renovada;
            }
            catch (Exception error)
            {
                log.LogError(
                    error,
                    "Mercado Pago rotó el refresh token de la conexión {IdConexion} (empresa {IdEmpresa}) pero no se pudo guardar: " +
                    "la rotación se perdió y la conexión probablemente necesite reconectarse.",
                    idConexion, conexion.IdEmpresa);
                return ResultadoDeRenovacion.RotacionPerdida;
            }
        });
    }

    /// <summary>Anota la hora del intento fallido y commitea, todavía con el lock. Si no se puede guardar, el resultado del intento
    /// no cambia: solo se pierde el backoff.</summary>
    private async Task GuardarIntentoFallidoAsync(
        IWaysDbContext db, IDbContextTransaction transaccion, ConexionMercadoPago conexion, DateTimeOffset ahora, CancellationToken ct)
    {
        try
        {
            conexion.FechaUltimoIntentoRenovacion = ahora;
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "No se pudo anotar el intento fallido de renovar la conexión {IdConexion} de Mercado Pago.", conexion.Id);
        }
    }

    /// <summary>Anota el intento cuando la transacción de la renovación ya no sirve (la rotación se perdió): un UPDATE aparte, que si
    /// falla solo se registra.</summary>
    private async Task RegistrarIntentoAparteAsync(int idConexion, int idTenant, DateTimeOffset ahora)
    {
        try
        {
            await using var alcance = alcances.CreateAsyncScope();
            using var suplantacion = alcance.ServiceProvider.GetRequiredService<ITenantActual>().Suplantar(idTenant);
            var db = alcance.ServiceProvider.GetRequiredService<IWaysDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE conexiones_mercadopago SET fecha_ultimo_intento_renovacion = {ahora} WHERE id_conexion_mercadopago = {idConexion}",
                CancellationToken.None);
        }
        catch (Exception error)
        {
            log.LogWarning(error, "No se pudo anotar el intento perdido de renovar la conexión {IdConexion} de Mercado Pago.", idConexion);
        }
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
