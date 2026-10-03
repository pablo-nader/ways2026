using Ways.Application.Exportacion;
using Ways.Application.Reportes;

namespace Ways.Application.Tests.Reportes;

/// <summary>Forma del XLSX del libro IVA: columnas fijas por alícuota, "otras alícuotas" que no
/// pierde ningún importe y fila de totales. Cada celda de cada fila se compara contra su fuente.</summary>
public class ExportacionDeLibroIvaTests
{
    private static readonly ContextoDeExportacion Contexto = new(
        "1", null, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), "N/A", "admin@ways.test",
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), null);

    private static FilaDeLibroIva Fila(
        string numero, DateOnly fecha, IReadOnlyList<AlicuotaDeLibroIva> alicuotas, decimal noGravado, decimal exento,
        decimal percepcionIva, decimal percepcionIibb, decimal total, decimal diferencia,
        params string[] advertencias) =>
        new(fecha, "C-FA", numero, $"Proveedor {numero}", $"CUIT-{numero}", alicuotas, noGravado, exento, percepcionIva,
            percepcionIibb, total, diferencia, advertencias);

    private static LibroIva LibroDeDosFilas()
    {
        var filas = new[]
        {
            Fila(
                "A-1", new DateOnly(2026, 5, 3),
                [new AlicuotaDeLibroIva(21m, 1000m, 210m), new AlicuotaDeLibroIva(0m, 80m, 0m)], 30m, 50m, 15m, 7m,
                1392m, 0m, AdvertenciasDeLibroIva.AnuladoSinNotaDeCredito, AdvertenciasDeLibroIva.SinNumeroFiscal),
            Fila(
                "A-2", new DateOnly(2026, 5, 4),
                [new AlicuotaDeLibroIva(10.5m, 200m, 21m), new AlicuotaDeLibroIva(2.5m, 40m, 1m)], 11m, 13m, 3m, 5m,
                300m, 6m)
        };

        var totales = new TotalesDeLibroIva(
            [
                new AlicuotaDeLibroIva(21m, 1000m, 210m), new AlicuotaDeLibroIva(10.5m, 200m, 21m),
                new AlicuotaDeLibroIva(2.5m, 40m, 1m), new AlicuotaDeLibroIva(0m, 80m, 0m)
            ],
            41m, 63m, 18m, 12m, 1692m, 6m);

        return new LibroIva(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), null, null, filas, totales);
    }

    [Fact]
    public void ElEncabezadoDeComprasTieneLasCincoAlicuotasFijasMasOtrasYLasPercepciones()
    {
        var tabla = ExportacionDeLibroIva.DeCompras(LibroDeDosFilas(), Contexto);

        Assert.Equal(
            [
                "Fecha", "Tipo", "Número", "Proveedor", "CUIT",
                "Neto 21%", "IVA 21%", "Neto 10,5%", "IVA 10,5%", "Neto 27%", "IVA 27%", "Neto 5%", "IVA 5%",
                "Neto 2,5%", "IVA 2,5%", "Neto otras alícuotas", "IVA otras alícuotas", "No gravado", "Exento",
                "Percepción IVA", "Percepción IIBB", "Total", "Diferencia", "Observaciones"
            ],
            tabla.Columnas.Select(c => c.Titulo));
    }

    [Fact]
    public void ElEncabezadoDeVentasNoTienePercepcionesNiProveedor()
    {
        var tabla = ExportacionDeLibroIva.DeVentas(LibroDeDosFilas(), Contexto);

        Assert.Equal(["Fecha", "Tipo", "Número", "Cliente", "Documento"], tabla.Columnas.Take(5).Select(c => c.Titulo));
        Assert.Equal(
            ["No gravado", "Exento", "Total", "Diferencia", "Observaciones"], tabla.Columnas.TakeLast(5).Select(c => c.Titulo));
        Assert.DoesNotContain(tabla.Columnas, c => c.Titulo.StartsWith("Percepción"));
    }

    [Fact]
    public void CadaCeldaDeCadaFilaDeComprasCoincideConSuFuenteYLosTotalesCierranLaHoja()
    {
        var tabla = ExportacionDeLibroIva.DeCompras(LibroDeDosFilas(), Contexto);

        Assert.Equal(3, tabla.Filas.Count);

        // Fila 1: 21% neto/IVA, 0% real en "otras", sin 10,5/27/5/2,5.
        Assert.Equal(new DateOnly(2026, 5, 3), tabla.Filas[0][0].Valor);
        Assert.Equal("C-FA", tabla.Filas[0][1].Valor);
        Assert.Equal("A-1", tabla.Filas[0][2].Valor);
        Assert.Equal("Proveedor A-1", tabla.Filas[0][3].Valor);
        Assert.Equal("CUIT-A-1", tabla.Filas[0][4].Valor);
        Assert.Equal(
            new object[]
            {
                1000m, 210m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 80m, 0m, 30m, 50m, 15m, 7m, 1392m, 0m,
                "Anulado sin NC; Sin número fiscal de PV"
            },
            tabla.Filas[0].Skip(5).Select(c => c.Valor).ToArray());

        // Fila 2: 10,5% y 2,5% en sus columnas fijas.
        Assert.Equal(new DateOnly(2026, 5, 4), tabla.Filas[1][0].Valor);
        Assert.Equal("A-2", tabla.Filas[1][2].Valor);
        Assert.Equal(
            new object[] { 0m, 0m, 200m, 21m, 0m, 0m, 0m, 0m, 40m, 1m, 0m, 0m, 11m, 13m, 3m, 5m, 300m, 6m, "" },
            tabla.Filas[1].Skip(5).Select(c => c.Valor).ToArray());

        // Totales: etiqueta en "Tipo", sin fecha, con las alícuotas fijas y las otras sumadas.
        var total = tabla.Filas[2];
        Assert.Null(total[0].Valor);
        Assert.Equal("Total", total[1].Valor);
        Assert.Equal(
            new object[] { 1000m, 210m, 200m, 21m, 0m, 0m, 0m, 0m, 40m, 1m, 80m, 0m, 41m, 63m, 18m, 12m, 1692m, 6m, "" },
            total.Skip(5).Select(c => c.Valor).ToArray());
    }

    [Fact]
    public void ElExportDeVentasOmiteLasPercepcionesPeroConservaElResto()
    {
        var tabla = ExportacionDeLibroIva.DeVentas(LibroDeDosFilas(), Contexto);

        Assert.Equal(
            new object[]
            {
                1000m, 210m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 80m, 0m, 30m, 50m, 1392m, 0m,
                "Anulado sin NC; Sin número fiscal de PV"
            },
            tabla.Filas[0].Skip(5).Select(c => c.Valor).ToArray());
        Assert.Equal("Total", tabla.Filas[2][1].Valor);
    }
}
