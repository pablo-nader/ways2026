using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Dominios;
using Ways.Domain.Common;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// ABM de dominios por tenant (solo plataforma): 404 primero, normalización del host, mover el
/// principal, verificación idempotente, y las tres redes de la base — el índice único de host, el
/// índice único de principal y el CHECK del formato — con su SQLSTATE y su traducción a la API.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DominiosPlataformaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeDominios apoyo = new(fixture);

    private static string Ruta(int idTenant) => $"/api/plataforma/tenants/{idTenant}/dominios";

    private static object Alta(string host, bool esPrincipal = false) => new { host, esPrincipal };

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    // ---- alta y listado ---------------------------------------------------------------------------

    [Fact]
    public async Task ElAltaNormalizaElHostLoDejaSinVerificarYApareceEnElListado()
    {
        var tenant = await apoyo.SembrarTenantAsync("alta");
        var host = ApoyoDeDominios.HostUnico("alta");
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta($"  {host.ToUpperInvariant()}:8080 "));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await ApoyoDeDominios.LeerJsonAsync(respuesta);
        Assert.Equal(host, creado.GetProperty("host").GetString());
        Assert.Equal(tenant.IdTenant, creado.GetProperty("idTenant").GetInt32());
        Assert.False(creado.GetProperty("esPrincipal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, creado.GetProperty("verificadoEn").ValueKind);
        var idDominio = creado.GetProperty("id").GetInt32();
        Assert.Equal($"{Ruta(tenant.IdTenant)}/{idDominio}", respuesta.Headers.Location?.OriginalString);

        var listado = await ApoyoDeDominios.LeerJsonAsync(await root.GetAsync(Ruta(tenant.IdTenant)));
        var fila = Assert.Single(listado.EnumerateArray());
        Assert.Equal(idDominio, fila.GetProperty("id").GetInt32());
        Assert.Equal(host, fila.GetProperty("host").GetString());
    }

    [Theory]
    [InlineData("https://tienda.com")]
    [InlineData("tienda.com/ruta")]
    [InlineData("tienda com")]
    [InlineData("")]
    public async Task UnHostInvalidoDevuelve400HostInvalidoYNoGuardaNada(string host)
    {
        var tenant = await apoyo.SembrarTenantAsync("host-invalido");
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta(host));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("host_invalido", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.Empty((await ApoyoDeDominios.LeerJsonAsync(await root.GetAsync(Ruta(tenant.IdTenant)))).EnumerateArray());
    }

    [Fact]
    public async Task ElListadoMuestraSoloLosDominiosVivosDelTenantYElPrincipalPrimero()
    {
        var tenant = await apoyo.SembrarTenantAsync("listado");
        var otro = await apoyo.SembrarTenantAsync("listado-otro");
        var hostSecundario = ApoyoDeDominios.HostUnico("a-secundario");
        var hostPrincipal = ApoyoDeDominios.HostUnico("z-principal");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, hostSecundario);
        await apoyo.SembrarDominioAsync(tenant.IdTenant, hostPrincipal, principal: true);
        await apoyo.SembrarDominioAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("eliminado"), eliminado: true);
        await apoyo.SembrarDominioAsync(otro.IdTenant, ApoyoDeDominios.HostUnico("ajeno"));
        using var root = await apoyo.ClienteRootAsync();

        var listado = await ApoyoDeDominios.LeerJsonAsync(await root.GetAsync(Ruta(tenant.IdTenant)));

        string[] hosts = [.. listado.EnumerateArray().Select(f => f.GetProperty("host").GetString()!)];
        Assert.Equal([hostPrincipal, hostSecundario], hosts);
    }

    [Fact]
    public async Task MarcarUnSegundoPrincipalMueveLaMarcaEnLaMismaOperacion()
    {
        var tenant = await apoyo.SembrarTenantAsync("principal");
        var primero = ApoyoDeDominios.HostUnico("principal-1");
        var segundo = ApoyoDeDominios.HostUnico("principal-2");
        var tercero = ApoyoDeDominios.HostUnico("principal-3");
        using var root = await apoyo.ClienteRootAsync();

        var idPrimero = (await ApoyoDeDominios.LeerJsonAsync(
            await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta(primero, esPrincipal: true)))).GetProperty("id").GetInt32();
        var idSegundo = (await ApoyoDeDominios.LeerJsonAsync(
            await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta(segundo, esPrincipal: true)))).GetProperty("id").GetInt32();

        Assert.False((await apoyo.LeerDominioAsync(idPrimero)).EsPrincipal);
        Assert.True((await apoyo.LeerDominioAsync(idSegundo)).EsPrincipal);

        // Un alta que no pide principal no toca la marca del que la tiene.
        var idTercero = (await ApoyoDeDominios.LeerJsonAsync(
            await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta(tercero)))).GetProperty("id").GetInt32();

        Assert.True((await apoyo.LeerDominioAsync(idSegundo)).EsPrincipal);
        Assert.False((await apoyo.LeerDominioAsync(idTercero)).EsPrincipal);
    }

    [Fact]
    public async Task MoverElPrincipalDeUnTenantNoTocaElPrincipalDeOtro()
    {
        var a = await apoyo.SembrarTenantAsync("principal-a");
        var b = await apoyo.SembrarTenantAsync("principal-b");
        var idDeB = await apoyo.SembrarDominioAsync(b.IdTenant, ApoyoDeDominios.HostUnico("principal-b"), principal: true);
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.PostAsJsonAsync(
            Ruta(a.IdTenant), Alta(ApoyoDeDominios.HostUnico("principal-a"), esPrincipal: true));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.True((await apoyo.LeerDominioAsync(idDeB)).EsPrincipal);
    }

    [Fact]
    public async Task UnHostYaCargadoDevuelve409HostDuplicadoAunQueSeaDeOtroTenant()
    {
        var a = await apoyo.SembrarTenantAsync("duplicado-a");
        var b = await apoyo.SembrarTenantAsync("duplicado-b");
        var host = ApoyoDeDominios.HostUnico("duplicado");
        await apoyo.SembrarDominioAsync(a.IdTenant, host);
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.PostAsJsonAsync(Ruta(b.IdTenant), Alta(host));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("host_duplicado", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task ElHostDeUnDominioDadoDeBajaSePuedeVolverACargar()
    {
        var tenant = await apoyo.SembrarTenantAsync("recarga");
        var host = ApoyoDeDominios.HostUnico("recarga");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host, eliminado: true);
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.PostAsJsonAsync(Ruta(tenant.IdTenant), Alta(host));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- verificar y dar de baja ------------------------------------------------------------------

    [Fact]
    public async Task VerificarEsIdempotenteYNoMueveElInstanteDeLaPrimeraVerificacion()
    {
        var tenant = await apoyo.SembrarTenantAsync("idempotente");
        var idDominio = await apoyo.SembrarDominioAsync(
            tenant.IdTenant, ApoyoDeDominios.HostUnico("idempotente"), verificado: false);
        using var root = await apoyo.ClienteRootAsync();
        var ruta = $"{Ruta(tenant.IdTenant)}/{idDominio}/verificar";

        var primera = await root.PostAsync(ruta, content: null);
        var tras1 = (await apoyo.LeerDominioAsync(idDominio)).VerificadoEn;
        var segunda = await root.PostAsync(ruta, content: null);
        var tras2 = (await apoyo.LeerDominioAsync(idDominio)).VerificadoEn;

        Assert.Equal(HttpStatusCode.OK, primera.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.NotNull(tras1);
        Assert.Equal(tras1, tras2);
        Assert.Equal(tras1, (await ApoyoDeDominios.LeerJsonAsync(segunda)).GetProperty("verificadoEn").GetDateTimeOffset());
    }

    [Fact]
    public async Task LaBajaEsLogicaYDejaLaFilaConSuInstante()
    {
        var tenant = await apoyo.SembrarTenantAsync("baja");
        var idDominio = await apoyo.SembrarDominioAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("baja"));
        var sibling = await apoyo.SembrarDominioAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("baja-hermano"));
        using var root = await apoyo.ClienteRootAsync();

        var respuesta = await root.DeleteAsync($"{Ruta(tenant.IdTenant)}/{idDominio}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.NotNull((await apoyo.LeerDominioAsync(idDominio)).DeletedAt);
        Assert.Null((await apoyo.LeerDominioAsync(sibling)).DeletedAt);
    }

    // ---- 404 primero ------------------------------------------------------------------------------

    [Fact]
    public async Task UnTenantInexistenteResponde404AunConUnCuerpoInvalido()
    {
        using var root = await apoyo.ClienteRootAsync();

        var invalido = await root.PostAsJsonAsync(Ruta(int.MaxValue), Alta("https://no-es-un-host"));
        var valido = await root.PostAsJsonAsync(Ruta(int.MaxValue), Alta(ApoyoDeDominios.HostUnico("sin-tenant")));
        var listado = await root.GetAsync(Ruta(int.MaxValue));

        Assert.Equal(HttpStatusCode.NotFound, invalido.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, valido.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, listado.StatusCode);
    }

    [Fact]
    public async Task UnDominioInexistenteODeOtroTenantOEliminadoResponde404EnVerificarYEnBaja()
    {
        var tenant = await apoyo.SembrarTenantAsync("404-dominio");
        var otro = await apoyo.SembrarTenantAsync("404-otro");
        var idAjeno = await apoyo.SembrarDominioAsync(otro.IdTenant, ApoyoDeDominios.HostUnico("404-ajeno"));
        var idEliminado = await apoyo.SembrarDominioAsync(
            tenant.IdTenant, ApoyoDeDominios.HostUnico("404-eliminado"), eliminado: true);
        using var root = await apoyo.ClienteRootAsync();

        foreach (var idDominio in new[] { int.MaxValue, idAjeno, idEliminado })
        {
            var verificar = await root.PostAsync($"{Ruta(tenant.IdTenant)}/{idDominio}/verificar", content: null);
            var baja = await root.DeleteAsync($"{Ruta(tenant.IdTenant)}/{idDominio}");

            Assert.Equal(HttpStatusCode.NotFound, verificar.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, baja.StatusCode);
        }

        // El dominio ajeno quedó intacto: el 404 no lo tocó.
        Assert.Null((await apoyo.LeerDominioAsync(idAjeno)).DeletedAt);
        Assert.NotNull((await apoyo.LeerDominioAsync(idAjeno)).VerificadoEn);
    }

    // ---- quién puede ------------------------------------------------------------------------------

    [Fact]
    public async Task SoloLaPlataformaAdministraDominios()
    {
        var tenant = await apoyo.SembrarTenantAsync("autorizacion");
        var idDominio = await apoyo.SembrarDominioAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("autorizacion"));
        var rutas = new (HttpMethod Metodo, string Ruta)[]
        {
            (HttpMethod.Get, Ruta(tenant.IdTenant)),
            (HttpMethod.Post, Ruta(tenant.IdTenant)),
            (HttpMethod.Post, $"{Ruta(tenant.IdTenant)}/{idDominio}/verificar"),
            (HttpMethod.Delete, $"{Ruta(tenant.IdTenant)}/{idDominio}")
        };

        HttpRequestMessage Solicitud(HttpMethod metodo, string ruta)
        {
            var solicitud = new HttpRequestMessage(metodo, ruta);

            // El alta lleva cuerpo válido: lo que tiene que cortarla es la policy, no el binding.
            if (metodo == HttpMethod.Post && ruta == Ruta(tenant.IdTenant))
            {
                solicitud.Content = JsonContent.Create(Alta(ApoyoDeDominios.HostUnico("autorizacion-post")));
            }

            return solicitud;
        }

        using var anonimo = fixture.CreateClient();
        using var adminDelTenant = fixture.CreateClient();
        var login = await adminDelTenant.PostAsJsonAsync(
            "/api/auth/login", new { mail = tenant.Mail, password = tenant.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        foreach (var (metodo, ruta) in rutas)
        {
            using var sinSesion = Solicitud(metodo, ruta);
            using var comoAdmin = Solicitud(metodo, ruta);

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(sinSesion)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await adminDelTenant.SendAsync(comoAdmin)).StatusCode);
        }

        Assert.Null((await apoyo.LeerDominioAsync(idDominio)).DeletedAt);
    }

    // ---- redes de la base -------------------------------------------------------------------------

    /// <summary>Dos altas del mismo host de tenants distintos pasan las dos el pre-chequeo (nada que
    /// las serialice: el lock es por tenant) y llegan juntas al INSERT. El rendezvous las suelta a la
    /// vez; la que pierde choca contra <c>ux_dominios_host</c> y <c>ManejadorDeErrores</c> la traduce.
    /// Sin la traducción sería un 500.</summary>
    [Fact]
    public async Task DosAltasDelMismoHostEnLaMismaVentanaDanUnaCreacionYUn409HostDuplicado()
    {
        var a = await apoyo.SembrarTenantAsync("carrera-a");
        var b = await apoyo.SembrarTenantAsync("carrera-b");
        var host = ApoyoDeDominios.HostUnico("carrera");
        using var rootA = await apoyo.ClienteRootAsync();
        using var rootB = await apoyo.ClienteRootAsync();

        using var rendezvous = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousDelAlta(rendezvous);

        HttpResponseMessage[] respuestas;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuestas = await Task.WhenAll(
                rootA.PostAsJsonAsync(Ruta(a.IdTenant), Alta(host)),
                rootB.PostAsJsonAsync(Ruta(b.IdTenant), Alta(host)));
        }

        Assert.Equal(2, interceptor.Participantes);
        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            respuestas.Select(r => r.StatusCode).Order().ToArray());

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("host_duplicado", await ApoyoDeDominios.CodigoDeErrorAsync(perdedora));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.Equal(1, await db.Dominios.CountAsync(d => d.Host == host));
    }

    [Fact]
    public async Task ElIndiceDeHostRechazaElDuplicadoConSqlState23505()
    {
        var tenant = await apoyo.SembrarTenantAsync("sql-host");
        var host = ApoyoDeDominios.HostUnico("sql-host");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, host);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(tenant.IdTenant, host, esPrincipal: false));

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_dominios_host", error.ConstraintName);
    }

    [Fact]
    public async Task ElIndiceDePrincipalRechazaUnSegundoPrincipalVivoConSqlState23505()
    {
        var tenant = await apoyo.SembrarTenantAsync("sql-principal");
        await apoyo.SembrarDominioAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("sql-principal-1"), principal: true);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("sql-principal-2"), esPrincipal: true));

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_dominios_principal_por_tenant", error.ConstraintName);
    }

    [Fact]
    public async Task UnPrincipalDadoDeBajaNoOcupaElLugarDelPrincipalVivo()
    {
        var tenant = await apoyo.SembrarTenantAsync("sql-principal-baja");
        await apoyo.SembrarDominioAsync(
            tenant.IdTenant, ApoyoDeDominios.HostUnico("sql-baja-1"), principal: true, eliminado: true);

        await InsertarCrudoAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("sql-baja-2"), esPrincipal: true);
    }

    [Theory]
    [InlineData("TIENDA.COM")]
    [InlineData("https://tienda.com")]
    [InlineData("tienda.com:8080")]
    [InlineData("tienda.com/ruta")]
    [InlineData("-tienda.com")]
    [InlineData("tienda..com")]
    [InlineData("tienda com")]
    [InlineData("")]
    public async Task ElCheckDelFormatoRechazaUnHostMalFormadoConSqlState23514(string host)
    {
        var tenant = await apoyo.SembrarTenantAsync("sql-formato");

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(tenant.IdTenant, host, esPrincipal: false));

        Assert.Equal("23514", error.SqlState);
        Assert.Equal("ck_dominios_host_formato", error.ConstraintName);
    }

    [Fact]
    public async Task ElCheckDelFormatoAceptaUnHostValidoDeLaBase()
    {
        var tenant = await apoyo.SembrarTenantAsync("sql-formato-ok");

        await InsertarCrudoAsync(tenant.IdTenant, ApoyoDeDominios.HostUnico("mi-tienda"), esPrincipal: false);
    }

    // ---- RLS (rol ways_app, sin BYPASSRLS) --------------------------------------------------------

    [Fact]
    public async Task EnModoLoginSePuedenLeerLosDominiosDeTodosLosTenantsPeroNoEscribirlos()
    {
        var a = await apoyo.SembrarTenantAsync("rls-a");
        var b = await apoyo.SembrarTenantAsync("rls-b");
        var hostA = ApoyoDeDominios.HostUnico("rls-a");
        var hostB = ApoyoDeDominios.HostUnico("rls-b");
        var idA = await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("login", idTenant: null);

        Assert.Equal(
            2L, await ContarAsync(conexion, "SELECT count(*) FROM dominios WHERE host IN ($1, $2)", hostA, hostB));

        // RLS filtra el UPDATE y el DELETE (0 filas, sin excepción) y rechaza el INSERT (42501).
        await using (var actualizar = conexion.CreateCommand())
        {
            actualizar.CommandText = "UPDATE dominios SET es_principal = true WHERE id_dominio = $1";
            actualizar.Parameters.Add(new NpgsqlParameter { Value = idA });
            Assert.Equal(0, await actualizar.ExecuteNonQueryAsync());
        }

        await using (var borrar = conexion.CreateCommand())
        {
            borrar.CommandText = "DELETE FROM dominios WHERE id_dominio = $1";
            borrar.Parameters.Add(new NpgsqlParameter { Value = idA });
            Assert.Equal(0, await borrar.ExecuteNonQueryAsync());
        }

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var insertar = conexion.CreateCommand();
            insertar.CommandText =
                "INSERT INTO dominios (id_tenant, host, created_at, updated_at) VALUES ($1, $2, now(), now())";
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertar.Parameters.Add(new NpgsqlParameter { Value = ApoyoDeDominios.HostUnico("rls-insert") });
            await insertar.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task UnTenantSoloVeSusDominiosYSinContextoNoSeVeNinguno()
    {
        var a = await apoyo.SembrarTenantAsync("rls-tenant-a");
        var b = await apoyo.SembrarTenantAsync("rls-tenant-b");
        var hostA = ApoyoDeDominios.HostUnico("rls-tenant-a");
        var hostB = ApoyoDeDominios.HostUnico("rls-tenant-b");
        await apoyo.SembrarDominioAsync(a.IdTenant, hostA);
        await apoyo.SembrarDominioAsync(b.IdTenant, hostB);

        await using (var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            Assert.Equal(1L, await ContarAsync(comoA, "SELECT count(*) FROM dominios WHERE host IN ($1, $2)", hostA, hostB));
            Assert.Equal(1L, await ContarAsync(comoA, "SELECT count(*) FROM dominios WHERE host = $1", hostA, null));
        }

        await using var sinContexto = await fixture.AbrirConexionCrudaAsync("", idTenant: null);
        Assert.Equal(
            0L, await ContarAsync(sinContexto, "SELECT count(*) FROM dominios WHERE host IN ($1, $2)", hostA, hostB));
    }

    // ---- reintentos -------------------------------------------------------------------------------

    /// <summary>Un fallo transitorio (40001) en el INSERT no se reintenta: la alta corre bajo
    /// <c>FabricaDeEstrategiaSinReintento</c>. <c>Intentos</c> es el valor discriminante — con la
    /// estrategia reintentable serían dos y el segundo intento comitearía la fila.</summary>
    [Fact]
    public async Task UnaFallaTransitoriaEnElAltaNoSeReintentaNiGuardaNada()
    {
        var tenant = await apoyo.SembrarTenantAsync("reintento");
        var host = ApoyoDeDominios.HostUnico("reintento");
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("dominios", "40001");
        var resolvedor = fixture.Services.GetRequiredService<IResolvedorDeDominios>();
        var reloj = new RelojFijo(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeDominios(db, reloj, resolvedor);

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.AgregarAsync(tenant.IdTenant, new SolicitudDeDominio(host, EsPrincipal: false)));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            Assert.Equal(0, await db.Dominios.CountAsync(d => d.Host == host));
        }

        // La misma alta, sin la falla inyectada, escribe exactamente una fila.
        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma))
        {
            await new ServicioDeDominios(db, reloj, resolvedor)
                .AgregarAsync(tenant.IdTenant, new SolicitudDeDominio(host, EsPrincipal: false));
        }

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            Assert.Equal(1, await db.Dominios.CountAsync(d => d.Host == host));
        }
    }

    // ---- apoyo ------------------------------------------------------------------------------------

    private async Task InsertarCrudoAsync(int idTenant, string host, bool esPrincipal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", idTenant: null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO dominios (id_tenant, host, es_principal, created_at, updated_at) VALUES ($1, $2, $3, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = host });
        comando.Parameters.Add(new NpgsqlParameter { Value = esPrincipal });
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string sql, string? primero, string? segundo)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.Add(new NpgsqlParameter { Value = primero });
        if (segundo is not null)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = segundo });
        }

        return (long)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Retiene los dos primeros <c>INSERT INTO dominios</c> hasta que ambos llegaron. Cada alta
    /// ya pasó su pre-chequeo de host (nada insertado todavía), así que las dos entran al índice único
    /// juntas.</summary>
    private sealed class InterceptorDeRendezVousDelAlta(CountdownEvent compuerta) : DbCommandInterceptor
    {
        private int participantes;

        public int Participantes => Volatile.Read(ref participantes);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            EsperarSiCorresponde(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EsperarSiCorresponde(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void EsperarSiCorresponde(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO dominios", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref participantes) > 2)
            {
                return;
            }

            compuerta.Signal();

            Assert.True(
                compuerta.Wait(TimeSpan.FromSeconds(10)),
                "El rendezvous del alta no llegó a los 2 participantes a tiempo.");
        }
    }
}
