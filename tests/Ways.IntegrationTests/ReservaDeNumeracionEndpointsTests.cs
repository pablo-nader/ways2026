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
/// stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): superficie HTTP completa —
/// <c>POST /api/ventas/reservas-numeracion</c> (autorización + validación de
/// <c>ServicioDeReservasDeNumeracion</c>) y <c>SolicitudDeVenta.NumeroPreasignado</c> en
/// <c>POST /api/ventas</c> (<c>ServicioDeVentas.EmitirAsync</c>). Mismo trámite de siembra que
/// <c>VentasModoPuntoVentaTests</c> — no se comparte helper entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReservaDeNumeracionEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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

    private async Task<int> AgregarSegundoPuntoVentaAsync(int idTenant, string nombre, ModoPuntoVenta modo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var idEmpresa = await db.Empresas.Select(e => e.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            Modo = modo,
            CreatedAt = ahora,
            UpdatedAt = ahora
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

    /// <summary>Vincula un dispositivo, siembra un cajero y devuelve un <see cref="HttpClient"/> ya
    /// logueado vía <c>login-dispositivo</c> — mismo flujo que <c>VentasModoPuntoVentaTests</c>,
    /// más el id del dispositivo (necesario para leer <c>reservas_numeracion</c> directo).</summary>
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

    private async Task<(int IdArticulo, int IdMedioEfectivo)> SembrarServicioYMedioEfectivoAsync(
        int idTenant, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"servicio-{Guid.NewGuid():N}",
            Nombre = "Servicio de prueba",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            IdListaPrecio = idListaGeneral,
            Monto = precio,
            VigenteDesde = ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (articulo.Id, idMedioEfectivo);
    }

    private static SolicitudDeVenta SolicitudDeServicio(
        int idPuntoVenta, int idArticulo, int idMedioPago, decimal precio, long? numeroPreasignado = null) =>
        new(idPuntoVenta, null, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioPago, precio, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numeroPreasignado);

    // ---- POST /api/ventas/reservas-numeracion ---------------------------------------------

    /// <summary>Prueba el 403 observable, no una capa aislada: la política del endpoint
    /// (<c>Politicas.RequiereDispositivo</c>) y el chequeo de <c>ServicioDeReservasDeNumeracion.
    /// ReservarAsync</c> (<c>contexto.IdDispositivo ?? throw ... 403</c>, defensa en profundidad
    /// deliberada) devuelven el MISMO 403 por separado — mutar cualquiera de las dos sola no pone
    /// esta prueba en rojo, confirmado (no solo asumido). Ninguna prueba de esta suite aísla una
    /// de la otra.</summary>
    [Fact]
    public async Task UnActorWebNoPuedeReservarUnBloque()
    {
        var (admin, _, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnActorWebNoPuedeReservarUnBloque));
        using var _admin = admin;

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnDispositivoNoPuedeReservarParaUnPuntoDeVentaQueNoEsElSuyo()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeReservarParaUnPuntoDeVentaQueNoEsElSuyo));
        var idPuntoVentaAjeno = await AgregarSegundoPuntoVentaAsync(idTenant, "Local 2", ModoPuntoVenta.Escritorio);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "propio");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVentaAjeno, "TX", 10));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task UnaCantidadFueraDeRangoEs400(int cantidad)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(UnaCantidadFueraDeRangoEs400)}-{cantidad}");
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "rango");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", cantidad));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Kill del límite superior: 500 (el tope exacto,
    /// <c>ServicioDeReservasDeNumeracion.CantidadMaxima</c>) tiene que funcionar — un mutante que
    /// cambie <c>&gt; CantidadMaxima</c> por <c>&gt;= CantidadMaxima</c> lo pone en rojo.</summary>
    [Fact]
    public async Task LaCantidadEnElTopeMaximoFunciona()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(LaCantidadEnElTopeMaximoFunciona));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "tope");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion",
            new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", ServicioDeReservasDeNumeracion.CantidadMaxima));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var bloque = (await respuesta.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(500, bloque.Hasta - bloque.Desde + 1);
    }

    [Fact]
    public async Task UnTipoDeComprobanteInvalidoEs400()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnTipoDeComprobanteInvalidoEs400));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "tipo-invalido");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "NO_EXISTE", 10));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_comprobante_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPuntoDeVentaInexistenteEs404()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnPuntoDeVentaInexistenteEs404));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "pv-inexistente");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(999_999, "TX", 10));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task ReservarUnBloqueDevuelveElRangoYCreaUnaFilaViva()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReservarUnBloqueDevuelveElRangoYCreaUnaFilaViva));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "ok");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var bloque = (await respuesta.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(1, bloque.Desde);
        Assert.Equal(10, bloque.Hasta);
        Assert.Equal(idPuntoVenta, bloque.IdPuntoVenta);
        Assert.Equal("TX", bloque.CodigoTipoComprobante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);
        Assert.Equal(1, fila.Desde);
        Assert.Equal(10, fila.Hasta);
        Assert.Null(fila.AbandonadaAt);
    }

    [Fact]
    public async Task PedirUnBloqueNuevoAbandonaElAnteriorViaEndpoint()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(PedirUnBloqueNuevoAbandonaElAnteriorViaEndpoint));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "reissue");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        var segunda = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var bloque2 = (await segunda.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(6, bloque2.Desde);
        Assert.Equal(10, bloque2.Hasta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await db.ReservasNumeracion
            .Where(r => r.IdDispositivo == idDispositivo).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, filas.Count);
        Assert.NotNull(filas[0].AbandonadaAt);
        Assert.Null(filas[1].AbandonadaAt);
    }

    /// <summary>db-error-backstops — INVARIANTE, no una carrera forzada (claims-match-code: se
    /// intentó forzar el 409 de <c>ux_reservas_numeracion_dispositivo_activo</c> con dos POSTs
    /// concurrentes y no se pudo reproducir de forma confiable bajo <c>Task.WhenAll</c> — el row
    /// lock intermedio de <c>AsignarBloqueAsync</c> le da a la segunda transacción margen de sobra
    /// para que su abandono encuentre ya comiteada la fila de la primera y la abandone en limpio,
    /// así que en este arnés las dos siempre terminan en 200. Lo que SÍ es una propiedad
    /// determinística, con cualquier entrelazado posible: al final quedan EXACTAMENTE dos filas
    /// (una viva, una abandonada) — nunca dos vivas — sin importar el orden de llegada. El 409
    /// traducido en sí (para el caso en que la carrera SÍ se dé, p.ej. contra un backend con más
    /// latencia real) lo prueba el bypass crudo de <c>ReservaDeNumeracionBackstopTests</c>.</summary>
    [Fact]
    public async Task DosPedidosConcurrentesDelMismoDispositivoTerminanConExactamenteUnaReservaViva()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(DosPedidosConcurrentesDelMismoDispositivoTerminanConExactamenteUnaReservaViva));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "carrera");
        using var _cajero = cajero;
        admin.Dispose();

        var tareaA = cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        var tareaB = cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r =>
            Assert.True(
                r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Status inesperado: {r.StatusCode}"));

        foreach (var conflicto in respuestas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var problema = await conflicto.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("reserva_de_numeracion_duplicada", problema.GetProperty("codigo").GetString());
        }

        // Bajo el entrelazado común (las dos comitean, 200+200) quedan DOS filas — la primera
        // abandonada por la segunda. Bajo el entrelazado racy (200+409) queda UNA sola — el
        // INSERT que chocó nunca comitea, así que ni su fila ni su bump del contador sobreviven
        // (la transacción entera hace rollback). La única invariante verdadera en los DOS casos
        // es "a lo sumo una viva" — nunca dos.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await db.ReservasNumeracion.Where(r => r.IdDispositivo == idDispositivo).ToListAsync();
        Assert.Single(filas, r => r.AbandonadaAt is null);
    }

    // ---- POST /api/ventas con NumeroPreasignado ------------------------------------------

    [Fact]
    public async Task UnActorWebNoPuedeMandarUnNumeroPreasignado()
    {
        // PV en modo Web a propósito (no el Escritorio default de AprovisionarComoAdminAsync):
        // sin esto, un actor web contra un PV Escritorio ya recibe 409
        // punto_venta_modo_incompatible por PoliticaDeModoDePuntoVenta, un confound que
        // enmascararía la mutación del guard bajo prueba (mutation-proof-tests regla 3).
        var (admin, idTenant, _) = await AprovisionarComoAdminAsync(
            nameof(UnActorWebNoPuedeMandarUnNumeroPreasignado));
        using var _admin = admin;
        var idPuntoVentaWeb = await AgregarSegundoPuntoVentaAsync(idTenant, "Web", ModoPuntoVenta.Web);

        await AbrirTurnoAsync(idTenant, idPuntoVentaWeb);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVentaWeb, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_admitido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroQueNoReservo()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroQueNoReservo));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sin-reserva");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>LOS DOS CONJUNTOS del rango, <c>numero &gt;= Desde</c> y <c>numero &lt;= Hasta</c>,
    /// cada uno aislado (mutation-proof-tests regla 3, "enumerar los conjuntos") — a diferencia de
    /// <see cref="UnDispositivoNoPuedeUsarUnNumeroQueNoReservo"/> (sin NINGUNA reserva, donde los
    /// seis conjuntos fallan a la vez y no aíslan nada), acá SÍ hay una reserva viva del
    /// dispositivo/PV/tipo correctos — un número apenas por encima y otro apenas por debajo del
    /// rango, cada uno matando solo su propio conjunto.</summary>
    [Theory]
    [InlineData(9L)]  // apenas por debajo de Desde=10 — mata "numero >= Desde".
    [InlineData(16L)] // apenas por encima de Hasta=15 — mata "numero <= Hasta".
    public async Task UnDispositivoNoPuedeUsarUnNumeroFueraDelRangoDeSuPropiaReservaViva(long numeroFueraDeRango)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(UnDispositivoNoPuedeUsarUnNumeroFueraDelRangoDeSuPropiaReservaViva)}-{numeroFueraDeRango}");
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "fuera-de-rango");
        using var _cajero = cajero;
        admin.Dispose();

        // Reserva [10,15] vigente (bloque de 6 arrancando en 10: la serie empieza en 1, así que
        // primero hay que quemar 9 con un bloque descartable).
        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 9));
        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 6));

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: numeroFueraDeRango));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>LA CLÁUSULA: <c>AbandonadaAt == null</c> del chequeo de pertenencia — un número que
    /// perteneció a un bloque YA abandonado (el dispositivo pidió uno nuevo) no sirve más, aunque
    /// el rango numérico lo siga conteniendo.</summary>
    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaAbandonada()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaAbandonada));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "abandonada");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        // Segundo pedido: abandona el bloque [1,5] y entrega [6,10].
        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 3));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Happy path + prueba de que el contador NO se vuelve a bumpear: una venta
    /// server-asignada posterior (sin NumeroPreasignado) tiene que seguir en 11, no en 6 — si
    /// <c>EmitirAsync</c> llamara a <c>AsignarComprometidoAsync</c> además de usar el número
    /// pre-asignado, el contador avanzaría de más.</summary>
    [Fact]
    public async Task UnDispositivoPuedeVenderConUnNumeroPreasignadoPropioYElContadorNoSeDuplica()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoPuedeVenderConUnNumeroPreasignadoPropioYElContadorNoSeDuplica));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "propio");
        using var _cajero = cajero;

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 5));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(5, emitido.Numero);

        // Una venta server-asignada (admin, web) sobre otro punto de venta Web del mismo tenant no
        // comparte la serie — se usa el MISMO punto de venta vía un segundo cajero de escritorio
        // para verificar el contador sin abrir un tercer actor: pide un bloque nuevo (abandona el
        // vigente) y el desde tiene que ser 11, nunca 6.
        var siguiente = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 1));
        var bloqueSiguiente = (await siguiente.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(11, bloqueSiguiente.Desde);
    }

    [Fact]
    public async Task ReenviarLaMismaVentaConElMismoNumeroPreasignadoDevuelveElMismoComprobante()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarLaMismaVentaConElMismoNumeroPreasignadoDevuelveElMismoComprobante));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var solicitud = SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1);

        var primera = await cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var segunda = await cajero.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);

        var emitido1 = (await primera.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        var emitido2 = (await segunda.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(emitido1.Id, emitido2.Id);
        Assert.Equal(emitido1.Numero, emitido2.Numero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var cantidad = await db.ComprobantesVenta
            .CountAsync(c => c.IdPuntoVenta == idPuntoVenta && c.Numero == 1);
        Assert.Equal(1, cantidad);
    }
}
