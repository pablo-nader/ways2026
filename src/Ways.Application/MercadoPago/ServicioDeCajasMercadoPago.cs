using System.Data;
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
/// sucursal actual en el <c>PUT /pos/{id}</c>: así Mercado Pago reapunta la caja y el vínculo guarda el local nuevo. Mover el punto de
/// venta no toca este vínculo ni lo bloquea; solo lo deja <c>desactualizada</c>.
///
/// <b>Lock</b> (<c>single-read-under-lock</c>): un <c>pg_advisory_xact_lock</c> por punto de venta serializa dos clics y se mantiene
/// mientras se llama a Mercado Pago, así que el segundo espera el resultado del primero y encuentra el vínculo ya guardado. El punto
/// de venta, el local y el vínculo se leen UNA vez, después del lock. El lock no cubre la edición ni el movimiento del punto de venta
/// (esos toman otros locks): una edición concurrente deja el vínculo desactualizado porque la fecha de sincronización es la de la
/// lectura del punto de venta, no la del fin de la llamada.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): la escritura agrega el vínculo y una fila de auditoría después de
/// una llamada externa que no se puede repetir a ciegas. Si Mercado Pago creó la caja y el guardado falla, el próximo intento la
/// encuentra por <c>external_id</c> y la adopta en vez de duplicarla.
///
/// Un 401 de Mercado Pago deja la conexión en <c>requiere_reconexion</c> dentro de esta misma transacción, así que se confirma
/// igual antes de relanzar el error: de lo contrario el rollback desharía esa marca.
///
/// Los campos de Mercado Pago (<c>id</c>, <c>external_id</c>, <c>store_id</c>, <c>qr</c>, la forma de la búsqueda) vienen de la
/// referencia de la API y no se ejercitaron en vivo; los DTO ignoran lo que no usan.
/// </summary>
public class ServicioDeCajasMercadoPago(
    IWaysDbContext db,
    IClienteApiMercadoPago api,
    IRelojDelSistema reloj,
    ServicioDeAuditoria auditoria)
{
    /// <summary>Segunda clave del <c>pg_advisory_xact_lock(int, int)</c>; la primera es el id del punto de venta.</summary>
    public const int ClaveDeLockDeCaja = 0x4D504350;

    private const string RutaDeCajas = "/pos";

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

        var idStore = caja.IdLocalMercadoPago == local.Id
            ? local.IdStoreMercadoPago
            : await db.LocalesMercadoPago
                .AsNoTracking()
                .Where(l => l.Id == caja.IdLocalMercadoPago)
                .Select(l => (long?)l.IdStoreMercadoPago)
                .FirstOrDefaultAsync(ct);

        var desactualizada = puntoVenta.UpdatedAt > caja.FechaSincronizacion || caja.IdLocalMercadoPago != local.Id;

        return new EstadoDeCajaMercadoPago(
            desactualizada ? EstadosDeCajaMercadoPago.Desactualizada : EstadosDeCajaMercadoPago.Registrada,
            caja.IdPosMercadoPago, idStore, caja.ExternalId, caja.UrlQr, caja.FechaRegistro, caja.FechaSincronizacion);
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
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await TomarLockDelPuntoVentaAsync(idPuntoVenta, ct);

            ErrorDominio? reconexion = null;
            EstadoDeCajaMercadoPago? resultado = null;
            try
            {
                resultado = await RegistrarBajoLockAsync(idPuntoVenta, ct);
            }
            catch (ErrorDominio error) when (error.Codigo == CodigosDeErrorDeMercadoPago.RequiereReconexion)
            {
                reconexion = error;
            }

            await transaccion.CommitAsync(ct);

            return reconexion is null ? resultado! : throw reconexion;
        });
    }

    private async Task<EstadoDeCajaMercadoPago> RegistrarBajoLockAsync(int idPuntoVenta, CancellationToken ct)
    {
        var puntoVenta = await LeerPuntoVentaAsync(idPuntoVenta, ct);

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

        // La fecha de la lectura del punto de venta, no la del fin de la llamada: una edición hecha mientras Mercado Pago responde
        // queda posterior a ella y el vínculo figura desactualizado.
        var sincronizadoEn = reloj.Ahora;
        var externalId = CajaParaMercadoPago.ExternalIdDe(idPuntoVenta);
        var cuerpo = new SolicitudDeCajaMercadoPago(nombre, externalId, local.IdStoreMercadoPago, FixedAmount: false);
        var estadoDelPuntoVenta = puntoVenta.UpdatedAt.UtcTicks;

        ResultadoDeEnvio enviado;
        if (vinculo is not null)
        {
            try
            {
                enviado = await ActualizarAsync(
                    puntoVenta.IdEmpresa, vinculo.IdPosMercadoPago, cuerpo, idPuntoVenta, estadoDelPuntoVenta, "actualizacion", ct);
            }
            catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404)
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
            sincronizadoEn);
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

        return await CrearAsync(idEmpresa, cuerpo, idPuntoVenta, ct);
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
    /// PUT que falló contra la caja borrada.</summary>
    private async Task<ResultadoDeEnvio> ActualizarAsync(
        int idEmpresa, long idPos, SolicitudDeCajaMercadoPago cuerpo, int idPuntoVenta, long estadoDelPuntoVenta, string operacion,
        CancellationToken ct)
    {
        var actualizada = await api.EnviarAsync<CajaDeMercadoPago>(
            idEmpresa, HttpMethod.Put, $"{RutaDeCajas}/{idPos}", cuerpo,
            $"ways-caja-{idPuntoVenta}-act-{HuellaDeTexto($"{idPos}|{HuellaDe(cuerpo)}|{estadoDelPuntoVenta}")}", ct);

        return new ResultadoDeEnvio(idPos, UrlDelQr(actualizada), operacion);
    }

    /// <summary>La caja que un intento anterior dejó creada en Mercado Pago sin llegar a guardar el vínculo, si hay una con el mismo
    /// <c>external_id</c>.</summary>
    private async Task<long?> BuscarPorExternalIdAsync(int idEmpresa, string externalId, CancellationToken ct)
    {
        var busqueda = await api.EnviarAsync<BusquedaDeCajasMercadoPago>(
            idEmpresa, HttpMethod.Get, $"{RutaDeCajas}?external_id={Uri.EscapeDataString(externalId)}", ct: ct);

        var coincidencia = busqueda.Results?.FirstOrDefault(
            r => r.Id > 0 && string.Equals(r.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

        return coincidencia?.Id;
    }

    private static EstadoDeCajaMercadoPago Estado(string estado) => new(estado, null, null, null, null, null, null);

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

    private async Task TomarLockDelPuntoVentaAsync(int idPuntoVenta, CancellationToken ct)
    {
        var transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "El lock de la caja de Mercado Pago es de alcance de transacción: tomarlo fuera de una no protege nada.");

        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, ClaveDeLockDeCaja);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private sealed record PuntoVentaLeido(int IdTenant, int IdEmpresa, int IdSucursal, string Nombre, DateTimeOffset UpdatedAt);
}
