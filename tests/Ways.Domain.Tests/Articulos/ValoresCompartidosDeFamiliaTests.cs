using System.Reflection;
using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

/// <summary>
/// Regla pura de qué campos de <see cref="Articulo"/> comparten los miembros de una
/// <see cref="Familia"/>. Cada una de las doce columnas compartidas tiene su propio caso: sacar una
/// del registro, de <see cref="ValoresCompartidosDeFamilia.AplicarA"/> o de
/// <see cref="ValoresCompartidosDeFamilia.CamposDistintos"/> pone en rojo exactamente el caso de esa
/// columna (un caso representativo no probaría las otras once). Los campos propios se prueban
/// aparte: ni se detectan ni se copian.
/// </summary>
public class ValoresCompartidosDeFamiliaTests
{
    /// <summary>Las doce columnas de <c>articulos</c> compartidas por la familia, en el orden en que
    /// <see cref="ValoresCompartidosDeFamilia.CamposDistintos"/> las informa. Escritas a mano a
    /// propósito: pinean los nombres que <c>CamposDistintos</c> devuelve. Que cada uno sea una columna
    /// real de <c>articulos</c> lo comprueba <c>ModeloDeArticulosYPreciosTests</c> contra el modelo de EF.</summary>
    private static readonly string[] ColumnasCompartidas =
    [
        "id_area",
        "id_categoria",
        "id_grupo",
        "id_proveedor_habitual",
        "id_alicuota_iva",
        "unidad_venta",
        "unidades_por_bulto",
        "es_producto",
        "controla_lote",
        "costo_lista",
        "descuento_proveedor",
        "costo_nominal"
    ];

    private static readonly string[] ColumnasCompartidasNulables =
    [
        "id_categoria",
        "id_grupo",
        "id_proveedor_habitual",
        "unidades_por_bulto",
        "costo_lista",
        "descuento_proveedor",
        "costo_nominal"
    ];

    private static readonly string[] CamposPropios =
    [
        "codigo_interno",
        "nombre",
        "descripcion",
        "id_marca",
        "activo",
        "disponible_para_todas"
    ];

    /// <summary>Cambia UNA sola columna compartida a un valor distinto del de <see cref="CrearArticulo"/>.</summary>
    private static readonly IReadOnlyDictionary<string, Action<Articulo>> CambiosCompartidos =
        new Dictionary<string, Action<Articulo>>
        {
            ["id_area"] = a => a.IdArea = 91,
            ["id_categoria"] = a => a.IdCategoria = 92,
            ["id_grupo"] = a => a.IdGrupo = 94,
            ["id_proveedor_habitual"] = a => a.IdProveedorHabitual = 95,
            ["id_alicuota_iva"] = a => a.IdAlicuotaIva = 96,
            ["unidad_venta"] = a => a.UnidadVenta = UnidadVenta.Peso,
            ["unidades_por_bulto"] = a => a.UnidadesPorBulto = 24m,
            ["es_producto"] = a => a.EsProducto = false,
            ["controla_lote"] = a => a.ControlaLote = true,
            ["costo_lista"] = a => a.CostoLista = 150m,
            ["descuento_proveedor"] = a => a.DescuentoProveedor = 15m,
            ["costo_nominal"] = a => a.CostoNominal = 130m
        };

    /// <summary>Deja en <c>null</c> UNA sola columna compartida nulable.</summary>
    private static readonly IReadOnlyDictionary<string, Action<Articulo>> CambiosANulo =
        new Dictionary<string, Action<Articulo>>
        {
            ["id_categoria"] = a => a.IdCategoria = null,
            ["id_grupo"] = a => a.IdGrupo = null,
            ["id_proveedor_habitual"] = a => a.IdProveedorHabitual = null,
            ["unidades_por_bulto"] = a => a.UnidadesPorBulto = null,
            ["costo_lista"] = a => a.CostoLista = null,
            ["descuento_proveedor"] = a => a.DescuentoProveedor = null,
            ["costo_nominal"] = a => a.CostoNominal = null
        };

    /// <summary>Cambia UNA sola columna propia del artículo.</summary>
    private static readonly IReadOnlyDictionary<string, Action<Articulo>> CambiosPropios =
        new Dictionary<string, Action<Articulo>>
        {
            ["codigo_interno"] = a => a.CodigoInterno = "OTRO-COD",
            ["nombre"] = a => a.Nombre = "Otro nombre",
            ["descripcion"] = a => a.Descripcion = "Otra descripción",
            ["id_marca"] = a => a.IdMarca = 93,
            ["activo"] = a => a.Activo = false,
            ["disponible_para_todas"] = a => a.DisponibleParaTodas = false
        };

    public static TheoryData<string> CamposCompartidos() => DatosDe(ColumnasCompartidas);

    public static TheoryData<string> CamposCompartidosNulables() => DatosDe(ColumnasCompartidasNulables);

    public static TheoryData<string> CamposPropiosDelArticulo() => DatosDe(CamposPropios);

    private static TheoryData<string> DatosDe(IEnumerable<string> valores)
    {
        var datos = new TheoryData<string>();

        foreach (var valor in valores)
        {
            datos.Add(valor);
        }

        return datos;
    }

