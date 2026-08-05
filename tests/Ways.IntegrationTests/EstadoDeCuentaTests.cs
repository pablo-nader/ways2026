using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente, Slice 4 (task 4.8; spec: estado-de-cuenta) — <c>GET
/// /api/clientes/{id}/cuenta-corriente</c> punta a punta: header (saldo/acuerdo/disponibilidad),
/// running balance leído de <c>saldo_resultante</c>, filtro de fecha por default/explícito/
/// histórico, scoping cross-tenant y el caso vacío. La cobertura RLS cruda de
/// <c>movimientos_cuenta_corriente</c> ya vive en <see cref="VentasStockYCuentaCorrienteRlsTests"/>
/// (matriz de las cinco tablas de stage 5) — acá el cross-tenant se prueba a nivel API (ADR-8:
/// mismo 404 para "no existe" y "es de otro tenant").
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EstadoDeCuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdListaPrecio, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idListaPrecio, admin);
    }

    private async Task<int> SembrarClienteAsync(
        Contexto ctx, string nombre, decimal saldo = 0m, decimal limiteCredito = 0m, bool creditoIlimitado = true)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = limiteCredito,
            CreditoIlimitado = creditoIlimitado, Saldo = saldo, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private async Task SembrarMovimientoAsync(
        Contexto ctx, int idCliente, TipoMovimientoCc tipo, decimal importe, decimal saldoResultante, DateTimeOffset fecha)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.MovimientosCuentaCorriente.Add(new MovimientoCuentaCorriente
        {
            IdTenant = ctx.IdTenant, IdCliente = idCliente, Fecha = fecha, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin, Tipo = tipo, IdComprobanteVenta = null, IdPagoComprobante = null,
            Importe = importe, SaldoResultante = saldoResultante, Detalle = tipo == TipoMovimientoCc.Ajuste ? "Ajuste de prueba" : null
        });
        await db.SaveChangesAsync();
    }

    private static async Task<EstadoDeCuenta> LeerEstadoDeCuentaAsync(
        Contexto ctx, int idCliente, string? query = null, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).GetAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente{query}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        return JsonSerializer.Deserialize<EstadoDeCuenta>(cuerpo, OpcionesJson)!;
    }

    // ---- Header: disponibilidad ------------------------------------------------------------------

    [Fact]
    public async Task DisponibilidadParaUnClienteConCreditoLimitado()
    {
        var ctx = await PrepararAsync(nameof(DisponibilidadParaUnClienteConCreditoLimitado));
        var idCliente = await SembrarClienteAsync(
            ctx, "Cliente credito limitado", saldo: 300m, limiteCredito: 1000m, creditoIlimitado: false);

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente);

        Assert.Equal(300m, estado.Header.Saldo);
        Assert.Equal(1000m, estado.Header.LimiteCredito);
        Assert.False(estado.Header.CreditoIlimitado);
        Assert.Equal(700m, estado.Header.Disponibilidad);
    }

    [Fact]
    public async Task DisponibilidadEsNulaCuandoElCreditoEsIlimitado()
    {
        var ctx = await PrepararAsync(nameof(DisponibilidadEsNulaCuandoElCreditoEsIlimitado));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente credito ilimitado", saldo: 300m, creditoIlimitado: true);

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente);

        Assert.True(estado.Header.CreditoIlimitado);
        Assert.Null(estado.Header.Disponibilidad);
    }

    // ---- Movement list: saldo_resultante como running balance ------------------------------------

    [Fact]
    public async Task LaListaDeMovimientosLeeElSaldoResultanteDeCadaFilaSinRederivarlo()
    {
        var ctx = await PrepararAsync(nameof(LaListaDeMovimientosLeeElSaldoResultanteDeCadaFilaSinRederivarlo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente running balance", saldo: 120m);
        var ahora = DateTimeOffset.UtcNow;

        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 100m, 100m, ahora.AddDays(-3));
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Pago, -50m, 50m, ahora.AddDays(-2));
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Ajuste, 70m, 120m, ahora.AddDays(-1));

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente, "?historico=true");

        Assert.Equal(3, estado.Movimientos.Count);
        Assert.Equal([100m, 50m, 120m], estado.Movimientos.Select(m => m.SaldoResultante).ToArray());
        // Orden ASC por fecha (spec: ordered by fecha).
        Assert.True(estado.Movimientos[0].Fecha < estado.Movimientos[1].Fecha);
        Assert.True(estado.Movimientos[1].Fecha < estado.Movimientos[2].Fecha);
    }

    // ---- Filtro de fecha: default último mes / desde-hasta / histórico ---------------------------

    [Fact]
    public async Task SinFiltroSoloDevuelveElUltimoMes()
    {
        var ctx = await PrepararAsync(nameof(SinFiltroSoloDevuelveElUltimoMes));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente dos anios", saldo: 0m);
        var ahora = DateTimeOffset.UtcNow;

        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 100m, 100m, ahora.AddYears(-2));
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 50m, 150m, ahora.AddDays(-5));

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente);

        var movimiento = Assert.Single(estado.Movimientos);
        Assert.Equal(50m, movimiento.Importe);
        Assert.False(estado.Historico);
    }

    [Fact]
    public async Task HistoricoDevuelveElLedgerCompleto()
    {
        var ctx = await PrepararAsync(nameof(HistoricoDevuelveElLedgerCompleto));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente historico", saldo: 0m);
        var ahora = DateTimeOffset.UtcNow;

        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 100m, 100m, ahora.AddYears(-2));
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 50m, 150m, ahora.AddDays(-5));

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente, "?historico=true");

        Assert.Equal(2, estado.Movimientos.Count);
        Assert.True(estado.Historico);
    }

    [Fact]
    public async Task UnDesdeHastaExplicitoAcotaLaVentana()
    {
        var ctx = await PrepararAsync(nameof(UnDesdeHastaExplicitoAcotaLaVentana));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente desde hasta", saldo: 0m);
        var ancla = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 100m, 100m, ancla.AddDays(-10));
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 50m, 150m, ancla);
        await SembrarMovimientoAsync(ctx, idCliente, TipoMovimientoCc.Consumo, 20m, 170m, ancla.AddDays(10));

        var desde = Uri.EscapeDataString(ancla.AddDays(-1).ToString("O"));
        var hasta = Uri.EscapeDataString(ancla.AddDays(1).ToString("O"));
        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente, $"?desde={desde}&hasta={hasta}");

        var movimiento = Assert.Single(estado.Movimientos);
        Assert.Equal(50m, movimiento.Importe);
    }

    // ---- Etiqueta estructural de ajuste (design decisión 8/9) -------------------------------------

    [Fact]
    public async Task LaListaEtiquetaUnAjusteManualComoDistintoDeUnaAnulacion()
    {
        var ctx = await PrepararAsync(nameof(LaListaEtiquetaUnAjusteManualComoDistintoDeUnaAnulacion));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente etiqueta ajuste", saldo: 200m);

        var respuestaAjuste = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/ajustes",
            new SolicitudDeAjuste(ctx.IdPuntoVenta, -20m, "Ajuste manual"));
        Assert.True(respuestaAjuste.IsSuccessStatusCode);

        var estado = await LeerEstadoDeCuentaAsync(ctx, idCliente, "?historico=true");

        var movimiento = Assert.Single(estado.Movimientos);
        Assert.Equal(TipoMovimientoCc.Ajuste, movimiento.Tipo);
        Assert.Equal(EtiquetaDeAjuste.Manual, movimiento.Etiqueta);
    }

    // ---- Scoping: cross-tenant y cliente nuevo -----------------------------------------------------

    [Fact]
    public async Task EstadoDeCuentaContraUnClienteDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(EstadoDeCuentaContraUnClienteDeOtroTenantDevuelve404) + "-A");
        var idClienteDeA = await SembrarClienteAsync(ctxA, "Cliente A cross-tenant estado");

        var ctxB = await PrepararAsync(nameof(EstadoDeCuentaContraUnClienteDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.GetAsync($"/api/clientes/{idClienteDeA}/cuenta-corriente");
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnClienteNuevoSinActividadDevuelveSaldoCeroYListaVaciaCon200()
    {
        var ctx = await PrepararAsync(nameof(UnClienteNuevoSinActividadDevuelveSaldoCeroYListaVaciaCon200));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente sin actividad", saldo: 0m);

        var respuesta = await ctx.Admin.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var estado = JsonSerializer.Deserialize<EstadoDeCuenta>(cuerpo, OpcionesJson)!;
        Assert.Equal(0m, estado.Header.Saldo);
        Assert.Empty(estado.Movimientos);
    }

    [Fact]
    public async Task EstadoDeCuentaSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/clientes/1/cuenta-corriente");
        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}
