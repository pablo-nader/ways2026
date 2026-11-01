using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// ABM de sucursales y movimiento de una caja entre sucursales de su empresa, contra Postgres real como <c>ways_app</c>.
/// Escenario: el tenant aprovisionado trae <c>Local 1</c> con su sucursal; cada prueba agrega las cajas que necesita.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SucursalesAbmTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, HttpClient Admin, int IdCaja1, int IdSucursal1, int IdArea, int IdAlicuotaIva);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area sucursales", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idSucursal1 = await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta);
        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, admin, resultado.IdPuntoVenta, idSucursal1, area.Id, idAlicuota);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertConflictoAsync(HttpResponseMessage respuesta, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static async Task<PuntoVentaListado> CrearCajaAsync(Contexto ctx, string nombre, int? idSucursal = null, int? idEmpresa = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta",
            new PuntoVentaAlta(idEmpresa ?? ctx.IdEmpresa, nombre, ModoPuntoVenta.Web, IdSucursal: idSucursal));
        return await LeerAsync<PuntoVentaListado>(respuesta, HttpStatusCode.Created);
    }

    private static async Task<SucursalListado> CrearSucursalAsync(Contexto ctx, string nombre, int? idEmpresa = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(idEmpresa ?? ctx.IdEmpresa, nombre));
        return await LeerAsync<SucursalListado>(respuesta, HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> MoverAsync(Contexto ctx, int idCaja, int? idSucursal) =>
        ctx.Admin.PostAsJsonAsync($"/api/puntos-venta/{idCaja}/sucursal", new PuntoVentaSucursalEdicion(idSucursal));

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = ctx.IdArea,
            IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task SembrarStockAsync(Contexto ctx, int idSucursal, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdSucursal = idSucursal, IdArticulo = idArticulo, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<SucursalListado>> ListarAsync(Contexto ctx, string consulta = "") =>
        await LeerAsync<List<SucursalListado>>(await ctx.Admin.GetAsync($"/api/sucursales{consulta}"), HttpStatusCode.OK);

    private async Task<int> SucursalDeLaCajaAsync(Contexto ctx, int idCaja)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await SucursalDePrueba.DeAsync(db, idCaja);
    }

    [Fact]
    public async Task SeCreaListaYRenombraUnaSucursalYQuedaElRastro()
    {
        var ctx = await PrepararAsync(nameof(SeCreaListaYRenombraUnaSucursalYQuedaElRastro));

        var creada = await CrearSucursalAsync(ctx, "  Depósito  ");
        Assert.Equal("Depósito", creada.Nombre);
        Assert.Equal(ctx.IdEmpresa, creada.IdEmpresa);
        Assert.Equal(0, creada.CantidadPuntosVenta);

        var lista = await ListarAsync(ctx, $"?idEmpresa={ctx.IdEmpresa}");
        Assert.Equal(2, lista.Count);
        Assert.Equal(1, lista.Single(s => s.Id == ctx.IdSucursal1).CantidadPuntosVenta);
        Assert.Contains(lista, s => s.Id == creada.Id && s.RazonSocialEmpresa is not null);
        Assert.Empty(await ListarAsync(ctx, "?idEmpresa=2000000000"));

        var renombrada = await LeerAsync<SucursalListado>(
            await ctx.Admin.PutAsJsonAsync($"/api/sucursales/{creada.Id}", new SucursalEdicion("Depósito central")),
            HttpStatusCode.OK);
        Assert.Equal("Depósito central", renombrada.Nombre);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var acciones = await db.Auditoria.Where(a => a.IdEntidad == creada.Id && a.Entidad == "sucursal")
            .OrderBy(a => a.Id).Select(a => a.Accion).ToListAsync();
        Assert.Equal(["sucursal.alta", "sucursal.edicion"], acciones);

        var sinNombre = await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(ctx.IdEmpresa, "  "));
        await AssertCodigoAsync(sinNombre, HttpStatusCode.BadRequest, "nombre_sucursal_requerido");
        var sinEmpresa = await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(null, "X"));
        await AssertCodigoAsync(sinEmpresa, HttpStatusCode.BadRequest, "empresa_requerida");
    }

    /// <summary>update-404-first: una sucursal inexistente contesta 404 aunque el cuerpo sea inválido.</summary>
    [Fact]
    public async Task UnaSucursalInexistenteContesta404ANTESDeValidarElCuerpo()
    {
        var ctx = await PrepararAsync(nameof(UnaSucursalInexistenteContesta404ANTESDeValidarElCuerpo));

        var put = await ctx.Admin.PutAsJsonAsync("/api/sucursales/2000000000", new SucursalEdicion(""));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        var delete = await ctx.Admin.DeleteAsync("/api/sucursales/2000000000");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var mover = await ctx.Admin.PostAsJsonAsync("/api/puntos-venta/2000000000/sucursal", new PuntoVentaSucursalEdicion(null));
        Assert.Equal(HttpStatusCode.NotFound, mover.StatusCode);
    }

    /// <summary>Cláusula bajo prueba: <c>sucursal.IdEmpresa != idEmpresa</c> en <c>BuscarSucursalDeLaEmpresaAsync</c>, en el
    /// alta de la caja. Una sucursal de otra empresa del mismo tenant es 409; una de otro tenant, invisible, es 404; una
    /// dada de baja, 404; sin sucursal, la caja sigue naciendo con la propia.</summary>
    [Fact]
    public async Task LaCajaNaceEnLaSucursalElegidaSiEsDeSuEmpresaYVivaYSinEllaConLaPropia()
    {
        var ctx = await PrepararAsync(nameof(LaCajaNaceEnLaSucursalElegidaSiEsDeSuEmpresaYVivaYSinEllaConLaPropia));
        var otro = await PrepararAsync(nameof(LaCajaNaceEnLaSucursalElegidaSiEsDeSuEmpresaYVivaYSinEllaConLaPropia) + "Otro");

        var elegida = await CrearCajaAsync(ctx, "Local 2", idSucursal: ctx.IdSucursal1);
        Assert.Equal(ctx.IdSucursal1, elegida.IdSucursal);
        Assert.Equal(2, (await ListarAsync(ctx)).Single(s => s.Id == ctx.IdSucursal1).CantidadPuntosVenta);
        Assert.Single(await ListarAsync(ctx));

        var propia = await CrearCajaAsync(ctx, "Local 3");
        Assert.NotEqual(ctx.IdSucursal1, propia.IdSucursal);
        Assert.Equal("Local 3", propia.NombreSucursal);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa2 = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Segunda SA", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(empresa2);
            await db.SaveChangesAsync();

            var deOtraEmpresa = await CrearSucursalAsync(ctx, "De la segunda", empresa2.Id);
            var rechazo = await ctx.Admin.PostAsJsonAsync(
                "/api/puntos-venta",
                new PuntoVentaAlta(ctx.IdEmpresa, "Local 4", ModoPuntoVenta.Web, IdSucursal: deOtraEmpresa.Id));
            await AssertConflictoAsync(rechazo, "sucursal_de_otra_empresa");

            var conMovimiento = await MoverAsync(ctx, propia.Id, deOtraEmpresa.Id);
            await AssertConflictoAsync(conMovimiento, "sucursal_de_otra_empresa");
            Assert.Equal(propia.IdSucursal, await SucursalDeLaCajaAsync(ctx, propia.Id));
        }

        var deOtroTenant = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta",
            new PuntoVentaAlta(ctx.IdEmpresa, "Local 5", ModoPuntoVenta.Web, IdSucursal: otro.IdSucursal1));
        Assert.Equal(HttpStatusCode.NotFound, deOtroTenant.StatusCode);

        var vacia = await CrearSucursalAsync(ctx, "Vacía");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/sucursales/{vacia.Id}")).StatusCode);
        var dadaDeBaja = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta",
            new PuntoVentaAlta(ctx.IdEmpresa, "Local 6", ModoPuntoVenta.Web, IdSucursal: vacia.Id));
        Assert.Equal(HttpStatusCode.NotFound, dadaDeBaja.StatusCode);
    }

    /// <summary>Una caja movida a otra sucursal comparte su stock: lo que se cargó en la sucursal destino lo ve la caja
    /// movida, y el stock de ninguna sucursal cambia por el movimiento. La sucursal que quedó sin cajas ni uso cae.</summary>
    [Fact]
    public async Task UnaCajaMovidaALaSucursalDeOtraComparteSuStock()
    {
        var ctx = await PrepararAsync(nameof(UnaCajaMovidaALaSucursalDeOtraComparteSuStock));
        var caja2 = await CrearCajaAsync(ctx, "Local 2");
        var articulo = await SembrarArticuloAsync(ctx, "compartido");
        await SembrarStockAsync(ctx, ctx.IdSucursal1, articulo, 12m);

        var movida = await LeerAsync<PuntoVentaListado>(await MoverAsync(ctx, caja2.Id, ctx.IdSucursal1), HttpStatusCode.OK);
        Assert.Equal(ctx.IdSucursal1, movida.IdSucursal);

        foreach (var idCaja in new[] { ctx.IdCaja1, caja2.Id })
        {
            var existencias = await LeerAsync<Existencias>(
                await ctx.Admin.GetAsync($"/api/reportes/stock/existencias?idPuntoVenta={idCaja}"), HttpStatusCode.OK);
            Assert.Equal(ctx.IdSucursal1, existencias.IdSucursal);
            Assert.Equal(12m, Assert.Single(existencias.Filas).Cantidad);
        }

        var sucursales = await ListarAsync(ctx);
        Assert.Equal([ctx.IdSucursal1], sucursales.Select(s => s.Id));
        Assert.Equal(2, sucursales[0].CantidadPuntosVenta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(12m, await db.Stock.Where(s => s.IdArticulo == articulo).SumAsync(s => s.Cantidad));
        Assert.Equal(1, await db.Stock.CountAsync(s => s.IdArticulo == articulo));

        var acciones = await db.Auditoria.Where(a => a.Accion == "pv.sucursal" || a.Accion == "sucursal.baja")
            .OrderBy(a => a.Id).Select(a => a.Accion).ToListAsync();
        Assert.Equal(["pv.sucursal", "sucursal.baja"], acciones);

        // El mismo destino no hace nada ni deja rastro.
        await LeerAsync<PuntoVentaListado>(await MoverAsync(ctx, caja2.Id, ctx.IdSucursal1), HttpStatusCode.OK);
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "pv.sucursal"));
    }

    /// <summary>Cláusula bajo prueba: el guard de uso de la sucursal de origen en <c>MoverPuntoVentaDeSucursalAsync</c>.
    /// Si la caja es la última de una sucursal que guarda stock, el movimiento se rechaza y nada cambia. Si quedan otras
    /// cajas, la sucursal con stock sigue viva y el movimiento pasa.</summary>
    [Fact]
    public async Task NoSeMueveLaUltimaCajaDeUnaSucursalQueTodaviaTieneStock()
    {
        var ctx = await PrepararAsync(nameof(NoSeMueveLaUltimaCajaDeUnaSucursalQueTodaviaTieneStock));
        var caja2 = await CrearCajaAsync(ctx, "Local 2");
        var caja3 = await CrearCajaAsync(ctx, "Local 3", idSucursal: caja2.IdSucursal);
        var articulo = await SembrarArticuloAsync(ctx, "con-stock");
        await SembrarStockAsync(ctx, caja2.IdSucursal, articulo, 5m);

        // Local 2 y Local 3 comparten sucursal con stock: sacar a una deja a la otra, así que pasa.
        await LeerAsync<PuntoVentaListado>(await MoverAsync(ctx, caja3.Id, ctx.IdSucursal1), HttpStatusCode.OK);
        Assert.Equal(caja2.IdSucursal, await SucursalDeLaCajaAsync(ctx, caja2.Id));
        Assert.Contains(await ListarAsync(ctx), s => s.Id == caja2.IdSucursal);

        // Local 2 es ahora la última caja de una sucursal con stock.
        var rechazo = await MoverAsync(ctx, caja2.Id, ctx.IdSucursal1);
        await AssertConflictoAsync(rechazo, "sucursal_en_uso");
        Assert.Equal(caja2.IdSucursal, await SucursalDeLaCajaAsync(ctx, caja2.Id));
        Assert.Contains(await ListarAsync(ctx), s => s.Id == caja2.IdSucursal);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(5m, await db.Stock.Where(s => s.IdSucursal == caja2.IdSucursal).SumAsync(s => s.Cantidad));
    }

    /// <summary>Cláusulas bajo prueba: las dos guardas de <c>EliminarSucursalAsync</c>. Con cajas vivas es
    /// <c>sucursal_con_puntos_venta</c>; sin cajas pero con stock, <c>sucursal_en_uso</c>; vacía, se da de baja y deja de
    /// listarse.</summary>
    [Fact]
    public async Task UnaSucursalConCajasOConStockNoSeDaDeBaja()
    {
        var ctx = await PrepararAsync(nameof(UnaSucursalConCajasOConStockNoSeDaDeBaja));

        var conCajas = await ctx.Admin.DeleteAsync($"/api/sucursales/{ctx.IdSucursal1}");
        await AssertConflictoAsync(conCajas, "sucursal_con_puntos_venta");

        var conStock = await CrearSucursalAsync(ctx, "Depósito con stock");
        var articulo = await SembrarArticuloAsync(ctx, "en-deposito");
        await SembrarStockAsync(ctx, conStock.Id, articulo, 3m);
        await AssertConflictoAsync(await ctx.Admin.DeleteAsync($"/api/sucursales/{conStock.Id}"), "sucursal_en_uso");

        var vacia = await CrearSucursalAsync(ctx, "Vacía");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/sucursales/{vacia.Id}")).StatusCode);

        var ids = (await ListarAsync(ctx)).Select(s => s.Id).ToList();
        Assert.Contains(ctx.IdSucursal1, ids);
        Assert.Contains(conStock.Id, ids);
        Assert.DoesNotContain(vacia.Id, ids);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "sucursal.baja" && a.IdEntidad == vacia.Id));
    }

    /// <summary>La transferencia entre sucursales por id de sucursal mueve los saldos igual que por caja; mezclar las dos
    /// formas, dejar una a medias o pedir la misma sucursal se rechaza con 400; las cajas siguen funcionando.</summary>
    [Fact]
    public async Task LaTransferenciaAceptaDosSucursalesOdosCajasPeroNoMezcladas()
    {
        var ctx = await PrepararAsync(nameof(LaTransferenciaAceptaDosSucursalesOdosCajasPeroNoMezcladas));
        var caja2 = await CrearCajaAsync(ctx, "Local 2");
        var articulo = await SembrarArticuloAsync(ctx, "a-transferir");
        await SembrarStockAsync(ctx, ctx.IdSucursal1, articulo, 10m);
        var lineas = new[] { new LineaDeTransferencia(articulo, 3m) };

        var porSucursal = await LeerAsync<ResultadoTransferencia>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/stock/transferencias",
                new SolicitudDeTransferencia(null, null, "Por sucursal", lineas, ctx.IdSucursal1, caja2.IdSucursal)),
            HttpStatusCode.OK);
        Assert.Equal(ctx.IdSucursal1, porSucursal.IdSucursalOrigen);
        Assert.Equal(caja2.IdSucursal, porSucursal.IdSucursalDestino);
        Assert.Equal(ctx.IdCaja1, porSucursal.IdPuntoVentaOrigen);
        Assert.Equal(caja2.Id, porSucursal.IdPuntoVentaDestino);
        Assert.Equal(7m, Assert.Single(porSucursal.Lineas).CantidadOrigen);

        var porCaja = await LeerAsync<ResultadoTransferencia>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/stock/transferencias",
                new SolicitudDeTransferencia(caja2.Id, ctx.IdCaja1, "Por caja", lineas)),
            HttpStatusCode.OK);
        Assert.Equal(10m, Assert.Single(porCaja.Lineas).CantidadDestino);

        foreach (var invalida in new[]
        {
            new SolicitudDeTransferencia(ctx.IdCaja1, null, "Mezclada", lineas, null, caja2.IdSucursal),
            new SolicitudDeTransferencia(ctx.IdCaja1, caja2.Id, "Las dos", lineas, ctx.IdSucursal1, caja2.IdSucursal),
            new SolicitudDeTransferencia(null, null, "Ninguna", lineas),
            new SolicitudDeTransferencia(null, null, "A medias", lineas, ctx.IdSucursal1, null)
        })
        {
            await AssertCodigoAsync(
                await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", invalida),
                HttpStatusCode.BadRequest, "transferencia_origen_destino_invalido");
        }

        await AssertCodigoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/stock/transferencias",
                new SolicitudDeTransferencia(null, null, "Misma", lineas, ctx.IdSucursal1, ctx.IdSucursal1)),
            HttpStatusCode.BadRequest, "transferencia_origen_igual_destino");

        var inexistente = await ctx.Admin.PostAsJsonAsync(
            "/api/stock/transferencias",
            new SolicitudDeTransferencia(null, null, "Inexistente", lineas, ctx.IdSucursal1, 2_000_000_000));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    /// <summary>La cabecera de los exports de stock dice "Sucursal: nombre" y no "Punto de venta: Sucursal N".</summary>
    [Fact]
    public async Task ElExportDeExistenciasRotulaLaSucursalPorSuNombre()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeExistenciasRotulaLaSucursalPorSuNombre));
        var nombre = (await ListarAsync(ctx)).Single().Nombre;

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/existencias/export?idSucursal={ctx.IdSucursal1}&formato=xlsx");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        using var libro = new ClosedXML.Excel.XLWorkbook(await respuesta.Content.ReadAsStreamAsync());
        var hoja = libro.Worksheets.First();
        var encabezado = hoja.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains($"Sucursal: {nombre}", encabezado);
        Assert.DoesNotContain(encabezado, c => c.StartsWith("Punto de venta:", StringComparison.Ordinal));
    }
}
