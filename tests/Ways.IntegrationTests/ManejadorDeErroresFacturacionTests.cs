using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T3 (db-error-backstops): traducción de los tres índices únicos nuevos.
/// Mismo patrón que <see cref="ManejadorDeErroresFiscalTests"/>: la <see cref="PostgresException"/> se
/// construye a mano porque todavía no hay endpoint que escriba estas filas (lo hacen las tareas siguientes).
/// </summary>
public class ManejadorDeErroresFacturacionTests
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

    [Theory]
    [InlineData("ux_comprobantes_venta_venta_origen", "venta_ya_facturada")]
    [InlineData("ux_facturaciones_de_venta_comprobante", "facturacion_de_venta_duplicada")]
    [InlineData("ux_facturaciones_de_venta_comprobante_fiscal", "factura_ya_asignada")]
    [InlineData("ux_facturaciones_de_venta_origen", "nota_de_credito_duplicada")]
    [InlineData("ux_comprobantes_venta_nota_credito_espejo", "nota_de_credito_duplicada")]
    public async Task LosIndicesUnicosDeLaFacturacionSeTraducenA409ConSuCodigoPropio(string indice, string codigoEsperado)
    {
        var postgres = CrearExcepcion("23505", indice);

        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal(codigoEsperado, codigo);
    }

    [Fact]
    public async Task ElCaminoCrudoSinDbUpdateExceptionTambienSeTraduce()
    {
        var (estado, codigo) = await ManejarAsync(CrearExcepcion("23505", "ux_comprobantes_venta_venta_origen"));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("venta_ya_facturada", codigo);
    }
}
