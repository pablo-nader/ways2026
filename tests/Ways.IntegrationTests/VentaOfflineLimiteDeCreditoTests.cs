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
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Venta local a clientes identificados: <c>SolicitudDeVenta.LimiteDeCreditoNoValidado</c> en
/// <c>POST /api/ventas</c>. El dispositivo cobró con cuenta corriente sin poder consultar el
/// límite y la venta se registra igual; si el saldo resultante supera el límite queda
/// <c>venta.sobrelimite</c> en <c>auditoria</c>. Mismo trámite de siembra que
/// <c>VentaOfflinePrecioTests</c>, sin helpers compartidos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaOfflineLimiteDeCreditoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";
    private const string AccionSobreLimite = "venta.sobrelimite";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        HttpClient Cajero, int IdTenant, int IdPuntoVenta, int IdArticulo, int IdMedioCuentaCorriente, int IdCliente);

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}";
        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var solicitud = new Ways.Application.Organizacion.SolicitudDeAprovisionamiento(
            unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
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

    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(HttpClient admin, int idTenant, int idPuntoVenta)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = "cajero-cc",
                Mail = $"cajero-cc-{idTenant}@ways.test",
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
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-cc", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cajero;
    }

    /// <summary>Tenant con turno abierto, un servicio a 300 en la lista general, un cliente con
    /// límite 1000 y el <paramref name="saldoInicial"/> dado, y un bloque de numeración del
    /// dispositivo.</summary>
    private async Task<Escenario> PrepararAsync(string nombre, decimal saldoInicial, bool creditoIlimitado = false)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nombre);
        int idArticulo, idMedioCuentaCorriente, idCliente;

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var idEmpleadoApertura = await db.Usuarios
                .Where(u => u.IdTenant == idTenant && u.NombreUsuario == "admin").Select(u => u.Id).FirstAsync();
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = idTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = idEmpleadoApertura,
                FechaApertura = ahora, FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });

            var area = new Area { IdTenant = idTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
            var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            var medioCuentaCorriente = new MedioPago
            {
                IdTenant = idTenant, Nombre = "Cuenta corriente", Orden = 3,
                Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.MediosPago.Add(medioCuentaCorriente);

            var articulo = new Articulo
            {
                IdTenant = idTenant, CodigoInterno = "servicio-cc", Nombre = "Servicio de prueba", IdArea = area.Id,
                IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = false,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);

            var cliente = new Cliente
            {
                IdTenant = idTenant, Numero = 5000, Nombre = "Cliente con cuenta",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = idListaGeneral, LimiteCredito = 1000m, CreditoIlimitado = creditoIlimitado,
                Saldo = saldoInicial, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();

            db.Precios.Add(new Precio
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral, Monto = 300m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();

            idMedioCuentaCorriente = medioCuentaCorriente.Id;
            idArticulo = articulo.Id;
            idCliente = cliente.Id;
        }

        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta);
        admin.Dispose();

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        return new Escenario(cajero, idTenant, idPuntoVenta, idArticulo, idMedioCuentaCorriente, idCliente);
    }

    /// <summary>La solicitud que drena el outbox: precio de línea congelado y número
    /// pre-asignado, pago entero en cuenta corriente.</summary>
    private static SolicitudDeVenta VentaEnCuentaCorriente(Escenario e, long? numeroPreasignado, bool limiteNoValidado) =>
        new(e.IdPuntoVenta, e.IdCliente, "TX", null,
            [new LineaDeVenta(e.IdArticulo, 1m, null, IdLote: null, PrecioUnitario: numeroPreasignado is null ? null : 300m)],
            [new PagoDeVenta(e.IdMedioCuentaCorriente, 300m, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numeroPreasignado, LimiteDeCreditoNoValidado: limiteNoValidado);

    private async Task<decimal> LeerSaldoAsync(Escenario e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        return await db.Clientes.Where(c => c.Id == e.IdCliente).Select(c => c.Saldo).SingleAsync();
    }

    private async Task<List<Ways.Domain.Auditoria.Auditoria>> LeerAuditoriaSobreLimiteAsync(Escenario e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        return await db.Auditoria.Where(a => a.IdTenant == e.IdTenant && a.Accion == AccionSobreLimite).ToListAsync();
    }

    private static async Task AfirmarErrorAsync(HttpResponseMessage respuesta, string codigo)
    {
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(codigo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Saldo 800 + consumo 300 = 1100 sobre un límite de 1000. Cláusulas que mata, cada
    /// una con su mutación corrida: el <c>exigirLimiteDeCredito</c> que <c>EmitirAsync</c> le
    /// pasa a <c>ValidadorDePagos</c> (forzado a <c>true</c>: 400 <c>limite_credito_excedido</c>),
    /// el <c>!plan.LimiteDeCreditoNoValidado</c> del backstop en transacción (borrado: el mismo
    /// 400 desde adentro de la transacción) y la escritura de auditoría (borrada: cero filas).
    /// El saldo anterior distinto de cero y del consumo hace que <c>saldo_anterior</c> solo pueda
    /// salir del <c>RETURNING</c>.</summary>
    [Fact]
    public async Task ConElLimiteNoValidadoLaVentaQueLoSuperaSeEmiteActualizaElSaldoYQuedaAuditada()
    {
        var e = await PrepararAsync(nameof(ConElLimiteNoValidadoLaVentaQueLoSuperaSeEmiteActualizaElSaldoYQuedaAuditada), 800m);
        using var _cajero = e.Cajero;

        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 1, limiteNoValidado: true));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(1100m, await LeerSaldoAsync(e));

        var fila = Assert.Single(await LeerAuditoriaSobreLimiteAsync(e));
        Assert.Equal("comprobante_venta", fila.Entidad);
        Assert.Equal(emitido.Id, fila.IdEntidad);
        Assert.Equal(e.IdPuntoVenta, fila.IdPuntoVenta);

        using var payload = JsonDocument.Parse(fila.ValorNuevo);
        var raiz = payload.RootElement;
        Assert.Equal(e.IdCliente, raiz.GetProperty("id_cliente").GetInt32());
        Assert.Equal(800m, raiz.GetProperty("saldo_anterior").GetDecimal());
        Assert.Equal(1100m, raiz.GetProperty("saldo_nuevo").GetDecimal());
        Assert.Equal(1000m, raiz.GetProperty("limite_credito").GetDecimal());
        Assert.Equal(1L, raiz.GetProperty("numero_comprobante").GetInt64());
    }

    [Fact]
    public async Task SinElFlagLaMismaVentaOfflineSeRechazaPorLimiteSinTocarElSaldo()
    {
        var e = await PrepararAsync(nameof(SinElFlagLaMismaVentaOfflineSeRechazaPorLimiteSinTocarElSaldo), 800m);
        using var _cajero = e.Cajero;

        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 1, limiteNoValidado: false));

        await AfirmarErrorAsync(respuesta, "limite_credito_excedido");
        Assert.Equal(800m, await LeerSaldoAsync(e));
        Assert.Empty(await LeerAuditoriaSobreLimiteAsync(e));
    }

    /// <summary>La cláusula: el guard <c>limite_no_validado_no_admitido</c> de
    /// <c>EmitirAsync</c>. El dispositivo pertenece al punto de venta y el saldo cabe en el
    /// límite, así que sin el guard nada más rechaza (mutación corrida: 201).</summary>
    [Fact]
    public async Task ElFlagSinNumeroPreasignadoSeRechaza()
    {
        var e = await PrepararAsync(nameof(ElFlagSinNumeroPreasignadoSeRechaza), 0m);
        using var _cajero = e.Cajero;

        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, null, limiteNoValidado: true));

        await AfirmarErrorAsync(respuesta, "limite_no_validado_no_admitido");
        Assert.Equal(0m, await LeerSaldoAsync(e));
    }

    /// <summary>La cláusula <c>saldoFinal &gt; plan.ClienteLimiteCredito</c> de la escritura de
    /// auditoría: 500 + 300 = 800 cabe en 1000, la venta sale sin rastro.</summary>
    [Fact]
    public async Task ConElFlagYElSaldoDentroDelLimiteNoQuedaAuditoria()
    {
        var e = await PrepararAsync(nameof(ConElFlagYElSaldoDentroDelLimiteNoQuedaAuditoria), 500m);
        using var _cajero = e.Cajero;

        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 1, limiteNoValidado: true));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(800m, await LeerSaldoAsync(e));
        Assert.Empty(await LeerAuditoriaSobreLimiteAsync(e));
    }

    /// <summary>La cláusula <c>!plan.ClienteCreditoIlimitado</c> de la escritura de auditoría: el
    /// saldo resultante supera el número del límite, pero el cliente no tiene límite.</summary>
    [Fact]
    public async Task ConElFlagYCreditoIlimitadoNoQuedaAuditoria()
    {
        var e = await PrepararAsync(nameof(ConElFlagYCreditoIlimitadoNoQuedaAuditoria), 800m, creditoIlimitado: true);
        using var _cajero = e.Cajero;

        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 1, limiteNoValidado: true));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(1100m, await LeerSaldoAsync(e));
        Assert.Empty(await LeerAuditoriaSobreLimiteAsync(e));
    }

    /// <summary>El outbox reenvía la MISMA solicitud cuando no supo si la primera llegó: el
    /// servidor la reconoce como la misma venta por número y contenido, sin un segundo consumo
    /// ni una segunda fila de auditoría.</summary>
    [Fact]
    public async Task ElReenvioConElMismoNumeroYElFlagDevuelveLaMismaVentaSinDuplicarSaldoNiAuditoria()
    {
        var e = await PrepararAsync(
            nameof(ElReenvioConElMismoNumeroYElFlagDevuelveLaMismaVentaSinDuplicarSaldoNiAuditoria), 800m);
        using var _cajero = e.Cajero;
        var solicitud = VentaEnCuentaCorriente(e, 1, limiteNoValidado: true);

        var primera = await e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var segunda = await e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        var idPrimera = (await primera.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!.Id;
        var idSegunda = (await segunda.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!.Id;
        Assert.Equal(idPrimera, idSegunda);
        Assert.Equal(1100m, await LeerSaldoAsync(e));
        Assert.Single(await LeerAuditoriaSobreLimiteAsync(e));
    }

    /// <summary>La cláusula <c>!reenvioDeVentaYaEmitida</c> de <c>EmitirAsync</c>. Saldo 700 +
    /// 300 deja al cliente justo en el límite: la venta entra validada. El reenvío (respuesta
    /// perdida) llega con el saldo ya en 1000, y la regla 6 lo juzgaría como si fuera otra venta
    /// de 300 — sin la cláusula, 400 <c>limite_credito_excedido</c> sobre una venta que sí quedó
    /// registrada, y el dispositivo la archivaría como rechazada.</summary>
    [Fact]
    public async Task ElReenvioDeUnaVentaValidadaYaEmitidaNoSeJuzgaDeNuevoContraElLimite()
    {
        var e = await PrepararAsync(nameof(ElReenvioDeUnaVentaValidadaYaEmitidaNoSeJuzgaDeNuevoContraElLimite), 700m);
        using var _cajero = e.Cajero;
        var solicitud = VentaEnCuentaCorriente(e, 1, limiteNoValidado: false);

        var primera = await e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(1000m, await LeerSaldoAsync(e));

        var segunda = await e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        var idPrimera = (await primera.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!.Id;
        var idSegunda = (await segunda.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!.Id;
        Assert.Equal(idPrimera, idSegunda);
        Assert.Equal(1000m, await LeerSaldoAsync(e));
    }

    /// <summary>Contracara de la anterior: un número NUEVO con el saldo ya en el límite sigue
    /// juzgándose contra él. Prueba que la excepción del reenvío no abre la puerta a otra
    /// venta.</summary>
    [Fact]
    public async Task UnaVentaNuevaConOtroNumeroSigueRechazadaPorLimite()
    {
        var e = await PrepararAsync(nameof(UnaVentaNuevaConOtroNumeroSigueRechazadaPorLimite), 700m);
        using var _cajero = e.Cajero;

        var primera = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 1, limiteNoValidado: false));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var otra = await e.Cajero.PostAsJsonAsync("/api/ventas", VentaEnCuentaCorriente(e, 2, limiteNoValidado: false));

        await AfirmarErrorAsync(otra, "limite_credito_excedido");
        Assert.Equal(1000m, await LeerSaldoAsync(e));
    }
}
