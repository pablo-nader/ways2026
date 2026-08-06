using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 2 (the centerpiece): el ciclo de vida
/// completo de <c>ServicioDeCompras</c> a través de la API real (tasks 2.8, 2.9, 2.10, 2.11,
/// 2.14; design: Transactions — CONFIRMAR COMPRA).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasLifecycleTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "vendedor-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Vendedor,
        int IdProveedor, int IdArticulo, int IdArticulo2, int IdAlicuotaIva21, int IdTipoCFA, int IdTipoCFB);

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
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-2-{Guid.NewGuid():N}", Nombre = "Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        var hasheador = new HasheadorPbkdf2();
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = resultado.IdTenant, NombreUsuario = "vendedor", Mail = mailVendedor, RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, vendedor,
            proveedor.Id, articulo1.Id, articulo2.Id, idAlicuotaIva21, idTipoCFA, idTipoCFB);
    }

    private static SolicitudDeCompra SolicitudSimple(
        Contexto ctx, decimal unidades = 10m, decimal costoUnitario = 100m, string? numeroExterno = "0001-00000001",
        bool actualizaCosto = true, int? idArticulo = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(idArticulo ?? ctx.IdArticulo, "Item de prueba", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, actualizaCosto)]);

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

    // ---- task 2.8: borrador CRUD, sin efecto de ledger -----------------------------------------

    [Fact]
    public async Task UnBorradorSeCreaSinItemsYNoGeneraMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorSeCreaSinItemsYNoGeneraMovimientos));

        var solicitud = new SolicitudDeCompra(ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, null, null, null, []);
        var creada = await CrearBorradorAsync(ctx, solicitud);

        Assert.Equal(EstadoCompra.Borrador, creada.Estado);
        Assert.Empty(creada.Items);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    [Fact]
    public async Task ItemsSeAgreganYQuitanEnVariosRequestsSinGenerarMovimientos()
    {
        var ctx = await PrepararAsync(nameof(ItemsSeAgreganYQuitanEnVariosRequestsSinGenerarMovimientos));
        var creada = await CrearBorradorAsync(ctx);

        var conDosItems = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-00000002", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 1", 5m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, true),
                new LineaDeCompraSolicitada(ctx.IdArticulo2, "Item 2", 3m, null, null, 30m, 0m, ctx.IdAlicuotaIva21, true)
            ]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", conDosItems);
        Assert.Equal(HttpStatusCode.OK, respuestaPut.StatusCode);
        var actualizada = (await respuestaPut.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson))!;
        Assert.Equal(2, actualizada.Items.Count);

        var conUnItem = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-00000002", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 1", 5m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, true)]);
        var respuestaPut2 = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", conUnItem);
        Assert.Equal(HttpStatusCode.OK, respuestaPut2.StatusCode);
        var conMenosItems = (await respuestaPut2.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson))!;
        Assert.Single(conMenosItems.Items);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
        Assert.Equal(1, await db.ItemsComprobanteCompra.CountAsync(i => i.IdComprobanteCompra == creada.Id));
    }

    [Fact]
    public async Task UnaCompraConfirmadaRechazaLaEdicionDeItems()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraConfirmadaRechazaLaEdicionDeItems));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", SolicitudSimple(ctx));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_editable", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaCompraAnuladaRechazaLaEdicionDeItems()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaRechazaLaEdicionDeItems));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", SolicitudSimple(ctx));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_editable", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.9: numero_externo identity + dedupe -----------------------------------------------

    /// <summary>La unicidad parcial (<c>estado &lt;&gt; 'anulada'</c>) alcanza a un borrador Y a
    /// una confirmada por igual (design: Backstop Map — "genuine race: two concurrent SAVES",
    /// no "two concurrent confirms") — con la primera ya confirmada, el segundo choca al
    /// intentar GUARDAR el mismo <c>numero_externo</c>, antes de siquiera llegar a confirmar.</summary>
    [Fact]
    public async Task ElMismoNumeroExternoDeUnaConfirmadaNoSePuedeReusarEnOtroBorrador()
    {
        var ctx = await PrepararAsync(nameof(ElMismoNumeroExternoDeUnaConfirmadaNoSePuedeReusarEnOtroBorrador));

        var primera = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: "0003-00012345"));
        await ConfirmarAsync(ctx, primera.Id);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, numeroExterno: "0003-00012345"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_duplicada", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnNumeroExternoAnuladoSePuedeReingresarYConfirmar()
    {
        var ctx = await PrepararAsync(nameof(UnNumeroExternoAnuladoSePuedeReingresarYConfirmar));

        var primera = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: "0003-00099999"));
        await ConfirmarAsync(ctx, primera.Id);
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{primera.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var segunda = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: "0003-00099999"));
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{segunda.Id}/confirmar", null);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task ConfirmarSinNumeroExternoEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarSinNumeroExternoEsRechazado));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: null));

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_numero_externo_requerido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Test diferido de la Slice 1 (task deferred): la unicidad usa <c>citext</c> —
    /// "F-0001" y "f-0001" son el MISMO valor para el índice parcial, así que guardar el
    /// segundo borrador choca contra la primera fila con el mismo <c>23505</c> traducido.</summary>
    [Fact]
    public async Task UnNumeroExternoDifiereSoloEnMayusculasChocaPorCitext()
    {
        var ctx = await PrepararAsync(nameof(UnNumeroExternoDifiereSoloEnMayusculasChocaPorCitext));

        var primera = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, numeroExterno: "F-0001"));
        await ConfirmarAsync(ctx, primera.Id);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, numeroExterno: "f-0001"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_duplicada", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.10: confirmar escribe stock + cache + costo juntos ---------------------------------

    [Fact]
    public async Task ConfirmarEscribeMovimientoCacheYCostoNominalJuntos()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarEscribeMovimientoCacheYCostoNominalJuntos));

        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-00000010", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(ctx.IdArticulo, "Actualiza costo", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true),
                new LineaDeCompraSolicitada(ctx.IdArticulo2, "No actualiza costo", 5m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, false)
            ]);
        var creada = await CrearBorradorAsync(ctx, solicitud);

        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        Assert.Equal(EstadoCompra.Confirmada, confirmada.Estado);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        Assert.Equal(2, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Compra));

        var stock1 = await db.Stock.Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        var stock2 = await db.Stock.Where(s => s.IdArticulo == ctx.IdArticulo2 && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(10m, stock1);
        Assert.Equal(5m, stock2);

        var articulo1 = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
        var articulo2 = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo2);
        // C-FA discrimina IVA: costoEfectivo = total * (1 + 21%) / cantidad = 100 * 1.21 = 121.
        Assert.Equal(121.00m, articulo1.CostoNominal);
        Assert.Null(articulo2.CostoNominal);
    }

    [Fact]
    public async Task ConfirmarUnaCompraYaConfirmadaEsRechazadaSinDuplicarMovimientos()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarUnaCompraYaConfirmadaEsRechazadaSinDuplicarMovimientos));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);

        var segundo = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);

        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_ya_procesada", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    [Fact]
    public async Task ConfirmarUnBorradorSinItemsEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarUnBorradorSinItemsEsRechazado));
        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-00000099", DateOnly.FromDateTime(DateTime.UtcNow), null, []);
        var creada = await CrearBorradorAsync(ctx, solicitud);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_sin_items", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.11: precio_sugerido es una sugerencia, nunca auto-aplicada ------------------------

    [Fact]
    public async Task ElBorradorCalculaYGuardaElPrecioSugeridoSinAbrirNingunPrecio()
    {
        var ctx = await PrepararAsync(nameof(ElBorradorCalculaYGuardaElPrecioSugeridoSinAbrirNingunPrecio));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, costoUnitario: 100m));

        // C-FA (discrimina IVA): costoEfectivo = 121; margen proveedor 50% -> sugerido = 181.5.
        Assert.Equal(181.5m, creada.Items[0].PrecioSugerido);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Precios.CountAsync());
    }

    [Fact]
    public async Task ConfirmarNoAbreUnPrecioYAplicarLoHaceRespetandoHistoria()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarNoAbreUnPrecioYAplicarLoHaceRespetandoHistoria));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, costoUnitario: 100m));
        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(181.5m, confirmada.Items[0].PrecioSugerido);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(0, await db.Precios.CountAsync());
        }

        int idListaPrecio;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lista = new ListaPrecio
            {
                IdTenant = ctx.IdTenant, Nombre = "Lista de prueba", EsDefault = false, Modo = ModoLista.Fija,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.ListasPrecio.Add(lista);
            await db.SaveChangesAsync();
            idListaPrecio = lista.Id;
        }

        var respuestaAplicar = await ctx.Admin.PostAsJsonAsync(
            $"/api/compras/{creada.Id}/precios", new SolicitudDeAplicarPrecios(idListaPrecio));
        Assert.Equal(HttpStatusCode.OK, respuestaAplicar.StatusCode);
        var resultados = (await respuestaAplicar.Content.ReadFromJsonAsync<List<ResultadoAplicarPrecio>>(OpcionesJson))!;

        Assert.Single(resultados);
        Assert.True(resultados[0].Aplicado);
        Assert.Equal(181.5m, resultados[0].Precio);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(1, await db.Precios.CountAsync());
            var precio = await db.Precios.FirstAsync();
            Assert.Equal(181.5m, precio.Monto);
            Assert.Null(precio.VigenteHasta);
        }
    }

    // ---- task 2.14: autorización -------------------------------------------------------------------

    [Fact]
    public async Task AdminConfirmaYAnula()
    {
        var ctx = await PrepararAsync(nameof(AdminConfirmaYAnula));
        var creada = await CrearBorradorAsync(ctx);
        var confirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        Assert.Equal(HttpStatusCode.OK, confirmar.StatusCode);

        var anular = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anular.StatusCode);
    }

    [Fact]
    public async Task VendedorEsBloqueadoEnTodoCaminoDeEscrituraDeCompra()
    {
        var ctx = await PrepararAsync(nameof(VendedorEsBloqueadoEnTodoCaminoDeEscrituraDeCompra));
        var creada = await CrearBorradorAsync(ctx);

        var crear = await ctx.Vendedor.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, numeroExterno: "vendedor-1"));
        Assert.Equal(HttpStatusCode.Forbidden, crear.StatusCode);

        var editar = await ctx.Vendedor.PutAsJsonAsync($"/api/compras/{creada.Id}", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, editar.StatusCode);

        var confirmar = await ctx.Vendedor.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        Assert.Equal(HttpStatusCode.Forbidden, confirmar.StatusCode);

        await ConfirmarAsync(ctx, creada.Id);
        var anular = await ctx.Vendedor.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Forbidden, anular.StatusCode);

        var precios = await ctx.Vendedor.PostAsJsonAsync($"/api/compras/{creada.Id}/precios", new SolicitudDeAplicarPrecios(1));
        Assert.Equal(HttpStatusCode.Forbidden, precios.StatusCode);
    }

    [Fact]
    public async Task VendedorPuedeLeerElListadoDeCompras()
    {
        var ctx = await PrepararAsync(nameof(VendedorPuedeLeerElListadoDeCompras));
        await CrearBorradorAsync(ctx);

        var respuesta = await ctx.Vendedor.GetAsync("/api/compras");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- invariante extendido: motivo=Compra participa del stock.cantidad = Σ movimientos -----

    [Fact]
    public async Task LaCantidadDeStockEsLaSumaDeMovimientosIncluyendoCompra()
    {
        var ctx = await PrepararAsync(nameof(LaCantidadDeStockEsLaSumaDeMovimientosIncluyendoCompra));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 25m));
        await ConfirmarAsync(ctx, creada.Id);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        var sumaDeMovimientos = await db.MovimientosStock
            .Where(m => m.IdArticulo == ctx.IdArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
            .SumAsync(m => m.Cantidad);

        Assert.Equal(25m, cantidad);
        Assert.Equal(cantidad, sumaDeMovimientos);
    }
}
