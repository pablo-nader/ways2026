using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 1 (task 1.19, db-error-backstops, design decisiones 10-11):
/// mismo patrón unit-style que <see cref="ManejadorDeErroresComprasTests"/> — sin
/// <c>WaysApiFixture</c> ni Postgres real, la <see cref="PostgresException"/> se construye "a
/// mano". No hay endpoint todavía en esta slice (<c>ServicioDeOrdenesDeCompra</c> es slice 2)
/// para ejercer el round-trip HTTP completo; la prueba de que <c>ux_ordenes_compra_numero</c>
/// gana la rama nueva ANTES de caer en la familia genérica de <c>ClasificarUnicidad</c> — la
/// **tercera ocurrencia** del ordering trap — vive acá, el binding gate test (c) del slice.
/// </summary>
public class ManejadorDeErroresOrdenesDeCompraTests
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

    // ---- ux_ordenes_compra_numero (el trap de ordering, TERCERA ocurrencia) -------------------

    /// <summary>Mutation target #7 / binding gate test (c): prueba tanto el camino EF
    /// (<c>DbUpdateException</c>) como el camino raw-ADO (<c>PostgresException</c> pelada,
    /// <c>enviar</c> usa SQL crudo) — los dos llaman a <c>ClasificarPostgresException</c>, misma
    /// prioridad de resolución.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UxOrdenesCompraNumeroGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero(bool caminoRawAdo)
    {
        var postgres = CrearExcepcion("23505", "ux_ordenes_compra_numero");
        Exception excepcion = caminoRawAdo ? postgres : new DbUpdateException("dup", postgres);

        var (estado, codigo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numero_de_orden_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero
        // — sin la rama exacta ANTES de ClasificarUnicidad, la substring "_numero" la atraparía
        // primero y la clasificaría como "numero_duplicado".
        Assert.NotEqual("numero_duplicado", codigo);
    }

    [Fact]
    public async Task UxItemsOrdenCompraOrdenSeTraduceA409OrdenDeItemDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_items_orden_compra_orden");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_de_item_duplicado", codigo);
    }

    // ---- ClasificarCheckDeOrdenesDeCompra (detrás del guard de prefijo) ------------------------

    [Fact]
    public async Task CkOrdenesCompraEnvioCompletoSeTraduceA409OrdenCompraEnvioIncompleto()
    {
        var postgres = CrearExcepcion("23514", "ck_ordenes_compra_envio_completo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_compra_envio_incompleto", codigo);
    }

    [Fact]
    public async Task CkOrdenesCompraCierreSeTraduceA409OrdenCompraCierreIncoherente()
    {
        var postgres = CrearExcepcion("23514", "ck_ordenes_compra_cierre");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_compra_cierre_incoherente", codigo);
    }

    [Fact]
    public async Task CkItemsOrdenCompraCantidadPositivaSeTraduceA400CantidadPedidaInvalida()
    {
        var postgres = CrearExcepcion("23514", "ck_items_orden_compra_cantidad_positiva");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cantidad_pedida_invalida", codigo);
    }

    [Fact]
    public async Task CkItemsOrdenCompraCostoNoNegativoSeTraduceA400CostoEstimadoInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_orden_compra_costo_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_estimado_invalido", codigo);
    }

    // ---- FKs exentas: el match genérico por prefijo "fk_" ya las cubre, sin cambio de código ---

    [Theory]
    [InlineData("fk_ordenes_compra_tenant")]
    [InlineData("fk_ordenes_compra_punto_venta")]
    [InlineData("fk_ordenes_compra_proveedor")]
    [InlineData("fk_ordenes_compra_empleado")]
    [InlineData("fk_ordenes_compra_empleado_cierre")]
    [InlineData("fk_items_orden_compra_tenant")]
    [InlineData("fk_items_orden_compra_orden_compra")]
    [InlineData("fk_items_orden_compra_articulo")]
    [InlineData("fk_comprobantes_compra_orden_compra")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }
}
