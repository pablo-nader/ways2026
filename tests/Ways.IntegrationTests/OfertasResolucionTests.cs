using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-4-ofertas, Slice 3 (tasks 3.11/3.12) — <c>POST /api/ofertas/resolver</c> punta a punta
/// contra Postgres real: escenario base + acumulable del spec, passthrough sin match, alcance
/// jerárquico de categoría, exclusión por empresa, base derivada, la aserción de "no escribe
/// nada" (spec: resolucion-de-ofertas / Applied Ofertas Are Reported, Never Persisted), y el
/// guard de cantidad constante de consultas (design: Testing Strategy — "Integration (batch)").
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasResolucionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>Cuenta cada <c>SELECT</c> que EF Core emite a través de su propio pipeline de
    /// comandos — el guard de la task 3.11 (design: "constant query count, independent of N").
    /// El <c>set_config</c> de <see cref="InterceptorDeContextoDeTenant"/> corre por FUERA de
    /// este pipeline (ADO.NET crudo sobre la conexión), así que nunca se cuenta acá.</summary>
    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(int IdTenant, string MailAdmin, string PasswordAdmin)> AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        return (resultado!.IdTenant, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarArticuloAsync(int idTenant, string nombre, int? idCategoria = null, int? idGrupo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdCategoria = idCategoria,
            IdGrupo = idGrupo,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarCategoriaAsync(int idTenant, string nombre, int? idCategoriaPadre = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var categoria = new Categoria
        {
            IdTenant = idTenant, Nombre = nombre, Orden = 1, IdCategoriaPadre = idCategoriaPadre,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        return categoria.Id;
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

    private async Task<int> SembrarListaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    private async Task<int> SembrarListaDerivadaAsync(int idTenant, string nombre, int idListaBase, decimal porcentaje)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Derivada,
            IdListaBase = idListaBase, Porcentaje = porcentaje, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    private async Task SembrarPrecioAsync(int idTenant, int idArticulo, int idListaPrecio, decimal monto)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant, IdArticulo = idArticulo, IdListaPrecio = idListaPrecio, Monto = monto,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static AltaOferta OfertaDeArticulo(
        int idArticulo, decimal? porcentaje = null, decimal? importeFijo = null, decimal? precioUnitario = null,
        int prioridad = 0, bool acumulable = false) => new(
        Nombre: "oferta de prueba", IdEmpresa: null, IdArticulo: idArticulo, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: precioUnitario, Porcentaje: porcentaje, ImporteFijo: importeFijo,
        Prioridad: prioridad, Acumulable: acumulable);

    private static AltaOferta OfertaDeCategoria(int idCategoria, decimal porcentaje, int prioridad = 0) => new(
        Nombre: "oferta de categoría", IdEmpresa: null, IdArticulo: null, IdGrupo: null, IdCategoria: idCategoria,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: prioridad, Acumulable: false);

    private static async Task<OfertaListado> CrearOfertaAsync(HttpClient cliente, AltaOferta datos)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas", datos);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<OfertaListado>())!;
    }

    // ---- task 3.12: escenario end-to-end (spec: Base plus one acumulable) ---------------------

    [Fact]
    public async Task ResolverAplicaBaseYAcumulableSobreDatosReales()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverAplicaBaseYAcumulableSobreDatosReales));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-base-acc");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 1000m);

        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 20m, prioridad: 10));
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 10m, acumulable: true));

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, idLista, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        var linea = Assert.Single(resultado!);

        Assert.Equal(1000m, linea.PrecioOriginal);
        Assert.Equal(700m, linea.PrecioFinal);
        Assert.Equal(300m, linea.DescuentoUnitario);
        Assert.Equal(2, linea.Aplicadas.Count);
    }

    /// <summary>Spec: "No matching oferta leaves the price unchanged".</summary>
    [Fact]
    public async Task ResolverSinOfertaQueMatcheeDevuelveElPrecioOriginalSinCambios()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverSinOfertaQueMatcheeDevuelveElPrecioOriginalSinCambios));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-sin-oferta");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 500m);

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, idLista, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        var linea = Assert.Single(resultado!);

        Assert.Equal(500m, linea.PrecioOriginal);
        Assert.Equal(500m, linea.PrecioFinal);
        Assert.Empty(linea.Aplicadas);
    }

    /// <summary>Spec: "Categoria-scoped oferta reaches subcategoria articulos" — Bebidas (padre)
    /// → Gaseosas (hija), artículo scoped a Gaseosas.</summary>
    [Fact]
    public async Task ResolverConCategoriaDescendienteAplicaLaOfertaDeLaCategoriaAncestro()
    {
        var (idTenant, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(ResolverConCategoriaDescendienteAplicaLaOfertaDeLaCategoriaAncestro));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idBebidas = await SembrarCategoriaAsync(idTenant, "Bebidas");
        var idGaseosas = await SembrarCategoriaAsync(idTenant, "Gaseosas", idBebidas);
        var idArticulo = await SembrarArticuloAsync(idTenant, "cola", idCategoria: idGaseosas);
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 200m);

        await CrearOfertaAsync(admin, OfertaDeCategoria(idBebidas, porcentaje: 15m));

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, idLista, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        var linea = Assert.Single(resultado!);

        Assert.Equal(30m, linea.DescuentoUnitario);
        Assert.Equal(170m, linea.PrecioFinal);
    }

    /// <summary>Spec: "Empresa-scoped oferta excludes other empresas".</summary>
    [Fact]
    public async Task ResolverExcluyeOfertasDeOtraEmpresa()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverExcluyeOfertasDeOtraEmpresa));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idEmpresaA = await SembrarEmpresaAsync(idTenant, "Empresa A");
        var idEmpresaB = await SembrarEmpresaAsync(idTenant, "Empresa B");

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-empresa");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 100m);

        var ofertaDeEmpresaA = OfertaDeArticulo(idArticulo, porcentaje: 50m) with { IdEmpresa = idEmpresaA };
        await CrearOfertaAsync(admin, ofertaDeEmpresaA);

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, idEmpresaB, idLista, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        var linea = Assert.Single(resultado!);

        Assert.Equal(100m, linea.PrecioFinal);
        Assert.Empty(linea.Aplicadas);
    }

    /// <summary>Spec: "Derivada lista price is the original base" — 200 base, -10% derivada =
    /// 180 (design decision 5, batch price path); -10% acumulable sobre 180 = 18, final 162.</summary>
    [Fact]
    public async Task ResolverConListaDerivadaUsaElPrecioDerivadoComoOriginal()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverConListaDerivadaUsaElPrecioDerivadoComoOriginal));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-derivada");
        var idListaBase = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idListaBase, 200m);
        var idListaDerivada = await SembrarListaDerivadaAsync(idTenant, "Derivada", idListaBase, -10m);

        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 10m, acumulable: true));

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, idListaDerivada, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        var linea = Assert.Single(resultado!);

        Assert.Equal(180m, linea.PrecioOriginal);
        Assert.Equal(18m, linea.DescuentoUnitario);
        Assert.Equal(162m, linea.PrecioFinal);
    }

    /// <summary>Spec: "Resolution performs no writes" — cuenta de filas de las cuatro tablas
    /// involucradas, antes y después, tiene que coincidir.</summary>
    [Fact]
    public async Task ResolverNoEscribeEnNingunaTabla()
    {
        var (idTenant, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(ResolverNoEscribeEnNingunaTabla));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-sin-escritura");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 300m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 10m));

        var filasAntes = await ContarFilasAsync(idTenant);

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, idLista, 5m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var filasDespues = await ContarFilasAsync(idTenant);

        Assert.Equal(filasAntes, filasDespues);
    }

    private async Task<(int Ofertas, int OfertasListas, int Precios, int Articulos)> ContarFilasAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

        return (
            await db.Ofertas.IgnoreQueryFilters(["BajaLogica"]).CountAsync(),
            await db.OfertasListas.CountAsync(),
            await db.Precios.IgnoreQueryFilters(["BajaLogica"]).CountAsync(),
            await db.Articulos.IgnoreQueryFilters(["BajaLogica"]).CountAsync());
    }

    // ---- judgment-day: caminos de error/borde sin cobertura previa -----------------------------

    /// <summary>db-error-backstops: <c>idArticulo</c> inexistente en el lote — el pre-chequeo de
    /// <see cref="ServicioDeOfertas.ResolverAsync"/> lo atrapa antes de tocar ninguna otra tabla,
    /// mismo código que el resto del ABM de ofertas.</summary>
    [Fact]
    public async Task ResolverConIdArticuloInexistenteDevuelve400ReferenciaInvalida()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverConIdArticuloInexistenteDevuelve400ReferenciaInvalida));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(999_999, null, idLista, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>db-error-backstops: <c>idListaPrecio</c> inexistente — lo atrapa
    /// <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/> (el lote de precios que
    /// <c>ResolverAsync</c> delega, design decision 5), mismo código 400 observable desde el
    /// endpoint.</summary>
    [Fact]
    public async Task ResolverConIdListaPrecioInexistenteDevuelve400ReferenciaInvalida()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverConIdListaPrecioInexistenteDevuelve400ReferenciaInvalida));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo-lista-inexistente");

        var solicitud = new SolicitudDeResolucion([new LineaDeResolucion(idArticulo, null, 999_999, 1m)]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>(judgment-day, item 1) <c>lineas</c> vacío es un lote válido y trivial —
    /// <c>ResolverAsync</c> devuelve el resultado vacío sin emitir ninguna consulta (early
    /// return antes de la primera query de artículos).</summary>
    [Fact]
    public async Task ResolverConLineasVaciasDevuelve200ConResultadoVacio()
    {
        var (_, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverConLineasVaciasDevuelve200ConResultadoVacio));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var solicitud = new SolicitudDeResolucion([]);
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas/resolver", solicitud);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        Assert.Empty(resultado!);
    }

    /// <summary>(judgment-day, item 1 — revisado) <c>{"lineas": null}</c> crudo bindea
    /// <c>null</c> más allá de <c>required</c> (STJ no valida miembros <c>required</c> en
    /// constructores <c>SetsRequiredMembers</c>) — <c>ResolverAsync</c> lo distingue de un lote
    /// vacío legítimo y devuelve 400 <c>lineas_requeridas</c>, nunca un 200 silencioso.</summary>
    [Fact]
    public async Task ResolverConLineasNulasEnJsonCrudoDevuelve400LineasRequeridas()
    {
        var (_, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverConLineasNulasEnJsonCrudoDevuelve400LineasRequeridas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        using var contenido = new StringContent("{\"lineas\": null}", Encoding.UTF8, "application/json");
        var respuesta = await admin.PostAsync("/api/ofertas/resolver", contenido);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lineas_requeridas", problema.GetProperty("codigo").GetString());
    }

    /// <summary>(judgment-day, item 1 — revisado) La clave <c>"lineas"</c> ausente del body es el
    /// mismo caso que <c>null</c> desde el punto de vista de STJ (no hay validación de
    /// <c>required</c> con <c>SetsRequiredMembers</c>): mismo 400 <c>lineas_requeridas</c>.</summary>
    [Fact]
    public async Task ResolverSinLaClaveLineasDevuelve400LineasRequeridas()
    {
        var (_, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverSinLaClaveLineasDevuelve400LineasRequeridas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        using var contenido = new StringContent("{}", Encoding.UTF8, "application/json");
        var respuesta = await admin.PostAsync("/api/ofertas/resolver", contenido);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lineas_requeridas", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.11: guard de cantidad constante de consultas -----------------------------------

    /// <summary>Design: Technical Approach — "7 constant queries per resolution call, independent
    /// of N articles × M listas". Corre la misma resolución con pocos y con muchos artículos (2 y
    /// 20) y exige la MISMA cantidad de consultas — el guard contra reintroducir un N+1
    /// silencioso.</summary>
    [Fact]
    public async Task ResolverEmiteUnaCantidadConstanteDeConsultasIndependienteDeN()
    {
        var (idTenant, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(ResolverEmiteUnaCantidadConstanteDeConsultasIndependienteDeN));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idCategoria = await SembrarCategoriaAsync(idTenant, "Categoria");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        var idListaDerivada = await SembrarListaDerivadaAsync(idTenant, "Derivada", idLista, -10m);

        await CrearOfertaAsync(admin, OfertaDeCategoria(idCategoria, porcentaje: 10m));

        var consultasConPocosArticulos =
            await ContarConsultasDeResolucionAsync(idTenant, cantidadDeArticulos: 2, idCategoria, idLista, idListaDerivada);
        var consultasConMuchosArticulos =
            await ContarConsultasDeResolucionAsync(idTenant, cantidadDeArticulos: 20, idCategoria, idLista, idListaDerivada);

        Assert.Equal(consultasConPocosArticulos, consultasConMuchosArticulos);
        Assert.True(
            consultasConPocosArticulos <= 7,
            $"Se esperaban a lo sumo 7 consultas (design: Technical Approach), se emitieron {consultasConPocosArticulos}.");
    }

    private async Task<int> ContarConsultasDeResolucionAsync(
        int idTenant, int cantidadDeArticulos, int idCategoria, int idLista, int idListaDerivada)
    {
        var idsArticulo = new List<int>();
        for (var i = 0; i < cantidadDeArticulos; i++)
        {
            var idArticulo = await SembrarArticuloAsync(idTenant, $"art-{Guid.NewGuid():N}", idCategoria: idCategoria);
            await SembrarPrecioAsync(idTenant, idArticulo, idLista, 100m);
            idsArticulo.Add(idArticulo);
        }

        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, idTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(idTenant);
        var servicioDePrecios = new ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);

        var lineas = idsArticulo
            .SelectMany(idArticulo => new[] { idLista, idListaDerivada }
                .Select(idListaPrecio => new LineaDeResolucion(idArticulo, null, idListaPrecio, 1m)))
            .ToList();

        await servicioDeOfertas.ResolverAsync(lineas, momento: null);

        return contador.Consultas;
    }
}
