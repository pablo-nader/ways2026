using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/compras/{id}/pagos</c>: pagar una compra confirmada desde su detalle o su cuenta
/// corriente. Un pago es UN gasto administrativo (categoría proveedor, origen tesorería) más el
/// movimiento <c>pago</c> de cuenta corriente imputado a la compra y el egreso de tesorería, todo en
/// una transacción, con el importe acotado por el saldo pendiente de ESA compra.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagoDeCompraEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";
    private const string SqlStateTransitorio = "40001";

    private static readonly TimeZoneInfo ZonaDelNegocio =
        TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdAlicuotaIva21,
        int IdTipoCFA, int IdMedioEfectivo, int IdMedioCuentaCorriente, string MailAdmin, string PasswordAdmin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        // La plantilla de aprovisionamiento no trae ningún medio de cuenta corriente.
        var medioCuentaCorriente = new MedioPago
        {
            Nombre = "Cuenta corriente", Orden = 99, Comportamiento = ComportamientoMedioPago.CuentaCorriente,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        dbTenant.MediosPago.Add(medioCuentaCorriente);
        await dbTenant.SaveChangesAsync();
        var idMedioCuentaCorriente = medioCuentaCorriente.Id;

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, idAlicuotaIva21,
            idTipoCFA, idMedioEfectivo, idMedioCuentaCorriente, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx)
    {
        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-pago-compra", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    /// <summary>Una compra de un solo concepto: total = <paramref name="costo"/> × 1.21 (factura A).</summary>
    private static async Task<CompraDetalle> CrearCompraAsync(
        Contexto ctx, bool confirmar, decimal costo = 1000m, string numeroExterno = "0001-00000001")
    {
        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, FechaDelNegocio.Hoy(), null,
            [new LineaDeCompraSolicitada(null, "Concepto de prueba", 1m, null, null, costo, 0m, ctx.IdAlicuotaIva21)]);
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;
        if (!confirmar)
        {
            return creada;
        }

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> PagarAsync(
        HttpClient cliente, int idCompra, decimal importe, int idMedioPago, DateOnly? fecha = null, string? concepto = null) =>
        cliente.PostAsJsonAsync(
            $"/api/compras/{idCompra}/pagos",
            new SolicitudDePagoDeCompra(fecha ?? FechaDelNegocio.Hoy(), importe, idMedioPago, concepto));

    private static async Task<ResultadoDePagoDeCompra> PagarOkAsync(
        HttpClient cliente, int idCompra, decimal importe, int idMedioPago, DateOnly? fecha = null, string? concepto = null)
    {
        var respuesta = await PagarAsync(cliente, idCompra, importe, idMedioPago, fecha, concepto);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ResultadoDePagoDeCompra>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta)
    {
        var problema = JsonSerializer.Deserialize<JsonElement>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson);
        return problema.GetProperty("codigo").GetString()!;
    }

    private static async Task<CompraDetalle> ObtenerCompraAsync(HttpClient cliente, int idCompra) =>
        (await cliente.GetFromJsonAsync<CompraDetalle>($"/api/compras/{idCompra}", OpcionesJson))!;

    private WaysDbContext ContextoDelTenant(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private sealed record Huella(decimal Saldo, int Gastos, int MovimientosDeCc, int MovimientosDeTesoreria);

    private async Task<Huella> HuellaAsync(Contexto ctx)
    {
        await using var db = ContextoDelTenant(ctx);
        return new Huella(
            await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync(),
            await db.Gastos.CountAsync(),
            await db.MovimientosCuentaCorrienteProveedor.CountAsync(),
            await db.MovimientosTesoreria.CountAsync());
    }

    // ---- camino feliz ------------------------------------------------------------------------

    [Fact]
    public async Task PagarElTotalDeLaCompraCreaElGastoElPagoDeCuentaCorrienteYElEgresoDeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(PagarElTotalDeLaCompraCreaElGastoElPagoDeCuentaCorrienteYElEgresoDeTesoreria));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        Assert.Equal(1210m, compra.Total);
        Assert.Equal(0m, compra.Pagado);
        Assert.Equal(1210m, compra.SaldoPendiente);
        var fechaDeNegocio = FechaDelNegocio.Hoy().AddDays(-3);

        var resultado = await PagarOkAsync(ctx.Admin, compra.Id, 1210m, ctx.IdMedioEfectivo, fechaDeNegocio);

        Assert.Equal(1210m, resultado.Pagado);
        Assert.Equal(0m, resultado.SaldoPendiente);
        var gastoCreado = resultado.Gasto;
        Assert.Equal(CategoriaGasto.Proveedor, gastoCreado.Categoria);
        Assert.Equal(OrigenFondosGasto.Tesoreria, gastoCreado.OrigenFondos);
        Assert.Equal(ctx.IdProveedor, gastoCreado.IdProveedor);
        Assert.Equal(ctx.IdPuntoVenta, gastoCreado.IdPuntoVenta);
        Assert.Equal(compra.Id, gastoCreado.IdComprobanteCompra);
        Assert.Null(gastoCreado.IdTurnoCaja);
        Assert.Equal("Pago Factura A de compra 0001-00000001", gastoCreado.Concepto);
        Assert.Equal(fechaDeNegocio, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(gastoCreado.Fecha, ZonaDelNegocio).DateTime));

        await using var db = ContextoDelTenant(ctx);
        var gasto = await db.Gastos.SingleAsync(g => g.Id == gastoCreado.Id);
        Assert.Equal(1210m, gasto.Importe);
        Assert.Equal(ctx.IdEmpresa, gasto.IdEmpresa);
        Assert.Equal(ctx.IdMedioEfectivo, gasto.IdMedioPago);

        var pago = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.Tipo == TipoMovimientoCcProveedor.Pago && m.IdGasto == gasto.Id);
        Assert.Equal(compra.Id, pago.IdComprobanteCompra);
        Assert.Equal(-1210m, pago.Importe);
        Assert.Equal(0m, pago.SaldoResultante);

        var egreso = await db.MovimientosTesoreria.SingleAsync(m => m.IdGasto == gasto.Id);
        Assert.Equal(TipoMovimientoTesoreria.Gasto, egreso.Tipo);
        Assert.Equal(1210m, egreso.Egreso);
        Assert.Equal(ctx.IdEmpresa, egreso.IdEmpresa);

        Assert.Equal(0m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());

        var detalle = await ObtenerCompraAsync(ctx.Admin, compra.Id);
        Assert.Equal(1210m, detalle.Pagado);
        Assert.Equal(0m, detalle.SaldoPendiente);
    }

    [Fact]
    public async Task DosPagosParcialesDescuentanElSaldoDeEsaCompraYElTercerPagoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(DosPagosParcialesDescuentanElSaldoDeEsaCompraYElTercerPagoSeRechaza));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        var otraCompra = await CrearCompraAsync(ctx, confirmar: true, numeroExterno: "0001-00000002");

        var primero = await PagarOkAsync(ctx.Admin, compra.Id, 400m, ctx.IdMedioEfectivo);
        Assert.Equal(400m, primero.Pagado);
        Assert.Equal(810m, primero.SaldoPendiente);

        var segundo = await PagarOkAsync(ctx.Admin, compra.Id, 810m, ctx.IdMedioEfectivo, concepto: "  Saldo final  ");
        Assert.Equal(1210m, segundo.Pagado);
        Assert.Equal(0m, segundo.SaldoPendiente);
        Assert.Equal("Saldo final", segundo.Gasto.Concepto);

        var tercero = await PagarAsync(ctx.Admin, compra.Id, 1m, ctx.IdMedioEfectivo);
        Assert.Equal(HttpStatusCode.Conflict, tercero.StatusCode);
        Assert.Equal("compra_sin_saldo_pendiente", await CodigoDeErrorAsync(tercero));

        var intacta = await ObtenerCompraAsync(ctx.Admin, otraCompra.Id);
        Assert.Equal(0m, intacta.Pagado);
        Assert.Equal(1210m, intacta.SaldoPendiente);

        await using var db = ContextoDelTenant(ctx);
        Assert.Equal(2, await db.Gastos.CountAsync(g => g.IdComprobanteCompra == compra.Id));
        Assert.Equal(1210m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
    }

    // ---- rechazos ----------------------------------------------------------------------------

    [Fact]
    public async Task UnPagoMayorAlSaldoPendienteSeRechazaYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnPagoMayorAlSaldoPendienteSeRechazaYNoEscribeNada));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        await PagarOkAsync(ctx.Admin, compra.Id, 200m, ctx.IdMedioEfectivo);
        var antes = await HuellaAsync(ctx);

        var respuesta = await PagarAsync(ctx.Admin, compra.Id, 1010.01m, ctx.IdMedioEfectivo);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_excede_saldo_pendiente", await CodigoDeErrorAsync(respuesta));
        Assert.Equal(antes, await HuellaAsync(ctx));

        var justo = await PagarOkAsync(ctx.Admin, compra.Id, 1010m, ctx.IdMedioEfectivo);
        Assert.Equal(0m, justo.SaldoPendiente);
    }

    [Fact]
    public async Task UnaCompraQueNoEstaConfirmadaNoSePaga()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraQueNoEstaConfirmadaNoSePaga));
        var borrador = await CrearCompraAsync(ctx, confirmar: false);
        var anulada = await CrearCompraAsync(ctx, confirmar: true, numeroExterno: "0001-00000002");
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{anulada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
        var antes = await HuellaAsync(ctx);

        var sobreBorrador = await PagarAsync(ctx.Admin, borrador.Id, 100m, ctx.IdMedioEfectivo);
        var sobreAnulada = await PagarAsync(ctx.Admin, anulada.Id, 100m, ctx.IdMedioEfectivo);

        Assert.Equal(HttpStatusCode.Conflict, sobreBorrador.StatusCode);
        Assert.Equal("compra_no_confirmada", await CodigoDeErrorAsync(sobreBorrador));
        Assert.Equal(HttpStatusCode.Conflict, sobreAnulada.StatusCode);
        Assert.Equal("compra_anulada", await CodigoDeErrorAsync(sobreAnulada));
        Assert.Equal(antes, await HuellaAsync(ctx));

        Assert.Equal(0m, (await ObtenerCompraAsync(ctx.Admin, borrador.Id)).SaldoPendiente);
        Assert.Equal(0m, (await ObtenerCompraAsync(ctx.Admin, anulada.Id)).SaldoPendiente);
    }

    [Fact]
    public async Task LasValidacionesDelGastoAdministrativoTambienRigenElPago()
    {
        var ctx = await PrepararAsync(nameof(LasValidacionesDelGastoAdministrativoTambienRigenElPago));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        var antes = await HuellaAsync(ctx);

        var importeCero = await PagarAsync(ctx.Admin, compra.Id, 0m, ctx.IdMedioEfectivo);
        Assert.Equal(HttpStatusCode.BadRequest, importeCero.StatusCode);
        Assert.Equal("gasto_importe_invalido", await CodigoDeErrorAsync(importeCero));

        var fechaFutura = await PagarAsync(
            ctx.Admin, compra.Id, 100m, ctx.IdMedioEfectivo, FechaDelNegocio.Hoy().AddDays(2));
        Assert.Equal(HttpStatusCode.BadRequest, fechaFutura.StatusCode);
        Assert.Equal("gasto_fecha_futura", await CodigoDeErrorAsync(fechaFutura));

        var cuentaCorriente = await PagarAsync(ctx.Admin, compra.Id, 100m, ctx.IdMedioCuentaCorriente);
        Assert.Equal(HttpStatusCode.BadRequest, cuentaCorriente.StatusCode);
        Assert.Equal("gasto_medio_pago_cuenta_corriente_invalido", await CodigoDeErrorAsync(cuentaCorriente));

        var medioInexistente = await PagarAsync(ctx.Admin, compra.Id, 100m, 999999);
        Assert.Equal(HttpStatusCode.NotFound, medioInexistente.StatusCode);

        var compraInexistente = await PagarAsync(ctx.Admin, 999999, 100m, ctx.IdMedioEfectivo);
        Assert.Equal(HttpStatusCode.NotFound, compraInexistente.StatusCode);

        Assert.Equal(antes, await HuellaAsync(ctx));
    }

    [Fact]
    public async Task UnVendedorNoPuedePagarPeroSiVeElSaldoPendienteDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorNoPuedePagarPeroSiVeElSaldoPendienteDeLaCompra));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        await PagarOkAsync(ctx.Admin, compra.Id, 210m, ctx.IdMedioEfectivo);
        using var vendedor = await CrearVendedorAsync(ctx);
        var antes = await HuellaAsync(ctx);

        var respuesta = await PagarAsync(vendedor, compra.Id, 100m, ctx.IdMedioEfectivo);
        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal(antes, await HuellaAsync(ctx));

        var detalle = await ObtenerCompraAsync(vendedor, compra.Id);
        Assert.Equal(210m, detalle.Pagado);
        Assert.Equal(1000m, detalle.SaldoPendiente);
        Assert.All(detalle.Items, i => Assert.Null(i.CostoUnitario));
    }

    [Fact]
    public async Task UnAdminDeOtroTenantNoPuedePagarLaCompra()
    {
        var ctx = await PrepararAsync(nameof(UnAdminDeOtroTenantNoPuedePagarLaCompra));
        var ajeno = await PrepararAsync($"{nameof(UnAdminDeOtroTenantNoPuedePagarLaCompra)}Ajeno");
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        var antes = await HuellaAsync(ctx);

        var respuesta = await PagarAsync(ajeno.Admin, compra.Id, 100m, ajeno.IdMedioEfectivo);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal(antes, await HuellaAsync(ctx));
    }

    // ---- saldo pendiente expuesto en el listado y en la cuenta corriente ------------------------

    [Fact]
    public async Task ElListadoDeComprasYLaCuentaCorrienteExponenElSaldoPendienteDeCadaCompra()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeComprasYLaCuentaCorrienteExponenElSaldoPendienteDeCadaCompra));
        var parcial = await CrearCompraAsync(ctx, confirmar: true, numeroExterno: "0001-00000001");
        var saldada = await CrearCompraAsync(ctx, confirmar: true, numeroExterno: "0001-00000002", costo: 100m);
        var borrador = await CrearCompraAsync(ctx, confirmar: false, numeroExterno: "0001-00000003");
        await PagarOkAsync(ctx.Admin, parcial.Id, 500m, ctx.IdMedioEfectivo);
        await PagarOkAsync(ctx.Admin, saldada.Id, 121m, ctx.IdMedioEfectivo);

        var listado = (await ctx.Admin.GetFromJsonAsync<PaginaDeCompras>("/api/compras?tamanio=200", OpcionesJson))!;
        Assert.Equal(710m, listado.Items.Single(c => c.Id == parcial.Id).SaldoPendiente);
        Assert.Equal(0m, listado.Items.Single(c => c.Id == saldada.Id).SaldoPendiente);
        Assert.Equal(0m, listado.Items.Single(c => c.Id == borrador.Id).SaldoPendiente);

        var estado = (await ctx.Admin.GetFromJsonAsync<PaginaDeEstadoDeCuentaDeProveedor>(
            $"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente?historico=true", OpcionesJson))!;
        var filaParcial = estado.Items.Single(m => m.Tipo == TipoMovimientoCcProveedor.Compra && m.IdComprobanteCompra == parcial.Id);
        var filaSaldada = estado.Items.Single(m => m.Tipo == TipoMovimientoCcProveedor.Compra && m.IdComprobanteCompra == saldada.Id);
        Assert.Equal(710m, filaParcial.SaldoPendienteDeLaCompra);
        Assert.Equal(0m, filaSaldada.SaldoPendienteDeLaCompra);
        Assert.All(
            estado.Items.Where(m => m.Tipo != TipoMovimientoCcProveedor.Compra),
            m => Assert.Null(m.SaldoPendienteDeLaCompra));
    }

    // ---- atomicidad y reintento -----------------------------------------------------------------

    /// <summary>Se rompe el egreso de tesorería, la ÚLTIMA escritura de la transacción: el gasto, el
    /// pago de cuenta corriente y el saldo del proveedor ya estaban escritos y tienen que
    /// deshacerse. <c>Intentos == 1</c> es el valor discriminante: bajo la estrategia reintentable el
    /// segundo intento comitearía un pago aunque el primero ya hubiera dejado un commit
    /// ambiguo.</summary>
    [Fact]
    public async Task UnFalloTransitorioAlFinalDelPagoNoSeReintentaYNoDejaNadaEscrito()
    {
        var ctx = await PrepararAsync(nameof(UnFalloTransitorioAlFinalDelPagoNoSeReintentaYNoDejaNadaEscrito));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        var antes = await HuellaAsync(ctx);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("movimientos_tesoreria", SqlStateTransitorio);
        HttpResponseMessage fallida;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            fallida = await PagarAsync(ctx.Admin, compra.Id, 300m, ctx.IdMedioEfectivo);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, fallida.StatusCode);
        Assert.Equal("resultado_incierto", await CodigoDeErrorAsync(fallida));
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, await HuellaAsync(ctx));
        Assert.Equal(1210m, (await ObtenerCompraAsync(ctx.Admin, compra.Id)).SaldoPendiente);

        var reintento = await PagarOkAsync(ctx.Admin, compra.Id, 300m, ctx.IdMedioEfectivo);
        Assert.Equal(910m, reintento.SaldoPendiente);

        var despues = await HuellaAsync(ctx);
        Assert.Equal(antes.Saldo - 300m, despues.Saldo);
        Assert.Equal(antes.Gastos + 1, despues.Gastos);
        Assert.Equal(antes.MovimientosDeCc + 1, despues.MovimientosDeCc);
        Assert.Equal(antes.MovimientosDeTesoreria + 1, despues.MovimientosDeTesoreria);
    }

    // ---- concurrencia ---------------------------------------------------------------------------

    /// <summary>Pausa el INSERT del gasto de la primera request: en ese punto el pago A ya tomó el
    /// lock de la compra y leyó el saldo, y todavía no escribió nada.</summary>
    private sealed class InterceptorQuePausaElInsertDelGasto(
        TaskCompletionSource llegoAlInsert, TaskCompletionSource liberar) : DbCommandInterceptor
    {
        private int pausas;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO gastos", StringComparison.Ordinal)
                && Interlocked.Increment(ref pausas) == 1)
            {
                llegoAlInsert.TrySetResult();
                await liberar.Task;
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Dos pagos de 60 % del total sobre la misma compra: solo uno puede entrar. El pago A se
    /// pausa con el lock tomado y el saldo ya leído; el pago B arranca en ese momento. Con el lock de
    /// la compra exclusivo, B espera y al retomar ve el saldo reducido (409). Con un lock compartido,
    /// B leería el mismo saldo, comitearía antes que A y los dos pagos entrarían.</summary>
    [Fact]
    public async Task DosPagosConcurrentesDeMasDelSaldoNuncaSuperanElTotalDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(DosPagosConcurrentesDeMasDelSaldoNuncaSuperanElTotalDeLaCompra));
        var compra = await CrearCompraAsync(ctx, confirmar: true);
        const decimal importe = 726m;

        var llegoAlInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorQuePausaElInsertDelGasto(llegoAlInsert, liberar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteA = factory.CreateClient();
        var login = await clienteA.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = PagarAsync(clienteA, compra.Id, importe, ctx.IdMedioEfectivo);
        await llegoAlInsert.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var tareaB = PagarAsync(ctx.Admin, compra.Id, importe, ctx.IdMedioEfectivo);
        await Task.WhenAny(tareaB, Task.Delay(TimeSpan.FromSeconds(2)));
        liberar.TrySetResult();

        var respuestaA = await tareaA;
        var respuestaB = await tareaB;

        Assert.Equal(HttpStatusCode.Created, respuestaA.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, respuestaB.StatusCode);
        Assert.Equal("pago_excede_saldo_pendiente", await CodigoDeErrorAsync(respuestaB));

        await using var db = ContextoDelTenant(ctx);
        Assert.Equal(importe, await db.Gastos.Where(g => g.IdComprobanteCompra == compra.Id).SumAsync(g => g.Importe));
        Assert.Equal(1210m - importe, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
    }
}
