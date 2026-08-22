using Ways.Domain.Common;

namespace Ways.Application.Fiscal;

/// <summary>Una línea ya congelada de <c>items_comprobante_venta</c> (design.md fact 5: el snapshot
/// por línea YA ES el <c>Iva[]</c> de ARCA) lista para componer. <see cref="Total"/> es el importe
/// de línea CON IVA incluido (mismo shape que <c>ItemComprobanteVenta.Total</c>,
/// <c>CalculadorDeTotales</c>) — la composición deriva neto/IVA de acá, nunca al revés.
/// <see cref="CodigoAfip"/> <c>NULL</c> es la señal de Exento/No gravado (decisión 11): el
/// bucketing es por ESTE campo más <see cref="NombreAlicuota"/>, JAMÁS por
/// <see cref="PorcentajeIva"/> == 0 — el 0% real (código 3) también tiene porcentaje 0.00 y
/// pertenece a <c>Iva[]</c>.</summary>
public sealed record LineaFiscal(
    int IdAlicuotaIva,
    string NombreAlicuota,
    short? CodigoAfip,
    decimal PorcentajeIva,
    decimal Total);

/// <summary>El resultado ya compuesto, listo para <see cref="SolicitudDeCae"/> (menos
/// <c>ImpTotal</c>, que <see cref="ComposicionDeTotalesFiscales.Componer"/> ya suma acá para que el
/// llamador nunca tenga que re-sumar los cinco términos por su cuenta).</summary>
public sealed record TotalesFiscales(
    decimal ImpNeto,
    decimal ImpIVA,
    decimal ImpOpEx,
    decimal ImpTotConc,
    decimal ImpTrib,
    decimal ImpTotal,
    IReadOnlyList<ItemIvaFiscal> Iva);

/// <summary>
/// Compone los totales fiscales desde el snapshot congelado por línea (design.md: Totals
/// Composition, decisión 11). Pura, sin base de datos: nunca vuelve a <c>alicuotas_iva</c> — todo
/// lo que necesita ya viaja en <see cref="LineaFiscal"/>, copiado al emitir (doc 10 principio 6).
/// </summary>
public static class ComposicionDeTotalesFiscales
{
    private const string NombreExento = "Exento";
    private const string NombreNoGravado = "No gravado";

    /// <summary>Sin tributos/percepciones en 19a (ningún flujo de esta sub-etapa los produce) —
    /// el campo existe en <see cref="TotalesFiscales"/> únicamente porque <c>ImpTotal</c> del wire
    /// lo exige como término, siempre en cero.</summary>
    private const decimal ImpTribSinPercepciones = 0m;

    public static TotalesFiscales Componer(IReadOnlyList<LineaFiscal> lineas)
    {
        var impNeto = 0m;
        var impIva = 0m;
        var itemsIva = new List<ItemIvaFiscal>();

        // GROUP BY IdAlicuotaIva (design fact 5) — SOLO las alícuotas con codigo_afip, nunca las
        // NULL-coded (Exento/No gravado JAMÁS entran a Iva[], decisión 11).
        foreach (var grupo in lineas.Where(l => l.CodigoAfip is not null).GroupBy(l => l.IdAlicuotaIva))
        {
            var totalDelGrupo = grupo.Sum(l => l.Total);
            var porcentaje = grupo.First().PorcentajeIva;
            var codigoAfip = grupo.First().CodigoAfip!.Value;

            // Total ya incluye IVA (precio final): neto = total / (1 + %/100); iva = total - neto,
            // así ImpNeto + ImpIVA reconstruye el total del grupo EXACTO, sin deriva de redondeo.
            var neto = Math.Round(totalDelGrupo / (1 + (porcentaje / 100m)), 2, MidpointRounding.AwayFromZero);
            var iva = totalDelGrupo - neto;

            impNeto += neto;
            impIva += iva;
            itemsIva.Add(new ItemIvaFiscal(codigoAfip, neto, iva));
        }

        var impOpEx = 0m;
        var impTotConc = 0m;

        foreach (var linea in lineas.Where(l => l.CodigoAfip is null))
        {
            switch (linea.NombreAlicuota)
            {
                case NombreExento:
                    impOpEx += linea.Total;
                    break;
                case NombreNoGravado:
                    impTotConc += linea.Total;
                    break;
                default:
                    // Bucketing por nombre, no por adivinanza: una alícuota NULL-coded que no sea
                    // Exento ni No gravado es un dato de catálogo sin mapeo AFIP conocido — facturar
                    // igual produciría un comprobante aritméticamente válido y legalmente incorrecto
                    // (decisión 11). Falla fuerte en vez de bucketearla en cualquiera de los dos.
                    throw new ErrorDominio(
                        "alicuota_sin_mapeo_afip",
                        $"La alícuota '{linea.NombreAlicuota}' no tiene código AFIP y no es " +
                        $"'{NombreExento}' ni '{NombreNoGravado}'.",
                        409);
            }
        }

        var impTotal = impNeto + impIva + impOpEx + impTotConc + ImpTribSinPercepciones;

        return new TotalesFiscales(impNeto, impIva, impOpEx, impTotConc, ImpTribSinPercepciones, impTotal, itemsIva);
    }
}
