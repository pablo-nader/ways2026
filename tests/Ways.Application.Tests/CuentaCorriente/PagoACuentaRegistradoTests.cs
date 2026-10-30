using Ways.Application.CuentaCorriente;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Application.Tests.CuentaCorriente;

/// <summary>
/// E7-S3: <see cref="PagoACuentaRegistrado"/> es la respuesta de <c>POST …/cuenta-corriente/pagos</c> y tiene que seguir
/// siendo, campo por campo, la que leen los registros instalados (<see cref="ComprobanteEmitido"/>) más las aplicaciones.
/// Si alguien agrega un campo a <see cref="ComprobanteEmitido"/>, esta prueba falla hasta que el recibo lo lleve también.
/// </summary>
public class PagoACuentaRegistradoTests
{
    private static ComprobanteEmitido Recibo() => new(
        Id: 7, Numero: 12, NumeroVisible: "0001-00000012", Estado: EstadoComprobante.Emitido,
        Fecha: new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), IdPuntoVenta: 1, IdCliente: 3,
        IdComprobanteAsociado: null, Subtotal: 100m, DescuentoTotal: 0m, DescuentoManualTotal: 0m, RecargoManualTotal: 0m,
        Total: 100m, DireccionEntrega: null, Observaciones: "obs", Items: [],
        Pagos: [new PagoEmitido(1, 100m, null, 0m)]);

    [Fact]
    public void LaRespuestaDelReciboTieneTodosLosCamposDelComprobanteEmitidoConElMismoTipo()
    {
        var delComprobante = typeof(ComprobanteEmitido).GetProperties()
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => p.PropertyType);
        var delRecibo = typeof(PagoACuentaRegistrado).GetProperties()
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => p.PropertyType);

        foreach (var (nombre, tipo) in delComprobante)
        {
            Assert.True(delRecibo.TryGetValue(nombre, out var tipoDelRecibo), $"Falta el campo {nombre} en PagoACuentaRegistrado.");
            Assert.Equal(tipo, tipoDelRecibo);
        }

        Assert.Equal(["Aplicaciones"], delRecibo.Keys.Except(delComprobante.Keys));
    }

    [Fact]
    public void DeCopiaCadaCampoDelComprobanteYAgregaLasAplicaciones()
    {
        var comprobante = Recibo();
        var aplicaciones = new[] { new AplicacionDeCobroListada(5, 9, 40m, comprobante.Fecha) };

        var registrado = PagoACuentaRegistrado.De(comprobante, aplicaciones);

        foreach (var propiedad in typeof(ComprobanteEmitido).GetProperties().Where(p => p.Name != "EqualityContract"))
        {
            var esperado = propiedad.GetValue(comprobante);
            var obtenido = typeof(PagoACuentaRegistrado).GetProperty(propiedad.Name)!.GetValue(registrado);
            Assert.Equal(esperado, obtenido);
        }

        Assert.Equal(aplicaciones, registrado.Aplicaciones);
    }
}
