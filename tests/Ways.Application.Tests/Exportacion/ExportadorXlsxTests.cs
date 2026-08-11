using ClosedXML.Excel;
using Ways.Application.Exportacion;
using Ways.Infrastructure.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1a (design decisión 1; spec exportacion-de-reportes: "In-Sheet Header Block")
/// — prueba de ida y vuelta: genera un workbook chico con <see cref="ExportadorXlsx"/> y lo
/// vuelve a leer con la misma librería para probar que la plata quedó como número con formato
/// (no como texto), la fecha quedó como fecha, y el texto quedó como texto. Es la prueba que un
/// golden-file byte-a-byte no puede dar: prueba tipos, no bytes.
/// </summary>
public class ExportadorXlsxTests
{
    private static readonly ContextoDeExportacion ContextoDePrueba = new(
        Empresa: "Empresa de prueba",
        PuntoVenta: "PV 3",
        Desde: new DateOnly(2026, 8, 1),
        Hasta: new DateOnly(2026, 8, 12),
        ZonaHoraria: "America/Argentina/Buenos_Aires",
        Usuario: "usuario-de-prueba",
        GeneradoEl: new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(-3)),
        Cobertura: null);

    private static readonly IReadOnlyList<ColumnaExportable> Columnas =
    [
        new ColumnaExportable("Período", TipoDeColumna.Texto),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda),
        new ColumnaExportable("Fecha", TipoDeColumna.Fecha)
    ];

    private static XLWorkbook GenerarYReabrir(TablaExportable tabla)
    {
        var exportador = new ExportadorXlsx();
        var bytes = exportador.Generar(tabla);

        using var memoria = new MemoryStream(bytes);
        return new XLWorkbook(memoria);
    }

    [Fact]
    public void LaPlataQuedaComoNumeroConFormatoNuncaComoTexto()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(700m), Celda.Fecha(new DateOnly(2026, 8, 12))]];
        var tabla = new TablaExportable("Hoja", ContextoDePrueba, Columnas, filas);

        using var libro = GenerarYReabrir(tabla);
        var celdaDeNeto = libro.Worksheets.First().Cell(7, 2);

        Assert.True(celdaDeNeto.Value.IsNumber);
        Assert.Equal(700d, celdaDeNeto.Value.GetNumber());
        Assert.NotEqual("General", celdaDeNeto.Style.NumberFormat.Format);
    }

    [Fact]
    public void LaFechaQuedaComoFechaNuncaComoTexto()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(700m), Celda.Fecha(new DateOnly(2026, 8, 12))]];
        var tabla = new TablaExportable("Hoja", ContextoDePrueba, Columnas, filas);

        using var libro = GenerarYReabrir(tabla);
        var celdaDeFecha = libro.Worksheets.First().Cell(7, 3);

        Assert.True(celdaDeFecha.Value.IsDateTime);
        Assert.Equal(new DateTime(2026, 8, 12), celdaDeFecha.Value.GetDateTime());
    }

    [Fact]
    public void ElTextoQuedaComoTexto()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(700m), Celda.Fecha(new DateOnly(2026, 8, 12))]];
        var tabla = new TablaExportable("Hoja", ContextoDePrueba, Columnas, filas);

        using var libro = GenerarYReabrir(tabla);
        var celdaDePeriodo = libro.Worksheets.First().Cell(7, 1);

        Assert.True(celdaDePeriodo.Value.IsText);
        Assert.Equal("2026-08", celdaDePeriodo.Value.GetText());
    }

    [Fact]
    public void UnValorNuloQuedaComoCeldaVaciaNuncaComoCeroNiGuion()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(null), Celda.Fecha(new DateOnly(2026, 8, 12))]];
        var tabla = new TablaExportable("Hoja", ContextoDePrueba, Columnas, filas);

        using var libro = GenerarYReabrir(tabla);
        var celdaDeNeto = libro.Worksheets.First().Cell(7, 2);

        Assert.True(celdaDeNeto.Value.IsBlank);
    }

    [Fact]
    public void ElEncabezadoOcupaLasFilas1A4YLaTablaArrancaEnLaFila6()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas = [[Celda.Texto("2026-08"), Celda.Moneda(700m), Celda.Fecha(new DateOnly(2026, 8, 12))]];
        var tabla = new TablaExportable("Hoja", ContextoDePrueba, Columnas, filas);

        using var libro = GenerarYReabrir(tabla);
        var hoja = libro.Worksheets.First();

        Assert.Contains("Empresa de prueba", hoja.Cell(1, 1).GetString());
        Assert.Contains("PV 3", hoja.Cell(2, 1).GetString());
        Assert.True(hoja.Cell(5, 1).Value.IsBlank);
        Assert.Equal("Período", hoja.Cell(6, 1).GetString());
    }
}
