using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 4 (tasks 4.22-4.26, db-error-backstops, design
/// decisión 18, proposal §J): mismo patrón unit-style que
/// <see cref="ManejadorDeErroresPresupuestosTests"/> — sin <c>WaysApiFixture</c> ni Postgres
/// real, la <see cref="PostgresException"/> se construye "a mano". No hay endpoint todavía en
/// esta slice (<c>ServicioDeRemitos</c> es slice 5, la consolidación es slice 6) para ejercer el
/// round-trip HTTP completo; la prueba de que <c>ux_remitos_numero</c> gana la rama nueva ANTES
/// de caer en la familia genérica de <c>ClasificarUnicidad</c> — la **quinta** ocurrencia del
/// ordering trap — vive acá, junto con <c>ux_items_remito_orden</c> y las cinco CHECKs nuevas
/// (CORRECCIÓN registrada en tasks.md: son CINCO, no tres — proposal §J agrupa CHECK 2/5/6/7 en
/// una sola fila "exact-name mapping" y design.md's Backstop Map lista CHECK 6/7 explícitamente;
/// el "3" de la Orchestrator Decision 9 de tasks.md es un artefacto de redacción).
/// </summary>
public class ManejadorDeErroresRemitosTests
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

    // ---- ux_remitos_numero (el trap de ordering, QUINTA ocurrencia) ----------------------------

    /// <summary>Mutation target #38: prueba tanto el camino EF (<c>DbUpdateException</c>) como el
    /// camino raw-ADO (<c>PostgresException</c> pelada, <c>emitir</c> usa SQL crudo, slice 5).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UxRemitosNumeroGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero(bool caminoRawAdo)
    {
        var postgres = CrearExcepcion("23505", "ux_remitos_numero");
        Exception excepcion = caminoRawAdo ? postgres : new DbUpdateException("dup", postgres);

        var (estado, codigo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numero_de_remito_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero
        // — sin la rama exacta ANTES de ClasificarUnicidad, la substring "_numero" la atraparía
        // primero y la clasificaría como "numero_duplicado".
        Assert.NotEqual("numero_duplicado", codigo);
    }

    [Fact]
    public async Task UxItemsRemitoOrdenSeTraduceA409OrdenDeItemDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_items_remito_orden");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_de_item_duplicado", codigo);
    }

    // ---- ClasificarCheckDeRemitos (detrás del guard de prefijo) — CINCO ramas -------------------

    [Fact]
    public async Task CkRemitosSalidaCompletaSeTraduceA409RemitoSalidaIncompleta()
    {
        var postgres = CrearExcepcion("23514", "ck_remitos_salida_completa");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("remito_salida_incompleta", codigo);
    }

    [Fact]
    public async Task CkRemitosFacturacionSeTraduceA409RemitoFacturacionIncoherente()
    {
        var postgres = CrearExcepcion("23514", "ck_remitos_facturacion");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("remito_facturacion_incoherente", codigo);
    }

    [Fact]
    public async Task CkItemsRemitoCantidadPositivaSeTraduceA400CantidadDeLineaInvalida()
    {
        var postgres = CrearExcepcion("23514", "ck_items_remito_cantidad_positiva");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cantidad_de_linea_invalida", codigo);
    }

    [Fact]
    public async Task CkItemsRemitoCostoNoNegativoSeTraduceA400CostoDeLineaInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_remito_costo_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_de_linea_invalido", codigo);
    }

    [Fact]
    public async Task CkItemsRemitoEstimadoConCostoSeTraduceA400CostoEstimadoInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_remito_estimado_con_costo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_estimado_invalido", codigo);
    }

    // ---- FKs exentas: el match genérico por prefijo "fk_" ya las cubre, sin cambio de código ---

    [Theory]
    [InlineData("fk_remitos_tenant")]
    [InlineData("fk_remitos_punto_venta")]
    [InlineData("fk_remitos_cliente")]
    [InlineData("fk_remitos_empleado")]
    [InlineData("fk_remitos_comprobante_venta")]
    [InlineData("fk_items_remito_tenant")]
    [InlineData("fk_items_remito_remito")]
    [InlineData("fk_items_remito_articulo")]
    [InlineData("fk_items_remito_lista_precio")]
    [InlineData("fk_items_remito_oferta")]
    [InlineData("fk_items_remito_alicuota_iva")]
    [InlineData("fk_items_remito_lote")]
    [InlineData("fk_movimientos_stock_remito")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }

    // ---- AK exenta: estructuralmente inviolable, sin rama de 23505 ------------------------------

    [Fact]
    public async Task AkRemitosSinRamaDe23505CaeAlDefaultDe500()
    {
        var postgres = CrearExcepcion("23505", "ak_remitos_id_remito_id_tenant");
        var (estado, _) = await ManejarAsync(new DbUpdateException("ak", postgres));

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
    }
}
