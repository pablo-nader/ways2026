namespace Ways.Domain.Catalogos;

/// <summary>
/// Un medio de pago es una fila, no una columna fija como en el legacy (doc 10 §1): la
/// caja totaliza por medio y agregar "QR" es un INSERT del tenant, no una migración.
/// </summary>
public class MedioPago : CatalogoSimple
{
    public int Orden { get; set; }

    public ComportamientoMedioPago Comportamiento { get; set; }

    /// <summary>Default según <see cref="Comportamiento"/>, editable por fila.</summary>
    public bool AdmiteVuelto { get; set; }

    /// <summary>Nro de cupón/operación.</summary>
    public bool RequiereReferencia { get; set; }

    /// <summary>P.ej. crédito +10%. Opcional.</summary>
    public decimal? RecargoPorcentaje { get; set; }
}
