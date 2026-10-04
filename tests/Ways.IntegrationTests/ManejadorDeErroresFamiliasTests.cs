using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// db-error-backstops de las familias de artículos: mismo patrón unit-style que
/// <see cref="ManejadorDeErroresDispositivosTests"/> — sin <c>WaysApiFixture</c> ni Postgres real, la
/// <see cref="PostgresException"/> se construye "a mano" contra el <see cref="ManejadorDeErrores"/>
/// real. Cubre la traducción de <c>ux_familias_nombre</c>; que ese índice existe y dispara 23505 de
/// verdad lo prueba <see cref="FamiliasEsquemaTests"/>.
/// </summary>
public class ManejadorDeErroresFamiliasTests
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

    /// <summary>Cláusula: el brazo de nombre exacto de <c>ux_familias_nombre</c>. Sin él, el brazo
    /// genérico de <c>ClasificarUnicidad</c> (que matchea cualquier índice con <c>_nombre</c>) lo
    /// traduce a <c>nombre_duplicado</c> — el mismo 409 pero con el código de un padrón cualquiera.</summary>
    [Fact]
    public async Task UxFamiliasNombreSeTraduceA409FamiliaNombreDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_familias_nombre");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("familia_nombre_duplicado", codigo);
    }

    /// <summary>La excepción pelada (camino raw-ADO, sin <see cref="DbUpdateException"/>) pasa por
    /// el mismo clasificador y recibe el mismo 409.</summary>
    [Fact]
    public async Task UxFamiliasNombreSeTraduceTambienCuandoLlegaPeladaDesdeSqlCrudo()
    {
        var (estado, codigo) = await ManejarAsync(CrearExcepcion("23505", "ux_familias_nombre"));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("familia_nombre_duplicado", codigo);
    }
}
