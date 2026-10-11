using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// db-error-backstops de los combos, mismo patrón unit-style que <see cref="ManejadorDeErroresFamiliasTests"/>: la
/// <see cref="PostgresException"/> se construye "a mano" contra el <see cref="ManejadorDeErrores"/> real. Que cada
/// índice dispara 23505 de verdad lo prueba <see cref="CombosEsquemaTests"/>, y que el endpoint lo devuelve como 409,
/// <see cref="CombosRespaldosTests"/>.
/// </summary>
public class ManejadorDeErroresCombosTests
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

    /// <summary>Cláusula: el brazo de nombre exacto de <c>ux_combos_codigo</c>. Sin él, el brazo genérico de
    /// <c>ClasificarUnicidad</c> (que matchea cualquier índice con <c>_codigo</c>) lo traduce a
    /// <c>codigo_duplicado</c>.</summary>
    [Fact]
    public async Task UxCombosCodigoSeTraduceA409ComboCodigoDuplicado()
    {
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", CrearExcepcion("23505", "ux_combos_codigo")));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("combo_codigo_duplicado", codigo);
    }

    [Fact]
    public async Task UxCombosCodigoSeTraduceTambienCuandoLlegaPeladaDesdeSqlCrudo()
    {
        var (estado, codigo) = await ManejarAsync(CrearExcepcion("23505", "ux_combos_codigo"));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("combo_codigo_duplicado", codigo);
    }

    [Theory]
    [InlineData("ux_combos_componentes_articulo")]
    [InlineData("ux_combos_componentes_familia")]
    public async Task LosIndicesDeComponenteSeTraducenA409ComboComponenteDuplicado(string indice)
    {
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", CrearExcepcion("23505", indice)));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("combo_componente_duplicado", codigo);
    }

    /// <summary>Un 23505 de un índice de combos que NO tiene traducción propia no se disfraza de uno que sí la tiene.</summary>
    [Fact]
    public async Task UnIndiceDeOtraTablaConCodigoEnElNombreSigueConElBrazoGenerico()
    {
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", CrearExcepcion("23505", "ux_otra_cosa_codigo")));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("codigo_duplicado", codigo);
    }
}
