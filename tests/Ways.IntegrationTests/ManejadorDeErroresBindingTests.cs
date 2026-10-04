using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Los dos brazos de <c>ManejadorDeErrores</c> que traducen el rechazo del binding de un endpoint
/// (<see cref="BadHttpRequestException"/>): el cuerpo que no deserializa —el framework envuelve la
/// <see cref="JsonException"/>— sale como 400 <c>cuerpo_invalido</c>, y cualquier otro rechazo conserva
/// el estado que eligió el framework y sale como <c>solicitud_invalida</c>. En los dos el título es fijo:
/// el mensaje de la excepción nombra tipos y parámetros internos y no llega al cliente.
///
/// <para>Mismo patrón unit-style que <see cref="ManejadorDeErroresResultadoInciertoTests"/>: las
/// excepciones se construyen a mano. Que el framework tire de verdad estas excepciones ante una solicitud
/// mal formada, también fuera de Development, lo prueba <see cref="SolicitudesMalFormadasTests"/>.</para>
/// </summary>
public class ManejadorDeErroresBindingTests
{
    private const string CopiaDeCuerpoInvalido = "Los datos enviados no tienen el formato esperado.";
    private const string CopiaDeSolicitudInvalida = "La solicitud no tiene el formato esperado.";

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

    private static async Task<(int Estado, string? Codigo, string? Titulo, string? Detalle)> ManejarAsync(
        Exception excepcion)
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();
        contexto.Request.Method = "POST";

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string, problema.Title, problema.Detail);
    }

    [Fact]
    public async Task UnCuerpoQueNoDeserializaEsCuerpoInvalidoSinElMensajeDelFramework()
    {
        var excepcion = new BadHttpRequestException(
            "Failed to read parameter \"SolicitudDeLogin solicitud\" from the request body as JSON.",
            new JsonException("The JSON value could not be converted to System.Boolean. Path: $.solicitarBearer"));

        var (estado, codigo, titulo, detalle) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cuerpo_invalido", codigo);
        Assert.Equal(CopiaDeCuerpoInvalido, titulo);
        Assert.Null(detalle);
    }

    /// <summary>Conserva el estado que eligió el framework en vez de fijarlo en 400. El 415 es el que el
    /// framework usa para un content type que no es JSON; en esta API ese caso no llega al manejador —el
    /// ruteo descarta el endpoint y responde el fallback de <c>/api</c> con 404—, pero es el único valor que
    /// distingue conservar el estado de fijarlo.</summary>
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType)]
    public async Task ElRestoDeLosRechazosDelBindingConservanSuEstadoYSonSolicitudInvalida(int estadoDelFramework)
    {
        var (estado, codigo, titulo, detalle) = await ManejarAsync(
            new BadHttpRequestException("Failed to bind parameter \"int pagina\" from \"dos\".", estadoDelFramework));

        Assert.Equal(estadoDelFramework, estado);
        Assert.Equal("solicitud_invalida", codigo);
        Assert.Equal(CopiaDeSolicitudInvalida, titulo);
        Assert.Null(detalle);
    }

    /// <summary>El brazo de <c>cuerpo_invalido</c> mira la envoltura del binding, no el tipo de la causa. Una
    /// <see cref="JsonException"/> suelta es un error del servidor —por ejemplo, un parámetro guardado que
    /// no deserializa—, no del cliente, y sigue siendo un error interno.</summary>
    [Fact]
    public async Task UnaJsonExceptionQueNoVieneDelBindingSigueSiendoErrorInterno()
    {
        var (estado, codigo, _, _) = await ManejarAsync(
            new JsonException("'q' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."));

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
        Assert.Equal("error_interno", codigo);
    }
}
