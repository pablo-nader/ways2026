using Ways.Application.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11, Slice 1a (design decisión 2; spec exportacion-de-reportes) — el constructor de
/// <see cref="TablaExportable"/> es la única barrera entre "un mapper puso un string en una
/// columna de plata" y una celda silenciosa en un archivo que nadie vuelve a abrir. Cada test
/// prueba una cláusula puntual del constructor: la mutación es borrar la comparación
/// correspondiente y ver que el test que la nombra empieza a fallar.
/// </summary>
public class TablaExportableTests
{
    private static readonly ContextoDeExportacion ContextoDePrueba = new(
        Empresa: "Empresa de prueba",
        PuntoVenta: "PV 1",
        Desde: new DateOnly(2026, 8, 1),
        Hasta: new DateOnly(2026, 8, 12),
        ZonaHoraria: "America/Argentina/Buenos_Aires",
        Usuario: "usuario-de-prueba",
        GeneradoEl: new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(-3)),
        Cobertura: null);

    private static readonly IReadOnlyList<ColumnaExportable> DosColumnas =
    [
        new ColumnaExportable("Período", TipoDeColumna.Texto),
        new ColumnaExportable("Neto", TipoDeColumna.Moneda)
    ];

    [Fact]
    public void UnaFilaConMenosCeldasQueColumnasLanza()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas = [[Celda.Texto("2026-08")]];

        var excepcion = Assert.Throws<ArgumentException>(() =>
            new TablaExportable("Hoja", ContextoDePrueba, DosColumnas, filas));

        Assert.Contains("1 celda", excepcion.Message);
    }

    [Fact]
    public void UnaFilaConMasCeldasQueColumnasLanza()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(1m), Celda.Moneda(2m)]];

        Assert.Throws<ArgumentException>(() =>
            new TablaExportable("Hoja", ContextoDePrueba, DosColumnas, filas));
    }

    [Fact]
    public void UnaCeldaDeTextoEnUnaColumnaDeMonedaLanza()
    {
        // Prueba puntual: "el mapper puso un string en la columna de plata" tiene que ser un
        // ArgumentException del constructor, no una celda de texto silenciosa en el workbook.
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Texto("setecientos")]];

        var excepcion = Assert.Throws<ArgumentException>(() =>
            new TablaExportable("Hoja", ContextoDePrueba, DosColumnas, filas));

        Assert.Contains("Moneda", excepcion.Message);
    }

    [Fact]
    public void UnaFilaConTiposCorrectosNoLanzaYPreservaElOrden()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(700m)]];

        var tabla = new TablaExportable("Hoja", ContextoDePrueba, DosColumnas, filas);

        Assert.Single(tabla.Filas);
        Assert.Equal(700m, tabla.Filas[0][1].Valor);
    }

    [Fact]
    public void UnValorNuloSePreservaComoNuloNuncaComoCeroNiGuion()
    {
        IReadOnlyList<IReadOnlyList<Celda>> filas =
            [[Celda.Texto("2026-08"), Celda.Moneda(null)]];

        var tabla = new TablaExportable("Hoja", ContextoDePrueba, DosColumnas, filas);

        Assert.Null(tabla.Filas[0][1].Valor);
    }
}
