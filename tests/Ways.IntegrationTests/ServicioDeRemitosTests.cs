using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
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
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 5 (tasks 5.1-5.22; design: Transactions — "EMITIR
/// REMITO"/"ANULAR REMITO"; mutation targets 40-47). <see cref="ServicioDeRemitos.EmitirAsync"/> es
/// el CUARTO write site de stock, implementado independiente de <c>ServicioDeVentas</c> (design
/// decisión 8) — este archivo prueba su propio orden de lock, su propia paridad FEFO contra el
/// checkout, y su propia reversa exacta desde el ledger (nunca re-derivada de <c>items_remito</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeRemitosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdCliente, int IdMedioEfectivo, int IdUsuarioAdmin, int IdPuntoVenta2,
        string MailAdmin, string PasswordAdmin);

    private async Task<Contexto> PrepararAsync(string nombre, bool conTurnoAbierto = false, bool conLotesHabilitado = false)
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Rem-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista de Remitos", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 2000 + Random.Shared.Next(1, 100_000), Nombre = $"{nombre}-cliente",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 0,
            CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = $"{nombre}-PV2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        if (conLotesHabilitado)
        {
            db.Parametros.Add(new Parametro
            {
                IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
                Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

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
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, cliente.Id, idMedioEfectivo, resultado.IdUsuarioAdmin, puntoVenta2.Id, mailAdmin,
            resultado.PasswordTemporal);
    }

    private async Task<int> SembrarArticuloAsync(
        Contexto ctx, string nombre, decimal precio, bool esProducto = true, bool controlaLote = false,
        decimal? costoNominal = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = esProducto, ControlaLote = controlaLote, CostoNominal = costoNominal,
            CreatedAt = ahora, UpdatedAt = ahora
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

    private async Task<int> SembrarLoteAsync(Contexto ctx, int idArticulo, string codigo, DateOnly? fechaVencimiento, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();

        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = lote.Id, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();

        return lote.Id;
    }

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock { IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeRemito SolicitudSimple(Contexto ctx, int idArticulo, decimal cantidad = 2m, int? idLote = null) =>
        new(ctx.IdPuntoVenta, ctx.IdCliente, "Calle Falsa 123", "obs", [new LineaDeRemito(idArticulo, cantidad, idLote)]);

    private static SolicitudDeRemito SolicitudSinItems(Contexto ctx) =>
        new(ctx.IdPuntoVenta, ctx.IdCliente, null, null, []);

    private static async Task<RemitoDetalle> CrearBorradorAsync(HttpClient cliente, SolicitudDeRemito solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/remitos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<RemitoDetalle>(cuerpo, OpcionesJson)!;
    }

    private static SolicitudDeVenta SolicitudDeVentaSimple(Contexto ctx, int idArticulo, decimal cantidad, int? idLote) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, cantidad * 1000m, null, 0m)],
            null, null);

    private static async Task<ComprobanteEmitido> EmitirVentaAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    // ---- task 5.2: crear borrador, precio resuelto al guardar --------------------------------------

    [Fact]
    public async Task UnBorradorSinItemsPersisteConNumeroYFechaSalidaNulos()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorSinItemsPersisteConNumeroYFechaSalidaNulos));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        Assert.Null(creado.Numero);
        Assert.Null(creado.NumeroFormateado);
        Assert.Null(creado.FechaSalida);
        Assert.Empty(creado.Items);
        Assert.Equal(EstadoRemito.Borrador, creado.Estado);
    }

    [Fact]
    public async Task UnBorradorConItemsResuelveElPrecioVigenteAlGuardar()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorConItemsResuelveElPrecioVigenteAlGuardar));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Articulo 1", 150m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 3m));

        var item = Assert.Single(creado.Items);
        Assert.Equal(150m, item.PrecioUnitario);
        Assert.Equal(450m, item.Total);
        Assert.Equal(450m, creado.Total);
        Assert.Null(item.CostoUnitario);
        Assert.False(item.CostoEsEstimado);
        Assert.Null(item.IdLote);
    }

    // ---- task 5.2: replace-set completo, hermano intacto (rule 12c) --------------------------------

    [Fact]
    public async Task ElReplaceSetReemplazaLosItemsCompletosSinTocarUnRemitoHermano()
    {
        var ctx = await PrepararAsync(nameof(ElReplaceSetReemplazaLosItemsCompletosSinTocarUnRemitoHermano));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Rem Hermano A", 100m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Rem Hermano B", 200m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo1, 1m));
        var hermano = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo1, 5m));
        Assert.Single(creado.Items);
        Assert.Single(hermano.Items);

        var reemplazo = new SolicitudDeRemito(
            ctx.IdPuntoVenta, ctx.IdCliente, "nueva direccion", null,
            [new LineaDeRemito(idArticulo2, 4m, null)]);
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/remitos/{creado.Id}", reemplazo);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var actualizado = (await respuesta.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var itemActualizado = Assert.Single(actualizado.Items);
        Assert.Equal(idArticulo2, itemActualizado.IdArticulo);
        Assert.Equal("nueva direccion", actualizado.DireccionEntrega);

        var detalleHermano = await ctx.Admin.GetFromJsonAsync<RemitoDetalle>($"/api/remitos/{hermano.Id}", OpcionesJson);
        var itemHermano = Assert.Single(detalleHermano!.Items);
        Assert.Equal(idArticulo1, itemHermano.IdArticulo);
        Assert.Equal(5m, itemHermano.Cantidad);
    }

    [Fact]
    public async Task EditarUnRemitoNoBorradorEsRechazado409()
    {
        var ctx = await PrepararAsync(nameof(EditarUnRemitoNoBorradorEsRechazado409));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem No Editable", 50m);
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 1m));

        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);

        var respuestaEdicion = await ctx.Admin.PutAsJsonAsync($"/api/remitos/{creado.Id}", SolicitudSimple(ctx, idArticulo, 2m));
        Assert.Equal(HttpStatusCode.Conflict, respuestaEdicion.StatusCode);
    }

    // ---- rule 12b: todo campo posicional del detalle se lee de vuelta con valores distinguibles ----

    [Fact]
    public async Task TodoCampoPosicionalDelDetalleSeLeeDeVueltaConValoresDistinguibles()
    {
        var ctx = await PrepararAsync(nameof(TodoCampoPosicionalDelDetalleSeLeeDeVueltaConValoresDistinguibles));
        var idArticulo1 = await SembrarArticuloAsync(ctx, "Rem Distinto A", 111m);
        var idArticulo2 = await SembrarArticuloAsync(ctx, "Rem Distinto B", 222m);

        var solicitud = new SolicitudDeRemito(
            ctx.IdPuntoVenta, ctx.IdCliente, "direccion distinguible", "obs distinguible",
            [new LineaDeRemito(idArticulo1, 1m, null), new LineaDeRemito(idArticulo2, 3m, null)]);
        var creado = await CrearBorradorAsync(ctx.Admin, solicitud);

        Assert.Equal(2, creado.Items.Count);
        var item1 = creado.Items.Single(i => i.IdArticulo == idArticulo1);
        var item2 = creado.Items.Single(i => i.IdArticulo == idArticulo2);

        // Subtotal == 111 + 666 == 777, sin descuento — pero DescuentoTotal/Subtotal/Total siguen
        // siendo campos DISTINTOS entre sí (0 != 777 != 777... el par Subtotal/Total coincide sin
        // descuento, así que el valor discriminante real acá es item1.Total != item2.Total y
        // item1.Orden != item2.Orden, cubriendo la clase de mutante "swap de dos campos del
        // mismo item").
        Assert.NotEqual(item1.Total, item2.Total);
        Assert.NotEqual(item1.Orden, item2.Orden);
        Assert.NotEqual(item1.PrecioUnitario, item2.PrecioUnitario);
        Assert.Equal("direccion distinguible", creado.DireccionEntrega);
        Assert.Equal("obs distinguible", creado.Observaciones);
        Assert.Equal(ctx.IdPuntoVenta, creado.IdPuntoVenta);
        Assert.Equal(ctx.IdCliente, creado.IdCliente);

        var listado = await ctx.Admin.GetFromJsonAsync<PaginaDeRemitos>(
            $"/api/remitos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        var fila = listado!.Items.Single(r => r.Id == creado.Id);
        Assert.Equal(creado.Total, fila.Total);
    }

    // ---- task 5.3-5.6: emitir — el cuarto write site (mutation targets 40-42) ----------------------

    [Fact]
    public async Task EmitirMueveStockConElMotivoDelRemito()
    {
        var ctx = await PrepararAsync(nameof(EmitirMueveStockConElMotivoDelRemito));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Motivo", 80m, costoNominal: 55m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 3m));

        var respuesta = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var emitido = JsonSerializer.Deserialize<RemitoDetalle>(cuerpo, OpcionesJson)!;

        Assert.Equal(EstadoRemito.Emitido, emitido.Estado);
        Assert.NotNull(emitido.Numero);
        Assert.NotNull(emitido.FechaSalida);
        // task 5.5: costo_unitario congelado de HOY (articulo.CostoNominal), no del momento del
        // borrador — costo_es_estimado siempre false, mismo criterio que ServicioDeVentas.
        Assert.Equal(55m, emitido.Items.Single().CostoUnitario);
        Assert.False(emitido.Items.Single().CostoEsEstimado);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdRemito == creado.Id);
        Assert.Equal(-3m, movimiento.Cantidad);
        Assert.Equal(MotivoStock.Remito, movimiento.Motivo);
        Assert.Equal(creado.Id, movimiento.IdRemito);
        Assert.Null(movimiento.IdComprobanteVenta);

        var stock = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(7m, stock.Cantidad);
    }

    [Fact]
    public async Task EmitirUnaLineaLoteEfectivaCongelaFefo()
    {
        var ctx = await PrepararAsync(nameof(EmitirUnaLineaLoteEfectivaCongelaFefo), conLotesHabilitado: true);
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Lote", 60m, controlaLote: true);
        var idLoteViejo = await SembrarLoteAsync(ctx, idArticulo, "L-VIEJO", new DateOnly(2099, 1, 1), 5m);
        await SembrarLoteAsync(ctx, idArticulo, "L-NUEVO", new DateOnly(2099, 6, 1), 5m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 2m));

        var respuesta = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var emitido = JsonSerializer.Deserialize<RemitoDetalle>(cuerpo, OpcionesJson)!;

        // FEFO: el lote con vencimiento MÁS CERCANO ("viejo") es el elegido.
        Assert.Equal(idLoteViejo, emitido.Items.Single().IdLote);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stockLote = await db.StockLotes.SingleAsync(s => s.IdLote == idLoteViejo);
        Assert.Equal(3m, stockLote.Cantidad);
        var itemPersistido = await db.ItemsRemito.SingleAsync(i => i.IdRemito == creado.Id);
        Assert.Equal(idLoteViejo, itemPersistido.IdLote);
    }

    [Fact]
    public async Task EmitirUnRemitoVacioEsRechazado400()
    {
        var ctx = await PrepararAsync(nameof(EmitirUnRemitoVacioEsRechazado400));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        var respuesta = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("remito_sin_items", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task EmitirUnaLineaDeServicioEsRechazada400()
    {
        var ctx = await PrepararAsync(nameof(EmitirUnaLineaDeServicioEsRechazada400));
        var idServicio = await SembrarArticuloAsync(ctx, "Rem Servicio", 40m, esProducto: false);
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idServicio, 1m));

        var respuesta = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("articulo_no_es_producto", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdRemito == creado.Id));
    }

    // ---- task 5.18: doble emitir (mutation target 44) ----------------------------------------------

    [Fact]
    public async Task DobleEmitirEsRechazado409()
    {
        var ctx = await PrepararAsync(nameof(DobleEmitirEsRechazado409));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Doble Emitir", 70m);
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 1m));

        var primero = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, primero.StatusCode);

        var segundo = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("remito_ya_emitido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdRemito == creado.Id));
    }

    // ---- mutation target 44 (mitad id_punto_venta): la carrera del relink de PV --------------------

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

    /// <summary>Mutation target 44 (mitad `id_punto_venta`, mismo patrón que
    /// <c>ServicioDePresupuestosTests.UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja</c>):
    /// un `PUT` que mueve el remito del PV 1 al PV 2 gana la carrera y COMMITEA DESPUÉS de que el
    /// número ya fue dibujado (serie del PV 1) pero ANTES de que el `UPDATE` final de `emitir`
    /// corra — pausado justo tras `BeginTransactionAsync` de `EjecutarEmisionAsync`. El `WHERE
    /// id_punto_venta = $pv` (pineado al PV 1, capturado en la pre-lectura) no matchea la fila ya
    /// movida al PV 2 ⇒ 0 filas ⇒ 409, el número dibujado para el PV 1 queda quemado sin aparecer
    /// en ningún remito.</summary>
    [Fact]
    public async Task UnPutQueMuevePuntoDeVentaConcurrenteConEmitirReclasificaA409YElNumeroQuedaEnLaSerieVieja()
    {
        var ctx = await PrepararAsync(nameof(UnPutQueMuevePuntoDeVentaConcurrenteConEmitirReclasificaA409YElNumeroQuedaEnLaSerieVieja));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Relink PV", 33m);
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 1m));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteEmitir = factory.CreateClient();
        var login = await clienteEmitir.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaEmitir = clienteEmitir.PostAsync($"/api/remitos/{creado.Id}/emitir", null);

        await transaccionIniciada.Task;

        var solicitudRelink = new SolicitudDeRemito(
            ctx.IdPuntoVenta2, ctx.IdCliente, null, null, [new LineaDeRemito(idArticulo, 1m, null)]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/remitos/{creado.Id}", solicitudRelink);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);

        puedeContinuar.TrySetResult();

        var respuestaEmitir = await tareaEmitir;
        var cuerpoEmitir = await respuestaEmitir.Content.ReadAsStringAsync();
        Assert.True(respuestaEmitir.StatusCode == HttpStatusCode.Conflict, cuerpoEmitir);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoEmitir, OpcionesJson);
        Assert.Equal("remito_ya_emitido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.Remitos.FirstAsync(r => r.Id == creado.Id);
        Assert.Equal(EstadoRemito.Borrador, actual.Estado);
        Assert.Null(actual.Numero);
        Assert.Equal(ctx.IdPuntoVenta2, actual.IdPuntoVenta);

        // El número quemado (serie del PV 1) nunca aparece en ningún remito: un emitir siguiente
        // del PV 1 salta directo al 2.
        var siguientePv1 = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 1m));
        var siguienteEmitirPv1 = await ctx.Admin.PostAsync($"/api/remitos/{siguientePv1.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, siguienteEmitirPv1.StatusCode);
        var siguienteEmitidoPv1 = (await siguienteEmitirPv1.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.Equal(2, siguienteEmitidoPv1.Numero);
    }

    // ---- task 5.8-5.9: anular (mutation targets 45-46) ----------------------------------------------

    [Fact]
    public async Task AnularUnRemitoEmitidoReviertelosMovimientosOriginales()
    {
        var ctx = await PrepararAsync(nameof(AnularUnRemitoEmitidoReviertelosMovimientosOriginales));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Anular", 90m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 4m));
        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);

        var anulado = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null);
        var cuerpo = await anulado.Content.ReadAsStringAsync();
        Assert.True(anulado.StatusCode == HttpStatusCode.OK, cuerpo);
        var detalle = JsonSerializer.Deserialize<RemitoDetalle>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoRemito.Anulado, detalle.Estado);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var inversa = await db.MovimientosStock.SingleAsync(m => m.IdRemito == creado.Id && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal(4m, inversa.Cantidad);

        var stock = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(20m, stock.Cantidad);
    }

    [Fact]
    public async Task AnularUnRemitoYaAnuladoEsRechazado409YNoEscribeSegundaReversa()
    {
        var ctx = await PrepararAsync(nameof(AnularUnRemitoYaAnuladoEsRechazado409YNoEscribeSegundaReversa));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Doble Anular", 55m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 1m));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null)).StatusCode);

        var segundaAnulacion = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Conflict, segundaAnulacion.StatusCode);
        var problema = await segundaAnulacion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("remito_ya_anulado", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdRemito == creado.Id && m.Motivo == MotivoStock.Anulacion));
    }

    [Fact]
    public async Task AnularUnRemitoFacturadoEsRechazado409()
    {
        var ctx = await PrepararAsync(nameof(AnularUnRemitoFacturadoEsRechazado409), conTurnoAbierto: true);
        var idArticuloRemito = await SembrarArticuloAsync(ctx, "Rem Facturado", 65m);
        var idArticuloVenta = await SembrarArticuloAsync(ctx, "Rem Facturado Venta", 65m);
        await SembrarStockAgregadoAsync(ctx, idArticuloRemito, 5m);
        await SembrarStockAgregadoAsync(ctx, idArticuloVenta, 5m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticuloRemito, 1m));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null)).StatusCode);

        // Fixture directo (sin Slice 6 — ServicioDeFacturacionDeRemitos todavía no existe): emite
        // una venta cualquiera para tener un id_comprobante_venta real y liga el remito a mano, vía
        // conexión owner (bypassa RLS/políticas de escritura de la app, no las de Postgres).
        var venta = await EmitirVentaAsync(ctx, SolicitudDeVentaSimple(ctx, idArticuloVenta, 1m, null));
        await using (var cruda = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "UPDATE remitos SET estado = 'facturado'::estado_remito, id_comprobante_venta = $1 WHERE id_remito = $2";
            comando.Parameters.AddWithValue(venta.Id);
            comando.Parameters.AddWithValue(creado.Id);
            await comando.ExecuteNonQueryAsync();
        }

        var anulado = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Conflict, anulado.StatusCode);
        var problema = await anulado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("remito_facturado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 5.16: la anulación lee los movimientos ORIGINALES, nunca re-deriva de items_remito ---

    [Fact]
    public async Task LaAnulacionLeeLosMovimientosOriginalesNoLosRederivaDeItems()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionLeeLosMovimientosOriginalesNoLosRederivaDeItems));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Original", 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 50m);

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 7m));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null)).StatusCode);

        // Diverge deliberadamente items_remito.cantidad DEL LEDGER — si AnularAsync re-derivara la
        // reversa desde items_remito (en vez de leer movimientos_stock), la inversa saldría con
        // esta cantidad mutada (99), no con la original (7) que quedó grabada en el ledger.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var item = await db.ItemsRemito.SingleAsync(i => i.IdRemito == creado.Id);
            item.Cantidad = 99m;
            await db.SaveChangesAsync();
        }

        var anulado = await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);

        await using var dbAssert = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var inversa = await dbAssert.MovimientosStock.SingleAsync(m => m.IdRemito == creado.Id && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal(7m, inversa.Cantidad);

        var stock = await dbAssert.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(50m, stock.Cantidad);
    }

    // ---- task 5.12/5.13: rendezvous forzado, sin deadlock, misma clave ascendente -------------------

    [Fact]
    public async Task RemitirYCheckoutSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan()
    {
        var ctx = await PrepararAsync(
            nameof(RemitirYCheckoutSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan),
            conTurnoAbierto: true, conLotesHabilitado: true);
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Rendezvous Checkout", 45m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-RDV-1", new DateOnly(2099, 1, 1), 100m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 100m);

        var remito = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 2m, idLote));

        var tareaRemito = ctx.Admin.PostAsync($"/api/remitos/{remito.Id}/emitir", null);
        var tareaVenta = ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudDeVentaSimple(ctx, idArticulo, 3m, idLote));

        var respuestas = await Task.WhenAll(tareaRemito, tareaVenta);

        Assert.Equal(HttpStatusCode.OK, respuestas[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, respuestas[1].StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stockLote = await db.StockLotes.SingleAsync(s => s.IdLote == idLote);
        Assert.Equal(95m, stockLote.Cantidad);

        var stockAgregado = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.Equal(95m, stockAgregado.Cantidad);
    }

    [Fact]
    public async Task RemitirYRemitirSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan()
    {
        var ctx = await PrepararAsync(
            nameof(RemitirYRemitirSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan), conLotesHabilitado: true);
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Rendezvous Rem", 33m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-RDV-2", new DateOnly(2099, 1, 1), 100m);

        var remitoA = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 2m, idLote));
        var remitoB = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 5m, idLote));

        var tareaA = ctx.Admin.PostAsync($"/api/remitos/{remitoA.Id}/emitir", null);
        var tareaB = ctx.Admin.PostAsync($"/api/remitos/{remitoB.Id}/emitir", null);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stockLote = await db.StockLotes.SingleAsync(s => s.IdLote == idLote);
        Assert.Equal(93m, stockLote.Cantidad);
        Assert.Equal(2, await db.MovimientosStock.CountAsync(m => m.IdLote == idLote && m.Motivo == MotivoStock.Remito));
    }

    // ---- task 5.14: paridad FEFO entre write site 1 (checkout) y write site 4 (remito) -------------

    [Fact]
    public async Task LaParidadFefoEligeElMismoLoteEnElCheckoutYEnElRemito()
    {
        var ctx = await PrepararAsync(nameof(LaParidadFefoEligeElMismoLoteEnElCheckoutYEnElRemito), conTurnoAbierto: true, conLotesHabilitado: true);

        var idArticuloVenta = await SembrarArticuloAsync(ctx, "Fefo Venta", 10m, controlaLote: true);
        var idLoteViejoVenta = await SembrarLoteAsync(ctx, idArticuloVenta, "V-VIEJO", new DateOnly(2099, 1, 1), 5m);
        await SembrarLoteAsync(ctx, idArticuloVenta, "V-NUEVO", new DateOnly(2099, 6, 1), 5m);

        var idArticuloRemito = await SembrarArticuloAsync(ctx, "Fefo Remito", 10m, controlaLote: true);
        var idLoteViejoRemito = await SembrarLoteAsync(ctx, idArticuloRemito, "R-VIEJO", new DateOnly(2099, 1, 1), 5m);
        await SembrarLoteAsync(ctx, idArticuloRemito, "R-NUEVO", new DateOnly(2099, 6, 1), 5m);

        var venta = await EmitirVentaAsync(ctx, SolicitudDeVentaSimple(ctx, idArticuloVenta, 1m, null));
        var idLoteElegidoPorVenta = venta.Items.Single().IdLote;
        Assert.Equal(idLoteViejoVenta, idLoteElegidoPorVenta);

        var remito = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticuloRemito, 1m));
        var respuesta = await ctx.Admin.PostAsync($"/api/remitos/{remito.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        Assert.Equal(idLoteViejoRemito, emitido.Items.Single().IdLote);
    }

    // ---- task 5.15: consistencia de nueve motivos ---------------------------------------------------

    [Fact]
    public async Task LaConsistenciaDeNueveMotivosSeMantieneTrasEmitirYAnularUnRemito()
    {
        var ctx = await PrepararAsync(nameof(LaConsistenciaDeNueveMotivosSeMantieneTrasEmitirYAnularUnRemito));
        var idArticulo = await SembrarArticuloAsync(ctx, "Rem Nueve Motivos", 15m);

        // Baseline vía un movimiento real (motivo = ajuste), NUNCA un `Stock.Add` desconectado del
        // ledger — la invariante `stock.cantidad == SUM(movimientos_stock.cantidad)` (stock/
        // spec.md:166-171, restated over los NUEVE motivos) solo es honesta si el punto de partida
        // TAMBIÉN es un movimiento, o el propio baseline la rompería por construcción.
        await using (var dbSeed = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            dbSeed.Stock.Add(new Stock { IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = 30m });
            dbSeed.MovimientosStock.Add(new MovimientoStock
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = 30m,
                Motivo = MotivoStock.Ajuste, IdEmpleado = ctx.IdUsuarioAdmin, CreadoEl = DateTimeOffset.UtcNow
            });
            await dbSeed.SaveChangesAsync();
        }

        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idArticulo, 6m));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/emitir", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/remitos/{creado.Id}/anular", null)).StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stock = await db.Stock.SingleAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        var sumaMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(m => m.Cantidad);

        Assert.Equal(sumaMovimientos, stock.Cantidad);
        Assert.Equal(30m, stock.Cantidad);
    }

    // ---- task 5.21: GATE GUARD ----------------------------------------------------------------------

    /// <summary>Gate guard (task 5.21): esta slice no agrega DDL — el modelo EF sigue coincidiendo
    /// exactamente con la migración de Slice 4 (RemitosEtapa17).</summary>
    [Fact]
    public async Task NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice4()
    {
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var hayPendientes = db.Database.HasPendingModelChanges();
        Assert.False(hayPendientes);
    }
}
