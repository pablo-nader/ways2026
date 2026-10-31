using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.10/3.12, db-error-backstops, design: Backstop Map —
/// "ordering trap"): mismo patrón unit-style que <see cref="ManejadorDeErroresTests"/> — sin
/// <c>WaysApiFixture</c> ni Postgres real, <see cref="PostgresException"/> se construye "a mano"
/// con el constructor largo (el único que expone <c>constraintName</c> como parámetro; la
/// propiedad es de solo lectura).
///
/// Esto prueba la traducción exacta que un raw-SQL backstop (23505/23514 + <c>ConstraintName</c>
/// contra Postgres real) no puede probar por sí solo: no hay endpoint todavía en esta slice
/// (<c>ServicioDeVentas</c> es Slice 4) para ejercer el round-trip HTTP completo, así que la
/// prueba de que <c>ux_comprobantes_venta_numero</c> gana la rama nueva ANTES de caer en la
/// familia genérica de <c>ClasificarUnicidad</c> (que la clasificaría como
/// <c>numero_duplicado</c>, el código de <c>ux_clientes_numero</c>) vive acá.
/// </summary>
public class ManejadorDeErroresVentasTests
{
    private sealed class ServicioDeProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }

    private static PostgresException CrearExcepcion(string sqlState, string constraintName) =>
        new(
            messageText: "mensaje de prueba",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName,
            file: null,
            line: null,
            routine: null);

    private static async Task<(int Estado, string? Codigo)> ManejarAsync(Exception excepcion)
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string);
    }

    [Fact]
    public async Task UxComprobantesVentaNumeroGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero()
    {
        var postgres = CrearExcepcion("23505", "ux_comprobantes_venta_numero");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numero_de_comprobante_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero.
        Assert.NotEqual("numero_duplicado", codigo);
    }

    [Fact]
    public async Task PkStockSeTraduceA409StockDuplicado()
    {
        var postgres = CrearExcepcion("23505", "pk_stock");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("stock_duplicado", codigo);
    }

    [Fact]
    public async Task CkComprobantesVentaNumeroPositivoSeTraduceA400NumeroDeComprobanteInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_comprobantes_venta_numero_positivo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("numero_de_comprobante_invalido", codigo);
    }

    [Fact]
    public async Task CkCobrosVueltoNoNegativoSeTraduceA400VueltoDePagoNegativo()
    {
        var postgres = CrearExcepcion("23514", "ck_cobros_vuelto_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("vuelto_de_pago_negativo", codigo);
        // Distinto del código de dominio de ValidadorDePagos para la regla 8 (Orchestrator
        // Decision 2, tasks.md): nunca "vuelto_invalido".
        Assert.NotEqual("vuelto_invalido", codigo);
    }

    [Fact]
    public async Task CkMovimientosStockCantidadNoCeroSeTraduceA400MovimientoDeStockSinCantidad()
    {
        var postgres = CrearExcepcion("23514", "ck_movimientos_stock_cantidad_no_cero");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("movimiento_de_stock_sin_cantidad", codigo);
    }

    [Fact]
    public async Task CkCobrosImporteNoNegativoSeTraduceA400PagoImporteNegativo()
    {
        var postgres = CrearExcepcion("23514", "ck_cobros_importe_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("pago_importe_negativo", codigo);
        // A diferencia de ck_cobros_vuelto_no_negativo, este código SÍ coincide con el
        // de la regla 0 de ValidadorDePagos a propósito (misma regla de negocio, backstop de
        // esquema de la misma validación).
    }

    // ---- stage-9-costo-congelado (task 1.14, design: Backstop Map) --------------------------

    [Fact]
    public async Task CkItemsComprobanteVentaCostoNoNegativoSeTraduceA400CostoDeItemInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_venta_costo_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_de_item_invalido", codigo);
    }

    [Fact]
    public async Task CkItemsComprobanteVentaEstimadoConCostoSeTraduceA400CostoEstimadoSinCosto()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_venta_estimado_con_costo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_estimado_sin_costo", codigo);
    }

    // ---- ajuste manual por línea (backstop de esquema) ---------------------------------------

    [Fact]
    public async Task CkItemsComprobanteVentaAjusteManualPorcentajeValidoSeTraduceA400AjusteManualInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_venta_ajuste_manual_porcentaje_valido");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("ajuste_manual_invalido", codigo);
    }

    [Fact]
    public async Task CkItemsComprobanteVentaAjusteManualConPorcentajeSeTraduceA400AjusteManualSinPorcentaje()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_venta_ajuste_manual_con_porcentaje");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("ajuste_manual_sin_porcentaje", codigo);
    }

    // ---- aplicaciones de cobros a ventas (E7-S2) -------------------------------------------

    /// <summary>Los seis respaldos de <c>aplicaciones_cobro</c>, envueltos por EF (un SaveChanges) y crudos (los triggers
    /// diferidos fallan en el COMMIT, que Npgsql tira sin envolver).</summary>
    [Theory]
    [InlineData("23514", "ck_aplicaciones_cobro_importe_positivo", StatusCodes.Status400BadRequest, "aplicacion_importe_invalido")]
    [InlineData("23514", "tg_aplicaciones_cobro_comprobantes_validos", StatusCodes.Status409Conflict, "aplicacion_cobro_comprobante_invalido")]
    [InlineData("23514", "tg_aplicaciones_cobro_no_excede_cobro", StatusCodes.Status409Conflict, "cobro_excede_disponible")]
    [InlineData("23514", "tg_aplicaciones_cobro_no_excede_venta", StatusCodes.Status409Conflict, "aplicacion_excede_saldo_de_venta")]
    [InlineData("23514", "tg_aplicaciones_cobro_mismo_cliente", StatusCodes.Status409Conflict, "aplicacion_cobro_otro_cliente")]
    [InlineData("23505", "ux_aplicaciones_cobro_cobro_venta", StatusCodes.Status409Conflict, "aplicacion_cobro_duplicada")]
    public async Task LosRespaldosDeAplicacionesDeCobroSeTraducenAlCodigoDeNegocio(
        string sqlState, string constraint, int estadoEsperado, string codigoEsperado)
    {
        var postgres = CrearExcepcion(sqlState, constraint);

        Assert.Equal((estadoEsperado, codigoEsperado), await ManejarAsync(new DbUpdateException("respaldo", postgres)));
        Assert.Equal((estadoEsperado, codigoEsperado), await ManejarAsync(CrearExcepcion(sqlState, constraint)));
    }
}
