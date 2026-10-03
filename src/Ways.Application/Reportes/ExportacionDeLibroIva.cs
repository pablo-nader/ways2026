using System.Globalization;
using Ways.Application.Exportacion;

namespace Ways.Application.Reportes;

/// <summary>
/// Mapper puro de <see cref="LibroIva"/> a <see cref="TablaExportable"/>: sin consultas, mismas
/// cifras que la respuesta JSON. Las columnas de alícuota son fijas (las cinco que existen en
/// AFIP, siempre presentes aunque estén en cero) más un par "otras alícuotas" que absorbe
/// cualquier otro porcentaje, así ningún importe queda afuera y la forma del archivo no cambia de
/// un período a otro.
/// </summary>
public static class ExportacionDeLibroIva
{
    public static readonly IReadOnlyList<decimal> AlicuotasFijas = [21m, 10.5m, 27m, 5m, 2.5m];

    public static TablaExportable DeCompras(LibroIva libro, ContextoDeExportacion ctx) =>
        Armar("Libro IVA compras", libro, ctx, "Proveedor", "CUIT", conPercepciones: true);

    public static TablaExportable DeVentas(LibroIva libro, ContextoDeExportacion ctx) =>
        Armar("Libro IVA ventas", libro, ctx, "Cliente", "Documento", conPercepciones: false);

    private static TablaExportable Armar(
        string hoja, LibroIva libro, ContextoDeExportacion ctx, string tituloContraparte, string tituloDocumento,
        bool conPercepciones)
    {
        var columnas = new List<ColumnaExportable>
        {
            new("Fecha", TipoDeColumna.Fecha),
            new("Tipo", TipoDeColumna.Texto),
            new("Número", TipoDeColumna.Texto),
            new(tituloContraparte, TipoDeColumna.Texto),
            new(tituloDocumento, TipoDeColumna.Texto)
        };

        foreach (var porcentaje in AlicuotasFijas)
        {
            columnas.Add(new ColumnaExportable($"Neto {Etiqueta(porcentaje)}", TipoDeColumna.Moneda));
            columnas.Add(new ColumnaExportable($"IVA {Etiqueta(porcentaje)}", TipoDeColumna.Moneda));
        }

        columnas.Add(new ColumnaExportable("Neto otras alícuotas", TipoDeColumna.Moneda));
        columnas.Add(new ColumnaExportable("IVA otras alícuotas", TipoDeColumna.Moneda));
        columnas.Add(new ColumnaExportable("No gravado", TipoDeColumna.Moneda));
        columnas.Add(new ColumnaExportable("Exento", TipoDeColumna.Moneda));

        if (conPercepciones)
        {
            columnas.Add(new ColumnaExportable("Percepción IVA", TipoDeColumna.Moneda));
            columnas.Add(new ColumnaExportable("Percepción IIBB", TipoDeColumna.Moneda));
        }

        columnas.Add(new ColumnaExportable("Total", TipoDeColumna.Moneda));
        columnas.Add(new ColumnaExportable("Diferencia", TipoDeColumna.Moneda));

        var filas = libro.Filas
            .Select(f => (IReadOnlyList<Celda>)Celdas(
                Celda.Fecha(f.Fecha), Celda.Texto(f.TipoComprobante), Celda.Texto(f.Numero),
                Celda.Texto(f.Contraparte), Celda.Texto(f.Documento), f.Alicuotas, f.NoGravado, f.Exento,
                f.PercepcionIva, f.PercepcionIibb, f.Total, f.Diferencia, conPercepciones))
            .ToList();

        var t = libro.Totales;
        filas.Add(Celdas(
            Celda.Fecha(null), Celda.Texto("Total"), Celda.Texto(null), Celda.Texto(null), Celda.Texto(null),
            t.PorAlicuota, t.NoGravado, t.Exento, t.PercepcionIva, t.PercepcionIibb, t.Total, t.Diferencia,
            conPercepciones));

        return new TablaExportable(hoja, ctx, columnas, filas);
    }

    private static List<Celda> Celdas(
        Celda fecha, Celda tipo, Celda numero, Celda contraparte, Celda documento,
        IReadOnlyList<AlicuotaDeLibroIva> alicuotas, decimal noGravado, decimal exento, decimal percepcionIva,
        decimal percepcionIibb, decimal total, decimal diferencia, bool conPercepciones)
    {
        var celdas = new List<Celda> { fecha, tipo, numero, contraparte, documento };

        foreach (var porcentaje in AlicuotasFijas)
        {
            var alicuota = alicuotas.FirstOrDefault(a => a.Porcentaje == porcentaje);
            celdas.Add(Celda.Moneda(alicuota?.Neto ?? 0m));
            celdas.Add(Celda.Moneda(alicuota?.Iva ?? 0m));
        }

        var otras = alicuotas.Where(a => !AlicuotasFijas.Contains(a.Porcentaje)).ToList();
        celdas.Add(Celda.Moneda(otras.Sum(a => a.Neto)));
        celdas.Add(Celda.Moneda(otras.Sum(a => a.Iva)));
        celdas.Add(Celda.Moneda(noGravado));
        celdas.Add(Celda.Moneda(exento));

        if (conPercepciones)
        {
            celdas.Add(Celda.Moneda(percepcionIva));
            celdas.Add(Celda.Moneda(percepcionIibb));
        }

        celdas.Add(Celda.Moneda(total));
        celdas.Add(Celda.Moneda(diferencia));
        return celdas;
    }

    private static string Etiqueta(decimal porcentaje) =>
        porcentaje.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + "%";
}
