namespace Ways.Domain.Catalogos;

/// <summary>
/// La marca comercial. A diferencia del legacy no arrastra proveedor ni grupo: una marca
/// puede venir de varios proveedores (doc 10 §1). Sin columnas propias además de las de
/// <see cref="CatalogoSimple"/>.
/// </summary>
public class Marca : CatalogoSimple;
