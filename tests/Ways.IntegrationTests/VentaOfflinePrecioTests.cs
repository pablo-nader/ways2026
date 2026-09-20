using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
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
/// stage-pos-venta-offline-backend (Parte B, DB CHANGE GATE: sin cambio de esquema — la
/// discrepancia vive en <c>auditoria</c>, nunca en una columna nueva de
/// <c>items_comprobante_venta</c>): superficie HTTP completa de precio offline en <c>POST
/// /api/ventas</c> — <c>LineaDeVenta.PrecioUnitario</c>/<c>DescuentoUnitario</c>,
/// <c>ExigirPreciosOfflineValidos</c>, <c>MaterializarItems</c>/<c>NetoDeLinea</c>,
/// <c>AccionAuditada.VentaDiscrepanciaDePrecio</c>. Mismo trámite de siembra que
/// <c>ReservaDeNumeracionEndpointsTests</c> — no se comparte helper entre archivos (convención
/// del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaOfflinePrecioTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        var solicitud = new Ways.Application.Organizacion.SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<Ways.Application.Organizacion.ResultadoAprovisionamiento>())!;

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

    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
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

    /// <summary>Artículo de servicio con precio vigente HOY — mismo helper que
    /// <c>ReservaDeNumeracionEndpointsTests</c>, duplicado a propósito (convención del repo).
    /// <paramref name="conPrecioVigente"/> en <c>false</c> siembra el artículo SIN fila en
    /// <c>precios</c> — el caso "perdió su precio entre el cobro offline y el sync".</summary>
    private async Task<(int IdArticulo, int IdMedioEfectivo)> SembrarServicioYMedioEfectivoAsync(
        int idTenant, decimal? precioVigente)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Ventas-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
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

        if (precioVigente is { } precio)
        {
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
        }

        return (articulo.Id, idMedioEfectivo);
    }

    private static SolicitudDeVenta SolicitudConPrecioOffline(
        int idPuntoVenta, int idArticulo, int idMedioPago, decimal totalPagado, long? numeroPreasignado,
        decimal? precioUnitarioOffline, decimal? descuentoUnitarioOffline = null) =>
        new(idPuntoVenta, null, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null, IdLote: null, PrecioUnitario: precioUnitarioOffline, DescuentoUnitario: descuentoUnitarioOffline)],
            [new PagoDeVenta(idMedioPago, totalPagado, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numeroPreasignado);

    private static async Task ReservarBloqueAsync(HttpClient cajero, int idPuntoVenta, int cantidad = 5)
    {
        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", cantidad));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- ExigirPreciosOfflineValidos: un test por disyunto (mismo criterio que
    // ReservaDeNumeracionEndpointsTests para ExigirMismoContenido). Las cuatro mutaciones de abajo
    // se corrieron de verdad (comentar el arm, dotnet test --filter, observar rojo, revertir,
    // volver a correr, observar verde) contra este mismo archivo completo (11 tests) — cada una
    // enrojeció EXACTAMENTE el/los test(s) nombrado(s) abajo y a ningún otro. --------------------

    /// <summary>Mutación CORRIDA: comentar el arm <c>precio_offline_no_admitido</c> puso este
    /// test en rojo — observado 409 (no 400): con el guard fuera, el flujo llegó hasta
    /// <c>PoliticaDeModoDePuntoVenta</c> (actor web contra un PV Escritorio), que rechazó por una
    /// razón DISTINTA. Sigue siendo un kill real (el código esperado nunca aparece sin el guard),
    /// no una survival. Revertida, vuelve a verde.</summary>
    [Fact]
    public async Task UnActorWebNoPuedeMandarPrecioDeLinea()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnActorWebNoPuedeMandarPrecioDeLinea));
        using var _admin = admin;
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas", SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: null, precioUnitarioOffline: 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_offline_no_admitido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mutación CORRIDA: comentar el mismo arm <c>precio_offline_no_admitido</c> puso
    /// ESTE test en rojo — observado 201 (venta emitida). Este es el kill "limpio" del disyunto:
    /// a diferencia de <see cref="UnActorWebNoPuedeMandarPrecioDeLinea"/>, acá el dispositivo SÍ
    /// pertenece a su propio punto de venta, así que sin el guard nada más lo detiene. Revertida,
    /// vuelve a verde; los otros 10 tests del archivo no se movieron en ninguna de las dos
    /// corridas.</summary>
    [Fact]
    public async Task UnDispositivoSinNumeroPreasignadoNoPuedeMandarPrecioDeLinea()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoSinNumeroPreasignadoNoPuedeMandarPrecioDeLinea));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sin-numero");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: null, precioUnitarioOffline: 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_offline_no_admitido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mutación CORRIDA: comentar el arm "descuento sin precio" (el primero del método,
    /// corre incluso sin <c>NumeroPreasignado</c>) puso este test en rojo — observado 409, otra
    /// vez vía <c>PoliticaDeModoDePuntoVenta</c> aguas abajo (mismo actor web + PV Escritorio que
    /// el primer test de este bloque). Kill real: sin el guard, el código
    /// <c>precio_offline_incompleto</c> nunca aparece. Revertida, vuelve a verde.</summary>
    [Fact]
    public async Task UnDescuentoSinPrecioEnUnaLineaEsInvalidoAunSinNumeroPreasignado()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDescuentoSinPrecioEnUnaLineaEsInvalidoAunSinNumeroPreasignado));
        using var _admin = admin;
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        // descuentoUnitario SIN precioUnitario, ni siquiera junto a NumeroPreasignado — este
        // disyunto corre ANTES del guard de NumeroPreasignado (ExigirPreciosOfflineValidos).
        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: null,
                precioUnitarioOffline: null, descuentoUnitarioOffline: 5m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_offline_incompleto", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mutación CORRIDA: comentar el arm "mezcla" puso este test en rojo — observado 500
    /// (<c>InvalidOperationException</c>: <c>Nullable object must have a value</c>), no 200. El
    /// arm siguiente (negativo) lee <c>l.PrecioUnitario!.Value</c> asumiendo que este arm ya
    /// garantizó que ninguna línea quedó sin precio; sin él, ese <c>!.Value</c> explota sobre la
    /// línea mezclada. Kill real por una vía distinta a la esperada — documentado, no
    /// escondido. Revertida, vuelve a verde.</summary>
    [Fact]
    public async Task ConPrecioOfflineTodasLasLineasTienenQueTraerPrecioONinguna()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ConPrecioOfflineTodasLasLineasTienenQueTraerPrecioONinguna));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo1, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var (idArticulo2, _) = await SembrarServicioYMedioEfectivoAsync(idTenant, 50m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "mezcla");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var solicitud = new SolicitudDeVenta(
            idPuntoVenta, null, "TX", null,
            [
                new LineaDeVenta(idArticulo1, 1m, null, IdLote: null, PrecioUnitario: 100m),
                new LineaDeVenta(idArticulo2, 1m, null) // sin precio offline: la mezcla bajo prueba
            ],
            [new PagoDeVenta(idMedio, 150m, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: 1);

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_offline_incompleto", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mutación CORRIDA: comentar el arm <c>precio_offline_invalido</c> puso AMBOS casos
    /// en rojo — observado 201 (precio -1: la venta se emitió con precio negativo) y 400 con el
    /// código equivocado <c>pago_no_ingresado</c> (descuento -1, en vez de
    /// <c>precio_offline_invalido</c>). Los dos disyuntos del OR (<c>PrecioUnitario &lt; 0</c> y
    /// <c>DescuentoUnitario &lt; 0</c>) quedan cada uno cubierto por un InlineData distinto.
    /// Revertida, vuelve a verde en los dos casos.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task PrecioODescuentoOfflineNegativoEsInvalido(decimal precio, decimal descuento)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(PrecioODescuentoOfflineNegativoEsInvalido)}-{precio}-{descuento}");
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "negativo");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 0m, numeroPreasignado: 1,
                precioUnitarioOffline: precio, descuentoUnitarioOffline: descuento));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_offline_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- El precio offline se registra, y el servidor todavía valida los pagos contra ÉL ------

    /// <summary>El test central de la Parte B: el artículo VALE HOY 150 (server-current), pero el
    /// dispositivo cobró 100 offline. Paga exactamente 100. Mutación CORRIDA (mutation-proof-tests
    /// regla 2, no solo razonada): borrar la rama <c>linea.PrecioUnitario is {} precioOffline</c>
    /// de <c>MaterializarItems</c> puso este test en rojo (esperado 201, observado 400 — coherente
    /// con <c>ValidadorDePagos</c> viendo un total de 150 contra un pago de 100) sin tocar ningún
    /// otro de los tests de <c>ExigirPreciosOfflineValidos</c>; revertida, vuelve a verde. La misma
    /// mutación también enrojeció <see cref="LaDiscrepanciaDePrecioOfflineQuedaAuditada"/> y
    /// <see cref="SinPrecioVigenteHoyLaVentaOfflineSincronizaIgualSinMarcarDiscrepancia"/> (este
    /// último con el mismo <c>articulo_sin_precio_vigente</c> que el diseño offline existe para
    /// evitar) — ningún otro test del archivo se movió.</summary>
    [Fact]
    public async Task LaVentaOfflineRegistraElPrecioQueElDispositivoCobroYValidaLosPagosContraEse()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaVentaOfflineRegistraElPrecioQueElDispositivoCobroYValidaLosPagosContraEse));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 150m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "discrepante");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: 1, precioUnitarioOffline: 100m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(100m, emitido.Total);
        var item = Assert.Single(emitido.Items);
        Assert.Equal(100m, item.PrecioUnitario);
        Assert.Equal(100m, item.Total);
        Assert.True(item.PrecioDiscrepante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var persistido = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == emitido.Id).Select(i => i.PrecioUnitario).SingleAsync();
        Assert.Equal(100m, persistido);
    }

    /// <summary>judgment-day ronda 1 (BLOCKER): con descuento, <c>PrecioUnitario</c> viaja como el
    /// precio de LISTA (bruto) y <c>DescuentoUnitario</c> por separado — exactamente lo que manda
    /// el POS web (<c>enriquecerLineasConPrecioOffline</c>: <c>precioOriginal</c>/
    /// <c>descuentoUnitario</c>, nunca <c>precioFinal</c> ya neto). El ticket mostró y el cajero
    /// cobró 120 (150 de lista − 30 de descuento); el total persistido tiene que ser 120, calculado
    /// por <c>CalculadorDeTotales</c> EXACTAMENTE como si <c>precioUnitarioOffline</c> fuera el
    /// precio de lista (nunca ya neto) — si el emisor mandara el neto (120) como precio con el
    /// mismo descuento de 30, el servidor restaría el descuento DOS VECES y persistiría 90 (el bug
    /// real de este BLOCKER, reproducido y cerrado en el front por
    /// <c>useSincronizacionOffline.test.ts</c>, que sí puede distinguir "mandó el bruto" de "mandó
    /// el neto"); este test cubre que el SERVIDOR aplica ese contrato bruto/descuento
    /// correctamente de punta a punta.</summary>
    [Fact]
    public async Task LaVentaOfflineConDescuentoRegistraElNetoQueElDispositivoCobro()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaVentaOfflineConDescuentoRegistraElNetoQueElDispositivoCobro));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 150m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "con-descuento");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 120m, numeroPreasignado: 1,
                precioUnitarioOffline: 150m, descuentoUnitarioOffline: 30m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(120m, emitido.Total);
        var item = Assert.Single(emitido.Items);
        Assert.Equal(150m, item.PrecioUnitario);
        Assert.Equal(30m, item.Descuento);
        Assert.Equal(120m, item.Total);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var persistido = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == emitido.Id)
            .Select(i => new { i.PrecioUnitario, i.Descuento, i.Total })
            .SingleAsync();
        Assert.Equal(150m, persistido.PrecioUnitario);
        Assert.Equal(30m, persistido.Descuento);
        Assert.Equal(120m, persistido.Total);
    }

    /// <summary>Rastro auditable de la discrepancia — nunca una columna nueva (DB CHANGE GATE
    /// evitado a propósito). Mutación CORRIDA: neutralizar la condición
    /// <c>if (lineasDiscrepantes.Count &gt; 0)</c> de <c>EjecutarTransaccionAsync</c> (a
    /// <c>if (false &amp;&amp; ...)</c>) puso ESTE test en rojo (<c>SingleAsync</c> sobre 0 filas
    /// de <c>auditoria</c>) y a NINGÚN otro de los 10 restantes del archivo — la venta offline
    /// sigue emitiéndose 201 con <c>PrecioDiscrepante = true</c> igual, la única baja es el
    /// rastro. Revertida, vuelve a verde.</summary>
    [Fact]
    public async Task LaDiscrepanciaDePrecioOfflineQuedaAuditada()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaDiscrepanciaDePrecioOfflineQuedaAuditada));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 150m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "auditoria");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: 1, precioUnitarioOffline: 100m));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.Auditoria
            .Where(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia")
            .SingleAsync();

        Assert.Equal("comprobante_venta", fila.Entidad);
        Assert.Equal(emitido.Id, fila.IdEntidad);
        Assert.Equal(idPuntoVenta, fila.IdPuntoVenta);

        using var payload = JsonDocument.Parse(fila.ValorNuevo);
        var itemAuditado = payload.RootElement.GetProperty("items")[0];
        Assert.Equal(idArticulo, itemAuditado.GetProperty("id_articulo").GetInt32());
        Assert.Equal(100m, itemAuditado.GetProperty("precio_cobrado").GetDecimal());
        Assert.Equal(150m, itemAuditado.GetProperty("precio_esperado").GetDecimal());
    }

    /// <summary>Sin discrepancia (precio offline == precio server-current), sin fila de
    /// auditoría — el flag/rastro solo aparece cuando hay algo real que reportar.</summary>
    [Fact]
    public async Task SinDiscrepanciaNoHayFilaDeAuditoriaYElItemNoQuedaMarcado()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(SinDiscrepanciaNoHayFilaDeAuditoriaYElItemNoQuedaMarcado));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sin-discrepancia");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: 1, precioUnitarioOffline: 100m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.False(Assert.Single(emitido.Items).PrecioDiscrepante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var cantidad = await db.Auditoria.CountAsync(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia");
        Assert.Equal(0, cantidad);
    }

    /// <summary>El artículo perdió su precio vigente ENTRE el cobro offline y este sync (fila de
    /// <c>precios</c> ausente) — la venta offline tiene que poder sincronizar igual (el ticket ya
    /// se le dio al cliente), sin la excepción <c>articulo_sin_precio_vigente</c> del camino
    /// online, y sin fabricar una discrepancia contra una expectativa que no existe.</summary>
    [Fact]
    public async Task SinPrecioVigenteHoyLaVentaOfflineSincronizaIgualSinMarcarDiscrepancia()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(SinPrecioVigenteHoyLaVentaOfflineSincronizaIgualSinMarcarDiscrepancia));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, precioVigente: null);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sin-precio-hoy");
        using var _cajero = cajero;
        await ReservarBloqueAsync(cajero, idPuntoVenta);
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudConPrecioOffline(idPuntoVenta, idArticulo, idMedio, 100m, numeroPreasignado: 1, precioUnitarioOffline: 100m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(100m, emitido.Total);
        Assert.False(Assert.Single(emitido.Items).PrecioDiscrepante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var cantidad = await db.Auditoria.CountAsync(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia");
        Assert.Equal(0, cantidad);
    }

    /// <summary>El camino online (dispositivo que omite <c>NumeroPreasignado</c> porque está
    /// online, spec: "Un dispositivo puede seguir omitiendo el número: se asigna como siempre",
    /// docs/10 §9.2) sigue exactamente igual: el total sale de
    /// <c>ServicioDeOfertas.ResolverAsync</c>, nunca de lo que el request hubiera podido mandar.
    /// Un actor WEB (sin claim de dispositivo) no puede ejercer este mismo camino contra ESTE
    /// punto de venta porque el tenant se aprovisiona en modo Escritorio (mismo motivo que
    /// <c>PoliticaDeModoDePuntoVenta</c> exige en todo el resto de esta suite) — el camino web
    /// puro ya está cubierto, sin tocar precio offline, por el resto de la suite de checkout
    /// (<c>VentasCheckoutTests</c>, no duplicado acá).</summary>
    [Fact]
    public async Task ElCaminoOnlineSigueCobrandoElPrecioDelServidorSinNumeroPreasignado()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ElCaminoOnlineSigueCobrandoElPrecioDelServidorSinNumeroPreasignado));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedio) = await SembrarServicioYMedioEfectivoAsync(idTenant, 150m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "online-sin-numero");
        using var _cajero = cajero;
        admin.Dispose();

        var solicitud = new SolicitudDeVenta(
            idPuntoVenta, null, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedio, 150m, null, 0m)], null, null);

        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(150m, emitido.Total);
        Assert.False(Assert.Single(emitido.Items).PrecioDiscrepante);
    }
}
