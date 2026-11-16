using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/reportes/valoracion</c> contra Postgres real. Un escenario fijo en septiembre de 2026 (hora de Argentina, UTC-3) con tres
/// cortes diarios, todos en el pasado, más una segunda sucursal, una empresa ajena del mismo tenant, compras que no escribieron costo y
/// fuentes de tesorería posteriores a cada corte.
///
/// Evidencia de mutación (mutation-proof-tests): (1) quitar <c>m.CreadoEl &lt;= corte</c> de <c>StockPositivoAsync</c> hace fallar 5 de las 9
/// pruebas, entre ellas <see cref="LosPuntosPasadosIgnoranTodoLoPosteriorAlCorte"/>; (2) cambiar <c>Math.Max(0m, ...)</c> por la suma sin
/// acotar en <c>CalculoDeValoracion.StockPositivoPorArticulo</c> hace fallar dos pruebas de dominio del acotado.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesValoracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string ZonaArgentina = "America/Argentina/Buenos_Aires";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static DateTimeOffset Utc(int mes, int dia, int hora) => new(2026, mes, dia, hora, 0, 0, TimeSpan.Zero);

    private sealed record Escenario(
        ContextoDeTesoreriaDePrueba Ctx, int IdCuentaBanco, int IdListaPrecio, int IdAreaAlmacen, int IdAreaVerduleria,
        int IdArticuloX, int IdArticuloY, int IdArticuloZ);

    private static string Url(int idEmpresa, string desde, string hasta, string granularidad = "Dia") =>
        $"/api/reportes/valoracion?idEmpresa={idEmpresa}&desde={desde}&hasta={hasta}&granularidad={granularidad}";

    private static async Task<ValoracionDeInventario> LeerAsync(HttpClient cliente, string url)
    {
        var respuesta = await cliente.GetAsync(url);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<ValoracionDeInventario>(OpcionesJson))!;
    }

    private static async Task<ValoracionDeInventario> LeerSeptiembreAsync(Escenario e) =>
        await LeerAsync(e.Ctx.Admin, Url(e.Ctx.IdEmpresa, "2026-09-01", "2026-09-03"));

    private static async Task<int> SembrarSucursalConPuntoDeVentaAsync(ContextoDeTesoreriaDePrueba ctx, int idEmpresa, string nombre)
    {
        await using var db = ctx.Db();
        var ahora = DateTimeOffset.UtcNow;
        var sucursal = new Sucursal { IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Sucursales.Add(sucursal);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, IdSucursal = sucursal.Id, Nombre = nombre, Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();
        return puntoVenta.Id;
    }

    private static async Task SembrarStockAsync(
        ContextoDeTesoreriaDePrueba ctx, int idArticulo, int idPuntoVenta, decimal cantidad, DateTimeOffset creadoEl)
    {
        await using var db = ctx.Db();
        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = idPuntoVenta,
            IdSucursal = await SucursalDePrueba.DeAsync(db, idPuntoVenta), Cantidad = cantidad,
            Motivo = cantidad > 0 ? MotivoStock.Compra : MotivoStock.Venta, IdEmpleado = ctx.IdUsuarioAdmin, CreadoEl = creadoEl
        });
        await db.SaveChangesAsync();
    }

    private static async Task SembrarPrecioAsync(
        ContextoDeTesoreriaDePrueba ctx, int idArticulo, int idLista, decimal monto, DateTimeOffset desde, DateTimeOffset? hasta)
    {
        await using var db = ctx.Db();
        var ahora = DateTimeOffset.UtcNow;
        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = idLista, Monto = monto, VigenteDesde = desde,
            VigenteHasta = hasta, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task SembrarCompraAsync(
        Escenario e, int idArticulo, DateTimeOffset recepcion, decimal cantidad, decimal costoUnitario, decimal total,
        bool discriminaIva, bool esNotaDeCredito = false, EstadoCompra estado = EstadoCompra.Confirmada, bool actualizaCosto = true)
    {
        await using var plataforma = fixture.CrearContextoDeAplicacion(Ways.Infrastructure.Multitenancy.TenantActualFijo.Plataforma);
        var idTipo = await plataforma.TiposComprobante
            .Where(t => t.Clase == ClaseComprobante.Compra && (esNotaDeCredito ? t.Signo < 0 : t.Signo > 0))
            .Select(t => t.Id).FirstAsync();

        await using var db = e.Ctx.Db();
        var ahora = DateTimeOffset.UtcNow;
        var idProveedor = await db.Proveedores.Select(p => p.Id).FirstAsync();
        var idPuntoVenta = await db.PuntosVenta.Where(p => p.IdEmpresa == e.Ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var compra = new ComprobanteCompra
        {
            IdTenant = e.Ctx.IdTenant, IdProveedor = idProveedor, IdTipoComprobante = idTipo, IdPuntoVenta = idPuntoVenta,
            IdEmpleado = e.Ctx.IdUsuarioAdmin, NumeroExterno = Guid.NewGuid().ToString("N")[..12], FechaRecepcion = estado == EstadoCompra.Borrador ? null : recepcion,
            FechaComprobante = DateOnly.FromDateTime(recepcion.UtcDateTime), Subtotal = total, Total = total,
            DiscriminaIva = discriminaIva, PreciosIncluyenIva = false, Estado = estado, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();

        db.ItemsComprobanteCompra.Add(new ItemComprobanteCompra
        {
            IdTenant = e.Ctx.IdTenant, IdComprobanteCompra = compra.Id, Orden = 1, IdArticulo = idArticulo, Descripcion = "Línea",
            Cantidad = cantidad, CostoUnitario = costoUnitario, Descuento = 0m, IdAlicuotaIva = idAlicuota, PorcentajeIva = 21m,
            Total = total, ActualizaCosto = actualizaCosto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task SembrarAjusteDeEfectivoAsync(
        ContextoDeTesoreriaDePrueba ctx, int idEmpresa, decimal importe, DateTimeOffset fecha)
    {
        await using var db = ctx.Db();
        db.MovimientosTesoreria.Add(new MovimientoTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Fecha = fecha, Tipo = TipoMovimientoTesoreria.AjusteManual,
            Concepto = "Ajuste de prueba", Inicio = 0m, Ingreso = importe > 0 ? importe : 0m, Egreso = importe < 0 ? -importe : 0m,
            Final = importe, IdEmpleado = ctx.IdUsuarioAdmin
        });
        await db.SaveChangesAsync();
    }

    private async Task<Escenario> PrepararEscenarioAsync(string nombre)
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nombre);
        var zona = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa, new ParametroAlta("zona_horaria", $"\"{ZonaArgentina}\"", null));
        Assert.Equal(HttpStatusCode.OK, zona.StatusCode);

        var ahora = DateTimeOffset.UtcNow;
        int idPuntoVenta1, idCuenta, idLista, idAlmacen, idVerduleria, idX, idY, idZ;
        await using (var db = ctx.Db())
        {
            idPuntoVenta1 = await db.PuntosVenta.Where(p => p.IdEmpresa == ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
            idLista = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
            var idCondicion = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

            var almacen = new Area { IdTenant = ctx.IdTenant, Nombre = "Almacén", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            var verduleria = new Area { IdTenant = ctx.IdTenant, Nombre = "Verdulería", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.AddRange(almacen, verduleria);
            db.CuentasTesoreria.Add(new CuentaTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 1000m, CreatedAt = ahora, UpdatedAt = ahora
            });
            db.Proveedores.Add(new Ways.Domain.Proveedores.Proveedor
            {
                IdTenant = ctx.IdTenant, RazonSocial = "Distribuidora", IdCondicionFiscal = idCondicion, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idAlmacen = almacen.Id;
            idVerduleria = verduleria.Id;
            idCuenta = await db.CuentasTesoreria.Where(c => c.IdEmpresa == ctx.IdEmpresa).Select(c => c.Id).SingleAsync();

            Articulo Nuevo(string codigo, int idArea, decimal? costo) => new()
            {
                IdTenant = ctx.IdTenant, CodigoInterno = $"{codigo}-{Guid.NewGuid():N}", Nombre = codigo, IdArea = idArea,
                IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CostoNominal = costo,
                CreatedAt = ahora, UpdatedAt = ahora
            };

            var x = Nuevo("X", idAlmacen, 20m);
            var y = Nuevo("Y", idAlmacen, null);
            var z = Nuevo("Z", idAlmacen, 5m);
            db.Articulos.AddRange(x, y, z);
            await db.SaveChangesAsync();
            idX = x.Id;
            idY = y.Id;
            idZ = z.Id;
        }

        var e = new Escenario(ctx, idCuenta, idLista, idAlmacen, idVerduleria, idX, idY, idZ);

        var idPuntoVenta2 = await SembrarSucursalConPuntoDeVentaAsync(ctx, ctx.IdEmpresa, "Local 2");

        // Stock: X +10 / +4 el 1/9, -7 en el segundo local el 2/9 (queda en -3) y +5 el 3/9; Y +2 el 2/9; Z +3 el 1/9.
        await SembrarStockAsync(ctx, idX, idPuntoVenta1, 10m, Utc(9, 1, 12));
        await SembrarStockAsync(ctx, idX, idPuntoVenta2, 4m, Utc(9, 1, 12));
        await SembrarStockAsync(ctx, idX, idPuntoVenta2, -7m, Utc(9, 2, 12));
        await SembrarStockAsync(ctx, idX, idPuntoVenta1, 5m, Utc(9, 3, 12));
        await SembrarStockAsync(ctx, idY, idPuntoVenta1, 2m, Utc(9, 2, 12));
        await SembrarStockAsync(ctx, idZ, idPuntoVenta1, 3m, Utc(9, 1, 12));

        // Precios: X vale 100 hasta el mediodía del 2/9 y 150 desde entonces; Z vale 10; Y no tiene precio.
        await SembrarPrecioAsync(ctx, idX, idLista, 100m, Utc(8, 1, 0), Utc(9, 2, 12));
        await SembrarPrecioAsync(ctx, idX, idLista, 150m, Utc(9, 2, 12), null);
        await SembrarPrecioAsync(ctx, idZ, idLista, 10m, Utc(8, 1, 0), null);

        // Costo de X: 9,68 desde el 15/8 (80 + 21% sobre 10 unidades) y 20 desde el 3/9. Las compras que NO escribieron costo
        // (nota de crédito, anulada, sin actualiza_costo) son más recientes que la primera y tienen un costo distinto.
        await SembrarCompraAsync(e, idX, Utc(8, 15, 12), 10m, 8m, 80m, discriminaIva: true);
        await SembrarCompraAsync(e, idX, Utc(8, 20, 12), 10m, 99m, 990m, discriminaIva: false, esNotaDeCredito: true);
        await SembrarCompraAsync(e, idX, Utc(8, 25, 12), 10m, 77m, 770m, discriminaIva: false, estado: EstadoCompra.Anulada);
        await SembrarCompraAsync(e, idX, Utc(8, 28, 12), 10m, 66m, 660m, discriminaIva: false, actualizaCosto: false);
        await SembrarCompraAsync(e, idX, Utc(9, 3, 18), 10m, 20m, 200m, discriminaIva: false);

        // Dinero: efectivo +500 el 1/9 y +300 el 3/9; la cuenta Banco parte de 1000 y recibe +200 el 2/9.
        await SembrarAjusteDeEfectivoAsync(ctx, ctx.IdEmpresa, 500m, Utc(9, 1, 12));
        await SembrarAjusteDeEfectivoAsync(ctx, ctx.IdEmpresa, 300m, Utc(9, 3, 12));
        await using (var db = ctx.Db())
        {
            db.MovimientosCuenta.Add(new MovimientoCuenta
            {
                IdTenant = ctx.IdTenant, IdCuentaTesoreria = idCuenta, Fecha = Utc(9, 2, 12), Tipo = TipoMovimientoCuenta.Ajuste,
                Concepto = "Ajuste", Inicio = 1000m, Ingreso = 200m, Egreso = 0m, Final = 1200m, IdEmpleado = ctx.IdUsuarioAdmin
            });
            await db.SaveChangesAsync();
        }

        // Una empresa ajena del mismo tenant con su sucursal: nada de lo suyo cuenta.
        int idEmpresaAjena;
        await using (var db = ctx.Db())
        {
            var ajena = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(ajena);
            await db.SaveChangesAsync();
            idEmpresaAjena = ajena.Id;
        }

        var idPuntoVentaAjeno = await SembrarSucursalConPuntoDeVentaAsync(ctx, idEmpresaAjena, "Ajena");
        await SembrarStockAsync(ctx, idX, idPuntoVentaAjeno, 100m, Utc(9, 1, 12));
        await SembrarAjusteDeEfectivoAsync(ctx, idEmpresaAjena, 9999m, Utc(9, 1, 12));

        return e;
    }

    // ---- el corte --------------------------------------------------------------------------------

    /// <summary>
    /// LA CLÁUSULA: <c>m.CreadoEl &lt;= corte</c> del stock, el precio vigente al corte, la última compra anterior al corte y los
    /// <c>&lt;=</c> de las fuentes de tesorería. Cada valor del corte del 1/9 es distinto del que tendría con el resto del escenario.
    /// </summary>
    [Fact]
    public async Task LosPuntosPasadosIgnoranTodoLoPosteriorAlCorte()
    {
        var e = await PrepararEscenarioAsync(nameof(LosPuntosPasadosIgnoranTodoLoPosteriorAlCorte));

        var reporte = await LeerSeptiembreAsync(e);

        Assert.Equal(3, reporte.Puntos.Count);
        var dia1 = reporte.Puntos[0];
        // X: 10 + 4 = 14 a 9,68 y a 100; Z: 3 a 5 (estimado) y a 10.
        Assert.Equal(14m * 9.68m + 15m, dia1.MercaderiaCosto);
        Assert.Equal(1400m + 30m, dia1.MercaderiaVenta);
        Assert.Equal(500m, dia1.Efectivo);
        Assert.Equal(1000m, dia1.Cuentas);
        Assert.Equal(1500m, dia1.Dinero);
        Assert.Equal(dia1.MercaderiaCosto + 1500m, dia1.TotalACosto);
        Assert.Equal(dia1.MercaderiaVenta + 1500m, dia1.TotalAVenta);

        var dia3 = reporte.Puntos[2];
        // X: 10 + 5 = 15 (el par del segundo local sigue negativo) a 20 y a 150.
        Assert.Equal(15m * 20m + 15m, dia3.MercaderiaCosto);
        Assert.Equal(15m * 150m + 30m, dia3.MercaderiaVenta);
        Assert.Equal(800m, dia3.Efectivo);
        Assert.Equal(1200m, dia3.Cuentas);
    }

    [Fact]
    public async Task UnSaldoNegativoEnUnaSucursalNoRestaDeLaOtra()
    {
        var e = await PrepararEscenarioAsync(nameof(UnSaldoNegativoEnUnaSucursalNoRestaDeLaOtra));

        var reporte = await LeerSeptiembreAsync(e);

        // 2/9: el primer local tiene 10 y el segundo -3. Sin acotar el par serían 7 unidades.
        Assert.Equal(10m * 9.68m + 15m, reporte.Puntos[1].MercaderiaCosto);
        Assert.Equal(10m * 150m + 30m, reporte.Puntos[1].MercaderiaVenta);
    }

    [Fact]
    public async Task LaCoberturaCuentaLoQueNoSePuedeValuarYLoEstimadoSinValuarEnCero()
    {
        var e = await PrepararEscenarioAsync(nameof(LaCoberturaCuentaLoQueNoSePuedeValuarYLoEstimadoSinValuarEnCero));

        var reporte = await LeerSeptiembreAsync(e);

        var dia1 = reporte.Puntos[0];
        Assert.Equal(0, dia1.ArticulosSinCosto);
        Assert.Equal(0, dia1.ArticulosSinPrecio);
        Assert.Equal(1, dia1.ArticulosConCostoEstimado);
        Assert.Equal(15m, dia1.ValorCostoEstimado);

        // 2/9: aparece Y, que no tiene costo nominal, ni compras ni precio.
        var dia2 = reporte.Puntos[1];
        Assert.Equal(1, dia2.ArticulosSinCosto);
        Assert.Equal(1, dia2.ArticulosSinPrecio);
        Assert.Equal(1, dia2.ArticulosConCostoEstimado);
    }

    [Fact]
    public async Task ElCostoPasadoSaleDeLaUltimaCompraQueEscribioCostoYIgnoraLasDemas()
    {
        var e = await PrepararEscenarioAsync(nameof(ElCostoPasadoSaleDeLaUltimaCompraQueEscribioCostoYIgnoraLasDemas));

        var reporte = await LeerSeptiembreAsync(e);

        // 1/9: la última compra válida es la del 15/8 (9,68), no la nota de crédito, la anulada ni la que no actualiza costo.
        Assert.Equal(14m * 9.68m + 15m, reporte.Puntos[0].MercaderiaCosto);
        // 3/9: ya entró la compra del 3/9 a 20.
        Assert.Equal(15m * 20m + 15m, reporte.Puntos[2].MercaderiaCosto);
    }

    [Fact]
    public async Task LoDeOtraEmpresaDelMismoTenantNoSeMezcla()
    {
        var e = await PrepararEscenarioAsync(nameof(LoDeOtraEmpresaDelMismoTenantNoSeMezcla));

        var reporte = await LeerSeptiembreAsync(e);

        // El stock de 100 y el ajuste de 9999 de la otra empresa no figuran.
        Assert.Equal(14m * 9.68m + 15m, reporte.Puntos[0].MercaderiaCosto);
        Assert.Equal(500m, reporte.Puntos[0].Efectivo);
    }

    // ---- el punto actual y el detalle -----------------------------------------------------------

    [Fact]
    public async Task ElPuntoActualUsaElCostoNominalYDetallaCuentasYAreasConUnaAreaDeBajaComoSinArea()
    {
        var e = await PrepararEscenarioAsync(nameof(ElPuntoActualUsaElCostoNominalYDetallaCuentasYAreasConUnaAreaDeBajaComoSinArea));

        // Z pasa a Verdulería y esa área se da de baja con una baja lógica real: Z queda con la FK colgada.
        await using (var db = e.Ctx.Db())
        {
            await db.Articulos.Where(a => a.Id == e.IdArticuloZ)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IdArea, e.IdAreaVerduleria));
            await db.Areas.IgnoreQueryFilters().Where(a => a.Id == e.IdAreaVerduleria)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));
        }

        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(ZonaArgentina)).DateTime);
        var reporte = await LeerAsync(e.Ctx.Admin, Url(e.Ctx.IdEmpresa, hoy.AddDays(-45).ToString("yyyy-MM-dd"), hoy.ToString("yyyy-MM-dd"), "Mes"));

        var actual = reporte.Puntos[^1];
        Assert.True(DateTimeOffset.UtcNow - actual.Fecha < TimeSpan.FromMinutes(5));
        // Ahora: X 15 a su costo nominal 20 y Z 3 a 5, sin estimar nada; los puntos de meses cerrados sí estiman.
        Assert.Equal(15m * 20m + 15m, actual.MercaderiaCosto);
        Assert.Equal(0, actual.ArticulosConCostoEstimado);
        Assert.Equal(1, actual.ArticulosSinCosto);
        Assert.Equal(2000m, actual.Dinero);

        Assert.Equal(e.IdListaPrecio, reporte.Actual.IdListaPrecio);
        var banco = Assert.Single(reporte.Actual.PorCuenta);
        Assert.Equal(1200m, banco.Saldo);

        var almacen = Assert.Single(reporte.Actual.PorArea, a => a.IdArea == e.IdAreaAlmacen);
        Assert.Equal(15m * 20m, almacen.MercaderiaCosto);
        var sinArea = Assert.Single(reporte.Actual.PorArea, a => a.IdArea is null);
        Assert.Equal("Sin área", sinArea.Nombre);
        Assert.Equal(15m, sinArea.MercaderiaCosto);
        Assert.DoesNotContain(reporte.Actual.PorArea, a => a.IdArea == e.IdAreaVerduleria);
    }

    // ---- alcance y errores -----------------------------------------------------------------------

    [Fact]
    public async Task UnaEmpresaInexistenteODeOtroTenantDevuelve404()
    {
        var a = await PrepararEscenarioAsync(nameof(UnaEmpresaInexistenteODeOtroTenantDevuelve404) + "A");
        var b = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nameof(UnaEmpresaInexistenteODeOtroTenantDevuelve404) + "B");

        var deOtroTenant = await a.Ctx.Admin.GetAsync(Url(b.IdEmpresa, "2026-09-01", "2026-09-03"));
        var inexistente = await a.Ctx.Admin.GetAsync(Url(999_999, "2026-09-01", "2026-09-03"));

        Assert.Equal(HttpStatusCode.NotFound, deOtroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoYUnSupervisorAceptado()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nameof(UnVendedorEsRechazadoYUnSupervisorAceptado));
        var url = Url(ctx.IdEmpresa, "2026-09-01", "2026-09-01");

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Supervisor.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task UnRangoDeMasDeUnAnoOConMasDeSesentaPuntosDevuelve400()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nameof(UnRangoDeMasDeUnAnoOConMasDeSesentaPuntosDevuelve400));

        var demasiadoLargo = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, "2025-01-01", "2026-09-01", "Mes"));
        var demasiadosPuntos = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, "2026-01-01", "2026-04-01", "Dia"));

        Assert.Equal(HttpStatusCode.BadRequest, demasiadoLargo.StatusCode);
        Assert.Contains("rango_demasiado_amplio", await demasiadoLargo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, demasiadosPuntos.StatusCode);
        Assert.Contains("demasiados_puntos", await demasiadosPuntos.Content.ReadAsStringAsync());
    }
}
