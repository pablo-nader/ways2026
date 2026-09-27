using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Pos;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/pos/rendicion-de-cola</c> punta a punta: autorización, validación y escritura del
/// reporte sobre el bloque VIVO de <c>reservas_numeracion</c>. Mismo trámite de siembra que
/// <see cref="ReservaDeNumeracionEndpointsTests"/> — no se comparte helper entre archivos
/// (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RendicionDeColaEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, resultado.IdTenant, resultado.IdPuntoVenta);
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Vincula un dispositivo, siembra un cajero Vendedor y devuelve un
    /// <see cref="HttpClient"/> logueado vía <c>login-dispositivo</c> — mismo flujo que
    /// <see cref="ReservaDeNumeracionEndpointsTests"/>.</summary>
    private async Task<(HttpClient Cliente, int IdDispositivo)> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vinculado = (await alta.Content.ReadFromJsonAsync<DispositivoVinculado>())!;
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = $"cajero-{sufijo}",
                Mail = $"cajero-{sufijo}-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo($"cajero-{sufijo}", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (cajero, vinculado.Datos.Id);
    }

    private static async Task<(long Desde, long Hasta)> ReservarBloqueAsync(
        HttpClient cajero, int idPuntoVenta, int cantidad = 10)
    {
        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion",
            new Ways.Application.Ventas.SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", cantidad));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var bloque = (await respuesta.Content.ReadFromJsonAsync<Ways.Application.Ventas.BloqueDeNumeracionReservado>())!;

        return (bloque.Desde, bloque.Hasta);
    }

    /// <summary>Mismo criterio (y misma limitación) que
    /// <c>ReservaDeNumeracionEndpointsTests.UnActorWebNoPuedeReservarUnBloque</c>: la policy del
    /// endpoint (<c>Politicas.RequiereDispositivo</c>) y la guarda propia del servicio devuelven el
    /// MISMO 403 y leen la MISMA claim, así que este nivel HTTP no puede aislarlas. Cada capa se
    /// prueba aislada donde sí se puede: la guarda del servicio en
    /// <c>Ways.Application.Tests.Pos.ServicioDeRendicionDeColaTests</c>, la declaración de la policy
    /// en <c>SuperficieDeAutorizacionTests.CadaRutaConPolicyAdicionalSobreSuGrupoLaApila</c>.</summary>
    [Fact]
    public async Task UnActorWebNoPuedeRendirSuCola()
    {
        var (admin, _, _) = await AprovisionarComoAdminAsync(nameof(UnActorWebNoPuedeRendirSuCola));
        using var _admin = admin;

        var respuesta = await admin.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", 1, 0));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>El positivo por ROL REAL que exige <c>mutation-proof-tests</c> regla 16 para toda
    /// ruta que entra al allowlist de <c>SuperficieDeAutorizacionTests</c>: un cajero VENDEDOR
    /// logueado por dispositivo rinde con éxito. Sin este test, apilarle <c>GestionDeCatalogo</c>
    /// (solo Admin) a la ruta sería un mutante que sobrevive a toda la suite. Se afirman los tres
    /// valores persistidos, no solo el 204 — <c>reportado_at</c> se compara contra el instante del
    /// request, así que un mutante que escriba una constante o deje la columna en null muere
    /// acá.</summary>
    [Fact]
    public async Task UnCajeroVendedorDeDispositivoRindeSuColaYQuedaPersistida()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnCajeroVendedorDeDispositivoRindeSuColaYQuedaPersistida));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "rinde");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, _) = await ReservarBloqueAsync(cajero, idPuntoVenta);
        var antes = DateTimeOffset.UtcNow;

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 2, 3));

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);

        Assert.Equal(desde + 2, fila.EntregadoHasta);
        Assert.Equal(3, fila.Pendientes);
        Assert.NotNull(fila.ReportadoAt);
        Assert.InRange(fila.ReportadoAt!.Value, antes.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(fila.ReportadoAt, fila.UpdatedAt);
    }

    /// <summary>Una segunda rendición PISA la primera (el reporte es un estado, no un ledger): sin
    /// esto, un dispositivo que drena su cola no podría volver a declararse limpio y el turno
    /// quedaría bloqueado para siempre.</summary>
    [Fact]
    public async Task UnaSegundaRendicionPisaLaPrimera()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnaSegundaRendicionPisaLaPrimera));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "dosveces");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, _) = await ReservarBloqueAsync(cajero, idPuntoVenta);

        var primera = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 4, 5));
        Assert.Equal(HttpStatusCode.NoContent, primera.StatusCode);

        var segunda = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 6, 0));
        Assert.Equal(HttpStatusCode.NoContent, segunda.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);

        Assert.Equal(desde + 6, fila.EntregadoHasta);
        Assert.Equal(0, fila.Pendientes);
    }

    /// <summary>El piso monótono visto desde el endpoint (judgment-day, SEVERE): la segunda rendición
    /// declara MENOS que la primera y se rechaza con su propio código, en vez de pisar la marca de agua
    /// hacia abajo — lo que dejaba el rango esperado vacío y hacía pasar el cierre con cualquier cosa en
    /// la cola. Se afirma además que lo persistido sigue siendo el valor alto: el 409 sin eso no
    /// probaría que no escribió.</summary>
    [Fact]
    public async Task UnaRendicionQueBajaElMaximoDeclaradoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnaRendicionQueBajaElMaximoDeclaradoEs409));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "regresiva");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, _) = await ReservarBloqueAsync(cajero, idPuntoVenta);

        var primera = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 6, 0));
        Assert.Equal(HttpStatusCode.NoContent, primera.StatusCode);

        var segunda = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 4, 0));

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rendicion_regresiva", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);
        Assert.Equal(desde + 6, fila.EntregadoHasta);
    }

    /// <summary>El borde VÁLIDO del mismo piso: repetir EXACTAMENTE el último valor declarado sigue
    /// siendo un 204 (es lo que hace un reintento honesto del ciclo de sincronización), y actualiza
    /// <c>pendientes</c>, que es el dato que cambió. Un mutante que use <c>&gt;</c> en vez de
    /// <c>&gt;=</c> deja este test en 409.</summary>
    [Fact]
    public async Task RepetirElMismoMaximoDeclaradoSigueSiendoValido()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(RepetirElMismoMaximoDeclaradoSigueSiendoValido));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "repite");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, _) = await ReservarBloqueAsync(cajero, idPuntoVenta);

        var primera = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 6, 2));
        Assert.Equal(HttpStatusCode.NoContent, primera.StatusCode);

        var segunda = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde + 6, 0));

        Assert.Equal(HttpStatusCode.NoContent, segunda.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);
        Assert.Equal(desde + 6, fila.EntregadoHasta);
        Assert.Equal(0, fila.Pendientes);
    }

    [Fact]
    public async Task RendirSinBloqueVivoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(RendirSinBloqueVivoEs409));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sinbloque");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", 1, 0));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rendicion_sin_bloque_vivo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnTipoDeComprobanteInvalidoEs400()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnTipoDeComprobanteInvalidoEs400));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "tipomalo");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("NO_EXISTE", 1, 0));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_comprobante_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaCantidadDePendientesNegativaEs400()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnaCantidadDePendientesNegativaEs400));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "negativo");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, _) = await ReservarBloqueAsync(cajero, idPuntoVenta);

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", desde, -1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pendientes_invalido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>La pre-validación de rango que <c>db-error-backstops</c> exige: sin ella estos dos
    /// valores llegarían a <c>ck_reservas_numeracion_entregado_en_rango</c> y saldrían como 500.
    /// <c>desde - 2</c> y <c>hasta + 1</c> son los dos primeros valores FUERA del rango
    /// permitido.</summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(1)]
    public async Task UnEntregadoHastaFueraDelBloqueEs400(int desvio)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(UnEntregadoHastaFueraDelBloqueEs400)}{desvio}");
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, $"fuera{desvio}");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, hasta) = await ReservarBloqueAsync(cajero, idPuntoVenta);
        var entregadoHasta = desvio < 0 ? desde + desvio : hasta + desvio;

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", entregadoHasta, 0));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entregado_hasta_invalido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Los dos bordes VÁLIDOS del mismo rango (<c>desde - 1</c> = todavía no repartió
    /// ninguno; <c>hasta</c> = agotó el bloque). Junto con el test de arriba, mata los dos mutantes
    /// de cada comparación (<c>&lt;</c> ↔ <c>&lt;=</c>, <c>&gt;</c> ↔ <c>&gt;=</c>).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LosBordesDelBloqueSonValidos(bool bordeInferior)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(LosBordesDelBloqueSonValidos)}{bordeInferior}");
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVenta, $"borde{bordeInferior}");
        using var _cajero = cajero;
        admin.Dispose();

        var (desde, hasta) = await ReservarBloqueAsync(cajero, idPuntoVenta);
        var entregadoHasta = bordeInferior ? desde - 1 : hasta;

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", entregadoHasta, 0));

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);
        Assert.Equal(entregadoHasta, fila.EntregadoHasta);
    }
}
