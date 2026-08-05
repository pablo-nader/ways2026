using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Precios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente, Slice 4 (task 4.9 — Success Criterion; spec: consumo-cuenta-corriente
/// / Saldo Is The Maintained Cache Of The Ledger, "Saldo matches the sum across a mixed sequence").
/// Cierra la etapa: <c>Cliente.Saldo</c> tiene que igualar la suma de
/// <c>movimientos_cuenta_corriente.importe</c> del cliente en CADA paso de una secuencia que
/// combina los cuatro escritores reales (consumo vía checkout CC, pago a cuenta, ajuste manual,
/// reliquidación) más la anulación de uno de ellos — no solo al final.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SaldoLedgerInvarianteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdArea, int IdAlicuotaIva, int IdListaPrecio,
        int IdMedioEfectivo, int IdMedioCuentaCorriente, HttpClient Admin);

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
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area invariante", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, area.Id, idAlicuotaIva, idListaPrecio,
            idMedioEfectivo, medioCc.Id, admin);
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Ways.Domain.Articulos.Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = Ways.Domain.Articulos.UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
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

    private static async Task<ComprobanteEmitido> RealizarConsumoAsync(
        Contexto ctx, int idCliente, int idArticulo, decimal cantidad, decimal precio)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, cantidad * precio, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<ComprobanteEmitido> RegistrarPagoAsync(Contexto ctx, int idCliente, decimal importe)
    {
        var solicitud = new SolicitudDePagoACuenta(
            ctx.IdPuntoVenta, [new PagoDeCuenta(ctx.IdMedioEfectivo, importe, null, 0m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/clientes/{idCliente}/cuenta-corriente/pagos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task RegistrarAjusteAsync(Contexto ctx, int idCliente, decimal importe, string detalle)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/ajustes", new SolicitudDeAjuste(ctx.IdPuntoVenta, importe, detalle));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
    }

    private async Task SubirPrecioAsync(Contexto ctx, int idArticulo, decimal precioAnterior, decimal precioNuevo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = ctx.IdListaPrecio, Monto = precioNuevo,
            VigenteDesde = ahora, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        var vieja = await db.Precios
            .Where(p => p.IdArticulo == idArticulo && p.VigenteHasta == null && p.Monto == precioAnterior).SingleAsync();
        vieja.VigenteHasta = ahora;
        await db.SaveChangesAsync();
    }

    private static async Task<decimal> EjecutarReliquidacionAsync(Contexto ctx, int idCliente)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/reliquidacion", new SolicitudDeReliquidacion(ctx.IdPuntoVenta));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        return JsonSerializer.Deserialize<ResultadoDeReliquidacion>(cuerpo, OpcionesJson)!.Delta;
    }

    private static async Task<HttpResponseMessage> AnularAsync(Contexto ctx, int idComprobante) =>
        await ctx.Admin.PostAsync($"/api/ventas/{idComprobante}/anulacion", null);

    /// <summary>El invariante en sí: <c>Cliente.Saldo</c> == Σ
    /// <c>movimientos_cuenta_corriente.importe</c> del cliente, leído directo de la base (nunca de
    /// la respuesta HTTP — la respuesta se proyecta de la entidad trackeada, no prueba persistencia
    /// por sí sola, mismo criterio que el resto de la suite).</summary>
    private async Task AssertInvarianteAsync(Contexto ctx, int idCliente)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync();
        var sumaDeMovimientos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idCliente).SumAsync(m => m.Importe);
        Assert.Equal(saldo, sumaDeMovimientos);
    }

    [Fact]
    public async Task ElSaldoIgualaLaSumaDeMovimientosEnCadaPasoDeUnaSecuenciaMixta()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoIgualaLaSumaDeMovimientosEnCadaPasoDeUnaSecuenciaMixta));
        await AbrirTurnoAsync(ctx);
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-invariante", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente invariante");

        // 1. Consumo (venta CC): 2 × 100 = 200, saldo 0 → 200.
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 2m, 100m);
        await AssertInvarianteAsync(ctx, idCliente);

        // 2. Pago a cuenta (RC): 80, saldo 200 → 120.
        var rc = await RegistrarPagoAsync(ctx, idCliente, 80m);
        await AssertInvarianteAsync(ctx, idCliente);

        // 3. Ajuste manual: -20, saldo 120 → 100.
        await RegistrarAjusteAsync(ctx, idCliente, -20m, "Descuento por reclamo — invariante");
        await AssertInvarianteAsync(ctx, idCliente);

        // 4. Reliquidación: el precio sube de 100 a 150 después de la venta — delta = (150-100)×2 =
        // 100 (factor 1, financiamiento total vía CC). saldo 100 → 200.
        await SubirPrecioAsync(ctx, idArticulo, precioAnterior: 100m, precioNuevo: 150m);
        var delta = await EjecutarReliquidacionAsync(ctx, idCliente);
        Assert.Equal(100m, delta);
        await AssertInvarianteAsync(ctx, idCliente);

        // 5. Anulación de la RC del paso 2: contramovimiento +80, saldo 200 → 280.
        var respuestaAnulacion = await AnularAsync(ctx, rc.Id);
        var cuerpoAnulacion = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);
        await AssertInvarianteAsync(ctx, idCliente);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldoFinal = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync();
        Assert.Equal(280m, saldoFinal);

        var tipos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idCliente).Select(m => m.Tipo).ToListAsync();
        Assert.Contains(TipoMovimientoCc.Consumo, tipos);
        Assert.Contains(TipoMovimientoCc.Pago, tipos);
        Assert.Contains(TipoMovimientoCc.Ajuste, tipos);
        Assert.Contains(TipoMovimientoCc.ActualizacionPrecios, tipos);
        // El contramovimiento de la anulación también es `Ajuste` (stage 5) — dos filas Ajuste en
        // total: la manual del paso 3 y la de la anulación del paso 5.
        Assert.Equal(2, tipos.Count(t => t == TipoMovimientoCc.Ajuste));
    }
}
