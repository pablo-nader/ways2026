using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>E6-S4: qué costos y qué códigos de proveedor vuelve a escribir la edición de una compra confirmada.</summary>
public class CalculadorDeEdicionDeCompraCostosTests
{
    private static readonly IReadOnlyDictionary<int, int> SinFamilias = new Dictionary<int, int>();

    private static LineaDeCostoDeCompra Linea(int orden, int articulo, decimal efectivo, bool actualiza = true, decimal unitario = 10m) =>
        new(orden, articulo, actualiza, unitario, efectivo);

    // ---- CostosQueCambian ------------------------------------------------------------------------------------

    /// <summary>Cláusula bajo prueba: comparar con la versión anterior. Si se borrara, una edición de cantidades (mismo costo
    /// efectivo) reescribiría el costo de todas las líneas y esta aserción vería una actualización.</summary>
    [Fact]
    public void UnaEdicionQueNoCambiaElCostoEfectivoNoActualizaNada()
    {
        var resultado = CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 5, 12m), Linea(2, 6, 7m)], [Linea(1, 5, 12m), Linea(2, 6, 7m)], SinFamilias);

        Assert.Empty(resultado);
    }

    [Fact]
    public void SoloSeActualizaElArticuloCuyoCostoEfectivoCambio()
    {
        var resultado = CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 5, 12m), Linea(2, 6, 7m)], [Linea(1, 5, 12m), Linea(2, 6, 8m)], SinFamilias);

        Assert.Equal(new ActualizacionDeCosto(null, 6, 8m), Assert.Single(resultado));
    }

    [Fact]
    public void UnaLineaNuevaQueActualizaElCostoSeEscribe()
    {
        var resultado = CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 5, 12m)], [Linea(1, 5, 12m), Linea(2, 6, 7m)], SinFamilias);

        Assert.Equal(new ActualizacionDeCosto(null, 6, 7m), Assert.Single(resultado));
    }

    [Fact]
    public void UnaLineaQueDejaDeActualizarOQueDesapareceNoRevierteElCosto()
    {
        Assert.Empty(CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 5, 12m)], [Linea(1, 5, 12m, actualiza: false)], SinFamilias));
        Assert.Empty(CalculadorDeEdicionDeCompra.CostosQueCambian([Linea(1, 5, 12m)], [], SinFamilias));
    }

    [Fact]
    public void UnaLineaQueAhoraActualizaElCostoSeEscribeAunqueElImporteNoCambie()
    {
        var resultado = CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 5, 12m, actualiza: false)], [Linea(1, 5, 12m)], SinFamilias);

        Assert.Equal(new ActualizacionDeCosto(null, 5, 12m), Assert.Single(resultado));
    }

    /// <summary>El guard anti-bonificación sigue valiendo: un costo efectivo cero no se escribe.</summary>
    [Fact]
    public void UnCostoEfectivoCeroNuncaSeEscribe() =>
        Assert.Empty(CalculadorDeEdicionDeCompra.CostosQueCambian([Linea(1, 5, 12m)], [Linea(1, 5, 0m)], SinFamilias));

    /// <summary>Por familia gana la línea de mayor orden entre TODAS las de la compra editada, no entre las que cambiaron:
    /// el artículo 5 (orden 2) sigue ganando aunque solo cambie el costo del 6 (orden 1), y como el costo de la familia no
    /// cambia, no se escribe nada.</summary>
    [Fact]
    public void LaFamiliaSeResuelveConElMismoDedupeDeLaConfirmacion()
    {
        var familias = new Dictionary<int, int> { [5] = 100, [6] = 100 };

        Assert.Empty(CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 6, 9m), Linea(2, 5, 12m)], [Linea(1, 6, 10m), Linea(2, 5, 12m)], familias));

        var cambia = CalculadorDeEdicionDeCompra.CostosQueCambian(
            [Linea(1, 6, 9m), Linea(2, 5, 12m)], [Linea(1, 6, 9m), Linea(2, 5, 13m)], familias);
        Assert.Equal(new ActualizacionDeCosto(100, 5, 13m), Assert.Single(cambia));
    }

    // ---- SinLosPisadosPorUnaCompraMasNueva --------------------------------------------------------------------

    [Fact]
    public void SinCompraMasNuevaTodoSeAplica()
    {
        var (aplicables, omitidos) = CalculadorDeEdicionDeCompra.SinLosPisadosPorUnaCompraMasNueva(
            [(5, 12m), (6, 7m)], new HashSet<int>(), SinFamilias);

        Assert.Equal(2, aplicables.Count);
        Assert.Empty(omitidos);
    }

    [Fact]
    public void UnArticuloConLineaPosteriorSeOmiteYLosDemasSeAplican()
    {
        var (aplicables, omitidos) = CalculadorDeEdicionDeCompra.SinLosPisadosPorUnaCompraMasNueva(
            [(5, 12m), (6, 7m)], new HashSet<int> { 6 }, SinFamilias);

        Assert.Equal((5, 12m), Assert.Single(aplicables));
        Assert.Equal([6], omitidos);
    }

    /// <summary>La compra más nueva domina a la familia entera aunque su línea sea de otro miembro.</summary>
    [Fact]
    public void LaLineaPosteriorDeUnMiembroOmiteATodaLaFamilia()
    {
        var familias = new Dictionary<int, int> { [5] = 100, [6] = 100, [7] = 100, [8] = 200 };

        var (aplicables, omitidos) = CalculadorDeEdicionDeCompra.SinLosPisadosPorUnaCompraMasNueva(
            [(5, 12m), (6, 12m), (8, 3m)], new HashSet<int> { 7 }, familias);

        Assert.Equal((8, 3m), Assert.Single(aplicables));
        Assert.Equal([5, 6], omitidos);
    }

    // ---- CodigosDeProveedorAAsociar ---------------------------------------------------------------------------

    private static LineaConCodigoDeProveedor Cod(int orden, int? articulo, string? codigo) => new(orden, articulo, codigo);

    [Fact]
    public void SoloSeAsociaElCodigoNuevoOCambiado()
    {
        var resultado = CalculadorDeEdicionDeCompra.CodigosDeProveedorAAsociar(
            [Cod(1, 5, "A-1")], [Cod(1, 5, "A-1"), Cod(2, 6, "B-2")], cambiaElProveedor: false);

        Assert.Equal(new AsociacionCandidata(6, "B-2"), Assert.Single(resultado));
    }

    [Fact]
    public void UnCodigoQueSeMueveDeArticuloSeVuelveAAsociar()
    {
        var resultado = CalculadorDeEdicionDeCompra.CodigosDeProveedorAAsociar(
            [Cod(1, 5, "A-1")], [Cod(1, 6, "A-1")], cambiaElProveedor: false);

        Assert.Equal(new AsociacionCandidata(6, "A-1"), Assert.Single(resultado));
    }

    /// <summary>Los códigos son del proveedor: si la compra cambia de proveedor, todos los pares se vuelven a intentar.</summary>
    [Fact]
    public void ConOtroProveedorSeIntentanTodosLosCodigos()
    {
        var resultado = CalculadorDeEdicionDeCompra.CodigosDeProveedorAAsociar(
            [Cod(1, 5, "A-1")], [Cod(1, 5, "A-1")], cambiaElProveedor: true);

        Assert.Equal(new AsociacionCandidata(5, "A-1"), Assert.Single(resultado));
    }

    [Fact]
    public void QuitarUnaLineaNoProduceCandidatosNiQuitaCodigos() =>
        Assert.Empty(CalculadorDeEdicionDeCompra.CodigosDeProveedorAAsociar(
            [Cod(1, 5, "A-1")], [], cambiaElProveedor: false));
}
