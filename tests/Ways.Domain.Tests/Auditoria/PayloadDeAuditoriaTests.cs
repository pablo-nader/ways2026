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
}
