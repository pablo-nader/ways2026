using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 2: anulación (tasks 2.12), atomicidad
/// forzada por punto de falla (task 2.10), las dos superficies racy de esta slice (task 2.13) y
/// el presupuesto de comandos (task 2.15).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasAnulacionYConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA,
        string MailAdmin, string PasswordAdmin);

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
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21, idTipoCFA,
            mailAdmin, resultado.PasswordTemporal);
    }

    private static SolicitudDeCompra SolicitudSimple(
        Contexto ctx, decimal unidades = 10m, decimal costoUnitario = 100m, string? numeroExterno = "0001-00000001") =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra? solicitud = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud ?? SolicitudSimple(ctx));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Simula una venta directa del ledger (sin pasar por el checkout completo, que
    /// necesita turno/cliente/medios de pago) — a la refusal de anulación (design decisión 5) le
    /// alcanza con que <c>stock.cantidad</c> haya bajado, no le importa cómo.</summary>
    private async Task ReducirStockComoVentaAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = -cantidad,
            Motivo = MotivoStock.Venta, IdEmpleado = idEmpleado, CreadoEl = ahora
        });
        await db.SaveChangesAsync();

        var stock = await db.Stock.FirstAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        stock.Cantidad -= cantidad;
        await db.SaveChangesAsync();
    }

    private async Task SembrarGastoLigadoAsync(Contexto ctx, int idComprobanteCompra)
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

        db.Gastos.Add(new Gasto
        {
            IdTenant = ctx.IdTenant, Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdTurnoCaja = turno.Id,
            IdEmpleado = idEmpleado, Categoria = CategoriaGasto.Proveedor, IdProveedor = ctx.IdProveedor,
            Concepto = "Pago de prueba", IdMedioPago = idMedioPago, Importe = 100m,
            IdComprobanteCompra = idComprobanteCompra, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- task 2.12: anulación reversa por contramovimientos --------------------------------------

    [Fact]
    public async Task AnulacionReviertaElStockYRestauraElCache()
    {
        var ctx = await PrepararAsync(nameof(AnulacionReviertaElStockYRestauraElCache));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 50m));
        await ConfirmarAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var resultado = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpo, OpcionesJson)!;

        Assert.Equal(EstadoCompra.Anulada, resultado.Compra.Estado);
        Assert.Equal(0, resultado.GastosLigados);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(0m, cantidad);

        Assert.Equal(
            1, await db.MovimientosStock.CountAsync(
                m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion && m.Cantidad == -50m));
    }

    [Fact]
    public async Task AnulacionEsRechazadaCuandoLaMercaderiaYaFueVendida()
    {
        var ctx = await PrepararAsync(nameof(AnulacionEsRechazadaCuandoLaMercaderiaYaFueVendida));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 50m));
        await ConfirmarAsync(ctx, creada.Id);
        await ReducirStockComoVentaAsync(ctx, ctx.IdArticulo, 40m);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_anulacion_stock_negativo", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Confirmada, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(10m, cantidad);
    }

    [Fact]
    public async Task CostoNominalNoSeRevierteAlAnular()
    {
        var ctx = await PrepararAsync(nameof(CostoNominalNoSeRevierteAlAnular));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, costoUnitario: 100m));
        await ConfirmarAsync(ctx, creada.Id);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
            Assert.Equal(121.00m, articulo.CostoNominal);
        }

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
            Assert.Equal(121.00m, articulo.CostoNominal);
        }
    }

    [Fact]
    public async Task AnulandoUnBorradorEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(AnulandoUnBorradorEsRechazado));
        var creada = await CrearBorradorAsync(ctx);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_procesada", problema.GetProperty("codigo").GetString());
    }

    /// <summary>La regla invertida (design decisión 6): la anulación NUNCA bloquea por un gasto
    /// ligado — procede y solo REPORTA cuántos pagos quedaron colgados.</summary>
    [Fact]
    public async Task AnulacionProcedeConGastosLigadosYLosReportaEnLaRespuesta()
    {
        var ctx = await PrepararAsync(nameof(AnulacionProcedeConGastosLigadosYLosReportaEnLaRespuesta));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);
        await SembrarGastoLigadoAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resultado = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoCompra.Anulada, resultado.Compra.Estado);
        Assert.Equal(1, resultado.GastosLigados);
    }

    // ---- task 2.10: atomicidad, un punto de falla por prueba (confirmar) -------------------------

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

    private async Task<HttpResponseMessage> ConfirmarConPrivilegioRevocadoAsync(Contexto ctx, int id, string tabla, string privilegios)
    {
        await RevocarAsync(tabla, privilegios);
        try
        {
            return await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        }
        finally
        {
            await RestaurarAsync(tabla, privilegios);
        }
    }

    [Fact]
    public async Task UnaFallaAlEscribirElMovimientoDeStockDejaLaCompraEnBorrador()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirElMovimientoDeStockDejaLaCompraEnBorrador));
        var creada = await CrearBorradorAsync(ctx);

        var respuesta = await ConfirmarConPrivilegioRevocadoAsync(ctx, creada.Id, "movimientos_stock", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
        Assert.Equal(0, await db.Stock.CountAsync(s => s.IdArticulo == ctx.IdArticulo));
    }

    [Fact]
    public async Task UnaFallaAlActualizarElCostoNominalDejaTodoSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlActualizarElCostoNominalDejaTodoSinCambios));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, costoUnitario: 100m));

        var respuesta = await ConfirmarConPrivilegioRevocadoAsync(ctx, creada.Id, "articulos", "UPDATE");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
        Assert.Null((await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
    }

    // ---- task 2.10 (anulación): un punto de falla en la reversa -----------------------------------

    [Fact]
    public async Task UnaFallaAlRevertirElStockDejaLaCompraConfirmadaYSinReversa()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlRevertirElStockDejaLaCompraConfirmadaYSinReversa));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 20m));
        await ConfirmarAsync(ctx, creada.Id);

        await RevocarAsync("stock", "UPDATE");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        }
        finally
        {
            await RestaurarAsync("stock", "UPDATE");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Confirmada, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(20m, cantidad);
    }

    // ---- task 2.13: superficies racy, forced rendezvous (row lock natural) ------------------------

    /// <summary>Design: Backstop Map — "genuinely racy surfaces... (1) double confirm of the same
    /// borrador". <c>ConfirmarAsync</c> tiene DOS capas (a diferencia de
    /// <c>ServicioDeVentas.AnularAsync</c>, que no tiene pre-chequeo secuencial): una lectura
    /// previa (fuera de la transacción) que da el código canónico del spec
    /// (<c>compra_ya_procesada</c>) cuando la compra YA está visiblemente procesada, y el
    /// <c>UPDATE ... RETURNING</c> atómico como backstop race-safe (<c>compra_no_es_borrador</c>).
    /// Sin forzar el rendezvous de las dos lecturas previas, el timing real del pool casi siempre
    /// deja que una gane del todo antes de que la otra arranque, y el "perdedor" ve la capa 1 en
    /// vez de la capa 2 — <c>InterceptorDeRendezVousConfirmar</c> retiene las dos lecturas hasta
    /// que ambas llegaron, así las dos entran a la transacción viendo <c>borrador</c> y la carrera
    /// real ocurre en el <c>UPDATE</c>.</summary>
    [Fact]
    public async Task DobleConfirmacionConcurrenteDaExactamenteUnGanador()
    {
        var ctx = await PrepararAsync(nameof(DobleConfirmacionConcurrenteDaExactamenteUnGanador));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: "race-confirmar"));

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousConfirmar(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = cliente.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var tareaB = cliente.PostAsync($"/api/compras/{creada.Id}/confirmar", null);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).OrderBy(s => s).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], estados);

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedora.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_es_borrador", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    /// <summary>Retiene las dos primeras consultas que leen <c>comprobantes_compra</c> — la
    /// pre-lectura de <c>ServicioDeCompras.ConfirmarAsync</c> de cada request — hasta que ambas
    /// llegaron. Mismo patrón que <c>ParametrosTests.InterceptorDeRendezVous</c>. No matchea
    /// <c>items_comprobante_compra</c> (nombre de tabla distinto, sin "comprobantes_compra" como
    /// substring).</summary>
    private sealed class InterceptorDeRendezVousConfirmar(CountdownEvent gate) : DbCommandInterceptor
    {
        private int _participantes;

        public int Participantes => _participantes;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            EsperarSiCorresponde(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EsperarSiCorresponde(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void EsperarSiCorresponde(DbCommand command)
        {
            if (!command.CommandText.Contains("comprobantes_compra", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _participantes) > 2)
            {
                return;
            }

            gate.Signal();

            var senializo = gate.Wait(TimeSpan.FromSeconds(10));
            Assert.True(senializo, "El rendezvous de InterceptorDeRendezVousConfirmar no llegó a los 2 participantes a tiempo.");
        }
    }

    /// <summary>Design: Backstop Map — superficie racy 2, "confirm × concurrent borrador edit".
    /// Ambos toman el MISMO lock de fila como primer statement (design decisión 1) — cualquiera
    /// de los dos resultados es representable: si el PUT gana primero, confirmar ve el estado ya
    /// editado y confirma sobre eso; si confirmar gana primero, el PUT ve <c>estado != borrador</c>
    /// al retomar el lock y se rechaza con <c>409</c>. Nunca un 500, nunca una mezcla corrupta.</summary>
    [Fact]
    public async Task ConfirmarYEditarElMismoBorradorEnParaleloNuncaCorrompenElEstado()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarYEditarElMismoBorradorEnParaleloNuncaCorrompenElEstado));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 10m));

        var tareaConfirmar = ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var tareaEditar = ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", SolicitudSimple(ctx, unidades: 30m));

        var confirmarResp = await tareaConfirmar;
        var editarResp = await tareaEditar;

        Assert.True(
            confirmarResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"confirmar: {confirmarResp.StatusCode}");
        Assert.True(
            editarResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"editar: {editarResp.StatusCode}");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // Cualquiera sea el resultado, el invariante de stock sigue en pie: la cantidad en caché
        // coincide con la suma de sus movimientos (nunca una escritura a medias).
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(cantidad, sumaDeMovimientos);

        // Un único movimiento de compra como máximo — nunca dos confirmaciones sobrevivieron.
        Assert.True(await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id) <= 1);
    }

    // ---- task 2.15: presupuesto de comandos --------------------------------------------------------

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

    [Fact]
    public async Task ConfirmarEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeItems()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeItems));

        var consultasConPocosItems = await MedirConsultasDeConfirmarAsync(ctx, cantidadDeItems: 2);
        var consultasConMuchosItems = await MedirConsultasDeConfirmarAsync(ctx, cantidadDeItems: 20);
        var consultasConMuchisimosItems = await MedirConsultasDeConfirmarAsync(ctx, cantidadDeItems: 100);

        Assert.Equal(consultasConPocosItems, consultasConMuchosItems);
        Assert.Equal(consultasConPocosItems, consultasConMuchisimosItems);
    }

    [Fact]
    public async Task AnularEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeItems()
    {
        var ctx = await PrepararAsync(nameof(AnularEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeItems));

        var consultasConPocosItems = await MedirConsultasDeAnularAsync(ctx, cantidadDeItems: 2);
        var consultasConMuchosItems = await MedirConsultasDeAnularAsync(ctx, cantidadDeItems: 20);
        var consultasConMuchisimosItems = await MedirConsultasDeAnularAsync(ctx, cantidadDeItems: 100);

        Assert.Equal(consultasConPocosItems, consultasConMuchosItems);
        Assert.Equal(consultasConPocosItems, consultasConMuchisimosItems);
    }

    private async Task<CompraDetalle> CrearBorradorDeNItemsAsync(Contexto ctx, int cantidadDeItems)
    {
        var lineas = new List<LineaDeCompraSolicitada>();
        for (var i = 0; i < cantidadDeItems; i++)
        {
            int idArticulo;
            await using (var dbSeed = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
            {
                var ahora = DateTimeOffset.UtcNow;
                var articulo = new Articulo
                {
                    IdTenant = ctx.IdTenant, CodigoInterno = $"presupuesto-{Guid.NewGuid():N}", Nombre = "Presupuesto",
                    IdArea = await dbSeed.Areas.Select(a => a.Id).FirstAsync(), IdAlicuotaIva = ctx.IdAlicuotaIva21,
                    UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
                };
                dbSeed.Articulos.Add(articulo);
                await dbSeed.SaveChangesAsync();
                idArticulo = articulo.Id;
            }

            lineas.Add(new LineaDeCompraSolicitada(idArticulo, $"Item {i}", 1m, null, null, 10m, 0m, ctx.IdAlicuotaIva21, true));
        }

        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, $"presupuesto-{Guid.NewGuid():N}",
            DateOnly.FromDateTime(DateTime.UtcNow), null, lineas);

        return await CrearBorradorAsync(ctx, solicitud);
    }

    private WaysDbContext CrearContextoConContador(int idTenant, ContadorDeComandos contador)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, idTenant);

        var opciones = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<Ways.Domain.Usuarios.EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<Ways.Domain.Organizacion.EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<Ways.Domain.Clientes.TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<Ways.Domain.Ventas.EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<Ways.Domain.Caja.TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<Ways.Domain.Caja.TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
            })
            .AddInterceptors(new Ways.Infrastructure.Multitenancy.InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    private static ServicioDeCompras CrearServicio(WaysDbContext db, int idTenant) => new(
        db, new RelojFijo(DateTimeOffset.UtcNow), new ContextoFijo(idTenant, usuarioId: 1),
        new Ways.Application.Precios.ServicioDePrecios(db, new RelojFijo(DateTimeOffset.UtcNow), new ContextoFijo(idTenant, 1)));

    private async Task<int> MedirConsultasDeConfirmarAsync(Contexto ctx, int cantidadDeItems)
    {
        var creada = await CrearBorradorDeNItemsAsync(ctx, cantidadDeItems);

        var contador = new ContadorDeComandos();
        await using var db = CrearContextoConContador(ctx.IdTenant, contador);
        var servicio = CrearServicio(db, ctx.IdTenant);

        await servicio.ConfirmarAsync(creada.Id);

        return contador.Consultas;
    }

    private async Task<int> MedirConsultasDeAnularAsync(Contexto ctx, int cantidadDeItems)
    {
        var creada = await CrearBorradorDeNItemsAsync(ctx, cantidadDeItems);
        await ConfirmarAsync(ctx, creada.Id);

        var contador = new ContadorDeComandos();
        await using var db = CrearContextoConContador(ctx.IdTenant, contador);
        var servicio = CrearServicio(db, ctx.IdTenant);

        await servicio.AnularAsync(creada.Id);

        return contador.Consultas;
    }
}
