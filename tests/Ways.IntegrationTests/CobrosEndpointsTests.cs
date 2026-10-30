using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// E7-S3: el HTTP de los cobros aplicados a ventas. <c>POST …/cuenta-corriente/pagos</c> con y sin <c>aplicaciones</c>
/// (el cuerpo viejo sigue andando; con aplicaciones, el recibo y ellas se escriben juntos o no se escribe nada),
/// <c>POST/DELETE /api/cobros/{id}/aplicaciones</c> (Supervisor y Admin) y los dos listados del cliente. Las ventas en
/// cuenta corriente se siembran directo con EF: lo que importa acá es su tipo, cliente y cobros; el recibo se registra
/// siempre por la ruta real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CobrosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordDeRol = "una-contraseña-larga";

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdAdmin, int IdListaPrecio, int IdMedioEfectivo, int IdMedioCuentaCorriente,
        string Nombre, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre, bool abrirTurno = true)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = "cobhttp-" + Guid.NewGuid().ToString("N")[..12];
        var mailAdmin = $"{unico}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = ContextoDelTenant(resultado.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        var ctx = new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idListaPrecio, idMedioEfectivo,
            medioCc.Id, nombre, admin);

        if (abrirTurno)
        {
            var apertura = await admin.PostAsJsonAsync(
                "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de prueba"));
            Assert.Equal(HttpStatusCode.Created, apertura.StatusCode);
        }

        return ctx;
    }

    private Ways.Infrastructure.Persistencia.WaysDbContext ContextoDelTenant(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = ContextoDelTenant(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = 0m, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    /// <summary>Una venta TX con todo su importe en cuenta corriente.</summary>
    private async Task<int> SembrarVentaEnCuentaCorrienteAsync(
        Contexto ctx, int idCliente, decimal importe, int minutosAtras = 0)
    {
        await using var db = ContextoDelTenant(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        var venta = new ComprobanteVenta
        {
            IdTipoComprobante = idTipo, Numero = Random.Shared.Next(1, int.MaxValue), Fecha = ahora.AddMinutes(-minutosAtras),
            IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleado = ctx.IdAdmin, IdCliente = idCliente, Subtotal = importe,
            DescuentoTotal = 0m, Total = importe, Estado = EstadoComprobante.Emitido, FacturacionDecididaEn = ahora,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(venta);
        await db.SaveChangesAsync();

        db.Cobros.Add(new Cobro
        {
            IdComprobanteVenta = venta.Id, IdMedioPago = ctx.IdMedioCuentaCorriente, Importe = importe, Vuelto = 0m,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
        return venta.Id;
    }

    private async Task<HttpClient> CrearClienteConRolAsync(Contexto ctx, RolConocido rol, string alias)
    {
        var mail = $"{alias}-{Guid.NewGuid().ToString("N")[..8]}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario(alias, mail, (int)rol, PasswordDeRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordDeRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static SolicitudDePagoACuenta Pago(Contexto ctx, decimal importe, params (int IdVenta, decimal Importe)[] aplicaciones) =>
        new(ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioEfectivo, importe, null, 0m)], null,
            aplicaciones.Length == 0
                ? null
                : aplicaciones.Select(a => (Ways.Domain.Ventas.AplicacionDeCobroPedida?)new AplicacionDeCobroPedida(a.IdVenta, a.Importe)).ToList());

    private static Task<HttpResponseMessage> RegistrarPagoAsync(
        HttpClient cliente, int idCliente, SolicitudDePagoACuenta solicitud) =>
        cliente.PostAsJsonAsync($"/api/clientes/{idCliente}/cuenta-corriente/pagos", solicitud);

    private static async Task<JsonElement> LeerAsync(HttpResponseMessage respuesta) =>
        JsonSerializer.Deserialize<JsonElement>(await respuesta.Content.ReadAsStringAsync());

    private static async Task<string?> CodigoDeAsync(HttpResponseMessage respuesta) =>
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString();

    private static async Task<int> RegistrarReciboAsync(Contexto ctx, int idCliente, decimal importe)
    {
        var respuesta = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, importe));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await LeerAsync(respuesta)).GetProperty("id").GetInt32();
    }

    private static object Aplicaciones(params (int IdVenta, decimal Importe)[] aplicaciones) =>
        new { aplicaciones = aplicaciones.Select(a => new { idComprobanteVenta = a.IdVenta, importe = a.Importe }) };

    // ---- POST …/cuenta-corriente/pagos ---------------------------------------------------------

    [Fact]
    public async Task ElCuerpoViejoSinAplicacionesSigueAndandoYResponde201ConAplicacionesVacias()
    {
        var ctx = await PrepararAsync(nameof(ElCuerpoViejoSinAplicacionesSigueAndandoYResponde201ConAplicacionesVacias));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente cuerpo viejo");

        // Cuerpo crudo de un registro instalado: sin el campo `aplicaciones`.
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/pagos",
            new { idPuntoVenta = ctx.IdPuntoVenta, pagos = new[] { new { idMedioPago = ctx.IdMedioEfectivo, importe = 80m, referencia = (string?)null, vuelto = 0m } }, observaciones = (string?)null });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var cuerpo = await LeerAsync(respuesta);
        Assert.Equal(0, cuerpo.GetProperty("aplicaciones").GetArrayLength());
        Assert.Equal(80m, cuerpo.GetProperty("total").GetDecimal());
        Assert.Equal(0, cuerpo.GetProperty("items").GetArrayLength());
        Assert.Equal(1, cuerpo.GetProperty("pagos").GetArrayLength());
    }

    [Fact]
    public async Task UnaListaVaciaDeAplicacionesEsComoNoMandarla()
    {
        var ctx = await PrepararAsync(nameof(UnaListaVaciaDeAplicacionesEsComoNoMandarla));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente lista vacía");

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/pagos",
            new { idPuntoVenta = ctx.IdPuntoVenta, pagos = new[] { new { idMedioPago = ctx.IdMedioEfectivo, importe = 30m, referencia = (string?)null, vuelto = 0m } }, aplicaciones = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(0, (await LeerAsync(respuesta)).GetProperty("aplicaciones").GetArrayLength());
    }

    [Fact]
    public async Task RegistrarUnReciboConAplicacionesLasEscribeJuntoConElRecibo()
    {
        var ctx = await PrepararAsync(nameof(RegistrarUnReciboConAplicacionesLasEscribeJuntoConElRecibo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente con aplicaciones");
        var venta1 = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var venta2 = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 50m);

        var respuesta = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 120m, (venta1, 70m), (venta2, 50m)));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var cuerpo = await LeerAsync(respuesta);
        var aplicaciones = cuerpo.GetProperty("aplicaciones");
        Assert.Equal(2, aplicaciones.GetArrayLength());
        Assert.Equal(new[] { venta1, venta2 }, aplicaciones.EnumerateArray().Select(a => a.GetProperty("idComprobanteVenta").GetInt32()));
        Assert.Equal(120m, aplicaciones.EnumerateArray().Sum(a => a.GetProperty("importe").GetDecimal()));

        var idRecibo = cuerpo.GetProperty("id").GetInt32();
        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(2, await db.AplicacionesCobro.CountAsync(a => a.IdComprobanteCobro == idRecibo));
        Assert.Equal(1, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdComprobanteVenta == idRecibo));
        Assert.Equal(-120m, await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync());
    }

    [Theory]
    [InlineData("excede_recibo", "cobro_excede_disponible")]
    [InlineData("excede_venta", "aplicacion_excede_saldo_de_venta")]
    [InlineData("otro_cliente", "aplicacion_cobro_otro_cliente")]
    public async Task UnaAplicacionRechazadaDeshaceElReciboEntero(string caso, string codigoEsperado)
    {
        var ctx = await PrepararAsync(nameof(UnaAplicacionRechazadaDeshaceElReciboEntero) + caso.Replace("_", string.Empty));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente atómico");
        var idOtro = await SembrarClienteAsync(ctx, "Otro cliente atómico");
        var ventaPropia = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ctx, idOtro, 100m);

        var solicitud = caso switch
        {
            "excede_recibo" => Pago(ctx, 40m, (ventaPropia, 60m)),
            "excede_venta" => Pago(ctx, 500m, (ventaPropia, 101m)),
            _ => Pago(ctx, 40m, (ventaAjena, 10m))
        };

        var respuesta = await RegistrarPagoAsync(ctx.Admin, idCliente, solicitud);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(codigoEsperado, await CodigoDeAsync(respuesta));

        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync(c => c.IdCliente == idCliente && c.Id != ventaPropia));
        Assert.Equal(0, await db.Cobros.CountAsync(c => c.IdMedioPago == ctx.IdMedioEfectivo));
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync());
        Assert.Equal(0, await db.AplicacionesCobro.CountAsync());
        Assert.Equal(0m, await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync());
    }

    /// <summary>Cláusula que prueba: la medición temprana de <c>ServicioDeCobros.ExigirAplicacionesViablesAsync</c>, que
    /// corre antes de reservar el número. Un pedido rechazado por esa medición no avanza <c>numeraciones_comprobante</c> del
    /// tipo RC; el recibo siguiente recibe el número que habría recibido el rechazado.</summary>
    [Theory]
    [InlineData("excede_recibo", "cobro_excede_disponible")]
    [InlineData("excede_venta", "aplicacion_excede_saldo_de_venta")]
    [InlineData("otro_cliente", "aplicacion_cobro_otro_cliente")]
    public async Task UnPedidoRechazadoPorLaMedicionTempranaNoAvanzaLaNumeracionDelRecibo(string caso, string codigoEsperado)
    {
        var ctx = await PrepararAsync(nameof(UnPedidoRechazadoPorLaMedicionTempranaNoAvanzaLaNumeracionDelRecibo) + caso.Replace("_", string.Empty));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente numeración");
        var idOtro = await SembrarClienteAsync(ctx, "Otro cliente numeración");
        var ventaPropia = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ctx, idOtro, 100m);

        // Un primer recibo crea la fila de numeración de RC; desde ahí se mide.
        await RegistrarReciboAsync(ctx, idCliente, 10m);
        var antes = await ProximoNumeroDeReciboAsync(ctx);

        var solicitud = caso switch
        {
            "excede_recibo" => Pago(ctx, 40m, (ventaPropia, 60m)),
            "excede_venta" => Pago(ctx, 500m, (ventaPropia, 101m)),
            _ => Pago(ctx, 40m, (ventaAjena, 10m))
        };
        var respuesta = await RegistrarPagoAsync(ctx.Admin, idCliente, solicitud);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(codigoEsperado, await CodigoDeAsync(respuesta));
        Assert.Equal(antes, await ProximoNumeroDeReciboAsync(ctx));

        var siguiente = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 20m));
        Assert.Equal(HttpStatusCode.Created, siguiente.StatusCode);
        Assert.Equal(antes, (await LeerAsync(siguiente)).GetProperty("numero").GetInt64());
    }

    private async Task<long> ProximoNumeroDeReciboAsync(Contexto ctx)
    {
        await using var db = ContextoDelTenant(ctx.IdTenant);
        return await db.NumeracionesComprobante
            .Where(n => n.IdPuntoVenta == ctx.IdPuntoVenta && n.TipoComprobante == "RC")
            .Select(n => n.ProximoNumero)
            .SingleAsync();
    }

    /// <summary>Un cajero (Vendedor: <c>OperacionDePos</c> sin <c>SupervisionDeCuentaCorriente</c>) registra un pago a cuenta
    /// con aplicaciones, pero no aplica ni libera después sobre <c>/api/cobros</c>.</summary>
    [Fact]
    public async Task UnVendedorRegistraElPagoConAplicacionesPeroNoAplicaNiLiberaDespues()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorRegistraElPagoConAplicacionesPeroNoAplicaNiLiberaDespues));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente cajero");
        var ventaA = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var ventaB = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        using var vendedor = await CrearClienteConRolAsync(ctx, RolConocido.Vendedor, "vendedor-caja");

        var registro = await RegistrarPagoAsync(vendedor, idCliente, Pago(ctx, 50m, (ventaA, 30m)));

        Assert.Equal(HttpStatusCode.Created, registro.StatusCode);
        var cuerpo = await LeerAsync(registro);
        var idRecibo = cuerpo.GetProperty("id").GetInt32();
        var idAplicacion = cuerpo.GetProperty("aplicaciones")[0].GetProperty("id").GetInt32();

        var aplicar = await vendedor.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((ventaB, 10m)));
        Assert.Equal(HttpStatusCode.Forbidden, aplicar.StatusCode);
        var liberar = await vendedor.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/{idAplicacion}");
        Assert.Equal(HttpStatusCode.Forbidden, liberar.StatusCode);

        await using var db = ContextoDelTenant(ctx.IdTenant);
        var vivas = await db.AplicacionesCobro.Where(a => a.IdComprobanteCobro == idRecibo).ToListAsync();
        Assert.Equal(ventaA, Assert.Single(vivas).IdComprobanteVenta);
        Assert.Null(vivas[0].DeletedAt);
    }

    [Fact]
    public async Task UnaVentaInexistenteODeOtroTenantEs404YNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaInexistenteODeOtroTenantEs404YNoEscribeNada));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente 404");
        var ctxAjeno = await PrepararAsync(nameof(UnaVentaInexistenteODeOtroTenantEs404YNoEscribeNada) + "x", abrirTurno: false);
        var clienteAjeno = await SembrarClienteAsync(ctxAjeno, "Cliente ajeno");
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ctxAjeno, clienteAjeno, 100m);

        foreach (var idVenta in new[] { 987_654_321, ventaAjena })
        {
            var respuesta = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 50m, (idVenta, 10m)));
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
    }

    [Fact]
    public async Task LasAplicacionesMalFormadasSeRechazanSinEscribirElRecibo()
    {
        var ctx = await PrepararAsync(nameof(LasAplicacionesMalFormadasSeRechazanSinEscribirElRecibo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente forma");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);

        var repetida = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 50m, (venta, 10m), (venta, 10m)));
        Assert.Equal(HttpStatusCode.BadRequest, repetida.StatusCode);
        Assert.Equal("aplicacion_venta_repetida", await CodigoDeAsync(repetida));

        var demasiadas = await RegistrarPagoAsync(
            ctx.Admin, idCliente, Pago(ctx, 50m, Enumerable.Range(1, 21).Select(i => (venta + i, 1m)).ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, demasiadas.StatusCode);
        Assert.Equal("aplicacion_demasiadas_ventas", await CodigoDeAsync(demasiadas));

        var importe = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 50m, (venta, 0m)));
        Assert.Equal(HttpStatusCode.BadRequest, importe.StatusCode);
        Assert.Equal("aplicacion_importe_invalido", await CodigoDeAsync(importe));

        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync(c => c.Id != venta));
    }

    // ---- POST /api/cobros/{idRecibo}/aplicaciones ----------------------------------------------

    [Fact]
    public async Task UnSupervisorAplicaYElReciboResponde200ConSuDisponibleYSusAplicaciones()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorAplicaYElReciboResponde200ConSuDisponibleYSusAplicaciones));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente aplicar");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var idRecibo = await RegistrarReciboAsync(ctx, idCliente, 80m);
        using var supervisor = await CrearClienteConRolAsync(ctx, RolConocido.Supervisor, "supervisor-cob");

        var respuesta = await supervisor.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((venta, 30m)));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = await LeerAsync(respuesta);
        Assert.Equal(idRecibo, cuerpo.GetProperty("idRecibo").GetInt32());
        Assert.Equal(80m, cuerpo.GetProperty("netoCobrado").GetDecimal());
        Assert.Equal(30m, cuerpo.GetProperty("aplicado").GetDecimal());
        Assert.Equal(50m, cuerpo.GetProperty("disponible").GetDecimal());
        Assert.Equal(1, cuerpo.GetProperty("aplicaciones").GetArrayLength());
    }

    [Fact]
    public async Task AplicarRechazaConLosCodigosDeLaReglaYConElRolSinPolitica()
    {
        var ctx = await PrepararAsync(nameof(AplicarRechazaConLosCodigosDeLaReglaYConElRolSinPolitica));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente rechazos");
        var idOtro = await SembrarClienteAsync(ctx, "Otro cliente rechazos");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ctx, idOtro, 100m);
        var idRecibo = await RegistrarReciboAsync(ctx, idCliente, 40m);

        var excede = await ctx.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((venta, 41m)));
        Assert.Equal(HttpStatusCode.Conflict, excede.StatusCode);
        Assert.Equal("cobro_excede_disponible", await CodigoDeAsync(excede));

        var otroCliente = await ctx.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((ventaAjena, 10m)));
        Assert.Equal(HttpStatusCode.Conflict, otroCliente.StatusCode);
        Assert.Equal("aplicacion_cobro_otro_cliente", await CodigoDeAsync(otroCliente));

        var recibo404 = await ctx.Admin.PostAsJsonAsync("/api/cobros/987654321/aplicaciones", Aplicaciones((venta, 10m)));
        Assert.Equal(HttpStatusCode.NotFound, recibo404.StatusCode);

        // update-404-first: una venta inexistente es 404 aunque el importe también sea inválido.
        var venta404 = await ctx.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((987_654_321, 0m)));
        Assert.Equal(HttpStatusCode.NotFound, venta404.StatusCode);

        var sinVentas = await ctx.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", new { aplicaciones = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, sinVentas.StatusCode);
        Assert.Equal("aplicacion_sin_ventas", await CodigoDeAsync(sinVentas));

        using var vendedor = await CrearClienteConRolAsync(ctx, RolConocido.Vendedor, "vendedor-cob");
        var prohibido = await vendedor.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((venta, 10m)));
        Assert.Equal(HttpStatusCode.Forbidden, prohibido.StatusCode);

        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(0, await db.AplicacionesCobro.CountAsync());
    }

    [Fact]
    public async Task AplicarUnReciboOUnaVentaDeOtroTenantEs404()
    {
        var ctx = await PrepararAsync(nameof(AplicarUnReciboOUnaVentaDeOtroTenantEs404));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente tenant");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var idRecibo = await RegistrarReciboAsync(ctx, idCliente, 40m);

        var ctxAjeno = await PrepararAsync(nameof(AplicarUnReciboOUnaVentaDeOtroTenantEs404) + "x", abrirTurno: false);

        var reciboAjeno = await ctxAjeno.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((venta, 10m)));
        Assert.Equal(HttpStatusCode.NotFound, reciboAjeno.StatusCode);
    }

    // ---- DELETE /api/cobros/{idRecibo}/aplicaciones/{idAplicacion} -----------------------------

    [Fact]
    public async Task DesaplicarLiberaLoAplicadoYResponde200ConElEstadoDelRecibo()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarLiberaLoAplicadoYResponde200ConElEstadoDelRecibo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente desaplicar");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var registro = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 60m, (venta, 60m)));
        var cuerpoRegistro = await LeerAsync(registro);
        var idRecibo = cuerpoRegistro.GetProperty("id").GetInt32();
        var idAplicacion = cuerpoRegistro.GetProperty("aplicaciones")[0].GetProperty("id").GetInt32();
        using var supervisor = await CrearClienteConRolAsync(ctx, RolConocido.Supervisor, "supervisor-des");

        var respuesta = await supervisor.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/{idAplicacion}");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var cuerpo = await LeerAsync(respuesta);
        Assert.Equal(0m, cuerpo.GetProperty("aplicado").GetDecimal());
        Assert.Equal(60m, cuerpo.GetProperty("disponible").GetDecimal());
        Assert.Equal(0, cuerpo.GetProperty("aplicaciones").GetArrayLength());
    }

    [Fact]
    public async Task DesaplicarRespondeLos404ElRolSinPoliticaYNoDejaDesaplicarDosVeces()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarRespondeLos404ElRolSinPoliticaYNoDejaDesaplicarDosVeces));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente 404 desaplicar");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var registro = await RegistrarPagoAsync(ctx.Admin, idCliente, Pago(ctx, 60m, (venta, 60m)));
        var cuerpoRegistro = await LeerAsync(registro);
        var idRecibo = cuerpoRegistro.GetProperty("id").GetInt32();
        var idAplicacion = cuerpoRegistro.GetProperty("aplicaciones")[0].GetProperty("id").GetInt32();
        var otroRecibo = await RegistrarReciboAsync(ctx, idCliente, 10m);

        using var vendedor = await CrearClienteConRolAsync(ctx, RolConocido.Vendedor, "vendedor-des");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await vendedor.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/{idAplicacion}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/cobros/987654321/aplicaciones/{idAplicacion}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/987654321")).StatusCode);
        // La aplicación existe pero es de otro recibo.
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/cobros/{otroRecibo}/aplicaciones/{idAplicacion}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/{idAplicacion}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/cobros/{idRecibo}/aplicaciones/{idAplicacion}")).StatusCode);
    }

    // ---- GET …/ventas-pendientes y …/recibos ----------------------------------------------------

    [Fact]
    public async Task VentasPendientesListaSoloLasQueTienenSaldoLaMasNuevaPrimeroYLeeUnVendedor()
    {
        var ctx = await PrepararAsync(nameof(VentasPendientesListaSoloLasQueTienenSaldoLaMasNuevaPrimeroYLeeUnVendedor));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente pendientes");
        var vieja = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m, minutosAtras: 30);
        var saldada = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 40m, minutosAtras: 20);
        var nueva = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 70m, minutosAtras: 10);
        var idRecibo = await RegistrarReciboAsync(ctx, idCliente, 90m);
        var aplicar = await ctx.Admin.PostAsJsonAsync($"/api/cobros/{idRecibo}/aplicaciones", Aplicaciones((saldada, 40m), (vieja, 50m)));
        Assert.Equal(HttpStatusCode.OK, aplicar.StatusCode);
        using var vendedor = await CrearClienteConRolAsync(ctx, RolConocido.Vendedor, "vendedor-pend");

        var respuesta = await vendedor.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente/ventas-pendientes");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var filas = (await LeerAsync(respuesta)).EnumerateArray().ToList();
        Assert.Equal(new[] { nueva, vieja }, filas.Select(f => f.GetProperty("id").GetInt32()));
        Assert.Equal(70m, filas[0].GetProperty("saldoPendiente").GetDecimal());
        Assert.Equal(0m, filas[0].GetProperty("cobrado").GetDecimal());
        Assert.Equal(50m, filas[1].GetProperty("saldoPendiente").GetDecimal());
        Assert.Equal(50m, filas[1].GetProperty("cobrado").GetDecimal());
        Assert.Equal(100m, filas[1].GetProperty("parteEnCuentaCorriente").GetDecimal());
        Assert.Equal("TX", filas[0].GetProperty("codigoTipo").GetString());
        Assert.False(string.IsNullOrEmpty(filas[0].GetProperty("numeroVisible").GetString()));
    }

    [Fact]
    public async Task LosListadosDeUnClienteInexistenteODeOtroTenantSon404()
    {
        var ctx = await PrepararAsync(nameof(LosListadosDeUnClienteInexistenteODeOtroTenantSon404), abrirTurno: false);
        var ctxAjeno = await PrepararAsync(nameof(LosListadosDeUnClienteInexistenteODeOtroTenantSon404) + "x", abrirTurno: false);
        var clienteAjeno = await SembrarClienteAsync(ctxAjeno, "Cliente ajeno listados");

        foreach (var id in new[] { 987_654_321, clienteAjeno })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/clientes/{id}/cuenta-corriente/ventas-pendientes")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/clientes/{id}/cuenta-corriente/recibos")).StatusCode);
        }
    }

    [Fact]
    public async Task RecibosMuestraCobradoAplicadoDisponibleYAplicacionesYFiltraPorDisponible()
    {
        var ctx = await PrepararAsync(nameof(RecibosMuestraCobradoAplicadoDisponibleYAplicacionesYFiltraPorDisponible));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente recibos");
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, idCliente, 100m);
        var parcial = await RegistrarReciboAsync(ctx, idCliente, 60m);
        var agotado = await RegistrarReciboAsync(ctx, idCliente, 30m);
        var anulado = await RegistrarReciboAsync(ctx, idCliente, 20m);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsJsonAsync($"/api/cobros/{parcial}/aplicaciones", Aplicaciones((venta, 10m)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsJsonAsync($"/api/cobros/{agotado}/aplicaciones", Aplicaciones((venta, 30m)))).StatusCode);

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            await db.ComprobantesVenta.Where(c => c.Id == anulado)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Estado, EstadoComprobante.Anulado));
            // La del recibo agotado pasa a histórica: el listado tiene que leer el origen de cada fila.
            await db.AplicacionesCobro.Where(a => a.IdComprobanteCobro == agotado)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Origen, OrigenAplicacionCobro.Historial));
        }

        var todos = (await LeerAsync(await ctx.Admin.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente/recibos"))).EnumerateArray().ToList();
        Assert.Equal(new[] { agotado, parcial }, todos.Select(r => r.GetProperty("id").GetInt32()));
        Assert.Equal("Historial", Assert.Single(todos[0].GetProperty("aplicaciones").EnumerateArray()).GetProperty("origen").GetString());
        var filaParcial = todos[1];
        Assert.Equal(60m, filaParcial.GetProperty("netoCobrado").GetDecimal());
        Assert.Equal(10m, filaParcial.GetProperty("aplicado").GetDecimal());
        Assert.Equal(50m, filaParcial.GetProperty("disponible").GetDecimal());
        var aplicacion = Assert.Single(filaParcial.GetProperty("aplicaciones").EnumerateArray());
        Assert.Equal(venta, aplicacion.GetProperty("idComprobanteVenta").GetInt32());
        Assert.False(string.IsNullOrEmpty(aplicacion.GetProperty("numeroVisibleDeVenta").GetString()));
        Assert.Equal("Manual", aplicacion.GetProperty("origen").GetString());

        var conDisponible = (await LeerAsync(await ctx.Admin.GetAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/recibos?conDisponible=true"))).EnumerateArray().ToList();
        Assert.Equal(new[] { parcial }, conDisponible.Select(r => r.GetProperty("id").GetInt32()));

        using var vendedor = await CrearClienteConRolAsync(ctx, RolConocido.Vendedor, "vendedor-rec");
        Assert.Equal(HttpStatusCode.OK, (await vendedor.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente/recibos")).StatusCode);
    }

    [Fact]
    public async Task LosListadosSinSesionDevuelven401()
    {
        using var anonimo = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/clientes/1/cuenta-corriente/ventas-pendientes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/clientes/1/cuenta-corriente/recibos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/cobros/1/aplicaciones", Aplicaciones((1, 1m)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.DeleteAsync("/api/cobros/1/aplicaciones/1")).StatusCode);
    }
}
