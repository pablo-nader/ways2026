using System.Net;
using System.Text.Json;
using Ways.Domain.Catalogos;

namespace Ways.IntegrationTests;

/// <summary>
/// Las ofertas en el catálogo público (<c>GET /api/publico/catalogo</c>): solo las disponibles online, a cantidad 1 y
/// en la lista de la vitrina, con el resolvedor puro del dominio. <c>precio</c> es lo que paga el cliente;
/// <c>precioOriginal</c> y <c>oferta</c> solo viajan cuando una oferta lo baja de verdad. Todo contra Postgres real y la
/// API completa por el host del comercio.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogoPublicoOfertasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    private HttpClient ClienteEn(ApoyoDeCatalogoOnline.Tienda tienda) => apoyo.Dominios.ClienteEnHost(tienda.Host);

    private static string[] Propiedades(JsonElement objeto) =>
        [.. objeto.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static JsonElement Articulo(JsonElement catalogo, string nombre) =>
        catalogo.GetProperty("articulos").EnumerateArray().Single(a => a.GetProperty("nombre").GetString() == nombre);

    private async Task<JsonElement> CatalogoAsync(ApoyoDeCatalogoOnline.Tienda tienda, string consulta = "")
    {
        using var cliente = ClienteEn(tienda);

        return await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, consulta);
    }

    /// <summary>(precio, precioOriginal, oferta) de un artículo; los dos últimos son <c>null</c> cuando la clave falta.</summary>
    private static (decimal Precio, decimal? Original, string? Oferta) Precios(JsonElement articulo) =>
        (articulo.GetProperty("precio").GetDecimal(),
            articulo.TryGetProperty("precioOriginal", out var original) ? original.GetDecimal() : null,
            articulo.TryGetProperty("oferta", out var oferta) ? oferta.GetString() : null);

    private static DateOnly Hoy => DateOnly.FromDateTime(DateTime.Now);

    // ---- el contrato ------------------------------------------------------------------------------

    /// <summary>Un artículo con oferta de cada beneficio, con valores distintos por artículo, y uno sin oferta: el
    /// último no lleva las claves nuevas (la forma exacta), los otros llevan las tres.</summary>
    [Fact]
    public async Task UnaOfertaDisponibleOnlineRebajaElPrecioYInformaElOriginalYElNombre()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-contrato");
        var porcentaje = await apoyo.SembrarVendibleAsync(tienda, "01 con porcentaje", 200m);
        var unitario = await apoyo.SembrarVendibleAsync(tienda, "02 con precio unitario", 150m);
        var fijo = await apoyo.SembrarVendibleAsync(tienda, "03 con importe fijo", 80m);
        await apoyo.SembrarVendibleAsync(tienda, "04 sin oferta", 60m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Quince por ciento", idArticulo: porcentaje, porcentaje: 15m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "A noventa", idArticulo: unitario, precioUnitario: 90m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Veinte menos", idArticulo: fijo, importeFijo: 20m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((170m, 200m, "Quince por ciento"), Precios(Articulo(catalogo, "01 con porcentaje")));
        Assert.Equal((90m, 150m, "A noventa"), Precios(Articulo(catalogo, "02 con precio unitario")));
        Assert.Equal((60m, 80m, "Veinte menos"), Precios(Articulo(catalogo, "03 con importe fijo")));
        Assert.Equal((60m, null, null), Precios(Articulo(catalogo, "04 sin oferta")));

        Assert.Equal(
            ["descripcion", "id", "idCategoria", "imagenVersion", "nombre", "oferta", "precio", "precioOriginal", "tieneImagen", "unidadVenta"],
            Propiedades(Articulo(catalogo, "01 con porcentaje")));
        Assert.Equal(
            ["descripcion", "id", "idCategoria", "imagenVersion", "nombre", "precio", "tieneImagen", "unidadVenta"],
            Propiedades(Articulo(catalogo, "04 sin oferta")));
    }

    /// <summary>Cláusula: <c>disponible_online</c> de la oferta (la decide la policy de <c>ofertas</c>). Cada fila
    /// que NO rebaja es el fixture de una cláusula: fuera de la tienda, inactiva, dada de baja, de otro tenant.</summary>
    [Fact]
    public async Task UnaOfertaFueraDeLaTiendaInactivaEliminadaODeOtroComercioNoSeAplica()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-flags");
        var ajena = await apoyo.SembrarTiendaAsync("ofe-flags-ajena");
        var vigente = await apoyo.SembrarVendibleAsync(tienda, "01 vigente", 100m);
        var fuera = await apoyo.SembrarVendibleAsync(tienda, "02 fuera de la tienda", 100m);
        var inactiva = await apoyo.SembrarVendibleAsync(tienda, "03 inactiva", 100m);
        var eliminada = await apoyo.SembrarVendibleAsync(tienda, "04 eliminada", 100m);
        var deOtro = await apoyo.SembrarVendibleAsync(tienda, "05 oferta de otro comercio", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Vigente", idArticulo: vigente, porcentaje: 10m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Fuera", idArticulo: fuera, porcentaje: 20m, disponibleOnline: false);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Inactiva", idArticulo: inactiva, porcentaje: 30m, activo: false);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Eliminada", idArticulo: eliminada, porcentaje: 40m, eliminada: true);
        // Una oferta del otro comercio sobre un artículo de ESTE no existe (FK compuesta); la del otro comercio sobre el
        // suyo no llega a este catálogo.
        var suyo = await apoyo.SembrarVendibleAsync(ajena, "suyo", 100m);
        await apoyo.SembrarOfertaAsync(ajena.IdTenant, "De otro", idArticulo: suyo, porcentaje: 50m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((90m, 100m, "Vigente"), Precios(Articulo(catalogo, "01 vigente")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "02 fuera de la tienda")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "03 inactiva")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "04 eliminada")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "05 oferta de otro comercio")));
        Assert.Equal(5, catalogo.GetProperty("total").GetInt32());
        Assert.Equal(deOtro, Articulo(catalogo, "05 oferta de otro comercio").GetProperty("id").GetInt32());
    }

    // ---- cantidad 1 -------------------------------------------------------------------------------

    /// <summary>Cláusula: la vista previa es a cantidad 1. Una oferta con cantidad mínima 3 NO aplica; una de cantidad
    /// mínima 1 sí (el borde). Mata un resolvedor llamado con otra cantidad.</summary>
    [Fact]
    public async Task UnaOfertaConCantidadMinimaMayorQueUnoNoAplicaYUnaDeUnoSi()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-cantidad");
        var escalonada = await apoyo.SembrarVendibleAsync(tienda, "01 escalonada", 100m);
        var borde = await apoyo.SembrarVendibleAsync(tienda, "02 minimo uno", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Llevando tres", idArticulo: escalonada, porcentaje: 25m, cantidadMinima: 3m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Desde uno", idArticulo: borde, porcentaje: 25m, cantidadMinima: 1m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "01 escalonada")));
        Assert.Equal((75m, 100m, "Desde uno"), Precios(Articulo(catalogo, "02 minimo uno")));
    }

    // ---- listas -----------------------------------------------------------------------------------

    /// <summary>Cláusula: <c>ofertas_listas</c>. Sin filas, la oferta aplica a toda lista; con filas, solo a esas. Una
    /// dirigida a otra lista no aplica; una dirigida a la de la vitrina sí.</summary>
    [Fact]
    public async Task UnaOfertaDirigidaAOtraListaNoAplicaYUnaDirigidaALaDeLaVitrinaSi()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-listas");
        var otraLista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Mayorista");
        var sinTarget = await apoyo.SembrarVendibleAsync(tienda, "01 sin lista objetivo", 100m);
        var deOtra = await apoyo.SembrarVendibleAsync(tienda, "02 de otra lista", 100m);
        var deLaVitrina = await apoyo.SembrarVendibleAsync(tienda, "03 de la lista de la vitrina", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Para todas", idArticulo: sinTarget, porcentaje: 10m);
        var ofertaOtra = await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Solo mayorista", idArticulo: deOtra, porcentaje: 20m);
        await apoyo.SembrarOfertaListaAsync(tienda.IdTenant, ofertaOtra, otraLista);
        var ofertaVitrina = await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Solo general", idArticulo: deLaVitrina, porcentaje: 30m);
        await apoyo.SembrarOfertaListaAsync(tienda.IdTenant, ofertaVitrina, tienda.IdListaDefault);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((90m, 100m, "Para todas"), Precios(Articulo(catalogo, "01 sin lista objetivo")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "02 de otra lista")));
        Assert.Equal((70m, 100m, "Solo general"), Precios(Articulo(catalogo, "03 de la lista de la vitrina")));
    }

    /// <summary>
    /// Cláusula: la línea lleva la lista ORIGINAL de la vitrina, no la base de la que sale el precio. La vitrina usa una
    /// lista derivada (+10% sobre la general): el precio de lista sale de la base (110), una oferta dirigida a la
    /// derivada aplica sobre ese 110 y una dirigida a la base NO aplica, porque la lista de la tienda es la derivada.
    /// </summary>
    [Fact]
    public async Task ConUnaListaDerivadaLasOfertasDirigidasAEllaAplicanSobreElPrecioDerivado()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-derivada");
        var derivada = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "General +10%", modo: ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 10m);
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, derivada);
        var aLaDerivada = await apoyo.SembrarVendibleAsync(tienda, "01 oferta de la derivada", 100m);
        var aLaBase = await apoyo.SembrarVendibleAsync(tienda, "02 oferta de la base", 100m);
        var sinOferta = await apoyo.SembrarVendibleAsync(tienda, "03 sin oferta", 100m);
        var ofertaDerivada = await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Solo derivada", idArticulo: aLaDerivada, porcentaje: 50m);
        await apoyo.SembrarOfertaListaAsync(tienda.IdTenant, ofertaDerivada, derivada);
        var ofertaBase = await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Solo base", idArticulo: aLaBase, porcentaje: 50m);
        await apoyo.SembrarOfertaListaAsync(tienda.IdTenant, ofertaBase, tienda.IdListaDefault);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((55m, 110m, "Solo derivada"), Precios(Articulo(catalogo, "01 oferta de la derivada")));
        Assert.Equal((110m, null, null), Precios(Articulo(catalogo, "02 oferta de la base")));
        Assert.Equal((110m, null, null), Precios(Articulo(catalogo, "03 sin oferta")));
        Assert.NotEqual(0, sinOferta);
    }

    // ---- empresa, alcance y vigencia --------------------------------------------------------------

    [Fact]
    public async Task UnaOfertaDeOtraEmpresaNoAplicaYUnaDeLaEmpresaDeLaVitrinaOdeTodoElTenantSi()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-empresa");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var deOtra = await apoyo.SembrarVendibleAsync(tienda, "01 de otra empresa", 100m);
        var propia = await apoyo.SembrarVendibleAsync(tienda, "02 de esta empresa", 100m);
        var detodas = await apoyo.SembrarVendibleAsync(tienda, "03 de todo el tenant", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Otra empresa", idArticulo: deOtra, porcentaje: 10m, idEmpresa: otraEmpresa);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Esta empresa", idArticulo: propia, porcentaje: 20m, idEmpresa: tienda.IdEmpresa);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Todas", idArticulo: detodas, porcentaje: 30m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "01 de otra empresa")));
        Assert.Equal((80m, 100m, "Esta empresa"), Precios(Articulo(catalogo, "02 de esta empresa")));
        Assert.Equal((70m, 100m, "Todas"), Precios(Articulo(catalogo, "03 de todo el tenant")));
    }

    /// <summary>Una oferta de categoría alcanza al artículo de una subcategoría (la cadena de ancestros), no al de una
    /// categoría hermana ni al que no tiene categoría.</summary>
    [Fact]
    public async Task UnaOfertaDeCategoriaAlcanzaALasSubcategoriasYNoALasHermanas()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-categoria");
        var bebidas = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Bebidas");
        var gaseosas = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Gaseosas", idCategoriaPadre: bebidas);
        var almacen = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Almacén");
        await apoyo.SembrarVendibleAsync(tienda, "01 gaseosa", 100m, idCategoria: gaseosas);
        await apoyo.SembrarVendibleAsync(tienda, "02 bebida", 100m, idCategoria: bebidas);
        await apoyo.SembrarVendibleAsync(tienda, "03 almacén", 100m, idCategoria: almacen);
        await apoyo.SembrarVendibleAsync(tienda, "04 sin categoría", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Semana de bebidas", idCategoria: bebidas, porcentaje: 12m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((88m, 100m, "Semana de bebidas"), Precios(Articulo(catalogo, "01 gaseosa")));
        Assert.Equal((88m, 100m, "Semana de bebidas"), Precios(Articulo(catalogo, "02 bebida")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "03 almacén")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "04 sin categoría")));
    }

    /// <summary>Cláusula: el alcance por grupo. Una oferta de grupo rebaja a los artículos de ese grupo y no a los de
    /// otro grupo ni a los que no tienen. Mata la cláusula de grupo de la consulta de ofertas candidatas y el cableado
    /// del grupo del artículo hacia el resolvedor.</summary>
    [Fact]
    public async Task UnaOfertaDeGrupoAlcanzaSoloALosArticulosDeEseGrupo()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-grupo");
        var latas = await apoyo.SembrarGrupoAsync(tienda.IdTenant, "Latas");
        var botellas = await apoyo.SembrarGrupoAsync(tienda.IdTenant, "Botellas");
        await apoyo.SembrarVendibleAsync(tienda, "01 lata", 100m, idGrupo: latas);
        await apoyo.SembrarVendibleAsync(tienda, "02 botella", 100m, idGrupo: botellas);
        await apoyo.SembrarVendibleAsync(tienda, "03 sin grupo", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Todas las latas", idGrupo: latas, porcentaje: 20m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((80m, 100m, "Todas las latas"), Precios(Articulo(catalogo, "01 lata")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "02 botella")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "03 sin grupo")));
    }

    /// <summary>Cláusula: el alcance por familia. Una oferta de familia rebaja a sus miembros y no a los de otra familia
    /// ni a los que no tienen. Mata la cláusula de familia de la consulta y el cableado de la familia del artículo.</summary>
    [Fact]
    public async Task UnaOfertaDeFamiliaAlcanzaSoloALosMiembrosDeEsaFamilia()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-familia");
        var remeras = await apoyo.SembrarFamiliaAsync(tienda.IdTenant, "Remeras");
        var pantalones = await apoyo.SembrarFamiliaAsync(tienda.IdTenant, "Pantalones");
        await apoyo.SembrarVendibleAsync(tienda, "01 remera roja", 100m, idFamilia: remeras);
        await apoyo.SembrarVendibleAsync(tienda, "02 remera azul", 100m, idFamilia: remeras);
        await apoyo.SembrarVendibleAsync(tienda, "03 pantalón", 100m, idFamilia: pantalones);
        await apoyo.SembrarVendibleAsync(tienda, "04 sin familia", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Remeras de temporada", idFamilia: remeras, porcentaje: 30m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((70m, 100m, "Remeras de temporada"), Precios(Articulo(catalogo, "01 remera roja")));
        Assert.Equal((70m, 100m, "Remeras de temporada"), Precios(Articulo(catalogo, "02 remera azul")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "03 pantalón")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "04 sin familia")));
    }

    /// <summary>Cláusula: la ventana horaria, con la hora local del servidor. Dos ofertas parten el día en mañana y
    /// tarde: aplica exactamente la que contiene la hora de ahora. Mata una hora mal cableada al resolvedor (por ejemplo
    /// corrida 12 horas) o ignorada (aplicarían las dos).</summary>
    [Fact]
    public async Task LaVentanaHorariaDeLaOfertaSeEvaluaConLaHoraLocal()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-hora");
        var deManana = await apoyo.SembrarVendibleAsync(tienda, "01 de la mañana", 100m);
        var deTarde = await apoyo.SembrarVendibleAsync(tienda, "02 de la tarde", 100m);
        await apoyo.SembrarOfertaAsync(
            tienda.IdTenant, "Hasta el mediodía", idArticulo: deManana, porcentaje: 10m,
            horaDesde: new TimeOnly(0, 0), horaHasta: new TimeOnly(11, 59, 59, 999));
        await apoyo.SembrarOfertaAsync(
            tienda.IdTenant, "Desde el mediodía", idArticulo: deTarde, porcentaje: 10m,
            horaDesde: new TimeOnly(12, 0), horaHasta: new TimeOnly(23, 59, 59, 999));
        var esDeManana = TimeOnly.FromDateTime(DateTime.Now) < new TimeOnly(12, 0);

        var catalogo = await CatalogoAsync(tienda);

        var conOferta = (90m, 100m);
        var sinOferta = (100m, (decimal?)null);
        var (precioManana, originalManana, _) = Precios(Articulo(catalogo, "01 de la mañana"));
        var (precioTarde, originalTarde, _) = Precios(Articulo(catalogo, "02 de la tarde"));
        Assert.Equal(esDeManana ? conOferta : sinOferta, (precioManana, originalManana));
        Assert.Equal(esDeManana ? sinOferta : conOferta, (precioTarde, originalTarde));
    }

    /// <summary>Cláusula: los días de la semana (ISO, 1 = lunes … 7 = domingo). Aplica la oferta de hoy y la que incluye
    /// hoy entre otros; no la de otro día. Mata un día mal cableado (por ejemplo corrido uno) o ignorado.</summary>
    [Fact]
    public async Task LosDiasDeSemanaDeLaOfertaSeEvaluanConElDiaLocal()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-dias");
        var hoyIso = (short)(DateTime.Now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)DateTime.Now.DayOfWeek);
        var otroDia = (short)(hoyIso % 7 + 1);
        var otroMas = (short)(otroDia % 7 + 1);
        var soloHoy = await apoyo.SembrarVendibleAsync(tienda, "01 solo hoy", 100m);
        var otroDiaId = await apoyo.SembrarVendibleAsync(tienda, "02 otro día", 100m);
        var variosDias = await apoyo.SembrarVendibleAsync(tienda, "03 varios días con hoy", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Solo hoy", idArticulo: soloHoy, porcentaje: 10m, diasSemana: [hoyIso]);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Otro día", idArticulo: otroDiaId, porcentaje: 10m, diasSemana: [otroDia]);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Varios", idArticulo: variosDias, porcentaje: 10m, diasSemana: [otroMas, hoyIso]);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((90m, 100m, "Solo hoy"), Precios(Articulo(catalogo, "01 solo hoy")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "02 otro día")));
        Assert.Equal((90m, 100m, "Varios"), Precios(Articulo(catalogo, "03 varios días con hoy")));
    }

    /// <summary>La ventana de fechas se evalúa con el reloj del servidor: ayer-ayer y mañana-mañana no aplican, hoy-hoy sí.</summary>
    [Fact]
    public async Task LaVigenciaPorFechaSeRespeta()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-fechas");
        var vencida = await apoyo.SembrarVendibleAsync(tienda, "01 vencida", 100m);
        var futura = await apoyo.SembrarVendibleAsync(tienda, "02 futura", 100m);
        var deHoy = await apoyo.SembrarVendibleAsync(tienda, "03 de hoy", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Vencida", idArticulo: vencida, fechaDesde: Hoy.AddDays(-5), fechaHasta: Hoy.AddDays(-1));
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Futura", idArticulo: futura, fechaDesde: Hoy.AddDays(1), fechaHasta: Hoy.AddDays(5));
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "De hoy", idArticulo: deHoy, fechaDesde: Hoy, fechaHasta: Hoy);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "01 vencida")));
        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "02 futura")));
        Assert.Equal((90m, 100m, "De hoy"), Precios(Articulo(catalogo, "03 de hoy")));
    }

    // ---- varias ofertas ---------------------------------------------------------------------------

    /// <summary>Entre dos ofertas no acumulables gana la de mayor prioridad aunque descuente menos, y el nombre
    /// informado es el suyo; una acumulable se suma y el nombre es el de mayor prioridad entre las aplicadas.</summary>
    [Fact]
    public async Task ConVariasOfertasGanaLaDeMayorPrioridadYLasAcumulablesSeSuman()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-varias");
        var compiten = await apoyo.SembrarVendibleAsync(tienda, "01 compiten", 100m);
        var apiladas = await apoyo.SembrarVendibleAsync(tienda, "02 apiladas", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Grande pero baja", idArticulo: compiten, porcentaje: 40m, prioridad: 1);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Chica pero alta", idArticulo: compiten, porcentaje: 10m, prioridad: 5);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Base", idArticulo: apiladas, porcentaje: 10m, prioridad: 1);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Extra", idArticulo: apiladas, importeFijo: 5m, prioridad: 9, acumulable: true);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((90m, 100m, "Chica pero alta"), Precios(Articulo(catalogo, "01 compiten")));
        Assert.Equal((85m, 100m, "Extra"), Precios(Articulo(catalogo, "02 apiladas")));
    }

    /// <summary>Una oferta que no descuenta (un precio unitario mayor que el de lista) no figura: ni
    /// <c>precioOriginal</c> ni <c>oferta</c>.</summary>
    [Fact]
    public async Task UnaOfertaQueNoRebajaElPrecioNoFiguraEnLaRespuesta()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-sin-rebaja");
        var caro = await apoyo.SembrarVendibleAsync(tienda, "01 oferta cara", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "A doscientos", idArticulo: caro, precioUnitario: 200m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal((100m, null, null), Precios(Articulo(catalogo, "01 oferta cara")));
    }

    /// <summary>Las ofertas no mueven qué artículos aparecen, ni el total, ni las cuentas de categoría: solo el precio
    /// que se ve. Un artículo con oferta pero sin precio de lista sigue sin aparecer.</summary>
    [Fact]
    public async Task LasOfertasNoCambianQueArticulosAparecenNiLasCuentas()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-conteos");
        var bebidas = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Bebidas");
        var agua = await apoyo.SembrarVendibleAsync(tienda, "01 agua", 50m, idCategoria: bebidas);
        var jugo = await apoyo.SembrarVendibleAsync(tienda, "02 jugo", 60m, idCategoria: bebidas);
        var sinPrecio = await apoyo.SembrarArticuloAsync(tienda, "03 sin precio", idCategoria: bebidas);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Agua", idArticulo: agua, porcentaje: 10m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Sin precio", idArticulo: sinPrecio, porcentaje: 10m);

        var catalogo = await CatalogoAsync(tienda);

        Assert.Equal(["01 agua", "02 jugo"], ApoyoDeCatalogoOnline.Nombres(catalogo));
        Assert.Equal(2, catalogo.GetProperty("total").GetInt32());
        var categoria = Assert.Single(catalogo.GetProperty("categorias").EnumerateArray());
        Assert.Equal((bebidas, 2), (categoria.GetProperty("id").GetInt32(), categoria.GetProperty("cantidad").GetInt32()));
        Assert.Equal((45m, 50m, "Agua"), Precios(Articulo(catalogo, "01 agua")));
        Assert.Equal((60m, null, null), Precios(Articulo(catalogo, "02 jugo")));
        Assert.NotEqual(0, jugo);
    }

    /// <summary>La respuesta con ofertas sigue siendo cacheable por un minuto y sin sesión.</summary>
    [Fact]
    public async Task LaRespuestaConOfertasSigueSiendoPublicaYCacheable()
    {
        var tienda = await apoyo.SembrarTiendaAsync("ofe-cache");
        var id = await apoyo.SembrarVendibleAsync(tienda, "01 con oferta", 100m);
        await apoyo.SembrarOfertaAsync(tienda.IdTenant, "Diez", idArticulo: id, porcentaje: 10m);

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("public, max-age=60", respuesta.Headers.CacheControl?.ToString());
    }
}
