using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class ServicioDeAvisosDeMercadoPagoTests
{
    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    }

    // En las rutas que cortan antes de la firma válida el servicio no toca ni los alcances ni la base: se pasan nulos a propósito.
    private static ServicioDeAvisosDeMercadoPago Crear(string? secreto) =>
        new(null!, null!, Options.Create(new OpcionesDeMercadoPago { SecretoWebhook = secreto }), new RelojFijo(),
            NullLogger<ServicioDeAvisosDeMercadoPago>.Instance);

    // Clausula: el cuerpo no se lee antes de validar la firma; una firma invalida (aunque el cuerpo exista) nunca invoca al lector.
    [Theory]
    [InlineData("ts=1,v1=00")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UnaFirmaInvalidaNuncaLeeElCuerpo(string? encabezado)
    {
        var lecturas = 0;
        var aviso = new AvisoDeMercadoPago(encabezado, "req-1", "ORD1", null, _ =>
        {
            lecturas++;
            return Task.FromResult<string?>("order");
        });

        var resultado = await Crear("secreto-de-prueba").ProcesarAsync(aviso);

        Assert.Equal(ResultadoDelAviso.FirmaInvalida, resultado);
        Assert.Equal(0, lecturas);
    }

    [Fact]
    public async Task SinSecretoConfiguradoNuncaLeeElCuerpo()
    {
        var lecturas = 0;
        var aviso = new AvisoDeMercadoPago("ts=1,v1=00", "req-1", "ORD1", null, _ =>
        {
            lecturas++;
            return Task.FromResult<string?>("order");
        });

        var resultado = await Crear(null).ProcesarAsync(aviso);

        Assert.Equal(ResultadoDelAviso.NoConfigurado, resultado);
        Assert.Equal(0, lecturas);
    }
}
