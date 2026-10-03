using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Ajuste manual de precio por línea (<c>LineaDeVenta.AjusteManualPorcentaje</c>): superficie HTTP
/// completa de <c>POST /api/ventas</c> en el camino online (actor web contra un punto de venta Web)
/// y en el offline (dispositivo con número pre-asignado y precio propio). El cliente manda SOLO el
/// porcentaje; todo importe lo calcula el servidor (<c>CalculadorDeTotales</c>). Mismo trámite de
/// siembra que <c>VentasCheckoutTests</c> / <c>VentaOfflinePrecioTests</c> — no se comparte helper
/// entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaAjusteManualTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdTurno, HttpClient Cliente, int IdMedioEfectivo);

    // ---- siembra ---------------------------------------------------------------------------

    private async Task<(int IdTenant, int IdPuntoVenta, int IdUsuarioAdmin, HttpClient Admin)> AprovisionarAsync(
        string nombre, ModoPuntoVenta modo)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants", new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, modo));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin);
    }

    private async Task<(int IdTurno, int IdMedioEfectivo)> AbrirTurnoYBuscarEfectivoAsync(
        int idTenant, int idPuntoVenta, int idEmpleadoApertura)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        return (turno.Id, idMedioEfectivo);
    }

    /// <summary>Tenant en modo Web: el admin vende directo, sin dispositivo ni número pre-asignado.</summary>
    private async Task<Contexto> PrepararOnlineAsync(string nombre)
    {
        var (idTenant, idPuntoVenta, idUsuarioAdmin, admin) = await AprovisionarAsync(nombre, ModoPuntoVenta.Web);
        var (idTurno, idMedioEfectivo) = await AbrirTurnoYBuscarEfectivoAsync(idTenant, idPuntoVenta, idUsuarioAdmin);
        return new Contexto(idTenant, idPuntoVenta, idTurno, admin, idMedioEfectivo);
    }

    /// <summary>Tenant en modo Escritorio: vende un cajero de dispositivo, con un bloque de
    /// numeración ya reservado (el primer número pre-asignado es el 1).</summary>
    private async Task<Contexto> PrepararOfflineAsync(string nombre)
    {
        var (idTenant, idPuntoVenta, idUsuarioAdmin, admin) = await AprovisionarAsync(nombre, ModoPuntoVenta.Escritorio);
        var (idTurno, idMedioEfectivo) = await AbrirTurnoYBuscarEfectivoAsync(idTenant, idPuntoVenta, idUsuarioAdmin);

        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, $"Caja {nombre}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valorCookie = setCookie[prefijo.Length..];
        var cookieDispositivo = valorCookie[..valorCookie.IndexOf(';')];
        admin.Dispose();

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = "cajero",
                Mail = $"cajero-{idTenant}@ways.test",
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
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        Assert.Equal(HttpStatusCode.OK, (await cajero.SendAsync(solicitud)).StatusCode);

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        return new Contexto(idTenant, idPuntoVenta, idTurno, cajero, idMedioEfectivo);
    }

    /// <summary>Artículo de servicio (sin stock) con precio vigente de lista HOY.</summary>
    private async Task<int> SembrarServicioAsync(int idTenant, decimal precio)
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

        return articulo.Id;
    }

    private static SolicitudDeVenta VentaOnline(Contexto ctx, decimal pagado, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], null, null);

    private static SolicitudDeVenta VentaOffline(Contexto ctx, decimal pagado, long numero, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numero);

    private static async Task<ComprobanteEmitido> EmitirOkAsync(HttpClient cliente, SolicitudDeVenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeRechazoAsync(HttpClient cliente, SolicitudDeVenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;
    }

    private async Task<(ComprobanteVenta Comprobante, List<ItemComprobanteVenta> Items)> LeerPersistidoAsync(
        int idTenant, int idComprobante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var comprobante = await db.ComprobantesVenta.AsNoTracking().SingleAsync(c => c.Id == idComprobante);
        var items = await db.ItemsComprobanteVenta.AsNoTracking()
            .Where(i => i.IdComprobanteVenta == idComprobante).OrderBy(i => i.Orden).ToListAsync();
        return (comprobante, items);
    }

    private async Task<int> ContarComprobantesAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ComprobantesVenta.CountAsync();
    }

    // ---- online: persistencia, totales y lectura ---------------------------------------------

    /// <summary>Tres líneas con valores todos distintos (descuento, recargo, sin ajuste) para que
    /// ningún swap entre líneas ni entre columnas pase: A 2 x 100 con -10 % (-20), B 1 x 50 con +20 %
    /// (+10) y C 1 x 30 sin ajuste. Subtotal 280, descuento manual 20, recargo manual 10, total 270.
    /// La misma verdad tiene que salir del emit, de la base y del detalle.</summary>
    [Fact]
    public async Task UnaVentaOnlineConAjustesManualesPersisteLasCuatroColumnasYElDetalleDevuelveLoMismo()
    {
        var ctx = await PrepararOnlineAsync(
            nameof(UnaVentaOnlineConAjustesManualesPersisteLasCuatroColumnasYElDetalleDevuelveLoMismo));
        var idA = await SembrarServicioAsync(ctx.IdTenant, 100m);
        var idB = await SembrarServicioAsync(ctx.IdTenant, 50m);
        var idC = await SembrarServicioAsync(ctx.IdTenant, 30m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(
                ctx, 270m,
                new LineaDeVenta(idA, 2m, null, AjusteManualPorcentaje: -10m),
                new LineaDeVenta(idB, 1m, null, AjusteManualPorcentaje: 20m),
                new LineaDeVenta(idC, 1m, null)));

        Assert.Equal(280m, emitido.Subtotal);
        Assert.Equal(0m, emitido.DescuentoTotal);
        Assert.Equal(20m, emitido.DescuentoManualTotal);
        Assert.Equal(10m, emitido.RecargoManualTotal);
        Assert.Equal(270m, emitido.Total);

        // Identidades del contrato: subtotal = Σ bruto de línea; total = subtotal - descuento -
        // descuento manual + recargo manual = Σ total de línea; total de línea = bruto - descuento
        // (solo oferta) + ajuste manual.
        Assert.Equal(emitido.Items.Sum(i => i.PrecioUnitario * i.Cantidad), emitido.Subtotal);
        Assert.Equal(
            emitido.Subtotal - emitido.DescuentoTotal - emitido.DescuentoManualTotal + emitido.RecargoManualTotal,
            emitido.Total);
        Assert.Equal(emitido.Items.Sum(i => i.Total), emitido.Total);
        Assert.All(
            emitido.Items,
            i => Assert.Equal(i.PrecioUnitario * i.Cantidad - i.Descuento + i.AjusteManual, i.Total));

        void AssertItems(IReadOnlyList<(int IdArticulo, decimal? Porcentaje, decimal Ajuste, decimal Total)> reales)
        {
            Assert.Equal(
                [(idA, (decimal?)-10m, -20m, 180m), (idB, 20m, 10m, 60m), (idC, null, 0m, 30m)],
                reales);
        }

        AssertItems(emitido.Items
            .Select(i => (i.IdArticulo!.Value, i.AjusteManualPorcentaje, i.AjusteManual, i.Total)).ToList());
        Assert.Equal([200m, 50m, 30m], emitido.Items.Select(i => i.PrecioUnitario * i.Cantidad).ToList());

        var (comprobante, items) = await LeerPersistidoAsync(ctx.IdTenant, emitido.Id);
        Assert.Equal(280m, comprobante.Subtotal);
        Assert.Equal(0m, comprobante.DescuentoTotal);
        Assert.Equal(20m, comprobante.DescuentoManualTotal);
        Assert.Equal(10m, comprobante.RecargoManualTotal);
        Assert.Equal(270m, comprobante.Total);
        AssertItems(items.Select(i => (i.IdArticulo!.Value, i.AjusteManualPorcentaje, i.AjusteManual, i.Total)).ToList());

        // Releer (reimpresión) devuelve el snapshot persistido, con los nombres JSON del contrato.
        var detalle = await ctx.Cliente.GetAsync($"/api/ventas/{emitido.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
        using var documento = JsonDocument.Parse(await detalle.Content.ReadAsStringAsync());
        var raiz = documento.RootElement;
        Assert.Equal(20m, raiz.GetProperty("descuentoManualTotal").GetDecimal());
        Assert.Equal(10m, raiz.GetProperty("recargoManualTotal").GetDecimal());
        Assert.Equal(270m, raiz.GetProperty("total").GetDecimal());

        var itemsJson = raiz.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, itemsJson.Count);
        Assert.Equal([-10m, 20m], itemsJson.Take(2).Select(i => i.GetProperty("ajusteManualPorcentaje").GetDecimal()).ToList());
        Assert.Equal(JsonValueKind.Null, itemsJson[2].GetProperty("ajusteManualPorcentaje").ValueKind);
        Assert.Equal([-20m, 10m, 0m], itemsJson.Select(i => i.GetProperty("ajusteManual").GetDecimal()).ToList());
        Assert.Equal([180m, 60m, 30m], itemsJson.Select(i => i.GetProperty("total").GetDecimal()).ToList());
    }

    /// <summary>El pago se valida contra el total YA ajustado: con +50 % sobre 1000 el total es 1500,
    /// así que pagar los 1000 sin ajustar es insuficiente (tolerancia de 10) y pagar 1500 es válido.</summary>
    [Fact]
    public async Task UnRecargoManualHaceInsuficienteElPagoDelTotalSinAjustar()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnRecargoManualHaceInsuficienteElPagoDelTotalSinAjustar));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 1000m);
        var linea = new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: 50m);

        var codigo = await CodigoDeRechazoAsync(ctx.Cliente, VentaOnline(ctx, 1000m, linea));
        Assert.Equal("tolerancia_de_pago_superada", codigo);
        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));

        var emitido = await EmitirOkAsync(ctx.Cliente, VentaOnline(ctx, 1500m, linea));
        Assert.Equal(1500m, emitido.Total);
        Assert.Equal(500m, emitido.Items[0].AjusteManual);
    }

    /// <summary>Con -50 % sobre 1000 el total es 500: pagar 500 alcanza. Si el pago se validara contra
    /// el total sin ajustar (1000) faltarían 500 y se rechazaría.</summary>
    [Fact]
    public async Task UnDescuentoManualBajaElTotalContraElQueSeValidaElPago()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnDescuentoManualBajaElTotalContraElQueSeValidaElPago));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 1000m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 500m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -50m)));

        Assert.Equal(500m, emitido.Total);
        Assert.Equal(-500m, emitido.Items[0].AjusteManual);
        Assert.Equal(500m, emitido.DescuentoManualTotal);
    }

    /// <summary>La oferta del servidor (10 % sobre el artículo) se resta primero: 2 x 100 = 200 de
    /// bruto, 20 de oferta, neto 180, y el -10 % manual sale sobre 180 (-18), no sobre 200 (-20).</summary>
    [Fact]
    public async Task ElAjusteManualSeAplicaSobreElNetoPosteriorALaOfertaDelServidor()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElAjusteManualSeAplicaSobreElNetoPosteriorALaOfertaDelServidor));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var altaOferta = new AltaOferta(
            Nombre: "oferta del 10", IdEmpresa: null, IdArticulo: idArticulo, IdGrupo: null, IdCategoria: null,
            FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
            CantidadMinima: null, PrecioUnitario: null, Porcentaje: 10m, ImporteFijo: null,
            Prioridad: 0, Acumulable: false);
        var respuestaOferta = await ctx.Cliente.PostAsJsonAsync("/api/ofertas", altaOferta);
        Assert.Equal(HttpStatusCode.Created, respuestaOferta.StatusCode);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 162m, new LineaDeVenta(idArticulo, 2m, null, AjusteManualPorcentaje: -10m)));

        var item = Assert.Single(emitido.Items);
        Assert.NotNull(item.IdOferta);
        Assert.Equal(20m, item.Descuento);
        Assert.Equal(-10m, item.AjusteManualPorcentaje);
        Assert.Equal(-18m, item.AjusteManual);
        Assert.Equal(162m, item.Total);
        Assert.Equal(item.PrecioUnitario * item.Cantidad - item.Descuento + item.AjusteManual, item.Total);
        Assert.Equal(200m, emitido.Subtotal);
        Assert.Equal(20m, emitido.DescuentoTotal);
        Assert.Equal(18m, emitido.DescuentoManualTotal);
        Assert.Equal(162m, emitido.Total);
    }

    /// <summary>Cada valor inválido se rechaza con 400 <c>ajuste_manual_invalido</c> y no se persiste
    /// ningún comprobante.</summary>
    [Fact]
    public async Task UnPorcentajeCeroFueraDeRangoOConMasDeDosDecimalesSeRechazaConAjusteManualInvalidoSinPersistirNada()
    {
        var ctx = await PrepararOnlineAsync(
            nameof(UnPorcentajeCeroFueraDeRangoOConMasDeDosDecimalesSeRechazaConAjusteManualInvalidoSinPersistirNada));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        foreach (var invalido in new[] { 0m, 0.00m, 100.01m, -100.01m, 101m, 0.001m, 12.345m, -33.333m })
        {
            var codigo = await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOnline(ctx, 100m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: invalido)));

            Assert.True(codigo == "ajuste_manual_invalido", $"porcentaje {invalido}: código {codigo}");
        }

        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Los límites exactos y los mínimos con dos decimales son válidos: -100 % deja la línea
    /// en cero (se cobra 0), +100 % la duplica, y 0.01 / -99.99 no se rechazan por redondeo.</summary>
    [Fact]
    public async Task LosPorcentajesLimiteSeAceptanYMenosCienPorCientoDejaLaLineaEnCero()
    {
        var ctx = await PrepararOnlineAsync(nameof(LosPorcentajesLimiteSeAceptanYMenosCienPorCientoDejaLaLineaEnCero));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var gratis = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 0m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -100m)));
        Assert.Equal(0m, gratis.Total);
        Assert.Equal(-100m, gratis.Items[0].AjusteManual);
        Assert.Equal(100m, gratis.DescuentoManualTotal);

        var doble = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 200m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: 100m)));
        Assert.Equal(200m, doble.Total);
        Assert.Equal(100m, doble.RecargoManualTotal);

        var minimoPositivo = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 100.01m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: 0.01m)));
        Assert.Equal(0.01m, minimoPositivo.Items[0].AjusteManual);
        Assert.Equal(100.01m, minimoPositivo.Total);

        var casiTodo = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 0.01m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -99.99m)));
        Assert.Equal(-99.99m, casiTodo.Items[0].AjusteManual);
        Assert.Equal(0.01m, casiTodo.Total);
    }

    /// <summary>Con <c>IdPresupuestoOrigen</c> las líneas no se admiten: la conversión rechaza
    /// <c>lineas_no_admitidas</c> antes de resolver el presupuesto (el id acá ni siquiera existe), así
    /// que un porcentaje no puede llegar ahí aceptado y descartado. Prueba el rechazo del request;
    /// que la conversión ignore un porcentaje no es observable, porque nunca lo recibe.</summary>
    [Fact]
    public async Task UnaConversionDePresupuestoConLineasQueTraenPorcentajeSeRechazaConLineasNoAdmitidas()
    {
        var ctx = await PrepararOnlineAsync(
            nameof(UnaConversionDePresupuestoConLineasQueTraenPorcentajeSeRechazaConLineasNoAdmitidas));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -10m)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 90m, null, 0m)], null, null, IdPresupuestoOrigen: 999_999);

        Assert.Equal("lineas_no_admitidas", await CodigoDeRechazoAsync(ctx.Cliente, solicitud));
        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Una devolución (NCX, cantidad negativa) sigue la convención de signo de
    /// <c>descuento_total</c>: un -10 % sobre -100 acerca el total a cero (+10) y se cuenta como
    /// DESCUENTO manual con signo negativo (-10); un +10 % lo aleja (-10) y se cuenta como RECARGO
    /// manual negativo. Clasificar por el signo del monto invertiría los dos.</summary>
    [Fact]
    public async Task UnaNcxClasificaDescuentoYRecargoManualPorElSignoDelPorcentaje()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnaNcxClasificaDescuentoYRecargoManualPorElSignoDelPorcentaje));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var conDescuento = await EmitirOkAsync(
            ctx.Cliente,
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, null, "NCX", null,
                [new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -10m)], [], null, null));
        Assert.Equal(-100m, conDescuento.Subtotal);
        Assert.Equal(-10m, conDescuento.DescuentoManualTotal);
        Assert.Equal(0m, conDescuento.RecargoManualTotal);
        Assert.Equal(-90m, conDescuento.Total);
        Assert.Equal(-10m, conDescuento.Items[0].AjusteManualPorcentaje);
        Assert.Equal(10m, conDescuento.Items[0].AjusteManual);
        Assert.Equal(-90m, conDescuento.Items[0].Total);

        var conRecargo = await EmitirOkAsync(
            ctx.Cliente,
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, null, "NCX", null,
                [new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: 10m)], [], null, null));
        Assert.Equal(0m, conRecargo.DescuentoManualTotal);
        Assert.Equal(-10m, conRecargo.RecargoManualTotal);
        Assert.Equal(-110m, conRecargo.Total);
        Assert.Equal(-10m, conRecargo.Items[0].AjusteManual);
    }

    /// <summary>Tres ventas con totales manuales distintos entre sí (descuento, recargo, ninguno) para
    /// que el listado y las ventas del turno asignen cada valor a su fila y a su columna.</summary>
    [Fact]
    public async Task ElListadoYLasVentasDelTurnoExponenLosTotalesManualesDeCadaFila()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElListadoYLasVentasDelTurnoExponenLosTotalesManualesDeCadaFila));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 200m);

        var conDescuento = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 170m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -15m)));
        var conRecargo = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 250m, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: 25m)));
        var sinAjuste = await EmitirOkAsync(ctx.Cliente, VentaOnline(ctx, 200m, new LineaDeVenta(idArticulo, 1m, null)));

        var esperado = new Dictionary<int, (decimal Total, decimal Descuento, decimal Recargo)>
        {
            [conDescuento.Id] = (170m, 30m, 0m),
            [conRecargo.Id] = (250m, 0m, 50m),
            [sinAjuste.Id] = (200m, 0m, 0m)
        };

        var listado = await ctx.Cliente.GetAsync($"/api/ventas?idPuntoVenta={ctx.IdPuntoVenta}&tamanio=50");
        Assert.Equal(HttpStatusCode.OK, listado.StatusCode);
        var cuerpoListado = await listado.Content.ReadAsStringAsync();
        var pagina = JsonSerializer.Deserialize<PaginaDeVentas>(cuerpoListado, OpcionesJson)!;
        Assert.Equal(3, pagina.Items.Count);
        foreach (var fila in pagina.Items)
        {
            Assert.Equal(esperado[fila.Id], (fila.Total, fila.DescuentoManualTotal, fila.RecargoManualTotal));
        }

        using (var documento = JsonDocument.Parse(cuerpoListado))
        {
            var filaJson = documento.RootElement.GetProperty("items").EnumerateArray()
                .Single(f => f.GetProperty("id").GetInt32() == conRecargo.Id);
            Assert.Equal(0m, filaJson.GetProperty("descuentoManualTotal").GetDecimal());
            Assert.Equal(50m, filaJson.GetProperty("recargoManualTotal").GetDecimal());
        }

        var porTurno = await ctx.Cliente.GetAsync($"/api/ventas/por-turno/{ctx.IdTurno}");
        Assert.Equal(HttpStatusCode.OK, porTurno.StatusCode);
        var cuerpoPorTurno = await porTurno.Content.ReadAsStringAsync();
        var filasDeTurno = JsonSerializer.Deserialize<List<VentaDeTurnoListado>>(cuerpoPorTurno, OpcionesJson)!;
        Assert.Equal(3, filasDeTurno.Count);
        foreach (var fila in filasDeTurno)
        {
            Assert.Equal(esperado[fila.Id], (fila.Total, fila.DescuentoManualTotal, fila.RecargoManualTotal));
        }

        using var documentoDeTurno = JsonDocument.Parse(cuerpoPorTurno);
        var filaDeTurnoJson = documentoDeTurno.RootElement.EnumerateArray()
            .Single(f => f.GetProperty("id").GetInt32() == conDescuento.Id);
        Assert.Equal(30m, filaDeTurnoJson.GetProperty("descuentoManualTotal").GetDecimal());
        Assert.Equal(0m, filaDeTurnoJson.GetProperty("recargoManualTotal").GetDecimal());
    }

    // ---- offline: discrepancia de precio, pagos y validación ----------------------------------

    private async Task<int> ContarDiscrepanciasAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.Auditoria.CountAsync(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia");
    }

    /// <summary>El dispositivo cobró el MISMO precio que hoy resuelve el servidor (100) y el cajero le
    /// aplicó -10 % a mano: la única diferencia es el ajuste, que no es una discrepancia de precio —
    /// ni la marca del item ni una fila de auditoría — pero sí se persiste y se cobra (90).</summary>
    [Fact]
    public async Task UnDescuentoManualOfflineSoloNoMarcaDiscrepanciaNiAuditoria()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnDescuentoManualOfflineSoloNoMarcaDiscrepanciaNiAuditoria));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 90m, numero: 1,
                new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: -10m)));

        var item = Assert.Single(emitido.Items);
        Assert.False(item.PrecioDiscrepante);
        Assert.Equal(100m, item.PrecioUnitario);
        Assert.Equal(-10m, item.AjusteManualPorcentaje);
        Assert.Equal(-10m, item.AjusteManual);
        Assert.Equal(90m, item.Total);
        Assert.Equal(10m, emitido.DescuentoManualTotal);
        Assert.Equal(0m, emitido.RecargoManualTotal);
        Assert.Equal(90m, emitido.Total);
        Assert.Equal(0, await ContarDiscrepanciasAsync(ctx.IdTenant));

        var (comprobante, items) = await LeerPersistidoAsync(ctx.IdTenant, emitido.Id);
        Assert.Equal(10m, comprobante.DescuentoManualTotal);
        Assert.Equal(90m, comprobante.Total);
        var persistido = Assert.Single(items);
        Assert.Equal(-10m, persistido.AjusteManualPorcentaje);
        Assert.Equal(-10m, persistido.AjusteManual);
    }

    [Fact]
    public async Task UnRecargoManualOfflineSoloNoMarcaDiscrepanciaNiAuditoria()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnRecargoManualOfflineSoloNoMarcaDiscrepanciaNiAuditoria));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 125m, numero: 1,
                new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: 25m)));

        var item = Assert.Single(emitido.Items);
        Assert.False(item.PrecioDiscrepante);
        Assert.Equal(25m, item.AjusteManual);
        Assert.Equal(125m, item.Total);
        Assert.Equal(25m, emitido.RecargoManualTotal);
        Assert.Equal(0, await ContarDiscrepanciasAsync(ctx.IdTenant));
    }

    /// <summary>Una diferencia REAL de precio (el dispositivo cobró 100, el servidor resuelve 150)
    /// sigue marcándose aunque haya un -10 % manual: el ajuste no la enmascara. La fila de auditoría
    /// lleva el precio esperado y, aparte, el porcentaje y el monto del ajuste que forman parte del
    /// total cobrado.</summary>
    [Fact]
    public async Task UnaDiferenciaRealDePrecioConAjusteManualSigueMarcandoDiscrepanciaYLaAuditaConElAjuste()
    {
        var ctx = await PrepararOfflineAsync(
            nameof(UnaDiferenciaRealDePrecioConAjusteManualSigueMarcandoDiscrepanciaYLaAuditaConElAjuste));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 150m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 90m, numero: 1,
                new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: -10m)));

        var item = Assert.Single(emitido.Items);
        Assert.True(item.PrecioDiscrepante);
        Assert.Equal(90m, item.Total);
        Assert.Equal(90m, emitido.Total);
        Assert.Equal(1, await ContarDiscrepanciasAsync(ctx.IdTenant));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.IdTenant == ctx.IdTenant && a.Accion == "venta.discrepancia");
        using var payload = JsonDocument.Parse(fila.ValorNuevo);
        var auditado = payload.RootElement.GetProperty("items")[0];
        Assert.Equal(100m, auditado.GetProperty("precio_cobrado").GetDecimal());
        Assert.Equal(150m, auditado.GetProperty("precio_esperado").GetDecimal());
        Assert.Equal(90m, auditado.GetProperty("total_cobrado").GetDecimal());
        Assert.Equal(-10m, auditado.GetProperty("ajuste_manual_porcentaje").GetDecimal());
        Assert.Equal(-10m, auditado.GetProperty("ajuste_manual").GetDecimal());
    }

    private async Task<JsonElement> LeerItemAuditadoDeLaDiscrepanciaAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia");
        using var payload = JsonDocument.Parse(fila.ValorNuevo);
        return payload.RootElement.GetProperty("items")[0].Clone();
    }

    /// <summary>Cláusula bajo prueba: la comparación de <c>MaterializarItems</c> es sobre el neto
    /// ANTERIOR al ajuste. Con −100 % el total cobrado es 0 y el esperado por el servidor también
    /// sería 0 si se comparara ya ajustado, así que un precio de dispositivo distinto (80 contra los
    /// 100 de lista) no se vería: tiene que marcar y dejar la fila de auditoría.</summary>
    [Fact]
    public async Task UnaDiferenciaDePrecioOfflineSigueMarcandoDiscrepanciaConMenosCienPorCientoDeAjuste()
    {
        var ctx = await PrepararOfflineAsync(
            nameof(UnaDiferenciaDePrecioOfflineSigueMarcandoDiscrepanciaConMenosCienPorCientoDeAjuste));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 0m, numero: 1,
                new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 80m, AjusteManualPorcentaje: -100m)));

        var item = Assert.Single(emitido.Items);
        Assert.True(item.PrecioDiscrepante);
        Assert.Equal(80m, item.PrecioUnitario);
        Assert.Equal(-80m, item.AjusteManual);
        Assert.Equal(0m, item.Total);
        Assert.Equal(1, await ContarDiscrepanciasAsync(ctx.IdTenant));

        var auditado = await LeerItemAuditadoDeLaDiscrepanciaAsync(ctx.IdTenant);
        Assert.Equal(80m, auditado.GetProperty("precio_cobrado").GetDecimal());
        Assert.Equal(100m, auditado.GetProperty("precio_esperado").GetDecimal());
        Assert.Equal(0m, auditado.GetProperty("total_cobrado").GetDecimal());
        Assert.Equal(-100m, auditado.GetProperty("ajuste_manual_porcentaje").GetDecimal());
        Assert.Equal(-80m, auditado.GetProperty("ajuste_manual").GetDecimal());
    }

    /// <summary>Misma cláusula con −50 %: el dispositivo cobró 10.01 y el servidor resuelve 10.00. El
    /// ajuste de 10.01 es −5.005 que redondea a −5.01 (total 5.00) y el de 10.00 es −5.00 (total
    /// 5.00): comparando totales ajustados los dos dan 5.00 y el centavo desaparece. Sobre el neto
    /// anterior al ajuste (10.01 contra 10.00) se ve.</summary>
    [Fact]
    public async Task UnCentavoDeDiferenciaDePrecioOfflineSigueMarcandoDiscrepanciaConMenosCincuentaPorCientoDeAjuste()
    {
        var ctx = await PrepararOfflineAsync(
            nameof(UnCentavoDeDiferenciaDePrecioOfflineSigueMarcandoDiscrepanciaConMenosCincuentaPorCientoDeAjuste));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 10m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 5m, numero: 1,
                new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 10.01m, AjusteManualPorcentaje: -50m)));

        var item = Assert.Single(emitido.Items);
        Assert.True(item.PrecioDiscrepante);
        Assert.Equal(-5.01m, item.AjusteManual);
        Assert.Equal(5m, item.Total);
        Assert.Equal(1, await ContarDiscrepanciasAsync(ctx.IdTenant));

        var auditado = await LeerItemAuditadoDeLaDiscrepanciaAsync(ctx.IdTenant);
        Assert.Equal(10.01m, auditado.GetProperty("precio_cobrado").GetDecimal());
        Assert.Equal(10m, auditado.GetProperty("precio_esperado").GetDecimal());
        Assert.Equal(-50m, auditado.GetProperty("ajuste_manual_porcentaje").GetDecimal());
    }

    /// <summary>Una línea offline con OFERTA del servidor (10 % sobre un artículo de 100) y un −10 %
    /// manual, con el precio y el descuento del dispositivo iguales a los del servidor, no es una
    /// discrepancia. 2 x 100 = 200, oferta 20, neto 180, ajuste −18 sobre el neto (no −20 sobre el
    /// bruto), total 162: si el esperado aplicara el porcentaje sobre el bruto (160) la línea se
    /// marcaría sin diferencia de precio alguna.</summary>
    [Fact]
    public async Task UnaLineaOfflineConOfertaDelServidorYAjusteManualConPrecioYDescuentoIgualesNoMarcaDiscrepancia()
    {
        var ctx = await PrepararOfflineAsync(
            nameof(UnaLineaOfflineConOfertaDelServidorYAjusteManualConPrecioYDescuentoIgualesNoMarcaDiscrepancia));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);
        await SembrarOfertaPorcentualAsync(ctx.IdTenant, idArticulo, 10m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 162m, numero: 1,
                new LineaDeVenta(
                    idArticulo, 2m, null, PrecioUnitario: 100m, DescuentoUnitario: 10m, AjusteManualPorcentaje: -10m)));

        var item = Assert.Single(emitido.Items);
        Assert.False(item.PrecioDiscrepante);
        Assert.NotNull(item.IdOferta);
        Assert.Equal(20m, item.Descuento);
        Assert.Equal(-18m, item.AjusteManual);
        Assert.Equal(162m, item.Total);
        Assert.Equal(0, await ContarDiscrepanciasAsync(ctx.IdTenant));
    }

    private async Task SembrarOfertaPorcentualAsync(int idTenant, int idArticulo, decimal porcentaje)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Ofertas.Add(new Oferta
        {
            IdTenant = idTenant,
            Nombre = $"Descuento {porcentaje}%",
            IdArticulo = idArticulo,
            Porcentaje = porcentaje,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>El pago offline también se valida contra el total ajustado: el dispositivo cobró 1000
    /// con +50 % (total 1500); un pago de 1000 no lo cubre y uno de 1500 sí.</summary>
    [Fact]
    public async Task UnaVentaOfflineConAjusteManualValidaLosPagosContraElTotalAjustado()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaVentaOfflineConAjusteManualValidaLosPagosContraElTotalAjustado));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 1000m);
        var linea = new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 1000m, AjusteManualPorcentaje: 50m);

        Assert.Equal(
            "tolerancia_de_pago_superada",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaOffline(ctx, 1000m, numero: 1, linea)));
        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));

        var emitido = await EmitirOkAsync(ctx.Cliente, VentaOffline(ctx, 1500m, numero: 1, linea));
        Assert.Equal(1500m, emitido.Total);
        Assert.False(Assert.Single(emitido.Items).PrecioDiscrepante);
    }

    [Fact]
    public async Task UnPorcentajeInvalidoSeRechazaTambienEnLaVentaOfflineConNumeroPreasignado()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnPorcentajeInvalidoSeRechazaTambienEnLaVentaOfflineConNumeroPreasignado));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);

        foreach (var invalido in new[] { 0m, 100.01m, -100.01m, 5.555m })
        {
            var codigo = await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOffline(
                    ctx, 100m, numero: 1,
                    new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: invalido)));

            Assert.True(codigo == "ajuste_manual_invalido", $"porcentaje {invalido}: código {codigo}");
        }

        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Reenviar la misma venta offline bajo el mismo número devuelve el comprobante ya
    /// emitido, leído de la base: el porcentaje y el monto de cada línea y los dos totales manuales
    /// tienen que volver iguales a los del primer envío.</summary>
    [Fact]
    public async Task ReenviarUnaVentaOfflineConAjusteManualDevuelveElMismoComprobanteConLosMismosAjustes()
    {
        var ctx = await PrepararOfflineAsync(
            nameof(ReenviarUnaVentaOfflineConAjusteManualDevuelveElMismoComprobanteConLosMismosAjustes));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);
        SolicitudDeVenta Solicitud() => VentaOffline(
            ctx, 112.5m, numero: 1,
            new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: 12.5m));

        var primera = await EmitirOkAsync(ctx.Cliente, Solicitud());
        var segunda = await EmitirOkAsync(ctx.Cliente, Solicitud());

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(12.5m, segunda.Items[0].AjusteManualPorcentaje);
        Assert.Equal(12.5m, segunda.Items[0].AjusteManual);
        Assert.Equal(112.5m, segunda.Items[0].Total);
        Assert.Equal(0m, segunda.DescuentoManualTotal);
        Assert.Equal(12.5m, segunda.RecargoManualTotal);
        Assert.Equal(112.5m, segunda.Total);
        Assert.Equal(1, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>El porcentaje es contenido tipeado por el operador, no dinero derivado por el
    /// servidor: reenviar el mismo número pre-asignado con otro porcentaje (o sin porcentaje) es otra
    /// venta y se rechaza 409 <c>numero_preasignado_con_otro_contenido</c> en vez de devolver en
    /// silencio el comprobante de la primera. Los pagos son idénticos en los tres envíos (90) y
    /// quedan dentro de la tolerancia del total de cada envío (95, 100 y 91 contra 90 pagados): el porcentaje es la
    /// ÚNICA diferencia entre las líneas.</summary>
    [Fact]
    public async Task ReenviarElMismoNumeroPreasignadoConOtroPorcentajeDeAjusteEs409()
    {
        var ctx = await PrepararOfflineAsync(nameof(ReenviarElMismoNumeroPreasignadoConOtroPorcentajeDeAjusteEs409));
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);
        SolicitudDeVenta Solicitud(decimal? porcentaje) => VentaOffline(
            ctx, 90m, numero: 1,
            new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: porcentaje));

        var primera = await EmitirOkAsync(ctx.Cliente, Solicitud(-10m));

        foreach (var otro in new decimal?[] { -5m, null, -9m })
        {
            var respuesta = await ctx.Cliente.PostAsJsonAsync("/api/ventas", Solicitud(otro));
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, $"porcentaje {otro}: {cuerpo}");
            Assert.Equal(
                "numero_preasignado_con_otro_contenido",
                JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
        }

        // El mismo contenido sigue devolviendo el comprobante original, y la base conserva el
        // porcentaje del primer envío.
        var reenvioIgual = await EmitirOkAsync(ctx.Cliente, Solicitud(-10m));
        Assert.Equal(primera.Id, reenvioIgual.Id);
        Assert.Equal(1, await ContarComprobantesAsync(ctx.IdTenant));
        var (_, items) = await LeerPersistidoAsync(ctx.IdTenant, primera.Id);
        Assert.Equal(-10m, Assert.Single(items).AjusteManualPorcentaje);
    }

    /// <summary>Dos líneas del MISMO artículo y la MISMA cantidad que solo difieren en el porcentaje
    /// (el API las acepta: no hay rechazo por artículo repetido), reenviadas bajo el mismo número en
    /// el orden inverso, son la misma venta y devuelven el comprobante original, no un 409. Cada caso
    /// de la teoría deja en rojo un desempate de <c>ExigirMismoContenido</c> al quitarlo: con el
    /// descuento primero, lo persistido ya queda de menor a mayor porcentaje y el reenvío trae las
    /// solicitadas al revés, así que solo las ordena su <c>ThenBy(Item3)</c>; con el recargo primero,
    /// el reenvío ya trae las solicitadas de menor a mayor porcentaje y lo persistido (por
    /// <c>orden</c>) queda al revés, así que solo lo ordena el <c>ThenBy(AjusteManualPorcentaje)</c>
    /// de las existentes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReenviarLasMismasLineasDelMismoArticuloYCantidadEnOtroOrdenDevuelveElComprobanteOriginal(
        bool primeroElDescuento)
    {
        var ctx = await PrepararOfflineAsync(
            $"ReenviarLineasMismoArticuloOtroOrden{(primeroElDescuento ? "DescuentoPrimero" : "RecargoPrimero")}");
        var idArticulo = await SembrarServicioAsync(ctx.IdTenant, 100m);
        var descuento = new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: -10m);
        var recargo = new LineaDeVenta(idArticulo, 1m, null, PrecioUnitario: 100m, AjusteManualPorcentaje: 20m);
        LineaDeVenta[] original = primeroElDescuento ? [descuento, recargo] : [recargo, descuento];
        LineaDeVenta[] invertido = [.. original.Reverse()];

        var primera = await EmitirOkAsync(ctx.Cliente, VentaOffline(ctx, 210m, numero: 1, original));
        var persistidoPorOrden = original.Select(l => l.AjusteManualPorcentaje).ToList();
        Assert.Equal(persistidoPorOrden, primera.Items.Select(i => i.AjusteManualPorcentaje).ToList());
        var (_, itemsPrimera) = await LeerPersistidoAsync(ctx.IdTenant, primera.Id);
        Assert.Equal(persistidoPorOrden, itemsPrimera.Select(i => i.AjusteManualPorcentaje).ToList());

        var segunda = await EmitirOkAsync(ctx.Cliente, VentaOffline(ctx, 210m, numero: 1, invertido));

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(persistidoPorOrden, segunda.Items.Select(i => i.AjusteManualPorcentaje).ToList());
        Assert.Equal(1, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Anular devuelve el comprobante releído de la base: conserva los cuatro campos del
    /// ajuste manual y la identidad del total.</summary>
    [Fact]
    public async Task LaAnulacionDevuelveElComprobanteConSusAjustesManuales()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaAnulacionDevuelveElComprobanteConSusAjustesManuales));
        var idA = await SembrarServicioAsync(ctx.IdTenant, 100m);
        var idB = await SembrarServicioAsync(ctx.IdTenant, 40m);
        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(
                ctx, 144m,
                new LineaDeVenta(idA, 1m, null, AjusteManualPorcentaje: -10m),
                new LineaDeVenta(idB, 1m, null, AjusteManualPorcentaje: 35m)));

        var respuesta = await ctx.Cliente.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var anulado = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
        Assert.Equal(10m, anulado.DescuentoManualTotal);
        Assert.Equal(14m, anulado.RecargoManualTotal);
        Assert.Equal(144m, anulado.Total);
        Assert.Equal(
            [(idA, (decimal?)-10m, -10m, 90m), (idB, 35m, 14m, 54m)],
            anulado.Items.Select(i => (i.IdArticulo!.Value, i.AjusteManualPorcentaje, i.AjusteManual, i.Total)).ToList());
    }
}
