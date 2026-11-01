using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Api.Endpoints;

public static class VentasEndpoints
{
    public static IEndpointRouteBuilder MapearVentas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ventas")
            .WithTags("Ventas")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-5-pos-ventas (Slice 4, task 4.6, design: API Surface): checkout — sin
        // GestionDeCatalogo apilado (design: Authorization Surface, "/api/ventas... solo
        // GestionDeCatalogo en POST /api/stock/ajustes"). Un Vendedor tiene que poder vender.
        //
        // Facturación por medio de pago: con la venta ya comiteada, el servidor decide en una transacción propia si el
        // ticket se factura. Nada de lo fiscal hace fallar la venta (DecidirEmitirYSanearAsync no lanza) ni recibe el token
        // de la solicitud: la venta ya está comiteada y un corte del cliente no debe dejarla sin decidir. Decide, si el
        // ticket quedó pendiente intenta emitir la factura con un presupuesto corto y, con lo que quede del tope, decide
        // las ventas debidas del mismo punto de venta (salvo que esta decisión haya terminado en sin_decidir).
        grupo.MapPost("/", async (
            ServicioDeVentas servicio, EmisionEnLineaAlCobrar emisionEnLinea, SolicitudDeVenta solicitud,
            CancellationToken ct) =>
        {
            var emitido = await servicio.EmitirAsync(solicitud, ct);
            var fiscal = await emisionEnLinea.DecidirEmitirYSanearAsync(emitido.Id, emitido.IdPuntoVenta);
            return Results.Created($"/api/ventas/{emitido.Id}", emitido with { Fiscal = fiscal });
        })
        .WithSummary("Emite un comprobante de venta (checkout). Todo el dinero se recalcula server-side.");

        // La lectura informa el estado fiscal igual que la emisión (con los datos de la factura si está aprobada, para
        // reimprimirla), pero solo lo lee: no decide, no emite ni llama a ARCA. Va acá y no en ObtenerAsync porque la
        // anulación también lo usa.
        grupo.MapGet("/{id:int}", async (
            ServicioDeVentas servicio, ServicioDeFacturacionDeVentas facturacion, int id, CancellationToken ct) =>
        {
            var comprobante = await servicio.ObtenerAsync(id, ct);
            return comprobante with { Fiscal = await facturacion.LeerEstadoDeVentaAsync(id, ct) };
        })
        .WithSummary(
            "Reimpresión: lee el snapshot del comprobante sin re-joinear el catálogo e informa su estado fiscal. " +
            "Con la factura aprobada, sus datos se leen del catálogo aunque esté dado de baja; si no se pueden leer, " +
            "el estado fiscal va sin ellos.");

        // stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): RequiereDispositivo apilado
        // sobre OperacionDePos (AND) — solo una sesión de dispositivo puede reservar un bloque
        // offline, y sigue necesitando un rol de venta. Bajo el mismo grupo /api/ventas por ser
        // el mismo espacio de numeración que el checkout, no un ABM aparte.
        grupo.MapPost("/reservas-numeracion", async (
            ServicioDeReservasDeNumeracion servicio, SolicitudDeReservaDeNumeracion solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.ReservarAsync(solicitud, ct)))
        .RequireAuthorization(Politicas.RequiereDispositivo)
        .WithSummary(
            "Reserva un bloque de números para vender offline. Pedir uno nuevo con un bloque " +
            "vigente lo abandona (pérdida de almacenamiento local) y entrega uno fresco.");

        // stage-desktop-pos ("Ventas del turno"): listado dedicado, no el paginado genérico de
        // abajo — filtrado por idTurno (que ese no expone), incluye anuladas, y trae
        // cliente/medios de pago porque el conjunto de un turno está acotado (ver doc-comment de
        // ServicioDeVentas.ListarPorTurnoAsync). Literal "por-turno" antes que "{id:int}" en el
        // orden de declaración no importa para el ruteo (los segmentos literales siempre ganan
        // sobre un parámetro), pero se declara acá al lado por lectura.
        grupo.MapGet("/por-turno/{idTurno:int}", (ServicioDeVentas servicio, int idTurno, CancellationToken ct) =>
            servicio.ListarPorTurnoAsync(idTurno, ct))
        .WithSummary(
            "Ventas de un turno (incluye anuladas), con cliente, medios de pago y estado fiscal — pantalla Ventas del turno.");

