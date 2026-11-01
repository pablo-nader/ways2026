using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Las lecturas de stock hablan de sucursal. Escenario: las cajas <c>Local 1</c> y <c>Local 2</c> comparten la sucursal
/// de <c>Local 1</c> (la de <c>Local 2</c> se reapunta a mano) y <c>Local 3</c> tiene la suya. Cada reporte tiene que
/// devolver UNA fila por artículo para la sucursal compartida, se le pregunte por cualquiera de sus cajas o por la
/// sucursal, y nada de la otra sucursal.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class LecturasDeStockPorSucursalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, HttpClient Admin, int IdCaja1, int IdCaja2, int IdCaja3, int IdSucursalCompartida,
        int IdSucursal3, string NombreSucursalCompartida, int IdArea, int IdAlicuotaIva, int IdEmpleado);

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
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 2", Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var caja3 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = "Local 3", Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.AddRange(caja2, caja3);
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area lecturas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idSucursalCompartida = await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta);
        var idSucursal3 = await SucursalDePrueba.DeAsync(db, caja3.Id);
        await SucursalDePrueba.ReapuntarAsync(fixture, caja2.Id, idSucursalCompartida);

        var nombreCompartida = await db.Sucursales.Where(s => s.Id == idSucursalCompartida).Select(s => s.Nombre).SingleAsync();
        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, admin, resultado.IdPuntoVenta, caja2.Id, caja3.Id, idSucursalCompartida,
            idSucursal3, nombreCompartida, area.Id, idAlicuota, idEmpleado);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
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
        return articulo.Id;
    }

    private async Task SembrarStockAsync(
        Contexto ctx, int idSucursal, int idArticulo, decimal cantidad, decimal? minimo = null, decimal? reposicion = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdSucursal = idSucursal, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarLoteAsync(Contexto ctx, int idSucursal, int idArticulo, string codigo, DateOnly vencimiento, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var lote = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = vencimiento,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdSucursal = idSucursal, IdLote = lote.Id, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarVentaAsync(Contexto ctx, int idCaja, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = idCaja,
            IdSucursal = await SucursalDePrueba.DeAsync(db, idCaja), Cantidad = -cantidad, Motivo = MotivoStock.Venta,
            IdEmpleado = ctx.IdEmpleado, CreadoEl = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
    }

    private static async Task<T> LeerAsync<T>(HttpClient cliente, string ruta)
    {
        var respuesta = await cliente.GetAsync(ruta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{ruta}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Cláusula bajo prueba: <c>Where(s => s.IdSucursal == alcance.IdSucursal)</c> en
    /// <c>ObtenerExistenciasAsync</c>. Tres entradas distintas (una caja, la otra caja y la sucursal) tienen que dar la
    /// MISMA respuesta, con una sola fila del artículo de la sucursal compartida y ninguna de la otra; cada
    /// proyección se lee de vuelta (id y nombre de la sucursal).</summary>
    [Fact]
    public async Task DosCajasDeUnaSucursalVenUnaSolaFilaDeExistencias()
    {
        var ctx = await PrepararAsync(nameof(DosCajasDeUnaSucursalVenUnaSolaFilaDeExistencias));
        var enCompartida = await SembrarArticuloAsync(ctx, "en-compartida");
        var enLaTres = await SembrarArticuloAsync(ctx, "en-la-tres");
        await SembrarStockAsync(ctx, ctx.IdSucursalCompartida, enCompartida, 10m, minimo: 4m, reposicion: 20m);
        await SembrarStockAsync(ctx, ctx.IdSucursal3, enLaTres, 7m);

        var porCaja1 = await LeerAsync<Existencias>(ctx.Admin, $"/api/reportes/stock/existencias?idPuntoVenta={ctx.IdCaja1}");
        var porCaja2 = await LeerAsync<Existencias>(ctx.Admin, $"/api/reportes/stock/existencias?idPuntoVenta={ctx.IdCaja2}");
        var porSucursal = await LeerAsync<Existencias>(ctx.Admin, $"/api/reportes/stock/existencias?idSucursal={ctx.IdSucursalCompartida}");
        var deLaTres = await LeerAsync<Existencias>(ctx.Admin, $"/api/reportes/stock/existencias?idSucursal={ctx.IdSucursal3}");

        foreach (var existencias in new[] { porCaja1, porCaja2, porSucursal })
        {
            Assert.Equal(ctx.IdSucursalCompartida, existencias.IdSucursal);
            Assert.Equal(ctx.NombreSucursalCompartida, existencias.NombreSucursal);
            var fila = Assert.Single(existencias.Filas);
            Assert.Equal(enCompartida, fila.IdArticulo);
            Assert.Equal(10m, fila.Cantidad);
            Assert.Equal(4m, fila.Minimo);
            Assert.Equal(20m, fila.Reposicion);
        }

        Assert.Equal(ctx.IdSucursal3, deLaTres.IdSucursal);
        Assert.NotEqual(ctx.NombreSucursalCompartida, deLaTres.NombreSucursal);
        var filaDeLaTres = Assert.Single(deLaTres.Filas);
        Assert.Equal(enLaTres, filaDeLaTres.IdArticulo);
        Assert.Equal(7m, filaDeLaTres.Cantidad);
    }

    /// <summary>Cláusula bajo prueba: <c>stockLote.IdSucursal == idSucursal</c> en
    /// <c>ConstruirQueryDeVencimientos</c>, para el listado y para el resumen del tablero.</summary>
    [Fact]
    public async Task DosCajasDeUnaSucursalVenUnSoloLoteEnVencimientos()
    {
        var ctx = await PrepararAsync(nameof(DosCajasDeUnaSucursalVenUnSoloLoteEnVencimientos));
        var articulo = await SembrarArticuloAsync(ctx, "con-lotes");
        var vencido = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
        await SembrarLoteAsync(ctx, ctx.IdSucursalCompartida, articulo, "L-COMPARTIDA", vencido, 6m);
        await SembrarLoteAsync(ctx, ctx.IdSucursal3, articulo, "L-DE-LA-TRES", vencido, 9m);

        var porCaja2 = await LeerAsync<Vencimientos>(ctx.Admin, $"/api/reportes/stock/vencimientos?idPuntoVenta={ctx.IdCaja2}");
        var porSucursal = await LeerAsync<Vencimientos>(ctx.Admin, $"/api/reportes/stock/vencimientos?idSucursal={ctx.IdSucursalCompartida}");
        var resumen = await LeerAsync<ResumenDeVencimientos>(ctx.Admin, $"/api/reportes/stock/vencimientos/resumen?idPuntoVenta={ctx.IdCaja1}");

        foreach (var vencimientos in new[] { porCaja2, porSucursal })
        {
            Assert.Equal(ctx.IdSucursalCompartida, vencimientos.IdSucursal);
            Assert.Equal(ctx.NombreSucursalCompartida, vencimientos.NombreSucursal);
            var fila = Assert.Single(vencimientos.Filas);
            Assert.Equal("L-COMPARTIDA", fila.CodigoLote);
            Assert.Equal(6m, fila.Cantidad);
        }

        Assert.Equal(ctx.IdSucursalCompartida, resumen.IdSucursal);
        Assert.Equal(ctx.NombreSucursalCompartida, resumen.NombreSucursal);
        Assert.Equal(1, resumen.Vencidos);
        Assert.Equal(0, resumen.PorVencer);
        Assert.Equal(0, resumen.SinFecha);
    }

    /// <summary>Cláusula bajo prueba: <c>s.IdSucursal == idSucursal</c> en <c>ConstruirQueryDeReposicion</c>, para el
    /// listado y para el resumen del tablero.</summary>
    [Fact]
    public async Task DosCajasDeUnaSucursalVenUnaSolaFilaDeReposicion()
    {
        var ctx = await PrepararAsync(nameof(DosCajasDeUnaSucursalVenUnaSolaFilaDeReposicion));
        var enCompartida = await SembrarArticuloAsync(ctx, "bajo-compartida");
        var enLaTres = await SembrarArticuloAsync(ctx, "bajo-la-tres");
        await SembrarStockAsync(ctx, ctx.IdSucursalCompartida, enCompartida, 0m, minimo: 5m, reposicion: 12m);
        await SembrarStockAsync(ctx, ctx.IdSucursal3, enLaTres, 2m, minimo: 5m);

        var porCaja1 = await LeerAsync<Reposicion>(ctx.Admin, $"/api/reportes/stock/reposicion?idPuntoVenta={ctx.IdCaja1}");
        var porCaja2 = await LeerAsync<Reposicion>(ctx.Admin, $"/api/reportes/stock/reposicion?idPuntoVenta={ctx.IdCaja2}");
        var porSucursal = await LeerAsync<Reposicion>(ctx.Admin, $"/api/reportes/stock/reposicion?idSucursal={ctx.IdSucursalCompartida}");
        var resumen = await LeerAsync<ResumenDeReposicion>(ctx.Admin, $"/api/reportes/stock/reposicion/resumen?idSucursal={ctx.IdSucursalCompartida}");

        foreach (var reposicion in new[] { porCaja1, porCaja2, porSucursal })
        {
            Assert.Equal(ctx.IdSucursalCompartida, reposicion.IdSucursal);
            Assert.Equal(ctx.NombreSucursalCompartida, reposicion.NombreSucursal);
            var fila = Assert.Single(reposicion.Filas);
            Assert.Equal(enCompartida, fila.IdArticulo);
            Assert.Equal(12m, fila.Sugerido);
        }

        Assert.Equal(ctx.IdSucursalCompartida, resumen.IdSucursal);
        Assert.Equal(ctx.NombreSucursalCompartida, resumen.NombreSucursal);
        Assert.Equal(1, resumen.BajoMinimo);
        Assert.Equal(1, resumen.SinStock);
    }

    /// <summary>Cláusula bajo prueba: <c>m.IdSucursal == idSucursal</c> en <c>LeerConsumoAsync</c>. Las ventas de las dos
    /// cajas de la sucursal compartida suman en una sola fila de rotación; la venta de la otra sucursal no entra.</summary>
    [Fact]
    public async Task LaRotacionSumaLasVentasDeTodasLasCajasDeLaSucursalYNadaMas()
    {
        var ctx = await PrepararAsync(nameof(LaRotacionSumaLasVentasDeTodasLasCajasDeLaSucursalYNadaMas));
        var articulo = await SembrarArticuloAsync(ctx, "rota");
        await SembrarVentaAsync(ctx, ctx.IdCaja1, articulo, 2m);
        await SembrarVentaAsync(ctx, ctx.IdCaja2, articulo, 3m);
        await SembrarVentaAsync(ctx, ctx.IdCaja3, articulo, 100m);

        var porCaja2 = await LeerAsync<Rotacion>(ctx.Admin, $"/api/reportes/stock/rotacion?idPuntoVenta={ctx.IdCaja2}");
        var porSucursal = await LeerAsync<Rotacion>(ctx.Admin, $"/api/reportes/stock/rotacion?idSucursal={ctx.IdSucursalCompartida}");

        foreach (var rotacion in new[] { porCaja2, porSucursal })
        {
            Assert.Equal(ctx.IdSucursalCompartida, rotacion.IdSucursal);
            Assert.Equal(ctx.NombreSucursalCompartida, rotacion.NombreSucursal);
            var fila = Assert.Single(rotacion.Filas);
            Assert.Equal(articulo, fila.IdArticulo);
            Assert.Equal(5m, fila.ConsumoEnVentana);
        }
    }

    [Theory]
    [InlineData(true, true, "alcance_de_stock_ambiguo")]
    [InlineData(false, false, "alcance_de_stock_requerido")]
    public async Task ElAlcanceDeStockExigeExactamenteUnaDeLasDosEntradas(bool mandaCaja, bool mandaSucursal, string codigoEsperado)
    {
        var ctx = await PrepararAsync(nameof(ElAlcanceDeStockExigeExactamenteUnaDeLasDosEntradas) + (mandaCaja ? "A" : "B"));
        var parametros = new List<string>();
        if (mandaCaja)
        {
            parametros.Add($"idPuntoVenta={ctx.IdCaja1}");
        }

        if (mandaSucursal)
        {
            parametros.Add($"idSucursal={ctx.IdSucursalCompartida}");
        }

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/existencias?{string.Join('&', parametros)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Una sucursal dada de baja que todavía tiene cajas y stock se trata como inexistente: 404 por su id y 404
    /// por la caja que la referencia, en el listado y en el export. Dato heredado: se estampa <c>deleted_at</c> a la
    /// fila real, sin tocar las cajas ni el stock.</summary>
    [Fact]
    public async Task UnaSucursalDadaDeBajaEsInexistenteParaLosReportesDeStock()
    {
        var ctx = await PrepararAsync(nameof(UnaSucursalDadaDeBajaEsInexistenteParaLosReportesDeStock));
        var articulo = await SembrarArticuloAsync(ctx, "en-la-baja");
        await SembrarStockAsync(ctx, ctx.IdSucursal3, articulo, 7m);
        await SucursalDePrueba.DarDeBajaAsync(fixture, ctx.IdSucursal3);

        foreach (var ruta in new[]
        {
            $"/api/reportes/stock/existencias?idSucursal={ctx.IdSucursal3}",
            $"/api/reportes/stock/existencias?idPuntoVenta={ctx.IdCaja3}",
            $"/api/reportes/stock/existencias/export?idSucursal={ctx.IdSucursal3}&formato=xlsx",
            $"/api/reportes/stock/reposicion?idPuntoVenta={ctx.IdCaja3}"
        })
        {
            var respuesta = await ctx.Admin.GetAsync(ruta);
            Assert.True(respuesta.StatusCode == HttpStatusCode.NotFound, $"{ruta}: {respuesta.StatusCode}");
        }

        var viva = await LeerAsync<Existencias>(ctx.Admin, $"/api/reportes/stock/existencias?idSucursal={ctx.IdSucursalCompartida}");
        Assert.Equal(ctx.IdSucursalCompartida, viva.IdSucursal);
    }

    /// <summary>El export nombra el archivo por sucursal: dos cajas de la misma sucursal descargan el mismo archivo.</summary>
    [Fact]
    public async Task ElExportDeExistenciasSeNombraPorSucursal()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeExistenciasSeNombraPorSucursal));
        var articulo = await SembrarArticuloAsync(ctx, "exportable");
        await SembrarStockAsync(ctx, ctx.IdSucursalCompartida, articulo, 3m);

        var porCaja2 = await ctx.Admin.GetAsync($"/api/reportes/stock/existencias/export?idPuntoVenta={ctx.IdCaja2}&formato=xlsx");
        var porSucursal = await ctx.Admin.GetAsync($"/api/reportes/stock/existencias/export?idSucursal={ctx.IdSucursalCompartida}&formato=xlsx");

        foreach (var respuesta in new[] { porCaja2, porSucursal })
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            var nombre = respuesta.Content.Headers.ContentDisposition!.FileName!.Trim('"');
            Assert.StartsWith($"existencias_sucursal{ctx.IdSucursalCompartida}_", nombre, StringComparison.Ordinal);
        }
    }
}
