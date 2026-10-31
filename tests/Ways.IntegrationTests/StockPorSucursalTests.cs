using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// El stock es de la sucursal. Escenario: las cajas <c>Local 1</c> y <c>Local 2</c> comparten una sucursal (la de
/// <c>Local 2</c> se reapunta a mano a la de <c>Local 1</c>, como hará la fusión de T5; la sucursal propia de
/// <c>Local 2</c> queda viva y sin cajas), y <c>Local 3</c> tiene la suya. Cada escritor resuelve la sucursal desde la
/// caja; cada reversa usa la sucursal guardada en el movimiento original.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class StockPorSucursalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, HttpClient Admin, int IdCaja1, int IdCaja2, int IdCaja3, int IdSucursalCompartida,
        int IdSucursalPropiaDeLaCaja2, int IdSucursal3, int IdArticulo, int IdCliente, int IdMedioEfectivo,
        int IdProveedor, int IdTipoCFA, int IdAlicuotaIva);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var caja2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 2", Modo = ModoPuntoVenta.Web, CreatedAt = ahora, UpdatedAt = ahora
        };
        var caja3 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 3", Modo = ModoPuntoVenta.Web, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.AddRange(caja2, caja3);
        await db.SaveChangesAsync();

        foreach (var idCaja in new[] { resultado.IdPuntoVenta, caja2.Id, caja3.Id })
        {
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = idCaja, IdEmpleadoApertura = resultado.IdUsuarioAdmin,
                FechaApertura = ahora, FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Sucursal-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista sucursal", EsDefault = false, Modo = ModoLista.Fija, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"suc-{Guid.NewGuid():N}", Nombre = "Yerba", IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente sucursal",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"Proveedor {nombre}", IdCondicionFiscal = idCondicionFiscal, Margen = 50m,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        db.Clientes.Add(cliente);
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = resultado.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = lista.Id, Monto = 10m,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var idSucursalCompartida = await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta);
        var idSucursalPropiaDeLaCaja2 = await SucursalDePrueba.DeAsync(db, caja2.Id);
        var idSucursal3 = await SucursalDePrueba.DeAsync(db, caja3.Id);

        await MoverCajaASucursalAsync(caja2.Id, idSucursalCompartida);

        return new Contexto(
            resultado.IdTenant, admin, resultado.IdPuntoVenta, caja2.Id, caja3.Id, idSucursalCompartida,
            idSucursalPropiaDeLaCaja2, idSucursal3, articulo.Id, cliente.Id, idMedioEfectivo, proveedor.Id, idTipoCFA,
            idAlicuotaIva);
    }

    private async Task MoverCajaASucursalAsync(int idPuntoVenta, int idSucursal)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = "UPDATE puntos_venta SET id_sucursal = $1 WHERE id_punto_venta = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    private static async Task AjustarAsync(Contexto ctx, int idCaja, decimal cantidad)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/ajustes", new SolicitudDeAjusteDeStock(idCaja, ctx.IdArticulo, cantidad, "Ajuste de prueba"));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PedirVentaAsync(Contexto ctx, int idCaja, decimal cantidad) =>
        ctx.Admin.PostAsJsonAsync(
            "/api/ventas",
            new SolicitudDeVenta(
                idCaja, ctx.IdCliente, "TX", null,
                [new LineaDeVenta(ctx.IdArticulo, cantidad, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, 10m * cantidad, null, 0m)],
                null, null));

    private static async Task<int> VenderAsync(Contexto ctx, int idCaja, decimal cantidad)
    {
        var respuesta = await PedirVentaAsync(ctx, idCaja, cantidad);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!.Id;
    }

    private static async Task<int> ComprarAsync(Contexto ctx, int idCaja, decimal cantidad, string numeroExterno)
    {
        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, idCaja, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Yerba", cantidad, null, null, 5m, 0m, ctx.IdAlicuotaIva, false)]);
        var creada = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await creada.Content.ReadAsStringAsync();
        Assert.True(creada.StatusCode == HttpStatusCode.Created, cuerpo);
        var id = JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!.Id;

        var confirmada = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        Assert.True(confirmada.StatusCode == HttpStatusCode.OK, await confirmada.Content.ReadAsStringAsync());
        return id;
    }

    private async Task<Dictionary<int, decimal>> StockPorSucursalAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock.Where(s => s.IdArticulo == ctx.IdArticulo).ToDictionaryAsync(s => s.IdSucursal, s => s.Cantidad);
    }

    private async Task<decimal> SumaDelLedgerAsync(Contexto ctx, int idSucursal)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticulo && m.IdSucursal == idSucursal)
            .SumAsync(m => m.Cantidad);
    }

    private static async Task<decimal> StockQueVeLaCajaAsync(Contexto ctx, int idCaja)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/stock?idPuntoVenta={idCaja}&idArticulo={ctx.IdArticulo}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!.Cantidad;
    }

    /// <summary>Cláusula: cada escritor resuelve la sucursal desde <c>puntos_venta.id_sucursal</c> de la caja. Ajuste,
    /// compra y venta desde las dos cajas de la sucursal compartida mueven UNA fila de stock; la sucursal propia de
    /// <c>Local 2</c> (viva, sin cajas) no recibe nada, y cada movimiento conserva la caja que lo originó.</summary>
    [Fact]
    public async Task DosCajasDeUnaSucursalVendenCompranYAjustanSobreUnaSolaFilaDeStock()
    {
        var ctx = await PrepararAsync(nameof(DosCajasDeUnaSucursalVendenCompranYAjustanSobreUnaSolaFilaDeStock));

        await AjustarAsync(ctx, ctx.IdCaja1, 10m);
        await ComprarAsync(ctx, ctx.IdCaja2, 5m, "0001-00000001");
        await VenderAsync(ctx, ctx.IdCaja2, 2m);
        await VenderAsync(ctx, ctx.IdCaja1, 1m);
        await AjustarAsync(ctx, ctx.IdCaja2, -1m);

        var stock = await StockPorSucursalAsync(ctx);
        Assert.Equal(new Dictionary<int, decimal> { [ctx.IdSucursalCompartida] = 11m }, stock);
        Assert.Equal(11m, await SumaDelLedgerAsync(ctx, ctx.IdSucursalCompartida));

        Assert.Equal(11m, await StockQueVeLaCajaAsync(ctx, ctx.IdCaja1));
        Assert.Equal(11m, await StockQueVeLaCajaAsync(ctx, ctx.IdCaja2));
        Assert.Equal(0m, await StockQueVeLaCajaAsync(ctx, ctx.IdCaja3));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticulo)
            .Select(m => new { m.IdSucursal, m.IdPuntoVenta, m.Motivo, m.Cantidad })
            .ToListAsync();
        Assert.Equal(5, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal(ctx.IdSucursalCompartida, m.IdSucursal));
        Assert.Equal(
            [(ctx.IdCaja1, -1m), (ctx.IdCaja1, 10m), (ctx.IdCaja2, -2m), (ctx.IdCaja2, -1m), (ctx.IdCaja2, 5m)],
            movimientos.Select(m => (m.IdPuntoVenta, m.Cantidad)).OrderBy(m => m.IdPuntoVenta).ThenBy(m => m.Cantidad).ToList());
    }

    /// <summary>Cláusula: la anulación de una venta revierte sobre la sucursal guardada en sus movimientos originales,
    /// no sobre la que la caja tenga al anular. <c>Local 3</c> vende desde su sucursal, se muda a la sucursal compartida y
    /// recién entonces se anula la venta.</summary>
    [Fact]
    public async Task LaAnulacionDeUnaVentaVuelveALaSucursalOriginalAunqueLaCajaSeHayaMudado()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeUnaVentaVuelveALaSucursalOriginalAunqueLaCajaSeHayaMudado));
        await AjustarAsync(ctx, ctx.IdCaja3, 10m);
        var idVenta = await VenderAsync(ctx, ctx.IdCaja3, 4m);

        await MoverCajaASucursalAsync(ctx.IdCaja3, ctx.IdSucursalCompartida);

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{idVenta}/anulacion", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());

        Assert.Equal(new Dictionary<int, decimal> { [ctx.IdSucursal3] = 10m }, await StockPorSucursalAsync(ctx));
        Assert.Equal(10m, await SumaDelLedgerAsync(ctx, ctx.IdSucursal3));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var reversa = await db.MovimientosStock.SingleAsync(m => m.IdComprobanteVenta == idVenta && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal((ctx.IdSucursal3, ctx.IdCaja3, 4m), (reversa.IdSucursal, reversa.IdPuntoVenta, reversa.Cantidad));
    }

    /// <summary>Cláusula: la anulación de una compra revierte sobre la sucursal guardada en su libro, no sobre la actual
    /// de la caja de recepción.</summary>
    [Fact]
    public async Task LaAnulacionDeUnaCompraVuelveALaSucursalOriginalAunqueLaCajaSeHayaMudado()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeUnaCompraVuelveALaSucursalOriginalAunqueLaCajaSeHayaMudado));
        var idCompra = await ComprarAsync(ctx, ctx.IdCaja3, 6m, "0001-00000002");

        await MoverCajaASucursalAsync(ctx.IdCaja3, ctx.IdSucursalCompartida);

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{idCompra}/anular", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());

        Assert.Equal(new Dictionary<int, decimal> { [ctx.IdSucursal3] = 0m }, await StockPorSucursalAsync(ctx));
        Assert.Equal(0m, await SumaDelLedgerAsync(ctx, ctx.IdSucursal3));
    }

    [Fact]
    public async Task UnaTransferenciaEntreDosCajasDeLaMismaSucursalSeRechazaCon409()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaEntreDosCajasDeLaMismaSucursalSeRechazaCon409));
        await AjustarAsync(ctx, ctx.IdCaja1, 10m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias",
            new SolicitudDeTransferencia(ctx.IdCaja1, ctx.IdCaja2, "Entre cajas", [new LineaDeTransferencia(ctx.IdArticulo, 3m)]));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transferencia_misma_sucursal", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.Motivo == MotivoStock.Transferencia));
        Assert.Equal(new Dictionary<int, decimal> { [ctx.IdSucursalCompartida] = 10m }, await StockPorSucursalAsync(ctx));
    }

    /// <summary>Una transferencia entre sucursales distintas sigue funcionando y deja la sucursal de destino en las dos
    /// filas espejadas.</summary>
    [Fact]
    public async Task UnaTransferenciaEntreSucursalesMueveAmbosSaldosYGuardaLaSucursalDeDestino()
    {
        var ctx = await PrepararAsync(nameof(UnaTransferenciaEntreSucursalesMueveAmbosSaldosYGuardaLaSucursalDeDestino));
        await AjustarAsync(ctx, ctx.IdCaja2, 10m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias",
            new SolicitudDeTransferencia(ctx.IdCaja2, ctx.IdCaja3, "A otra sucursal", [new LineaDeTransferencia(ctx.IdArticulo, 3m)]));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(
            new Dictionary<int, decimal> { [ctx.IdSucursalCompartida] = 7m, [ctx.IdSucursal3] = 3m },
            await StockPorSucursalAsync(ctx));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.MovimientosStock.Where(m => m.Motivo == MotivoStock.Transferencia).ToListAsync();
        Assert.Equal(2, filas.Count);
        Assert.All(filas, m => Assert.Equal((ctx.IdCaja3, ctx.IdSucursal3), (m.IdPuntoVentaDestino!.Value, m.IdSucursalDestino!.Value)));
    }

    /// <summary>Ventas concurrentes desde las dos cajas de la sucursal sobre el mismo artículo: el upsert atómico toma el
    /// lock de la única fila, así que ninguna escritura se pierde y el saldo sigue igual a la suma del ledger.</summary>
    [Fact]
    public async Task VentasConcurrentesDesdeLasDosCajasNoPierdenNingunaEscritura()
    {
        var ctx = await PrepararAsync(nameof(VentasConcurrentesDesdeLasDosCajasNoPierdenNingunaEscritura));
        await AjustarAsync(ctx, ctx.IdCaja1, 20m);

        var respuestas = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => PedirVentaAsync(ctx, i % 2 == 0 ? ctx.IdCaja1 : ctx.IdCaja2, 1m)));

        foreach (var respuesta in respuestas)
        {
            Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
        }

        Assert.Equal(new Dictionary<int, decimal> { [ctx.IdSucursalCompartida] = 10m }, await StockPorSucursalAsync(ctx));
        Assert.Equal(10m, await SumaDelLedgerAsync(ctx, ctx.IdSucursalCompartida));
    }
}
