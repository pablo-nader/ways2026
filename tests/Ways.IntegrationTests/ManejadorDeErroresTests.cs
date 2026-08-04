using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// db-error-backstops (judgment-day ronda 2, item 4b, triage Judge B): coverage gap del arco
/// <c>DbUpdateConcurrencyException</c> → 409 <c>edicion_concurrente</c> de
/// <see cref="ManejadorDeErrores"/> — ninguna prueba existente lo ejercita, porque a diferencia de
/// las demás ramas del switch (23505/23503/23514 con <c>InnerException PostgresException</c>) esta
/// excepción no depende de Postgres para construirse: alcanza con instanciarla "a mano". No hay
/// una clase de test dedicada a <see cref="ManejadorDeErrores"/> todavía (las demás ramas se
/// prueban indirectamente, forzando la excepción real vía los distintos *BackstopTests) — vive acá
/// unit-style, sin <c>WaysApiFixture</c> ni Postgres, porque <see cref="ManejadorDeErrores"/> vive
/// en <c>Ways.Api</c> y este es el único proyecto de test con referencia a ese ensamblado.
/// </summary>
public class ManejadorDeErroresTests
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

    [Fact]
    public async Task UnaDbUpdateConcurrencyExceptionSeTraduceA409EdicionConcurrente()
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contexto, new DbUpdateConcurrencyException(), CancellationToken.None);

        Assert.True(manejado);
        Assert.Equal(StatusCodes.Status409Conflict, contexto.Response.StatusCode);

        Assert.NotNull(servicioDeProblemDetails.Ultimo);
        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        Assert.Equal(StatusCodes.Status409Conflict, problema.Status);
        Assert.Equal("edicion_concurrente", problema.Extensions["codigo"]);
    }
}
