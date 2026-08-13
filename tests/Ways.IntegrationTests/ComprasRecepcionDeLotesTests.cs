using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 5 (tasks 5.5-5.12): recepción de lotes vía
/// <c>ServicioDeCompras</c> punta a punta — el invariante de saldo (compra leg), el get-or-create
/// bajo el lock del header (creación/reuso/conflicto), la carrera de dos confirmaciones sobre el
/// mismo <c>(articulo, codigo_lote)</c>, la concurrencia confirmar-vs-checkout, la captura de
/// borrador sin resolver, y el rechazo de recepción vencida en cada guardado.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasRecepcionDeLotesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // Regla permanente 3: fechas de vencimiento FIJAS y lejanas — independientes del reloj de la
    // corrida. El borde "hoy" no se asserta acá (no hay reloj pineado en los tests HTTP).
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);
    private static readonly DateOnly VencimientoLejanoFuturoAlterno = new(2098, 6, 30);
    private static readonly DateOnly VencimientoLejanoPasado = new(2020, 1, 15);

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticuloLote,
        int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin,
        int IdUsuarioAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Recepcion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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

        var articuloLote = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-lote-{Guid.NewGuid():N}", Nombre = "Articulo con lote",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            ControlaLote = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articuloLote);
        await db.SaveChangesAsync();

        // Etapa 12: módulo de lotes ON a nivel empresa — sin este flag, ControlEfectivo nunca da
        // true y el confirmar corre byte-idéntico al camino previo a esta etapa (spec
        // lotes-y-vencimientos: "Effective Lot Control").
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.IdTenant == resultado.IdTenant && m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Turno abierto — precondición del checkout usado por el test de concurrencia
        // confirmar-vs-checkout (task 5.10); las demás pruebas de este archivo no lo necesitan
        // pero sembrarlo acá evita duplicar PrepararAsync.
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        // Plataforma mode no filtra por tenant — sin el IdTenant explícito acá, FirstAsync puede
        // levantar el Consumidor Final de OTRO tenant sembrado por una corrida anterior de la
        // misma suite secuencial (mismo motivo que el resto de este método usa resultado.IdTenant
        // explícito en cada Where).
        var cf = await db.Clientes.FirstAsync(c => c.IdTenant == resultado.IdTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal);
        db.Precios.Add(new Precio
        {
            IdTenant = resultado.IdTenant, IdArticulo = articuloLote.Id, IdListaPrecio = cf.IdListaPrecio,
            Monto = 100m, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, articuloLote.Id,
            idAlicuotaIva21, idTipoCFA, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal, resultado.IdUsuarioAdmin);
    }

    private static SolicitudDeCompra SolicitudConLote(
        Contexto ctx, string? codigoLote, DateOnly? fechaVencimiento, decimal unidades = 10m,
        decimal costoUnitario = 100m, string? numeroExterno = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno ?? $"NE-{Guid.NewGuid():N}",
            DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(
                    ctx.IdArticuloLote, "Item con lote", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21,
                    ActualizaCosto: true, CodigoLote: codigoLote, FechaVencimiento: fechaVencimiento)
            ]);

    private static async Task<HttpResponseMessage> CrearBorradorRawAsync(Contexto ctx, SolicitudDeCompra solicitud) =>
        await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await CrearBorradorRawAsync(ctx, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<HttpResponseMessage> ConfirmarRawAsync(Contexto ctx, int id) =>
        await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ConfirmarRawAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<HttpResponseMessage> AnularRawAsync(Contexto ctx, int id) =>
        await ctx.Admin.PostAsync($"/api/compras/{id}/anular", null);

    // ---- task 5.5: invariante — stock_lotes.cantidad = SUM(movimientos con ese lote) -----------

    [Fact]
    public async Task StockLotesCantidadEsLaSumaDeSusMovimientosTrasDosCompras()
    {
        var ctx = await PrepararAsync(nameof(StockLotesCantidadEsLaSumaDeSusMovimientosTrasDosCompras));

        var primera = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-INV", VencimientoLejanoFuturo, unidades: 30m));
        var confirmada1 = await ConfirmarAsync(ctx, primera.Id);
        var idLote = confirmada1.Items[0].IdLote;
        Assert.NotNull(idLote);

        var segunda = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-INV", VencimientoLejanoFuturo, unidades: 20m));
        var confirmada2 = await ConfirmarAsync(ctx, segunda.Id);
        Assert.Equal(idLote, confirmada2.Items[0].IdLote);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidadStockLotes = await db.StockLotes
            .Where(sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad)
            .FirstAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticuloLote && m.IdPuntoVenta == ctx.IdPuntoVenta && m.IdLote == idLote)
            .SumAsync(m => m.Cantidad);

        Assert.Equal(50m, cantidadStockLotes);
        Assert.Equal(sumaDeMovimientos, cantidadStockLotes);
    }

    // ---- task 5.6: get-or-create crea y congela el lote sobre el item ----------------------------

    [Fact]
    public async Task ConfirmarGetOrCreaUnLoteYLoCongelaSobreElItem()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarGetOrCreaUnLoteYLoCongelaSobreElItem));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-002", VencimientoLejanoFuturo, unidades: 15m));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        var item = confirmada.Items[0];
        Assert.NotNull(item.IdLote);
        Assert.Equal("L-002", item.CodigoLote);
        Assert.Equal(VencimientoLejanoFuturo, item.FechaVencimiento);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var lote = await db.Lotes.SingleAsync(l => l.IdArticulo == ctx.IdArticuloLote && l.Codigo == "L-002");
        Assert.Equal(item.IdLote, lote.Id);
        Assert.Equal(VencimientoLejanoFuturo, lote.FechaVencimiento);

        Assert.Equal(
            1, await db.MovimientosStock.CountAsync(
                m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Compra && m.IdLote == lote.Id));

        var stockLote = await db.StockLotes
            .Where(sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == lote.Id)
            .Select(sl => sl.Cantidad)
            .FirstAsync();
        Assert.Equal(15m, stockLote);
    }

    // ---- task 5.7: una segunda recepción con vencimiento coincidente reusa el lote ---------------

    [Fact]
    public async Task ConfirmarReusaUnLoteExistenteConVencimientoCoincidente()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarReusaUnLoteExistenteConVencimientoCoincidente));

        var primera = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-REUSO", VencimientoLejanoFuturo, unidades: 10m));
        var confirmada1 = await ConfirmarAsync(ctx, primera.Id);
        var idLoteOriginal = confirmada1.Items[0].IdLote;

        var segunda = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-REUSO", VencimientoLejanoFuturo, unidades: 5m));
        var confirmada2 = await ConfirmarAsync(ctx, segunda.Id);

        Assert.Equal(idLoteOriginal, confirmada2.Items[0].IdLote);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.Lotes.CountAsync(l => l.IdArticulo == ctx.IdArticuloLote && l.Codigo == "L-REUSO"));
    }

    // ---- task 5.8: un vencimiento en conflicto para el mismo codigo se rechaza -------------------

    [Fact]
    public async Task ConfirmarRechazaUnVencimientoEnConflictoParaElMismoCodigo()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarRechazaUnVencimientoEnConflictoParaElMismoCodigo));

        var primera = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-CONFLICTO", VencimientoLejanoFuturo, unidades: 10m));
        await ConfirmarAsync(ctx, primera.Id);

        var segunda = await CrearBorradorAsync(
            ctx, SolicitudConLote(ctx, "L-CONFLICTO", VencimientoLejanoFuturoAlterno, unidades: 5m));
        var respuesta = await ConfirmarRawAsync(ctx, segunda.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_vencimiento_incompatible", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // Rollback completo: la segunda compra sigue en borrador, sin id_lote congelado y sin
        // movimiento propio — el 409 no dejó ningún rastro parcial.
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == segunda.Id)).Estado);
        Assert.Null(await db.ItemsComprobanteCompra.Where(i => i.IdComprobanteCompra == segunda.Id).Select(i => i.IdLote).FirstAsync());
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == segunda.Id));
        Assert.Equal(1, await db.Lotes.CountAsync(l => l.IdArticulo == ctx.IdArticuloLote && l.Codigo == "L-CONFLICTO"));
    }

    // ---- task 5.9: db-error-backstops — dos confirmaciones concurrentes sobre el mismo lote ------

    /// <summary>Diferencia deliberada con el race backstop clásico de <c>ManejadorDeErrores</c>
    /// (p.ej. <c>numero_externo</c> duplicado, o el alta admin de <c>ServicioDeLotes.CrearAsync</c>
    /// en Slice 3): <c>ServicioDeLotes.ResolverOCrearAsync</c> get-or-crea vía <c>INSERT ... ON
    /// CONFLICT (id_tenant, id_articulo, codigo) DO UPDATE ... RETURNING</c> (design decisión 4)
    /// contra <c>ux_lotes_articulo_codigo</c>, cuyas columnas y predicado COINCIDEN EXACTO con el
    /// índice (<c>LoteConfiguration.cs</c>). Postgres resuelve un <c>INSERT ... ON CONFLICT DO
    /// UPDATE</c> targeteado así completamente DENTRO del motor — el perdedor espera el lock de
    /// fila de la primera sesión y, cuando esta commitea, reevalúa el <c>WHERE</c> y hace el
    /// <c>UPDATE</c> — nunca propaga un <c>23505</c> al cliente. Esto NO es un error del apply: es
    /// exactamente la garantía que decisión 4 documenta como motivo de NO tener un retry-read loop
    /// ("There is therefore no retry-read loop in this design, and saying so is more honest than
    /// writing one that can never fire") y el mismo criterio que el doc-comment de
    /// <c>ServicioDeLotesTests.DosCrearAsyncConcurrentesDelMismoCodigoChocanConSqlstate23505AntesDelMapeo</c>
    /// usa para explicar por qué ESA prueba (que sí choca) tiene que pasar por
    /// <c>CrearAsync</c> —un <c>INSERT</c> EF plano, sin <c>ON CONFLICT</c>— en vez de
    /// <c>ResolverOCrearAsync</c>. Esta prueba corre el race real de todos modos (dos
    /// confirmaciones concurrentes sobre el mismo <c>(articulo, codigo_lote)</c>, dos conexiones
    /// independientes) y assertea el resultado GENUINAMENTE OBSERVADO: ambas confirmaciones
    /// terminan exitosas y resuelven al MISMO lote, sin ninguna excepción cruda que traducir —el
    /// requisito de negocio del spec ("both confirms succeed against the same lot") se cumple
    /// igual, solo que por el camino de auto-resolución de Postgres en vez de por un backstop de
    /// <c>ManejadorDeErrores</c>. (APPLY-RUN NOTE para judgment-day: si esta observación resulta
    /// sorprendente, es la misma garantía ya documentada en Slice 3 — no una regresión de este
    /// slice.)</summary>
    [Fact]
    public async Task DosConfirmacionesConcurrentesSobreElMismoCodigoDeLoteResuelvenAlMismoLote()
    {
        var ctx = await PrepararAsync(nameof(DosConfirmacionesConcurrentesSobreElMismoCodigoDeLoteResuelvenAlMismoLote));

        var compraA = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-RACE", VencimientoLejanoFuturo, unidades: 12m));
        var compraB = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-RACE", VencimientoLejanoFuturo, unidades: 8m));

        var tareaA = ConfirmarRawAsync(ctx, compraA.Id);
        var tareaB = ConfirmarRawAsync(ctx, compraB.Id);
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        foreach (var respuesta in respuestas)
        {
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(1, await db.Lotes.CountAsync(l => l.IdArticulo == ctx.IdArticuloLote && l.Codigo == "L-RACE"));

        var idLoteA = await db.ItemsComprobanteCompra.Where(i => i.IdComprobanteCompra == compraA.Id).Select(i => i.IdLote).FirstAsync();
        var idLoteB = await db.ItemsComprobanteCompra.Where(i => i.IdComprobanteCompra == compraB.Id).Select(i => i.IdLote).FirstAsync();
        Assert.NotNull(idLoteA);
        Assert.Equal(idLoteA, idLoteB);

        var cantidadStockLotes = await db.StockLotes
            .Where(sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLoteA)
            .Select(sl => sl.Cantidad)
            .FirstAsync();
        Assert.Equal(20m, cantidadStockLotes);
    }

    // ---- task 5.10: concurrencia, write-site 2 — confirmar vs. checkout, sin 40P01 ----------------

    /// <summary>El checkout de este worktree (slices 1-3 mergeadas, ANTES de Slice 7/8) todavía no
    /// toca <c>lotes</c>/<c>stock_lotes</c> — corre byte-idéntico al camino agregado-only. El
    /// bloque de resolución NUEVO que este slice agrega al confirmar toma el lock de <c>lotes</c>
    /// ANTES del de <c>stock</c> (decisión 3); el checkout de HOY solo toma el lock de
    /// <c>stock</c>. Sin un segundo recurso compartido en orden opuesto no hay ciclo posible —
    /// esta prueba es la regresión honesta que SÍ es verificable en este slice: agregar un nuevo
    /// paso de lock al confirmar no introdujo un deadlock contra el escritor de venta existente
    /// sobre el mismo artículo/PV. La prueba conjunta real (confirmar/checkout compitiendo por el
    /// MISMO lote) queda diferida a cuando el checkout sea lot-aware (Slice 7/8), mismo criterio
    /// que la nota 7 de tasks.md sobre "cross-slice concurrency dependency".</summary>
    [Fact]
    public async Task ConfirmarYCheckoutDelMismoArticuloEnParaleloNuncaDan40P01()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarYCheckoutDelMismoArticuloEnParaleloNuncaDan40P01));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-CONCURRENCIA", VencimientoLejanoFuturo, unidades: 40m));

        var solicitudVenta = new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(ctx.IdArticuloLote, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        var tareaConfirmar = ConfirmarRawAsync(ctx, creada.Id);
        var tareaCheckout = ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudVenta);

        var respuestaConfirmar = await tareaConfirmar;
        var respuestaCheckout = await tareaCheckout;

        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        var cuerpoCheckout = await respuestaCheckout.Content.ReadAsStringAsync();

        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        Assert.True(respuestaCheckout.StatusCode == HttpStatusCode.Created, cuerpoCheckout);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticuloLote && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticuloLote && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(m => m.Cantidad);
        Assert.Equal(cantidad, sumaDeMovimientos);
        Assert.Equal(39m, cantidad);
    }

    // ---- task 5.11: una línea de borrador captura el input de lote sin resolverlo ------------------

    [Fact]
    public async Task UnaLineaDeBorradorCapturaElInputDeLoteSinResolverlo()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaDeBorradorCapturaElInputDeLoteSinResolverlo));

        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-DRAFT", VencimientoLejanoFuturo, unidades: 7m));

        var item = creada.Items[0];
        Assert.Equal("L-DRAFT", item.CodigoLote);
        Assert.Equal(VencimientoLejanoFuturo, item.FechaVencimiento);
        Assert.Null(item.IdLote);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var itemPersistido = await db.ItemsComprobanteCompra.FirstAsync(i => i.IdComprobanteCompra == creada.Id);
        Assert.Equal("L-DRAFT", itemPersistido.CodigoLote);
        Assert.Equal(VencimientoLejanoFuturo, itemPersistido.FechaVencimiento);
        Assert.Null(itemPersistido.IdLote);

        Assert.Equal(0, await db.Lotes.CountAsync(l => l.IdArticulo == ctx.IdArticuloLote && l.Codigo == "L-DRAFT"));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    // ---- task 5.12: recepción vencida se rechaza al guardar; futura se acepta ---------------------

    [Fact]
    public async Task UnaLineaConVencimientoPasadoEsRechazadaAlGuardarElBorrador()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaConVencimientoPasadoEsRechazadaAlGuardarElBorrador));

        var respuesta = await CrearBorradorRawAsync(ctx, SolicitudConLote(ctx, "L-VENCIDO", VencimientoLejanoPasado));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_vencido_en_recepcion", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesCompra.CountAsync(c => c.IdProveedor == ctx.IdProveedor));
    }

    [Fact]
    public async Task UnaLineaConVencimientoFuturoEsAceptadaAlGuardarElBorrador()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaConVencimientoFuturoEsAceptadaAlGuardarElBorrador));

        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-FUTURO", VencimientoLejanoFuturo));

        Assert.Equal(VencimientoLejanoFuturo, creada.Items[0].FechaVencimiento);
    }

    /// <summary>Mismo rechazo, ahora en la edición de un borrador ya existente (spec: "This check
    /// MUST fire when the line is saved or edited, not only at confirm") — un PUT también es un
    /// "save".</summary>
    [Fact]
    public async Task UnaLineaConVencimientoPasadoEsRechazadaAlEditarElBorrador()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaConVencimientoPasadoEsRechazadaAlEditarElBorrador));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-EDICION", VencimientoLejanoFuturo));

        var edicion = SolicitudConLote(ctx, "L-EDICION", VencimientoLejanoPasado, numeroExterno: $"NE-{Guid.NewGuid():N}");
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", edicion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_vencido_en_recepcion", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var itemPersistido = await db.ItemsComprobanteCompra.FirstAsync(i => i.IdComprobanteCompra == creada.Id);
        Assert.Equal(VencimientoLejanoFuturo, itemPersistido.FechaVencimiento);
    }

    // ---- soporte: 400 lote_requerido cuando un artículo lot-effective no trae fecha/codigo -------

    /// <summary>No nombrado explícitamente en el test plan de la slice (5.5-5.12), pero es la
    /// guarda que el bloque de resolución de <c>EjecutarConfirmarAsync</c> agrega (design: Write
    /// site 2, pseudocódigo de la sección "Confirm") — un ítem lot-effective sin ningún input de
    /// lote no puede resolver <c>ResolverOCrearAsync</c> (que exige <c>fechaVencimiento</c> no
    /// nula), así que se rechaza ANTES de intentarlo.</summary>
    [Fact]
    public async Task ConfirmarRechazaUnItemLoteEfectivoSinFechaDeVencimientoConLoteRequerido()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarRechazaUnItemLoteEfectivoSinFechaDeVencimientoConLoteRequerido));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, null, null));

        var respuesta = await ConfirmarRawAsync(ctx, creada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_requerido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    // ---- judgment-day (juez B) FIX 1: codigo_lote sin fecha vía API nunca da 500 -------------------

    /// <summary>Guard de aplicación (<c>ValidarVencimientosDeRecepcion</c>, FIX 1a) — corre en
    /// <c>CrearBorradorAsync</c>, ANTES de cualquier lectura de base, así que nunca llega siquiera
    /// a ejercitar el backstop de esquema (<c>ck_items_comprobante_compra_lote_input</c>, FIX 1b)
    /// en el camino normal.</summary>
    [Fact]
    public async Task CrearBorradorConCodigoDeLoteSinFechaDeVencimientoDa400LoteInputIncompletoNunca500()
    {
        var ctx = await PrepararAsync(nameof(CrearBorradorConCodigoDeLoteSinFechaDeVencimientoDa400LoteInputIncompletoNunca500));

        var respuesta = await CrearBorradorRawAsync(ctx, SolicitudConLote(ctx, "L-SIN-FECHA", null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_input_incompleto", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesCompra.CountAsync(c => c.IdProveedor == ctx.IdProveedor));
    }

    // ---- judgment-day (juez B) FIX 2: re-check de vencido al confirmar, borrador que vence entre save y confirm ----

    /// <summary>El borrador se guarda con una fecha FUTURA válida (pasa el guard de
    /// <c>CrearBorradorAsync</c>), y después se muta esa fecha a PASADA directo en la base vía la
    /// conexión owner de la fixture — esquivando <c>ValidarVencimientosDeRecepcion</c> a propósito
    /// (esa validación solo corre en <c>Crear/ActualizarBorradorAsync</c>, nunca en un UPDATE
    /// crudo), simulando el borrador que vence ENTRE el save y el confirm (spec: "This check MUST
    /// fire when the line is saved or edited, not only at confirm" — el rechequeo de
    /// <c>EjecutarConfirmarAsync</c>, ~líneas 409-419, es la otra mitad de esa garantía).</summary>
    [Fact]
    public async Task ConfirmarRechazaUnLoteQueVencioEntreElSaveYElConfirm()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarRechazaUnLoteQueVencioEntreElSaveYElConfirm));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-VENCE-ENTRE", VencimientoLejanoFuturo));

        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var comando = owner.CreateCommand();
            comando.CommandText =
                "UPDATE items_comprobante_compra SET fecha_vencimiento = $1 WHERE id_comprobante_compra = $2";
            comando.Parameters.Add(new NpgsqlParameter { Value = VencimientoLejanoPasado });
            comando.Parameters.Add(new NpgsqlParameter { Value = creada.Id });
            await comando.ExecuteNonQueryAsync();
        }

        var respuesta = await ConfirmarRawAsync(ctx, creada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("lote_vencido_en_recepcion", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    // ---- task 6.3: reversa exacta por lote (reemplaza el guard interino de slice 5 FIX 4) --------

    /// <summary>Etapa 12, slice 6, task 6.1 — reemplaza el guard interino
    /// (<c>compra_anulacion_lotes_pendiente</c>) documentado en el judgment-day de slice 5 (FIX 4)
    /// por la reversa EXACTA: el movimiento inverso copia <c>original.IdLote</c> (sin re-derivar,
    /// design: "Exactness is structural, not derived") y <c>stock_lotes</c> del lote se revierte en
    /// la misma transacción. (spec comprobantes-compra: "Anulación reverses a lot-bearing item into
    /// its exact lot")</summary>
    [Fact]
    public async Task AnularUnaCompraConLoteResueltoRevierteElLoteExacto()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraConLoteResueltoRevierteElLoteExacto));
        var creada = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-REVERSA", VencimientoLejanoFuturo, unidades: 12m));
        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        var idLote = confirmada.Items[0].IdLote;
        Assert.NotNull(idLote);

        var respuesta = await AnularRawAsync(ctx, creada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(EstadoCompra.Anulada, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);

        var movimientoAnulacion = await db.MovimientosStock
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal(idLote, movimientoAnulacion.IdLote);
        Assert.Equal(-12m, movimientoAnulacion.Cantidad);

        var cantidadStock = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticuloLote && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(0m, cantidadStock);

        var cantidadStockLote = await db.StockLotes
            .Where(sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(0m, cantidadStockLote);
    }

    // ---- task 6.2: mutation target — el chequeo POR LOTE, no solo el agregado ---------------------

    /// <summary>Mutation target (spec: "Anulación refused by a lot-level underflow despite a
    /// sufficient aggregate"; mutation-proof-tests). Regla 3 de mutation-proof-tests, matar el
    /// confound del agregado: sembramos el mismo artículo con SALDO SUFICIENTE en el agregado
    /// repartido en DOS lotes (lote L-7 con 50 recibidas, lote L-9 con 100 recibidas, agregado =
    /// 150) — si el chequeo por lote se borrara, el chequeo agregado por sí solo NO detectaría el
    /// problema, porque el agregado nunca queda negativo. Una venta simulada (mismo patrón que la
    /// prueba self-heal de <c>ReconciliacionTests</c>, task 4.7 — el escritor de venta lot-aware es
    /// Slice 7/8, todavía no aterriza en esta rama) saca 40 unidades ESPECÍFICAMENTE del lote L-7
    /// (deja <c>stock_lotes</c> de L-7 en 10, agregado en 110). Anular la compra que recibió L-7 (50
    /// unidades) dejaría el agregado en 60 (positivo, el chequeo agregado lo aprobaría) pero el
    /// lote L-7 en -40 — solo el chequeo por lote lo detecta.</summary>
    [Fact]
    public async Task AnularUnaCompraEsRechazadaPorUnLoteInsuficienteAunConAgregadoSuficiente()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraEsRechazadaPorUnLoteInsuficienteAunConAgregadoSuficiente));

        var compraLote7 = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-7", VencimientoLejanoFuturo, unidades: 50m));
        var confirmadaLote7 = await ConfirmarAsync(ctx, compraLote7.Id);
        var idLote7 = confirmadaLote7.Items[0].IdLote!.Value;

        var compraLote9 = await CrearBorradorAsync(ctx, SolicitudConLote(ctx, "L-9", VencimientoLejanoFuturo, unidades: 100m));
        await ConfirmarAsync(ctx, compraLote9.Id);

        // Venta simulada: 40 unidades salen específicamente del lote 7 (patrón self-heal de
        // ReconciliacionTests task 4.7 — sin escritor de venta lot-aware todavía en esta rama).
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.MovimientosStock.Add(new MovimientoStock
            {
                IdTenant = ctx.IdTenant, IdArticulo = ctx.IdArticuloLote, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = -40m,
                Motivo = MotivoStock.Venta, IdLote = idLote7, IdEmpleado = ctx.IdUsuarioAdmin, CreadoEl = ahora
            });

            var stock = await db.Stock.SingleAsync(s => s.IdArticulo == ctx.IdArticuloLote && s.IdPuntoVenta == ctx.IdPuntoVenta);
            stock.Cantidad -= 40m;

            var stockLote7 = await db.StockLotes.SingleAsync(
                sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote7);
            stockLote7.Cantidad -= 40m;

            await db.SaveChangesAsync();
        }

        await using (var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var agregadoAntes = await dbAntes.Stock
                .Where(s => s.IdArticulo == ctx.IdArticuloLote && s.IdPuntoVenta == ctx.IdPuntoVenta)
                .Select(s => s.Cantidad).FirstAsync();
            Assert.Equal(110m, agregadoAntes);
        }

        var respuesta = await AnularRawAsync(ctx, compraLote7.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("compra_anulacion_stock_negativo", problema.GetProperty("codigo").GetString());

        await using var db2 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // Rollback completo: ni el estado, ni el agregado, ni el lote 7 cambiaron.
        Assert.Equal(EstadoCompra.Confirmada, (await db2.ComprobantesCompra.FirstAsync(c => c.Id == compraLote7.Id)).Estado);
        Assert.Equal(
            0, await db2.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == compraLote7.Id && m.Motivo == MotivoStock.Anulacion));

        var agregadoDespues = await db2.Stock
            .Where(s => s.IdArticulo == ctx.IdArticuloLote && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(110m, agregadoDespues);

        var stockLote7Despues = await db2.StockLotes
            .Where(sl => sl.IdArticulo == ctx.IdArticuloLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote7)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(10m, stockLote7Despues);
    }

    /// <summary>Regresión del guard interino de FIX 4: un artículo que NO controla lote (mismo
    /// tenant, módulo de lotes ON a nivel empresa) sigue confirmando y anulando byte-idéntico al
    /// camino agregado-only previo a esta etapa — <c>item.IdLote</c> nunca se pobla, así que el
    /// guard nuevo nunca dispara.</summary>
    [Fact]
    public async Task AnularUnaCompraDeUnArticuloQueNoControlaLoteSigueFuncionando()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraDeUnArticuloQueNoControlaLoteSigueFuncionando));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idArea = await db.Areas.Where(a => a.IdTenant == ctx.IdTenant).Select(a => a.Id).FirstAsync();
        var articuloSinLote = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"sin-lote-{Guid.NewGuid():N}", Nombre = "Articulo sin lote",
            IdArea = idArea, IdAlicuotaIva = ctx.IdAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            ControlaLote = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articuloSinLote);
        await db.SaveChangesAsync();

        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, $"NE-{Guid.NewGuid():N}", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(articuloSinLote.Id, "Item sin lote", 8m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, true)]);

        var creada = await CrearBorradorAsync(ctx, solicitud);
        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        Assert.Null(confirmada.Items[0].IdLote);

        var respuesta = await AnularRawAsync(ctx, creada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Anulada, (await dbFinal.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        var cantidad = await dbFinal.Stock
            .Where(s => s.IdArticulo == articuloSinLote.Id && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(0m, cantidad);
    }
}
