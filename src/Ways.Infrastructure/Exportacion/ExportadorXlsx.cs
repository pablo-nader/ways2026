using ClosedXML.Excel;
using Ways.Application.Exportacion;

namespace Ways.Infrastructure.Exportacion;

/// <summary>
/// Único archivo de <c>src/</c> que referencia ClosedXML — contenido por
/// <c>ContencionDelExportadorTests</c> (decisión 4 del design de la etapa 11). Escribe valores
/// numéricos/de fecha reales más un formato de número a nivel COLUMNA, nunca un string
/// pre-formateado por celda (decisión 1): una columna de importe que Excel no puede sumar porque
/// en realidad es texto es la falla exacta que este adaptador evita. También escribe el bloque de
/// encabezado en las filas 1-4, deja la fila 5 vacía y arranca la tabla en la fila 6.
/// </summary>
public sealed class ExportadorXlsx : IExportadorDeTabla
{
    private const int PrimeraFilaDeEncabezado = 1;
    private const int FilaDeTituloDeTabla = 6;

    public string TipoDeContenido =>
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] Generar(TablaExportable tabla)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(tabla.NombreDeHoja);

        EscribirEncabezado(hoja, tabla.Contexto);
        EscribirTabla(hoja, tabla);

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    private static void EscribirEncabezado(IXLWorksheet hoja, ContextoDeExportacion contexto)
    {
        var generadoPor =
            $"Generado el {contexto.GeneradoEl:yyyy-MM-dd HH:mm} ({contexto.ZonaHoraria}) por {contexto.Usuario}";

        hoja.Cell(PrimeraFilaDeEncabezado, 1).Value = $"Empresa: {contexto.Empresa}";
        hoja.Cell(PrimeraFilaDeEncabezado + 1, 1).Value =
            $"Punto de venta: {contexto.PuntoVenta ?? "Todos"}";
        hoja.Cell(PrimeraFilaDeEncabezado + 2, 1).Value =
            $"Período: {contexto.Desde:yyyy-MM-dd} a {contexto.Hasta:yyyy-MM-dd}";
        hoja.Cell(PrimeraFilaDeEncabezado + 3, 1).Value = contexto.Cobertura is null
            ? generadoPor
            : $"{generadoPor} — {contexto.Cobertura}";

        // La fila 5 queda vacía a propósito: separador visual antes del encabezado de tabla.
    }

    private static void EscribirTabla(IXLWorksheet hoja, TablaExportable tabla)
    {
        for (var c = 0; c < tabla.Columnas.Count; c++)
        {
            hoja.Cell(FilaDeTituloDeTabla, c + 1).Value = tabla.Columnas[c].Titulo;
        }

        var primeraFilaDeDatos = FilaDeTituloDeTabla + 1;

        for (var f = 0; f < tabla.Filas.Count; f++)
        {
            var fila = tabla.Filas[f];

            for (var c = 0; c < fila.Count; c++)
            {
                EscribirCelda(hoja.Cell(primeraFilaDeDatos + f, c + 1), fila[c]);
            }
        }

        for (var c = 0; c < tabla.Columnas.Count; c++)
        {
            AplicarFormatoDeColumna(hoja.Column(c + 1), tabla.Columnas[c].Tipo);
        }
    }

    private static void EscribirCelda(IXLCell celda, Celda valor)
    {
        if (valor.Valor is null)
        {
            // null siempre es celda vacía — nunca 0 ni "-" (decisión 2 del design).
            return;
        }

        celda.Value = valor.Tipo switch
        {
            TipoDeColumna.Texto => (string)valor.Valor,
            TipoDeColumna.Entero => (int)valor.Valor,
            TipoDeColumna.Decimal or TipoDeColumna.Moneda or TipoDeColumna.Cantidad => (decimal)valor.Valor,
            TipoDeColumna.Fecha => ((DateOnly)valor.Valor).ToDateTime(TimeOnly.MinValue),
            TipoDeColumna.FechaHora => (DateTime)valor.Valor,
            _ => throw new NotSupportedException($"Tipo de columna no soportado: {valor.Tipo}.")
        };
    }

    private static void AplicarFormatoDeColumna(IXLColumn columna, TipoDeColumna tipo)
    {
        var formato = tipo switch
        {
            TipoDeColumna.Entero => "#,##0",
            TipoDeColumna.Decimal or TipoDeColumna.Cantidad => "#,##0.00",
            TipoDeColumna.Moneda => "$ #,##0.00",
            TipoDeColumna.Fecha => "yyyy-mm-dd",
            TipoDeColumna.FechaHora => "yyyy-mm-dd hh:mm",
            _ => null
        };

        if (formato is not null)
        {
            columna.Style.NumberFormat.Format = formato;
        }
    }
}
