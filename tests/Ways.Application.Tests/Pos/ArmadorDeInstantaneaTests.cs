using Ways.Application.Ofertas;
using Ways.Application.Pos;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using static Ways.Application.Pos.ArmadorDeInstantanea;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Parte pura del armado de la instantánea (<see cref="ArmadorDeInstantanea"/>): selección de
/// listas, FK de cliente a una lista dada de baja, precios por lista, el formato original derivado
/// y la etiqueta de contenido. Los valores de cada campo son distintos entre sí a propósito, para
/// que un intercambio de columnas o de listas no pase desapercibido.
/// </summary>
public class ArmadorDeInstantaneaTests
{
    private const int IdEmpresa = 10;

    private static ListaVisible Fija(int id, int? idEmpresa = null, bool activo = true) =>
        new(id, idEmpresa, activo, ModoLista.Fija, null, null);

    private static ListaVisible Derivada(int id, int? idBase, decimal? porcentaje = 10m) =>
        new(id, null, true, ModoLista.Derivada, idBase, porcentaje);

    [Fact]
    public void ListasAResolverTomaLasActivasDeLaEmpresaYLasQueReferencianLosClientes()
    {
        var visibles = new[]
        {
            Fija(5),                              // activa compartida
            Fija(3, idEmpresa: IdEmpresa),        // activa de la empresa
            Fija(4, idEmpresa: 99),               // activa de otra empresa, sin clientes
            Fija(6, activo: false),               // inactiva, sin clientes
            Fija(7, activo: false),               // inactiva, referenciada por un cliente
            Fija(8, idEmpresa: 99, activo: true), // de otra empresa, referenciada
        };

        var listas = ListasAResolver(visibles, IdEmpresa, [7, null, 8, 5]);

        Assert.Equal([3, 5, 7, 8], listas);
    }

    [Fact]
    public void ListasAResolverDescartaLaDerivadaSinPorcentajeOSinUnaBaseFijaVisible()
    {
        var visibles = new[]
        {
            Fija(1),
            Derivada(2, idBase: 1),   // base fija visible
            Derivada(3, idBase: 404), // base dada de baja (no visible)
            Derivada(4, idBase: 2),   // base derivada
            Derivada(5, idBase: null),
            Derivada(6, idBase: 1, porcentaje: null), // sin porcentaje
        };

        var listas = ListasAResolver(visibles, IdEmpresa, []);

        Assert.Equal([1, 2], listas);
    }

    [Fact]
    public void UnaListaFueraDeLasVisiblesSeProyectaComoNula()
    {
        var visibles = new HashSet<int> { 1, 2 };

        Assert.Equal(2, ListaEfectiva(2, visibles));
        Assert.Null(ListaEfectiva(3, visibles));
    }

    [Fact]
    public void LasLineasVanArticuloPorArticuloYListaPorListaACantidadUno()
    {
        var articulos = new[] { new ArticuloAResolver(11, "a", "A", 1, true, UnidadVenta.Unidad), new ArticuloAResolver(12, "b", "B", 1, true, UnidadVenta.Unidad) };

        var lineas = LineasDeResolucion(articulos, [3, 5], IdEmpresa);

        Assert.Equal(
            [
                new LineaDeResolucion(11, IdEmpresa, 3, 1m),
                new LineaDeResolucion(11, IdEmpresa, 5, 1m),
                new LineaDeResolucion(12, IdEmpresa, 3, 1m),
                new LineaDeResolucion(12, IdEmpresa, 5, 1m),
            ],
            lineas);
    }

    private static ResultadoDeResolucionConEscalones Resuelto(
        int idArticulo, int idLista, decimal? original, decimal? final, decimal descuento,
        IReadOnlyList<OfertaAplicadaDto>? aplicadas = null, IReadOnlyList<EscalonDeCantidad>? escalones = null) =>
        new(new ResultadoDeResolucion(idArticulo, idLista, original, final, descuento, aplicadas ?? []), escalones ?? []);

