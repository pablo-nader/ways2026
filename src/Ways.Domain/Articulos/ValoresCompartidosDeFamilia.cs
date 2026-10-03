namespace Ways.Domain.Articulos;

/// <summary>
/// Los doce campos de <see cref="Articulo"/> que son IGUALES para todos los miembros de una
/// <see cref="Familia"/> (doc 10 §3). Regla pura, sin base de datos: <see cref="De"/> toma la foto de
/// un artículo, <see cref="AplicarA"/> la copia sobre otro y <see cref="CamposDistintos"/> nombra, con
/// el nombre de columna de la tabla <c>articulos</c>, qué campos difieren — es el insumo del detalle
/// de un error de API.
///
/// Lo que NO está acá es propio de cada artículo y nunca se copia ni se compara:
/// <c>nombre</c>, <c>descripcion</c>, <c>codigo_interno</c>, los códigos de barra, <c>id_marca</c>,
/// <c>activo</c>, <c>disponible_para_todas</c> (con su <c>articulos_empresas</c>) y la propia
/// <see cref="Articulo.IdFamilia"/>. El estado de precios de las listas fijas también es compartido,
/// pero vive en <c>precios</c> y lo replica quien escribe precios, no este registro.
///
/// Un campo nuevo de <see cref="Articulo"/> debe clasificarse como compartido (se agrega acá) o
/// propio: la prueba <c>TodaPropiedadDeArticuloEstaClasificada</c> falla hasta que se decida.
/// </summary>
public sealed record ValoresCompartidosDeFamilia(
    int IdArea,
    int? IdCategoria,
    int? IdGrupo,
    int? IdProveedorHabitual,
    int IdAlicuotaIva,
    UnidadVenta UnidadVenta,
    decimal? UnidadesPorBulto,
    bool EsProducto,
    bool ControlaLote,
    decimal? CostoLista,
    decimal? DescuentoProveedor,
    decimal? CostoNominal)
{
    public static ValoresCompartidosDeFamilia De(Articulo articulo) => new(
        articulo.IdArea,
        articulo.IdCategoria,
        articulo.IdGrupo,
        articulo.IdProveedorHabitual,
        articulo.IdAlicuotaIva,
        articulo.UnidadVenta,
        articulo.UnidadesPorBulto,
        articulo.EsProducto,
        articulo.ControlaLote,
        articulo.CostoLista,
        articulo.DescuentoProveedor,
        articulo.CostoNominal);

    /// <summary>Copia SOLO los doce campos compartidos sobre <paramref name="articulo"/>. No toca
    /// ningún campo propio, la familia, el tenant ni los sellos de auditoría: quien persiste decide
    /// <c>UpdatedAt</c>.</summary>
    public void AplicarA(Articulo articulo)
    {
        articulo.IdArea = IdArea;
        articulo.IdCategoria = IdCategoria;
        articulo.IdGrupo = IdGrupo;
        articulo.IdProveedorHabitual = IdProveedorHabitual;
        articulo.IdAlicuotaIva = IdAlicuotaIva;
        articulo.UnidadVenta = UnidadVenta;
        articulo.UnidadesPorBulto = UnidadesPorBulto;
        articulo.EsProducto = EsProducto;
        articulo.ControlaLote = ControlaLote;
        articulo.CostoLista = CostoLista;
        articulo.DescuentoProveedor = DescuentoProveedor;
        articulo.CostoNominal = CostoNominal;
    }

    /// <summary>Nombres de columna (<c>id_area</c>, <c>costo_lista</c>, …) de los campos en que
    /// este registro difiere de <paramref name="otros"/>, en el orden de declaración. Vacío si son
    /// idénticos.</summary>
    public IReadOnlyList<string> CamposDistintos(ValoresCompartidosDeFamilia otros)
    {
        var distintos = new List<string>();

        if (IdArea != otros.IdArea)
        {
            distintos.Add("id_area");
        }

        if (IdCategoria != otros.IdCategoria)
        {
            distintos.Add("id_categoria");
        }

        if (IdGrupo != otros.IdGrupo)
        {
            distintos.Add("id_grupo");
        }

        if (IdProveedorHabitual != otros.IdProveedorHabitual)
        {
            distintos.Add("id_proveedor_habitual");
        }

        if (IdAlicuotaIva != otros.IdAlicuotaIva)
        {
            distintos.Add("id_alicuota_iva");
        }

        if (UnidadVenta != otros.UnidadVenta)
        {
            distintos.Add("unidad_venta");
        }

        if (UnidadesPorBulto != otros.UnidadesPorBulto)
        {
            distintos.Add("unidades_por_bulto");
        }

        if (EsProducto != otros.EsProducto)
        {
            distintos.Add("es_producto");
        }

        if (ControlaLote != otros.ControlaLote)
        {
            distintos.Add("controla_lote");
        }

        if (CostoLista != otros.CostoLista)
        {
            distintos.Add("costo_lista");
        }

        if (DescuentoProveedor != otros.DescuentoProveedor)
        {
            distintos.Add("descuento_proveedor");
        }

        if (CostoNominal != otros.CostoNominal)
        {
            distintos.Add("costo_nominal");
        }

        return distintos;
    }
}
