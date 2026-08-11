using Ways.Application.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1a (spec exportacion-de-reportes: "XLSX Response Contract And Deterministic
/// Naming") — mismos parámetros ⇒ mismo nombre, siempre, y el nombre es ASCII por construcción.
/// </summary>
public class NombreDeArchivoTests
{
    [Fact]
    public void DosLlamadosConLosMismosParametrosProducenElMismoNombre()
    {
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 12);

        var primero = NombreDeArchivo.Construir("ventas_resumen", "pv3", desde, hasta);
        var segundo = NombreDeArchivo.Construir("ventas_resumen", "pv3", desde, hasta);

        Assert.Equal(primero, segundo);
    }

    [Fact]
    public void ElNombreEsAsciiPuro()
    {
        var nombre = NombreDeArchivo.Construir(
            "ventas_resumen", "pv3", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 12));

        Assert.All(nombre, caracter => Assert.True(caracter <= 127));
    }

    [Fact]
    public void ElEjemploDelSpecProduceElNombreEsperado()
    {
        var nombre = NombreDeArchivo.Construir(
            "ventas_resumen", "pv3", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 12));

        Assert.Equal("ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx", nombre);
    }
}
