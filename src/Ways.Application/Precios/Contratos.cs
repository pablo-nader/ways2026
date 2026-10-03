namespace Ways.Application.Precios;

/// <summary>Establece el precio vigente de un artículo en una lista <c>fija</c>, efectivo
/// AHORA (<c>ServicioDePrecios.EstablecerPrecioAsync</c> resuelve <c>vigente_desde</c> desde el
/// reloj del sistema, nunca desde el cliente). <see cref="ConfirmarReemplazo"/> solo importa
/// cuando ya existe un precio PENDIENTE (programado a futuro) para el mismo par — sin
/// confirmación, esa fila pendiente se conserva y el alta se rechaza con
/// <c>precio_pendiente_existe</c> (spec: Programmable Future Prices, At Most One Pending; design
/// decision 4). Sin <c>Id</c>: no existe edición de una fila existente — el único camino de
/// escritura es abrir una fila nueva (design decision 3, "precios never has an entity-level
/// Update").
///
/// <para><see cref="Alcance"/> es la decisión del cliente cuando el artículo pertenece a una familia
/// (doc 10 §3); cada valor tiene un destino: <c>null</c> en un artículo sin familia lo escribe solo,
/// <c>null</c> en un miembro se rechaza con <c>alcance_requerido</c> (409) sin escribir nada,
/// <see cref="AlcanceDeFamilia.Familia"/> aplica el precio a todos los miembros vivos,
/// <see cref="AlcanceDeFamilia.SoloEste"/> saca al artículo de la familia y lo escribe solo, y un
/// <see cref="Alcance"/> explícito sobre un artículo que ya no es miembro se rechaza con
/// <c>familia_cambio</c> (409). Un ordinal que no es el de ninguno de los dos valores (el JSON del
/// servidor acepta el ordinal además del nombre; <c>0</c> incluido) llega al servicio y se rechaza con
/// <c>alcance_invalido</c> (400). Un texto que no se lee ni como el nombre de un valor ni como un
/// ordinal no llega al servicio: lo rechaza el binding JSON del framework, igual que para cualquier
/// otro enum de la API, sin pasar por <c>alcance_invalido</c>.</para></summary>
public record AltaPrecio(
    int IdListaPrecio, decimal Precio, bool ConfirmarReemplazo = false, AlcanceDeFamilia? Alcance = null);

/// <summary>Programa un precio a futuro (spec: Programmable Future Prices) — <see
/// cref="VigenteDesde"/> tiene que ser una fecha futura (con una tolerancia de desfasaje de
/// reloj, ver <c>ServicioDePrecios.ToleranciaReloj</c>); si ya hay un precio pendiente para el
/// mismo par, <see cref="ConfirmarReemplazo"/> en <c>true</c> lo reemplaza, en <c>false</c>
/// rechaza con <c>precio_pendiente_existe</c> (409). <see cref="Alcance"/> tiene el mismo
/// significado que en <see cref="AltaPrecio"/>; sobre una familia, el reemplazo del pendiente se
/// evalúa por cada miembro y un solo pendiente sin confirmar rechaza la solicitud entera.</summary>
public record ProgramarPrecio(
    int IdListaPrecio, decimal Precio, DateTimeOffset VigenteDesde, bool ConfirmarReemplazo = false,
    AlcanceDeFamilia? Alcance = null);

/// <summary>Lo que el cliente elige cuando el artículo cuyo precio cambia pertenece a una familia
/// (doc 10 §3). La ausencia de valor es una tercera respuesta —"sin elección"— y la API la rechaza
/// para un miembro: elegir por el cliente podría pisar el precio de artículos que no quería tocar.
///
/// <para>Los valores numéricos empiezan en 1 a propósito: <c>0</c> es el valor por defecto de un
/// entero y no tiene que elegir un alcance —y menos el más amplio—, así que ningún miembro lo
/// nombra y un <c>0</c> en el JSON se rechaza con <c>alcance_invalido</c>.</para></summary>
public enum AlcanceDeFamilia
{
    /// <summary>El precio se aplica a todos los miembros vivos de la familia, en la misma
    /// transacción.</summary>
    Familia = 1,

    /// <summary>El precio se aplica solo a este artículo, que sale de la familia.</summary>
    SoloEste = 2
}

/// <summary>Precio resuelto de un artículo en una lista a una fecha dada (spec: Current-Price
/// Query Semantics By Date, Derived List Price Resolution At Read Time) — <see cref="Precio"/>
/// es <c>null</c> cuando no hay ninguna fila vigente a esa fecha (artículo sin precio cargado
/// todavía en esa lista, o lista derivada cuya base tampoco tiene precio a esa fecha).</summary>
public record PrecioVigente(int IdArticulo, int IdListaPrecio, decimal? Precio, DateTimeOffset Fecha);

/// <summary>Una fila de historial (spec: Price History Never Overwrites) — solo existe para
/// listas <c>fija</c>; una lista <c>derivada</c> nunca tiene filas propias en <c>precios</c>.
/// <see cref="VigenteHasta"/> <c>null</c> ⇒ es la fila actualmente abierta (vigente o
/// pendiente, según su propio <see cref="VigenteDesde"/> contra "ahora").</summary>
public record HistorialDePrecio(int Id, decimal Precio, DateTimeOffset VigenteDesde, DateTimeOffset? VigenteHasta);
