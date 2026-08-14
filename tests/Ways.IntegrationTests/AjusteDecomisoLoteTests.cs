using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 11 (tasks 11.4-11.10): el ajuste lote-consciente y el
/// decomiso de primera clase punta a punta — <c>idLote</c> requerido/rechazado según
/// <c>EsLoteEfectivo</c> (spec stock: Manual Ajuste Path Is Admin-Only), la disciplina de signo de
/// <c>ContarAsync</c> aplicada a un decomiso (cantidad positiva del cliente, negada server-side), el
/// único rechazo de negatividad de <c>ServicioDeStock</c> (<c>409
/// stock_insuficiente_para_decomiso</c>) y la decisión 9 del proposal: decomiso NO restringido a
/// lotes vencidos, Admin-only vía <c>GestionDeCatalogo</c> apilada sobre <c>OperacionDePos</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AjusteDecomisoLoteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Regla permanente 3: fechas fijas y lejanas — independientes del reloj de la corrida.
    private static readonly DateOnly VencimientoLejanoFuturo = new(2099, 12, 31);
    private static readonly DateOnly VencimientoLejanoPasado = new(2020, 1, 1);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva, int IdListaPrecio);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ajuste-decomiso-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Ajuste Decomiso", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        // Módulo de lotes ON a nivel empresa — mismo criterio que TransferenciaLoteTests.
        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva, lista.Id);
    }

    private async Task<int> SembrarArticuloLoteEfectivoAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = true, CreatedAt = ahora, UpdatedAt = ahora
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

    /// <summary>Artículo SIN control de lote — contraparte del lote-efectivo, usado por el rechazo
    /// de <c>observaciones_requeridas</c>/mutación (no necesitan la dimensión de lote).</summary>
    private async Task<int> SembrarArticuloSinLoteAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = false, CreatedAt = ahora, UpdatedAt = ahora
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

    private async Task SembrarStockAgregadoAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private async Task<decimal> LeerStockAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
    }

    private async Task<decimal> LeerStockLoteAsync(Contexto ctx, int idArticulo, int idLote)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.StockLotes
            .Where(sl => sl.IdArticulo == idArticulo && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).FirstOrDefaultAsync();
    }

    private async Task<HttpClient> CrearClienteVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(nombre, mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return vendedor;
    }

    // ---- task 11.10: ajuste lote-consciente ×2 ------------------------------------------------

    /// <summary>spec stock: "Ajuste of a lot-effective articulo requires idLote and updates both
    /// caches" — un único <c>movimientos_stock</c> con <c>id_lote</c>, ambas cachés (agregada Y de
    /// lote) actualizadas en la misma transacción.</summary>
    [Fact]
    public async Task UnAjusteDeUnArticuloLoteEfectivoConIdLoteActualizaAmbasCaches()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteDeUnArticuloLoteEfectivoConIdLoteActualizaAmbasCaches));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-ajuste-lote", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-AJUSTE", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 5m, "Reconteo físico", idLote);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(45m, actual.Cantidad);
        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(45m, await LeerStockAsync(ctx, idArticulo));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Ajuste);
        Assert.Equal(5m, movimiento.Cantidad);
        Assert.Equal(idLote, movimiento.IdLote);
    }

    /// <summary>spec stock: "Ajuste of a lot-effective articulo without idLote is rejected".</summary>
    [Fact]
    public async Task UnAjusteDeUnArticuloLoteEfectivoSinIdLoteEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteDeUnArticuloLoteEfectivoSinIdLoteEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-ajuste-sin-lote", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 40m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, 5m, "Reconteo físico");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_requerido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Ajuste));
        Assert.Equal(40m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- judgment-day (juez B): el ajuste PUEDE dejar negativo, a diferencia del decomiso -------

    /// <summary>spec stock: "no negativity refusal" para el ajuste (a diferencia del único rechazo
    /// que conoce <c>DecomisarAsync</c>) — un ajuste que deja el lote Y el agregado NEGATIVOS se
    /// acepta con 200 y el saldo negativo queda persistido exacto.
    ///
    /// EVIDENCIA DE MUTACIÓN (juez B): agregado temporalmente un chequeo de negatividad a
    /// <c>EjecutarAjusteAsync</c> (mismo <c>if (nuevaDelLote &lt; 0m) throw ...</c> que usa el
    /// decomiso) — build, filtro
    /// <c>FullyQualifiedName~UnAjusteQueDejaSaldoNegativoEsAceptado</c>: este test <b>FALLÓ</b>
    /// (409 en vez de 200 — la rama muerta habría bloqueado la corrección de un saldo negativo).
    /// Revertido el mutante, corrida de nuevo: <b>GREEN</b>.</summary>
    [Fact]
    public async Task UnAjusteQueDejaSaldoNegativoEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteQueDejaSaldoNegativoEsAceptado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-ajuste-negativo", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-AJUSTE-NEGATIVO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 3m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 3m);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticulo, -5m, "Corrección a negativo", idLote);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actual = JsonSerializer.Deserialize<StockActual>(cuerpo, OpcionesJson)!;
        Assert.Equal(-2m, actual.Cantidad);
        Assert.Equal(-2m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(-2m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- gap de cobertura (dto-contract-honesty): guard simétrico de lote_no_aplica ------------

    /// <summary>spec stock (guard simétrico del anterior): un <c>idLote</c> provisto sobre un
    /// artículo SIN lote efectivo se rechaza en vez de ignorarse en silencio — mismo criterio que
    /// <c>TransferenciaLoteTests.UnaLineaSinLoteEfectivoConIdLoteProvistoEsRechazadaComoLoteInvalido</c>.</summary>
    [Fact]
    public async Task UnAjusteDeUnArticuloSinLoteConIdLoteProvistoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteDeUnArticuloSinLoteConIdLoteProvistoEsRechazado));
        var idArticuloSinLote = await SembrarArticuloSinLoteAsync(ctx, "articulo-ajuste-sin-lote-idlote", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticuloSinLote, 40m);

        var idArticuloConLote = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-ajeno-ajuste", 10m);
        var idLoteAjeno = await SembrarLoteAsync(ctx, idArticuloConLote, "L-AJENO-AJUSTE", VencimientoLejanoFuturo);

        var solicitud = new SolicitudDeAjusteDeStock(ctx.IdPuntoVenta, idArticuloSinLote, 5m, "Idlote ajeno", idLoteAjeno);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/ajustes", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_no_aplica", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloSinLote && m.Motivo == MotivoStock.Ajuste));
    }

    // ---- task 11.6: disciplina de signo --------------------------------------------------------

    /// <summary>spec lotes-y-vencimientos: "A positive client cantidad is negated by the server" —
    /// el cliente manda 5 (positivo), el movimiento queda -5 y el saldo baja, nunca sube.</summary>
    [Fact]
    public async Task UnDecomisoConCantidadPositivaDelClienteSeNiegaServerSide()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoConCantidadPositivaDelClienteSeNiegaServerSide));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-signo", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-SIGNO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Rotura de envase");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(15m, await LeerStockAsync(ctx, idArticulo));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso);
        Assert.Equal(-5m, movimiento.Cantidad);
        Assert.Equal(idLote, movimiento.IdLote);
    }

    // ---- judgment-day (juez B): cantidad inválida en decomiso ----------------------------------

    /// <summary>spec lotes-y-vencimientos: "the client MUST send a positive cantidad" —
    /// <c>ExigirCantidadDeDecomisoValida</c> rechaza cero y negativo con <c>400
    /// cantidad_de_ajuste_invalida</c>, antes de tocar la base.
    ///
    /// EVIDENCIA DE MUTACIÓN (juez B): anulado el <c>if (cantidad &lt;= 0)</c> de
    /// <c>ExigirCantidadDeDecomisoValida</c> — build, filtro
    /// <c>FullyQualifiedName~UnDecomisoConCantidadCeroOatNegativaEsRechazado</c>: este test
    /// <b>FALLÓ</b> (la cantidad cero/negativa del cliente pasaba sin chequeo). Revertido el
    /// mutante, corrida de nuevo: <b>GREEN</b>.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task UnDecomisoConCantidadCeroONegativaEsRechazado(decimal cantidad)
    {
        var ctx = await PrepararAsync($"{nameof(UnDecomisoConCantidadCeroONegativaEsRechazado)}-{cantidad}");
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-cant-invalida", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-CANT-INVALIDA", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, cantidad, "Cantidad inválida");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_de_ajuste_invalida", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
        Assert.Equal(20m, await LeerStockAsync(ctx, idArticulo));
    }

    /// <summary>spec lotes-y-vencimientos / ck_movimientos_stock_cantidad — misma disciplina de
    /// precisión que <c>ExigirCantidadValida</c>: más de 3 decimales se rechaza con <c>400
    /// cantidad_invalida</c>.</summary>
    [Fact]
    public async Task UnDecomisoConMasDeTresDecimalesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoConMasDeTresDecimalesEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-4-decimales", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-4-DECIMALES", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5.1234m, "Cuatro decimales");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_invalida", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
        Assert.Equal(20m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- task 11.7: idLote requerido en decomiso -----------------------------------------------

    /// <summary>spec lotes-y-vencimientos: "A decomiso of a lot-effective articulo requires
    /// idLote".</summary>
    [Fact]
    public async Task UnDecomisoDeUnArticuloLoteEfectivoSinIdLoteEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnArticuloLoteEfectivoSinIdLoteEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-sin-lote", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, null, 5m, "Rotura sin lote");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_requerido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
        Assert.Equal(20m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- gap de cobertura: lote_invalido en decomiso -------------------------------------------

    /// <summary>dto-contract-honesty: un <c>idLote</c> que no pertenece al artículo se rechaza
    /// explícitamente (<c>ResolverIdLoteEfectivoAsync</c>) en vez de romper con la FK cruda de
    /// <c>stock_lotes</c>.</summary>
    [Fact]
    public async Task UnDecomisoConIdLoteQueNoPerteneceAlArticuloEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoConIdLoteQueNoPerteneceAlArticuloEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-lote-ajeno", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var idOtroArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-otro", 10m);
        var idLoteAjeno = await SembrarLoteAsync(ctx, idOtroArticulo, "L-AJENO-DECOMISO", VencimientoLejanoFuturo);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLoteAjeno, 5m, "Lote ajeno");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day (juez B): decomiso de artículo SIN lote efectivo (rama else, código muerto
    // para tests hasta esta ronda) -----------------------------------------------------------------

    /// <summary>spec lotes-y-vencimientos: un artículo SIN lote efectivo también admite decomiso —
    /// baja el agregado, el movimiento queda con <c>motivo = Decomiso</c> e <c>id_lote = null</c>.
    ///
    /// EVIDENCIA DE MUTACIÓN (juez B): forzado un <c>throw</c> incondicional en la rama
    /// <c>else</c> de <c>EjecutarDecomisoAsync</c> (la que corre cuando <c>idLote is null</c>) —
    /// build, filtro <c>FullyQualifiedName~UnDecomisoDeUnArticuloSinLoteEfectivoEsAceptado</c>:
    /// este test <b>FALLÓ</b> (la rama era código muerto para la suite hasta ahora). Revertido el
    /// mutante, corrida de nuevo: <b>GREEN</b>.</summary>
    [Fact]
    public async Task UnDecomisoDeUnArticuloSinLoteEfectivoEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnArticuloSinLoteEfectivoEsAceptado));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-decomiso-sin-lote-ok", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, null, 5m, "Rotura, artículo sin lote");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal(15m, await LeerStockAsync(ctx, idArticulo));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosStock.SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso);
        Assert.Equal(-5m, movimiento.Cantidad);
        Assert.Null(movimiento.IdLote);
    }

    /// <summary>Contraparte 409 de la anterior: mismo <c>stock_insuficiente_para_decomiso</c> que
    /// el camino lote-efectivo, pero evaluado sobre el agregado (rama <c>else if (nuevaAgregada
    /// &lt; 0m)</c> de <c>EjecutarDecomisoAsync</c>).</summary>
    [Fact]
    public async Task UnDecomisoDeUnArticuloSinLoteEfectivoQueDejariaElAgregadoNegativoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnArticuloSinLoteEfectivoQueDejariaElAgregadoNegativoEsRechazado));
        var idArticulo = await SembrarArticuloSinLoteAsync(ctx, "articulo-decomiso-sin-lote-insuf", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 3m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, null, 5m, "Rotura mayor al saldo agregado");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_insuficiente_para_decomiso", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
        Assert.Equal(3m, await LeerStockAsync(ctx, idArticulo));
    }

    /// <summary>Hallazgo menor del juez B: guard simétrico de <c>lote_no_aplica</c> en decomiso —
    /// mismo criterio que <c>UnAjusteDeUnArticuloSinLoteConIdLoteProvistoEsRechazado</c>, barato
    /// porque <c>ResolverIdLoteEfectivoAsync</c> ya es compartido por ambos servicios.</summary>
    [Fact]
    public async Task UnDecomisoDeUnArticuloSinLoteConIdLoteProvistoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnArticuloSinLoteConIdLoteProvistoEsRechazado));
        var idArticuloSinLote = await SembrarArticuloSinLoteAsync(ctx, "articulo-decomiso-sin-lote-idlote", 10m);
        await SembrarStockAgregadoAsync(ctx, idArticuloSinLote, 20m);

        var idArticuloConLote = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-ajeno-decomiso-idlote", 10m);
        var idLoteAjeno = await SembrarLoteAsync(ctx, idArticuloConLote, "L-AJENO-DECOMISO-IDLOTE", VencimientoLejanoFuturo);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticuloSinLote, idLoteAjeno, 5m, "Idlote ajeno en decomiso");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lote_no_aplica", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticuloSinLote && m.Motivo == MotivoStock.Decomiso));
    }

    // ---- task 11.5: stock_insuficiente_para_decomiso -------------------------------------------

    /// <summary>spec lotes-y-vencimientos: "A decomiso that would go negative is refused" — back-
    /// office tightening, mismo criterio asimétrico que una transferencia (nunca una venta).</summary>
    [Fact]
    public async Task UnDecomisoQueDejariaElLoteNegativoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoQueDejariaElLoteNegativoEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-insuficiente", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-INSUF", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 3m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 3m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Rotura mayor al saldo");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_insuficiente_para_decomiso", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));

        // El rollback deja TODO exactamente como estaba.
        Assert.Equal(3m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
        Assert.Equal(3m, await LeerStockAsync(ctx, idArticulo));
    }

    // ---- task 11.8: decomiso de lote NO vencido permitido (decisión 9) --------------------------

    /// <summary>spec lotes-y-vencimientos: "Decomiso applies to a non-expired lot too" — decisión 9
    /// del proposal: decomiso NO restringido a lotes vencidos, la merma real (rotura) entra en el
    /// mismo cajón que la vencida.</summary>
    [Fact]
    public async Task UnDecomisoDeUnLoteNoVencidoEsPermitido()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnLoteNoVencidoEsPermitido));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-no-vencido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-NO-VENCIDO-DECOMISO", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Rotura, lote no vencido");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }

    /// <summary>Contraparte del anterior con un lote VENCIDO — decomiso también se permite, mismo
    /// resultado, probando que la fecha de vencimiento nunca entra en la decisión.</summary>
    [Fact]
    public async Task UnDecomisoDeUnLoteVencidoTambienEsPermitido()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoDeUnLoteVencidoTambienEsPermitido));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-vencido", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-VENCIDO-DECOMISO", VencimientoLejanoPasado);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Merma por vencimiento");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal(15m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }

    // ---- task 11.9: observaciones obligatoria ----------------------------------------------------

    /// <summary>spec lotes-y-vencimientos: "Decomiso without observaciones is rejected".</summary>
    [Fact]
    public async Task UnDecomisoSinObservacionesEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnDecomisoSinObservacionesEsRechazado));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-sin-obs", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-SIN-OBS", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "");
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("observaciones_requeridas", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
    }

    // ---- task 11.4: mutation target — Vendedor-403 -----------------------------------------------

    /// <summary>Mutation target (spec: "Vendedor is blocked from decomiso"; mutation-proof-tests):
    /// el grupo <c>/api/stock</c> apila SOLO <c>OperacionDePos</c> — cualquier rol autenticado pasa
    /// esa política. Es <c>Politicas.GestionDeCatalogo</c> apilada en <c>/decomiso</c>
    /// (<c>StockEndpoints.cs</c>) quien bloquea al Vendedor.
    ///
    /// EVIDENCIA DE MUTACIÓN: borrado <c>.RequireAuthorization(Politicas.GestionDeCatalogo)</c> de
    /// <c>/decomiso</c> en <c>StockEndpoints.cs</c> — build, filtro
    /// <c>FullyQualifiedName~UnVendedorEsBloqueadoDelDecomiso</c>: este test <b>FALLÓ</b> (200 en
    /// vez de 403 — el Vendedor pasa con solo <c>OperacionDePos</c> del grupo). Revertido el
    /// mutante, corrida de nuevo: <b>GREEN</b>.</summary>
    [Fact]
    public async Task UnVendedorEsBloqueadoDelDecomiso()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsBloqueadoDelDecomiso));
        var idArticulo = await SembrarArticuloLoteEfectivoAsync(ctx, "articulo-decomiso-vendedor", 10m);
        var idLote = await SembrarLoteAsync(ctx, idArticulo, "L-VENDEDOR", VencimientoLejanoFuturo);
        await SembrarStockLoteAsync(ctx, idArticulo, idLote, 20m);
        await SembrarStockAgregadoAsync(ctx, idArticulo, 20m);

        var vendedor = await CrearClienteVendedorAsync(ctx, "vendedor-decomiso");

        var solicitud = new SolicitudDeDecomiso(ctx.IdPuntoVenta, idArticulo, idLote, 5m, "Intento de vendedor");
        var respuesta = await vendedor.PostAsJsonAsync("/api/stock/decomiso", solicitud);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Decomiso));
        Assert.Equal(20m, await LeerStockLoteAsync(ctx, idArticulo, idLote));
    }
}
