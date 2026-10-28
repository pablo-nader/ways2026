namespace Ways.Domain.Compras;

/// <summary>Cantidad que una compra tiene (o tendrá) puesta en el stock de un artículo, en un punto de venta y, si el
/// artículo controla lote, en un lote. Un asiento del libro de stock de la compra y una línea de la compra editada
/// se expresan igual, para poder compararlos por clave.</summary>
public sealed record CantidadDeStockDeCompra(int IdArticulo, int IdPuntoVenta, int? IdLote, decimal Cantidad);

/// <summary>Lo que hay que sumar (positivo) o restar (negativo) al stock de una clave para pasar de lo que la compra tenía
/// puesto a lo que tendrá. Nunca es cero: una clave que no cambia no figura.</summary>
public sealed record DiferenciaDeStockDeCompra(int IdArticulo, int IdPuntoVenta, int? IdLote, decimal Delta);

/// <summary>Asiento <c>compra</c> de la cuenta corriente de un proveedor, ligado a la compra.</summary>
public sealed record AsientoDeCuentaDeCompra(int IdProveedor, decimal Importe);

/// <summary>Lo que hay que sumar (positivo) o restar (negativo) a la cuenta de un proveedor para que el asiento de la
/// compra pase a valer lo que corresponde. Nunca es cero.</summary>
public sealed record DiferenciaDeCuentaDeCompra(int IdProveedor, decimal Delta);

/// <summary>Una línea con artículo de la compra ya confirmada, tal como quedó guardada: lo que identifica su lote y el
/// lote que se resolvió al confirmar (nulo si el artículo no lo controlaba).</summary>
public sealed record LineaDeLoteDeCompra(int IdArticulo, string? CodigoLote, DateOnly? FechaVencimiento, int? IdLote);

/// <summary>Una línea de la compra tal como la deja el rastro de auditoría de la edición: <c>IdArticulo</c> nulo es un concepto.</summary>
public sealed record LineaAuditadaDeCompra(int? IdArticulo, decimal Cantidad, decimal Total);

/// <summary>El estado de una compra que el rastro <c>compra.edicion</c> guarda antes y después de editarla.</summary>
public sealed record ResumenDeCompraEditada(
    int IdProveedor, int IdPuntoVenta, string? NumeroExterno, int? IdOrdenCompra, decimal Total,
    IReadOnlyList<LineaAuditadaDeCompra> Lineas);

/// <summary>
/// Cálculo puro de la edición de una compra confirmada (sin base de datos): el stock y la cuenta corriente se corrigen
/// por DIFERENCIA contra lo que la compra ya asentó, nunca reescribiendo los libros. Los libros son de solo agregado: lo
/// que cambia se asienta como un movimiento nuevo con el signo de la diferencia.
/// </summary>
public static class CalculadorDeEdicionDeCompra
{
    /// <summary>Diferencia por clave <c>(artículo, punto de venta, lote)</c> entre lo que el libro de stock de la compra
    /// ya tiene sumado (<paramref name="original"/>) y lo que tendrá la compra editada (<paramref name="nueva"/>). Las
    /// cantidades de una misma clave se netean antes de comparar —así +10 y −4 en el libro valen +6 y no dos
    /// diferencias—, y la clave cuya diferencia es cero no figura. El resultado va en orden ascendente por artículo,
    /// punto de venta y lote (sin lote primero): el orden en que el servicio toma los locks de stock. Con
    /// <paramref name="nueva"/> vacío es la reversa exacta de lo asentado (la que usa la anulación).</summary>
    public static IReadOnlyList<DiferenciaDeStockDeCompra> DiferenciasDeStock(
        IEnumerable<CantidadDeStockDeCompra> original, IEnumerable<CantidadDeStockDeCompra> nueva)
    {
        var neto = new Dictionary<(int IdArticulo, int IdPuntoVenta, int? IdLote), decimal>();

        foreach (var asiento in original)
        {
            var clave = (asiento.IdArticulo, asiento.IdPuntoVenta, asiento.IdLote);
            neto[clave] = neto.GetValueOrDefault(clave) - asiento.Cantidad;
        }

        foreach (var linea in nueva)
        {
            var clave = (linea.IdArticulo, linea.IdPuntoVenta, linea.IdLote);
            neto[clave] = neto.GetValueOrDefault(clave) + linea.Cantidad;
        }

        return neto
            .Where(par => par.Value != 0m)
            .Select(par => new DiferenciaDeStockDeCompra(par.Key.IdArticulo, par.Key.IdPuntoVenta, par.Key.IdLote, par.Value))
            .OrderBy(d => d.IdArticulo)
            .ThenBy(d => d.IdPuntoVenta)
            .ThenBy(d => d.IdLote)
            .ToList();
    }

