using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Registra un punto de venta como caja (<i>POS</i>) de la cuenta de Mercado Pago conectada a su empresa, colgada del local de su
/// sucursal, y guarda el vínculo (<c>cajas_mercadopago</c>). Todo lo usa un Admin (la política se aplica en la capa de API). Orden de
/// los errores: 404 del punto de venta, 400 del nombre sin nada admitido, 409 de la conexión ausente o pendiente de reconexión, 409
/// <c>mercadopago_local_no_registrado</c> si su sucursal todavía no es un local de la cuenta.
///
/// <b>Mudanza de sucursal.</b> Registrar de nuevo un punto de venta que cambió de sucursal manda el <c>store_id</c> del local de la
/// sucursal actual en el <c>PATCH /v2/pos/{id}</c>: así Mercado Pago reapunta la caja y el vínculo guarda el local nuevo. Mover el punto
/// de venta no toca este vínculo ni lo bloquea; solo lo deja <c>desactualizada</c>. También queda <c>desactualizada</c> si el local de
/// la sucursal se volvió a registrar con otro <c>id</c> de Mercado Pago (el vínculo guarda el <c>id_store_mercadopago</c> con el que se
/// registró la caja).
///
/// <b>Locks</b> (<c>single-read-under-lock</c>): orden fijo sucursal → punto de venta. Primero el <c>pg_advisory_xact_lock</c> de la
/// sucursal del punto de venta (el mismo de <see cref="ServicioDeLocalesMercadoPago"/>, que toma solo ese), así una nueva registración
/// del local no cambia su <c>id</c> a mitad de la llamada; después el del punto de venta, que serializa dos clics. Ambos se mantienen
/// mientras se llama a Mercado Pago, así que el segundo espera el resultado del primero y encuentra el vínculo ya guardado. El punto
/// de venta, el local y el vínculo se leen UNA vez, después de los locks; si el punto de venta cambió de sucursal entre la lectura que
/// eligió el lock y la de después, responde 409 y no registra nada. Los locks no cubren la edición ni el movimiento del punto de venta
/// (esos toman otros locks): una edición concurrente deja el vínculo desactualizado porque la fecha de sincronización es la de la
/// lectura del punto de venta, no la del fin de la llamada.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): la escritura agrega el vínculo y una fila de auditoría después de
/// una llamada externa que no se puede repetir a ciegas. Si Mercado Pago creó la caja y el guardado falla, el próximo intento la
/// encuentra por <c>external_id</c> y la adopta en vez de duplicarla; lo mismo si el alta contesta 409 <c>pos_already_exists</c>.
///
/// Un 401 de Mercado Pago deja la conexión en <c>requiere_reconexion</c> dentro de esta misma transacción, así que se confirma
/// igual antes de relanzar el error: de lo contrario el rollback desharía esa marca.
///
/// Los campos de Mercado Pago (<c>id</c>, <c>external_id</c>, <c>store_id</c>, <c>qr_response.image</c>, la búsqueda <c>{paging, data}</c>)
/// están verificados contra la referencia de la API (2026-11-01) pero no se ejercitaron en vivo; los DTO ignoran lo que no usan.
/// </summary>
public class ServicioDeCajasMercadoPago(
    IWaysDbContext db,
    IClienteApiMercadoPago api,
    IRelojDelSistema reloj,
    ServicioDeAuditoria auditoria)
{
    /// <summary>Segunda clave del <c>pg_advisory_xact_lock(int, int)</c>; la primera es el id del punto de venta.</summary>
    public const int ClaveDeLockDeCaja = 0x4D504350;

    private const string RutaDeCajas = "/v2/pos";

    private const int LargoMaximoDeUrlQr = 500;

    public async Task<EstadoDeCajaMercadoPago> ObtenerAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        var puntoVenta = await LeerPuntoVentaAsync(idPuntoVenta, ct);

        var conexion = await LeerConexionAsync(puntoVenta.IdEmpresa, ct);
        if (conexion is not { Estado: EstadoConexionMercadoPago.Activa })
        {
            return Estado(EstadosDeCajaMercadoPago.SinConexion);
        }

        var local = await db.LocalesMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.IdSucursal == puntoVenta.IdSucursal && l.IdUsuarioMercadoPago == conexion.IdUsuarioMercadoPago, ct);
        if (local is null)
        {
            return Estado(EstadosDeCajaMercadoPago.LocalNoRegistrado);
        }

        var caja = await db.CajasMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == idPuntoVenta && c.IdUsuarioMercadoPago == conexion.IdUsuarioMercadoPago, ct);
        if (caja is null)
        {
            return Estado(EstadosDeCajaMercadoPago.NoRegistrada);
        }

        var desactualizada = puntoVenta.UpdatedAt > caja.FechaSincronizacion
            || caja.IdLocalMercadoPago != local.Id
            || caja.IdStoreMercadoPago != local.IdStoreMercadoPago;

        return new EstadoDeCajaMercadoPago(
            desactualizada ? EstadosDeCajaMercadoPago.Desactualizada : EstadosDeCajaMercadoPago.Registrada,
            caja.IdPosMercadoPago, caja.IdStoreMercadoPago, caja.ExternalId, caja.UrlQr, caja.FechaRegistro, caja.FechaSincronizacion,
            caja.IdTerminalPoint);
    }

    /// <summary>
    /// Crea la caja en Mercado Pago, adopta la que ya existe con el mismo <c>external_id</c> o actualiza el vínculo con los datos
    /// actuales del punto de venta y el local de su sucursal, según lo que encuentre bajo el lock. Devuelve el estado resultante.
    /// </summary>
    public async Task<EstadoDeCajaMercadoPago> RegistrarAsync(int idPuntoVenta, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            // Lectura previa solo para saber qué lock de sucursal tomar; el punto de venta se lee de nuevo bajo los locks.
            var idSucursal = (await LeerPuntoVentaAsync(idPuntoVenta, ct)).IdSucursal;

            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await TomarLockAsync(db, idSucursal, ServicioDeLocalesMercadoPago.ClaveDeLockDeLocal, ct);
            await TomarLockAsync(db, idPuntoVenta, ClaveDeLockDeCaja, ct);

            ErrorDominio? reconexion = null;
            EstadoDeCajaMercadoPago? resultado = null;
            try
            {
                resultado = await RegistrarBajoLockAsync(idPuntoVenta, idSucursal, ct);
            }
            catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.RequiereReconexion)
            {
                reconexion = error;
            }

            await transaccion.CommitAsync(ct);

            return reconexion is null ? resultado! : throw reconexion;
        });
    }

    private async Task<EstadoDeCajaMercadoPago> RegistrarBajoLockAsync(int idPuntoVenta, int idSucursalBloqueada, CancellationToken ct)
    {
        // La fecha se toma antes de leer el punto de venta: una edición que confirme después de la lectura queda posterior a ella y
        // el vínculo figura desactualizado.
        var sincronizadoEn = reloj.Ahora;
        var puntoVenta = await LeerPuntoVentaAsync(idPuntoVenta, ct);
        if (puntoVenta.IdSucursal != idSucursalBloqueada)
        {
            throw ErrorDominio.Conflicto(
                "mercadopago_punto_venta_movido",
                "El punto de venta cambió de sucursal mientras se registraba: intentalo de nuevo.");
        }

        var nombre = CajaParaMercadoPago.NombreDe(puntoVenta.Nombre);
        if (nombre.Length == 0)
        {
            throw new ErrorDominio(
                CajaParaMercadoPago.CodigoDeNombreInvalido,
                "El nombre del punto de venta no tiene ningún carácter que Mercado Pago admita: renombralo antes de registrarlo.",
                400);
        }

        var conexion = await LeerConexionAsync(puntoVenta.IdEmpresa, ct);
        var idUsuario = conexion switch
        {
            { Estado: EstadoConexionMercadoPago.Activa } => conexion.IdUsuarioMercadoPago,
            { Estado: EstadoConexionMercadoPago.RequiereReconexion } => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RequiereReconexion,
                "La conexión con Mercado Pago de esta empresa dejó de ser válida: hay que reconectarla.",
                409),
            _ => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.NoConectada, "La empresa no tiene Mercado Pago conectado.", 409)
        };

        var local = await db.LocalesMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.IdSucursal == puntoVenta.IdSucursal && l.IdUsuarioMercadoPago == idUsuario, ct)
            ?? throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.LocalNoRegistrado,
                "La sucursal del punto de venta todavía no está registrada como local en Mercado Pago: registrala primero.",
                409);

        var vinculo = await db.CajasMercadoPago
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == idPuntoVenta && c.IdUsuarioMercadoPago == idUsuario, ct);

        var externalId = CajaParaMercadoPago.ExternalIdDe(idPuntoVenta);
        var cuerpo = new SolicitudDeCajaMercadoPago(
            nombre, externalId, local.IdStoreMercadoPago.ToString(CultureInfo.InvariantCulture), ConfiguracionDeCajaMercadoPago.ModoPdv);
        var estadoDelPuntoVenta = puntoVenta.UpdatedAt.UtcTicks;

        ResultadoDeEnvio enviado;
        if (vinculo is not null)
        {
            try
            {
                enviado = await ActualizarAsync(
                    puntoVenta.IdEmpresa, vinculo.IdPosMercadoPago, cuerpo, idPuntoVenta, estadoDelPuntoVenta, "actualizacion", ct);
            }
            catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404 && error.CodigoMercadoPago != "store_not_found")
            {
                // La caja se borró en Mercado Pago: se la busca por external_id y, si tampoco está, se la crea de nuevo.
                enviado = await AdoptarOCrearAsync(puntoVenta.IdEmpresa, externalId, cuerpo, idPuntoVenta, estadoDelPuntoVenta, ct);
            }
        }
        else
        {
            enviado = await AdoptarOCrearAsync(puntoVenta.IdEmpresa, externalId, cuerpo, idPuntoVenta, estadoDelPuntoVenta, ct);
        }

        var ahora = reloj.Ahora;
        var urlQr = enviado.UrlQr ?? vinculo?.UrlQr;
        if (vinculo is null)
        {
            vinculo = new CajaMercadoPago
            {
                IdTenant = puntoVenta.IdTenant,
                IdEmpresa = puntoVenta.IdEmpresa,
                IdPuntoVenta = idPuntoVenta,
                IdLocalMercadoPago = local.Id,
                IdStoreMercadoPago = local.IdStoreMercadoPago,
                IdUsuarioMercadoPago = idUsuario,
                IdPosMercadoPago = enviado.IdPos,
                ExternalId = externalId,
                UrlQr = urlQr,
                FechaRegistro = ahora,
                FechaSincronizacion = sincronizadoEn,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.CajasMercadoPago.Add(vinculo);
        }
        else
        {
            vinculo.IdLocalMercadoPago = local.Id;
            vinculo.IdStoreMercadoPago = local.IdStoreMercadoPago;
            vinculo.IdPosMercadoPago = enviado.IdPos;
            vinculo.UrlQr = urlQr;
            vinculo.FechaSincronizacion = sincronizadoEn;
            vinculo.UpdatedAt = ahora;
        }

        var (anterior, nuevo) = PayloadDeAuditoria.CajaDeMercadoPago(
            enviado.Operacion, idUsuario, local.IdStoreMercadoPago, enviado.IdPos, externalId, sincronizadoEn);
        auditoria.Registrar(new RegistroDeAuditoria(
            puntoVenta.IdTenant, idPuntoVenta, AccionAuditada.PuntoVentaMercadoPago, idPuntoVenta, anterior, nuevo));
        await db.SaveChangesAsync(ct);

        return new EstadoDeCajaMercadoPago(
            EstadosDeCajaMercadoPago.Registrada, enviado.IdPos, local.IdStoreMercadoPago, externalId, urlQr, vinculo.FechaRegistro,
            sincronizadoEn, vinculo.IdTerminalPoint);
    }

    private sealed record ResultadoDeEnvio(long IdPos, string? UrlQr, string Operacion);

    private async Task<ResultadoDeEnvio> AdoptarOCrearAsync(
        int idEmpresa, string externalId, SolicitudDeCajaMercadoPago cuerpo, int idPuntoVenta, long estadoDelPuntoVenta,
        CancellationToken ct)
    {
        if (await BuscarPorExternalIdAsync(idEmpresa, externalId, ct) is { } existente)
        {
            return await ActualizarAsync(idEmpresa, existente, cuerpo, idPuntoVenta, estadoDelPuntoVenta, "adopcion", ct);
        }

        try
        {
            return await CrearAsync(idEmpresa, cuerpo, idPuntoVenta, ct);
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 409)
        {
            // 409 pos_already_exists: otra instancia creó la caja después de la búsqueda; se la adopta.
            if (await BuscarPorExternalIdAsync(idEmpresa, externalId, ct) is { } yaCreada)
            {
                return await ActualizarAsync(idEmpresa, yaCreada, cuerpo, idPuntoVenta, estadoDelPuntoVenta, "adopcion", ct);
            }

            throw;
        }
    }

    private static string HuellaDe(SolicitudDeCajaMercadoPago cuerpo) => HuellaDeTexto(JsonSerializer.Serialize(cuerpo));

    private static string HuellaDeTexto(string texto) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];

    private static string? UrlDelQr(CajaDeMercadoPago caja) =>
        caja.ImagenDelQr is { Length: <= LargoMaximoDeUrlQr } imagen ? imagen : null;

    /// <summary>La clave cambia con el contenido (incluido el <c>store_id</c>): Mercado Pago contesta 409 si la misma clave llega con
    /// otro cuerpo dentro de las 24 h, así que un alta con datos editados no puede reusar la clave del intento anterior.</summary>
    private async Task<ResultadoDeEnvio> CrearAsync(
        int idEmpresa, SolicitudDeCajaMercadoPago cuerpo, int idPuntoVenta, CancellationToken ct)
    {
        var creada = await api.EnviarAsync<CajaDeMercadoPago>(
            idEmpresa, HttpMethod.Post, RutaDeCajas, cuerpo, $"ways-caja-{idPuntoVenta}-alta-{HuellaDe(cuerpo)}", ct);

        if (creada.Id <= 0)
        {
            throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RespuestaInvalida, "Mercado Pago respondió con una caja sin identificador.", 502);
        }

        return new ResultadoDeEnvio(creada.Id, UrlDelQr(creada), "alta");
    }

    /// <summary>La clave lleva la caja de destino, el contenido (con el <c>store_id</c> del local de la sucursal) y el estado del punto
    /// de venta (su <c>updated_at</c>): reintentar el mismo intento repite la clave, volver a un contenido anterior (A, B, A) después de
    /// una edición o de una mudanza no recibe la respuesta guardada del primero, y la adopción que sigue a un 404 no reusa la clave del
    /// PATCH que falló contra la caja borrada.</summary>
    private async Task<ResultadoDeEnvio> ActualizarAsync(
        int idEmpresa, long idPos, SolicitudDeCajaMercadoPago cuerpo, int idPuntoVenta, long estadoDelPuntoVenta, string operacion,
        CancellationToken ct)
    {
        var actualizada = await api.EnviarAsync<CajaDeMercadoPago>(
            idEmpresa, HttpMethod.Patch, $"{RutaDeCajas}/{idPos}", cuerpo,
            $"ways-caja-{idPuntoVenta}-act-{HuellaDeTexto($"{idPos}|{HuellaDe(cuerpo)}|{estadoDelPuntoVenta}")}", ct);

        return new ResultadoDeEnvio(idPos, UrlDelQr(actualizada), operacion);
    }

    /// <summary>La caja que un intento anterior dejó creada en Mercado Pago sin llegar a guardar el vínculo, si hay una con el mismo
    /// <c>external_id</c>.</summary>
    private async Task<long?> BuscarPorExternalIdAsync(int idEmpresa, string externalId, CancellationToken ct)
    {
        BusquedaDeCajasMercadoPago busqueda;
        try
        {
            busqueda = await api.EnviarAsync<BusquedaDeCajasMercadoPago>(
                idEmpresa, HttpMethod.Get, $"{RutaDeCajas}?external_id={Uri.EscapeDataString(externalId)}", ct: ct);
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404)
        {
            // Como la búsqueda de locales, una búsqueda sin coincidencias puede contestar 404 en vez de una lista vacía.
            return null;
        }

        var coincidencia = busqueda.Data?.FirstOrDefault(
            r => r.Id > 0 && string.Equals(r.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

        return coincidencia?.Id;
    }

    private static EstadoDeCajaMercadoPago Estado(string estado) => new(estado, null, null, null, null, null, null, null);

    private async Task<PuntoVentaLeido> LeerPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta
            .AsNoTracking()
            .Where(p => p.Id == idPuntoVenta)
            .Select(p => new PuntoVentaLeido(p.IdTenant, p.IdEmpresa, p.IdSucursal, p.Nombre, p.UpdatedAt))
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    /// <summary>La conexión activa de la empresa; sin activa, la más reciente (para distinguir "nunca conectó" de "hay que reconectar").</summary>
    private Task<ConexionMercadoPago?> LeerConexionAsync(int idEmpresa, CancellationToken ct) =>
        db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .OrderByDescending(c => c.Estado == EstadoConexionMercadoPago.Activa)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Lo comparte <see cref="ServicioDeTerminalesMercadoPago"/>, que toma los mismos dos locks en el mismo orden.</summary>
    internal static async Task TomarLockAsync(IWaysDbContext db, int primeraClave, int segundaClave, CancellationToken ct)
    {
        var transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Los locks de la caja de Mercado Pago son de alcance de transacción: tomarlos fuera de una no protege nada.");

        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";
        ParametrosDeComando.Agregar(comando, primeraClave);
        ParametrosDeComando.Agregar(comando, segundaClave);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private sealed record PuntoVentaLeido(int IdTenant, int IdEmpresa, int IdSucursal, string Nombre, DateTimeOffset UpdatedAt);
}
