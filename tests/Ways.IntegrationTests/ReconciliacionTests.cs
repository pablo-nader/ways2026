using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 4 (tasks 4.5-4.10): <c>ServicioDeLotes.ReconciliarAsync</c>
/// punta a punta — el par neto cero de <c>reclasificacion</c> (design decisión 13/14), su
/// idempotencia (con evidencia de mutación sobre el guard de residuo), el self-heal de una venta
/// que cae en un par sin reconciliar, la discriminación de <c>motivo</c>, el caso de residuo cero
/// que nunca toca el CHECK de <c>movimientos_stock</c>, y los dos disparadores automáticos
/// (<c>ServicioDeArticulos</c>/<c>ServicioDeParametros</c>).
///
/// Las pruebas 4.5-4.9 activan el módulo con <c>lotes_habilitado=true</c> y
/// <c>articulos.controla_lote=true</c> escrito DIRECTO por EF (sin pasar por
/// <c>ServicioDeArticulos.ActualizarAsync</c>) — así el par queda "activado pero nunca
/// reconciliado" de forma controlada, sin que el disparador automático interfiera con el
/// escenario bajo prueba. Los disparadores automáticos tienen su propio par de pruebas dedicado
/// (4.10, <see cref="UnFlipDeControlaLoteViaArticulosDisparaLaReconciliacionAutomaticamente"/> /
/// <see cref="UnFlipDeLotesHabilitadoViaParametrosDisparaLaReconciliacionAutomaticamente"/>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReconciliacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Mismo motivo que ArticulosEndpointsTests.OpcionesJson: el server registra
    // JsonStringEnumConverter (Program.cs) pero ReadFromJsonAsync<T>() sin opciones usa las
    // opciones DEFAULT del lado cliente, que no lo traen — ArticuloListado.UnidadVenta revienta
    // la deserialización sin esto.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuarioAdmin, HttpClient Admin, int IdArea, int IdAlicuotaIva);

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

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Reconciliacion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, area.Id,
            idAlicuotaIva);
    }

    /// <summary>Alta DIRECTA por EF (no vía <c>ServicioDeArticulos</c>): no hay "antes" que
    /// comparar, así que nunca dispara la reconciliación automática — el par queda activado pero
    /// sin reconciliar, el estado que 4.5-4.9 necesitan bajo control.</summary>
    private async Task<int> SembrarArticuloConLoteEfectivoAsync(Contexto ctx, string nombre)
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

        return articulo.Id;
    }

    private static async Task HabilitarLotesAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}", new ParametroAlta("lotes_habilitado", "true", null));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    private async Task SembrarStockAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ResultadoDeReconciliacion> ReconciliarAsync(Contexto ctx, int? idArticulo, int? idPuntoVenta)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/lotes/reconciliacion", new SolicitudDeReconciliacion(idArticulo, idPuntoVenta));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return (await respuesta.Content.ReadFromJsonAsync<ResultadoDeReconciliacion>())!;
    }

    // ---- task 4.5: net-zero proof --------------------------------------------------------------

    [Fact]
    public async Task LaReconciliacionEscribeUnParNetoCeroYDejaStockIntactoMientrasElSinIdentificarRecibeElResiduo()
    {
        var ctx = await PrepararAsync(nameof(LaReconciliacionEscribeUnParNetoCeroYDejaStockIntactoMientrasElSinIdentificarRecibeElResiduo));
        await HabilitarLotesAsync(ctx);
        var idArticulo = await SembrarArticuloConLoteEfectivoAsync(ctx, "articulo-net-zero");
        await SembrarStockAsync(ctx, idArticulo, 40m);

        var resultado = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);

        Assert.Equal(1, resultado.ParesReconciliados);
        Assert.Equal(0, resultado.ParesSinResiduo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .OrderBy(m => m.Id)
            .ToListAsync();
        Assert.Equal(2, movimientos.Count);
        Assert.Equal(0m, movimientos.Sum(m => m.Cantidad));

        var filaAgregada = movimientos.Single(m => m.IdLote is null);
        Assert.Equal(-40m, filaAgregada.Cantidad);

        var loteSinIdentificar = await db.Lotes.SingleAsync(l => l.IdArticulo == idArticulo && l.EsSinIdentificar);
        var filaSinIdentificar = movimientos.Single(m => m.IdLote == loteSinIdentificar.Id);
        Assert.Equal(40m, filaSinIdentificar.Cantidad);

        var stock = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(40m, stock.Cantidad);

        var stockLoteSinIdentificar = await db.StockLotes.SingleAsync(
            sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == loteSinIdentificar.Id);
        Assert.Equal(40m, stockLoteSinIdentificar.Cantidad);
    }

    // ---- task 4.6: mutation target — el guard `residuo == 0 ⇒ escribir nada` -------------------

    /// <summary>Idempotencia con evidencia de mutación (mutation-proof-tests): una segunda corrida
    /// sobre el MISMO par tiene que dejar la cuenta de <c>movimientos_stock</c> IDÉNTICA — el
    /// valor discriminante que un guard borrado no puede sostener (una cuenta que crece prueba
    /// que el guard de residuo cero desapareció). *(APPLY-RUN NOTE: mutación aplicada — la
    /// condición `if (residuo == 0m)` en `ServicioDeLotes.ReconciliarParAsync` reemplazada por
    /// `if (false)` — build, mismo filtro: este test cae RED, pero no en la aserción de conteo
    /// esperada: la segunda corrida intenta escribir el par con `residuo = 0` de todos modos, lo
    /// que dispara un `500 error_interno` (`ck_movimientos_stock_cantidad_no_cero`) ANTES de
    /// llegar a la aserción — la evidencia de mutación más fuerte posible, un CHECK real de
    /// Postgres frenando la fila cero que el guard debía evitar. Revertida la mutación, build,
    /// mismo filtro: GREEN, 7/7 en `ReconciliacionTests`.)*</summary>
    [Fact]
    public async Task UnaSegundaReconciliacionSobreElMismoParEsUnNoOpQueNoDuplicaMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnaSegundaReconciliacionSobreElMismoParEsUnNoOpQueNoDuplicaMovimientos));
        await HabilitarLotesAsync(ctx);
        var idArticulo = await SembrarArticuloConLoteEfectivoAsync(ctx, "articulo-idempotencia");
        await SembrarStockAsync(ctx, idArticulo, 25m);

        var primeraCorrida = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);
        Assert.Equal(1, primeraCorrida.ParesReconciliados);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(2, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta));
        }

        var segundaCorrida = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);
        Assert.Equal(0, segundaCorrida.ParesReconciliados);
        Assert.Equal(1, segundaCorrida.ParesSinResiduo);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            // Valor discriminante (spec: "A second reconciliation run is a no-op"): la CUENTA de
            // filas, no solo la suma — una cuenta que creció probaría que el guard se borró
            // aunque la suma siguiera dando cero (dos pares nuevos también suman cero).
            Assert.Equal(2, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta));
        }
    }

    // ---- task 4.7: self-heal ---------------------------------------------------------------------

    /// <summary>Design decisión 13: una venta que cae en un par sin reconciliar deja el lote sin
    /// identificar en negativo — la próxima reconciliación se autocura, recomputando el residuo
    /// desde el estado ACTUAL, nunca desde una foto vieja. Simula el escritor de venta con
    /// lotes (slice 8, en paralelo, todavía no aterriza en esta rama): un movimiento de venta que
    /// decrementa <c>stock</c> Y <c>stock_lotes</c> del sin-identificar, sin que el par haya sido
    /// reconciliado todavía.</summary>
    [Fact]
    public async Task UnaVentaQueCaeEnUnParSinReconciliarSeAutocuraEnLaProximaReconciliacion()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaQueCaeEnUnParSinReconciliarSeAutocuraEnLaProximaReconciliacion));
        await HabilitarLotesAsync(ctx);
        var idArticulo = await SembrarArticuloConLoteEfectivoAsync(ctx, "articulo-self-heal");
        await SembrarStockAsync(ctx, idArticulo, 40m);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;

            // get-or-create del sin-identificar, mismo statement que el write-path real (design
            // decisión 5) — el par todavía no fue reconciliado, así que su stock_lotes arranca
            // en 0 antes de esta venta simulada.
            var conexion = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            var idLoteSinIdentificar = await ServicioDeLotes.ResolverSinIdentificarAsync(
                conexion, null, ctx.IdTenant, idArticulo, ahora, CancellationToken.None);

            db.MovimientosStock.Add(new MovimientoStock
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = -5m,
                Motivo = MotivoStock.Venta, IdLote = idLoteSinIdentificar, IdEmpleado = ctx.IdUsuarioAdmin, CreadoEl = ahora
            });

            var stock = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
            stock.Cantidad -= 5m;

            db.StockLotes.Add(new StockLote
            {
                IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLoteSinIdentificar,
                IdTenant = ctx.IdTenant, Cantidad = -5m
            });

            await db.SaveChangesAsync();
        }

        await using (var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var stockAntes = await dbAntes.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
            Assert.Equal(35m, stockAntes.Cantidad);

            var sumaLotesAntes = await dbAntes.StockLotes
                .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta)
                .SumAsync(sl => sl.Cantidad);
            Assert.Equal(-5m, sumaLotesAntes);
        }

        var resultado = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);
        Assert.Equal(1, resultado.ParesReconciliados);

        await using var db2 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var stockDespues = await db2.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        var sumaLotesDespues = await db2.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(sl => sl.Cantidad);

        Assert.Equal(35m, stockDespues.Cantidad);
        Assert.Equal(stockDespues.Cantidad, sumaLotesDespues);
    }

    // ---- task 4.8: motivo discrimination -----------------------------------------------------------

    [Fact]
    public async Task LasFilasDeReconciliacionSiempreUsanMotivoReclasificacionNuncaAjuste()
    {
        var ctx = await PrepararAsync(nameof(LasFilasDeReconciliacionSiempreUsanMotivoReclasificacionNuncaAjuste));
        await HabilitarLotesAsync(ctx);
        var idArticulo = await SembrarArticuloConLoteEfectivoAsync(ctx, "articulo-motivo");
        await SembrarStockAsync(ctx, idArticulo, 12m);

        await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .ToListAsync();

        Assert.Equal(2, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal(MotivoStock.Reclasificacion, m.Motivo));
        Assert.DoesNotContain(movimientos, m => m.Motivo == MotivoStock.Ajuste);
    }

    // ---- task 4.9: zero-residue never violates the non-zero CHECK -----------------------------------

    /// <summary>Un par cuyo residuo YA da cero en la primera corrida (sin pasar antes por una
    /// reconciliación previa) — <c>stock_lotes</c> sembrado directo a mano, sumando exactamente
    /// la cantidad agregada. spec: "A zero-cantidad reclasificación row never violates the
    /// non-zero CHECK" — si el guard fallara, el INSERT de una fila `cantidad = 0` chocaría con
    /// <c>ck_movimientos_stock_cantidad_no_cero</c> y este request devolvería un 500 crudo en vez
    /// de un 200 con cero pares escritos.</summary>
    [Fact]
    public async Task UnResiduoQueYaDaCeroEnLaPrimeraCorridaNoEscribeNiViolaElCheckDeCantidadNoCero()
    {
        var ctx = await PrepararAsync(nameof(UnResiduoQueYaDaCeroEnLaPrimeraCorridaNoEscribeNiViolaElCheckDeCantidadNoCero));
        await HabilitarLotesAsync(ctx);
        var idArticulo = await SembrarArticuloConLoteEfectivoAsync(ctx, "articulo-residuo-cero");
        await SembrarStockAsync(ctx, idArticulo, 18m);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var loteFechado = new Lote
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = "L-RESIDUO-CERO",
                FechaVencimiento = new DateOnly(2030, 12, 31), EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Lotes.Add(loteFechado);
            await db.SaveChangesAsync();

            db.StockLotes.Add(new StockLote
            {
                IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = loteFechado.Id,
                IdTenant = ctx.IdTenant, Cantidad = 18m
            });
            await db.SaveChangesAsync();
        }

        var resultado = await ReconciliarAsync(ctx, idArticulo, ctx.IdPuntoVenta);

        Assert.Equal(0, resultado.ParesReconciliados);
        Assert.Equal(1, resultado.ParesSinResiduo);

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await dbFinal.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta));
    }

    // ---- task 4.10: activation-trigger tests ×2 ------------------------------------------------------

    /// <summary>Task 4.2: el flip <c>controla_lote false → true</c> vía
    /// <c>PUT /api/articulos/{id}</c> dispara la reconciliación automáticamente, sin que nadie
    /// llame al endpoint de re-run.</summary>
    [Fact]
    public async Task UnFlipDeControlaLoteViaArticulosDisparaLaReconciliacionAutomaticamente()
    {
        var ctx = await PrepararAsync(nameof(UnFlipDeControlaLoteViaArticulosDisparaLaReconciliacionAutomaticamente));
        await HabilitarLotesAsync(ctx);

        var alta = new AltaArticulo(
            CodigoInterno: null, Nombre: "Articulo trigger ABM", Descripcion: null, IdArea: ctx.IdArea,
            IdCategoria: null, IdMarca: null, IdGrupo: null, IdProveedorHabitual: null, IdAlicuotaIva: ctx.IdAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null, EsProducto: true, CostoLista: null,
            DescuentoProveedor: null, CostoNominal: null, ControlaLote: false);
        var creacion = await ctx.Admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, creacion.StatusCode);
        var articulo = (await creacion.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        await SembrarStockAsync(ctx, articulo.Id, 60m);

        var edicion = new EdicionArticulo(
            Nombre: articulo.Nombre, Descripcion: articulo.Descripcion, IdArea: articulo.IdArea,
            IdCategoria: articulo.IdCategoria, IdMarca: articulo.IdMarca, IdGrupo: articulo.IdGrupo,
            IdProveedorHabitual: articulo.IdProveedorHabitual, IdAlicuotaIva: articulo.IdAlicuotaIva,
            UnidadVenta: articulo.UnidadVenta, UnidadesPorBulto: articulo.UnidadesPorBulto, EsProducto: articulo.EsProducto,
            CostoLista: articulo.CostoLista, DescuentoProveedor: articulo.DescuentoProveedor, CostoNominal: articulo.CostoNominal,
            DisponibleParaTodas: articulo.DisponibleParaTodas, IdsEmpresas: null, Activo: articulo.Activo, ControlaLote: true);
        var edicionRespuesta = await ctx.Admin.PutAsJsonAsync($"/api/articulos/{articulo.Id}", edicion);
        var cuerpo = await edicionRespuesta.Content.ReadAsStringAsync();
        Assert.True(edicionRespuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(2, await db.MovimientosStock.CountAsync(
            m => m.IdArticulo == articulo.Id && m.IdPuntoVenta == ctx.IdPuntoVenta && m.Motivo == MotivoStock.Reclasificacion));

        var loteSinIdentificar = await db.Lotes.SingleAsync(l => l.IdArticulo == articulo.Id && l.EsSinIdentificar);
        var stockLote = await db.StockLotes.SingleAsync(
            sl => sl.IdArticulo == articulo.Id && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == loteSinIdentificar.Id);
        Assert.Equal(60m, stockLote.Cantidad);
    }

    /// <summary>Task 4.3: el flip <c>lotes_habilitado false → true</c> vía
    /// <c>PUT /api/parametros</c> dispara la reconciliación automáticamente sobre los artículos ya
    /// <c>controla_lote = true</c> de esa empresa.</summary>
    [Fact]
    public async Task UnFlipDeLotesHabilitadoViaParametrosDisparaLaReconciliacionAutomaticamente()
    {
        var ctx = await PrepararAsync(nameof(UnFlipDeLotesHabilitadoViaParametrosDisparaLaReconciliacionAutomaticamente));

        var alta = new AltaArticulo(
            CodigoInterno: null, Nombre: "Articulo trigger parametros", Descripcion: null, IdArea: ctx.IdArea,
            IdCategoria: null, IdMarca: null, IdGrupo: null, IdProveedorHabitual: null, IdAlicuotaIva: ctx.IdAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null, EsProducto: true, CostoLista: null,
            DescuentoProveedor: null, CostoNominal: null, ControlaLote: true);
        var creacion = await ctx.Admin.PostAsJsonAsync("/api/articulos", alta);
        Assert.Equal(HttpStatusCode.Created, creacion.StatusCode);
        var articulo = (await creacion.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        await SembrarStockAsync(ctx, articulo.Id, 17m);

        // lotes_habilitado arranca en el default declarado (false, sin fila) — este PUT es el
        // flip false → true que ServicioDeParametros.EstablecerAsync tiene que detectar.
        await HabilitarLotesAsync(ctx);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(2, await db.MovimientosStock.CountAsync(
            m => m.IdArticulo == articulo.Id && m.IdPuntoVenta == ctx.IdPuntoVenta && m.Motivo == MotivoStock.Reclasificacion));

        var loteSinIdentificar = await db.Lotes.SingleAsync(l => l.IdArticulo == articulo.Id && l.EsSinIdentificar);
        var stockLote = await db.StockLotes.SingleAsync(
            sl => sl.IdArticulo == articulo.Id && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == loteSinIdentificar.Id);
        Assert.Equal(17m, stockLote.Cantidad);
    }
}
