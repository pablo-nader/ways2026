using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Elige la terminal Point de la caja de Mercado Pago de un punto de venta y la pasa a modo <c>PDV</c>. Todo lo usa un Admin (la política
/// se aplica en la capa de API). Orden de los errores: 404 del punto de venta, 400 del identificador de la terminal, 409 de la conexión
/// ausente o pendiente de reconexión, 409 <c>mercadopago_caja_no_registrada</c> si el punto de venta todavía no es una caja de la cuenta.
///
/// <b>La vinculación no es de Ways.</b> Una terminal se vincula a un local y a una caja solo en la app móvil de Mercado Pago (se enciende
/// la terminal, se escanea el QR de su pantalla y se elige local y caja); la API únicamente la lista y le cambia el modo. Por eso el
/// listado pide las terminales de la caja (<c>pos_id</c>) y solo una que figure ahí, con ese <c>pos_id</c>, se puede asignar; si no, 409
/// <c>mercadopago_terminal_no_vinculada</c> con las instrucciones. Una caja admite una terminal.
///
/// <b>Modo.</b> Si la terminal ya está en <c>PDV</c> no se manda ningún <c>PATCH /terminals/v1/setup</c>; si no, se manda con una clave de
/// idempotencia derivada de la terminal y el modo. Los rechazos de ese <c>PATCH</c> son 409 con mensaje propio: 403
/// <c>terminal_not_allowed_action</c> (modelo no admitido), 403 <c>store_pos_not_found</c> (la terminal perdió su vinculación) y 412 (la
/// caja ya tiene otra terminal en <c>PDV</c> o suspendida). Mercado Pago pide reiniciar la terminal para que tome el modo.
///
/// <b>Locks</b> (<c>single-read-under-lock</c>): el mismo orden sucursal → punto de venta que <see cref="ServicioDeCajasMercadoPago"/>, así
/// una nueva registración del local o de la caja no corre en paralelo. El punto de venta, la conexión y la caja se leen UNA vez, después de
/// los locks; si el punto de venta cambió de sucursal entre la lectura que eligió el lock y la de después, 409 y no se escribe nada.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): la escritura sigue a un cambio de modo en Mercado Pago que no se puede
/// repetir a ciegas; si el guardado falla, el próximo intento encuentra la terminal ya en <c>PDV</c>, no manda otro <c>PATCH</c> y guarda.
/// Un 401 deja la conexión en <c>requiere_reconexion</c> dentro de la transacción, así que se confirma igual antes de relanzar el error.
///
/// <b>Quitar la terminal</b> solo borra la asignación en Ways: no toca Mercado Pago ni el modo de la terminal.
/// </summary>
public class ServicioDeTerminalesMercadoPago(
    IWaysDbContext db,
    IClienteApiMercadoPago api,
    IRelojDelSistema reloj,
    ServicioDeAuditoria auditoria)
{
    private const string RutaDeModo = "/terminals/v1/setup";

    /// <summary>Cómo vincular una terminal Point a la caja; solo se muestra cuando el listado viene vacío.</summary>
    public const string InstruccionesDeVinculacion =
        "Para vincular la terminal: encendela, abrí la app de Mercado Pago con la cuenta conectada, escaneá el código QR que muestra " +
        "su pantalla y elegí el local y la caja de este punto de venta. Después volvé a consultar. Solo se admiten las terminales " +
        "Newland N950 y PAX A910, y una caja admite una sola.";

    public async Task<ListadoDeTerminalesDelPuntoDeVenta> ListarAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        var puntoVenta = await LeerPuntoVentaAsync(idPuntoVenta, ct);
        var idUsuario = await LeerIdUsuarioAsync(puntoVenta.IdEmpresa, ct);
        var caja = await db.CajasMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == idPuntoVenta && c.IdUsuarioMercadoPago == idUsuario, ct)
            ?? throw CajaNoRegistrada();

        var terminales = await ListarDeLaCajaAsync(puntoVenta.IdEmpresa, caja.IdPosMercadoPago, ct);
        var vistas = terminales
            .Select(t => new TerminalDelPuntoDeVenta(t.Id, t.ModoOperativo, caja.IdTerminalPoint == t.Id))
            .ToList();

        return new ListadoDeTerminalesDelPuntoDeVenta(vistas, vistas.Count == 0 ? InstruccionesDeVinculacion : null);
    }

    /// <summary>Asigna la terminal a la caja y la pasa a <c>PDV</c> si no lo estaba. Idempotente: repetir la misma asignación con la
    /// terminal ya en <c>PDV</c> no llama a Mercado Pago más que para listar ni escribe nada.</summary>
    public async Task<AsignacionDeTerminalMercadoPago> AsignarAsync(
        int idPuntoVenta, SolicitudDeTerminalMercadoPago? solicitud, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            // El 404 va antes que la validación del cuerpo; esta lectura previa solo elige el lock de sucursal.
            var idSucursal = (await LeerPuntoVentaAsync(idPuntoVenta, ct)).IdSucursal;

            var idTerminal = TerminalPointMercadoPago.Normalizar(solicitud?.IdTerminal)
                ?? throw new ErrorDominio(
                    TerminalPointMercadoPago.CodigoDeIdInvalido,
                    "El identificador de la terminal no es válido: tiene la forma TIPO__SERIE, por ejemplo PAX_A910__8701.",
                    400);

            return await EnTransaccionAsync(
                idPuntoVenta, idSucursal, ct,
                (puntoVenta, idUsuario) => AsignarBajoLockAsync(puntoVenta, idPuntoVenta, idUsuario, idTerminal, ct));
        });
    }

    /// <summary>Borra la asignación de la terminal en Ways. Idempotente: sin terminal asignada no cambia nada.</summary>
    public async Task<AsignacionDeTerminalMercadoPago> QuitarAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            var idSucursal = (await LeerPuntoVentaAsync(idPuntoVenta, ct)).IdSucursal;

            return await EnTransaccionAsync(
                idPuntoVenta, idSucursal, ct, (puntoVenta, idUsuario) => QuitarBajoLockAsync(puntoVenta, idPuntoVenta, idUsuario, ct));
        });
    }

    private async Task<AsignacionDeTerminalMercadoPago> EnTransaccionAsync(
        int idPuntoVenta, int idSucursal, CancellationToken ct,
        Func<PuntoVentaLeido, long, Task<AsignacionDeTerminalMercadoPago>> bajoLock)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        await ServicioDeCajasMercadoPago.TomarLockAsync(db, idSucursal, ServicioDeLocalesMercadoPago.ClaveDeLockDeLocal, ct);
        await ServicioDeCajasMercadoPago.TomarLockAsync(db, idPuntoVenta, ServicioDeCajasMercadoPago.ClaveDeLockDeCaja, ct);

        var puntoVenta = await LeerPuntoVentaAsync(idPuntoVenta, ct);
        if (puntoVenta.IdSucursal != idSucursal)
        {
            throw ErrorDominio.Conflicto(
                "mercadopago_punto_venta_movido",
                "El punto de venta cambió de sucursal mientras se asignaba la terminal: intentalo de nuevo.");
        }

        ErrorDominio? reconexion = null;
        AsignacionDeTerminalMercadoPago? resultado = null;
        try
        {
            var idUsuario = await LeerIdUsuarioAsync(puntoVenta.IdEmpresa, ct);
            resultado = await bajoLock(puntoVenta, idUsuario);
        }
        catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.RequiereReconexion)
        {
            reconexion = error;
        }

        await transaccion.CommitAsync(ct);

        return reconexion is null ? resultado! : throw reconexion;
    }

    private async Task<AsignacionDeTerminalMercadoPago> AsignarBajoLockAsync(
        PuntoVentaLeido puntoVenta, int idPuntoVenta, long idUsuario, string idTerminal, CancellationToken ct)
    {
        var caja = await db.CajasMercadoPago
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == idPuntoVenta && c.IdUsuarioMercadoPago == idUsuario, ct)
            ?? throw CajaNoRegistrada();

        var terminal = (await ListarDeLaCajaAsync(puntoVenta.IdEmpresa, caja.IdPosMercadoPago, ct))
            .FirstOrDefault(t => string.Equals(t.Id, idTerminal, StringComparison.Ordinal))
            ?? throw TerminalNoVinculada();

        var modoCambiado = false;
        if (!TerminalPointMercadoPago.EstaEnPdv(terminal.ModoOperativo))
        {
            await PasarAPdvAsync(puntoVenta.IdEmpresa, idPuntoVenta, idTerminal, ct);
            modoCambiado = true;
        }

        if (!modoCambiado && caja.IdTerminalPoint == idTerminal)
        {
            return new AsignacionDeTerminalMercadoPago(caja.IdTerminalPoint, caja.FechaTerminal, false);
        }

        var ahora = reloj.Ahora;
        var anterior = caja.IdTerminalPoint;
        caja.IdTerminalPoint = idTerminal;
        caja.FechaTerminal = ahora;
        caja.UpdatedAt = ahora;

        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.TerminalDeMercadoPago(
            "asignacion", idUsuario, caja.IdPosMercadoPago, anterior, idTerminal, modoCambiado);
        auditoria.Registrar(new RegistroDeAuditoria(
            puntoVenta.IdTenant, idPuntoVenta, AccionAuditada.PuntoVentaTerminalMercadoPago, idPuntoVenta, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        return new AsignacionDeTerminalMercadoPago(idTerminal, ahora, modoCambiado);
    }

    private async Task<AsignacionDeTerminalMercadoPago> QuitarBajoLockAsync(
        PuntoVentaLeido puntoVenta, int idPuntoVenta, long idUsuario, CancellationToken ct)
    {
        var caja = await db.CajasMercadoPago
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == idPuntoVenta && c.IdUsuarioMercadoPago == idUsuario, ct)
            ?? throw CajaNoRegistrada();

        if (caja.IdTerminalPoint is null)
        {
            return new AsignacionDeTerminalMercadoPago(null, null, false);
        }

        var anterior = caja.IdTerminalPoint;
        caja.IdTerminalPoint = null;
        caja.FechaTerminal = null;
        caja.UpdatedAt = reloj.Ahora;

        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.TerminalDeMercadoPago(
            "desasignacion", idUsuario, caja.IdPosMercadoPago, anterior, null, false);
        auditoria.Registrar(new RegistroDeAuditoria(
            puntoVenta.IdTenant, idPuntoVenta, AccionAuditada.PuntoVentaTerminalMercadoPago, idPuntoVenta, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        return new AsignacionDeTerminalMercadoPago(null, null, false);
    }

    /// <summary>La clave lleva la terminal y el modo pedido: reintentar el mismo cambio repite la clave y Mercado Pago contesta lo ya
    /// guardado en vez de aplicarlo dos veces.</summary>
    private async Task PasarAPdvAsync(int idEmpresa, int idPuntoVenta, string idTerminal, CancellationToken ct)
    {
        var cuerpo = new SolicitudDeModoDeTerminalMercadoPago(
            [new ModoDeTerminalMercadoPago(idTerminal, TerminalPointMercadoPago.ModoPdv)]);
        var huella = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{idTerminal}|{TerminalPointMercadoPago.ModoPdv}")))[..16];

        try
        {
            await api.EnviarAsync<RespuestaDeModoDeTerminalMercadoPago>(
                idEmpresa, HttpMethod.Patch, RutaDeModo, cuerpo, $"ways-terminal-{idPuntoVenta}-{huella}", ct);
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 412)
        {
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeMercadoPago.CajaConOtraTerminalEnPdv,
                "La caja ya tiene otra terminal en modo PDV o suspendida en Mercado Pago, y una caja admite una sola. Desvinculá la otra " +
                "terminal en la app de Mercado Pago y volvé a intentarlo.");
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 403 && error.CodigoMercadoPago == "terminal_not_allowed_action")
        {
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeMercadoPago.TerminalModeloNoAdmitido,
                "Mercado Pago no permite cambiar el modo de este modelo de terminal. Solo se admiten las terminales Newland N950 y PAX A910.");
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 403 && error.CodigoMercadoPago == "store_pos_not_found")
        {
            throw TerminalNoVinculada();
        }
    }

    /// <summary>Las terminales que Mercado Pago tiene vinculadas a la caja. El filtro <c>pos_id</c> lo aplica Mercado Pago; se repite
    /// acá para que una terminal de otra caja nunca se pueda asignar a esta.</summary>
    private async Task<IReadOnlyList<TerminalMercadoPago>> ListarDeLaCajaAsync(int idEmpresa, long idPos, CancellationToken ct)
    {
        var listado = await api.ListarTerminalesAsync(idEmpresa, idPos: idPos, ct: ct);
        return [.. listado.Terminales.Where(t => t.IdPos == idPos)];
    }

    private static ErrorDominio TerminalNoVinculada() =>
        ErrorDominio.Conflicto(
            CodigosDeErrorDeMercadoPago.TerminalNoVinculada,
            "Esa terminal no está vinculada a la caja de este punto de venta en Mercado Pago. " + InstruccionesDeVinculacion);

    private static ErrorDominio CajaNoRegistrada() =>
        ErrorDominio.Conflicto(
            CodigosDeErrorDeMercadoPago.CajaNoRegistrada,
            "El punto de venta todavía no está registrado como caja en Mercado Pago: registralo primero.");

    private async Task<PuntoVentaLeido> LeerPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta
            .AsNoTracking()
            .Where(p => p.Id == idPuntoVenta)
            .Select(p => new PuntoVentaLeido(p.IdTenant, p.IdEmpresa, p.IdSucursal))
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    /// <summary>El vendedor de la conexión activa de la empresa; sin activa, 409 según la más reciente (nunca conectó o hay que reconectar).</summary>
    private async Task<long> LeerIdUsuarioAsync(int idEmpresa, CancellationToken ct)
    {
        var conexion = await db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .OrderByDescending(c => c.Estado == EstadoConexionMercadoPago.Activa)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        return conexion switch
        {
            { Estado: EstadoConexionMercadoPago.Activa } => conexion.IdUsuarioMercadoPago,
            { Estado: EstadoConexionMercadoPago.RequiereReconexion } => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RequiereReconexion,
                "La conexión con Mercado Pago de esta empresa dejó de ser válida: hay que reconectarla.",
                409),
            _ => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.NoConectada, "La empresa no tiene Mercado Pago conectado.", 409)
        };
    }

    private sealed record PuntoVentaLeido(int IdTenant, int IdEmpresa, int IdSucursal);
}