    /// <summary>Todos los campos compartidos con un valor no nulo y distinto de los de
    /// <see cref="CambiosCompartidos"/>, y todos los propios con valores que ningún caso comparte.</summary>
    private static Articulo CrearArticulo() => new()
    {
        Id = 10,
        IdTenant = 1,
        CodigoInterno = "ART-10",
        Nombre = "Gaseosa cola 500 cc",
        Descripcion = "Botella descartable",
        IdArea = 1,
        IdCategoria = 2,
        IdMarca = 3,
        IdGrupo = 4,
        IdProveedorHabitual = 5,
        IdAlicuotaIva = 6,
        UnidadVenta = UnidadVenta.Unidad,
        UnidadesPorBulto = 12m,
        EsProducto = true,
        ControlaLote = false,
        CostoLista = 100m,
        DescuentoProveedor = 10m,
        CostoNominal = 90m,
        DisponibleParaTodas = true,
        Activo = true,
        IdFamilia = 7,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
    };

    /// <summary>Con los doce campos distintos, <see cref="ValoresCompartidosDeFamilia.CamposDistintos"/>
    /// informa exactamente los nombres de <see cref="ColumnasCompartidas"/>, en el orden de
    /// declaración, y hay un cambio por cada nombre. Compara contra la lista escrita en esta clase: no
    /// consulta la tabla ni el modelo de EF.</summary>
    [Fact]
    public void CamposDistintosInformaLasDoceColumnasDeLaPruebaEnOrdenDeDeclaracion()
    {
        Assert.Equal(12, ColumnasCompartidas.Length);
        Assert.Equal(ColumnasCompartidas.Order(), CambiosCompartidos.Keys.Order());

        var todosDistintos = CrearArticulo();
        foreach (var cambio in CambiosCompartidos.Values)
        {
            cambio(todosDistintos);
        }

        Assert.Equal(
            ColumnasCompartidas,
            ValoresCompartidosDeFamilia.De(CrearArticulo())
                .CamposDistintos(ValoresCompartidosDeFamilia.De(todosDistintos)));
    }

    [Fact]
    public void DosArticulosConLosMismosValoresCompartidosNoTienenCamposDistintos()
    {
        var uno = ValoresCompartidosDeFamilia.De(CrearArticulo());
        var otro = ValoresCompartidosDeFamilia.De(CrearArticulo());

        Assert.Empty(uno.CamposDistintos(otro));
        Assert.Equal(uno, otro);
    }

    [Theory]
    [MemberData(nameof(CamposCompartidos))]
    public void UnCampoCompartidoDistintoSeDetectaYSoloEse(string columna)
    {
        var otro = CrearArticulo();
        CambiosCompartidos[columna](otro);

        var distintos = ValoresCompartidosDeFamilia.De(CrearArticulo())
            .CamposDistintos(ValoresCompartidosDeFamilia.De(otro));

        Assert.Equal([columna], distintos);
    }

    /// <summary>Que un lado tenga valor y el otro <c>null</c> es una diferencia, en las dos
    /// direcciones: un valor limpiado en un miembro no puede pasar por igual.</summary>
    [Theory]
    [MemberData(nameof(CamposCompartidosNulables))]
    public void UnCampoCompartidoNuloContraUnValorSeDetectaEnAmbasDirecciones(string columna)
    {
        var conNulo = CrearArticulo();
        CambiosANulo[columna](conNulo);
        var conValor = CrearArticulo();

        Assert.Equal(
            [columna],
            ValoresCompartidosDeFamilia.De(conValor).CamposDistintos(ValoresCompartidosDeFamilia.De(conNulo)));
        Assert.Equal(
            [columna],
            ValoresCompartidosDeFamilia.De(conNulo).CamposDistintos(ValoresCompartidosDeFamilia.De(conValor)));
    }

    [Theory]
    [MemberData(nameof(CamposCompartidos))]
    public void AplicarACopiaElCampoCompartido(string columna)
    {
        var origen = CrearArticulo();
        CambiosCompartidos[columna](origen);
        var destino = CrearArticulo();

        ValoresCompartidosDeFamilia.De(origen).AplicarA(destino);

        Assert.Empty(ValoresCompartidosDeFamilia.De(origen).CamposDistintos(ValoresCompartidosDeFamilia.De(destino)));
        Assert.Equal(ValoresCompartidosDeFamilia.De(origen), ValoresCompartidosDeFamilia.De(destino));
    }

    /// <summary>Copiar un <c>null</c> es copiar: un campo limpiado en un miembro tiene que quedar
    /// limpio en todos, no conservar el valor viejo.</summary>
    [Theory]
    [MemberData(nameof(CamposCompartidosNulables))]
    public void AplicarACopiaTambienUnNuloSobreUnValor(string columna)
    {
        var origen = CrearArticulo();
        CambiosANulo[columna](origen);
        var destino = CrearArticulo();

        ValoresCompartidosDeFamilia.De(origen).AplicarA(destino);

        Assert.Empty(ValoresCompartidosDeFamilia.De(origen).CamposDistintos(ValoresCompartidosDeFamilia.De(destino)));
        Assert.Equal(ValoresCompartidosDeFamilia.De(origen), ValoresCompartidosDeFamilia.De(destino));
    }

