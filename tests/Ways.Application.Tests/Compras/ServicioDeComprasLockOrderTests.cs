namespace Ways.Application.Tests.Compras;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 2 (task 2.9; design.md: Transactions — Total order,
/// "`proveedores` is the last row lock any transaction takes for update, and the ledger `INSERT`
/// follows it immediately"). Mutation target #19: si el lock de <c>proveedores</c>
/// (<c>EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync</c>) se moviera al paso
/// 1.5 (antes del loop de stock) dentro de <c>EjecutarConfirmarAsync</c>/
/// <c>EjecutarAnulacionAsync</c>, esta prueba debe fallar.
///
/// Resuelto con una aserción de TEXTO FUENTE, no de comportamiento — mismo criterio que los
/// mutation targets #4/#11 de slice 1 (<c>CuentaCorrienteProveedorBackfillTests</c>): un
/// <c>DbCommandInterceptor</c> de EF Core NUNCA ve los statements de
/// <c>EjecutarConfirmarAsync</c>/<c>EjecutarAnulacionAsync</c> — se crean vía
/// <c>conexion.CreateCommand()</c> sobre <c>db.Database.GetDbConnection()</c> directamente, fuera
/// del pipeline de comandos de EF Core (confirmado empíricamente: la primera versión de esta
/// prueba usaba un interceptor y <c>interceptor.Orden</c> quedó vacío en la corrida real contra
/// <c>WaysApiFixture</c> — mutation-proof-tests rule 2, "no lo razones, corré la prueba"). No hay
/// seam de runtime para rutear la prueba por debajo del confound (rule 3 exhausted first); el
/// orden de las llamadas es, literalmente, el único artefacto observable.
/// </summary>
public class ServicioDeComprasLockOrderTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        // tests/Ways.Application.Tests/Compras/ → ../../src/Ways.Application/Compras/ (mismo
        // criterio que CuentaCorrienteProveedorBackfillTests.RutaDeEsteArchivo, slice 1).
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Compras", "ServicioDeCompras.cs");

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
    public void ElLockDeSaldoDeProveedorEsElUltimoDeEjecutarConfirmarAsyncYElLedgerLoSigue()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<CompraDetalle> EjecutarConfirmarAsync(",
            "public async Task<ResultadoAnulacion> AnularAsync(");

        var indiceStockInsert = metodo.LastIndexOf("InsertarMovimientoStockAsync(", StringComparison.Ordinal);
        var indiceCostoUpdate = metodo.LastIndexOf("ActualizarCostoNominalAsync(", StringComparison.Ordinal);
        var indiceLockProveedor = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);
        var indiceLedgerInsert = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(", StringComparison.Ordinal);
        var indiceCommit = metodo.IndexOf("transaccion.CommitAsync(ct);", StringComparison.Ordinal);

        Assert.True(indiceStockInsert >= 0, "No se encontró la última llamada a InsertarMovimientoStockAsync.");
        Assert.True(indiceCostoUpdate >= 0, "No se encontró la llamada a ActualizarCostoNominalAsync.");
        Assert.True(indiceLockProveedor >= 0, "No se encontró la llamada a ActualizarSaldoProveedorAsync.");
        Assert.True(indiceLedgerInsert >= 0, "No se encontró la llamada a InsertarMovimientoCcProveedorAsync.");
        Assert.True(indiceCommit >= 0, "No se encontró el commit de EjecutarConfirmarAsync.");

        Assert.True(
            indiceLockProveedor > indiceStockInsert,
            "El lock de proveedores debe aparecer DESPUÉS del último InsertarMovimientoStockAsync (mutation target #19).");
        Assert.True(
            indiceLockProveedor > indiceCostoUpdate,
            "El lock de proveedores debe aparecer DESPUÉS de ActualizarCostoNominalAsync (mutation target #19).");
        Assert.True(
            indiceLedgerInsert > indiceLockProveedor,
            "El INSERT del ledger de proveedor debe seguir inmediatamente al lock de saldo.");
        Assert.True(
            indiceCommit > indiceLedgerInsert,
            "El commit de confirmar debe ser posterior al INSERT del ledger de proveedor.");
    }

    [Fact]
    public void ElLockDeSaldoDeProveedorEsElUltimoDeEjecutarAnulacionAsyncYElLedgerLoSigue()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<ResultadoAnulacion> EjecutarAnulacionAsync(",
            "// ---- aplicar precio sugerido");

        var indiceGastosLigados = metodo.IndexOf("db.Gastos.CountAsync(g => g.IdComprobanteCompra == id, ct);", StringComparison.Ordinal);
        var indiceLockProveedor = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);
        var indiceLedgerInsert = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(", StringComparison.Ordinal);
        var indiceCommit = metodo.IndexOf("transaccion.CommitAsync(ct);", StringComparison.Ordinal);

        Assert.True(indiceGastosLigados >= 0, "No se encontró el conteo informativo de gastosLigados.");
        Assert.True(indiceLockProveedor >= 0, "No se encontró la llamada a ActualizarSaldoProveedorAsync.");
        Assert.True(indiceLedgerInsert >= 0, "No se encontró la llamada a InsertarMovimientoCcProveedorAsync.");
        Assert.True(indiceCommit >= 0, "No se encontró el commit de EjecutarAnulacionAsync.");

        Assert.True(
            indiceLockProveedor > indiceGastosLigados,
            "El lock de proveedores debe aparecer DESPUÉS del conteo informativo de gastosLigados (mutation target #19).");
        Assert.True(
            indiceLedgerInsert > indiceLockProveedor,
            "El INSERT del ledger de proveedor debe seguir inmediatamente al lock de saldo.");
        Assert.True(
            indiceCommit > indiceLedgerInsert,
            "El commit de anular debe ser posterior al INSERT del ledger de proveedor.");
    }
}
