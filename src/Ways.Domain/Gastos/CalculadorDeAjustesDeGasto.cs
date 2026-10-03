namespace Ways.Domain.Gastos;

/// <summary>Los campos de un gasto que deciden sus efectos sobre los ledgers (cuenta corriente de
/// proveedor, tesorería y arqueo del turno).</summary>
public sealed record EstadoContableDeGasto(CategoriaGasto Categoria, int? IdProveedor, int IdMedioPago, decimal Importe);

/// <summary>Importe que se suma a <c>proveedores.saldo</c> (y que lleva el movimiento de ajuste).</summary>
public sealed record AjusteDeSaldoDeProveedor(int IdProveedor, decimal Importe);

public sealed record AjusteDeTesoreria(decimal Ingreso, decimal Egreso);

/// <summary>
/// Efectos de editar o dar de baja un gasto ya registrado, sin base de datos. Los ledgers nunca se
/// reescriben: cada diferencia se compensa con un movimiento nuevo de ajuste.
/// </summary>
public static class CalculadorDeAjustesDeGasto
{
    /// <summary>El gasto escribió un pago a proveedor (saldo − importe) solo si es de categoría
    /// proveedor con proveedor asignado — mismo predicado que el alta. Revertir el efecto anterior
    /// suma su importe; aplicar el nuevo lo resta; sobre el mismo proveedor se netea en un único
    /// ajuste. <paramref name="nuevo"/> <c>null</c> es una baja. Sale ordenado por proveedor
    /// ascendente: es el orden en que el llamador toma los locks de fila.</summary>
    public static IReadOnlyList<AjusteDeSaldoDeProveedor> AjustesDeProveedor(
        EstadoContableDeGasto anterior, EstadoContableDeGasto? nuevo)
    {
        var porProveedor = new SortedDictionary<int, decimal>();

        if (ProveedorPagado(anterior) is { } idAnterior)
        {
            porProveedor[idAnterior] = anterior.Importe;
        }

        if (nuevo is not null && ProveedorPagado(nuevo) is { } idNuevo)
        {
            porProveedor[idNuevo] = porProveedor.GetValueOrDefault(idNuevo) - nuevo.Importe;
        }

        return porProveedor
            .Where(par => par.Value != 0m)
            .Select(par => new AjusteDeSaldoDeProveedor(par.Key, par.Value))
            .ToList();
    }

    /// <summary>Solo un gasto pagado de la tesorería tiene movimiento de tesorería. Un importe
    /// mayor egresa la diferencia, uno menor (o la baja) la reingresa.</summary>
    public static AjusteDeTesoreria? AjusteDeTesoreriaPara(
        OrigenFondosGasto origen, decimal importeAnterior, decimal? importeNuevo)
    {
        if (origen != OrigenFondosGasto.Tesoreria)
        {
            return null;
        }

        var diferencia = (importeNuevo ?? 0m) - importeAnterior;

        return diferencia switch
        {
            > 0m => new AjusteDeTesoreria(0m, diferencia),
            < 0m => new AjusteDeTesoreria(-diferencia, 0m),
            _ => null
        };
    }

    /// <summary>El arqueo solo descuenta gastos de caja, por su medio de pago y su importe.</summary>
    public static bool AfectaElArqueo(
        OrigenFondosGasto origen, EstadoContableDeGasto anterior, EstadoContableDeGasto? nuevo) =>
        origen == OrigenFondosGasto.CajaTurno
        && (nuevo is null || nuevo.Importe != anterior.Importe || nuevo.IdMedioPago != anterior.IdMedioPago);

    private static int? ProveedorPagado(EstadoContableDeGasto estado) =>
        estado.Categoria == CategoriaGasto.Proveedor ? estado.IdProveedor : null;
}
