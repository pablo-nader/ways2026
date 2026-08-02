using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Los números mágicos del legacy ($10 de tolerancia, $20 de vuelto máximo…) vueltos
/// configuración (doc 10 §9, ADR-13). Operativa con fallback a empresa: a diferencia de
/// <see cref="CatalogoSimple"/>, <see cref="IdEmpresa"/> acá es obligatorio — siempre hay
/// una fila de empresa que actúa de default — y <see cref="IdPuntoVenta"/> es lo opcional.
/// </summary>
public class Parametro : EntidadTenant
{
    public int Id { get; set; }

    public int IdEmpresa { get; set; }

    /// <summary><c>NULL</c> ⇒ default de la empresa. Un valor ⇒ propio de ese punto de
    /// venta, gana por sobre el de la empresa (<see cref="ResolucionDeParametros"/>).</summary>
    public int? IdPuntoVenta { get; set; }

    public required string Clave { get; set; }

    /// <summary>JSON crudo (columna <c>jsonb</c>). El tipo CLR fuerte lo declara
    /// <see cref="ParametroConocido"/>; esta tabla no lo tipa a nivel de esquema para no
    /// necesitar una migración por cada clave nueva.</summary>
    public required string Valor { get; set; }
}
