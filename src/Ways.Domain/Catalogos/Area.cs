namespace Ways.Domain.Catalogos;

/// <summary>
/// El rubro operativo (Almacén, Verdulería, Cigarrillos…): corta los totales de caja y los
/// reportes por rubro. Plano, pocos valores (doc 10 §1).
/// </summary>
public class Area : CatalogoSimple
{
    public int Orden { get; set; }
}
