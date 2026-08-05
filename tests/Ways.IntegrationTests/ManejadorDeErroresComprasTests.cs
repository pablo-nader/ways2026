using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 1 (task 1.10, db-error-backstops, design:
/// Backstop Map — "ordering trap"): mismo patrón unit-style que
/// <see cref="ManejadorDeErroresVentasTests"/> — sin <c>WaysApiFixture</c> ni Postgres real, la
/// <see cref="PostgresException"/> se construye "a mano". No hay endpoint todavía en esta slice
/// (<c>ServicioDeCompras</c> es Slice 2) para ejercer el round-trip HTTP completo, así que la
/// prueba de que <c>ux_comprobantes_compra_numero_externo</c> gana la rama nueva ANTES de caer
/// en la familia genérica de <c>ClasificarUnicidad</c> (que la clasificaría como
/// <c>numero_duplicado</c>, el código de <c>ux_clientes_numero</c>, por la substring "_numero")
/// vive acá — el mismo hallazgo que <c>ux_comprobantes_venta_numero</c> ya necesitó.
/// </summary>
public class ManejadorDeErroresComprasTests
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

    // ---- ux_comprobantes_compra_numero_externo (el trap de ordering) -------------------------

    [Fact]
    public async Task UxComprobantesCompraNumeroExternoGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero()
    {
        var postgres = CrearExcepcion("23505", "ux_comprobantes_compra_numero_externo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("compra_duplicada", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero
        // — sin la rama exacta ANTES de ClasificarUnicidad, la substring "_numero" la atraparía
        // primero y la clasificaría mal.
        Assert.NotEqual("numero_duplicado", codigo);
    }

    [Fact]
    public async Task UxItemsComprobanteCompraOrdenSeTraduceA409OrdenDeItemDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_items_comprobante_compra_orden");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_de_item_duplicado", codigo);
    }

    // ---- ClasificarCheckDeCompras (detrás del guard de prefijo) -------------------------------

    [Fact]
    public async Task CkComprobantesCompraConfirmadaCompletaSeTraduceA400CompraIncompletaParaConfirmar()
    {
        var postgres = CrearExcepcion("23514", "ck_comprobantes_compra_confirmada_completa");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("compra_incompleta_para_confirmar", codigo);
    }

    [Fact]
    public async Task CkComprobantesCompraTotalesNoNegativosSeTraduceA400TotalesDeCompraInvalidos()
    {
        var postgres = CrearExcepcion("23514", "ck_comprobantes_compra_totales_no_negativos");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("totales_de_compra_invalidos", codigo);
    }

    [Fact]
    public async Task CkItemsComprobanteCompraCantidadPositivaSeTraduceA400CantidadDeItemInvalida()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_compra_cantidad_positiva");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cantidad_de_item_invalida", codigo);
    }

    [Fact]
    public async Task CkItemsComprobanteCompraCostoNoNegativoSeTraduceA400CostoDeItemInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_compra_costo_no_negativo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("costo_de_item_invalido", codigo);
    }

    [Fact]
    public async Task CkItemsComprobanteCompraImportesNoNegativosSeTraduceA400ImportesDeItemInvalidos()
    {
        var postgres = CrearExcepcion("23514", "ck_items_comprobante_compra_importes_no_negativos");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("importes_de_item_invalidos", codigo);
    }

    // ---- FKs nuevas: el match genérico por prefijo "fk_" ya las cubre, sin cambio de código --

    [Theory]
    [InlineData("fk_comprobantes_compra_tenant")]
    [InlineData("fk_comprobantes_compra_proveedor")]
    [InlineData("fk_comprobantes_compra_punto_venta")]
    [InlineData("fk_comprobantes_compra_empleado")]
    [InlineData("fk_comprobantes_compra_tipo_comprobante")]
    [InlineData("fk_items_comprobante_compra_tenant")]
    [InlineData("fk_items_comprobante_compra_comprobante")]
    [InlineData("fk_items_comprobante_compra_articulo")]
    [InlineData("fk_items_comprobante_compra_alicuota_iva")]
    [InlineData("fk_movimientos_stock_comprobante_compra")]
    [InlineData("fk_gastos_comprobante_compra")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }
}
