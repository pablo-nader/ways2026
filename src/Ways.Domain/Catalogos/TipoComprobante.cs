using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Factura A/B/C, notas de crédito/débito, presupuesto (doc 10 §1). <c>[global]</c>: la
/// define la plataforma, no el tenant. Sin <c>id_tenant</c> (ADR-11, gate #4).
///
/// La letra sale del cruce condición fiscal emisor × condición fiscal cliente (regla de
/// dominio, no de esta tabla): el comprobante guarda el <c>id_tipo_comprobante</c> ya
/// resuelto, el cruce decide en el momento de emitir y nunca se re-deriva.
/// </summary>
public class TipoComprobante : EntidadBase
{
    public int Id { get; set; }

    public ClaseComprobante Clase { get; set; }

    /// <summary>FA, FB, FC, NCA, NCB, NCC, NDA…, TX, NCX, PRE.</summary>
    public required string Codigo { get; set; }

    public required string Nombre { get; set; }

    /// <summary>A, B, C, X; <c>NULL</c> para presupuesto.</summary>
    public char? Letra { get; set; }

    /// <summary>+1 suma a la cuenta, −1 resta (nota de crédito = −1).</summary>
    public short Signo { get; set; }

    /// <summary>A: neto + IVA por alícuota; B/C/X: total.</summary>
    public bool DiscriminaIva { get; set; }

    /// <summary>¿Reporta a AFIP/ARCA cuando exista facturación electrónica?</summary>
    public bool EsFiscal { get; set; }

    /// <summary>Presupuesto: no.</summary>
    public bool AfectaStock { get; set; }

    public short? CodigoAfip { get; set; }

    public bool Activo { get; set; } = true;
}
