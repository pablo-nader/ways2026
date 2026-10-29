using Ways.Domain.Auditoria;
using Ways.Domain.Compras;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (task 1.14, design decisión 5): un caso por cada una
/// de las 12 fábricas de <see cref="PayloadDeAuditoria"/> — ninguna produce un payload que viole
/// el subconjunto, la denylist o snake_case, probado construyendo el
/// <see cref="RegistroDeAuditoria"/> real con la acción emparejada de <see cref="AccionAuditada"/>.
/// </summary>
public class PayloadDeAuditoriaTests
{
    private static void AsumirConstruible(
        AccionAuditada accion,
        (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo) payload)
    {
        var excepcion = Record.Exception(() =>
            new RegistroDeAuditoria(1, idPuntoVenta: null, accion, idEntidad: 1, payload.Anterior, payload.Nuevo));

        Assert.Null(excepcion);
    }

    [Fact]
    public void CambioDePrecioEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.PrecioCambio,
            PayloadDeAuditoria.CambioDePrecio(1, 90m, DateTimeOffset.UtcNow.AddDays(-1), 100m, DateTimeOffset.UtcNow));

    [Fact]
    public void CambioDePrecioSinAnteriorEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.PrecioCambio,
            PayloadDeAuditoria.CambioDePrecio(1, null, null, 100m, DateTimeOffset.UtcNow));

    [Fact]
    public void AltaDeUsuarioEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.UsuarioAlta,
            PayloadDeAuditoria.AltaDeUsuario("nuevo.usuario", "nuevo@ways.test", 2, EstadoUsuario.Activo));

    [Fact]
    public void ActualizacionDeUsuarioEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.UsuarioActualizacion,
            PayloadDeAuditoria.ActualizacionDeUsuario(
                "viejo.usuario", "viejo@ways.test", 2, EstadoUsuario.Activo,
                "nuevo.usuario", "nuevo@ways.test", 3, EstadoUsuario.Inactivo));

    [Fact]
    public void BajaDeUsuarioEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.UsuarioBaja,
            PayloadDeAuditoria.BajaDeUsuario(EstadoUsuario.Activo, DateTimeOffset.UtcNow));

    [Fact]
    public void DesbloqueoDeUsuarioEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.UsuarioDesbloqueo,
            PayloadDeAuditoria.DesbloqueoDeUsuario(EstadoUsuario.Bloqueado, EstadoUsuario.Activo));

    [Fact]
    public void CambioDePasswordEsConstruible() =>
        AsumirConstruible(AccionAuditada.UsuarioPassword, PayloadDeAuditoria.CambioDePassword(true));

    [Fact]
    public void AnulacionDeVentaEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.VentaAnulacion,
            PayloadDeAuditoria.AnulacionDeVenta(EstadoComprobante.Emitido, EstadoComprobante.Anulado));

    [Fact]
    public void AnulacionDeCompraEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.CompraAnulacion,
            PayloadDeAuditoria.AnulacionDeCompra(EstadoCompra.Confirmada, EstadoCompra.Anulada));

    [Fact]
    public void DescarteDeCompraEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.CompraDescarte,
            PayloadDeAuditoria.DescarteDeCompra(idProveedor: 7, numeroExterno: "0001-00000001", total: 121m, cantidadDeItems: 2));

    [Fact]
    public void EdicionDeCompraEsConstruibleYLlevaLasDiferenciasSoloEnElValorNuevo()
    {
        var anterior = new ResumenDeCompraEditada(
            7, 2, "0001-00000001", null, 100m, [new LineaAuditadaDeCompra(5, 10m, 100m)]);
        var nuevo = anterior with { Total = 60m, Lineas = [new LineaAuditadaDeCompra(5, 6m, 60m)] };
        var payload = PayloadDeAuditoria.EdicionDeCompra(
            anterior, nuevo,
            [new DiferenciaDeStockDeCompra(5, 2, null, -4m)],
            [new DiferenciaDeCuentaDeCompra(7, -40m)]);

        AsumirConstruible(AccionAuditada.CompraEdicion, payload);
        Assert.Equal(100m, payload.Anterior!["total"]);
        Assert.Equal(60m, payload.Nuevo["total"]);
        Assert.False(payload.Anterior.ContainsKey("diferencias_de_stock"));
        Assert.True(payload.Nuevo.ContainsKey("diferencias_de_stock"));
        Assert.True(payload.Nuevo.ContainsKey("diferencias_de_cuenta"));
    }

    [Fact]
    public void EdicionDeCompraConPagosLlevaLasAplicacionesTratadasElRedondeoYLosVinculosCortados()
    {
        var anterior = new ResumenDeCompraEditada(7, 2, "0001-00000001", null, 100m, [new LineaAuditadaDeCompra(5, 10m, 100m)]);
        var payload = PayloadDeAuditoria.EdicionDeCompra(
            anterior, anterior with { IdProveedor = 9 }, [], [],
            new TratamientoDePagosDeLaEdicion(
                [new AplicacionTratadaPorLaEdicion(11, 21, 40m, 0m)], 3m, [new PagoDesvinculadoPorLaEdicion(21, 2)]),
            new CostosDeLaEdicion([new CostoActualizadoPorLaEdicion(5, 12.5m)], [8]));

        AsumirConstruible(AccionAuditada.CompraEdicion, payload);
        var tratada = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)payload.Nuevo["aplicaciones_tratadas"]!));
        Assert.Equal(11, tratada["id_aplicacion"]);
        Assert.Equal(21, tratada["id_pago"]);
        Assert.Equal(0m, tratada["importe_nuevo"]);
        Assert.Equal(3m, payload.Nuevo["redondeo_revertido"]);
        var desvinculado = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single((List<object?>)payload.Nuevo["pagos_desvinculados"]!));
        Assert.Equal(21, desvinculado["id_pago"]);
        Assert.Equal(2, desvinculado["id_punto_venta_heredado"]);
        var costo = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)payload.Nuevo["costos_actualizados"]!));
        Assert.Equal(5, costo["id_articulo"]);
        Assert.Equal(12.5m, costo["costo"]);
        Assert.Equal([8], ((List<object?>)payload.Nuevo["costos_omitidos_por_compra_mas_nueva"]!).Cast<int>());
        Assert.False(payload.Anterior!.ContainsKey("costos_actualizados"));
        Assert.False(payload.Anterior!.ContainsKey("aplicaciones_tratadas"));
    }

    [Fact]
    public void AjusteDeStockEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.StockAjuste,
            PayloadDeAuditoria.AjusteDeStock(10m, 15m, idMovimientoStock: 99, observaciones: "ajuste manual"));

    [Fact]
    public void DecomisoDeStockEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.StockDecomiso,
            PayloadDeAuditoria.DecomisoDeStock(10m, 8m, idMovimientoStock: 99, observaciones: "vencido", idLote: 5));

    [Fact]
    public void ConteoDeStockEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.StockConteo,
            PayloadDeAuditoria.Conteo(
                cantidadAlInicio: 10m, cantidadFinal: 14m, movimientosGenerados: [101, 102],
                lotesAfectados: 2, deltaTotal: 4m));

    [Fact]
    public void ReliquidacionDeCcEsConstruible() =>
        AsumirConstruible(
            AccionAuditada.CcReliquidacion,
            PayloadDeAuditoria.ReliquidacionDeCc(
                saldoAnterior: 1000m, saldoNuevo: 1200m, idMovimiento: 77, consumosActualizados: 3, diferencia: 200m));

    [Fact]
    public void AltaDePuntoVentaEsConstruibleYNoTieneEstadoPrevio()
    {
        var payload = PayloadDeAuditoria.AltaDePuntoVenta(7, "Sucursal Centro", Ways.Domain.Organizacion.ModoPuntoVenta.Web);

        AsumirConstruible(AccionAuditada.PuntoVentaAlta, payload);

        Assert.Null(payload.Anterior);
        Assert.Equal(7, payload.Nuevo["id_empresa"]);
        Assert.Equal("Sucursal Centro", payload.Nuevo["nombre"]);
        Assert.Equal(Ways.Domain.Organizacion.ModoPuntoVenta.Web, payload.Nuevo["modo"]);
    }
}
