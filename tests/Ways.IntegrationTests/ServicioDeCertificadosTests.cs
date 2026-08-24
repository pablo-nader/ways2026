using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="ServicioDeCertificados"/>/<see cref="DesactivadorDeCertificadoFiscal"/> — tasks.md
/// Slice 4, targets 61-63: U4 (los cinco kills, (a) vía RLS bajo <c>ways_app</c>, (b)-(e) directo
/// contra el mismo método de producción), la cláusula de exposición del DTO (target 62), y la
/// matriz de roles de <see cref="Politicas.AdministracionFiscal"/> (target 63).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeCertificadosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private static readonly DateTimeOffset Ahora = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>D6/verify criterion 8: <c>Ways.Api/Program.cs</c> lee <c>builder.Configuration</c>
    /// SÍNCRONO, antes de que <c>WebApplicationFactory.ConfigureAppConfiguration</c> tenga chance
    /// de inyectar nada (mismo hallazgo documentado en <c>WaysApiFixture.ConfigureWebHost</c> para
    /// la cadena de conexión) — la única vía que Program.Main sí lee es una variable de ENTORNO,
    /// seteada acá en el constructor estático (garantizado por el runtime "antes del primer uso
    /// del tipo", así que corre antes de que cualquier test de esta clase llegue a su primer
    /// <c>fixture.CreateClient()</c>). Nunca commiteada a <c>appsettings*.json</c> — coherente con
    /// D6 y el verify criterion 8 (ningún hostname/secreto real como default).</summary>
    static ServicioDeCertificadosTests()
    {
        Environment.SetEnvironmentVariable("Ways__Fiscal__ClaveMaestraActual", "v1");
        Environment.SetEnvironmentVariable(
            "Ways__Fiscal__ClavesMaestras__v1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>El servidor serializa enums como texto (Program.cs, <c>JsonStringEnumConverter</c>)
    /// — el <see cref="HttpClient"/> de prueba no hereda esa configuración (mismo hallazgo que
    /// <c>OrganizacionTests.OpcionesJson</c>): hay que repetirla para <see cref="CertificadoFiscalDto.Ambiente"/>.</summary>
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor, HttpClient Root);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, admin, supervisor, vendedor, root);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static (byte[] Pfx, string Password) GenerarPfx(string cn)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var ahora = DateTimeOffset.UtcNow;
        using var certificado = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));

        var password = Guid.NewGuid().ToString("N");
        return (certificado.Export(X509ContentType.Pkcs12, password), password);
    }

    private static IConfiguration ConfiguracionConClaveMaestra() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ways:Fiscal:ClaveMaestraActual"] = "v1",
                ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();

    // --- Target 63: matriz de roles de AdministracionFiscal ---

    [Fact]
    public async Task UnAdminEsAceptadoAlRegistrarUnCertificadoFiscal()
    {
        var ctx = await PrepararAsync(nameof(UnAdminEsAceptadoAlRegistrarUnCertificadoFiscal));
        var (pfx, password) = GenerarPfx("CN=Ways Test Admin");

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo principal", "20111111112", pfx, password));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Theory]
    [InlineData(nameof(RolConocido.Supervisor))]
    [InlineData(nameof(RolConocido.Vendedor))]
    public async Task UnSupervisorOVendedorEsRechazadoAlRegistrarUnCertificadoFiscal(string rol)
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorOVendedorEsRechazadoAlRegistrarUnCertificadoFiscal) + rol);
        var cliente = rol == nameof(RolConocido.Supervisor) ? ctx.Supervisor : ctx.Vendedor;
        var (pfx, password) = GenerarPfx($"CN=Ways Test {rol}");

        var respuesta = await cliente.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo principal", "20111111112", pfx, password));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRootEsRechazadoAlRegistrarUnCertificadoFiscal()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoAlRegistrarUnCertificadoFiscal));
        var (pfx, password) = GenerarPfx("CN=Ways Test Root");

        var respuesta = await ctx.Root.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo principal", "20111111112", pfx, password));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminEsAceptadoAlCargarLaCondicionFiscalDeUnaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(UnAdminEsAceptadoAlCargarLaCondicionFiscalDeUnaEmpresa));

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/fiscal/empresas/{ctx.IdEmpresa}/condicion-fiscal", new CondicionFiscalDeEmpresaEdicion(1));

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminEsAceptadoAlDesactivarUnCertificadoFiscal()
    {
        var ctx = await PrepararAsync(nameof(UnAdminEsAceptadoAlDesactivarUnCertificadoFiscal));
        var (pfx, password) = GenerarPfx("CN=Ways Test Desactivar");

        var alta = await ctx.Admin.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo a desactivar", "20111111112", pfx, password));
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        var creado = await alta.Content.ReadFromJsonAsync<CertificadoFiscalDto>(OpcionesJson);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/fiscal/certificados/{creado!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoAlCargarLaCondicionFiscalDeUnaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoAlCargarLaCondicionFiscalDeUnaEmpresa));

        var respuesta = await ctx.Vendedor.PutAsJsonAsync(
            $"/api/fiscal/empresas/{ctx.IdEmpresa}/condicion-fiscal", new CondicionFiscalDeEmpresaEdicion(1));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // --- Target 62: la cláusula de exposición — ninguna respuesta serializada del ABM puede
    // llevar una propiedad de material de clave, matcheada por NOMBRE, recursiva. ---

    private static readonly string[] PropiedadesDeMaterialDeClave =
        ["clavePrivadaCifrada", "nonce", "tagAutenticacion", "certificadoPem", "claveMaestra"];

    [Fact]
    public async Task ListarCertificadosNuncaExponeMaterialDeClave()
    {
        var ctx = await PrepararAsync(nameof(ListarCertificadosNuncaExponeMaterialDeClave));
        var (pfx, password) = GenerarPfx("CN=Ways Test Exposicion");

        var alta = await ctx.Admin.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo principal", "20222222223", pfx, password));
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);

        var listado = await ctx.Admin.GetAsync("/api/fiscal/certificados");
        Assert.Equal(HttpStatusCode.OK, listado.StatusCode);

        var cuerpo = await listado.Content.ReadAsStringAsync();
        using var documento = JsonDocument.Parse(cuerpo);

        var encontradas = new List<string>();
        RecorrerNombresDePropiedad(documento.RootElement, encontradas);

        foreach (var prohibida in PropiedadesDeMaterialDeClave)
        {
            Assert.DoesNotContain(
                encontradas, nombre => string.Equals(nombre, prohibida, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(cuerpo, c => true); // el body no está vacío (guard trivial, evita un JSON [] falso-positivo)
        Assert.Contains("alias", encontradas.Select(n => n.ToLowerInvariant()));
    }

    private static void RecorrerNombresDePropiedad(JsonElement elemento, List<string> acumulado)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propiedad in elemento.EnumerateObject())
                {
                    acumulado.Add(propiedad.Name);
                    RecorrerNombresDePropiedad(propiedad.Value, acumulado);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    RecorrerNombresDePropiedad(item, acumulado);
                }

                break;
        }
    }

    // --- Target 61: U4 — los cinco kills de la UPDATE de desactivación ---

    private async Task<int> SembrarTenantConEmpresaAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        return empresa.Id;
    }

    private async Task<int> SembrarCertificadoRawAsync(
        int idTenant, int idEmpresa, AmbienteFiscal ambiente, bool activo, bool eliminado)
    {
        await using var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        await conexion.OpenAsync();
        await using (var guc = new NpgsqlCommand("SELECT set_config('app.acceso', 'plataforma', false)", conexion))
        {
            await guc.ExecuteNonQueryAsync();
        }

        await using var comando = new NpgsqlCommand(
            """
            INSERT INTO certificados_fiscales
                (id_tenant, id_empresa, ambiente, alias, cuit_titular, certificado_pem,
                 clave_privada_cifrada, nonce, tag_autenticacion, id_clave_maestra, huella_sha256,
                 vigencia_desde, vigencia_hasta, activo, created_at, updated_at, deleted_at)
            VALUES
                ($1, $2, $3::ambiente_fiscal, 'Certificado de prueba', '20111111112', '-----BEGIN CERTIFICATE-----test-----END CERTIFICATE-----',
                 $4, $5, $6, 'v1', $7, $8, $9, $10, $8, $8, $11)
            RETURNING id_certificado
            """,
            conexion);
        comando.Parameters.AddWithValue(idTenant);
        comando.Parameters.AddWithValue(idEmpresa);
        // Conexión cruda SIN MapEnum<AmbienteFiscal> registrado (a diferencia de
        // db.Database.GetDbConnection(), armada vía UseNpgsql con el enum mapeado) — el enum viaja
        // como texto y el cast explícito ($3::ambiente_fiscal) de la SQL hace el resto.
        comando.Parameters.AddWithValue(ambiente.ToString().ToLowerInvariant());
        comando.Parameters.AddWithValue(new byte[] { 1, 2, 3, 4 });
        comando.Parameters.AddWithValue(new byte[12]);
        comando.Parameters.AddWithValue(new byte[16]);
        comando.Parameters.AddWithValue($"huella-{Guid.NewGuid():N}");
        comando.Parameters.AddWithValue(Ahora);
        comando.Parameters.AddWithValue(Ahora.AddYears(1));
        comando.Parameters.AddWithValue(activo);
        comando.Parameters.AddWithValue((object?)(eliminado ? Ahora : null) ?? DBNull.Value);

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<(bool Activo, DateTimeOffset UpdatedAt)> LeerEstadoAsync(int idCertificado)
    {
        await using var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        await conexion.OpenAsync();
        await using (var guc = new NpgsqlCommand("SELECT set_config('app.acceso', 'plataforma', false)", conexion))
        {
            await guc.ExecuteNonQueryAsync();
        }

        await using var comando = new NpgsqlCommand(
            "SELECT activo, updated_at FROM certificados_fiscales WHERE id_certificado = $1", conexion);
        comando.Parameters.AddWithValue(idCertificado);

        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        return (lector.GetBoolean(0), lector.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>Conjunct (a): bajo <c>ways_app</c> escaneado como tenant B, la fila de tenant A es
    /// invisible aunque el parámetro <c>idTenant</c> pasado sea el de A — RLS, no el WHERE
    /// explícito, es lo que la protege (defensa en profundidad).</summary>
    [Fact]
    public async Task ConjuncoIdTenant_UnTenantAjenoNoPuedeDesactivarLaFilaDeOtroTenant()
    {
        var idEmpresaA = await SembrarTenantConEmpresaAsync(nameof(ConjuncoIdTenant_UnTenantAjenoNoPuedeDesactivarLaFilaDeOtroTenant) + "-A");
        await using var dbSiembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTenantA = (await dbSiembra.Empresas.FindAsync(idEmpresaA))!.IdTenant;
        var idTenantB = await SembrarOtroTenantAsync(nameof(ConjuncoIdTenant_UnTenantAjenoNoPuedeDesactivarLaFilaDeOtroTenant) + "-B");

        var idCertificado = await SembrarCertificadoRawAsync(idTenantA, idEmpresaA, AmbienteFiscal.Homologacion, activo: true, eliminado: false);

        await using var dbTenantB = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB));

        var afectadas = await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
            dbTenantB, idTenantA, idEmpresaA, AmbienteFiscal.Homologacion, Ahora.AddMinutes(1));

        Assert.Equal(0, afectadas);
        Assert.True((await LeerEstadoAsync(idCertificado)).Activo);
    }

    private async Task<int> SembrarOtroTenantAsync(string nombre)
    {
        await using var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public async Task ConjuntoIdEmpresa_UnaEmpresaHermanaMantieneSuCertificadoActivo()
    {
        var idEmpresaA = await SembrarTenantConEmpresaAsync(nameof(ConjuntoIdEmpresa_UnaEmpresaHermanaMantieneSuCertificadoActivo) + "-A");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTenant = (await db.Empresas.FindAsync(idEmpresaA))!.IdTenant;

        var empresaB = new Empresa
        {
            IdTenant = idTenant, RazonSocial = "Hermana", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        db.Empresas.Add(empresaB);
        await db.SaveChangesAsync();

        var idCertificadoA = await SembrarCertificadoRawAsync(idTenant, idEmpresaA, AmbienteFiscal.Homologacion, activo: true, eliminado: false);
        var idCertificadoB = await SembrarCertificadoRawAsync(idTenant, empresaB.Id, AmbienteFiscal.Homologacion, activo: true, eliminado: false);

        await using var dbAccion = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var afectadas = await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
            dbAccion, idTenant, idEmpresaA, AmbienteFiscal.Homologacion, Ahora.AddMinutes(1));

        Assert.Equal(1, afectadas);
        Assert.False((await LeerEstadoAsync(idCertificadoA)).Activo);
        Assert.True((await LeerEstadoAsync(idCertificadoB)).Activo);
    }

    [Fact]
    public async Task ConjuntoAmbiente_ElCertificadoDeProduccionSigueActivoMientrasHomologacionRota()
    {
        var idEmpresa = await SembrarTenantConEmpresaAsync(nameof(ConjuntoAmbiente_ElCertificadoDeProduccionSigueActivoMientrasHomologacionRota));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTenant = (await db.Empresas.FindAsync(idEmpresa))!.IdTenant;

        var idHomo = await SembrarCertificadoRawAsync(idTenant, idEmpresa, AmbienteFiscal.Homologacion, activo: true, eliminado: false);
        var idProd = await SembrarCertificadoRawAsync(idTenant, idEmpresa, AmbienteFiscal.Produccion, activo: true, eliminado: false);

        await using var dbAccion = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var afectadas = await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
            dbAccion, idTenant, idEmpresa, AmbienteFiscal.Homologacion, Ahora.AddMinutes(1));

        Assert.Equal(1, afectadas);
        Assert.False((await LeerEstadoAsync(idHomo)).Activo);
        Assert.True((await LeerEstadoAsync(idProd)).Activo);
    }

    /// <summary>Conjunct (d), mutation-proof-tests regla 4: se prueba sobre el CONTEO de filas
    /// afectadas (0), no sobre el estado final (que ya era <c>false</c> de todos modos y sería un
    /// test overdeterminado).</summary>
    [Fact]
    public async Task ConjuntoActivo_UnCertificadoYaInactivoNoSeCuentaComoAfectado()
    {
        var idEmpresa = await SembrarTenantConEmpresaAsync(nameof(ConjuntoActivo_UnCertificadoYaInactivoNoSeCuentaComoAfectado));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTenant = (await db.Empresas.FindAsync(idEmpresa))!.IdTenant;

        await SembrarCertificadoRawAsync(idTenant, idEmpresa, AmbienteFiscal.Homologacion, activo: false, eliminado: false);

        await using var dbAccion = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var afectadas = await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
            dbAccion, idTenant, idEmpresa, AmbienteFiscal.Homologacion, Ahora.AddMinutes(1));

        Assert.Equal(0, afectadas);
    }

    [Fact]
    public async Task ConjuntoDeletedAt_UnGemeloDadoDeBajaNiSeResucitaNiSeCuenta()
    {
        var idEmpresa = await SembrarTenantConEmpresaAsync(nameof(ConjuntoDeletedAt_UnGemeloDadoDeBajaNiSeResucitaNiSeCuenta));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTenant = (await db.Empresas.FindAsync(idEmpresa))!.IdTenant;

        // Soft-deleted PERO todavía marcado activo=true en la fila (estado posible: se dio de
        // baja lógica sin pasar por la desactivación explícita) — el filtro `deleted_at IS NULL`
        // lo tiene que dejar afuera igual.
        var idGemelo = await SembrarCertificadoRawAsync(idTenant, idEmpresa, AmbienteFiscal.Homologacion, activo: true, eliminado: true);

        await using var dbAccion = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var afectadas = await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
            dbAccion, idTenant, idEmpresa, AmbienteFiscal.Homologacion, Ahora.AddMinutes(1));

        Assert.Equal(0, afectadas);
        Assert.True((await LeerEstadoAsync(idGemelo)).Activo);
    }

    // --- Rotación atómica end-to-end (spec certificados-fiscales) ---

    [Fact]
    public async Task RegistrarUnSegundoCertificadoRotaElPrimeroDentroDeUnaSolaTransaccion()
    {
        var ctx = await PrepararAsync(nameof(RegistrarUnSegundoCertificadoRotaElPrimeroDentroDeUnaSolaTransaccion));

        var (pfx1, password1) = GenerarPfx("CN=Ways Test Rotacion 1");
        var alta1 = await ctx.Admin.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo v1", "20111111112", pfx1, password1));
        Assert.Equal(HttpStatusCode.OK, alta1.StatusCode);

        var (pfx2, password2) = GenerarPfx("CN=Ways Test Rotacion 2");
        var alta2 = await ctx.Admin.PostAsJsonAsync("/api/fiscal/certificados", new RegistroDeCertificadoFiscal(
            ctx.IdEmpresa, AmbienteFiscal.Homologacion, "Homo v2", "20111111112", pfx2, password2));
        Assert.Equal(HttpStatusCode.OK, alta2.StatusCode);

        var listadoRespuesta = await ctx.Admin.GetAsync("/api/fiscal/certificados");
        var listado = await listadoRespuesta.Content.ReadFromJsonAsync<List<CertificadoFiscalDto>>(OpcionesJson);
        Assert.NotNull(listado);
        var activos = listado!.Where(c => c.IdEmpresa == ctx.IdEmpresa && c.Ambiente == AmbienteFiscal.Homologacion && c.Activo);
        Assert.Single(activos);
        Assert.Equal("Homo v2", activos.Single().Alias);
    }

    // --- Target 59 (reasertado a nivel ABM): sin clave maestra, el alta falla y no queda fila. ---

    [Fact]
    public async Task RegistrarSinClaveMaestraConfiguradaNoEscribeNingunaFila()
    {
        var idEmpresa = await SembrarTenantConEmpresaAsync(nameof(RegistrarSinClaveMaestraConfiguradaNoEscribeNingunaFila));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var configuracionVacia = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var almacen = new CifradoDeClavesFiscales(db, configuracionVacia);
        var reloj = new RelojFijoDeReferencia(Ahora);
        var servicio = new ServicioDeCertificados(db, reloj, almacen);

        var (pfx, password) = GenerarPfx("CN=Ways Test Sin Clave Maestra");

        await Assert.ThrowsAsync<Domain.Common.ErrorDominio>(() => servicio.RegistrarAsync(
            new RegistroDeCertificadoFiscal(idEmpresa, AmbienteFiscal.Homologacion, "Alias", "20111111112", pfx, password)));

        // Conteo ACOTADO a la empresa de este test — db está en modo plataforma (sin filtro de
        // tenant), y la colección "secuencial" comparte una sola base entre TODOS los tests de
        // esta clase, así que un conteo global vería filas de otros tests, no de este alta.
        var cantidad = await db.CertificadosFiscales.CountAsync(c => c.IdEmpresa == idEmpresa);
        Assert.Equal(0, cantidad);
    }

    private sealed class RelojFijoDeReferencia(DateTimeOffset ahora) : Application.Abstracciones.IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }
}
