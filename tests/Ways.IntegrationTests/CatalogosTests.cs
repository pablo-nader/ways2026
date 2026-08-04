using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Catalogos;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3 (tasks 3.19): CRUD de los catálogos de tenant a través de la ruta compartida
/// (ADR-11 — la máquina genérica se ejerce una vez por catálogo, no cinco veces a mano),
/// aislamiento cross-tenant, la regla de profundidad de <c>categorias</c> a través del
/// servicio real (CTE contra Postgres, no la regla pura), y los catálogos fiscales
/// (solo lectura, gate #4). Corre contra Postgres real, migraciones 1-5 aplicadas.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";

    // El servidor serializa enums como texto (Program.cs, JsonStringEnumConverter) — el
    // HttpClient de prueba, a diferencia del servidor, no hereda esa configuración: hay que
    // repetirla acá para los DTOs que llevan un enum (TipoComprobanteListado.Clase).
    // PropertyNameCaseInsensitive = true es igual de necesario y faltaba (etapa 4B, batch 11):
    // un `new JsonSerializerOptions()` no lo trae en true por default, a diferencia del
    // `ReadFromJsonAsync<T>()` sin opciones que sí usa este archivo para el resto de los DTOs
    // (sin enums) — sin esto, cada propiedad de TipoComprobanteListado quedaba en su default
    // (`Clase` = `Venta`, el primer valor del enum; `Nombre`/`Codigo` = `null`) en vez de
    // tirar un error, invisible acá porque `LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant`
    // solo hacía `Assert.NotEmpty(tipos!)` (cuenta de la lista, no contenido de cada fila).
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdTenant, string Mail)> SembrarTenantConAdminAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (tenant.Id, mail);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));
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
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    [Fact]
    public async Task CrudCompletoDeUnCatalogoDeTenantAtravesDeLaRutaCompartida()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(CrudCompletoDeUnCatalogoDeTenantAtravesDeLaRutaCompartida));
        using var cliente = await ClienteLogueadoAsync(mail);

        var alta = new AreaAlta("Almacén", IdEmpresa: null, Orden: 1);
        var creacion = await cliente.PostAsJsonAsync("/api/catalogos/areas", alta);
        Assert.Equal(HttpStatusCode.Created, creacion.StatusCode);
        var creada = await creacion.Content.ReadFromJsonAsync<AreaListado>();
        Assert.NotNull(creada);
        Assert.Equal("Almacén", creada!.Nombre);

        var obtenida = await cliente.GetFromJsonAsync<AreaListado>($"/api/catalogos/areas/{creada.Id}");
        Assert.Equal(creada.Id, obtenida!.Id);

        var listado = await cliente.GetFromJsonAsync<List<AreaListado>>("/api/catalogos/areas");
        Assert.Contains(listado!, a => a.Id == creada.Id);

        var edicion = await cliente.PutAsJsonAsync(
            $"/api/catalogos/areas/{creada.Id}", alta with { Nombre = "Almacén General", Orden = 2 });
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var editada = await edicion.Content.ReadFromJsonAsync<AreaListado>();
        Assert.Equal("Almacén General", editada!.Nombre);

        var baja = await cliente.DeleteAsync($"/api/catalogos/areas/{creada.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var listadoTrasBaja = await cliente.GetFromJsonAsync<List<AreaListado>>("/api/catalogos/areas");
        Assert.DoesNotContain(listadoTrasBaja!, a => a.Id == creada.Id);
    }

    [Fact]
    public async Task UnIdDeCatalogoDeOtroTenantDevuelve404()
    {
        var (_, mailA) = await SembrarTenantConAdminAsync(nameof(UnIdDeCatalogoDeOtroTenantDevuelve404) + "A");
        var (_, mailB) = await SembrarTenantConAdminAsync(nameof(UnIdDeCatalogoDeOtroTenantDevuelve404) + "B");

        using var clienteA = await ClienteLogueadoAsync(mailA);
        var creacion = await clienteA.PostAsJsonAsync(
            "/api/catalogos/marcas", new MarcaAlta("Marca de A", IdEmpresa: null));
        var creada = await creacion.Content.ReadFromJsonAsync<MarcaListado>();

        using var clienteB = await ClienteLogueadoAsync(mailB);
        var respuesta = await clienteB.GetAsync($"/api/catalogos/marcas/{creada!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaCategoriaEnElCuartoNivelDevuelve400()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(UnaCategoriaEnElCuartoNivelDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        var nivel1 = await CrearCategoriaAsync(cliente, "Bebidas", null);
        var nivel2 = await CrearCategoriaAsync(cliente, "Gaseosas", nivel1.Id);
        var nivel3 = await CrearCategoriaAsync(cliente, "Cola", nivel2.Id);

        var intentoNivel4 = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta("Cola 500ml", null, 1, nivel3.Id));

        Assert.Equal(HttpStatusCode.BadRequest, intentoNivel4.StatusCode);
    }

    private static async Task<CategoriaListado> CrearCategoriaAsync(HttpClient cliente, string nombre, int? idPadre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta(nombre, null, 1, idPadre));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CategoriaListado>())!;
    }

    [Fact]
    public async Task UnPutDeCategoriaConSuPropioIdComoPadreDevuelve400()
    {
        // CRITICAL de judgment-day (slice 3, ronda 1): "descendientes" nunca incluye al
        // propio nodo, así que sin el chequeo explícito de ReglaDeCategorias.ValidarSinCiclo
        // este PUT pasaba la validación y dejaba un ciclo de longitud 1 persistido — el
        // próximo WITH RECURSIVE sobre esa fila entraba en loop infinito.
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(UnPutDeCategoriaConSuPropioIdComoPadreDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        var categoria = await CrearCategoriaAsync(cliente, "Bebidas", null);

        var intento = await cliente.PutAsJsonAsync(
            $"/api/catalogos/categorias/{categoria.Id}",
            new CategoriaAlta("Bebidas", null, 1, categoria.Id));

        Assert.Equal(HttpStatusCode.BadRequest, intento.StatusCode);
    }

    [Fact]
    public async Task UnIntentoDirectoPorSqlDeAutoPadreViolaLaCheckConstraint()
    {
        // Defensa en profundidad del mismo bug de arriba: la constraint de esquema
        // (ck_categorias_padre_no_self) cierra la misma puerta para una fila escrita por
        // fuera del servicio (SQL directo), no solo para el PUT de la API.
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nameof(UnIntentoDirectoPorSqlDeAutoPadreViolaLaCheckConstraint),
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var categoria = new Categoria
        {
            IdTenant = tenant.Id, Nombre = "Bebidas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE categorias SET id_categoria_padre = id_categoria WHERE id_categoria = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = categoria.Id });

        // 23514 = check_violation.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
    }

    [Fact]
    public async Task CrearUnaCategoriaBajoUnPadreDadoDeBajaDevuelve400()
    {
        // El padre existente con hijos previos a la baja queda como está (comportamiento
        // actual, documentado acá, no cambia): lo que se rechaza es un alta NUEVA que
        // intente colgarse de un id ya dado de baja.
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(CrearUnaCategoriaBajoUnPadreDadoDeBajaDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        var padre = await CrearCategoriaAsync(cliente, "Bebidas", null);
        await CrearCategoriaAsync(cliente, "Gaseosas", padre.Id);

        var baja = await cliente.DeleteAsync($"/api/catalogos/categorias/{padre.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var intento = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta("Cola", null, 1, padre.Id));

        Assert.Equal(HttpStatusCode.BadRequest, intento.StatusCode);
    }

    [Fact]
    public async Task DosAltasConcurrentesConElMismoNombreEnElMismoAlcanceDisparanElBackstopDelSaveChanges()
    {
        // Mismo mecanismo que el análogo de UsuariosYLoginTests, generalizado por
        // ManejadorDeErrores.ClasificarUnicidad (judgment-day, slice 3 ronda 1): dos altas a
        // la vez, mismo nombre y mismo alcance (id_empresa NULL, compartido) — las dos pasan
        // el chequeo previo en memoria antes de que cualquiera haga commit, así que el 23505
        // de ux_areas_nombre_compartido recién aparece en el SaveChangesAsync de la que
        // pierde la carrera. El código de negocio es el mismo (nombre_duplicado) sin importar
        // qué camino lo atrapó — por diseño, para que el cliente vea siempre el mismo 409.
        var (_, mail) = await SembrarTenantConAdminAsync(
            nameof(DosAltasConcurrentesConElMismoNombreEnElMismoAlcanceDisparanElBackstopDelSaveChanges));
        using var cliente = await ClienteLogueadoAsync(mail);

        const string nombreCompartido = "Almacén concurrente";

        var tareaA = cliente.PostAsJsonAsync("/api/catalogos/areas", new AreaAlta(nombreCompartido, null, 1));
        var tareaB = cliente.PostAsJsonAsync("/api/catalogos/areas", new AreaAlta(nombreCompartido, null, 1));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nombre_duplicado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaEmpresaInexistenteEnUnCatalogoDevuelve400NoUnError500()
    {
        // Judgment-day (slice 3, ronda 1): ManejadorDeErrores ahora traduce cualquier 23503
        // de una FK "fk_*" a 400 referencia_invalida en vez de dejar pasar un 500 genérico.
        // A diferencia de IdCategoriaPadre (que ExistePadreAsync ahora rechaza antes, con un
        // 400 de dominio más específico, sin llegar nunca al SaveChangesAsync), IdEmpresa en
        // los catálogos simples no tiene un chequeo previo — este es el camino que sí llega
        // hasta el backstop de FK (fk_areas_empresa).
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(UnaEmpresaInexistenteEnUnCatalogoDevuelve400NoUnError500));
        using var cliente = await ClienteLogueadoAsync(mail);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/areas", new AreaAlta("Almacén ajeno", IdEmpresa: 999999, Orden: 1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>db-error-backstops (judgment-day ronda 1, Slice 3): <c>ServicioDeGrupos</c> no
    /// tiene un pre-chequeo de rango para <c>Margen</c> (a diferencia de
    /// <c>ServicioDeProveedores.ExigirMargenValido</c>) — este es el camino HTTP más barato
    /// para llegar de verdad al mapeo genérico 22003 → 400 <c>valor_fuera_de_rango</c> de
    /// <c>ManejadorDeErrores</c>, sin ningún chequeo de aplicación de por medio.</summary>
    [Fact]
    public async Task CrearUnGrupoConMargenQueDesbordaNumericDevuelve400ViaElBackstopDe22003()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(
            nameof(CrearUnGrupoConMargenQueDesbordaNumericDevuelve400ViaElBackstopDe22003));
        using var cliente = await ClienteLogueadoAsync(mail);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/grupos", new GrupoAlta("Desbordado", IdEmpresa: null, Margen: 9999.99m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("valor_fuera_de_rango", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaSesionDeRootRecibe403EnUnCatalogoDeTenant()
    {
        // Judgment-day (slice 3, ronda 1): GestionDeCatalogo dejó de incluir Root — "root
        // administra tenants, no opera ninguno" (doc 09/design.md), mismo criterio que
        // SoloPlataforma en espejo. El seed por defecto de InicializadorDeBaseDeDatos ya crea
        // el root (doc 08), no hace falta sembrar nada acá.
        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/areas", new AreaAlta("Intrusa", IdEmpresa: null, Orden: 1));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant()
    {
        var (_, mail) = await SembrarTenantConAdminAsync(nameof(LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant));
        using var cliente = await ClienteLogueadoAsync(mail);

        var lectura = await cliente.GetAsync("/api/catalogos-fiscales/condiciones-fiscales");
        Assert.Equal(HttpStatusCode.OK, lectura.StatusCode);
        var condiciones = await lectura.Content.ReadFromJsonAsync<List<CondicionFiscalListado>>();
        Assert.NotEmpty(condiciones!);

        var alicuotas = await cliente.GetFromJsonAsync<List<AlicuotaIvaListado>>("/api/catalogos-fiscales/alicuotas-iva");
        Assert.NotEmpty(alicuotas!);

        var tipos = await cliente.GetFromJsonAsync<List<TipoComprobanteListado>>(
            "/api/catalogos-fiscales/tipos-comprobante", OpcionesJson);
        Assert.NotEmpty(tipos!);

        // ADR-11 (override de gate #4, ver design.md): no hay ningún POST/PUT/DELETE mapeado
        // para los catálogos fiscales — a propósito, no por un 403 de policy. La ausencia de
        // ruta es la superficie de API; RLS (HabilitarRlsDeCatalogoGlobal) es la segunda capa
        // detrás. Por eso este caso devuelve 404 (ruta inexistente), no 403.
        var intentoDeEscritura = await cliente.PostAsJsonAsync(
            "/api/catalogos-fiscales/condiciones-fiscales", new { });
        Assert.Equal(HttpStatusCode.NotFound, intentoDeEscritura.StatusCode);
    }

    /// <summary>CRITICAL (judgment-day, stage-5-pos-ventas Slice 1): el grupo
    /// <c>/api/catalogos-fiscales</c> no apilaba <see cref="Politicas.OperacionDePos"/> y caía
    /// al fallback autenticado-only — un Vendedor igual podía leer (por accidente, no por
    /// policy). Esta prueba fija el comportamiento POSITIVO ahora que la policy está explícita:
    /// un Vendedor lee.</summary>
    [Fact]
    public async Task UnVendedorPuedeLeerCatalogosFiscales()
    {
        var (idTenant, _) = await SembrarTenantConAdminAsync(nameof(UnVendedorPuedeLeerCatalogosFiscales));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorPuedeLeerCatalogosFiscales));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor);

        var respuesta = await vendedor.GetAsync("/api/catalogos-fiscales/condiciones-fiscales");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Companion del fix de arriba: mismo criterio que
    /// <see cref="UnaSesionDeRootRecibe403EnUnCatalogoDeTenant"/> — root queda afuera de
    /// <see cref="Politicas.OperacionDePos"/> ("root administra tenants, no opera ninguno").</summary>
    [Fact]
    public async Task UnaSesionDeRootRecibe403AlLeerCatalogosFiscales()
    {
        using var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await cliente.GetAsync("/api/catalogos-fiscales/condiciones-fiscales");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
