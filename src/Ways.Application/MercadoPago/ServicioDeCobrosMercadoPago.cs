using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Cobros con QR dinámico de Mercado Pago que pide el dispositivo del POS (<c>cobros_mercadopago</c>). Crear, consultar y cancelar:
/// el punto de venta sale siempre de la sesión del dispositivo (nunca del cuerpo) y un cobro de otro punto de venta o de otro tenant
/// es un 404. Orden de los errores al crear: 403 si la sesión no es de un dispositivo vigente, 400 del cuerpo, 400 del medio (que
/// tiene que ser de la empresa del punto de venta y tener el proveedor <c>mercadopago_qr</c>), 409 del modo del punto de venta, y
/// ya bajo el lock del punto de venta el cobro vivo del número (409 <c>cobro_mercadopago_ya_aprobado</c> o
/// <c>cobro_mercadopago_en_curso</c>, o el mismo cobro si coinciden medio e importe), la conexión (409) y la caja (409
/// <c>mercadopago_caja_no_registrada</c>).
///
/// <b>Un cobro vivo por número.</b> Un cobro <c>pendiente</c> o <c>aprobado</c> ocupa el número de venta
/// (<c>ux_cobros_mercadopago_vivo_numero</c>). Crear con el mismo medio e importe devuelve el pendiente tal cual (el dispositivo que
/// reintenta no abre otra orden); con otro importe es un 409. Un cobro pendiente cuyo plazo ya pasó se consulta a Mercado Pago antes
/// de decidir, porque pudo vencer o pagarse justo antes. Cada cobro nuevo después de uno cancelado o vencido suma uno a
/// <c>intento</c>; la <c>external_reference</c> y la clave de idempotencia derivan de él, así que reintentar una creación que
/// quedó a medias (Mercado Pago creó la orden y el guardado falló) devuelve la misma orden y nunca abre una segunda.
///
/// <b>Locks</b> (<c>single-read-under-lock</c>): crear toma el lock del punto de venta y, si refresca un cobro pendiente
/// vencido, el del cobro (siempre en ese orden); consultar y cancelar toman solo el del cobro. Los cobros del número se leen UNA vez,
/// después del lock. El lock se mantiene mientras se llama a Mercado Pago: dos consultas del mismo cobro se serializan, y la segunda
/// ve la <c>fecha_ultima_consulta</c> de la primera, de modo que Mercado Pago recibe a lo sumo una lectura cada
/// <see cref="IntervaloMinimoDeConsulta"/> por cobro.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): hay una llamada externa en medio de la escritura. Un 401 de Mercado
/// Pago deja la conexión en <c>requiere_reconexion</c> dentro de esta transacción, y cualquier otro <see cref="ErrorDominio"/> puede
/// llegar después de una escritura ya hecha (el estado nuevo de un cobro refrescado): en ambos casos se confirma lo ya guardado
/// antes de relanzar. Cada <c>SaveChanges</c> deja un estado consistente por sí solo, así que confirmar nunca deja algo a medias.
///
/// Los campos de la orden (<c>id</c>, <c>status</c>, <c>type_response.qr_data</c>, <c>transactions.payments[]</c>) están verificados
/// contra la referencia de la API (2026-11-01) pero no se ejercitaron en vivo; los DTO ignoran lo que no usan.
/// </summary>
public class ServicioDeCobrosMercadoPago(IWaysDbContext db, IClienteApiMercadoPago api, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// <summary>Segunda clave del <c>pg_advisory_xact_lock(int, int)</c> por punto de venta; la primera es el id del punto de venta.</summary>
    public const int ClaveDeLockDeCobrosDelPuntoVenta = 0x4D504351;

    /// <summary>Segunda clave del lock por cobro; la primera es el id del cobro.</summary>
    public const int ClaveDeLockDeCobro = 0x4D504352;

    /// <summary>Cada cuánto, como mínimo, se lee de Mercado Pago la orden de un mismo cobro pendiente.</summary>
    public static readonly TimeSpan IntervaloMinimoDeConsulta = TimeSpan.FromSeconds(2);

    /// <summary>Cuánto vive la orden en Mercado Pago (<c>expiration_time</c>, duración ISO 8601).</summary>
    public const string VigenciaDeLaOrden = "PT10M";

    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    private const decimal ImporteMaximo = 9_999_999_999_999m;

    private const int LargoMaximoDeId = 64;

    public async Task<CobroDeMercadoPago> CrearAsync(SolicitudDeCobroMercadoPago solicitud, CancellationToken ct = default)
    {
        var (idDispositivo, idPuntoVenta, idTenant) = await ResolverDispositivoAsync(ct);
        ValidarSolicitud(solicitud);

        var puntoVenta = await db.PuntosVenta
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        await ExigirMedioConQrAsync(solicitud.IdMedioPago, puntoVenta.IdEmpresa, ct);
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        return await EnLockAsync(
            idPuntoVenta, ClaveDeLockDeCobrosDelPuntoVenta, () => CrearBajoLockAsync(solicitud, puntoVenta, idDispositivo, idTenant, ct), ct);
    }

    private async Task<CobroDeMercadoPago> CrearBajoLockAsync(
        SolicitudDeCobroMercadoPago solicitud, PuntoVenta puntoVenta, int idDispositivo, int idTenant, CancellationToken ct)
    {
        var cobros = await db.CobrosMercadoPago
            .Where(c => c.IdPuntoVenta == puntoVenta.Id && c.NumeroPreasignado == solicitud.NumeroPreasignado)
            .ToListAsync(ct);

        if (cobros.FirstOrDefault(c => EstadosDeCobroMercadoPago.EsVivo(c.Estado)) is { } vivo)
        {
            if (vivo.Estado == EstadosDeCobroMercadoPago.Pendiente && reloj.Ahora >= vivo.ExpiraEn)
            {
                await ServicioDeCajasMercadoPago.TomarLockAsync(db, vivo.Id, ClaveDeLockDeCobro, ct);
                await RefrescarAsync(vivo, respetarIntervalo: false, ct);
            }

            if (vivo.Estado == EstadosDeCobroMercadoPago.Aprobado)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.YaAprobado, "Esa venta ya tiene un cobro aprobado con Mercado Pago.");
            }

            if (vivo.Estado == EstadosDeCobroMercadoPago.Pendiente)
            {
                if (vivo.IdMedioPago == solicitud.IdMedioPago && vivo.Importe == solicitud.Importe)
                {
                    return Mapear(vivo);
                }

                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.EnCurso,
                    "Esa venta ya tiene un cobro pendiente con Mercado Pago por otro medio o importe: cancelalo antes de cobrar de nuevo.");
            }
        }

        var intento = cobros.Count == 0 ? 1 : cobros.Max(c => c.Intento) + 1;

        var conexion = await db.ConexionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == puntoVenta.IdEmpresa)
            .OrderByDescending(c => c.Estado == EstadoConexionMercadoPago.Activa)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        var idUsuarioMercadoPago = conexion switch
        {
            { Estado: EstadoConexionMercadoPago.Activa } => conexion.IdUsuarioMercadoPago,
            { Estado: EstadoConexionMercadoPago.RequiereReconexion } => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RequiereReconexion,
                "La conexión con Mercado Pago de esta empresa dejó de ser válida: hay que reconectarla.",
                409),
            _ => throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.NoConectada, "La empresa no tiene Mercado Pago conectado.", 409)
        };

        var caja = await db.CajasMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == puntoVenta.Id && c.IdUsuarioMercadoPago == idUsuarioMercadoPago, ct)
            ?? throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.CajaNoRegistrada,
                "El punto de venta todavía no está registrado como caja en Mercado Pago: registralo primero.",
                409);

        var externalReference = $"W{idTenant}P{puntoVenta.Id}N{solicitud.NumeroPreasignado}I{intento}";
        var importe = ComoImporteDeMercadoPago(solicitud.Importe);
        var cuerpo = new SolicitudDeOrdenQrMercadoPago(
            "qr", externalReference, importe, VigenciaDeLaOrden,
            new ConfiguracionDeOrdenMercadoPago(new ConfiguracionDeQrDeOrdenMercadoPago(caja.ExternalId, "dynamic")),
            [new ItemDeOrdenMercadoPago($"Venta {solicitud.NumeroPreasignado}", importe, 1, "unit")]);

        var inicio = reloj.Ahora;
        var orden = await api.EnviarAsync<OrdenDeMercadoPago>(
            puntoVenta.IdEmpresa, HttpMethod.Post, "/v1/orders", cuerpo,
            $"ways-cobro-{Huella($"{puntoVenta.Id}|{solicitud.NumeroPreasignado}|{intento}|{importe}")}", ct);

        if (string.IsNullOrWhiteSpace(orden.Id) || orden.Id.Trim().Length > LargoMaximoDeId
            || string.IsNullOrWhiteSpace(orden.TypeResponse?.QrData))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RespuestaInvalida, "Mercado Pago respondió con una orden sin identificador o sin QR.", 502);
        }

        var cobro = new CobroMercadoPago
        {
            IdTenant = idTenant,
            IdEmpresa = puntoVenta.IdEmpresa,
            IdPuntoVenta = puntoVenta.Id,
            IdDispositivo = idDispositivo,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroPreasignado = solicitud.NumeroPreasignado,
            Intento = intento,
            Tipo = TiposDeCobroMercadoPago.Qr,
            Importe = solicitud.Importe,
            ExternalReference = externalReference,
            IdOrdenMercadoPago = orden.Id.Trim(),
            Estado = EstadosDeCobroMercadoPago.Pendiente,
            QrData = orden.TypeResponse!.QrData!.Trim(),
            ExpiraEn = inicio + Vigencia,
            CreatedAt = inicio,
            UpdatedAt = inicio
        };
        AplicarOrden(cobro, orden, inicio);
        db.CobrosMercadoPago.Add(cobro);
        await db.SaveChangesAsync(ct);

        return Mapear(cobro);
    }

    public async Task<CobroDeMercadoPago> ObtenerAsync(int idCobro, CancellationToken ct = default)
    {
        var (_, idPuntoVenta, _) = await ResolverDispositivoAsync(ct);

        var previo = await db.CobrosMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == idCobro && c.IdPuntoVenta == idPuntoVenta, ct)
            ?? throw CobroInexistente(idCobro);

        if (previo.Estado != EstadosDeCobroMercadoPago.Pendiente)
        {
            return Mapear(previo);
        }

        return await EnLockAsync(idCobro, ClaveDeLockDeCobro, async () =>
        {
            var cobro = await LeerCobroAsync(idCobro, idPuntoVenta, ct);
            if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente)
            {
                await RefrescarAsync(cobro, respetarIntervalo: true, ct);
            }

            return Mapear(cobro);
        }, ct);
    }

    public async Task<CobroDeMercadoPago> CancelarAsync(int idCobro, CancellationToken ct = default)
    {
        var (_, idPuntoVenta, _) = await ResolverDispositivoAsync(ct);

        if (!await db.CobrosMercadoPago.AnyAsync(c => c.Id == idCobro && c.IdPuntoVenta == idPuntoVenta, ct))
        {
            throw CobroInexistente(idCobro);
        }

        return await EnLockAsync(idCobro, ClaveDeLockDeCobro, async () =>
        {
            var cobro = await LeerCobroAsync(idCobro, idPuntoVenta, ct);

            if (cobro.Estado == EstadosDeCobroMercadoPago.Aprobado)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.YaAprobado, "El cobro ya está aprobado: no se puede cancelar.");
            }

            if (cobro.Estado != EstadosDeCobroMercadoPago.Pendiente)
            {
                return Mapear(cobro);
            }

            try
            {
                var cancelada = await api.EnviarAsync<OrdenDeMercadoPago>(
                    cobro.IdEmpresa, HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(cobro.IdOrdenMercadoPago)}/cancel",
                    claveIdempotencia: $"ways-cobro-{cobro.Id}-cancelar", ct: ct);

                var ahora = reloj.Ahora;
                AplicarOrden(cobro, cancelada, ahora);
                if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente)
                {
                    cobro.Estado = EstadosDeCobroMercadoPago.Cancelado;
                }

                cobro.UpdatedAt = ahora;
                await db.SaveChangesAsync(ct);
            }
            catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 409)
            {
                // Mercado Pago solo cancela una orden en estado created: si contesta 409 pudo pagarse (o vencer) mientras tanto.
                await RefrescarAsync(cobro, respetarIntervalo: false, ct);
            }

            return Mapear(cobro);
        }, ct);
    }

    /// <summary>Lee la orden de Mercado Pago y aplica su estado al cobro pendiente. Con <paramref name="respetarIntervalo"/> no hace
    /// nada si la última consulta es de hace menos de <see cref="IntervaloMinimoDeConsulta"/>. Quien la llama tiene el lock del cobro.</summary>
    private async Task RefrescarAsync(CobroMercadoPago cobro, bool respetarIntervalo, CancellationToken ct)
    {
        var ahora = reloj.Ahora;
        if (respetarIntervalo && cobro.FechaUltimaConsulta is { } ultima && ahora - ultima < IntervaloMinimoDeConsulta)
        {
            return;
        }

        var orden = await api.EnviarAsync<OrdenDeMercadoPago>(
            cobro.IdEmpresa, HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(cobro.IdOrdenMercadoPago)}", ct: ct);

        AplicarOrden(cobro, orden, ahora);
        cobro.FechaUltimaConsulta = ahora;
        cobro.UpdatedAt = ahora;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Un estado de la orden que Ways no reconoce (<c>created</c>, <c>at_terminal</c>…) deja el cobro como está.</summary>
    private static void AplicarOrden(CobroMercadoPago cobro, OrdenDeMercadoPago orden, DateTimeOffset ahora)
    {
        switch (orden.Status)
        {
            case "processed":
                var pagos = orden.Transactions?.Payments;
                var pago = pagos?.FirstOrDefault(p => p.Status == "processed") ?? pagos?.FirstOrDefault();
                cobro.Estado = EstadosDeCobroMercadoPago.Aprobado;
                cobro.FechaAprobacion = ahora;
                cobro.IdPagoMercadoPago = pago?.Id is { Length: > 0 and <= LargoMaximoDeId } idPago ? idPago : null;
                cobro.ReferenciaPagoMercadoPago = pago?.ReferenciaNumerica;
                cobro.ImportePagado = pago is null ? null : ImportePagadoValido(pago.PaidAmount ?? pago.Amount);
                break;
            case "canceled":
                cobro.Estado = EstadosDeCobroMercadoPago.Cancelado;
                break;
            case "expired":
                cobro.Estado = EstadosDeCobroMercadoPago.Expirado;
                break;
            case "refunded":
                cobro.Estado = EstadosDeCobroMercadoPago.Devuelto;
                break;
        }
    }

    private static decimal? ImportePagadoValido(decimal? importe) =>
        importe is >= 0m and <= ImporteMaximo ? Math.Round(importe.Value, 2, MidpointRounding.AwayFromZero) : null;

    private static CobroDeMercadoPago Mapear(CobroMercadoPago cobro)
    {
        var pendiente = cobro.Estado == EstadosDeCobroMercadoPago.Pendiente;
        return new CobroDeMercadoPago(
            cobro.Id, cobro.Estado, pendiente ? cobro.QrData : null, cobro.ExpiraEn, cobro.Importe, cobro.ImportePagado,
            cobro.Estado == EstadosDeCobroMercadoPago.Aprobado && cobro.ImportePagado is { } pagado && pagado != cobro.Importe);
    }

    private static void ValidarSolicitud(SolicitudDeCobroMercadoPago solicitud)
    {
        if (solicitud.NumeroPreasignado <= 0)
        {
            throw new ErrorDominio(
                CodigosDeErrorDeCobrosMercadoPago.NumeroInvalido, "El número de venta tiene que ser mayor a 0.", 400);
        }

        if (solicitud.Importe <= 0m || solicitud.Importe > ImporteMaximo || solicitud.Importe != Math.Round(solicitud.Importe, 2))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeCobrosMercadoPago.ImporteInvalido,
                "El importe tiene que ser mayor a 0 y tener hasta 2 decimales.",
                400);
        }
    }

    private async Task ExigirMedioConQrAsync(int idMedioPago, int idEmpresaDelPuntoVenta, CancellationToken ct)
    {
        var medio = await db.MediosPago
            .AsNoTracking()
            .Where(m => m.Id == idMedioPago)
            .Select(m => new { m.Activo, m.IdEmpresa, m.Proveedor })
            .FirstOrDefaultAsync(ct);

        if (medio is not { Activo: true, Proveedor: ProveedorDeCobro.MercadoPagoQr }
            || (medio.IdEmpresa is { } idEmpresa && idEmpresa != idEmpresaDelPuntoVenta))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeCobrosMercadoPago.MedioInvalido,
                "El medio de pago no existe, está inactivo o no cobra con QR de Mercado Pago.",
                400);
        }
    }

    private async Task<(int IdDispositivo, int IdPuntoVenta, int IdTenant)> ResolverDispositivoAsync(CancellationToken ct)
    {
        var idDispositivo = contexto.IdDispositivo
            ?? throw new ErrorDominio("prohibido", "Esta operación requiere un dispositivo autenticado.", 403);
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCobrosMercadoPago requiere un actor de tenant; RequiereDispositivo no admite plataforma.");

        var idPuntoVenta = await db.Dispositivos
            .Where(d => d.Id == idDispositivo)
            .Select(d => (int?)d.IdPuntoVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw new ErrorDominio("prohibido", "Este dispositivo ya no está vigente.", 403);

        return (idDispositivo, idPuntoVenta, idTenant);
    }

    private async Task<CobroMercadoPago> LeerCobroAsync(int idCobro, int idPuntoVenta, CancellationToken ct) =>
        await db.CobrosMercadoPago.FirstOrDefaultAsync(c => c.Id == idCobro && c.IdPuntoVenta == idPuntoVenta, ct)
        ?? throw CobroInexistente(idCobro);

    private static ErrorDominio CobroInexistente(int idCobro) => ErrorDominio.NoEncontrado($"No existe el cobro {idCobro}.");

    private async Task<T> EnLockAsync<T>(int primeraClave, int segundaClave, Func<Task<T>> operacion, CancellationToken ct)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await ServicioDeCajasMercadoPago.TomarLockAsync(db, primeraClave, segundaClave, ct);

            ErrorDominio? diferido = null;
            T? resultado = default;
            try
            {
                resultado = await operacion();
            }
            catch (ErrorDominio error)
            {
                diferido = error;
            }

            await transaccion.CommitAsync(ct);

            return diferido is null ? resultado! : throw diferido;
        });
    }

    private static string ComoImporteDeMercadoPago(decimal importe) => importe.ToString("F2", CultureInfo.InvariantCulture);

    private static string Huella(string texto) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..16];
}
