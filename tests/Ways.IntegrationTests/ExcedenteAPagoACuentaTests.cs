using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/ventas</c> con <c>excedenteComoPagoACuenta</c>: lo cobrado por encima del total de una venta a un cliente con
/// cuenta corriente pasa a un recibo (RC) del cliente en la misma transacción. La venta conserva cobros por su total, el
/// recibo se lleva la diferencia con el mismo medio, el saldo del cliente se acredita y el cierre del turno cuenta el dinero
/// una sola vez. Sin la opción, con el Consumidor Final, offline, en una nota de crédito o sin excedente se rechaza.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ExcedenteAPagoACuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Cajero, int IdArticulo, int IdEfectivo, int IdQr, int IdConsumidorFinal,
        int IdClienteConCuenta);

    /// <summary>Tenant con un artículo de 990, un QR sin vuelto y un cliente con cuenta corriente. El cajero es el admin web,
    /// o con <paramref name="escritorio"/> un cajero de dispositivo.</summary>
    private async Task<Contexto> PrepararAsync(string nombre, bool escritorio = false)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}";
        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new Ways.Application.Organizacion.SolicitudDeAprovisionamiento(
                unico, $"{unico} SA", "Local 1", mailAdmin, escritorio ? ModoPuntoVenta.Escritorio : ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<Ways.Application.Organizacion.ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        int idArticulo, idEfectivo, idQr, idConsumidorFinal, idClienteConCuenta;
        await using (var db = Db(resultado.IdTenant))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            var qr = new MedioPago
            {
                IdTenant = resultado.IdTenant, Nombre = "QR", Orden = 60, Comportamiento = ComportamientoMedioPago.Electronico,
                AdmiteVuelto = false, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.MediosPago.Add(qr);
            await db.SaveChangesAsync();

            var articulo = new Articulo
            {
                IdTenant = resultado.IdTenant, CodigoInterno = "servicio", Nombre = "Servicio de prueba", IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
                EsProducto = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();

            var idLista = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            db.Precios.Add(new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idLista, Monto = 990m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            var cliente = new Cliente
            {
                IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente con cuenta",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(), IdListaPrecio = idLista,
                CreditoIlimitado = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();

            idArticulo = articulo.Id;
            idQr = qr.Id;
            idClienteConCuenta = cliente.Id;
            idEfectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
            idConsumidorFinal = await db.Clientes
                .Where(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal).Select(c => c.Id).FirstAsync();
        }

        var cajero = escritorio ? await LoguearComoCajeroDeDispositivoAsync(admin, resultado.IdTenant, resultado.IdPuntoVenta) : admin;

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, cajero, idArticulo, idEfectivo, idQr, idConsumidorFinal,
            idClienteConCuenta);
    }

    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(HttpClient admin, int idTenant, int idPuntoVenta)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        var cookie = valor[..valor.IndexOf(';')];

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant, NombreUsuario = "cajero-excedente", Mail = $"cajero-excedente-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor, PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-excedente", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookie}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cajero;
    }

    private WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Cajero.PostAsJsonAsync("/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private async Task SembrarTurnoAbiertoAsync(Contexto ctx)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleadoApertura = await db.Usuarios.Select(u => u.Id).FirstAsync(), FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta Venta(
        Contexto ctx, int idCliente, IReadOnlyList<PagoDeVenta> pagos, bool aCuenta = true, string tipo = "TX",
        long? numero = null) =>
        new(ctx.IdPuntoVenta, idCliente, tipo, null,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null, PrecioUnitario: numero is null ? null : 990m)],
            pagos, null, null, NumeroPreasignado: numero, ExcedenteComoPagoACuenta: aCuenta);

    private static async Task<ComprobanteEmitido> VenderAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private async Task AfirmarRechazoSinEscrituraAsync(Contexto ctx, SolicitudDeVenta solicitud, string codigo)
    {
        var respuesta = await ctx.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
    }

    private async Task<List<(int IdMedioPago, decimal Importe, decimal Vuelto)>> CobrosAsync(Contexto ctx, int idComprobante)
    {
        await using var db = Db(ctx.IdTenant);
        return (await db.Cobros.Where(c => c.IdComprobanteVenta == idComprobante).OrderBy(c => c.Id).ToListAsync())
            .Select(c => (c.IdMedioPago, c.Importe, c.Vuelto))
            .ToList();
    }

    [Fact]
    public async Task UnQrPorEncimaDelTotalDejaLaDiferenciaComoPagoACuentaYElCierreLaCuentaUnaVez()
    {
        var ctx = await PrepararAsync("excedenteqr");
        var turno = await AbrirTurnoAsync(ctx);

        var venta = await VenderAsync(ctx, Venta(ctx, ctx.IdClienteConCuenta, [new PagoDeVenta(ctx.IdQr, 1000m, null, 0m)]));

        Assert.Equal(990m, venta.Total);
        Assert.Equal((ctx.IdQr, 990m), (Assert.Single(venta.Pagos).IdMedioPago, venta.Pagos[0].Importe));
        Assert.NotNull(venta.PagoACuenta);
        Assert.Equal(10m, venta.PagoACuenta!.Importe);
        Assert.Equal([(ctx.IdQr, 990m, 0m)], await CobrosAsync(ctx, venta.Id));
        Assert.Equal([(ctx.IdQr, 10m, 0m)], await CobrosAsync(ctx, venta.PagoACuenta.Id));

        await using (var db = Db(ctx.IdTenant))
        {
            var recibo = await db.ComprobantesVenta.SingleAsync(c => c.Id == venta.PagoACuenta.Id);
            var tipoRc = await db.TiposComprobante.SingleAsync(t => t.Codigo == "RC");
            Assert.Equal(
                (tipoRc.Id, ctx.IdClienteConCuenta, turno, 10m, (string?)$"Excedente de la venta {venta.NumeroVisible}"),
                (recibo.IdTipoComprobante, recibo.IdCliente, recibo.IdTurnoCaja ?? 0, recibo.Total, recibo.Observaciones));
            Assert.Equal(venta.PagoACuenta.NumeroVisible, NumeroDeComprobante.Formatear(recibo.IdPuntoVenta, recibo.Numero));

            Assert.Equal(-10m, await db.Clientes.Where(c => c.Id == ctx.IdClienteConCuenta).Select(c => c.Saldo).SingleAsync());
            var movimiento = await db.MovimientosCuentaCorriente.SingleAsync(m => m.IdCliente == ctx.IdClienteConCuenta);
            Assert.Equal((TipoMovimientoCc.Pago, -10m, (int?)recibo.Id), (movimiento.Tipo, movimiento.Importe, movimiento.IdComprobanteVenta));
        }

        var cierre = await ctx.Cajero.PostAsJsonAsync(
            $"/api/caja/turnos/{turno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        var cuerpoCierre = await cierre.Content.ReadAsStringAsync();
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, cuerpoCierre);

        await using (var db = Db(ctx.IdTenant))
        {
            var esperado = await db.ArqueosTurno
                .Where(a => a.IdTurnoCaja == turno && a.IdMedioPago == ctx.IdQr).Select(a => a.ImporteEsperado).SingleAsync();
            Assert.Equal(1000m, esperado);
        }
    }

    [Fact]
    public async Task ElExcedenteSaleDelQrAntesQueDelEfectivoYElEfectivoTambienPuedeQuedarACuenta()
    {
        var ctx = await PrepararAsync("excedentemixto");
        await AbrirTurnoAsync(ctx);

        var mixta = await VenderAsync(ctx, Venta(
            ctx, ctx.IdClienteConCuenta,
            [new PagoDeVenta(ctx.IdEfectivo, 500m, null, 0m), new PagoDeVenta(ctx.IdQr, 600m, null, 0m)]));
        Assert.Equal([(ctx.IdEfectivo, 500m, 0m), (ctx.IdQr, 490m, 0m)], await CobrosAsync(ctx, mixta.Id));
        Assert.Equal([(ctx.IdQr, 110m, 0m)], await CobrosAsync(ctx, mixta.PagoACuenta!.Id));

        var efectivo = await VenderAsync(ctx, Venta(
            ctx, ctx.IdClienteConCuenta, [new PagoDeVenta(ctx.IdEfectivo, 1000m, null, 0m)]));
        Assert.Equal([(ctx.IdEfectivo, 990m, 0m)], await CobrosAsync(ctx, efectivo.Id));
        Assert.Equal([(ctx.IdEfectivo, 10m, 0m)], await CobrosAsync(ctx, efectivo.PagoACuenta!.Id));

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(-120m, await db.Clientes.Where(c => c.Id == ctx.IdClienteConCuenta).Select(c => c.Saldo).SingleAsync());
    }

    [Fact]
    public async Task SinLaOpcionUnQrPorEncimaDelTotalSigueRechazado()
    {
        var ctx = await PrepararAsync("excedentesinopcion");
        await AbrirTurnoAsync(ctx);

        await AfirmarRechazoSinEscrituraAsync(
            ctx, Venta(ctx, ctx.IdClienteConCuenta, [new PagoDeVenta(ctx.IdQr, 1100m, null, 0m)], aCuenta: false),
            "importe_supera_el_total");
    }

    [Fact]
    public async Task ElConsumidorFinalNoPuedeDejarElExcedenteACuenta()
    {
        var ctx = await PrepararAsync("excedentecf");
        await AbrirTurnoAsync(ctx);

        await AfirmarRechazoSinEscrituraAsync(
            ctx, Venta(ctx, ctx.IdConsumidorFinal, [new PagoDeVenta(ctx.IdQr, 1000m, null, 0m)]),
            "pago_a_cuenta_sin_cuenta_corriente");
    }

    [Fact]
    public async Task SinExcedenteLaOpcionSeRechaza()
    {
        var ctx = await PrepararAsync("excedentenulo");
        await AbrirTurnoAsync(ctx);

        await AfirmarRechazoSinEscrituraAsync(
            ctx, Venta(ctx, ctx.IdClienteConCuenta, [new PagoDeVenta(ctx.IdQr, 990m, null, 0m)]),
            "pago_a_cuenta_sin_excedente");
    }

    [Fact]
    public async Task UnaNotaDeCreditoNoAdmiteLaOpcion()
    {
        var ctx = await PrepararAsync("excedentencx");
        await AbrirTurnoAsync(ctx);

        await AfirmarRechazoSinEscrituraAsync(
            ctx, Venta(ctx, ctx.IdClienteConCuenta, [], tipo: "NCX"), "pago_a_cuenta_no_admitido");
    }

    [Fact]
    public async Task UnaVentaOfflineNoAdmiteLaOpcion()
    {
        var ctx = await PrepararAsync("excedenteoffline", escritorio: true);
        await SembrarTurnoAbiertoAsync(ctx);
        var reserva = await ctx.Cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(ctx.IdPuntoVenta, "TX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        await AfirmarRechazoSinEscrituraAsync(
            ctx, Venta(ctx, ctx.IdClienteConCuenta, [new PagoDeVenta(ctx.IdQr, 1000m, null, 0m)], numero: 1),
            "pago_a_cuenta_sin_conexion");
    }
}
