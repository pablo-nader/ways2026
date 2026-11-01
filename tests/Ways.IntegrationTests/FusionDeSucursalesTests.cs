using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Fusión de dos sucursales (<c>POST /api/sucursales/{id}/fusionar</c>) contra Postgres real como <c>ways_app</c>.
/// Escenario: <c>Local 1</c> en la sucursal destino y <c>Local 2</c> en la de origen, cada una con su stock, que se
/// siembra con <see cref="EscritorDeStock"/> para que los dos invariantes valgan antes de fusionar.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FusionDeSucursalesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdOrigen, int IdArea, int IdAlicuotaIva, int IdLista, int IdCliente, int IdMedioEfectivo);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area fusion", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista fusion", EsDefault = false, Modo = ModoLista.Fija, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente fusion",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, admin, resultado.IdPuntoVenta, caja2.Id,
            await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta), caja2.IdSucursal, area.Id, idAlicuotaIva,
            lista.Id, cliente.Id, idMedioEfectivo);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static Task<HttpResponseMessage> FusionarAsync(Contexto ctx, int idOrigen, int? idDestino) =>
        ctx.Admin.PostAsJsonAsync($"/api/sucursales/{idOrigen}/fusionar", new SucursalFusion(idDestino));

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, decimal? precio = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = ctx.IdArea,
            IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (precio is { } monto)
        {
            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdLista, Monto = monto,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return articulo.Id;
    }

    private async Task<int> SembrarLoteAsync(Contexto ctx, int idArticulo, string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var lote = new Lote { IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60), CreatedAt = ahora, UpdatedAt = ahora };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();
        return lote.Id;
    }

    /// <summary>Asienta con el escritor real: el movimiento y los saldos del agregado y del lote.</summary>
    private async Task AsentarAsync(Contexto ctx, int idSucursal, int idCaja, int idArticulo, decimal cantidad, int? idLote = null)
    {
        await using var escritura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        await using var transaccion = await escritura.Database.BeginTransactionAsync();
        await EscritorDeStock.AsentarAsync(
            escritura.Database.GetDbConnection(), transaccion.GetDbTransaction(),
            [
                new AsientoDeStock(ctx.IdTenant, idSucursal, idCaja, idArticulo, cantidad, MotivoStock.Ajuste, ctx.IdAdmin, DateTimeOffset.UtcNow)
                {
                    IdLote = idLote
                }
            ],
            CancellationToken.None);
        await transaccion.CommitAsync();
    }

    private async Task FijarUmbralesAsync(Contexto ctx, int idSucursal, int idArticulo, decimal? minimo, decimal? reposicion)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_sucursal, id_tenant, cantidad, minimo, reposicion) VALUES ($1, $2, $3, 0, $4, $5) " +
            "ON CONFLICT (id_articulo, id_sucursal) DO UPDATE SET minimo = EXCLUDED.minimo, reposicion = EXCLUDED.reposicion";
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });
        comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)minimo ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)reposicion ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Numeric });
        await comando.ExecuteNonQueryAsync();
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

    /// <summary>Cláusulas bajo prueba: la suma de cantidades, de lotes y de stock negativo hacia el destino; el par de
    /// movimientos de cada saldo; el origen en cero y dado de baja; las cajas del origen en el destino; la regla de
    /// umbrales (el destino conserva los suyos, toma los del origen donde no tiene ninguno).</summary>
    [Fact]
    public async Task LaFusionSumaStockLotesYNegativosEnElDestinoYDejaElOrigenEnCeroYDadoDeBaja()
    {
        var ctx = await PrepararAsync(nameof(LaFusionSumaStockLotesYNegativosEnElDestinoYDejaElOrigenEnCeroYDadoDeBaja));
        var comun = await SembrarArticuloAsync(ctx, "comun");
        var negativo = await SembrarArticuloAsync(ctx, "negativo");
        var soloNegativo = await SembrarArticuloAsync(ctx, "solo-negativo");
        var conLotes = await SembrarArticuloAsync(ctx, "con-lotes");
        var soloUmbral = await SembrarArticuloAsync(ctx, "solo-umbral");
        var lote1 = await SembrarLoteAsync(ctx, conLotes, "L1");
        var lote2 = await SembrarLoteAsync(ctx, conLotes, "L2");

        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, comun, 5m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, comun, 7m);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, negativo, -3m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, negativo, 10m);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, soloNegativo, -2m);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, conLotes, 4m, lote1);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, conLotes, 6m, lote2);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, conLotes, 1m, lote1);

        await FijarUmbralesAsync(ctx, ctx.IdOrigen, comun, 2m, 5m);
        await FijarUmbralesAsync(ctx, ctx.IdOrigen, negativo, 9m, 9m);
        await FijarUmbralesAsync(ctx, ctx.IdDestino, negativo, 1m, 3m);
        await FijarUmbralesAsync(ctx, ctx.IdOrigen, soloUmbral, 4m, null);
        await AssertInvariantesAsync(ctx);

        var destino = await LeerAsync<SucursalListado>(await FusionarAsync(ctx, ctx.IdOrigen, ctx.IdDestino), HttpStatusCode.OK);
        Assert.Equal(ctx.IdDestino, destino.Id);
        Assert.Equal(2, destino.CantidadPuntosVenta);

        var stock = await StockAsync(ctx);
        Assert.Equal(12m, stock[(ctx.IdDestino, comun)]);
        Assert.Equal(7m, stock[(ctx.IdDestino, negativo)]);
        Assert.Equal(-2m, stock[(ctx.IdDestino, soloNegativo)]);
        Assert.Equal(11m, stock[(ctx.IdDestino, conLotes)]);
        Assert.All(stock.Where(s => s.Key.Sucursal == ctx.IdOrigen), s => Assert.Equal(0m, s.Value));

        var lotes = await LotesAsync(ctx);
        Assert.Equal(5m, lotes[(ctx.IdDestino, conLotes, lote1)]);
        Assert.Equal(6m, lotes[(ctx.IdDestino, conLotes, lote2)]);
        Assert.All(lotes.Where(l => l.Key.Sucursal == ctx.IdOrigen), l => Assert.Equal(0m, l.Value));

        await AssertInvariantesAsync(ctx);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // Un par por saldo: comun, negativo, soloNegativo y los dos lotes. Cada egreso del origen apunta al destino.
        var pares = await db.MovimientosStock
            .Where(m => m.Motivo == MotivoStock.Transferencia)
            .Select(m => new { m.IdSucursal, m.IdSucursalDestino, m.IdArticulo, m.IdLote, m.Cantidad })
            .ToListAsync();
        Assert.Equal(10, pares.Count);
        Assert.All(pares, m => Assert.Equal(ctx.IdDestino, m.IdSucursalDestino));
        Assert.Equal(5, pares.Count(m => m.IdSucursal == ctx.IdOrigen));
        Assert.Contains(pares, m => m.IdSucursal == ctx.IdOrigen && m.IdArticulo == soloNegativo && m.Cantidad == 2m);
        Assert.Contains(pares, m => m.IdSucursal == ctx.IdDestino && m.IdArticulo == conLotes && m.IdLote == lote2 && m.Cantidad == 6m);

        var umbrales = await db.Stock.Where(s => s.IdSucursal == ctx.IdDestino)
            .ToDictionaryAsync(s => s.IdArticulo, s => (s.Minimo, s.Reposicion));
        Assert.Equal((2m, 5m), umbrales[comun]);
        Assert.Equal((1m, 3m), umbrales[negativo]);
        Assert.Equal((4m, (decimal?)null), umbrales[soloUmbral]);

        var origen = await db.Sucursales.IgnoreQueryFilters().SingleAsync(s => s.Id == ctx.IdOrigen);
        Assert.NotNull(origen.DeletedAt);
        Assert.Equal(ctx.IdDestino, await SucursalDePrueba.DeAsync(db, ctx.IdCaja2));

        var rastro = await db.Auditoria.SingleAsync(a => a.Accion == "sucursal.fusion");
        Assert.Equal(ctx.IdOrigen, rastro.IdEntidad);
        using var nuevo = JsonDocument.Parse(rastro.ValorNuevo);
        Assert.Equal(ctx.IdDestino, nuevo.RootElement.GetProperty("id_sucursal_destino").GetInt32());
        Assert.Equal(1, nuevo.RootElement.GetProperty("puntos_venta").GetInt32());
        Assert.Equal(10, nuevo.RootElement.GetProperty("movimientos").GetInt32());
    }

    /// <summary>Después de la fusión la caja que era del origen vende contra el stock compartido del destino.</summary>
    [Fact]
    public async Task LaCajaDelOrigenFusionadoVendeContraElStockCompartido()
    {
        var ctx = await PrepararAsync(nameof(LaCajaDelOrigenFusionadoVendeContraElStockCompartido));
        var articulo = await SembrarArticuloAsync(ctx, "yerba", precio: 10m);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 4m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, articulo, 6m);

        await LeerAsync<SucursalListado>(await FusionarAsync(ctx, ctx.IdOrigen, ctx.IdDestino), HttpStatusCode.OK);

        var venta = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas",
            new SolicitudDeVenta(
                ctx.IdCaja2, ctx.IdCliente, "TX", null,
                [new LineaDeVenta(articulo, 3m, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, 30m, null, 0m)],
                null, null));
        Assert.True(venta.StatusCode == HttpStatusCode.Created, await venta.Content.ReadAsStringAsync());

        var stock = await StockAsync(ctx);
        Assert.Equal(7m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>Ventas en las dos cajas mientras corre la fusión: ninguna se deadlockea ni se pierde. La fusión toma las
    /// cajas con <c>FOR UPDATE</c> antes del stock y una venta toma su caja con <c>FOR SHARE</c> antes del stock, así que
    /// una de las dos espera a la otra entera.</summary>
    [Fact]
    public async Task LaFusionConVentasConcurrentesNoSeDeadlockeaYConservaLaSuma()
    {
        var ctx = await PrepararAsync(nameof(LaFusionConVentasConcurrentesNoSeDeadlockeaYConservaLaSuma));
        var articulo = await SembrarArticuloAsync(ctx, "concurrente", precio: 10m);
        await AsentarAsync(ctx, ctx.IdOrigen, ctx.IdCaja2, articulo, 20m);
        await AsentarAsync(ctx, ctx.IdDestino, ctx.IdCaja1, articulo, 20m);

        Task<HttpResponseMessage> Vender(int idCaja) =>
            ctx.Admin.PostAsJsonAsync(
                "/api/ventas",
                new SolicitudDeVenta(
                    idCaja, ctx.IdCliente, "TX", null,
                    [new LineaDeVenta(articulo, 1m, null)],
                    [new PagoDeVenta(ctx.IdMedioEfectivo, 10m, null, 0m)],
                    null, null));

        var ventas = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 3; i++)
        {
            ventas.Add(Vender(ctx.IdCaja1));
            ventas.Add(Vender(ctx.IdCaja2));
        }

        var fusion = FusionarAsync(ctx, ctx.IdOrigen, ctx.IdDestino);
        await Task.WhenAll([.. ventas, fusion]);

        await LeerAsync<SucursalListado>(await fusion, HttpStatusCode.OK);
        foreach (var venta in ventas)
        {
            var respuesta = await venta;
            Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
        }

        var stock = await StockAsync(ctx);
        Assert.Equal(34m, stock[(ctx.IdDestino, articulo)]);
        Assert.Equal(0m, stock[(ctx.IdOrigen, articulo)]);
        await AssertInvariantesAsync(ctx);
    }

    /// <summary>update-404-first: un origen inexistente, dado de baja o de otro tenant es 404 aunque el cuerpo sea
    /// inválido; un destino en las mismas condiciones también es 404.</summary>
    [Fact]
    public async Task UnOrigenODestinoInexistenteDadoDeBajaODeOtroTenantContesta404()
    {
        var ctx = await PrepararAsync(nameof(UnOrigenODestinoInexistenteDadoDeBajaODeOtroTenantContesta404));
        var otro = await PrepararAsync(nameof(UnOrigenODestinoInexistenteDadoDeBajaODeOtroTenantContesta404) + "Otro");

        var dadaDeBaja = await LeerAsync<SucursalListado>(
            await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(ctx.IdEmpresa, "Vieja")), HttpStatusCode.Created);
        await SucursalDePrueba.DarDeBajaAsync(fixture, dadaDeBaja.Id);

        foreach (var origen in new[] { 2_000_000_000, dadaDeBaja.Id, otro.IdOrigen })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await FusionarAsync(ctx, origen, null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await FusionarAsync(ctx, origen, ctx.IdDestino)).StatusCode);
        }

        foreach (var destino in new[] { 2_000_000_000, dadaDeBaja.Id, otro.IdDestino })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await FusionarAsync(ctx, ctx.IdOrigen, destino)).StatusCode);
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Null((await db.Sucursales.SingleAsync(s => s.Id == ctx.IdOrigen)).DeletedAt);
    }

    /// <summary>Cláusula bajo prueba: el destino tiene que ser de la empresa del origen (<c>409 sucursal_de_otra_empresa</c>);
    /// sin destino es <c>400 sucursal_destino_requerida</c>; el mismo origen y destino, <c>400 fusion_misma_sucursal</c>.
    /// Ningún rechazo mueve una caja.</summary>
    [Fact]
    public async Task ElDestinoTieneQueSerOtraSucursalDeLaMismaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(ElDestinoTieneQueSerOtraSucursalDeLaMismaEmpresa));

        int idDeOtraEmpresa;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa2 = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Segunda SA", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(empresa2);
            await db.SaveChangesAsync();
            idDeOtraEmpresa = (await LeerAsync<SucursalListado>(
                await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(empresa2.Id, "De la segunda")),
                HttpStatusCode.Created)).Id;
        }

        await AssertCodigoAsync(await FusionarAsync(ctx, ctx.IdOrigen, idDeOtraEmpresa), HttpStatusCode.Conflict, "sucursal_de_otra_empresa");
        await AssertCodigoAsync(await FusionarAsync(ctx, ctx.IdOrigen, null), HttpStatusCode.BadRequest, "sucursal_destino_requerida");
        await AssertCodigoAsync(await FusionarAsync(ctx, ctx.IdOrigen, ctx.IdOrigen), HttpStatusCode.BadRequest, "fusion_misma_sucursal");

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(ctx.IdOrigen, await SucursalDePrueba.DeAsync(lectura, ctx.IdCaja2));
        Assert.Null((await lectura.Sucursales.SingleAsync(s => s.Id == ctx.IdOrigen)).DeletedAt);
        Assert.False(await lectura.Auditoria.AnyAsync(a => a.Accion == "sucursal.fusion"));
    }
}
