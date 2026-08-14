using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-13-stock-inteligente, Slice 5 (tasks 5.6-5.19): rotación — la trampa del neteo (mutation
/// target 5.6), el borde de ventana en la zona horaria del PV (5.7), la historia cero (5.8), el
/// corto-circuito de PV sin mínimos por conteo exacto de queries (5.9), los motivos excluidos, el
/// override <c>?dias=</c> en ambas rutas, la ausencia (nunca fila en cero) de <c>GET /rotacion</c>,
/// <c>dias_cobertura_objetivo</c> alimentando <c>minimoSugerido</c> y el invariante de no-escritura
/// automática a <c>stock.minimo</c>. <see cref="ReposicionReporteTests"/> cubre el resto de
/// <c>GET /api/reportes/stock/reposicion</c> (slice 4); acá solo lo que la rotación agrega.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RotacionReporteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Vendedor);

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

    /// <summary>Cuenta cada comando que dispara <c>ReaderExecuting</c> — mismo criterio que
    /// <c>VentasCheckoutTests.ContadorDeComandos</c> (task 5.14, mutation target 5.9): el <c>SET
    /// LOCAL</c> de <c>InterceptorDeContextoDeTenant</c> corre por <c>ExecuteNonQueryAsync</c> en
    /// <c>ConnectionOpened</c>, nunca cuenta.</summary>
    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
    {
        var host = factory ?? fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area rotacion", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, vendedor);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task SembrarStockAsync(Contexto ctx, int idArticulo, decimal cantidad, decimal? minimo, decimal? reposicion = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Siembra directa de un movimiento del ledger (mismo criterio que
    /// <c>ComprasAnulacionYConcurrenciaTests.ReducirStockComoVentaAsync</c>) — <c>id_empleado</c>
    /// es cualquier usuario del tenant, <c>id_comprobante_venta</c>/<c>id_comprobante_compra</c>
    /// no llevan FK obligatoria cuando son <c>null</c>.</summary>
    private async Task SembrarMovimientoAsync(
        Contexto ctx, int idArticulo, decimal cantidad, MotivoStock motivo, DateTimeOffset creadoEl,
        int? idComprobanteCompra = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();

        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = cantidad,
            Motivo = motivo, IdEmpleado = idEmpleado, CreadoEl = creadoEl, IdComprobanteCompra = idComprobanteCompra
        });
        await db.SaveChangesAsync();
    }

    private async Task<decimal?> LeerMinimoPersistidoAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Minimo)
            .FirstAsync();
    }

    private static Task<HttpResponseMessage> LlamarRotacionAsync(HttpClient cliente, int idPuntoVenta, int? dias = null) =>
        cliente.GetAsync(
            $"/api/reportes/stock/rotacion?idPuntoVenta={idPuntoVenta}"
            + (dias is { } valorDias ? $"&dias={valorDias}" : string.Empty));

    private static async Task<Rotacion> ObtenerRotacionAsync(HttpClient cliente, int idPuntoVenta, int? dias = null)
    {
        var respuesta = await LlamarRotacionAsync(cliente, idPuntoVenta, dias);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Rotacion>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> LlamarReposicionAsync(HttpClient cliente, int idPuntoVenta, int? dias = null) =>
        cliente.GetAsync(
            $"/api/reportes/stock/reposicion?idPuntoVenta={idPuntoVenta}"
            + (dias is { } valorDias ? $"&dias={valorDias}" : string.Empty));

    private static async Task<Reposicion> ObtenerReposicionAsync(HttpClient cliente, int idPuntoVenta, int? dias = null)
    {
        var respuesta = await LlamarReposicionAsync(cliente, idPuntoVenta, dias);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Reposicion>(cuerpo, OpcionesJson)!;
    }

    // ---- compra real, para el único movimiento que necesita un id_comprobante_compra con FK ----

    private async Task<(int IdProveedor, int IdTipoCFA)> PrepararCompraAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = "Proveedor rotacion", IdCondicionFiscal = idCondicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return (proveedor.Id, idTipoCFA);
    }

    private static async Task<int> CrearConfirmarYAnularCompraAsync(
        Contexto ctx, int idProveedor, int idTipoCFA, int idArticulo, decimal unidades, decimal costoUnitario)
    {
        var solicitud = new SolicitudDeCompra(
            idProveedor, idTipoCFA, ctx.IdPuntoVenta, $"0001-{Guid.NewGuid().ToString("N")[..8]}",
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(idArticulo, "Item rotacion", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva)]);

        var creada = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoCreada = await creada.Content.ReadAsStringAsync();
        Assert.True(creada.StatusCode == HttpStatusCode.Created, cuerpoCreada);
        var detalle = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCreada, OpcionesJson)!;

        var confirmada = await ctx.Admin.PostAsync($"/api/compras/{detalle.Id}/confirmar", null);
        var cuerpoConfirmada = await confirmada.Content.ReadAsStringAsync();
        Assert.True(confirmada.StatusCode == HttpStatusCode.OK, cuerpoConfirmada);

        var anulada = await ctx.Admin.PostAsync($"/api/compras/{detalle.Id}/anular", null);
        var cuerpoAnulada = await anulada.Content.ReadAsStringAsync();
        Assert.True(anulada.StatusCode == HttpStatusCode.OK, cuerpoAnulada);

        return detalle.Id;
    }

    // ---- task 5.10 / mutation target 5.6: la trampa del neteo -------------------------------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): <c>&amp;&amp;
    /// m.IdComprobanteCompra == null</c> en <c>LeerConsumoAsync</c>. Secuencia: compra (15
    /// unidades, confirmada y luego ANULADA — motivo <c>anulacion</c> CON <c>id_comprobante_compra</c>,
    /// EXCLUIDA) → venta directa (8 unidades) → anulación de la venta (motivo <c>anulacion</c> SIN
    /// <c>id_comprobante_compra</c>, INCLUIDA). Consumo esperado: 8 − 3 = 5 — las cuatro magnitudes
    /// (15, 8, 3, 5) son todas distintas, así que ninguna combinación accidental de otro subconjunto
    /// puede producir el mismo resultado. Mutación aplicada (borrar <c>&amp;&amp;
    /// m.IdComprobanteCompra == null</c> de <c>LeerConsumoAsync</c>): la reversión de la compra
    /// (<c>-15</c>, motivo <c>anulacion</c>) queda incluida junto a las otras dos filas, neto
    /// <c>-8 + 3 + (-15) = -20</c>, <c>-neto = 20</c> — este test pasó de esperar <c>5</c> y
    /// obtener <c>20</c> — FALLÓ — a pasar al revertir.</summary>
    [Fact]
    public async Task LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas()
    {
        var ctx = await PrepararAsync(nameof(LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-trampa-neteo");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 20m, minimo: null);

        var (idProveedor, idTipoCFA) = await PrepararCompraAsync(ctx);
        await CrearConfirmarYAnularCompraAsync(ctx, idProveedor, idTipoCFA, idArticulo, unidades: 15m, costoUnitario: 100m);

        var ahora = DateTimeOffset.UtcNow;
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -8m, MotivoStock.Venta, ahora);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 3m, MotivoStock.Anulacion, ahora.AddMinutes(1), idComprobanteCompra: null);

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(5m, fila.ConsumoEnVentana);
    }

    // ---- task 5.11 / mutation target 5.7: el borde de ventana en la zona del PV -------------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): la conversión de zona que
    /// <c>ObtenerRotacionAsync</c> pasa a <c>ReglaDeReposicion.VentanaDeRotacion</c> (design decisión
    /// 7). Reloj fijado a mediodía UTC del 2026-08-14 (así "hoy" es el mismo día en UTC y en
    /// <c>America/Argentina/Buenos_Aires</c>, -03:00 — el borde de ventana, no la fecha, carga la
    /// aserción), <c>dias=1</c>: la ventana correcta es <c>[2026-08-14T03:00Z,
    /// 2026-08-15T03:00Z)</c>. Un movimiento a las 02:00Z (23:00 local del 13/8) queda AFUERA; uno a
    /// las 04:00Z (01:00 local del 14/8) queda ADENTRO — magnitudes distintas (13 vs 9). Mutación
    /// aplicada (reemplazar <c>TimeZoneInfo.FindSystemTimeZoneById(zonaId)</c> por
    /// <c>TimeZoneInfo.Utc</c> en la llamada a <c>VentanaDeRotacion</c> dentro de
    /// <c>ObtenerRotacionAsync</c>): la ventana se corre a <c>[2026-08-14T00:00Z,
    /// 2026-08-15T00:00Z)</c>, el movimiento "afuera" (02:00Z) entra también y
    /// <c>consumoEnVentana</c> pasa de <c>9</c> a <c>22</c> — este test FALLÓ (esperaba 9, obtuvo
    /// 22) con la mutación aplicada; revertido, vuelve a pasar.</summary>
    [Fact]
    public async Task LaVentanaDeRotacionResuelveElBordeEnLaZonaHorariaDelPuntoDeVenta()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(
            nameof(LaVentanaDeRotacionResuelveElBordeEnLaZonaHorariaDelPuntoDeVenta), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-borde-zona");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 20m, minimo: null);

        await SembrarMovimientoAsync(
            ctx, idArticulo, cantidad: -13m, MotivoStock.Venta, new DateTimeOffset(2026, 8, 14, 2, 0, 0, TimeSpan.Zero));
        await SembrarMovimientoAsync(
            ctx, idArticulo, cantidad: -9m, MotivoStock.Venta, new DateTimeOffset(2026, 8, 14, 4, 0, 0, TimeSpan.Zero));

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 1);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(9m, fila.ConsumoEnVentana);
    }

    // ---- task 5.12 / mutation target 5.8: historia cero muestra nulos honestos, nunca cero --------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): el guard
    /// <c>netoConsumido is null ⇒ null</c> de <c>ReglaDeReposicion.ConsumoDiario</c> (ya probado en
    /// Domain puro por <c>ReglaDeReposicionTests</c>) — acá se prueba el WIRING de extremo a
    /// extremo: un artículo bajo mínimo pero SIN ningún movimiento calificado en la ventana debe
    /// mostrar <c>consumoDiarioPromedio</c>/<c>diasDeCobertura</c> nulos en <c>/reposicion</c>, nunca
    /// cero.</summary>
    [Fact]
    public async Task UnArticuloSinHistoriaDeConsumoMuestraNulosDeRotacionEnLaReposicionNuncaCero()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinHistoriaDeConsumoMuestraNulosDeRotacionEnLaReposicionNuncaCero));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-historia-cero");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 5m, minimo: 10m);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(reposicion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Null(fila.ConsumoDiarioPromedio);
        Assert.Null(fila.DiasDeCobertura);
    }

    // ---- task 5.13: los cinco motivos no calificados nunca alteran el consumo ----------------------

    [Fact]
    public async Task UnaSecuenciaDeMotivosNoCalificadosNuncaAlteraElConsumoEnVentana()
    {
        var ctx = await PrepararAsync(nameof(UnaSecuenciaDeMotivosNoCalificadosNuncaAlteraElConsumoEnVentana));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-motivos-excluidos");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 50m, minimo: null);

        var ahora = DateTimeOffset.UtcNow;
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -20m, MotivoStock.Venta, ahora);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 1m, MotivoStock.Ajuste, ahora.AddMinutes(1));
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 2m, MotivoStock.Inventario, ahora.AddMinutes(2));
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -3m, MotivoStock.Decomiso, ahora.AddMinutes(3));
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 4m, MotivoStock.Transferencia, ahora.AddMinutes(4));
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 5m, MotivoStock.Reclasificacion, ahora.AddMinutes(5));

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(20m, fila.ConsumoEnVentana);
    }

    // ---- task 5.14 / mutation target 5.9: PV sin mínimos, conteo exacto de queries -----------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): el corto-circuito
    /// <c>crudas.Count == 0</c> de <c>ObtenerReposicionAsync</c> (design decisión 12) — un PV con
    /// cientos de artículos stockeados y CERO mínimos configurados tiene que costar exactamente 6
    /// consultas (2 de <c>ResolverContextoAsync</c>, 2 de <c>ResolverDiasRotacionAsync</c>, 1 de
    /// <c>ConstruirQueryDeReposicion</c>, la sexta contada por el propio <c>ResolverAsync</c> de
    /// <c>ServicioDeParametros</c> como <c>ValidarPuntoVentaDeLaEmpresaAsync</c> + la consulta de
    /// <c>parametros</c> propiamente dicha) — nunca disparar <c>LeerConsumoAsync</c>. Llama al
    /// servicio DIRECTO (no vía HTTP) con un <c>ContadorDeComandos</c> propio, mismo criterio que
    /// <c>VentasCheckoutTests.EmitirYContarConsultasAsync</c>. Mutación aplicada (borrar el
    /// <c>if (crudas.Count == 0) return …</c>): el conteo sube (la rama vacía igual dispara
    /// <c>LeerConsumoAsync</c>, que filtra por una lista vacía de ids pero SÍ emite una consulta) y
    /// este test FALLÓ; revertido, vuelve a pasar.</summary>
    [Fact]
    public async Task UnPuntoDeVentaSinMinimosNoConsultaMovimientosStock()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaSinMinimosNoConsultaMovimientosStock));

        const int cantidadDeArticulos = 200;
        for (var i = 0; i < cantidadDeArticulos; i++)
        {
            var idArticulo = await SembrarArticuloAsync(ctx, $"rotacion-sin-minimo-{i}");
            await SembrarStockAsync(ctx, idArticulo, cantidad: 10m, minimo: null);
        }

        var contador = new ContadorDeComandos();
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
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDeLotes = new ServicioDeLotes(db, reloj, contexto);
        var servicioDeParametros = new ServicioDeParametros(db, reloj, servicioDeLotes);
        var servicio = new ServicioDeReportesDeStock(db, servicioDeParametros, reloj);

        var reposicion = await servicio.ObtenerReposicionAsync(ctx.IdPuntoVenta, dias: null);

        Assert.Empty(reposicion.Filas);
        Assert.Equal(6, contador.Consultas);
    }

    // ---- task 5.15: una cifra de rotación arbitraria nunca bloquea ni destraba la alerta ------------

    [Fact]
    public async Task UnaCifraDeRotacionArbitrariaNuncaGatillaNiSuprimeLaAlerta()
    {
        var ctx = await PrepararAsync(nameof(UnaCifraDeRotacionArbitrariaNuncaGatillaNiSuprimeLaAlerta));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-cifra-arbitraria");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 8m, minimo: 10m);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -500m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(reposicion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(8m, fila.Cantidad);
        Assert.Equal(10m, fila.Minimo);
        Assert.NotNull(fila.ConsumoDiarioPromedio);
    }

    // ---- task 5.16: ?dias= ensancha la ventana en las dos rutas de rotación -------------------------

    [Fact]
    public async Task UnDiasExplicitoEnsanchaLaVentanaEnReposicionYEnRotacion()
    {
        var ctx = await PrepararAsync(nameof(UnDiasExplicitoEnsanchaLaVentanaEnReposicionYEnRotacion));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-dias-override");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 50m, minimo: 100m);
        await SembrarMovimientoAsync(
            ctx, idArticulo, cantidad: -60m, MotivoStock.Venta, DateTimeOffset.UtcNow.AddDays(-45));

        var reposicionDefault = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);
        var filaDefault = Assert.Single(reposicionDefault.Filas, f => f.IdArticulo == idArticulo);
        Assert.Null(filaDefault.ConsumoDiarioPromedio);

        var rotacionDefaultRespuesta = await LlamarRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Equal(HttpStatusCode.OK, rotacionDefaultRespuesta.StatusCode);
        var rotacionDefault = JsonSerializer.Deserialize<Rotacion>(
            await rotacionDefaultRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.DoesNotContain(rotacionDefault.Filas, f => f.IdArticulo == idArticulo);

        var reposicionAmpliada = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 60);
        var filaAmpliada = Assert.Single(reposicionAmpliada.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(1m, filaAmpliada.ConsumoDiarioPromedio);

        var rotacionAmpliada = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 60);
        var filaRotacionAmpliada = Assert.Single(rotacionAmpliada.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(60m, filaRotacionAmpliada.ConsumoEnVentana);
        Assert.Equal(1m, filaRotacionAmpliada.ConsumoDiarioPromedio);
    }

    // ---- task 5.17: GET /rotacion omite un artículo sin movimiento calificado — ausencia, no cero --

    [Fact]
    public async Task GetRotacionOmiteUnArticuloSinMovimientoCalificadoNuncaUnaFilaEnCero()
    {
        var ctx = await PrepararAsync(nameof(GetRotacionOmiteUnArticuloSinMovimientoCalificadoNuncaUnaFilaEnCero));
        var idConHistoria = await SembrarArticuloAsync(ctx, "rotacion-con-historia");
        await SembrarStockAsync(ctx, idConHistoria, cantidad: 5m, minimo: null);
        await SembrarMovimientoAsync(ctx, idConHistoria, cantidad: -6m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        var idSinHistoria = await SembrarArticuloAsync(ctx, "rotacion-sin-historia");
        await SembrarStockAsync(ctx, idSinHistoria, cantidad: 5m, minimo: null);

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Contains(rotacion.Filas, f => f.IdArticulo == idConHistoria);
        Assert.DoesNotContain(rotacion.Filas, f => f.IdArticulo == idSinHistoria);
    }

    // ---- task 5.18: dias_cobertura_objetivo alimenta minimoSugerido, nunca escribe minimo ----------

    /// <summary>spec parametros-operativos: "dias_cobertura_objetivo feeds minimoSugerido, never
    /// minimo directly" — consumo total 90 sobre la ventana default de 30 días ⇒ consumoDiarioPromedio
    /// = 3; dias_cobertura_objetivo default = 7 ⇒ minimoSugerido = 21. Cierra también la mitad
    /// no-escritura de la task (re-lee <c>stock.minimo</c> tras la corrida y confirma que sigue
    /// <c>NULL</c>).</summary>
    [Fact]
    public async Task DiasCoberturaObjetivoAlimentaElMinimoSugeridoDeRotacionSinEscribirEnMinimo()
    {
        var ctx = await PrepararAsync(nameof(DiasCoberturaObjetivoAlimentaElMinimoSugeridoDeRotacionSinEscribirEnMinimo));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-dias-cobertura");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 40m, minimo: null);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -90m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(3m, fila.ConsumoDiarioPromedio);
        Assert.Equal(21m, fila.MinimoSugerido);
        Assert.Equal(7, rotacion.DiasCoberturaObjetivo);

        Assert.Null(await LeerMinimoPersistidoAsync(ctx, idArticulo));
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #1 (MAJOR): dias_cobertura_objetivo <= 0 --

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): la llamada a
    /// <c>ReglaDeReposicion.ExigirVentanaValida(diasDeCoberturaObjetivo, "dias_cobertura_invalido")</c>
    /// dentro de <c>ObtenerRotacionAsync</c> — antes del fix, ningún test pasaba por ella (a
    /// diferencia de <c>dias_rotacion</c>, <c>dias_cobertura_objetivo</c> nunca acepta un
    /// <c>?dias=</c> de query, solo el parámetro almacenado) y un <c>dias_cobertura_objetivo = 0</c>
    /// persistido fabricaba un <c>minimoSugerido</c> igual a <c>0</c> en vez de rechazar con 400.
    /// Mutación aplicada (borrar el <c>ExigirVentanaValida</c> agregado): este test pasó de FALLAR
    /// (200 en lugar de 400) a pasar al revertir.</summary>
    [Fact]
    public async Task UnDiasDeCoberturaObjetivoInvalidoEsRechazadoConCuatrocientos()
    {
        var ctx = await PrepararAsync(nameof(UnDiasDeCoberturaObjetivoInvalidoEsRechazadoConCuatrocientos));

        var alta = await ctx.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}",
            new ParametroAlta("dias_cobertura_objetivo", "0", null));
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);

        var respuesta = await LlamarRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dias_cobertura_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #2 (WARNING): clamp a 0, nunca negativo --

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): <c>Math.Max(0m, -par.Value)</c>
    /// en <c>ObtenerRotacionAsync</c> — el contrato documentado en <see cref="FilaDeRotacion"/>
    /// ("recortado a 0 — nunca negativo — cuando las devoluciones superan a las ventas") no tenía
    /// test propio. Venta de 3 unidades (cantidad <c>-3</c>) y anulación-de-venta de 8 unidades
    /// (cantidad <c>+8</c>, sin <c>id_comprobante_compra</c>) ⇒ neto <c>= -3 + 8 = +5</c> (las
    /// devoluciones superan a las ventas dentro de la ventana). El artículo SÍ tiene historia
    /// calificante (design decisión 14) — la fila existe, pero <c>ConsumoEnVentana</c> Y
    /// <c>ConsumoDiarioPromedio</c> clampean a <c>0</c>, NUNCA a <c>5</c> (que sería
    /// <c>Math.Abs(neto)</c>, la mutación bajo prueba). Mutación aplicada (reemplazar
    /// <c>Math.Max(0m, -par.Value)</c> por <c>Math.Abs(par.Value)</c>): este test pasó de FALLAR
    /// (esperaba <c>0</c>, obtuvo <c>5</c>) a pasar al revertir.</summary>
    [Fact]
    public async Task UnaVentanaConDevolucionesNetasPositivasClampeaElConsumoAZeroNuncaNegativo()
    {
        var ctx = await PrepararAsync(nameof(UnaVentanaConDevolucionesNetasPositivasClampeaElConsumoAZeroNuncaNegativo));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-devoluciones-netas");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 20m, minimo: null);

        var ahora = DateTimeOffset.UtcNow;
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -3m, MotivoStock.Venta, ahora);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: 8m, MotivoStock.Anulacion, ahora.AddMinutes(1), idComprobanteCompra: null);

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(0m, fila.ConsumoEnVentana);
        Assert.Equal(0m, fila.ConsumoDiarioPromedio);
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #3 (WARNING): bordes exactos de la ventana

    /// <summary>Nombra las cláusulas bajo prueba (mutation-proof-tests): <c>m.CreadoEl >= desdeUtc</c>
    /// (borde inferior INCLUSIVO) y <c>m.CreadoEl &lt; hastaUtcExclusivo</c> (borde superior
    /// EXCLUSIVO) de <c>LeerConsumoAsync</c> — el test 5.11 pinnea la ZONA horaria pero ningún
    /// movimiento cae exactamente en un borde. Mismo reloj fijo que 5.11 (mediodía UTC del
    /// 2026-08-14), <c>dias=1</c> ⇒ ventana <c>[2026-08-14T03:00Z, 2026-08-15T03:00Z)</c>. Un
    /// movimiento EXACTAMENTE en <c>desdeUtc</c> (cantidad <c>-7</c>, INCLUIDO) y otro EXACTAMENTE
    /// en <c>hastaUtcExclusivo</c> (cantidad <c>-11</c>, EXCLUIDO) — magnitudes distintas. Mutación
    /// del borde inferior (<c>&gt;=</c> → <c>&gt;</c>): el movimiento en <c>desdeUtc</c> queda
    /// afuera, el artículo pierde toda historia calificada y desaparece de <c>Filas</c> —
    /// <c>Assert.Single</c> FALLA. Mutación del borde superior (<c>&lt;</c> → <c>&lt;=</c>): el
    /// movimiento en <c>hastaUtcExclusivo</c> entra, <c>ConsumoEnVentana</c> pasa de <c>7</c> a
    /// <c>18</c> — FALLA. Ambas mutaciones revertidas, el test vuelve a pasar.</summary>
    [Fact]
    public async Task LaVentanaDeRotacionIncluyeElBordeInferiorYExcluyeElBordeSuperiorExactos()
    {
        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)))));

        var ctx = await PrepararAsync(
            nameof(LaVentanaDeRotacionIncluyeElBordeInferiorYExcluyeElBordeSuperiorExactos), factoryConRelojFijo);
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-borde-exacto");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 20m, minimo: null);

        await SembrarMovimientoAsync(
            ctx, idArticulo, cantidad: -7m, MotivoStock.Venta, new DateTimeOffset(2026, 8, 14, 3, 0, 0, TimeSpan.Zero));
        await SembrarMovimientoAsync(
            ctx, idArticulo, cantidad: -11m, MotivoStock.Venta, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var rotacion = await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 1);

        var fila = Assert.Single(rotacion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(7m, fila.ConsumoEnVentana);
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #4 (WARNING): DiasDeCobertura usa Cantidad

    /// <summary>Nombra el argumento bajo prueba (mutation-proof-tests): <c>f.Cantidad</c> (NUNCA
    /// <c>f.Minimo</c>) como primer argumento de <c>ReglaDeReposicion.DiasDeCobertura</c> dentro de
    /// <c>ObtenerReposicionAsync</c> — ningún test previo asertaba un valor concreto de
    /// <c>DiasDeCobertura</c>. Cantidad <c>10</c>, mínimo <c>100</c> (bajo mínimo, alerta), consumo
    /// total <c>60</c> sobre la ventana default de 30 días ⇒ <c>consumoDiarioPromedio = 2</c> ⇒
    /// <c>DiasDeCobertura = cantidad / consumo = 10 / 2 = 5</c> — DISTINTO del que saldría con
    /// <c>minimo</c> como argumento (<c>100 / 2 = 50</c>). Mutación aplicada (<c>f.Cantidad</c> →
    /// <c>f.Minimo</c>): este test pasó de FALLAR (esperaba <c>5</c>, obtuvo <c>50</c>) a pasar al
    /// revertir.</summary>
    [Fact]
    public async Task LaCoberturaDeDiasSeCalculaSobreCantidadNuncaSobreMinimo()
    {
        var ctx = await PrepararAsync(nameof(LaCoberturaDeDiasSeCalculaSobreCantidadNuncaSobreMinimo));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-cobertura-cantidad");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 10m, minimo: 100m);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -60m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(reposicion.Filas, f => f.IdArticulo == idArticulo);
        Assert.Equal(2m, fila.ConsumoDiarioPromedio);
        Assert.Equal(5m, fila.DiasDeCobertura);
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #5 (WARNING): artículo soft-deleted -------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): <c>.Where(par =>
    /// nombres.ContainsKey(par.Key))</c> en <c>ObtenerRotacionAsync</c> — el ledger de
    /// <c>movimientos_stock</c> es append-only y no conoce baja lógica (mismo trade-off que
    /// <c>ExistenciasTests.UnArticuloEliminadoNuncaApareceEnLasExistencias</c>), así que un artículo
    /// con ventas calificadas en la ventana que luego se da de baja debe DESAPARECER de
    /// <c>Filas</c> con 200, nunca reventar con <c>KeyNotFoundException</c> (500) al indexar
    /// <c>nombres[par.Key]</c>. Secuencia: venta dentro de la ventana → <c>DELETE
    /// /api/articulos/{id}</c> (camino real del API, baja lógica) → <c>GET /rotacion</c>. Mutación
    /// aplicada (borrar el <c>.Where(...)</c>): este test pasó de FALLAR (500/KeyNotFoundException
    /// en vez de 200) a pasar al revertir.</summary>
    [Fact]
    public async Task UnArticuloDadoDeBajaConHistoriaCalificadaDesapareceDeLaRotacionSinReventar()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloDadoDeBajaConHistoriaCalificadaDesapareceDeLaRotacionSinReventar));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-baja-con-historia");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 5m, minimo: null);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -6m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        var baja = await ctx.Admin.DeleteAsync($"/api/articulos/{idArticulo}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var respuesta = await LlamarRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var rotacion = JsonSerializer.Deserialize<Rotacion>(cuerpo, OpcionesJson)!;

        Assert.DoesNotContain(rotacion.Filas, f => f.IdArticulo == idArticulo);
    }

    // ---- judgment-day round 1, slice 5, juez B, hallazgo #6 (SUGGESTION): 403, espejo de 4.11 ------

    [Fact]
    public async Task UnVendedorEsRechazadoDelReporteDeRotacion()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelReporteDeRotacion));

        var respuesta = await LlamarRotacionAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 5.19: ninguna de las dos rutas de rotación escribe stock.minimo automáticamente ------

    /// <summary>APPLY NOTE: la task 5.19 dice literalmente "after /reposicion or /reposicion runs"
    /// — desvío registrado en tasks.md: se interpreta como "/reposicion or /rotacion" (la única
    /// lectura consistente con el resto de la spec, que habla de ambas rutas nombradas en la task
    /// 5.16 inmediatamente anterior).</summary>
    [Fact]
    public async Task NingunaDeLasDosRutasDeRotacionEscribeEnStockMinimoAutomaticamente()
    {
        var ctx = await PrepararAsync(nameof(NingunaDeLasDosRutasDeRotacionEscribeEnStockMinimoAutomaticamente));
        var idArticulo = await SembrarArticuloAsync(ctx, "rotacion-sin-escritura");
        await SembrarStockAsync(ctx, idArticulo, cantidad: 12m, minimo: null);
        await SembrarMovimientoAsync(ctx, idArticulo, cantidad: -30m, MotivoStock.Venta, DateTimeOffset.UtcNow);

        await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Null(await LeerMinimoPersistidoAsync(ctx, idArticulo));

        await ObtenerRotacionAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Null(await LeerMinimoPersistidoAsync(ctx, idArticulo));
    }
}
