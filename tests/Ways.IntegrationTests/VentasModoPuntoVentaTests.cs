using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos (DB CHANGE GATE aprobado): invariante "una PC-caja = un punto de venta" del
/// lado de <c>ServicioDeVentas.ResolverPuntoVentaAsync</c> — un actor con claim de dispositivo solo
/// puede vender contra SU PROPIO punto de venta Escritorio; un actor sin esa claim (sesión web
/// normal) solo puede vender contra un punto de venta Web. Cualquier otra combinación es 409
/// <c>punto_venta_modo_incompatible</c>, nunca 404 (el punto de venta existe y es del tenant
/// correcto). Las pruebas de RECHAZO cortan ANTES de <c>ResolverTurnoAbiertoAsync</c> (design
/// decisión 11: modo se resuelve inmediatamente después de existencia), así que no hace falta
/// abrir turno ni sembrar artículo/cliente — <c>IdCliente: null</c> resuelve a Consumidor Final (ya
/// aprovisionado) y el artículo nunca se llega a mirar.
///
/// judgment-day ronda 1 (hallazgo CRITICAL 4a): las pruebas de HAPPY PATH, agregadas después, sí
/// necesitan turno abierto y un artículo con precio — <see cref="AbrirTurnoAsync"/> y
/// <see cref="SembrarServicioYMedioEfectivoAsync"/> son el mínimo para eso (un servicio sin stock,
/// mismo criterio que <c>VentasCheckoutTests.UnaVentaDeUnServicioNoGeneraMovimientoNiFilaDeStock</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasModoPuntoVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    // Mismo motivo que VentasCheckoutTests.OpcionesJson: ReadFromJsonAsync<T>() sin opciones usa
    // las opciones DEFAULT del lado cliente, que no traen JsonStringEnumConverter — ComprobanteEmitido.Estado
    // revienta la deserialización sin esto.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static SolicitudDeVenta SolicitudMinima(int idPuntoVenta) => new(
        idPuntoVenta, null, "TX", null, [new LineaDeVenta(1, 1m, null)], [], null, null);

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(
        string nombre, ModoPuntoVenta modo)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, modo);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, resultado.IdTenant, resultado.IdPuntoVenta);
    }

    /// <summary>Agrega un segundo punto de venta al tenant vía EF directo — no hay endpoint de alta
    /// (plataforma-only vía aprovisionamiento, ADR-16); mismo criterio que
    /// <c>CostoCongeladoTests</c> sembrando filas auxiliares que la API no expone.</summary>
    private async Task<int> AgregarSegundoPuntoVentaAsync(int idTenant, string nombre, ModoPuntoVenta modo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var idEmpresa = await db.Empresas.Select(e => e.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdEmpresa = idEmpresa, Nombre = nombre, Modo = modo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
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

    /// <summary>Vincula un dispositivo al punto de venta indicado, siembra un cajero y devuelve un
    /// <see cref="HttpClient"/> YA logueado como ese cajero vía <c>login-dispositivo</c> — mismo
    /// flujo que <c>DispositivosTests</c>.</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
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

        return cajero;
    }

    /// <summary>Abre un turno de caja — a diferencia de las tres pruebas de rechazo de más abajo
    /// (que nunca llegan a <c>ResolverTurnoAbiertoAsync</c>), los happy paths (judgment-day ronda
    /// 1, hallazgo CRITICAL 4a) sí necesitan un turno abierto para llegar a emitir.</summary>
    private async Task AbrirTurnoAsync(int idTenant, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEmpleadoApertura = await db.Usuarios
            .Where(u => u.IdTenant == idTenant && u.NombreUsuario == "admin")
            .Select(u => u.Id)
            .FirstAsync();

        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Un servicio (<c>EsProducto = false</c>, sin stock) con precio en la lista general
    /// default que el aprovisionamiento ya siembra — el mínimo para que un happy path de checkout
    /// no tenga que sembrar stock/lotes. El medio Efectivo también viene sembrado por el
    /// aprovisionamiento.</summary>
    private async Task<(int IdArticulo, int IdMedioEfectivo)> SembrarServicioYMedioEfectivoAsync(
        int idTenant, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = idTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant, CodigoInterno = $"servicio-{Guid.NewGuid():N}", Nombre = "Servicio de prueba",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (articulo.Id, idMedioEfectivo);
    }

    private static SolicitudDeVenta SolicitudDeServicio(int idPuntoVenta, int idArticulo, int idMedioPago, decimal precio) =>
        new(idPuntoVenta, null, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioPago, precio, null, 0m)], null, null);

    [Fact]
    public async Task UnActorSinClaimDeDispositivoNoPuedeVenderContraUnPuntoVentaEscritorio()
    {
        var (admin, _, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnActorSinClaimDeDispositivoNoPuedeVenderContraUnPuntoVentaEscritorio), ModoPuntoVenta.Escritorio);
        using var _admin = admin;

        var respuesta = await admin.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVenta));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeVenderContraUnPuntoVentaQueNoEsElSuyo()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeVenderContraUnPuntoVentaQueNoEsElSuyo), ModoPuntoVenta.Escritorio);
        var idPuntoVentaAjeno = await AgregarSegundoPuntoVentaAsync(idTenant, "Local 2", ModoPuntoVenta.Escritorio);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "propio");
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeVenderContraUnPuntoVentaWeb()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeVenderContraUnPuntoVentaWeb), ModoPuntoVenta.Escritorio);
        var idPuntoVentaWeb = await AgregarSegundoPuntoVentaAsync(idTenant, "Local Web", ModoPuntoVenta.Web);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "web");
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVentaWeb));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day ronda 1 (hallazgo CRITICAL 4b): en la prueba de arriba, los dos
    /// disjuntos de <c>puntoVenta.Modo != Escritorio || idPuntoVentaDelDispositivo != puntoVenta.Id</c>
    /// son verdaderos a la vez (el PV Web NO es el suyo Y no es Escritorio) — un mutante que
    /// borrara el disjunto de Modo seguiría en rojo por el de identidad, así que esa prueba sola
    /// no lo aísla. Acá el dispositivo apunta a SU PROPIO punto de venta (mismo id — el segundo
    /// disjunto es SIEMPRE falso) mientras ESE punto de venta pasa a Web — solo el disjunto de
    /// Modo puede explicar el 409.</summary>
    [Fact]
    public async Task UnDispositivoNoPuedeVenderCuandoSuPropioPuntoVentaPasaAWeb()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeVenderCuandoSuPropioPuntoVentaPasaAWeb), ModoPuntoVenta.Escritorio);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "propio");

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)))
        {
            var pv = await db.PuntosVenta.FirstAsync(p => p.Id == idPuntoVentaPropio);
            pv.Modo = ModoPuntoVenta.Web;
            await db.SaveChangesAsync();
        }

        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", SolicitudMinima(idPuntoVentaPropio));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day ronda 1 (hallazgo CRITICAL 4a): happy path — sin esto, un mutante que
    /// hiciera <c>ExigirModoCompatibleConElActorAsync</c>/<c>PoliticaDeModoDePuntoVenta</c> lanzar
    /// SIEMPRE dejaba la suite entera en verde (solo había ramas de rechazo).
    ///
    /// judgment-day ronda 2 (residual test-quality): un 201 solo no prueba que la venta haya
    /// quedado escrita — se lee el cuerpo devuelto (identidad + total) y se relee la fila real de
    /// <c>comprobantes_venta</c> por su id, mismo criterio que <c>VentasCheckoutTests</c>.</summary>
    [Fact]
    public async Task UnDispositivoPuedeVenderContraSuPropioPuntoVentaEscritorio()
    {
        const decimal precio = 100m;
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoPuedeVenderContraSuPropioPuntoVentaEscritorio), ModoPuntoVenta.Escritorio);

        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, precio);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "escritorio-ok");
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, precio));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(idPuntoVenta, emitido.IdPuntoVenta);
        Assert.Equal(precio, emitido.Total);
        Assert.StartsWith($"{idPuntoVenta:D4}-", emitido.NumeroVisible);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ComprobantesVenta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == emitido.Id);
        Assert.NotNull(fila);
        Assert.Equal(emitido.Numero, fila!.Numero);
        Assert.Equal(precio, fila.Total);
    }

    /// <summary>judgment-day ronda 1 (hallazgo CRITICAL 4a): happy path simétrico del lado Web.
    ///
    /// judgment-day ronda 2 (residual test-quality): mismo criterio de aserciones que el happy
    /// path de Escritorio de arriba — cuerpo devuelto + fila persistida.</summary>
    [Fact]
    public async Task UnActorWebPuedeVenderContraUnPuntoVentaWeb()
    {
        const decimal precio = 100m;
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnActorWebPuedeVenderContraUnPuntoVentaWeb), ModoPuntoVenta.Web);
        using var _admin = admin;

        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, precio);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, precio));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(idPuntoVenta, emitido.IdPuntoVenta);
        Assert.Equal(precio, emitido.Total);
        Assert.StartsWith($"{idPuntoVenta:D4}-", emitido.NumeroVisible);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ComprobantesVenta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == emitido.Id);
        Assert.NotNull(fila);
        Assert.Equal(emitido.Numero, fila!.Numero);
        Assert.Equal(precio, fila.Total);
    }
}
