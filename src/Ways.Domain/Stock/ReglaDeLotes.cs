namespace Ways.Domain.Stock;

/// <summary>
/// Proyección de saldo de un lote para las reglas puras de esta clase — <c>ServicioDeLotes</c>
/// (Application, slice 3) es quien materializa estos records a partir del join
/// <c>lotes ⟕ stock_lotes</c> (design decisión 6); acá no se conoce ninguna dependencia de base
/// de datos.
/// </summary>
public readonly record struct SaldoDeLote(
    int IdArticulo, int IdLote, string Codigo, bool EsSinIdentificar,
    DateOnly? FechaVencimiento, decimal Cantidad);

/// <summary>Clasificación de un lote respecto de "hoy" (design decisión 15/16). <see
/// cref="SinFecha"/> es el lote sin identificar — se incluye en el reporte, nunca se excluye
/// (spec lotes-y-vencimientos: "the sin-identificar residue is exactly the number that should
/// nag someone into identifying it").</summary>
public enum EstadoDeVencimiento
{
    Vencido,
    PorVencer,
    Vigente,
    SinFecha
}

/// <summary>
/// La regla de lotes, pura y sin base de datos (design decisión 1, patrón
/// <c>PoliticaDeRoles</c>): control efectivo, orden FEFO, elección FEFO, derivación de código y
/// clasificación de vencimiento. Cada uno de los tres sitios de escritura (venta, compra,
/// transferencia) la consume igual — la regla se testea UNA vez acá, nunca reimplementada.
/// </summary>
public static class ReglaDeLotes
{
    /// <summary>Código reservado del lote "sin identificar" (design decisión 5) — un
    /// <c>codigoLote</c> de cliente igual a esta literal se rechaza <c>400
    /// codigo_de_lote_reservado</c> (slice 3).</summary>
    public const string CodigoSinIdentificar = "SIN-IDENTIFICAR";

    /// <summary>Control efectivo = flag del artículo AND parámetro de la empresa (spec
    /// lotes-y-vencimientos: "Effective Lot Control Is controla_lote AND lotes_habilitado").
    /// Con cualquiera de los dos en <c>false</c>, el movimiento corre byte-idéntico al camino
    /// agregado-only anterior a esta etapa.</summary>
    public static bool ControlEfectivo(bool controlaLote, bool lotesHabilitado) =>
        controlaLote && lotesHabilitado;

    /// <summary>Orden FEFO server-computed (spec: "FEFO Is The Server-Computed Default"):
    /// sin-identificar PRIMERO (<c>es_sin_identificar DESC</c>), después vencimiento ascendente
    /// (<c>NULLS</c> no aplica acá porque el sin-identificar ya quedó primero), <c>id_lote</c>
    /// como desempate.</summary>
    public static IReadOnlyList<SaldoDeLote> OrdenarFefo(IEnumerable<SaldoDeLote> saldos) =>
        saldos
            .OrderByDescending(s => s.EsSinIdentificar)
            .ThenBy(s => s.FechaVencimiento ?? DateOnly.MinValue)
            .ThenBy(s => s.IdLote)
            .ToList();

    /// <summary>Lote por defecto de una línea sin <c>idLote</c> explícito. <c>null</c> ⇒ ningún
    /// lote del artículo tiene saldo positivo — el llamador resuelve (o crea, get-or-create) el
    /// lote sin identificar en su lugar (design decisión 7), esta función nunca lo crea.</summary>
    public static SaldoDeLote? ElegirFefo(IEnumerable<SaldoDeLote> saldosDelArticulo)
    {
        var conSaldoPositivo = saldosDelArticulo.Where(s => s.Cantidad > 0m).ToList();

        return conSaldoPositivo.Count == 0 ? null : OrdenarFefo(conSaldoPositivo)[0];
    }

    /// <summary>Código server-derivado a partir del vencimiento ISO (spec: "A lot is created
    /// with a server-derived codigo") — usado cuando el llamador omite <c>codigo</c> en la
    /// recepción/alta.</summary>
    public static string DerivarCodigo(DateOnly fechaVencimiento) =>
        fechaVencimiento.ToString("yyyy-MM-dd");

    /// <summary>Un lote sin fecha (sin identificar) nunca está vencido.</summary>
    public static bool EstaVencido(DateOnly? fecha, DateOnly hoy) =>
        fecha is not null && fecha.Value < hoy;

    /// <summary>Clasificación en las cuatro categorías del reporte de vencimientos (design
    /// decisión 16, spec: "Vencimientos Report Resolves Hoy…"): <c>vencido</c> si ya pasó,
    /// <c>por_vencer</c> si entra dentro del horizonte de alerta (inclusive en ambos extremos),
    /// <c>vigente</c> más allá del horizonte, <c>sin_fecha</c> para el lote sin identificar.</summary>
    public static EstadoDeVencimiento Clasificar(DateOnly? fecha, DateOnly hoy, int diasDeAlerta)
    {
        if (fecha is null)
        {
            return EstadoDeVencimiento.SinFecha;
        }

        if (EstaVencido(fecha, hoy))
        {
            return EstadoDeVencimiento.Vencido;
        }

        return fecha.Value <= hoy.AddDays(diasDeAlerta)
            ? EstadoDeVencimiento.PorVencer
            : EstadoDeVencimiento.Vigente;
    }
}
