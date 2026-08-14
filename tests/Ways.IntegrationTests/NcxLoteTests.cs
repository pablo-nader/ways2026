using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
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
/// stage-12-lotes-vencimientos, Slice 9 (tasks 9.4-9.9): reglas de lote en NCX punta a punta
/// contra Postgres real, mismo <c>ServicioDeVentas.EmitirAsync</c> que <see cref="PlanDeVentaFefoTests"/>
/// (Slice 7) y <see cref="VentaEscrituraLoteTests"/> (Slice 8), acá ejercitado con
/// <c>tipo.Signo &lt; 0</c> (design: "NCX", decisión 8 del proposal): idLote explícito
/// obligatorio (sin default FEFO — task 9.1), sugerencia del picker desde el snapshot del
/// comprobante asociado (task 9.2), el lote sin-identificar como válvula de escape, y el retorno
/// a un lote vencido permitido con warning (task 9.3/9.7 — la MISMA aserción de "warning nunca
/// bloquea" que Slice 7 ya prueba del lado TX en
/// <see cref="PlanDeVentaFefoTests.UnIdLoteProvistoDeUnLoteVencidoDevuelveLoteVencidoEnTrue"/> y
/// <see cref="PlanDeVentaFefoTests.UnIdLoteOmitidoConVencidoYVigenteAmbosConSaldoEligeElVigente"/>
/// — <c>ItemEmitido.LoteVencido = ReglaDeLotes.EstaVencido(...)</c> corre por el MISMO código para
/// TX y NCX, así que esas dos escenas no se duplican acá; <see cref="RetornarAUnLoteVencidoEsPermitidoYQuedaMarcadoConWarning"/>
/// es la prueba de esa misma aserción del lado NCX, task 9.7).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class NcxLoteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas de vencimiento FIJAS y lejanas — independientes del reloj de la
    // corrida.
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ncx-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Ncx", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Módulo de lotes siempre prendido a nivel empresa — todos los tests de esta clase
        // trabajan sobre artículos lote-efectivos.
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
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente Ncx",
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

    private async Task<decimal> LeerStockLoteAsync(Contexto ctx, int idArticulo, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad)
            .FirstOrDefaultAsync();
    }

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task<decimal> LeerStockAgregadoAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync();
    }

    private static async Task<ComprobanteEmitido> AnularAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static SolicitudDeVenta SolicitudTx(Contexto ctx, int idArticulo, decimal cantidad, int? idLote) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, cantidad * 100m, null, 0m)],
            null, null);

    // spec comprobantes-venta / Devoluciones As NCX Comprobantes: Cantidad siempre POSITIVA en el
    // request (design decisión 4) — el signo lo aplica ServicioDeVentas a partir de
    // tipos_comprobante.signo. Sin pagos: una devolución no cobra, el total negativo no exige
    // ninguna línea de pago (mismo patrón que VentasCheckoutTests.DevolucionStandaloneSePersisteComoNcxSinAsociado).
    private static SolicitudDeVenta SolicitudNcx(
        Contexto ctx, int idArticulo, decimal cantidad, int? idLote, int? idComprobanteAsociado = null) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "NCX", idComprobanteAsociado,
            [new LineaDeVenta(idArticulo, cantidad, null, idLote)],
            [],
            null, null);

    private static async Task<ComprobanteEmitido> EmitirAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Cuerpo)> EmitirCrudoAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return (respuesta.StatusCode, cuerpo);
    }

    // ---- task 9.4/9.1 — NCX exige idLote explícito, nunca un default FEFO -------------------------

    /// <summary>Mutation target (task 9.1; skill mutation-proof-tests, pedido explícito del
    /// orquestador para este slice): la cláusula bajo prueba es
    /// <c>if (idLotePedido is null &amp;&amp; tipo.Signo &lt; 0)</c> en
    /// <c>ServicioDeVentas.EmitirAsync</c>. Borrarla no solo hace desaparecer el <c>400
    /// lote_requerido</c> — también deja pasar la línea al default FEFO (<c>ElegirFefo</c>), que
    /// con un solo lote con saldo positivo resolvería igual y devolvería <c>201 Created</c>: la
    /// MISMA aserción de status cachea las dos mitades de la regla (spec comprobantes-venta: "An
    /// NCX line for a lot-effective articulo requires idLote").
    ///
    /// <para>Evidencia de mutación registrada (apply-run, slice 9): se reemplazó la condición por
    /// <c>if (false)</c> en <c>ServicioDeVentas.cs</c>; build; filtro
    /// <c>FullyQualifiedName~UnaLineaNcxDeArticuloLoteEfectivoSinIdLoteEsRechazadaConLoteRequerido</c>
    /// → RED (<c>Assert.Equal() Failure: Expected: BadRequest / Actual: Created</c>, la línea
    /// resolvió vía FEFO al único lote con saldo). Revertido; mismo filtro → GREEN; suite completa
    /// de esta clase → GREEN.</para></summary>
    [Fact]
    public async Task UnaLineaNcxDeArticuloLoteEfectivoSinIdLoteEsRechazadaConLoteRequerido()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaNcxDeArticuloLoteEfectivoSinIdLoteEsRechazadaConLoteRequerido));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-sin-lote", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L1", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);

        var (status, cuerpo) = await EmitirCrudoAsync(ctx, SolicitudNcx(ctx, idArticulo, 1m, idLote: null));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("lote_requerido", cuerpo.GetProperty("codigo").GetString());
    }

    // ---- task 9.2 — el picker sugiere desde el snapshot del comprobante asociado, no FEFO ---------

    /// <summary>spec comprobantes-venta: "idLote is suggested from the associated comprobante's
    /// snapshot". La venta original elige EXPLÍCITAMENTE el lote de vencimiento más LEJANO
    /// (L-LEJANO) sobre el más cercano (L-CERCANO, que sería el pick FEFO por defecto) — si la
    /// sugerencia del picker cayera en FEFO en vez del snapshot, este test la cazaría: esperaría
    /// L-CERCANO sugerido y encontraría L-LEJANO.</summary>
    [Fact]
    public async Task UnaLineaNcxConIdComprobanteAsociadoSugiereElLoteDelSnapshotNoFefo()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaNcxConIdComprobanteAsociadoSugiereElLoteDelSnapshotNoFefo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-snapshot", 100m, controlaLote: true);
        var idLoteCercano = await SembrarLoteAsync(ctx, idArticulo, "L-CERCANO", VencimientoLejanoFuturoAlterno);
        var idLoteLejano = await SembrarLoteAsync(ctx, idArticulo, "L-LEJANO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteCercano, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteLejano, 10m);

        var original = await EmitirAsync(ctx, SolicitudTx(ctx, idArticulo, 1m, idLote: idLoteLejano));
        Assert.Equal(idLoteLejano, Assert.Single(original.Items).IdLote);

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/stock/lotes?idPuntoVenta={ctx.IdPuntoVenta}&idArticulo={idArticulo}&idComprobanteAsociado={original.Id}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var lotes = JsonSerializer.Deserialize<List<LoteListado>>(cuerpo, OpcionesJson)!;

        var sugerido = Assert.Single(lotes, l => l.Sugerido);
        Assert.Equal(idLoteLejano, sugerido.IdLote);
    }

    /// <summary>Contraste del test de arriba: SIN <c>idComprobanteAsociado</c>, el mismo picker
    /// vuelve a sugerir FEFO (L-CERCANO) — la sugerencia por snapshot es condicional a que el
    /// parámetro venga, nunca el comportamiento por defecto del endpoint.</summary>
    [Fact]
    public async Task ElMismoPickerSinIdComprobanteAsociadoSigueSugiriendoFefo()
    {
        var ctx = await PrepararAsync(nameof(ElMismoPickerSinIdComprobanteAsociadoSigueSugiriendoFefo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-snapshot-sin-asoc", 100m, controlaLote: true);
        var idLoteCercano = await SembrarLoteAsync(ctx, idArticulo, "L-CERCANO", VencimientoLejanoFuturoAlterno);
        var idLoteLejano = await SembrarLoteAsync(ctx, idArticulo, "L-LEJANO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteCercano, 10m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteLejano, 10m);

        var respuesta = await ctx.Admin.GetAsync($"/api/stock/lotes?idPuntoVenta={ctx.IdPuntoVenta}&idArticulo={idArticulo}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var lotes = JsonSerializer.Deserialize<List<LoteListado>>(cuerpo, OpcionesJson)!;

        var sugerido = Assert.Single(lotes, l => l.Sugerido);
        Assert.Equal(idLoteCercano, sugerido.IdLote);
    }

    /// <summary>judgment-day slice 9 (juez A, MAJOR): el lote sugerido del snapshot tenía que
    /// resolverse ANTES de <c>LeerSaldosAsync</c> y pasarse en <c>idsLotePedidos</c> — el caso
    /// TÍPICO de devolución es justo el que rompía: el lote del comprobante original se vendió
    /// COMPLETO (saldo 0 en el PV) y ahora vuelven unidades. Sin el fix, ese lote ni aparece
    /// listado ni sugerido, violando "idLote is suggested from the associated comprobante's
    /// snapshot" del spec. L-OTRO (saldo &gt; 0, sin relación con el comprobante) es el control
    /// discriminante: tiene que listarse con <c>Sugerido = false</c>.
    ///
    /// <para>Evidencia de mutación registrada (jd-fix, slice 9 juez A): se revirtió el fix
    /// (resolver <c>idLoteSugerido</c> DESPUÉS de <c>LeerSaldosAsync</c> con
    /// <c>idsLotePedidos</c> vacío, como estaba antes); build; filtro
    /// <c>FullyQualifiedName~ElLoteSugeridoDelSnapshotApareceListadoAunqueSuSaldoEnElPvSeaCero</c>
    /// → RED (<c>Assert.Contains</c> del lote agotado en <c>lotes</c> falló: el lote no aparecía
    /// en absoluto, filtrado por <c>LeerSaldosAsync</c> al no tener saldo ni estar en
    /// <c>idsLotePedidos</c>). Revertido; mismo filtro → GREEN; suite completa de esta clase →
    /// GREEN.</para></summary>
    [Fact]
    public async Task ElLoteSugeridoDelSnapshotApareceListadoAunqueSuSaldoEnElPvSeaCero()
    {
        var ctx = await PrepararAsync(nameof(ElLoteSugeridoDelSnapshotApareceListadoAunqueSuSaldoEnElPvSeaCero));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-agotado", 100m, controlaLote: true);
        var idLoteAgotado = await SembrarLoteAsync(ctx, idArticulo, "L-AGOTADO", VencimientoLejanoFuturo);
        var idLoteOtro = await SembrarLoteAsync(ctx, idArticulo, "L-OTRO", VencimientoLejanoFuturoAlterno);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteAgotado, 5m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLoteOtro, 5m);

        // La venta original agota COMPLETO el lote elegido — saldo 0 en el PV tras la TX, el
        // escenario típico de devolución.
        var original = await EmitirAsync(ctx, SolicitudTx(ctx, idArticulo, 5m, idLote: idLoteAgotado));
        Assert.Equal(idLoteAgotado, Assert.Single(original.Items).IdLote);
        Assert.Equal(0m, await LeerStockLoteAsync(ctx, idArticulo, idLoteAgotado));

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/stock/lotes?idPuntoVenta={ctx.IdPuntoVenta}&idArticulo={idArticulo}&idComprobanteAsociado={original.Id}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var lotes = JsonSerializer.Deserialize<List<LoteListado>>(cuerpo, OpcionesJson)!;

        var loteAgotadoListado = Assert.Single(lotes, l => l.IdLote == idLoteAgotado);
        Assert.Equal(0m, loteAgotadoListado.Cantidad);
        Assert.True(loteAgotadoListado.Sugerido);

        var loteOtroListado = Assert.Single(lotes, l => l.IdLote == idLoteOtro);
        Assert.False(loteOtroListado.Sugerido);
    }

    // ---- task 9.6 — el lote sin identificar es una elección explícita válida en una devolución ----

    /// <summary>spec comprobantes-venta: "idLote is required even without an associated
    /// comprobante" — una devolución standalone (sin id_comprobante_asociado) sobre un artículo
    /// lote-efectivo cuando el operador no puede identificar el lote físico. El lote sin
    /// identificar ya existe (sembrado directo, mismo criterio que la reconciliación de Slice 4)
    /// — el operador lo elige EXPLÍCITAMENTE como <c>idLote</c>, nunca es un fallback silencioso
    /// del servidor (eso es exclusivo del camino TX, decisión 7, y NCX lo tiene cerrado por task
    /// 9.1).</summary>
    [Fact]
    public async Task UnaDevolucionStandaloneAceptaElLoteSinIdentificarComoValvulaDeEscape()
    {
        var ctx = await PrepararAsync(nameof(UnaDevolucionStandaloneAceptaElLoteSinIdentificarComoValvulaDeEscape));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-sin-identificar", 100m, controlaLote: true);
        var idSinIdentificar = await SembrarLoteAsync(
            ctx, idArticulo, ReglaDeLotes.CodigoSinIdentificar, null, esSinIdentificar: true);

        var emitido = await EmitirAsync(ctx, SolicitudNcx(ctx, idArticulo, 1m, idLote: idSinIdentificar));

        Assert.Null(emitido.IdComprobanteAsociado);
        var item = Assert.Single(emitido.Items);
        Assert.Equal(idSinIdentificar, item.IdLote);
        Assert.Equal(ReglaDeLotes.CodigoSinIdentificar, item.CodigoLote);
        Assert.False(item.LoteVencido);

        Assert.Equal(1m, await LeerStockLoteAsync(ctx, idArticulo, idSinIdentificar));
    }

    // ---- task 9.7/9.3 — retornar a un lote vencido está permitido, marcado con warning ------------

    /// <summary>spec comprobantes-venta: "Returning into an expired lot is permitted" — el mismo
    /// <c>ItemEmitido.LoteVencido = ReglaDeLotes.EstaVencido(...)</c> que Slice 7 probó para TX
    /// (task 9.3: computado para TX y NCX por igual, nunca un bloqueo). El lote no tiene stock
    /// previo — una devolución siempre SUMA, nunca puede dejar el saldo del lote negativo (mismo
    /// criterio que <c>UpsertStockLoteAsync</c>, sin chequeo de negativo en este camino de
    /// escritura).</summary>
    [Fact]
    public async Task RetornarAUnLoteVencidoEsPermitidoYQuedaMarcadoConWarning()
    {
        var ctx = await PrepararAsync(nameof(RetornarAUnLoteVencidoEsPermitidoYQuedaMarcadoConWarning));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-vencido", 100m, controlaLote: true);
        var idVencido = await SembrarLoteAsync(ctx, idArticulo, "L-DEVUELTO-VENCIDO", VencimientoLejanoPasado);

        var emitido = await EmitirAsync(ctx, SolicitudNcx(ctx, idArticulo, 1m, idLote: idVencido));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(idVencido, item.IdLote);
        Assert.Equal("L-DEVUELTO-VENCIDO", item.CodigoLote);
        Assert.True(item.LoteVencido);

        Assert.Equal(1m, await LeerStockLoteAsync(ctx, idArticulo, idVencido));
    }

    // ---- task 9.4 — un idLote inválido (de otro artículo) sigue rechazado en NCX -------------------

    /// <summary>Mismo camino de validación que TX (<c>PlanDeVentaFefoTests.UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalido</c>)
    /// — la resolución de línea de NCX reusa exactamente la misma rama de <c>idLotePedido is {
    /// } idLote</c> que TX; solo el CAMINO de "idLote ausente" difiere (task 9.1). Un idLote
    /// explícito pero apócrifo (de otro artículo) se rechaza igual en los dos tipos.</summary>
    [Fact]
    public async Task UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalidoEnNcx()
    {
        var ctx = await PrepararAsync(nameof(UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalidoEnNcx));
        var idArticuloA = await SembrarArticuloAsync(ctx, "articulo-ncx-invalido-a", 100m, controlaLote: true);
        var idArticuloB = await SembrarArticuloAsync(ctx, "articulo-ncx-invalido-b", 100m, controlaLote: true);
        var idLoteDeB = await SembrarLoteAsync(ctx, idArticuloB, "L-B", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticuloB, idLoteDeB, 10m);

        var (status, cuerpo) = await EmitirCrudoAsync(ctx, SolicitudNcx(ctx, idArticuloA, 1m, idLote: idLoteDeB));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("lote_invalido", cuerpo.GetProperty("codigo").GetString());
    }

    // ---- gap de cobertura (judgment-day slice 9, juez B) — anulación de una NCX lot-bearing -------

    /// <summary>Gap de cobertura señalado por juez B (judgment-day slice 9): ningún test anulaba un
    /// comprobante NCX con lote — toda la cobertura de "anulación + lote" (<see cref="VentaEscrituraLoteTests"/>,
    /// Slice 8) ejercita exclusivamente TX. <c>EjecutarAnulacionAsync</c> es el MISMO código para los
    /// dos tipos (lee <c>movimientos_stock</c> filtrando por <c>Motivo = MotivoStock.Venta</c>, sin
    /// distinguir TX de NCX) y ya fue auditado como correcto — esto cierra la red del lado NCX
    /// específicamente. Una NCX lot-bearing SUMA stock al lote (retorno); su anulación debe REVERTIR
    /// esa suma con signo negativo, dejando stock y stock_lotes en el valor EXACTO pre-NCX (5, un
    /// valor discriminante, no cero — así un revert que "olvide" restar y solo deje el post-NCX no
    /// pasa por casualidad).
    ///
    /// EVIDENCIA DE MUTACIÓN (apply-run, judgment-day slice 9): en <c>EjecutarAnulacionAsync</c>, el
    /// guard <c>if (original.IdLote is { } idLote)</c> que envuelve el <c>UpsertStockLoteAsync</c> del
    /// espejo por-lote se mutó a <c>if (original.IdLote is { } idLote &amp;&amp; inversa >= 0)</c> —
    /// salteando el upsert espejo exactamente cuando la reversa (<c>inversa = -original.Cantidad</c>)
    /// es NEGATIVA. Ese es, estructuralmente, el caso EXCLUSIVO de anular una NCX: una NCX SUMA stock
    /// (<c>original.Cantidad &gt; 0</c>), así que su contramovimiento siempre RESTA (<c>inversa &lt;
    /// 0</c>); anular una TX es el espejo (<c>original.Cantidad &lt; 0</c>, <c>inversa &gt; 0</c>),
    /// así que esta condición nunca toca ese camino — es la mutación que aísla el gap sin tocar
    /// ninguna aserción de <see cref="VentaEscrituraLoteTests"/>. Build; filtro
    /// <c>FullyQualifiedName~LaAnulacionDeUnaNcxLoteEfectivaRevierteElLoteExactoConSignoCorrecto</c>:
    /// RED — <c>Assert.Equal(5m, stockLotePostAnulacion)</c> falló (<c>Expected: 5 / Actual: 8</c>,
    /// el valor post-NCX sin revertir; el <c>stock</c> agregado sí volvió a 5 porque
    /// <c>UpsertStockAsync</c> no está guardado por esta condición). Revertido; mismo filtro: GREEN;
    /// suite completa de esta clase y de <see cref="VentaEscrituraLoteTests"/>: GREEN (la mutación
    /// nunca las tocó, confirmando que el gap era real y específico de NCX).</summary>
    [Fact]
    public async Task LaAnulacionDeUnaNcxLoteEfectivaRevierteElLoteExactoConSignoCorrecto()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeUnaNcxLoteEfectivaRevierteElLoteExactoConSignoCorrecto));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-anulacion", 100m, controlaLote: true);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-NCX-ANUL", VencimientoLejanoFuturo);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 5m);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 5m);

        var emitido = await EmitirAsync(ctx, SolicitudNcx(ctx, idArticulo, 3m, idLote: idLote));
        var item = Assert.Single(emitido.Items);
        Assert.Equal(idLote, item.IdLote);
        Assert.Equal(-3m, item.Cantidad);

        Assert.Equal(8m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(8m, await LeerStockAgregadoAsync(ctx, idArticulo));

        var anulado = await AnularAsync(ctx, emitido.Id);
        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var movimientoAnulacion = await db.MovimientosStock
                .SingleAsync(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion);
            Assert.Equal(idLote, movimientoAnulacion.IdLote);
            Assert.Equal(-3m, movimientoAnulacion.Cantidad);
        }

        Assert.Equal(5m, await LeerStockAgregadoAsync(ctx, idArticulo));
        Assert.Equal(5m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }
}
