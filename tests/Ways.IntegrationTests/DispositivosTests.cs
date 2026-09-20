using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos: vinculación de dispositivos (<c>POST/GET/DELETE /api/dispositivos</c>),
/// resolución anónima (<c>GET /api/dispositivos/actual</c>) y login de cajero
/// (<c>POST /api/auth/login-dispositivo</c>). Corre contra Postgres real, con la migración
/// <c>DispositivosPos</c> ya aplicada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DispositivosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Aprovisiona un tenant completo (tenant + empresa + PV + admin) y devuelve un
    /// HttpClient YA logueado como ese admin, más los ids relevantes. <paramref name="modo"/>
    /// default Escritorio (el modo que necesita casi todo este archivo, que es sobre dispositivos);
    /// judgment-day ronda 1 (hallazgo CRITICAL 4c) le agregó el parámetro para poder aprovisionar
    /// también un punto de venta Web.</summary>
    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta, string MailAdmin)>
        AprovisionarYLoguearComoAdminAsync(string nombre, ModoPuntoVenta modo = ModoPuntoVenta.Escritorio)
    {
        using var root = await ClienteComoRootAsync();

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nombre,
            RazonSocialEmpresa: $"Empresa {nombre}",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nombre.ToLowerInvariant()}-admin@ways.test",
            Modo: modo);

        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (cliente, resultado.IdTenant, resultado.IdPuntoVenta, solicitud.MailAdmin);
    }

    /// <summary>Siembra un cajero (Vendedor, por default) directo en la base, con hash real — la
    /// API bajo prueba en estos tests es la de dispositivos/login-dispositivo, no la de alta de
    /// usuarios (mismo criterio que <c>UsuariosYLoginTests.SembrarTenantConUsuarioAsync</c>).</summary>
    private async Task SembrarCajeroAsync(
        int idTenant, string nombreUsuario, string password, RolConocido rol = RolConocido.Vendedor)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = nombreUsuario,
            Mail = $"{nombreUsuario}-{idTenant}@ways.test",
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Extrae el valor crudo de la cookie <c>ways.dispositivo</c> de un
    /// <see cref="HttpResponseMessage"/> — necesario porque la resolución del dispositivo
    /// (<c>actual</c>/<c>login-dispositivo</c>) es anónima y corre en un cliente/proceso
    /// distinto del que hizo el <c>POST /api/dispositivos</c> como admin.</summary>
    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Manda una request anónima llevando la cookie de dispositivo SOLO en ese mensaje
    /// (nunca como header default del cliente): el cliente en sí sigue con
    /// <c>HandleCookies = true</c> (default de <c>WebApplicationFactory</c>), así que un
    /// <c>Set-Cookie: ways.sesion=...</c> de la respuesta (login-dispositivo exitoso) queda
    /// guardado en su cookie jar y viaja solo en los requests siguientes — evita el riesgo de
    /// mandar dos líneas "Cookie:" que tendría un header default a nivel cliente conviviendo con
    /// el cookie jar automático.</summary>
    private static async Task<HttpResponseMessage> EnviarConCookieDeDispositivoAsync(
        HttpClient cliente, HttpMethod metodo, string ruta, string cookieDispositivo, object? contenido = null)
    {
        using var request = new HttpRequestMessage(metodo, ruta);
        if (contenido is not null)
        {
            request.Content = JsonContent.Create(contenido);
        }

        request.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        return await cliente.SendAsync(request);
    }

    [Fact]
    public async Task UnAdminVinculaUnDispositivoYRecibeLaCookieConElCuerpoEsperado()
    {
        var (cliente, idTenant, idPuntoVenta, _) =
            await AprovisionarYLoguearComoAdminAsync(nameof(UnAdminVinculaUnDispositivoYRecibeLaCookieConElCuerpoEsperado));
        using var _cliente = cliente;

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Contains(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith($"{CookieDispositivo}=", StringComparison.Ordinal));

        var actual = await respuesta.Content.ReadFromJsonAsync<DispositivoActual>();
        Assert.NotNull(actual);
        Assert.Equal("Caja 1", actual!.Nombre);
        Assert.Equal(idPuntoVenta, actual.IdPuntoVenta);
        Assert.Equal(idPuntoVenta, actual.PuntoVenta.Numero);
        Assert.Equal("Local 1", actual.PuntoVenta.Nombre);
        Assert.Equal(
            $"Empresa {nameof(UnAdminVinculaUnDispositivoYRecibeLaCookieConElCuerpoEsperado)}", actual.Empresa.Nombre);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var fila = await db.Dispositivos.FirstOrDefaultAsync(d => d.Id == actual.Id);
        Assert.NotNull(fila);
        Assert.Equal(idTenant, fila!.IdTenant);
        Assert.Equal(64, fila.TokenHash.Length);
    }

    /// <summary>stage-desktop-pos (db-error-backstops): invariante "una PC-caja = un punto de
    /// venta" — dos vinculaciones concurrentes al MISMO punto de venta chocan contra
    /// <c>ux_dispositivos_punto_venta_activo</c>, ganando exactamente una. Mismo patrón de carrera
    /// que <c>ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoInternoProvistoDaExactamenteUnGanador</c>.</summary>
    [Fact]
    public async Task LaVinculacionConcurrenteDeDosDispositivosAlMismoPuntoDeVentaDaExactamenteUnGanador()
    {
        var (cliente, _, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(LaVinculacionConcurrenteDeDosDispositivosAlMismoPuntoDeVentaDaExactamenteUnGanador));
        using var _cliente = cliente;

        var tareaA = cliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja A"));
        var tareaB = cliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja B"));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_ya_tiene_dispositivo", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day ronda 1 (hallazgo CRITICAL 4c): el guard de modo de
    /// <c>ServicioDeDispositivos.CrearAsync</c> (409 <c>punto_venta_modo_incompatible</c>) no
    /// tenía NINGÚN test. Este test cubre SOLO el pre-chequeo best-effort: con un punto de venta
    /// que YA es Web al momento de leerlo, ese pre-chequeo lanza antes de que la transacción se
    /// abra — el re-chequeo bajo <c>FOR UPDATE</c> de <c>BloquearYLeerModoDePuntoVentaAsync</c>
    /// nunca se alcanza acá. La prueba de ESE statement es la rendezvous
    /// <see cref="CrearDispositivoCuyaTransaccionYaArrancoCuandoElFlipDeModoComiteaSeRechazaBajoElRechequeo"/>,
    /// que fuerza el flip de modo DESPUÉS del pre-chequeo, dentro de la ventana de la
    /// transacción.</summary>
    [Fact]
    public async Task VincularUnDispositivoAUnPuntoVentaWebDaModoIncompatible()
    {
        var (cliente, _, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(VincularUnDispositivoAUnPuntoVentaWebDaModoIncompatible), ModoPuntoVenta.Web);
        using var _cliente = cliente;

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja web"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnVendedorNoPuedeVincularUnDispositivo()
    {
        var (adminCliente, idTenant, idPuntoVenta, _) =
            await AprovisionarYLoguearComoAdminAsync(nameof(UnVendedorNoPuedeVincularUnDispositivo));
        adminCliente.Dispose();

        await SembrarCajeroAsync(idTenant, "vendedor1", PasswordCajero);

        using var cliente = fixture.CreateClient();
        var loginVendedor = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin($"vendedor1-{idTenant}@ways.test", PasswordCajero));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task VincularAUnPuntoDeVentaDeOtroTenantDaNoEncontrado()
    {
        var (clienteA, _, _, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(VincularAUnPuntoDeVentaDeOtroTenantDaNoEncontrado) + "A");
        using var _clienteA = clienteA;
        var (clienteB, _, idPuntoVentaB, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(VincularAUnPuntoDeVentaDeOtroTenantDaNoEncontrado) + "B");
        clienteB.Dispose();

        var respuesta = await clienteA.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVentaB, "Caja ajena"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task VincularAUnPuntoDeVentaDadoDeBajaDaNoEncontrado()
    {
        var (cliente, _, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(VincularAUnPuntoDeVentaDadoDeBajaDaNoEncontrado));
        using var _cliente = cliente;

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == idPuntoVenta);
            pv.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja dada de baja"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task ActualSinCookieDaDispositivoNoVinculado()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/dispositivos/actual");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dispositivo_no_vinculado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ActualConCookieDesconocidaDaDispositivoNoVinculado()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await EnviarConCookieDeDispositivoAsync(
            cliente, HttpMethod.Get, "/api/dispositivos/actual", "token-que-nunca-existio");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task ActualConDispositivoRevocadoDaDispositivoNoVinculado()
    {
        var (cliente, _, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(ActualConDispositivoRevocadoDaDispositivoNoVinculado));
        using var _cliente = cliente;

        var alta = await cliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var actual = (await alta.Content.ReadFromJsonAsync<DispositivoActual>())!;
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var revocar = await cliente.DeleteAsync($"/api/dispositivos/{actual.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revocar.StatusCode);

        using var anonimo = fixture.CreateClient();
        var respuesta = await EnviarConCookieDeDispositivoAsync(
            anonimo, HttpMethod.Get, "/api/dispositivos/actual", cookieDispositivo);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task LoginDeDispositivoConUsuarioValidoFuncionaYRegistraElUso()
    {
        var (adminCliente, idTenant, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(LoginDeDispositivoConUsuarioValidoFuncionaYRegistraElUso));

        var alta = await adminCliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var actual = (await alta.Content.ReadFromJsonAsync<DispositivoActual>())!;
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);
        adminCliente.Dispose();

        await SembrarCajeroAsync(idTenant, "cajero1", PasswordCajero);

        // cajero: HandleCookies = true (default) — la cookie de dispositivo viaja SOLO en este
        // primer mensaje; ways.sesion, una vez emitida, queda en el cookie jar del cliente y
        // viaja sola en los requests siguientes (/me).
        using var cajero = fixture.CreateClient();
        var login = await EnviarConCookieDeDispositivoAsync(
            cajero, HttpMethod.Post, "/api/auth/login-dispositivo", cookieDispositivo,
            new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var usuario = await login.Content.ReadFromJsonAsync<UsuarioAutenticado>();
        Assert.NotNull(usuario);
        Assert.Equal(idTenant, usuario!.IdTenant);

        // La sesión sirve en un endpoint protegido (cualquiera autenticado).
        var me = await cajero.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var fila = await db.Dispositivos.FirstAsync(d => d.Id == actual.Id);
        Assert.NotNull(fila.UltimoUsoAt);
    }

    [Fact]
    public async Task LoginDeDispositivoConPasswordIncorrectaDevuelveCredencialesInvalidas()
    {
        var (adminCliente, idTenant, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(LoginDeDispositivoConPasswordIncorrectaDevuelveCredencialesInvalidas));
        var alta = await adminCliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);
        adminCliente.Dispose();

        await SembrarCajeroAsync(idTenant, "cajero1", PasswordCajero);

        using var cajero = fixture.CreateClient();
        var login = await EnviarConCookieDeDispositivoAsync(
            cajero, HttpMethod.Post, "/api/auth/login-dispositivo", cookieDispositivo,
            new SolicitudDeLoginDeDispositivo("cajero1", "password-incorrecta"));

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var fila = await db.Usuarios.FirstAsync(u => u.IdTenant == idTenant && u.NombreUsuario == "cajero1");
        Assert.Equal(1, fila.IntentosFallidos);
    }

    /// <summary>
    /// Mutation-proof (skill <c>mutation-proof-tests</c>), TOMO 1 — end-to-end vía HTTP. La
    /// cláusula NOMINAL bajo prueba es <c>u.IdTenant == idTenantDispositivo</c> en
    /// <c>ServicioDeAutenticacion.IniciarSesionDeDispositivoAsync</c>. El cajero "cajero1" existe
    /// ÚNICAMENTE en el tenant B, con contraseña real conocida.
    ///
    /// <para>MUTADO Y CORRIDO DE VERDAD (no solo razonado): comentar esa cláusula deja ESTE test
    /// en VERDE igual — CONFOUND real, regla 3 del skill. <c>AuthEndpoints</c> pone el contexto
    /// en modo <c>Tenant(idTenantA)</c> ANTES de llamar al servicio, y el filtro AMBIENTE de EF
    /// sobre <c>Usuario</c> (<c>WaysDbContext.AplicarFiltroDeTenantEnUsuario</c>) ya excluye la
    /// fila de "cajero1" de tenant B por sí solo — el predicado explícito de la consulta es una
    /// segunda capa redundante bajo el flujo HTTP normal. Este test por sí solo NO mata ese
    /// mutante; se conserva como regresión end-to-end real (documenta que el sistema completo es
    /// seguro), pero la prueba que efectivamente mata la cláusula es TOMO 2, más abajo, que llama
    /// al servicio bajo modo Plataforma (sin el filtro ambiente) para aislar el predicado.</para>
    /// </summary>
    [Fact]
    public async Task UnUsuarioDeOtroTenantConElMismoNombreNoPuedeEntrarPorEsteDispositivo()
    {
        var (clienteA, idTenantA, idPuntoVentaA, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(UnUsuarioDeOtroTenantConElMismoNombreNoPuedeEntrarPorEsteDispositivo) + "A");
        var altaA = await clienteA.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVentaA, "Caja A"));
        var cookieDispositivoA = ExtraerCookieDeDispositivo(altaA);
        clienteA.Dispose();

        var (clienteB, idTenantB, _, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(UnUsuarioDeOtroTenantConElMismoNombreNoPuedeEntrarPorEsteDispositivo) + "B");
        clienteB.Dispose();

        Assert.NotEqual(idTenantA, idTenantB);
        await SembrarCajeroAsync(idTenantB, "cajero1", PasswordCajero);

        using var atacante = fixture.CreateClient();
        var login = await EnviarConCookieDeDispositivoAsync(
            atacante, HttpMethod.Post, "/api/auth/login-dispositivo", cookieDispositivoA,
            new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero));

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    /// <summary>
    /// Mutation-proof, TOMO 2 — el que realmente mata la cláusula (mutation-proof-tests regla 3:
    /// "route the test BELOW the confound"). Llama a <c>ServicioDeAutenticacion</c> DIRECTO, con
    /// un <see cref="IWaysDbContext"/> en modo <see cref="TenantActualFijo.Plataforma"/> — bajo
    /// ese modo el filtro ambiente de EF sobre <c>Usuario</c> pasa CUALQUIER tenant
    /// (<c>EsPlataforma</c> es verdadero), así que el ÚNICO guardián que queda en pie es el
    /// predicado explícito <c>u.IdTenant == idTenantDispositivo</c> de la consulta.
    ///
    /// <para>EVIDENCIA DE MUTACIÓN REGISTRADA (reproducida a mano en esta sesión): se comentó la
    /// cláusula <c>u.IdTenant == idTenantDispositivo &amp;&amp;</c> en
    /// <c>ServicioDeAutenticacion.IniciarSesionDeDispositivoAsync</c> (dejando solo
    /// <c>u.NombreUsuario == nombreUsuario</c>) y se corrió ÚNICAMENTE este test: pasó de VERDE
    /// (lanza <c>ErrorDominio credenciales_invalidas</c>) a ROJO — <c>Assert.ThrowsAsync</c> no
    /// lanzó nada, la llamada devolvió un <c>UsuarioAutenticado</c> válido para "cajero1" de
    /// tenant B a través del contexto de tenant A. Se revirtió la mutación y el test volvió a
    /// VERDE.</para>
    /// </summary>
    [Fact]
    public async Task ElServicioFiltraPorTenantAunSinElFiltroAmbienteDeEfBajoModoPlataforma()
    {
        var (clienteA, idTenantA, _, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(ElServicioFiltraPorTenantAunSinElFiltroAmbienteDeEfBajoModoPlataforma) + "A");
        clienteA.Dispose();

        var (clienteB, idTenantB, _, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(ElServicioFiltraPorTenantAunSinElFiltroAmbienteDeEfBajoModoPlataforma) + "B");
        clienteB.Dispose();

        Assert.NotEqual(idTenantA, idTenantB);
        await SembrarCajeroAsync(idTenantB, "cajero1", PasswordCajero);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var servicio = new ServicioDeAutenticacion(
            db, db, new HasheadorPbkdf2(), new Ways.Application.Abstracciones.RelojDelSistema(),
            NullLogger<ServicioDeAutenticacion>.Instance);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.IniciarSesionDeDispositivoAsync(
                idTenantA, new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero)));

        Assert.Equal("credenciales_invalidas", error.Codigo);
    }

    [Fact]
    public async Task RevocarElDispositivoCortaUnaSesionDeCajeroYaAbierta()
    {
        var (adminCliente, idTenant, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(RevocarElDispositivoCortaUnaSesionDeCajeroYaAbierta));
        var alta = await adminCliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        var actual = (await alta.Content.ReadFromJsonAsync<DispositivoActual>())!;
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        await SembrarCajeroAsync(idTenant, "cajero1", PasswordCajero);

        using var cajero = fixture.CreateClient();
        var login = await EnviarConCookieDeDispositivoAsync(
            cajero, HttpMethod.Post, "/api/auth/login-dispositivo", cookieDispositivo,
            new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var meAntes = await cajero.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meAntes.StatusCode);

        var revocar = await adminCliente.DeleteAsync($"/api/dispositivos/{actual.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revocar.StatusCode);
        adminCliente.Dispose();

        var meDespues = await cajero.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meDespues.StatusCode);
    }

    [Fact]
    public async Task DarDeBajaElPuntoDeVentaCortaUnaSesionDeCajeroYaAbierta()
    {
        var (adminCliente, idTenant, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(DarDeBajaElPuntoDeVentaCortaUnaSesionDeCajeroYaAbierta));
        var alta = await adminCliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja 1"));
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);
        adminCliente.Dispose();

        await SembrarCajeroAsync(idTenant, "cajero1", PasswordCajero);

        using var cajero = fixture.CreateClient();
        var login = await EnviarConCookieDeDispositivoAsync(
            cajero, HttpMethod.Post, "/api/auth/login-dispositivo", cookieDispositivo,
            new SolicitudDeLoginDeDispositivo("cajero1", PasswordCajero));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == idPuntoVenta);
            pv.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var meDespues = await cajero.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meDespues.StatusCode);
    }

    [Fact]
    public async Task ListarDispositivosNoMuestraLosDeOtroTenantAunqueTengaElMismoNombre()
    {
        var (clienteA, _, idPuntoVentaA, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(ListarDispositivosNoMuestraLosDeOtroTenantAunqueTengaElMismoNombre) + "A");
        using var _clienteA = clienteA;
        var altaA = await clienteA.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVentaA, "Caja compartida"));
        Assert.Equal(HttpStatusCode.Created, altaA.StatusCode);

        var (clienteB, _, idPuntoVentaB, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(ListarDispositivosNoMuestraLosDeOtroTenantAunqueTengaElMismoNombre) + "B");
        using var _clienteB = clienteB;
        var altaB = await clienteB.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(idPuntoVentaB, "Caja compartida"));
        Assert.Equal(HttpStatusCode.Created, altaB.StatusCode);

        var listadoB = await clienteB.GetFromJsonAsync<List<DispositivoListado>>("/api/dispositivos");
        Assert.NotNull(listadoB);
        Assert.Single(listadoB!);
    }

    /// <summary>judgment-day ronda 1 (hallazgo BLOCKER 1): la carrera real entre
    /// <c>ServicioDeDispositivos.CrearAsync</c> y <c>ServicioDeOrganizacion.ActualizarModoPuntoVentaAsync</c>
    /// sobre el MISMO punto de venta — sin el <c>FOR UPDATE</c> de fila que serializa a los dos, bajo
    /// READ COMMITTED podían leer cada uno el estado PRE-escritura del otro y comitear los dos,
    /// dejando un dispositivo activo vinculado a un punto de venta que acababa de pasar a Web (un
    /// dispositivo que <c>ServicioDeVentas.ResolverPuntoVentaAsync</c> nunca vuelve a aceptar).
    /// Mismo patrón que <see cref="LaVinculacionConcurrenteDeDosDispositivosAlMismoPuntoDeVentaDaExactamenteUnGanador"/>:
    /// dos requests concurrentes sobre el mismo cliente HTTP, <c>Task.WhenAll</c>, exactamente un
    /// ganador — acá además se releé la fila para confirmar la invariante real (nunca modo Web CON
    /// dispositivo activo), no solo los códigos de estado.</summary>
    [Fact]
    public async Task CrearDispositivoYCambiarModoAWebEnParaleloDaExactamenteUnGanadorYSostieneLaInvariante()
    {
        var (cliente, _, idPuntoVenta, _) = await AprovisionarYLoguearComoAdminAsync(
            nameof(CrearDispositivoYCambiarModoAWebEnParaleloDaExactamenteUnGanadorYSostieneLaInvariante));
        using var _cliente = cliente;

        var tareaDispositivo = cliente.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja"));
        var tareaModo = cliente.PostAsJsonAsync(
            $"/api/puntos-venta/{idPuntoVenta}/modo", new PuntoVentaModoEdicion(ModoPuntoVenta.Web));

        var respuestas = await Task.WhenAll(tareaDispositivo, tareaModo);
        var (respuestaDispositivo, respuestaModo) = (respuestas[0], respuestas[1]);

        if (respuestaDispositivo.StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.Conflict, respuestaModo.StatusCode);
            var problema = await respuestaModo.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("punto_venta_con_dispositivo_activo", problema.GetProperty("codigo").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, respuestaDispositivo.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respuestaModo.StatusCode);
            var problema = await respuestaDispositivo.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
        }

        // La invariante real, releída DESPUÉS de que las dos transacciones en pugna comitearon:
        // nunca un dispositivo activo colgado de un punto de venta en modo Web.
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var puntoVentaFinal = await db.PuntosVenta.FirstAsync(p => p.Id == idPuntoVenta);
        var tieneDispositivoActivo = await db.Dispositivos.AnyAsync(d => d.IdPuntoVenta == idPuntoVenta);

        Assert.False(
            puntoVentaFinal.Modo == ModoPuntoVenta.Web && tieneDispositivoActivo,
            "Invariante violada: quedó un dispositivo activo vinculado a un punto de venta Web.");
    }

    /// <summary>Mismo patrón de rendezvous forzado que
    /// <c>ComprasAnulacionYConcurrenciaTests.InterceptorDePausaTrasIniciarLaTransaccion</c>: pausa
    /// justo DESPUÉS de <c>BeginTransactionAsync</c> (antes del primer statement de la transacción),
    /// hasta que el llamador libere <paramref name="puedeContinuar"/>. Reproduce
    /// DETERMINÍSTICAMENTE el interleaving "el flip de modo gana la carrera y commitea ANTES de
    /// que el re-chequeo bajo lock de <c>CrearAsync</c> corra" — la carrera libre por HTTP
    /// (<see cref="CrearDispositivoYCambiarModoAWebEnParaleloDaExactamenteUnGanadorYSostieneLaInvariante"/>)
    /// no puede garantizar esa interleaving en particular, así que este test es el que
    /// efectivamente mata la ausencia del re-chequeo.</summary>
    private sealed class InterceptorDePausaTrasIniciarLaTransaccion(
        TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            transaccionIniciada.TrySetResult();
            await puedeContinuar.Task;
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>judgment-day ronda 1 (hallazgo BLOCKER 1) — evidencia mutation-proof de que el
    /// re-chequeo bajo lock de <c>ServicioDeDispositivos.CrearAsync</c>
    /// (<c>BloquearYLeerModoDePuntoVentaAsync</c>) es lo que de verdad sostiene la invariante, no
    /// el pre-chequeo best-effort de más arriba: la transacción de <c>CrearAsync</c> arranca y
    /// queda PAUSADA (interceptor), el flip de modo a Web corre y COMITEA completo mientras
    /// <c>CrearAsync</c> sigue esperando, y RECIÉN AHÍ se libera <c>CrearAsync</c> — que tiene que
    /// re-leer <c>modo = 'web'</c> bajo su propio lock y rechazar, nunca insertar el
    /// dispositivo.</summary>
    [Fact]
    public async Task CrearDispositivoCuyaTransaccionYaArrancoCuandoElFlipDeModoComiteaSeRechazaBajoElRechequeo()
    {
        using var root = await ClienteComoRootAsync();

        var nombre = nameof(CrearDispositivoCuyaTransaccionYaArrancoCuandoElFlipDeModoComiteaSeRechazaBajoElRechequeo);
        var mailAdmin = $"{nombre.ToLowerInvariant()}-admin@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"Empresa {nombre}", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteDispositivo = factory.CreateClient();
        var loginDispositivo = await clienteDispositivo.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginDispositivo.StatusCode);

        var tareaAlta = clienteDispositivo.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja race"));

        await transaccionIniciada.Task;

        // Con la transacción de CrearAsync ya abierta y PAUSADA (todavía sin tomar el lock de
        // fila), el flip de modo corre COMPLETO en un host/cliente aparte y comitea.
        using var clienteModo = fixture.CreateClient();
        var loginModo = await clienteModo.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginModo.StatusCode);

        var respuestaModo = await clienteModo.PostAsJsonAsync(
            $"/api/puntos-venta/{resultado.IdPuntoVenta}/modo", new PuntoVentaModoEdicion(ModoPuntoVenta.Web));
        var cuerpoModo = await respuestaModo.Content.ReadAsStringAsync();
        Assert.True(respuestaModo.StatusCode == HttpStatusCode.OK, cuerpoModo);

        puedeContinuar.TrySetResult();

        var respuestaAlta = await tareaAlta;

        Assert.Equal(HttpStatusCode.Conflict, respuestaAlta.StatusCode);
        var problema = await respuestaAlta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tieneDispositivoActivo = await db.Dispositivos.AnyAsync(d => d.IdPuntoVenta == resultado.IdPuntoVenta);
        Assert.False(tieneDispositivoActivo);
    }

    /// <summary>El espejo exacto del test anterior, mismo rendezvous forzado — acá la transacción
    /// PAUSADA es la del FLIP DE MODO, y la que corre y comitea completa mientras tanto es
    /// <c>CrearAsync</c>: prueba que <c>ServicioDeOrganizacion.ActualizarModoPuntoVentaAsync</c>
    /// re-chequea "sin dispositivo activo" bajo su PROPIO lock (no el pre-chequeo best-effort de
    /// más arriba) — sin ese re-chequeo, el flip a Web podía comitear sobre un punto de venta que
    /// ACABA de recibir un dispositivo activo mientras la transacción del flip esperaba el
    /// lock.</summary>
    [Fact]
    public async Task CambiarModoAWebCuyaTransaccionYaArrancoCuandoElAltaDeDispositivoComiteaSeRechazaBajoElRechequeo()
    {
        using var root = await ClienteComoRootAsync();

        var nombre = nameof(CambiarModoAWebCuyaTransaccionYaArrancoCuandoElAltaDeDispositivoComiteaSeRechazaBajoElRechequeo);
        var mailAdmin = $"{nombre.ToLowerInvariant()}-admin@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"Empresa {nombre}", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteModo = factory.CreateClient();
        var loginModo = await clienteModo.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginModo.StatusCode);

        var tareaModo = clienteModo.PostAsJsonAsync(
            $"/api/puntos-venta/{resultado.IdPuntoVenta}/modo", new PuntoVentaModoEdicion(ModoPuntoVenta.Web));

        await transaccionIniciada.Task;

        // Con la transacción del flip ya abierta y PAUSADA (todavía sin tomar el lock de fila), el
        // alta de dispositivo corre COMPLETA en un host/cliente aparte y comitea.
        using var clienteDispositivo = fixture.CreateClient();
        var loginDispositivo = await clienteDispositivo.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginDispositivo.StatusCode);

        var respuestaAlta = await clienteDispositivo.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja race"));
        var cuerpoAlta = await respuestaAlta.Content.ReadAsStringAsync();
        Assert.True(respuestaAlta.StatusCode == HttpStatusCode.Created, cuerpoAlta);

        puedeContinuar.TrySetResult();

        var respuestaModo = await tareaModo;

        Assert.Equal(HttpStatusCode.Conflict, respuestaModo.StatusCode);
        var problema = await respuestaModo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_con_dispositivo_activo", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var puntoVentaFinal = await db.PuntosVenta.FirstAsync(p => p.Id == resultado.IdPuntoVenta);
        Assert.Equal(ModoPuntoVenta.Escritorio, puntoVentaFinal.Modo);
    }
}
