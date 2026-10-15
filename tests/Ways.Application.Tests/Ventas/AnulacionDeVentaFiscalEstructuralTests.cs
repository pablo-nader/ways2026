using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Ventas;

/// <summary>
/// Facturación por medio de pago, T8c: pinea por texto lo que la anulación de una venta facturada no puede hacer. El paso A del
/// proceso fiscal toma la serie de <c>numeraciones_fiscales</c> ANTES que la fila de la venta; si la anulación (que ya tiene la
/// venta bloqueada) pidiera esa serie, se cruzaría con él y se produciría un deadlock. El comportamiento lo prueba el test de
/// integración <c>LaAnulacionTerminaConTodasLasSeriesFiscalesBloqueadasPorqueNuncaLasToca</c>; acá se fija el texto de lo que la
/// anulación ejecuta dentro de su transacción (<c>ServicioDeVentas.EjecutarAnulacionAsync</c>, la decisión de la nota de crédito y
/// la liquidación de la factura rechazada de <c>ServicioDeFacturacionDeVentas</c>), que además cubre el camino que ese test no
/// recorre (cualquier número fiscal tomado por la vía de <c>AsignadorDeNumeroFiscal</c>).
///
/// Cada test verifica el texto del archivo, no el comportamiento, y su nombre dice solo eso.
/// </summary>
public class AnulacionDeVentaFiscalEstructuralTests
{
    private static string Fuente(string ruta) =>
        File.ReadAllText(Path.Combine(RaizDelRepositorio.Resolver(), "src", Path.Combine(ruta.Split('/')))).Replace("\r\n", "\n");

    private static string Entre(string fuente, string desde, string hasta)
    {
        var inicio = fuente.IndexOf(desde, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{desde}'.");
        var fin = fuente.IndexOf(hasta, inicio + desde.Length, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró '{hasta}' después de '{desde}'.");
        return fuente[inicio..fin];
    }

    [Fact]
    public void ElTextoDeLaAnulacionNoNombraLasSeriesFiscalesNiElAsignadorDeNumeros()
    {
        var fuente = Fuente("Ways.Application/Ventas/ServicioDeVentas.cs");
        var anulacion = Entre(fuente, "private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(", "private static async Task ExigirTurnoNoCerradoAsync(");
        var nota = Entre(fuente, "private static async Task DecidirNotaDeCreditoAsync(", "/// <summary>task 3.13");

        // La liquidación de la factura rechazada corre dentro de la transacción de la anulación: su entrada, su cuerpo sin
        // transacción propia y la reversión de los aportes que ese cuerpo llama.
        var liquidacion = Entre(
            Fuente("Ways.Application/Fiscal/ServicioDeFacturacionDeVentas.cs"),
            "public async Task LiquidarFacturaRechazadaDeVentaAnuladaAsync(", "private async Task RevertirElCupoDeLaFacturaAsync(");

        foreach (var texto in new[] { anulacion, nota, liquidacion })
        {
            Assert.DoesNotContain("numeraciones_fiscales", texto, StringComparison.Ordinal);
            Assert.DoesNotContain("AsignadorDeNumeroFiscal", texto, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ElTextoDeLaNotaBloqueaLaDecisionDeLaFacturaAntesDeInsertarLaSuya()
    {
        var nota = Entre(
            Fuente("Ways.Application/Ventas/ServicioDeVentas.cs"),
            "private static async Task DecidirNotaDeCreditoAsync(", "/// <summary>task 3.13");

        var bloqueo = nota.IndexOf("id_facturacion_origen IS NULL FOR UPDATE", StringComparison.Ordinal);
        var insercion = nota.IndexOf("INSERT INTO facturaciones_de_venta", StringComparison.Ordinal);

        Assert.True(bloqueo >= 0, "Falta el FOR UPDATE de la decisión de la factura.");
        Assert.True(insercion > bloqueo, "La decisión de la nota se inserta antes de bloquear la de la factura.");
    }
}
