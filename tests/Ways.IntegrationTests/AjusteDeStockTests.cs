using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 5 (tasks 5.7-5.8): <c>POST /api/stock/ajustes</c> y
/// <c>GET /api/stock</c> punta a punta — admin-only, observaciones obligatorias, y el invariante
/// <c>stock.cantidad = Σ movimientos_stock</c> tras una secuencia mixta de venta/ajuste/anulación
/// (spec: stock / Cantidad Is Always The Sum Of Its Movimientos).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AjusteDeStockTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo);

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

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Ajuste-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Ajuste", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // stage-6-turnos-caja, Slice 5 (task 5.9): checkout ahora exige un turno abierto (409
        // turno_no_abierto) — sembrado directo por EF, mismo criterio que el resto de este
        // método, en vez de un round-trip HTTP extra por cada PrepararAsync.
        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
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
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    // ---- task 5.7 ---------------------------------------------------------------------------------

    [Fact]
    public async Task AdminCargaStockInicialViaAjuste()
    {
        var ctx = await PrepararAsync(nameof(AdminCargaStockInicialViaAjuste));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-inicial", 10m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 100m, "Carga inicial de stock");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(100m, actual.Cantidad);

        var badge = await ctx.Admin.GetFromJsonAsync<StockActual>(
            $"/api/stock?idPuntoVenta={ctx.IdPuntoVenta}&idArticulo={idArticulo}", OpcionesJson);
        Assert.Equal(100m, badge!.Cantidad);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo);
        Assert.Equal(100m, movimiento.Cantidad);
        Assert.Equal(MotivoStock.Ajuste, movimiento.Motivo);
        Assert.Equal("Carga inicial de stock", movimiento.Observaciones);
    }

    [Fact]
    public async Task AdminDescargaStockConUnAjusteNegativo()
    {
        var ctx = await PrepararAsync(nameof(AdminDescargaStockConUnAjusteNegativo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-negativo", 10m);

        var carga = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 50m, "Carga inicial");
        await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", carga);

        var descarga = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, -20m, "Merma detectada en conteo físico");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", descarga);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(30m, actual.Cantidad);
    }

    [Fact]
    public async Task UnVendedorEsBloqueadoDelAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsBloqueadoDelAjuste));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-vendedor", 10m);

        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-ajuste", mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        using var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 100m, "Intento de vendedor");
        var respuesta = await vendedor.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAjusteSinObservacionesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteSinObservacionesEsRechazado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-sin-obs", 10m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 100m, "");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("observaciones_requeridas", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteConCantidadCeroEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConCantidadCeroEsRechazado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-cero", 10m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 0m, "Cantidad cero");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_de_ajuste_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteConMasDeTresDecimalesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConMasDeTresDecimalesEsRechazado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-decimales", 10m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 1.2345m, "Cantidad con demasiados decimales");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- pre-checks de FK: referencias inválidas nunca llegan como 500 --------------------------

    [Fact]
    public async Task UnAjusteConArticuloInexistenteEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConArticuloInexistenteEsRechazadoCon400));

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, 999_999, 10m, "Artículo inexistente");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteConPuntoDeVentaInexistenteEsRechazadoCon404()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConPuntoDeVentaInexistenteEsRechazadoCon404));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ajuste-pv-inexistente", 10m);

        var solicitud = new SolicitudDeAjusteDeStock(999_999, idArticulo, 10m, "Punto de venta inexistente");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteConArticuloDeOtroTenantEsRechazadoCon400()
    {
        var ctxUno = await PrepararAsync($"{nameof(UnAjusteConArticuloDeOtroTenantEsRechazadoCon400)}-uno");
        var idArticulo = await SembrarArticuloConPrecioAsync(ctxUno, "articulo-ajuste-tenant-uno", 10m);

        var ctxDos = await PrepararAsync($"{nameof(UnAjusteConArticuloDeOtroTenantEsRechazadoCon400)}-dos");

        var solicitud = new SolicitudDeAjusteDeStock(ctxDos.IdPuntoVenta, idArticulo, 10m, "Artículo de otro tenant");
        var respuesta = await ctxDos.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- task 5.8: invariante tras una secuencia mixta venta/ajuste/anulación ------------------

    [Fact]
    public async Task ElStockEsSiempreLaSumaDeSusMovimientosTrasUnaSecuenciaMixta()
    {
        // Spec: stock / Cantidad Is Always The Sum Of Its Movimientos — misma secuencia y mismo
        // resultado que el escenario de spec (venta -5, ajuste +100, venta -2, anulación +5 ⇒
        // 98), armada con los tres caminos de escritura reales de esta etapa.
        var ctx = await PrepararAsync(nameof(ElStockEsSiempreLaSumaDeSusMovimientosTrasUnaSecuenciaMixta));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-secuencia-mixta", 10m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Secuencia Mixta");

        var solicitudVenta1 = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 5m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 0m)],
            null, null);
        var respuestaVenta1 = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudVenta1);
        Assert.Equal(HttpStatusCode.Created, respuestaVenta1.StatusCode);
        var venta1 = (await respuestaVenta1.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var ajuste = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 100m, "Carga de mercadería recibida");
        var respuestaAjuste = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", ajuste);
        Assert.Equal(HttpStatusCode.OK, respuestaAjuste.StatusCode);

        var solicitudVenta2 = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 2m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 20m, null, 0m)],
            null, null);
        var respuestaVenta2 = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudVenta2);
        Assert.Equal(HttpStatusCode.Created, respuestaVenta2.StatusCode);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{venta1.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, respuestaAnulacion.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(m => m.Cantidad);

        Assert.Equal(98m, cantidad);
        Assert.Equal(98m, sumaDeMovimientos);
        Assert.Equal(cantidad, sumaDeMovimientos);
    }
}
