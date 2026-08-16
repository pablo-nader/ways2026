using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 4 (tasks 4.9-4.14): <c>stock.ajuste</c>/
/// <c>stock.decomiso</c>/<c>stock.conteo</c> punta a punta contra Postgres real — before-image
/// derivado del <c>RETURNING</c> autoritativo (nunca un segundo <c>SELECT</c>, design decisión 9),
/// el conteo sin diferencia mudo (ledger Y auditoría, tasks.md Orchestrator Decision #1), el
/// escenario reconciliado de conteo por lote (UNA fila por operación, no por lote) y el límite
/// estructural de <c>stock.transferencia</c> (proposal decisión 5).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class StockAuditoriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas fijas y lejanas — independientes del reloj de la corrida.
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleadoAdmin, HttpClient Admin, int IdArea,
        int IdAlicuotaIva, int IdListaPrecio);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Auditoria-stock-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Auditoria Stock", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        // Módulo de lotes ON a nivel empresa — mismo criterio que AjusteDecomisoLoteTests/ConteoPorLoteTests.
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, area.Id,
            idAlicuotaIva, lista.Id);
    }

    private async Task<int> SembrarArticuloLoteEfectivoAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = true, CreatedAt = ahora, UpdatedAt = ahora
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

    private async Task<int> SembrarArticuloSinLoteAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = false, CreatedAt = ahora, UpdatedAt = ahora
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

    private async Task<int> SembrarLoteAsync(Contexto ctx, int idArticulo, string codigo, DateOnly? fechaVencimiento)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        return lote.Id;
    }

    private async Task SembrarStockLoteAsync(Contexto ctx, int idArticulo, int idLote, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    /// <summary>Judgment-day fix (juez B, slice 4, ronda 1, finding 2): sin este quemado previo,
    /// <c>id_articulo</c> e <c>id_movimiento_stock</c> coinciden por alineación accidental de las
    /// secuencias en el entorno de test — mutar el call site de auditoría a
    /// <c>idMovimientoStock</c> en vez de <c>idArticulo</c> sobrevive porque ambos valores son
    /// iguales por casualidad. Regla permanente (magnitudes discriminantes): ningún id usado por
    /// una aserción de auditoría (articulo/punto de venta/actor/entidad/movimiento/cliente) puede
    /// coincidir con otro por casualidad — se desincroniza a propósito quemando filas descartables
    /// ANTES de sembrar la entidad real que el test audita.</summary>
    private async Task QuemarArticulosDescartablesAsync(Contexto ctx, int cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        for (var i = 0; i < cantidad; i++)
        {
            db.Articulos.Add(new Articulo
            {
                IdTenant = ctx.IdTenant, CodigoInterno = $"quemado-{Guid.NewGuid():N}", Nombre = "quemado",
                IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
                EsProducto = true, ControlaLote = false, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
    }

    // ---- task 4.9 / mutation target 4.6 (design mutation-targets, slice 4, fila 1) ---------------

    /// <summary>spec `auditoria-de-operaciones`: catálogo de las doce acciones, cobertura de
    /// `stock.ajuste`. Mutation target (slice 4, fila 1): mutar el before-image de
    /// `nuevaCantidad - cantidad` a `nuevaCantidad` en los dos lados hace fallar el
    /// <c>Assert.NotEqual</c> de acá.</summary>
    [Fact]
    public async Task UnAjusteDeStockEscribeUnaFilaDeAuditoriaConAnteriorDistintoDeNuevo()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteDeStockEscribeUnaFilaDeAuditoriaConAnteriorDistintoDeNuevo));
        await QuemarArticulosDescartablesAsync(ctx, 2);
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-auditoria-ajuste", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 50m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 20m, "Carga adicional auditada");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "stock.ajuste" && a.IdEntidad == idArticulo);

        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Equal("articulo", fila.Entidad);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdActor);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(50m, anterior.GetProperty("cantidad").GetDecimal());
        Assert.Equal(70m, nuevo.GetProperty("cantidad").GetDecimal());
        Assert.NotEqual(anterior.GetProperty("cantidad").GetDecimal(), nuevo.GetProperty("cantidad").GetDecimal());
        Assert.Equal("Carga adicional auditada", nuevo.GetProperty("observaciones").GetString());
        Assert.True(nuevo.TryGetProperty("id_movimiento_stock", out var idMovimiento));

        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Ajuste);
        Assert.Equal(movimiento.Id, idMovimiento.GetInt32());
    }

    // ---- task 4.12: stock.decomiso coverage --------------------------------------------------------

    [Fact]
    public async Task UnDecomisoDeStockEscribeUnaFilaConIdLotePresenteCuandoEsLoteEfectivo()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeStockEscribeUnaFilaConIdLotePresenteCuandoEsLoteEfectivo));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-auditoria-lote", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-DECOMISO-AUDITORIA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Rotura auditada");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "stock.decomiso" && a.IdEntidad == idArticulo);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(40m, anterior.GetProperty("cantidad").GetDecimal());
        Assert.Equal(35m, nuevo.GetProperty("cantidad").GetDecimal());
        Assert.Equal(idLote, nuevo.GetProperty("id_lote").GetInt32());
        Assert.Equal("Rotura auditada", nuevo.GetProperty("observaciones").GetString());

        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso);
        Assert.Equal(movimiento.Id, nuevo.GetProperty("id_movimiento_stock").GetInt32());
    }

    [Fact]
    public async Task UnDecomisoDeStockEscribeUnaFilaConIdLoteNuloCuandoNoEsLoteEfectivo()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeStockEscribeUnaFilaConIdLoteNuloCuandoNoEsLoteEfectivo));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-decomiso-auditoria-sin-lote", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, null, 5m, "Rotura sin lote auditada");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "stock.decomiso" && a.IdEntidad == idArticulo);

        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(JsonValueKind.Null, nuevo.GetProperty("id_lote").ValueKind);
        Assert.Equal("Rotura sin lote auditada", nuevo.GetProperty("observaciones").GetString());

        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso);
        Assert.Equal(movimiento.Id, nuevo.GetProperty("id_movimiento_stock").GetInt32());
    }

    /// <summary>Los dos caminos de rechazo <c>409 stock_insuficiente_para_decomiso</c> (lote y
    /// agregado) dejan cero filas de auditoría — el rechazo corre ANTES del
    /// <c>RegistrarAsync</c>.</summary>
    [Fact]
    public async Task UnDecomisoRechazadoPorStockInsuficienteEnElLoteNoEscribeFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoRechazadoPorStockInsuficienteEnElLoteNoEscribeFilaDeAuditoria));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-auditoria-insuf-lote", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-INSUF-AUDITORIA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 3m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 3m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Rotura mayor al saldo del lote");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "stock.decomiso" && a.IdEntidad == idArticulo));
    }

    [Fact]
    public async Task UnDecomisoRechazadoPorStockInsuficienteEnElAgregadoNoEscribeFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoRechazadoPorStockInsuficienteEnElAgregadoNoEscribeFilaDeAuditoria));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-decomiso-auditoria-insuf-agregado", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 3m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, null, 5m, "Rotura mayor al saldo agregado");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "stock.decomiso" && a.IdEntidad == idArticulo));
    }

    // ---- task 4.10 / mutation target 4.7 (design mutation-targets, slice 4, fila 2) ---------------

    /// <summary>spec `auditoria-de-operaciones`: "A zero-difference conteo writes no audit row".
    /// Mutation target (slice 4, fila 2): borrar el early-return de delta cero hace que este test
    /// falle (aparecería una fila de ledger Y una de auditoría).</summary>
    [Fact]
    public async Task UnConteoAgregadoSinDiferenciaNoEscribeFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnConteoAgregadoSinDiferenciaNoEscribeFilaDeAuditoria));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-conteo-auditoria-sin-diferencia", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, 40m, "Conteo sin diferencia auditado");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "stock.conteo" && a.IdEntidad == idArticulo));
    }

    /// <summary>Judgment-day fix (juez B, slice 4, ronda 1, finding 1): el conteo AGREGADO con
    /// diferencia (delta ≠ 0, <c>EjecutarConteoAsync</c>) no tenía ningún test — hardcodear
    /// <c>delta_total = 0</c> en el payload, o borrar el bloque <c>RegistrarAsync</c> entero,
    /// sobrevivían los 66/66 tests previos (solo estaban cubiertos el camino sin diferencia y el
    /// camino por-lote). Magnitudes discriminantes (88/61/-27, delta negativo — complementa el
    /// +3 del escenario por-lote) y secuencias desincronizadas
    /// (<see cref="QuemarArticulosDescartablesAsync"/>) — verifica el payload clave por clave.</summary>
    [Fact]
    public async Task UnConteoAgregadoConDiferenciaEscribeUnaFilaDeAuditoriaConPayloadCompleto()
    {
        var ctx = await PrepararAsync(nameof(UnConteoAgregadoConDiferenciaEscribeUnaFilaDeAuditoriaConPayloadCompleto));
        await QuemarArticulosDescartablesAsync(ctx, 2);
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-conteo-auditoria-agregado-con-diferencia", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 88m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, 61m, "Conteo agregado con diferencia auditado");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario);

        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "stock.conteo" && a.IdEntidad == idArticulo);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Equal("articulo", fila.Entidad);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdActor);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(88m, anterior.GetProperty("cantidad").GetDecimal());
        Assert.Equal(61m, nuevo.GetProperty("cantidad").GetDecimal());
        Assert.Equal(-27m, nuevo.GetProperty("delta_total").GetDecimal());
        Assert.Equal(0, nuevo.GetProperty("lotes_afectados").GetInt32());

        var idsGenerados = nuevo.GetProperty("movimientos_generados").EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Equal([movimiento.Id], idsGenerados);
    }

    // ---- task 4.11: el escenario reconciliado (tasks.md Orchestrator Decision #1) -----------------

    /// <summary>spec `auditoria-de-operaciones`: "Each operation MUST write exactly one row" — el
    /// test discriminante que reemplaza el texto per-lote obsoleto de design.md (call-site row 11):
    /// un conteo por lote sobre un artículo con 3 lotes, 2 con diferencia, escribe UNA sola fila de
    /// auditoría para la operación entera — no dos.</summary>
    [Fact]
    public async Task UnConteoPorLoteConDosDeTresLotesDiferentesEscribeUnaSolaFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnConteoPorLoteConDosDeTresLotesDiferentesEscribeUnaSolaFilaDeAuditoria));
        await QuemarArticulosDescartablesAsync(ctx, 2);
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-auditoria-reconciliado", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L1-CONTEO-AUDITORIA", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L2-CONTEO-AUDITORIA", VencimientoLejanoFuturo);
        var idLote3 = await SembrarLoteAsync(ctx, idArticulo, "L3-CONTEO-AUDITORIA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote1, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote2, 20m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote3, 5m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 35m);

        // L1 (10→15, +5) y L3 (5→3, -2) difieren; L2 (20→20) coincide — el escenario reconciliado
        // exacto de tasks.md 4.11. Delta neto +3, deliberadamente no-cero para que anterior/nuevo
        // del agregado también discriminen.
        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Conteo con dos de tres lotes con diferencia",
            [new ConteoDeLote(idLote1, 15m), new ConteoDeLote(idLote2, 20m), new ConteoDeLote(idLote3, 3m)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimientosDelLedger = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario).ToListAsync();
        Assert.Equal(2, movimientosDelLedger.Count);

        var filas = await db.Auditoria.Where(a => a.Accion == "stock.conteo" && a.IdEntidad == idArticulo).ToListAsync();
        var fila = Assert.Single(filas);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(35m, anterior.GetProperty("cantidad").GetDecimal());
        Assert.Equal(38m, nuevo.GetProperty("cantidad").GetDecimal());
        Assert.Equal(2, nuevo.GetProperty("lotes_afectados").GetInt32());
        Assert.Equal(3m, nuevo.GetProperty("delta_total").GetDecimal());

        var idsGenerados = nuevo.GetProperty("movimientos_generados").EnumerateArray().Select(e => e.GetInt32()).OrderBy(id => id).ToList();
        var idsDelLedger = movimientosDelLedger.Select(m => m.Id).OrderBy(id => id).ToList();
        Assert.Equal(idsDelLedger, idsGenerados);
    }

    // ---- task 4.14: stock.transferencia — límite estructural (proposal decisión 5) ----------------

    /// <summary>spec `auditoria-de-operaciones`: "stock.transferencia is excluded by scope, not by
    /// defect" — ninguna de las dos patas escribe una fila de auditoría, y ambas siguen cargando su
    /// propio <c>id_empleado</c> en <c>movimientos_stock</c>.</summary>
    [Fact]
    public async Task UnaTransferenciaNoEscribeFilasDeAuditoriaParaNingunaDeLasDosPatas()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaNoEscribeFilasDeAuditoriaParaNingunaDeLasDosPatas));
        var idPuntoVentaDestino = await SembrarPuntoVentaAsync(ctx, "Destino transferencia auditoria");
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-transferencia-auditoria", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVenta, idPuntoVentaDestino, "Transferencia auditada",
            [new LineaDeTransferencia(idArticulo, 5m)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia).ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal(ctx.IdEmpleadoAdmin, m.IdEmpleado));

        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Entidad == "articulo" && a.IdEntidad == idArticulo));
    }
}
