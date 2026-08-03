using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios; // PaginaDe<T>, SolicitudDeLogin
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 2 (tasks 2.5, 2.8-2.12, db-error-backstops skill): <c>ServicioDeArticulos</c>/
/// <c>ArticulosEndpoints</c> punta a punta contra Postgres real — carreras genuinas de
/// <c>codigo_interno</c> (autogenerado y provisto por el cliente) y <c>codigos_barra</c>,
/// resolución de disponibilidad por empresa, FKs nuevas de esta slice, ABM completo con la
/// policy <c>GestionDeCatalogo</c> (admin-only), y el 404 uniforme cross-tenant (ADR-8).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    // Mismo motivo que CatalogosTests.OpcionesJson/OrganizacionTests.OpcionesJson: el server
    // registra JsonStringEnumConverter (Program.cs, "los enums viajan como texto") pero
    // ReadFromJsonAsync<T>()/GetFromJsonAsync<T>() sin opciones usa las opciones DEFAULT de
    // System.Text.Json del lado cliente, que no lo traen — ArticuloListado.UnidadVenta (NUNCA
    // null, a diferencia de TipoDocumento? de Cliente) revienta la deserialización sin esto.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, string MailAdmin, string PasswordAdmin)>
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

        return (resultado.IdTenant, area.Id, idAlicuotaIva, mailAdmin, resultado.PasswordTemporal);
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
            IdTenant = idTenant,
            NombreUsuario = "vendedor",
            Mail = mail,
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
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

    private static AltaArticulo AltaValida(int idArea, int idAlicuotaIva, string? codigoInterno = null) =>
        new(
            CodigoInterno: codigoInterno,
            Nombre: "Coca Cola 500ml",
            Descripcion: null,
            IdArea: idArea,
            IdCategoria: null,
            IdMarca: null,
            IdGrupo: null,
            IdProveedorHabitual: null,
            IdAlicuotaIva: idAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad,
            UnidadesPorBulto: null,
            EsProducto: true,
            CostoLista: null,
            DescuentoProveedor: null,
            CostoNominal: null);

    // ---- task 2.8: codigo_interno ----------------------------------------------------------

    /// <summary>Spec: Concurrent autogeneration yields no gaps or duplicates. Sin interceptor
    /// de rendezvous (mismo criterio que <c>ClientesEndpointsTests</c>): el lock de fila de
    /// <c>numeraciones_articulos</c> ya serializa la carrera por construcción.</summary>
    [Fact]
    public async Task LaCreacionConcurrenteSinCodigoInternoAsignaValoresDistintosSinExponerElBackstop()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteSinCodigoInternoAsignaValoresDistintosSinExponerElBackstop));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var altaA = AltaValida(idArea, idAlicuotaIva) with { Nombre = "Artículo A" };
        var altaB = AltaValida(idArea, idAlicuotaIva) with { Nombre = "Artículo B" };

        var tareaA = admin.PostAsJsonAsync("/api/articulos", altaA);
        var tareaB = admin.PostAsJsonAsync("/api/articulos", altaB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var creados = await Task.WhenAll(respuestas.Select(r => r.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson)));
        var codigos = creados.Select(a => a!.CodigoInterno).Distinct().ToList();

        Assert.Equal(2, codigos.Count);
    }

    /// <summary>Spec: User-supplied codigo_interno is honored when unique.</summary>
    [Fact]
    public async Task CrearConCodigoInternoProvistoLoRespeta()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConCodigoInternoProvistoLoRespeta));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = AltaValida(idArea, idAlicuotaIva, codigoInterno: "COD-100");

        var respuesta = await admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var creado = await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson);
        Assert.Equal("COD-100", creado!.CodigoInterno);
    }

    /// <summary>Spec: Duplicate user-supplied codigo_interno is rejected.</summary>
    [Fact]
    public async Task CrearConCodigoInternoDuplicadoDevuelve409()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConCodigoInternoDuplicadoDevuelve409));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = AltaValida(idArea, idAlicuotaIva, codigoInterno: "COD-200");
        var primera = await admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segunda = await admin.PostAsJsonAsync("/api/articulos", alta with { Nombre = "Otro nombre" });

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_interno_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Duplicate user-supplied codigo_interno is rejected — carrera genuina: a
    /// diferencia del camino autogenerado (contador con lock de fila), acá no hay ningún lock
    /// que serialice la carrera por construcción (mismo criterio que
    /// <c>ProveedoresEndpointsTests.LaCreacionConcurrenteConElMismoCuitDaExactamenteUnGanador</c>)
    /// — dos POST con el mismo <c>codigo_interno</c> provisto, lanzados con
    /// <c>Task.WhenAll</c>, compiten de verdad.</summary>
    [Fact]
    public async Task LaCreacionConcurrenteConElMismoCodigoInternoProvistoDaExactamenteUnGanador()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteConElMismoCodigoInternoProvistoDaExactamenteUnGanador));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        const string codigoCompartido = "COD-RACE";
        var altaA = AltaValida(idArea, idAlicuotaIva, codigoCompartido) with { Nombre = "Artículo A" };
        var altaB = AltaValida(idArea, idAlicuotaIva, codigoCompartido) with { Nombre = "Artículo B" };

        var tareaA = admin.PostAsJsonAsync("/api/articulos", altaA);
        var tareaB = admin.PostAsJsonAsync("/api/articulos", altaB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_interno_duplicado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.9: codigos_barra -----------------------------------------------------------

    /// <summary>Spec: Same barcode across different tenants is allowed.</summary>
    [Fact]
    public async Task ElMismoCodigoDeBarraEnDosTenantsEsPermitido()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(ElMismoCodigoDeBarraEnDosTenantsEsPermitido) + "-A");
        var (_, idAreaB, idAlicuotaIvaB, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(ElMismoCodigoDeBarraEnDosTenantsEsPermitido) + "-B");

        const string codigoCompartido = "7791234567890";

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var articuloA = await CrearArticuloAsync(adminA, idAreaA, idAlicuotaIvaA);
        var respuestaA = await adminA.PostAsJsonAsync(
            $"/api/articulos/{articuloA.Id}/codigos-barra", new AltaCodigoBarra(codigoCompartido));
        Assert.Equal(HttpStatusCode.Created, respuestaA.StatusCode);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var articuloB = await CrearArticuloAsync(adminB, idAreaB, idAlicuotaIvaB);
        var respuestaB = await adminB.PostAsJsonAsync(
            $"/api/articulos/{articuloB.Id}/codigos-barra", new AltaCodigoBarra(codigoCompartido));
        Assert.Equal(HttpStatusCode.Created, respuestaB.StatusCode);
    }

    /// <summary>Spec: Duplicate barcode within the same tenant is rejected.</summary>
    [Fact]
    public async Task AgregarUnCodigoDeBarraDuplicadoEnElMismoTenantDevuelve409()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(AgregarUnCodigoDeBarraDuplicadoEnElMismoTenantDevuelve409));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var articuloUno = await CrearArticuloAsync(admin, idArea, idAlicuotaIva, "Artículo 1");
        var articuloDos = await CrearArticuloAsync(admin, idArea, idAlicuotaIva, "Artículo 2");

        const string codigo = "7791234567890";
        var primero = await admin.PostAsJsonAsync($"/api/articulos/{articuloUno.Id}/codigos-barra", new AltaCodigoBarra(codigo));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        var segundo = await admin.PostAsJsonAsync($"/api/articulos/{articuloDos.Id}/codigos-barra", new AltaCodigoBarra(codigo));

        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_barra_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Concurrent creation race yields exactly one winner — carrera genuina:
    /// <c>codigo</c> es un valor provisto por el cliente HTTP, sin lock de fila que la
    /// serialice por construcción.</summary>
    [Fact]
    public async Task LaCreacionConcurrenteConElMismoCodigoDeBarraDaExactamenteUnGanador()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteConElMismoCodigoDeBarraDaExactamenteUnGanador));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var articuloUno = await CrearArticuloAsync(admin, idArea, idAlicuotaIva, "Artículo 1");
        var articuloDos = await CrearArticuloAsync(admin, idArea, idAlicuotaIva, "Artículo 2");

        const string codigoCompartido = "7799999999999";

        var tareaA = admin.PostAsJsonAsync($"/api/articulos/{articuloUno.Id}/codigos-barra", new AltaCodigoBarra(codigoCompartido));
        var tareaB = admin.PostAsJsonAsync($"/api/articulos/{articuloDos.Id}/codigos-barra", new AltaCodigoBarra(codigoCompartido));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_barra_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Admin removes a barcode without affecting the articulo.</summary>
    [Fact]
    public async Task UnAdminRemueveUnCodigoDeBarraSinAfectarElArticulo()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnAdminRemueveUnCodigoDeBarraSinAfectarElArticulo));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);
        var codigoUno = await AgregarCodigoBarraAsync(admin, articulo.Id, "7791111111111");
        await AgregarCodigoBarraAsync(admin, articulo.Id, "7792222222222");

        var baja = await admin.DeleteAsync($"/api/articulos/{articulo.Id}/codigos-barra/{codigoUno.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var articuloTrasBaja = await admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{articulo.Id}", OpcionesJson);
        Assert.NotNull(articuloTrasBaja);
        Assert.Equal(articulo.Nombre, articuloTrasBaja!.Nombre);
    }

    // ---- task 2.10: disponibilidad ---------------------------------------------------------

    /// <summary>Spec: Default-true articulo is visible to a later empresa.</summary>
    [Fact]
    public async Task UnArticuloDisponibleParaTodasEsVisibleParaUnaEmpresaCreadaDespues()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnArticuloDisponibleParaTodasEsVisibleParaUnaEmpresaCreadaDespues));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, nameof(UnArticuloDisponibleParaTodasEsVisibleParaUnaEmpresaCreadaDespues));

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>($"/api/articulos?idEmpresa={idEmpresa}", OpcionesJson);

        Assert.NotNull(listado);
        Assert.Contains(listado!.Items, a => a.Id == articulo.Id);
    }

    /// <summary>Spec: Explicit subset excludes other empresas.</summary>
    [Fact]
    public async Task UnArticuloRestringidoNoEsVisibleParaUnaEmpresaFueraDelSubset()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnArticuloRestringidoNoEsVisibleParaUnaEmpresaFueraDelSubset));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var idEmpresaIncluida = await SembrarEmpresaAsync(idTenant, nameof(UnArticuloRestringidoNoEsVisibleParaUnaEmpresaFueraDelSubset) + "-incluida");
        var idEmpresaExcluida = await SembrarEmpresaAsync(idTenant, nameof(UnArticuloRestringidoNoEsVisibleParaUnaEmpresaFueraDelSubset) + "-excluida");

        var alta = AltaValida(idArea, idAlicuotaIva) with
        {
            DisponibleParaTodas = false,
            IdsEmpresas = [idEmpresaIncluida]
        };
        var respuesta = await admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson);

        var listadoIncluida = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>($"/api/articulos?idEmpresa={idEmpresaIncluida}", OpcionesJson);
        Assert.Contains(listadoIncluida!.Items, a => a.Id == creado!.Id);

        var listadoExcluida = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>($"/api/articulos?idEmpresa={idEmpresaExcluida}", OpcionesJson);
        Assert.DoesNotContain(listadoExcluida!.Items, a => a.Id == creado!.Id);
    }

    /// <summary>Spec: Cross-tenant empresa reference is blocked.</summary>
    [Fact]
    public async Task CrearConEmpresaDeOtroTenantEnElSubsetDevuelve400()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(CrearConEmpresaDeOtroTenantEnElSubsetDevuelve400) + "-A");
        var (idTenantB, _, _, _, _) =
            await AprovisionarTenantAsync(nameof(CrearConEmpresaDeOtroTenantEnElSubsetDevuelve400) + "-B");
        var idEmpresaDeB = await SembrarEmpresaAsync(idTenantB, nameof(CrearConEmpresaDeOtroTenantEnElSubsetDevuelve400) + "-empresa");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);

        var alta = AltaValida(idAreaA, idAlicuotaIvaA) with
        {
            DisponibleParaTodas = false,
            IdsEmpresas = [idEmpresaDeB]
        };
        var respuesta = await adminA.PostAsJsonAsync("/api/articulos", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.11: FK smoke tests ---------------------------------------------------------

    [Fact]
    public async Task CrearConIdAreaInexistenteDevuelve400()
    {
        var (_, _, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConIdAreaInexistenteDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync("/api/articulos", AltaValida(idArea: 999_999, idAlicuotaIva));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdCategoriaDeOtroTenantDevuelve400()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(CrearConIdCategoriaDeOtroTenantDevuelve400) + "-A");
        var (idTenantB, _, _, _, _) =
            await AprovisionarTenantAsync(nameof(CrearConIdCategoriaDeOtroTenantDevuelve400) + "-B");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var categoriaDeB = new Categoria { IdTenant = idTenantB, Nombre = "Categoría de B", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Categorias.Add(categoriaDeB);
        await db.SaveChangesAsync();

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = AltaValida(idAreaA, idAlicuotaIvaA) with { IdCategoria = categoriaDeB.Id };
        var respuesta = await adminA.PostAsJsonAsync("/api/articulos", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdAlicuotaIvaInexistenteDevuelve400()
    {
        var (_, idArea, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConIdAlicuotaIvaInexistenteDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync("/api/articulos", AltaValida(idArea, idAlicuotaIva: 999_999));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>fk_codigos_barra_articulo: agregar un código de barras a un artículo
    /// inexistente da el mismo 404 uniforme (ADR-8), no un 400 — el artículo padre se resuelve
    /// primero por <c>BuscarAsync</c>.</summary>
    [Fact]
    public async Task AgregarCodigoBarraAUnArticuloInexistenteDevuelve404()
    {
        var (_, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(AgregarCodigoBarraAUnArticuloInexistenteDevuelve404));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync("/api/articulos/999999/codigos-barra", new AltaCodigoBarra("7791234567890"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 2.12: ABM round trip + autorización ------------------------------------------

    [Fact]
    public async Task UnAdminCreaYDaDeBajaUnArticulo()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnAdminCreaYDaDeBajaUnArticulo));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creado = await CrearArticuloAsync(admin, idArea, idAlicuotaIva, "De alta y baja");

        var baja = await admin.DeleteAsync($"/api/articulos/{creado.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var listado = await admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>("/api/articulos?busqueda=De+alta+y+baja", OpcionesJson);
        Assert.DoesNotContain(listado!.Items, a => a.Id == creado.Id);
    }

    [Fact]
    public async Task UnVendedorNoPuedeCrearArticulos()
    {
        var (idTenant, idArea, idAlicuotaIva, _, _) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeCrearArticulos));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeCrearArticulos));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PostAsJsonAsync("/api/articulos", AltaValida(idArea, idAlicuotaIva));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorNoPuedeAgregarCodigosDeBarra()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeAgregarCodigosDeBarra));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeAgregarCodigosDeBarra));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/codigos-barra", new AltaCodigoBarra("7791234567890"));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>Spec: articulos / Availability Model — un vendedor tampoco puede cambiar la
    /// disponibilidad de un artículo (la disponibilidad viaja en el PUT de edición, no en un
    /// endpoint propio).</summary>
    [Fact]
    public async Task UnVendedorNoPuedeCambiarLaDisponibilidad()
    {
        var (idTenant, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeCambiarLaDisponibilidad));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, nameof(UnVendedorNoPuedeCambiarLaDisponibilidad));

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeCambiarLaDisponibilidad));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var edicion = EdicionDesde(articulo) with { DisponibleParaTodas = false, IdsEmpresas = [idEmpresa] };
        var respuesta = await vendedor.PutAsJsonAsync($"/api/articulos/{articulo.Id}", edicion);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant".</summary>
    [Fact]
    public async Task UnArticuloDeOtroTenantDevuelve404()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnArticuloDeOtroTenantDevuelve404) + "-A");
        var (_, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnArticuloDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var articuloDeA = await CrearArticuloAsync(adminA, idAreaA, idAlicuotaIvaA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.GetAsync($"/api/articulos/{articuloDeA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnPutSobreUnArticuloDeOtroTenantDevuelve404()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnArticuloDeOtroTenantDevuelve404) + "-A");
        var (_, idAreaB, idAlicuotaIvaB, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnArticuloDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var articuloDeA = await CrearArticuloAsync(adminA, idAreaA, idAlicuotaIvaA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var edicion = EdicionDesde(articuloDeA) with { IdArea = idAreaB, IdAlicuotaIva = idAlicuotaIvaB };
        var respuesta = await adminB.PutAsJsonAsync($"/api/articulos/{articuloDeA.Id}", edicion);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnDeleteSobreUnArticuloDeOtroTenantDevuelve404()
    {
        var (_, idAreaA, idAlicuotaIvaA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnArticuloDeOtroTenantDevuelve404) + "-A");
        var (_, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnArticuloDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var articuloDeA = await CrearArticuloAsync(adminA, idAreaA, idAlicuotaIvaA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.DeleteAsync($"/api/articulos/{articuloDeA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Spec: Margin-Based Price Suggestion, "Suggestion requires explicit apply" —
    /// el endpoint de sugerencia nunca escribe un <c>precios</c> row.</summary>
    [Fact]
    public async Task LaSugerenciaDePrecioNoPersisteNada()
    {
        var (_, idArea, idAlicuotaIva, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaSugerenciaDePrecioNoPersisteNada));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = AltaValida(idArea, idAlicuotaIva) with { CostoNominal = 100m };
        var respuestaAlta = await admin.PostAsJsonAsync("/api/articulos", alta);
        var creado = await respuestaAlta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson);

        var respuesta = await admin.GetAsync($"/api/articulos/{creado!.Id}/sugerencia-precio");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var sugerencia = await respuesta.Content.ReadFromJsonAsync<SugerenciaDePrecio>();
        // Sin grupo/proveedor con margen: no hay sugerencia posible (spec no cubre el caso con
        // un escenario propio — ausencia de dato, no un error).
        Assert.Null(sugerencia!.PrecioSugerido);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static async Task<ArticuloListado> CrearArticuloAsync(
        HttpClient cliente, int idArea, int idAlicuotaIva, string nombre = "Artículo de prueba")
    {
        var alta = AltaValida(idArea, idAlicuotaIva) with { Nombre = nombre };
        var respuesta = await cliente.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
    }

    private static async Task<CodigoBarraListado> AgregarCodigoBarraAsync(HttpClient cliente, int idArticulo, string codigo)
    {
        var respuesta = await cliente.PostAsJsonAsync($"/api/articulos/{idArticulo}/codigos-barra", new AltaCodigoBarra(codigo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CodigoBarraListado>())!;
    }

    private static EdicionArticulo EdicionDesde(ArticuloListado articulo) => new(
        Nombre: articulo.Nombre,
        Descripcion: articulo.Descripcion,
        IdArea: articulo.IdArea,
        IdCategoria: articulo.IdCategoria,
        IdMarca: articulo.IdMarca,
        IdGrupo: articulo.IdGrupo,
        IdProveedorHabitual: articulo.IdProveedorHabitual,
        IdAlicuotaIva: articulo.IdAlicuotaIva,
        UnidadVenta: articulo.UnidadVenta,
        UnidadesPorBulto: articulo.UnidadesPorBulto,
        EsProducto: articulo.EsProducto,
        CostoLista: articulo.CostoLista,
        DescuentoProveedor: articulo.DescuentoProveedor,
        CostoNominal: articulo.CostoNominal,
        DisponibleParaTodas: articulo.DisponibleParaTodas,
        IdsEmpresas: null,
        Activo: articulo.Activo);
}
