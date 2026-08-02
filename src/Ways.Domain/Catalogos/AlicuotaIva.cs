using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Alícuota de IVA (21%, 10.5%, 27%, 0%, Exento, No gravado) — doc 10 §1. <c>[global]</c>:
/// la define la plataforma, no el tenant. Sin <c>id_tenant</c> (ADR-11, gate #4).
/// </summary>
public class AlicuotaIva : EntidadBase
{
    public int Id { get; set; }

    /// <summary>Etiqueta visible: "21%", "10.5%"…</summary>
    public required string Nombre { get; set; }

    public decimal Porcentaje { get; set; }

    public short? CodigoAfip { get; set; }

    public bool Activo { get; set; } = true;
}
