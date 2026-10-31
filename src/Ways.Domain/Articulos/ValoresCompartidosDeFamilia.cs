namespace Ways.Domain.Articulos;

/// <summary>
/// Los dieciséis campos de <see cref="Articulo"/> que son IGUALES para todos los miembros de una
/// <see cref="Familia"/> (doc 10 §3). Regla pura, sin base de datos: <see cref="De"/> toma la foto de
/// un artículo, <see cref="AplicarA"/> la copia sobre otro y <see cref="CamposDistintos"/> nombra, con
/// el nombre de columna de la tabla <c>articulos</c>, qué campos difieren.
///
/// Lo que NO está acá es propio de cada artículo y nunca se copia ni se compara:
/// <c>nombre</c>, <c>descripcion</c>, <c>codigo_interno</c>, los códigos de barra, <c>id_marca</c>,
/// <c>activo</c>, <c>disponible_para_todas</c> (con su <c>articulos_empresas</c>) y la propia
/// <see cref="Articulo.IdFamilia"/>. El estado de precios de las listas fijas también es compartido,
/// pero vive en <c>precios</c> y lo replica quien escribe precios, no este registro.
///
/// <para>Composición del costo: cuando la familia permite desglosar el costo (<see cref="Familia.PermiteDesglosarCosto"/>),
/// cinco de los dieciséis campos pasan a ser PROPIOS de cada miembro —<c>costo_lista</c>, <c>costo_lista_por</c>,
/// <c>costo_lista_incluye_iva</c>, <c>descuento_proveedor</c> e <c>impuesto_interno</c>, ver
/// <see cref="ColumnasDeLaComposicion"/>— y <see cref="CamposDistintos"/> y <see cref="AplicarA"/> los dejan afuera
/// con su parámetro; los otros once, incluidos <c>id_alicuota_iva</c>, <c>unidades_por_bulto</c> y
/// <c>costo_nominal</c>, siguen compartidos. Sin la marca, el comportamiento es el de siempre.</para>
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
    bool AcumulaEnVenta,
    decimal? CostoLista,
    CostoListaPor CostoListaPor,
    bool CostoListaIncluyeIva,
    decimal? DescuentoProveedor,
    decimal ImpuestoInterno,
    decimal? CostoNominal)
{
    /// <summary>Las columnas de la composición del costo, en el orden de declaración de este registro: las que una
    /// familia que permite desglosar el costo deja de compartir.</summary>
    public static readonly IReadOnlyList<string> ColumnasDeLaComposicion =
    [
        "costo_lista",
        "costo_lista_por",
        "costo_lista_incluye_iva",
        "descuento_proveedor",
        "impuesto_interno"
    ];

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
        articulo.AcumulaEnVenta,
        articulo.CostoLista,
        articulo.CostoListaPor,
        articulo.CostoListaIncluyeIva,
        articulo.DescuentoProveedor,
        articulo.ImpuestoInterno,
        articulo.CostoNominal);

    /// <summary>Copia SOLO los dieciséis campos compartidos sobre <paramref name="articulo"/>. No toca
    /// ningún campo propio, la familia, el tenant ni los sellos de auditoría: quien persiste decide
    /// <c>UpdatedAt</c>. Con <paramref name="conservarComposicion"/> copia solo los once que una familia que permite
    /// desglosar el costo sigue compartiendo y deja la composición del costo (<see cref="ColumnasDeLaComposicion"/>)
    /// del artículo como está.</summary>
    public void AplicarA(Articulo articulo, bool conservarComposicion = false)
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
        articulo.AcumulaEnVenta = AcumulaEnVenta;
        articulo.CostoNominal = CostoNominal;

        if (conservarComposicion)
        {
            return;
        }

        articulo.CostoLista = CostoLista;
        articulo.CostoListaPor = CostoListaPor;
        articulo.CostoListaIncluyeIva = CostoListaIncluyeIva;
        articulo.DescuentoProveedor = DescuentoProveedor;
        articulo.ImpuestoInterno = ImpuestoInterno;
    }

    /// <summary>Este registro con la composición del costo de <paramref name="otros"/>: lo que queda guardado en un
    /// artículo de una familia que permite desglosar el costo cuando se le aplica este registro con
    /// <c>conservarComposicion</c> (<see cref="AplicarA"/>).</summary>
    public ValoresCompartidosDeFamilia ConComposicionDe(ValoresCompartidosDeFamilia otros) => this with
    {
        CostoLista = otros.CostoLista,
        CostoListaPor = otros.CostoListaPor,
        CostoListaIncluyeIva = otros.CostoListaIncluyeIva,
        DescuentoProveedor = otros.DescuentoProveedor,
        ImpuestoInterno = otros.ImpuestoInterno
    };

    /// <summary>Nombres de columna (<c>id_area</c>, <c>costo_lista</c>, …) de los campos en que
    /// este registro difiere de <paramref name="otros"/>, en el orden de declaración. Vacío si son
    /// idénticos. Con <paramref name="permiteDesglosarCosto"/> la composición del costo es propia de cada artículo y
    /// no se compara (<see cref="ColumnasDeLaComposicion"/>); <c>costo_nominal</c> sí.</summary>
    public IReadOnlyList<string> CamposDistintos(ValoresCompartidosDeFamilia otros, bool permiteDesglosarCosto = false)
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

        if (AcumulaEnVenta != otros.AcumulaEnVenta)
        {
            distintos.Add("acumula_en_venta");
        }

        if (CostoLista != otros.CostoLista)
        {
            distintos.Add("costo_lista");
        }

        if (CostoListaPor != otros.CostoListaPor)
        {
            distintos.Add("costo_lista_por");
        }

        if (CostoListaIncluyeIva != otros.CostoListaIncluyeIva)
        {
            distintos.Add("costo_lista_incluye_iva");
        }

        if (DescuentoProveedor != otros.DescuentoProveedor)
        {
            distintos.Add("descuento_proveedor");
        }

        if (ImpuestoInterno != otros.ImpuestoInterno)
        {
            distintos.Add("impuesto_interno");
        }

        if (CostoNominal != otros.CostoNominal)
        {
            distintos.Add("costo_nominal");
        }

        return permiteDesglosarCosto
            ? [.. distintos.Where(columna => !ColumnasDeLaComposicion.Contains(columna))]
            : distintos;
    }
}
