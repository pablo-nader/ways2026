using Ways.Application.CuentaCorriente;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.Tests.CuentaCorriente;

/// <summary>
/// judgment-day fix 4 (MINOR, judge B): <c>EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync</c>
/// no validaba la forma nullable por tipo que hoy solo garantizan los llamadores (design: Table
/// Shapes — write path C). El guard corre ANTES de tocar la conexión, así que <c>null!</c> alcanza
/// para pinearlo sin una base de datos real — si algún día deja de ser cierto, el <see
/// cref="NullReferenceException"/> resultante hace evidente que el guard se movió.
/// </summary>
public class EscriturasDeCuentaCorrienteTests
{
    [Fact]
    public async Task UnConsumoSinIdPagoComprobanteViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                null!, null, idTenant: 1, idCliente: 1, fecha: DateTimeOffset.UtcNow, idPuntoVenta: 1, idEmpleado: 1,
                TipoMovimientoCc.Consumo, idComprobanteVenta: 10, idPagoComprobante: null, importe: 100m,
                saldoResultante: 100m, CancellationToken.None));

        Assert.Contains("Consumo", excepcion.Message);
    }

    [Fact]
    public async Task UnPagoConIdPagoComprobanteViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                null!, null, idTenant: 1, idCliente: 1, fecha: DateTimeOffset.UtcNow, idPuntoVenta: 1, idEmpleado: 1,
                TipoMovimientoCc.Pago, idComprobanteVenta: 10, idPagoComprobante: 5, importe: -100m,
                saldoResultante: 0m, CancellationToken.None));

        Assert.Contains("Pago", excepcion.Message);
    }
}
