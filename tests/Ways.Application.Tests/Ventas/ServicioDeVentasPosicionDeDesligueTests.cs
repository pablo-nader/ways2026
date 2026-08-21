using System.Text.RegularExpressions;

namespace Ways.Application.Tests.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (design: Transactions — "ANULAR VENTA", decisión 4/7;
/// mutation targets 55/56/58; task 6.19). Mismo criterio que
/// <see cref="ServicioDeVentasPosicionDeConversionTests"/> — extracción de texto fuente + búsqueda
/// de substring inequívoca, nunca comportamiento.
///
/// **Por qué texto fuente (mutation-proof-tests regla 3)**: el guard estructural
/// <c>if (codigoTipoAnulado == "TXR")</c> alrededor de <c>EscriturasDeRemito.DesligarAsync</c> —
/// igual que el guard nulo de <c>IdPresupuestoOrigen</c> de Slice 3 — llama a un método que corre
/// SQL crudo vía <c>ExecuteNonQueryAsync</c> sobre un <c>DbCommand</c> creado directo con
/// <c>conexion.CreateCommand()</c>: ese camino NUNCA pasa por el pipeline de
/// <c>DbCommandInterceptor.ReaderExecuting[Async]</c> de EF Core, así que un
/// <c>ContadorDeComandos</c> (el mismo usado por <c>ServicioDeVentasConversionTests</c>) vería el
/// MISMO conteo de consultas EF con o sin este bloque, guardado o no — es ciego por construcción,
/// no una falla de instrumentación. La única red real de "cero statements extra para una
/// anulación ordinaria" (mutation targets 55/56) es estructural: la llamada real a
/// <c>EscriturasDeRemito.DesligarAsync(</c> solo puede aparecer DENTRO del guard
/// <c>codigoTipoAnulado == "TXR"</c> en todo el método, nunca incondicional — exactamente el mismo
/// argumento que <c>LaLlamadaAMarcarConvertidoAsyncNuncaOcurreFueraDelGuardNuloDeIdPresupuestoOrigen</c>.
/// </summary>
public class ServicioDeVentasPosicionDeDesligueTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Ventas", "ServicioDeVentas.cs");

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

    private static int EncontrarCierreBalanceado(string texto, int indiceApertura)
    {
        var profundidad = 0;
        for (var i = indiceApertura; i < texto.Length; i++)
        {
            if (texto[i] == '{')
            {
                profundidad++;
            }
            else if (texto[i] == '}')
            {
                profundidad--;
                if (profundidad == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Design: "posición 1.6 — inmediatamente DESPUÉS de MarcarAnuladoAsync (paso 1) y
    /// ANTES de la auditoría (paso 1.5, preexistente) — nunca después del loop de stock ni de
    /// cuenta corriente" (mutation target 58: la rendezvous anular-TXR × facturar depende de que
    /// el desligue corra ANTES, no después, de esas escrituras).</summary>
    [Fact]
    public void ElDesligueDeRemitosVaInmediatamenteDespuesDeMarcarAnuladoYAntesDeLaAuditoria()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente,
            "private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(",
            "private static async Task ExigirTurnoNoCerradoAsync(");

        var indiceMarcarAnulado = metodo.IndexOf("var resultadoAnulacion = await MarcarAnuladoAsync(", StringComparison.Ordinal);
        var indiceGuardTxr = metodo.IndexOf("if (codigoTipoAnulado == \"TXR\")", StringComparison.Ordinal);
        var indiceDesligar = metodo.IndexOf("EscriturasDeRemito.DesligarAsync(", StringComparison.Ordinal);
        var indiceAuditoria = metodo.IndexOf("var servicioDeAuditoriaAnulacion = new", StringComparison.Ordinal);
        var indiceStock = metodo.IndexOf("var movimientosOriginales = await db.MovimientosStock", StringComparison.Ordinal);
        var indiceCc = metodo.IndexOf("var movimientosCcOriginales = await db.MovimientosCuentaCorriente", StringComparison.Ordinal);

        Assert.True(indiceMarcarAnulado >= 0, "No se encontró la llamada a MarcarAnuladoAsync.");
        Assert.True(indiceGuardTxr >= 0, "No se encontró el guard codigoTipoAnulado == \"TXR\".");
        Assert.True(indiceDesligar >= 0, "No se encontró la llamada a EscriturasDeRemito.DesligarAsync.");
        Assert.True(indiceAuditoria >= 0, "No se encontró el bloque de auditoría (paso 1.5).");
        Assert.True(indiceStock >= 0, "No se encontró la reversa de stock (paso 2).");
        Assert.True(indiceCc >= 0, "No se encontró el contramovimiento de CC (paso 3).");

        Assert.True(indiceGuardTxr > indiceMarcarAnulado, "El guard TXR debe ir DESPUÉS de MarcarAnuladoAsync.");
        Assert.True(indiceDesligar > indiceGuardTxr, "DesligarAsync debe estar DENTRO del guard TXR.");
        Assert.True(indiceAuditoria > indiceDesligar, "La auditoría debe ir DESPUÉS del desligue (posición 1.6, no 1.5).");
        Assert.True(indiceStock > indiceAuditoria, "La reversa de stock debe seguir siendo posterior.");
        Assert.True(indiceCc > indiceStock, "El contramovimiento de CC debe seguir siendo el último.");

        // Nunca DESPUÉS de stock/CC (design: "posición 2, nunca después de stock/clientes") —
        // mutation target 58, el mutante concreto que la rendezvous anular-TXR × facturar mata.
        Assert.True(indiceDesligar < indiceStock, "DesligarAsync no puede correr después de la reversa de stock.");
        Assert.True(indiceDesligar < indiceCc, "DesligarAsync no puede correr después del contramovimiento de CC.");
    }

    /// <summary>Mutation targets 55/56 (RED estructural del "cero statements extra"): ver el
    /// doc-comment de la clase sobre por qué <c>ContadorDeComandos</c> es ciego a esta llamada
    /// (SQL crudo vía <c>ExecuteNonQueryAsync</c>, nunca pasa por el pipeline de EF). La red REAL
    /// es que <c>EscriturasDeRemito.DesligarAsync(</c> solo puede aparecer DENTRO del guard
    /// <c>codigoTipoAnulado == "TXR"</c> — nunca incondicional, nunca fuera de él.</summary>
    [Fact]
    public void LaLlamadaADesligarAsyncNuncaOcurreFueraDelGuardCodigoTipoTxr()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente,
            "private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(",
            "private static async Task ExigirTurnoNoCerradoAsync(");

        const string guardIf = "if (codigoTipoAnulado == \"TXR\")";
        var indiceGuard = metodo.IndexOf(guardIf, StringComparison.Ordinal);
        Assert.True(indiceGuard >= 0, "No se encontró el guard codigoTipoAnulado == \"TXR\".");

        var indiceApertura = metodo.IndexOf('{', indiceGuard + guardIf.Length);
        var indiceCierre = EncontrarCierreBalanceado(metodo, indiceApertura);
        Assert.True(indiceApertura > 0 && indiceCierre > indiceApertura, "No se pudo delimitar el bloque del guard TXR.");

        var antesDelGuard = metodo[..indiceGuard];
        var dentroDelGuard = metodo[indiceGuard..(indiceCierre + 1)];
        var despuesDelGuard = metodo[(indiceCierre + 1)..];

        var patronLlamadaReal = new Regex(
            @"EscriturasDeRemito\.DesligarAsync\(\s*conexion,", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.False(patronLlamadaReal.IsMatch(antesDelGuard), "La llamada real no debe aparecer ANTES del guard TXR.");
        Assert.False(patronLlamadaReal.IsMatch(despuesDelGuard), "La llamada real no debe aparecer DESPUÉS del guard TXR.");
        Assert.True(patronLlamadaReal.IsMatch(dentroDelGuard), "La llamada real debe estar DENTRO del guard TXR.");
    }

    /// <summary>Mutation target 55: el <c>codigoTipo</c> tiene que salir del scalar subquery
    /// ensanchado de <c>MarcarAnuladoAsync</c> — nunca un <c>SELECT</c> separado (eso agregaría un
    /// statement extra a TODA anulación, no solo a la de un TXR). Prueba de texto fuente: el
    /// método <c>MarcarAnuladoAsync</c> contiene el subquery escalar en su propio
    /// <c>CommandText</c>, y <c>EjecutarAnulacionAsync</c> nunca declara un segundo <c>comando</c>
    /// separado antes del guard TXR.</summary>
    [Fact]
    public void ElCodigoTipoSaleDelSubqueryEscalarDeMarcarAnuladoNuncaDeUnSelectSeparado()
    {
        var fuente = LeerFuente();

        var metodoMarcarAnulado = ExtraerMetodo(
            fuente,
            "private static async Task<(int IdPuntoVenta, string CodigoTipo)?> MarcarAnuladoAsync(",
            "// ---- La transacción");

        Assert.Contains(
            "(SELECT t.codigo FROM tipos_comprobante t WHERE t.id_tipo_comprobante = comprobantes_venta.id_tipo_comprobante)",
            metodoMarcarAnulado);

        var metodoAnulacion = ExtraerMetodo(
            fuente,
            "private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(",
            "private static async Task ExigirTurnoNoCerradoAsync(");

        var indiceGuardTxr = metodoAnulacion.IndexOf("if (codigoTipoAnulado == \"TXR\")", StringComparison.Ordinal);
        Assert.True(indiceGuardTxr >= 0);

        var antesDelGuard = metodoAnulacion[..indiceGuardTxr];

        // Ningún SELECT/comando adicional a tipos_comprobante antes del guard TXR — el ÚNICO lugar
        // que resuelve el código del tipo es el subquery escalar dentro del RETURNING de
        // MarcarAnuladoAsync.
        Assert.DoesNotContain("tipos_comprobante", antesDelGuard, StringComparison.Ordinal);
    }
}
