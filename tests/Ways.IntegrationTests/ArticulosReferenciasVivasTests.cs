using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// fix/articulos-lock-referencias: cierra, del lado de los escritores de artículos, el RESIDUAL
/// documentado en <c>GuardaDeReferencias</c> (fix/bajas-catalogos-guarda-de-uso) — un escritor
/// cuyo pre-chequeo de existencia (filtrado, sin lock) corrió ANTES del commit de una baja de
/// catálogo, y cuyo INSERT/UPDATE llega DESPUÉS de que esa baja tomó su <c>FOR UPDATE</c>, podía
/// comitear una referencia a la fila recién dada de baja. <c>ServicioDeArticulos.CrearAsync</c>/
/// <c>ActualizarAsync</c> reemplazan ese pre-chequeo por
/// <c>GuardaDeReferencias.BloquearSiEstaVivaAsync</c> (<c>FOR KEY SHARE ... WHERE deleted_at IS
/// NULL</c>) dentro de su propia transacción, para las 5 referencias mutables de
/// <c>articulos</c>: área, categoría, marca, grupo, proveedor habitual.
///
/// Reusa el patrón de siembra/rendezvous de <c>BajasDeCatalogosTests</c> (C7a/C7b: raw ADO +
/// <c>pg_stat_activity</c>, bounded poll sin sleep) y el interceptor de
/// <c>EscriturasSinReintentoTests</c> para el residual de commit ambiguo (cubierto ahí, sitio
/// "articulos (edición)").
///
/// Por qué la prueba SECUENCIAL (una referencia ya dada de baja antes de que el escritor arranque)
/// no alcanza por sí sola para probar el lock (mutation-proof-tests regla 3: un guard mirado por
/// un pre-chequeo previo puede sobrevivir a la mutación de su clave si el pre-chequeo YA
/// rechazaba el mismo caso): el query filter <c>BajaLogica</c> de EF ya escondía una fila borrada
/// del <c>AnyAsync</c> de antes, así que la prueba secuencial por sí sola solo prueba que el
/// CALL SITE de cada chequeo sigue ahí (mutación: borrarlo entero). Las dos pruebas de CARRERA
/// (creación/edición) son las que prueban el lock en sí: sin él, o sin el <c>deleted_at IS
/// NULL</c> de su WHERE, la escritura del artículo comitearía una referencia colgante.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosReferenciasVivasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public enum Referencia { Area, Categoria, Marca, Grupo, ProveedorHabitual }

    public static TheoryData<Referencia, bool> ReferenciasPorOperacion()
    {
        var data = new TheoryData<Referencia, bool>();
        foreach (var referencia in new[]
        {
            Referencia.Area, Referencia.Categoria, Referencia.Marca, Referencia.Grupo, Referencia.ProveedorHabitual
        })
        {
            data.Add(referencia, false); // POST (crear)
            data.Add(referencia, true); // PUT (editar)
        }

        return data;
    }

    private sealed record Sembrado(int IdTenant, int IdArea, string MailAdmin, string PasswordAdmin);

    // ---- siembra de tenant -----------------------------------------------------------------------

    private async Task<Sembrado> AprovisionarAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}";
        var mailAdmin = $"{unico}@ways.test";

        using var root = fixture.CreateClient();
        var login = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SRL", $"{unico} - Local 1", mailAdmin));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        using var admin = await ClienteAdminAsync(mailAdmin, resultado!.PasswordTemporal);
        var area = await CrearAreaAsync(admin, "Área base");

        return new Sembrado(resultado.IdTenant, area.Id, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteAdminAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private WaysDbContext ContextoDelTenant(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    // ---- catálogos vía API (mismos payloads que BajasDeCatalogosTests) ---------------------------

    private static async Task<AreaListado> CrearAreaAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/catalogos/areas", new AreaAlta(nombre, null, 1));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<AreaListado>())!;
    }

    private static async Task<CategoriaListado> CrearCategoriaAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta(nombre, null, 1, null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CategoriaListado>())!;
    }

    private static async Task<MarcaListado> CrearMarcaAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/catalogos/marcas", new MarcaAlta(nombre, null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<MarcaListado>())!;
    }

    private static async Task<GrupoListado> CrearGrupoAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/grupos", new GrupoAlta(nombre, null, Margen: null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<GrupoListado>())!;
    }

    private async Task<ProveedorListado> CrearProveedorAsync(HttpClient cliente, string nombre)
    {
        var idCondicionFiscal = await IdDeCondicionFiscalAsync();
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/proveedores",
            new AltaProveedor(
                RazonSocial: nombre, NombreFantasia: null, Cuit: null, IdCondicionFiscal: idCondicionFiscal,
                Domicilio: null, Telefono: null, Email: null, Vendedor: null, CelularVendedor: null,
                Supervisor: null, CelularSupervisor: null, Margen: null, Observaciones: null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ProveedorListado>())!;
    }

    private async Task<int> IdDeCondicionFiscalAsync()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
    }

    private async Task<int> IdDeAlicuotaIvaAsync()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
    }

    // ---- artículos vía API -------------------------------------------------------------------------

    private static async Task<ArticuloListado> CrearArticuloBaseAsync(HttpClient admin, int idArea, int idAlicuotaIva)
    {
        var alta = new AltaArticulo(
            CodigoInterno: null, Nombre: $"Artículo base {Guid.NewGuid():N}", Descripcion: null, IdArea: idArea,
            IdCategoria: null, IdMarca: null, IdGrupo: null, IdProveedorHabitual: null, IdAlicuotaIva: idAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null, EsProducto: true, CostoLista: null,
            DescuentoProveedor: null, CostoNominal: null);

        var respuesta = await admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
    }

    private async Task<int> ContarArticulosAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Articulos.IgnoreQueryFilters()
            .CountAsync(a => a.IdTenant == idTenant && a.Nombre == nombre);
    }

    // ---- referencia viva / dada de baja, por tipo --------------------------------------------------

    private async Task<int> CrearReferenciaVivaAsync(HttpClient admin, Referencia referencia) => referencia switch
    {
        Referencia.Area => (await CrearAreaAsync(admin, $"Área {Guid.NewGuid():N}")).Id,
        Referencia.Categoria => (await CrearCategoriaAsync(admin, $"Categoría {Guid.NewGuid():N}")).Id,
        Referencia.Marca => (await CrearMarcaAsync(admin, $"Marca {Guid.NewGuid():N}")).Id,
        Referencia.Grupo => (await CrearGrupoAsync(admin, $"Grupo {Guid.NewGuid():N}")).Id,
        Referencia.ProveedorHabitual => (await CrearProveedorAsync(admin, $"Proveedor {Guid.NewGuid():N}")).Id,
        _ => throw new ArgumentOutOfRangeException(nameof(referencia))
    };

    /// <summary>Estampa <c>deleted_at</c> directamente (bypassea <c>GuardaDeReferencias</c>: el
    /// endpoint DELETE rechazaría un catálogo referenciado) — mismo criterio que
    /// <c>BajasDeCatalogosTests.DarDeBajaDirectamenteAsync</c>, acá sobre el catálogo en vez del
    /// artículo.</summary>
    private async Task<int> CrearReferenciaSoftDeletedAsync(HttpClient admin, int idTenant, Referencia referencia)
    {
        var id = await CrearReferenciaVivaAsync(admin, referencia);

        await using var db = ContextoDelTenant(idTenant);
        var ahora = DateTimeOffset.UtcNow;

        switch (referencia)
        {
            case Referencia.Area:
                (await db.Areas.SingleAsync(a => a.Id == id)).DeletedAt = ahora;
                break;
            case Referencia.Categoria:
                (await db.Categorias.SingleAsync(c => c.Id == id)).DeletedAt = ahora;
                break;
            case Referencia.Marca:
                (await db.Marcas.SingleAsync(m => m.Id == id)).DeletedAt = ahora;
                break;
            case Referencia.Grupo:
                (await db.Grupos.SingleAsync(g => g.Id == id)).DeletedAt = ahora;
                break;
            case Referencia.ProveedorHabitual:
                (await db.Proveedores.SingleAsync(p => p.Id == id)).DeletedAt = ahora;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(referencia));
        }

        await db.SaveChangesAsync();
        return id;
    }

    // ---- payloads de artículo, con una referencia puntual ------------------------------------------

    private static AltaArticulo AltaConReferencia(int idArea, int idAlicuotaIva, Referencia referencia, int idReferencia, string nombre) => new(
        CodigoInterno: null,
        Nombre: nombre,
        Descripcion: null,
        IdArea: referencia == Referencia.Area ? idReferencia : idArea,
        IdCategoria: referencia == Referencia.Categoria ? idReferencia : null,
        IdMarca: referencia == Referencia.Marca ? idReferencia : null,
        IdGrupo: referencia == Referencia.Grupo ? idReferencia : null,
        IdProveedorHabitual: referencia == Referencia.ProveedorHabitual ? idReferencia : null,
        IdAlicuotaIva: idAlicuotaIva,
        UnidadVenta: UnidadVenta.Unidad,
        UnidadesPorBulto: null,
        EsProducto: true,
        CostoLista: null,
        DescuentoProveedor: null,
        CostoNominal: null);

    private static EdicionArticulo EdicionConReferencia(ArticuloListado actual, Referencia referencia, int idReferencia) => new(
        Nombre: actual.Nombre,
        Descripcion: actual.Descripcion,
        IdArea: referencia == Referencia.Area ? idReferencia : actual.IdArea,
        IdCategoria: referencia == Referencia.Categoria ? idReferencia : actual.IdCategoria,
        IdMarca: referencia == Referencia.Marca ? idReferencia : actual.IdMarca,
        IdGrupo: referencia == Referencia.Grupo ? idReferencia : actual.IdGrupo,
        IdProveedorHabitual: referencia == Referencia.ProveedorHabitual ? idReferencia : actual.IdProveedorHabitual,
        IdAlicuotaIva: actual.IdAlicuotaIva,
        UnidadVenta: actual.UnidadVenta,
        UnidadesPorBulto: actual.UnidadesPorBulto,
        EsProducto: actual.EsProducto,
        CostoLista: actual.CostoLista,
        DescuentoProveedor: actual.DescuentoProveedor,
        CostoNominal: actual.CostoNominal,
        DisponibleParaTodas: actual.DisponibleParaTodas,
        IdsEmpresas: null,
        Activo: actual.Activo,
        ControlaLote: actual.ControlaLote);

    // =================================================================================================
    // 1. Secuencial: una referencia YA dada de baja antes de que el escritor arranque — 400
    // referencia_invalida, nada persistido/cambiado. Mutación: borrar el call site del chequeo
    // correspondiente (área/categoría/marca/grupo/proveedor, en Crear o en Actualizar) → ese caso
    // (y solo ese) se pone en rojo. Ver el doc-comment de la clase para por qué esto NO prueba el
    // lock por sí solo — eso lo prueban las dos pruebas de carrera de abajo.
    // =================================================================================================

    [Theory]
    [MemberData(nameof(ReferenciasPorOperacion))]
    public async Task UnaReferenciaSoftDeletedEsRechazada(Referencia referencia, bool esEdicion)
    {
        var s = await AprovisionarAsync($"referencia-soft-deleted-{referencia}-{(esEdicion ? "editar" : "crear")}".ToLowerInvariant());
        using var admin = await ClienteAdminAsync(s.MailAdmin, s.PasswordAdmin);
        var idAlicuota = await IdDeAlicuotaIvaAsync();

        var idReferencia = await CrearReferenciaSoftDeletedAsync(admin, s.IdTenant, referencia);

        if (!esEdicion)
        {
            var nombre = $"Artículo {Guid.NewGuid():N}";
            var alta = AltaConReferencia(s.IdArea, idAlicuota, referencia, idReferencia, nombre);

            var respuesta = await admin.PostAsJsonAsync("/api/articulos", alta);

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());

            Assert.Equal(0, await ContarArticulosAsync(s.IdTenant, nombre));
            return;
        }

        var creado = await CrearArticuloBaseAsync(admin, s.IdArea, idAlicuota);
        var edicion = EdicionConReferencia(creado, referencia, idReferencia);

        var respuestaEdicion = await admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion);

        Assert.Equal(HttpStatusCode.BadRequest, respuestaEdicion.StatusCode);
        var problemaEdicion = await respuestaEdicion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problemaEdicion.GetProperty("codigo").GetString());

        var sinCambios = await admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{creado.Id}", OpcionesJson);
        Assert.Equal(creado.IdArea, sinCambios!.IdArea);
        Assert.Equal(creado.IdCategoria, sinCambios.IdCategoria);
        Assert.Equal(creado.IdMarca, sinCambios.IdMarca);
        Assert.Equal(creado.IdGrupo, sinCambios.IdGrupo);
        Assert.Equal(creado.IdProveedorHabitual, sinCambios.IdProveedorHabitual);
    }

    // =================================================================================================
    // 2/3. Carrera: el lock de BloquearSiEstaVivaAsync hace ESPERAR al escritor contra una baja
    // concurrente que ya tomó FOR UPDATE — mismo rendezvous que BajasDeCatalogosTests C7a/C7b, roles
    // invertidos (acá la conexión cruda simula la BAJA, la API es la que espera el lock).
    // =================================================================================================

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, int pidExcluido1, int pidExcluido2)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1 AND pid <> $2",
                conexionPoll);
            comando.Parameters.AddWithValue(pidExcluido1);
            comando.Parameters.AddWithValue(pidExcluido2);

            var cantidad = (long)(await comando.ExecuteScalarAsync())!;
            if (cantidad > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>C2 del plan: un T1 crudo toma <c>FOR UPDATE</c> sobre la marca (stand-in de la
    /// baja) ANTES de que el alta arranque; el alta queda esperando ese lock (afirmado vía
    /// <c>pg_stat_activity</c>); T1 recién ENTONCES estampa <c>deleted_at</c> y comitea. La
    /// relectura de <c>BloquearSiEstaVivaAsync</c>, después de esperar, ve la marca YA borrada y
    /// el alta se rechaza — nada queda persistido. Mutaciones (a probar a mano sobre
    /// <c>GuardaDeReferencias.BloquearSiEstaVivaAsync</c>): (a) sacar el <c>AND deleted_at IS
    /// NULL</c> del WHERE → este caso pasa a 201 (la relectura ya no ve la baja); (b) cambiar
    /// <c>FOR KEY SHARE</c> por un <c>SELECT</c> plano → el alta ya no espera el lock de T1 y
    /// también comitea con la marca borrada (201).</summary>
    [Fact]
    public async Task UnaBajaConcurrenteDeMarcaGanaLaCarreraContraUnAltaDeArticuloYElAltaSeRechaza()
    {
        var s = await AprovisionarAsync("carrera-alta-marca");
        using var admin = await ClienteAdminAsync(s.MailAdmin, s.PasswordAdmin);
        var idAlicuota = await IdDeAlicuotaIvaAsync();

        var marca = await CrearMarcaAsync(admin, "Marca en carrera de alta");

        await using var conexionBaja = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidBaja = await BackendPidAsync(conexionBaja);

        await using var transaccionBaja = await conexionBaja.BeginTransactionAsync();
        await using (var comandoLock = new NpgsqlCommand(
            "SELECT 1 FROM marcas WHERE id_marca = $1 FOR UPDATE", conexionBaja, transaccionBaja))
        {
            comandoLock.Parameters.AddWithValue(marca.Id);
            await comandoLock.ExecuteScalarAsync();
        }

        var nombre = $"Artículo {Guid.NewGuid():N}";
        var alta = AltaConReferencia(s.IdArea, idAlicuota, Referencia.Marca, marca.Id, nombre);
        var altaTask = admin.PostAsJsonAsync("/api/articulos", alta);

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);

        var observado = await EsperarBackendBloqueadoAsync(conexionPoll, pidBaja, pidPoll);
        Assert.True(observado, "El alta nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await using (var comandoBaja = new NpgsqlCommand(
            "UPDATE marcas SET deleted_at = now() WHERE id_marca = $1", conexionBaja, transaccionBaja))
        {
            comandoBaja.Parameters.AddWithValue(marca.Id);
            await comandoBaja.ExecuteNonQueryAsync();
        }

        await transaccionBaja.CommitAsync();

        var respuesta = await altaTask;
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());

        Assert.Equal(0, await ContarArticulosAsync(s.IdTenant, nombre));
    }

    /// <summary>C3 del plan: mismo rendezvous que la prueba de arriba, sobre un PUT que reasigna
    /// <c>idMarca</c> de un artículo ya existente. El artículo conserva su marca anterior — la
    /// edición nunca llega a pisarla.</summary>
    [Fact]
    public async Task UnaBajaConcurrenteDeMarcaGanaLaCarreraContraUnaEdicionDeArticuloYLaEdicionSeRechaza()
    {
        var s = await AprovisionarAsync("carrera-edicion-marca");
        using var admin = await ClienteAdminAsync(s.MailAdmin, s.PasswordAdmin);
        var idAlicuota = await IdDeAlicuotaIvaAsync();

        var creado = await CrearArticuloBaseAsync(admin, s.IdArea, idAlicuota);
        var marca = await CrearMarcaAsync(admin, "Marca en carrera de edición");

        await using var conexionBaja = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidBaja = await BackendPidAsync(conexionBaja);

        await using var transaccionBaja = await conexionBaja.BeginTransactionAsync();
        await using (var comandoLock = new NpgsqlCommand(
            "SELECT 1 FROM marcas WHERE id_marca = $1 FOR UPDATE", conexionBaja, transaccionBaja))
        {
            comandoLock.Parameters.AddWithValue(marca.Id);
            await comandoLock.ExecuteScalarAsync();
        }

        var edicion = EdicionConReferencia(creado, Referencia.Marca, marca.Id);
        var editTask = admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion);

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);

        var observado = await EsperarBackendBloqueadoAsync(conexionPoll, pidBaja, pidPoll);
        Assert.True(observado, "La edición nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await using (var comandoBaja = new NpgsqlCommand(
            "UPDATE marcas SET deleted_at = now() WHERE id_marca = $1", conexionBaja, transaccionBaja))
        {
            comandoBaja.Parameters.AddWithValue(marca.Id);
            await comandoBaja.ExecuteNonQueryAsync();
        }

        await transaccionBaja.CommitAsync();

        var respuesta = await editTask;
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());

        var sinCambios = await admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{creado.Id}", OpcionesJson);
        Assert.Equal(creado.IdMarca, sinCambios!.IdMarca);
    }

    // =================================================================================================
    // 4. Happy path: el lock no rompe un alta/edición normal con las 5 referencias vivas a la vez.
    // =================================================================================================

    [Fact]
    public async Task UnAltaYUnaEdicionConLasCincoReferenciasVivasFuncionan()
    {
        var s = await AprovisionarAsync("felices-cinco-referencias");
        using var admin = await ClienteAdminAsync(s.MailAdmin, s.PasswordAdmin);
        var idAlicuota = await IdDeAlicuotaIvaAsync();

        var categoria = await CrearCategoriaAsync(admin, "Categoría viva");
        var marca = await CrearMarcaAsync(admin, "Marca viva");
        var grupo = await CrearGrupoAsync(admin, "Grupo vivo");
        var proveedor = await CrearProveedorAsync(admin, "Proveedor vivo");

        var alta = new AltaArticulo(
            CodigoInterno: null, Nombre: $"Artículo {Guid.NewGuid():N}", Descripcion: null, IdArea: s.IdArea,
            IdCategoria: categoria.Id, IdMarca: marca.Id, IdGrupo: grupo.Id, IdProveedorHabitual: proveedor.Id,
            IdAlicuotaIva: idAlicuota, UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null, EsProducto: true,
            CostoLista: null, DescuentoProveedor: null, CostoNominal: null);

        var respuestaAlta = await admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, respuestaAlta.StatusCode);
        var creado = await respuestaAlta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson);
        Assert.Equal(categoria.Id, creado!.IdCategoria);
        Assert.Equal(marca.Id, creado.IdMarca);
        Assert.Equal(grupo.Id, creado.IdGrupo);
        Assert.Equal(proveedor.Id, creado.IdProveedorHabitual);

        var categoriaDos = await CrearCategoriaAsync(admin, "Categoría viva dos");
        var marcaDos = await CrearMarcaAsync(admin, "Marca viva dos");
        var grupoDos = await CrearGrupoAsync(admin, "Grupo vivo dos");
        var proveedorDos = await CrearProveedorAsync(admin, "Proveedor vivo dos");

        var edicion = new EdicionArticulo(
            Nombre: creado.Nombre, Descripcion: creado.Descripcion, IdArea: creado.IdArea,
            IdCategoria: categoriaDos.Id, IdMarca: marcaDos.Id, IdGrupo: grupoDos.Id,
            IdProveedorHabitual: proveedorDos.Id, IdAlicuotaIva: creado.IdAlicuotaIva,
            UnidadVenta: creado.UnidadVenta, UnidadesPorBulto: creado.UnidadesPorBulto,
            EsProducto: creado.EsProducto, CostoLista: creado.CostoLista,
            DescuentoProveedor: creado.DescuentoProveedor, CostoNominal: creado.CostoNominal,
            DisponibleParaTodas: creado.DisponibleParaTodas, IdsEmpresas: null, Activo: creado.Activo,
            ControlaLote: creado.ControlaLote);

        var respuestaEdicion = await admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion);
        Assert.Equal(HttpStatusCode.OK, respuestaEdicion.StatusCode);

        var editado = await respuestaEdicion.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson);
        Assert.Equal(categoriaDos.Id, editado!.IdCategoria);
        Assert.Equal(marcaDos.Id, editado.IdMarca);
        Assert.Equal(grupoDos.Id, editado.IdGrupo);
        Assert.Equal(proveedorDos.Id, editado.IdProveedorHabitual);
    }
}