    [Theory]
    [MemberData(nameof(CamposPropiosDelArticulo))]
    public void UnCampoPropioDistintoNoSeDetectaComoDiferencia(string campo)
    {
        var otro = CrearArticulo();
        CambiosPropios[campo](otro);

        var distintos = ValoresCompartidosDeFamilia.De(CrearArticulo())
            .CamposDistintos(ValoresCompartidosDeFamilia.De(otro));

        Assert.Empty(distintos);
    }

    /// <summary>Origen y destino difieren en TODO (los doce compartidos y los seis propios, más la
    /// familia): después de aplicar, el destino tiene los doce del origen y conserva intactos sus
    /// propios, su familia, su identidad y sus sellos de auditoría.</summary>
    [Fact]
    public void AplicarACopiaSoloLosDoceCamposCompartidosYNoTocaLosPropios()
    {
        var origen = CrearArticulo();
        foreach (var cambio in CambiosCompartidos.Values.Concat(CambiosPropios.Values))
        {
            cambio(origen);
        }

        origen.IdFamilia = 70;
        origen.IdTenant = 2;
        origen.Id = 99;

        var destino = CrearArticulo();
        var antes = CrearArticulo();

        ValoresCompartidosDeFamilia.De(origen).AplicarA(destino);

        Assert.Equal(ValoresCompartidosDeFamilia.De(origen), ValoresCompartidosDeFamilia.De(destino));

        Assert.Equal(antes.CodigoInterno, destino.CodigoInterno);
        Assert.Equal(antes.Nombre, destino.Nombre);
        Assert.Equal(antes.Descripcion, destino.Descripcion);
        Assert.Equal(antes.IdMarca, destino.IdMarca);
        Assert.Equal(antes.Activo, destino.Activo);
        Assert.Equal(antes.DisponibleParaTodas, destino.DisponibleParaTodas);

        Assert.Equal(antes.IdFamilia, destino.IdFamilia);
        Assert.Equal(antes.Id, destino.Id);
        Assert.Equal(antes.IdTenant, destino.IdTenant);
        Assert.Equal(antes.CreatedAt, destino.CreatedAt);
        Assert.Equal(antes.UpdatedAt, destino.UpdatedAt);
        Assert.Equal(antes.DeletedAt, destino.DeletedAt);
    }

    /// <summary>Red de clasificación: toda propiedad pública de <see cref="Articulo"/> es compartida
    /// (está en <see cref="ValoresCompartidosDeFamilia"/>), propia o estructural. Una columna nueva en
    /// <c>articulos</c> rompe esta prueba hasta que alguien decida a qué grupo pertenece — sin ella, un
    /// campo agregado después quedaría fuera de la familia sin que nadie lo haya resuelto.</summary>
    [Fact]
    public void TodaPropiedadDeArticuloEstaClasificada()
    {
        string[] compartidas =
        [
            nameof(Articulo.IdArea),
            nameof(Articulo.IdCategoria),
            nameof(Articulo.IdGrupo),
            nameof(Articulo.IdProveedorHabitual),
            nameof(Articulo.IdAlicuotaIva),
            nameof(Articulo.UnidadVenta),
            nameof(Articulo.UnidadesPorBulto),
            nameof(Articulo.EsProducto),
            nameof(Articulo.ControlaLote),
            nameof(Articulo.CostoLista),
            nameof(Articulo.DescuentoProveedor),
            nameof(Articulo.CostoNominal)
        ];

        string[] propias =
        [
            nameof(Articulo.CodigoInterno),
            nameof(Articulo.Nombre),
            nameof(Articulo.Descripcion),
            nameof(Articulo.IdMarca),
            nameof(Articulo.Activo),
            nameof(Articulo.DisponibleParaTodas)
        ];

        string[] estructurales =
        [
            nameof(Articulo.Id),
            nameof(Articulo.IdTenant),
            nameof(Articulo.IdFamilia),
            nameof(Articulo.CreatedAt),
            nameof(Articulo.UpdatedAt),
            nameof(Articulo.DeletedAt),
            nameof(Articulo.EstaEliminada)
        ];

        var delRegistro = typeof(ValoresCompartidosDeFamilia)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order()
            .ToList();

        Assert.Equal(compartidas.Order(), delRegistro);

        var delArticulo = typeof(Articulo)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        var sinClasificar = delArticulo.Except(compartidas).Except(propias).Except(estructurales).ToList();

        Assert.True(
            sinClasificar.Count == 0,
            "Estas propiedades de Articulo no están clasificadas como compartidas, propias ni " +
            $"estructurales: {string.Join(", ", sinClasificar)}. Decidir si los miembros de una " +
            "familia deben compartirlas y reflejarlo en ValoresCompartidosDeFamilia.");

        Assert.Empty(compartidas.Intersect(propias));
        Assert.Empty(compartidas.Intersect(estructurales));
        Assert.Empty(propias.Intersect(estructurales));
    }
}
