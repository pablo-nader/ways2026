using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 1 (task 1.16, db-error-backstops, design decisión 5):
/// mismo patrón unit-style que <see cref="ManejadorDeErroresComprasTests"/> — sin
/// <c>WaysApiFixture</c> ni Postgres real, la <see cref="PostgresException"/> se construye "a
/// mano". No hay ningún endpoint todavía en esta slice (<c>ServicioDeLotes</c> es Slice 3) para
/// ejercer el round-trip HTTP completo, así que la prueba de que <c>ux_lotes_articulo_codigo</c>
/// gana la rama nueva ANTES de caer en la familia genérica de <c>ClasificarUnicidad</c> (que la
/// clasificaría como <c>codigo_duplicado</c>, por la substring "_codigo") vive acá — el mismo
/// hallazgo que <c>ux_comprobantes_venta_numero</c>/<c>ux_comprobantes_compra_numero_externo</c>
/// ya necesitaron.
/// </summary>
public class ManejadorDeErroresLotesTests
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

    // ---- ux_lotes_articulo_codigo (el trap de ordering: substring "_codigo") -----------------

    [Fact]
    public async Task UxLotesArticuloCodigoGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeCodigo()
    {
        var postgres = CrearExcepcion("23505", "ux_lotes_articulo_codigo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("lote_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico "_codigo" —
        // sin la rama exacta ANTES de ClasificarUnicidad, la substring "_codigo" la atraparía
        // primero y la clasificaría mal.
        Assert.NotEqual("codigo_duplicado", codigo);
    }

    // ---- ux_lotes_sin_identificar (exención documentada, sin ruta de cliente que la ejerza) --

    [Fact]
    public async Task UxLotesSinIdentificarSeTraduceA409LoteSinIdentificarDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_lotes_sin_identificar");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("lote_sin_identificar_duplicado", codigo);
    }

    // ---- las cuatro FKs nuevas: el match genérico por prefijo "fk_" ya las cubre --------------

    [Theory]
    [InlineData("fk_lotes_tenant")]
    [InlineData("fk_lotes_articulo")]
    [InlineData("fk_stock_lotes_tenant")]
    [InlineData("fk_stock_lotes_lote")]
    [InlineData("fk_stock_lotes_punto_venta")]
    [InlineData("fk_movimientos_stock_lote")]
    [InlineData("fk_items_comprobante_venta_lote")]
    [InlineData("fk_items_comprobante_compra_lote")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }
}
