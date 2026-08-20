namespace Ways.Application.Tests.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (design decisión 12, mutation target 49; task 6.4).
/// Mismo criterio que <see cref="ServicioDeVentasPosicionDeConversionTests"/> — extracción de texto
/// fuente + búsqueda de substring inequívoca, nunca comportamiento.
///
/// **Por qué texto fuente (mutation-proof-tests regla 3)**: se corrió la mutación REAL — mover
/// <c>EscriturasDeRemito.BloquearAscendenteAsync</c> de la posición 1 (antes del INSERT del
/// comprobante) a justo antes de <c>LigarAsync</c> (después del loop de CC, posición 4.5) — y la
/// suite completa de <c>ServicioDeFacturacionDeRemitosTests</c>, incluidas las dos rendezvous de la
/// tarea 6.15 (facturar × anular-remito, ambos órdenes) y la de la tarea 6.23 (anular-TXR ×
/// facturar), siguió en verde. Mismo hallazgo EXACTO que judgment-day slice-3, juez B, sobre la
/// posición 1.5 de la conversión de presupuesto: la POSICIÓN no es lo que garantiza la corrección
/// observable — eso lo da la ATOMICIDAD de la transacción (cualquier <c>throw</c> revierte TODO lo
/// ya escrito, sin importar en qué línea esté) más el guard final de <c>LigarAsync</c> (mutation
/// target 50), que sigue siendo la autoridad race-safe sin importar cuándo se tomó el lock
/// ascendente dentro de la MISMA transacción. Ningún test de comportamiento puede discriminar "en
/// posición 1" de "justo antes de LigarAsync" sin mentir sobre qué prueba — esta clase pinea la
/// posición por el motivo REAL (fail-fast defensivo: ahorra materializar comprobante/pagos/CC para
/// una consolidación que de todos modos va a fallar, y mantiene el orden total de locks documentado
/// en el Lock order table del design), así un move accidental se caza sin afirmar una correctitud
/// que la posición no otorga.
/// </summary>
public class ServicioDeFacturacionDeRemitosPosicionDeLockTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Ventas", "ServicioDeFacturacionDeRemitos.cs");

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string ExtraerMetodo(string fuente, string firma, string siguiente)
    {
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró el método '{firma}'.");

        var fin = fuente.IndexOf(siguiente, inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró '{siguiente}' después de '{firma}'.");

        return fuente[inicio..fin];
    }

    /// <summary>Design: "EscriturasDeRemito.BloquearAscendenteAsync ANTES del INSERT del
    /// comprobante y ANTES de clientes" (decisión 12, mutation target 49) — verificado en orden:
    /// turno (paso 0) → lock ascendente (paso 1) → INSERT del comprobante (paso 2) → pagos (paso 3)
    /// → cuenta corriente (paso 4) → LigarAsync (paso 5).</summary>
    [Fact]
    public void ElLockAscendenteVaAntesDelInsertDelComprobanteYAntesDeLaCuentaCorriente()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente,
            "private async Task<ComprobanteEmitido> EjecutarFacturacionAsync(",
            "// ---- Resolución de datos, fuera de la transacción");

        var indiceTurno = metodo.IndexOf("servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(", StringComparison.Ordinal);
        var indiceLockAscendente = metodo.IndexOf("EscriturasDeRemito.BloquearAscendenteAsync(", StringComparison.Ordinal);
        var indiceInsertComprobante = metodo.IndexOf("db.ComprobantesVenta.Add(comprobante)", StringComparison.Ordinal);
        var indiceCc = metodo.IndexOf("EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(", StringComparison.Ordinal);
        var indiceLigar = metodo.IndexOf("EscriturasDeRemito.LigarAsync(", StringComparison.Ordinal);

        Assert.True(indiceTurno >= 0, "No se encontró el guard de turno (paso 0).");
        Assert.True(indiceLockAscendente >= 0, "No se encontró la llamada a BloquearAscendenteAsync.");
        Assert.True(indiceInsertComprobante >= 0, "No se encontró el INSERT del comprobante (paso 2).");
        Assert.True(indiceCc >= 0, "No se encontró el loop de cuenta corriente (paso 4).");
        Assert.True(indiceLigar >= 0, "No se encontró la llamada a LigarAsync (paso 5).");

        Assert.True(indiceLockAscendente > indiceTurno, "El lock ascendente debe ir DESPUÉS del guard de turno.");
        Assert.True(indiceInsertComprobante > indiceLockAscendente, "El INSERT del comprobante debe ir DESPUÉS del lock ascendente.");
        Assert.True(indiceCc > indiceLockAscendente, "La cuenta corriente debe seguir siendo posterior al lock ascendente.");
        Assert.True(indiceLigar > indiceCc, "LigarAsync debe seguir siendo el último statement, después de la cuenta corriente.");
    }
}
