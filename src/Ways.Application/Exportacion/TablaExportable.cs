namespace Ways.Application.Exportacion;

/// <summary>
/// Tabla neutra que todo mapper produce y todo <see cref="IExportadorDeTabla"/> consume — el
/// puerto de exportación de la etapa 11. El constructor se auto-valida (decisión 2 del design):
/// cada fila debe tener exactamente <see cref="Columnas"/>.Count celdas y el tipo de cada celda
/// debe coincidir con el de su columna, o lanza. Así "un mapper puso un string en una columna de
/// plata" es un test unitario que falla, no una celda silenciosa en un archivo que nadie vuelve
/// a abrir.
/// </summary>
public sealed record TablaExportable
{
    public string NombreDeHoja { get; }

    public ContextoDeExportacion Contexto { get; }

    public IReadOnlyList<ColumnaExportable> Columnas { get; }

    public IReadOnlyList<IReadOnlyList<Celda>> Filas { get; }

    public TablaExportable(
        string nombreDeHoja,
        ContextoDeExportacion contexto,
        IReadOnlyList<ColumnaExportable> columnas,
        IReadOnlyList<IReadOnlyList<Celda>> filas)
    {
        for (var f = 0; f < filas.Count; f++)
        {
            var fila = filas[f];

            if (fila.Count != columnas.Count)
            {
                throw new ArgumentException(
                    $"La fila {f} tiene {fila.Count} celda(s); se esperaban {columnas.Count} (una por columna).",
                    nameof(filas));
            }

            for (var c = 0; c < columnas.Count; c++)
            {
                if (fila[c].Tipo != columnas[c].Tipo)
                {
                    throw new ArgumentException(
                        $"La celda [{f}][{c}] es de tipo {fila[c].Tipo}, pero la columna " +
                        $"\"{columnas[c].Titulo}\" espera {columnas[c].Tipo}.",
                        nameof(filas));
                }
            }
        }

        NombreDeHoja = nombreDeHoja;
        Contexto = contexto;
        Columnas = columnas;
        Filas = filas;
    }
}
