using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 10 (tasks 10.4-10.12): la transferencia lote-consciente
/// punta a punta — el orden ensanchado a <c>≥2N</c> claves <c>(id_articulo, id_punto_venta,
/// id_lote NULLS FIRST)</c>, el lote que viaja, la suficiencia per-lote, el default FEFO, el
/// rechazo de vencidos y de duplicados post-defaulting, y el joint deadlock proof que cierra el
/// pairing dejado abierto en la task 8.7 de <c>VentaEscrituraLoteTests</c> (el checkout ya es
/// lot-aware desde el slice 8, así que la mitad conjunta del proof es posible acá).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TransferenciaLoteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas fijas y lejanas — independientes del reloj de la corrida.
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);
    private static readonly DateOnly VencimientoLejanoFuturoTemprano = new(2090, 1, 1);
    private static readonly DateOnly VencimientoLejanoPasado = new(2020, 1, 1);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVentaOrigen, int IdPuntoVentaDestino, HttpClient Admin,
        int IdArea, int IdAlicuotaIva, int IdListaPrecio, int IdMedioEfectivo, int IdCliente,
        string MailAdmin, string PasswordAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Transferencia-lote-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Transferencia Lote", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Segundo punto de venta REAL — nace DESPUÉS del de origen, así que su id SIEMPRE es
        // mayor (usado por la task 10.11 para forzar el caso discriminante: transferir desde el
        // PV de id MAYOR hacia el de id MENOR).
        var puntoVentaDestino = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 2 (lote)",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVentaDestino);
        await db.SaveChangesAsync();

        // Módulo de lotes ON a nivel empresa — todas las pruebas de este archivo son sobre
        // artículos lote-efectivos (mismo criterio que VentaEscrituraLoteTests.PrepararAsync).
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        // Turno abierto en el origen — necesario para el checkout del joint proof (task 10.12).
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente Transferencia Lote",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 1_000_000m,
            CreditoIlimitado = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVentaDestino.Id, admin, area.Id,
            idAlicuotaIva, lista.Id, idMedioEfectivo, cliente.Id, mailAdmin, resultado.PasswordTemporal);
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

    /// <summary>Artículo SIN control de lote — <c>ControlaLote = false</c>, contraparte del
    /// lote-efectivo de arriba, usado por el caso mixto y por el rechazo de <c>lote_invalido</c>
    /// (mismo criterio que <c>TransferenciasYConteoDeInventarioTests.SembrarArticuloConPrecioAsync</c>,
    /// copiado acá a propósito — frentes en paralelo, sin infra de test compartida entre archivos).</summary>
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

    /// <summary>Ajuste manual (<c>/api/stock/ajustes</c>) TODAVÍA no es lote-consciente en este
    /// worktree — esa extensión es slice 11 (Ajuste + Decomiso), fuera del alcance de este slice.
    /// Sembrar <c>stock</c>/<c>stock_lotes</c> directo por EF es el mismo criterio que
    /// <c>VentaEscrituraLoteTests</c>.</summary>
    private async Task SembrarStockLoteAsync(Contexto ctx, int idPuntoVenta, int idArticulo, int idLote, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = idPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = idPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeTransferencia SolicitudDeUnaLinea(
        Contexto ctx, int idArticulo, decimal cantidad, int? idLote,
        int? idPuntoVentaOrigen = null, int? idPuntoVentaDestino = null, string observaciones = "Reposición con lote") =>
        new(
            idPuntoVentaOrigen ?? ctx.IdPuntoVentaOrigen, idPuntoVentaDestino ?? ctx.IdPuntoVentaDestino, observaciones,
            [new LineaDeTransferencia(idArticulo, cantidad, idLote)]);

    private async Task<decimal> LeerStockAsync(Contexto ctx, int idPuntoVenta, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
    }

    private async Task<decimal> LeerStockLoteAsync(Contexto ctx, int idPuntoVenta, int idArticulo, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == idPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstOrDefaultAsync();
    }

    // ---- task 10.7: el lote viaja ------------------------------------------------------------------

    /// <summary>spec transferencias-de-stock: "A lot-effective articulo transfer moves the same lot
    /// at both ends" — ambos movimientos_stock llevan el MISMO id_lote y ambos stock_lotes (origen Y
    /// destino, este último recién creado) quedan exactos.</summary>
    [Fact]
    public async Task UnaTransferenciaDeUnArticuloLoteEfectivoMueveElMismoLoteEnAmbasPuntas()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaDeUnArticuloLoteEfectivoMueveElMismoLoteEnAmbasPuntas));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-lote-viaja", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-VIAJA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m, idLote));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal(idLote, m.IdLote));

        Assert.Equal(12m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote));
        Assert.Equal(8m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLote));
        Assert.Equal(12m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
        Assert.Equal(8m, await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo));
    }

    // ---- task 10.6: insuficiencia per-lote con agregado suficiente ----------------------------------

    /// <summary>spec transferencias-de-stock: "A lot-level underflow is refused even with a
    /// sufficient aggregate" — mata el confound: el agregado (30) alcanzaría de sobra para 8
    /// unidades, pero el lote pedido (L1) solo tiene 5.</summary>
    [Fact]
    public async Task UnaTransferenciaConInsuficienciaDeLoteEsRechazadaAunqueElAgregadoAlcance()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaConInsuficienciaDeLoteEsRechazadaAunqueElAgregadoAlcance));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-insuficiencia-lote", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L-INSUF-1", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L-INSUF-2", VencimientoLejanoFuturoTemprano);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote1, 5m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote2, 25m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 30m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 8m, idLote1));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_insuficiente_para_transferencia", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));

        // El rollback deja TODO exactamente como estaba — agregado suficiente incluido, la
        // parte del confound que este test existe para matar.
        Assert.Equal(30m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
        Assert.Equal(5m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote1));
    }

    // ---- task 10.8: default FEFO en la fase de decisión ---------------------------------------------

    /// <summary>spec: "An omitted idLote resolves via FEFO at transfer time" — L1 vence antes que
    /// L2, ninguno vencido, así que el FEFO puro (sin partición por vencido) elige L1.</summary>
    [Fact]
    public async Task UnaLineaSinIdLoteResuelveViaFefoEnLaTransferencia()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaSinIdLoteResuelveViaFefoEnLaTransferencia));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-fefo-transferencia", 10m);
        var idLoteTemprano = await SembrarLoteAsync(ctx, idArticulo, "L-FEFO-TEMPRANO", VencimientoLejanoFuturoTemprano);
        var idLoteTardio = await SembrarLoteAsync(ctx, idArticulo, "L-FEFO-TARDIO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteTemprano, 10m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteTardio, 10m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 3m, idLote: null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal(idLoteTemprano, m.IdLote));

        Assert.Equal(7m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteTemprano));
        Assert.Equal(10m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteTardio));
    }

    // ---- task 10.9: transferencia_lote_vencido ×2 --------------------------------------------------

    /// <summary>spec: "Transferring an explicitly expired lot is refused".</summary>
    [Fact]
    public async Task UnLoteVencidoExplicitoEsRechazadoSinEscribirNingunMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnLoteVencidoExplicitoEsRechazadoSinEscribirNingunMovimiento));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-lote-vencido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-VENCIDO", VencimientoLejanoPasado);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 5m, idLote));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transferencia_lote_vencido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
        Assert.Equal(20m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote));
    }

    /// <summary>spec: "A non-expired lot transfers normally" — regresión positiva junto al
    /// rechazo de arriba, mismo artículo/forma, solo cambia la fecha.</summary>
    [Fact]
    public async Task UnLoteNoVencidoTransfiereNormalmente()
    {
        var ctx = await PrepararAsync(nameof(UnLoteNoVencidoTransfiereNormalmente));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-lote-no-vencido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-NO-VENCIDO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 5m, idLote));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal(15m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote));
        Assert.Equal(5m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLote));
    }

    // ---- task 10.10: duplicados ×3 -------------------------------------------------------------------

    /// <summary>spec: "Two lines of the same articulo with different explicit lots are accepted" —
    /// mueve DOS lotes del mismo artículo en una sola transferencia, operación de depósito legítima
    /// que la clave pre-etapa-12 (solo id_articulo) hubiera rechazado sin motivo.</summary>
    [Fact]
    public async Task DosLineasDelMismoArticuloConLotesExplicitosDistintosSonAceptadas()
    {
        var ctx = await PrepararAsync(nameof(DosLineasDelMismoArticuloConLotesExplicitosDistintosSonAceptadas));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-dos-lotes-distintos", 10m);
        var idLote1 = await SembrarLoteAsync(ctx, idArticulo, "L-DUP-A", VencimientoLejanoFuturo);
        var idLote2 = await SembrarLoteAsync(ctx, idArticulo, "L-DUP-B", VencimientoLejanoFuturoTemprano);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote1, 10m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote2, 10m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Dos lotes del mismo artículo",
            [new LineaDeTransferencia(idArticulo, 3m, idLote1), new LineaDeTransferencia(idArticulo, 4m, idLote2)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.Contains(movimientos, m => m.IdLote == idLote1 && m.Cantidad == -3m);
        Assert.Contains(movimientos, m => m.IdLote == idLote2 && m.Cantidad == -4m);

        Assert.Equal(7m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote1));
        Assert.Equal(6m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote2));
        Assert.Equal(13m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
    }

    /// <summary>spec: "Two lines resolving to the same explicit lot are rejected".</summary>
    [Fact]
    public async Task DosLineasConElMismoLoteExplicitoSonRechazadas()
    {
        var ctx = await PrepararAsync(nameof(DosLineasConElMismoLoteExplicitoSonRechazadas));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-mismo-lote-explicito", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-MISMO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Mismo lote dos veces",
            [new LineaDeTransferencia(idArticulo, 3m, idLote), new LineaDeTransferencia(idArticulo, 2m, idLote)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_repetido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
    }

    /// <summary>spec: "Two lines both omitting idLote that resolve to the same FEFO lot are
    /// rejected" — el chequeo corre DESPUÉS del defaulting, no contra el input (vacío) del
    /// cliente: solo hay un lote con saldo positivo, así que ambas líneas resuelven a él.</summary>
    [Fact]
    public async Task DosLineasSinIdLoteQueResuelvenAlMismoLotePorFefoSonRechazadas()
    {
        var ctx = await PrepararAsync(nameof(DosLineasSinIdLoteQueResuelvenAlMismoLotePorFefoSonRechazadas));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-fefo-duplicado", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-FEFO-UNICO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Dos líneas sin idLote",
            [new LineaDeTransferencia(idArticulo, 3m, null), new LineaDeTransferencia(idArticulo, 2m, null)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_repetido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
    }

    // ---- gaps de cobertura (judgment-day, ronda de fix): mixta + lote_invalido sobre línea sin lote --

    /// <summary>spec transferencias-de-stock: una transferencia con una línea de artículo
    /// lote-efectivo y una línea de artículo SIN control de lote completa AMBAS en la misma
    /// transacción — el filtro <c>indicesConLoteEfectivo</c> de <see cref="ServicioDeStock"/>
    /// separa las líneas correctamente en <c>ConstruirClavesOrdenadas</c> (2 claves para la línea
    /// sin lote, 4 para la línea con lote), sin que una contamine el tratamiento de la otra.
    ///
    /// EVIDENCIA DE MUTACIÓN: mutado el ternario de <c>ConstruirClavesOrdenadas</c> para emitir
    /// también una clave de lote (<c>IdLote = 0</c>) en la rama sin-lote — build, filtro
    /// <c>FullyQualifiedName~TransferenciaLote</c>: este test <b>FALLA</b> (el movimiento sin lote
    /// deja de tener <c>id_lote == null</c> / aparece una fila de <c>stock_lotes</c> inesperada).
    /// Revertido el mutante, corrida de nuevo: GREEN.</summary>
    [Fact]
    public async Task UnaTransferenciaMixtaConLineaLoteEfectivaYLineaSinLoteCompletaAmbas()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaMixtaConLineaLoteEfectivaYLineaSinLoteCompletaAmbas));

        var idArticuloConLote = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-mixta-con-lote", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticuloConLote, "L-MIXTA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloConLote, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloConLote, 20m);

        var idArticuloSinLote = await SembrarArticuloSinLoteAsync(ctx, "articulo-mixta-sin-lote", 15m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloSinLote, 30m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Transferencia mixta",
            [new LineaDeTransferencia(idArticuloSinLote, 6m, null), new LineaDeTransferencia(idArticuloConLote, 8m, idLote)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimientosSinLote = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticuloSinLote && m.Motivo == MotivoStock.Transferencia)
            .ToListAsync();
        Assert.Equal(2, movimientosSinLote.Count);
        Assert.All(movimientosSinLote, m => Assert.Null(m.IdLote));
        Assert.Contains(movimientosSinLote, m => m.IdPuntoVenta == ctx.IdPuntoVentaOrigen && m.Cantidad == -6m);
        Assert.Contains(movimientosSinLote, m => m.IdPuntoVenta == ctx.IdPuntoVentaDestino && m.Cantidad == 6m);

        var movimientosConLote = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticuloConLote && m.Motivo == MotivoStock.Transferencia)
            .ToListAsync();
        Assert.Equal(2, movimientosConLote.Count);
        Assert.All(movimientosConLote, m => Assert.Equal(idLote, m.IdLote));
        Assert.Contains(movimientosConLote, m => m.IdPuntoVenta == ctx.IdPuntoVentaOrigen && m.Cantidad == -8m);
        Assert.Contains(movimientosConLote, m => m.IdPuntoVenta == ctx.IdPuntoVentaDestino && m.Cantidad == 8m);

        Assert.Equal(24m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloSinLote));
        Assert.Equal(6m, await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticuloSinLote));
        Assert.Equal(12m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloConLote));
        Assert.Equal(8m, await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticuloConLote));
        Assert.Equal(12m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloConLote, idLote));
        Assert.Equal(8m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticuloConLote, idLote));
    }

    /// <summary>dto-contract-honesty (mismo criterio que <c>ServicioDeVentas</c>): un <c>idLote</c>
    /// provisto en una línea de artículo SIN lote efectivo (<c>ControlaLote = false</c>) se
    /// rechaza explícitamente en vez de ignorarse en silencio — <c>ServicioDeStock.cs</c>,
    /// guard ~269-281.
    ///
    /// EVIDENCIA DE MUTACIÓN: anulado el guard (comentado el <c>if</c> que lanza
    /// <c>lote_invalido</c>) — build, filtro <c>FullyQualifiedName~TransferenciaLote</c>: este
    /// test <b>FALLA</b> (200 en vez de 400 — el idLote ajeno se ignora en silencio). Revertido
    /// el mutante, corrida de nuevo: GREEN.</summary>
    [Fact]
    public async Task UnaLineaSinLoteEfectivoConIdLoteProvistoEsRechazadaComoLoteInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaSinLoteEfectivoConIdLoteProvistoEsRechazadaComoLoteInvalido));

        var idArticuloSinLote = await SembrarArticuloSinLoteAsync(ctx, "articulo-sin-lote-invalido", 12m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloSinLote, 20m);

        var idArticuloConLote = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-lote-ajeno", 10m);
        var idLoteAjeno = await SembrarLoteAsync(ctx, idArticuloConLote, "L-AJENO", VencimientoLejanoFuturo);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticuloSinLote, 5m, idLoteAjeno));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloSinLote && m.Motivo == MotivoStock.Transferencia));
        Assert.Equal(20m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloSinLote));
    }

    // ---- judgment-day (juez A, ronda 1, FIX 1): el response no colapsa multi-lote del mismo artículo --

    /// <summary>dto-contract-honesty (design.md:180 — <c>LineaTransferida(int IdArticulo, int? IdLote,
    /// decimal CantidadOrigen, decimal CantidadDestino)</c>): dos líneas del MISMO artículo con lotes
    /// explícitos distintos son un caso ACEPTADO por spec (mismo escenario que
    /// <see cref="DosLineasDelMismoArticuloConLotesExplicitosDistintosSonAceptadas"/>) — la clave de
    /// agregación del response ensancha a <c>(IdArticulo, IdLote)</c>, así que esas dos líneas producen
    /// DOS filas, no una sola que las colapsa. Se suma una tercera línea de un artículo DISTINTO sin
    /// <c>idLote</c> (FEFO-default) para probar que el lote resuelto por FEFO también viaja en la fila.
    ///
    /// <see cref="LineaTransferida.CantidadOrigen"/>/<see cref="LineaTransferida.CantidadDestino"/> son
    /// el valor de <c>stock.cantidad</c> (la fila AGREGADA, compartida por todos los lotes del mismo
    /// artículo) tal como lo devolvió el upsert de ESA línea puntual, en el orden en que las líneas
    /// llegaron en la solicitud — no el saldo final del artículo ni el saldo del lote en particular
    /// (ese vive en <c>stock_lotes</c>, fuera de este contrato). Para una única línea por artículo
    /// (todos los tests de arriba) ese checkpoint coincide con el saldo final, que es lo que ya
    /// probaban; acá, con dos líneas del mismo artículo, se ven los DOS checkpoints intermedios.
    ///
    /// EVIDENCIA DE MUTACIÓN: revertida la clave de <c>resultadosPorArticuloYLote</c> a <c>IdArticulo</c>
    /// solo (<c>ServicioDeStock.EjecutarTransferenciaAsync</c>) — build, filtro
    /// <c>FullyQualifiedName~TransferenciaLote</c>: este test <b>FALLA</b> (2 filas en <c>Lineas</c> en
    /// vez de 3, la del artículo A pisada por la segunda línea). Revertido el mutante, corrida de
    /// nuevo: GREEN.</summary>
    [Fact]
    public async Task LaRespuestaDeUnaTransferenciaConDosLotesDelMismoArticuloTraeUnaFilaPorLoteConIdLote()
    {
        var ctx = await PrepararAsync(nameof(LaRespuestaDeUnaTransferenciaConDosLotesDelMismoArticuloTraeUnaFilaPorLoteConIdLote));

        var idArticuloA = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-response-dos-lotes", 10m);
        var idLoteA1 = await SembrarLoteAsync(ctx, idArticuloA, "L-RESP-A1", VencimientoLejanoFuturo);
        var idLoteA2 = await SembrarLoteAsync(ctx, idArticuloA, "L-RESP-A2", VencimientoLejanoFuturoTemprano);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloA, idLoteA1, 10m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloA, idLoteA2, 10m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloA, 20m);

        var idArticuloB = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-response-fefo", 10m);
        var idLoteB = await SembrarLoteAsync(ctx, idArticuloB, "L-RESP-B", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloB, idLoteB, 15m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloB, 15m);

        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Response multi-lote del mismo artículo",
            [
                new LineaDeTransferencia(idArticuloA, 3m, idLoteA1),
                new LineaDeTransferencia(idArticuloA, 4m, idLoteA2),
                new LineaDeTransferencia(idArticuloB, 5m, null)
            ]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<ResultadoTransferencia>(cuerpo, OpcionesJson)!;
        Assert.Equal(3, resultado.Lineas.Count);

        var filaA1 = Assert.Single(resultado.Lineas, l => l.IdArticulo == idArticuloA && l.IdLote == idLoteA1);
        Assert.Equal(17m, filaA1.CantidadOrigen);
        Assert.Equal(3m, filaA1.CantidadDestino);

        var filaA2 = Assert.Single(resultado.Lineas, l => l.IdArticulo == idArticuloA && l.IdLote == idLoteA2);
        Assert.Equal(13m, filaA2.CantidadOrigen);
        Assert.Equal(7m, filaA2.CantidadDestino);

        // FEFO-default: idLoteB es el único lote con saldo del artículo B — el lote resuelto viaja
        // en IdLote aunque el cliente nunca lo pidió explícito.
        var filaB = Assert.Single(resultado.Lineas, l => l.IdArticulo == idArticuloB && l.IdLote == idLoteB);
        Assert.Equal(10m, filaB.CantidadOrigen);
        Assert.Equal(5m, filaB.CantidadDestino);

        Assert.Equal(7m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloA, idLoteA1));
        Assert.Equal(6m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloA, idLoteA2));
        Assert.Equal(10m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticuloB, idLoteB));
    }

    // ---- judgment-day (juez A, ronda 1, FIX 2): re-check de vencido corre también sobre FEFO --------

    /// <summary>spec transferencias-de-stock: "Transferring an expired lot is refused... always,
    /// whether the lot is explicit or FEFO-resolved" — contraparte por FEFO de
    /// <see cref="UnLoteVencidoExplicitoEsRechazadoSinEscribirNingunMovimiento"/>: el ÚNICO lote con
    /// saldo positivo del artículo está vencido, la línea omite <c>idLote</c>, FEFO lo resuelve de
    /// todos modos (no filtra por vencido, solo por saldo positivo) y el re-check incondicional de
    /// <c>ServicioDeStock.ResolverLineasDeTransferenciaAsync</c> lo rechaza igual que si hubiera sido
    /// explícito.
    ///
    /// EVIDENCIA DE MUTACIÓN: comentado el <c>if (ReglaDeLotes.EstaVencido(...))</c> que lanza
    /// <c>transferencia_lote_vencido</c> — build, filtro <c>FullyQualifiedName~TransferenciaLote</c>:
    /// este test <b>FALLA</b> (200, transfiere el lote vencido resuelto por FEFO). Revertido el
    /// mutante, corrida de nuevo: GREEN.</summary>
    [Fact]
    public async Task UnaLineaSinIdLoteQueResuelvePorFefoAUnUnicoLoteVencidoEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaSinIdLoteQueResuelvePorFefoAUnUnicoLoteVencidoEsRechazada));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-fefo-unico-vencido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-FEFO-VENCIDO", VencimientoLejanoPasado);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias", SolicitudDeUnaLinea(ctx, idArticulo, 5m, idLote: null));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transferencia_lote_vencido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Transferencia));
        Assert.Equal(20m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote));
        Assert.Equal(20m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
    }

    // ---- task 10.4 / 10.5: mutation target + A→B vs. B→A, sin 40P01 -------------------------------

    /// <summary>Pausa la transacción manual justo DESPUÉS de <c>BeginTransactionAsync</c> hasta que
    /// AMBOS participantes llegaron — mismo patrón (<c>CountdownEvent</c> de N) que
    /// <c>TransferenciasYConteoDeInventarioTests.InterceptorDeRendezVousDeTransaccion</c>, copiado
    /// acá a propósito (frentes en paralelo de la etapa, mismo criterio de no compartir infra de
    /// test entre archivos).</summary>
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

    /// <summary>Mutation target (design decisión 9, forma ensanchada de la task 8.7; spec stock:
    /// "Lock Order Extends To The Lot Dimension..."; mutation-proof-tests): DOS transferencias
    /// RECÍPROCAS (A: origen→destino, B: destino→origen) del MISMO artículo, cada una con DOS
    /// líneas de lotes explícitos DISTINTOS, enviadas en orden OPUESTO entre sí (A: [L-menor,
    /// L-mayor]; B: [L-mayor, L-menor]) — sin <c>.ThenBy(IdLote.HasValue).ThenBy(IdLote ?? 0)</c>,
    /// el sort estable preserva el orden de ARRIBO dentro de cada punto de venta compartido, así
    /// que A bloquearía sus filas de lote en orden (menor, mayor) mientras B las bloquea en orden
    /// (mayor, menor) — el ciclo clásico de deadlock (A retiene menor, espera mayor; B retiene
    /// mayor, espera menor). El <c>CountdownEvent(2)</c> fuerza que AMBAS transacciones ya estén
    /// abiertas antes de que cualquiera intente su primer lock, maximizando la ventana de carrera
    /// real — sin este forced rendezvous, la carrera podría no manifestarse de forma confiable.
    ///
    /// EVIDENCIA DE MUTACIÓN (retomada tras el corte del proceso — corrida real, no la aspiracional
    /// que dejó el wip): borrado <c>.ThenBy(c => c.IdLote.HasValue).ThenBy(c => c.IdLote ?? 0)</c> de
    /// <c>ConstruirClavesOrdenadas</c> (queda solo <c>.OrderBy(IdArticulo).ThenBy(IdPuntoVenta)</c>);
    /// build, filtro <c>FullyQualifiedName~TransferenciaLote</c> (el archivo completo, no solo este
    /// test), corrida ×2: <b>GREEN las 2 corridas, sin excepción</b> — este test NO mata la mutación.
    /// Causa raíz (analizada, no adivinada): dentro de un mismo <c>(id_articulo, id_punto_venta)</c>,
    /// el elemento AGREGADO de CADA línea precede a su elemento LOTE por construcción del array por
    /// línea (<c>new[] { agg, lote, agg, lote }</c>), independientemente de cualquier <c>ThenBy</c>
    /// adicional — así que el PRIMER elemento del bucket compartido por dos transferencias recíprocas
    /// del MISMO artículo es SIEMPRE un elemento agregado, y ambas transacciones lo tocan sobre la
    /// MISMA fila física de <c>stock</c>. Esa fila compartida actúa de convoy: quien la toca primero
    /// la retiene hasta el <c>COMMIT</c>, y la otra transacción queda bloqueada ahí mismo — nunca
    /// llega a competir por las filas de <c>stock_lotes</c> en el orden opuesto que este test intenta
    /// forzar, así que el ciclo clásico de deadlock nunca se forma. El tie-break por <c>id_lote</c>
    /// sigue siendo correcto y exigido por el design/spec (orden total ≥2N-key, consistente con los
    /// otros dos sitios de escritura), pero ESTE test de transferencias recíprocas del mismo artículo
    /// no es el que lo prueba — la convoy del agregado lo neutraliza estructuralmente. Ningún test de
    /// este archivo (tampoco 10.11, de una sola línea/lote, ni 10.12, mismo artículo y PV) queda
    /// expuesto a esta mutación por el mismo motivo. Dejado como evidencia negativa documentada en
    /// vez de una afirmación falsa de RED→GREEN — ver la nota de la task 10.4 en tasks.md.</summary>
    [Fact]
    public async Task TransferenciasReciprocasDelMismoArticuloConLotesEnOrdenOpuestoNoDeadlockean()
    {
        var ctx = await PrepararAsync(nameof(TransferenciasReciprocasDelMismoArticuloConLotesEnOrdenOpuestoNoDeadlockean));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-deadlock-reciproco", 10m);
        var idLoteMenor = await SembrarLoteAsync(ctx, idArticulo, "L-RECIPROCO-MENOR", VencimientoLejanoFuturo);
        var idLoteMayor = await SembrarLoteAsync(ctx, idArticulo, "L-RECIPROCO-MAYOR", VencimientoLejanoFuturo);
        Assert.True(idLoteMenor < idLoteMayor);

        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteMenor, 100m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteMayor, 100m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 200m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLoteMenor, 100m);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLoteMayor, 100m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, 200m);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousDeTransaccion(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitudA = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Transferencia A: menor luego mayor",
            [new LineaDeTransferencia(idArticulo, 3m, idLoteMenor), new LineaDeTransferencia(idArticulo, 5m, idLoteMayor)]);
        var solicitudB = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaDestino, ctx.IdPuntoVentaOrigen, "Transferencia B: mayor luego menor",
            [new LineaDeTransferencia(idArticulo, 2m, idLoteMayor), new LineaDeTransferencia(idArticulo, 4m, idLoteMenor)]);

        var tareaA = cliente.PostAsJsonAsync("/api/stock/transferencias", solicitudA);
        var tareaB = cliente.PostAsJsonAsync("/api/stock/transferencias", solicitudB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var cuerpos = await Task.WhenAll(respuestas.Select(r => r.Content.ReadAsStringAsync()));

        Assert.True(respuestas[0].StatusCode == HttpStatusCode.OK, cuerpos[0]);
        Assert.True(respuestas[1].StatusCode == HttpStatusCode.OK, cuerpos[1]);

        // Invariante final: ningún artículo se creó ni se destruyó — la suma de ambos puntos de
        // venta se mantiene en 400 exactamente (200+200 iniciales).
        var totalFinal =
            await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo) + await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo);
        Assert.Equal(400m, totalFinal);

        var totalLoteMenor =
            await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteMenor)
            + await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLoteMenor);
        var totalLoteMayor =
            await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLoteMayor)
            + await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLoteMayor);
        Assert.Equal(200m, totalLoteMenor);
        Assert.Equal(200m, totalLoteMayor);

        // Origen: -3(A, menor) +4(B, menor) -5(A, mayor) +2(B, mayor) = 200 -3+4-5+2 = 198.
        Assert.Equal(198m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
        Assert.Equal(202m, await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo));
    }

    // ---- task 10.11: un único orden ascendente, origen Y destino -----------------------------------

    /// <summary>Construye un <see cref="ServicioDeStock"/> propio, con su PROPIA conexión (mismo
    /// patrón que <c>VentaEscrituraLoteTests.EmitirObservandoOrdenDeLocksAsync</c>) — necesario
    /// para conocer <c>pg_backend_pid()</c> de la transacción de la transferencia y poder pollear
    /// <c>pg_locks</c> por ese PID específico.</summary>
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

    /// <summary>spec stock: "Lock Order Extends To The Lot Dimension..."; spec
    /// transferencias-de-stock: "A single ascending order covers both origin and destination lot
    /// rows... never all-origin-then-all-destino". El caso discriminante NECESITA que el destino
    /// tenga un <c>id_punto_venta</c> MENOR que el origen — con la asignación natural (destino
    /// nace después, id mayor) el orden ascendente COINCIDE por casualidad con "todo el origen
    /// primero", así que este test transfiere en la dirección INVERSA de la fixture: origen =
    /// <see cref="Contexto.IdPuntoVentaDestino"/> (id mayor), destino = <see
    /// cref="Contexto.IdPuntoVentaOrigen"/> (id menor). Bajo el orden correcto, el PRIMER elemento
    /// tocado es el agregado del PV de id MENOR (el destino semántico) — jamás el del PV de id
    /// MAYOR (el origen semántico). Retiene esa fila y confirma, vía el mismo mecanismo empírico a
    /// nivel relación que <c>VentaEscrituraLoteTests.EmitirObservandoOrdenDeLocksAsync</c>, que
    /// ningún statement contra <c>stock_lotes</c> corrió todavía mientras la transacción espera
    /// ahí — "todo el origen primero" habría procesado agregado Y lote del origen antes de llegar
    /// a esa fila.</summary>
    [Fact]
    public async Task ElOrdenDeLocksDeUnaTransferenciaConLoteEsUnaUnicaSecuenciaAscendentePorPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElOrdenDeLocksDeUnaTransferenciaConLoteEsUnaUnicaSecuenciaAscendentePorPuntoDeVenta));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-orden-de-locks", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-ORDEN", VencimientoLejanoFuturo);

        // Caso discriminante: origen = PV de id MAYOR (ctx.IdPuntoVentaDestino), destino = PV de
        // id MENOR (ctx.IdPuntoVentaOrigen) — dirección INVERSA de la fixture a propósito.
        var idPuntoVentaConIdMayor = ctx.IdPuntoVentaDestino;
        var idPuntoVentaConIdMenor = ctx.IdPuntoVentaOrigen;
        Assert.True(idPuntoVentaConIdMenor < idPuntoVentaConIdMayor);

        await SembrarStockLoteAsync(ctx, idPuntoVentaConIdMayor, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idPuntoVentaConIdMayor, idArticulo, 20m);
        await SembrarStockAgregadoAsync(ctx, idPuntoVentaConIdMenor, idArticulo, 0m);

        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);
        await db.Database.OpenConnectionAsync();
        var conexionTransferencia = (NpgsqlConnection)db.Database.GetDbConnection();
        var pidTransferencia = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexionTransferencia).ExecuteScalarAsync())!;

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDeLotes = new ServicioDeLotes(db, reloj, contexto);
        var servicioDeStock = new ServicioDeStock(db, reloj, contexto, servicioDeLotes);

        var solicitud = new SolicitudDeTransferencia(
            idPuntoVentaConIdMayor, idPuntoVentaConIdMenor, "Orden de locks",
            [new LineaDeTransferencia(idArticulo, 5m, idLote)]);

        // Retiene el PRIMER elemento del orden ascendente correcto (stock del DESTINO, id menor,
        // el agregado) — deliberadamente sin comitear, para forzar a la transferencia a
        // bloquearse ahí mismo, en el primer statement de la transacción completa.
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
            "SELECT cantidad FROM stock WHERE id_articulo = $1 AND id_punto_venta = $2 FOR UPDATE",
            conexionBloqueo, transaccionBloqueo))
        {
            comandoBloqueo.Parameters.AddWithValue(idArticulo);
            comandoBloqueo.Parameters.AddWithValue(idPuntoVentaConIdMenor);
            await comandoBloqueo.ExecuteScalarAsync();
        }

        var tareaTransferencia = servicioDeStock.TransferirAsync(solicitud);

        await using var conexionPoll = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionPoll.OpenAsync();

        var observado = false;
        var stockLotesYaTocado = true;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            // Mismo mecanismo empírico que VentaEscrituraLoteTests.EmitirObservandoOrdenDeLocksAsync
            // (chequeo a nivel RELACIÓN, nunca 'tuple' directo sobre la fila en pugna — comprobado
            // ahí que el bloqueo real aparece como una espera de transactionid, no como una fila
            // tuple con granted=false): mientras el backend de la transferencia está bloqueado en
            // el PRIMER elemento del orden (stock del destino, id menor), NINGÚN statement contra
            // stock_lotes pudo haber corrido todavía — "todo el origen primero" habría procesado
            // agregado Y lote del origen (id mayor) ANTES de llegar acá, dejando una entrada de
            // relación 'stock_lotes' ya presente.
            await using var comandoPoll = new NpgsqlCommand(
                "SELECT " +
                "  bool_or(l.locktype = 'relation' AND l.relation::regclass::text = 'stock_lotes') AS stock_lotes_tocado, " +
                "  bool_or(NOT l.granted) AS esperando_algo " +
                "FROM pg_locks l WHERE l.pid = $1",
                conexionPoll);
            comandoPoll.Parameters.AddWithValue(pidTransferencia);

            await using var lector = await comandoPoll.ExecuteReaderAsync();
            if (await lector.ReadAsync())
            {
                var stockLotesTocado = !lector.IsDBNull(0) && lector.GetBoolean(0);
                var esperandoAlgo = !lector.IsDBNull(1) && lector.GetBoolean(1);
                if (esperandoAlgo)
                {
                    observado = true;
                    stockLotesYaTocado = stockLotesTocado;
                    break;
                }
            }

            await Task.Delay(25);
        }

        await transaccionBloqueo.RollbackAsync();
        var resultado = await tareaTransferencia;

        Assert.True(observado, "Nunca se observó a la transferencia bloqueada esperando la fila retenida.");
        Assert.False(
            stockLotesYaTocado,
            "stock_lotes ya fue tocado mientras la transferencia esperaba la PRIMERA fila del orden — " +
            "prueba que el origen se procesó antes que el destino (bug 'todo el origen primero').");
        Assert.Single(resultado.Lineas);

        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idPuntoVentaConIdMayor, idArticulo, idLote));
        Assert.Equal(5m, await LeerStockLoteAsync(ctx, idPuntoVentaConIdMenor, idArticulo, idLote));
    }

    // ---- task 10.12: joint proof checkout × transferencia (cierra el pairing de la task 8.7) ------

    /// <summary>spec stock: "A concurrent checkout and reverse transfer of the same articulo and
    /// lots do not deadlock" — cierra el pairing dejado abierto en <c>VentaEscrituraLoteTests</c>,
    /// task 8.7 (ver la nota de esa task en tasks.md: "la mitad conjunta del proof queda diferida a
    /// cuando la transferencia sea lot-aware — task 10.12"). Un checkout vendiendo del lote 7 del
    /// artículo 40 en PV1, CONCURRENTE con una transferencia moviendo el MISMO lote 7 del MISMO
    /// artículo desde PV1 hacia PV2 — ambas construyen el MISMO orden ascendente total
    /// <c>(id_articulo, id_punto_venta, id_lote NULLS FIRST)</c> sobre las filas que tocan (design:
    /// "identically at all three write sites"), así que ninguna puede formar un ciclo con la otra.
    /// <c>CountdownEvent(2)</c> fuerza el rendezvous — ambas transacciones abiertas antes de que
    /// cualquiera intente su primer lock.</summary>
    [Fact]
    public async Task UnCheckoutYUnaTransferenciaConcurrentesDelMismoArticuloYLoteNoDeadlockean()
    {
        var ctx = await PrepararAsync(nameof(UnCheckoutYUnaTransferenciaConcurrentesDelMismoArticuloYLoteNoDeadlockean));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-joint-proof", 100m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-JOINT-7", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, 20m);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousDeTransaccion(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitudVenta = new SolicitudDeVenta(
            ctx.IdPuntoVentaOrigen, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);
        var solicitudTransferencia = SolicitudDeUnaLinea(ctx, idArticulo, 1m, idLote);

        var tareaVenta = cliente.PostAsJsonAsync("/api/ventas", solicitudVenta);
        var tareaTransferencia = cliente.PostAsJsonAsync("/api/stock/transferencias", solicitudTransferencia);

        await Task.WhenAll(tareaVenta, tareaTransferencia);

        var respuestaVenta = await tareaVenta;
        var respuestaTransferencia = await tareaTransferencia;
        var cuerpoVenta = await respuestaVenta.Content.ReadAsStringAsync();
        var cuerpoTransferencia = await respuestaTransferencia.Content.ReadAsStringAsync();

        Assert.True(respuestaVenta.StatusCode == HttpStatusCode.Created, cuerpoVenta);
        Assert.True(respuestaTransferencia.StatusCode == HttpStatusCode.OK, cuerpoTransferencia);

        // 20 iniciales - 1 (venta) - 1 (transferencia) = 18 en origen; 1 llegó a destino.
        Assert.Equal(18m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo, idLote));
        Assert.Equal(1m, await LeerStockLoteAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo, idLote));
        Assert.Equal(18m, await LeerStockAsync(ctx, ctx.IdPuntoVentaOrigen, idArticulo));
        Assert.Equal(1m, await LeerStockAsync(ctx, ctx.IdPuntoVentaDestino, idArticulo));
    }
}
