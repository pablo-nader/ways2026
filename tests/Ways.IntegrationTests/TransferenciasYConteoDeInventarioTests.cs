using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 3 (tasks 3.5-3.14): <c>POST
/// /api/stock/transferencias</c> y <c>POST /api/stock/conteos</c> punta a punta — la transacción
/// de dos filas espejadas de una transferencia, la asimetría contra el checkout (spec:
/// transferencias-de-stock / Insufficient Origin Stock Is Refused), el conteo con delta derivado
/// del servidor bajo lock (spec: conteo-de-inventario), y el invariante de suma extendido a los
/// dos motivos nuevos de esta slice.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TransferenciasYConteoDeInventarioTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVentaOrigen, int IdPuntoVentaDestino, HttpClient Admin, int IdArea,
        int IdAlicuotaIva, int IdListaPrecio, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Transferencia-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Transferencia", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Segundo punto de venta REAL de la misma empresa (mismo criterio que
        // ParametrosTests.SembrarTenantConAdminAsync) — una transferencia necesita origen Y
        // destino reales.
        var puntoVentaDestino = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVentaDestino);
        await db.SaveChangesAsync();

        // stage-6-turnos-caja: el checkout exige un turno abierto en el punto de venta de origen
        // (mismo criterio que AjusteDeStockTests.PrepararAsync) — necesario para el escenario de
        // asimetría venta-vs-transferencia y para la carrera transferencia × checkout.
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, puntoVentaDestino.Id, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal);
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

    private static async Task<decimal> CargarStockInicialAsync(Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad)
    {
        var solicitud = new SolicitudDeAjusteDeStock(idPuntoVenta, idArticulo, cantidad, "Carga inicial de prueba");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!.Cantidad;
    }

    /// <summary>Simula una venta directa del ledger, sin pasar por el checkout completo (mismo
    /// criterio que <c>ComprasAnulacionYConcurrenciaTests.ReducirStockComoVentaAsync</c>).</summary>
    private async Task ReducirStockComoVentaAsync(Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = idPuntoVenta, Cantidad = -cantidad,
            Motivo = MotivoStock.Venta, IdEmpleado = idEmpleado, CreadoEl = ahora
        });
        await db.SaveChangesAsync();

        var stock = await db.Stock.FirstAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta);
        stock.Cantidad -= cantidad;
        await db.SaveChangesAsync();
    }

    private static SolicitudDeTransferencia SolicitudDeUnaLinea(
        Contexto ctx, int idArticulo, decimal cantidad, string observaciones = "Reposición de local") =>
        new(ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, observaciones, [new LineaDeTransferencia(idArticulo, cantidad)]);

    // ---- task 3.5: transferencia atómica de dos filas espejadas -----------------------------------

    [Fact]
    public async Task UnaTransferenciaDeUnSoloItemMueveAmbosCachesAtomicamente()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaDeUnSoloItemMueveAmbosCachesAtomicamente));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-transferencia-simple", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, 5m);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<ResultadoTransferencia>(cuerpo, OpcionesJson)!;
        var linea = Assert.Single(resultado.Lineas);
        Assert.Equal(12m, linea.CantidadOrigen);
        Assert.Equal(13m, linea.CantidadDestino);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidadOrigen = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        var cantidadDestino = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaDestino)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(12m, cantidadOrigen);
        Assert.Equal(13m, cantidadDestino);

        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.Contains(movimientos, m => m.IdPuntoVenta == ctx.IdPuntoVentaOrigen && m.Cantidad == -8m && m.IdPuntoVentaDestino == ctx.IdPuntoVentaDestino);
        Assert.Contains(movimientos, m => m.IdPuntoVenta == ctx.IdPuntoVentaDestino && m.Cantidad == 8m && m.IdPuntoVentaDestino == ctx.IdPuntoVentaDestino);
    }

    [Fact]
    public async Task UnaTransferenciaDeVariosItemsEscribeExactamente2NFilasAtomicamente()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaDeVariosItemsEscribeExactamente2NFilasAtomicamente));
        var articulo1 = await SembrarArticuloConPrecioAsync(ctx, "articulo-multi-1", 10m);
        var articulo2 = await SembrarArticuloConPrecioAsync(ctx, "articulo-multi-2", 10m);
        var articulo3 = await SembrarArticuloConPrecioAsync(ctx, "articulo-multi-3", 10m);

        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, articulo1, 30m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, articulo2, 30m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, articulo3, 30m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Reposición de tres artículos",
            [
                new LineaDeTransferencia(articulo1, 5m),
                new LineaDeTransferencia(articulo2, 7m),
                new LineaDeTransferencia(articulo3, 9m)
            ]);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var totalMovimientos = await db.MovimientosStock.CountAsync(
            m => m.Motivo == MotivoStock.Transferencia
                 && (m.IdArticulo == articulo1 || m.IdArticulo == articulo2 || m.IdArticulo == articulo3));
        Assert.Equal(6, totalMovimientos);
    }

    // ---- task 3.5 / 3.6: atomicidad forzada por punto de falla -------------------------------------

    private async Task RevocarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"REVOKE {privilegios} ON {tabla} FROM {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task RestaurarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"GRANT {privilegios} ON {tabla} TO {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task UnaFallaEnElSegundoLadoDeLaTransferenciaNoMueveNingunLado()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElSegundoLadoDeLaTransferenciaNoMueveNingunLado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-falla-transferencia", 10m);

        // Ambas filas de stock ya existen (ON CONFLICT DO UPDATE en las dos, así que revocar
        // UPDATE sobre "stock" garantiza la falla sin importar el orden asc (id_articulo,
        // id_punto_venta) que decida cuál de las dos claves procesa primero.
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, 5m);

        await RevocarAsync("stock", "UPDATE");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m));
        }
        finally
        {
            await RestaurarAsync("stock", "UPDATE");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));

        var cantidadOrigen = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        var cantidadDestino = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaDestino)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(20m, cantidadOrigen);
        Assert.Equal(5m, cantidadDestino);
    }

    // ---- task 3.6: asimetría back-office vs. checkout ----------------------------------------------

    [Fact]
    public async Task UnaTransferenciaQueDejariaElOrigenNegativoEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaQueDejariaElOrigenNegativoEsRechazada));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-transferencia-insuficiente", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 5m);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_insuficiente_para_transferencia", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));

        var cantidadOrigen = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(5m, cantidadOrigen);
    }

    [Fact]
    public async Task UnaVentaDelMismoArticuloSigueYendoAlNegativoLaAsimetriaEsRealEnAmbasDirecciones()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaDelMismoArticuloSigueYendoAlNegativoLaAsimetriaEsRealEnAmbasDirecciones));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-asimetria", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 5m);

        var transferencia = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m));
        Assert.Equal(HttpStatusCode.Conflict, transferencia.StatusCode);

        // La venta del mismo artículo, en el mismo punto de venta, SÍ va al negativo — la
        // asimetría declarada por el spec (counter operations never block on stock).
        await ReducirStockComoVentaAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 8m);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(-3m, cantidad);
    }

    // ---- task 3.7: origen ≠ destino, cross-tenant destino --------------------------------------

    [Fact]
    public async Task UnaTransferenciaConOrigenIgualADestinoEsRechazadaAntesDeCualquierEscritura()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaConOrigenIgualADestinoEsRechazadaAntesDeCualquierEscritura));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-origen-igual-destino", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaOrigen, "Intento inválido", [new LineaDeTransferencia(idArticulo, 5m)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transferencia_origen_igual_destino", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
    }

    [Fact]
    public async Task UnDestinoDeOtroTenantEsRechazadoComoReferenciaInvalida()
    {
        var ctxUno = await PrepararAsync($"{nameof(UnDestinoDeOtroTenantEsRechazadoComoReferenciaInvalida)}-uno");
        var idArticulo = await SembrarArticuloConPrecioAsync(ctxUno, "articulo-destino-otro-tenant", 10m);
        await CargarStockInicialAsync(ctxUno, ctxUno.IdPuntoVentaOrigen, idArticulo, 20m);

        var ctxDos = await PrepararAsync($"{nameof(UnDestinoDeOtroTenantEsRechazadoComoReferenciaInvalida)}-dos");

        var solicitud = new SolicitudDeTransferencia(
            ctxUno.IdPuntoVentaOrigen, ctxDos.IdPuntoVentaOrigen, "Intento cross-tenant",
            [new LineaDeTransferencia(idArticulo, 5m)]);
        var respuesta = await ctxUno.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctxUno.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
    }

    // ---- articulo repetido (design decisión 9) ------------------------------------------------

    [Fact]
    public async Task UnArticuloRepetidoEnUnaMismaTransferenciaEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloRepetidoEnUnaMismaTransferenciaEsRechazado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-repetido", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Dos líneas del mismo artículo",
            [new LineaDeTransferencia(idArticulo, 3m), new LineaDeTransferencia(idArticulo, 2m)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_repetido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.8: conteo, delta con signo derivado del servidor -----------------------------------

    [Fact]
    public async Task UnConteoPorEncimaDelCacheProduceUnMovimientoPositivo()
    {
        var ctx = await PrepararAsync(nameof(UnConteoPorEncimaDelCacheProduceUnMovimientoPositivo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-conteo-positivo", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 45m, "Recuento físico semanal");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(45m, actual.Cantidad);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario);
        Assert.Equal(5m, movimiento.Cantidad);
        Assert.Equal("Recuento físico semanal", movimiento.Observaciones);
    }

    [Fact]
    public async Task UnConteoPorDebajoDelCacheProduceUnMovimientoNegativo()
    {
        var ctx = await PrepararAsync(nameof(UnConteoPorDebajoDelCacheProduceUnMovimientoNegativo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-conteo-negativo", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 33m, "Merma detectada en recuento");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(33m, actual.Cantidad);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario);
        Assert.Equal(-7m, movimiento.Cantidad);
    }

    /// <summary>spec: conteo-de-inventario / Conteo Input Is The Counted Total, Never A Delta —
    /// "the conteo request contract carries only cantidad_contada, never a delta/ajuste field".
    /// <see cref="SolicitudDeConteo"/> es el contrato real; esta prueba lo inspecciona por
    /// reflexión en vez de confiar en una lectura visual del archivo.</summary>
    [Fact]
    public void ElContratoDeConteoNuncaExponeUnCampoDeDeltaOAjuste()
    {
        var propiedades = typeof(SolicitudDeConteo).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("Contada", propiedades);
        Assert.DoesNotContain(propiedades, nombre => nombre.Contains("Delta", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propiedades, nombre => nombre.Contains("Ajuste", StringComparison.OrdinalIgnoreCase));
    }

    // ---- task 3.9: conteo sin diferencia, no-op ----------------------------------------------------

    [Fact]
    public async Task UnConteoQueCoincideConElCacheNoEscribeNadaYDevuelve200()
    {
        var ctx = await PrepararAsync(nameof(UnConteoQueCoincideConElCacheNoEscribeNadaYDevuelve200));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-conteo-sin-diferencia", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 40m, "Recuento sin diferencias");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(40m, actual.Cantidad);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(40m, cantidad);
    }

    // ---- task 3.10: observaciones obligatorias, motivo distinto de ajuste --------------------------

    [Fact]
    public async Task UnConteoSinObservacionesEsRechazadoAntesDeLaBaseDeDatos()
    {
        var ctx = await PrepararAsync(nameof(UnConteoSinObservacionesEsRechazadoAntesDeLaBaseDeDatos));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-conteo-sin-obs", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 45m, "");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("observaciones_requeridas", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
    }

    [Fact]
    public async Task MotivoInventarioNuncaSaleDeAjustesYMotivoAjusteNuncaSaleDeConteos()
    {
        var ctx = await PrepararAsync(nameof(MotivoInventarioNuncaSaleDeAjustesYMotivoAjusteNuncaSaleDeConteos));
        var idArticuloAjuste = await SembrarArticuloConPrecioAsync(ctx, "articulo-solo-ajuste", 10m);
        var idArticuloConteo = await SembrarArticuloConPrecioAsync(ctx, "articulo-solo-conteo", 10m);

        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloAjuste, 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloConteo, 10m);

        var conteo = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticuloConteo, 15m, "Recuento físico");
        var respuestaConteo = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", conteo);
        Assert.Equal(HttpStatusCode.OK, respuestaConteo.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloAjuste && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloAjuste && m.Motivo == MotivoStock.Ajuste));

        // idArticuloConteo tiene UN ajuste (la carga inicial vía CargarStockInicialAsync, que
        // usa /api/stock/ajustes) y UN inventario (el conteo de abajo) — el conteo mismo nunca
        // produce una fila adicional con motivo = ajuste.
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloConteo && m.Motivo == MotivoStock.Ajuste));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloConteo && m.Motivo == MotivoStock.Inventario));
    }

    // ---- task 3.13: superficie de autorización ------------------------------------------------

    [Fact]
    public async Task UnAdminPuedeTransferirYContar()
    {
        var ctx = await PrepararAsync(nameof(UnAdminPuedeTransferirYContar));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-admin-ok", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var transferencia = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 5m));
        Assert.Equal(HttpStatusCode.OK, transferencia.StatusCode);

        var conteo = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 20m, "Recuento admin");
        var respuestaConteo = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", conteo);
        Assert.Equal(HttpStatusCode.OK, respuestaConteo.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsBloqueadoDeTransferenciaYConteo()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsBloqueadoDeTransferenciaYConteo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-vendedor-bloqueado", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-transferencia", mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        using var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var transferencia = await vendedor.PostAsJsonAsync("/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 5m));
        Assert.Equal(HttpStatusCode.Forbidden, transferencia.StatusCode);

        var conteo = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 25m, "Intento de vendedor");
        var respuestaConteo = await vendedor.PostAsJsonAsync("/api/stock/conteos", conteo);
        Assert.Equal(HttpStatusCode.Forbidden, respuestaConteo.StatusCode);
    }

    // ---- task 3.12: invariante de suma extendido a transferencia + inventario ----------------------

    private async Task<(int IdProveedor, int IdTipoCFA)> SembrarProveedorYTipoAsync(Contexto ctx, string nombre)
    {
        // condiciones_fiscales es una tabla de plataforma (mismo criterio que
        // ComprasAnulacionYConcurrenciaTests.PrepararAsync) — el contexto de tenant no la puede
        // escribir bajo RLS.
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return (proveedor.Id, idTipoCFA);
    }

    [Fact]
    public async Task ElInvarianteDeSumaSeMantienePorPuntoDeVentaTrasUnaSecuenciaMixtaConTransferenciaEInventario()
    {
        var ctx = await PrepararAsync(nameof(ElInvarianteDeSumaSeMantienePorPuntoDeVentaTrasUnaSecuenciaMixtaConTransferenciaEInventario));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-secuencia-extendida", 10m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Secuencia Extendida");
        var (idProveedor, idTipoCFA) = await SembrarProveedorYTipoAsync(ctx, nameof(ElInvarianteDeSumaSeMantienePorPuntoDeVentaTrasUnaSecuenciaMixtaConTransferenciaEInventario));

        // 1. compra confirmada: +30 en origen.
        var solicitudCompra = new SolicitudDeCompra(
            idProveedor, idTipoCFA, ctx.IdPuntoVentaOrigen, "0001-00000099", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(idArticulo, "Item de prueba", 30m, null, null, 100m, 0m, ctx.IdAlicuotaIva, true)]);
        var respuestaBorrador = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitudCompra);
        var cuerpoBorrador = await respuestaBorrador.Content.ReadAsStringAsync();
        Assert.True(respuestaBorrador.StatusCode == HttpStatusCode.Created, cuerpoBorrador);
        var borrador = JsonSerializer.Deserialize<CompraDetalle>(cuerpoBorrador, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{borrador.Id}/confirmar", null);
        Assert.Equal(HttpStatusCode.OK, respuestaConfirmar.StatusCode);

        // 2. venta: -5 en origen.
        var solicitudVenta = new SolicitudDeVenta(
            ctx.IdPuntoVentaOrigen, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 5m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 0m)],
            null, null);
        var respuestaVenta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudVenta);
        Assert.Equal(HttpStatusCode.Created, respuestaVenta.StatusCode);

        // 3. ajuste: +50 en origen.
        var ajuste = new SolicitudDeAjusteDeStock(ctx.IdPuntoVentaOrigen, idArticulo, 50m, "Carga adicional");
        var respuestaAjuste = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", ajuste);
        Assert.Equal(HttpStatusCode.OK, respuestaAjuste.StatusCode);

        // 4. transferencia: -20 en origen, +20 en destino. Origen antes: 30-5+50=75 ⇒ 55.
        var respuestaTransferencia = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 20m));
        Assert.Equal(HttpStatusCode.OK, respuestaTransferencia.StatusCode);

        // 5. conteo en destino: cache=20, contada=25 ⇒ +5 en destino.
        var conteo = new SolicitudDeConteo(ctx.IdPuntoVentaDestino, idArticulo, 25m, "Recuento del destino");
        var respuestaConteo = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", conteo);
        Assert.Equal(HttpStatusCode.OK, respuestaConteo.StatusCode);

        // 6. anulación de la compra: -30 en origen (reversa el movimiento original). Origen: 55-30=25.
        var respuestaAnular = await ctx.Admin.PostAsync($"/api/compras/{borrador.Id}/anular", null);
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.OK, cuerpoAnular);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidadOrigen = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaOrigen = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(25m, cantidadOrigen);
        Assert.Equal(cantidadOrigen, sumaOrigen);

        var cantidadDestino = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaDestino)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaDestino = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaDestino)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(25m, cantidadDestino);
        Assert.Equal(cantidadDestino, sumaDestino);
    }

    // ---- task 3.11: superficies racy, forced rendezvous --------------------------------------------

    /// <summary>Pausa la transacción manual justo DESPUÉS de <c>BeginTransactionAsync</c> — mismo
    /// patrón que <c>ComprasAnulacionYConcurrenciaTests.InterceptorDePausaTrasIniciarLaTransaccion</c>
    /// — hasta que el test la libera. Usada acá solo en el cliente de la TRANSFERENCIA (un
    /// segundo <c>WebApplicationFactory</c>), nunca en el del checkout (<c>ctx.Admin</c>, sin
    /// interceptor), así que solo la transferencia se detiene.</summary>
    private sealed class InterceptorDePausaTrasIniciarLaTransaccion(
        TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            transaccionIniciada.TrySetResult();
            await puedeContinuar.Task;
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Design: Backstop Map — superficie racy 3, "transferencia × checkout on the same
    /// (articulo, pv)". La transferencia arranca su transacción y se PAUSA antes de tocar el
    /// lock de fila de <c>stock</c>; mientras está pausada, una venta directa del mismo artículo
    /// en el mismo punto de venta de origen COMMITEA. Al reanudar, la transferencia ve el stock
    /// YA actualizado por la venta bajo el mismo row lock — cualquiera de los dos resultados
    /// (200 o 409) es representable, lo único que no lo es es una escritura corrupta.</summary>
    [Fact]
    public async Task TransferenciaYCheckoutConcurrentesSobreElMismoArticuloNuncaCorrompenElStock()
    {
        var ctx = await PrepararAsync(nameof(TransferenciaYCheckoutConcurrentesSobreElMismoArticuloNuncaCorrompenElStock));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-race-transferencia-checkout", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 10m);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteTransferencia = factory.CreateClient();
        var login = await clienteTransferencia.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaTransferencia = clienteTransferencia.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 7m));

        await transaccionIniciada.Task;

        // El checkout gana la carrera: reduce el origen a 4 (venta directa del ledger) y
        // COMMITEA antes de que la transferencia retome su transacción.
        await ReducirStockComoVentaAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 6m);

        puedeContinuar.TrySetResult();

        var respuestaTransferencia = await tareaTransferencia;

        // La transferencia de 7 unidades ya no alcanza (origen quedó en 4) — se rechaza, nunca
        // un 500 ni una escritura corrupta.
        Assert.True(
            respuestaTransferencia.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"transferencia: {respuestaTransferencia.StatusCode}");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidadOrigen = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaOrigen = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(cantidadOrigen, sumaOrigen);

        if (respuestaTransferencia.StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Equal(4m, cantidadOrigen);
        }
    }

    /// <summary>Retiene N participantes en <c>TransactionStarted</c> hasta que todos llegaron —
    /// generaliza <c>InterceptorDePausaTrasIniciarLaTransaccion</c> (un único par) a un
    /// <see cref="CountdownEvent"/> de <paramref name="gate"/> participantes, mismo criterio de
    /// forced rendezvous que <c>ParametrosTests.InterceptorDeRendezVous</c> pero enganchado al
    /// ciclo de vida de la transacción.</summary>
    private sealed class InterceptorDeRendezVousDeTransaccion(CountdownEvent gate) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            if (!gate.IsSet)
            {
                gate.Signal();
                var senializo = gate.Wait(TimeSpan.FromSeconds(10));
                Assert.True(senializo, "El rendezvous de InterceptorDeRendezVousDeTransaccion no llegó a los participantes a tiempo.");
            }

            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>spec: conteo-de-inventario / Concurrent Conteos — dos conteos concurrentes del
    /// mismo artículo serializan en el row lock del upsert no-op (<c>BloquearYCrearSiFaltaStockAsync</c>):
    /// el que corre SEGUNDO ve el <c>cantidad</c> ya comiteado por el primero bajo READ COMMITTED,
    /// así que <c>delta = contada − actual</c> siempre converge al <c>contada</c> del último en
    /// ejecutar — nunca una pérdida de escritura (los dos movimientos quedan registrados).</summary>
    [Fact]
    public async Task DosConteosConcurrentesDelMismoArticuloSerializanSinPerderNingunaEscritura()
    {
        var ctx = await PrepararAsync(nameof(DosConteosConcurrentesDelMismoArticuloSerializanSinPerderNingunaEscritura));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-race-conteo", 10m);
        await CargarStockInicialAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 40m);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousDeTransaccion(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var conteoA = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 45m, "Conteo A");
        var conteoB = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, 50m, "Conteo B");

        var tareaA = cliente.PostAsJsonAsync("/api/stock/conteos", conteoA);
        var tareaB = cliente.PostAsJsonAsync("/api/stock/conteos", conteoB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidadFinal = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(s => s.Cantidad).FirstAsync();

        // El resultado final siempre coincide con el "contada" de quien ejecutó SEGUNDO bajo el
        // lock — nunca 40 (ninguno aplicado) ni una suma corrupta (95).
        Assert.True(cantidadFinal is 45m or 50m, $"cantidadFinal={cantidadFinal}");

        Assert.Equal(
            2, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));

        // Invariante general (spec: stock / Cantidad Is Always The Sum Of Its Movimientos): la
        // suma de TODOS los movimientos (la carga inicial vía ajuste + los dos conteos) coincide
        // con el caché — el telescoping de los dos deltas hace que el resultado converja al
        // "contada" del último en ejecutar sin perder ninguna escritura.
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(cantidadFinal, sumaDeMovimientos);
    }
}