        // stage-5-pos-ventas (Slice 5, task 5.2, design: API Surface): POST, no DELETE — produce
        // filas (movimientos inversos + contramovimiento CC), no elimina ninguna. Sin
        // GestionDeCatalogo apilado (spec: OperacionDePos Authorization For Emission and
        // Anulación — un Vendedor puede anular su propia venta, mismo criterio que emitir).
        grupo.MapPost("/{id:int}/anulacion", async (
            ServicioDeVentas servicio, EmisionEnLineaAlCobrar emisionEnLinea, int id, CancellationToken ct) =>
        {
            var anulado = await servicio.AnularAsync(id, ct);

            // Con la factura aprobada la anulación dejó decidida la nota de crédito: se intenta emitirla con un presupuesto corto
            // y sin el token de la solicitud (la anulación ya está comiteada). No lanza; lo que no salga lo emite el lote.
            await emisionEnLinea.IntentarNotaDeCreditoAsync(id);
            return Results.Ok(anulado with { Fiscal = await emisionEnLinea.LeerEstadoTrasAnularAsync(id) });
        })
        .WithSummary(
            "Anula un comprobante: revierte stock y cuenta corriente en la misma transacción. No existe restaurar. " +
            "Con la factura aprobada decide la nota de crédito (3/8/13) y la intenta emitir en línea; con la factura en trámite " +
            "responde 409 venta_con_factura_en_tramite. La respuesta informa el estado fiscal, con fiscal.notaDeCredito.");

        // Corrección del medio de un ticket desde la caja: mismo criterio que la anulación (un Vendedor corrige lo que cobró), y
        // solo con el turno de la venta abierto.
        grupo.MapPut("/{id:int}/medios", async (
            ServicioDeCambioDeMedios servicio, ServicioDeFacturacionDeVentas facturacion, int id,
            SolicitudDeCambioDeMedios solicitud, CancellationToken ct) =>
        {
            var venta = await servicio.CambiarDesdeLaCajaAsync(id, solicitud, ct);
            return Results.Ok(venta with { Fiscal = await facturacion.LeerEstadoDeVentaAsync(id, ct) });
        })
        .WithSummary(
            "Reemplaza los medios de pago de una venta del turno abierto sin cambiar lo cobrado. Rechaza con 409 una venta con " +
            "CAE, con la factura en trámite, con cuenta corriente, anulada, de un turno cerrado o de una fecha conciliada.");

        grupo.MapGet("/", (
            ServicioDeVentas servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? idCliente,
            EstadoComprobante? estado,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, idCliente, estado, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista comprobantes de venta con filtros y paginado.");

        // stage-11-exportacion-reportes (Slice 3, design decisión 7): sibling declarado
        // inmediatamente después de su ruta fuente — hereda OperacionDePos por co-locación, sin
        // política propia. `desde`/`hasta` son OBLIGATORIOS acá (a diferencia del listado JSON):
        // un export es por definición un rango acotado, y el nombre de archivo determinístico
        // necesita ambas fechas (spec exportacion-de-reportes: XLSX Response Contract And
        // Deterministic Naming).
        grupo.MapGet("/export", async (
            ServicioDeVentas servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj, ServicioDeParametros parametros, IWaysDbContext db,
            int? idPuntoVenta, DateTimeOffset desde, DateTimeOffset hasta, int? idCliente, EstadoComprobante? estado,
            string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarParaExportacionAsync(
                idPuntoVenta, desde, hasta, idCliente, estado, opciones.Value.TopeDeFilas, ct);

            var (empresa, zonaId) = await AlcanceDeListadoHttp.ResolverAsync(db, parametros, idPuntoVenta, ct);
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);
            var (desdeFecha, hastaFecha) = FechaDelRango.De(desde, hasta);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, empresa, idPuntoVenta is { } id ? $"PV {id}" : null, desdeFecha, hastaFecha, zonaId);
            var tabla = ExportacionDeListados.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var alcance = idPuntoVenta is { } pv ? $"pv{pv}" : "todos";
            var nombre = NombreDeArchivo.Construir("ventas_listado", alcance, desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de GET /api/ventas: mismo filtro, sin paginado bajo el tope, gate " +
            "OperacionDePos heredado por co-locación.");

        // Back office: solo Admin, con el turno de la venta abierto o cerrado.
        var administracion = app.MapGroup("/api/ventas/administracion")
            .WithTags("Ventas")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        administracion.MapPut("/{id:int}/medios", async (
            ServicioDeCambioDeMedios servicio, ServicioDeFacturacionDeVentas facturacion, int id,
            SolicitudDeCambioDeMedios solicitud, CancellationToken ct) =>
        {
            var venta = await servicio.CambiarDeAdministracionAsync(id, solicitud, ct);
            return Results.Ok(venta with { Fiscal = await facturacion.LeerEstadoDeVentaAsync(id, ct) });
        })
        .WithSummary(
            "Reemplaza los medios de pago de cualquier venta sin cambiar lo cobrado. Con el turno cerrado recalcula el arqueo " +
            "(con la marca del recálculo) y compensa las cuentas de tesorería de los medios electrónicos.");

        return app;
    }
}
