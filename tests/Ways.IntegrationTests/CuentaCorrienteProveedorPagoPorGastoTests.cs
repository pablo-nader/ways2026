using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 3: el movimiento `pago` dentro de
/// <c>ServicioDeGastos.InsertarGastoAsync</c> (task 3.1) — la predicado (tasks 3.3-3.5), el guard
/// de turno intacto (task 3.6), el no-regresión de arqueo (task 3.7), las dos carreras REALES
/// (tasks 3.8-3.9, decisión 7 de tasks.md: slice 2 solo pudo simular el pago llamando directo al
/// writer), el punto de falla (task 3.10) y el backstop de <c>fk_..._comprobante_compra</c> (task
/// 3.14). Mutation targets #21-#23 (tasks 3.11-3.13) — evidencia de mutación registrada en el PR
/// body, no en este archivo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteProveedorPagoPorGastoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdAlicuotaIva21,
        int IdTipoCFA, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "CC-proveedor-pago-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        // MedioPago es tenant-scoped — bajo TenantActualFijo.Plataforma el filtro de EF no acota
        // por tenant y .FirstAsync() puede devolver la fila de OTRO tenant creado en paralelo por
        // otra prueba de esta colección (mismo criterio que GastosLigadosACompraTests.PrepararAsync).
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
        db.Proveedores.Add(proveedor);
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
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21,
            idTipoCFA, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal);
    }

    private static SolicitudDeCompra SolicitudSimple(Contexto ctx, decimal costoUnitario = 100m, string? numeroExterno = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno ?? $"0001-{Guid.NewGuid():N}"[..14],
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 10m, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearYConfirmarCompraAsync(Contexto ctx, decimal costoUnitario = 100m, string? numeroExterno = null)
    {
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, costoUnitario, numeroExterno));
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

    private static SolicitudDeGasto SolicitudDePago(
        Contexto ctx, decimal importe, int? idComprobanteCompra = null, int? idProveedor = null,
        CategoriaGasto categoria = CategoriaGasto.Proveedor) =>
        new(
            ctx.IdPuntoVenta, categoria, idProveedor ?? (categoria == CategoriaGasto.Proveedor ? ctx.IdProveedor : null),
            null, "Pago a proveedor", null, ctx.IdMedioEfectivo, null, importe, idComprobanteCompra);

    // ---- task 3.3: gasto proveedor ligado escribe exactamente un pago imputado ---------------------

    [Fact]
    public async Task UnGastoDeCategoriaProveedorLigadoEscribeExactamenteUnPagoImputado()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeCategoriaProveedorLigadoEscribeExactamenteUnPagoImputado));
        var compra = await CrearYConfirmarCompraAsync(ctx, costoUnitario: 100m); // total 1000
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDePago(ctx, importe: 1000m, idComprobanteCompra: compra.Id));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pagos = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Pago && m.IdGasto == gasto.Id)
            .ToListAsync();

        // mutation target #22 (el write movido ANTES de SaveChangesAsync): si id_gasto viniera de
        // una fila todavía no flusheada, sería 0 — nunca el id real recién generado.
        var pago = Assert.Single(pagos);
        Assert.Equal(gasto.Id, pago.IdGasto);
        Assert.True(pago.IdGasto > 0);
        Assert.Equal(-1000m, pago.Importe);
        Assert.Equal(compra.Id, pago.IdComprobanteCompra);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        // compra (+1000) − pago (1000) = 0.
        Assert.Equal(0m, proveedor.Saldo);
        Assert.Equal(proveedor.Saldo, pago.SaldoResultante);
    }

    // ---- task 3.4: gasto sin comprobante reduce el saldo sin imputación ----------------------------

    [Fact]
    public async Task UnGastoDeCategoriaProveedorSinComprobanteEscribeUnPagoSinImputacion()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeCategoriaProveedorSinComprobanteEscribeUnPagoSinImputacion));
        // deuda previa REAL para discriminar el saldo_resultante del mutante de valor (mismo
        // criterio, hallazgo del juez B slice 2: ningún assert de saldo_resultante contra un
        // proveedor "fresco").
        await CrearYConfirmarCompraAsync(ctx, costoUnitario: 80m); // saldo previo = 800
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 300m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pago = await db.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdGasto == gasto.Id);

        Assert.Null(pago.IdComprobanteCompra);
        Assert.Equal(-300m, pago.Importe);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(500m, proveedor.Saldo); // 800 − 300
        Assert.Equal(proveedor.Saldo, pago.SaldoResultante);
    }

    // ---- task 3.5 / mutation target #21: los DOS conjuntos del predicado, cada uno por separado ----

    [Fact]
    public async Task UnGastoDeOtraCategoriaConIdProveedorNoEscribeMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeOtraCategoriaConIdProveedorNoEscribeMovimiento));
        await AbrirTurnoAsync(ctx);

        // categoria != proveedor, con id_proveedor de todos modos presente en la solicitud (el
        // servicio lo ignora: SolicitudDeGasto.IdProveedor solo se usa cuando la categoría ES
        // proveedor) — discrimina el conjunto "categoria = proveedor" del predicado.
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Servicios, ctx.IdProveedor, null, "Gasto de servicios", null,
                ctx.IdMedioEfectivo, null, 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdGasto == gasto.Id));
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);
    }

    [Fact]
    public async Task UnGastoDeCategoriaProveedorSinIdProveedorNoEscribeMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeCategoriaProveedorSinIdProveedorNoEscribeMovimiento));
        await AbrirTurnoAsync(ctx);

        // categoria = proveedor, id_proveedor NULL — discrimina el conjunto "id_proveedor no
        // nulo" del predicado (el mismo caso que la fórmula retirada de ServicioDeSaldoDeProveedor
        // ya excluía). Construido directo (no vía SolicitudDePago, que RELLENA id_proveedor con
        // ctx.IdProveedor por defecto cuando la categoría es proveedor).
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, null, null, "Gasto sin proveedor", null,
                ctx.IdMedioEfectivo, null, 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdGasto == gasto.Id));
    }

    // ---- task 3.6: el guard de turno sigue intacto — 409 no escribe movimiento ----------------------

    [Fact]
    public async Task UnGastoDeProveedorSinTurnoAbiertoEsRechazadoYNoEscribeMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeProveedorSinTurnoAbiertoEsRechazadoYNoEscribeMovimiento));

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdProveedor == ctx.IdProveedor));
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);
    }

    // ---- task 3.7: el pago sigue apareciendo en el arqueo del turno, sin término nuevo -------------

    [Fact]
    public async Task ElPagoAProveedorSigueApareciendoEnElEgresoPorCategoriaDelResumenSinTerminoNuevo()
    {
        var ctx = await PrepararAsync(nameof(ElPagoAProveedorSigueApareciendoEnElEgresoPorCategoriaDelResumenSinTerminoNuevo));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 120m));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        // El gasto pesa en Egresos.PorCategoria exactamente como antes de esta etapa (design "What
        // does NOT change": ningún término nuevo, ninguna derivación nueva) — el ledger de
        // proveedores no agrega ni resta nada acá.
        var proveedor = Assert.Single(resumen!.Egresos.PorCategoria, e => e.Categoria == CategoriaGasto.Proveedor);
        Assert.Equal(120m, proveedor.Total);
    }

    // ---- task 3.8: pago × pago sobre el mismo proveedor, sin lost update ---------------------------

    [Fact]
    public async Task DosPagosConcurrentesAlMismoProveedorSeSerializanSinPerderActualizaciones()
    {
        var ctx = await PrepararAsync(nameof(DosPagosConcurrentesAlMismoProveedorSeSerializanSinPerderActualizaciones));
        await CrearYConfirmarCompraAsync(ctx, costoUnitario: 150m); // saldo previo = 1500
        await AbrirTurnoAsync(ctx);

        var tareaUno = ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 300m));
        var tareaDos = ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 200m));

        await Task.WhenAll(tareaUno, tareaDos);

        var respuestaUno = await tareaUno;
        var respuestaDos = await tareaDos;
        Assert.True(respuestaUno.StatusCode == HttpStatusCode.Created, await respuestaUno.Content.ReadAsStringAsync());
        Assert.True(respuestaDos.StatusCode == HttpStatusCode.Created, await respuestaDos.Content.ReadAsStringAsync());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);

        // 1500 − 300 − 200 = 1000, sin lost update — serializados sobre la misma fila de
        // proveedores (design: Concurrency guarantees, "pago × pago... serializado, ambos aditivos").
        Assert.Equal(1000m, proveedor.Saldo);

        var suma = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdProveedor == ctx.IdProveedor)
            .SumAsync(m => m.Importe);
        Assert.Equal(proveedor.Saldo, suma);
    }

    // ---- task 3.9: anulación × pago sobre la misma compra, por el call site REAL de ambos lados ----

    /// <summary>Pausa la transacción manual (<c>ServicioDeCompras.EjecutarAnulacionAsync</c>) justo
    /// DESPUÉS de <c>BeginTransactionAsync</c> — mismo patrón que
    /// <c>GastosLigadosACompraTests.InterceptorDePausaTrasIniciarLaTransaccion</c> /
    /// <c>ComprasAnulacionYConcurrenciaTests</c>.</summary>
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

    /// <summary>design decisión 7 / Concurrency guarantees: el pago toma <c>FOR SHARE</c> sobre el
    /// header (vía <c>ExigirCompraLigableAsync</c>, reusado sin cambios) ANTES que la anulación
    /// tome su <c>FOR UPDATE</c> exclusivo — si el pago corre y comitea completo mientras la
    /// anulación sigue pausada sin ningún lock propio todavía, la anulación retoma viendo un ledger
    /// que YA contiene el pago: su reversa (que solo suma los movimientos `compra`, design decisión
    /// 6) queda intacta y el pago nunca se toca — "sin motor de reversión de gastos".</summary>
    [Fact]
    public async Task ElPagoQueGanaLaCarreraDejaSuMovimientoVisibleEnElLedgerQueLaAnulacionQueLlegaDespuesComputa()
    {
        var ctx = await PrepararAsync(nameof(ElPagoQueGanaLaCarreraDejaSuMovimientoVisibleEnElLedgerQueLaAnulacionQueLlegaDespuesComputa));
        var compra = await CrearYConfirmarCompraAsync(ctx, costoUnitario: 100m); // total 1000
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

        // El pago corre COMPLETO por el call site real mientras la anulación sigue pausada, todavía
        // sin ningún lock propio.
        var respuestaPago = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDePago(ctx, importe: 400m, idComprobanteCompra: compra.Id));
        var cuerpoPago = await respuestaPago.Content.ReadAsStringAsync();
        Assert.True(respuestaPago.StatusCode == HttpStatusCode.Created, cuerpoPago);

        puedeContinuar.TrySetResult();

        var respuestaAnulacion = await tareaAnulacion;
        var cuerpoAnulacion = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);
        var resultadoAnulacion = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpoAnulacion, OpcionesJson)!;
        Assert.Equal(1, resultadoAnulacion.GastosLigados);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == compra.Id && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
        Assert.Equal(-1000m, ajuste.Importe); // reversa SOLO el `compra` original — nunca el pago.

        var pago = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == compra.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(-400m, pago.Importe);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        // 1000 (compra) − 400 (pago) − 1000 (reversa) = −400, sin lost update.
        Assert.Equal(-400m, proveedor.Saldo);
        Assert.Equal(proveedor.Saldo, ajuste.SaldoResultante);
    }

    /// <summary>task 3.14 (`db-error-backstops`, `fk_..._comprobante_compra` race): la anulación
    /// gana la carrera — commitea completa mientras el pago sigue pausado sin haber tomado su
    /// propio <c>FOR SHARE</c> todavía. El pago retoma, <c>ExigirCompraLigableAsync</c> ve
    /// <c>anulada</c> ya comiteada y rechaza con <c>409 compra_anulada</c> — ningún movimiento de
    /// ledger, ninguna fila de <c>gastos</c>, ningún riesgo de violar
    /// <c>fk_..._comprobante_compra</c> con una imputación a una compra que ya no admite pagos.
    /// Mismo patrón que <c>GastosLigadosACompraTests.LaAnulacionGanandoLaCarreraDejaAlGastoRechazadoSinVinculoCorrupto</c>,
    /// con el assert propio de esta etapa sobre el ledger.</summary>
    [Fact]
    public async Task UnPagoQueIntentaImputarseAUnaCompraSiendoAnuladaConcurrentementeEsRechazadoSinEscribirMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnPagoQueIntentaImputarseAUnaCompraSiendoAnuladaConcurrentementeEsRechazadoSinEscribirMovimiento));
        var compra = await CrearYConfirmarCompraAsync(ctx, costoUnitario: 100m);
        await AbrirTurnoAsync(ctx);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clientePago = factory.CreateClient();
        var login = await clientePago.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaPago = clientePago.PostAsJsonAsync(
            "/api/gastos", SolicitudDePago(ctx, importe: 1000m, idComprobanteCompra: compra.Id));

        await transaccionIniciada.Task;

        // La anulación corre y commitea COMPLETA mientras el pago sigue pausado sin lock propio.
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        var cuerpoAnulacion = await anulacion.Content.ReadAsStringAsync();
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);

        puedeContinuar.TrySetResult();

        var respuestaPago = await tareaPago;
        var cuerpoPago = await respuestaPago.Content.ReadAsStringAsync();
        Assert.True(respuestaPago.StatusCode == HttpStatusCode.Conflict, cuerpoPago);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoPago, OpcionesJson);
        Assert.Equal("compra_anulada", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Gastos.CountAsync(g => g.IdComprobanteCompra == compra.Id));
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.Tipo == TipoMovimientoCcProveedor.Pago));

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        // −1000 (reversa de la compra) — el pago rechazado no aportó nada.
        Assert.Equal(-1000m, proveedor.Saldo);
    }

    // ---- task 3.10: falla en el punto del ledger deja saldo/ledger/el gasto sin cambios ------------

    private async Task RevocarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"REVOKE {privilegios} ON {tabla} FROM {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task RestaurarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"GRANT {privilegios} ON {tabla} TO {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task UnaFallaAlEscribirElLedgerDeProveedorDejaSaldoLedgerYElGastoSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirElLedgerDeProveedorDejaSaldoLedgerYElGastoSinCambios));
        await AbrirTurnoAsync(ctx);

        await RevocarAsync("movimientos_cuenta_corriente_proveedor", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 200m));
        }
        finally
        {
            await RestaurarAsync("movimientos_cuenta_corriente_proveedor", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        // La transacción entera (INSERT gastos + UPDATE saldo + INSERT ledger) revierte junta — el
        // gasto NUNCA queda huérfano de su movimiento.
        Assert.Equal(0, await db.Gastos.CountAsync(g => g.IdProveedor == ctx.IdProveedor));
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdProveedor == ctx.IdProveedor));

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);
    }

    // ---- mutation target #23: importe = −gasto.Importe — el invariante saldo == Σ importe ----------

    [Fact]
    public async Task ElSaldoDelProveedorIgualaLaSumaDeMovimientosTrasApertreCompraYPago()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoDelProveedorIgualaLaSumaDeMovimientosTrasApertreCompraYPago));
        var compra = await CrearYConfirmarCompraAsync(ctx, costoUnitario: 100m); // +1000
        await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos", SolicitudDePago(ctx, importe: 600m, idComprobanteCompra: compra.Id));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pago = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == compra.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);

        // Si la negación se perdiera (`importe = gasto.Importe`), el pago SUMARÍA en vez de restar
        // y el invariante saldo == Σ importe seguiría cumpliéndose trivialmente por construcción —
        // por eso se compara además contra el valor NEGATIVO explícito, no solo la suma.
        Assert.Equal(-600m, pago.Importe);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        var suma = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdProveedor == ctx.IdProveedor)
            .SumAsync(m => m.Importe);
        Assert.Equal(400m, proveedor.Saldo); // 1000 − 600
        Assert.Equal(proveedor.Saldo, suma);
    }

    // ---- decisión 13 de tasks.md: el movimiento `pago` bajo un reloj con offset real -03:00 --------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    [Fact]
    public async Task ElMovimientoDePagoUsaLaFechaExactaBajoUnRelojConOffsetRealMenosTres()
    {
        var ctx = await PrepararAsync(nameof(ElMovimientoDePagoUsaLaFechaExactaBajoUnRelojConOffsetRealMenosTres));
        await AbrirTurnoAsync(ctx);

        // mutation-proof-tests rule 10: offset real -03:00, nunca Z (mediodía UTC — task de la
        // etapa: RelojFijo(2026-08-17T12:00:00Z)).
        var instanteFijo = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero), instanteFijo);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await cliente.PostAsJsonAsync("/api/gastos", SolicitudDePago(ctx, importe: 150m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pago = await db.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdGasto == gasto.Id);

        Assert.Equal(instanteFijo.UtcDateTime, pago.Fecha.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, pago.Fecha.Offset);
    }
}
