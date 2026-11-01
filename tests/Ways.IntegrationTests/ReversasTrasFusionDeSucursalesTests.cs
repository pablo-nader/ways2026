using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// Una reversa (anulación de venta, de remito o de compra, y la edición de una compra confirmada) de un documento
/// asentado antes de una fusión de sucursales asienta en la sucursal que absorbió a la guardada en sus movimientos,
/// nunca en el origen fusionado. Escenario: <c>Local 1</c> en la sucursal destino y <c>Local 2</c> en el origen.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReversasTrasFusionDeSucursalesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdAdmin, HttpClient Admin, int IdCaja1, int IdCaja2, int IdDestino,
        int IdOrigen, int IdArea, int IdAlicuotaIva, int IdLista, int IdCliente, int IdMedioEfectivo, int IdProveedor,
        int IdTipoCompra);

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

        var caja2 = await LeerAsync<PuntoVentaListado>(
            await admin.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(resultado.IdEmpresa, "Local 2", ModoPuntoVenta.Web)),
            HttpStatusCode.Created);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        foreach (var idCaja in new[] { resultado.IdPuntoVenta, caja2.Id })
        {
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = idCaja, IdEmpleadoApertura = resultado.IdUsuarioAdmin,
                FechaApertura = ahora, FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area reversa", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista reversa", EsDefault = false, Modo = ModoLista.Fija, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente reversa",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre} proveedor", IdCondicionFiscal = idCondicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idTipoCompra = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, admin, resultado.IdPuntoVenta, caja2.Id,
            await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta), caja2.IdSucursal, area.Id, idAlicuotaIva,
            lista.Id, cliente.Id, idMedioEfectivo, proveedor.Id, idTipoCompra);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> FusionarAsync(Contexto ctx, int idOrigen, int idDestino) =>
        ctx.Admin.PostAsJsonAsync($"/api/sucursales/{idOrigen}/fusionar", new SucursalFusion(idDestino));

    private static async Task FusionarOkAsync(Contexto ctx, int idOrigen, int idDestino) =>
        await LeerAsync<SucursalListado>(await FusionarAsync(ctx, idOrigen, idDestino), HttpStatusCode.OK);

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, bool controlaLote = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = ctx.IdArea,
            IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, ControlaLote = controlaLote,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdLista, Monto = 10m,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task HabilitarLotesAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Parametros.Add(new Parametro
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, IdPuntoVenta = null, Clave = "lotes_habilitado",
            Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task AsentarAsync(Contexto ctx, int idSucursal, int idCaja, int idArticulo, decimal cantidad)
    {
        await using var escritura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        await using var transaccion = await escritura.Database.BeginTransactionAsync();
        await EscritorDeStock.AsentarAsync(
            escritura.Database.GetDbConnection(), transaccion.GetDbTransaction(),
            [new AsientoDeStock(ctx.IdTenant, idSucursal, idCaja, idArticulo, cantidad, MotivoStock.Ajuste, ctx.IdAdmin, DateTimeOffset.UtcNow)],
            CancellationToken.None);
        await transaccion.CommitAsync();
    }

    private static Task<HttpResponseMessage> VenderAsync(Contexto ctx, int idCaja, int idArticulo, decimal cantidad) =>
        ctx.Admin.PostAsJsonAsync(
            "/api/ventas",
            new SolicitudDeVenta(
                idCaja, ctx.IdCliente, "TX", null,
                [new LineaDeVenta(idArticulo, cantidad, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, cantidad * 10m, null, 0m)],
                null, null));

    private static async Task<int> VenderOkAsync(Contexto ctx, int idCaja, int idArticulo, decimal cantidad) =>
        (await LeerAsync<ComprobanteEmitido>(await VenderAsync(ctx, idCaja, idArticulo, cantidad), HttpStatusCode.Created)).Id;

    private static Task<HttpResponseMessage> AnularVentaAsync(Contexto ctx, int idVenta) =>
        ctx.Admin.PostAsync($"/api/ventas/{idVenta}/anulacion", null);

    private static SolicitudDeCompra NuevaCompra(Contexto ctx, int idArticulo, decimal unidades, string? lote = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCompra, ctx.IdCaja2, $"0001-{Random.Shared.Next(1, 99_999_999):D8}",
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(
                    idArticulo, "Item reversa", unidades, null, null, 100m, 0m, ctx.IdAlicuotaIva, false,
                    CodigoLote: lote, FechaVencimiento: lote is null ? null : DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90))
            ]);

    private static async Task<int> ComprarAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var borrador = await LeerAsync<CompraDetalle>(await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud), HttpStatusCode.Created);
        await LeerAsync<CompraDetalle>(await ctx.Admin.PostAsync($"/api/compras/{borrador.Id}/confirmar", null), HttpStatusCode.OK);
        return borrador.Id;
    }

    private async Task<Dictionary<(int Sucursal, int Articulo), decimal>> StockAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock.ToDictionaryAsync(s => (s.IdSucursal, s.IdArticulo), s => s.Cantidad);
    }

    private async Task<Dictionary<(int Sucursal, int Articulo, int Lote), decimal>> LotesAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes.ToDictionaryAsync(s => (s.IdSucursal, s.IdArticulo, s.IdLote), s => s.Cantidad);
    }

    /// <summary>Los dos invariantes del ledger, por sucursal y artículo: <c>stock = Σ movimientos</c> y, donde hay lotes,
    /// <c>Σ stock_lotes = stock</c>.</summary>
    private async Task AssertInvariantesAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stock = await db.Stock.ToListAsync();
        var ledger = await db.MovimientosStock
            .GroupBy(m => new { m.IdSucursal, m.IdArticulo })
            .Select(g => new { g.Key.IdSucursal, g.Key.IdArticulo, Suma = g.Sum(m => m.Cantidad) })
            .ToDictionaryAsync(g => (g.IdSucursal, g.IdArticulo), g => g.Suma);
        var lotes = await db.StockLotes
            .GroupBy(l => new { l.IdSucursal, l.IdArticulo })
            .Select(g => new { g.Key.IdSucursal, g.Key.IdArticulo, Suma = g.Sum(l => l.Cantidad) })
            .ToDictionaryAsync(g => (g.IdSucursal, g.IdArticulo), g => g.Suma);

        Assert.NotEmpty(stock);
        foreach (var fila in stock)
        {
            Assert.Equal(fila.Cantidad, ledger.GetValueOrDefault((fila.IdSucursal, fila.IdArticulo)));
            if (lotes.TryGetValue((fila.IdSucursal, fila.IdArticulo), out var sumaDeLotes))
            {
                Assert.Equal(fila.Cantidad, sumaDeLotes);
            }
        }
    }

    /// <summary>Cláusula bajo prueba: la anulación de la venta resuelve la sucursal guardada en su movimiento (el origen,
    /// ya fusionado) con <see cref="EscritorDeStock.ResolverSucursalesDeReversaAsync"/>. Sin esa resolución el stock
    /// volvería al origen dado de baja y el destino quedaría corto.</summary>
    [Fact]
    public async Task AnularUnaVentaPreviaALaFusionDevuelveElStockAlDestino()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaVentaPreviaALaFusionDevuelveElStockAlDestino));
        var articulo = await SembrarArticuloAsync(ctx, "venta");
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 10m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, articulo, 5m);
        var venta = await VenderOkAsync(ctx, ctx.IdCaja2, articulo, 3m);

        await FusionarOkAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        await LeerAsync<JsonElement>(await AnularVentaAsync(ctx, venta), HttpStatusCode.OK);

        var stock = await StockAsync(ctx);
        Assert.Equal(15m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>Cláusula bajo prueba: la resolución sigue la cadena de fusiones entera (S1→S2 y después S2→S3), no solo
    /// el primer tramo.</summary>
    [Fact]
    public async Task AnularTrasDosFusionesEncadenadasDevuelveElStockALaUltima()
    {
        var ctx = await PrepararAsync(nameof(AnularTrasDosFusionesEncadenadasDevuelveElStockALaUltima));
        var articulo = await SembrarArticuloAsync(ctx, "cadena");
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 10m);
        var venta = await VenderOkAsync(ctx, ctx.IdCaja2, articulo, 4m);

        var tercera = await LeerAsync<SucursalListado>(
            await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(ctx.IdEmpresa, "Tercera")), HttpStatusCode.Created);
        await LeerAsync<PuntoVentaListado>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "Local 3", ModoPuntoVenta.Web, IdSucursal: tercera.Id)),
            HttpStatusCode.Created);

        await FusionarOkAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        await FusionarOkAsync(ctx, ctx.IdDestino, tercera.Id);
        await LeerAsync<JsonElement>(await AnularVentaAsync(ctx, venta), HttpStatusCode.OK);

        var stock = await StockAsync(ctx);
        Assert.Equal(10m, stock[(tercera.Id, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdDestino, articulo)]);
        await AssertInvariantesAsync(ctx);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fusiones = await db.Sucursales.IgnoreQueryFilters()
            .Where(s => s.Id == ctx.IdOrigen || s.Id == ctx.IdDestino || s.Id == tercera.Id)
            .ToDictionaryAsync(s => s.Id, s => s.IdSucursalFusion);
        Assert.Equal(ctx.IdDestino, fusiones[ctx.IdOrigen]);
        Assert.Equal(tercera.Id, fusiones[ctx.IdDestino]);
        Assert.Null(fusiones[tercera.Id]);
    }

    /// <summary>Cláusula bajo prueba: la anulación del remito resuelve la sucursal guardada en su movimiento.</summary>
    [Fact]
    public async Task AnularUnRemitoPrevioALaFusionDevuelveElStockAlDestino()
    {
        var ctx = await PrepararAsync(nameof(AnularUnRemitoPrevioALaFusionDevuelveElStockAlDestino));
        var articulo = await SembrarArticuloAsync(ctx, "remito");
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 10m);

        var borrador = await LeerAsync<RemitoDetalle>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/remitos", new SolicitudDeRemito(ctx.IdCaja2, ctx.IdCliente, null, null, [new LineaDeRemito(articulo, 2m, null)])),
            HttpStatusCode.Created);
        await LeerAsync<RemitoDetalle>(await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", null), HttpStatusCode.OK);
        Assert.Equal(8m, (await StockAsync(ctx))[(ctx.IdOrigen, articulo)]);

        await FusionarOkAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        await LeerAsync<RemitoDetalle>(await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/anular", null), HttpStatusCode.OK);

        var stock = await StockAsync(ctx);
        Assert.Equal(10m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>Cláusula bajo prueba: la anulación de la compra resuelve la sucursal guardada, también para el saldo del
    /// lote, que la fusión llevó al destino bajo el mismo <c>id_lote</c>. Sin la resolución el origen quedaría en −6 y la
    /// anulación rechazaría con stock negativo.</summary>
    [Fact]
    public async Task AnularUnaCompraConLotePreviaALaFusionDescuentaDelDestinoYDeSuLote()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraConLotePreviaALaFusionDescuentaDelDestinoYDeSuLote));
        await HabilitarLotesAsync(ctx);
        var articulo = await SembrarArticuloAsync(ctx, "compra-lote", controlaLote: true);
        var compra = await ComprarAsync(ctx, NuevaCompra(ctx, articulo, 6m, lote: "L-REV"));

        await FusionarOkAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        var lotesFusionados = await LotesAsync(ctx);
        var idLote = Assert.Single(lotesFusionados.Keys.Where(k => k.Sucursal == ctx.IdDestino)).Lote;
        Assert.Equal(6m, lotesFusionados[(ctx.IdDestino, articulo, idLote)]);

        var anulada = await ctx.Admin.PostAsync($"/api/compras/{compra}/anular", null);
        Assert.True(anulada.StatusCode == HttpStatusCode.OK, await anulada.Content.ReadAsStringAsync());

        var stock = await StockAsync(ctx);
        Assert.Equal(0m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        var lotes = await LotesAsync(ctx);
        Assert.Equal(0m, lotes[(ctx.IdDestino, articulo, idLote)]);
        Assert.Equal(0m, lotes[(ctx.IdOrigen, articulo, idLote)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>Cláusula bajo prueba: la edición de una compra confirmada resuelve la sucursal guardada en su libro, así
    /// que la diferencia se netea contra lo nuevo en el destino. Sin la resolución restaría 10 del origen en cero
    /// (<c>compra_edicion_stock_negativo</c>).</summary>
    [Fact]
    public async Task EditarUnaCompraPreviaALaFusionNeteaEnElDestino()
    {
        var ctx = await PrepararAsync(nameof(EditarUnaCompraPreviaALaFusionNeteaEnElDestino));
        var articulo = await SembrarArticuloAsync(ctx, "compra-edicion");
        var solicitud = NuevaCompra(ctx, articulo, 10m);
        var compra = await ComprarAsync(ctx, solicitud);

        await FusionarOkAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        var editada = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{compra}",
            solicitud with { Items = [solicitud.Items[0] with { Unidades = 4m }] });
        Assert.True(editada.StatusCode == HttpStatusCode.OK, await editada.Content.ReadAsStringAsync());

        var stock = await StockAsync(ctx);
        Assert.Equal(4m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>
    /// Una anulación en vuelo durante la fusión termina en el destino. La caja de la venta se mudó a una tercera
    /// sucursal, así que la fusión no la bloquea y solo la sucursal serializa a las dos. El owner retiene la fila del
    /// artículo con <c>FOR UPDATE</c>: la anulación ya resolvió su sucursal (el origen, todavía vivo) y espera en el FK
    /// de su primer movimiento sin haber tomado stock; recién entonces arranca la fusión.
    ///
    /// Cláusulas bajo prueba: el <c>FOR SHARE</c> de la reversa sobre su sucursal y el <c>FOR UPDATE</c> de la fusión
    /// sobre las dos sucursales. Con los dos, la fusión espera a la anulación y después lleva su stock al destino. Sin el
    /// <c>FOR UPDATE</c>, la fusión toma el stock y se deadlockea con la anulación; sin la resolución, la anulación
    /// asienta en el origen ya fusionado.
    /// </summary>
    [Fact]
    public async Task UnaAnulacionEnVueloDuranteLaFusionTerminaEnElDestino()
    {
        var ctx = await PrepararAsync(nameof(UnaAnulacionEnVueloDuranteLaFusionTerminaEnElDestino));
        var articulo = await SembrarArticuloAsync(ctx, "en-vuelo");
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 10m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, articulo, 5m);
        var venta = await VenderOkAsync(ctx, ctx.IdCaja2, articulo, 3m);

        var tercera = await LeerAsync<SucursalListado>(
            await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(ctx.IdEmpresa, "Tercera")), HttpStatusCode.Created);
        await SucursalDePrueba.ReapuntarAsync(fixture, ctx.IdCaja2, tercera.Id);

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var bloqueo = await owner.BeginTransactionAsync();
        await using (var comando = owner.CreateCommand())
        {
            comando.Transaction = bloqueo;
            comando.CommandText = "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE";
            comando.Parameters.Add(new NpgsqlParameter { Value = articulo });
            await comando.ExecuteScalarAsync();
        }

        var anulacion = AnularVentaAsync(ctx, venta);
        await EsperarEsperasDeLockAsync(1, anulacion);

        var fusion = FusionarAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        await EsperarEsperasDeLockAsync(2, anulacion, fusion);

        await bloqueo.CommitAsync();

        await LeerAsync<JsonElement>(await anulacion, HttpStatusCode.OK);
        await LeerAsync<SucursalListado>(await fusion, HttpStatusCode.OK);

        var stock = await StockAsync(ctx);
        Assert.Equal(15m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>Espera hasta que haya <paramref name="cantidad"/> backends esperando un lock. Si una de las requests
    /// termina antes, el escenario no se armó y la prueba falla: correría en serie y no probaría la carrera.</summary>
    private async Task EsperarEsperasDeLockAsync(int cantidad, params Task<HttpResponseMessage>[] requests)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        var limite = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < limite)
        {
            if (requests.FirstOrDefault(r => r.IsCompleted) is { } terminada)
            {
                var respuesta = await terminada;
                Assert.Fail(
                    $"Una request terminó antes de llegar a {cantidad} esperas de lock: " +
                    $"{respuesta.StatusCode} {await respuesta.Content.ReadAsStringAsync()}");
            }

            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'";
            if (Convert.ToInt32(await comando.ExecuteScalarAsync()) >= cantidad)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"No se llegó a {cantidad} esperas de lock en 30 s.");
    }
}
