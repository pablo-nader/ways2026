namespace Ways.Domain.Ofertas;

/// <summary>
/// Proyección total del beneficio de una <see cref="Oferta"/> (design decision 2): mismo
/// criterio que <see cref="AlcanceDeOferta"/> — exactamente uno de los tres factory methods
/// construye la instancia, cada uno ya con su rango validado por
/// <see cref="ReglaDeOfertas.LeerBeneficio"/>, así que el resolver (Slice 3) expresa la
/// intención directamente (p.ej. <c>BeneficioDeOferta.DePorcentaje(10m)</c>) en vez de leer
/// permutaciones de nullables.
/// </summary>
public readonly record struct BeneficioDeOferta
{
    private BeneficioDeOferta(decimal? precioUnitario, decimal? porcentaje, decimal? importeFijo)
    {
        PrecioUnitario = precioUnitario;
        Porcentaje = porcentaje;
        ImporteFijo = importeFijo;
    }

    public decimal? PrecioUnitario { get; }
    public decimal? Porcentaje { get; }
    public decimal? ImporteFijo { get; }

    public static BeneficioDeOferta DePrecioUnitario(decimal precioUnitario) => new(precioUnitario, null, null);
    public static BeneficioDeOferta DePorcentaje(decimal porcentaje) => new(null, porcentaje, null);
    public static BeneficioDeOferta DeImporteFijo(decimal importeFijo) => new(null, null, importeFijo);
}
