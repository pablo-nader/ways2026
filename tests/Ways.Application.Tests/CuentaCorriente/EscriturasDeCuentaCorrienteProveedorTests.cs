using Ways.Application.CuentaCorriente;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.Tests.CuentaCorriente;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 2 (task 2.10; design.md:113-118, 372): la matriz 4×3 de
/// forma por tipo de <c>EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync</c>
/// — un fact por combinación ilegal. El guard corre ANTES de tocar la conexión (mismo criterio que
/// <see cref="EscriturasDeCuentaCorrienteTests"/>), así que <c>null!</c> alcanza para pinearlo sin
/// una base de datos real.
///
/// Mutation targets #12 y #13 (task 2.18, 2.19): borrar el arm de <c>Compra</c> exige comprobante,
/// o el arm de <c>Apertura</c> prohíbe actor/PV, y estos mismos facts deben fallar.
/// </summary>
public class EscriturasDeCuentaCorrienteProveedorTests
{
    private static Task<int> InsertarAsync(
        TipoMovimientoCcProveedor tipo, int? idComprobanteCompra = null, int? idGasto = null,
        int? idPuntoVenta = null, int? idEmpleado = null) =>
        EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            null!, null, idTenant: 1, idProveedor: 1, fecha: DateTimeOffset.UtcNow, idPuntoVenta, idEmpleado,
            tipo, idComprobanteCompra, idGasto, importe: 100m, saldoResultante: 100m, detalle: null,
            CancellationToken.None);

    // ---- apertura ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnaAperturaConIdPuntoVentaViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Apertura, idPuntoVenta: 5));

        Assert.Contains("Apertura", excepcion.Message);
    }

    [Fact]
    public async Task UnaAperturaConIdEmpleadoViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Apertura, idEmpleado: 5));

        Assert.Contains("Apertura", excepcion.Message);
    }

    [Fact]
    public async Task UnaAperturaConIdComprobanteCompraViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Apertura, idComprobanteCompra: 5));

        Assert.Contains("Apertura", excepcion.Message);
    }

    [Fact]
    public async Task UnaAperturaConIdGastoViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Apertura, idGasto: 5));

        Assert.Contains("Apertura", excepcion.Message);
    }

    // ---- compra (mutation target #12) --------------------------------------------------------------

    [Fact]
    public async Task UnaCompraSinIdComprobanteCompraViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Compra, idPuntoVenta: 1, idEmpleado: 1));

        Assert.Contains("Compra", excepcion.Message);
        Assert.Contains("id_comprobante_compra", excepcion.Message);
    }

    [Fact]
    public async Task UnaCompraConIdGastoViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Compra, idComprobanteCompra: 5, idGasto: 9, idPuntoVenta: 1, idEmpleado: 1));

        Assert.Contains("Compra", excepcion.Message);
    }

    [Fact]
    public async Task UnaCompraSinPuntoDeVentaViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Compra, idComprobanteCompra: 5, idEmpleado: 1));

        Assert.Contains("Compra", excepcion.Message);
    }

    // ---- pago ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnPagoSinIdGastoViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Pago, idPuntoVenta: 1, idEmpleado: 1));

        Assert.Contains("Pago", excepcion.Message);
        Assert.Contains("id_gasto", excepcion.Message);
    }

    [Fact]
    public async Task UnPagoSinActorViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Pago, idGasto: 9));

        Assert.Contains("Pago", excepcion.Message);
    }

    // ---- ajuste --------------------------------------------------------------------------------------

    [Fact]
    public async Task UnAjusteConIdGastoViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Ajuste, idGasto: 9, idPuntoVenta: 1, idEmpleado: 1));

        Assert.Contains("Ajuste", excepcion.Message);
    }

    [Fact]
    public async Task UnAjusteSinActorViolaLaFormaYLanza()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Ajuste, idComprobanteCompra: 5));

        Assert.Contains("Ajuste", excepcion.Message);
    }

    // ---- mutation target #13: Apertura forbids actor/PV (arm aislado de compra/pago/ajuste) ---------

    [Fact]
    public async Task UnaAperturaConPuntoDeVentaYEmpleadoJuntosViolaLaFormaYLanzaMencionandoApertura()
    {
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InsertarAsync(TipoMovimientoCcProveedor.Apertura, idPuntoVenta: 1, idEmpleado: 1));

        Assert.Contains("Apertura", excepcion.Message);
        Assert.Contains("id_punto_venta", excepcion.Message);
    }

    // ---- mutation target #17: UPDATE aditivo crudo, nunca un Proveedor.Saldo trackeado -------------

    /// <summary>Mutation target #17 (task 2.23) es un mutante PROVABLY EQUIVALENT AT RUNTIME contra
    /// los call sites reales de este slice — resuelto con una aserción de texto fuente, mismo
    /// criterio que los targets #4/#11 de slice 1
    /// (<c>CuentaCorrienteProveedorBackfillTests</c>) y el #19 de este mismo slice
    /// (<c>ServicioDeComprasLockOrderTests</c>), <c>mutation-proof-tests</c> rule 3 agotada
    /// primero.
    ///
    /// El escenario literal del target ("double-count under a forced execution-strategy retry")
    /// es estructuralmente INALCANZABLE por <c>ServicioDeCompras.ConfirmarAsync</c>/
    /// <c>AnularAsync</c>: ambos envuelven su núcleo transaccional en
    /// <c>FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento</c>
    /// (<c>maxRetryCount: 0</c>, <c>ShouldRetryOn</c> siempre <c>false</c>) — cuyo propio
    /// doc-comment declara que existe PRECISAMENTE para que un reintento nunca vuelva a ejecutar
    /// este statement. Construir una prueba de comportamiento que fuerce ese reintento exigiría
    /// bypassear la propia clase que este código usa para prevenirlo — lo que la prueba
    /// necesitaría demostrar que NO puede pasar.
    /// </summary>
    [Fact]
    public void ElTextoFuenteDeActualizarSaldoProveedorAsyncUsaElUpdateAditivoCrudoTarget17()
    {
        var fuente = LeerFuenteDeEscriturasDeCuentaCorrienteProveedor();
        var metodo = ExtraerMetodo(fuente, "public static async Task<decimal> ActualizarSaldoProveedorAsync(", "public static async Task<int> InsertarMovimientoCcProveedorAsync(");

        Assert.Contains("UPDATE proveedores SET saldo = saldo + $1", metodo);

        // Nunca una entidad Proveedor trackeada — ningún acceso a IWaysDbContext.Proveedores ni a
        // SaveChangesAsync dentro de este método (design decisión 2).
        Assert.DoesNotContain(".Proveedores", metodo);
        Assert.DoesNotContain("SaveChangesAsync", metodo);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuenteDeEscriturasDeCuentaCorrienteProveedor()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "CuentaCorriente", "EscriturasDeCuentaCorrienteProveedor.cs");

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string ExtraerMetodo(string fuente, string firma, string siguiente)
    {
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{firma}'.");

        var fin = fuente.IndexOf(siguiente, inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró '{siguiente}' después de '{firma}'.");

        return fuente[inicio..fin];
    }
}
