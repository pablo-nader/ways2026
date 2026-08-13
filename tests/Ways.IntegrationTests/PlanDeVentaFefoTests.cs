using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// stage-12-lotes-vencimientos, Slice 7 (tasks 7.4-7.12): el plan FEFO de
/// <c>ServicioDeVentas.EmitirAsync</c> punta a punta — solo la fase de DECISIÓN cambia en esta
/// slice (design: "Write site 1", decide phase); la transacción sigue sin escribir <c>id_lote</c>
/// (eso es slice 8), así que cada prueba acá asserta el PLAN/response (<see cref="ItemEmitido"/>),
/// nunca <c>movimientos_stock</c>/<c>stock_lotes</c> con lote.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PlanDeVentaFefoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas de vencimiento FIJAS y lejanas — independientes del reloj de la
    // corrida. LoteVencido solo se asserta con estas fechas, nunca con un borde "hoy".
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);
    private static readonly DateOnly VencimientoLejanoFuturoAlterno = new(2098, 6, 30);
    private static readonly DateOnly VencimientoLejanoPasado = new(2020, 1, 15);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdCliente);

    private async Task<Contexto> PrepararAsync(string nombre, bool lotesHabilitado = true)
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Fefo-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Fefo", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // stage-12 slice 7: módulo de lotes a nivel empresa — controlable por prueba (7.10 lo
        // deja en true igual, el articulo del carrito es el que no controla lote).
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = lotesHabilitado ? "true" : "false", CreatedAt = ahora, UpdatedAt = ahora
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
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente Fefo",
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

    private async Task<int> SembrarLoteAsync(
        Contexto ctx, int idArticulo, string codigo, DateOnly? fechaVencimiento, bool esSinIdentificar = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var lote = new Lote
        {
            IdArticulo = idArticulo, Codigo = codigo, FechaVencimiento = fechaVencimiento,
            EsSinIdentificar = esSinIdentificar, CreatedAt = ahora, UpdatedAt = ahora
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

    // ---- task 7.5: FEFO por defecto — nearest-expiry ------------------------------------------

    [Fact]
    public async Task UnIdLoteOmitidoResuelveAlLoteDeVencimientoMasCercano()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteOmitidoResuelveAlLoteDeVencimientoMasCercano));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-cercano", 100m, controlaLote: true);
        var idL1 = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturoAlterno);
        var idL2 = await SembrarLoteAsync(ctx, idArticulo, "L2", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idL1, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idL2, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idL1, item.IdLote);
        Assert.Equal("L1", item.CodigoLote);
        Assert.False(item.LoteVencido);
    }

    // ---- task 7.6: sin-identificar SIEMPRE primero (spec: "offered before every dated lot") ---

    [Fact]
    public async Task ElLoteSinIdentificarSeOfreceAntesQueCualquierLoteConFecha()
    {
        var ctx = await PrepararAsync(nameof(ElLoteSinIdentificarSeOfreceAntesQueCualquierLoteConFecha));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-sin-id", 100m, controlaLote: true);
        var idSinIdentificar = await SembrarLoteAsync(ctx, idArticulo, ReglaDeLotes.CodigoSinIdentificar, null, esSinIdentificar: true);
        var idL1 = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturoAlterno);
        await SembrarStockLoteAsync(ctx, idArticulo, idSinIdentificar, 5m);
        await SembrarStockLoteAsync(ctx, idArticulo, idL1, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idSinIdentificar, item.IdLote);
        Assert.Equal(ReglaDeLotes.CodigoSinIdentificar, item.CodigoLote);
    }

    // ---- task 7.7: un idLote provisto se HONRA aunque no sea el pick FEFO ----------------------

    [Fact]
    public async Task UnIdLoteProvistoSeHonraAunqueNoSeaElPickFefo()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteProvistoSeHonraAunqueNoSeaElPickFefo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-honrado", 100m, controlaLote: true);
        var idL1 = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturoAlterno);
        var idL2 = await SembrarLoteAsync(ctx, idArticulo, "L2", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idL1, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idL2, 10m);

        // El pick FEFO natural sería L1 (vence antes) — pide L2 explícito.
        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: idL2));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idL2, item.IdLote);
        Assert.Equal("L2", item.CodigoLote);
    }

    // ---- task 7.8: idLote inválido → 400 lote_invalido (mutation-proof-tests) ------------------

    /// <summary>Mutation target nombrado por la regla permanente 6 de este apply: la validación
    /// <c>saldosDelArticulo.FindIndex(s => s.IdLote == idLote)</c> en
    /// <c>ServicioDeVentas.EmitirAsync</c> es la ÚNICA barrera entre un <c>idLote</c> ajeno/
    /// inexistente y el checkout. Evidencia de mutación (regla permanente 6, apply de slice 7):
    /// con el <c>if (posicion &lt; 0) throw ...</c> comentado (la línea cae directo a
    /// <c>loteResuelto = saldosDelArticulo[posicion]</c> con <c>posicion = -1</c>), este test
    /// corrió y **no** dio <c>400 lote_invalido</c> — tiró <c>IndexOutOfRangeException</c> sin
    /// capturar, <c>500</c> genérico (rojo, mensaje distinto al esperado). Revertido el comentado,
    /// vuelve a <c>400 lote_invalido</c> (verde). No se debilitó ninguna otra capa — la prueba
    /// llama al endpoint real de punta a punta.</summary>
    [Fact]
    public async Task UnIdLoteInvalidoEsRechazadoConLoteInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteInvalidoEsRechazadoConLoteInvalido));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-invalido", 100m, controlaLote: true);
        var idL1 = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idL1, 10m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas", SolicitudSimple(ctx, idArticulo, 1m, idLote: idL1 + 999_000));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo código <c>lote_invalido</c>, otra causa: el lote existe pero es de OTRO
    /// artículo — la búsqueda está scopeada a <c>saldosPorArticulo[item.IdArticulo]</c>, así que
    /// un lote real ajeno tampoco aparece en <c>saldosDelArticulo</c>.</summary>
    [Fact]
    public async Task UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalido));
        var idArticuloA = await SembrarArticuloAsync(ctx, "articulo-fefo-a", 100m, controlaLote: true);
        var idArticuloB = await SembrarArticuloAsync(ctx, "articulo-fefo-b", 100m, controlaLote: true);
        var idLoteDeB = await SembrarLoteAsync(ctx, idArticuloB, "L-B", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloB, idLoteDeB, 10m);
        var idLoteDeA = await SembrarLoteAsync(ctx, idArticuloA, "L-A", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloA, idLoteDeA, 10m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas", SolicitudSimple(ctx, idArticuloA, 1m, idLote: idLoteDeB));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 7.9: un lote corto completa la línea igual, nunca auto-split ---------------------

    [Fact]
    public async Task UnLoteCortoCompletaLaLineaSinAutoSplit()
    {
        var ctx = await PrepararAsync(nameof(UnLoteCortoCompletaLaLineaSinAutoSplit));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-corto", 100m, controlaLote: true);
        var idLoteCorto = await SembrarLoteAsync(ctx, idArticulo, "L-CORTO", VencimientoLejanoFuturoAlterno);
        var idLoteLargo = await SembrarLoteAsync(ctx, idArticulo, "L-LARGO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteCorto, 3m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteLargo, 100m);

        // Pide 5 unidades — el lote FEFO (L-CORTO, vence antes) solo tiene saldo 3.
        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 5m, idLote: null));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idLoteCorto, item.IdLote);
        Assert.Equal(5m, item.Cantidad);
    }

    // ---- task 7.4/7.10: guard de presupuesto de consultas — 16 → 17 solo con lote en el carrito ---

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

    /// <summary>Igual técnica que <c>VentasCheckoutTests.ContadorDeComandos</c>: cuenta cada
    /// comando que dispara <c>ReaderExecuting</c> (incluye los dos <c>SaveChangesAsync</c> de la
    /// mitad transaccional, de cantidad constante — no rompe el guard).</summary>
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

    /// <summary>Filtrado a los comandos cuyo texto referencia <c>lotes</c>/<c>stock_lotes</c> —
    /// aísla la query de <see cref="ServicioDeLotes.LeerSaldosAsync"/> del resto del checkout.</summary>
    private sealed class ContadorDeConsultasDeLotes : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        private static bool Coincide(DbCommand command) =>
            command.CommandText.Contains("stock_lotes", StringComparison.OrdinalIgnoreCase)
            || command.CommandText.Contains("\"l\".\"id_lote\"", StringComparison.OrdinalIgnoreCase)
            || command.CommandText.Contains("FROM lotes", StringComparison.OrdinalIgnoreCase);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (Coincide(command))
            {
                Consultas++;
            }

            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Coincide(command))
            {
                Consultas++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(int Total, int DeLotes)> EmitirYContarConsultasAsync(
        Contexto ctx, int idArticulo, int? idLote)
    {
        var contadorTotal = new ContadorDeComandos();
        var contadorDeLotes = new ContadorDeConsultasDeLotes();
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
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contadorTotal, contadorDeLotes)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var lectorDeMovimientos = new Ways.Application.Caja.LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new Ways.Application.Caja.ServicioDeTurnos(db, reloj, contexto, lectorDeMovimientos);
        var servicioDeLotes = new ServicioDeLotes(db, reloj, contexto);
        var servicioDeVentas = new ServicioDeVentas(db, reloj, contexto, servicioDeOfertas, servicioDeTurnos, servicioDeLotes);

        await servicioDeVentas.EmitirAsync(SolicitudSimple(ctx, idArticulo, 1m, idLote));

        return (contadorTotal.Consultas, contadorDeLotes.Consultas);
    }

    [Fact]
    public async Task ElCheckoutEmiteDiecisieteConsultasConUnArticuloConLoteEnElCarrito()
    {
        var ctx = await PrepararAsync(nameof(ElCheckoutEmiteDiecisieteConsultasConUnArticuloConLoteEnElCarrito));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-presupuesto", 10m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var (total, deLotes) = await EmitirYContarConsultasAsync(ctx, idArticulo, idLote: null);

        // spec lotes-y-vencimientos: "Module on with a lot-controlled articulo nets zero
        // round-trip change" — 16 (baseline post-slice-2) + 1 (LeerSaldosAsync) = 17.
        Assert.Equal(17, total);
        Assert.Equal(1, deLotes);
    }

    [Fact]
    public async Task ElCheckoutEmiteDieciseisConsultasSinArticuloConLoteEnElCarrito()
    {
        var ctx = await PrepararAsync(nameof(ElCheckoutEmiteDieciseisConsultasSinArticuloConLoteEnElCarrito));
        // lotes_habilitado = true a nivel empresa, pero el articulo del carrito NO controla lote
        // (spec: "Module on with no lot-controlled articulo in the cart issues no FEFO query").
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-lote-presupuesto", 10m, controlaLote: false);

        var (total, deLotes) = await EmitirYContarConsultasAsync(ctx, idArticulo, idLote: null);

        Assert.Equal(16, total);
        Assert.Equal(0, deLotes);
    }

    // ---- task 7.11: cliente legado sin idLote transacciona bien --------------------------------

    [Fact]
    public async Task UnClienteLegadoSinIdLoteTransaccionaCorrectamente()
    {
        var ctx = await PrepararAsync(nameof(UnClienteLegadoSinIdLoteTransaccionaCorrectamente));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-legado", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        // Payload crudo: la línea NO tiene la propiedad "idLote" en absoluto — un cliente legado
        // que ni siquiera conoce el campo (a diferencia de mandar el campo con valor null).
        var payload = $$"""
            {
              "idPuntoVenta": {{ctx.IdPuntoVenta}},
              "idCliente": {{ctx.IdCliente}},
              "codigoTipoComprobante": "TX",
              "idComprobanteAsociado": null,
              "lineas": [ { "idArticulo": {{idArticulo}}, "cantidad": 1, "codigoBarra": null } ],
              "pagos": [ { "idMedioPago": {{ctx.IdMedioEfectivo}}, "importe": 100, "referencia": null, "vuelto": 0 } ],
              "direccionEntrega": null,
              "observaciones": null
            }
            """;

        var respuesta = await ctx.Admin.PostAsync(
            "/api/ventas", new StringContent(payload, Encoding.UTF8, "application/json"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        var item = Assert.Single(emitido.Items);
        Assert.Equal(idLote, item.IdLote);
    }

    // ---- task 7.12: carrito mixto (lote-efectivo + sin lote) ------------------------------------

    [Fact]
    public async Task UnCarritoMixtoDeArticuloConLoteYSinLoteResuelveAmbosCaminos()
    {
        var ctx = await PrepararAsync(nameof(UnCarritoMixtoDeArticuloConLoteYSinLoteResuelveAmbosCaminos));
        var idArticuloConLote = await SembrarArticuloAsync(ctx, "articulo-mixto-con-lote", 100m, controlaLote: true);
        var idArticuloSinLote = await SembrarArticuloAsync(ctx, "articulo-mixto-sin-lote", 50m, controlaLote: false);
        var idLote = await SembrarLoteAsync(ctx, idArticuloConLote, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloConLote, idLote, 10m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticuloConLote, 1m, null), new LineaDeVenta(idArticuloSinLote, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 150m, null, 0m)],
            null, null);

        var emitido = await EmitirAsync(ctx, solicitud);

        Assert.Equal(2, emitido.Items.Count);
        var itemConLote = emitido.Items.Single(i => i.IdArticulo == idArticuloConLote);
        var itemSinLote = emitido.Items.Single(i => i.IdArticulo == idArticuloSinLote);
        Assert.Equal(idLote, itemConLote.IdLote);
        Assert.Null(itemSinLote.IdLote);
        Assert.Null(itemSinLote.CodigoLote);
        Assert.False(itemSinLote.LoteVencido);
    }

    // ---- judgment-day slice 7, FIX 1 (CRITICAL 1) — decisión 15: ElegirFefo prefiere no vencidos ---

    /// <summary>Repro exacto del juez B: un lote VENCIDO (2020-01-15) y uno VIGENTE (2099-12-31),
    /// AMBOS con saldo positivo, <c>idLote</c> omitido. El orden FEFO base (fecha ASC pura)
    /// elegiría el vencido, por vencer antes — decisión 15 exige que la partición no-vencido gane
    /// primero. <para>EVIDENCIA DE MUTACIÓN: se revirtió <c>ReglaDeLotes.ElegirFefo</c> a la
    /// partición vieja (<c>OrdenarFefo(conSaldoPositivo)[0]</c>, fecha ASC pura, sin partición por
    /// vencimiento) — build, mismo filtro: este test cae RED (<c>item.IdLote</c> esperado
    /// <c>idVigente</c>, actual <c>idVencido</c>). Restaurada la partición de decisión 15, build,
    /// mismo filtro: GREEN.</para></summary>
    [Fact]
    public async Task UnIdLoteOmitidoConVencidoYVigenteAmbosConSaldoEligeElVigente()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteOmitidoConVencidoYVigenteAmbosConSaldoEligeElVigente));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-decision-15", 100m, controlaLote: true);
        var idVencido = await SembrarLoteAsync(ctx, idArticulo, "L-VENCIDO", VencimientoLejanoPasado);
        var idVigente = await SembrarLoteAsync(ctx, idArticulo, "L-VIGENTE", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idVencido, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idVigente, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idVigente, item.IdLote);
        Assert.Equal("L-VIGENTE", item.CodigoLote);
        Assert.False(item.LoteVencido);
    }

    // ---- judgment-day slice 7, FIX 2 (CRITICAL 2) — fallback sin-identificar sin lote con saldo ---

    /// <summary>Artículo lote-efectivo sin ningún lote con saldo positivo (acá, directamente sin
    /// ningún lote sembrado — la otra mitad válida de la condición, "o sin lotes"): el plan
    /// resuelve el sin-identificar vía get-or-create perezoso (<c>ResolverSinIdentificarAsync</c>),
    /// nunca revienta ni deja la línea sin lote.</summary>
    [Fact]
    public async Task UnArticuloSinNingunLoteConSaldoResuelveElSinIdentificarPorGetOrCreate()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinNingunLoteConSaldoResuelveElSinIdentificarPorGetOrCreate));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-sin-lotes", 100m, controlaLote: true);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(ReglaDeLotes.CodigoSinIdentificar, item.CodigoLote);
        Assert.NotNull(item.IdLote);
        Assert.False(item.LoteVencido);
    }

    // ---- judgment-day slice 7, FIX 3 (HIGH 3) — LoteVencido asertado en true ---------------------

    /// <summary>Un <c>idLote</c> explícito de un lote VENCIDO se HONRA (spec: "A supplied idLote
    /// is honoured even when it is not the FEFO pick") — a diferencia de todos los tests previos
    /// de esta clase, que solo aserteaban <c>LoteVencido == false</c>, este es el primero que
    /// asserta <c>true</c>.</summary>
    [Fact]
    public async Task UnIdLoteProvistoDeUnLoteVencidoDevuelveLoteVencidoEnTrue()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteProvistoDeUnLoteVencidoDevuelveLoteVencidoEnTrue));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fefo-vencido-honrado", 100m, controlaLote: true);
        var idVencido = await SembrarLoteAsync(ctx, idArticulo, "L-VENCIDO-HONRADO", VencimientoLejanoPasado);
        await SembrarStockLoteAsync(ctx, idArticulo, idVencido, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: idVencido));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idVencido, item.IdLote);
        Assert.Equal("L-VENCIDO-HONRADO", item.CodigoLote);
        Assert.True(item.LoteVencido);
    }

    // ---- judgment-day slice 7, FIX 4 (WARNING 4) — idLote sobre línea sin lote efectivo -----------

    /// <summary>dto-contract-honesty: un <c>idLote</c> mandado sobre una línea de un artículo que
    /// NO controla lote no tiene destino real — antes se ignoraba en silencio, ahora se rechaza
    /// con el mismo código que un idLote inválido (el campo no puede aterrizar en ningún lado).</summary>
    [Fact]
    public async Task UnIdLoteSobreUnaLineaSinLoteEfectivoEsRechazadoConLoteInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteSobreUnaLineaSinLoteEfectivoEsRechazadoConLoteInvalido));
        var idArticuloConLote = await SembrarArticuloAsync(ctx, "articulo-fix4-con-lote", 100m, controlaLote: true);
        var idArticuloSinLote = await SembrarArticuloAsync(ctx, "articulo-fix4-sin-lote", 50m, controlaLote: false);
        var idLote = await SembrarLoteAsync(ctx, idArticuloConLote, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloConLote, idLote, 10m);

        // El idLote va sobre la línea SIN lote efectivo — inválido, aunque el id exista y sea real
        // (es de OTRO artículo que sí controla lote).
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticuloSinLote, 1m, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day slice 7, FIX 5 (WARNING 5) — response fresco vs relectura -------------------

    /// <summary>Límite honesto de esta slice (ver el doc-comment de <c>Proyectar</c> en
    /// <c>ServicioDeVentas.cs</c>, APPLY-RUN NOTE de la task 7.3): el checkout FRESCO devuelve
    /// <c>id_lote</c> desde el plan (no persistido todavía), pero una relectura vía
    /// <c>ObtenerAsync</c> cae al valor YA persistido en <c>items_comprobante_venta.id_lote</c>,
    /// que esta slice todavía no escribe — <c>null</c> hasta slice 8. (Al llegar slice 8, este test
    /// SE ACTUALIZA para esperar el mismo <c>IdLote</c> en ambas lecturas — ver la nota del bloque
    /// Slice 8 en tasks.md.)</summary>
    [Fact]
    public async Task ElCheckoutFrescoDevuelveIdLotePeroLaRelecturaTodaviaLoDevuelveNullHastaSlice8()
    {
        var ctx = await PrepararAsync(nameof(ElCheckoutFrescoDevuelveIdLotePeroLaRelecturaTodaviaLoDevuelveNullHastaSlice8));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-fix5-contraste", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var emitido = await EmitirAsync(ctx, SolicitudSimple(ctx, idArticulo, 1m, idLote: null));
        var itemFresco = Assert.Single(emitido.Items);
        Assert.Equal(idLote, itemFresco.IdLote);

        var respuestaRelectura = await ctx.Admin.GetAsync($"/api/ventas/{emitido.Id}");
        var cuerpoRelectura = await respuestaRelectura.Content.ReadAsStringAsync();
        Assert.True(respuestaRelectura.StatusCode == HttpStatusCode.OK, cuerpoRelectura);
        var releido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpoRelectura, OpcionesJson)!;

        var itemReleido = Assert.Single(releido.Items);
        Assert.Null(itemReleido.IdLote);
    }
}
