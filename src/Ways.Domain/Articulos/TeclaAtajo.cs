namespace Ways.Domain.Articulos;

/// <summary>
/// Tecla de función del POS que agrega al carrito un artículo vendido por importe
/// (<see cref="UnidadVenta.Importe"/>) con el monto que está escrito en la caja de escaneo. Se guarda como texto
/// (<c>tecla_atajo</c>) con el nombre del miembro, acotado por un CHECK de la tabla; <c>NULL</c> es "sin atajo".
/// </summary>
public enum TeclaAtajo
{
    F5,
    F6,
    F7,
    F8
}
