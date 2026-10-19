using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Npgsql;
using Ways.Api.ConectorMcp;
using Ways.Domain.Common;

namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync"/> sin base ni host: cada brazo del runner
/// común de las herramientas del conector, con la excepción construida a mano.
/// </summary>
public class ConectorMcpResultadoDeHerramientaTests
{
    private static readonly ILogger Log = NullLogger.Instance;

    private static string Texto(CallToolResult resultado) =>
        Assert.IsType<TextContentBlock>(Assert.Single(resultado.Content)).Text;

    [Fact]
    public async Task UnTextoVuelveComoResultadoSinError()
    {
        var resultado = await Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync(() => Task.FromResult("listo"), Log);

        Assert.False(resultado.IsError);
        Assert.Equal("listo", Texto(resultado));
    }

    [Fact]
    public async Task UnErrorDeHerramientaVuelveConIsErrorYSuMensaje()
    {
        var resultado = await Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync(
            () => throw new ErrorDeHerramienta("No existe el artículo 7."), Log);

        Assert.True(resultado.IsError);
        Assert.Equal("No existe el artículo 7.", Texto(resultado));
    }

    /// <summary>Cláusula bajo prueba: el brazo de <see cref="ErrorDominio"/>. Sin él la excepción escapa y el SDK la
    /// reemplaza por su mensaje genérico.</summary>
    [Fact]
    public async Task UnErrorDominioVuelveConIsErrorYSuMensaje()
    {
        var resultado = await Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync(
            () => throw new ErrorDominio("periodo_invalido", "El período 'ayerr' no existe.", 400), Log);

        Assert.True(resultado.IsError);
        Assert.Equal("El período 'ayerr' no existe.", Texto(resultado));
    }

    /// <summary>Cláusula bajo prueba: el brazo de <see cref="DbUpdateException"/> con la clasificación de la API. El
    /// texto es el título que daría el 409 del endpoint, nunca el mensaje de EF ni el de Postgres.</summary>
    [Fact]
    public async Task UnaViolacionUnicaClasificadaVuelveConIsErrorYElTituloDeLaApi()
    {
        var postgres = new PostgresException(
            "mensaje de postgres", "ERROR", "ERROR", "23505", constraintName: "ux_codigos_barra_codigo_tenant");

        var resultado = await Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync(
            () => throw new DbUpdateException("mensaje de EF", postgres), Log);

        Assert.True(resultado.IsError);
        Assert.Equal("Ya existe ese código de barras en este tenant.", Texto(resultado));
    }

    /// <summary>Lo que la API no clasifica tampoco lo clasifica el runner: sigue siendo un error inesperado.</summary>
    [Fact]
    public async Task UnDbUpdateExceptionSinClasificarSigueEscapando()
    {
        var postgres = new PostgresException("mensaje de postgres", "ERROR", "ERROR", "XX000");

        await Assert.ThrowsAsync<DbUpdateException>(() => Ways.Api.ConectorMcp.ResultadoDeHerramienta.EjecutarAsync(
            () => throw new DbUpdateException("mensaje de EF", postgres), Log));
    }
}
