using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Códigos de proveedor (<c>codigos_proveedor</c>): búsqueda por el código que el proveedor imprime
/// en su factura, alta de artículo con código y asociación posterior. Contra Postgres real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosCodigosProveedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdArea, int IdAlicuotaIva, int IdProveedor, HttpClient Admin);

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
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

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var (idTenant, idArea, idAlicuotaIva, mail, password) = await AprovisionarTenantAsync(nombre);
        var admin = await ClienteLogueadoAsync(mail, password);
        var idProveedor = await SembrarProveedorAsync(idTenant, nombre + "-prov");
        return new Contexto(idTenant, idArea, idAlicuotaIva, idProveedor, admin);
    }

    private async Task<int> SembrarProveedorAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicion = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicion);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = idTenant, RazonSocial = nombre, IdCondicionFiscal = condicion.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return proveedor.Id;
    }

    private static AltaArticulo Alta(
        Contexto c, string nombre, string? codigoProveedor = null, int? idProveedorHabitual = null) =>
        new(
            CodigoInterno: null,
            Nombre: nombre,
            Descripcion: null,
            IdArea: c.IdArea,
            IdCategoria: null,
            IdMarca: null,
            IdGrupo: null,
            IdProveedorHabitual: idProveedorHabitual,
            IdAlicuotaIva: c.IdAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad,
            UnidadesPorBulto: null,
            EsProducto: true,
            CostoLista: null,
            DescuentoProveedor: null,
            CostoNominal: null,
            CodigoProveedor: codigoProveedor);

    private static async Task<ArticuloListado> CrearArticuloAsync(Contexto c, string nombre)
    {
        var respuesta = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, nombre));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
    }

    private static Task<HttpResponseMessage> AsociarAsync(HttpClient cliente, int idArticulo, int idProveedor, string codigo) =>
        cliente.PostAsJsonAsync($"/api/articulos/{idArticulo}/codigos-proveedor", new AltaCodigoProveedor(idProveedor, codigo));

    private static async Task<List<ArticuloListado>> BuscarAsync(HttpClient cliente, string busqueda, int? idProveedor)
    {
        var url = $"/api/articulos?busqueda={Uri.EscapeDataString(busqueda)}";
        if (idProveedor is { } id)
        {
            url += $"&idProveedor={id}";
        }

        var pagina = await cliente.GetFromJsonAsync<PaginaDe<ArticuloListado>>(url, OpcionesJson);
        return pagina!.Items.ToList();
    }

    private async Task EjecutarComoPlataformaAsync(string sql, params object[] parametros)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    private static async Task InsertarCodigoCrudoAsync(
        NpgsqlConnection cruda, int idTenant, int idArticulo, int idProveedor, string codigo)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO codigos_proveedor (id_tenant, id_articulo, id_proveedor, codigo, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
        await comando.ExecuteNonQueryAsync();
    }

    // ---- búsqueda ---------------------------------------------------------------------------

    /// <summary>Cláusula bajo prueba: la igualdad del código de proveedor y su ranking. El artículo
    /// que coincide SOLO por el código tiene un nombre que ordena último, y el que coincide por
    /// nombre ordena primero: si el código no los promoviera, el orden sería el inverso.</summary>
    [Fact]
    public async Task BuscarConIdProveedorListaPrimeroLosQueCoincidenPorSuCodigoYLoDevuelve()
    {
        var c = await PrepararAsync(nameof(BuscarConIdProveedorListaPrimeroLosQueCoincidenPorSuCodigoYLoDevuelve));
        using var _ = c.Admin;

        var porNombre = await CrearArticuloAsync(c, "AAA tornillo ABC-1");
        var porCodigo = await CrearArticuloAsync(c, "ZZZ clavo");
        Assert.Equal(HttpStatusCode.Created, (await AsociarAsync(c.Admin, porCodigo.Id, c.IdProveedor, "Abc-1")).StatusCode);

        var items = await BuscarAsync(c.Admin, "abc-1", c.IdProveedor);

        Assert.Equal([porCodigo.Id, porNombre.Id], items.Select(i => i.Id).ToArray());
        Assert.Equal("Abc-1", items[0].CodigoProveedor);
        Assert.Null(items[1].CodigoProveedor);
    }

    [Fact]
    public async Task BuscarSinIdProveedorNoUsaLosCodigosDeProveedorYNoCambiaElResultado()
    {
        var c = await PrepararAsync(nameof(BuscarSinIdProveedorNoUsaLosCodigosDeProveedorYNoCambiaElResultado));
        using var _ = c.Admin;

        var porNombre = await CrearArticuloAsync(c, "AAA tornillo ABC-1");
        var porCodigo = await CrearArticuloAsync(c, "ZZZ clavo");
        await AsociarAsync(c.Admin, porCodigo.Id, c.IdProveedor, "ABC-1");

        var items = await BuscarAsync(c.Admin, "ABC-1", idProveedor: null);

        Assert.Equal([porNombre.Id], items.Select(i => i.Id).ToArray());
        Assert.All(items, i => Assert.Null(i.CodigoProveedor));
    }

    [Fact]
    public async Task UnCodigoDeOtroProveedorNoCoincide()
    {
        var c = await PrepararAsync(nameof(UnCodigoDeOtroProveedorNoCoincide));
        using var _ = c.Admin;
        var otroProveedor = await SembrarProveedorAsync(c.IdTenant, nameof(UnCodigoDeOtroProveedorNoCoincide) + "-otro");

        var articulo = await CrearArticuloAsync(c, "Clavo");
        await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "XYZ-9");

        Assert.Empty(await BuscarAsync(c.Admin, "XYZ-9", otroProveedor));
        Assert.Single(await BuscarAsync(c.Admin, "XYZ-9", c.IdProveedor));
    }

    /// <summary>Cláusula bajo prueba: <c>IdProveedor</c> en la proyección de <c>codigoProveedor</c>
    /// (no en el filtro). El artículo entra por NOMBRE y su único código es de otro proveedor: no
    /// debe exponerlo ni subir en el orden.</summary>
    [Fact]
    public async Task ElCodigoDeOtroProveedorNoSeExponeNiPromueveAUnArticuloQueEntraPorNombre()
    {
        var c = await PrepararAsync(nameof(ElCodigoDeOtroProveedorNoSeExponeNiPromueveAUnArticuloQueEntraPorNombre));
        using var _ = c.Admin;
        var otroProveedor = await SembrarProveedorAsync(
            c.IdTenant, nameof(ElCodigoDeOtroProveedorNoSeExponeNiPromueveAUnArticuloQueEntraPorNombre) + "-otro");

        var porNombre = await CrearArticuloAsync(c, "AAA tornillo ABC-1");
        await AsociarAsync(c.Admin, porNombre.Id, otroProveedor, "ABC-1");
        var porCodigo = await CrearArticuloAsync(c, "ZZZ clavo");
        await AsociarAsync(c.Admin, porCodigo.Id, c.IdProveedor, "ABC-1");

        var items = await BuscarAsync(c.Admin, "ABC-1", c.IdProveedor);

        Assert.Equal([porCodigo.Id, porNombre.Id], items.Select(i => i.Id).ToArray());
        Assert.Equal("ABC-1", items[0].CodigoProveedor);
        Assert.Null(items[1].CodigoProveedor);
    }

    [Fact]
    public async Task ElCodigoDeProveedorSoloCoincideExacto()
    {
        var c = await PrepararAsync(nameof(ElCodigoDeProveedorSoloCoincideExacto));
        using var _ = c.Admin;

        var articulo = await CrearArticuloAsync(c, "Clavo");
        await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "XYZ-900");

        Assert.Empty(await BuscarAsync(c.Admin, "XYZ-9", c.IdProveedor));
    }

    [Fact]
    public async Task UnCodigoDadoDeBajaNoCoincide()
    {
        var c = await PrepararAsync(nameof(UnCodigoDadoDeBajaNoCoincide));
        using var _ = c.Admin;

        var articulo = await CrearArticuloAsync(c, "Clavo");
        var asociado = await (await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "BAJA-1"))
            .Content.ReadFromJsonAsync<CodigoProveedorListado>();
        Assert.Single(await BuscarAsync(c.Admin, "BAJA-1", c.IdProveedor));

        await EjecutarComoPlataformaAsync(
            "UPDATE codigos_proveedor SET deleted_at = now() WHERE id_codigo_proveedor = $1", asociado!.IdCodigoProveedor);

        Assert.Empty(await BuscarAsync(c.Admin, "BAJA-1", c.IdProveedor));
    }

    /// <summary>Soft delete real del proveedor (dangling-fk-read-models): el código queda con su FK
    /// intacta pero el proveedor es invisible, así que no debe coincidir.</summary>
    [Fact]
    public async Task UnProveedorDadoDeBajaNoCoincidePorCodigo()
    {
        var c = await PrepararAsync(nameof(UnProveedorDadoDeBajaNoCoincidePorCodigo));
        using var _ = c.Admin;

        var articulo = await CrearArticuloAsync(c, "Clavo");
        await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "PROV-BAJA");
        Assert.Single(await BuscarAsync(c.Admin, "PROV-BAJA", c.IdProveedor));

        await EjecutarComoPlataformaAsync("UPDATE proveedores SET deleted_at = now() WHERE id_proveedor = $1", c.IdProveedor);

        Assert.Empty(await BuscarAsync(c.Admin, "PROV-BAJA", c.IdProveedor));
    }

    [Fact]
    public async Task UnArticuloDadoDeBajaNoSeListaPorSuCodigoDeProveedor()
    {
        var c = await PrepararAsync(nameof(UnArticuloDadoDeBajaNoSeListaPorSuCodigoDeProveedor));
        using var _ = c.Admin;

        var articulo = await CrearArticuloAsync(c, "Clavo");
        await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "ART-BAJA");

        var baja = await c.Admin.DeleteAsync($"/api/articulos/{articulo.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        Assert.Empty(await BuscarAsync(c.Admin, "ART-BAJA", c.IdProveedor));
    }

    [Fact]
    public async Task ElFiltroDeEmpresaSigueAplicandoALosCoincidentesPorCodigo()
    {
        var c = await PrepararAsync(nameof(ElFiltroDeEmpresaSigueAplicandoALosCoincidentesPorCodigo));
        using var _ = c.Admin;
        var idEmpresa = await SembrarEmpresaAsync(c.IdTenant, nameof(ElFiltroDeEmpresaSigueAplicandoALosCoincidentesPorCodigo));

        var restringido = await c.Admin.PostAsJsonAsync(
            "/api/articulos",
            Alta(c, "Restringido") with { DisponibleParaTodas = false, IdsEmpresas = [idEmpresa] });
        Assert.Equal(HttpStatusCode.Created, restringido.StatusCode);
        var articulo = (await restringido.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "EMP-1");

        var otraEmpresa = await SembrarEmpresaAsync(c.IdTenant, nameof(ElFiltroDeEmpresaSigueAplicandoALosCoincidentesPorCodigo) + "-2");

        var pagina = await c.Admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?busqueda=EMP-1&idProveedor={c.IdProveedor}&idEmpresa={otraEmpresa}", OpcionesJson);
        Assert.Empty(pagina!.Items);

        var conLaEmpresa = await c.Admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>(
            $"/api/articulos?busqueda=EMP-1&idProveedor={c.IdProveedor}&idEmpresa={idEmpresa}", OpcionesJson);
        Assert.Single(conLaEmpresa!.Items);
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

    // ---- alta de artículo con código ---------------------------------------------------------

    [Fact]
    public async Task ElAltaConCodigoProveedorLoPersisteParaElProveedorHabitual()
    {
        var c = await PrepararAsync(nameof(ElAltaConCodigoProveedorLoPersisteParaElProveedorHabitual));
        using var _ = c.Admin;

        var respuesta = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, "Clavo", "  FAC-77 ", c.IdProveedor));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var articulo = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT id_proveedor, codigo FROM codigos_proveedor WHERE id_articulo = $1 AND deleted_at IS NULL";
        comando.Parameters.Add(new NpgsqlParameter { Value = articulo.Id });
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        Assert.Equal(c.IdProveedor, lector.GetInt32(0));
        Assert.Equal("FAC-77", lector.GetString(1));
        Assert.False(await lector.ReadAsync());

        var encontrados = await BuscarAsync(c.Admin, "fac-77", c.IdProveedor);
        Assert.Equal(articulo.Id, Assert.Single(encontrados).Id);
    }

    [Fact]
    public async Task ElAltaConCodigoProveedorSinProveedorHabitualDevuelve400YNoCreaElArticulo()
    {
        var c = await PrepararAsync(nameof(ElAltaConCodigoProveedorSinProveedorHabitualDevuelve400YNoCreaElArticulo));
        using var _ = c.Admin;

        var respuesta = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, "Sin proveedor", "FAC-1"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proveedor_habitual_requerido", problema.GetProperty("codigo").GetString());
        Assert.Empty(await BuscarAsync(c.Admin, "Sin proveedor", idProveedor: null));
    }

    [Fact]
    public async Task ElAltaConCodigoProveedorEnBlancoSeTrataComoSinCodigo()
    {
        var c = await PrepararAsync(nameof(ElAltaConCodigoProveedorEnBlancoSeTrataComoSinCodigo));
        using var _ = c.Admin;

        var respuesta = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, "En blanco", "   "));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task ElAltaConCodigoProveedorMuyLargoDevuelve400()
    {
        var c = await PrepararAsync(nameof(ElAltaConCodigoProveedorMuyLargoDevuelve400));
        using var _ = c.Admin;

        var respuesta = await c.Admin.PostAsJsonAsync(
            "/api/articulos", Alta(c, "Largo", new string('x', 51), c.IdProveedor));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_proveedor_muy_largo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElAltaConUnCodigoYaAsignadoAOtroArticuloDevuelve409YNoCreaElArticulo()
    {
        var c = await PrepararAsync(nameof(ElAltaConUnCodigoYaAsignadoAOtroArticuloDevuelve409YNoCreaElArticulo));
        using var _ = c.Admin;

        var primero = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, "Primero", "DUP-1", c.IdProveedor));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        var segundo = await c.Admin.PostAsJsonAsync("/api/articulos", Alta(c, "Segundo", "dup-1", c.IdProveedor));

        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_proveedor_duplicado", problema.GetProperty("codigo").GetString());
        Assert.Empty(await BuscarAsync(c.Admin, "Segundo", idProveedor: null));
    }

    // ---- asociación posterior ----------------------------------------------------------------

    [Fact]
    public async Task AsociarUnCodigoNuevoDevuelve201ConElContratoEsperado()
    {
        var c = await PrepararAsync(nameof(AsociarUnCodigoNuevoDevuelve201ConElContratoEsperado));
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Clavo");

        var respuesta = await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "  NUEVO-1 ");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["codigo", "idArticulo", "idCodigoProveedor", "idProveedor"],
            cuerpo.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(articulo.Id, cuerpo.GetProperty("idArticulo").GetInt32());
        Assert.Equal(c.IdProveedor, cuerpo.GetProperty("idProveedor").GetInt32());
        Assert.Equal("NUEVO-1", cuerpo.GetProperty("codigo").GetString());
        Assert.True(cuerpo.GetProperty("idCodigoProveedor").GetInt32() > 0);
    }

    [Fact]
    public async Task AsociarElMismoCodigoAlMismoArticuloEsIdempotenteYDevuelve200()
    {
        var c = await PrepararAsync(nameof(AsociarElMismoCodigoAlMismoArticuloEsIdempotenteYDevuelve200));
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Clavo");

        var primera = await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "IDEM-1");
        var segunda = await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "idem-1");

        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        var uno = await primera.Content.ReadFromJsonAsync<CodigoProveedorListado>();
        var dos = await segunda.Content.ReadFromJsonAsync<CodigoProveedorListado>();
        Assert.Equal(uno!.IdCodigoProveedor, dos!.IdCodigoProveedor);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        await using var cuenta = cruda.CreateCommand();
        cuenta.CommandText = "SELECT count(*) FROM codigos_proveedor WHERE id_articulo = $1";
        cuenta.Parameters.Add(new NpgsqlParameter { Value = articulo.Id });
        Assert.Equal(1L, await cuenta.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnArticuloPuedeTenerVariosCodigosDelMismoProveedor()
    {
        var c = await PrepararAsync(nameof(UnArticuloPuedeTenerVariosCodigosDelMismoProveedor));
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Clavo");

        Assert.Equal(HttpStatusCode.Created, (await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "UNO")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "DOS")).StatusCode);
    }

    [Fact]
    public async Task AsociarUnCodigoYaAsignadoAOtroArticuloDelProveedorDevuelve409()
    {
        var c = await PrepararAsync(nameof(AsociarUnCodigoYaAsignadoAOtroArticuloDelProveedorDevuelve409));
        using var _ = c.Admin;
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");
        await AsociarAsync(c.Admin, uno.Id, c.IdProveedor, "CHOQUE");

        var respuesta = await AsociarAsync(c.Admin, dos.Id, c.IdProveedor, "CHOQUE");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_proveedor_duplicado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElMismoCodigoPuedeAsociarseAOtroArticuloBajoOtroProveedor()
    {
        var c = await PrepararAsync(nameof(ElMismoCodigoPuedeAsociarseAOtroArticuloBajoOtroProveedor));
        using var _ = c.Admin;
        var otroProveedor = await SembrarProveedorAsync(c.IdTenant, nameof(ElMismoCodigoPuedeAsociarseAOtroArticuloBajoOtroProveedor) + "-otro");
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");
        await AsociarAsync(c.Admin, uno.Id, c.IdProveedor, "COMUN");

        var respuesta = await AsociarAsync(c.Admin, dos.Id, otroProveedor, "COMUN");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnCodigoDadoDeBajaSePuedeVolverAAsociar()
    {
        var c = await PrepararAsync(nameof(UnCodigoDadoDeBajaSePuedeVolverAAsociar));
        using var _ = c.Admin;
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");
        var original = await (await AsociarAsync(c.Admin, uno.Id, c.IdProveedor, "REUSO"))
            .Content.ReadFromJsonAsync<CodigoProveedorListado>();
        await EjecutarComoPlataformaAsync(
            "UPDATE codigos_proveedor SET deleted_at = now() WHERE id_codigo_proveedor = $1", original!.IdCodigoProveedor);

        var respuesta = await AsociarAsync(c.Admin, dos.Id, c.IdProveedor, "REUSO");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task AsociarAUnArticuloOProveedorInexistenteDevuelve404()
    {
        var c = await PrepararAsync(nameof(AsociarAUnArticuloOProveedorInexistenteDevuelve404));
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Clavo");

        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(c.Admin, 999_999, c.IdProveedor, "X")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(c.Admin, articulo.Id, 999_999, "X")).StatusCode);
    }

    [Fact]
    public async Task AsociarAUnArticuloOProveedorDadoDeBajaDevuelve404()
    {
        var c = await PrepararAsync(nameof(AsociarAUnArticuloOProveedorDadoDeBajaDevuelve404));
        using var _ = c.Admin;
        var articuloDeBaja = await CrearArticuloAsync(c, "De baja");
        var articuloVivo = await CrearArticuloAsync(c, "Vivo");
        var proveedorDeBaja = await SembrarProveedorAsync(c.IdTenant, nameof(AsociarAUnArticuloOProveedorDadoDeBajaDevuelve404) + "-baja");

        Assert.Equal(HttpStatusCode.NoContent, (await c.Admin.DeleteAsync($"/api/articulos/{articuloDeBaja.Id}")).StatusCode);
        await EjecutarComoPlataformaAsync("UPDATE proveedores SET deleted_at = now() WHERE id_proveedor = $1", proveedorDeBaja);

        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(c.Admin, articuloDeBaja.Id, c.IdProveedor, "X")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(c.Admin, articuloVivo.Id, proveedorDeBaja, "X")).StatusCode);
    }

    [Fact]
    public async Task AsociarConArticuloOProveedorDeOtroTenantDevuelve404()
    {
        var a = await PrepararAsync(nameof(AsociarConArticuloOProveedorDeOtroTenantDevuelve404) + "-A");
        var b = await PrepararAsync(nameof(AsociarConArticuloOProveedorDeOtroTenantDevuelve404) + "-B");
        using var _a = a.Admin;
        using var _b = b.Admin;
        var articuloDeA = await CrearArticuloAsync(a, "De A");
        var articuloDeB = await CrearArticuloAsync(b, "De B");

        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(b.Admin, articuloDeA.Id, b.IdProveedor, "X")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsociarAsync(b.Admin, articuloDeB.Id, a.IdProveedor, "X")).StatusCode);
    }

    [Fact]
    public async Task AsociarConUnPayloadInvalidoDevuelve400()
    {
        var c = await PrepararAsync(nameof(AsociarConUnPayloadInvalidoDevuelve400));
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Clavo");

        var sinCodigo = await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, "   ");
        Assert.Equal(HttpStatusCode.BadRequest, sinCodigo.StatusCode);
        Assert.Equal(
            "codigo_requerido",
            (await sinCodigo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        var largo = await AsociarAsync(c.Admin, articulo.Id, c.IdProveedor, new string('x', 51));
        Assert.Equal(HttpStatusCode.BadRequest, largo.StatusCode);
        Assert.Equal(
            "codigo_muy_largo",
            (await largo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        var sinProveedor = await AsociarAsync(c.Admin, articulo.Id, 0, "X");
        Assert.Equal(HttpStatusCode.BadRequest, sinProveedor.StatusCode);
        Assert.Equal(
            "id_proveedor_requerido",
            (await sinProveedor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnVendedorNoPuedeAsociarCodigosDeProveedor()
    {
        var (idTenant, idArea, idAlicuotaIva, mail, password) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeAsociarCodigosDeProveedor));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idProveedor = await SembrarProveedorAsync(idTenant, nameof(UnVendedorNoPuedeAsociarCodigosDeProveedor));
        var articulo = await CrearArticuloAsync(new Contexto(idTenant, idArea, idAlicuotaIva, idProveedor, admin), "Clavo");

        var hasheador = new HasheadorPbkdf2();
        var mailVendedor = $"{nameof(UnVendedorNoPuedeAsociarCodigosDeProveedor).ToLowerInvariant()}-vendedor@ways.test";
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = "vendedor",
                Mail = mailVendedor,
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordVendedor),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);
        var respuesta = await AsociarAsync(vendedor, articulo.Id, idProveedor, "X");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- backstop del índice único -----------------------------------------------------------

    /// <summary>Carrera real: un INSERT crudo sin commitear (invisible al pre-chequeo) bloquea el
    /// índice; la asociación HTTP pasa el pre-chequeo, espera, y al comitear el otro INSERT choca
    /// con 23505, que <c>ManejadorDeErrores</c> tiene que traducir a 409 de negocio y no a 500.</summary>
    [Fact]
    public async Task ElBackstopTraduceElChoqueDelIndiceUnicoA409()
    {
        var c = await PrepararAsync(nameof(ElBackstopTraduceElChoqueDelIndiceUnicoA409));
        using var _ = c.Admin;
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        await using var transaccion = await cruda.BeginTransactionAsync();
        await InsertarCodigoCrudoAsync(cruda, c.IdTenant, uno.Id, c.IdProveedor, "CARRERA-1");

        var asociacion = AsociarAsync(c.Admin, dos.Id, c.IdProveedor, "CARRERA-1");
        await Task.Delay(1000);
        Assert.False(asociacion.IsCompleted, "la asociación debía quedar esperando el índice único");

        await transaccion.CommitAsync();
        var respuesta = await asociacion;

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_proveedor_duplicado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task DosAsociacionesConcurrentesDelMismoCodigoDanExactamenteUnGanador()
    {
        var c = await PrepararAsync(nameof(DosAsociacionesConcurrentesDelMismoCodigoDanExactamenteUnGanador));
        using var _ = c.Admin;
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");

        var respuestas = await Task.WhenAll(
            AsociarAsync(c.Admin, uno.Id, c.IdProveedor, "RACE-1"),
            AsociarAsync(c.Admin, dos.Id, c.IdProveedor, "RACE-1"));

        var estados = respuestas.Select(r => r.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);
        var conflicto = await respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict)
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_proveedor_duplicado", conflicto.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnInsertCrudoDuplicadoViolaElIndiceUnicoParcial()
    {
        var c = await PrepararAsync(nameof(UnInsertCrudoDuplicadoViolaElIndiceUnicoParcial));
        using var _ = c.Admin;
        var uno = await CrearArticuloAsync(c, "Uno");
        var dos = await CrearArticuloAsync(c, "Dos");
        var otroProveedor = await SembrarProveedorAsync(c.IdTenant, nameof(UnInsertCrudoDuplicadoViolaElIndiceUnicoParcial) + "-otro");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        await InsertarCodigoCrudoAsync(cruda, c.IdTenant, uno.Id, c.IdProveedor, "Raw-1");

        // Mismo código con otras mayúsculas: citext lo trata como igual.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCodigoCrudoAsync(cruda, c.IdTenant, dos.Id, c.IdProveedor, "RAW-1"));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_codigos_proveedor_proveedor_codigo", excepcion.ConstraintName);

        // El predicado parcial y la clave por proveedor dejan pasar estos dos.
        await InsertarCodigoCrudoAsync(cruda, c.IdTenant, dos.Id, otroProveedor, "RAW-1");
        await using var baja = cruda.CreateCommand();
        baja.CommandText = "UPDATE codigos_proveedor SET deleted_at = now() WHERE id_proveedor = $1 AND id_articulo = $2";
        baja.Parameters.Add(new NpgsqlParameter { Value = c.IdProveedor });
        baja.Parameters.Add(new NpgsqlParameter { Value = uno.Id });
        Assert.Equal(1, await baja.ExecuteNonQueryAsync());
        await InsertarCodigoCrudoAsync(cruda, c.IdTenant, dos.Id, c.IdProveedor, "RAW-1");
    }

    [Theory]
    [InlineData(" X", "inicio")]
    [InlineData("X ", "fin")]
    [InlineData("", "vacio")]
    public async Task UnCodigoSinRecortarOVacioViolaElCheckDeNormalizacion(string codigo, string caso)
    {
        var c = await PrepararAsync(nameof(UnCodigoSinRecortarOVacioViolaElCheckDeNormalizacion) + caso);
        using var _ = c.Admin;
        var articulo = await CrearArticuloAsync(c, "Uno");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", c.IdTenant);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCodigoCrudoAsync(cruda, c.IdTenant, articulo.Id, c.IdProveedor, codigo));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_codigos_proveedor_codigo_normalizado", excepcion.ConstraintName);
    }

    // ---- RLS ---------------------------------------------------------------------------------

    private async Task<(Contexto A, int IdTenantB, int IdCodigo)> SembrarCodigoDeAAsync(string nombre)
    {
        var a = await PrepararAsync(nombre + "-A");
        var articulo = await CrearArticuloAsync(a, "Clavo");
        var codigo = await (await AsociarAsync(a.Admin, articulo.Id, a.IdProveedor, "RLS-1"))
            .Content.ReadFromJsonAsync<CodigoProveedorListado>();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var tenantB = new Tenant { Nombre = nombre + "-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        return (a, tenantB.Id, codigo!.IdCodigoProveedor);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeNiActualizaElCodigoDeProveedor()
    {
        var (a, idTenantB, idCodigo) = await SembrarCodigoDeAAsync(nameof(UnaSesionDeOtroTenantNoVeNiActualizaElCodigoDeProveedor));
        using var _ = a.Admin;

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var seleccion = cruda.CreateCommand();
        seleccion.CommandText = "SELECT count(*) FROM codigos_proveedor WHERE id_codigo_proveedor = $1";
        seleccion.Parameters.Add(new NpgsqlParameter { Value = idCodigo });
        Assert.Equal(0L, await seleccion.ExecuteScalarAsync());

        await using var actualizacion = cruda.CreateCommand();
        actualizacion.CommandText = "UPDATE codigos_proveedor SET updated_at = now() WHERE id_codigo_proveedor = $1";
        actualizacion.Parameters.Add(new NpgsqlParameter { Value = idCodigo });
        Assert.Equal(0, await actualizacion.ExecuteNonQueryAsync());

        await using var propia = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var visible = propia.CreateCommand();
        visible.CommandText = "SELECT count(*) FROM codigos_proveedor WHERE id_codigo_proveedor = $1";
        visible.Parameters.Add(new NpgsqlParameter { Value = idCodigo });
        Assert.Equal(1L, await visible.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechazaPorRls()
    {
        var (a, idTenantB, _) = await SembrarCodigoDeAAsync(nameof(UnInsertConIdTenantAjenoSeRechazaPorRls));
        using var _ = a.Admin;
        var articulo = await CrearArticuloAsync(a, "Otro");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCodigoCrudoAsync(cruda, idTenantB, articulo.Id, a.IdProveedor, "INTRUSO"));

        Assert.Equal("42501", excepcion.SqlState);
    }
}
