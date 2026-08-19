using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 1 (tasks 1.21-1.25, db-error-backstops, design
/// decisión 18, proposal §J): mismo patrón unit-style que
/// <see cref="ManejadorDeErroresOrdenesDeCompraTests"/> — sin <c>WaysApiFixture</c> ni Postgres
/// real, la <see cref="PostgresException"/> se construye "a mano". No hay endpoint todavía en
/// esta slice (<c>ServicioDePresupuestos</c> es slice 2, la conversión es slice 3) para ejercer
/// el round-trip HTTP completo; la prueba de que <c>ux_presupuestos_numero</c> gana la rama
/// nueva ANTES de caer en la familia genérica de <c>ClasificarUnicidad</c> — la **cuarta**
/// ocurrencia del ordering trap — vive acá, junto con la traducción de
/// <c>ux_comprobantes_venta_presupuesto_origen</c> (mutation targets 7/8) y las dos CHECKs
/// nuevas (mutation targets 2/3, junto con las pruebas de esquema/SQLSTATE puro en
/// <c>PresupuestosSchemaTests</c>).
/// </summary>
public class ManejadorDeErroresPresupuestosTests
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

    // ---- ux_presupuestos_numero (el trap de ordering, CUARTA ocurrencia) ----------------------

    /// <summary>Mutation target #7: prueba tanto el camino EF (<c>DbUpdateException</c>) como el
    /// camino raw-ADO (<c>PostgresException</c> pelada, <c>enviar</c> usa SQL crudo, slice 2) —
    /// los dos llaman a <c>ClasificarPostgresException</c>, misma prioridad de resolución.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UxPresupuestosNumeroGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero(bool caminoRawAdo)
    {
        var postgres = CrearExcepcion("23505", "ux_presupuestos_numero");
        Exception excepcion = caminoRawAdo ? postgres : new DbUpdateException("dup", postgres);

        var (estado, codigo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numero_de_presupuesto_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero
        // — sin la rama exacta ANTES de ClasificarUnicidad, la substring "_numero" la atraparía
        // primero y la clasificaría como "numero_duplicado".
        Assert.NotEqual("numero_duplicado", codigo);
    }

    // ---- ux_comprobantes_venta_presupuesto_origen (mutation target #8) ------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UxComprobantesVentaPresupuestoOrigenSeTraduceA409PresupuestoYaConvertido(bool caminoRawAdo)
    {
        var postgres = CrearExcepcion("23505", "ux_comprobantes_venta_presupuesto_origen");
        Exception excepcion = caminoRawAdo ? postgres : new DbUpdateException("dup", postgres);

        var (estado, codigo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("presupuesto_ya_convertido", codigo);
    }

    [Fact]
    public async Task UxItemsPresupuestoOrdenSeTraduceA409OrdenDeItemDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_items_presupuesto_orden");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("orden_de_item_duplicado", codigo);
    }

    // ---- ClasificarCheckDePresupuestos (detrás del guard de prefijo) --------------------------

    [Fact]
    public async Task CkPresupuestosEnvioCompletoSeTraduceA409PresupuestoEnvioIncompleto()
    {
        var postgres = CrearExcepcion("23514", "ck_presupuestos_envio_completo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("presupuesto_envio_incompleto", codigo);
    }

    [Fact]
    public async Task CkItemsPresupuestoCantidadPositivaSeTraduceA400CantidadDeLineaInvalida()
    {
        var postgres = CrearExcepcion("23514", "ck_items_presupuesto_cantidad_positiva");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cantidad_de_linea_invalida", codigo);
    }

    // ---- FKs exentas: el match genérico por prefijo "fk_" ya las cubre, sin cambio de código ---

    [Theory]
    [InlineData("fk_presupuestos_tenant")]
    [InlineData("fk_presupuestos_punto_venta")]
    [InlineData("fk_presupuestos_cliente")]
    [InlineData("fk_presupuestos_empleado")]
    [InlineData("fk_items_presupuesto_tenant")]
    [InlineData("fk_items_presupuesto_presupuesto")]
    [InlineData("fk_items_presupuesto_articulo")]
    [InlineData("fk_items_presupuesto_lista_precio")]
    [InlineData("fk_items_presupuesto_oferta")]
    [InlineData("fk_items_presupuesto_alicuota_iva")]
    [InlineData("fk_comprobantes_venta_presupuesto_origen")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }
}
