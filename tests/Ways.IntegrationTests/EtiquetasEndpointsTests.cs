using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Etiquetas;
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
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-18-etiquetas-y-consulta, Slice 2 (tasks 2.25-2.38) — <c>ServicioDeEtiquetas</c>/
/// <c>POST /api/etiquetas/datos</c> punta a punta contra Postgres real: la guarda XOR, el tope de
/// 200, <c>cantidad=1</c>/<c>IdEmpresa</c>, la exclusión sin precio, la divergencia de
/// <c>soloConOfertaVigente</c> contra el resolver real, <c>truncado</c> acoplado al clamp,
/// <c>NombreDeLista</c> fuente-de-verdad, el momento único de la hoja, el read-back pairwise, la
/// cláusula de exposición y el presupuesto de comandos.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EtiquetasEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";
    private const string PasswordSupervisor = "una-contraseña-larga";
    private const string PasswordAdmin2 = "una-contraseña-larga";

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>judgment-day Slice 2, ronda 1 (juez B, MAJOR 1): a diferencia de <see cref="RelojFijo"/>,
    /// devuelve un valor DISTINTO en cada lectura de <see cref="Ahora"/> (arranca en
    /// <paramref name="inicio"/> y suma 1 segundo por get) — así un test puede distinguir "el
    /// momento se resolvió UNA vez y se reusó" de "se leyó el reloj más de una vez" (design decisión
    /// 10, mutation target 25 REAL bajo este reloj: con <see cref="RelojFijo"/> ambos escenarios son
    /// indistinguibles porque el reloj fijo siempre devuelve lo mismo).</summary>
    private sealed class RelojQueAvanza(DateTimeOffset inicio) : IRelojDelSistema
    {
        private DateTimeOffset _proximaLectura = inicio;

        public int Lecturas { get; private set; }

        public DateTimeOffset Ahora
        {
            get
            {
                Lecturas++;
                var valor = _proximaLectura;
                _proximaLectura = _proximaLectura.AddSeconds(1);
                return valor;
            }
        }
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>Mismo patrón que <c>OfertasResolucionTests.ContadorDeComandos</c> (task 2.38,
    /// design.md:253-257) — cuenta cada <c>SELECT</c> emitido a través del pipeline de EF Core.</summary>
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

    private async Task<(int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdAlicuotaIva, int IdListaGeneral, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        // La aprovisionamiento (ServicioDeAprovisionamiento) ya siembra la lista "General",
        // EsDefault=true — la reusamos en vez de crear una segunda.
        var idListaGeneral = await db.ListasPrecio.Where(l => l.IdTenant == resultado.IdTenant).Select(l => l.Id).FirstAsync();

        return (resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, idListaGeneral, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarVendedorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant, NombreUsuario = "vendedor", Mail = mail, RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    private async Task<string> SembrarSupervisorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-supervisor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant, NombreUsuario = "supervisor", Mail = mail, RolId = (int)RolConocido.Supervisor,
            PasswordHash = hasheador.Hashear(PasswordSupervisor), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
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

    private async Task<int> SembrarArticuloAsync(
        int idTenant, string nombre, int idArea, int idAlicuotaIva, int? idCategoria = null, string? codigoBarra = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = idTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = idArea,
            IdCategoria = idCategoria, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (codigoBarra is not null)
        {
            db.CodigosBarra.Add(new CodigoBarra
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, Codigo = codigoBarra, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return articulo.Id;
    }

    private async Task SembrarPrecioAsync(int idArticulo, int idListaPrecio, decimal monto)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        // En modo plataforma WaysDbContext.EstamparTenant exige id_tenant explícito antes de
        // insertar (Precio hereda EntidadTenant) — se deriva del propio artículo en vez de
        // threadear idTenant por cada call site.
        var idTenant = await db.Articulos.Where(a => a.Id == idArticulo).Select(a => a.IdTenant).FirstAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant, IdArticulo = idArticulo, IdListaPrecio = idListaPrecio, Monto = monto,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static AltaOferta OfertaDeArticulo(
        int idArticulo, decimal porcentaje, int? idEmpresa = null, decimal? cantidadMinima = null) => new(
        Nombre: "oferta de etiquetas", IdEmpresa: idEmpresa, IdArticulo: idArticulo, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: cantidadMinima, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: 0, Acumulable: false);

    private static AltaOferta OfertaDeCategoria(int idCategoria, decimal porcentaje) => new(
        Nombre: "oferta de categoría de etiquetas", IdEmpresa: null, IdArticulo: null, IdGrupo: null,
        IdCategoria: idCategoria, FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null,
        DiasSemana: null, CantidadMinima: null, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: 0, Acumulable: false);

    private static async Task CrearOfertaAsync(HttpClient cliente, AltaOferta datos)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas", datos);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    /// <summary>Instancia <see cref="ServicioDeEtiquetas"/> de forma cruda, sin pasar por HTTP —
    /// necesario para el reloj fijado (momento pinneado) y para el contador de comandos, mismo
    /// criterio que <c>OfertasResolucionTests.ContarConsultasDeResolucionAsync</c>.</summary>
    private (ServicioDeEtiquetas Servicio, ContadorDeComandos Contador) CrearServicioCrudo(int idTenant, IRelojDelSistema reloj)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, idTenant);
        var contador = new ContadorDeComandos();

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

        var db = new WaysDbContext(opciones, tenantActual);
        var contexto = new ContextoFijo(idTenant);

        var servicioDePrecios = new ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var servicioDeArticulos = new ServicioDeArticulos(db, reloj, contexto, new Ways.Application.Stock.ServicioDeLotes(db, reloj, contexto));
        var servicioDeEtiquetas = new ServicioDeEtiquetas(db, reloj, servicioDeArticulos, servicioDeOfertas);

        return (servicioDeEtiquetas, contador);
    }

    // ---- task 2.25: la guarda XOR, las dos direcciones + el límite --------------------------

    [Fact]
    public async Task AmbosSelectoresPresentesDevuelve400SeleccionAmbigua()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(AmbosSelectoresPresentesDevuelve400SeleccionAmbigua));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var idArticulo = await SembrarArticuloAsync(idTenant, "art-ambigua", idArea, idAlicuotaIva);

        var solicitud = new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, [idArticulo], new FiltroDeEtiquetas(null, null, null, null));

        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("seleccion_ambigua", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task NingunSelectorPresenteDevuelve400SeleccionRequerida()
    {
        var (_, _, idPuntoVenta, _, _, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(NingunSelectorPresenteDevuelve400SeleccionRequerida));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, null, null);

        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("seleccion_requerida", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day Slice 2, ronda 1 (juez B, MINOR 2): lista de precios inexistente -------

    /// <summary>design.md:237, "(404 si no existe)" — la lectura de <c>listas_precio</c> por el
    /// servidor devuelve 404 uniforme cuando <c>idListaPrecio</c> no existe, mismo criterio que el
    /// 404 de <c>idPuntoVenta</c> (ADR-8).</summary>
    [Fact]
    public async Task IdListaPrecioInexistenteDevuelve404()
    {
        var (_, _, idPuntoVenta, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(IdListaPrecioInexistenteDevuelve404));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, -1, [], null);

        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 2.26: el límite explícito de 200 ---------------------------------------------

    [Fact]
    public async Task Con200IdsExplicitosProcede()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(Con200IdsExplicitosProcede));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var ids = new List<int>();
        for (var i = 0; i < 200; i++)
        {
            ids.Add(await SembrarArticuloAsync(idTenant, $"art-200-{i}", idArea, idAlicuotaIva));
        }

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, ids, null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task Con201IdsExplicitosDevuelve400SeleccionExcedida()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(Con201IdsExplicitosDevuelve400SeleccionExcedida));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var ids = new List<int>();
        for (var i = 0; i < 201; i++)
        {
            ids.Add(await SembrarArticuloAsync(idTenant, $"art-201-{i}", idArea, idAlicuotaIva));
        }

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, ids, null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("seleccion_excedida", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.27: cantidad_minima > 1 nunca imprime descuento -----------------------------

    [Fact]
    public async Task UnaOfertaConCantidadMinimaTresNoAplicaAUnaEtiqueta()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnaOfertaConCantidadMinimaTresNoAplicaAUnaEtiqueta));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "llevando-3", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticulo, idLista, 1000m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 30m, cantidadMinima: 3m));

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idArticulo], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        var fila = Assert.Single(datos!.Filas);

        Assert.Empty(fila.Ofertas);
        Assert.Equal(1000m, fila.PrecioOriginal);
        Assert.Equal(1000m, fila.PrecioFinal);
    }

    // ---- task 2.28: oferta de otra empresa no aparece ---------------------------------------

    /// <summary>Fixture de DOS direcciones (mutation target 9): un artículo con oferta scoped a
    /// OTRA empresa (debe quedar afuera) Y un artículo HERMANO con oferta scoped EXPLÍCITAMENTE a
    /// la propia empresa del PV (debe aplicar). Un mutante que reemplace <c>idEmpresa</c> por
    /// <c>null</c> en <c>LineaDeResolucion</c> pasaría la primera mitad (ambos <c>null</c> ≠
    /// <c>idEmpresaB</c> siguen sin matchear) pero FALLARÍA la segunda: <c>ReglaDeOfertas.
    /// CoincideEmpresa(idEmpresaA, null)</c> es <c>false</c>, así que la oferta de la propia
    /// empresa dejaría de aplicar — el escenario positivo es el que realmente mata el mutante.</summary>
    [Fact]
    public async Task UnaOfertaDeOtraEmpresaNoApareceYUnaDeLaPropiaEmpresaSiAplica()
    {
        var (idTenant, idEmpresaA, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnaOfertaDeOtraEmpresaNoApareceYUnaDeLaPropiaEmpresaSiAplica));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idEmpresaB = await SembrarEmpresaAsync(idTenant, "Empresa B de etiquetas");

        var idArticuloOtraEmpresa = await SembrarArticuloAsync(idTenant, "articulo-empresa-b", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticuloOtraEmpresa, idLista, 500m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticuloOtraEmpresa, porcentaje: 50m, idEmpresa: idEmpresaB));

        var idArticuloPropiaEmpresa = await SembrarArticuloAsync(idTenant, "articulo-empresa-a", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticuloPropiaEmpresa, idLista, 500m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticuloPropiaEmpresa, porcentaje: 50m, idEmpresa: idEmpresaA));

        var solicitud = new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, [idArticuloOtraEmpresa, idArticuloPropiaEmpresa], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();

        var filaOtraEmpresa = datos!.Filas.Single(f => f.IdArticulo == idArticuloOtraEmpresa);
        Assert.Empty(filaOtraEmpresa.Ofertas);
        Assert.Equal(500m, filaOtraEmpresa.PrecioFinal);

        var filaPropiaEmpresa = datos.Filas.Single(f => f.IdArticulo == idArticuloPropiaEmpresa);
        Assert.Single(filaPropiaEmpresa.Ofertas);
        Assert.Equal(250m, filaPropiaEmpresa.PrecioFinal);
    }

    // ---- task 2.29: sin precio vigente, ambas direcciones -----------------------------------

    [Fact]
    public async Task UnArticuloSinPrecioVigenteQuedaExcluidoConIdentidadYNuncaEnFilas()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnArticuloSinPrecioVigenteQuedaExcluidoConIdentidadYNuncaEnFilas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idConPrecio = await SembrarArticuloAsync(idTenant, "con-precio", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idConPrecio, idLista, 100m);
        var idSinPrecio = await SembrarArticuloAsync(idTenant, "sin-precio", idArea, idAlicuotaIva);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idConPrecio, idSinPrecio], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();

        Assert.DoesNotContain(datos!.Filas, f => f.IdArticulo == idSinPrecio);
        Assert.Contains(datos.Filas, f => f.IdArticulo == idConPrecio);

        var excluido = Assert.Single(datos.Excluidos);
        Assert.Equal(idSinPrecio, excluido.IdArticulo);
        Assert.False(string.IsNullOrWhiteSpace(excluido.CodigoInterno));
        Assert.False(string.IsNullOrWhiteSpace(excluido.Nombre));
        Assert.False(string.IsNullOrWhiteSpace(excluido.Motivo));
    }

    // ---- task 2.30: divergencia de soloConOfertaVigente contra el resolver real -------------

    /// <summary>design.md:319: fixture discriminante — un artículo-scoped en ventana, uno
    /// categoría-scoped que alcanza un descendiente, uno fuera de ventana, uno con
    /// cantidad_minima=3, uno de otra empresa. <c>soloConOfertaVigente=true</c> tiene que devolver
    /// EXACTAMENTE los dos primeros.</summary>
    [Fact]
    public async Task SoloConOfertaVigenteCoincideExactamenteConElResolverReal()
    {
        var (idTenant, idEmpresaA, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(SoloConOfertaVigenteCoincideExactamenteConElResolverReal));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idEmpresaB = await SembrarEmpresaAsync(idTenant, "Empresa B divergencia");
        var idBebidas = await SembrarCategoriaAsync(idTenant, "Bebidas divergencia");
        var idGaseosas = await SembrarCategoriaAsync(idTenant, "Gaseosas divergencia", idBebidas);

        var idArticuloScoped = await SembrarArticuloAsync(idTenant, "art-scoped-vigente", idArea, idAlicuotaIva);
        var idCategoriaScoped = await SembrarArticuloAsync(idTenant, "art-categoria-vigente", idArea, idAlicuotaIva, idCategoria: idGaseosas);
        var idFueraDeVentana = await SembrarArticuloAsync(idTenant, "art-fuera-ventana", idArea, idAlicuotaIva);
        var idCantidadMinima = await SembrarArticuloAsync(idTenant, "art-cantidad-minima", idArea, idAlicuotaIva);
        var idOtraEmpresa = await SembrarArticuloAsync(idTenant, "art-otra-empresa", idArea, idAlicuotaIva);
        var idSinOferta = await SembrarArticuloAsync(idTenant, "art-sin-oferta", idArea, idAlicuotaIva);
        // Discriminante contra el confound "Aplicadas.Count>0 SIEMPRE implica PrecioFinal <
        // PrecioOriginal": una oferta que APLICA (ImporteFijo=0, beneficio válido, Aplicadas no
        // vacía) pero no mueve el precio — un mutante que decida soloConOfertaVigente por
        // "PrecioFinal < PrecioOriginal" en vez de "Aplicadas.Count > 0" excluiría este artículo
        // incorrectamente.
        var idOfertaSinDescuento = await SembrarArticuloAsync(idTenant, "art-oferta-sin-descuento", idArea, idAlicuotaIva);
        // judgment-day Slice 2, ronda 1 (juez B, MAJOR 2): discriminante contra el orden real del
        // post-filtro (ServicioDeEtiquetas.cs:136-139 filtra `soloConOfertaVigente` SOLO sobre
        // `Filas`, DESPUÉS de armar `Excluidos`) — un artículo SIN precio vigente, CON oferta
        // vigente, para que un mutante que mueva el filtro ANTES del loop (descartándolo del
        // candidato grueso en vez de post-filtrar sobre `Filas` ya resueltas) lo haga desaparecer
        // de `Excluidos` en vez de dejarlo ahí con su identidad y motivo (regla 12c).
        var idSinPrecioConOferta = await SembrarArticuloAsync(idTenant, "art-sin-precio-con-oferta", idArea, idAlicuotaIva);

        foreach (var id in new[] { idArticuloScoped, idCategoriaScoped, idFueraDeVentana, idCantidadMinima, idOtraEmpresa, idSinOferta, idOfertaSinDescuento })
        {
            await SembrarPrecioAsync(id, idLista, 100m);
        }

        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticuloScoped, porcentaje: 10m));
        await CrearOfertaAsync(admin, OfertaDeArticulo(idSinPrecioConOferta, porcentaje: 10m));
        await CrearOfertaAsync(admin, OfertaDeCategoria(idBebidas, porcentaje: 10m));

        var ayer = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        var anteayer = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5));
        await CrearOfertaAsync(admin, OfertaDeArticulo(idFueraDeVentana, porcentaje: 10m) with { FechaDesde = anteayer, FechaHasta = ayer });

        await CrearOfertaAsync(admin, OfertaDeArticulo(idCantidadMinima, porcentaje: 10m, cantidadMinima: 3m));
        await CrearOfertaAsync(admin, OfertaDeArticulo(idOtraEmpresa, porcentaje: 10m, idEmpresa: idEmpresaB));

        var ofertaSinDescuento = OfertaDeArticulo(idOfertaSinDescuento, porcentaje: 10m) with { Porcentaje = null, ImporteFijo = 0m };
        await CrearOfertaAsync(admin, ofertaSinDescuento);

        var solicitud = new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, null,
            new FiltroDeEtiquetas(Busqueda: "art-", IdArea: null, IdCategoria: null, IdMarca: null, SoloConOfertaVigente: true));

        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        var idsDevueltos = datos!.Filas.Select(f => f.IdArticulo).ToHashSet();

        Assert.Equal(new HashSet<int> { idArticuloScoped, idCategoriaScoped, idOfertaSinDescuento }, idsDevueltos);
        Assert.True(idEmpresaA > 0);

        // judgment-day Slice 2, ronda 1 (juez B, MAJOR 2): el sin-precio-con-oferta nunca es una
        // fila (ni con ni sin `soloConOfertaVigente`), pero SIGUE en `Excluidos`, con su identidad
        // y motivo — el post-filtro corre sobre `Filas` ya resueltas, nunca sobre el candidato
        // grueso que arma `Excluidos`.
        Assert.DoesNotContain(datos.Filas, f => f.IdArticulo == idSinPrecioConOferta);
        var excluidoConOferta = Assert.Single(datos.Excluidos, e => e.IdArticulo == idSinPrecioConOferta);
        Assert.False(string.IsNullOrWhiteSpace(excluidoConOferta.CodigoInterno));
        Assert.False(string.IsNullOrWhiteSpace(excluidoConOferta.Nombre));
        Assert.False(string.IsNullOrWhiteSpace(excluidoConOferta.Motivo));
    }

    // ---- task 2.31: 200/201 vía filtro — truncado ---------------------------------------------

    [Fact]
    public async Task Con200ArticulosMatcheadosPorFiltroTruncadoEsFalse()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(Con200ArticulosMatcheadosPorFiltroTruncadoEsFalse));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        for (var i = 0; i < 200; i++)
        {
            await SembrarArticuloAsync(idTenant, $"trunc-200-{i}", idArea, idAlicuotaIva);
        }

        var solicitud = new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, null, new FiltroDeEtiquetas("trunc-200-", null, null, null));
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        Assert.False(datos!.Truncado);
    }

    [Fact]
    public async Task Con201ArticulosMatcheadosPorFiltroTruncadoEsTrue()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(Con201ArticulosMatcheadosPorFiltroTruncadoEsTrue));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        for (var i = 0; i < 201; i++)
        {
            await SembrarArticuloAsync(idTenant, $"trunc-201-{i}", idArea, idAlicuotaIva);
        }

        var solicitud = new SolicitudDeEtiquetas(
            idPuntoVenta, idLista, null, new FiltroDeEtiquetas("trunc-201-", null, null, null));
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        Assert.True(datos!.Truncado);
    }

    // ---- task 2.33: NombreDeLista fuente-de-verdad (rule 12a) --------------------------------

    [Fact]
    public async Task NombreDeListaDesincronizadoPorUnUpdateCrudoSurgeElSentinel()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(NombreDeListaDesincronizadoPorUnUpdateCrudoSurgeElSentinel));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var idArticulo = await SembrarArticuloAsync(idTenant, "art-sentinel", idArea, idAlicuotaIva);

        const string sentinel = "NOMBRE-SENTINELA-XYZ";
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var lista = await db.ListasPrecio.FirstAsync(l => l.Id == idLista);
            lista.Nombre = sentinel;
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idArticulo], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        Assert.Equal(sentinel, datos!.NombreDeLista);
    }

    // ---- task 2.34: un momento por hoja, pinneado, atravesando hora_hasta -------------------

    /// <summary>mutation target 25 (judgment-day Slice 2, ronda 1, juez B, MAJOR 1): con
    /// <see cref="RelojFijo"/> "resuelto una vez" y "resuelto dos veces" son indistinguibles porque
    /// el reloj fijo devuelve siempre el mismo valor — este test usa <see cref="RelojQueAvanza"/>
    /// (suma 1 segundo por lectura) para que sí lo sean: el <c>Momento</c> echado tiene que ser
    /// EXACTAMENTE el valor de la PRIMERA lectura, y toda la hoja (dos líneas) se resuelve con UNA
    /// sola lectura del reloj — nunca una por línea (design decisión 10, "nunca uno por línea").</summary>
    [Fact]
    public async Task UnMomentoPinneadoSeEchaExactoYGobiernaTodaLaHoja()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, _, _) =
            await AprovisionarTenantAsync(nameof(UnMomentoPinneadoSeEchaExactoYGobiernaTodaLaHoja));

        var idUno = await SembrarArticuloAsync(idTenant, "art-momento-uno", idArea, idAlicuotaIva);
        var idDos = await SembrarArticuloAsync(idTenant, "art-momento-dos", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idUno, idLista, 100m);
        await SembrarPrecioAsync(idDos, idLista, 200m);

        var primeraLectura = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var reloj = new RelojQueAvanza(primeraLectura);
        var (servicio, _) = CrearServicioCrudo(idTenant, reloj);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idUno, idDos], null);
        var datos = await servicio.ComponerAsync(solicitud);

        Assert.Equal(primeraLectura, datos.Momento);
        Assert.Equal(1, reloj.Lecturas);
    }

    // ---- task 2.35: read-back pairwise de cada campo posicional (rule 12b/12c) --------------

    [Fact]
    public async Task CadaCampoPosicionalDeFilaDeEtiquetaSeLeeDeVueltaConValoresDistintos()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CadaCampoPosicionalDeFilaDeEtiquetaSeLeeDeVueltaConValoresDistintos));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        // rule 12c: un HERMANO del mismo tenant, con valores DISTINTOS en cada campo — para que
        // un swap posicional entre las dos filas sea detectable.
        var idUno = await SembrarArticuloAsync(idTenant, "Alfa", idArea, idAlicuotaIva, codigoBarra: "7790000000011");
        var idDos = await SembrarArticuloAsync(idTenant, "Beta", idArea, idAlicuotaIva, codigoBarra: "7790000000022");

        await SembrarPrecioAsync(idUno, idLista, 111.11m);
        await SembrarPrecioAsync(idDos, idLista, 222.22m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idDos, porcentaje: 10m));

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idUno, idDos], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);
        var datos = await respuesta.Content.ReadFromJsonAsync<DatosDeEtiquetas>();

        Assert.Equal(2, datos!.Filas.Count);

        var filaUno = datos.Filas.Single(f => f.IdArticulo == idUno);
        Assert.Equal("Alfa", filaUno.Nombre);
        Assert.Contains("Alfa", filaUno.CodigoInterno);
        Assert.Equal("7790000000011", filaUno.CodigoBarra);
        Assert.Equal("Unidad", filaUno.UnidadVenta);
        Assert.Equal(111.11m, filaUno.PrecioOriginal);
        Assert.Equal(111.11m, filaUno.PrecioFinal);
        Assert.Empty(filaUno.Ofertas);

        var filaDos = datos.Filas.Single(f => f.IdArticulo == idDos);
        Assert.Equal("Beta", filaDos.Nombre);
        Assert.Contains("Beta", filaDos.CodigoInterno);
        Assert.Equal("7790000000022", filaDos.CodigoBarra);
        Assert.Equal(222.22m, filaDos.PrecioOriginal);
        Assert.Equal(200.00m, filaDos.PrecioFinal);
        Assert.Single(filaDos.Ofertas);

        // Campos de DatosDeEtiquetas, pairwise-distintos también.
        Assert.Equal(idLista, datos.IdListaPrecio);
        Assert.False(string.IsNullOrWhiteSpace(datos.NombreDeLista));
        Assert.False(datos.Truncado);
        Assert.Empty(datos.Excluidos);
    }

    // ---- task 2.36: la cláusula de exposición — nombre de propiedad, nunca substring --------

    private static readonly HashSet<string> PropiedadesDeCostoProhibidas = new(StringComparer.OrdinalIgnoreCase)
    {
        "costo", "costoLista", "costoNominal", "descuentoProveedor", "idProveedorHabitual", "proveedor", "margen"
    };

    private static void RecorrerYAsertarSinPropiedadesDeCosto(JsonElement elemento)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propiedad in elemento.EnumerateObject())
                {
                    Assert.False(
                        PropiedadesDeCostoProhibidas.Contains(propiedad.Name),
                        $"Propiedad de costo/proveedor expuesta en el DTO serializado: {propiedad.Name}");
                    RecorrerYAsertarSinPropiedadesDeCosto(propiedad.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    RecorrerYAsertarSinPropiedadesDeCosto(item);
                }

                break;
        }
    }

    [Fact]
    public async Task LaRespuestaSerializadaNoContieneNingunaPropiedadDeCostoOProveedor()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaRespuestaSerializadaNoContieneNingunaPropiedadDeCostoOProveedor));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticulo = await SembrarArticuloAsync(idTenant, "art-exposicion", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticulo, idLista, 100m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticulo, porcentaje: 15m));

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idArticulo], null);
        var respuesta = await admin.PostAsJsonAsync("/api/etiquetas/datos", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var json = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        RecorrerYAsertarSinPropiedadesDeCosto(json);

        // Positivo de control: el DTO SÍ trae "descuentoUnitario" (OfertaAplicadaDto), que
        // contiene la SUBSTRING "descuento" — la prueba no debe fallar por eso (nunca substring).
        var textoCrudo = json.GetRawText();
        Assert.Contains("descuentoUnitario", textoCrudo, StringComparison.OrdinalIgnoreCase);
    }

    // ---- task 2.37: matriz de autorización + tenant scoping --------------------------------

    [Theory]
    [InlineData(RolConocido.Vendedor)]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Admin)]
    public async Task RolesDelPosPuedenComponerLaHoja(RolConocido rol)
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync($"{nameof(RolesDelPosPuedenComponerLaHoja)}{rol}");

        HttpClient cliente = rol switch
        {
            RolConocido.Vendedor => await ClienteLogueadoAsync(await SembrarVendedorAsync(idTenant, nameof(RolesDelPosPuedenComponerLaHoja)), PasswordVendedor),
            RolConocido.Supervisor => await ClienteLogueadoAsync(await SembrarSupervisorAsync(idTenant, nameof(RolesDelPosPuedenComponerLaHoja)), PasswordSupervisor),
            _ => await ClienteLogueadoAsync(mailAdmin, passwordAdmin)
        };
        using var _ = cliente;

        var idArticulo = await SembrarArticuloAsync(idTenant, "art-autorizacion", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticulo, idLista, 50m);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idArticulo], null);
        var respuesta = await cliente.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task RootEsRechazadoConForbidden()
    {
        var (_, _, idPuntoVenta, _, _, idLista, _, _) =
            await AprovisionarTenantAsync(nameof(RootEsRechazadoConForbidden));

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeEtiquetas(idPuntoVenta, idLista, [], null);
        var respuesta = await root.PostAsJsonAsync("/api/etiquetas/datos", solicitud);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>task 2.37: tenant B nunca ve los artículos de tenant A — un admin de tenant B
    /// pidiendo explícitamente los ids de tenant A no los recibe (el filtro global de EF/RLS los
    /// deja invisibles), y el PV de tenant A es un 404 uniforme (ADR-8) para tenant B.</summary>
    [Fact]
    public async Task TenantBNuncaVeLosArticulosNiElPuntoDeVentaDeTenantA()
    {
        var (idTenantA, _, idPuntoVentaA, idAreaA, idAlicuotaIvaA, idListaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(TenantBNuncaVeLosArticulosNiElPuntoDeVentaDeTenantA) + "A");
        var (_, _, idPuntoVentaB, _, _, idListaB, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(TenantBNuncaVeLosArticulosNiElPuntoDeVentaDeTenantA) + "B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);

        var idArticuloDeA = await SembrarArticuloAsync(idTenantA, "articulo-tenant-a", idAreaA, idAlicuotaIvaA);
        await SembrarPrecioAsync(idArticuloDeA, idListaA, 100m);

        // El PV de A es 404 uniforme para B — nunca revela ni siquiera que el id existe.
        var solicitudPvAjeno = new SolicitudDeEtiquetas(idPuntoVentaA, idListaB, [], null);
        var respuestaPvAjeno = await adminB.PostAsJsonAsync("/api/etiquetas/datos", solicitudPvAjeno);
        Assert.Equal(HttpStatusCode.NotFound, respuestaPvAjeno.StatusCode);

        // Pidiendo explícitamente el id de A desde el PV/lista de B: el filtro global de tenant ya
        // lo deja invisible en `db.Articulos` — la fila nunca aparece en Filas ni en Excluidos con
        // la identidad real de A.
        var solicitudIdAjeno = new SolicitudDeEtiquetas(idPuntoVentaB, idListaB, [idArticuloDeA], null);
        var respuestaIdAjeno = await adminB.PostAsJsonAsync("/api/etiquetas/datos", solicitudIdAjeno);
        Assert.Equal(HttpStatusCode.OK, respuestaIdAjeno.StatusCode);

        var datos = await respuestaIdAjeno.Content.ReadFromJsonAsync<DatosDeEtiquetas>();
        Assert.Empty(datos!.Filas);
        Assert.Empty(datos.Excluidos);
    }

    // ---- task 2.38: presupuesto de comandos, 1 y 200 artículos, mismo conteo ---------------

    [Fact]
    public async Task ElPresupuestoDeComandosEsIgualParaUnArticuloY200Articulos()
    {
        var (idTenant, _, idPuntoVenta, idArea, idAlicuotaIva, idLista, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElPresupuestoDeComandosEsIgualParaUnArticuloY200Articulos));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idArticuloUnico = await SembrarArticuloAsync(idTenant, "art-budget-1", idArea, idAlicuotaIva);
        await SembrarPrecioAsync(idArticuloUnico, idLista, 10m);
        await CrearOfertaAsync(admin, OfertaDeArticulo(idArticuloUnico, porcentaje: 5m));

        var idsMuchos = new List<int>();
        for (var i = 0; i < 200; i++)
        {
            var id = await SembrarArticuloAsync(idTenant, $"art-budget-200-{i}", idArea, idAlicuotaIva);
            await SembrarPrecioAsync(id, idLista, 10m);
            idsMuchos.Add(id);
        }
        await CrearOfertaAsync(admin, OfertaDeArticulo(idsMuchos[0], porcentaje: 5m));

        var momento = DateTimeOffset.UtcNow;

        var (servicioUno, contadorUno) = CrearServicioCrudo(idTenant, new RelojFijo(momento));
        await servicioUno.ComponerAsync(new SolicitudDeEtiquetas(idPuntoVenta, idLista, [idArticuloUnico], null));

        var (servicioMuchos, contadorMuchos) = CrearServicioCrudo(idTenant, new RelojFijo(momento));
        await servicioMuchos.ComponerAsync(new SolicitudDeEtiquetas(idPuntoVenta, idLista, idsMuchos, null));

        Assert.Equal(contadorUno.Consultas, contadorMuchos.Consultas);
        Assert.True(
            contadorUno.Consultas <= 11,
            $"Se esperaban a lo sumo 11 consultas (design: Testing Strategy), se emitieron {contadorUno.Consultas}.");
    }
}
