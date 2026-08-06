using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 4 (tasks 4.1, 4.5, 4.6, design decisión 7):
/// el guard <c>SELECT ... FOR SHARE</c> que <c>ServicioDeGastos</c> toma sobre el header de la
/// compra cuando la solicitud trae <c>idComprobanteCompra</c> — el vínculo feliz, los cuatro
/// rechazos (categoría incoherente, compra inexistente/de otro tenant, borrador, anulada,
/// proveedor que no coincide) y la superficie racy 5 del Backstop Map (gasto ligado ×
/// anulación de la misma compra).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosLigadosACompraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdProveedor2, int IdArticulo,
        int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin);

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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Compras-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        // MedioPago es tenant-scoped (CatalogoSimple : EntidadTenant) — bajo TenantActualFijo
        // .Plataforma el filtro de EF no acota por tenant y .FirstAsync() puede devolver la fila
        // de OTRO tenant creado en paralelo por otra prueba de esta misma colección, violando
        // fk_gastos_medio_pago más adelante. Contexto tenant-scoped propio, mismo criterio que
        // GastosEndpointsTests.PrepararAsync.
        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedor2);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, proveedor2.Id, articulo.Id,
            idAlicuotaIva21, idTipoCFA, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal);
    }

    private static SolicitudDeCompra SolicitudSimple(Contexto ctx, int idProveedor, decimal costoUnitario = 100m, string? numeroExterno = "0001-00000001") =>
        new(
            idProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 10m, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearYConfirmarCompraAsync(
        Contexto ctx, int? idProveedor = null, string? numeroExterno = "0001-00000001")
    {
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", SolicitudSimple(ctx, idProveedor ?? ctx.IdProveedor, numeroExterno: numeroExterno));
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private static SolicitudDeGasto SolicitudDeGastoLigado(
        Contexto ctx, int idComprobanteCompra, decimal importe = 1000m, int? idProveedor = null,
        CategoriaGasto categoria = CategoriaGasto.Proveedor) =>
        new(
            ctx.IdPuntoVenta, categoria, idProveedor, null, "Pago de compra", null, ctx.IdMedioEfectivo, null,
            importe, idComprobanteCompra);

    // ---- task 4.5: vínculo feliz + el pre-check de categoría, antes de la DB ------------------

    [Fact]
    public async Task UnGastoSeLigaAUnaCompraConfirmadaBajoElGateDeTurnoAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnGastoSeLigaAUnaCompraConfirmadaBajoElGateDeTurnoAbierto));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id, importe: 500m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(compra.Id, gasto.IdComprobanteCompra);
        Assert.Equal(ctx.IdProveedor, gasto.IdProveedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var persistido = await db.Gastos.FirstAsync(g => g.Id == gasto.Id);
        Assert.Equal(compra.Id, persistido.IdComprobanteCompra);
    }

    [Fact]
    public async Task UnaCategoriaDistintaDeProveedorNoPuedeLigarseAUnaCompraAntesDeLlegarALaBaseDeDatos()
    {
        var ctx = await PrepararAsync(nameof(UnaCategoriaDistintaDeProveedorNoPuedeLigarseAUnaCompraAntesDeLlegarALaBaseDeDatos));

        // Ni turno abierto ni una compra real: si el chequeo de categoría no corriera ANTES de
        // tocar la base, la respuesta sería 409 turno_no_abierto (o 404), nunca este 400.
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Servicios, null, null, "Gasto mal categorizado", null,
                ctx.IdMedioEfectivo, null, 100m, IdComprobanteCompra: 999999));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_de_compra_debe_ser_de_proveedor", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoLigadoAUnaCompraSigueExigiendoTurnoAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraSigueExigiendoTurnoAbierto));
        var compra = await CrearYConfirmarCompraAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
    }

    // ---- rechazos de la compra ligada: inexistente / otro tenant / borrador / anulada ---------

    [Fact]
    public async Task UnGastoLigadoAUnaCompraInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraInexistenteDevuelve404));
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, idComprobanteCompra: 999999));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnGastoLigadoAUnaCompraDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraDeOtroTenantDevuelve404) + "-B");
        var compraDeB = await CrearYConfirmarCompraAsync(ctxB);
        await AbrirTurnoAsync(ctxA);

        var respuesta = await ctxA.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctxA, compraDeB.Id));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnGastoLigadoAUnaCompraEnBorradorEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraEnBorradorEsRechazado));
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, ctx.IdProveedor));
        var borrador = JsonSerializer.Deserialize<CompraDetalle>(await respuestaCrear.Content.ReadAsStringAsync(), OpcionesJson)!;
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, borrador.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_confirmada", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoLigadoAUnaCompraAnuladaEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraAnuladaEsRechazado));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_anulada", problema.GetProperty("codigo").GetString());
    }

    // ---- id_proveedor: se deriva cuando falta, se exige que coincida cuando viene --------------

    [Fact]
    public async Task ElProveedorAusenteSeDerivaDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(ElProveedorAusenteSeDerivaDeLaCompra));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id, idProveedor: null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(ctx.IdProveedor, gasto.IdProveedor);
    }

    [Fact]
    public async Task UnProveedorQueNoCoincideConLaCompraEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorQueNoCoincideConLaCompraEsRechazado));
        var compra = await CrearYConfirmarCompraAsync(ctx, idProveedor: ctx.IdProveedor);
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id, idProveedor: ctx.IdProveedor2));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proveedor_no_coincide_con_la_compra", problema.GetProperty("codigo").GetString());
    }

    // ---- task 4.6: superficie racy 5, forced rendezvous (row lock natural) --------------------

    /// <summary>Pausa cada transacción manual (<c>ServicioDeGastos.InsertarGastoAsync</c>/
    /// <c>ServicioDeCompras.EjecutarAnulacionAsync</c>) justo DESPUÉS de
    /// <c>BeginTransactionAsync</c>, hasta que el test la libera — mismo patrón que
    /// <c>ComprasAnulacionYConcurrenciaTests.InterceptorDePausaTrasIniciarLaTransaccion</c>.</summary>
    private sealed class InterceptorDePausaTrasIniciarLaTransaccion(
        TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            transaccionIniciada.TrySetResult();
            await puedeContinuar.Task;
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>design decisión 7, Backstop Map racy surface 5: la anulación gana la carrera —
    /// commitea ANTES de que la transacción del gasto retome y tome su <c>FOR SHARE</c>. El
    /// gasto retoma viendo <c>anulada</c> ya comiteada — <c>409 compra_anulada</c>, nunca un
    /// vínculo corrupto a una compra anulada.</summary>
    [Fact]
    public async Task LaAnulacionGanandoLaCarreraDejaAlGastoRechazadoSinVinculoCorrupto()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionGanandoLaCarreraDejaAlGastoRechazadoSinVinculoCorrupto));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AbrirTurnoAsync(ctx);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteGasto = factory.CreateClient();
        var login = await clienteGasto.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaGasto = clienteGasto.PostAsJsonAsync("/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id));

        await transaccionIniciada.Task;

        // La anulación corre y commitea COMPLETA mientras el gasto sigue pausado justo después
        // de abrir su propia transacción — todavía no tomó ningún lock.
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        var cuerpoAnulacion = await anulacion.Content.ReadAsStringAsync();
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);
        var resultadoAnulacion = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpoAnulacion, OpcionesJson)!;
        Assert.Equal(0, resultadoAnulacion.GastosLigados);

        puedeContinuar.TrySetResult();

        var respuestaGasto = await tareaGasto;
        var cuerpoGasto = await respuestaGasto.Content.ReadAsStringAsync();
        Assert.True(respuestaGasto.StatusCode == HttpStatusCode.Conflict, cuerpoGasto);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoGasto, OpcionesJson);
        Assert.Equal("compra_anulada", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Gastos.CountAsync(g => g.IdComprobanteCompra == compra.Id));
    }

    /// <summary>design decisión 7, Backstop Map racy surface 5, la otra dirección: el gasto gana
    /// la carrera — su transacción entera (turno + <c>FOR SHARE</c> + INSERT + COMMIT) corre y
    /// commitea mientras la anulación sigue pausada justo después de abrir la suya, todavía sin
    /// tomar el lock exclusivo del header. La anulación retoma, procede igual (regla invertida,
    /// decisión 6 — nunca bloquea) y su conteo informativo ve el gasto recién comiteado, sin
    /// staleness.</summary>
    [Fact]
    public async Task ElGastoGanandoLaCarreraQuedaCorrectamenteContadoPorLaAnulacionQueLlegaDespues()
    {
        var ctx = await PrepararAsync(nameof(ElGastoGanandoLaCarreraQuedaCorrectamenteContadoPorLaAnulacionQueLlegaDespues));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AbrirTurnoAsync(ctx);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteAnular = factory.CreateClient();
        var login = await clienteAnular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaAnulacion = clienteAnular.PostAsync($"/api/compras/{compra.Id}/anular", null);

        await transaccionIniciada.Task;

        var respuestaGasto = await ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDeGastoLigado(ctx, compra.Id, importe: 750m));
        var cuerpoGasto = await respuestaGasto.Content.ReadAsStringAsync();
        Assert.True(respuestaGasto.StatusCode == HttpStatusCode.Created, cuerpoGasto);

        puedeContinuar.TrySetResult();

        var respuestaAnulacion = await tareaAnulacion;
        var cuerpoAnulacion = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);
        var resultadoAnulacion = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpoAnulacion, OpcionesJson)!;
        Assert.Equal(1, resultadoAnulacion.GastosLigados);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.Gastos.CountAsync(g => g.IdComprobanteCompra == compra.Id));
    }
}
