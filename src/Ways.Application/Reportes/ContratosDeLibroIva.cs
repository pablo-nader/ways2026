namespace Ways.Application.Reportes;

/// <summary>Neto gravado e IVA de una alícuota. Una fila de libro trae una entrada por
/// <see cref="Porcentaje"/> presente; no hay columnas fijas en el contrato, el export las arma.</summary>
public sealed record AlicuotaDeLibroIva(decimal Porcentaje, decimal Neto, decimal Iva);

/// <summary>Un comprobante del libro IVA. <see cref="Total"/> es el del comprobante tal como se
/// guardó, con signo (una nota de crédito resta); <see cref="Diferencia"/> es
/// <c>Total − (neto gravado + IVA + no gravado + exento + percepciones)</c>: cero en un dato bien
/// formado, distinto de cero cuando los componentes no cierran. Esas filas se informan, nunca se
/// ocultan ni se corrigen en silencio. En ventas las percepciones son siempre cero (no se emiten
/// percepciones en ventas).</summary>
public sealed record FilaDeLibroIva(
    DateOnly Fecha,
    string TipoComprobante,
    string Numero,
    string Contraparte,
    string? Documento,
    IReadOnlyList<AlicuotaDeLibroIva> Alicuotas,
    decimal NoGravado,
    decimal Exento,
    decimal PercepcionIva,
    decimal PercepcionIibb,
    decimal Total,
    decimal Diferencia)
{
    public decimal NetoGravado => Alicuotas.Sum(a => a.Neto);

    public decimal IvaTotal => Alicuotas.Sum(a => a.Iva);
}

public sealed record TotalesDeLibroIva(
    IReadOnlyList<AlicuotaDeLibroIva> PorAlicuota,
    decimal NoGravado,
    decimal Exento,
    decimal PercepcionIva,
    decimal PercepcionIibb,
    decimal Total,
    decimal Diferencia)
{
    public decimal NetoGravado => PorAlicuota.Sum(a => a.Neto);

    public decimal IvaTotal => PorAlicuota.Sum(a => a.Iva);
}

/// <summary><see cref="IdEmpresa"/> es <c>null</c> cuando el libro abarca todas las empresas del
/// tenant. <see cref="ZonaHoraria"/> es la zona con la que se resolvió la fecha de cada fila de
/// ventas cuando el libro es de una sola empresa; <c>null</c> en compras (filtran por la fecha del
/// comprobante, un <c>DateOnly</c> sin zona) y en ventas de todas las empresas (cada una usa la
/// suya).</summary>
public sealed record LibroIva(
    DateOnly Desde,
    DateOnly Hasta,
    int? IdEmpresa,
    string? ZonaHoraria,
    IReadOnlyList<FilaDeLibroIva> Filas,
    TotalesDeLibroIva Totales);
