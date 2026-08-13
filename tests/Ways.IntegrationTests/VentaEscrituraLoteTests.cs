using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
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
/// stage-12-lotes-vencimientos, Slice 8 (tasks 8.1-8.8): la escritura per-lot de
/// <c>ServicioDeVentas</c> punta a punta — el orden pinneado (<c>id_articulo, id_lote NULLS
/// FIRST</c>) dentro de la transacción, el snapshot congelado en <c>items_comprobante_venta.id_lote</c>
/// y la anulación exacta que revierte el lote sin re-derivarlo. A diferencia de
/// <see cref="PlanDeVentaFefoTests"/> (Slice 7, solo la fase de DECISIÓN), acá cada prueba asserta
/// el LEDGER (<c>movimientos_stock</c>/<c>stock_lotes</c>) además de la respuesta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaEscrituraLoteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas de vencimiento FIJAS y lejanas — independientes del reloj de la
    // corrida.
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);
    private static readonly DateOnly VencimientoLejanoFuturoAlterno = new(2098, 6, 30);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdCliente);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Escritura-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Escritura", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente Escritura",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 1_000_000m,
            CreditoIlimitado = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, cliente.Id);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, decimal precio, bool controlaLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = controlaLote, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarLoteAsync(Contexto ctx, int idArticulo, string codigo, DateOnly? fechaVencimiento)
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

        return lote.Id;
    }

    private async Task SembrarStockLoteAsync(Contexto ctx, int idArticulo, int idLote, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.StockLotes.Add(new StockLote
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    /// <summary>El agregado (<c>stock</c>) es un caché INDEPENDIENTE de <c>stock_lotes</c> — el
    /// upsert de <c>UpsertStockAsync</c> lo crea desde cero (delta puro) si no existe ninguna fila
    /// previa, así que un test de invariante que arranca sin sembrar acá vería el agregado partir
    /// de 0, no del total físico esperado.</summary>
    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta SolicitudSimple(Contexto ctx, int idArticulo, decimal cantidad, int? idLote) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, cantidad * 100m, null, 0m)],
            null, null);

    private static async Task<ComprobanteEmitido> EmitirAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<ComprobanteEmitido> AnularAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    // ---- task 8.4: invariante — stock.cantidad y stock_lotes.cantidad correctos tras venta +
    // anulación ------------------------------------------------------------------------------------

    /// <summary>spec lotes-y-vencimientos: "Stock Lotes Balance And Its Two Invariants" — pierna de
    /// venta+anulación (la pierna de compra ya la prueba <c>ComprasRecepcionDeLotesTests</c>, Slice
    /// 5). Tras la venta ambos cachés bajan; tras la anulación ambos vuelven exactamente al valor
    /// original.</summary>
    [Fact]
    public async Task StockYStockLotesQuedanCorrectosTrasVentaYAnulacion()
    {
        var ctx = await PrepararAsync(nameof(StockYStockLotesQuedanCorrectosTrasVentaYAnulacion));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-invariante", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-INV", VencimientoLejanoFuturo);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 4m, idLote: null));
        var item = Assert.Single(emitido.Items);
        Assert.Equal(idLote, item.IdLote);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var stock = await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
                .Select(s => s.Cantidad).FirstAsync();
            Assert.Equal(6m, stock);

            var stockLote = await db.StockLotes
                .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
                .Select(sl => sl.Cantidad).FirstAsync();
            Assert.Equal(6m, stockLote);
        }

        await AnularAsync(ctx, emitido.Id);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var stock = await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
                .Select(s => s.Cantidad).FirstAsync();
            Assert.Equal(10m, stock);

            var stockLote = await db.StockLotes
                .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
                .Select(sl => sl.Cantidad).FirstAsync();
            Assert.Equal(10m, stockLote);
        }
    }

    // ---- task 8.5: mutation target — el snapshot congelado (ItemComprobanteVenta.IdLote) ----------

    /// <summary>Mutation target (design: "Item snapshot"; spec comprobantes-venta: "A lot-effective
    /// line freezes its resolved lot onto the snapshot", "Anulación of a lot-bearing sale reverses
    /// the exact lot"; mutation-proof-tests). Asserta DOS cosas en la MISMA corrida: (1) el
    /// snapshot persistido en <c>items_comprobante_venta.id_lote</c> — la parte que la mutación
    /// (<c>ItemComprobanteVenta.IdLote = i.IdLote</c> reemplazado por <c>null</c>) rompe
    /// directamente; y (2) la reversa exacta de la anulación (<c>movimientos_stock.id_lote</c> +
    /// saldo por lote), que lee del LEDGER, no del snapshot — documentado acá para que quede claro
    /// por qué la aserción (1) es la que efectivamente mata la mutación.
    ///
    /// EVIDENCIA DE MUTACIÓN (regla permanente 6 de este apply): <c>IdLote = i.IdLote</c> en el
    /// <c>AddRange</c> de <c>EjecutarTransaccionAsync</c> reemplazado por <c>IdLote = null</c>; build,
    /// filtro <c>FullyQualifiedName~UnaVentaLoteEfectivaCongelaElSnapshotYSuAnulacionRevierteElLoteExacto</c>:
    /// RED — <c>Assert.Equal(idLote, itemPersistido.IdLote)</c> falló (<c>Expected: {idLote} /
    /// Actual: null</c>), exactamente la aserción del snapshot, mientras que la reversa de la
    /// anulación (aserciones 2-3, más abajo) siguió en verde porque lee <c>movimientos_stock</c>, no
    /// el snapshot — confirma que la mutación es invisible para cualquier prueba que solo mire el
    /// ledger. Revertido, mismo filtro: GREEN, junto con la suite completa de este archivo.</summary>
    [Fact]
    public async Task UnaVentaLoteEfectivaCongelaElSnapshotYSuAnulacionRevierteElLoteExacto()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaLoteEfectivaCongelaElSnapshotYSuAnulacionRevierteElLoteExacto));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-snapshot", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-SNAP", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 4m, idLote: null));

        // (1) El snapshot persistido — GIVEN de la spec ("a sale item persisted id_lote = 7").
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var itemPersistido = await db.ItemsComprobanteVenta.SingleAsync(i => i.IdComprobanteVenta == emitido.Id);
            Assert.Equal(idLote, itemPersistido.IdLote);
        }

        await AnularAsync(ctx, emitido.Id);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            // (2) La reversa exacta en el ledger — leída de movimientos_stock, no re-derivada.
            var movimientoAnulacion = await db.MovimientosStock
                .SingleAsync(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion);
            Assert.Equal(idLote, movimientoAnulacion.IdLote);
            Assert.Equal(4m, movimientoAnulacion.Cantidad);

            // (3) El saldo por lote, restaurado.
            var stockLote = await db.StockLotes
                .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
                .Select(sl => sl.Cantidad).FirstAsync();
            Assert.Equal(10m, stockLote);
        }
    }

    // ---- task 8.6: lock order — stock ANTES que stock_lotes para el mismo par ----------------------

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

    /// <summary>Los statements de <see cref="ServicioDeVentas"/>'s paso 5 (<c>InsertarMovimientoStockAsync</c>/
    /// <c>UpsertStockAsync</c>/<c>UpsertStockLoteAsync</c>) corren sobre un <c>DbCommand</c> crudo
    /// creado directo con <c>conexion.CreateCommand()</c> — NUNCA pasan por el pipeline de
    /// <c>DbCommandInterceptor</c> de EF Core (ese pipeline solo envuelve comandos que EF mismo arma
    /// para LINQ/SaveChanges; confirmado empíricamente, tanto para <c>ScalarExecuting</c> como para
    /// el logging nativo de Npgsql configurado vía <c>NpgsqlLoggingConfiguration.InitializeLogging</c>
    /// — este fixture ya abrió conexiones antes de que un test pueda inicializarlo, así que Npgsql
    /// cachea su logger nulo process-wide antes de que cualquier suscripción pueda engancharse). La
    /// prueba de orden acá NO intercepta texto de comando — observa el ESTADO DE LOCKS real en
    /// <c>pg_locks</c> desde una conexión separada, mientras el checkout queda deliberadamente
    /// bloqueado en <c>stock_lotes</c> por una tercera conexión que sostiene esa fila con
    /// <c>FOR UPDATE</c>: si en ese instante <c>stock</c> YA aparece <c>granted</c> para el backend
    /// del checkout, es prueba estructural de que el upsert de <c>stock</c> corrió (y tomó su lock)
    /// ANTES de que el checkout siquiera intentara <c>stock_lotes</c>.</summary>
    private async Task<(ComprobanteEmitido Emitido, bool StockYaBloqueadoMientrasEsperaStockLotes)> EmitirObservandoOrdenDeLocksAsync(
        Contexto ctx, SolicitudDeVenta solicitud, int idArticulo, int idLote)
    {
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
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<Ways.Domain.Caja.EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);
        await db.Database.OpenConnectionAsync();
        var conexionCheckout = (NpgsqlConnection)db.Database.GetDbConnection();
        var pidCheckout = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexionCheckout).ExecuteScalarAsync())!;

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var lectorDeMovimientos = new Ways.Application.Caja.LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new Ways.Application.Caja.ServicioDeTurnos(db, reloj, contexto, lectorDeMovimientos);
        var servicioDeLotes = new ServicioDeLotes(db, reloj, contexto);
        var servicioDeVentas = new ServicioDeVentas(db, reloj, contexto, servicioDeOfertas, servicioDeTurnos, servicioDeLotes);

        // Conexión que SOSTIENE el lock de stock_lotes — deliberadamente sin comitear todavía, para
        // forzar al checkout a bloquearse justo ahí y ensanchar la ventana de observación. RLS exige
        // el mismo GUC que setea InterceptorDeContextoDeTenant sobre cada conexión física (ADR-3) —
        // sin esto, la conexión cruda ve CERO filas de stock_lotes (otro tenant, invisible), el
        // SELECT ... FOR UPDATE no toma ningún lock, y el checkout nunca se bloquea.
        await using var conexionBloqueo = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionBloqueo.OpenAsync();
        await using (var comandoGuc = new NpgsqlCommand(
            "SELECT set_config('app.acceso', 'tenant', false), set_config('app.tenant_id', $1, false)",
            conexionBloqueo))
        {
            comandoGuc.Parameters.AddWithValue(ctx.IdTenant.ToString());
            await comandoGuc.ExecuteNonQueryAsync();
        }

        await using var transaccionBloqueo = await conexionBloqueo.BeginTransactionAsync();
        await using (var comandoBloqueo = new NpgsqlCommand(
            "SELECT cantidad FROM stock_lotes WHERE id_articulo = $1 AND id_punto_venta = $2 AND id_lote = $3 FOR UPDATE",
            conexionBloqueo, transaccionBloqueo))
        {
            comandoBloqueo.Parameters.AddWithValue(idArticulo);
            comandoBloqueo.Parameters.AddWithValue(ctx.IdPuntoVenta);
            comandoBloqueo.Parameters.AddWithValue(idLote);
            await comandoBloqueo.ExecuteScalarAsync();
        }

        var checkoutTask = servicioDeVentas.EmitirAsync(solicitud);

        await using var conexionPoll = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionPoll.OpenAsync();

        var observado = false;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            // Postgres representa "bloqueado esperando la fila que otra transacción tiene" como un
            // ShareLock NO otorgado sobre el transactionid de esa otra transacción (locktype
            // 'transactionid') — NUNCA como una fila `tuple` con granted=false sobre stock_lotes
            // directamente (comprobado empíricamente con un dump de pg_locks completo). El único
            // lock artificialmente disputado en este test es el de stock_lotes (sostenido por
            // conexionBloqueo), así que CUALQUIER lock no otorgado del backend del checkout es, acá,
            // ese bloqueo.
            await using var comandoPoll = new NpgsqlCommand(
                "SELECT " +
                "  bool_or(l.locktype = 'relation' AND l.relation::regclass::text = 'stock' AND l.granted) AS stock_otorgado, " +
                "  bool_or(NOT l.granted) AS esperando_algo " +
                "FROM pg_locks l WHERE l.pid = $1",
                conexionPoll);
            comandoPoll.Parameters.AddWithValue(pidCheckout);

            await using var lector = await comandoPoll.ExecuteReaderAsync();
            if (await lector.ReadAsync())
            {
                var stockOtorgado = !lector.IsDBNull(0) && lector.GetBoolean(0);
                var esperandoAlgo = !lector.IsDBNull(1) && lector.GetBoolean(1);
                if (stockOtorgado && esperandoAlgo)
                {
                    observado = true;
                    break;
                }
            }

            await Task.Delay(25);
        }

        // Libera el lock retenido — el checkout, hasta acá bloqueado, ahora puede completar.
        await transaccionBloqueo.RollbackAsync();

        var emitido = await checkoutTask;

        return (emitido, observado);
    }

    /// <summary>spec stock: "A checkout locks stock before stock_lotes for the same pair" (design
    /// decisión 1/3: <c>stock</c> es el único statement que toma el row lock que reemplaza al
    /// advisory lock; <c>stock_lotes</c> lo sigue SIEMPRE, nunca lo precede).</summary>
    [Fact]
    public async Task UnCheckoutBloqueaStockAntesQueStockLotesParaElMismoPar()
    {
        var ctx = await PrepararAsync(nameof(UnCheckoutBloqueaStockAntesQueStockLotesParaElMismoPar));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-lock-order", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-LOCK", VencimientoLejanoFuturo);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var (emitido, observado) = await EmitirObservandoOrdenDeLocksAsync(
            ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null), idArticulo, idLote);

        Assert.True(
            observado,
            "Nunca se observó al checkout con el lock de stock ya otorgado mientras esperaba stock_lotes.");
        Assert.Single(emitido.Items);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stock = await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(9m, stock);
    }

    // ---- task 8.7: mutation target (half A, deadlock) — orden ascendente por id_lote, no por
    // orden de envío -----------------------------------------------------------------------------

    /// <summary>Mutation target (design decisión 8/9; spec stock: "A checkout locks stock before
    /// stock_lotes..."; mutation-proof-tests). Dos líneas del MISMO artículo lote-efectivo, cada una
    /// con un <c>idLote</c> EXPLÍCITO distinto, enviadas en orden DESCENDENTE (lote de id mayor
    /// primero) — un <c>OrderBy(IdArticulo)</c> sin desempate por lote preserva el orden de arribo
    /// (sort estable), así que sin <c>.ThenBy(IdLote.HasValue).ThenBy(IdLote ?? 0)</c> los
    /// movimientos se escribirían en el orden de ENVÍO (lote mayor, después menor) — la "media
    /// mitad" del proof de deadlock que le toca a esta slice (la mitad conjunta checkout-vs-transfer
    /// es la task 10.12, cross-slice, ver la nota de la task 8.7 en tasks.md).
    ///
    /// EVIDENCIA DE MUTACIÓN (regla permanente 6 de este apply): borrado
    /// <c>.ThenBy(i => i.IdLote.HasValue).ThenBy(i => i.IdLote ?? 0)</c> del loop del paso 5 de
    /// <c>EjecutarTransaccionAsync</c> (queda solo <c>.OrderBy(i => i.IdArticulo)</c>); build, filtro
    /// <c>FullyQualifiedName~LosMovimientosDeDosLotesDelMismoArticuloSeEscribenEnOrdenAscendentePorIdLote</c>:
    /// RED — <c>Assert.Equal(idLoteMenor, movimientos[0].IdLote)</c> falló (<c>Expected: {idLoteMenor}
    /// / Actual: {idLoteMayor}</c>): el sort estable preservó el orden de ENVÍO (mayor primero), tal
    /// como predice el doc-comment. Revertido, mismo filtro: GREEN.
    ///
    /// Extensión (judgment-day slice 8, ronda 2, juez A MAJOR): las dos líneas también asertan el
    /// agregado <c>stock.cantidad</c> final, sembrado con un valor inicial y cantidades DISTINTAS
    /// por línea (3 vs. 5) — discriminante contra una "optimización" que agrupe por
    /// <c>id_articulo</c> y llame <c>UpsertStockAsync</c> UNA sola vez con el delta de la ÚLTIMA
    /// línea en vez de re-emitirlo por línea (design decisión 8: "re-issuing is a re-lock ... zero
    /// contention"; la alternativa agrupada es la que el design explícitamente rechaza).
    ///
    /// EVIDENCIA DE MUTACIÓN (agregado, regla permanente 6): paso 5 de <c>EjecutarTransaccionAsync</c>
    /// mutado para agrupar <c>plan.Items</c> por <c>IdArticulo</c> y llamar <c>UpsertStockAsync</c>
    /// una única vez por artículo con <c>-grupo.Last().Cantidad</c> (delta de la línea de
    /// <c>idLoteMenor</c>, la última enviada) en vez de re-emitirlo en cada iteración; build, mismo
    /// filtro: RED — <c>Assert.Equal(12m, stock)</c> falló (<c>Expected: 12 / Actual: 15</c>): el
    /// agregado solo restó 5 (delta de la última línea), no 8 (3+5, la suma de ambas). Revertido,
    /// mismo filtro: GREEN.</summary>
    [Fact]
    public async Task LosMovimientosDeDosLotesDelMismoArticuloSeEscribenEnOrdenAscendentePorIdLote()
    {
        var ctx = await PrepararAsync(nameof(LosMovimientosDeDosLotesDelMismoArticuloSeEscribenEnOrdenAscendentePorIdLote));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-multilote", 100m, controlaLote: true);
        // idLoteMenor se crea PRIMERO (id de secuencia más chico) — se envía SEGUNDO en el carrito
        // para que el orden de escritura correcto (ascendente por id_lote) contradiga el orden de
        // envío (mayor primero).
        var idLoteMenor = await SembrarLoteAsync(ctx, idArticulo, "L-MENOR", VencimientoLejanoFuturoAlterno);
        var idLoteMayor = await SembrarLoteAsync(ctx, idArticulo, "L-MAYOR", VencimientoLejanoFuturo);
        Assert.True(idLoteMenor < idLoteMayor);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteMenor, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteMayor, 10m);

        // Cantidades DISTINTAS por línea (3 vs. 5, discriminantes) — si el agregado se upsertea una
        // sola vez por artículo con el delta de la última línea, en vez de una vez por línea, el
        // stock final delata cuál delta ganó.
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [
                new LineaDeVenta(idArticulo, 3m, null, idLoteMayor),
                new LineaDeVenta(idArticulo, 5m, null, idLoteMenor)
            ],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 800m, null, 0m)],
            null, null);

        var emitido = await EmitirAsync(ctx, solicitud);
        Assert.Equal(2, emitido.Items.Count);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Venta)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.Equal(2, movimientos.Count);
        Assert.Equal(idLoteMenor, movimientos[0].IdLote);
        Assert.Equal(5m, movimientos[0].Cantidad * -1);
        Assert.Equal(idLoteMayor, movimientos[1].IdLote);
        Assert.Equal(3m, movimientos[1].Cantidad * -1);

        var stock = await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(12m, stock);

        var stockLoteMenor = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLoteMenor)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(5m, stockLoteMenor);

        var stockLoteMayor = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLoteMayor)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(7m, stockLoteMayor);
    }

    // ---- task 8.8: regresión — artículo sin control de lote nunca lleva id_lote --------------------

    /// <summary>spec comprobantes-venta: "A non-lot-effective line never carries a lot"; spec stock:
    /// "A non-lot articulo's movement never carries a lot". <c>lotes_habilitado</c> sigue ON a nivel
    /// empresa (seteado por <see cref="PrepararAsync"/>) — lo único que decide acá es
    /// <c>controla_lote = false</c> del artículo (decisión 2, <c>ControlEfectivo</c>).
    ///
    /// Caveat (judgment-day slice 8, juez B, nota no bloqueante): una mutación que fuerce un
    /// <c>id_lote</c> ajeno sobre el movimiento de este artículo muere PRIMERO en la FK compuesta
    /// <c>fk_movimientos_stock_lote</c> (<c>IdLote, IdArticulo, IdTenant</c>) — el lote forzado no
    /// existe para ESTE artículo, así que Postgres rechaza el INSERT antes de que la aserción de
    /// abajo llegue a evaluarse; es el mismo patrón de defensa doble que <c>pk_stock</c> (backstop
    /// del schema primero, aserción de C# como segunda capa).</summary>
    [Fact]
    public async Task UnArticuloSinControlDeLoteNuncaLlevaIdLoteEnItemNiMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinControlDeLoteNuncaLlevaIdLoteEnItemNiMovimiento));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-lote-escritura", 100m, controlaLote: false);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 2m, idLote: null));
        var item = Assert.Single(emitido.Items);
        Assert.Null(item.IdLote);
        Assert.Null(item.CodigoLote);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var itemPersistido = await db.ItemsComprobanteVenta.SingleAsync(i => i.IdComprobanteVenta == emitido.Id);
        Assert.Null(itemPersistido.IdLote);

        var movimiento = await db.MovimientosStock
            .SingleAsync(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Venta);
        Assert.Null(movimiento.IdLote);
    }

    // ---- task 8.9 (judgment-day slice 8, FIX 1): mutation target — fidelidad por-movimiento del
    // id_lote en la anulación multi-línea ------------------------------------------------------

    /// <summary>Mutation target (judgment-day slice 8, juez B REJECT: "hoistear el primer IdLote
    /// visto y pasarlo a TODOS los contramovimientos" sobrevive los 176 tests existentes porque
    /// ningún test de anulación previo vende más de una línea del mismo artículo). Dos líneas del
    /// MISMO artículo, cada una con un lote EXPLÍCITO distinto (mismo fixture que la task 8.7) →
    /// anular → CADA contramovimiento de anulación debe llevar SU <c>id_lote</c> original — no el
    /// del primer movimiento visto en el <c>foreach</c> del paso 2 — y el saldo por lote de AMBOS
    /// lotes debe volver exacto.
    ///
    /// EVIDENCIA DE MUTACIÓN (regla permanente 6 de este apply): en <c>EjecutarAnulacionAsync</c>
    /// (paso 2), <c>original.IdLote</c> pasado a <c>InsertarMovimientoStockAsync</c> reemplazado
    /// por un <c>primerLoteVisto</c> hoisteado ANTES del <c>foreach</c> y fijado una única vez en
    /// la primera iteración (mutación exacta del juez B); build, filtro
    /// <c>FullyQualifiedName~LaAnulacionMultilineaRevierteCadaMovimientoConSuPropioIdLote</c>: RED
    /// — el iterador procesa primero el movimiento de <c>idLoteMenor</c> (orden ascendente por
    /// <c>id_lote</c>, task 8.7), así que <c>primerLoteVisto</c> quedó fijo en ese valor y lo
    /// heredó también el contramovimiento del lote mayor: <c>Assert.Equal(idLoteMayor,
    /// movimientoLoteMayor.IdLote)</c> falló (<c>Expected: idLoteMayor / Actual: idLoteMenor</c>).
    /// Los saldos por lote (<c>stockLoteMayor</c>/<c>stockLoteMenor</c>) NO se ven afectados por
    /// esta mutación puntual — <c>UpsertStockLoteAsync</c> sigue leyendo <c>original.IdLote</c> sin
    /// tocar, solo el <c>id_lote</c> grabado en <c>movimientos_stock</c> queda infiel — por eso la
    /// aserción del ledger es la que efectivamente mata la mutación, no la del agregado. Revertido,
    /// mismo filtro: GREEN, junto con la suite completa de este archivo.</summary>
    [Fact]
    public async Task LaAnulacionMultilineaRevierteCadaMovimientoConSuPropioIdLote()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionMultilineaRevierteCadaMovimientoConSuPropioIdLote));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-anulacion-multilote", 100m, controlaLote: true);
        var idLoteMenor = await SembrarLoteAsync(ctx, idArticulo, "L-ANUL-MENOR", VencimientoLejanoFuturoAlterno);
        var idLoteMayor = await SembrarLoteAsync(ctx, idArticulo, "L-ANUL-MAYOR", VencimientoLejanoFuturo);
        Assert.True(idLoteMenor < idLoteMayor);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteMenor, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteMayor, 10m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [
                new LineaDeVenta(idArticulo, 3m, null, idLoteMayor),
                new LineaDeVenta(idArticulo, 2m, null, idLoteMenor)
            ],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 500m, null, 0m)],
            null, null);

        var emitido = await EmitirAsync(ctx, solicitud);
        Assert.Equal(2, emitido.Items.Count);

        await AnularAsync(ctx, emitido.Id);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimientoLoteMayor = await db.MovimientosStock.SingleAsync(
            m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion && m.Cantidad == 3m);
        Assert.Equal(idLoteMayor, movimientoLoteMayor.IdLote);

        var movimientoLoteMenor = await db.MovimientosStock.SingleAsync(
            m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion && m.Cantidad == 2m);
        Assert.Equal(idLoteMenor, movimientoLoteMenor.IdLote);

        var stockLoteMayor = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLoteMayor)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(10m, stockLoteMayor);

        var stockLoteMenor = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLoteMenor)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(10m, stockLoteMenor);
    }

    /// <summary>Mutation target (judgment-day slice 8, FIX 1, extensión mixta): una venta MIXTA —
    /// una línea de un artículo lote-efectivo y otra línea de un artículo SIN control de lote — al
    /// anularse debe dejar el contramovimiento de la línea con lote con SU <c>id_lote</c> y el de
    /// la línea sin lote con <c>id_lote</c> null. La mutación del juez B (hoistear el primer
    /// <c>IdLote</c> visto) también se prueba acá desde el ángulo inverso: si la línea CON lote se
    /// procesa primero (orden ascendente por <c>id_articulo</c>, este artículo se sembró primero),
    /// el hoist filtraría ese id_lote hacia el contramovimiento que debe quedar null.
    ///
    /// EVIDENCIA DE MUTACIÓN (regla permanente 6 de este apply): misma mutación exacta del juez B
    /// aplicada a <c>EjecutarAnulacionAsync</c> (ver evidencia completa en
    /// <see cref="LaAnulacionMultilineaRevierteCadaMovimientoConSuPropioIdLote"/>); build, filtro
    /// <c>FullyQualifiedName~LaAnulacionDeUnaVentaMixtaRevierteLaLineaConLoteYLaLineaSinLoteCadaUnaConSuIdLote</c>:
    /// RED — pero NO como fallo de aserción: <c>AnularAsync</c> devolvió <c>500</c> y
    /// <c>Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo)</c> abortó el test, porque
    /// el <c>idLote</c> hoisteado (el del artículo CON lote, primero en el orden) se intentó grabar
    /// en el contramovimiento del artículo SIN lote — combinación que no existe y viola la FK
    /// compuesta <c>fk_movimientos_stock_lote</c> (Postgres 23503) antes de que cualquier aserción
    /// de C# corra. Confirma, desde el ángulo inverso, el mismo backstop de schema documentado en
    /// <see cref="UnArticuloSinControlDeLoteNuncaLlevaIdLoteEnItemNiMovimiento"/> — acá la mutación
    /// muere en la base, no en la aserción. Revertido, mismo filtro: GREEN.</summary>
    [Fact]
    public async Task LaAnulacionDeUnaVentaMixtaRevierteLaLineaConLoteYLaLineaSinLoteCadaUnaConSuIdLote()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeUnaVentaMixtaRevierteLaLineaConLoteYLaLineaSinLoteCadaUnaConSuIdLote));
        var idArticuloConLote = await SembrarArticuloAsync(ctx, "articulo-mixto-con-lote", 100m, controlaLote: true);
        var idArticuloSinLote = await SembrarArticuloAsync(ctx, "articulo-mixto-sin-lote", 100m, controlaLote: false);
        var idLote = await SembrarLoteAsync(ctx, idArticuloConLote, "L-MIXTO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloConLote, idLote, 10m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [
                new LineaDeVenta(idArticuloConLote, 3m, null, idLote),
                new LineaDeVenta(idArticuloSinLote, 2m, null, null)
            ],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 500m, null, 0m)],
            null, null);

        var emitido = await EmitirAsync(ctx, solicitud);
        Assert.Equal(2, emitido.Items.Count);

        await AnularAsync(ctx, emitido.Id);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimientoConLote = await db.MovimientosStock.SingleAsync(
            m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion && m.IdArticulo == idArticuloConLote);
        Assert.Equal(idLote, movimientoConLote.IdLote);

        var movimientoSinLote = await db.MovimientosStock.SingleAsync(
            m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion && m.IdArticulo == idArticuloSinLote);
        Assert.Null(movimientoSinLote.IdLote);

        var stockLote = await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticuloConLote && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstAsync();
        Assert.Equal(10m, stockLote);
    }
}
