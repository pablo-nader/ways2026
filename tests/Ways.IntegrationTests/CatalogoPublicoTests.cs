using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ways.Application.CatalogoOnline;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// El catálogo anónimo de la tienda: <c>GET /api/publico/catalogo</c> y <c>/api/publico/articulos/{id}/imagen</c>
/// por el host del comercio, con la API completa contra Postgres real. El tenant sale siempre del host.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogoPublicoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    private HttpClient ClienteEn(ApoyoDeCatalogoOnline.Tienda tienda) => apoyo.Dominios.ClienteEnHost(tienda.Host);

    private static string[] Propiedades(JsonElement objeto) =>
        [.. objeto.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static Dictionary<string, decimal> PreciosPorNombre(JsonElement catalogo) =>
        catalogo.GetProperty("articulos").EnumerateArray()
            .ToDictionary(a => a.GetProperty("nombre").GetString()!, a => a.GetProperty("precio").GetDecimal());

    // ---- el contrato ------------------------------------------------------------------------------

    /// <summary>Cada campo de la respuesta con su verdad, con valores distintos por artículo y por categoría (dos
    /// campos intercambiados no pasan): la forma exacta, el orden por nombre y el de las categorías por orden, la
    /// cabecera de caché y que no viaja nada más (código interno, costo, stock).</summary>
    [Fact]
    public async Task ElCatalogoDevuelveCadaCampoConSuVerdadYNadaMas()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-contrato");
        var bebidas = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Bebidas", orden: 2);
        var almacen = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Almacén", orden: 1);
        var sal = await apoyo.SembrarVendibleAsync(tienda, "Sal fina", 5m);
        var agua = await apoyo.SembrarVendibleAsync(
            tienda, "Agua mineral", 12.50m, idCategoria: bebidas, descripcion: "Sin gas, 500 cc", codigoInterno: "AG-500");
        var arroz = await apoyo.SembrarVendibleAsync(
            tienda, "Arroz largo", 99.99m, idCategoria: almacen, unidadVenta: UnidadVenta.Peso, codigoInterno: "AR-1");
        var cargada = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, agua, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng), actualizadoEn: cargada);

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("public, max-age=60", respuesta.Headers.CacheControl?.ToString());
        var catalogo = await ApoyoDeDominios.LeerJsonAsync(respuesta);

        Assert.Equal(["articulos", "categorias", "pagina", "tamano", "total"], Propiedades(catalogo));
        Assert.Equal((3, 1, 24), (catalogo.GetProperty("total").GetInt32(), catalogo.GetProperty("pagina").GetInt32(), catalogo.GetProperty("tamano").GetInt32()));

        // Categorías: por orden (Almacén 1, Bebidas 2), con su cuenta; la de "sin categoría" no es un chip.
        var categorias = catalogo.GetProperty("categorias").EnumerateArray().ToList();
        Assert.Equal(2, categorias.Count);
        Assert.All(categorias, c => Assert.Equal(["cantidad", "id", "nombre"], Propiedades(c)));
        Assert.Equal((almacen, "Almacén", 1), (categorias[0].GetProperty("id").GetInt32(), categorias[0].GetProperty("nombre").GetString(), categorias[0].GetProperty("cantidad").GetInt32()));
        Assert.Equal((bebidas, "Bebidas", 1), (categorias[1].GetProperty("id").GetInt32(), categorias[1].GetProperty("nombre").GetString(), categorias[1].GetProperty("cantidad").GetInt32()));

        // Artículos: por nombre (Agua, Arroz, Sal).
        var articulos = catalogo.GetProperty("articulos").EnumerateArray().ToList();
        Assert.Equal(3, articulos.Count);
        Assert.All(
            articulos,
            a => Assert.Equal(
                ["descripcion", "id", "idCategoria", "imagenVersion", "nombre", "precio", "tieneImagen", "unidadVenta"],
                Propiedades(a)));

        Assert.Equal(agua, articulos[0].GetProperty("id").GetInt32());
        Assert.Equal("Agua mineral", articulos[0].GetProperty("nombre").GetString());
        Assert.Equal("Sin gas, 500 cc", articulos[0].GetProperty("descripcion").GetString());
        Assert.Equal(bebidas, articulos[0].GetProperty("idCategoria").GetInt32());
        Assert.Equal(12.50m, articulos[0].GetProperty("precio").GetDecimal());
        Assert.Equal("Unidad", articulos[0].GetProperty("unidadVenta").GetString());
        Assert.True(articulos[0].GetProperty("tieneImagen").GetBoolean());
        Assert.Equal(cargada.ToUnixTimeSeconds(), articulos[0].GetProperty("imagenVersion").GetInt64());

        Assert.Equal(arroz, articulos[1].GetProperty("id").GetInt32());
        Assert.Equal("Arroz largo", articulos[1].GetProperty("nombre").GetString());
        Assert.Equal(JsonValueKind.Null, articulos[1].GetProperty("descripcion").ValueKind);
        Assert.Equal(almacen, articulos[1].GetProperty("idCategoria").GetInt32());
        Assert.Equal(99.99m, articulos[1].GetProperty("precio").GetDecimal());
        Assert.Equal("Peso", articulos[1].GetProperty("unidadVenta").GetString());
        Assert.False(articulos[1].GetProperty("tieneImagen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, articulos[1].GetProperty("imagenVersion").ValueKind);

        Assert.Equal(sal, articulos[2].GetProperty("id").GetInt32());
        Assert.Equal("Sal fina", articulos[2].GetProperty("nombre").GetString());
        Assert.Equal(JsonValueKind.Null, articulos[2].GetProperty("idCategoria").ValueKind);
        Assert.Equal(5m, articulos[2].GetProperty("precio").GetDecimal());
    }

    // ---- qué artículos aparecen -------------------------------------------------------------------

    /// <summary>
    /// Cada artículo que NO aparece es el fixture que mata UNA cláusula: la publicación y la actividad (las decide
    /// la policy de <c>articulos</c>), la baja, la disponibilidad por empresa (las dos formas: restringido a otra
    /// empresa y restringido a ninguna), cada cláusula del precio vigente (sin precio, vencido, futuro, en otra
    /// lista, dado de baja) y el tenant. El que aparece con historial de precios muestra el vigente y una sola vez.
    /// </summary>
    [Fact]
    public async Task SoloAparecenLosPublicadosActivosVivosDisponiblesYConPrecioVigenteEnLaListaDeLaTienda()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-matriz");
        var ajena = await apoyo.SembrarTiendaAsync("pub-matriz-ajena");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var otraLista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Otra lista");
        var ahora = DateTimeOffset.UtcNow;

        // Los que aparecen.
        var comun = await apoyo.SembrarVendibleAsync(tienda, "01 común", 20m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, comun, tienda.IdListaDefault, 10m, desde: ahora.AddDays(-30), hasta: ahora.AddDays(-20));
        var deEstaEmpresa = await apoyo.SembrarArticuloAsync(tienda, "02 restringido a esta empresa", idEmpresaDisponible: tienda.IdEmpresa);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, deEstaEmpresa, tienda.IdListaDefault, 30m);

        // Un cambio de precio programado: el vigente cierra mañana y el nuevo empieza mañana, sin cierre. Hoy vale
        // el vigente.
        var programado = await apoyo.SembrarArticuloAsync(tienda, "03 con precio programado");
        await apoyo.SembrarPrecioAsync(
            tienda.IdTenant, programado, tienda.IdListaDefault, 40m, desde: ahora.AddDays(-10), hasta: ahora.AddDays(1));
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, programado, tienda.IdListaDefault, 50m, desde: ahora.AddDays(1));

        // Dos ventanas vigentes a la vez (los datos no lo impiden, solo las filas abiertas son únicas): gana la que
        // empezó más tarde, la misma regla que el resto de los lectores de precios.
        var solapado = await apoyo.SembrarArticuloAsync(tienda, "04 con ventanas solapadas");
        await apoyo.SembrarPrecioAsync(
            tienda.IdTenant, solapado, tienda.IdListaDefault, 1m, desde: ahora.AddDays(-10), hasta: ahora.AddDays(5));
        await apoyo.SembrarPrecioAsync(
            tienda.IdTenant, solapado, tienda.IdListaDefault, 2m, desde: ahora.AddDays(-2), hasta: ahora.AddDays(3));

        // Los que no.
        await apoyo.SembrarVendibleAsync(tienda, "90 sin publicar", 1m, publicado: false);
        var inactivo = await apoyo.SembrarArticuloAsync(tienda, "91 inactivo", activo: false);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, inactivo, tienda.IdListaDefault, 1m);
        var eliminado = await apoyo.SembrarArticuloAsync(tienda, "92 eliminado", eliminado: true);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, eliminado, tienda.IdListaDefault, 1m);
        var deOtraEmpresa = await apoyo.SembrarArticuloAsync(tienda, "93 de otra empresa", idEmpresaDisponible: otraEmpresa);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, deOtraEmpresa, tienda.IdListaDefault, 1m);
        var deNinguna = await apoyo.SembrarArticuloAsync(tienda, "94 restringido a ninguna", restringidoSinEmpresas: true);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, deNinguna, tienda.IdListaDefault, 1m);
        await apoyo.SembrarArticuloAsync(tienda, "95 sin precio");
        var vencido = await apoyo.SembrarArticuloAsync(tienda, "96 precio vencido");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, vencido, tienda.IdListaDefault, 1m, desde: ahora.AddDays(-30), hasta: ahora.AddDays(-2));
        var futuro = await apoyo.SembrarArticuloAsync(tienda, "97 precio futuro");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, futuro, tienda.IdListaDefault, 1m, desde: ahora.AddDays(1));
        var enOtraLista = await apoyo.SembrarArticuloAsync(tienda, "98 precio en otra lista");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, enOtraLista, otraLista, 1m);
        var precioEliminado = await apoyo.SembrarArticuloAsync(tienda, "99 precio eliminado");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, precioEliminado, tienda.IdListaDefault, 1m, eliminado: true);
        await apoyo.SembrarVendibleAsync(ajena, "Del otro tenant", 1m);

        using var cliente = ClienteEn(tienda);
        var catalogo = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente);

        Assert.Equal(
            ["01 común", "02 restringido a esta empresa", "03 con precio programado", "04 con ventanas solapadas"],
            ApoyoDeCatalogoOnline.Nombres(catalogo));
        Assert.Equal(4, catalogo.GetProperty("total").GetInt32());
        Assert.Equal(
            new Dictionary<string, decimal>
            {
                ["01 común"] = 20m,
                ["02 restringido a esta empresa"] = 30m,
                ["03 con precio programado"] = 40m,
                ["04 con ventanas solapadas"] = 2m
            },
            PreciosPorNombre(catalogo));
    }

    /// <summary>Lo que cada host ve es solo lo suyo, aun con los mismos nombres y la misma estructura.</summary>
    [Fact]
    public async Task CadaHostVeSoloElCatalogoDeSuTenant()
    {
        var a = await apoyo.SembrarTiendaAsync("pub-host-a");
        var b = await apoyo.SembrarTiendaAsync("pub-host-b");
        var deA = await apoyo.SembrarVendibleAsync(a, "Producto compartido", 11m);
        var deB = await apoyo.SembrarVendibleAsync(b, "Producto compartido", 22m);

        using var clienteA = ClienteEn(a);
        using var clienteB = ClienteEn(b);
        var catalogoA = await ApoyoDeCatalogoOnline.CatalogoAsync(clienteA);
        var catalogoB = await ApoyoDeCatalogoOnline.CatalogoAsync(clienteB);

        Assert.Equal((deA, 11m), (catalogoA.GetProperty("articulos")[0].GetProperty("id").GetInt32(), catalogoA.GetProperty("articulos")[0].GetProperty("precio").GetDecimal()));
        Assert.Equal((deB, 22m), (catalogoB.GetProperty("articulos")[0].GetProperty("id").GetInt32(), catalogoB.GetProperty("articulos")[0].GetProperty("precio").GetDecimal()));
        Assert.Equal(1, catalogoA.GetProperty("total").GetInt32());
        Assert.Equal(1, catalogoB.GetProperty("total").GetInt32());
    }

    // ---- precios ----------------------------------------------------------------------------------

    /// <summary>
    /// El precio sale de la lista de la tienda: la de la vitrina si la tiene, si no la default compartida del
    /// tenant; una fija da su precio, una derivada aplica su porcentaje al de su base con el redondeo del POS
    /// (2 decimales, lejos de cero). Cada lista tiene precios distintos para el mismo artículo, así que de dónde
    /// salió el precio se lee en el valor.
    /// </summary>
    [Fact]
    public async Task ElPrecioSaleDeLaListaFijaDeLaVitrinaDeLaDerivadaOdeLaDefault()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-precios");
        var mayorista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Mayorista");
        var recargo = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "Con recargo", modo: ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 10m);
        var descuento = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "Con descuento", modo: ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: -10m);
        var recargoMitad = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "Recargo a la mitad", modo: ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 50m);

        var a = await apoyo.SembrarArticuloAsync(tienda, "A con todo");
        var b = await apoyo.SembrarArticuloAsync(tienda, "B solo en la default");
        var c = await apoyo.SembrarArticuloAsync(tienda, "C solo en mayorista");
        var d = await apoyo.SembrarArticuloAsync(tienda, "D centavos");
        // Los precios de la default empezaron antes que los de la mayorista: si una consulta mezclara las listas, el
        // más reciente (el de la mayorista) ganaría y el precio de la tienda saldría de la lista equivocada.
        var ahora = DateTimeOffset.UtcNow;
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, a, tienda.IdListaDefault, 100m, desde: ahora.AddDays(-10));
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, a, mayorista, 80m, desde: ahora.AddDays(-1));
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, b, tienda.IdListaDefault, 35m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, c, mayorista, 7m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, d, tienda.IdListaDefault, 0.15m, desde: ahora.AddDays(-10));
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, d, mayorista, 1m, desde: ahora.AddDays(-1));

        using var cliente = ClienteEn(tienda);

        // Sin lista en la vitrina: la default compartida.
        var porDefecto = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente);
        Assert.Equal(
            new Dictionary<string, decimal> { ["A con todo"] = 100m, ["B solo en la default"] = 35m, ["D centavos"] = 0.15m },
            PreciosPorNombre(porDefecto));

        // Lista fija en la vitrina: sus precios, y los artículos sin precio en ella desaparecen.
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, mayorista);
        var fija = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente);
        Assert.Equal(
            new Dictionary<string, decimal> { ["A con todo"] = 80m, ["C solo en mayorista"] = 7m, ["D centavos"] = 1m },
            PreciosPorNombre(fija));
        Assert.Equal(3, fija.GetProperty("total").GetInt32());

        // Derivadas: el porcentaje sobre el precio de la BASE (no sobre otra lista), redondeado al peso entero lejos de cero, y sin
        // precio en la base no hay artículo aunque la derivada no tenga filas propias.
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, recargo);
        Assert.Equal(
            new Dictionary<string, decimal> { ["A con todo"] = 110m, ["B solo en la default"] = 39m, ["D centavos"] = 0m },
            PreciosPorNombre(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)));

        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, descuento);
        Assert.Equal(
            new Dictionary<string, decimal> { ["A con todo"] = 90m, ["B solo en la default"] = 32m, ["D centavos"] = 0m },
            PreciosPorNombre(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)));

        // 35 × 1,5 = 52,5: al peso entero y lejos de cero da 53 (el redondeo bancario daría 52).
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, recargoMitad);
        Assert.Equal(
            new Dictionary<string, decimal> { ["A con todo"] = 150m, ["B solo en la default"] = 53m, ["D centavos"] = 0m },
            PreciosPorNombre(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)));
    }

    /// <summary>La default que cuenta es la compartida del tenant: una default propia de la empresa (que la base
    /// admite) no la reemplaza. La de la empresa se crea ANTES que la compartida vigente, así que si la consulta no
    /// distinguiera el alcance, la primera que encuentra sería la de la empresa.</summary>
    [Fact]
    public async Task LaDefaultEsLaCompartidaAunqueLaEmpresaTengaUnaDefaultPropia()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-default-propia");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Producto");
        await apoyo.ModificarListaAsync(tienda.IdListaDefault, l => l.EsDefault = false);
        var propia = await apoyo.SembrarListaAsync(tienda.IdTenant, "Default de la empresa", esDefault: true, idEmpresa: tienda.IdEmpresa);
        var compartida = await apoyo.SembrarListaAsync(tienda.IdTenant, "Nueva general", esDefault: true);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, tienda.IdListaDefault, 77m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, propia, 99m);
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, compartida, 10m);

        using var cliente = ClienteEn(tienda);

        Assert.Equal(10m, PreciosPorNombre(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente))["Producto"]);
    }

    /// <summary>Una lista de la tienda que no se puede usar deja el catálogo vacío, sin error: inactiva, dada de
    /// baja, de otra empresa, derivada de otra derivada, derivada sin base fija, o ninguna default. Cada caso mata
    /// una cláusula de la resolución de la lista.</summary>
    [Fact]
    public async Task UnaListaDeLaTiendaQueNoSePuedeUsarDejaElCatalogoVacio()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-lista-rota");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Producto");
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, tienda.IdListaDefault, 10m);

        var inactiva = await apoyo.SembrarListaAsync(tienda.IdTenant, "Inactiva", activo: false);
        var eliminada = await apoyo.SembrarListaAsync(tienda.IdTenant, "Eliminada");
        var deOtraEmpresa = await apoyo.SembrarListaAsync(tienda.IdTenant, "De otra empresa", idEmpresa: otraEmpresa);
        var derivada = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "Derivada", modo: ModoLista.Derivada, idListaBase: tienda.IdListaDefault, porcentaje: 10m);
        var derivadaDeDerivada = await apoyo.SembrarListaAsync(
            tienda.IdTenant, "Derivada de derivada", modo: ModoLista.Derivada, idListaBase: derivada, porcentaje: 10m);
        foreach (var lista in new[] { inactiva, eliminada, deOtraEmpresa })
        {
            await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, lista, 1m);
        }

        // Una derivada con una fila propia (los datos no lo impiden): si una derivada pudiera ser la base de otra, el
        // precio de la derivada de derivada saldría de esa fila.
        await apoyo.SembrarPrecioAsync(tienda.IdTenant, id, derivada, 5m);

        using var cliente = ClienteEn(tienda);

        // Estados que se dan por el camino normal de la base (una baja solo se estampa a mano, ver
        // dangling-fk-read-models): la lista de la vitrina quedó apuntando a una lista que ya no sirve.
        await apoyo.ModificarListaAsync(eliminada, l => l.DeletedAt = DateTimeOffset.UtcNow);
        foreach (var rota in new[] { inactiva, eliminada, deOtraEmpresa, derivadaDeDerivada })
        {
            await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, rota);

            var catalogo = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente);

            Assert.Empty(ApoyoDeCatalogoOnline.Nombres(catalogo));
            Assert.Equal(0, catalogo.GetProperty("total").GetInt32());
            Assert.Equal(0, catalogo.GetProperty("categorias").GetArrayLength());
        }

        // La misma lista, una vez que sirve, ofrece el artículo: el vacío de arriba era de la lista.
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, derivada);
        Assert.Equal(11m, PreciosPorNombre(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente))["Producto"]);

        // Sin vitrina apuntando a nada y sin default: vacío. La default se restituye al final: el arranque de
        // cada host siguiente crea una "General" para el tenant que no tenga ninguna, y chocaría con esta.
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, null);
        await apoyo.ModificarListaAsync(tienda.IdListaDefault, l => l.EsDefault = false);

        try
        {
            Assert.Empty(ApoyoDeCatalogoOnline.Nombres(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)));
        }
        finally
        {
            await apoyo.ModificarListaAsync(tienda.IdListaDefault, l => l.EsDefault = true);
        }
    }

    // ---- búsqueda y categorías --------------------------------------------------------------------

    [Fact]
    public async Task LaBusquedaEncuentraPorNombreSinMayusculasNiAcentosNoPorCodigoInternoYTrataElPorcentajeComoLiteral()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-buscar");
        await apoyo.SembrarVendibleAsync(tienda, "Ñandú de peluche", 10m, codigoInterno: "PEL-001");
        await apoyo.SembrarVendibleAsync(tienda, "Café molido", 20m, codigoInterno: "CAF-777");
        await apoyo.SembrarVendibleAsync(tienda, "Descuento 50% especial", 30m, codigoInterno: "X-1");
        await apoyo.SembrarVendibleAsync(tienda, "Zapallo", 40m, codigoInterno: "ZAP-050");
        await apoyo.SembrarVendibleAsync(tienda, "Sin publicar café", 50m, publicado: false);
        await apoyo.SembrarVendibleAsync(tienda, new string('a', 120), 60m);

        using var cliente = ClienteEn(tienda);

        async Task<string[]> BuscarAsync(string buscar) =>
            ApoyoDeCatalogoOnline.Nombres(await ApoyoDeCatalogoOnline.CatalogoAsync(
                cliente, $"?buscar={Uri.EscapeDataString(buscar)}"));

        Assert.Equal(["Ñandú de peluche"], await BuscarAsync("nandu"));
        Assert.Equal(["Ñandú de peluche"], await BuscarAsync("ÑANDÚ"));
        Assert.Equal(["Café molido"], await BuscarAsync("CAFE"));

        // El código interno no se publica, así que tampoco se busca: encontrarlo sería un oráculo sobre un dato
        // que la tienda no muestra.
        Assert.Empty(await BuscarAsync("caf-777"));
        Assert.Empty(await BuscarAsync("zap-050"));
        Assert.Empty(await BuscarAsync("PEL-001"));
        Assert.Equal(["Café molido"], await BuscarAsync("  cafe  "));
        Assert.Equal(["Descuento 50% especial"], await BuscarAsync("50%"));
        Assert.Empty(await BuscarAsync("_"));
        Assert.Empty(await BuscarAsync("inexistente"));

        // Vacío o solo espacios: sin búsqueda.
        Assert.Equal(5, (await BuscarAsync("   ")).Length);
        Assert.Equal(5, ApoyoDeCatalogoOnline.Nombres(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, "?buscar=")).Length);

        // El término se acota a 100 caracteres: uno de 150 "a" encuentra el nombre de 120 (que contiene 100), y uno
        // enorme que no coincide no rompe nada.
        Assert.Equal([new string('a', 120)], await BuscarAsync(new string('a', 150)));
        Assert.Empty(await BuscarAsync(new string('x', 5_000)));
    }

    /// <summary>
    /// Un carácter de control en <c>buscar</c> (un <c>%00</c> es un NUL que Postgres rechaza con un error) se quita
    /// antes de armar el patrón: lo que queda es la búsqueda, y si no queda nada es "sin búsqueda".
    /// </summary>
    [Fact]
    public async Task LosCaracteresDeControlDeLaBusquedaSeQuitanYNuncaDan500()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-buscar-control");
        await apoyo.SembrarVendibleAsync(tienda, "Abcdario", 10m);
        await apoyo.SembrarVendibleAsync(tienda, "Otro artículo", 20m);

        using var cliente = ClienteEn(tienda);

        async Task<string[]> BuscarCrudoAsync(string buscarCodificado) =>
            ApoyoDeCatalogoOnline.Nombres(await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, $"?buscar={buscarCodificado}"));

        Assert.Equal(["Abcdario"], await BuscarCrudoAsync("%00abc"));
        Assert.Equal(["Abcdario"], await BuscarCrudoAsync("a%00b%01c%09"));
        Assert.Equal(["Abcdario", "Otro artículo"], await BuscarCrudoAsync("%00"));
        Assert.Equal(["Abcdario", "Otro artículo"], await BuscarCrudoAsync("%00%20%07%0A"));
    }

    /// <summary>
    /// Las categorías son las de los artículos que coinciden con la búsqueda, con su cuenta, IGNORANDO el filtro
    /// de categoría (los chips conservan sus cuentas con una seleccionada); el total y la página sí lo aplican.
    /// Una categoría inactiva, de otra empresa, dada de baja o de otro tenant no es un chip, sus artículos salen sin
    /// categoría y filtrar por ella no encuentra nada.
    /// </summary>
    [Fact]
    public async Task LasCategoriasCuentanLosPublicadosQueCoincidenIgnorandoElFiltroYLasInvisiblesNoCuentan()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-categorias");
        var ajena = await apoyo.SembrarTiendaAsync("pub-categorias-ajena");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var lacteos = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Lácteos", orden: 1);
        var limpieza = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Limpieza", orden: 2);
        var deLaEmpresa = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Solo de esta empresa", orden: 3, idEmpresa: tienda.IdEmpresa);
        var inactiva = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Inactiva", orden: 4, activo: false);
        var deOtraEmpresa = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "De otra empresa", orden: 5, idEmpresa: otraEmpresa);
        var eliminada = await apoyo.SembrarCategoriaAsync(tienda.IdTenant, "Eliminada", orden: 6);
        var deOtroTenant = await apoyo.SembrarCategoriaAsync(ajena.IdTenant, "De otro tenant", orden: 7);

        await apoyo.SembrarVendibleAsync(tienda, "Leche entera", 1m, idCategoria: lacteos);
        await apoyo.SembrarVendibleAsync(tienda, "Leche descremada", 2m, idCategoria: lacteos);
        await apoyo.SembrarVendibleAsync(tienda, "Queso", 3m, idCategoria: lacteos);
        await apoyo.SembrarVendibleAsync(tienda, "Lavandina de leche", 4m, idCategoria: limpieza);
        await apoyo.SembrarVendibleAsync(tienda, "Estante", 5m, idCategoria: deLaEmpresa);
        await apoyo.SembrarVendibleAsync(tienda, "Del chip inactivo", 6m, idCategoria: inactiva);
        await apoyo.SembrarVendibleAsync(tienda, "Del chip de otra empresa", 7m, idCategoria: deOtraEmpresa);
        await apoyo.SembrarVendibleAsync(tienda, "Del chip eliminado", 8m, idCategoria: eliminada);
        await apoyo.SembrarVendibleAsync(tienda, "Sin categoría", 9m);
        await apoyo.SembrarVendibleAsync(tienda, "Lácteo sin publicar", 10m, idCategoria: lacteos, publicado: false);
        await apoyo.ModificarCategoriaAsync(eliminada, c => c.DeletedAt = DateTimeOffset.UtcNow);

        using var cliente = ClienteEn(tienda);

        static (int Id, int Cantidad)[] Chips(JsonElement catalogo) =>
            [.. catalogo.GetProperty("categorias").EnumerateArray()
                .Select(c => (c.GetProperty("id").GetInt32(), c.GetProperty("cantidad").GetInt32()))];

        (int Id, int Cantidad)[] chipsSinFiltro = [(lacteos, 3), (limpieza, 1), (deLaEmpresa, 1)];

        var todos = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente);
        Assert.Equal(chipsSinFiltro, Chips(todos));
        Assert.Equal(9, todos.GetProperty("total").GetInt32());
        var idsDeCategoria = todos.GetProperty("articulos").EnumerateArray()
            .ToDictionary(a => a.GetProperty("nombre").GetString()!, a => a.GetProperty("idCategoria"));
        Assert.Equal(lacteos, idsDeCategoria["Queso"].GetInt32());
        foreach (var nombre in new[] { "Del chip inactivo", "Del chip de otra empresa", "Del chip eliminado", "Sin categoría" })
        {
            Assert.Equal(JsonValueKind.Null, idsDeCategoria[nombre].ValueKind);
        }

        // Con una categoría seleccionada: sus artículos, pero los chips conservan sus cuentas.
        var soloLacteos = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, $"?idCategoria={lacteos}");
        Assert.Equal(["Leche descremada", "Leche entera", "Queso"], ApoyoDeCatalogoOnline.Nombres(soloLacteos));
        Assert.Equal(3, soloLacteos.GetProperty("total").GetInt32());
        Assert.Equal(chipsSinFiltro, Chips(soloLacteos));

        // Categoría y búsqueda juntas: los chips cuentan lo que coincide con la búsqueda, no con la categoría.
        var conBusqueda = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, $"?idCategoria={limpieza}&buscar=leche");
        Assert.Equal(["Lavandina de leche"], ApoyoDeCatalogoOnline.Nombres(conBusqueda));
        Assert.Equal([(lacteos, 2), (limpieza, 1)], Chips(conBusqueda));

        // Una categoría que no se ve no encuentra nada, ni siquiera los artículos que la tenían guardada.
        foreach (var invisible in new[] { inactiva, deOtraEmpresa, eliminada, deOtroTenant, 987_654_321 })
        {
            var catalogo = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, $"?idCategoria={invisible}");

            Assert.Empty(ApoyoDeCatalogoOnline.Nombres(catalogo));
            Assert.Equal(0, catalogo.GetProperty("total").GetInt32());
            Assert.Equal(chipsSinFiltro, Chips(catalogo));
        }
    }

    // ---- paginación -------------------------------------------------------------------------------

    [Fact]
    public async Task LaPaginaYElTamanoSeAcotanYLasPaginasRecorrenTodoElCatalogoSinRepetir()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-paginas");
        for (var i = 1; i <= 65; i++)
        {
            await apoyo.SembrarVendibleAsync(tienda, $"P{i:D2}", i);
        }

        using var cliente = ClienteEn(tienda);

        async Task<JsonElement> PedirAsync(string consulta) => await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, consulta);

        var porDefecto = await PedirAsync("");
        Assert.Equal((65, 1, 24), (porDefecto.GetProperty("total").GetInt32(), porDefecto.GetProperty("pagina").GetInt32(), porDefecto.GetProperty("tamano").GetInt32()));
        Assert.Equal(24, porDefecto.GetProperty("articulos").GetArrayLength());

        // El tamaño se acota a 1..60.
        Assert.Equal(60, (await PedirAsync("?tamano=1000")).GetProperty("tamano").GetInt32());
        Assert.Equal(60, (await PedirAsync("?tamano=61")).GetProperty("articulos").GetArrayLength());
        Assert.Equal(60, (await PedirAsync("?tamano=60")).GetProperty("articulos").GetArrayLength());
        var minimo = await PedirAsync("?tamano=0");
        Assert.Equal(1, minimo.GetProperty("tamano").GetInt32());
        Assert.Equal(["P01"], ApoyoDeCatalogoOnline.Nombres(minimo));
        Assert.Equal(1, (await PedirAsync("?tamano=-7")).GetProperty("tamano").GetInt32());

        // La página se acota a 1.
        foreach (var pagina in new[] { "0", "-5" })
        {
            var p = await PedirAsync($"?pagina={pagina}");
            Assert.Equal(1, p.GetProperty("pagina").GetInt32());
            Assert.Equal("P01", p.GetProperty("articulos")[0].GetProperty("nombre").GetString());
        }

        // Recorrerlo de a 24: 24 + 24 + 17, sin huecos ni repetidos, ordenado por nombre.
        var vistos = new List<string>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var p = await PedirAsync($"?pagina={pagina}");
            Assert.Equal(65, p.GetProperty("total").GetInt32());
            Assert.Equal(pagina, p.GetProperty("pagina").GetInt32());
            vistos.AddRange(ApoyoDeCatalogoOnline.Nombres(p));
        }

        Assert.Equal(Enumerable.Range(1, 65).Select(i => $"P{i:D2}"), vistos);

        // Una página más allá del final: vacía, con el total.
        var mas = await PedirAsync("?pagina=4");
        Assert.Empty(ApoyoDeCatalogoOnline.Nombres(mas));
        Assert.Equal(65, mas.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Una página enorme se acota a <see cref="ConsultaDeCatalogoPublico.PaginaMaxima"/>: sin tope, el
    /// desplazamiento (<c>(pagina - 1) * tamano</c>) desbordaba el <c>int</c>, daba negativo y Postgres rechazaba
    /// el <c>OFFSET</c> con un 500. Una página más allá del final es vacía, con el <c>total</c> real.
    /// </summary>
    [Theory]
    [InlineData("2147483647")]
    [InlineData("10001")]
    [InlineData("10000")]
    public async Task UnaPaginaDescomunalSeAcotaYDaUnaPaginaVaciaConElTotalReal(string pagina)
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-pagina-enorme");
        await apoyo.SembrarVendibleAsync(tienda, "Uno", 1m);
        await apoyo.SembrarVendibleAsync(tienda, "Dos", 2m);
        await apoyo.SembrarVendibleAsync(tienda, "Tres", 3m);

        using var cliente = ClienteEn(tienda);

        var catalogo = await ApoyoDeCatalogoOnline.CatalogoAsync(cliente, $"?pagina={pagina}");

        Assert.Empty(ApoyoDeCatalogoOnline.Nombres(catalogo));
        Assert.Equal(3, catalogo.GetProperty("total").GetInt32());
        Assert.Equal(ConsultaDeCatalogoPublico.PaginaMaxima, catalogo.GetProperty("pagina").GetInt32());
    }

    /// <summary>Un parámetro que no es un número es un 400 del binding, no un 500.</summary>
    [Theory]
    [InlineData("?idCategoria=abc")]
    [InlineData("?pagina=x")]
    [InlineData("?tamano=1.5")]
    public async Task UnParametroQueNoEsUnNumeroDa400(string consulta)
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-binding");

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo + consulta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    // ---- sin vitrina publicada --------------------------------------------------------------------

    [Fact]
    public async Task SinVitrinaPublicadaElCatalogoYLaImagenDan404VitrinaNoPublicada()
    {
        var sinVitrina = await apoyo.SembrarTiendaAsync("pub-sin-vitrina", vitrinaPublicada: false);
        var sinPublicar = await apoyo.SembrarTiendaAsync("pub-sin-publicar", vitrinaPublicada: false);
        await apoyo.Vitrinas.SembrarVitrinaAsync(sinPublicar.IdTenant, sinPublicar.IdEmpresa, publicada: false);
        var eliminada = await apoyo.SembrarTiendaAsync("pub-eliminada", vitrinaPublicada: false);
        await apoyo.Vitrinas.SembrarVitrinaAsync(eliminada.IdTenant, eliminada.IdEmpresa, publicada: true, eliminada: true);
        var suspendida = await apoyo.SembrarTiendaAsync("pub-suspendida");

        var articulos = new Dictionary<string, int>();
        foreach (var tienda in new[] { sinVitrina, sinPublicar, eliminada, suspendida })
        {
            var id = await apoyo.SembrarVendibleAsync(tienda, "Producto", 10m);
            await apoyo.SembrarImagenAsync(tienda.IdTenant, id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));
            articulos[tienda.Host] = id;
        }

        // Control: la suspendida sirve su catálogo hasta que se la suspende.
        using (var control = ClienteEn(suspendida))
        {
            Assert.Equal(HttpStatusCode.OK, (await control.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo)).StatusCode);
        }

        await apoyo.Vitrinas.SuspenderTenantAsync(suspendida.IdTenant);

        foreach (var tienda in new[] { sinVitrina, sinPublicar, eliminada })
        {
            using var cliente = ClienteEn(tienda);

            var catalogo = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo);
            Assert.Equal(HttpStatusCode.NotFound, catalogo.StatusCode);
            Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(catalogo));
            Assert.NotEqual("public, max-age=60", catalogo.Headers.CacheControl?.ToString());

            var imagen = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(articulos[tienda.Host]));
            Assert.Equal(HttpStatusCode.NotFound, imagen.StatusCode);
        }

        // La suspensión se ve recién cuando vence la caché de resolución del host (60 s): un host nuevo, sin caché.
        var otroHost = ApoyoDeDominios.HostUnico("pub-suspendida-2");
        await apoyo.Dominios.SembrarDominioAsync(suspendida.IdTenant, otroHost);
        using (var cliente = apoyo.Dominios.ClienteEnHost(otroHost))
        {
            var catalogo = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo);
            Assert.Equal(HttpStatusCode.NotFound, catalogo.StatusCode);
            Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(catalogo));

            var imagen = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(articulos[suspendida.Host]));
            Assert.Equal(HttpStatusCode.NotFound, imagen.StatusCode);
        }
    }

    [Fact]
    public async Task EnElHostDeLaPlataformaNoHayCatalogo()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaCatalogo);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(1))).StatusCode);
    }

    // ---- la imagen --------------------------------------------------------------------------------

    [Fact]
    public async Task LaImagenSeSirveConSuTipoCacheDeUnaHoraEtagYUnaCondicionalDa304()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-imagen");
        var id = await apoyo.SembrarVendibleAsync(tienda, "Con imagen", 10m);
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoWebp, largo: 300, relleno: 9);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, bytes, ValidadorDeImagen.TipoWebp);
        var guardada = Assert.Single(await apoyo.LeerImagenesAsync(id));
        var ruta = ApoyoDeCatalogoOnline.RutaImagenPublica(id);

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ruta);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(bytes, await respuesta.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/webp", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=3600", respuesta.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", respuesta.Headers.GetValues("X-Content-Type-Options").Single());

        var etag = $"\"{guardada.ActualizadoEn.UtcTicks:x}\"";
        Assert.Equal(etag, respuesta.Headers.ETag?.Tag);

        using var condicional = new HttpRequestMessage(HttpMethod.Get, ruta);
        condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var noModificado = await cliente.SendAsync(condicional);

        Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
        Assert.Empty(await noModificado.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, noModificado.Headers.ETag?.Tag);
        Assert.Equal("public, max-age=3600", noModificado.Headers.CacheControl?.ToString());

        using var otroEtag = new HttpRequestMessage(HttpMethod.Get, ruta);
        otroEtag.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"viejo\""));
        var distinto = await cliente.SendAsync(otroEtag);

        Assert.Equal(HttpStatusCode.OK, distinto.StatusCode);
        Assert.Equal(bytes, await distinto.Content.ReadAsByteArrayAsync());
    }

    /// <summary>El catálogo anuncia la imagen con su versión y la versión cambia cuando se reemplaza.</summary>
    [Fact]
    public async Task LaVersionDeLaImagenDelCatalogoCambiaCuandoSeReemplazaYDesapareceCuandoSeQuita()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-version");
        var admin = await apoyo.Vitrinas.ClienteAdminAsync(tienda.Comercio);
        var id = await apoyo.SembrarVendibleAsync(tienda, "Versionada", 10m);
        var ruta = $"/api/articulos/{id}/imagen";
        using var cliente = ClienteEn(tienda);

        async Task<JsonElement> ArticuloAsync() =>
            (await ApoyoDeCatalogoOnline.CatalogoAsync(cliente)).GetProperty("articulos")[0];

        Assert.False((await ArticuloAsync()).GetProperty("tieneImagen").GetBoolean());

        var primera = new HttpRequestMessage(HttpMethod.Put, ruta) { Content = ApoyoDeVitrinas.Contenido(ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, relleno: 1), "image/png") };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(primera)).StatusCode);
        var v1 = (await ArticuloAsync()).GetProperty("imagenVersion").GetInt64();
        Assert.True((await ArticuloAsync()).GetProperty("tieneImagen").GetBoolean());

        await Task.Delay(1100);
        var segunda = new HttpRequestMessage(HttpMethod.Put, ruta) { Content = ApoyoDeVitrinas.Contenido(ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, relleno: 2), "image/png") };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(segunda)).StatusCode);
        var v2 = (await ArticuloAsync()).GetProperty("imagenVersion").GetInt64();
        Assert.True(v2 > v1);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(ruta)).StatusCode);
        var sin = await ArticuloAsync();
        Assert.False(sin.GetProperty("tieneImagen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sin.GetProperty("imagenVersion").ValueKind);
    }

    /// <summary>
    /// 404 <c>imagen_no_encontrada</c> para cada artículo cuya imagen no es del catálogo de ESTE host. Cada uno
    /// tiene imagen cargada, así que lo único que la oculta es su propia cláusula: sin publicar, inactivo, dado de
    /// baja, restringido a otra empresa, de otro tenant. Con una condicional que coincidiría tampoco hay 304: no se
    /// confirma la existencia de lo que no se sirve.
    /// </summary>
    [Fact]
    public async Task LaImagenDeUnArticuloQueElCatalogoNoMuestraDa404AunConCondicional()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-imagen-404");
        var ajena = await apoyo.SembrarTiendaAsync("pub-imagen-404-ajena");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, relleno: 6);

        var sinPublicar = await apoyo.SembrarArticuloAsync(tienda, "Sin publicar", publicado: false);
        var inactivo = await apoyo.SembrarArticuloAsync(tienda, "Inactivo", activo: false);
        var eliminado = await apoyo.SembrarArticuloAsync(tienda, "Eliminado", eliminado: true);
        var deOtraEmpresa = await apoyo.SembrarArticuloAsync(tienda, "De otra empresa", idEmpresaDisponible: otraEmpresa);
        var deNinguna = await apoyo.SembrarArticuloAsync(tienda, "Restringido a ninguna", restringidoSinEmpresas: true);
        var deOtroTenant = await apoyo.SembrarArticuloAsync(ajena, "De otro tenant");
        var sinImagen = await apoyo.SembrarArticuloAsync(tienda, "Sin imagen");
        var conImagenEliminada = await apoyo.SembrarArticuloAsync(tienda, "Imagen eliminada");

        // Uno que sí se sirve: pedir la imagen de otro artículo no puede devolver esta.
        var visible = await apoyo.SembrarArticuloAsync(tienda, "Visible con imagen");
        await apoyo.SembrarImagenAsync(tienda.IdTenant, visible, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoJpeg, relleno: 3), ValidadorDeImagen.TipoJpeg);

        var conImagen = new Dictionary<int, int>
        {
            [sinPublicar] = tienda.IdTenant, [inactivo] = tienda.IdTenant, [eliminado] = tienda.IdTenant,
            [deOtraEmpresa] = tienda.IdTenant, [deNinguna] = tienda.IdTenant, [deOtroTenant] = ajena.IdTenant
        };
        foreach (var (idArticulo, idTenant) in conImagen)
        {
            await apoyo.SembrarImagenAsync(idTenant, idArticulo, bytes);
        }

        await apoyo.SembrarImagenAsync(tienda.IdTenant, conImagenEliminada, bytes, eliminada: true);

        using var cliente = ClienteEn(tienda);

        foreach (var id in conImagen.Keys.Concat([sinImagen, conImagenEliminada, 987_654_321]))
        {
            var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(id));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("imagen_no_encontrada", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
            Assert.NotEqual("public, max-age=3600", respuesta.Headers.CacheControl?.ToString());
        }

        foreach (var id in conImagen.Keys)
        {
            var guardada = (await apoyo.LeerImagenesAsync(id)).Single();
            using var condicional = new HttpRequestMessage(HttpMethod.Get, ApoyoDeCatalogoOnline.RutaImagenPublica(id));
            condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{guardada.ActualizadoEn.UtcTicks:x}\""));

            Assert.Equal(HttpStatusCode.NotFound, (await cliente.SendAsync(condicional)).StatusCode);
        }
    }

    /// <summary>La imagen de un artículo restringido a la empresa de la vitrina sí se sirve: la disponibilidad por
    /// empresa no es solo un filtro del catálogo.</summary>
    [Fact]
    public async Task LaImagenDeUnArticuloRestringidoALaEmpresaDeLaVitrinaSeSirve()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-imagen-empresa");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Solo esta empresa", idEmpresaDisponible: tienda.IdEmpresa);
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoJpeg, relleno: 5);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, bytes, ValidadorDeImagen.TipoJpeg);

        using var cliente = ClienteEn(tienda);
        var respuesta = await cliente.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(id));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(bytes, await respuesta.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UnaImagenDeOtroTenantNoSeSirveEnElHostDeEsteAunConSuId()
    {
        var a = await apoyo.SembrarTiendaAsync("pub-imagen-host-a");
        var b = await apoyo.SembrarTiendaAsync("pub-imagen-host-b");
        var deB = await apoyo.SembrarVendibleAsync(b, "De B", 10m);
        await apoyo.SembrarImagenAsync(b.IdTenant, deB, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));

        using var clienteA = ClienteEn(a);
        using var clienteB = ClienteEn(b);

        Assert.Equal(HttpStatusCode.NotFound, (await clienteA.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(deB))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clienteB.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(deB))).StatusCode);
    }

    [Fact]
    public async Task UnIdQueNoEsUnNumeroEnLaRutaDeLaImagenDa404()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-imagen-ruta");

        using var cliente = ClienteEn(tienda);

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/publico/articulos/abc/imagen")).StatusCode);
    }

    /// <summary>Una condicional que coincide contesta 304 SIN leer la columna <c>contenido</c>: el lector real va
    /// envuelto en uno que cuenta cada llamada que trae los bytes. Solo la request que no coincide pasa a la
    /// segunda consulta.</summary>
    [Fact]
    public async Task UnaCondicionalQueCoincideDa304SinLeerLosBytesDeLaImagen()
    {
        var tienda = await apoyo.SembrarTiendaAsync("pub-imagen-304-sin-bytes");
        var id = await apoyo.SembrarVendibleAsync(tienda, "Sin leer bytes", 10m);
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, largo: 300, relleno: 4);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, bytes);
        var etag = $"\"{Assert.Single(await apoyo.LeerImagenesAsync(id)).ActualizadoEn.UtcTicks:x}\"";
        var ruta = ApoyoDeCatalogoOnline.RutaImagenPublica(id);

        var lector = new LectorQueCuentaLasLecturas();
        await using var host = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(servicios =>
        {
            servicios.RemoveAll<ILectorDeCatalogoPublico>();
            servicios.AddSingleton<ILectorDeCatalogoPublico>(sp =>
            {
                lector.Real = new LectorDeCatalogoPublico(sp.GetRequiredService<IServiceScopeFactory>());
                return lector;
            });
        }));
        using var cliente = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{tienda.Host}")
        });

        var completa = await cliente.GetAsync(ruta);
        Assert.Equal(HttpStatusCode.OK, completa.StatusCode);
        Assert.Equal(bytes, await completa.Content.ReadAsByteArrayAsync());
        Assert.Equal((1, 1), (lector.LecturasDeVersion, lector.LecturasDeBytes));

        foreach (var condicion in new[] { new EntityTagHeaderValue(etag), EntityTagHeaderValue.Any, new EntityTagHeaderValue(etag, isWeak: true) })
        {
            using var condicional = new HttpRequestMessage(HttpMethod.Get, ruta);
            condicional.Headers.IfNoneMatch.Add(condicion);

            var noModificado = await cliente.SendAsync(condicional);

            Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
            Assert.Empty(await noModificado.Content.ReadAsByteArrayAsync());
            Assert.Equal(etag, noModificado.Headers.ETag?.Tag);
        }

        // Tres condicionales que coinciden: tres lecturas de versión más y ninguna de bytes.
        Assert.Equal((4, 1), (lector.LecturasDeVersion, lector.LecturasDeBytes));

        using var otroEtag = new HttpRequestMessage(HttpMethod.Get, ruta);
        otroEtag.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"viejo\""));
        var distinto = await cliente.SendAsync(otroEtag);

        Assert.Equal(HttpStatusCode.OK, distinto.StatusCode);
        Assert.Equal((5, 2), (lector.LecturasDeVersion, lector.LecturasDeBytes));
    }

    /// <summary>Envuelve al lector real y cuenta cuántas veces se pidió la versión de la imagen y cuántas sus
    /// bytes: es lo que prueba que la columna no se leyó.</summary>
    private sealed class LectorQueCuentaLasLecturas : ILectorDeCatalogoPublico
    {
        private int lecturasDeVersion;
        private int lecturasDeBytes;

        public ILectorDeCatalogoPublico Real { get; set; } = null!;

        public int LecturasDeVersion => lecturasDeVersion;

        public int LecturasDeBytes => lecturasDeBytes;

        public Task<CatalogoPublico?> ObtenerAsync(int idTenant, ConsultaDeCatalogo consulta, CancellationToken ct = default) =>
            Real.ObtenerAsync(idTenant, consulta, ct);

        public Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default)
        {
            Interlocked.Increment(ref lecturasDeVersion);
            return Real.ObtenerVersionDeImagenAsync(idTenant, idArticulo, ct);
        }

        public Task<ImagenServida?> ObtenerImagenAsync(int idTenant, int idArticulo, CancellationToken ct = default)
        {
            Interlocked.Increment(ref lecturasDeBytes);
            return Real.ObtenerImagenAsync(idTenant, idArticulo, ct);
        }
    }
}