    [Fact]
    public void ArmarArticulosTraeElPrecioDeCadaListaYOmiteLoQueNoTienePrecio()
    {
        var articulos = new[]
        {
            new ArticuloAResolver(11, "A11", "Arroz", 21, false, UnidadVenta.Unidad),
            new ArticuloAResolver(12, "A12", "Fideos", 22, true, UnidadVenta.Peso),
            new ArticuloAResolver(13, "A13", "Sin precio", 21, true, UnidadVenta.Unidad),
        };
        var aplicadaEnB = new OfertaAplicadaDto(70, "Promo B", 15m);
        var escalonEnA = new EscalonDeCantidad(6m, 90m, 10m, [new OfertaAplicadaDto(71, "Seis", 10m)]);
        var resolucion = new[]
        {
            Resuelto(11, 3, 100m, 100m, 0m, escalones: [escalonEnA]),
            Resuelto(11, 5, 150m, 135m, 15m, aplicadas: [aplicadaEnB]),
            Resuelto(12, 3, null, null, 0m),
            Resuelto(12, 5, 40m, null, 0m),
            Resuelto(13, 3, null, null, 0m),
            Resuelto(13, 5, null, null, 0m),
        };
        var codigos = new Dictionary<int, IReadOnlyList<string>> { [11] = ["7790011"] };
        var porcentajes = new Dictionary<int, decimal> { [21] = 21m, [22] = 10.5m };

        var resultado = ArmarArticulos(articulos, [3, 5], resolucion, codigos, porcentajes);

        Assert.Equal(2, resultado.Count);

        var arroz = resultado[0];
        Assert.Equal(11, arroz.IdArticulo);
        Assert.Equal("A11", arroz.CodigoInterno);
        Assert.Equal("Arroz", arroz.Nombre);
        Assert.Equal(["7790011"], arroz.CodigosBarra);
        Assert.Equal(21, arroz.IdAlicuotaIva);
        Assert.Equal(21m, arroz.PorcentajeIva);
        Assert.False(arroz.AcumulaEnVenta);
        Assert.True(resultado[1].AcumulaEnVenta);
        Assert.Equal(UnidadVenta.Unidad, arroz.UnidadVenta);
        Assert.Equal(2, arroz.PreciosPorLista.Count);

        var arrozEnA = arroz.PreciosPorLista[0];
        Assert.Equal(3, arrozEnA.IdListaPrecio);
        Assert.Equal(100m, arrozEnA.PrecioOriginal);
        Assert.Equal(100m, arrozEnA.PrecioFinal);
        Assert.Equal(0m, arrozEnA.DescuentoUnitario);
        Assert.Empty(arrozEnA.Aplicadas);
        Assert.Equal([escalonEnA], arrozEnA.Escalones);

        var arrozEnB = arroz.PreciosPorLista[1];
        Assert.Equal(5, arrozEnB.IdListaPrecio);
        Assert.Equal(150m, arrozEnB.PrecioOriginal);
        Assert.Equal(135m, arrozEnB.PrecioFinal);
        Assert.Equal(15m, arrozEnB.DescuentoUnitario);
        Assert.Equal([aplicadaEnB], arrozEnB.Aplicadas);
        Assert.Null(arrozEnB.Escalones);

        var fideos = resultado[1];
        Assert.Equal(12, fideos.IdArticulo);
        Assert.Empty(fideos.CodigosBarra);
        Assert.Equal(22, fideos.IdAlicuotaIva);
        Assert.Equal(10.5m, fideos.PorcentajeIva);
        Assert.Equal(UnidadVenta.Peso, fideos.UnidadVenta);
        var fideosEnB = Assert.Single(fideos.PreciosPorLista);
        Assert.Equal(5, fideosEnB.IdListaPrecio);
        Assert.Equal(40m, fideosEnB.PrecioOriginal);
        Assert.Equal(40m, fideosEnB.PrecioFinal);
    }

    [Fact]
    public void ArmarArticulosRechazaUnaResolucionDeOtroTamanio()
    {
        var articulos = new[] { new ArticuloAResolver(11, "A11", "Arroz", 21, true, UnidadVenta.Unidad) };

        Assert.Throws<InvalidOperationException>(() => ArmarArticulos(
            articulos, [3, 5], [Resuelto(11, 3, 1m, 1m, 0m)],
            new Dictionary<int, IReadOnlyList<string>>(), new Dictionary<int, decimal> { [21] = 21m }));
    }

    private static ClienteDeInstantanea Cliente(int id, int numero, int? idLista, decimal saldo = 0m) =>
        new(id, numero, $"Cliente {id}", null, null, null, null, 1, null, idLista, numero == 1, saldo, 0m, false);

    private static PrecioDeListaDeInstantanea Precio(int idLista, decimal original, decimal final, decimal descuento) =>
        new(idLista, original, final, descuento, []);

