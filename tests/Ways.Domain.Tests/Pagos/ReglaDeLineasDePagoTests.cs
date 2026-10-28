using Ways.Domain.Common;
using Ways.Domain.Pagos;

namespace Ways.Domain.Tests.Pagos;

public class ReglaDeLineasDePagoTests
{
    private static LineaDePago Linea(decimal importe = 10m, int medio = 1, string? referencia = null) =>
        new(medio, importe, referencia);

    private static List<LineaDePago?> Lineas(int cantidad) => Enumerable.Range(1, cantidad).Select(i => (LineaDePago?)Linea(1m, i)).ToList();

    private static void AfirmarError(string codigo, Action accion)
    {
        var error = Assert.Throws<ErrorDominio>(accion);
        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void SinLineasEsUnError()
    {
        AfirmarError("pago_sin_lineas", () => ReglaDeLineasDePago.Planificar(null));
        AfirmarError("pago_sin_lineas", () => ReglaDeLineasDePago.Planificar([]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public void UnaALasVeinteLineasSeAceptan(int cantidad)
    {
        var plan = ReglaDeLineasDePago.Planificar(Lineas(cantidad));

        Assert.Equal(cantidad, plan.Lineas.Count);
        Assert.Equal(cantidad, plan.Total);
    }

    [Fact]
    public void VeintiunaLineasSeRechazan() =>
        AfirmarError("pago_demasiadas_lineas", () => ReglaDeLineasDePago.Planificar(Lineas(21)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10.005)]
    [InlineData(1000000000000)]
    public void UnImporteInvalidoSeRechaza(double importe) =>
        AfirmarError("pago_linea_importe_invalido", () => ReglaDeLineasDePago.Planificar([Linea((decimal)importe)]));

    [Fact]
    public void UnaLineaNulaSeRechazaComoImporteInvalido() =>
        AfirmarError("pago_linea_importe_invalido", () => ReglaDeLineasDePago.Planificar([null]));

    [Fact]
    public void ElMinimoYElMaximoDeImporteSeAceptan()
    {
        Assert.Equal(0.01m, ReglaDeLineasDePago.Planificar([Linea(0.01m)]).Total);
        Assert.Equal(999_999_999_999.99m, ReglaDeLineasDePago.Planificar([Linea(999_999_999_999.99m)]).Total);
    }

    [Fact]
    public void LaSumaQueSuperaElMaximoSeRechazaAunqueCadaLineaEntre() =>
        AfirmarError("importe_fuera_de_rango", () => ReglaDeLineasDePago.Planificar(
            [Linea(999_999_999_999m), Linea(999_999_999_999m)]));

    [Fact]
    public void LaReferenciaSeRecortaYVaciaQuedaNula()
    {
        var plan = ReglaDeLineasDePago.Planificar([Linea(referencia: "  cupón 1  "), Linea(referencia: "   "), Linea(referencia: "")]);

        Assert.Equal(["cupón 1", null, null], plan.Lineas.Select(l => l.Referencia));
    }

    [Fact]
    public void LaReferenciaDeCienCaracteresSeAceptaYLaDeCientoUnaNo()
    {
        var cien = new string('a', 100);

        Assert.Equal(cien, ReglaDeLineasDePago.Planificar([Linea(referencia: cien)]).Lineas[0].Referencia);
        Assert.Equal(cien, ReglaDeLineasDePago.Planificar([Linea(referencia: $"  {cien}  ")]).Lineas[0].Referencia);
        AfirmarError("pago_linea_referencia_muy_larga", () => ReglaDeLineasDePago.Planificar([Linea(referencia: cien + "a")]));
    }

    [Fact]
    public void UnMedioRepetidoSeAceptaYElTotalEsLaSuma()
    {
        var plan = ReglaDeLineasDePago.Planificar([Linea(10.25m, medio: 3), Linea(4.75m, medio: 3), Linea(5m, medio: 1)]);

        Assert.Equal(20m, plan.Total);
        Assert.Equal(3, plan.Lineas.Count);
    }

    [Fact]
    public void ElMedioDeLaCabeceraEsElDeLaPrimeraLinea()
    {
        var plan = ReglaDeLineasDePago.Planificar([Linea(medio: 7), Linea(medio: 2)]);

        Assert.Equal(7, plan.IdMedioPagoDeLaCabecera);
    }
}
