using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos (db-error-backstops): mismo patrón unit-style que
/// <see cref="ManejadorDeErroresFiscalTests"/> — sin <c>WaysApiFixture</c> ni Postgres real, la
/// <see cref="PostgresException"/> se construye "a mano" contra el <see cref="ManejadorDeErrores"/>
/// real. Cubre la rama nueva de <c>ux_dispositivos_token_hash</c>. Sin prueba de carrera real: el
/// secreto sale de <c>RandomNumberGenerator</c> sobre 32 bytes (2^256 valores), así que forzar una
/// colisión real es impracticable — el backstop se prueba acá solo como traducción de esquema.
/// </summary>
public class ManejadorDeErroresDispositivosTests
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
    public async Task UxDispositivosTokenHashSeTraduceA409DispositivoTokenDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_dispositivos_token_hash");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("dispositivo_token_duplicado", codigo);
    }
}
