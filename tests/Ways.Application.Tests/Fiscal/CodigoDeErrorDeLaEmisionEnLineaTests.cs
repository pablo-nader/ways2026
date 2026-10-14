using Ways.Application.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// El error que la respuesta de la venta informa de su facturación es solo un código: el texto anotado puede traer mensajes de
/// excepciones y de ARCA que no son para el cajero.
/// </summary>
public class CodigoDeErrorDeLaEmisionEnLineaTests
{
    [Theory]
    [InlineData("arca_no_definitivo: HTTP 500 en https://servidor.interno/wsfe", "arca_no_definitivo")]
    [InlineData("serie_bloqueada: el espejo 42 sigue pendiente", "serie_bloqueada")]
    [InlineData("compuerta_de_prueba", "compuerta_de_prueba")]
    [InlineData("  serie_ocupada  : otra llamada está usando la serie.", "serie_ocupada")]
    [InlineData("10015: Rechazado por el receptor.; 10016: Otro error", "10015")]
    [InlineData("error_inesperado", "error_inesperado")]
    public void UnTextoQueEmpiezaConUnCodigoSeReducePorSoloAEseCodigo(string anotado, string esperado)
    {
        Assert.Equal(esperado, EmisionEnLineaAlCobrar.CodigoDeError(anotado));
    }

    [Theory]
    [InlineData("HttpRequestException: Connection refused (10.0.0.5:443)")]
    [InlineData("NpgsqlException: password authentication failed for user \"ways\"")]
    [InlineData("InvalidOperationException: El certificado no tiene clave privada.")]
    [InlineData("ARCA rechazó el comprobante.")]
    [InlineData("texto libre sin dos puntos")]
    [InlineData("Texto: con mayúscula inicial")]
    [InlineData(": sin código")]
    [InlineData("con espacio: interno")]
    public void CualquierOtroTextoSeInformaComoErrorInesperado(string anotado)
    {
        Assert.Equal(EmisionEnLineaAlCobrar.ErrorInesperado, EmisionEnLineaAlCobrar.CodigoDeError(anotado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinErrorAnotadoNoHayCodigo(string? anotado)
    {
        Assert.Null(EmisionEnLineaAlCobrar.CodigoDeError(anotado));
    }
}
