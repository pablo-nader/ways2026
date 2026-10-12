using Ways.Application.Plantel;
using Ways.Domain.Plantel;

namespace Ways.Application.Tests.Plantel;

/// <summary>
/// La forma por tipo se valida antes de tocar la conexión, así que estas pruebas no necesitan base:
/// una conexión nula alcanza para probar que el escritor se niega.
/// </summary>
public class EscriturasDePersonalTests
{
    private static Task InsertarAsync(TipoMovimientoPersonal tipo, decimal? horas, decimal? valorHora) =>
        EscriturasDePersonal.InsertarMovimientoAsync(
            null!, null, idTenant: 1, idPersonal: 1, idEmpleado: 1, new DateOnly(2026, 10, 1), tipo, horas, valorHora,
            importe: 10m, saldoResultante: 10m, detalle: null, creadoEn: DateTimeOffset.UtcNow, CancellationToken.None);

    [Theory]
    [InlineData(null, 100.0)]
    [InlineData(8.0, null)]
    public async Task UnaCargaDeHorasSinHorasOSinValorHoraSeRechazaAntesDeEscribir(double? horas, double? valorHora)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InsertarAsync(TipoMovimientoPersonal.Horas, (decimal?)horas, (decimal?)valorHora));

        Assert.Contains("requiere horas y valor_hora", error.Message);
    }

    [Theory]
    [InlineData(TipoMovimientoPersonal.Ajuste, 8.0, null)]
    [InlineData(TipoMovimientoPersonal.Ajuste, null, 100.0)]
    [InlineData(TipoMovimientoPersonal.Pago, 8.0, 100.0)]
    public async Task UnAjusteOUnPagoConHorasOValorHoraSeRechazaAntesDeEscribir(
        TipoMovimientoPersonal tipo, double? horas, double? valorHora)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InsertarAsync(tipo, (decimal?)horas, (decimal?)valorHora));

        Assert.Contains("no lleva horas ni valor_hora", error.Message);
    }
}
