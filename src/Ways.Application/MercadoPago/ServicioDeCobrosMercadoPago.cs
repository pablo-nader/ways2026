using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// <b>Un cobro vivo por número.</b> Un cobro <c>creando</c>, <c>pendiente</c> o <c>aprobado</c> ocupa el número de venta
/// (<c>ux_cobros_mercadopago_vivo_numero</c>). Crear con el mismo medio e importe devuelve el pendiente tal cual (el dispositivo que
/// reintenta no abre otra orden); con otro importe es un 409. Un cobro pendiente cuyo plazo ya pasó se consulta a Mercado Pago antes
/// de decidir, porque pudo vencer o pagarse justo antes. Cada cobro nuevo después de uno cancelado o vencido suma uno a
/// <c>intento</c>.
///
/// <b>Reservar y después llamar.</b> Crear confirma primero la fila en estado <c>creando</c>, con la orden que se va a pedir
/// (<c>external_reference</c>, importe, <c>external_pos_id</c> de la caja) y la clave de idempotencia, que es la huella del cuerpo
/// completo; recién después llama a <c>POST /v1/orders</c> y pasa la fila a <c>pendiente</c>. Si Mercado Pago creó la orden y el
/// guardado posterior falló, la fila queda <c>creando</c>: el siguiente crear, consultar o cancelar del cobro reenvía la orden
/// ORIGINAL armada con los valores guardados (no con la caja actual) y la misma clave, de modo que Mercado Pago devuelve la misma
/// orden, y solo entonces rigen las reglas de siempre (mismo medio e importe devuelve el cobro; otro importe es 409). Al completarse,
/// <c>expira_en</c> pasa a ser ahora más la vigencia (el reenvío lleva una orden con su plazo completo). <b>Plazos de una fila
/// <c>creando</c></b>, contados desde su reserva (<c>created_at</c>): mientras no pasó una vigencia se reenvía, y cualquier fallo (un
/// 4xx incluido: no prueba que la orden no exista) la deja <c>creando</c>; desde una vigencia no se reenvía nunca más, porque podría
/// crear una orden nueva y cobrable, y crear, consultar y cancelar responden 409 <c>cobro_mercadopago_en_curso</c>; desde dos
/// vigencias más 5 minutos (cualquier orden que pudo crear un reenvío ya venció) la fila pasa a <c>expirado</c> sin llamar a Mercado
/// Pago, y libera el número. Una orden en estado <c>created</c> sin <c>qr_data</c> es una respuesta inválida (502); una en estado
/// terminal pasa por el mapeo de estados; una <c>processed</c> sin pago legible (importe o id) queda <c>pendiente</c> sin QR
/// (<c>VerificandoPago</c> y <c>PagoInformadoSinDatos</c>).
///
/// <b>Locks</b> (<c>single-read-under-lock</c>), siempre en el orden punto de venta → cobro: crear toma el lock del punto de venta y,
/// si hay un cobro vivo del número, el del cobro; consultar y cancelar toman el del cobro y, si el cobro está <c>creando</c> (hay
/// que reenviar la orden), antes el del punto de venta. Todo camino que devuelve o modifica un cobro vivo lo vuelve a leer después
/// de tomar su lock. El lock se mantiene mientras se llama a Mercado Pago: dos consultas del mismo cobro se serializan, y la segunda
/// ve la <c>fecha_ultima_consulta</c> de la primera, de modo que Mercado Pago recibe a lo sumo una lectura cada
/// <see cref="IntervaloMinimoDeConsulta"/> por cobro.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): hay una llamada externa en medio de la escritura. Un 401 de Mercado
/// Pago deja la conexión en <c>requiere_reconexion</c> dentro de esta transacción, y cualquier otro <see cref="ErrorDominio"/> puede
/// llegar después de una escritura ya hecha (el estado nuevo de un cobro refrescado): en ambos casos se confirma lo ya guardado
/// antes de relanzar. Cada <c>SaveChanges</c> deja un estado consistente por sí solo, así que confirmar nunca deja algo a medias.
///
/// <b>Un pago sin importe o sin id legibles no aprueba.</b> Una orden <c>processed</c> solo aprueba el cobro si trae un pago con
/// importe pagado válido (<c>paid_amount</c> o, si falta, <c>amount</c>; mayor a 0 y con hasta 2 decimales) e id no vacío de hasta
/// 64 caracteres (sin espacios en los bordes); si no, el cobro sigue pendiente, se avisa en el log (sin datos sensibles) y la próxima
/// consulta lo reintenta. Como el cliente ya pagó, la primera vez se guarda <c>fecha_procesada_mercadopago</c>: desde entonces no se
/// devuelve el QR, el cobro se informa como <c>VerificandoPago</c> y <c>PagoInformadoSinDatos</c>, y cancelarlo es 409
/// <c>cobro_mercadopago_pagado_sin_datos</c> sin llamar a Mercado Pago. Consultar sigue refrescando (con el límite de siempre) y un
/// pago legible posterior lo aprueba.
///
/// Los campos de la orden (<c>id</c>, <c>status</c>, <c>type_response.qr_data</c>, <c>transactions.payments[]</c>) están verificados
/// contra la referencia de la API (2026-11-01) pero no se ejercitaron en vivo; los DTO ignoran lo que no usan.
/// </summary>
public class ServicioDeCobrosMercadoPago(
    IWaysDbContext db, IClienteApiMercadoPago api, IRelojDelSistema reloj, IContextoDeUsuario contexto,
    ILogger<ServicioDeCobrosMercadoPago> log)
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

    private static readonly TimeSpan MargenDeAbandono = TimeSpan.FromMinutes(5);

    private const decimal ImporteMaximo = 9_999_999_999_999m;

    private const int LargoMaximoDeId = 64;

    private const int MaximoDeVueltasAlCrear = 5;

    private const string PrefijoDeClave = "ways-cobro-";

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

        // Cada vuelta es una transacción bajo el lock del punto de venta. Reservar la fila confirma y termina la vuelta sin respuesta:
        // la siguiente la encuentra creando, reenvía la orden y aplica las reglas del cobro vivo.
        for (var vuelta = 0; vuelta < MaximoDeVueltasAlCrear; vuelta++)
        {
            var respuesta = await EnLockAsync(
                idPuntoVenta, ClaveDeLockDeCobrosDelPuntoVenta,
                () => CrearBajoLockAsync(solicitud, puntoVenta, idDispositivo, idTenant, ct), ct);
            if (respuesta is not null)
            {
                return respuesta;
            }
        }

        throw ErrorDominio.Conflicto(
            CodigosDeErrorDeCobrosMercadoPago.EnCurso, "Esa venta tiene un cobro con Mercado Pago en curso: consultá su estado antes de cobrar de nuevo.");
    }

    /// <summary>Devuelve la respuesta al dispositivo, o nulo si reservó una fila nueva (<c>creando</c>) que falta completar.</summary>
    private async Task<CobroDeMercadoPago?> CrearBajoLockAsync(
        SolicitudDeCobroMercadoPago solicitud, PuntoVenta puntoVenta, int idDispositivo, int idTenant, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var cobros = await db.CobrosMercadoPago
            .AsNoTracking()
            .Where(c => c.IdPuntoVenta == puntoVenta.Id && c.NumeroPreasignado == solicitud.NumeroPreasignado)
            .Select(c => new { c.Id, c.Estado, c.Intento })
            .ToListAsync(ct);

        if (cobros.FirstOrDefault(c => EstadosDeCobroMercadoPago.EsVivo(c.Estado)) is { } referencia)
        {
            await ServicioDeCajasMercadoPago.TomarLockAsync(db, referencia.Id, ClaveDeLockDeCobro, ct);
            var vivo = await LeerCobroAsync(referencia.Id, puntoVenta.Id, ct);

            if (vivo.Estado == EstadosDeCobroMercadoPago.Creando)
            {
                await ResolverCreandoAsync(vivo, ct);
            }

            if (vivo.Estado == EstadosDeCobroMercadoPago.Pendiente && reloj.Ahora >= vivo.ExpiraEn)
            {
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

        await FijarMedioAsync(solicitud.IdMedioPago, puntoVenta.IdEmpresa, ct);

        var externalReference = $"W{idTenant}P{puntoVenta.Id}N{solicitud.NumeroPreasignado}I{intento}";
        var cuerpo = ArmarOrden(externalReference, solicitud.Importe, caja.ExternalId, solicitud.NumeroPreasignado);
        var ahora = reloj.Ahora;

        db.CobrosMercadoPago.Add(new CobroMercadoPago
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
            ExternalPosId = caja.ExternalId,
            ClaveIdempotencia = ClaveDeIdempotenciaDe(cuerpo),
            Estado = EstadosDeCobroMercadoPago.Creando,
            ExpiraEn = ahora + Vigencia,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync(ct);

        return null;
    }

    /// <summary>
    /// Resuelve una fila <c>creando</c> según el instante en que se reservó (<c>created_at</c>). Dentro de <see cref="Vigencia"/> reenvía
    /// la orden. Pasado ese plazo no la reenvía nunca más (un reenvío podría crear una orden nueva y cobrable) y responde 409 mientras
    /// una orden creada por un reenvío anterior pueda seguir viva; cuando ya no puede, la fila se da por <c>expirado</c> sin llamar a
    /// Mercado Pago y libera el número. Un 4xx no prueba que la orden no exista, por eso nunca cancela la fila. Quien la llama tiene el
    /// lock del punto de venta y el del cobro y acaba de leer la fila.
    /// </summary>
    private async Task ResolverCreandoAsync(CobroMercadoPago cobro, CancellationToken ct)
    {
        var ahora = reloj.Ahora;
        if (ahora >= cobro.CreatedAt + Vigencia + Vigencia + MargenDeAbandono)
        {
            cobro.Estado = EstadosDeCobroMercadoPago.Expirado;
            cobro.UpdatedAt = ahora;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (ahora >= cobro.CreatedAt + Vigencia)
        {
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeCobrosMercadoPago.EnCurso,
                "Mercado Pago todavía está resolviendo este cobro: no se puede cancelar ni reenviar. Podés cobrar con otro medio de pago.");
        }

        await CompletarCreacionAsync(cobro, ct);
    }

    /// <summary>
    /// Pide a Mercado Pago la orden guardada en una fila <c>creando</c> (la original, con su clave: si ya existe, la devuelve) y
    /// aplica la respuesta. Quien la llama tiene el lock del punto de venta y el del cobro y acaba de leer la fila.
    /// </summary>
    private async Task CompletarCreacionAsync(CobroMercadoPago cobro, CancellationToken ct)
    {
        var cuerpo = ArmarOrden(cobro.ExternalReference, cobro.Importe, cobro.ExternalPosId, cobro.NumeroPreasignado);

        var orden = await api.EnviarAsync<OrdenDeMercadoPago>(
            cobro.IdEmpresa, HttpMethod.Post, "/v1/orders", cuerpo, cobro.ClaveIdempotencia, ct);

        var id = orden.Id?.Trim();
        var qr = orden.TypeResponse?.QrData?.Trim();
        var conEstadoFinal = orden.Status is "processed" or "canceled" or "expired" or "refunded";
        if (string.IsNullOrEmpty(id) || id.Length > LargoMaximoDeId || (!conEstadoFinal && string.IsNullOrEmpty(qr)))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RespuestaInvalida, "Mercado Pago respondió con una orden sin identificador o sin QR.", 502);
        }

        var ahora = reloj.Ahora;
        cobro.IdOrdenMercadoPago = id;
        cobro.ExpiraEn = ahora + Vigencia;
        AplicarOrden(cobro, orden, ahora);
        if (cobro.Estado == EstadosDeCobroMercadoPago.Creando)
        {
            cobro.Estado = EstadosDeCobroMercadoPago.Pendiente;
        }

        cobro.QrData = cobro.Estado == EstadosDeCobroMercadoPago.Pendiente ? qr : null;
        cobro.UpdatedAt = ahora;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CobroDeMercadoPago> ObtenerAsync(int idCobro, CancellationToken ct = default)
    {
        var (_, idPuntoVenta, _) = await ResolverDispositivoAsync(ct);

        return await SobreCobroAsync(idCobro, idPuntoVenta, async cobro =>
        {
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

        return await SobreCobroAsync(idCobro, idPuntoVenta, async cobro =>
        {
            if (cobro.Estado == EstadosDeCobroMercadoPago.Aprobado)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.YaAprobado, "El cobro ya está aprobado: no se puede cancelar.");
            }

            if (cobro.Estado != EstadosDeCobroMercadoPago.Pendiente)
            {
                return Mapear(cobro);
            }

            if (cobro.FechaProcesadaMercadoPago is not null)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.PagadoSinDatos,
                    "Mercado Pago informa que el cliente ya pagó este cobro, pero todavía falta confirmar el pago: no se puede cancelar. " +
                    "Esperá la confirmación o usá la aprobación manual.");
            }

            try
            {
                var cancelada = await api.EnviarAsync<OrdenDeMercadoPago>(
                    cobro.IdEmpresa, HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(cobro.IdOrdenMercadoPago!)}/cancel",
                    claveIdempotencia: $"ways-cobro-{cobro.Id}-cancelar", ct: ct);

                var ahora = reloj.Ahora;
                AplicarOrden(cobro, cancelada, ahora);
                if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente && cancelada.Status != "processed")
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

    /// <summary>
    /// Consultar y cancelar: un cobro que ya no está vivo se devuelve sin lock ni llamada; uno vivo se vuelve a leer después de tomar su
    /// lock. Si está <c>creando</c> hay que reenviar la orden antes de operar, y eso se hace bajo el lock del punto de venta primero
    /// (el mismo orden que crear).
    /// </summary>
    private async Task<CobroDeMercadoPago> SobreCobroAsync(
        int idCobro, int idPuntoVenta, Func<CobroMercadoPago, Task<CobroDeMercadoPago>> operacion, CancellationToken ct)
    {
        var previo = await db.CobrosMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == idCobro && c.IdPuntoVenta == idPuntoVenta, ct)
            ?? throw CobroInexistente(idCobro);

        if (!EstadosDeCobroMercadoPago.EsVivo(previo.Estado))
        {
            return Mapear(previo);
        }

        if (previo.Estado == EstadosDeCobroMercadoPago.Creando)
        {
            return await EnLockAsync(idPuntoVenta, ClaveDeLockDeCobrosDelPuntoVenta, async () =>
            {
                await ServicioDeCajasMercadoPago.TomarLockAsync(db, idCobro, ClaveDeLockDeCobro, ct);
                db.ChangeTracker.Clear();
                var cobro = await LeerCobroAsync(idCobro, idPuntoVenta, ct);
                if (cobro.Estado == EstadosDeCobroMercadoPago.Creando)
                {
                    await ResolverCreandoAsync(cobro, ct);
                }

                return await operacion(cobro);
            }, ct);
        }

        return await EnLockAsync(idCobro, ClaveDeLockDeCobro, async () =>
        {
            db.ChangeTracker.Clear();
            return await operacion(await LeerCobroAsync(idCobro, idPuntoVenta, ct));
        }, ct);
    }

    /// <summary>Lee la orden de Mercado Pago y aplica su estado al cobro pendiente. Con <paramref name="respetarIntervalo"/> no hace
    /// nada si la última consulta es de hace menos de <see cref="IntervaloMinimoDeConsulta"/>. Quien la llama tiene el lock del cobro.
    /// Un 404 de una orden que ya pasó su plazo la da por vencida; antes del plazo sigue siendo un error.</summary>
    private async Task RefrescarAsync(CobroMercadoPago cobro, bool respetarIntervalo, CancellationToken ct)
    {
        var ahora = reloj.Ahora;
        if (respetarIntervalo && cobro.FechaUltimaConsulta is { } ultima && ahora - ultima < IntervaloMinimoDeConsulta)
        {
            return;
        }

        OrdenDeMercadoPago orden;
        try
        {
            orden = await api.EnviarAsync<OrdenDeMercadoPago>(
                cobro.IdEmpresa, HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(cobro.IdOrdenMercadoPago!)}", ct: ct);
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404 && ahora >= cobro.ExpiraEn)
        {
            cobro.Estado = EstadosDeCobroMercadoPago.Expirado;
            cobro.FechaUltimaConsulta = ahora;
            cobro.UpdatedAt = ahora;
            await db.SaveChangesAsync(ct);
            return;
        }

        AplicarOrden(cobro, orden, ahora);
        cobro.FechaUltimaConsulta = ahora;
        cobro.UpdatedAt = ahora;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Un estado de la orden que Ways no reconoce (<c>created</c>, <c>at_terminal</c>…) deja el cobro como está. <c>processed</c> solo
    /// aprueba si trae un pago con importe pagado e id válidos (la venta se apoya en ambos); si no, el cobro sigue como estaba y se avisa en el log.
    /// </summary>
    private void AplicarOrden(CobroMercadoPago cobro, OrdenDeMercadoPago orden, DateTimeOffset ahora)
    {
        switch (orden.Status)
        {
            case "processed":
                var pagos = orden.Transactions?.Payments;
                var pago = pagos?.FirstOrDefault(p => p.Status == "processed") ?? pagos?.FirstOrDefault();
                var pagado = pago is null ? null : ImportePagadoValido(pago.PaidAmount ?? pago.Amount);
                var idPago = pago?.Id?.Trim() is { Length: > 0 and <= LargoMaximoDeId } id ? id : null;
                if (pagado is null || idPago is null)
                {
                    cobro.FechaProcesadaMercadoPago ??= ahora;
                    log.LogWarning(
                        "La orden {IdOrden} de Mercado Pago figura procesada pero sin un pago con importe e id legibles: el cobro {IdCobro} sigue sin aprobarse.",
                        cobro.IdOrdenMercadoPago, cobro.Id);
                    break;
                }

                cobro.Estado = EstadosDeCobroMercadoPago.Aprobado;
                cobro.FechaAprobacion = ahora;
                cobro.IdPagoMercadoPago = idPago;
                cobro.ReferenciaPagoMercadoPago = pago!.ReferenciaNumerica;
                cobro.ImportePagado = pagado;
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
        importe is > 0m and <= ImporteMaximo && importe == Math.Round(importe.Value, 2) ? importe : null;

    private static SolicitudDeOrdenQrMercadoPago ArmarOrden(string externalReference, decimal importe, string externalPosId, long numero)
    {
        var total = ComoImporteDeMercadoPago(importe);
        return new SolicitudDeOrdenQrMercadoPago(
            "qr", externalReference, total, VigenciaDeLaOrden,
            new ConfiguracionDeOrdenMercadoPago(new ConfiguracionDeQrDeOrdenMercadoPago(externalPosId, "dynamic")),
            [new ItemDeOrdenMercadoPago($"Venta {numero}", total, 1, "unit")]);
    }

    private static CobroDeMercadoPago Mapear(CobroMercadoPago cobro)
    {
        var pendiente = cobro.Estado == EstadosDeCobroMercadoPago.Pendiente;
        var pagoInformadoSinDatos = pendiente && cobro.FechaProcesadaMercadoPago is not null;
        return new CobroDeMercadoPago(
            cobro.Id, cobro.Estado, pendiente && !pagoInformadoSinDatos ? cobro.QrData : null, cobro.ExpiraEn, cobro.Importe,
            cobro.ImportePagado,
            cobro.Estado == EstadosDeCobroMercadoPago.Aprobado && cobro.ImportePagado is { } pagado && pagado != cobro.Importe,
            pendiente && (cobro.QrData is null || pagoInformadoSinDatos),
            pagoInformadoSinDatos);
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

    /// <summary>
    /// Bajo el lock del punto de venta y justo antes de reservar: toma <c>FOR KEY SHARE</c> sobre la fila del medio, que choca con el
    /// <c>FOR UPDATE</c> de una edición del medio (<c>ServicioDeMediosPago</c>), y vuelve a validar el medio. O la edición confirma antes
    /// y se ve acá, o espera a que esta reserva confirme y ve el cobro.
    /// </summary>
    private async Task FijarMedioAsync(int idMedioPago, int idEmpresaDelPuntoVenta, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM medios_pago WHERE id_medio_pago = {idMedioPago} FOR KEY SHARE", ct);
        await ExigirMedioConQrAsync(idMedioPago, idEmpresaDelPuntoVenta, ct);
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

    /// <summary><c>ways-cobro-</c> más la huella SHA-256 del cuerpo completo de la orden, cortada para entrar en los 64 caracteres.</summary>
    private static string ClaveDeIdempotenciaDe(SolicitudDeOrdenQrMercadoPago cuerpo) =>
        PrefijoDeClave + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cuerpo))))[..(64 - PrefijoDeClave.Length)];
}
