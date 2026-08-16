using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 12 (tasks 12.5-12.11): el conteo por lote punta a punta —
/// el split adquisición/aplicación de locks (design decisión 12), el contrato exactly-one-of
/// <c>Contada</c>/<c>Lotes</c> (decisión 18), el delta agregado derivado de los deltas por lote, la
/// disciplina de "nunca fabricar en el sin-identificar" y <c>conteo_lote_repetido</c>. La suite de
/// invariantes cross-cutting (task 12.12) vive en <see cref="InvarianteStockYStockLotesTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConteoPorLoteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private sealed record Contexto(int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva, int IdListaPrecio);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Conteo-por-lote-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Conteo Por Lote", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        // Módulo de lotes ON a nivel empresa — mismo criterio que AjusteDecomisoLoteTests.
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva, lista.Id);
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

    /// <summary>Judgment-day fix (juez B, FIX 1): contraparte SIN control efectivo de lote —
    /// <c>ControlaLote = false</c>, a diferencia de <see cref="SembrarArticuloLoteEfectivoAsync"/>.
    /// Necesaria porque la 12.11 usaba (incorrectamente) un artículo lote-efectivo para su
    /// escenario de regresión del camino agregado.</summary>
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

    private async Task<int> SembrarLoteAsync(Contexto ctx, int idArticulo, string codigo, DateOnly? fechaVencimiento, bool esSinIdentificar = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = esSinIdentificar, CreatedAt = ahora, UpdatedAt = ahora
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

    private async Task<decimal> LeerStockAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
    }

    private async Task<decimal> LeerStockLoteAsync(Contexto ctx, int idArticulo, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstOrDefaultAsync();
    }

    private static async Task<HttpResponseMessage> ContarRawAsync(Contexto ctx, SolicitudDeConteo solicitud) =>
        await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);

    private static async Task<ResultadoConteo> ContarAsync(Contexto ctx, SolicitudDeConteo solicitud)
    {
        var respuesta = await ContarRawAsync(ctx, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResultadoConteo>(cuerpo, OpcionesJson)!;
    }

    // ---- task 12.7: exactly-one-of ---------------------------------------------------------------

    /// <summary>spec conteo-de-inventario: "Supplying both cantidad_contada and lotes is
    /// rejected".</summary>
    [Fact]
    public async Task UnConteoConCantidadContadaYLotesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoConCantidadContadaYLotesEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-ambos", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-AMBOS", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 12m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, 40m, "Ambos a la vez", [new ConteoDeLote(idLote, 10m)]);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_contada_y_lotes", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
    }

    /// <summary>spec conteo-de-inventario: "Supplying neither cantidad_contada nor lotes is
    /// rejected".</summary>
    [Fact]
    public async Task UnConteoSinCantidadContadaNiLotesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoSinCantidadContadaNiLotesEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-ninguno", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, null, "Ninguno provisto", null);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_contada_y_lotes", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Contraparte del guard: un <c>Lotes</c> presente pero VACÍO cuenta como "ausente" —
    /// mismo código de rechazo que enviar <c>null</c> (dto-contract-honesty: un array vacío no trae
    /// ningún total accionable).</summary>
    [Fact]
    public async Task UnConteoConListaDeLotesVaciaEsRechazadoComoSiEstuvieraAusente()
    {
        var ctx = await PrepararAsync(nameof(UnConteoConListaDeLotesVaciaEsRechazadoComoSiEstuvieraAusente));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-lotes-vacio", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, null, "Lotes vacío", []);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_contada_y_lotes", problema.GetProperty("codigo").GetString());
    }

    // ---- task 12.10: conteo_lote_repetido -----------------------------------------------------

    /// <summary>design decisión 12: <c>conteo_lote_repetido</c> se rechaza ANTES de cualquier
    /// lock — ningún <c>movimientos_stock</c> se escribe.</summary>
    [Fact]
    public async Task UnConteoConUnIdLoteRepetidoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoConUnIdLoteRepetidoEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-repetido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-REPETIDO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 12m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Lote repetido",
            [new ConteoDeLote(idLote, 10m), new ConteoDeLote(idLote, 15m)]);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_lote_repetido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(12m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }

    // ---- task 12.6: zero-difference-lot-writes-nothing -------------------------------------------

    /// <summary>spec conteo-de-inventario: "A lot with no difference writes no row even when a
    /// sibling lot differs".</summary>
    [Fact]
    public async Task UnLoteSinDiferenciaNoEscribeFilaAunqueUnLoteHermanoDifiera()
    {
        var ctx = await PrepararAsync(nameof(UnLoteSinDiferenciaNoEscribeFilaAunqueUnLoteHermanoDifiera));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-cero-diff", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L1-CERO-DIFF", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L2-CERO-DIFF", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote1, 12m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote2, 28m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "L1 matching, L2 difiere",
            [new ConteoDeLote(idLote1, 12m), new ConteoDeLote(idLote2, 30m)]);
        var resultado = await ContarAsync(ctx, solicitud);

        Assert.Equal(2, resultado.Lotes!.Count);
        var l1 = Assert.Single(resultado.Lotes, l => l.IdLote == idLote1);
        Assert.False(l1.MovimientoRegistrado);
        Assert.Equal(0m, l1.Delta);
        var l2 = Assert.Single(resultado.Lotes, l => l.IdLote == idLote2);
        Assert.True(l2.MovimientoRegistrado);
        Assert.Equal(2m, l2.Delta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario).ToListAsync();
        var movimiento = Assert.Single(movimientos);
        Assert.Equal(idLote2, movimiento.IdLote);
        Assert.Equal(2m, movimiento.Cantidad);

        Assert.Equal(12m, await LeerStockLoteAsync(ctx, idArticulo, idLote1));
        Assert.Equal(30m, await LeerStockLoteAsync(ctx, idArticulo, idLote2));
        Assert.Equal(42m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- task 12.8: per-lot-derives-aggregate-delta ------------------------------------------------

    /// <summary>spec: "A lot-effective conteo derives the aggregate delta from per-lot deltas" —
    /// literalmente el escenario del spec: L1 12→15 (+3), L2 28→20 (-8), agregado se mueve -5.</summary>
    [Fact]
    public async Task UnConteoLoteEfectivoDerivaElDeltaAgregadoDeLosDeltasPorLote()
    {
        var ctx = await PrepararAsync(nameof(UnConteoLoteEfectivoDerivaElDeltaAgregadoDeLosDeltasPorLote));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-deriva", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L1-DERIVA", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L2-DERIVA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote1, 12m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote2, 28m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Deriva agregado",
            [new ConteoDeLote(idLote1, 15m), new ConteoDeLote(idLote2, 20m)]);
        var resultado = await ContarAsync(ctx, solicitud);

        Assert.Equal(-5m, resultado.Delta);
        Assert.Equal(35m, resultado.Cantidad);
        Assert.Equal(40m, resultado.CantidadAnterior);
        Assert.True(resultado.MovimientoRegistrado);

        var l1 = Assert.Single(resultado.Lotes!, l => l.IdLote == idLote1);
        Assert.Equal(3m, l1.Delta);
        var l2 = Assert.Single(resultado.Lotes!, l => l.IdLote == idLote2);
        Assert.Equal(-8m, l2.Delta);

        Assert.Equal(35m, await LeerStockAsync(ctx, idArticulo));
        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote1));
        Assert.Equal(20m, await LeerStockLoteAsync(ctx, idArticulo, idLote2));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario).ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.Contains(movimientos, m => m.IdLote == idLote1 && m.Cantidad == 3m);
        Assert.Contains(movimientos, m => m.IdLote == idLote2 && m.Cantidad == -8m);
    }

    // ---- task 12.9: never-fabricate-into-sin-identificar ------------------------------------------

    /// <summary>spec: "A lot-effective conteo never writes into the sin-identificar lot to absorb
    /// a difference" — L1 12→10 (-2) escribe con <c>id_lote = L1</c>; el sin-identificar (sembrado,
    /// saldo 0) queda EXACTAMENTE en 0, nunca absorbe la diferencia.</summary>
    [Fact]
    public async Task UnConteoLoteEfectivoNuncaEscribeEnElLoteSinIdentificarParaAbsorberUnaDiferencia()
    {
        var ctx = await PrepararAsync(nameof(UnConteoLoteEfectivoNuncaEscribeEnElLoteSinIdentificarParaAbsorberUnaDiferencia));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-no-fabrica", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L1-NO-FABRICA", VencimientoLejanoFuturo);
        var idSinIdentificar = await SembrarLoteAsync(
            ctx, idArticulo, ReglaDeLotes.CodigoSinIdentificar, null, esSinIdentificar: true);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote1, 12m);
        await SembrarStockLoteAsync(ctx, idArticulo, idSinIdentificar, 0m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "No fabrica en sin-identificar",
            [new ConteoDeLote(idLote1, 10m)]);
        var resultado = await ContarAsync(ctx, solicitud);

        Assert.Equal(-2m, resultado.Delta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock
            .SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario);
        Assert.Equal(idLote1, movimiento.IdLote);
        Assert.NotEqual(idSinIdentificar, movimiento.IdLote);
        Assert.Equal(-2m, movimiento.Cantidad);

        Assert.Equal(10m, await LeerStockLoteAsync(ctx, idArticulo, idLote1));
        Assert.Equal(0m, await LeerStockLoteAsync(ctx, idArticulo, idSinIdentificar));
    }

    // ---- task 12.11: aggregate-grain regression ----------------------------------------------------

    /// <summary>spec: "A matching count writes nothing" — regresión del camino agregado
    /// (<c>Contada</c>, artículo SIN lote efectivo) tras el ensanchamiento a <c>decimal?</c> /
    /// exactly-one-of de esta slice: sigue siendo un no-op byte-idéntico al de slices previos.
    /// Judgment-day fix (juez B, FIX 1): esta versión usa <see cref="SembrarArticuloSinLoteAsync"/> —
    /// la original usaba (incorrectamente) un artículo lote-efectivo, lo que escondía el bug del
    /// FIX 1 (delta cero nunca llega a escribir nada, sea cual sea la forma del conteo, así que el
    /// gap de correctitud quedaba invisible acá; ver
    /// <see cref="UnConteoAgregadoParaUnArticuloLoteEfectivoEsRechazado"/> para el caso con delta
    /// NO cero que sí lo exponía).</summary>
    [Fact]
    public async Task UnConteoAgregadoDeContadaIgualALaActualSigueSinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnConteoAgregadoDeContadaIgualALaActualSigueSinEscribirNada));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-conteo-agregado-regresion", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, 40m, "Recuento sin diferencias");
        var resultado = await ContarAsync(ctx, solicitud);

        Assert.False(resultado.MovimientoRegistrado);
        Assert.Equal(0m, resultado.Delta);
        Assert.Equal(40m, resultado.Cantidad);
        Assert.Null(resultado.Lotes);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(40m, await LeerStockAsync(ctx, idArticulo));
    }

    /// <summary>spec conteo-de-inventario (Amended at slice-12 judgment-day, juez B FIX 1): un
    /// total agregado (<c>Contada</c>) contra un artículo lote-efectivo se rechaza con <c>400
    /// conteo_requiere_lotes</c> ANTES de cualquier lock — nunca movió <c>stock.cantidad</c> sin
    /// tocar <c>stock_lotes</c> (el bug empírico: 40→50 agregado, lotes quedan en 40, invariante 3
    /// roto en silencio). Delta explícitamente NO cero para que la escritura real quede expuesta si
    /// el guard llegara a fallar.</summary>
    [Fact]
    public async Task UnConteoAgregadoParaUnArticuloLoteEfectivoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoAgregadoParaUnArticuloLoteEfectivoEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-agregado-rechazado", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-AGREGADO-RECHAZADO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVenta, idArticulo, 50m, "Total agregado sobre lote-efectivo");
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_requiere_lotes", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(40m, await LeerStockAsync(ctx, idArticulo));
        Assert.Equal(40m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }

    /// <summary>Simetría inversa del FIX 1: un desglose por lote (<c>Lotes</c>) contra un artículo
    /// SIN control efectivo de lote no tiene destino — rechazado con <c>400
    /// conteo_no_aplica_lotes</c>, mismo criterio que <c>lote_no_aplica</c> en
    /// <c>ResolverIdLoteEfectivoAsync</c>.</summary>
    [Fact]
    public async Task UnConteoPorLoteParaUnArticuloSinLoteEfectivoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoPorLoteParaUnArticuloSinLoteEfectivoEsRechazado));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-conteo-lotes-sin-lote-efectivo", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Desglose sobre articulo sin lote efectivo",
            [new ConteoDeLote(999_999, 10m)]);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conteo_no_aplica_lotes", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(40m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- judgment-day fix (juez B, FIX 2): idLote inválido en conteo por lote ---------------------

    /// <summary>Un <c>idLote</c> inexistente en el desglose por lote se rechaza con <c>400
    /// lote_invalido</c> ANTES de cualquier lock — nunca un 500 crudo de FK dentro del upsert
    /// no-op de <c>BloquearYCrearSiFaltaStockLoteAsync</c>.</summary>
    [Fact]
    public async Task UnConteoPorLoteConUnIdLoteInexistenteEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnConteoPorLoteConUnIdLoteInexistenteEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-conteo-lote-inexistente", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-LOTE-INEXISTENTE", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 12m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 12m);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Lote inexistente", [new ConteoDeLote(999_999, 10m)]);
        var respuesta = await ContarRawAsync(ctx, solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Inventario));
        Assert.Equal(12m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(12m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- task 12.5: lock-acquisition-order ----------------------------------------------------------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>design decisión 12: "the per-lot conteo acquires all its locks first ..., derives
    /// every delta, and only then writes". Caso discriminante: dos lotes, L1 (id menor) y L2 (id
    /// mayor) — el orden ascendente pineado procesa PRIMERO el agregado, después L1 (sin
    /// contención), y recién ahí L2. Reteniendo EXTERNAMENTE el lock no-op de L2 (el ÚLTIMO
    /// elemento del orden), el conteo se bloquea justo ahí — DESPUÉS de haber adquirido el lock de
    /// L1, pero ANTES de aplicar cualquier delta (ni siquiera el de L1, cuyo lock ya tiene). La
    /// prueba, mismo mecanismo empírico a nivel RELACIÓN que
    /// <c>TransferenciaLoteTests.ElOrdenDeLocksDeUnaTransferenciaConLoteEsUnaUnicaSecuenciaAscendentePorPuntoDeVenta</c>:
    /// mientras el backend del conteo espera ahí, NINGÚN statement contra <c>movimientos_stock</c>
    /// pudo haber corrido todavía — una implementación que escribiera el delta de L1 apenas
    /// adquirido su lock (sin esperar al de L2) ya habría tocado esa relación.</summary>
    [Fact]
    public async Task ElConteoPorLoteAdquiereTodosLosLocksAntesDeEscribirCualquierDelta()
    {
        var ctx = await PrepararAsync(nameof(ElConteoPorLoteAdquiereTodosLosLocksAntesDeEscribirCualquierDelta));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-orden-conteo", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L1-ORDEN-CONTEO", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L2-ORDEN-CONTEO", VencimientoLejanoFuturo);
        Assert.True(idLote1 < idLote2);

        await SembrarStockLoteAsync(ctx, idArticulo, idLote1, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote2, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 30m);

        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<Ways.Domain.Organizacion.EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<Ways.Domain.Catalogos.ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<Ways.Domain.Clientes.TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<Ways.Domain.Ventas.EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<Ways.Domain.Caja.EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);
        await db.Database.OpenConnectionAsync();
        var conexionConteo = (NpgsqlConnection)db.Database.GetDbConnection();
        var pidConteo = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexionConteo).ExecuteScalarAsync())!;

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDeLotes = new ServicioDeLotes(db, reloj, contexto);
        var servicioDeAuditoria = new Ways.Application.Auditoria.ServicioDeAuditoria(db, reloj, contexto);
        var servicioDeStock = new ServicioDeStock(db, reloj, contexto, servicioDeLotes, servicioDeAuditoria);

        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVenta, idArticulo, null, "Orden de locks",
            [new ConteoDeLote(idLote1, 15m), new ConteoDeLote(idLote2, 25m)]);

        // Retiene EXTERNAMENTE el lock no-op del ÚLTIMO elemento del orden (L2, id mayor) — el
        // conteo tiene que bloquearse ahí, después de L1 pero antes de aplicar ningún delta.
        await using var conexionBloqueo = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionBloqueo.OpenAsync();
        await using (var comandoGuc = new NpgsqlCommand(
            "SELECT set_config('app.acceso', 'tenant', false), set_config('app.tenant_id', $1, false)", conexionBloqueo))
        {
            comandoGuc.Parameters.AddWithValue(ctx.IdTenant.ToString());
            await comandoGuc.ExecuteNonQueryAsync();
        }

        await using var transaccionBloqueo = await conexionBloqueo.BeginTransactionAsync();
        await using (var comandoBloqueo = new NpgsqlCommand(
            "SELECT cantidad FROM stock_lotes WHERE id_articulo = $1 AND id_punto_venta = $2 AND id_lote = $3 FOR UPDATE",
            conexionBloqueo, transaccionBloqueo))
        {
            comandoBloqueo.Parameters.AddWithValue(idArticulo);
            comandoBloqueo.Parameters.AddWithValue(ctx.IdPuntoVenta);
            comandoBloqueo.Parameters.AddWithValue(idLote2);
            await comandoBloqueo.ExecuteScalarAsync();
        }

        var tareaConteo = servicioDeStock.ContarAsync(solicitud);

        await using var conexionPoll = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionPoll.OpenAsync();

        var observado = false;
        var movimientosStockYaTocado = true;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            await using var comandoPoll = new NpgsqlCommand(
                "SELECT " +
                "  bool_or(l.locktype = 'relation' AND l.relation::regclass::text = 'movimientos_stock') AS movimientos_tocado, " +
                "  bool_or(NOT l.granted) AS esperando_algo " +
                "FROM pg_locks l WHERE l.pid = $1",
                conexionPoll);
            comandoPoll.Parameters.AddWithValue(pidConteo);

            await using var lector = await comandoPoll.ExecuteReaderAsync();
            if (await lector.ReadAsync())
            {
                var movimientosTocado = !lector.IsDBNull(0) && lector.GetBoolean(0);
                var esperandoAlgo = !lector.IsDBNull(1) && lector.GetBoolean(1);
                if (esperandoAlgo)
                {
                    observado = true;
                    movimientosStockYaTocado = movimientosTocado;
                    break;
                }
            }

            await Task.Delay(25);
        }

        await transaccionBloqueo.RollbackAsync();
        var resultado = await tareaConteo;

        Assert.True(observado, "Nunca se observó al conteo bloqueado esperando el lock retenido del segundo lote.");
        Assert.False(
            movimientosStockYaTocado,
            "movimientos_stock ya fue tocado mientras el conteo esperaba el lock del SEGUNDO lote (L2) — " +
            "prueba que se escribió un delta (el de L1) antes de terminar la fase de ADQUISICIÓN completa " +
            "(design decisión 12: adquisición de TODOS los locks, después aplicación).");

        Assert.Equal(2, resultado.Lotes!.Count);
        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote1));
        Assert.Equal(25m, await LeerStockLoteAsync(ctx, idArticulo, idLote2));
        Assert.Equal(40m, await LeerStockAsync(ctx, idArticulo));
    }
}
