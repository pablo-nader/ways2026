using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Caja;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Visibilidad de los turnos de caja para el Vendedor en las lecturas (<c>GET /api/caja/turnos</c> y
/// sus cinco rutas por id). Un turno no visible responde el mismo 404 que uno inexistente. Las
/// escrituras no se tocan. La regla vive en <see cref="PoliticaDeVisibilidadDeTurnos"/>; acá se
/// prueba de punta a punta que cada ruta la consulta ANTES de leer nada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VisibilidadDeTurnosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordDelRol = "una-contraseña-larga";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly string[] RutasPorId =
    [
        "", "/resumen", "/detalle", "/detalle/export?formato=xlsx", "/resumen-de-cierre"
    ];

    private sealed record Escenario(
        int IdTenant,
        HttpClient Admin,
        HttpClient Supervisor,
        HttpClient VendedorA,
        HttpClient CajeroDeDispositivo,
        int CerradoAjeno,
        int CerradoQueCerroA,
        int AbiertoDeBEnElPuntoDeVentaDelDispositivo,
        int AbiertoPropioDeA,
        int AbiertoAjenoDeOtroPuntoDeVenta,
        int AbiertoPorElCajeroEnOtroPuntoDeVenta,
        int CerradoPorElCajeroEnOtroPuntoDeVenta,
        int AbiertoDeBEnUnPuntoDeVentaWeb,
        int CerradoDeBEnUnPuntoDeVentaWeb,
        int IdPuntoVentaWeb,
        int IdPuntoVentaDelDispositivo,
        int IdPuntoVentaEscritorioPropioDeA,
        int IdPuntoVentaEscritorioAjeno);

    // ---- siembra -------------------------------------------------------------------------------

    private async Task<HttpClient> CrearUsuarioYLoguearAsync(
        HttpClient admin, string nombreUsuario, string mail, RolConocido rol)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(nombreUsuario, mail, (int)rol, PasswordDelRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordDelRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<Escenario> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja de escritorio"));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var cookieDelDispositivo = ExtraerCookieDeDispositivo(altaDispositivo);

        var sufijo = resultado.IdTenant;
        var supervisor = await CrearUsuarioYLoguearAsync(
            admin, "supervisor-vis", $"supervisor-vis-{sufijo}@ways.test", RolConocido.Supervisor);
        var vendedorA = await CrearUsuarioYLoguearAsync(
            admin, "vendedor-a", $"vendedor-a-{sufijo}@ways.test", RolConocido.Vendedor);
        _ = await CrearUsuarioYLoguearAsync(
            admin, "vendedor-b", $"vendedor-b-{sufijo}@ways.test", RolConocido.Vendedor);
        _ = await CrearUsuarioYLoguearAsync(
            admin, "cajero-vis", $"cajero-vis-{sufijo}@ways.test", RolConocido.Vendedor);

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-vis", PasswordDelRol))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDelDispositivo}");
        var loginDispositivo = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, loginDispositivo.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idA = await db.Usuarios.Where(u => u.NombreUsuario == "vendedor-a").Select(u => u.Id).FirstAsync();
        var idB = await db.Usuarios.Where(u => u.NombreUsuario == "vendedor-b").Select(u => u.Id).FirstAsync();
        var idCajero = await db.Usuarios.Where(u => u.NombreUsuario == "cajero-vis").Select(u => u.Id).FirstAsync();
        var idEmpresa = await db.PuntosVenta.Where(p => p.Id == resultado.IdPuntoVenta).Select(p => p.IdEmpresa).FirstAsync();

        var ahora = DateTimeOffset.UtcNow;
        var puntoVentaPropio = NuevoPuntoVenta(resultado.IdTenant, idEmpresa, "Local 2", ahora);
        var puntoVentaAjeno = NuevoPuntoVenta(resultado.IdTenant, idEmpresa, "Local 3", ahora);
        var puntoVentaDelCajero = NuevoPuntoVenta(resultado.IdTenant, idEmpresa, "Local 4", ahora);
        var puntoVentaWeb = NuevoPuntoVenta(resultado.IdTenant, idEmpresa, "Web 1", ahora);
        puntoVentaWeb.Modo = ModoPuntoVenta.Web;
        db.PuntosVenta.AddRange(puntoVentaPropio, puntoVentaAjeno, puntoVentaDelCajero, puntoVentaWeb);
        await db.SaveChangesAsync();

        var cerradoAjeno = NuevoTurno(resultado.IdTenant, resultado.IdPuntoVenta, idB, idB, ahora.AddHours(-6), ahora);
        var cerradoQueCerroA = NuevoTurno(resultado.IdTenant, resultado.IdPuntoVenta, idB, idA, ahora.AddHours(-4), ahora);
        var abiertoDeB = NuevoTurno(resultado.IdTenant, resultado.IdPuntoVenta, idB, null, ahora.AddHours(-1), ahora);
        var abiertoPropioDeA = NuevoTurno(resultado.IdTenant, puntoVentaPropio.Id, idA, null, ahora.AddHours(-1), ahora);
        var abiertoAjenoOtroPv = NuevoTurno(resultado.IdTenant, puntoVentaAjeno.Id, idB, null, ahora.AddHours(-1), ahora);
        var abiertoPorElCajeroEnOtroPv = NuevoTurno(resultado.IdTenant, puntoVentaDelCajero.Id, idCajero, null, ahora.AddHours(-2), ahora);
        var cerradoPorElCajeroEnOtroPv = NuevoTurno(resultado.IdTenant, puntoVentaPropio.Id, idB, idCajero, ahora.AddHours(-3), ahora);
        var abiertoWebDeB = NuevoTurno(resultado.IdTenant, puntoVentaWeb.Id, idB, null, ahora.AddHours(-1), ahora);
        var cerradoWebDeB = NuevoTurno(resultado.IdTenant, puntoVentaWeb.Id, idB, idB, ahora.AddHours(-9), ahora);
        db.TurnosCaja.AddRange(
            cerradoAjeno, cerradoQueCerroA, abiertoDeB, abiertoPropioDeA, abiertoAjenoOtroPv,
            abiertoPorElCajeroEnOtroPv, cerradoPorElCajeroEnOtroPv, abiertoWebDeB, cerradoWebDeB);
        await db.SaveChangesAsync();

        return new Escenario(
            resultado.IdTenant, admin, supervisor, vendedorA, cajero,
            cerradoAjeno.Id, cerradoQueCerroA.Id, abiertoDeB.Id, abiertoPropioDeA.Id, abiertoAjenoOtroPv.Id,
            abiertoPorElCajeroEnOtroPv.Id, cerradoPorElCajeroEnOtroPv.Id,
            abiertoWebDeB.Id, cerradoWebDeB.Id, puntoVentaWeb.Id, resultado.IdPuntoVenta, puntoVentaPropio.Id,
            puntoVentaAjeno.Id);
    }

    private static PuntoVenta NuevoPuntoVenta(int idTenant, int idEmpresa, string nombre, DateTimeOffset ahora) => new()
    {
        IdTenant = idTenant, IdEmpresa = idEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
    };

    private static TurnoCaja NuevoTurno(
        int idTenant, int idPuntoVenta, int idApertura, int? idCierre, DateTimeOffset apertura, DateTimeOffset ahora) => new()
    {
        IdTenant = idTenant,
        IdPuntoVenta = idPuntoVenta,
        IdEmpleadoApertura = idApertura,
        IdEmpleadoCierre = idCierre,
        FechaApertura = apertura,
        FechaCierre = idCierre is null ? null : apertura.AddHours(1),
        FondoInicial = 100m,
        Estado = idCierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado,
        CreatedAt = ahora,
        UpdatedAt = ahora
    };

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    // ---- aserciones ----------------------------------------------------------------------------

    private static async Task ExigirNoEncontradoEnLasCincoRutasAsync(HttpClient cliente, int idTurno)
    {
        foreach (var ruta in RutasPorId)
        {
            var respuesta = await cliente.GetAsync($"/api/caja/turnos/{idTurno}{ruta}");
            Assert.True(
                respuesta.StatusCode == HttpStatusCode.NotFound,
                $"GET {ruta} sobre el turno {idTurno}: {(int)respuesta.StatusCode}");
        }
    }

    /// <summary>Las cinco rutas llegan a los datos. En un turno ABIERTO <c>/resumen-de-cierre</c>
    /// responde <c>409 turno_no_cerrado</c>, que también prueba que el turno es visible: un turno
    /// invisible nunca pasa de 404.</summary>
    private static async Task ExigirVisibleEnLasCincoRutasAsync(HttpClient cliente, int idTurno, bool abierto)
    {
        foreach (var ruta in RutasPorId)
        {
            var esperado = abierto && ruta == "/resumen-de-cierre" ? HttpStatusCode.Conflict : HttpStatusCode.OK;
            var respuesta = await cliente.GetAsync($"/api/caja/turnos/{idTurno}{ruta}");
            Assert.True(
                respuesta.StatusCode == esperado,
                $"GET {ruta} sobre el turno {idTurno}: {(int)respuesta.StatusCode} {await respuesta.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task<PaginaDeTurnos> ListarAsync(HttpClient cliente, string consulta = "")
    {
        var respuesta = await cliente.GetAsync($"/api/caja/turnos{consulta}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<PaginaDeTurnos>(OpcionesJson))!;
    }

    // ---- lecturas por id -----------------------------------------------------------------------

    [Fact]
    public async Task ElVendedorWebNoLeeUnTurnoCerradoAjenoEnNingunaRuta()
    {
        var e = await PrepararAsync(nameof(ElVendedorWebNoLeeUnTurnoCerradoAjenoEnNingunaRuta));

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.VendedorA, e.CerradoAjeno);
    }

    [Fact]
    public async Task ElVendedorWebNoLeeUnTurnoAbiertoAjenoNiSiquieraEnElPuntoDeVentaDelDispositivo()
    {
        var e = await PrepararAsync(nameof(ElVendedorWebNoLeeUnTurnoAbiertoAjenoNiSiquieraEnElPuntoDeVentaDelDispositivo));

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.VendedorA, e.AbiertoDeBEnElPuntoDeVentaDelDispositivo);
        await ExigirNoEncontradoEnLasCincoRutasAsync(e.VendedorA, e.AbiertoAjenoDeOtroPuntoDeVenta);
    }

    [Fact]
    public async Task ElVendedorLeeElTurnoQueAbrioEnLasCincoRutas()
    {
        var e = await PrepararAsync(nameof(ElVendedorLeeElTurnoQueAbrioEnLasCincoRutas));

        await ExigirVisibleEnLasCincoRutasAsync(e.VendedorA, e.AbiertoPropioDeA, abierto: true);
    }

    [Fact]
    public async Task ElVendedorLeeUnTurnoQueSoloCerroAbiertoPorOtro()
    {
        var e = await PrepararAsync(nameof(ElVendedorLeeUnTurnoQueSoloCerroAbiertoPorOtro));

        await ExigirVisibleEnLasCincoRutasAsync(e.VendedorA, e.CerradoQueCerroA, abierto: false);
    }

    [Fact]
    public async Task LaSesionDeDispositivoLeeElTurnoAbiertoAjenoDeSuPuntoDeVenta()
    {
        var e = await PrepararAsync(nameof(LaSesionDeDispositivoLeeElTurnoAbiertoAjenoDeSuPuntoDeVenta));

        await ExigirVisibleEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.AbiertoDeBEnElPuntoDeVentaDelDispositivo, abierto: true);
    }

    [Fact]
    public async Task LaSesionDeDispositivoLeeUnTurnoCerradoAjenoDeSuPuntoDeVentaEnLasCincoRutas()
    {
        var e = await PrepararAsync(nameof(LaSesionDeDispositivoLeeUnTurnoCerradoAjenoDeSuPuntoDeVentaEnLasCincoRutas));

        await ExigirVisibleEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.CerradoAjeno, abierto: false);
        await ExigirVisibleEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.CerradoQueCerroA, abierto: false);
    }

    [Fact]
    public async Task LaSesionDeDispositivoLeeLosTurnosPropiosDeOtroPuntoDeVenta()
    {
        var e = await PrepararAsync(nameof(LaSesionDeDispositivoLeeLosTurnosPropiosDeOtroPuntoDeVenta));

        await ExigirVisibleEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.AbiertoPorElCajeroEnOtroPuntoDeVenta, abierto: true);
        await ExigirVisibleEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.CerradoPorElCajeroEnOtroPuntoDeVenta, abierto: false);
    }

    [Fact]
    public async Task ElVendedorWebLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWebEnLasCincoRutas()
    {
        var e = await PrepararAsync(nameof(ElVendedorWebLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWebEnLasCincoRutas));

        await ExigirVisibleEnLasCincoRutasAsync(e.VendedorA, e.AbiertoDeBEnUnPuntoDeVentaWeb, abierto: true);
    }

    [Fact]
    public async Task ElVendedorWebNoLeeUnTurnoCerradoAjenoDeUnPuntoDeVentaWeb()
    {
        var e = await PrepararAsync(nameof(ElVendedorWebNoLeeUnTurnoCerradoAjenoDeUnPuntoDeVentaWeb));

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.VendedorA, e.CerradoDeBEnUnPuntoDeVentaWeb);
    }

    [Fact]
    public async Task ElVendedorWebNoLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWebDadoDeBaja()
    {
        var e = await PrepararAsync(nameof(ElVendedorWebNoLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWebDadoDeBaja));
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var puntoVenta = await db.PuntosVenta.FirstAsync(p => p.Id == e.IdPuntoVentaWeb);
            puntoVenta.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.VendedorA, e.AbiertoDeBEnUnPuntoDeVentaWeb);
    }

    [Fact]
    public async Task LaSesionDeDispositivoNoLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWeb()
    {
        var e = await PrepararAsync(nameof(LaSesionDeDispositivoNoLeeElTurnoAbiertoAjenoDeUnPuntoDeVentaWeb));

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.AbiertoDeBEnUnPuntoDeVentaWeb);
    }

    [Fact]
    public async Task LaSesionDeDispositivoNoLeeElTurnoAbiertoDeOtroPuntoDeVenta()
    {
        var e = await PrepararAsync(nameof(LaSesionDeDispositivoNoLeeElTurnoAbiertoDeOtroPuntoDeVenta));

        await ExigirNoEncontradoEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.AbiertoAjenoDeOtroPuntoDeVenta);
        await ExigirNoEncontradoEnLasCincoRutasAsync(e.CajeroDeDispositivo, e.AbiertoPropioDeA);
    }

    [Fact]
    public async Task SupervisorYAdminLeenTurnosAjenosEnLasCincoRutas()
    {
        var e = await PrepararAsync(nameof(SupervisorYAdminLeenTurnosAjenosEnLasCincoRutas));

        foreach (var cliente in new[] { e.Supervisor, e.Admin })
        {
            await ExigirVisibleEnLasCincoRutasAsync(cliente, e.CerradoAjeno, abierto: false);
            await ExigirVisibleEnLasCincoRutasAsync(cliente, e.AbiertoDeBEnElPuntoDeVentaDelDispositivo, abierto: true);
        }
    }

    [Fact]
    public async Task UnTurnoInvisibleResponde404IgualQueUnTurnoInexistente()
    {
        var e = await PrepararAsync(nameof(UnTurnoInvisibleResponde404IgualQueUnTurnoInexistente));

        var invisible = await e.VendedorA.GetAsync($"/api/caja/turnos/{e.CerradoAjeno}");
        var inexistente = await e.VendedorA.GetAsync("/api/caja/turnos/2000000000");

        Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal(await CodigoAsync(inexistente), await CodigoAsync(invisible));
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta)
    {
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return documento.RootElement.GetProperty("codigo").GetString()!;
    }

    // ---- /abierto ------------------------------------------------------------------------------

    private static async Task<int?> IdDelAbiertoAsync(HttpClient cliente, int idPuntoVenta)
    {
        var respuesta = await cliente.GetAsync($"/api/caja/turnos/abierto?idPuntoVenta={idPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        if (cuerpo == "null")
        {
            return null;
        }

        using var documento = JsonDocument.Parse(cuerpo);
        return documento.RootElement.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task ElAbiertoDelVendedorWebMuestraElTurnoAjenoDeUnPuntoDeVentaWeb()
    {
        var e = await PrepararAsync(nameof(ElAbiertoDelVendedorWebMuestraElTurnoAjenoDeUnPuntoDeVentaWeb));

        Assert.Equal(e.AbiertoDeBEnUnPuntoDeVentaWeb, await IdDelAbiertoAsync(e.VendedorA, e.IdPuntoVentaWeb));
    }

    [Fact]
    public async Task ElAbiertoDelVendedorWebEsNullParaUnTurnoAjenoDeUnPuntoDeVentaEscritorio()
    {
        var e = await PrepararAsync(nameof(ElAbiertoDelVendedorWebEsNullParaUnTurnoAjenoDeUnPuntoDeVentaEscritorio));

        Assert.Null(await IdDelAbiertoAsync(e.VendedorA, e.IdPuntoVentaEscritorioAjeno));
        Assert.Null(await IdDelAbiertoAsync(e.VendedorA, e.IdPuntoVentaDelDispositivo));
    }

    [Fact]
    public async Task ElAbiertoDelVendedorWebMuestraSuPropioTurnoEnUnPuntoDeVentaEscritorio()
    {
        var e = await PrepararAsync(nameof(ElAbiertoDelVendedorWebMuestraSuPropioTurnoEnUnPuntoDeVentaEscritorio));

        Assert.Equal(e.AbiertoPropioDeA, await IdDelAbiertoAsync(e.VendedorA, e.IdPuntoVentaEscritorioPropioDeA));
    }

    [Fact]
    public async Task ElAbiertoDeLaSesionDeDispositivoMuestraElDeSuPuntoDeVentaYNullEnElDeOtro()
    {
        var e = await PrepararAsync(nameof(ElAbiertoDeLaSesionDeDispositivoMuestraElDeSuPuntoDeVentaYNullEnElDeOtro));

        Assert.Equal(
            e.AbiertoDeBEnElPuntoDeVentaDelDispositivo,
            await IdDelAbiertoAsync(e.CajeroDeDispositivo, e.IdPuntoVentaDelDispositivo));
        Assert.Null(await IdDelAbiertoAsync(e.CajeroDeDispositivo, e.IdPuntoVentaEscritorioAjeno));
        Assert.Null(await IdDelAbiertoAsync(e.CajeroDeDispositivo, e.IdPuntoVentaWeb));
    }

    [Fact]
    public async Task ElAbiertoDeSupervisorYAdminMuestraElTurnoDeCualquierPuntoDeVenta()
    {
        var e = await PrepararAsync(nameof(ElAbiertoDeSupervisorYAdminMuestraElTurnoDeCualquierPuntoDeVenta));

        foreach (var cliente in new[] { e.Supervisor, e.Admin })
        {
            Assert.Equal(e.AbiertoAjenoDeOtroPuntoDeVenta, await IdDelAbiertoAsync(cliente, e.IdPuntoVentaEscritorioAjeno));
            Assert.Equal(e.AbiertoDeBEnUnPuntoDeVentaWeb, await IdDelAbiertoAsync(cliente, e.IdPuntoVentaWeb));
        }
    }

    // ---- listado -------------------------------------------------------------------------------

    [Fact]
    public async Task ElListadoDelVendedorWebTraeSoloSusTurnosVisiblesConElTotalCorrecto()
    {
        var e = await PrepararAsync(nameof(ElListadoDelVendedorWebTraeSoloSusTurnosVisiblesConElTotalCorrecto));

        var pagina = await ListarAsync(e.VendedorA);

        Assert.Equal(3, pagina.Total);
        Assert.Equal(
            new[] { e.CerradoQueCerroA, e.AbiertoPropioDeA, e.AbiertoDeBEnUnPuntoDeVentaWeb }.Order(),
            pagina.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task ElTotalDelListadoDelVendedorCuentaSoloLosVisiblesAunPaginando()
    {
        var e = await PrepararAsync(nameof(ElTotalDelListadoDelVendedorCuentaSoloLosVisiblesAunPaginando));

        var pagina = await ListarAsync(e.VendedorA, "?tamanio=1&pagina=2");

        Assert.Equal(3, pagina.Total);
        Assert.Single(pagina.Items);
    }

    [Fact]
    public async Task ElListadoDeLaSesionDeDispositivoTraeLosPropiosYTodosLosDeSuPuntoDeVenta()
    {
        var e = await PrepararAsync(nameof(ElListadoDeLaSesionDeDispositivoTraeLosPropiosYTodosLosDeSuPuntoDeVenta));

        var pagina = await ListarAsync(e.CajeroDeDispositivo);

        Assert.Equal(5, pagina.Total);
        Assert.Equal(
            new[]
            {
                e.CerradoAjeno, e.CerradoQueCerroA, e.AbiertoDeBEnElPuntoDeVentaDelDispositivo,
                e.AbiertoPorElCajeroEnOtroPuntoDeVenta, e.CerradoPorElCajeroEnOtroPuntoDeVenta
            }.Order(),
            pagina.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task ElFiltroDeEstadoDelListadoDeLaSesionDeDispositivoConservaLaVisibilidad()
    {
        var e = await PrepararAsync(nameof(ElFiltroDeEstadoDelListadoDeLaSesionDeDispositivoConservaLaVisibilidad));

        var cerrados = await ListarAsync(e.CajeroDeDispositivo, "?estado=Cerrado");
        var abiertos = await ListarAsync(e.CajeroDeDispositivo, "?estado=Abierto");

        Assert.Equal(3, cerrados.Total);
        Assert.Equal(
            new[] { e.CerradoAjeno, e.CerradoQueCerroA, e.CerradoPorElCajeroEnOtroPuntoDeVenta }.Order(),
            cerrados.Items.Select(i => i.Id).Order());
        Assert.Equal(2, abiertos.Total);
        Assert.Equal(
            new[] { e.AbiertoDeBEnElPuntoDeVentaDelDispositivo, e.AbiertoPorElCajeroEnOtroPuntoDeVenta }.Order(),
            abiertos.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task ElFiltroDeEstadoDelListadoDelVendedorWebNoRevelaTurnosAjenos()
    {
        var e = await PrepararAsync(nameof(ElFiltroDeEstadoDelListadoDelVendedorWebNoRevelaTurnosAjenos));

        var cerrados = await ListarAsync(e.VendedorA, "?estado=Cerrado");
        var abiertos = await ListarAsync(e.VendedorA, "?estado=Abierto");

        Assert.Equal(1, cerrados.Total);
        Assert.Equal(e.CerradoQueCerroA, Assert.Single(cerrados.Items).Id);
        Assert.Equal(2, abiertos.Total);
        Assert.Equal(
            new[] { e.AbiertoPropioDeA, e.AbiertoDeBEnUnPuntoDeVentaWeb }.Order(),
            abiertos.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task ElListadoDeSupervisorYAdminTraeTodosLosTurnosDelTenant()
    {
        var e = await PrepararAsync(nameof(ElListadoDeSupervisorYAdminTraeTodosLosTurnosDelTenant));

        foreach (var cliente in new[] { e.Supervisor, e.Admin })
        {
            var pagina = await ListarAsync(cliente);

            Assert.Equal(9, pagina.Total);
            Assert.Equal(9, pagina.Items.Count);
        }
    }
}
