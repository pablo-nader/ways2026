using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/personal/{id}/pagos</c> punta a punta contra Postgres real: cada combinación de líneas y de
/// origen de fondos, el desglose en el estado de cuenta, los rechazos sin escrituras parciales, la atomicidad
/// entre los cuatro libros (personal, cuenta corriente del cliente, pagos y tesorería), la concurrencia sobre
/// los saldos y la inmutabilidad de los pagos que nacen de un pago.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagoDePersonalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdMedioEfectivo, int IdMedioTarjeta, int IdMedioCuentaCorriente,
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();

        // La plantilla de aprovisionamiento no trae ningún medio de cuenta corriente.
        var ahora = DateTimeOffset.UtcNow;
        var medioCuentaCorriente = new MedioPago
        {
            Nombre = "Cuenta corriente", Orden = 99, Comportamiento = ComportamientoMedioPago.CuentaCorriente,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCuentaCorriente);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idMedioEfectivo, idMedioTarjeta,
            medioCuentaCorriente.Id, admin, supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Cliente vivo con la deuda indicada, cargada por el mismo ajuste manual que usa el back office
    /// para que el libro y el saldo coincidan.</summary>
    private async Task<int> CrearClienteConDeudaAsync(Contexto ctx, decimal deuda, string nombre = "Cliente empleado")
    {
        int idCliente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var cliente = new Cliente
            {
                IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync(),
                LimiteCredito = 0m, CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            idCliente = cliente.Id;
        }

        if (deuda > 0m)
        {
            var ajuste = await ctx.Admin.PostAsJsonAsync(
                $"/api/clientes/{idCliente}/cuenta-corriente/ajustes",
                new SolicitudDeAjuste(ctx.IdPuntoVenta, deuda, "Deuda inicial de la prueba"));
            Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        }

        return idCliente;
    }

    private static async Task<int> CrearPersonalAsync(
        HttpClient admin, int? idCliente = null, decimal saldoInicial = 0m, string nombre = "Ana")
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, null, null, null, 1000m, null, null, idCliente));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var id = (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        if (saldoInicial != 0m)
        {
            var ajuste = await admin.PostAsJsonAsync(
                $"/api/personal/{id}/ajustes", new { importe = saldoInicial, detalle = "Saldo inicial de la prueba", fecha = "2026-10-01" });
            Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        }

        return id;
    }

    private static string Dia(int diasAtras = 0) =>
        FechaDelNegocio.Hoy().AddDays(-diasAtras).ToString("yyyy-MM-dd");

    private static object Linea(int idMedioPago, decimal importe, string? referencia = null) =>
        new { idMedioPago, importe, referencia };

    private static object Cuerpo(
        string? fecha, int? idEmpresa = null, int? idPuntoVenta = null, string? origen = null,
        decimal? descuento = null, object[]? lineas = null, string? detalle = null) =>
        new
        {
            fecha, idEmpresa, idPuntoVenta, origenFondos = origen, descuentoDeCuentaCorriente = descuento, lineas, detalle
        };

    private static Task<HttpResponseMessage> PagarAsync(HttpClient cliente, int idPersonal, object cuerpo) =>
        cliente.PostAsJsonAsync($"/api/personal/{idPersonal}/pagos", cuerpo);

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString();

    private static async Task<JsonElement> CuentaAsync(HttpClient cliente, int id) =>
        await CuerpoAsync(await cliente.GetAsync($"/api/personal/{id}/cuenta?historico=true"), HttpStatusCode.OK);

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private Infrastructure.Persistencia.WaysDbContext Abrir(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    /// <summary>Saldos y cantidad de filas de los cuatro libros del tenant (todas las filas del tenant: el
    /// ajuste que siembra la deuda es anterior a la foto).</summary>
    private sealed record Foto(
        decimal SaldoDelPersonal, decimal SaldoDelCliente, int AsientosDelPersonal, int AsientosDeCuentaCorriente,
        int Pagos, int AsientosDeTesoreria);

    private async Task<Foto> FotoAsync(Contexto ctx, int idPersonal, int? idCliente = null)
    {
        await using var abierto = Abrir(ctx);
        var db = abierto;
        return new Foto(
            await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync(),
            idCliente is { } c ? await db.Clientes.Where(x => x.Id == c).Select(x => x.Saldo).SingleAsync() : 0m,
            await db.MovimientosPersonal.CountAsync(),
            await db.MovimientosCuentaCorriente.CountAsync(),
            await db.Pagos.CountAsync(),
            await db.MovimientosTesoreria.CountAsync());
    }

    // ---- combinaciones de líneas y origen ------------------------------------------------------

    [Fact]
    public async Task UnPagoDeTesoreriaConDescuentoYDosLineasEscribeLosCuatroLibrosYQuedaAtadoAlAsientoDelPersonal()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeTesoreriaConDescuentoYDosLineasEscribeLosCuatroLibrosYQuedaAtadoAlAsientoDelPersonal));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 4000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 10000m);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(10), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 1500m,
            lineas: [Linea(ctx.IdMedioEfectivo, 2000m), Linea(ctx.IdMedioTarjeta, 500m, "  cup-77 ")],
            detalle: "  adelanto de octubre "));
        var pago = await CuerpoAsync(respuesta, HttpStatusCode.Created);

        Assert.Equal($"/api/personal/{idPersonal}/cuenta", respuesta.Headers.Location!.OriginalString);
        Assert.Equal("pago", pago.GetProperty("tipo").GetString(), ignoreCase: true);
        Assert.Equal(-4000m, pago.GetProperty("importe").GetDecimal());
        Assert.Equal(6000m, pago.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(Dia(10), pago.GetProperty("fecha").GetString());
        Assert.Equal("adelanto de octubre", pago.GetProperty("detalle").GetString());
        var idMovimiento = pago.GetProperty("idMovimiento").GetInt32();

        await using var abierto = Abrir(ctx);
        var db = abierto;

        var asiento = await db.MovimientosPersonal.SingleAsync(m => m.Id == idMovimiento);
        Assert.Equal(TipoMovimientoPersonal.Pago, asiento.Tipo);
        Assert.Equal(-4000m, asiento.Importe);
        Assert.Equal(6000m, asiento.SaldoResultante);
        Assert.Equal("adelanto de octubre", asiento.Detalle);
        Assert.Null(asiento.Horas);
        Assert.Equal(6000m, await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync());

        var descuento = await db.MovimientosCuentaCorriente.SingleAsync(m => m.IdMovimientoPersonal == idMovimiento);
        Assert.Equal(TipoMovimientoCc.DescuentoPersonal, descuento.Tipo);
        Assert.Equal(idCliente, descuento.IdCliente);
        Assert.Equal(-1500m, descuento.Importe);
        Assert.Equal(2500m, descuento.SaldoResultante);
        Assert.Equal(ctx.IdPuntoVenta, descuento.IdPuntoVenta);
        Assert.Null(descuento.IdComprobanteVenta);
        Assert.Null(descuento.IdCobro);
        Assert.Equal("Descuento por pago de personal: Ana", descuento.Detalle);
        Assert.Equal(2500m, await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync());

        // Las dos líneas de dinero son UN pago con dos ítems: la cabecera suma el total y no lleva detalle.
        var unico = await db.Pagos.SingleAsync(g => g.IdMovimientoPersonal == idMovimiento);
        Assert.Equal(CategoriaPago.Sueldos, unico.Categoria);
        Assert.Equal(OrigenFondosPago.Tesoreria, unico.OrigenFondos);
        Assert.Null(unico.IdTurnoCaja);
        Assert.Equal(ctx.IdEmpresa, unico.IdEmpresa);
        Assert.Equal(ctx.IdPuntoVenta, unico.IdPuntoVenta);
        Assert.Equal("Pago de personal: Ana", unico.Concepto);
        // La fecha de negocio retroactiva elegida es la que persiste en el pago.
        Assert.Equal(FechaDelNegocio.Hoy().AddDays(-10), DateOnly.FromDateTime(unico.Fecha.UtcDateTime));
        Assert.Equal(2500m, unico.Importe);
        Assert.Null(unico.Detalle);

        // La referencia de cada línea vive solo en su ítem.
        var itemsDeSueldo = await db.ItemsPago.Where(i => i.IdPago == unico.Id).OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(
            [(ctx.IdMedioEfectivo, 2000m, (string?)null), (ctx.IdMedioTarjeta, 500m, "cup-77")],
            itemsDeSueldo.Select(i => (i.IdMedioPago, i.Importe, i.Referencia)));

        // Un único egreso de tesorería por el total del pago.
        var egreso = await db.MovimientosTesoreria.SingleAsync(m => m.IdEmpresa == ctx.IdEmpresa);
        Assert.Equal(unico.Id, egreso.IdPago);
        Assert.Equal(0m, egreso.Inicio);
        Assert.Equal(-2500m, egreso.Final);
        Assert.Equal(2500m, egreso.Egreso);
        Assert.Equal(TipoMovimientoTesoreria.Pago, egreso.Tipo);
        Assert.Equal(ctx.IdPuntoVenta, egreso.IdPuntoVenta);
        Assert.Null(egreso.IdTurnoCaja);

        // El estado de cuenta explica el pago: el descuento y cada línea con su medio, importe y origen.
        var cuenta = await CuentaAsync(ctx.Admin, idPersonal);
        var item = cuenta.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("idMovimiento").GetInt32() == idMovimiento);
        var desglose = item.GetProperty("desglose");
        Assert.Equal(1500m, desglose.GetProperty("descuentoDeCuentaCorriente").GetDecimal());
        var lineas = desglose.GetProperty("lineas").EnumerateArray().ToList();
        Assert.Equal(2, lineas.Count);
        Assert.All(lineas, l => Assert.Equal(unico.Id, l.GetProperty("idGasto").GetInt32()));
        Assert.Equal(itemsDeSueldo.Select(i => i.Id), lineas.Select(l => l.GetProperty("idItem").GetInt32()));
        Assert.Equal([ctx.IdMedioEfectivo, ctx.IdMedioTarjeta], lineas.Select(l => l.GetProperty("idMedioPago").GetInt32()));
        Assert.Equal([2000m, 500m], lineas.Select(l => l.GetProperty("importe").GetDecimal()));
        Assert.All(lineas, l => Assert.Equal("tesoreria", l.GetProperty("origenFondos").GetString(), ignoreCase: true));
        Assert.All(lineas, l => Assert.False(string.IsNullOrEmpty(l.GetProperty("nombreMedioPago").GetString())));
        Assert.Equal(JsonValueKind.Null, lineas[0].GetProperty("referencia").ValueKind);
        Assert.Equal("cup-77", lineas[1].GetProperty("referencia").GetString());

        var desgloseDeLaRespuesta = pago.GetProperty("desglose");
        Assert.Equal(1500m, desgloseDeLaRespuesta.GetProperty("descuentoDeCuentaCorriente").GetDecimal());
        Assert.Equal(2, desgloseDeLaRespuesta.GetProperty("lineas").GetArrayLength());
        Assert.Equal(
            itemsDeSueldo.Select(i => i.Id),
            desgloseDeLaRespuesta.GetProperty("lineas").EnumerateArray().Select(l => l.GetProperty("idItem").GetInt32()));
    }

    /// <summary>Un pago de personal repartido en dos ítems se lista en el desglose como dos líneas, una por ítem, con
    /// el mismo idGasto.</summary>
    [Fact]
    public async Task UnPagoPartidoEnDosItemsSeListaEnElDesglosePorItemConElMismoIdGasto()
    {
        var ctx = await PrepararAsync(nameof(UnPagoPartidoEnDosItemsSeListaEnElDesglosePorItemConElMismoIdGasto));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var pago = await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 100m)])),
            HttpStatusCode.Created);
        var idMovimiento = pago.GetProperty("idMovimiento").GetInt32();

        int idPago;
        await using (var db = Abrir(ctx))
        {
            var cabecera = await db.Pagos.SingleAsync(g => g.IdMovimientoPersonal == idMovimiento);
            idPago = cabecera.Id;
            var primero = await db.ItemsPago.SingleAsync(i => i.IdPago == idPago);
            primero.Importe = 60m;
            var ahora = DateTimeOffset.UtcNow;
            db.ItemsPago.Add(new ItemPago
            {
                IdTenant = ctx.IdTenant, IdPago = idPago, IdMedioPago = ctx.IdMedioTarjeta, Importe = 40m,
                CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        List<int> idsDeItems;
        await using (var db = Abrir(ctx))
        {
            idsDeItems = await db.ItemsPago.Where(i => i.IdPago == idPago).OrderBy(i => i.Id).Select(i => i.Id).ToListAsync();
        }

        var cuenta = await CuentaAsync(ctx.Admin, idPersonal);
        var item = cuenta.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("idMovimiento").GetInt32() == idMovimiento);
        var lineas = item.GetProperty("desglose").GetProperty("lineas").EnumerateArray().ToList();

        Assert.Equal(2, lineas.Count);
        Assert.Equal(idsDeItems, lineas.Select(l => l.GetProperty("idItem").GetInt32()));
        Assert.Equal(2, lineas.Select(l => l.GetProperty("idItem").GetInt32()).Distinct().Count());
        Assert.Equal([ctx.IdMedioEfectivo, ctx.IdMedioTarjeta], lineas.Select(l => l.GetProperty("idMedioPago").GetInt32()));
        Assert.Equal([60m, 40m], lineas.Select(l => l.GetProperty("importe").GetDecimal()));
        Assert.All(lineas, l => Assert.Equal(idPago, l.GetProperty("idGasto").GetInt32()));
    }

    [Fact]
    public async Task UnPagoSoloDeDescuentoBajaLaDeudaSinPagosNiTesoreriaNiOrigen()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSoloDeDescuentoBajaLaDeudaSinPagosNiTesoreriaNiOrigen));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 700m);

        var pago = await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, descuento: 1000m)),
            HttpStatusCode.Created);

        // Pagar más que el saldo es un adelanto: el saldo del personal queda negativo.
        Assert.Equal(-300m, pago.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(0, pago.GetProperty("desglose").GetProperty("lineas").GetArrayLength());

        var foto = await FotoAsync(ctx, idPersonal, idCliente);
        Assert.Equal(-300m, foto.SaldoDelPersonal);
        Assert.Equal(0m, foto.SaldoDelCliente);
        Assert.Equal(0, foto.Pagos);
        Assert.Equal(0, foto.AsientosDeTesoreria);
    }

    [Fact]
    public async Task UnPagoSoloDeDineroPorTesoreriaSinPuntoDeVentaDejaElPagoYSuEgresoSinPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSoloDeDineroPorTesoreriaSinPuntoDeVentaDejaElPagoYSuEgresoSinPuntoDeVenta));
        var idPersonal = await CrearPersonalAsync(ctx.Admin);

        var pago = await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(3), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 300m)])),
            HttpStatusCode.Created);

        Assert.Equal(-300m, pago.GetProperty("saldoResultante").GetDecimal());

        await using var abierto = Abrir(ctx);
        var pagoPersistido = await abierto.Pagos.SingleAsync();
        Assert.Null(pagoPersistido.IdPuntoVenta);
        Assert.Equal(CategoriaPago.Sueldos, pagoPersistido.Categoria);
        Assert.Equal(0, await abierto.MovimientosCuentaCorriente.CountAsync());
        var egreso = await abierto.MovimientosTesoreria.SingleAsync();
        Assert.Null(egreso.IdPuntoVenta);
        Assert.Equal(-300m, egreso.Final);
    }

    [Fact]
    public async Task UnDescuentoSinPuntoDeVentaSeRechazaConSu400AunConOrigenTesoreriaYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnDescuentoSinPuntoDeVentaSeRechazaConSu400AunConOrigenTesoreriaYNoEscribeNada));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 500m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);

        var soloDescuento = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(Dia(), descuento: 100m));
        var conTesoreria = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", descuento: 100m, lineas: [Linea(ctx.IdMedioEfectivo, 50m)]));

        Assert.Equal("pago_personal_punto_venta_requerido", await CodigoAsync(soloDescuento, HttpStatusCode.BadRequest));
        Assert.Equal("pago_personal_punto_venta_requerido", await CodigoAsync(conTesoreria, HttpStatusCode.BadRequest));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));
    }

    [Fact]
    public async Task UnPagoDelCajonImpactaElArqueoDelTurnoAbiertoYNoEscribeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDelCajonImpactaElArqueoDelTurnoAbiertoYNoEscribeTesoreria));
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var idCliente = await CrearClienteConDeudaAsync(ctx, 400m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 1000m);

        var antes = DateTimeOffset.UtcNow;
        var pago = await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", descuento: 100m,
                lineas: [Linea(ctx.IdMedioEfectivo, 200m), Linea(ctx.IdMedioEfectivo, 50m)])),
            HttpStatusCode.Created);
        var despues = DateTimeOffset.UtcNow;

        Assert.Equal(650m, pago.GetProperty("saldoResultante").GetDecimal());

        await using var abierto = Abrir(ctx);
        var unico = await abierto.Pagos.SingleAsync();
        Assert.Equal(OrigenFondosPago.CajaTurno, unico.OrigenFondos);
        Assert.Equal(idTurno, unico.IdTurnoCaja);
        Assert.Equal(ctx.IdPuntoVenta, unico.IdPuntoVenta);
        Assert.Equal(CategoriaPago.Sueldos, unico.Categoria);
        // Igual que el pago del POS, la fecha del cajón es el instante real.
        Assert.InRange(unico.Fecha, antes.AddSeconds(-1), despues.AddSeconds(1));
        Assert.Equal(250m, unico.Importe);
        Assert.Equal([200m, 50m], await abierto.ItemsPago.Where(i => i.IdPago == unico.Id).OrderBy(i => i.Id).Select(i => i.Importe).ToListAsync());
        Assert.Equal(0, await abierto.MovimientosTesoreria.CountAsync());
        Assert.Equal(300m, await abierto.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync());

        // El efectivo esperado del turno ya descuenta las dos líneas: 500 de fondo − 200 − 50.
        var resumen = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{idTurno}/resumen");
        var efectivo = resumen.GetProperty("medios").EnumerateArray()
            .Single(m => m.GetProperty("idMedioPago").GetInt32() == ctx.IdMedioEfectivo);
        Assert.Equal(250m, efectivo.GetProperty("importeEsperado").GetDecimal());
    }

    [Fact]
    public async Task UnPagoDelCajonSinTurnoAbiertoSeRechazaConSu409YNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDelCajonSinTurnoAbiertoSeRechazaConSu409YNoEscribeNada));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 400m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", descuento: 100m,
            lineas: [Linea(ctx.IdMedioEfectivo, 200m)]));

        Assert.Equal("turno_no_abierto", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));
    }

    [Fact]
    public async Task ElValorDelMedioDelCajonSeTrataComoElDelPosYLasLineasDelMismoMedioSumanEnElArqueo()
    {
        var ctx = await PrepararAsync(nameof(ElValorDelMedioDelCajonSeTrataComoElDelPosYLasLineasDelMismoMedioSumanEnElArqueo));
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin);

        await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno",
                lineas: [Linea(ctx.IdMedioEfectivo, 30m), Linea(ctx.IdMedioTarjeta, 20m, "cup-1")])),
            HttpStatusCode.Created);

        var resumen = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{idTurno}/resumen");
        var medios = resumen.GetProperty("medios").EnumerateArray()
            .ToDictionary(m => m.GetProperty("idMedioPago").GetInt32(), m => m.GetProperty("importeEsperado").GetDecimal());

        Assert.Equal(70m, medios[ctx.IdMedioEfectivo]);
        Assert.Equal(-20m, medios[ctx.IdMedioTarjeta]);

        await using var abierto = Abrir(ctx);
        Assert.Equal(1, await abierto.Pagos.CountAsync(g => g.IdTurnoCaja == idTurno));
    }

    // ---- rechazos: ninguno deja un libro a medias -------------------------------------------------

    [Fact]
    public async Task UnDescuentoSinClienteVinculadoSeRechazaYNoDejaRastroEnNingunLibroAunConLineasDeDinero()
    {
        var ctx = await PrepararAsync(nameof(UnDescuentoSinClienteVinculadoSeRechazaYNoDejaRastroEnNingunLibroAunConLineasDeDinero));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 100m,
            lineas: [Linea(ctx.IdMedioEfectivo, 200m)]));

        Assert.Equal("personal_sin_cliente_vinculado", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task UnClienteVinculadoDadoDeBajaCuentaComoSinVinculo()
    {
        var ctx = await PrepararAsync(nameof(UnClienteVinculadoDadoDeBajaCuentaComoSinVinculo));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 500m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 1000m);
        await DarDeBajaElClienteAsync(idCliente);
        var antes = await FotoAsync(ctx, idPersonal);
        var saldoDelClienteAntes = await SaldoDeClienteCrudoAsync(idCliente);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, descuento: 100m));

        Assert.Equal("personal_sin_cliente_vinculado", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
        Assert.Equal(saldoDelClienteAntes, await SaldoDeClienteCrudoAsync(idCliente));
    }

    private async Task DarDeBajaElClienteAsync(int idCliente)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        await using var baja = new NpgsqlCommand("UPDATE clientes SET deleted_at = now() WHERE id_cliente = $1", conexion);
        baja.Parameters.Add(new NpgsqlParameter { Value = idCliente });
        Assert.Equal(1, await baja.ExecuteNonQueryAsync());
    }

    /// <summary>El saldo leído por fuera del filtro de baja lógica: un cliente dado de baja ya no aparece por EF.</summary>
    private async Task<decimal> SaldoDeClienteCrudoAsync(int idCliente)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand("SELECT saldo FROM clientes WHERE id_cliente = $1", conexion);
        consulta.Parameters.Add(new NpgsqlParameter { Value = idCliente });
        return (decimal)(await consulta.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task UnDescuentoQueSuperaLaDeudaSeRechazaYRevierteTodoElPagoMientrasQueUnoIgualALaDeudaPasa()
    {
        var ctx = await PrepararAsync(nameof(UnDescuentoQueSuperaLaDeudaSeRechazaYRevierteTodoElPagoMientrasQueUnoIgualALaDeudaPasa));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 5000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);

        var excede = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 1000.01m,
            lineas: [Linea(ctx.IdMedioEfectivo, 200m)]));

        Assert.Equal("descuento_supera_la_deuda", await CodigoAsync(excede, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));

        var exacto = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 1000m,
            lineas: [Linea(ctx.IdMedioEfectivo, 200m)]));
        await CuerpoAsync(exacto, HttpStatusCode.Created);

        var despues = await FotoAsync(ctx, idPersonal, idCliente);
        Assert.Equal(0m, despues.SaldoDelCliente);
        Assert.Equal(3800m, despues.SaldoDelPersonal);
    }

    [Fact]
    public async Task UnDescuentoSobreUnClienteSinDeudaOConSaldoAFavorSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnDescuentoSobreUnClienteSinDeudaOConSaldoAFavorSeRechaza));
        var sinDeuda = await CrearClienteConDeudaAsync(ctx, 0m, "Sin deuda");
        var aFavor = await CrearClienteConDeudaAsync(ctx, 0m, "A favor");
        var ajusteAFavor = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{aFavor}/cuenta-corriente/ajustes",
            new SolicitudDeAjuste(ctx.IdPuntoVenta, -300m, "Saldo a favor de la prueba"));
        Assert.Equal(HttpStatusCode.Created, ajusteAFavor.StatusCode);
        var personalSinDeuda = await CrearPersonalAsync(ctx.Admin, sinDeuda, nombre: "Uno");
        var personalAFavor = await CrearPersonalAsync(ctx.Admin, aFavor, nombre: "Dos");

        foreach (var id in new[] { personalSinDeuda, personalAFavor })
        {
            var respuesta = await PagarAsync(ctx.Admin, id, Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, descuento: 0.01m));
            Assert.Equal("descuento_supera_la_deuda", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        }
    }

    [Fact]
    public async Task LasValidacionesDeLaSolicitudRespondenSuCodigoYNoEscribenNada()
    {
        var ctx = await PrepararAsync(nameof(LasValidacionesDeLaSolicitudRespondenSuCodigoYNoEscribenNada));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);
        var efectivo = Linea(ctx.IdMedioEfectivo, 10m);

        var casos = new (object Cuerpo, HttpStatusCode Estado, string Codigo)[]
        {
            (Cuerpo(Dia()), HttpStatusCode.BadRequest, "pago_personal_vacio"),
            (Cuerpo(null, descuento: 10m, idPuntoVenta: ctx.IdPuntoVenta), HttpStatusCode.BadRequest, "fecha_requerida"),
            (Cuerpo(Dia(), idEmpresa: ctx.IdEmpresa, lineas: [efectivo]), HttpStatusCode.BadRequest, "pago_personal_origen_requerido"),
            (Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", descuento: 10m),
                HttpStatusCode.BadRequest, "pago_personal_origen_sin_lineas"),
            (Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [efectivo], idEmpresa: ctx.IdEmpresa),
                HttpStatusCode.BadRequest, "pago_personal_empresa_no_aplica"),
            (Cuerpo(Dia(), origen: "Tesoreria", lineas: [efectivo]), HttpStatusCode.BadRequest, "pago_personal_empresa_requerida"),
            (Cuerpo(Dia(), origen: "CajaTurno", lineas: [efectivo]), HttpStatusCode.BadRequest, "pago_personal_punto_venta_requerido"),
            (Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, descuento: 0m), HttpStatusCode.BadRequest, "pago_personal_descuento_invalido"),
            (Cuerpo(Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, -5m)]),
                HttpStatusCode.BadRequest, "pago_personal_importe_invalido"),
            (Cuerpo(Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioCuentaCorriente, 10m)]),
                HttpStatusCode.BadRequest, "gasto_medio_pago_cuenta_corriente_invalido"),
            (Cuerpo(Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(987654, 10m)]),
                HttpStatusCode.NotFound, "no_encontrado"),
            (Cuerpo(Dia(), idEmpresa: 987654, origen: "Tesoreria", lineas: [efectivo]), HttpStatusCode.NotFound, "no_encontrado"),
            (Cuerpo(Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: 987654, origen: "Tesoreria", lineas: [efectivo]),
                HttpStatusCode.NotFound, "no_encontrado"),
            (Cuerpo(Dia(-1), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [efectivo]),
                HttpStatusCode.BadRequest, "gasto_fecha_futura")
        };

        foreach (var (cuerpo, estado, codigo) in casos)
        {
            var respuesta = await PagarAsync(ctx.Admin, idPersonal, cuerpo);
            Assert.Equal(codigo, await CodigoAsync(respuesta, estado));
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));
    }

    [Fact]
    public async Task UnPuntoDeVentaDeOtraEmpresaConOrigenTesoreriaSeRechazaConSu400()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaDeOtraEmpresaConOrigenTesoreriaSeRechazaConSu400));
        var idPersonal = await CrearPersonalAsync(ctx.Admin);
        int idPuntoVentaAjeno;
        await using (var abierto = Abrir(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            abierto.Empresas.Add(empresa);
            await abierto.SaveChangesAsync();
            var puntoVenta = new PuntoVenta
            {
                IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = "Local ajeno", CreatedAt = ahora, UpdatedAt = ahora
            };
            abierto.PuntosVenta.Add(puntoVenta);
            await abierto.SaveChangesAsync();
            idPuntoVentaAjeno = puntoVenta.Id;
        }

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: idPuntoVentaAjeno, origen: "Tesoreria",
            lineas: [Linea(ctx.IdMedioEfectivo, 10m)]));

        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task UnOrigenQueNoExisteSeRechazaComoSolicitudInvalidaYNoComoError500()
    {
        var ctx = await PrepararAsync(nameof(UnOrigenQueNoExisteSeRechazaComoSolicitudInvalidaYNoComoError500));
        var idPersonal = await CrearPersonalAsync(ctx.Admin);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, origen: "Banco", lineas: [Linea(ctx.IdMedioEfectivo, 10m)]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaLineaNulaSeRechazaConSu400YNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaNulaSeRechazaConSu400YNoEscribeNada));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [null!]));

        Assert.Equal("pago_personal_importe_invalido", await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task UnOrigenNumericoFueraDelEnumSeRechazaConSu400YNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnOrigenNumericoFueraDelEnumSeRechazaConSu400YNoEscribeNada));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var antes = await FotoAsync(ctx, idPersonal);

        var respuesta = await PagarAsync(ctx.Admin, idPersonal, new
        {
            fecha = Dia(), idEmpresa = ctx.IdEmpresa, idPuntoVenta = ctx.IdPuntoVenta, origenFondos = 7,
            lineas = new[] { Linea(ctx.IdMedioEfectivo, 10m) }
        });

        Assert.Equal("pago_personal_origen_invalido", await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    /// <summary>Ids REALES de otro tenant (no inventados): el filtro de tenant de cada lookup tiene que
    /// responder 404 para ellos, igual que para uno inexistente, y el otro tenant no recibe nada.</summary>
    [Fact]
    public async Task LaEmpresaElPuntoDeVentaYElMedioDeOtroTenantRespondenSu404YNoEscribenNada()
    {
        var ctx = await PrepararAsync(nameof(LaEmpresaElPuntoDeVentaYElMedioDeOtroTenantRespondenSu404YNoEscribenNada));
        var otro = await PrepararAsync(nameof(LaEmpresaElPuntoDeVentaYElMedioDeOtroTenantRespondenSu404YNoEscribenNada) + "otro");
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        await AbrirTurnoAsync(ctx);
        var antes = await FotoAsync(ctx, idPersonal);
        var personalDelOtro = await CrearPersonalAsync(otro.Admin, saldoInicial: 700m);
        var antesDelOtro = await FotoAsync(otro, personalDelOtro);
        var efectivo = Linea(ctx.IdMedioEfectivo, 10m);

        var casos = new (string Nombre, object Cuerpo)[]
        {
            ("empresa ajena", Cuerpo(Dia(), idEmpresa: otro.IdEmpresa, origen: "Tesoreria", lineas: [efectivo])),
            ("punto de venta ajeno con tesorería", Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: otro.IdPuntoVenta, origen: "Tesoreria", lineas: [efectivo])),
            ("punto de venta ajeno con el cajón", Cuerpo(
                Dia(), idPuntoVenta: otro.IdPuntoVenta, origen: "CajaTurno", lineas: [efectivo])),
            ("medio de pago ajeno", Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(otro.IdMedioEfectivo, 10m)]))
        };

        foreach (var (nombre, cuerpo) in casos)
        {
            var respuesta = await PagarAsync(ctx.Admin, idPersonal, cuerpo);
            Assert.True(respuesta.StatusCode == HttpStatusCode.NotFound, $"{nombre}: {respuesta.StatusCode}");
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
        Assert.Equal(antesDelOtro, await FotoAsync(otro, personalDelOtro));
    }

    // ---- autorización y 404 primero ----------------------------------------------------------------

    [Fact]
    public async Task AdminYSupervisorPaganPeroElVendedorYElAnonimoQuedanAfuera()
    {
        var ctx = await PrepararAsync(nameof(AdminYSupervisorPaganPeroElVendedorYElAnonimoQuedanAfuera));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        var cuerpo = Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [Linea(ctx.IdMedioEfectivo, 10m)]);

        await CuerpoAsync(await PagarAsync(ctx.Admin, idPersonal, cuerpo), HttpStatusCode.Created);
        var delSupervisor = await CuerpoAsync(await PagarAsync(ctx.Supervisor, idPersonal, cuerpo), HttpStatusCode.Created);
        Assert.Equal(980m, delSupervisor.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden, (await PagarAsync(ctx.Vendedor, idPersonal, cuerpo)).StatusCode);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PagarAsync(anonimo, idPersonal, cuerpo)).StatusCode);
    }

    /// <summary>Pagar de la tesorería es de Admin, igual que <c>/api/pagos/administracion</c>: el Supervisor
    /// paga del cajón o descuenta de la cuenta corriente, pero no del fondo de la empresa. Los dos Supervisor
    /// de tesorería van con un cuerpo que además es inválido (sin fecha / sin empresa) para fijar que el 403 se
    /// decide antes que cualquier 400 que dependa del cuerpo.</summary>
    [Fact]
    public async Task ElSupervisorNoPagaDeLaTesoreriaPeroElAdminSiYElSupervisorSiDescuentaYPagaDelCajon()
    {
        var ctx = await PrepararAsync(nameof(ElSupervisorNoPagaDeLaTesoreriaPeroElAdminSiYElSupervisorSiDescuentaYPagaDelCajon));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 5000m);
        await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);
        var tesoreria = Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", descuento: 100m, idPuntoVenta: ctx.IdPuntoVenta,
            lineas: [Linea(ctx.IdMedioEfectivo, 10m)]);

        foreach (var cuerpo in new[]
        {
            tesoreria,
            Cuerpo(null, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 10m)]),
            Cuerpo(Dia(), origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 10m)])
        })
        {
            Assert.Equal("prohibido", await CodigoAsync(await PagarAsync(ctx.Supervisor, idPersonal, cuerpo), HttpStatusCode.Forbidden));
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));

        await CuerpoAsync(await PagarAsync(ctx.Admin, idPersonal, tesoreria), HttpStatusCode.Created);

        var soloDescuento = await CuerpoAsync(
            await PagarAsync(ctx.Supervisor, idPersonal, Cuerpo(Dia(), idPuntoVenta: ctx.IdPuntoVenta, descuento: 200m)),
            HttpStatusCode.Created);
        Assert.Equal(4690m, soloDescuento.GetProperty("saldoResultante").GetDecimal());

        var delCajon = await CuerpoAsync(
            await PagarAsync(ctx.Supervisor, idPersonal, Cuerpo(
                Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [Linea(ctx.IdMedioEfectivo, 30m)])),
            HttpStatusCode.Created);
        Assert.Equal(4660m, delCajon.GetProperty("saldoResultante").GetDecimal());
    }

    [Fact]
    public async Task UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo()
    {
        var ctx = await PrepararAsync(nameof(UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo));
        var otro = await PrepararAsync(nameof(UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo) + "otro");
        var ajeno = await CrearPersonalAsync(otro.Admin);
        var dadoDeBaja = await CrearPersonalAsync(ctx.Admin, nombre: "Baja");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{dadoDeBaja}")).StatusCode);

        foreach (var id in new[] { 987654, ajeno, dadoDeBaja })
        {
            // Cuerpo inválido a propósito: vacío y sin fecha.
            var respuesta = await PagarAsync(ctx.Admin, id, Cuerpo(null));
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    // ---- el estado de cuenta ----------------------------------------------------------------------

    [Fact]
    public async Task LosMovimientosQueNoSonPagoNoTraenDesgloseYUnMedioDadoDeBajaDejaElNombreEnNull()
    {
        var ctx = await PrepararAsync(nameof(LosMovimientosQueNoSonPagoNoTraenDesgloseYUnMedioDadoDeBajaDejaElNombreEnNull));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 500m);
        await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioTarjeta, 50m)])),
            HttpStatusCode.Created);

        await using (var conexion = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await conexion.OpenAsync();
            await using var baja = new NpgsqlCommand("UPDATE medios_pago SET deleted_at = now() WHERE id_medio_pago = $1", conexion);
            baja.Parameters.Add(new NpgsqlParameter { Value = ctx.IdMedioTarjeta });
            Assert.Equal(1, await baja.ExecuteNonQueryAsync());
        }

        var items = (await CuentaAsync(ctx.Admin, idPersonal)).GetProperty("items").EnumerateArray().ToList();
        var ajuste = items.Single(i => i.GetProperty("tipo").GetString()!.Equals("ajuste", StringComparison.OrdinalIgnoreCase));
        var pago = items.Single(i => i.GetProperty("tipo").GetString()!.Equals("pago", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(JsonValueKind.Null, ajuste.GetProperty("desglose").ValueKind);
        var linea = pago.GetProperty("desglose").GetProperty("lineas")[0];
        Assert.Equal(JsonValueKind.Null, linea.GetProperty("nombreMedioPago").ValueKind);
        Assert.Equal(50m, linea.GetProperty("importe").GetDecimal());
    }

    // ---- atomicidad -------------------------------------------------------------------------------

    [Fact]
    public async Task UnFalloEnElUltimoPasoDeTesoreriaRevierteElAsientoElDescuentoLosPagosYLosSaldos()
    {
        var ctx = await PrepararAsync(nameof(UnFalloEnElUltimoPasoDeTesoreriaRevierteElAsientoElDescuentoLosPagosYLosSaldos));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 2000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 5000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("movimientos_tesoreria", "40001");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 500m,
                lineas: [Linea(ctx.IdMedioEfectivo, 300m), Linea(ctx.IdMedioTarjeta, 100m)]));
        }

        Assert.Equal("resultado_incierto", await CodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable));
        // Sin reintento: el INSERT roto se intentó una sola vez.
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));
    }

    private sealed class InterceptorQueRompeElPrimerCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        private int commits;

        public int Commits => Volatile.Read(ref commits);

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                throw new PostgresException("falla inyectada por la prueba al comitear", "ERROR", "ERROR", "40001");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task UnFalloTransitorioAlComitearNoSeReintentaYNoDejaNadaEnNingunLibro()
    {
        var ctx = await PrepararAsync(nameof(UnFalloTransitorioAlComitearNoSeReintentaYNoDejaNadaEnNingunLibro));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 2000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 5000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);
        var interceptor = new InterceptorQueRompeElPrimerCommit();

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 500m,
                lineas: [Linea(ctx.IdMedioEfectivo, 300m)]));
        }

        Assert.Equal("resultado_incierto", await CodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable));
        Assert.Equal(1, interceptor.Commits);
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idCliente));
    }

    // ---- concurrencia -----------------------------------------------------------------------------

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, params int[] pidsExcluidos)
    {
        var limite = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND NOT (pid = ANY($1))", conexionPoll);
            comando.Parameters.AddWithValue(pidsExcluidos);

            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    /// <summary>Una escritura ajena sin comitear sobre la fila del personal hace esperar al pago en su primer
    /// lock de fila. Al comitear, el pago calcula su saldo resultante sobre el saldo ya comiteado: 1000 + 500 −
    /// 200. Si el pago leyera el saldo antes del lock, el asiento y el saldo final dirían otra cosa.</summary>
    [Fact]
    public async Task UnPagoEsperaLaFilaDelPersonalYCalculaSuSaldoSobreLoQueComiteoElOtro()
    {
        var ctx = await PrepararAsync(nameof(UnPagoEsperaLaFilaDelPersonalYCalculaSuSaldoSobreLoQueComiteoElOtro));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);
        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            "UPDATE personal SET saldo = saldo + 500 WHERE id_personal = $1", conexionEscritor, transaccionEscritor))
        {
            bloquear.Parameters.AddWithValue(idPersonal);
            await bloquear.ExecuteNonQueryAsync();
        }

        var pagoTask = PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 200m)]));

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll),
            "El pago nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        var pago = await CuerpoAsync(await pagoTask, HttpStatusCode.Created);
        Assert.Equal(1300m, pago.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(1300m, (await FotoAsync(ctx, idPersonal)).SaldoDelPersonal);
    }

    /// <summary>La deuda del cliente baja a 200 mientras el pago espera la fila del cliente; el descuento de
    /// 500 era válido contra la deuda que el pago habría leído antes (1000) y deja de serlo contra el saldo que
    /// devuelve el UPDATE atómico. Rechazar acá prueba que la decisión se toma sobre el saldo devuelto.</summary>
    [Fact]
    public async Task ElDescuentoSeDecideSobreElSaldoQueDevuelveElUpdateYNoSobreUnaLecturaPrevia()
    {
        var ctx = await PrepararAsync(nameof(ElDescuentoSeDecideSobreElSaldoQueDevuelveElUpdateYNoSobreUnaLecturaPrevia));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 3000m);
        var antes = await FotoAsync(ctx, idPersonal, idCliente);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);
        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var cobrar = new NpgsqlCommand(
            "UPDATE clientes SET saldo = saldo - 800 WHERE id_cliente = $1", conexionEscritor, transaccionEscritor))
        {
            cobrar.Parameters.AddWithValue(idCliente);
            await cobrar.ExecuteNonQueryAsync();
        }

        var pagoTask = PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 500m,
            lineas: [Linea(ctx.IdMedioEfectivo, 100m)]));

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll),
            "El pago nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        Assert.Equal("descuento_supera_la_deuda", await CodigoAsync(await pagoTask, HttpStatusCode.Conflict));
        var despues = await FotoAsync(ctx, idPersonal, idCliente);
        // Solo queda el cobro ajeno: el pago revirtió el personal, el pago y la tesorería.
        Assert.Equal(antes with { SaldoDelCliente = 200m }, despues);
    }

    /// <summary>Cláusula bajo prueba: el <c>deleted_at IS NULL</c> del <c>UPDATE</c> atómico del cliente. Una baja
    /// lógica del cliente vinculado sin comitear hace esperar al pago en esa fila; cuando la baja comitea, el
    /// <c>UPDATE</c> se reevalúa y no encuentra fila viva. Cualquier lectura previa vería al cliente vivo (la baja
    /// todavía no comiteó), así que solo el predicado del <c>UPDATE</c> puede rechazar el pago.</summary>
    [Fact]
    public async Task UnClienteQueSeDaDeBajaMientrasElPagoEsperaSuFilaNoRecibeElDescuentoYTodoRevierte()
    {
        var ctx = await PrepararAsync(nameof(UnClienteQueSeDaDeBajaMientrasElPagoEsperaSuFilaNoRecibeElDescuentoYTodoRevierte));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 3000m);
        var antes = await FotoAsync(ctx, idPersonal);
        var saldoDelClienteAntes = await SaldoDeClienteCrudoAsync(idCliente);
        Assert.Equal(1000m, saldoDelClienteAntes);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);
        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var darDeBaja = new NpgsqlCommand(
            "UPDATE clientes SET deleted_at = now() WHERE id_cliente = $1", conexionEscritor, transaccionEscritor))
        {
            darDeBaja.Parameters.AddWithValue(idCliente);
            Assert.Equal(1, await darDeBaja.ExecuteNonQueryAsync());
        }

        var pagoTask = PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 500m,
            lineas: [Linea(ctx.IdMedioEfectivo, 100m)]));

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll),
            "El pago nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        Assert.Equal("personal_sin_cliente_vinculado", await CodigoAsync(await pagoTask, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
        Assert.Equal(saldoDelClienteAntes, await SaldoDeClienteCrudoAsync(idCliente));
    }

    [Fact]
    public async Task PagosConcurrentesDelMismoPersonalDejanLosSaldosExactosYLasCadenasConsistentes()
    {
        var ctx = await PrepararAsync(nameof(PagosConcurrentesDelMismoPersonalDejanLosSaldosExactosYLasCadenasConsistentes));
        var idCliente = await CrearClienteConDeudaAsync(ctx, 4000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, idCliente, saldoInicial: 10000m);
        const int pagos = 8;

        var respuestas = await Task.WhenAll(Enumerable.Range(1, pagos).Select(_ => PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idEmpresa: ctx.IdEmpresa, idPuntoVenta: ctx.IdPuntoVenta, origen: "Tesoreria", descuento: 100m,
            lineas: [Linea(ctx.IdMedioEfectivo, 50m)]))));

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }

        await using var abierto = Abrir(ctx);
        var db = abierto;
        Assert.Equal(10000m - pagos * 150m, await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync());
        Assert.Equal(4000m - pagos * 100m, await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync());

        var asientos = await db.MovimientosPersonal
            .Where(m => m.Tipo == TipoMovimientoPersonal.Pago).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(pagos, asientos.Count);
        var acumulado = 10000m;
        foreach (var asiento in asientos)
        {
            acumulado += asiento.Importe;
            Assert.Equal(acumulado, asiento.SaldoResultante);
        }

        var descuentos = await db.MovimientosCuentaCorriente
            .Where(m => m.Tipo == TipoMovimientoCc.DescuentoPersonal).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(pagos, descuentos.Count);
        var saldoDelCliente = 4000m;
        foreach (var descuento in descuentos)
        {
            saldoDelCliente += descuento.Importe;
            Assert.Equal(saldoDelCliente, descuento.SaldoResultante);
        }

        var egresos = await db.MovimientosTesoreria.OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(pagos, egresos.Count);
        var final = 0m;
        foreach (var egreso in egresos)
        {
            Assert.Equal(final, egreso.Inicio);
            final = egreso.Final;
        }

        Assert.Equal(-pagos * 50m, final);
        Assert.Equal(pagos, await db.Pagos.CountAsync(g => g.IdMovimientoPersonal != null));
    }

    /// <summary>Un cierre en curso (el turno pasa a cerrado sin comitear) hace esperar al pago del cajón en el
    /// <c>FOR SHARE</c> del turno. El pre-chequeo previo a la transacción ya vio el turno abierto; solo el
    /// re-chequeo bajo el lock puede rechazar el pago cuando el cierre comitea. Sin él, el pago entraría a un
    /// turno cuyo arqueo ya se derivó.</summary>
    [Fact]
    public async Task UnPagoDelCajonQueEsperaAlCierreSeRechazaSiElTurnoQuedoCerradoYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDelCajonQueEsperaAlCierreSeRechazaSiElTurnoQuedoCerradoYNoEscribeNada));
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 500m);
        var antes = await FotoAsync(ctx, idPersonal);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);
        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var cerrar = new NpgsqlCommand(
            "UPDATE turnos_caja SET estado = 'cerrado', fecha_cierre = now(), " +
            "id_empleado_cierre = (SELECT id_usuario FROM usuarios WHERE id_tenant = $1 ORDER BY id_usuario LIMIT 1) " +
            "WHERE id_turno_caja = $2", conexionEscritor, transaccionEscritor))
        {
            cerrar.Parameters.AddWithValue(ctx.IdTenant);
            cerrar.Parameters.AddWithValue(idTurno);
            Assert.Equal(1, await cerrar.ExecuteNonQueryAsync());
        }

        var pagoTask = PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [Linea(ctx.IdMedioEfectivo, 20m)]));

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll),
            "El pago nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        Assert.Equal("turno_no_abierto", await CodigoAsync(await pagoTask, HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    /// <summary>Pagos del cajón y el cierre del mismo turno compiten por el lock del turno (FOR SHARE contra
    /// FOR UPDATE). Cada pago o comitea antes del cierre o lo ve cerrado y responde 409; nunca se traban ni
    /// quedan fuera del arqueo persistido.</summary>
    [Fact]
    public async Task PagosDelCajonConcurrentesConElCierreNoSeTrabanYElArqueoPersistidoIncluyeLosComiteados()
    {
        var ctx = await PrepararAsync(nameof(PagosDelCajonConcurrentesConElCierreNoSeTrabanYElArqueoPersistidoIncluyeLosComiteados));
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 1000m);
        var idPersonal = await CrearPersonalAsync(ctx.Admin);

        var cierre = ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 0m)], null));
        var pagos = Enumerable.Range(1, 6).Select(_ => PagarAsync(ctx.Admin, idPersonal, Cuerpo(
            Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [Linea(ctx.IdMedioEfectivo, 10m)]))).ToList();

        var todas = await Task.WhenAll(pagos.Append(cierre));
        var respuestaDeCierre = await CuerpoAsync(todas[^1], HttpStatusCode.OK);

        var comiteados = 0;
        foreach (var respuesta in todas.Take(6))
        {
            if (respuesta.StatusCode == HttpStatusCode.Created)
            {
                comiteados++;
            }
            else
            {
                Assert.Equal("turno_no_abierto", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
            }
        }

        var esperado = respuestaDeCierre.GetProperty("arqueos").EnumerateArray()
            .Single(a => a.GetProperty("idMedioPago").GetInt32() == ctx.IdMedioEfectivo)
            .GetProperty("importeEsperado").GetDecimal();
        Assert.Equal(1000m - comiteados * 10m, esperado);

        await using var abierto = Abrir(ctx);
        Assert.Equal(comiteados, await abierto.Pagos.CountAsync(g => g.IdTurnoCaja == idTurno));
    }

    // ---- los pagos de un pago no se tocan por separado ---------------------------------------------

    private async Task<(int IdPagoDeTesoreria, int IdPagoDeCajon)> PagarYObtenerPagosAsync(Contexto ctx, int idPersonal)
    {
        await AbrirTurnoAsync(ctx);
        await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idEmpresa: ctx.IdEmpresa, origen: "Tesoreria", lineas: [Linea(ctx.IdMedioEfectivo, 70m)])),
            HttpStatusCode.Created);
        await CuerpoAsync(
            await PagarAsync(ctx.Admin, idPersonal, Cuerpo(
                Dia(), idPuntoVenta: ctx.IdPuntoVenta, origen: "CajaTurno", lineas: [Linea(ctx.IdMedioEfectivo, 30m)])),
            HttpStatusCode.Created);

        await using var abierto = Abrir(ctx);
        var pagos = await abierto.Pagos.OrderBy(g => g.Id).ToListAsync();
        return (pagos[0].Id, pagos[1].Id);
    }

    private static SolicitudDeEdicionDePagoConLineas EdicionValida(Contexto ctx) =>
        PagoDeUnMedio.Edicion(CategoriaPago.Sueldos, null, null, "Concepto editado", null, ctx.IdMedioEfectivo, null, 5m);

    [Fact]
    public async Task UnPagoDeUnPagoNoSeEditaNiSeDaDeBajaNiSeLigaPorSeparadoYSiguenEditablesLosDemas()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeUnPagoNoSeEditaNiSeDaDeBajaNiSeLigaPorSeparadoYSiguenEditablesLosDemas));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var (idDeTesoreria, idDelCajon) = await PagarYObtenerPagosAsync(ctx, idPersonal);
        var antes = await FotoAsync(ctx, idPersonal);

        foreach (var id in new[] { idDeTesoreria, idDelCajon })
        {
            var edicionAdmin = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{id}", EdicionValida(ctx));
            Assert.Equal("gasto_de_pago_de_personal", await CodigoAsync(edicionAdmin, HttpStatusCode.Conflict));

            var baja = await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{id}");
            Assert.Equal("gasto_de_pago_de_personal", await CodigoAsync(baja, HttpStatusCode.Conflict));
        }

        // La edición del POS exige el turno abierto: el pago del cajón lo cumple y aun así se rechaza por su vínculo;
        // el de tesorería no tiene turno, y el código tiene que seguir siendo el del vínculo y no gasto_turno_cerrado
        // (la guarda va antes de la comprobación del turno).
        foreach (var id in new[] { idDelCajon, idDeTesoreria })
        {
            var edicionPos = await ctx.Admin.PutAsJsonAsync($"/api/pagos/{id}", EdicionValida(ctx));
            Assert.Equal("gasto_de_pago_de_personal", await CodigoAsync(edicionPos, HttpStatusCode.Conflict));
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
        await using (var abierto = Abrir(ctx))
        {
            var pagos = await abierto.Pagos.OrderBy(g => g.Id).ToListAsync();
            Assert.Equal([70m, 30m], pagos.Select(g => g.Importe));
            Assert.All(pagos, g => Assert.Null(g.DeletedAt));
            Assert.All(pagos, g => Assert.Equal("Pago de personal: Ana", g.Concepto));
        }

        // Un pago administrativo común sigue editable y con baja.
        var comun = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion",
            PagoDeUnMedio.Administracion(
                FechaDelNegocio.Hoy(), ctx.IdEmpresa, null, CategoriaPago.Otros, null, null, "Gasto común", null,
                ctx.IdMedioEfectivo, null, 20m));
        var idComun = (await CuerpoAsync(comun, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        Assert.Equal(
            HttpStatusCode.OK, (await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{idComun}", EdicionValida(ctx))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{idComun}")).StatusCode);
    }

    /// <summary>El cliente no puede adivinar qué pagos van a responder 409 <c>gasto_de_pago_de_personal</c>: los
    /// listados del POS y de administración, y el detalle administrativo, traen <c>idMovimientoPersonal</c>. Un
    /// pago común lo trae en <c>null</c>.</summary>
    [Fact]
    public async Task LosListadosDePagosExponenElVinculoConElPagoYUnPagoComunLoTraeNulo()
    {
        var ctx = await PrepararAsync(nameof(LosListadosDePagosExponenElVinculoConElPagoYUnPagoComunLoTraeNulo));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var (idDeTesoreria, idDelCajon) = await PagarYObtenerPagosAsync(ctx, idPersonal);

        var comunDeAdministracion = await CuerpoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                PagoDeUnMedio.Administracion(
                    FechaDelNegocio.Hoy(), ctx.IdEmpresa, null, CategoriaPago.Otros, null, null, "Gasto común", null,
                    ctx.IdMedioEfectivo, null, 20m)),
            HttpStatusCode.Created);
        var idComunDeAdministracion = comunDeAdministracion.GetProperty("id").GetInt32();
        var comunDelPos = await CuerpoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos",
                PagoDeUnMedio.Pos(
                    ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto común del POS", null, ctx.IdMedioEfectivo, null, 15m)),
            HttpStatusCode.Created);
        var idComunDelPos = comunDelPos.GetProperty("id").GetInt32();

        int idAsientoDeTesoreria;
        int idAsientoDelCajon;
        await using (var abierto = Abrir(ctx))
        {
            idAsientoDeTesoreria = await abierto.Pagos.Where(g => g.Id == idDeTesoreria).Select(g => g.IdMovimientoPersonal!.Value).SingleAsync();
            idAsientoDelCajon = await abierto.Pagos.Where(g => g.Id == idDelCajon).Select(g => g.IdMovimientoPersonal!.Value).SingleAsync();
        }

        Assert.NotEqual(idAsientoDeTesoreria, idAsientoDelCajon);

        var administracion = await CuerpoAsync(
            await ctx.Admin.GetAsync($"/api/pagos/administracion?idEmpresa={ctx.IdEmpresa}&tamanio=50"), HttpStatusCode.OK);
        var filas = administracion.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32());
        Assert.Equal(idAsientoDeTesoreria, filas[idDeTesoreria].GetProperty("idMovimientoPersonal").GetInt32());
        Assert.Equal(idAsientoDelCajon, filas[idDelCajon].GetProperty("idMovimientoPersonal").GetInt32());
        Assert.Equal(JsonValueKind.Null, filas[idComunDeAdministracion].GetProperty("idMovimientoPersonal").ValueKind);

        var detalle = await CuerpoAsync(
            await ctx.Admin.GetAsync($"/api/pagos/administracion/{idDeTesoreria}"), HttpStatusCode.OK);
        Assert.Equal(idAsientoDeTesoreria, detalle.GetProperty("idMovimientoPersonal").GetInt32());

        var delPos = await CuerpoAsync(
            await ctx.Admin.GetAsync($"/api/pagos?idPuntoVenta={ctx.IdPuntoVenta}&tamanio=50"), HttpStatusCode.OK);
        var filasDelPos = delPos.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32());
        Assert.Equal(idAsientoDelCajon, filasDelPos[idDelCajon].GetProperty("idMovimientoPersonal").GetInt32());
        Assert.Equal(JsonValueKind.Null, filasDelPos[idComunDelPos].GetProperty("idMovimientoPersonal").ValueKind);
    }

    [Fact]
    public async Task ElRechazoDeUnPagoDeUnPagoVieneDespuesDelCuatrocientosCuatroYNuncaAntes()
    {
        var ctx = await PrepararAsync(nameof(ElRechazoDeUnPagoDeUnPagoVieneDespuesDelCuatrocientosCuatroYNuncaAntes));
        var otro = await PrepararAsync(nameof(ElRechazoDeUnPagoDeUnPagoVieneDespuesDelCuatrocientosCuatroYNuncaAntes) + "otro");
        var idPersonalAjeno = await CrearPersonalAsync(otro.Admin, saldoInicial: 1000m);
        var (idAjeno, _) = await PagarYObtenerPagosAsync(otro, idPersonalAjeno);

        foreach (var id in new[] { 987654, idAjeno })
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{id}", EdicionValida(ctx))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync($"/api/pagos/{id}", EdicionValida(ctx))).StatusCode);
        }
    }

    [Fact]
    public async Task UnPagoDeUnPagoNoSeVinculaAUnaCompraYElVinculoQuedaSinEscribir()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeUnPagoNoSeVinculaAUnaCompraYElVinculoQuedaSinEscribir));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, saldoInicial: 1000m);
        var (idPago, _) = await PagarYObtenerPagosAsync(ctx, idPersonal);
        var idCompra = await SembrarCompraConfirmadaAsync(ctx);
        decimal saldoDelProveedorAntes;
        await using (var previo = Abrir(ctx))
        {
            saldoDelProveedorAntes = await previo.Proveedores.Select(p => p.Saldo).SingleAsync();
        }

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{idPago}/vincular-compra", new SolicitudDeVincularCompra(idCompra));

        Assert.Equal("gasto_de_pago_de_personal", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        await using var abierto = Abrir(ctx);
        var pago = await abierto.Pagos.SingleAsync(g => g.Id == idPago);
        Assert.Equal(CategoriaPago.Sueldos, pago.Categoria);
        Assert.Null(pago.IdComprobanteCompra);
        Assert.Equal(saldoDelProveedorAntes, await abierto.Proveedores.Select(p => p.Saldo).SingleAsync());
    }

    private async Task<int> SembrarCompraConfirmadaAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = ctx.IdTenant, Nombre = "Area de la compra", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        var condicionFiscal = new CondicionFiscal { Codigo = $"CF-{Guid.NewGuid():N}", Nombre = "CF", CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Ways.Domain.Proveedores.Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = "Proveedor", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        var idAlicuota = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"A-{Guid.NewGuid():N}", Nombre = "Articulo", IdArea = area.Id,
            IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        await db.SaveChangesAsync();

        var solicitud = new SolicitudDeCompra(
            proveedor.Id, idTipo, ctx.IdPuntoVenta, "0001-00000001", FechaDelNegocio.Hoy(), null,
            [new LineaDeCompraSolicitada(articulo.Id, "Item", 10m, null, null, 100m, 0m, idAlicuota, true)]);
        var creada = await CuerpoAsync(await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud), HttpStatusCode.Created);
        var idCompra = creada.GetProperty("id").GetInt32();
        await CuerpoAsync(await ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null), HttpStatusCode.OK);
        return idCompra;
    }
}
