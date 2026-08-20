using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (tasks 6.13-6.24; design: Transactions — "FACTURAR
/// REMITOS (consolidación)"; mutation targets 48-58). <see cref="ServicioDeFacturacionDeRemitos.FacturarAsync"/>
/// consolida N remitos <c>emitido</c> en UN comprobante <c>TXR</c> itemless (precedente <c>RC</c>) —
/// este archivo prueba cero items/cero stock en ambas direcciones (emisión y anulación), las tres
/// razas (facturar × facturar, facturar × anular-remito, anular-TXR × facturar), el backstop de
/// límite de crédito re-implementado, el guard de turno (y su ausencia deliberada en el cuarto
/// write site), el desligue guardado y el discriminante OD8/T3.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeFacturacionDeRemitosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdPuntoVenta2, HttpClient Admin, int IdArea,
        int IdAlicuotaIva, int IdListaPrecio, int IdCliente, int IdMedioEfectivo, int IdMedioCuentaCorriente,
        int IdUsuarioAdmin, string MailAdmin, string PasswordAdmin);

    private async Task<Contexto> PrepararAsync(string nombre, bool conTurnoAbierto = true)
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Txr-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista TXR", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 3000 + Random.Shared.Next(1, 100_000), Nombre = $"{nombre}-cliente",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 0,
            CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente TXR", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = $"{nombre}-PV2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        if (conTurnoAbierto)
        {
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
                IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
                Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVenta2.Id, admin, area.Id,
            idAlicuotaIva, lista.Id, cliente.Id, idMedioEfectivo, medioCc.Id, resultado.IdUsuarioAdmin, mailAdmin,
            resultado.PasswordTemporal);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, decimal precio)
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

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad, int? idPuntoVenta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, decimal limiteCredito = 0, bool creditoIlimitado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 4000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = limiteCredito,
            CreditoIlimitado = creditoIlimitado, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private static SolicitudDeRemito SolicitudRemitoSimple(
        Contexto ctx, int idArticulo, decimal cantidad, int? idCliente = null, int? idPuntoVenta = null) =>
        new(idPuntoVenta ?? ctx.IdPuntoVenta, idCliente ?? ctx.IdCliente, null, null, [new LineaDeRemito(idArticulo, cantidad, null)]);

    /// <summary>Crea + emite un remito vía HTTP, devolviendo el <see cref="RemitoDetalle"/> ya
    /// <c>emitido</c> — helper compartido por todos los tests de este archivo.</summary>
    private static async Task<RemitoDetalle> CrearYEmitirRemitoAsync(HttpClient cliente, SolicitudDeRemito solicitud)
    {
        var creado = await cliente.PostAsJsonAsync("/api/remitos", solicitud);
        var cuerpoCreado = await creado.Content.ReadAsStringAsync();
        Assert.True(creado.StatusCode == HttpStatusCode.Created, cuerpoCreado);
        var detalle = JsonSerializer.Deserialize<RemitoDetalle>(cuerpoCreado, OpcionesJson)!;

        var emitido = await cliente.PostAsync($"/api/remitos/{detalle.Id}/emitir", null);
        var cuerpoEmitido = await emitido.Content.ReadAsStringAsync();
        Assert.True(emitido.StatusCode == HttpStatusCode.OK, cuerpoEmitido);
        return JsonSerializer.Deserialize<RemitoDetalle>(cuerpoEmitido, OpcionesJson)!;
    }

    private static SolicitudDeFacturacionDeRemitos SolicitudFacturacion(
        Contexto ctx, IReadOnlyList<int> idsRemito, decimal importe, int? idMedio = null) =>
        new(ctx.IdPuntoVenta, idsRemito, [new PagoDeVenta(idMedio ?? ctx.IdMedioEfectivo, importe, null, 0m)], "obs-txr");

    // =============================================================================================
    // task 6.13: consolidación básica — itemless, total == Σ headers, cero movimientos_stock
    // =============================================================================================

    [Fact]
    public async Task DosRemitosConsolidanEnUnTxrItemlessConTotalIgualALaSumaDeLosHeadersYCeroMovimientosDeStock()
    {
        var ctx = await PrepararAsync(nameof(DosRemitosConsolidanEnUnTxrItemlessConTotalIgualALaSumaDeLosHeadersYCeroMovimientosDeStock));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Art 1", 100m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Art 2", 250m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 20m);

        var remito1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 3m));
        var remito2 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 2m));

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientosAntes = await dbAntes.MovimientosStock.CountAsync();

        var totalEsperado = remito1.Total + remito2.Total;
        var solicitud = SolicitudFacturacion(ctx, [remito1.Id, remito2.Id], totalEsperado);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var txr = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        Assert.Empty(txr.Items);
        Assert.Equal(totalEsperado, txr.Total);
        Assert.Equal(remito1.Subtotal + remito2.Subtotal, txr.Subtotal);
        Assert.Equal(remito1.DescuentoTotal + remito2.DescuentoTotal, txr.DescuentoTotal);
        Assert.Equal(ctx.IdCliente, txr.IdCliente);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r1 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito1.Id);
        var r2 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito2.Id);
        Assert.Equal(EstadoRemito.Facturado, r1.Estado);
        Assert.Equal(EstadoRemito.Facturado, r2.Estado);
        Assert.Equal(txr.Id, r1.IdComprobanteVenta);
        Assert.Equal(txr.Id, r2.IdComprobanteVenta);

        // Mutation target 52 (mitad "cero items"): cero movimientos_stock adicionales — la
        // mercadería ya salió por los remitos individuales (write site 4), la consolidación no
        // toca stock por construcción.
        var movimientosDespues = await db.MovimientosStock.CountAsync();
        Assert.Equal(movimientosAntes, movimientosDespues);
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteVenta == txr.Id));

        var itemsDelTxr = await db.ItemsComprobanteVenta.CountAsync(i => i.IdComprobanteVenta == txr.Id);
        Assert.Equal(0, itemsDelTxr);
    }

    // =============================================================================================
    // mutation target 48 — FINDING REGISTERED (mutation-proof-tests regla 2/3, "run it, don't
    // reason it" + "route below the confound"). La rendezvous pausada-vs-libre de task 6.14 (más
    // abajo) NO discrimina el orden: corrida bajo la mutación REAL (ORDER BY id_remito DESC en
    // BloquearAscendenteAsync), la suite completa de este archivo siguió en verde — confirmado
    // EMPÍRICAMENTE (`dotnet test` con la mutación aplicada, revertida después), no razonado. Motivo
    // estructural: esa rendezvous nunca pone dos transacciones a competir de verdad por filas en
    // orden inverso — una queda pausada ANTES de intentar cualquier lock mientras la otra corre
    // completa y sin contención, así que el ORDEN de adquisición nunca queda expuesto (mismo
    // "pre-check que espeja un guard transaccional" que rule 3 documenta, salvo que acá el espejo es
    // la ausencia total de contención real).
    //
    // Se intentó una prueba below-the-confound de dos conexiones (bloqueadora + probe NOWAIT desde
    // una tercera conexión), mismo patrón que <c>ServicioDeRemitosTests.EmitirRemitoObservandoOrdenDeLocksAsync</c>
    // (target 40) — un experimento psql de dos sesiones AISLADO (dos <c>psql -c</c> concurrentes
    // contra una tabla mínima de 2 filas) confirmó que Postgres SÍ lockea en orden ascendente para
    // <c>ORDER BY id FOR UPDATE</c> (el probe NOWAIT de la fila menor falló con <c>55P03</c> mientras
    // la fila mayor seguía bloqueada por la conexión bloqueadora — la fila menor YA estaba tomada).
    // Pero la MISMA técnica reimplementada contra el harness completo (WebApplicationFactory +
    // fixture.AbrirConexionCrudaAsync abriendo ~250 conexiones nuevas en 8s) nunca reprodujo la señal
    // — ruido de pool/timing del harness, no una falla de la implementación (la evidencia SQL
    // aislada ya la prueba). Se descartó esa prueba en vez de dejarla flaky/incorrecta.
    //
    // Red REAL para este target (below-the-confound, estable): fuente de texto, mismo criterio que
    // <c>PresupuestosSchemaTests.ElTextoFuenteDeLaMigracionConservaLosDosFiltrosParcialesTargets4Y5</c>
    // y <c>ServicioDeVentasPosicionDeConversionTests</c> — el ORDER BY exacto tiene que estar en el
    // texto fuente de <c>BloquearAscendenteAsync</c>, nunca invertido.
    // =============================================================================================

    [Fact]
    public void ElOrderByDeBloquearAscendenteEsAscendentePorIdRemitoNuncaDescendente()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Application", "Ventas", "EscriturasDeRemito.cs");
        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        var fuente = File.ReadAllText(ruta);

        var inicio = fuente.IndexOf(
            "public static async Task<IReadOnlyList<(int IdRemito, string Estado, int? IdComprobante)>> BloquearAscendenteAsync(",
            StringComparison.Ordinal);
        Assert.True(inicio >= 0, "No se encontró el método BloquearAscendenteAsync.");
        var fin = fuente.IndexOf("public static async Task<int> LigarAsync(", inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, "No se encontró el cierre de BloquearAscendenteAsync.");

        var metodo = fuente[inicio..fin];
        Assert.Contains("ORDER BY id_remito FOR UPDATE", metodo, StringComparison.Ordinal);
        Assert.DoesNotContain("id_remito DESC", metodo, StringComparison.Ordinal);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    // =============================================================================================
    // task 6.14: facturar × facturar sobre sets superpuestos — exactamente un 201 + un 409
    // (mutation targets 48/50)
    // =============================================================================================

    private sealed class InterceptorDePausaTrasIniciarLaTransaccion(
        TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
    {
        public override async ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
            System.Data.Common.DbConnection connection, TransactionEndEventData eventData,
            System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            transaccionIniciada.TrySetResult();
            await puedeContinuar.Task;
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Mismo patrón que <c>ServicioDeRemitosTests.DobleEmitirConcurrenteEsRechazado409ViaElGuardNoViaElPreCheck</c>:
    /// el primer <c>facturar</c> pausa justo tras abrir SU transacción — antes de tomar el lock
    /// ascendente de <see cref="EscriturasDeRemito.BloquearAscendenteAsync"/> — mientras un segundo
    /// <c>facturar</c> sobre un set SUPERPUESTO (comparte el remito 2) corre completo y commitea.
    /// Al reanudar, el primero toma el lock (ahora libre, remito 2 ya <c>facturado</c>), pero
    /// <see cref="EscriturasDeRemito.LigarAsync"/> (mutation target 50: <c>estado='emitido' AND
    /// id_comprobante_venta IS NULL</c> + el rowcount == N) solo matchea 1 de 2 filas ⇒
    /// <c>409 remito_no_facturable</c>, con TODO lo que esa transacción ya escribió (comprobante,
    /// pagos) revertido por la atomicidad — nunca un TXR fantasma.</summary>
    [Fact]
    public async Task FacturarXFacturarSobreSetsSuperpuestosDaExactamenteUn201YUn409()
    {
        var ctx = await PrepararAsync(nameof(FacturarXFacturarSobreSetsSuperpuestosDaExactamenteUn201YUn409));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Solap 1", 50m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Solap 2", 70m);
        var idArticulo3 = await SembrarArticuloAsync(ctx, "Txr Solap 3", 90m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo3, 10m);

        var remito1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 1m));
        var remito2 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 1m)); // compartido
        var remito3 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo3, 1m));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clientePrimero = factory.CreateClient();
        var login = await clientePrimero.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Primero: remitos [1,2], pausado tras abrir su transacción.
        var solicitudPrimero = SolicitudFacturacion(ctx, [remito1.Id, remito2.Id], remito1.Total + remito2.Total);
        var tareaPrimero = clientePrimero.PostAsJsonAsync("/api/remitos/facturacion", solicitudPrimero);

        await transaccionIniciada.Task;

        // Segundo: remitos [2,3] — SUPERPUESTO en el remito 2 — corre completo, fuera del interceptor.
        var solicitudSegundo = SolicitudFacturacion(ctx, [remito2.Id, remito3.Id], remito2.Total + remito3.Total);
        var respuestaSegundo = await ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", solicitudSegundo);
        var cuerpoSegundo = await respuestaSegundo.Content.ReadAsStringAsync();
        Assert.True(respuestaSegundo.StatusCode == HttpStatusCode.Created, cuerpoSegundo);
        var txrSegundo = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpoSegundo, OpcionesJson)!;

        puedeContinuar.TrySetResult();

        var respuestaPrimero = await tareaPrimero;
        var cuerpoPrimero = await respuestaPrimero.Content.ReadAsStringAsync();
        Assert.True(respuestaPrimero.StatusCode == HttpStatusCode.Conflict, cuerpoPrimero);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoPrimero, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r1 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito1.Id);
        var r2 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito2.Id);
        var r3 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito3.Id);

        // El perdedor (primero) nunca escribió NADA: remito 1 sigue emitido, sin ligar.
        Assert.Equal(EstadoRemito.Emitido, r1.Estado);
        Assert.Null(r1.IdComprobanteVenta);
        // El ganador (segundo) ligó 2 y 3 a SU comprobante.
        Assert.Equal(EstadoRemito.Facturado, r2.Estado);
        Assert.Equal(EstadoRemito.Facturado, r3.Estado);
        Assert.Equal(txrSegundo.Id, r2.IdComprobanteVenta);
        Assert.Equal(txrSegundo.Id, r3.IdComprobanteVenta);

        // Exactamente UN comprobante TXR fue creado — el rollback del perdedor no dejó rastro.
        var idTipoTxr = await db.TiposComprobante.Where(t => t.Codigo == "TXR").Select(t => t.Id).FirstAsync();
        Assert.Equal(1, await db.ComprobantesVenta.CountAsync(c => c.IdTipoComprobante == idTipoTxr));
    }

    // =============================================================================================
    // task 6.15: facturar × anular-remito, ambos órdenes (mutation target 49)
    // =============================================================================================

    [Fact]
    public async Task FacturarGanaLaCarreraContraAnularRemitoYAnularRecibe409RemitoFacturado()
    {
        var ctx = await PrepararAsync(nameof(FacturarGanaLaCarreraContraAnularRemitoYAnularRecibe409RemitoFacturado));
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Vs Anular A", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        var remito = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteAnular = factory.CreateClient();
        var login = await clienteAnular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // El ANULAR es el pausado (tras abrir SU transacción, antes de tomar ningún lock) — el
        // facturar CONCURRENTE, sin interceptor, corre completo y gana la carrera del lock
        // ascendente (mutation target 49: el lock del remito antes del INSERT/clientes le permite
        // ganar sin contienda). Al reanudar, el anular retoma bajo el MISMO row lock que su propio
        // UPDATE ya iba a tomar — lo ve libre, pero el estado ya cambió a 'facturado'.
        var tareaAnular = clienteAnular.PostAsync($"/api/remitos/{remito.Id}/anular", null);

        await transaccionIniciada.Task;

        var respuestaFacturar = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));

        puedeContinuar.TrySetResult();

        var cuerpoFacturar = await respuestaFacturar.Content.ReadAsStringAsync();
        var respuestaAnular = await tareaAnular;
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();

        Assert.True(respuestaFacturar.StatusCode == HttpStatusCode.Created, cuerpoFacturar);
        Assert.Equal(HttpStatusCode.Conflict, respuestaAnular.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoAnular, OpcionesJson);
        Assert.Equal("remito_facturado", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r = await db.Remitos.AsNoTracking().FirstAsync(x => x.Id == remito.Id);
        Assert.Equal(EstadoRemito.Facturado, r.Estado);
    }

    [Fact]
    public async Task AnularRemitoGanaLaCarreraContraFacturarYFacturarRecibe409RemitoNoFacturable()
    {
        var ctx = await PrepararAsync(nameof(AnularRemitoGanaLaCarreraContraFacturarYFacturarRecibe409RemitoNoFacturable));
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Vs Anular B", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        var remito = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteFacturar = factory.CreateClient();
        var login = await clienteFacturar.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Esta vez el FACTURAR es el pausado (tras abrir su transacción, antes de tomar el lock
        // ascendente) — el anular CONCURRENTE, sin interceptor, corre completo y gana el row lock
        // del remito primero. Al reanudar, BloquearAscendenteAsync toma el lock ya libre, pero
        // LigarAsync (mutation target 50) ve el estado 'anulado' — no matchea, 0 filas ⇒ 409.
        var tareaFacturar = clienteFacturar.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));

        await transaccionIniciada.Task;

        var respuestaAnular = await ctx.Admin.PostAsync($"/api/remitos/{remito.Id}/anular", null);

        puedeContinuar.TrySetResult();

        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        var respuestaFacturar = await tareaFacturar;
        var cuerpoFacturar = await respuestaFacturar.Content.ReadAsStringAsync();

        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.OK, cuerpoAnular);
        Assert.Equal(HttpStatusCode.Conflict, respuestaFacturar.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoFacturar, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r = await db.Remitos.AsNoTracking().FirstAsync(x => x.Id == remito.Id);
        Assert.Equal(EstadoRemito.Anulado, r.Estado);
        Assert.Null(r.IdComprobanteVenta);
    }

    // =============================================================================================
    // task 6.16: sets mixtos — 409 ANTES de escribir (mutation target 51). Un test por conjunto,
    // mutation-proof-tests regla 3: enumerar cada conjunto del guard "mismo cliente/PV/tenant,
    // todos emitido y sin ligar" y aparear cada uno con su propio kill.
    // =============================================================================================

    [Fact]
    public async Task UnSetConClientesMixtosEsRechazado409AntesDeEscribir()
    {
        var ctx = await PrepararAsync(nameof(UnSetConClientesMixtosEsRechazado409AntesDeEscribir));
        var idCliente2 = await SembrarClienteAsync(ctx, "Cliente TXR Mixto", creditoIlimitado: true);
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Mixto Cliente", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        var remitoCliente1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));
        var remitoCliente2 = await CrearYEmitirRemitoAsync(
            ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m, idCliente: idCliente2));

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var comprobantesAntes = await dbAntes.ComprobantesVenta.CountAsync();

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion",
            SolicitudFacturacion(ctx, [remitoCliente1.Id, remitoCliente2.Id], remitoCliente1.Total + remitoCliente2.Total));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(comprobantesAntes, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(EstadoRemito.Emitido, (await db.Remitos.FirstAsync(r => r.Id == remitoCliente1.Id)).Estado);
        Assert.Equal(EstadoRemito.Emitido, (await db.Remitos.FirstAsync(r => r.Id == remitoCliente2.Id)).Estado);
    }

    [Fact]
    public async Task UnSetConPuntosDeVentaMixtosEsRechazado409AntesDeEscribir()
    {
        var ctx = await PrepararAsync(nameof(UnSetConPuntosDeVentaMixtosEsRechazado409AntesDeEscribir));
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Mixto PV", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m, idPuntoVenta: ctx.IdPuntoVenta2);

        var remitoPv1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));
        var remitoPv2 = await CrearYEmitirRemitoAsync(
            ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m, idPuntoVenta: ctx.IdPuntoVenta2));

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var comprobantesAntes = await dbAntes.ComprobantesVenta.CountAsync();

        // idPuntoVenta de la solicitud fija el PV1 — remitoPv2 no coincide.
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion",
            SolicitudFacturacion(ctx, [remitoPv1.Id, remitoPv2.Id], remitoPv1.Total + remitoPv2.Total));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(comprobantesAntes, await db.ComprobantesVenta.CountAsync());
    }

    [Fact]
    public async Task UnRemitoYaFacturadoDentroDelSetEsRechazado409AntesDeEscribir()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoYaFacturadoDentroDelSetEsRechazado409AntesDeEscribir));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Ya Facturado 1", 40m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Ya Facturado 2", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 10m);

        var remitoYaFacturado = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 1m));
        var primeraFacturacion = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remitoYaFacturado.Id], remitoYaFacturado.Total));
        Assert.Equal(HttpStatusCode.Created, primeraFacturacion.StatusCode);

        var remitoNuevo = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 1m));

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var comprobantesAntes = await dbAntes.ComprobantesVenta.CountAsync();

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion",
            SolicitudFacturacion(ctx, [remitoYaFacturado.Id, remitoNuevo.Id], remitoYaFacturado.Total + remitoNuevo.Total));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(comprobantesAntes, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(EstadoRemito.Emitido, (await db.Remitos.FirstAsync(r => r.Id == remitoNuevo.Id)).Estado);
    }

    // =============================================================================================
    // task 6.17: límite de crédito excedido por consolidación CONCURRENTE (mutation target 53) —
    // mismo patrón (sin interceptor) que VentasAtomicidadYConcurrenciaTests.
    // DosVentasConcurrentesDeCuentaCorrienteNuncaSuperanElLimite: dos facturaciones IDÉNTICAS,
    // cada una individualmente bajo el límite, juntas lo superan — el pre-chequeo de
    // ValidadorDePagos (fuera de la transacción) pasa las DOS, el backstop DENTRO de la
    // transacción (re-implementado, OD9/T9) atrapa a la perdedora de la carrera del UPDATE...
    // RETURNING sobre clientes.saldo.
    // =============================================================================================

    [Fact]
    public async Task LimiteDeCreditoExcedidoPorConsolidacionConcurrenteEntrePreChequeoYCommit()
    {
        var ctx = await PrepararAsync(nameof(LimiteDeCreditoExcedidoPorConsolidacionConcurrenteEntrePreChequeoYCommit));
        var idClienteLimitado = await SembrarClienteAsync(ctx, "Cliente TXR Limitado", limiteCredito: 1000m);
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Credito 1", 600m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Credito 2", 600m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 10m);

        var remitoA = await CrearYEmitirRemitoAsync(
            ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 1m, idCliente: idClienteLimitado));
        var remitoB = await CrearYEmitirRemitoAsync(
            ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 1m, idCliente: idClienteLimitado));

        var solicitudA = SolicitudFacturacion(ctx, [remitoA.Id], remitoA.Total, ctx.IdMedioCuentaCorriente);
        var solicitudB = SolicitudFacturacion(ctx, [remitoB.Id], remitoB.Total, ctx.IdMedioCuentaCorriente);

        var tareaA = ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", solicitudA);
        var tareaB = ctx.Admin.PostAsJsonAsync("/api/remitos/facturacion", solicitudB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.BadRequest, estados);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idClienteLimitado).Select(c => c.Saldo).FirstAsync();
        var sumaDeMovimientos = await db.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idClienteLimitado).SumAsync(m => m.Importe);

        Assert.Equal(600m, saldo);
        Assert.Equal(saldo, sumaDeMovimientos);
        Assert.True(saldo <= 1000m, $"El saldo ({saldo}) no puede superar el límite de crédito (1000).");
    }

    // =============================================================================================
    // task 6.18: turno cerrado 409 para la consolidación; ausencia deliberada para emitir de
    // remito (decisión 13, ambas direcciones)
    // =============================================================================================

    [Fact]
    public async Task LaConsolidacionSinTurnoAbiertoEsRechazada409TurnoNoAbierto()
    {
        var ctx = await PrepararAsync(nameof(LaConsolidacionSinTurnoAbiertoEsRechazada409TurnoNoAbierto), conTurnoAbierto: false);
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Sin Turno", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        // ServicioDeRemitos.EmitirAsync (decisión 13, mismo test que abajo) NO exige turno — el
        // remito se emite igual, sin turno abierto en todo el punto de venta.
        var remito = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoRemito.Emitido, (await db.Remitos.FirstAsync(r => r.Id == remito.Id)).Estado);
    }

    [Fact]
    public async Task EmitirUnRemitoNoExigeNingunTurnoAbierto()
    {
        // Mutation target 54, mitad "ausencia deliberada": la creación + emisión de un remito
        // (el cuarto write site) tiene que suceder aunque el punto de venta no tenga NINGÚN turno
        // abierto — a diferencia de la consolidación (test de arriba), decisión 13 del proposal:
        // "un remito mueve mercadería, no dinero".
        var ctx = await PrepararAsync(nameof(EmitirUnRemitoNoExigeNingunTurnoAbierto), conTurnoAbierto: false);
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Emitir Sin Turno", 40m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        var remito = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));
        Assert.Equal(EstadoRemito.Emitido, remito.Estado);
    }

    // =============================================================================================
    // task 6.20/6.21: anular un TXR — devuelve remitos a emitido, limpia ligadura, revierte CC,
    // cero movimientos de stock. 6.21 es el discriminante OD8/T3: ambas mitades juntas, en una
    // sola transacción.
    // =============================================================================================

    [Fact]
    public async Task AnularUnTxrDevuelveSusRemitosAEmitidoLimpiaLaLigaduraYNoEscribeMovimientosDeStock()
    {
        var ctx = await PrepararAsync(nameof(AnularUnTxrDevuelveSusRemitosAEmitidoLimpiaLaLigaduraYNoEscribeMovimientosDeStock));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Anular 1", 30m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Anular 2", 30m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 10m);

        var remito1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 1m));
        var remito2 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 1m));

        var facturado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion",
            SolicitudFacturacion(ctx, [remito1.Id, remito2.Id], remito1.Total + remito2.Total));
        Assert.Equal(HttpStatusCode.Created, facturado.StatusCode);
        var txr = (await facturado.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientosAntes = await dbAntes.MovimientosStock.CountAsync();

        var anulado = await ctx.Admin.PostAsync($"/api/ventas/{txr.Id}/anulacion", null);
        var cuerpoAnulado = await anulado.Content.ReadAsStringAsync();
        Assert.True(anulado.StatusCode == HttpStatusCode.OK, cuerpoAnulado);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r1 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito1.Id);
        var r2 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito2.Id);

        Assert.Equal(EstadoRemito.Emitido, r1.Estado);
        Assert.Equal(EstadoRemito.Emitido, r2.Estado);
        Assert.Null(r1.IdComprobanteVenta);
        Assert.Null(r2.IdComprobanteVenta);

        // Mutation target 52 (mitad "cero movimientos_stock" en la anulación): el read set de la
        // reversa de stock de ServicioDeVentas.EjecutarAnulacionAsync es VACÍO por construcción
        // (un TXR nunca tuvo items ⇒ nunca tuvo movimientos motivo='venta') — la trampa de doble
        // decremento y el stock fantasma quedan estructuralmente inalcanzables.
        var movimientosDespues = await db.MovimientosStock.CountAsync();
        Assert.Equal(movimientosAntes, movimientosDespues);
    }

    /// <summary>[OD8/T3, discriminant test] tasks.md decisión 6: un TXR anulado prueba AMBAS
    /// mitades de la composición JUNTAS, en la misma transacción — (a) cero
    /// <c>movimientos_stock</c> creados Y (b) el saldo de CC revertido por el importe EXACTO
    /// original. Ninguna mitad sola alcanza (stage-16-slice-3, "las composiciones se
    /// prueban").</summary>
    [Fact]
    public async Task LaAnulacionDeUnTxrConCuentaCorrienteOriginalRevierteAmbasMitadesJuntasEnUnaSolaTransaccion()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeUnTxrConCuentaCorrienteOriginalRevierteAmbasMitadesJuntasEnUnaSolaTransaccion));
        // mutation-proof-tests regla 11: deuda previa DISCRIMINANTE — nunca un cliente fresco
        // (saldo 0), donde saldo_resultante == importe por coincidencia aritmética.
        var idCliente = await SembrarClienteAsync(ctx, "Cliente TXR CC Discriminante", creditoIlimitado: true);

        await using (var dbSeed = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cliente = await dbSeed.Clientes.FirstAsync(c => c.Id == idCliente);
            cliente.Saldo = 800m;
            await dbSeed.SaveChangesAsync();
        }

        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Cc Discriminante", 1500m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        var remito = await CrearYEmitirRemitoAsync(
            ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m, idCliente: idCliente));

        var facturado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion",
            SolicitudFacturacion(ctx, [remito.Id], remito.Total, ctx.IdMedioCuentaCorriente));
        var cuerpoFacturado = await facturado.Content.ReadAsStringAsync();
        Assert.True(facturado.StatusCode == HttpStatusCode.Created, cuerpoFacturado);
        var txr = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpoFacturado, OpcionesJson)!;

        await using var dbTrasFacturar = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldoTrasFacturar = await dbTrasFacturar.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(800m + remito.Total, saldoTrasFacturar); // 800 + 1500 = 2300, discriminante (nunca 0 + 1500).

        var movimientosStockAntes = await dbTrasFacturar.MovimientosStock.CountAsync();

        var anulado = await ctx.Admin.PostAsync($"/api/ventas/{txr.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // Mitad (a): cero movimientos_stock — la misma transacción de anulación.
        var movimientosStockDespues = await db.MovimientosStock.CountAsync();
        Assert.Equal(movimientosStockAntes, movimientosStockDespues);

        // Mitad (b): el saldo vuelve EXACTO a la deuda previa — ni el importe original (1500,
        // arithmetic-coincidence si el cliente fuera fresco) ni cualquier otro valor.
        var saldoFinal = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(800m, saldoFinal);

        var contramovimiento = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == txr.Id && m.Tipo == TipoMovimientoCc.Ajuste)
            .SingleAsync();
        Assert.Equal(-remito.Total, contramovimiento.Importe);
        Assert.Equal(800m, contramovimiento.SaldoResultante);

        var r = await db.Remitos.AsNoTracking().FirstAsync(x => x.Id == remito.Id);
        Assert.Equal(EstadoRemito.Emitido, r.Estado);
        Assert.Null(r.IdComprobanteVenta);
    }

    // =============================================================================================
    // task 6.22: ck_remitos_facturacion — la mutación real de DesligarAsync (limpiar solo UNA de
    // las dos columnas) tira 23514. Corrido contra un remito REALMENTE facturado (nunca un INSERT
    // sintético) — simula exactamente lo que un DesligarAsync mal escrito produciría.
    // =============================================================================================

    [Fact]
    public async Task UnUpdateQueLimpiaSoloUnaDeLasDosColumnasDeLaLigaduraViolaCkRemitosFacturacion()
    {
        var ctx = await PrepararAsync(nameof(UnUpdateQueLimpiaSoloUnaDeLasDosColumnasDeLaLigaduraViolaCkRemitosFacturacion));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Txr Check Desligue 1", 20m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Txr Check Desligue 2", 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo1, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo2, 10m);

        var remito1 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo1, 1m));
        var remito2 = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo2, 1m));

        var facturado1 = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito1.Id], remito1.Total));
        Assert.Equal(HttpStatusCode.Created, facturado1.StatusCode);
        var facturado2 = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito2.Id], remito2.Total));
        Assert.Equal(HttpStatusCode.Created, facturado2.StatusCode);

        // Dirección 1: limpia SOLO `estado` (vuelve a 'emitido'), deja id_comprobante_venta ligado.
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "UPDATE remitos SET estado = 'emitido'::estado_remito WHERE id_remito = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = remito1.Id });

            var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23514", excepcion.SqlState);
            Assert.Equal("ck_remitos_facturacion", excepcion.ConstraintName);
        }

        // Dirección 2: limpia SOLO id_comprobante_venta, deja estado='facturado'.
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "UPDATE remitos SET id_comprobante_venta = NULL WHERE id_remito = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = remito2.Id });

            var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23514", excepcion.SqlState);
            Assert.Equal("ck_remitos_facturacion", excepcion.ConstraintName);
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r1 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito1.Id);
        var r2 = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == remito2.Id);
        Assert.Equal(EstadoRemito.Facturado, r1.Estado); // el UPDATE fallido no dejó rastro
        Assert.Equal(EstadoRemito.Facturado, r2.Estado);
        Assert.NotNull(r1.IdComprobanteVenta);
        Assert.NotNull(r2.IdComprobanteVenta);
    }

    // =============================================================================================
    // task 6.23: anular-TXR × facturar — sin ciclo (T10): el desligue de un TXR (comprobantes_venta
    // → remitos) nunca contiende por el MISMO recurso que un facturar sobre esos remitos podría
    // tomar en orden inverso, porque facturar nunca toma comprobantes_venta como lock (el INSERT
    // no es una posición del orden). Ambos completan en tiempo acotado, sin deadlock.
    // =============================================================================================

    [Fact]
    public async Task AnularUnTxrXFacturarLosMismosRemitosNoDeadlockeaYAmbosResuelvenEnTiempoAcotado()
    {
        var ctx = await PrepararAsync(nameof(AnularUnTxrXFacturarLosMismosRemitosNoDeadlockeaYAmbosResuelvenEnTiempoAcotado));
        var idArticulo = await SembrarArticuloAsync(ctx, "Txr Anular Vs Facturar", 25m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        var remito = await CrearYEmitirRemitoAsync(ctx.Admin, SolicitudRemitoSimple(ctx, idArticulo, 1m));
        var facturado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));
        Assert.Equal(HttpStatusCode.Created, facturado.StatusCode);
        var txr = (await facturado.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteAnular = factory.CreateClient();
        var login = await clienteAnular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Anular-TXR pausado tras abrir su transacción — el remito sigue 'facturado' en este
        // instante, así que un facturar CONCURRENTE sobre el mismo remito lo ve todavía ligado
        // (pre-chequeo, sin lock) y se rechaza limpio, sin nunca intentar tomar un lock que
        // colisione con el de la anulación — no hay ventana de deadlock por construcción (T10).
        var tareaAnular = clienteAnular.PostAsync($"/api/ventas/{txr.Id}/anulacion", null);

        await transaccionIniciada.Task;

        var tareaFacturarConcurrente = ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));

        var respuestaFacturarConcurrente = await tareaFacturarConcurrente.WaitAsync(TimeSpan.FromSeconds(15));
        var cuerpoFacturarConcurrente = await respuestaFacturarConcurrente.Content.ReadAsStringAsync();

        puedeContinuar.TrySetResult();
        var respuestaAnular = await tareaAnular.WaitAsync(TimeSpan.FromSeconds(15));
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();

        // Ninguna de las dos requests colgó (bounded timeout arriba, sin 40P01) — el facturar
        // concurrente ve el remito todavía facturado (pre-chequeo sin lock) y se rechaza; el
        // anular, sin contención, completa limpio.
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.OK, cuerpoAnular);
        Assert.Equal(HttpStatusCode.Conflict, respuestaFacturarConcurrente.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoFacturarConcurrente, OpcionesJson);
        Assert.Equal("remito_no_facturable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var r = await db.Remitos.AsNoTracking().FirstAsync(x => x.Id == remito.Id);
        Assert.Equal(EstadoRemito.Emitido, r.Estado);
        Assert.Null(r.IdComprobanteVenta);

        // Un facturar SIGUIENTE (sin contención) recién ahora sí lo consolida — confirma que la
        // anulación dejó el remito genuinamente libre.
        var facturarSiguiente = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos/facturacion", SolicitudFacturacion(ctx, [remito.Id], remito.Total));
        Assert.Equal(HttpStatusCode.Created, facturarSiguiente.StatusCode);
    }
}
