using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

/// <summary>
/// La regla pura de una familia que permite desglosar el costo: las cinco columnas de la composición dejan de ser
/// compartidas, el resto sigue igual y, sin la marca, nada cambia. Cada columna de la composición tiene su propio
/// caso: sacar una de <see cref="ValoresCompartidosDeFamilia.ColumnasDeLaComposicion"/> pone en rojo solo el suyo.
/// </summary>
public class DesgloseDeCostoDeFamiliaTests
{
    private static readonly string[] Composicion =
        ["costo_lista", "costo_lista_por", "costo_lista_incluye_iva", "descuento_proveedor", "impuesto_interno"];

    private static readonly string[] OtrosCompartidos =
    [
        "id_area", "id_categoria", "id_grupo", "id_proveedor_habitual", "id_alicuota_iva", "unidad_venta",
        "unidades_por_bulto", "es_producto", "controla_lote", "acumula_en_venta", "costo_nominal"
    ];

    private static readonly IReadOnlyDictionary<string, Action<Articulo>> Cambios =
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
            ["acumula_en_venta"] = a => a.AcumulaEnVenta = false,
            ["costo_lista"] = a => a.CostoLista = 150m,
            ["costo_lista_por"] = a => a.CostoListaPor = CostoListaPor.Bulto,
            ["costo_lista_incluye_iva"] = a => a.CostoListaIncluyeIva = false,
            ["descuento_proveedor"] = a => a.DescuentoProveedor = 15m,
            ["impuesto_interno"] = a => a.ImpuestoInterno = 3.5m,
            ["costo_nominal"] = a => a.CostoNominal = 130m
        };

    public static TheoryData<string> ColumnasDeLaComposicion() => DatosDe(Composicion);

    public static TheoryData<string> ColumnasQueSiguenCompartidas() => DatosDe(OtrosCompartidos);

    private static TheoryData<string> DatosDe(IEnumerable<string> valores)
    {
        var datos = new TheoryData<string>();

        foreach (var valor in valores)
        {
            datos.Add(valor);
        }

        return datos;
    }

    private static Articulo CrearArticulo(int id = 10) => new()
    {
        Id = id,
        IdTenant = 1,
        CodigoInterno = $"ART-{id}",
        Nombre = "Gaseosa cola 500 cc",
        IdArea = 1,
        IdCategoria = 2,
        IdGrupo = 4,
        IdProveedorHabitual = 5,
        IdAlicuotaIva = 6,
        UnidadVenta = UnidadVenta.Unidad,
        UnidadesPorBulto = 12m,
        EsProducto = true,
        ControlaLote = false,
        AcumulaEnVenta = true,
        CostoLista = 100m,
        CostoListaPor = CostoListaPor.Unidad,
        CostoListaIncluyeIva = true,
        DescuentoProveedor = 10m,
        ImpuestoInterno = 0m,
        CostoNominal = 90m,
        IdFamilia = 7
    };

    [Fact]
    public void LaListaDeColumnasDeLaComposicionSonLasCincoDeLaDecision()
    {
        Assert.Equal(Composicion, ValoresCompartidosDeFamilia.ColumnasDeLaComposicion);
    }

    [Theory]
    [MemberData(nameof(ColumnasDeLaComposicion))]
    public void SinLaMarcaUnaColumnaDeLaComposicionDistintaSigueSiendoUnCampoDistinto(string columna)
    {
        var otro = CrearArticulo();
        Cambios[columna](otro);

        Assert.Equal(
            [columna],
            ValoresCompartidosDeFamilia.De(CrearArticulo()).CamposDistintos(ValoresCompartidosDeFamilia.De(otro)));
        Assert.Equal(
            [columna],
            ValoresCompartidosDeFamilia.De(CrearArticulo())
                .CamposDistintos(ValoresCompartidosDeFamilia.De(otro), permiteDesglosarCosto: false));
    }

    [Theory]
    [MemberData(nameof(ColumnasDeLaComposicion))]
    public void ConLaMarcaUnaColumnaDeLaComposicionDistintaNoEsUnCampoDistinto(string columna)
    {
        var otro = CrearArticulo();
        Cambios[columna](otro);

        Assert.Empty(
            ValoresCompartidosDeFamilia.De(CrearArticulo())
                .CamposDistintos(ValoresCompartidosDeFamilia.De(otro), permiteDesglosarCosto: true));
    }

    [Theory]
    [MemberData(nameof(ColumnasQueSiguenCompartidas))]
    public void ConLaMarcaLosOtrosOnceCamposSiguenSiendoCompartidos(string columna)
    {
        var otro = CrearArticulo();
        Cambios[columna](otro);

        Assert.Equal(
            [columna],
            ValoresCompartidosDeFamilia.De(CrearArticulo())
                .CamposDistintos(ValoresCompartidosDeFamilia.De(otro), permiteDesglosarCosto: true));
    }

    [Fact]
    public void ConLaMarcaLaComposicionDistintaSoloDejaAlNominalComoCampoDistinto()
    {
        var otro = CrearArticulo();
        Cambios["costo_lista"](otro);
        Cambios["impuesto_interno"](otro);
        Cambios["costo_nominal"](otro);

        Assert.Equal(
            ["costo_nominal"],
            ValoresCompartidosDeFamilia.De(CrearArticulo())
                .CamposDistintos(ValoresCompartidosDeFamilia.De(otro), permiteDesglosarCosto: true));
    }

    [Fact]
    public void AplicarConConservarComposicionCopiaLosOnceYDejaLaComposicionDelDestino()
    {
        var origen = CrearArticulo(1);
        Cambios["id_area"](origen);
        Cambios["id_alicuota_iva"](origen);
        Cambios["unidades_por_bulto"](origen);
        Cambios["costo_nominal"](origen);
        Cambios["costo_lista"](origen);
        Cambios["costo_lista_por"](origen);
        Cambios["costo_lista_incluye_iva"](origen);
        Cambios["descuento_proveedor"](origen);
        Cambios["impuesto_interno"](origen);

        var destino = CrearArticulo(2);

        ValoresCompartidosDeFamilia.De(origen).AplicarA(destino, conservarComposicion: true);

        Assert.Equal(origen.IdArea, destino.IdArea);
        Assert.Equal(origen.IdAlicuotaIva, destino.IdAlicuotaIva);
        Assert.Equal(origen.UnidadesPorBulto, destino.UnidadesPorBulto);
        Assert.Equal(130m, destino.CostoNominal);
        Assert.Equal(100m, destino.CostoLista);
        Assert.Equal(CostoListaPor.Unidad, destino.CostoListaPor);
        Assert.True(destino.CostoListaIncluyeIva);
        Assert.Equal(10m, destino.DescuentoProveedor);
        Assert.Equal(0m, destino.ImpuestoInterno);
    }

    [Fact]
    public void AplicarSinConservarComposicionCopiaLosDieciseisComoSiempre()
    {
        var origen = CrearArticulo(1);
        foreach (var cambio in Cambios.Values)
        {
            cambio(origen);
        }

        var destino = CrearArticulo(2);

        ValoresCompartidosDeFamilia.De(origen).AplicarA(destino);

        Assert.Empty(ValoresCompartidosDeFamilia.De(origen).CamposDistintos(ValoresCompartidosDeFamilia.De(destino)));
    }

    [Fact]
    public void ConComposicionDeTomaSoloLaComposicionDelOtroRegistro()
    {
        var referencia = CrearArticulo(1);
        Cambios["id_area"](referencia);
        Cambios["costo_nominal"](referencia);
        var otro = CrearArticulo(2);
        foreach (var columna in Composicion)
        {
            Cambios[columna](otro);
        }

        var resultado = ValoresCompartidosDeFamilia.De(referencia).ConComposicionDe(ValoresCompartidosDeFamilia.De(otro));

        Assert.Equal(referencia.IdArea, resultado.IdArea);
        Assert.Equal(130m, resultado.CostoNominal);
        Assert.Equal(ComposicionDelCosto.De(otro), new ComposicionDelCosto(
            resultado.CostoLista, resultado.CostoListaPor, resultado.CostoListaIncluyeIva, resultado.DescuentoProveedor,
            resultado.ImpuestoInterno));
    }

    // ---- El ingreso de un artículo a una familia que permite desglosar el costo ----

    [Theory]
    [MemberData(nameof(ColumnasDeLaComposicion))]
    public void ConLaMarcaElIngresoConOtraComposicionYElMismoNominalEsPermitido(string columna)
    {
        var pedido = CrearArticulo(20);
        Cambios[columna](pedido);
        pedido.CostoNominal = 90m;

        Assert.Equal(
            ResolucionDeIngresoAFamilia.Permitido,
            ReglaDeFamilias.ResolverIngreso(
                true, ValoresCompartidosDeFamilia.De(CrearArticulo()), ValoresCompartidosDeFamilia.De(pedido),
                permiteDesglosarCosto: true));
    }

    [Fact]
    public void SinLaMarcaElIngresoConOtraComposicionSigueSiendoValoresDistintos()
    {
        var pedido = CrearArticulo(20);
        Cambios["impuesto_interno"](pedido);

        Assert.Equal(
            ResolucionDeIngresoAFamilia.ValoresDistintos,
            ReglaDeFamilias.ResolverIngreso(
                true, ValoresCompartidosDeFamilia.De(CrearArticulo()), ValoresCompartidosDeFamilia.De(pedido)));
    }

    [Fact]
    public void ConLaMarcaUnNominalDistintoConLosDemasCamposIgualesEsCostoNominalDistinto()
    {
        var pedido = CrearArticulo(20);
        Cambios["costo_lista"](pedido);
        Cambios["costo_nominal"](pedido);

        Assert.Equal(
            ResolucionDeIngresoAFamilia.CostoNominalDistinto,
            ReglaDeFamilias.ResolverIngreso(
                true, ValoresCompartidosDeFamilia.De(CrearArticulo()), ValoresCompartidosDeFamilia.De(pedido),
                permiteDesglosarCosto: true));
    }

    [Fact]
    public void ConLaMarcaUnNominalDistintoJuntoConOtroCampoCompartidoEsValoresDistintos()
    {
        var pedido = CrearArticulo(20);
        Cambios["costo_nominal"](pedido);
        Cambios["id_alicuota_iva"](pedido);

        Assert.Equal(
            ResolucionDeIngresoAFamilia.ValoresDistintos,
            ReglaDeFamilias.ResolverIngreso(
                true, ValoresCompartidosDeFamilia.De(CrearArticulo()), ValoresCompartidosDeFamilia.De(pedido),
                permiteDesglosarCosto: true));
    }

    [Fact]
    public void LaPrecedenciaDeLosRechazosNoCambiaConLaMarca()
    {
        var pedido = ValoresCompartidosDeFamilia.De(CrearArticulo(20));

        Assert.Equal(
            ResolucionDeIngresoAFamilia.FamiliaInactiva,
            ReglaDeFamilias.ResolverIngreso(false, null, pedido, permiteDesglosarCosto: true));
        Assert.Equal(
            ResolucionDeIngresoAFamilia.FamiliaSinArticulos,
            ReglaDeFamilias.ResolverIngreso(true, null, pedido, permiteDesglosarCosto: true));
    }

    // ---- Apagar la marca ----

    [Fact]
    public void SePuedeApagarLaMarcaSiTodosLosMiembrosTienenLaMismaComposicion()
    {
        var miembros = new[] { CrearArticulo(1), CrearArticulo(2), CrearArticulo(3) }.Select(ComposicionDelCosto.De);

        Assert.True(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto(miembros));
    }

    [Fact]
    public void SePuedeApagarLaMarcaSinMiembrosOConUnoSolo()
    {
        Assert.True(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto([]));
        Assert.True(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto([ComposicionDelCosto.De(CrearArticulo())]));
    }

    [Theory]
    [MemberData(nameof(ColumnasDeLaComposicion))]
    public void NoSePuedeApagarLaMarcaSiAlgunMiembroDifiereEnUnaColumnaDeLaComposicion(string columna)
    {
        var distinto = CrearArticulo(2);
        Cambios[columna](distinto);

        Assert.False(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto(
            [ComposicionDelCosto.De(CrearArticulo(1)), ComposicionDelCosto.De(distinto), ComposicionDelCosto.De(CrearArticulo(3))]));
    }

    [Fact]
    public void ElNominalYLosDemasCamposCompartidosNoCuentanParaApagarLaMarca()
    {
        var otro = CrearArticulo(2);
        Cambios["costo_nominal"](otro);
        Cambios["id_area"](otro);

        Assert.True(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto(
            [ComposicionDelCosto.De(CrearArticulo(1)), ComposicionDelCosto.De(otro)]));
    }

    [Fact]
    public void LaIgualdadDeLaComposicionIgnoraLaEscalaDelDecimal()
    {
        var uno = CrearArticulo(1);
        var otro = CrearArticulo(2);
        uno.CostoLista = 100m;
        otro.CostoLista = 100.00m;

        Assert.True(ReglaDeFamilias.PuedeApagarElDesgloseDeCosto([ComposicionDelCosto.De(uno), ComposicionDelCosto.De(otro)]));
    }
}