    /// <summary>Diferencia por proveedor entre los asientos <c>compra</c> que la compra ya tiene (<paramref name="asientos"/>) y
    /// lo que deben sumar: el total nuevo para el proveedor de la compra editada y cero para cualquier otro que haya
    /// tenido. Si el proveedor no cambia es una sola diferencia, <c>totalNuevo − suma</c>; si cambia, el anterior
    /// recibe la reversa de lo suyo y el nuevo el total. Orden ascendente por proveedor (el orden de sus locks); la
    /// diferencia cero no figura.</summary>
    public static IReadOnlyList<DiferenciaDeCuentaDeCompra> DiferenciasDeCuentaCorriente(
        IEnumerable<AsientoDeCuentaDeCompra> asientos, int idProveedorNuevo, decimal totalNuevo)
    {
        var objetivo = new Dictionary<int, decimal> { [idProveedorNuevo] = totalNuevo };
        var vigente = new Dictionary<int, decimal>();

        foreach (var asiento in asientos)
        {
            vigente[asiento.IdProveedor] = vigente.GetValueOrDefault(asiento.IdProveedor) + asiento.Importe;
            objetivo.TryAdd(asiento.IdProveedor, 0m);
        }

        return objetivo
            .Select(par => new DiferenciaDeCuentaDeCompra(par.Key, par.Value - vigente.GetValueOrDefault(par.Key)))
            .Where(d => d.Delta != 0m)
            .OrderBy(d => d.IdProveedor)
            .ToList();
    }

    /// <summary>
    /// Si la edición toca lo que los pagos ya aplicados a la compra dependen de: el total baja por debajo de lo ya pagado
    /// (aplicaciones más lo perdonado por redondeo), cambia el total de una compra con redondeo perdonado (la diferencia
    /// perdonada ya no sería la que cierra el saldo), o cambia el proveedor, el punto de venta o la empresa de una compra con
    /// pagos aplicados o redondeo (los pagos son de un proveedor y de una empresa). Una compra sin pagos ni redondeo se edita
    /// sin restricción.
    /// </summary>
    public static bool RequiereTratarLosPagos(
        decimal pagado, decimal redondeoVigente, bool tieneAplicaciones, decimal totalAnterior, decimal totalNuevo,
        bool cambiaProveedor, bool cambiaPuntoVenta, bool cambiaEmpresa)
    {
        var tienePagos = tieneAplicaciones || redondeoVigente != 0m;

        return totalNuevo < pagado
            || (redondeoVigente != 0m && totalNuevo != totalAnterior)
            || (tienePagos && (cambiaProveedor || cambiaPuntoVenta || cambiaEmpresa));
    }

    /// <summary>La línea ya guardada que tiene exactamente el mismo artículo, código de lote y vencimiento que la pedida, o
    /// <c>null</c>. Una línea así no cambia de lote: conserva el que se resolvió al confirmar y no se vuelve a verificar su
    /// vencimiento (la mercadería ya entró). El código se compara recortado y sin distinguir otra cosa que el texto.</summary>
    public static LineaDeLoteDeCompra? LineaSinCambioDeLote(
        IEnumerable<LineaDeLoteDeCompra> existentes, int idArticulo, string? codigoLote, DateOnly? fechaVencimiento)
    {
        var codigo = Normalizar(codigoLote);

        return existentes.FirstOrDefault(e =>
            e.IdArticulo == idArticulo && Normalizar(e.CodigoLote) == codigo && e.FechaVencimiento == fechaVencimiento);
    }

    private static string? Normalizar(string? codigo)
    {
        var limpio = codigo?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }
}
