using Ways.Application.Fiscal;

namespace Ways.Application.Reportes;

public sealed record AlicuotaDeCompraParaLibro(
    short? CodigoAfip, string NombreAlicuota, decimal Porcentaje, decimal Neto, decimal Iva);

public sealed record CompraParaLibro(
    DateOnly Fecha,
    string CodigoTipo,
    string Numero,
    string Proveedor,
    string? Cuit,
    bool DiscriminaIva,
    decimal Total,
    IReadOnlyList<AlicuotaDeCompraParaLibro> Alicuotas,
    decimal PercepcionIva,
    decimal PercepcionIibb);

/// <summary><see cref="Signo"/> viene de <c>tipos_comprobante.signo</c>: la emisión fiscal guarda
/// los importes de una nota de crédito en positivo (exige cantidades positivas), así que el libro
/// los resta acá.</summary>
public sealed record VentaParaLibro(
    DateOnly Fecha,
    string CodigoTipo,
    string Numero,
    string Cliente,
    string? Documento,
    short Signo,
    decimal Total,
    IReadOnlyList<LineaFiscal> Lineas);

/// <summary>
/// Arma las filas y los totales del libro IVA. Pura, sin base de datos. Compras: el desglose sale
/// de las alícuotas guardadas en el comprobante; un comprobante que no discrimina IVA (factura B/C)
/// no tiene crédito fiscal, y todo lo que no son percepciones va a no gravado. Ventas: el desglose
/// se recompone desde las líneas con <see cref="ComposicionDeTotalesFiscales.Componer"/>, la misma
/// función que usa la emisión fiscal, sin tabla propia.
/// </summary>
public static class ComposicionDeLibroIva
{
    public static LibroIva DeCompras(
        DateOnly desde, DateOnly hasta, int? idEmpresa, IReadOnlyList<CompraParaLibro> compras)
    {
        var filas = compras.Select(FilaDeCompra).ToList();
        return Armar(desde, hasta, idEmpresa, null, filas);
    }

    public static LibroIva DeVentas(
        DateOnly desde, DateOnly hasta, int? idEmpresa, string? zonaHoraria, IReadOnlyList<VentaParaLibro> ventas)
    {
        var filas = ventas.Select(FilaDeVenta).ToList();
        return Armar(desde, hasta, idEmpresa, zonaHoraria, filas);
    }

    private static FilaDeLibroIva FilaDeCompra(CompraParaLibro compra)
    {
        var alicuotas = new List<AlicuotaDeLibroIva>();
        var exento = 0m;
        var noGravado = 0m;

        if (compra.DiscriminaIva)
        {
            foreach (var alicuota in compra.Alicuotas)
            {
                switch (ComposicionDeTotalesFiscales.Clasificar(alicuota.CodigoAfip, alicuota.NombreAlicuota))
                {
                    case ClaseDeAlicuota.Gravada:
                        alicuotas.Add(new AlicuotaDeLibroIva(alicuota.Porcentaje, alicuota.Neto, alicuota.Iva));
                        break;
                    case ClaseDeAlicuota.Exento:
                        exento += alicuota.Neto;
                        break;
                    default:
                        noGravado += alicuota.Neto;
                        break;
                }
            }
        }
        else
        {
            noGravado = compra.Total - compra.PercepcionIva - compra.PercepcionIibb;
        }

        return NuevaFila(
            compra.Fecha, compra.CodigoTipo, compra.Numero, compra.Proveedor, compra.Cuit, alicuotas, noGravado,
            exento, compra.PercepcionIva, compra.PercepcionIibb, compra.Total);
    }

    private static FilaDeLibroIva FilaDeVenta(VentaParaLibro venta)
    {
        var totales = ComposicionDeTotalesFiscales.Componer(venta.Lineas);
        var signo = venta.Signo;

        var porcentajePorCodigo = venta.Lineas
            .Where(l => l.CodigoAfip is not null)
            .GroupBy(l => l.CodigoAfip!.Value)
            .ToDictionary(g => g.Key, g => g.First().PorcentajeIva);

        var alicuotas = totales.Iva
            .Select(i => new AlicuotaDeLibroIva(porcentajePorCodigo[i.Id], signo * i.BaseImp, signo * i.Importe))
            .ToList();

        return NuevaFila(
            venta.Fecha, venta.CodigoTipo, venta.Numero, venta.Cliente, venta.Documento, alicuotas,
            signo * totales.ImpTotConc, signo * totales.ImpOpEx, 0m, 0m, signo * venta.Total);
    }

    private static FilaDeLibroIva NuevaFila(
        DateOnly fecha, string codigoTipo, string numero, string contraparte, string? documento,
        IReadOnlyList<AlicuotaDeLibroIva> alicuotas, decimal noGravado, decimal exento, decimal percepcionIva,
        decimal percepcionIibb, decimal total)
    {
        var porAlicuota = AgruparPorPorcentaje(alicuotas);
        var componentes = porAlicuota.Sum(a => a.Neto + a.Iva) + noGravado + exento + percepcionIva + percepcionIibb;

        return new FilaDeLibroIva(
            fecha, codigoTipo, numero, contraparte, documento, porAlicuota, noGravado, exento, percepcionIva,
            percepcionIibb, total, total - componentes);
    }

    private static LibroIva Armar(
        DateOnly desde, DateOnly hasta, int? idEmpresa, string? zonaHoraria, List<FilaDeLibroIva> filas)
    {
        var ordenadas = filas
            .OrderBy(f => f.Fecha)
            .ThenBy(f => f.TipoComprobante, StringComparer.Ordinal)
            .ThenBy(f => f.Numero, StringComparer.Ordinal)
            .ToList();

        var totales = new TotalesDeLibroIva(
            AgruparPorPorcentaje(ordenadas.SelectMany(f => f.Alicuotas).ToList()),
            ordenadas.Sum(f => f.NoGravado),
            ordenadas.Sum(f => f.Exento),
            ordenadas.Sum(f => f.PercepcionIva),
            ordenadas.Sum(f => f.PercepcionIibb),
            ordenadas.Sum(f => f.Total),
            ordenadas.Sum(f => f.Diferencia));

        return new LibroIva(desde, hasta, idEmpresa, zonaHoraria, ordenadas, totales);
    }

    private static List<AlicuotaDeLibroIva> AgruparPorPorcentaje(IReadOnlyList<AlicuotaDeLibroIva> alicuotas) =>
        alicuotas
            .GroupBy(a => a.Porcentaje)
            .OrderByDescending(g => g.Key)
            .Select(g => new AlicuotaDeLibroIva(g.Key, g.Sum(a => a.Neto), g.Sum(a => a.Iva)))
            .ToList();
}
