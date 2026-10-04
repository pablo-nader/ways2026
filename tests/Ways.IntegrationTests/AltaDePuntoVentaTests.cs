using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Alta de puntos de venta sobre una empresa existente (<c>POST /api/puntos-venta</c>), contra
/// Postgres real. Cierra la latencia OD5 de la etapa 20 para puntos de venta: hasta acá el único
/// camino que creaba uno era el aprovisionamiento de un tenant.
///
/// Las cláusulas que no se pueden ver desde la fila que el alta escribe tienen su propia prueba:
/// la estrategia SIN reintento (un fallo transitorio inducido de verdad, con los reintentos
/// activos) y el lock contra la baja concurrente de la empresa (rendezvous determinístico, por
/// debajo del pre-chequeo de 404).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AltaDePuntoVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(RolConocido rol, int usuarioId, int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol { get; } = rol;
        public int? IdTenant { get; } = idTenant;
    }

    private sealed record Sembrado(
        Tenant Tenant, Empresa Empresa, PuntoVenta PuntoVenta, string MailAdmin, int IdAdmin);

    private WaysDbContext ContextoDePlataforma() =>
        fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

    private async Task<Sembrado> SembrarTenantAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}";
        var hasheador = new HasheadorPbkdf2();
        await using var db = ContextoDePlataforma();

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = unico, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id, RazonSocial = $"{unico} SRL", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = $"{unico} - Local 1",
            Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var admin = NuevoUsuario(tenant.Id, RolConocido.Admin, mailAdmin, hasheador, ahora);
        db.Usuarios.Add(admin);
        await db.SaveChangesAsync();

        return new Sembrado(tenant, empresa, puntoVenta, mailAdmin, admin.Id);
    }

    private static Usuario NuevoUsuario(
        int idTenant, RolConocido rol, string mail, HasheadorPbkdf2 hasheador, DateTimeOffset ahora) => new()
    {
        IdTenant = idTenant,
        NombreUsuario = $"{rol}-{Guid.NewGuid().ToString("N")[..6]}".ToLowerInvariant(),
        Mail = mail,
        RolId = (int)rol,
        PasswordHash = hasheador.Hashear(Password),
        PasswordAlgoritmo = hasheador.Algoritmo,
        PasswordActualizadoEl = ahora,
        CreatedAt = ahora,
        UpdatedAt = ahora
    };

    private async Task<Empresa> SembrarEmpresaAsync(int idTenant, string razonSocial)
    {
        await using var db = ContextoDePlataforma();
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa
        {
            IdTenant = idTenant, RazonSocial = razonSocial, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa;
    }

    private async Task<HttpClient> ClienteAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private Task<HttpClient> ClienteComoAdminAsync(Sembrado s) => ClienteAsync(s.MailAdmin, Password);

    private Task<HttpClient> ClienteComoRootAsync() => ClienteAsync(MailRoot, PasswordRoot);

    private async Task<int> CantidadDePuntosVentaAsync(int idEmpresa)
    {
        await using var db = ContextoDePlataforma();
        return await db.PuntosVenta.IgnoreQueryFilters().CountAsync(p => p.IdEmpresa == idEmpresa);
    }

    private static async Task<JsonElement> ProblemaAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);

        return JsonDocument.Parse(cuerpo).RootElement.Clone();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // ---- camino feliz ---------------------------------------------------------------------------

    /// <summary>Round-trip de CADA campo del DTO (<c>dto-contract-honesty</c>): lo que se manda es lo
    /// que se lee de la respuesta, del listado y de la fila. El nombre llega con espacios y se
    /// persiste recortado.</summary>
    [Fact]
    public async Task UnAdminCreaUnPuntoDeVentaEnSuEmpresaYQuedaPersistidoListadoYAuditado()
    {
        var s = await SembrarTenantAsync(nameof(UnAdminCreaUnPuntoDeVentaEnSuEmpresaYQuedaPersistidoListadoYAuditado));
        using var cliente = await ClienteComoAdminAsync(s);

        var respuesta = await cliente.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(
            s.Empresa.Id, "  Sucursal Norte  ", ModoPuntoVenta.Escritorio,
            Domicilio: "Calle 1", Horario: "9 a 18", Whatsapp: "1155550000",
            Instagram: "@norte", Facebook: "norte.fb", Web: "https://norte.test"));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var creado = JsonSerializer.Deserialize<PuntoVentaListado>(cuerpo, OpcionesJson)!;
        Assert.Equal($"/api/puntos-venta/{creado.Id}", respuesta.Headers.Location?.OriginalString);
        Assert.Equal(s.Tenant.Id, creado.IdTenant);
        Assert.Equal(s.Empresa.Id, creado.IdEmpresa);
        Assert.Equal("Sucursal Norte", creado.Nombre);
        Assert.Equal(ModoPuntoVenta.Escritorio, creado.Modo);
        Assert.Equal("Calle 1", creado.Domicilio);
        Assert.Equal("9 a 18", creado.Horario);
        Assert.Equal("1155550000", creado.Whatsapp);
        Assert.Equal("@norte", creado.Instagram);
        Assert.Equal("norte.fb", creado.Facebook);
        Assert.Equal("https://norte.test", creado.Web);
        Assert.Equal(s.Empresa.RazonSocial, creado.RazonSocialEmpresa);

        var listado = await cliente.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        Assert.Contains(listado!, p => p.Id == creado.Id && p.Nombre == "Sucursal Norte");

        await using var db = ContextoDePlataforma();
        var fila = await db.PuntosVenta.SingleAsync(p => p.Id == creado.Id);
        Assert.Equal(s.Tenant.Id, fila.IdTenant);
        Assert.Equal(ModoPuntoVenta.Escritorio, fila.Modo);
        Assert.Equal("Calle 1", fila.Domicilio);
        Assert.Equal("norte.fb", fila.Facebook);

        var rastro = await db.Auditoria.IgnoreQueryFilters()
            .SingleAsync(a => a.Accion == "pv.alta" && a.IdEntidad == creado.Id);
        Assert.Equal(s.Tenant.Id, rastro.IdTenant);
        Assert.Equal(creado.Id, rastro.IdPuntoVenta);
        Assert.Equal("punto_venta", rastro.Entidad);
        Assert.Null(rastro.ValorAnterior);

        var nuevo = JsonDocument.Parse(rastro.ValorNuevo).RootElement;
        Assert.Equal(s.Empresa.Id, nuevo.GetProperty("id_empresa").GetInt32());
        Assert.Equal("Sucursal Norte", nuevo.GetProperty("nombre").GetString());
        Assert.Equal("escritorio", nuevo.GetProperty("modo").GetString());
    }

    /// <summary>Plataforma no tiene tenant: el <c>id_tenant</c> de la fila y del rastro tiene que
    /// salir de la EMPRESA, no de la sesión.</summary>
    [Fact]
    public async Task PlataformaCreaUnPuntoDeVentaBajoLaEmpresaDeUnTenantYHeredaSuTenant()
    {
        var s = await SembrarTenantAsync(nameof(PlataformaCreaUnPuntoDeVentaBajoLaEmpresaDeUnTenantYHeredaSuTenant));
        using var cliente = await ClienteComoRootAsync();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(s.Empresa.Id, "Creado por plataforma", ModoPuntoVenta.Web));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var creado = JsonSerializer.Deserialize<PuntoVentaListado>(cuerpo, OpcionesJson)!;
        Assert.Equal(s.Tenant.Id, creado.IdTenant);

        await using var db = ContextoDePlataforma();
        var rastro = await db.Auditoria.IgnoreQueryFilters()
            .SingleAsync(a => a.Accion == "pv.alta" && a.IdEntidad == creado.Id);
        Assert.Equal(s.Tenant.Id, rastro.IdTenant);
    }

    // ---- validación del cuerpo ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"idEmpresa":__E__,"nombre":"X"}""", "modo_requerido")]
    [InlineData("""{"nombre":"X","modo":"Web"}""", "empresa_requerida")]
    [InlineData("""{"idEmpresa":__E__,"nombre":"X","modo":7}""", "modo_invalido")]
    [InlineData("""{"idEmpresa":__E__,"nombre":"   ","modo":"Web"}""", "nombre_punto_venta_requerido")]
    [InlineData("""{"idEmpresa":__E__,"modo":"Web"}""", "nombre_punto_venta_requerido")]
    public async Task UnCuerpoInvalidoEs400ConSuCodigoYNoCreaNada(string plantilla, string codigoEsperado)
    {
        var s = await SembrarTenantAsync("cuerpo-invalido");
        using var cliente = await ClienteComoAdminAsync(s);
        var antes = await CantidadDePuntosVentaAsync(s.Empresa.Id);

        var respuesta = await cliente.PostAsync(
            "/api/puntos-venta", Json(plantilla.Replace("__E__", s.Empresa.Id.ToString())));

        var problema = await ProblemaAsync(respuesta, HttpStatusCode.BadRequest);
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
        Assert.Equal(antes, await CantidadDePuntosVentaAsync(s.Empresa.Id));
    }

    [Theory]
    [InlineData("nombre", 151, "nombre_punto_venta_muy_largo")]
    [InlineData("domicilio", 256, "domicilio_muy_largo")]
    [InlineData("horario", 256, "horario_muy_largo")]
    [InlineData("whatsapp", 31, "whatsapp_muy_largo")]
    [InlineData("instagram", 151, "instagram_muy_largo")]
    [InlineData("facebook", 151, "facebook_muy_largo")]
    [InlineData("web", 256, "sitio_web_muy_largo")]
    public async Task UnCampoQueSuperaSuLargoMaximoEs400YNoCreaNada(string campo, int largo, string codigoEsperado)
    {
        var s = await SembrarTenantAsync("largo-maximo");
        using var cliente = await ClienteComoAdminAsync(s);
        var antes = await CantidadDePuntosVentaAsync(s.Empresa.Id);

        var cuerpo = new Dictionary<string, object?>
        {
            ["idEmpresa"] = s.Empresa.Id,
            ["nombre"] = "Valido",
            ["modo"] = "Web",
            [campo] = new string('a', largo)
        };
        var respuesta = await cliente.PostAsJsonAsync("/api/puntos-venta", cuerpo);

        var problema = await ProblemaAsync(respuesta, HttpStatusCode.BadRequest);
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
        Assert.Equal(antes, await CantidadDePuntosVentaAsync(s.Empresa.Id));
    }

    // ---- alcance y estado de la empresa ---------------------------------------------------------

    [Fact]
    public async Task UnAdminNoPuedeCrearBajoLaEmpresaDeOtroTenantYRecibe404()
    {
        var a = await SembrarTenantAsync(nameof(UnAdminNoPuedeCrearBajoLaEmpresaDeOtroTenantYRecibe404) + "-A");
        var b = await SembrarTenantAsync(nameof(UnAdminNoPuedeCrearBajoLaEmpresaDeOtroTenantYRecibe404) + "-B");
        using var cliente = await ClienteComoAdminAsync(a);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(b.Empresa.Id, "Intruso", ModoPuntoVenta.Web));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal(1, await CantidadDePuntosVentaAsync(b.Empresa.Id));
    }

    [Fact]
    public async Task UnaEmpresaInexistenteEs404()
    {
        var s = await SembrarTenantAsync(nameof(UnaEmpresaInexistenteEs404));
        using var cliente = await ClienteComoAdminAsync(s);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(999_999, "Fantasma", ModoPuntoVenta.Web));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaDadaDeBajaEs404ParaAdminYParaPlataformaYNoCreaNada()
    {
        var s = await SembrarTenantAsync(nameof(UnaEmpresaDadaDeBajaEs404ParaAdminYParaPlataformaYNoCreaNada));
        var dadaDeBaja = await SembrarEmpresaAsync(s.Tenant.Id, "Empresa dada de baja");

        await using (var db = ContextoDePlataforma())
        {
            var fila = await db.Empresas.SingleAsync(e => e.Id == dadaDeBaja.Id);
            fila.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var admin = await ClienteComoAdminAsync(s);
        using var root = await ClienteComoRootAsync();

        foreach (var cliente in new[] { admin, root })
        {
            var respuesta = await cliente.PostAsJsonAsync(
                "/api/puntos-venta", new PuntoVentaAlta(dadaDeBaja.Id, "Sobre una baja", ModoPuntoVenta.Web));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        Assert.Equal(0, await CantidadDePuntosVentaAsync(dadaDeBaja.Id));
    }

    // ---- autorización ---------------------------------------------------------------------------

    [Theory]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Vendedor)]
    public async Task UnRolSinGestionDeOrganizacionRecibe403YNoCreaNada(RolConocido rol)
    {
        var s = await SembrarTenantAsync("sin-permiso-" + rol);
        var mail = $"{rol}-{Guid.NewGuid():N}@ways.test".ToLowerInvariant();

        await using (var db = ContextoDePlataforma())
        {
            db.Usuarios.Add(NuevoUsuario(s.Tenant.Id, rol, mail, new HasheadorPbkdf2(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var cliente = await ClienteAsync(mail, Password);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(s.Empresa.Id, "No autorizado", ModoPuntoVenta.Web));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal(1, await CantidadDePuntosVentaAsync(s.Empresa.Id));
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(1, "Anónimo", ModoPuntoVenta.Web));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    // ---- reintento (ef-retry-safe-writes) -------------------------------------------------------

    /// <summary>
    /// LA CLÁUSULA de la estrategia sin reintento. El contexto tiene los reintentos ACTIVOS: la única
    /// razón por la que el alta no reintenta es la que elige el código de producción. El fallo
    /// transitorio (<c>40001</c>) se inyecta sobre cada una de las dos escrituras del alta; en las dos
    /// la escritura se intenta UNA sola vez, el error llega tal cual y no queda ni el punto de venta
    /// (ya insertado en el primer caso, antes de que falle el rastro) ni el rastro. Con la estrategia
    /// reintentable el segundo intento comitearía un punto de venta DUPLICADO.
    /// </summary>
    [Theory]
    [InlineData("puntos_venta")]
    [InlineData("auditoria")]
    public async Task UnaFallaTransitoriaNoSeReintentaYNoDejaNiPuntoDeVentaNiRastro(string tablaQueFalla)
    {
        var s = await SembrarTenantAsync("reintento-" + tablaQueFalla);
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura(tablaQueFalla, "40001");
        var antes = await CantidadDePuntosVentaAsync(s.Empresa.Id);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(
            TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = ServicioSobre(db, s.IdAdmin);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearPuntoVentaAsync(
                new PuntoVentaAlta(s.Empresa.Id, "Con falla", ModoPuntoVenta.Web)));

            Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, await CantidadDePuntosVentaAsync(s.Empresa.Id));

        await using var verificacion = ContextoDePlataforma();
        Assert.False(await verificacion.Auditoria.IgnoreQueryFilters()
            .AnyAsync(a => a.Accion == "pv.alta" && a.IdTenant == s.Tenant.Id));

        // Y la misma alta, sin la falla, escribe exactamente una fila y un rastro.
        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma))
        {
            await ServicioSobre(db, s.IdAdmin).CrearPuntoVentaAsync(
                new PuntoVentaAlta(s.Empresa.Id, "Sin falla", ModoPuntoVenta.Web));
        }

        Assert.Equal(antes + 1, await CantidadDePuntosVentaAsync(s.Empresa.Id));
        Assert.Equal(1, await verificacion.Auditoria.IgnoreQueryFilters()
            .CountAsync(a => a.Accion == "pv.alta" && a.IdTenant == s.Tenant.Id));
    }

    private static ServicioDeOrganizacion ServicioSobre(WaysDbContext db, int idActor)
    {
        var contexto = new ContextoFijo(RolConocido.Root, idActor, idTenant: null);
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);

        return new ServicioDeOrganizacion(
            db, reloj, contexto, new InspectorDeUso(db), new ServicioDeAuditoria(db, reloj, contexto));
    }

    private static PostgresException ErrorDePostgres(Exception error)
    {
        for (Exception? actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException($"La excepción no envuelve ninguna PostgresException: {error}");
    }

    // ---- carrera contra la baja de la empresa (single-read-under-lock) --------------------------

    /// <summary>
    /// La lectura de la empresa nace DENTRO de la transacción, después del pre-chequeo de 404. El
    /// alta (perdedor) abre su transacción y se pausa antes de tomar el lock; mientras tanto la baja
    /// de la empresa (ganador) corre completa y comitea. Con la lectura dentro de la transacción, el
    /// alta ve la empresa dada de baja y es un 404. Sin ella inserta un punto de venta VIVO bajo una
    /// empresa muerta: la FK compuesta no mira <c>deleted_at</c>, así que nada más lo frenaría. La
    /// baja comitea antes de que el alta llegue al lock, así que este test no ejercita la contención
    /// del lock: su presencia y su orden los fija el test estructural de <c>BajasEstructuralesTests</c>.
    /// </summary>
    [Fact]
    public async Task UnAltaQuePierdeLaCarreraContraLaBajaDeLaEmpresaEs404YNoDejaUnPuntoDeVentaHuerfano()
    {
        var s = await SembrarTenantAsync(
            nameof(UnAltaQuePierdeLaCarreraContraLaBajaDeLaEmpresaEs404YNoDejaUnPuntoDeVentaHuerfano));
        var segunda = await SembrarEmpresaAsync(s.Tenant.Id, "Segunda empresa");

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clientePerdedor = factory.CreateClient();
        var login = await clientePerdedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(s.MailAdmin, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaPerdedora = clientePerdedor.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(segunda.Id, "Perdedor", ModoPuntoVenta.Web));

        await transaccionIniciada.Task;

        await using (var db = ContextoDePlataforma())
        {
            await ServicioSobre(db, s.IdAdmin).EliminarEmpresaAsync(segunda.Id);
        }

        puedeContinuar.TrySetResult();

        var respuesta = await tareaPerdedora;
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);

        await using var verificacion = ContextoDePlataforma();
        Assert.False(await verificacion.PuntosVenta.IgnoreQueryFilters()
            .AnyAsync(p => p.IdEmpresa == segunda.Id && p.Nombre == "Perdedor"));
        Assert.False(await verificacion.Auditoria.IgnoreQueryFilters()
            .AnyAsync(a => a.Accion == "pv.alta" && a.IdTenant == s.Tenant.Id));
    }

    // ---- la baja de puntos de venta ahora es alcanzable por la API ------------------------------

    /// <summary>Con la alta por API, el segundo punto de venta ya no se siembra a mano: el mínimo
    /// estructural deja de disparar y el guard de uso (<c>punto_venta_en_uso</c>) es alcanzable
    /// punta a punta. Los tests de la etapa 20 que lo sembraban por debajo siguen siendo válidos.</summary>
    [Fact]
    public async Task LaBajaDelPuntoDeVentaEsAlcanzablePorLaApiYElGuardDeUsoDisparaConUnTurno()
    {
        var s = await SembrarTenantAsync(nameof(LaBajaDelPuntoDeVentaEsAlcanzablePorLaApiYElGuardDeUsoDisparaConUnTurno));
        using var cliente = await ClienteComoAdminAsync(s);

        var alta = await cliente.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(s.Empresa.Id, "Segundo", ModoPuntoVenta.Web));
        var segundo = (await alta.Content.ReadFromJsonAsync<PuntoVentaListado>(OpcionesJson))!;

        await using (var db = ContextoDePlataforma())
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = s.Tenant.Id,
                IdPuntoVenta = segundo.Id,
                IdEmpleadoApertura = s.IdAdmin,
                FechaApertura = ahora.AddMinutes(1),
                FondoInicial = 0m,
                Estado = EstadoTurno.Abierto,
                CreatedAt = ahora.AddMinutes(1),
                UpdatedAt = ahora.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }

        var enUso = await cliente.DeleteAsync($"/api/puntos-venta/{segundo.Id}");
        var problema = await ProblemaAsync(enUso, HttpStatusCode.Conflict);
        Assert.Equal("punto_venta_en_uso", problema.GetProperty("codigo").GetString());

        var sinUso = await cliente.DeleteAsync($"/api/puntos-venta/{s.PuntoVenta.Id}");
        Assert.Equal(HttpStatusCode.NoContent, sinUso.StatusCode);

        var ultimo = await cliente.DeleteAsync($"/api/puntos-venta/{segundo.Id}");
        var minimo = await ProblemaAsync(ultimo, HttpStatusCode.Conflict);
        Assert.Equal("ultimo_punto_venta_de_la_empresa", minimo.GetProperty("codigo").GetString());
    }
}
