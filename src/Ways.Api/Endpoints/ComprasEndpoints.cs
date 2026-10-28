using Microsoft.Extensions.Options;
using Ways.Api.Exportacion;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Exportacion;
using Ways.Application.Pagos;
using Ways.Domain.Compras;

namespace Ways.Api.Endpoints;

/// <summary>
/// Comprobantes de compra (design: API Surface) — lecturas bajo <c>OperacionDePos</c>, toda
/// escritura apila <c>GestionDeCatalogo</c> (Admin-only): el ciclo de vida entero de una compra
/// mueve dinero y stock, mismo criterio que <c>POST /api/stock/ajustes</c>.
/// </summary>
public static class ComprasEndpoints
{
    public static IEndpointRouteBuilder MapearCompras(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/compras")
            .WithTags("Compras")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeCompras servicio,
            int? idProveedor,
            EstadoCompra? estado,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? idEmpresa,
            OrigenCompra? origen,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idProveedor, estado, desde, hasta, idEmpresa, pagina ?? 1, tamanio ?? 25, origen, ct))
        .WithSummary(
            "Lista comprobantes de compra con filtros y paginado. idEmpresa (stage-tesoreria-por-" +
            "empresa, PR5) filtra por la empresa del punto de venta de la compra — usado por el " +
            "picker 'Vincular a compra' de Pagos para no ofrecer compras de otra empresa. origen " +
            "filtra por quién cargó la compra (Manual o Asistente); la API no permite fijarlo.");

        // stage-11-exportacion-reportes (Slice 3, design decisión 7): sibling declarado
        // inmediatamente después de su ruta fuente — hereda OperacionDePos por co-locación.
        // Sin idPuntoVenta (el listado JSON tampoco lo tiene): Empresa/zona usan el default de
        // AlcanceDeListadoHttp, no una consulta nueva. judgment-day PR5, hallazgo #3: idEmpresa
        // viaja EXACTAMENTE igual que en GET /api/compras (contrato "el export es igual al
        // JSON") — ServicioDeCompras.ListarParaExportacionAsync ya lo acepta, solo faltaba
        // reenviarlo desde acá.
        grupo.MapGet("/export", async (
            ServicioDeCompras servicio, IExportadorDeTabla exportador, IOptions<OpcionesDeExportacion> opciones,
            IContextoDeUsuario usuario, IRelojDelSistema reloj,
            int? idProveedor, EstadoCompra? estado, DateTimeOffset desde, DateTimeOffset hasta, int? idEmpresa,
            OrigenCompra? origen, string formato, CancellationToken ct) =>
        {
            FormatoDeExportacion.Parsear(formato);

            var filas = await servicio.ListarParaExportacionAsync(
                idProveedor, estado, desde, hasta, opciones.Value.TopeDeFilas, idEmpresa, origen, ct);

            var zona = TimeZoneInfo.FindSystemTimeZoneById(AlcanceDeListadoHttp.ZonaPorDefecto);
            var (desdeFecha, hastaFecha) = FechaDelRango.De(desde, hasta);

            var ctx = ContextoDeExportacionHttp.Construir(
                usuario, reloj, "Todas", puntoVenta: null, desdeFecha, hastaFecha, AlcanceDeListadoHttp.ZonaPorDefecto);
            var tabla = ExportacionDeListados.De(filas, ctx, zona);

            var bytes = exportador.Generar(tabla);
            var nombre = NombreDeArchivo.Construir("compras_listado", "todas", desdeFecha, hastaFecha);

            return ResultadoDeExportacion.Archivo(bytes, exportador.TipoDeContenido, nombre);
        })
        .WithSummary(
            "Export XLSX de GET /api/compras: mismo filtro, sin paginado bajo el tope, gate " +
            "OperacionDePos heredado por co-locación.");

        grupo.MapGet("/duplicados", (
            ServicioDeCompras servicio, int idProveedor, int idTipoComprobante, string? numeroExterno, int? excluirId,
            CancellationToken ct) =>
            servicio.BuscarDuplicadosAsync(idProveedor, idTipoComprobante, numeroExterno, excluirId, ct))
        .WithSummary(
            "Compras no anuladas del mismo proveedor y tipo con ese número. En C-FA/C-FB/C-FC compara el valor " +
            "numérico de punto de venta y número (3-12345 = 0003-00012345); en los demás tipos, el texto sin " +
            "distinguir mayúsculas. excluirId deja afuera la compra que se está editando. Un número fiscal " +
            "ininterpretable responde 400 numero_comprobante_invalido.");

        grupo.MapGet("/{id:int}", (ServicioDeCompras servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Detalle de una compra: header + items + precioSugerido por item.");

        grupo.MapPost("/", async (ServicioDeCompras servicio, SolicitudDeCompra solicitud, CancellationToken ct) =>
        {
            var creada = await servicio.CrearBorradorAsync(solicitud, ct);
            return Results.Created($"/api/compras/{creada.Id}", creada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un borrador de compra (header + items opcionales).");

        grupo.MapPut("/{id:int}", async (ServicioDeCompras servicio, int id, SolicitudDeCompra solicitud, CancellationToken ct) =>
        {
            var actualizada = await servicio.ActualizarAsync(id, solicitud, ct);
            return Results.Ok(actualizada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Reemplaza el header y el set completo de items de una compra. Un borrador se reemplaza tal cual; una " +
            "confirmada se edita por diferencia (el stock y la cuenta del proveedor se corrigen con movimientos nuevos, " +
            "409 compra_edicion_stock_negativo si el stock no alcanza y 409 compra_edicion_con_aplicaciones si toca lo " +
            "que sus pagos suponen). Una anulada responde 409 compra_no_editable.");

        grupo.MapDelete("/{id:int}", async (ServicioDeCompras servicio, int id, CancellationToken ct) =>
        {
            await servicio.DescartarBorradorAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Descarta un borrador: borra el encabezado, las líneas, las alícuotas y las percepciones, y deja el " +
            "rastro compra.descarte. Una compra confirmada o anulada responde 409 compra_no_descartable.");

        grupo.MapPost("/{id:int}/confirmar", async (ServicioDeCompras servicio, int id, CancellationToken ct) =>
        {
            var confirmada = await servicio.ConfirmarAsync(id, ct);
            return Results.Ok(confirmada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Confirma un borrador: entra el stock, se actualiza costo_nominal (en todos los miembros vivos de la "
                + "familia del artículo, si la tiene), se congela precio_sugerido.");

        grupo.MapPost("/{id:int}/anular", async (ServicioDeCompras servicio, int id, CancellationToken ct) =>
        {
            var resultado = await servicio.AnularAsync(id, ct);
            return Results.Ok(resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Anula una compra confirmada: contramovimientos de stock, nunca revierte costo_nominal.");

        grupo.MapPost("/{id:int}/ajuste-de-redondeo", (ServicioDePagos servicio, int id, CancellationToken ct) =>
            servicio.AjustarRedondeoDeCompraAsync(id, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Cierra en cero una compra confirmada con saldo pendiente de hasta la tolerancia de redondeo: perdona el saldo " +
            "con un movimiento de redondeo de la cuenta corriente del proveedor.");

        grupo.MapPost("/{id:int}/pagos", async (
            ServicioDePagos servicio, int id, SolicitudDePagoDeCompra solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.PagarCompraAsync(id, solicitud, ct);
            return Results.Created($"/api/gastos/administracion/{resultado.Pago.Id}", resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Paga una compra confirmada: crea el gasto de tesorería y el pago de cuenta corriente imputado a la " +
            "compra en una transacción. El importe no puede superar el saldo pendiente de esa compra.");

        grupo.MapPost("/{id:int}/precios", async (
            ServicioDeCompras servicio, int id, SolicitudDeAplicarPrecios solicitud, CancellationToken ct) =>
        {
            var resultados = await servicio.AplicarPrecioSugeridoAsync(id, solicitud, ct);
            return Results.Ok(resultados);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary(
            "Aplica precio_sugerido por item vía ServicioDePrecios, per-line results. De las líneas de una misma "
                + "familia solo se intenta la de mayor orden, a toda la familia si su artículo es miembro.");

        return app;
    }
}
