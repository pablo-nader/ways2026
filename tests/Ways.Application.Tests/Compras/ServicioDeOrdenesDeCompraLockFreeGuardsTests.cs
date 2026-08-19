namespace Ways.Application.Tests.Compras;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 4 (design decisión 9; ANULAR OC statements 2/3; mutation
/// target #33). Regresión ESTRUCTURAL PERMANENTE del invariante "estos dos guards son lock-free"
/// de <see cref="ServicioDeOrdenesDeCompra"/>: <c>TieneRecepcionConfirmadaAsync</c> (statement 2) y
/// <c>TieneComprobanteLigadoEnBorradorAsync</c> (statement 3) deben seguir siendo un
/// <c>SELECT EXISTS</c> simple, SIN <c>FOR SHARE</c>/<c>FOR UPDATE</c> — agregar cualquiera de los
/// dos cierra el ciclo de deadlock contra <c>EjecutarConfirmarAsync</c> (que toma
/// <c>comprobantes_compra</c> primero y <c>ordenes_compra</c> después, el orden inverso exacto de
/// esta transacción).
///
/// **Por qué texto fuente y no comportamiento (mutation-proof-tests regla 3)**: la AUSENCIA de un
/// deadlock es, por construcción, un no-evento — no hay ningún resultado observable que distinga
/// "nunca hubo contención" de "hubo contención pero se resolvió por casualidad de timing". Los dos
/// tests de interceptor ya existentes (<c>AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada</c>/
/// <c>AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada</c>,
/// <c>OrdenesCompraCierreYAnulacionTests</c>) pausan el interceptor INMEDIATAMENTE después de
/// <c>BeginTransactionAsync</c> — ANTES de que corra el primer statement de cualquiera de las dos
/// transacciones — así que jamás recrean la ventana de contención de fila que un deadlock necesita;
/// regresionan el RESULTADO de la carrera (quién gana el guard de estado), no la ausencia de
/// locking en estos dos statements. La captura empírica real del deadlock (dos conexiones ADO
/// crudas forzando el ciclo exacto de locks) fue un experimento ONE-SHOT, removido tras confirmar
/// el hallazgo — ver tasks.md decisión 22/23 — y no reproducible como test estable (depende del
/// detector de deadlock de Postgres disparando dentro de una ventana de tiempo). Esta clase cierra
/// esa brecha con la misma técnica que <c>EscriturasDeOrdenDeCompraLockOrderTests</c>/
/// <c>ServicioDeComprasLockOrderTests</c>: extracción del método real + búsqueda de substring
/// inequívoca, sin duplicar el SQL en un string aparte.
/// </summary>
public class ServicioDeOrdenesDeCompraLockFreeGuardsTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Compras", "ServicioDeOrdenesDeCompra.cs");

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

    [Fact]
    public void TieneRecepcionConfirmadaAsyncSigueSiendoUnExistsLockFreeSinForShareNiForUpdate()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente,
            "private static async Task<bool> TieneRecepcionConfirmadaAsync(",
            // Se corta ANTES del doc-comment XML del método siguiente, no en su firma: ese
            // doc-comment describe en prosa por qué NO se agrega `FOR SHARE` al statement 3 (mismo
            // riesgo de confusión de substring que EscriturasDeOrdenDeCompraLockOrderTests ya
            // documenta para el nombre pelado de una clase en prosa) y un corte en la firma lo
            // incluiría dentro del método extraído, haciendo que este test falle por una mención en
            // prosa en vez de por SQL real.
            "/// <summary>design: Transactions — ANULAR OC, statement 3");

        Assert.Contains("SELECT EXISTS (", metodo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR SHARE", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR UPDATE", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TieneComprobanteLigadoEnBorradorAsyncSigueSiendoUnExistsLockFreeSinForShareNiForUpdate()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente,
            "private static async Task<bool> TieneComprobanteLigadoEnBorradorAsync(",
            "private static async Task<string?> MarcarOrdenAnuladaAsync(");

        Assert.Contains("SELECT EXISTS (", metodo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR SHARE", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR UPDATE", metodo, StringComparison.OrdinalIgnoreCase);
    }
}
