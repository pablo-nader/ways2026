using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios; // SolicitudDeLogin
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-articulos-grilla-api: <c>GET /api/articulos/grilla</c> contra Postgres real — el
/// listado dedicado del back-office (precio de la lista default + proveedor + estado), distinto
/// de <c>GET /api/articulos</c> (<c>ArticulosEndpointsTests</c>/<c>ArticulosFiltrosTests</c>, que
/// sigue intacto). Cada filtro se sembró de forma asimétrica (mutation-proof-tests): borrar la
/// guarda de un filtro tiene que mover el <c>Total</c> de alguna prueba, nunca dejarlo
/// byte-idéntico.
///
/// Precios sembrados con <see cref="MomentoDePrecioFijo"/> (2024-01-01, bien en el pasado, sin
/// <c>vigente_hasta</c>) — nunca <c>DateTimeOffset.UtcNow</c>: la fila queda vigente sin importar
/// cuándo corra la prueba, sin depender de un reloj pineado en el host (mutation-proof-tests
/// regla 14 — acá no hay una comparación de fecha que cruce un límite de día, así que un instante
/// fijo bien en el pasado alcanza).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosGrillaEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly DateTimeOffset MomentoDePrecioFijo = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    // ---- provisioning ---------------------------------------------------------------------------

    private static async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, int IdCondicionFiscalCf,
            int IdListaGeneral, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre, WaysApiFixture fixture, WebApplicationFactory<Program> factory)
    {
        using var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idCondicionFiscalCf = await db.CondicionesFiscales
            .Where(c => c.Codigo == "CF")
            .Select(c => c.Id)
            .SingleAsync();
        var idListaGeneral = await db.ListasPrecio
            .Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        return (resultado.IdTenant, area.Id, idAlicuotaIva, idCondicionFiscalCf, idListaGeneral, mailAdmin, resultado.PasswordTemporal);
    }

    private static async Task<HttpClient> ClienteLogueadoAsync(
        string mail, string password, WebApplicationFactory<Program> factory)
    {
        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    // ---- seeding ----------------------------------------------------------------------------------

    private async Task<int> SembrarArticuloAsync(
        int idTenant, string nombre, int idArea, int idAlicuotaIva,
        int? idProveedorHabitual = null, bool activo = true, bool eliminado = false, string? codigoInterno = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = codigoInterno ?? $"{nombre}-{Guid.NewGuid():N}",
            Nombre = nombre,
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            IdProveedorHabitual = idProveedorHabitual,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            Activo = activo,
            DeletedAt = eliminado ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task SembrarCodigoBarraAsync(int idTenant, int idArticulo, string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        db.CodigosBarra.Add(new CodigoBarra
        {
            IdTenant = idTenant, IdArticulo = idArticulo, Codigo = codigo, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarProveedorAsync(
        int idTenant, int idCondicionFiscal, string razonSocial, string? nombreFantasia = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var proveedor = new Proveedor
        {
            IdTenant = idTenant, RazonSocial = razonSocial, NombreFantasia = nombreFantasia,
            IdCondicionFiscal = idCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return proveedor.Id;
    }

    private async Task SembrarPrecioAsync(int idTenant, int idArticulo, int idListaPrecio, decimal monto)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = idListaPrecio,
            Monto = monto,
            VigenteDesde = MomentoDePrecioFijo,
            VigenteHasta = null,
            CreatedAt = MomentoDePrecioFijo,
            UpdatedAt = MomentoDePrecioFijo
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa.Id;
    }

    /// <summary>Lista default PROPIA de una empresa (<c>ux_listas_precio_default_empresa</c>) —
    /// coexiste con la default COMPARTIDA sin chocar (son dos índices únicos parciales
    /// distintos): usada para probar que la resolución de precio de esta grilla (que no tiene
    /// <c>idEmpresa</c>) ignora esta lista y usa la compartida.</summary>
    private async Task<(int Id, string Nombre)> SembrarListaPrecioDefaultDeEmpresaAsync(
        int idTenant, int idEmpresa, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = idTenant, IdEmpresa = idEmpresa, Nombre = nombre, EsDefault = true,
            Modo = ModoLista.Fija, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return (lista.Id, lista.Nombre);
    }

    /// <summary>Desmarca la default compartida actual (la "General" del aprovisionamiento) y
    /// promueve una lista DERIVADA nueva en su lugar — dos <c>SaveChangesAsync</c> secuenciales
    /// (no simultáneos) para no chocar contra <c>ux_listas_precio_default_compartido</c>, mismo
    /// orden que el intercambio real de <c>ServicioDeListasPrecio.DesmarcarDefaultActualAsync</c>.</summary>
    private async Task<(int Id, string Nombre)> PromoverListaDerivadaComoDefaultAsync(
        int idTenant, int idListaBase, decimal porcentaje, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var actualDefault = await db.ListasPrecio
            .SingleAsync(l => l.IdTenant == idTenant && l.IdEmpresa == null && l.EsDefault);
        actualDefault.EsDefault = false;
        actualDefault.UpdatedAt = ahora;
        await db.SaveChangesAsync();

        var derivada = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = true, Modo = ModoLista.Derivada,
            IdListaBase = idListaBase, Porcentaje = porcentaje, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(derivada);
        await db.SaveChangesAsync();

        return (derivada.Id, derivada.Nombre);
    }

    /// <summary>Desmarca la default compartida SIN reemplazo — deja al tenant sin ninguna lista
    /// default (alcanzable solo con escritura directa: <c>ServicioDeListasPrecio</c> nunca lo
    /// permite, spec "One Default List Per Tenant"). Usado para probar el camino "sin lista
    /// default" del endpoint.</summary>
    private async Task QuitarLaListaDefaultAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var actualDefault = await db.ListasPrecio
            .SingleAsync(l => l.IdTenant == idTenant && l.IdEmpresa == null && l.EsDefault);
        actualDefault.EsDefault = false;
        actualDefault.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Contraparte de <see cref="QuitarLaListaDefaultAsync"/> — SIEMPRE se llama en un
    /// <c>finally</c>: dejar un tenant sin ninguna lista default compartida hace que
    /// <c>InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync</c> (corre en CADA
    /// arranque de host, incluido el de un <c>WithWebHostBuilder</c> nuevo de otra prueba de esta
    /// misma clase o de cualquier otra que comparta el contenedor) intente sembrar una SEGUNDA
    /// lista "General" para ese tenant y choque contra <c>ux_listas_precio_nombre_compartido</c> —
    /// tumbando el arranque de ESE host, no solo esta prueba.</summary>
    private async Task RestaurarLaListaDefaultAsync(int idListaGeneral)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var lista = await db.ListasPrecio.SingleAsync(l => l.Id == idListaGeneral);
        lista.EsDefault = true;
        lista.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string UrlGrilla(string query) => $"/api/articulos/grilla?{query}";

    // ---- codigo: codigo_interno (mutation target: `a.CodigoInterno.Contains(termino)`) ----------

    [Fact]
    public async Task CodigoFiltraPorCodigoInterno()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CodigoFiltraPorCodigoInterno), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var matchea = await SembrarArticuloAsync(idTenant, "A", idArea, idAlicuotaIva, codigoInterno: "SKU-MATCH-001");
        await SembrarArticuloAsync(idTenant, "B", idArea, idAlicuotaIva, codigoInterno: "OTRO-999");

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("codigo=match"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(matchea, Assert.Single(pagina.Items).Id);
    }

    // ---- codigo: codigos_barra (mutation target: el EXISTS de codigos_barra) ----------------------

    [Fact]
    public async Task CodigoFiltraPorCodigoDeBarra()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CodigoFiltraPorCodigoDeBarra), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var escaneado = await SembrarArticuloAsync(idTenant, "Escaneado", idArea, idAlicuotaIva, codigoInterno: "COD-1");
        await SembrarCodigoBarraAsync(idTenant, escaneado, "7791234567890");
        var otro = await SembrarArticuloAsync(idTenant, "Otro", idArea, idAlicuotaIva, codigoInterno: "COD-2");
        await SembrarCodigoBarraAsync(idTenant, otro, "1112223334445");

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("codigo=7791234567890"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(escaneado, Assert.Single(pagina.Items).Id);
    }

    // ---- nombre (mutation target: `a.Nombre.Contains(terminoNombre)`) ------------------------------

    [Fact]
    public async Task NombreFiltraPorNombre()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(NombreFiltraPorNombre), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var matchea = await SembrarArticuloAsync(idTenant, "Coca Cola 500ml", idArea, idAlicuotaIva);
        await SembrarArticuloAsync(idTenant, "Agua Mineral", idArea, idAlicuotaIva);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("nombre=cola"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(matchea, Assert.Single(pagina.Items).Id);
    }

    // ---- precioDesde / precioHasta: límites inclusivos (mutation target: `<`/`>` en CumpleFiltroDePrecio) ----

    [Fact]
    public async Task PrecioDesdeEsInclusivoEnElLimite()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(PrecioDesdeEsInclusivoEnElLimite), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var debajo = await SembrarArticuloAsync(idTenant, "Debajo", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, debajo, idListaGeneral, 99m);
        var enElLimite = await SembrarArticuloAsync(idTenant, "EnElLimite", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, enElLimite, idListaGeneral, 100m);
        var arriba = await SembrarArticuloAsync(idTenant, "Arriba", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, arriba, idListaGeneral, 101m);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioDesde=100&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        var ids = pagina.Items.Select(i => i.Id).ToList();
        Assert.Contains(enElLimite, ids);
        Assert.Contains(arriba, ids);
        Assert.DoesNotContain(debajo, ids);
    }

    [Fact]
    public async Task PrecioHastaEsInclusivoEnElLimite()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(PrecioHastaEsInclusivoEnElLimite), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var debajo = await SembrarArticuloAsync(idTenant, "Debajo", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, debajo, idListaGeneral, 99m);
        var enElLimite = await SembrarArticuloAsync(idTenant, "EnElLimite", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, enElLimite, idListaGeneral, 100m);
        var arriba = await SembrarArticuloAsync(idTenant, "Arriba", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, arriba, idListaGeneral, 101m);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioHasta=100&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        var ids = pagina.Items.Select(i => i.Id).ToList();
        Assert.Contains(debajo, ids);
        Assert.Contains(enElLimite, ids);
        Assert.DoesNotContain(arriba, ids);
    }

    // ---- artículo sin precio nunca matchea un filtro de precio activo ------------------------------

    [Fact]
    public async Task ArticuloSinPrecioNuncaMatcheaUnFiltroDePrecioActivo()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ArticuloSinPrecioNuncaMatcheaUnFiltroDePrecioActivo), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var conPrecio = await SembrarArticuloAsync(idTenant, "ConPrecio", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, conPrecio, idListaGeneral, 50m);
        await SembrarArticuloAsync(idTenant, "SinPrecio", idArea, idAlicuotaIva);

        // precioDesde=0: el más laxo posible — si el guard de "sin precio" se rompiera, un
        // artículo sin precio pasaría igual (0 no es un límite exigente).
        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioDesde=0&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(conPrecio, Assert.Single(pagina.Items).Id);
    }

    // ---- artículo sin precio nunca matchea un filtro `precioHasta` solo (rama independiente de la
    // de `precioDesde` en `CumpleFiltroDePrecio`) --------------------------------------------------

    [Fact]
    public async Task ArticuloSinPrecioNuncaMatcheaUnFiltroDePrecioHastaSolo()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ArticuloSinPrecioNuncaMatcheaUnFiltroDePrecioHastaSolo), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var conPrecio = await SembrarArticuloAsync(idTenant, "ConPrecio", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, conPrecio, idListaGeneral, 50m);
        await SembrarArticuloAsync(idTenant, "SinPrecio", idArea, idAlicuotaIva);

        // precioHasta muy laxo: si el guard de "sin precio" de la rama `precioHasta` se rompiera,
        // el artículo sin precio pasaría igual (un límite alto no lo filtraría por valor).
        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioHasta=999999&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(conPrecio, Assert.Single(pagina.Items).Id);
    }

    // ---- sin lista default: precio null en todas las filas, y el filtro de precio da vacío --------

    [Fact]
    public async Task SinListaDefaultElPrecioEsNuloYElFiltroDePrecioDaVacio()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(SinListaDefaultElPrecioEsNuloYElFiltroDePrecioDaVacio), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var articulo = await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);
        // Precio real en una lista que YA NO es default — probar que el endpoint no "cae" a
        // cualquier lista fija cuando no hay default, sino que devuelve null a propósito.
        await SembrarPrecioAsync(idTenant, articulo, idListaGeneral, 500m);
        await QuitarLaListaDefaultAsync(idTenant);

        try
        {
            var sinFiltro = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);
            Assert.NotNull(sinFiltro);
            Assert.Null(sinFiltro!.NombreListaPrecio);
            Assert.Null(Assert.Single(sinFiltro.Items).Precio);

            var conFiltro = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
                UrlGrilla("precioDesde=0&tamanio=50"), OpcionesJson);
            Assert.NotNull(conFiltro);
            Assert.Equal(0, conFiltro!.Total);
        }
        finally
        {
            await RestaurarLaListaDefaultAsync(idListaGeneral);
        }
    }

    // ---- precio de una lista default FIJA (la "General" del aprovisionamiento) ---------------------

    [Fact]
    public async Task ElPrecioSeResuelveDeLaListaDefaultFija()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElPrecioSeResuelveDeLaListaDefaultFija), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var articulo = await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, articulo, idListaGeneral, 250m);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal("General", pagina!.NombreListaPrecio);
        var fila = Assert.Single(pagina.Items);
        Assert.Equal(250m, fila.Precio);
    }

    // ---- precio de una lista default DERIVADA -------------------------------------------------------

    [Fact]
    public async Task ElPrecioSeResuelveDeLaListaDefaultDerivada()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElPrecioSeResuelveDeLaListaDefaultDerivada), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var articulo = await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);
        // El precio SIEMPRE se guarda en la lista fija (base) — una derivada nunca tiene fila
        // propia (spec: Derived List Price Resolution At Read Time).
        await SembrarPrecioAsync(idTenant, articulo, idListaGeneral, 200m);

        var (idDerivada, nombreDerivada) = await PromoverListaDerivadaComoDefaultAsync(
            idTenant, idListaGeneral, porcentaje: 10m, nombre: "Derivada 10%");

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(nombreDerivada, pagina!.NombreListaPrecio);
        var fila = Assert.Single(pagina.Items);
        Assert.Equal(220m, fila.Precio); // 200 * 1.10, ResolvedorDePrecios.ResolverPrecioDerivado
        _ = idDerivada;
    }

    // ---- BuscarListaDefaultAsync: SOLO la default COMPARTIDA (id_empresa IS NULL) cuenta, nunca
    // la de una empresa (mutation target: `l.IdEmpresa == null`). Diseño deliberado: la default de
    // empresa queda como ÚNICO candidato `EsDefault=true` del tenant (compartida desmarcada, mismo
    // helper que "sin lista default") — con AMBAS marcadas default a la vez, un `FirstOrDefaultAsync`
    // sin ORDER BY es ambiguo entre 2 filas y el resultado depende del plan/orden físico de
    // Postgres, no del clause bajo prueba (confirmado corriendo la mutación con las dos default:
    // sobrevivió 27/27 — Postgres devolvió la compartida por casualidad de orden, no por el
    // filtro). Con un solo candidato posible bajo la mutación, la discriminación es 0-filas
    // (correcto) contra 1-fila (mutante), sin depender de ningún orden. --------------------------

    [Fact]
    public async Task LaGrillaIgnoraUnaListaDefaultDeEmpresaYNuncaLeResuelvePrecio()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(
                nameof(LaGrillaIgnoraUnaListaDefaultDeEmpresaYNuncaLeResuelvePrecio), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var idEmpresa = await SembrarEmpresaAsync(idTenant, "Empresa 1");
        var (idListaEmpresa, _) = await SembrarListaPrecioDefaultDeEmpresaAsync(
            idTenant, idEmpresa, "Lista De La Empresa");

        var articulo = await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idTenant, articulo, idListaGeneral, 100m);
        // Precio en la lista de LA EMPRESA para el MISMO artículo — si el `Where` perdiera el
        // `IdEmpresa == null`, la grilla (que no tiene idEmpresa) resolvería ESTE precio en vez de
        // devolver null.
        await SembrarPrecioAsync(idTenant, articulo, idListaEmpresa, 999m);
        await QuitarLaListaDefaultAsync(idTenant); // deja la de empresa como ÚNICO EsDefault=true

        try
        {
            var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

            Assert.NotNull(pagina);
            Assert.Null(pagina!.NombreListaPrecio);
            Assert.Null(Assert.Single(pagina.Items).Precio);
        }
        finally
        {
            await RestaurarLaListaDefaultAsync(idListaGeneral);
        }
    }

    // ---- sinProveedor (mutation target: `a.IdProveedorHabitual == null`) ---------------------------

    [Fact]
    public async Task SinProveedorDevuelveSoloArticulosSinProveedorHabitual()
    {
        var (idTenant, idArea, idAlicuotaIva, idCondicionFiscalCf, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(SinProveedorDevuelveSoloArticulosSinProveedorHabitual), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var idProveedor = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Proveedor SA");
        var conProveedor = await SembrarArticuloAsync(idTenant, "ConProveedor", idArea, idAlicuotaIva, idProveedor);
        var sinProveedor = await SembrarArticuloAsync(idTenant, "SinProveedor", idArea, idAlicuotaIva);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("sinProveedor=true&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(sinProveedor, Assert.Single(pagina.Items).Id);
        _ = conProveedor;
    }

    // ---- idProveedor (mutation target: `a.IdProveedorHabitual == idProveedorValor`) ----------------

    [Fact]
    public async Task IdProveedorDevuelveSoloLosArticulosDeEseProveedor()
    {
        var (idTenant, idArea, idAlicuotaIva, idCondicionFiscalCf, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(IdProveedorDevuelveSoloLosArticulosDeEseProveedor), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var proveedorA = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Proveedor A");
        var proveedorB = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Proveedor B");

        for (var i = 0; i < 2; i++)
        {
            await SembrarArticuloAsync(idTenant, $"a-{i}", idArea, idAlicuotaIva, proveedorA);
        }

        for (var i = 0; i < 3; i++)
        {
            await SembrarArticuloAsync(idTenant, $"b-{i}", idArea, idAlicuotaIva, proveedorB);
        }

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla($"idProveedor={proveedorA}&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.All(pagina.Items, i => Assert.Equal(proveedorA, i.IdProveedorHabitual));
    }

    // ---- idProveedor + sinProveedor a la vez: 400 -----------------------------------------------

    [Fact]
    public async Task CombinarIdProveedorConSinProveedorDevuelve400()
    {
        var (_, _, _, idCondicionFiscalCf, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CombinarIdProveedorConSinProveedorDevuelve400), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);
        _ = idCondicionFiscalCf;

        var respuesta = await admin.GetAsync(UrlGrilla("idProveedor=1&sinProveedor=true"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_proveedor_ambiguo", problema.GetProperty("codigo").GetString());
    }

    // ---- proveedor de baja lógica: se trata igual que "sin proveedor" en TODA la grilla -----------

    [Fact]
    public async Task UnProveedorDeBajaLogicaSeTrataComoSinProveedorEnTodaLaGrilla()
    {
        var (idTenant, idArea, idAlicuotaIva, idCondicionFiscalCf, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(
                nameof(UnProveedorDeBajaLogicaSeTrataComoSinProveedorEnTodaLaGrilla), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var idProveedor = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "De Baja SA");
        var articulo = await SembrarArticuloAsync(idTenant, "ConProveedorDeBaja", idArea, idAlicuotaIva, idProveedor);

        // Baja lógica REAL (endpoint DELETE), no un DeletedAt sembrado a mano: prueba el camino
        // que un operador realmente dispara.
        var baja = await admin.DeleteAsync($"/api/proveedores/{idProveedor}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var conSinProveedor = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("sinProveedor=true&tamanio=50"), OpcionesJson);
        Assert.NotNull(conSinProveedor);
        Assert.Equal(1, conSinProveedor!.Total);
        var fila = Assert.Single(conSinProveedor.Items);
        Assert.Equal(articulo, fila.Id);
        Assert.Null(fila.Proveedor);
        Assert.Null(fila.IdProveedorHabitual);

        // El id colgante ya no matchea ningún artículo — sin este guard, `idProveedor=<id de baja>`
        // seguiría encontrando el artículo.
        var conIdProveedorColgante = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla($"idProveedor={idProveedor}&tamanio=50"), OpcionesJson);
        Assert.NotNull(conIdProveedorColgante);
        Assert.Equal(0, conIdProveedorColgante!.Total);
    }

    // ---- activo: true / false / null (mutation target: `a.Activo == activoValor`) ------------------

    [Fact]
    public async Task ActivoFiltraElListadoSegunElEstado()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ActivoFiltraElListadoSegunElEstado), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        for (var i = 0; i < 2; i++)
        {
            await SembrarArticuloAsync(idTenant, $"activo-{i}", idArea, idAlicuotaIva, activo: true);
        }

        for (var i = 0; i < 3; i++)
        {
            await SembrarArticuloAsync(idTenant, $"inactivo-{i}", idArea, idAlicuotaIva, activo: false);
        }

        var soloActivos = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("activo=true&tamanio=50"), OpcionesJson);
        Assert.Equal(2, soloActivos!.Total);
        Assert.All(soloActivos.Items, i => Assert.True(i.Activo));

        var soloInactivos = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("activo=false&tamanio=50"), OpcionesJson);
        Assert.Equal(3, soloInactivos!.Total);
        Assert.All(soloInactivos.Items, i => Assert.False(i.Activo));

        var todos = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);
        Assert.Equal(5, todos!.Total);
    }

    // ---- etiqueta de proveedor: fantasía / razón social / fantasía en blanco -----------------------

    [Fact]
    public async Task LaEtiquetaDeProveedorUsaLaFantasiaOCaeARazonSocialSiEstaVaciaOAusente()
    {
        var (idTenant, idArea, idAlicuotaIva, idCondicionFiscalCf, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(
                nameof(LaEtiquetaDeProveedorUsaLaFantasiaOCaeARazonSocialSiEstaVaciaOAusente), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var conFantasia = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Distribuidora SRL", "Distri");
        var sinFantasia = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Mayorista SA");
        var fantasiaEnBlanco = await SembrarProveedorAsync(idTenant, idCondicionFiscalCf, "Comercial SA", "   ");

        var articuloConFantasia = await SembrarArticuloAsync(idTenant, "art-con-fantasia", idArea, idAlicuotaIva, conFantasia);
        var articuloSinFantasia = await SembrarArticuloAsync(idTenant, "art-sin-fantasia", idArea, idAlicuotaIva, sinFantasia);
        var articuloFantasiaBlanco = await SembrarArticuloAsync(idTenant, "art-fantasia-blanco", idArea, idAlicuotaIva, fantasiaEnBlanco);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        var porId = pagina!.Items.ToDictionary(i => i.Id);
        Assert.Equal("Distri", porId[articuloConFantasia].Proveedor);
        Assert.Equal("Mayorista SA", porId[articuloSinFantasia].Proveedor);
        Assert.Equal("Comercial SA", porId[articuloFantasiaBlanco].Proveedor);
    }

    // ---- integridad de la fila: TODOS los campos posicionales de ArticuloGrillaFila con valores
    // pairwise-distintos (mutation-proof-tests regla 12b) — CodigoInterno/Nombre nunca se
    // afirmaban por valor: un swap entre ambos en Proyectar sobrevivía sin que ningún test lo note.

    [Fact]
    public async Task LaFilaProyectaTodosLosCamposConSusValoresReales()
    {
        var (idTenant, idArea, idAlicuotaIva, idCondicionFiscalCf, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaFilaProyectaTodosLosCamposConSusValoresReales), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var idProveedor = await SembrarProveedorAsync(
            idTenant, idCondicionFiscalCf, "Razon Social Distinta", "Fantasia Distinta");
        var idArticulo = await SembrarArticuloAsync(
            idTenant, "Nombre Distinto Del Codigo", idArea, idAlicuotaIva, idProveedor,
            activo: false, codigoInterno: "COD-DISTINTO-999");
        await SembrarPrecioAsync(idTenant, idArticulo, idListaGeneral, 123.45m);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("activo=false&tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        var fila = Assert.Single(pagina!.Items);
        Assert.Equal(idArticulo, fila.Id);
        Assert.Equal("COD-DISTINTO-999", fila.CodigoInterno);
        Assert.Equal("Nombre Distinto Del Codigo", fila.Nombre);
        Assert.Equal(123.45m, fila.Precio);
        Assert.Equal(idProveedor, fila.IdProveedorHabitual);
        Assert.Equal("Fantasia Distinta", fila.Proveedor);
        Assert.False(fila.Activo);
    }

    // ---- soft-delete: siempre excluido --------------------------------------------------------------

    [Fact]
    public async Task LosArticulosDadosDeBajaQuedanSiempreExcluidos()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LosArticulosDadosDeBajaQuedanSiempreExcluidos), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        var vivo = await SembrarArticuloAsync(idTenant, "Vivo", idArea, idAlicuotaIva);
        await SembrarArticuloAsync(idTenant, "Eliminado", idArea, idAlicuotaIva, eliminado: true);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        Assert.Equal(vivo, Assert.Single(pagina.Items).Id);
    }

    // ---- orden estable: Nombre, luego Id como desempate ----------------------------------------------

    [Fact]
    public async Task ElOrdenEsPorNombreYLuegoPorIdComoDesempate()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElOrdenEsPorNombreYLuegoPorIdComoDesempate), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        // mutation-proof-tests regla 3 (confound de scan order): con pocas filas empatadas, un
        // table scan sin ORDER BY ya devuelve las filas en orden de Id ascendente por pura
        // casualidad de inserción — borrar el `.ThenBy(f => f.Id)`/`, a.Id` del código no se nota
        // (confirmado corriendo la mutación con 3 filas Y con una reubicación física forzada vía
        // UPDATE de una columna indexada: las dos veces, 0 pruebas rotas). Un lote GRANDE de
        // empates fuerza a Postgres a un Sort real (no el camino trivial de pocas filas) — acá SÍ
        // se observa la diferencia: confirmado corriendo la mutación con este seed de 60 filas
        // (la aserción de abajo rompe con la secuencia desordenada que produce el Sort sin
        // desempate explícito).
        var ids = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            ids.Add(await SembrarArticuloAsync(idTenant, "Empate", idArea, idAlicuotaIva));
        }

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=100"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(60, pagina!.Total);
        Assert.Equal(ids, pagina.Items.Select(i => i.Id).ToList());
    }

    // ---- paginación: total sin filtro de precio (COUNT en SQL) -------------------------------------

    [Fact]
    public async Task LaPaginacionSinFiltroDePrecioCuentaYPaginaSobreElTotalFiltrado()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(
                nameof(LaPaginacionSinFiltroDePrecioCuentaYPaginaSobreElTotalFiltrado), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        for (var i = 0; i < 5; i++)
        {
            await SembrarArticuloAsync(idTenant, $"art-{i:00}", idArea, idAlicuotaIva);
        }

        var pagina1 = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=2&pagina=1"), OpcionesJson);
        var pagina2 = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=2&pagina=2"), OpcionesJson);
        var pagina3 = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=2&pagina=3"), OpcionesJson);

        Assert.Equal(5, pagina1!.Total);
        Assert.Equal(5, pagina2!.Total);
        Assert.Equal(5, pagina3!.Total);
        Assert.Equal(2, pagina1.Items.Count);
        Assert.Equal(2, pagina2.Items.Count);
        Assert.Single(pagina3.Items);

        var idsTotales = pagina1.Items.Concat(pagina2.Items).Concat(pagina3.Items).Select(i => i.Id).ToList();
        Assert.Equal(5, idsTotales.Distinct().Count());
    }

    // ---- paginación: total CON filtro de precio (COUNT en memoria, sobre el conjunto post-filtro) ---

    [Fact]
    public async Task LaPaginacionConFiltroDePrecioCuentaElConjuntoPostFiltro()
    {
        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(
                nameof(LaPaginacionConFiltroDePrecioCuentaElConjuntoPostFiltro), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        // 3 dentro del rango [100, 200], 2 afuera — el Total tiene que ser 3 (el conjunto YA
        // filtrado por precio), no 5 (el conteo SQL crudo de candidatos).
        var precios = new[] { 50m, 100m, 150m, 200m, 250m };
        foreach (var (precio, i) in precios.Select((p, i) => (p, i)))
        {
            var id = await SembrarArticuloAsync(idTenant, $"precio-{i}", idArea, idAlicuotaIva);
            await SembrarPrecioAsync(idTenant, id, idListaGeneral, precio);
        }

        var pagina1 = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioDesde=100&precioHasta=200&tamanio=2&pagina=1"), OpcionesJson);
        var pagina2 = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("precioDesde=100&precioHasta=200&tamanio=2&pagina=2"), OpcionesJson);

        Assert.Equal(3, pagina1!.Total);
        Assert.Equal(3, pagina2!.Total);
        Assert.Equal(2, pagina1.Items.Count);
        Assert.Single(pagina2.Items);
        Assert.All(pagina1.Items.Concat(pagina2.Items), i => Assert.InRange(i.Precio!.Value, 100m, 200m));
    }

    // ---- tope del filtro de precio: rechaza, no trunca ------------------------------------------------

    [Fact]
    public async Task ElFiltroDePrecioQueExcedeElTopeDeCandidatosSeRechazaConUn400()
    {
        using var factoryConTopeBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeGrillaDeArticulos>(o => o.TopeDeCandidatosPorFiltroDePrecio = 3)));

        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(ElFiltroDePrecioQueExcedeElTopeDeCandidatosSeRechazaConUn400), fixture, factoryConTopeBajo);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, factoryConTopeBajo);

        // tope=3, 5 candidatos (sin precio siquiera — el tope se aplica sobre el conteo de
        // candidatos ANTES de resolver precio, así que ninguno necesita precio real).
        for (var i = 0; i < 5; i++)
        {
            await SembrarArticuloAsync(idTenant, $"candidato-{i}", idArea, idAlicuotaIva);
        }

        var respuesta = await admin.GetAsync(UrlGrilla("precioDesde=0&tamanio=50"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filtro_precio_excede_tope", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElFiltroDePrecioDentroDelTopeSeResuelveSinRechazo()
    {
        using var factoryConTopeBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeGrillaDeArticulos>(o => o.TopeDeCandidatosPorFiltroDePrecio = 3)));

        var (idTenant, idArea, idAlicuotaIva, _, idListaGeneral, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(ElFiltroDePrecioDentroDelTopeSeResuelveSinRechazo), fixture, factoryConTopeBajo);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, factoryConTopeBajo);

        for (var i = 0; i < 3; i++)
        {
            var id = await SembrarArticuloAsync(idTenant, $"candidato-{i}", idArea, idAlicuotaIva);
            await SembrarPrecioAsync(idTenant, id, idListaGeneral, 100m);
        }

        var respuesta = await admin.GetAsync(UrlGrilla("precioDesde=0&tamanio=50"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var pagina = await respuesta.Content.ReadFromJsonAsync<PaginaDeArticulosGrilla>(OpcionesJson);
        Assert.Equal(3, pagina!.Total);
        // Camino CON filtro de precio: `NombreListaPrecio` también viaja acá, no solo en el
        // camino sin filtro.
        Assert.Equal("General", pagina.NombreListaPrecio);
    }

    // ---- tamanio/pagina: clamp (mutation target: `Math.Clamp`/`Math.Max` en ListarAsync) -----------

    [Fact]
    public async Task TamanioPorEncimaDeDoscientosSeTopeaEnDoscientos()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(TamanioPorEncimaDeDoscientosSeTopeaEnDoscientos), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);

        var pagina = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=500"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(200, pagina!.Tamanio);
    }

    [Fact]
    public async Task PaginaCeroSeTrataComoLaPrimera()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(PaginaCeroSeTrataComoLaPrimera), fixture, fixture);
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin, fixture);

        await SembrarArticuloAsync(idTenant, "Articulo", idArea, idAlicuotaIva);

        var paginaConCero = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("tamanio=50&pagina=0"), OpcionesJson);
        var paginaConUno = await admin.GetFromJsonAsync<PaginaDeArticulosGrilla>(
            UrlGrilla("tamanio=50&pagina=1"), OpcionesJson);

        Assert.NotNull(paginaConCero);
        Assert.Equal(1, paginaConCero!.Pagina);
        Assert.Equal(
            paginaConUno!.Items.Select(i => i.Id).ToList(), paginaConCero.Items.Select(i => i.Id).ToList());
    }

    // ---- aislamiento de tenant: los artículos/proveedores de otro tenant nunca aparecen -------------

    [Fact]
    public async Task LosArticulosYProveedoresDeOtroTenantNuncaAparecen()
    {
        var (idTenantA, idAreaA, idAlicuotaIvaA, idCondicionFiscalA, _, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(LosArticulosYProveedoresDeOtroTenantNuncaAparecen) + "-A", fixture, fixture);
        var (idTenantB, idAreaB, idAlicuotaIvaB, idCondicionFiscalB, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(LosArticulosYProveedoresDeOtroTenantNuncaAparecen) + "-B", fixture, fixture);

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA, fixture);

        var proveedorA = await SembrarProveedorAsync(idTenantA, idCondicionFiscalA, "Proveedor-A-Exclusivo");
        var articuloA = await SembrarArticuloAsync(idTenantA, "Articulo-A-Exclusivo", idAreaA, idAlicuotaIvaA, proveedorA);

        var proveedorB = await SembrarProveedorAsync(idTenantB, idCondicionFiscalB, "Proveedor-B-Exclusivo");
        await SembrarArticuloAsync(idTenantB, "Articulo-B-Exclusivo", idAreaB, idAlicuotaIvaB, proveedorB);
        _ = mailAdminB;
        _ = passwordAdminB;

        var pagina = await adminA.GetFromJsonAsync<PaginaDeArticulosGrilla>(UrlGrilla("tamanio=50"), OpcionesJson);

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.Total);
        var fila = Assert.Single(pagina.Items);
        Assert.Equal(articuloA, fila.Id);
        Assert.Equal("Proveedor-A-Exclusivo", fila.Proveedor);
    }
}