    private static InstantaneaDePos Instantanea(
        IReadOnlyList<ArticuloDeInstantanea> articulos, IReadOnlyList<ClienteDeInstantanea> clientes) =>
        new(
            new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero),
            7,
            articulos,
            clientes,
            [new MedioPagoDeInstantanea(1, "Efectivo", ComportamientoMedioPago.Efectivo, true, false)],
            10m);


    [Fact]
    public void ParaElFormatoOriginalSoloQuedaLaListaDelConsumidorFinal()
    {
        IReadOnlyList<int> idsLista = [3, 5, 8];

        Assert.Equal([5], SoloListaDelConsumidorFinal(idsLista, [Cliente(2, 2, idLista: 3), Cliente(1, 1, idLista: 5)]));
        Assert.Empty(SoloListaDelConsumidorFinal(idsLista, [Cliente(1, 1, idLista: null)]));
        Assert.Empty(SoloListaDelConsumidorFinal(idsLista, [Cliente(1, 1, idLista: 99)]));
    }

    [Fact]
    public void ElFormatoOriginalTomaLaListaDelConsumidorFinalYOmiteLoQueNoTienePrecioAhi()
    {
        var escalon = new EscalonDeCantidad(3m, 80m, 20m, []);
        var articulos = new[]
        {
            new ArticuloDeInstantanea(11, "A11", "Arroz", ["7790011"], 21, 21m,
                true, UnidadVenta.Unidad, [Precio(3, 100m, 95m, 5m), new PrecioDeListaDeInstantanea(5, 150m, 120m, 30m, [], [escalon])]),
            new ArticuloDeInstantanea(12, "A12", "Fideos", [], 22, 10.5m, true, UnidadVenta.Peso, [Precio(3, 40m, 40m, 0m)]),
        };
        var instantanea = Instantanea(articulos, [Cliente(1, 1, idLista: 5), Cliente(2, 2, idLista: 3)]);

        var legada = ProyectarLegada(instantanea);

        Assert.Equal(instantanea.Momento, legada.Momento);
        Assert.Equal(7, legada.IdPuntoVenta);
        Assert.Same(instantanea.MediosDePago, legada.MediosDePago);
        Assert.Equal(10m, legada.ToleranciaPago);

        var arroz = Assert.Single(legada.Articulos);
        Assert.Equal(11, arroz.IdArticulo);
        Assert.Equal("A11", arroz.CodigoInterno);
        Assert.Equal("Arroz", arroz.Nombre);
        Assert.Equal(["7790011"], arroz.CodigosBarra);
        Assert.Equal(150m, arroz.PrecioOriginal);
        Assert.Equal(120m, arroz.PrecioFinal);
        Assert.Equal(30m, arroz.DescuentoUnitario);
        Assert.Equal(21, arroz.IdAlicuotaIva);
        Assert.Equal(21m, arroz.PorcentajeIva);
        Assert.Equal([escalon], arroz.Escalones);
    }

    [Fact]
    public void ElFormatoOriginalNoExponeLaUnidadDeVentaALosPosYaInstalados()
    {
        var articulos = new[]
        {
            new ArticuloDeInstantanea(11, "A11", "Arroz", [], 21, 21m, true, UnidadVenta.Peso, [Precio(5, 100m, 100m, 0m)])
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            ProyectarLegada(Instantanea(articulos, [Cliente(1, 1, idLista: 5)])),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"idArticulo\":11", json);
        Assert.DoesNotContain("unidadVenta", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SinListaEfectivaDelConsumidorFinalElFormatoOriginalNoOfreceArticulos()
    {
        var articulos = new[] { new ArticuloDeInstantanea(11, "A11", "Arroz", [], 21, 21m, true, UnidadVenta.Unidad, [Precio(3, 1m, 1m, 0m)]) };

        var legada = ProyectarLegada(Instantanea(articulos, [Cliente(1, 1, idLista: null)]));

        Assert.Empty(legada.Articulos);
    }

    [Fact]
    public void LaEtiquetaIgnoraElMomentoYCambiaConElContenido()
    {
        var articulos = new[] { new ArticuloDeInstantanea(11, "A11", "Arroz", [], 21, 21m, true, UnidadVenta.Unidad, [Precio(3, 100m, 100m, 0m)]) };
        var base_ = Instantanea(articulos, [Cliente(1, 1, idLista: 3), Cliente(2, 2, idLista: 3, saldo: 50m)]);

        var etiqueta = EtiquetaDeInstantanea.Calcular(base_);

        Assert.Matches("^\"[0-9a-f]{64}\"$", etiqueta);
        Assert.Equal(etiqueta, EtiquetaDeInstantanea.Calcular(base_ with { Momento = base_.Momento.AddHours(3) }));

        var otroSaldo = base_ with { Clientes = [Cliente(1, 1, idLista: 3), Cliente(2, 2, idLista: 3, saldo: 51m)] };
        Assert.NotEqual(etiqueta, EtiquetaDeInstantanea.Calcular(otroSaldo));

        var otroPrecio = base_ with
        {
            Articulos = [new ArticuloDeInstantanea(11, "A11", "Arroz", [], 21, 21m, true, UnidadVenta.Unidad, [Precio(3, 100m, 99m, 1m)])]
        };
        Assert.NotEqual(etiqueta, EtiquetaDeInstantanea.Calcular(otroPrecio));

        var otraUnidad = base_ with { Articulos = [base_.Articulos[0] with { UnidadVenta = UnidadVenta.Peso }] };
        Assert.NotEqual(etiqueta, EtiquetaDeInstantanea.Calcular(otraUnidad));

        Assert.NotEqual(etiqueta, EtiquetaDeInstantanea.Calcular(base_ with { ToleranciaPago = 11m }));
    }
}
