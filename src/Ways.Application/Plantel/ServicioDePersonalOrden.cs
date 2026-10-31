using Ways.Application.Listados;

namespace Ways.Application.Plantel;

/// <summary>Columnas de datos de la grilla del personal: una por cada columna que la pantalla muestra, menos
/// Acciones.</summary>
public enum ColumnaDeOrdenDePersonal
{
    Nombre,
    ValorHora,
    Saldo,
    Usuario,
    Cliente,
}

/// <summary>Filtros por columna de <c>GET /api/personal</c>. Los de texto contienen el valor, sin mayúsculas ni
/// acentos; valor hora y saldo son rangos inclusivos. Todos se combinan con Y. Usuario y cliente filtran por el
/// nombre que la celda muestra, así que un vínculo a un registro dado de baja no coincide con ningún texto.</summary>
public sealed record FiltrosDePersonal(
    string? Nombre = null,
    decimal? ValorHoraDesde = null,
    decimal? ValorHoraHasta = null,
    decimal? SaldoDesde = null,
    decimal? SaldoHasta = null,
    string? Usuario = null,
    string? Cliente = null);

public static class OrdenDePersonal
{
    /// <summary>Lista cerrada de claves de <c>orden</c>: cada una ordena por lo que la columna muestra.</summary>
    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDePersonal> Claves =
        new Dictionary<string, ColumnaDeOrdenDePersonal>(StringComparer.Ordinal)
        {
            ["nombre"] = ColumnaDeOrdenDePersonal.Nombre,
            ["valorHora"] = ColumnaDeOrdenDePersonal.ValorHora,
            ["saldo"] = ColumnaDeOrdenDePersonal.Saldo,
            ["usuario"] = ColumnaDeOrdenDePersonal.Usuario,
            ["cliente"] = ColumnaDeOrdenDePersonal.Cliente,
        };

    public static OrdenDeListado<ColumnaDeOrdenDePersonal>? Interpretar(string? orden, string? direccion) =>
        OrdenDeListado.Interpretar(orden, direccion, Claves);
}
