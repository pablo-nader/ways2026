using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// db-error-backstops del artículo por importe: mismo patrón unit-style que
/// <see cref="ManejadorDeErroresFamiliasTests"/>, con la <see cref="PostgresException"/> armada a mano contra el
/// <see cref="ManejadorDeErrores"/> real. Que el índice y los CHECK existen y disparan de verdad con ese SQLSTATE y ese
/// nombre lo prueba <see cref="ArticulosPorImporteTests"/>; acá se prueba solo la traducción de cada uno.
/// </summary>
public class ManejadorDeErroresArticulosPorImporteTests
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

        return (contexto.Response.StatusCode, servicioDeProblemDetails.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }

    /// <summary>Cada respaldo tiene su propio caso: "ck_articulos_tecla_atajo" es prefijo de
    /// "ck_articulos_tecla_atajo_solo_importe", y un brazo por prefijo los confundiría.</summary>
    [Theory]
    [InlineData("23505", "ux_articulos_tecla_atajo", StatusCodes.Status409Conflict, "tecla_atajo_duplicada")]
    [InlineData("23514", "ck_articulos_importe_no_acumula", StatusCodes.Status400BadRequest, "importe_no_acumula")]
    [InlineData("23514", "ck_articulos_tecla_atajo_solo_importe", StatusCodes.Status400BadRequest, "tecla_atajo_solo_importe")]
    [InlineData("23514", "ck_articulos_tecla_atajo", StatusCodes.Status400BadRequest, "tecla_atajo_invalida")]
    public async Task CadaRespaldoDelArticuloPorImporteSeTraduceASuCodigoDeDominio(
        string sqlState, string constraint, int estadoEsperado, string codigoEsperado)
    {
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("respaldo", CrearExcepcion(sqlState, constraint)));
        var (estadoPelado, codigoPelado) = await ManejarAsync(CrearExcepcion(sqlState, constraint));

        Assert.Equal((estadoEsperado, codigoEsperado), (estado, codigo));
        Assert.Equal((estadoEsperado, codigoEsperado), (estadoPelado, codigoPelado));
    }
}
