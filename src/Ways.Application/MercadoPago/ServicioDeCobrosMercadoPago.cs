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
/// Cobros con QR dinámico o con la terminal Point de Mercado Pago que pide el dispositivo del POS (<c>cobros_mercadopago</c>). Crear, consultar y cancelar:
/// el punto de venta sale siempre de la sesión del dispositivo (nunca del cuerpo) y un cobro de otro punto de venta o de otro tenant
/// es un 404. Orden de los errores al crear: 403 si la sesión no es de un dispositivo vigente, 400 del cuerpo, 400 del medio (que
/// tiene que ser de la empresa del punto de venta y tener el proveedor <c>mercadopago_qr</c> o <c>mercadopago_point</c>), 409 del modo del punto de venta, y
/// ya bajo el lock del punto de venta el cobro vivo del número (409 <c>cobro_mercadopago_ya_aprobado</c> o
/// <c>cobro_mercadopago_en_curso</c>, o el mismo cobro si coinciden medio e importe), la conexión (409) y la caja (409
/// <c>mercadopago_caja_no_registrada</c>).
///
/// <b>Point (E4).</b> El proveedor del medio decide el tipo del cobro. Un cobro Point pide <c>type: "point"</c> a la terminal de la caja
/// (guardada en el cobro: un reintento reenvía la misma; sin terminal, 409 <c>mercadopago_caja_sin_terminal</c>), no tiene QR, y su orden
/// pasa por <c>created</c>, <c>at_terminal</c> y <c>action_required</c> (situación en la terminal, <c>pendiente</c>), <c>processed</c>
/// (<c>aprobado</c>) o <c>failed</c> (<c>fallido</c>, no vivo: libera el número, pero se sigue leyendo hasta que pasa el plazo de la orden más
/// el margen de abandono; si Mercado Pago informa después un pago legible el cobro pasa a <c>sin_venta</c>, nunca a <c>aprobado</c>, porque
/// su número pudo reutilizarse; <c>canceled</c> o <c>expired</c> lo cierran como <c>cancelado</c> o <c>expirado</c>). Solo se cancela con la orden <c>created</c>; ya en la terminal se cancela
/// ahí (409 <c>mercadopago_cancelar_en_la_terminal</c>). Un 409 <c>already_queued_order_for_terminal</c> al crear es 409
/// <c>mercadopago_terminal_ocupada</c> y la fila pasa a <c>cancelado</c> (sin orden): ese código exacto rechaza la creación, no es un
/// reenvío idempotente de una orden ya creada, y el número queda libre para otro intento. El cuerpo Point no lleva <c>total_amount</c>.
/// Antes de leer la terminal de la caja se toma el lock de la caja (el mismo de asignar y quitar la terminal).
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
/// <b>Locks</b> (<c>single-read-under-lock</c>), siempre en el orden punto de venta → cobro (→ caja, solo al crear: la terminal se lee bajo el lock de la caja): crear toma el lock del punto de venta y,
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
/// <b>Apartar.</b> El dispositivo aparta un cobro cuya venta no se va a cerrar (<see cref="ApartarAsync"/>): un cobro aprobado sin venta
/// pasa a <c>sin_venta</c>, que no es vivo (libera el número y el medio), no paga ninguna venta y queda listado para la conciliación
/// (<see cref="ListarSinVentaAsync"/>).
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

    /// <summary>Un fallido con pago informado sin datos legibles se sigue leyendo hasta este plazo desde la reserva.</summary>
    private static readonly TimeSpan TopeDeVigilanciaConPagoInformado = TimeSpan.FromHours(24);

    private const decimal ImporteMaximo = 9_999_999_999_999m;

    private const int LargoMaximoDeId = 64;

    private const int MaximoDeVueltasAlCrear = 5;

    private const string PrefijoDeClave = "ways-cobro-";

    /// <summary>Código con que Mercado Pago contesta 409 a una orden Point cuando la terminal ya tiene otra esperando.</summary>
    private const string CodigoDeTerminalOcupada = "already_queued_order_for_terminal";

    public async Task<CobroDeMercadoPago> CrearAsync(SolicitudDeCobroMercadoPago solicitud, CancellationToken ct = default)
    {
        var (idDispositivo, idPuntoVenta, idTenant) = await ResolverDispositivoAsync(ct);
        ValidarSolicitud(solicitud);

        var puntoVenta = await db.PuntosVenta
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        await ExigirMedioDeCobroAsync(solicitud.IdMedioPago, puntoVenta.IdEmpresa, ct);
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        // Cada vuelta es una transacción bajo el lock del punto de venta. Reservar la fila confirma y termina la vuelta sin respuesta:
        // la siguiente la encuentra creando, reenvía la orden y aplica las reglas del cobro vivo.
        int? reservadoEnEstaLlamada = null;
        for (var vuelta = 0; vuelta < MaximoDeVueltasAlCrear; vuelta++)
        {
            var (respuesta, idReservado) = await EnLockAsync(
                idPuntoVenta, ClaveDeLockDeCobrosDelPuntoVenta,
                () => CrearBajoLockAsync(solicitud, puntoVenta, idDispositivo, idTenant, reservadoEnEstaLlamada, ct), ct);
            if (respuesta is not null)
            {
                return respuesta;
            }

            reservadoEnEstaLlamada ??= idReservado;
        }

        throw ErrorDominio.Conflicto(
            CodigosDeErrorDeCobrosMercadoPago.EnCurso, "Esa venta tiene un cobro con Mercado Pago en curso: consultá su estado antes de cobrar de nuevo.");
    }

    /// <summary>Devuelve la respuesta al dispositivo, o sin respuesta y con el id si reservó una fila nueva (<c>creando</c>) que falta
    /// completar. <paramref name="reservadoEnEstaLlamada"/> es la fila que reservó una vuelta anterior de esta misma llamada: su primer envío.</summary>
    private async Task<(CobroDeMercadoPago? Respuesta, int? IdReservado)> CrearBajoLockAsync(
        SolicitudDeCobroMercadoPago solicitud, PuntoVenta puntoVenta, int idDispositivo, int idTenant, int? reservadoEnEstaLlamada,
        CancellationToken ct)
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
                await ResolverCreandoAsync(vivo, vivo.Id == reservadoEnEstaLlamada, ct);
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
                    return (Mapear(vivo), null);
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

        // Lock de la caja (orden: punto de venta -> cobro -> caja; asignar, quitar y registrar la terminal toman sucursal -> caja y nunca
        // el del punto de venta de cobros): o la reasignación confirma antes y se ve acá, o espera a que esta reserva confirme.
        await ServicioDeCajasMercadoPago.TomarLockAsync(db, puntoVenta.Id, ServicioDeCajasMercadoPago.ClaveDeLockDeCaja, ct);
        var caja = await db.CajasMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdPuntoVenta == puntoVenta.Id && c.IdUsuarioMercadoPago == idUsuarioMercadoPago, ct)
            ?? throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.CajaNoRegistrada,
                "El punto de venta todavía no está registrado como caja en Mercado Pago: registralo primero.",
                409);

        var proveedor = await FijarMedioAsync(solicitud.IdMedioPago, puntoVenta.IdEmpresa, ct);
        var tipo = proveedor == ProveedorDeCobro.MercadoPagoPoint ? TiposDeCobroMercadoPago.Point : TiposDeCobroMercadoPago.Qr;
        var idTerminal = tipo == TiposDeCobroMercadoPago.Point
            ? caja.IdTerminalPoint ?? throw new ErrorDominio(
                CodigosDeErrorDeCobrosMercadoPago.CajaSinTerminal,
                "La caja del punto de venta no tiene una terminal Point asignada: asignala antes de cobrar con Point.",
                409)
            : null;

        var externalReference = $"W{idTenant}P{puntoVenta.Id}N{solicitud.NumeroPreasignado}I{intento}";
        var cuerpo = ArmarOrden(tipo, externalReference, solicitud.Importe, caja.ExternalId, idTerminal, solicitud.NumeroPreasignado);
        var ahora = reloj.Ahora;

        var nuevo = new CobroMercadoPago
        {
            IdTenant = idTenant,
            IdEmpresa = puntoVenta.IdEmpresa,
            IdPuntoVenta = puntoVenta.Id,
            IdDispositivo = idDispositivo,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroPreasignado = solicitud.NumeroPreasignado,
            Intento = intento,
            Tipo = tipo,
            Importe = solicitud.Importe,
            ExternalReference = externalReference,
            ExternalPosId = caja.ExternalId,
            IdTerminalPoint = idTerminal,
            ClaveIdempotencia = ClaveDeIdempotenciaDe(cuerpo),
            Estado = EstadosDeCobroMercadoPago.Creando,
            ExpiraEn = ahora + Vigencia,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.CobrosMercadoPago.Add(nuevo);
        await db.SaveChangesAsync(ct);

        return (null, nuevo.Id);
    }

    /// <summary>
    /// Resuelve una fila <c>creando</c> según el instante en que se reservó (<c>created_at</c>). Dentro de <see cref="Vigencia"/> reenvía
    /// la orden. Pasado ese plazo no la reenvía nunca más (un reenvío podría crear una orden nueva y cobrable) y responde 409 mientras
    /// una orden creada por un reenvío anterior pueda seguir viva; cuando ya no puede, la fila se da por <c>expirado</c> sin llamar a
    /// Mercado Pago y libera el número. Un 4xx no prueba que la orden no exista, por eso nunca cancela la fila (salvo el 409 exacto de terminal ocupada de Point, que rechaza la creación). Quien la llama tiene el
    /// lock del punto de venta y el del cobro y acaba de leer la fila.
    /// </summary>
    private async Task ResolverCreandoAsync(CobroMercadoPago cobro, bool primerEnvio, CancellationToken ct)
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

        await CompletarCreacionAsync(cobro, primerEnvio, ct);
    }

    /// <summary>
    /// Pide a Mercado Pago la orden guardada en una fila <c>creando</c> (la original, con su clave: si ya existe, la devuelve) y
    /// aplica la respuesta. Quien la llama tiene el lock del punto de venta y el del cobro y acaba de leer la fila.
    /// </summary>
    private async Task CompletarCreacionAsync(CobroMercadoPago cobro, bool primerEnvio, CancellationToken ct)
    {
        var esPoint = cobro.Tipo == TiposDeCobroMercadoPago.Point;
        var cuerpo = ArmarOrden(
            cobro.Tipo, cobro.ExternalReference, cobro.Importe, cobro.ExternalPosId, cobro.IdTerminalPoint, cobro.NumeroPreasignado);

        OrdenDeMercadoPago orden;
        try
        {
            orden = await api.EnviarAsync<OrdenDeMercadoPago>(
                cobro.IdEmpresa, HttpMethod.Post, "/v1/orders", cuerpo, cobro.ClaveIdempotencia, ct);
        }
        catch (ErrorDeApiMercadoPago error) when (esPoint && error.EstadoMercadoPago == 409 && error.CodigoMercadoPago == CodigoDeTerminalOcupada)
        {
            // En el primer envío de la fila no hay orden anterior: el 409 rechaza la creación, se cancela y se libera el número. En un
            // reenvío pudo haber una orden creada cuya respuesta se perdió, y Mercado Pago puede mirar la cola de la terminal antes de
            // reconocer la clave: la fila sigue creando (retiene el número) y las bandas de tiempo la liberan.
            if (!primerEnvio)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.TerminalOcupada,
                    "La terminal tiene otra orden esperando: terminá o cancelá ese cobro en la terminal y probá de nuevo.");
            }

            var ahoraOcupada = reloj.Ahora;
            cobro.Estado = EstadosDeCobroMercadoPago.Cancelado;
            cobro.SituacionTerminal = null;
            cobro.UpdatedAt = ahoraOcupada;
            await db.SaveChangesAsync(ct);
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeCobrosMercadoPago.TerminalOcupada,
                "La terminal tiene otra orden esperando: terminá o cancelá ese cobro en la terminal y probá de nuevo.");
        }

        var id = orden.Id?.Trim();
        var qr = orden.TypeResponse?.QrData?.Trim();
        var conEstadoFinal = orden.Status is "processed" or "canceled" or "expired" or "refunded" or "failed";
        if (string.IsNullOrEmpty(id) || id.Length > LargoMaximoDeId || (!esPoint && !conEstadoFinal && string.IsNullOrEmpty(qr)))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeMercadoPago.RespuestaInvalida,
                esPoint ? "Mercado Pago respondió con una orden sin identificador." : "Mercado Pago respondió con una orden sin identificador o sin QR.",
                502);
        }

        var ahora = reloj.Ahora;
        cobro.IdOrdenMercadoPago = id;
        cobro.ExpiraEn = ahora + Vigencia;
        AplicarOrden(cobro, orden, ahora);
        if (cobro.Estado == EstadosDeCobroMercadoPago.Creando)
        {
            cobro.Estado = EstadosDeCobroMercadoPago.Pendiente;
        }

        cobro.QrData = cobro.Estado == EstadosDeCobroMercadoPago.Pendiente && !esPoint ? qr : null;
        cobro.UpdatedAt = ahora;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CobroDeMercadoPago> ObtenerAsync(int idCobro, CancellationToken ct = default)
    {
        var (_, idPuntoVenta, _) = await ResolverDispositivoAsync(ct);

        return await SobreCobroAsync(idCobro, idPuntoVenta, async cobro =>
        {
            if (SeSigueVigilando(cobro))
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

            await CancelarPendienteAsync(cobro, ct);

            return Mapear(cobro);
        }, ct);
    }

    /// <summary>
    /// El dispositivo aparta un cobro que su venta no va a usar. Un cobro <c>aprobado</c> sin venta pasa a <c>sin_venta</c> (con
    /// <c>fecha_apartado</c>): el dinero entró, el número y el medio quedan libres y la conciliación lo resuelve. Un cobro
    /// <c>creando</c> o <c>pendiente</c> se resuelve primero (se reenvía o se refresca): si todavía se puede pagar se cancela en
    /// Mercado Pago y termina <c>cancelado</c>, y si ya se pagó termina <c>sin_venta</c>. Un cobro que una venta ya usó es 409
    /// <c>cobro_mercadopago_ya_usado</c>; uno ya <c>sin_venta</c>, cancelado, vencido o devuelto se devuelve tal cual (idempotente).
    /// Si Mercado Pago informa el pago sin datos legibles es 409 <c>cobro_mercadopago_pagado_sin_datos</c> (como al cancelar). La fila
    /// se toma con <c>FOR UPDATE</c> antes de leerla, el mismo lock con que la venta liga el cobro: o la venta confirma antes y se ve
    /// acá, o el apartado confirma antes y la venta ya no encuentra el cobro aprobado.
    /// </summary>
    public async Task<CobroDeMercadoPago> ApartarAsync(int idCobro, CancellationToken ct = default)
    {
        var (_, idPuntoVenta, _) = await ResolverDispositivoAsync(ct);

        return await SobreCobroAsync(idCobro, idPuntoVenta, async cobro =>
        {
            if (cobro.IdComprobanteVenta is not null)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDePagoConMercadoPago.YaUsado, "El cobro de Mercado Pago ya pagó una venta: no se puede apartar.");
            }

            if (SeSigueVigilando(cobro))
            {
                await RefrescarAsync(cobro, respetarIntervalo: false, ct);
            }

            if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente)
            {
                await CancelarPendienteAsync(cobro, ct);
            }

            if (cobro.Estado == EstadosDeCobroMercadoPago.Aprobado)
            {
                var ahora = reloj.Ahora;
                cobro.Estado = EstadosDeCobroMercadoPago.SinVenta;
                cobro.FechaApartado = ahora;
                cobro.UpdatedAt = ahora;
                await db.SaveChangesAsync(ct);
            }

            return Mapear(cobro);
        }, ct, bloquearFila: true);
    }

    /// <summary>
    /// El aviso firmado de Mercado Pago dice que la orden del cobro cambió: lee la orden y aplica su estado sin el límite de una
    /// consulta cada <see cref="IntervaloMinimoDeConsulta"/> por cobro, igual que el sondeo del dispositivo: un aviso firmado capturado
    /// y repetido dentro de la ventana de la firma no puede amplificarse en llamadas a Mercado Pago. Corre bajo el lock del cobro
    /// y con una sola lectura de la fila después de tomarlo; solo un cobro <c>pendiente</c> se refresca, cualquier otro estado ya es
    /// final (o todavía no tiene orden) y no se toca. No usa el dispositivo de la sesión: quien llama ya fijó el tenant del cobro.
    /// </summary>
    public async Task ActualizarPorAvisoAsync(int idCobro, CancellationToken ct = default) =>
        await EnLockAsync(idCobro, ClaveDeLockDeCobro, async () =>
        {
            db.ChangeTracker.Clear();
            var cobro = await db.CobrosMercadoPago.FirstOrDefaultAsync(c => c.Id == idCobro, ct);
            if (cobro is not null && SeSigueVigilando(cobro))
            {
                await RefrescarAsync(cobro, respetarIntervalo: true, ct);
            }

            return true;
        }, ct);

    /// <summary>Los cobros apartados (<c>sin_venta</c>) de una empresa, del más reciente al más antiguo, para que la conciliación los resuelva.</summary>
    public async Task<IReadOnlyList<CobroSinVentaListado>> ListarSinVentaAsync(int idEmpresa, CancellationToken ct = default)
    {
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }

        return await db.CobrosMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa
                && (c.Estado == EstadosDeCobroMercadoPago.SinVenta
                    || (c.Estado == EstadosDeCobroMercadoPago.Fallido && c.FechaProcesadaMercadoPago != null)))
            .OrderByDescending(c => c.FechaApartado ?? c.FechaProcesadaMercadoPago)
            .ThenByDescending(c => c.Id)
            .Select(c => new CobroSinVentaListado(
                c.Id, c.IdPuntoVenta, c.IdMedioPago, c.NumeroPreasignado, c.Importe, c.ImportePagado, c.IdPagoMercadoPago,
                c.IdOrdenMercadoPago, c.FechaAprobacion, c.FechaApartado, c.Estado == EstadosDeCobroMercadoPago.Fallido))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Cancela en Mercado Pago un cobro pendiente y aplica la respuesta; un 409 lee la orden, que pudo pagarse. Un pago ya informado
    /// sin datos legibles no se cancela (409). Quien la llama tiene el lock del cobro.
    /// </summary>
    private async Task CancelarPendienteAsync(CobroMercadoPago cobro, CancellationToken ct)
    {
        if (cobro.FechaProcesadaMercadoPago is not null)
        {
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeCobrosMercadoPago.PagadoSinDatos,
                "Mercado Pago informa que el cliente ya pagó este cobro, pero todavía falta confirmar el pago: no se puede cancelar. " +
                "Esperá la confirmación o usá la aprobación manual.");
        }

        ExigirCancelableDesdeElPos(cobro);

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
            ExigirCancelableDesdeElPos(cobro);

            // Un pago informado sin datos legibles es otro desenlace (el cobro se muestra como verificando), no una cancelación fallida.
            if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente && cobro.FechaProcesadaMercadoPago is null)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeCobrosMercadoPago.CancelacionNoAplicada,
                    "Mercado Pago no pudo cancelar el cobro y la orden sigue pendiente: consultá su estado y probá de nuevo.");
            }
        }
    }

    /// <summary>
    /// Mercado Pago solo cancela una orden Point en estado <c>created</c>: una que ya llegó a la terminal (o que pide mirarla) se
    /// cancela ahí. Se mira la última lectura del cobro; si cambió desde entonces, el 409 de Mercado Pago la refresca y se vuelve a mirar.
    /// </summary>
    private static void ExigirCancelableDesdeElPos(CobroMercadoPago cobro)
    {
        if (cobro.Estado == EstadosDeCobroMercadoPago.Pendiente && cobro.Tipo == TiposDeCobroMercadoPago.Point && cobro.SituacionTerminal is not null)
        {
            throw ErrorDominio.Conflicto(
                CodigosDeErrorDeCobrosMercadoPago.CancelarEnLaTerminal,
                "El cobro ya está en la terminal: cancelalo en la terminal y esperá a que el POS lo refleje.");
        }
    }

    /// <summary>
    /// Consultar y cancelar: un cobro que ya no está vivo se devuelve sin lock ni llamada; uno vivo se vuelve a leer después de tomar su
    /// lock. Si está <c>creando</c> hay que reenviar la orden antes de operar, y eso se hace bajo el lock del punto de venta primero
    /// (el mismo orden que crear).
    /// </summary>
    private async Task<CobroDeMercadoPago> SobreCobroAsync(
        int idCobro, int idPuntoVenta, Func<CobroMercadoPago, Task<CobroDeMercadoPago>> operacion, CancellationToken ct,
        bool bloquearFila = false)
    {
        var previo = await db.CobrosMercadoPago
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == idCobro && c.IdPuntoVenta == idPuntoVenta, ct)
            ?? throw CobroInexistente(idCobro);

        if (!EstadosDeCobroMercadoPago.EsVivo(previo.Estado) && !SeSigueVigilando(previo))
        {
            return Mapear(previo);
        }

        if (previo.Estado == EstadosDeCobroMercadoPago.Creando)
        {
            return await EnLockAsync(idPuntoVenta, ClaveDeLockDeCobrosDelPuntoVenta, async () =>
            {
                await ServicioDeCajasMercadoPago.TomarLockAsync(db, idCobro, ClaveDeLockDeCobro, ct);
                db.ChangeTracker.Clear();
                if (bloquearFila)
                {
                    await BloquearFilaAsync(idCobro, ct);
                }

                var cobro = await LeerCobroAsync(idCobro, idPuntoVenta, ct);
                if (cobro.Estado == EstadosDeCobroMercadoPago.Creando)
                {
                    await ResolverCreandoAsync(cobro, primerEnvio: false, ct);
                }

                return await operacion(cobro);
            }, ct);
        }

        return await EnLockAsync(idCobro, ClaveDeLockDeCobro, async () =>
        {
            db.ChangeTracker.Clear();
            if (bloquearFila)
            {
                await BloquearFilaAsync(idCobro, ct);
            }

            return await operacion(await LeerCobroAsync(idCobro, idPuntoVenta, ct));
        }, ct);
    }

    /// <summary>
    /// Un cobro <c>pendiente</c> se refresca siempre; uno <c>fallido</c> (Point) también hasta que pasa el plazo de su orden más el
    /// margen de abandono: <c>failed</c> pudo ser un rechazo del primer pago y el cliente reintentar o pagar en la terminal. No es vivo
    /// (libera el número), pero si Mercado Pago informa después el pago el dinero tiene que verse.
    /// </summary>
    private bool SeSigueVigilando(CobroMercadoPago cobro) =>
        cobro.Estado == EstadosDeCobroMercadoPago.Pendiente
        || (cobro.Estado == EstadosDeCobroMercadoPago.Fallido
            && (reloj.Ahora < cobro.ExpiraEn + MargenDeAbandono
                || (cobro.FechaProcesadaMercadoPago is not null && reloj.Ahora < cobro.CreatedAt + TopeDeVigilanciaConPagoInformado)));

    /// <summary><c>FOR UPDATE</c> de la fila del cobro, antes de leerla: es el lock con que la venta liga el cobro aprobado.</summary>
    private async Task BloquearFilaAsync(int idCobro, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM cobros_mercadopago WHERE id_cobro_mercadopago = {idCobro} FOR UPDATE", ct);

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
        catch (ErrorDominio error) when (cobro.Estado == EstadosDeCobroMercadoPago.Fallido && !ct.IsCancellationRequested)
        {
            // La vigilancia de un fallido es un extra: un error de Mercado Pago o de la red no puede romper la consulta del rechazo.
            log.LogWarning(
                "No se pudo releer la orden {IdOrden} del cobro fallido {IdCobro} ({Codigo}): se devuelve el estado guardado.",
                cobro.IdOrdenMercadoPago, cobro.Id, error.Codigo);
            // Cuenta como consulta para el intervalo: mientras Mercado Pago falle, cada sondeo no vuelve a pegarle antes de tiempo.
            cobro.FechaUltimaConsulta = ahora;
            return;
        }
        catch (ErrorDeApiMercadoPago error) when (error.EstadoMercadoPago == 404 && ahora >= cobro.ExpiraEn)
        {
            cobro.Estado = EstadosDeCobroMercadoPago.Expirado;
            cobro.SituacionTerminal = null;
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
    /// Un estado de la orden que no cambia el estado del cobro (<c>created</c>, <c>at_terminal</c>, <c>action_required</c>…) lo deja como está (Point guarda dónde está la orden en <c>situacion_terminal</c>); <c>failed</c> solo cierra un cobro Point (<c>fallido</c>). <c>processed</c> solo
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

                // Un fallido ya liberó su número (otro cobro pudo tomarlo): el pago tardío no aprueba, queda a la vista de la conciliación.
                if (cobro.Estado == EstadosDeCobroMercadoPago.Fallido)
                {
                    cobro.Estado = EstadosDeCobroMercadoPago.SinVenta;
                    cobro.FechaApartado = ahora;
                }
                else
                {
                    cobro.Estado = EstadosDeCobroMercadoPago.Aprobado;
                }

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
            case "failed" when cobro.Tipo == TiposDeCobroMercadoPago.Point:
                cobro.Estado = EstadosDeCobroMercadoPago.Fallido;
                break;
        }

        // Dónde está la orden Point en la terminal (created: todavía en ningún lado): solo vale mientras el cobro sigue por resolverse.
        var sigue = cobro.Estado is EstadosDeCobroMercadoPago.Pendiente or EstadosDeCobroMercadoPago.Creando;
        cobro.SituacionTerminal = sigue && cobro.Tipo == TiposDeCobroMercadoPago.Point
            ? orden.Status switch
            {
                "at_terminal" => SituacionesDeTerminalMercadoPago.EnTerminal,
                "action_required" => SituacionesDeTerminalMercadoPago.RevisarTerminal,
                _ => null
            }
            : null;
    }

    private static decimal? ImportePagadoValido(decimal? importe) =>
        importe is > 0m and <= ImporteMaximo && importe == Math.Round(importe.Value, 2) ? importe : null;

    /// <summary>Cuerpo de <c>POST /v1/orders</c> según el tipo del cobro: Point lleva la terminal y el pago; el QR, la caja y el ítem.</summary>
    private static object ArmarOrden(
        string tipo, string externalReference, decimal importe, string externalPosId, string? idTerminal, long numero)
    {
        var total = ComoImporteDeMercadoPago(importe);
        if (tipo == TiposDeCobroMercadoPago.Point)
        {
            return new SolicitudDeOrdenPointMercadoPago(
                "point", externalReference, VigenciaDeLaOrden,
                new ConfiguracionDeOrdenPointMercadoPago(new ConfiguracionDePointDeOrdenMercadoPago(idTerminal!, "no_ticket")),
                new TransaccionesDeSolicitudPointMercadoPago([new PagoDeSolicitudPointMercadoPago(total)]));
        }

        return new SolicitudDeOrdenQrMercadoPago(
            "qr", externalReference, total, VigenciaDeLaOrden,
            new ConfiguracionDeOrdenMercadoPago(new ConfiguracionDeQrDeOrdenMercadoPago(externalPosId, "dynamic")),
            [new ItemDeOrdenMercadoPago($"Venta {numero}", total, 1, "unit")]);
    }

    private static CobroDeMercadoPago Mapear(CobroMercadoPago cobro)
    {
        var pendiente = cobro.Estado == EstadosDeCobroMercadoPago.Pendiente;
        var esQr = cobro.Tipo == TiposDeCobroMercadoPago.Qr;
        var pagoInformadoSinDatos = pendiente && cobro.FechaProcesadaMercadoPago is not null;
        return new CobroDeMercadoPago(
            cobro.Id, cobro.Estado, pendiente && !pagoInformadoSinDatos ? cobro.QrData : null, cobro.ExpiraEn, cobro.Importe,
            cobro.ImportePagado,
            cobro.Estado == EstadosDeCobroMercadoPago.Aprobado && cobro.ImportePagado is { } pagado && pagado != cobro.Importe,
            pendiente && ((esQr && cobro.QrData is null) || pagoInformadoSinDatos),
            pagoInformadoSinDatos,
            cobro.IdComprobanteVenta is not null,
            cobro.Tipo,
            cobro.IdTerminalPoint,
            pendiente && cobro.SituacionTerminal == SituacionesDeTerminalMercadoPago.EnTerminal,
            pendiente && cobro.SituacionTerminal == SituacionesDeTerminalMercadoPago.RevisarTerminal);
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

    private async Task<string> ExigirMedioDeCobroAsync(int idMedioPago, int idEmpresaDelPuntoVenta, CancellationToken ct)
    {
        var medio = await db.MediosPago
            .AsNoTracking()
            .Where(m => m.Id == idMedioPago)
            .Select(m => new { m.Activo, m.IdEmpresa, m.Proveedor })
            .FirstOrDefaultAsync(ct);

        if (medio is not { Activo: true, Proveedor: ProveedorDeCobro.MercadoPagoQr or ProveedorDeCobro.MercadoPagoPoint }
            || (medio.IdEmpresa is { } idEmpresa && idEmpresa != idEmpresaDelPuntoVenta))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeCobrosMercadoPago.MedioInvalido,
                "El medio de pago no existe, está inactivo o no cobra con Mercado Pago (QR o Point).",
                400);
        }

        return medio.Proveedor;
    }

    /// <summary>
    /// Bajo el lock del punto de venta y justo antes de reservar: toma <c>FOR KEY SHARE</c> sobre la fila del medio, que choca con el
    /// <c>FOR UPDATE</c> de una edición del medio (<c>ServicioDeMediosPago</c>), y vuelve a validar el medio. O la edición confirma antes
    /// y se ve acá, o espera a que esta reserva confirme y ve el cobro.
    /// </summary>
    private async Task<string> FijarMedioAsync(int idMedioPago, int idEmpresaDelPuntoVenta, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM medios_pago WHERE id_medio_pago = {idMedioPago} FOR KEY SHARE", ct);
        return await ExigirMedioDeCobroAsync(idMedioPago, idEmpresaDelPuntoVenta, ct);
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

    /// <summary>404 con código propio: el POS lo distingue de un 404 de ruteo (un servidor sin el endpoint, un proxy) antes de dar
    /// por resuelto un cobro apartado.</summary>
    private static ErrorDominio CobroInexistente(int idCobro) =>
        new("cobro_mercadopago_inexistente", $"No existe el cobro {idCobro}.", 404);

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
    private static string ClaveDeIdempotenciaDe(object cuerpo) =>
        PrefijoDeClave + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cuerpo, cuerpo.GetType()))))[..(64 - PrefijoDeClave.Length)];
}
