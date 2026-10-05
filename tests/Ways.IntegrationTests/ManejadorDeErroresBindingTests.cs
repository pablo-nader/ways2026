using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ways.Api.Seguridad;
using Ways.Domain.Common;

namespace Ways.IntegrationTests;

/// <summary>
/// Los dos brazos de <c>ManejadorDeErrores</c> que traducen el rechazo del binding de un endpoint
/// (<see cref="BadHttpRequestException"/>): el cuerpo que no deserializa —el framework envuelve la
/// <see cref="JsonException"/>— sale como 400 <c>cuerpo_invalido</c>, y cualquier otro rechazo conserva
/// el estado que eligió el framework y sale como <c>solicitud_invalida</c>. En los dos el título es fijo:
/// el mensaje de la excepción nombra tipos y parámetros internos, no llega al cliente y queda solo en el
/// log del servidor.
///
/// <para>Mismo patrón unit-style que <see cref="ManejadorDeErroresResultadoInciertoTests"/>: las
/// excepciones se construyen a mano. Que el framework tire de verdad estas excepciones ante una solicitud
/// mal formada, también fuera de Development, lo prueba <see cref="SolicitudesMalFormadasTests"/>.</para>
/// </summary>
public class ManejadorDeErroresBindingTests
{
    private const string CopiaDeCuerpoInvalido = "Los datos enviados no tienen el formato esperado.";
    private const string CopiaDeSolicitudInvalida = "La solicitud no es válida.";

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

    private sealed class LogCapturado : ILogger<ManejadorDeErrores>
    {
        public List<(LogLevel Nivel, Exception? Excepcion)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, exception));
    }

    private static async Task<(int Estado, string? Codigo, string? Titulo, string? Detalle)> ManejarAsync(
        Exception excepcion, ILogger<ManejadorDeErrores>? log = null)
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(
            servicioDeProblemDetails, log ?? NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();
        contexto.Request.Method = "POST";

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string, problema.Title, problema.Detail);
    }

    private static BadHttpRequestException RechazoDelBinding(bool conCausaJson) =>
        conCausaJson
            ? new BadHttpRequestException(
                "Failed to read parameter \"SolicitudDeLogin solicitud\" from the request body as JSON.",
                new JsonException("The JSON value could not be converted to System.Boolean. Path: $.solicitarBearer"))
            : new BadHttpRequestException("Failed to bind parameter \"int pagina\" from \"dos\".");

    [Fact]
    public async Task UnCuerpoQueNoDeserializaEsCuerpoInvalidoSinElMensajeDelFramework()
    {
        var (estado, codigo, titulo, detalle) = await ManejarAsync(RechazoDelBinding(conCausaJson: true));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cuerpo_invalido", codigo);
        Assert.Equal(CopiaDeCuerpoInvalido, titulo);
        Assert.Null(detalle);
    }

    /// <summary>Conserva el estado que eligió el framework en vez de fijarlo en 400. El 415 es el que el
    /// framework usa para un content type que no es JSON, y es el valor que distingue conservar el estado de
    /// fijarlo.</summary>
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

    /// <summary>La causa también decide el brazo: un rechazo del binding cuya causa no es una
    /// <see cref="JsonException"/> —como el de un cuerpo de formulario que no se puede leer— es
    /// <c>solicitud_invalida</c>, no <c>cuerpo_invalido</c>.</summary>
    [Fact]
    public async Task UnRechazoConUnaCausaQueNoEsJsonEsSolicitudInvalida()
    {
        var (estado, codigo, _, _) = await ManejarAsync(new BadHttpRequestException(
            "Failed to read parameter \"IFormFile archivo\" from the request body as form.",
            new InvalidDataException("Form value count limit 1024 exceeded.")));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("solicitud_invalida", codigo);
    }

    /// <summary>El brazo de <c>cuerpo_invalido</c> exige las dos cosas: la envoltura del binding y una
    /// <see cref="JsonException"/> como causa. Una <see cref="JsonException"/> suelta es un error del
    /// servidor —por ejemplo, un parámetro guardado que no deserializa—, no del cliente, y sigue siendo un
    /// error interno.</summary>
    [Fact]
    public async Task UnaJsonExceptionQueNoVieneDelBindingSigueSiendoErrorInterno()
    {
        var (estado, codigo, _, _) = await ManejarAsync(
            new JsonException("'q' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."));

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
        Assert.Equal("error_interno", codigo);
    }

    /// <summary>Lo que el título fijo le oculta al cliente queda en el log del servidor: una sola entrada de
    /// nivel Information con la excepción del framework, que nombra el parámetro y la ruta JSON.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ElRechazoDelBindingQuedaEnElLogConLaExcepcionDelFramework(bool conCausaJson)
    {
        var log = new LogCapturado();
        var rechazo = RechazoDelBinding(conCausaJson);

        await ManejarAsync(rechazo, log);

        var entrada = Assert.Single(log.Entradas);
        Assert.Equal(LogLevel.Information, entrada.Nivel);
        Assert.Same(rechazo, entrada.Excepcion);
    }

    /// <summary>El log es propio del rechazo del binding: un <see cref="ErrorDominio"/> 4xx sigue sin dejar
    /// entrada, como antes.</summary>
    [Fact]
    public async Task UnErrorDeDominioNoDejaEntradaEnElLog()
    {
        var log = new LogCapturado();

        var (estado, _, _, _) = await ManejarAsync(
            new ErrorDominio("alcance_invalido", "El alcance no es válido.", StatusCodes.Status400BadRequest), log);

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Empty(log.Entradas);
    }
}
