using System.Text.Json;
using Ways.Application.Catalogos;

namespace Ways.Application.Tests.Catalogos;

public class MedioPagoAltaJsonTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private const string Base =
        "\"nombre\":\"QR\",\"idEmpresa\":null,\"orden\":1,\"comportamiento\":\"Electronico\"," +
        "\"admiteVuelto\":false,\"requiereReferencia\":false,\"recargoPorcentaje\":null";

    private static MedioPagoAlta Leer(string extra) =>
        JsonSerializer.Deserialize<MedioPagoAlta>("{" + Base + extra + "}", Opciones)!;

    [Fact]
    public void SinLaClaveLaCuentaQuedaAusente()
    {
        var alta = Leer(string.Empty);

        Assert.False(alta.IdCuentaTesoreria.Presente);
        Assert.Null(alta.ComisionPorcentaje);
    }

    [Fact]
    public void ConLaClaveEnNullLaCuentaEstaPresenteYVacia()
    {
        var alta = Leer(",\"idCuentaTesoreria\":null");

        Assert.True(alta.IdCuentaTesoreria.Presente);
        Assert.Null(alta.IdCuentaTesoreria.Valor);
    }

    [Fact]
    public void ConUnIdLaCuentaEstaPresenteConSuValor()
    {
        var alta = Leer(",\"idCuentaTesoreria\":12,\"comisionPorcentaje\":0,\"retencionIvaPorcentaje\":2.5");

        Assert.True(alta.IdCuentaTesoreria.Presente);
        Assert.Equal(12, alta.IdCuentaTesoreria.Valor);
        Assert.Equal(0m, alta.ComisionPorcentaje);
        Assert.Equal(2.5m, alta.RetencionIvaPorcentaje);
        Assert.Null(alta.RetencionIibbPorcentaje);
    }

    [Fact]
    public void AlSerializarLaCuentaAusenteNoSeEscribeYLaPresenteSi()
    {
        var sin = new MedioPagoAlta("QR", null, 1, Ways.Domain.Catalogos.ComportamientoMedioPago.Electronico, false, false, null);
        var enNull = sin with { IdCuentaTesoreria = CampoOpcional<int?>.De(null) };
        var conId = sin with { IdCuentaTesoreria = CampoOpcional<int?>.De(4) };

        Assert.DoesNotContain("idCuentaTesoreria", JsonSerializer.Serialize(sin, Opciones));
        Assert.Contains("\"idCuentaTesoreria\":null", JsonSerializer.Serialize(enNull, Opciones));
        Assert.Contains("\"idCuentaTesoreria\":4", JsonSerializer.Serialize(conId, Opciones));
    }
}
