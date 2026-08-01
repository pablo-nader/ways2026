using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Condición fiscal ante AFIP/ARCA (RI, Monotributo, Exento, Consumidor Final, No
/// Responsable) — doc 10 §1. <c>[global]</c>: la define la plataforma, no el tenant. No
/// hereda de <see cref="Ways.Domain.Common.EntidadTenant"/> a propósito: no tiene
/// <c>id_tenant</c>, y por lo tanto ninguna RLS de tenant se le aplica (ADR-11, gate #4).
/// </summary>
public class CondicionFiscal : EntidadBase
{
    public int Id { get; set; }

    /// <summary>RI, MONOTRIBUTO, EXENTO, CF, NO_RESP.</summary>
    public required string Codigo { get; set; }

    public required string Nombre { get; set; }

    public short? CodigoAfip { get; set; }

    public bool Activo { get; set; } = true;
}
