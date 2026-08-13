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
using Ways.Domain.Common;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 3 (tasks 3.5-3.10): <c>ServicioDeLotes</c> punta a punta —
/// la carrera de <c>ux_lotes_articulo_codigo</c> en el alta admin (a diferencia de
/// <c>LotesBackstopTests</c>, Slice 1, que la prueba con INSERTs crudos sin pasar por
/// <c>ServicioDeLotes</c>), la inmutabilidad de <c>fecha_vencimiento</c> bajo el lock de
/// <c>ResolverOCrearAsync</c>, la idempotencia del lote sin identificar, la query acotada de
/// <c>LeerSaldosAsync</c> (design decisión 6) y el gating de rol de <c>POST /api/stock/lotes</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeLotesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora => ahora;
    }

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
            IdTenant = resultado.IdTenant, Nombre = "Lotes-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
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

    // ---- task 3.5: db-error-backstops — la carrera de ux_lotes_articulo_codigo en el alta admin ---

    /// <summary>Llama a <c>ServicioDeLotes.CrearAsync</c> directamente (sin HTTP, dos
    /// <c>WaysDbContext</c> independientes) para capturar la excepción CRUDA — el
    /// <c>DbUpdateException</c>/<c>PostgresException</c> con SQLSTATE <c>23505</c> ANTES de que
    /// <c>ManejadorDeErrores</c> (que solo corre en el pipeline HTTP) tenga oportunidad de
    /// traducirlo. A diferencia de <c>ResolverOCrearAsync</c> (design decisión 4, <c>ON CONFLICT
    /// ... DO UPDATE</c> — nunca choca), esta vía es un <c>INSERT</c> EF plano: un admin que da
    /// de alta un duplicado tiene que ver un genuino 409, no una reutilización silenciosa.</summary>
    [Fact]
    public async Task DosCrearAsyncConcurrentesDelMismoCodigoChocanConSqlstate23505AntesDelMapeo()
    {
        var ctx = await PrepararAsync(nameof(DosCrearAsyncConcurrentesDelMismoCodigoChocanConSqlstate23505AntesDelMapeo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-race-app");

        await using var dbA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        await using var dbB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var servicioA = new ServicioDeLotes(dbA, reloj);
        var servicioB = new ServicioDeLotes(dbB, reloj);

        var solicitud = new SolicitudDeLote(idArticulo, "L-RACE-APP", new DateOnly(2030, 12, 31));

        var tareaA = servicioA.CrearAsync(solicitud, CancellationToken.None);
        var tareaB = servicioB.CrearAsync(solicitud, CancellationToken.None);

        // Mismo criterio que LotesBackstopTests: esperar las dos tareas sin que la primera
        // excepción cancele la espera de la otra.
        await Task.WhenAll(tareaA.ContinueWith(_ => { }), tareaB.ContinueWith(_ => { }));

        var tareas = new Task[] { tareaA, tareaB };
        Assert.Equal(1, tareas.Count(t => t.IsCompletedSuccessfully));
        Assert.Equal(1, tareas.Count(t => t.IsFaulted));

        var tareaFallida = tareas.Single(t => t.IsFaulted);
        var dbUpdateException = Assert.IsType<DbUpdateException>(tareaFallida.Exception!.InnerException);
        var postgresException = Assert.IsType<PostgresException>(dbUpdateException.InnerException);
        Assert.Equal("23505", postgresException.SqlState);
        Assert.Equal("ux_lotes_articulo_codigo", postgresException.ConstraintName);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var sobrevivientes = await db.Lotes.CountAsync(l => l.IdArticulo == idArticulo && l.Codigo == "L-RACE-APP");
        Assert.Equal(1, sobrevivientes);
    }

    /// <summary>El mismo choque, ahora punta a punta contra <c>POST /api/stock/lotes</c> — el
    /// camino que sí atraviesa <c>ManejadorDeErrores</c> (el "mapeo" de la prueba de arriba).
    /// task 3.5 completo: exactamente un <c>201</c> y un <c>409 lote_duplicado</c>.</summary>
    [Fact]
    public async Task DosPostConcurrentesAApiStockLotesConElMismoCodigoDanExactamenteUnCreadoYUnConflicto()
    {
        var ctx = await PrepararAsync(nameof(DosPostConcurrentesAApiStockLotesConElMismoCodigoDanExactamenteUnCreadoYUnConflicto));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-race-http");

        var solicitud = new SolicitudDeLote(idArticulo, "L-RACE-HTTP", new DateOnly(2030, 12, 31));

        var tareaA = ctx.Admin.PostAsJsonAsync("/api/stock/lotes", solicitud);
        var tareaB = ctx.Admin.PostAsJsonAsync("/api/stock/lotes", solicitud);

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var conflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await conflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_duplicado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.6: inmutabilidad de fecha_vencimiento bajo el lock de ResolverOCrearAsync -------

    [Fact]
    public async Task UnaSegundaResolucionConVencimientoCoincidenteReutilizaElLote()
    {
        var ctx = await PrepararAsync(nameof(UnaSegundaResolucionConVencimientoCoincidenteReutilizaElLote));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-inmutable-ok");

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var fecha = new DateOnly(2030, 12, 31);
        var momento = DateTimeOffset.UtcNow;

        var idLote1 = await ServicioDeLotes.ResolverOCrearAsync(
            conexion, null, ctx.IdTenant, idArticulo, "L-INM-1", fecha, momento, CancellationToken.None);
        var idLote2 = await ServicioDeLotes.ResolverOCrearAsync(
            conexion, null, ctx.IdTenant, idArticulo, "L-INM-1", fecha, momento, CancellationToken.None);

        Assert.Equal(idLote1, idLote2);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.Lotes.CountAsync(l => l.IdArticulo == idArticulo && l.Codigo == "L-INM-1");
        Assert.Equal(1, filas);
    }

    [Fact]
    public async Task UnaSegundaResolucionConVencimientoDistintoEsRechazadaConLoteVencimientoIncompatible()
    {
        var ctx = await PrepararAsync(nameof(UnaSegundaResolucionConVencimientoDistintoEsRechazadaConLoteVencimientoIncompatible));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-inmutable-choque");

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var momento = DateTimeOffset.UtcNow;

        var idLote1 = await ServicioDeLotes.ResolverOCrearAsync(
            conexion, null, ctx.IdTenant, idArticulo, "L-INM-2", new DateOnly(2030, 12, 31), momento, CancellationToken.None);

        var excepcion = await Assert.ThrowsAsync<ErrorDominio>(() =>
            ServicioDeLotes.ResolverOCrearAsync(
                conexion, null, ctx.IdTenant, idArticulo, "L-INM-2", new DateOnly(2031, 1, 15), momento, CancellationToken.None));

        Assert.Equal("lote_vencimiento_incompatible", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.Lotes.CountAsync(l => l.IdArticulo == idArticulo && l.Codigo == "L-INM-2");
        Assert.Equal(1, filas);

        var fechaGuardada = await db.Lotes.Where(l => l.Id == idLote1).Select(l => l.FechaVencimiento).SingleAsync();
        Assert.Equal(new DateOnly(2030, 12, 31), fechaGuardada);
    }

    // ---- task 3.7: código reservado del lote sin identificar en el alta admin ---------------------

    [Fact]
    public async Task UnCodigoReservadoEnAltaEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnCodigoReservadoEnAltaEsRechazadoCon400));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-codigo-reservado");

        var solicitud = new SolicitudDeLote(idArticulo, ReglaDeLotes.CodigoSinIdentificar, new DateOnly(2030, 12, 31));
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/lotes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("codigo_de_lote_reservado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.8: idempotencia del lote sin identificar ------------------------------------------

    /// <summary>Design decisión 5: el código reservado sirve exactamente un target de conflicto
    /// (<c>ux_lotes_articulo_codigo</c>), así que dos resoluciones — acá, simulando dos escritores
    /// independientes de dos puntos de venta distintos, ya que el lote sin identificar es
    /// tenant-wide por artículo, no por PV — convergen en la MISMA fila.</summary>
    [Fact]
    public async Task ElLoteSinIdentificarSeCreaUnaVezYSeReusaEntreDosResoluciones()
    {
        var ctx = await PrepararAsync(nameof(ElLoteSinIdentificarSeCreaUnaVezYSeReusaEntreDosResoluciones));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-identificar");

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var momento = DateTimeOffset.UtcNow;

        var idLote1 = await ServicioDeLotes.ResolverSinIdentificarAsync(
            conexion, null, ctx.IdTenant, idArticulo, momento, CancellationToken.None);
        var idLote2 = await ServicioDeLotes.ResolverSinIdentificarAsync(
            conexion, null, ctx.IdTenant, idArticulo, momento, CancellationToken.None);

        Assert.Equal(idLote1, idLote2);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.Lotes.CountAsync(l => l.IdArticulo == idArticulo && l.EsSinIdentificar);
        Assert.Equal(1, filas);
    }

    // ---- task 3.9: LeerSaldosAsync acotada (design decisión 6) -------------------------------------

    /// <summary>Punta a punta vía <c>GET /api/stock/lotes</c>: prueba en un solo request el
    /// endpoint (task 3.3), la cláusula acotada de <c>LeerSaldosAsync</c> (saldo distinto de cero
    /// + sin-identificar SIEMPRE, sin importar su saldo, EXCLUYENDO un lote fechado de saldo cero
    /// no pedido) y la proyección server-side de <c>estado</c>/<c>sugerido</c> (design decisión
    /// 19, dto-contract-honesty: cada campo de <c>LoteListado</c> tiene un destino real acá).
    /// <c>sugerido</c> demuestra la distinción entre ORDEN de display (sin-identificar primero,
    /// <c>OrdenarFefo</c>) y PICK real (<c>ElegirFefo</c>, saldo positivo only) — acá el
    /// sin-identificar tiene saldo cero, así que el lote fechado con saldo es el sugerido pese a
    /// aparecer segundo en la lista.</summary>
    [Fact]
    public async Task GetLotesDevuelveSaldoNoCeroYSinIdentificarExcluyendoUnFechadoDeSaldoCeroNoPedido()
    {
        var ctx = await PrepararAsync(nameof(GetLotesDevuelveSaldoNoCeroYSinIdentificarExcluyendoUnFechadoDeSaldoCeroNoPedido));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-saldos-acotados");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var loteConSaldo = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = "L-CON-SALDO",
            FechaVencimiento = new DateOnly(2030, 6, 15), EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        var loteSinSaldo = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = "L-SIN-SALDO",
            FechaVencimiento = new DateOnly(2030, 9, 1), EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        var loteSinIdentificar = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = ReglaDeLotes.CodigoSinIdentificar,
            FechaVencimiento = null, EsSinIdentificar = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Lotes.AddRange(loteConSaldo, loteSinSaldo, loteSinIdentificar);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = loteConSaldo.Id, IdTenant = ctx.IdTenant, Cantidad = 5m
        });
        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = loteSinSaldo.Id, IdTenant = ctx.IdTenant, Cantidad = 0m
        });
        // loteSinIdentificar deliberadamente SIN fila de stock_lotes: decisión 6 lo incluye igual.
        await db.SaveChangesAsync();

        var respuesta = await ctx.Admin.GetAsync($"/api/stock/lotes?idPuntoVenta={ctx.IdPuntoVenta}&idArticulo={idArticulo}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var lotes = JsonSerializer.Deserialize<List<LoteListado>>(cuerpo, OpcionesJson)!;

        Assert.Equal(2, lotes.Count);
        Assert.DoesNotContain(lotes, l => l.IdLote == loteSinSaldo.Id);

        var conSaldo = lotes.Single(l => l.IdLote == loteConSaldo.Id);
        Assert.Equal(5m, conSaldo.Cantidad);
        Assert.False(conSaldo.EsSinIdentificar);
        Assert.True(conSaldo.Sugerido);

        var sinIdentificar = lotes.Single(l => l.IdLote == loteSinIdentificar.Id);
        Assert.Equal(0m, sinIdentificar.Cantidad);
        Assert.True(sinIdentificar.EsSinIdentificar);
        Assert.False(sinIdentificar.Sugerido);
    }

    /// <summary>La otra mitad de la cláusula acotada — "más los que el cliente nombró" — que el
    /// picker HTTP no ejerce (no acepta una lista de lotes pedidos; eso lo consumen los
    /// escritores de negocio, slices 5-10). Llamada directa a <c>LeerSaldosAsync</c>: un lote
    /// fechado de saldo CERO aparece si y solo si su id está en <c>idsLotePedidos</c>.</summary>
    [Fact]
    public async Task LeerSaldosAsyncIncluyeUnLoteDeSaldoCeroCuandoFueExplicitamentePedido()
    {
        var ctx = await PrepararAsync(nameof(LeerSaldosAsyncIncluyeUnLoteDeSaldoCeroCuandoFueExplicitamentePedido));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-saldo-pedido");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lotePedidoSaldoCero = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = "L-PEDIDO-CERO",
            FechaVencimiento = new DateOnly(2030, 3, 1), EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        var loteNoPedidoSaldoCero = new Lote
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, Codigo = "L-NO-PEDIDO-CERO",
            FechaVencimiento = new DateOnly(2030, 4, 1), EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Lotes.AddRange(lotePedidoSaldoCero, loteNoPedidoSaldoCero);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = lotePedidoSaldoCero.Id, IdTenant = ctx.IdTenant, Cantidad = 0m
        });
        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = loteNoPedidoSaldoCero.Id, IdTenant = ctx.IdTenant, Cantidad = 0m
        });
        await db.SaveChangesAsync();

        var reloj = new RelojFijo(ahora);
        var servicio = new ServicioDeLotes(db, reloj);

        var saldos = await servicio.LeerSaldosAsync(
            ctx.IdPuntoVenta, [idArticulo], [lotePedidoSaldoCero.Id], CancellationToken.None);

        Assert.Contains(saldos, s => s.IdLote == lotePedidoSaldoCero.Id);
        Assert.DoesNotContain(saldos, s => s.IdLote == loteNoPedidoSaldoCero.Id);
    }

    // ---- task 3.10: rol -----------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsBloqueadoDelAltaDeLotes()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsBloqueadoDelAltaDeLotes));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-vendedor-lote");

        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-lote", mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        using var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitud = new SolicitudDeLote(idArticulo, "L-VENDEDOR", new DateOnly(2030, 12, 31));
        var respuesta = await vendedor.PostAsJsonAsync("/api/stock/lotes", solicitud);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
