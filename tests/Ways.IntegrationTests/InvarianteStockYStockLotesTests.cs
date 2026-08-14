using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 12 (task 12.12): la SUITE DE INVARIANTES cross-cutting —
/// ahora provable end to end con los OCHO motivos vivos (Venta, Compra, Anulacion, Ajuste,
/// Transferencia, Inventario, Decomiso, Reclasificacion). Tres tests long-form, cada uno asertando
/// la igualdad DESPUÉS DE CADA PASO de la secuencia (no solo al final, mismo criterio que
/// <see cref="SaldoLedgerInvarianteTests"/>):
/// <list type="number">
/// <item><see cref="LaCantidadDeStockEsLaSumaDeSusMovimientosTrasUnaSecuenciaConLosOchoMotivos"/> —
/// <c>stock.cantidad = SUM(movimientos)</c> (spec stock: "Cantidad Is Always The Sum Of Its
/// Movimientos"), incluyendo un par de <c>reclasificacion</c>.</item>
/// <item><see cref="StockLotesCantidadEsLaSumaDeSusMovimientosConEseLoteTrasLaCadenaCompraVentaTransferenciaNcxAnulacionConteoDecomiso"/> —
/// <c>stock_lotes.cantidad = SUM(movimientos con ese id_lote)</c> (spec lotes-y-vencimientos: "Stock
/// Lotes Balance And Its Two Invariants") tras compra→venta→transferencia→NCX→anulación→conteo→decomiso.</item>
/// <item><see cref="LaSumaDeStockLotesIgualaElAgregadoParaUnParLoteEfectivoReconciliado"/> —
/// <c>SUM(stock_lotes) = stock.cantidad</c> para un par lote-efectivo reconciliado.</item>
/// </list>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class InvarianteStockYStockLotesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdEmpresa, int IdPuntoVentaOrigen, int IdPuntoVentaDestino, HttpClient Admin,
        int IdArea, int IdAlicuotaIva, int IdListaPrecio, int IdMedioEfectivo, int IdCliente,
        int IdProveedor, int IdTipoCFA);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Invariante-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Invariante", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Segundo punto de venta REAL — destino de la transferencia de la secuencia.
        var puntoVentaDestino = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local invariante 2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVentaDestino);
        await db.SaveChangesAsync();

        // El checkout (TX/NCX) exige un turno abierto en el punto de venta de origen.
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
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente invariante",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 1_000_000m,
            CreditoIlimitado = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        // condiciones_fiscales es catálogo de PLATAFORMA (sin id_tenant) — el alta necesita el
        // contexto de plataforma, mismo criterio que ComprasRecepcionDeLotesTests.PrepararAsync.
        int idCondicionFiscalProveedor;
        await using (var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var condicionFiscalProveedor = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
            dbPlataforma.CondicionesFiscales.Add(condicionFiscalProveedor);
            await dbPlataforma.SaveChangesAsync();
            idCondicionFiscalProveedor = condicionFiscalProveedor.Id;
        }

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"Proveedor {nombre}", IdCondicionFiscal = idCondicionFiscalProveedor,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        // Módulo de lotes ON a nivel empresa — necesario para el paso de reclasificación de los
        // tres tests (aunque los tests 1/2 solo lo usan al final o desde el arranque).
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVentaDestino.Id, admin, area.Id,
            idAlicuotaIva, lista.Id, idMedioEfectivo, cliente.Id, proveedor.Id, idTipoCFA);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, bool controlaLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = controlaLote, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = 100m,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    /// <summary>Flip <c>ControlaLote</c> DIRECTO por EF (no vía <c>ServicioDeArticulos</c>) — mismo
    /// criterio que <c>ReconciliacionTests</c>: no dispara la reconciliación automática, así que el
    /// test controla explícitamente CUÁNDO corre (vía <see cref="ReconciliarAsync"/>).</summary>
    private async Task FlipControlaLoteAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.ControlaLote = true;
        await db.SaveChangesAsync();
    }

    private static SolicitudDeCompra SolicitudCompra(
        Contexto ctx, int idArticulo, decimal unidades, string? codigoLote, DateOnly? fechaVencimiento) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVentaOrigen, $"NE-{Guid.NewGuid():N}",
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(
                    idArticulo, "Item invariante", unidades, null, null, 10m, 0m, ctx.IdAlicuotaIva,
                    ActualizaCosto: true, CodigoLote: codigoLote, FechaVencimiento: fechaVencimiento)
            ]);

    private static async Task<CompraDetalle> CrearYConfirmarCompraAsync(
        Contexto ctx, int idArticulo, decimal unidades, string? codigoLote, DateOnly? fechaVencimiento)
    {
        var borrador = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudCompra(ctx, idArticulo, unidades, codigoLote, fechaVencimiento));
        var cuerpoBorrador = await borrador.Content.ReadAsStringAsync();
        Assert.True(borrador.StatusCode == HttpStatusCode.Created, cuerpoBorrador);
        var detalle = JsonSerializer.Deserialize<CompraDetalle>(cuerpoBorrador, OpcionesJson)!;

        var confirmar = await ctx.Admin.PostAsync($"/api/compras/{detalle.Id}/confirmar", null);
        var cuerpoConfirmar = await confirmar.Content.ReadAsStringAsync();
        Assert.True(confirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static SolicitudDeVenta SolicitudTx(Contexto ctx, int idArticulo, decimal cantidad, int? idLote) =>
        new(
            ctx.IdPuntoVentaOrigen, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, cantidad * 100m, null, 0m)],
            null, null);

    private static SolicitudDeVenta SolicitudNcx(Contexto ctx, int idArticulo, decimal cantidad, int? idLote) =>
        new(
            ctx.IdPuntoVentaOrigen, ctx.IdCliente, "NCX", null,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [],
            null, null);

    private static async Task<ComprobanteEmitido> EmitirAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<ComprobanteEmitido> AnularVentaAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task TransferirAsync(Contexto ctx, int idArticulo, decimal cantidad, int? idLote)
    {
        var solicitud = new SolicitudDeTransferencia(
            ctx.IdPuntoVentaOrigen, ctx.IdPuntoVentaDestino, "Transferencia invariante",
            [new LineaDeTransferencia(idArticulo, cantidad, idLote)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private static async Task<ResultadoConteo> ContarAgregadoAsync(Contexto ctx, int idArticulo, decimal contada)
    {
        var solicitud = new SolicitudDeConteo(ctx.IdPuntoVentaOrigen, idArticulo, contada, "Conteo invariante agregado");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResultadoConteo>(cuerpo, OpcionesJson)!;
    }

    private static async Task<ResultadoConteo> ContarPorLoteAsync(Contexto ctx, int idArticulo, int idLote, decimal contada)
    {
        var solicitud = new SolicitudDeConteo(
            ctx.IdPuntoVentaOrigen, idArticulo, null, "Conteo invariante por lote", [new ConteoDeLote(idLote, contada)]);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/conteos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResultadoConteo>(cuerpo, OpcionesJson)!;
    }

    private static async Task DecomisarAsync(Contexto ctx, int idArticulo, int? idLote, decimal cantidad)
    {
        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVentaOrigen, idArticulo, idLote, cantidad, "Decomiso invariante");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private static async Task AjustarAsync(Contexto ctx, int idArticulo, int? idLote, decimal cantidad)
    {
        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVentaOrigen, idArticulo, cantidad, "Ajuste invariante", idLote);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private static async Task<ResultadoDeReconciliacion> ReconciliarAsync(Contexto ctx, int idArticulo, int idPuntoVenta)
    {
        var solicitud = new SolicitudDeReconciliacion(idArticulo, idPuntoVenta);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/lotes/reconciliacion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResultadoDeReconciliacion>(cuerpo, OpcionesJson)!;
    }

    private async Task<decimal> LeerStockAsync(Contexto ctx, int idArticulo, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
    }

    private async Task<decimal> LeerStockLoteAsync(Contexto ctx, int idArticulo, int idPuntoVenta, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == idPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstOrDefaultAsync();
    }

    /// <summary>El invariante 1 en sí (spec stock: "Cantidad Is Always The Sum Of Its
    /// Movimientos"): <c>stock.cantidad</c> == Σ <c>movimientos_stock.cantidad</c> del par
    /// (articulo, punto de venta), leído directo de la base.</summary>
    private async Task AssertInvarianteStockAsync(Contexto ctx, int idArticulo, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == idPuntoVenta)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(sumaDeMovimientos, cantidad);
    }

    /// <summary>El invariante 2 en sí (spec lotes-y-vencimientos: "Stock Lotes Balance And Its Two
    /// Invariants"): <c>stock_lotes.cantidad</c> == Σ <c>movimientos_stock.cantidad</c> de ESE
    /// <c>id_lote</c> en ESE punto de venta.</summary>
    private async Task AssertInvarianteStockLoteAsync(Contexto ctx, int idArticulo, int idPuntoVenta, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == idPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstOrDefaultAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == idPuntoVenta && m.IdLote == idLote)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(sumaDeMovimientos, cantidad);
    }

    /// <summary>El invariante 3 en sí: para un par (articulo, PV) lote-efectivo y reconciliado,
    /// Σ <c>stock_lotes.cantidad</c> == <c>stock.cantidad</c>.</summary>
    private async Task AssertSumaDeStockLotesIgualaAlAgregadoAsync(Contexto ctx, int idArticulo, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var agregado = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
        var sumaDeLotes = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == idPuntoVenta)
            .SumAsync(sl => sl.Cantidad);
        Assert.Equal(agregado, sumaDeLotes);
    }

    // ---- invariante 1: stock.cantidad = SUM(movimientos), los OCHO motivos ------------------------

    [Fact]
    public async Task LaCantidadDeStockEsLaSumaDeSusMovimientosTrasUnaSecuenciaConLosOchoMotivos()
    {
        var ctx = await PrepararAsync(nameof(LaCantidadDeStockEsLaSumaDeSusMovimientosTrasUnaSecuenciaConLosOchoMotivos));
        // NO lote-efectivo hasta el último paso — así los primeros ocho movimientos ejercitan el
        // camino agregado puro de los siete motivos de escritura previos a esta etapa, y el
        // noveno (el flip) agrega Reclasificacion sin re-derivar nada de lo anterior.
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-invariante-8-motivos", controlaLote: false);

        // 1. Ajuste: +100.
        await AjustarAsync(ctx, idArticulo, idLote: null, 100m);
        Assert.Equal(100m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 2. Compra: +50.
        await CrearYConfirmarCompraAsync(ctx, idArticulo, 50m, codigoLote: null, fechaVencimiento: null);
        Assert.Equal(150m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 3. Venta (TX): -20.
        var tx = await EmitirAsync(ctx, SolicitudTx(ctx, idArticulo, 20m, idLote: null));
        Assert.Equal(130m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 4. Transferencia: -10 en el origen (el +10 del destino es un stock row distinto, fuera
        // del alcance de este invariante puntual).
        await TransferirAsync(ctx, idArticulo, 10m, idLote: null);
        Assert.Equal(120m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 5. NCX (devolución): +5 — mismo motivo Venta que la TX, sign flip por tipo comprobante.
        await EmitirAsync(ctx, SolicitudNcx(ctx, idArticulo, 5m, idLote: null));
        Assert.Equal(125m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 6. Anulación: reversa exacta de la TX del paso 3, +20.
        await AnularVentaAsync(ctx, tx.Id);
        Assert.Equal(145m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 7. Conteo (Inventario): 145 → 150, +5.
        await ContarAgregadoAsync(ctx, idArticulo, 150m);
        Assert.Equal(150m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 8. Decomiso: -8.
        await DecomisarAsync(ctx, idArticulo, idLote: null, 8m);
        Assert.Equal(142m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // 9. Reclasificación: flip controla_lote (sin stock_lotes previo) → residuo = 142, par neto
        // cero (design decisión 14: stock NUNCA se toca acá) — el invariante tiene que seguir
        // sosteniéndose exactamente igual.
        await FlipControlaLoteAsync(ctx, idArticulo);
        var resultado = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);
        Assert.Equal(1, resultado.ParesReconciliados);
        Assert.Equal(142m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertInvarianteStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var motivosPresentes = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen)
            .Select(m => m.Motivo).Distinct().ToListAsync();
        Assert.Equal(
            new[]
            {
                MotivoStock.Venta, MotivoStock.Compra, MotivoStock.Anulacion, MotivoStock.Ajuste,
                MotivoStock.Transferencia, MotivoStock.Inventario, MotivoStock.Decomiso, MotivoStock.Reclasificacion
            }.OrderBy(m => m),
            motivosPresentes.OrderBy(m => m));
    }

    // ---- invariante 2: stock_lotes.cantidad = SUM(movimientos con ese lote), la cadena de 12.12 ---

    [Fact]
    public async Task StockLotesCantidadEsLaSumaDeSusMovimientosConEseLoteTrasLaCadenaCompraVentaTransferenciaNcxAnulacionConteoDecomiso()
    {
        var ctx = await PrepararAsync(
            nameof(StockLotesCantidadEsLaSumaDeSusMovimientosConEseLoteTrasLaCadenaCompraVentaTransferenciaNcxAnulacionConteoDecomiso));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-invariante-cadena", controlaLote: true);

        // 1. Compra: +30 del lote L (get-or-create lo crea).
        var confirmada = await CrearYConfirmarCompraAsync(ctx, idArticulo, 30m, "L-INV8", VencimientoLejanoFuturo);
        var idLote = confirmada.Items[0].IdLote;
        Assert.NotNull(idLote);
        Assert.Equal(30m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote!.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        // 2. Venta (TX): -5 del lote L, explícito.
        var tx = await EmitirAsync(ctx, SolicitudTx(ctx, idArticulo, 5m, idLote));
        Assert.Equal(25m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        // 3. Transferencia: -10 del lote L en origen (el destino recibe su propia fila, verificada
        // aparte por brevedad no repetida acá — TransferenciaLoteTests ya la cubre punta a punta).
        await TransferirAsync(ctx, idArticulo, 10m, idLote);
        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);
        // Bonus: el destino también sostiene el invariante sobre SU propia fila del mismo lote.
        Assert.Equal(10m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaDestino, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaDestino, idLote.Value);

        // 4. NCX (devolución): +3 al lote L, explícito (NCX exige idLote — nunca default FEFO).
        await EmitirAsync(ctx, SolicitudNcx(ctx, idArticulo, 3m, idLote));
        Assert.Equal(18m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        // 5. Anulación: reversa exacta de la TX del paso 2, +5.
        await AnularVentaAsync(ctx, tx.Id);
        Assert.Equal(23m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        // 6. Conteo por lote: 23 → 30, +7.
        await ContarPorLoteAsync(ctx, idArticulo, idLote.Value, 30m);
        Assert.Equal(30m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        // 7. Decomiso: -4 del lote L.
        await DecomisarAsync(ctx, idArticulo, idLote, 4m);
        Assert.Equal(26m, await LeerStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value));
        await AssertInvarianteStockLoteAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen, idLote.Value);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var motivosDelLoteEnOrigen = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVentaOrigen && m.IdLote == idLote)
            .Select(m => m.Motivo).Distinct().ToListAsync();
        Assert.Equal(
            new[]
            {
                MotivoStock.Compra, MotivoStock.Venta, MotivoStock.Transferencia, MotivoStock.Anulacion,
                MotivoStock.Inventario, MotivoStock.Decomiso
            }.OrderBy(m => m),
            motivosDelLoteEnOrigen.OrderBy(m => m));
    }

    // ---- invariante 3: SUM(stock_lotes) = stock.cantidad, par lote-efectivo reconciliado ----------

    [Fact]
    public async Task LaSumaDeStockLotesIgualaElAgregadoParaUnParLoteEfectivoReconciliado()
    {
        var ctx = await PrepararAsync(nameof(LaSumaDeStockLotesIgualaElAgregadoParaUnParLoteEfectivoReconciliado));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-invariante-reconciliado", controlaLote: false);

        // Stock preexistente ANTES de activar el control de lote (el escenario que la
        // reconciliación existe para resolver — decisión 3 del proposal).
        await AjustarAsync(ctx, idArticulo, idLote: null, 50m);
        Assert.Equal(50m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));

        await FlipControlaLoteAsync(ctx, idArticulo);
        var resultado = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);
        Assert.Equal(1, resultado.ParesReconciliados);

        // Tras la reconciliación: el residuo entero (50) quedó en el lote sin-identificar, el
        // agregado no se movió (decisión 14) — el invariante 3 tiene que sostenerse.
        Assert.Equal(50m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertSumaDeStockLotesIgualaAlAgregadoAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // Una recepción de un lote FECHADO nuevo, además del residuo sin-identificar — prueba que
        // el invariante se sostiene con una MEZCLA de lotes, no solo con el caso trivial de un
        // único lote.
        var confirmada = await CrearYConfirmarCompraAsync(ctx, idArticulo, 10m, "L-RECON", VencimientoLejanoFuturo);
        Assert.NotNull(confirmada.Items[0].IdLote);

        Assert.Equal(60m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertSumaDeStockLotesIgualaAlAgregadoAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);

        // Una segunda reconciliación sobre el mismo par es un no-op (idempotencia, decisión 13) —
        // el invariante sigue sosteniéndose byte-idéntico.
        var segunda = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);
        Assert.Equal(1, segunda.ParesSinResiduo);
        Assert.Equal(60m, await LeerStockAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen));
        await AssertSumaDeStockLotesIgualaAlAgregadoAsync(ctx, idArticulo, ctx.IdPuntoVentaOrigen);
    }
}
