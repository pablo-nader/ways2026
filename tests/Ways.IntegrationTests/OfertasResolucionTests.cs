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
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);

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

    private async Task<int> SembrarArticuloAsync(
        int idTenant, string nombre, int? idCategoria = null, int? idGrupo = null, int? idFamilia = null)
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
            IdFamilia = idFamilia,
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

    private async Task<int> SembrarGrupoAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var grupo = new Grupo { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Grupos.Add(grupo);
        await db.SaveChangesAsync();

        return grupo.Id;
    }

    private async Task<int> SembrarFamiliaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var familia = new Familia { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    /// <summary>Cambia la pertenencia del artículo directo en la base, sin pasar por ningún servicio: lo que se
    /// prueba es que la resolución lee la familia VIGENTE del artículo y no una copia.</summary>
    private async Task CambiarFamiliaDelArticuloAsync(int idArticulo, int? idFamilia)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = new Npgsql.NpgsqlCommand(
            "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", cruda);
        comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = (object?)idFamilia ?? DBNull.Value });
        comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = idArticulo });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    private static AltaOferta OfertaDeGrupo(int idGrupo, decimal porcentaje) => new(
        Nombre: "oferta de grupo", IdEmpresa: null, IdArticulo: null, IdGrupo: idGrupo, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: 0, Acumulable: false);

    private static AltaOferta OfertaDeFamilia(int idFamilia, decimal porcentaje, int prioridad = 0) => new(
        Nombre: "oferta de familia", IdEmpresa: null, IdArticulo: null, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: prioridad, Acumulable: false, IdFamilia: idFamilia);

    /// <summary>El precio final de cada artículo (una unidad, sin empresa) en un solo pedido de resolución.</summary>
    private static async Task<Dictionary<int, decimal?>> PreciosFinalesAsync(
        HttpClient cliente, int idLista, params int[] idsArticulo)
    {
        var solicitud = new SolicitudDeResolucion(
            [.. idsArticulo.Select(id => new LineaDeResolucion(id, null, idLista, 1m))]);
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas/resolver", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();

        return resultado!.ToDictionary(r => r.IdArticulo, r => r.PrecioFinal);
    }

    /// <summary>La oferta de grupo llega a los miembros del grupo y a nadie más. No existía una prueba contra la base
    /// del alcance por grupo: el filtro grueso de candidatas (<c>ConstruirContextoAsync</c>) es SQL que ninguna
    /// prueba del dominio ejercita.</summary>
    [Fact]
    public async Task ResolverAplicaLaOfertaDeGrupoSoloALosMiembrosDelGrupo()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverAplicaLaOfertaDeGrupoSoloALosMiembrosDelGrupo));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idGrupo = await SembrarGrupoAsync(idTenant, "Gaseosas");
        var idOtroGrupo = await SembrarGrupoAsync(idTenant, "Lácteos");
        var miembro = await SembrarArticuloAsync(idTenant, "cola", idGrupo: idGrupo);
        var deOtroGrupo = await SembrarArticuloAsync(idTenant, "leche", idGrupo: idOtroGrupo);
        var sinGrupo = await SembrarArticuloAsync(idTenant, "suelto");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        foreach (var id in new[] { miembro, deOtroGrupo, sinGrupo })
        {
            await SembrarPrecioAsync(idTenant, id, idLista, 100m);
        }

        await CrearOfertaAsync(admin, OfertaDeGrupo(idGrupo, porcentaje: 20m));

        var precios = await PreciosFinalesAsync(admin, idLista, miembro, deOtroGrupo, sinGrupo);

        Assert.Equal(80m, precios[miembro]);
        Assert.Equal(100m, precios[deOtroGrupo]);
        Assert.Equal(100m, precios[sinGrupo]);
    }

    [Fact]
    public async Task ResolverAplicaLaOfertaDeFamiliaSoloALosMiembrosDeLaFamilia()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ResolverAplicaLaOfertaDeFamiliaSoloALosMiembrosDeLaFamilia));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idFamilia = await SembrarFamiliaAsync(idTenant, "Remeras");
        var idOtraFamilia = await SembrarFamiliaAsync(idTenant, "Pantalones");
        var miembroUno = await SembrarArticuloAsync(idTenant, "remera-roja", idFamilia: idFamilia);
        var miembroDos = await SembrarArticuloAsync(idTenant, "remera-azul", idFamilia: idFamilia);
        var deOtraFamilia = await SembrarArticuloAsync(idTenant, "pantalon", idFamilia: idOtraFamilia);
        var sinFamilia = await SembrarArticuloAsync(idTenant, "suelto");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        foreach (var id in new[] { miembroUno, miembroDos, deOtraFamilia, sinFamilia })
        {
            await SembrarPrecioAsync(idTenant, id, idLista, 100m);
        }

        await CrearOfertaAsync(admin, OfertaDeFamilia(idFamilia, porcentaje: 20m));

        var precios = await PreciosFinalesAsync(admin, idLista, miembroUno, miembroDos, deOtraFamilia, sinFamilia);

        Assert.Equal(80m, precios[miembroUno]);
        Assert.Equal(80m, precios[miembroDos]);
        Assert.Equal(100m, precios[deOtraFamilia]);
        Assert.Equal(100m, precios[sinFamilia]);
    }

    /// <summary>La pertenencia se lee al resolver: el artículo que entra a la familia recibe la oferta, el que sale
    /// la pierde y el que pasa a otra familia deja de recibirla.</summary>
    [Fact]
    public async Task LaOfertaDeFamiliaSigueALosArticulosQueSeUnenYSalen()
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaOfertaDeFamiliaSigueALosArticulosQueSeUnenYSalen));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idFamilia = await SembrarFamiliaAsync(idTenant, "Remeras");
        var idOtraFamilia = await SembrarFamiliaAsync(idTenant, "Pantalones");
        var articulo = await SembrarArticuloAsync(idTenant, "viajero");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, articulo, idLista, 100m);

        await CrearOfertaAsync(admin, OfertaDeFamilia(idFamilia, porcentaje: 20m));

        Assert.Equal(100m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);

        await CambiarFamiliaDelArticuloAsync(articulo, idFamilia);
        Assert.Equal(80m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);

        await CambiarFamiliaDelArticuloAsync(articulo, idOtraFamilia);
        Assert.Equal(100m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);

        await CambiarFamiliaDelArticuloAsync(articulo, idFamilia);
        await CambiarFamiliaDelArticuloAsync(articulo, null);
        Assert.Equal(100m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);
    }

    /// <summary>Sin precedencia por tipo de alcance: la oferta de familia de mayor prioridad le gana a la de grupo
    /// sobre el mismo artículo, y la de grupo de mayor prioridad le gana a la de familia.</summary>
    [Fact]
    public async Task UnaOfertaDeFamiliaYUnaDeGrupoSobreElMismoArticuloCompitenSoloPorPrioridad()
    {
        var (idTenant, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(UnaOfertaDeFamiliaYUnaDeGrupoSobreElMismoArticuloCompitenSoloPorPrioridad));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idFamilia = await SembrarFamiliaAsync(idTenant, "Remeras");
        var idGrupo = await SembrarGrupoAsync(idTenant, "Verano");
        var articulo = await SembrarArticuloAsync(idTenant, "remera", idGrupo: idGrupo, idFamilia: idFamilia);
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, articulo, idLista, 100m);

        var deGrupo = await CrearOfertaAsync(admin, OfertaDeGrupo(idGrupo, porcentaje: 10m) with { Prioridad = 5 });
        await CrearOfertaAsync(admin, OfertaDeFamilia(idFamilia, porcentaje: 30m, prioridad: 9));
        Assert.Equal(70m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);

        var subida = new EdicionOferta(
            Nombre: deGrupo.Nombre, IdEmpresa: null, IdArticulo: null, IdGrupo: deGrupo.IdGrupo, IdCategoria: null,
            FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
            CantidadMinima: null, PrecioUnitario: null, Porcentaje: 10m, ImporteFijo: null,
            Prioridad: 20, Acumulable: false, IdsListas: null, Activo: true, DisponibleOnline: true);
        var respuesta = await admin.PutAsJsonAsync($"/api/ofertas/{deGrupo.Id}", subida);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        Assert.Equal(90m, (await PreciosFinalesAsync(admin, idLista, articulo))[articulo]);
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

    private static AltaOferta OfertaDeFamiliaConMinimo(int idFamilia, decimal cantidadMinima, decimal precioUnitario) => new(
        Nombre: "2 x 3500", IdEmpresa: null, IdArticulo: null, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: cantidadMinima, PrecioUnitario: precioUnitario, Porcentaje: null, ImporteFijo: null,
        Prioridad: 0, Acumulable: false, IdFamilia: idFamilia);

    /// <summary>Cuerpo crudo: <paramref name="agregacion"/> <c>null</c> omite la clave, como el POS ya
    /// instalado.</summary>
    private static Task<HttpResponseMessage> ResolverConAgregacionAsync(
        HttpClient cliente, int idLista, object? agregacion, params int[] idsArticulo)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["lineas"] = idsArticulo.Select(id => new { idArticulo = id, idEmpresa = (int?)null, idListaPrecio = idLista, cantidad = 1 }).ToList()
        };
        if (agregacion is not null)
        {
            cuerpo["agregacion"] = agregacion;
        }

        return cliente.PostAsJsonAsync("/api/ofertas/resolver", cuerpo);
    }

    /// <summary>Cláusula: el despacho por <see cref="AgregacionDeResolucion"/> de
    /// <c>ServicioDeOfertas.ResolverSolicitudAsync</c>. Dos artículos de una familia con oferta
    /// "2 x 3500" a cantidad 1: como documento (y sin el campo, el cuerpo de los POS instalados) suman
    /// 2 y ambos descuentan; por línea cada uno es una unidad suelta a precio de lista.</summary>
    [Theory]
    [InlineData(null, 1750)]
    [InlineData("Documento", 1750)]
    [InlineData("PorLinea", 2000)]
    public async Task ResolverRespetaLaAgregacionPedidaYSinCampoAgregaComoDocumento(string? agregacion, int precioEsperado)
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync($"Agregacion{agregacion ?? "Omitida"}");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idFamilia = await SembrarFamiliaAsync(idTenant, "Alfajores");
        var a = await SembrarArticuloAsync(idTenant, "alfajor-a", idFamilia: idFamilia);
        var b = await SembrarArticuloAsync(idTenant, "alfajor-b", idFamilia: idFamilia);
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        foreach (var id in new[] { a, b })
        {
            await SembrarPrecioAsync(idTenant, id, idLista, 2000m);
        }

        await CrearOfertaAsync(admin, OfertaDeFamiliaConMinimo(idFamilia, 2m, 1750m));

        var respuesta = await ResolverConAgregacionAsync(admin, idLista, agregacion, a, b);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<List<ResultadoDeResolucion>>();
        Assert.Equal([precioEsperado, precioEsperado], resultado!.Select(r => (int)r.PrecioFinal!.Value));
    }

    /// <summary>Un nombre que no es de ninguna agregación lo rechaza el binding JSON (400
    /// <c>cuerpo_invalido</c>, como cualquier otro enum de la API); un entero fuera del enum, que el
    /// binding sí acepta, lo rechaza el servicio con <c>agregacion_invalida</c>.</summary>
    [Theory]
    [InlineData("Todas", "cuerpo_invalido")]
    [InlineData(7, "agregacion_invalida")]
    public async Task ResolverRechazaUnaAgregacionInvalida(object agregacion, string codigoEsperado)
    {
        var (idTenant, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync($"AgregacionInvalida{agregacion}");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var idArticulo = await SembrarArticuloAsync(idTenant, "cualquiera");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        await SembrarPrecioAsync(idTenant, idArticulo, idLista, 100m);

        var respuesta = await ResolverConAgregacionAsync(admin, idLista, agregacion, idArticulo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
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

    /// <summary>La curva de escalones de la instantánea offline sale de las MISMAS candidatas ya
    /// materializadas (su <c>cantidad_minima</c> ya viaja en la <c>OfertaCandidata</c>): la
    /// resolución por umbral es pura, en memoria. Este guard exige el MISMO presupuesto de consultas
    /// que <see cref="ServicioDeOfertas.ResolverAsync"/> — una oferta por volumen de categoría que
    /// alcanza a los 20 artículos, o sea 40 líneas (con la lista derivada) con curva, sin una sola
    /// consulta extra. El <c>Assert.Contains</c> de
    /// <see cref="ContarConsultasDeResolucionAsync"/> es lo que impide que este guard pase sobre una
    /// curva que nunca se calculó.</summary>
    [Fact]
    public async Task ResolverConEscalonesEmiteLasMismasConsultasQueResolver()
    {
        var (idTenant, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(
            nameof(ResolverConEscalonesEmiteLasMismasConsultasQueResolver));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idCategoria = await SembrarCategoriaAsync(idTenant, "Categoria");
        var idLista = await SembrarListaAsync(idTenant, "Lista de Prueba");
        var idListaDerivada = await SembrarListaDerivadaAsync(idTenant, "Derivada", idLista, -10m);

        var oferta = OfertaDeCategoria(idCategoria, porcentaje: 10m);
        await CrearOfertaAsync(admin, oferta with { CantidadMinima = 6m, Nombre = "Por volumen" });

        var consultasDeResolver = await ContarConsultasDeResolucionAsync(
            idTenant, cantidadDeArticulos: 20, idCategoria, idLista, idListaDerivada, conEscalones: false);
        var consultasDeResolverConEscalones = await ContarConsultasDeResolucionAsync(
            idTenant, cantidadDeArticulos: 20, idCategoria, idLista, idListaDerivada, conEscalones: true);

        Assert.Equal(consultasDeResolver, consultasDeResolverConEscalones);
    }

    private async Task<int> ContarConsultasDeResolucionAsync(
        int idTenant, int cantidadDeArticulos, int idCategoria, int idLista, int idListaDerivada,
        bool conEscalones = false)
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
                npgsql.MapEnum<Ways.Domain.Fiscal.ResultadoFiscal>("resultado_fiscal");
                npgsql.MapEnum<Ways.Domain.Fiscal.AmbienteFiscal>("ambiente_fiscal");
                npgsql.MapEnum<ModoPuntoVenta>("modo_punto_venta");
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

        if (conEscalones)
        {
            var conCurva = await servicioDeOfertas.ResolverConEscalonesAsync(lineas, momento: null);

            // La curva tiene que haberse calculado de verdad: un contador igual sobre un resultado
            // vacío no probaría nada (mismo criterio que asserts sobre valores discriminantes).
            Assert.Contains(conCurva, r => r.Escalones.Count > 0);
        }
        else
        {
            await servicioDeOfertas.ResolverDocumentoAsync(lineas, momento: null);
        }

        return contador.Consultas;
    }
}
