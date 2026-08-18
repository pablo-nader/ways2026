using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
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
/// stage-15-cc-proveedores-ledger, Slice 2: el movimiento `compra` en confirmar (tasks 2.11,
/// 2.15, 2.16), el contramovimiento `ajuste` en anular — impago, pagado y pre-cutover (tasks
/// 2.12-2.14) —, el orden de locks pineado (task 2.9) y la carrera confirm × pago (task 2.17).
/// Mutation targets #14-#20 (tasks 2.20-2.26) — evidencia de mutación registrada en el PR body,
/// no en este archivo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteProveedorEscriturasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTipoCFB, string MailAdmin, string PasswordAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "CC-proveedor-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

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

        // C-FB no discrimina IVA (a diferencia de C-FA) — total == unidades * costoUnitario
        // exactamente, sin sumar el 21%, así los importes esperados de este archivo son directos.
        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21,
            idTipoCFB, mailAdmin, resultado.PasswordTemporal);
    }

    private static SolicitudDeCompra SolicitudSimple(
        Contexto ctx, decimal unidades = 10m, decimal costoUnitario = 100m, string? numeroExterno = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFB, ctx.IdPuntoVenta, numeroExterno ?? $"0001-{Guid.NewGuid():N}"[..14],
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra? solicitud = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud ?? SolicitudSimple(ctx));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarViaApiAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    // ---- task 2.11: confirmar escribe exactamente un movimiento compra ----------------------------

    [Fact]
    public async Task ConfirmarUnaCompraEscribeExactamenteUnMovimientoCompraYSubeElSaldo()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarUnaCompraEscribeExactamenteUnMovimientoCompraYSubeElSaldo));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 500m));

        await ConfirmarViaApiAsync(ctx, creada.Id);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdComprobanteCompra == creada.Id)
            .ToListAsync();

        var movimiento = Assert.Single(movimientos);
        Assert.Equal(TipoMovimientoCcProveedor.Compra, movimiento.Tipo);
        Assert.Equal(5000m, movimiento.Importe);
        Assert.Null(movimiento.IdGasto);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(5000m, proveedor.Saldo);
        Assert.Equal(proveedor.Saldo, movimiento.SaldoResultante);
    }

    // ---- mutation target #15: id_proveedor/total salen del RETURNING del lock, no de preLectura ---

    /// <summary>Pausa <c>EjecutarConfirmarAsync</c> justo DESPUÉS de <c>BeginTransactionAsync</c> —
    /// antes de que el <c>UPDATE ... RETURNING</c> del header corra — mismo patrón que
    /// <c>ComprasAnulacionYConcurrenciaTests.InterceptorDePausaTrasIniciarLaTransaccion</c> (cada
    /// archivo de este repo mantiene su propia copia, no comparte una base).</summary>
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

    /// <summary>Mutation target #15 (task 2.21): si <c>encabezado.Total</c> viniera de
    /// <c>preLectura</c> (leída ANTES de la transacción) en vez del <c>RETURNING</c> del lock, el
    /// movimiento `compra` cargaría el total VIEJO — un PUT concurrente que sube el costoUnitario
    /// (y por lo tanto el total) DESPUÉS de <c>preLectura</c> pero ANTES del lock debe reflejarse
    /// en el importe del movimiento.</summary>
    [Fact]
    public async Task ConfirmarConCambioDeTotalConcurrentePorUnPutUsaElTotalQueElLockRealmenteVio()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarConCambioDeTotalConcurrentePorUnPutUsaElTotalQueElLockRealmenteVio));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 100m, numeroExterno: "total-race"));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteConfirmar = factory.CreateClient();
        var login = await clienteConfirmar.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaConfirmar = clienteConfirmar.PostAsync($"/api/compras/{creada.Id}/confirmar", null);

        await transaccionIniciada.Task;

        // El PUT gana la carrera: sube costoUnitario (100 → 250, total 1000 → 2500) y COMMITEA
        // antes de que el lock del header de confirmar corra.
        var respuestaPut = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", SolicitudSimple(ctx, unidades: 10m, costoUnitario: 250m, numeroExterno: "total-race"));
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);

        puedeContinuar.TrySetResult();

        var respuestaConfirmar = await tareaConfirmar;
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdComprobanteCompra == creada.Id);

        // El total que el LOCK vio (2500, post-PUT) — nunca el 1000 que preLectura capturó antes
        // de que el confirmar entrara a la transacción.
        Assert.Equal(2500m, movimiento.Importe);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(2500m, proveedor.Saldo);
    }

    // ---- task 2.12: anulando una compra impaga reversa solo la deuda ------------------------------

    [Fact]
    public async Task AnulandoUnaCompraImpagaReviertaSoloLaDeuda()
    {
        var ctx = await PrepararAsync(nameof(AnulandoUnaCompraImpagaReviertaSoloLaDeuda));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 100m));
        await ConfirmarViaApiAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ajustes = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdComprobanteCompra == creada.Id && m.Tipo == TipoMovimientoCcProveedor.Ajuste)
            .ToListAsync();

        var ajuste = Assert.Single(ajustes);
        Assert.Equal(-1000m, ajuste.Importe);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);
    }

    // ---- task 2.13: anulando una compra totalmente pagada deja saldo a favor ----------------------

    /// <summary>Simula el pago llamando DIRECTO a la clase escritora del slice 2 (decisión 7 de
    /// tasks.md: el call site real de <c>ServicioDeGastos</c> recién existe en slice 3) — necesita
    /// un <c>gastos</c> row solo para satisfacer la FK de <c>id_gasto</c>, sin pasar por
    /// <c>InsertarGastoAsync</c>.</summary>
    private async Task<int> SembrarGastoParaFkAsync(Contexto ctx, int idComprobanteCompra, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var idMedioPago = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleadoApertura = idEmpleado,
            FechaApertura = ahora, FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        var gasto = new Gasto
        {
            IdTenant = ctx.IdTenant, Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdTurnoCaja = turno.Id,
            IdEmpleado = idEmpleado, Categoria = CategoriaGasto.Proveedor, IdProveedor = ctx.IdProveedor,
            Concepto = "Pago simulado (slice 2)", IdMedioPago = idMedioPago, Importe = importe,
            IdComprobanteCompra = idComprobanteCompra, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Gastos.Add(gasto);
        await db.SaveChangesAsync();

        return gasto.Id;
    }

    private async Task<int> SimularPagoDirectoAsync(Contexto ctx, int idComprobanteCompra, int idGasto, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var conexion = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, null, ctx.IdTenant, ctx.IdProveedor, -importe, CancellationToken.None);

        await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, null, ctx.IdTenant, ctx.IdProveedor, DateTimeOffset.UtcNow, ctx.IdPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Pago, idComprobanteCompra, idGasto, -importe, nuevoSaldo, detalle: null,
            CancellationToken.None);

        return idGasto;
    }

    [Fact]
    public async Task AnulandoUnaCompraTotalmentePagadaDejaSaldoAFavor()
    {
        var ctx = await PrepararAsync(nameof(AnulandoUnaCompraTotalmentePagadaDejaSaldoAFavor));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 100m));
        await ConfirmarViaApiAsync(ctx, creada.Id);

        var idGasto = await SembrarGastoParaFkAsync(ctx, creada.Id, 1000m);
        await SimularPagoDirectoAsync(ctx, creada.Id, idGasto, 1000m);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
        Assert.Equal(-1000m, ajuste.Importe);

        // El pago y su gasto ligado NUNCA se tocan — "sin motor de reversión de gastos" (design
        // decisión 5).
        var pago = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(-1000m, pago.Importe);
        Assert.True(await db.Gastos.AnyAsync(g => g.Id == idGasto));

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(-1000m, proveedor.Saldo);
    }

    // ---- task 2.14 / mutation target #20: anulación pre-cutover usa el fallback -total ------------

    /// <summary>Simula una compra "pre-cutover": confirmada (con su movimiento `compra` normal),
    /// luego se borra ESE movimiento a mano — la forma exacta de "su deuda vive solo en el
    /// `apertura` del backfill, sin movimiento `compra` propio" que decisión 1 del proposal
    /// describe, sin tener que correr una migración completa dentro de un test de slice 2.</summary>
    private async Task BorrarMovimientoCompraAsync(Contexto ctx, int idComprobanteCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == idComprobanteCompra && m.Tipo == TipoMovimientoCcProveedor.Compra);
        db.MovimientosCuentaCorrienteProveedor.Remove(movimiento);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnulandoUnaCompraPreCutoverEscribeElAjusteConElFallback()
    {
        var ctx = await PrepararAsync(nameof(AnulandoUnaCompraPreCutoverEscribeElAjusteConElFallback));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 200m));
        await ConfirmarViaApiAsync(ctx, creada.Id);
        await BorrarMovimientoCompraAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id && m.Tipo == TipoMovimientoCcProveedor.Ajuste);

        Assert.Equal(-2000m, ajuste.Importe);
        Assert.NotNull(ajuste.Detalle);
        Assert.Contains("compra confirmada antes del ledger", ajuste.Detalle);

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);
    }

    // ---- task 2.15 / mutation target #14: fecha bajo offset real -03:00 ---------------------------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    [Fact]
    public async Task ElMovimientoDeCompraUsaLaFechaExactaBajoUnRelojConOffsetRealMenosTres()
    {
        var ctx = await PrepararAsync(nameof(ElMovimientoDeCompraUsaLaFechaExactaBajoUnRelojConOffsetRealMenosTres));
        var creada = await CrearBorradorAsync(ctx);

        // mutation-proof-tests rule 10: offset real -03:00, nunca Z — es la única forma de ver una
        // regresión de normalización UTC cruda (mutation target #14: ParametrosDeComando.Agregar
        // sin ToUniversalTime habría hecho que Npgsql rechace este parámetro contra timestamptz).
        var instanteFijo = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero), instanteFijo);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await cliente.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdComprobanteCompra == creada.Id);

        Assert.Equal(instanteFijo.UtcDateTime, movimiento.Fecha.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, movimiento.Fecha.Offset);
    }

    // ---- task 2.16: falla en el punto del ledger deja saldo/ledger/estado sin cambios --------------

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
    public async Task UnaFallaAlEscribirElLedgerDeProveedorDejaSaldoLedgerYEstadoSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirElLedgerDeProveedorDejaSaldoLedgerYEstadoSinCambios));
        var creada = await CrearBorradorAsync(ctx);

        await RevocarAsync("movimientos_cuenta_corriente_proveedor", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        }
        finally
        {
            await RestaurarAsync("movimientos_cuenta_corriente_proveedor", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdComprobanteCompra == creada.Id));

        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);
        Assert.Equal(0m, proveedor.Saldo);

        // El stock SÍ se escribió antes de llegar al paso del ledger (paso 5/6, el ÚLTIMO) — el
        // rollback de la transacción entera lo revierte igual, mismo criterio que
        // ComprasAnulacionYConcurrenciaTests.UnaFallaAlEscribirElMovimientoDeStockDejaLaCompraEnBorrador.
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    // ---- task 2.17: confirm × pago (directo) rendezvous, sin deadlock -----------------------------

    [Fact]
    public async Task ConfirmarYUnPagoDirectoAlMismoProveedorSeSerializanSinDeadlock()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarYUnPagoDirectoAlMismoProveedorSeSerializanSinDeadlock));
        var otraCompra = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 5m, costoUnitario: 300m));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m, costoUnitario: 100m));
        await ConfirmarViaApiAsync(ctx, otraCompra.Id);
        var idGasto = await SembrarGastoParaFkAsync(ctx, otraCompra.Id, 400m);

        var tareaConfirmar = ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var tareaPago = SimularPagoDirectoAsync(ctx, otraCompra.Id, idGasto, 400m);

        await Task.WhenAll(tareaConfirmar, tareaPago);

        var respuestaConfirmar = await tareaConfirmar;
        Assert.Equal(HttpStatusCode.OK, respuestaConfirmar.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == ctx.IdProveedor);

        // Compra "otraCompra" (1500) + esta compra (1000) − el pago directo (400) = 2100, sin
        // lost update — serializados sobre la misma fila de proveedores (design: Concurrency
        // guarantees).
        Assert.Equal(2100m, proveedor.Saldo);

        var suma = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdProveedor == ctx.IdProveedor)
            .SumAsync(m => m.Importe);
        Assert.Equal(proveedor.Saldo, suma);
    }

    // ---- task 2.9 / mutation target #19: proveedores es el ÚLTIMO lock, el ledger INSERT sigue ----
    //
    // Intentado primero como prueba de comportamiento (un DbCommandInterceptor de EF Core
    // capturando el orden real de statements) y descartado empíricamente: los statements crudos de
    // EjecutarConfirmarAsync/EjecutarAnulacionAsync se crean vía `conexion.CreateCommand()` sobre
    // `db.Database.GetDbConnection()` directamente — nunca pasan por el pipeline de comandos de EF
    // Core, así que NINGÚN DbCommandInterceptor (ReaderExecuting/ScalarExecuting/NonQueryExecuting)
    // los ve; `interceptor.Orden` quedó vacío en la corrida real (mutation-proof-tests rule 2: "no
    // lo razones, corré la prueba"). Resuelto con una aserción de texto fuente —mismo criterio que
    // los targets #4/#11 de slice 1— en
    // Ways.Application.Tests/Compras/ServicioDeComprasLockOrderTests.cs, que lee
    // ServicioDeCompras.cs desde disco y verifica el orden de aparición de las llamadas dentro de
    // EjecutarConfirmarAsync/EjecutarAnulacionAsync.

    // ---- mutation target #18: AND id_tenant = $3, probado por debajo de RLS -----------------------

    [Fact]
    public async Task LaActualizacionCrudaDeSaldoExigeElTenantAunConUnaConexionQueBypaseaRls()
    {
        var ctxA = await PrepararAsync(nameof(LaActualizacionCrudaDeSaldoExigeElTenantAunConUnaConexionQueBypaseaRls) + "A");
        var ctxB = await PrepararAsync(nameof(LaActualizacionCrudaDeSaldoExigeElTenantAunConUnaConexionQueBypaseaRls) + "B");

        // ways_owner bypasea RLS por completo (rol superuser del contenedor) — a propósito
        // (mutation-proof-tests rule 3: "route the test BELOW the confound"), para que la ÚNICA
        // capa que pueda discriminar acá sea el WHERE del propio statement, nunca la política RLS.
        await using var dbOwner = fixture.CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, ctxB.IdTenant));
        var conexion = dbOwner.Database.GetDbConnection();
        await dbOwner.Database.OpenConnectionAsync();

        // id_proveedor de tenant A, id_tenant de tenant B — combinación que no matchea ninguna
        // fila. Con "AND id_tenant = $3" intacto, 0 filas ⇒ InvalidOperationException.
        var excepcion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
                conexion, null, ctxB.IdTenant, ctxA.IdProveedor, 100m, CancellationToken.None));

        Assert.Contains(ctxA.IdProveedor.ToString(), excepcion.Message);

        await using var dbVerificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctxA.IdTenant));
        var proveedorA = await dbVerificacion.Proveedores.FirstAsync(p => p.Id == ctxA.IdProveedor);
        Assert.Equal(0m, proveedorA.Saldo);
    }

    // ---- task 2.27: non-regression — el diff de esta slice no toca Ventas -------------------------
    // Verificado con `git diff --stat` en el reporte de evidencia de mutación (mutation-proof-tests
    // + design.md Binding verify criteria #2), no como assertion de C# — VentasCheckoutTests.cs
    // queda intocado por construcción (ningún archivo de esta slice está bajo
    // src/Ways.Application/Ventas/).
}
