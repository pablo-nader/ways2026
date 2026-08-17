using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-13-stock-inteligente, Slice 4 (task 4.10): <c>GET /api/reportes/stock/reposicion/export</c>
/// — equality fila-por-fila (mutation-proof-tests regla 6: dos filas con TODAS las columnas
/// distintas, incluida la celda vacía de proveedor de la fila "Sin proveedor" y la celda vacía de
/// <c>sugerido</c> nulo, nunca <c>0</c>), el cap (design decisión 13: agregado acotado por
/// catálogo, mismo shape que <c>/stock/existencias/export</c>, "refuses rather than truncates") y
/// el 403 del gate. La clasificación y el 403 del JSON viven en <see cref="ReposicionReporteTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReposicionExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
    {
        var host = factory ?? fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area reposicion export", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, vendedor);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarProveedorAsync(Contexto ctx, string razonSocial)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = razonSocial, IdCondicionFiscal = idCondicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return proveedor.Id;
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, int? idProveedorHabitual = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, IdProveedorHabitual = idProveedorHabitual, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task SembrarStockAsync(Contexto ctx, int idArticulo, decimal cantidad, decimal? minimo, decimal? reposicion)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> LlamarExportAsync(HttpClient cliente, int idPuntoVenta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/reportes/stock/reposicion/export?idPuntoVenta={idPuntoVenta}&formato={formato}");

    // ---- task 4.10: equality fila-por-fila, incluida la celda vacía de "Sin proveedor" y de
    // sugerido nulo (mutation-proof-tests regla 6) ---------------------------------------------------

    /// <summary>Nombra el objetivo de mutación (mutation-proof-tests regla 6): el call site de
    /// <c>ExportacionDeReportes.De(Reposicion, ContextoDeExportacion)</c> dentro del endpoint
    /// <c>/stock/reposicion/export</c>. Dos filas con TODAS las columnas distintas (artículo,
    /// cantidad, mínimo, reposición, sugerido, proveedor) — una con proveedor asignado y sugerido
    /// numérico, otra "Sin proveedor" con <c>reposicion</c> sin configurar (sugerido nulo): ambas
    /// celdas vacías (proveedor y sugerido) quedan cubiertas en la misma fila.</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonEnTodasLasColumnasIncluidasLasCeldasVacias()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonEnTodasLasColumnasIncluidasLasCeldasVacias));

        var idProveedor = await SembrarProveedorAsync(ctx, "Proveedor export");
        var idArticuloA = await SembrarArticuloAsync(ctx, "Detergente 750ml", idProveedor);
        await SembrarStockAsync(ctx, idArticuloA, cantidad: 3m, minimo: 5m, reposicion: 15m);

        var idArticuloB = await SembrarArticuloAsync(ctx, "Papel higienico x4");
        await SembrarStockAsync(ctx, idArticuloB, cantidad: 0m, minimo: 0m, reposicion: null);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/reposicion?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reposicion = JsonSerializer.Deserialize<Reposicion>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, reposicion.Filas.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (headers), los datos empiezan en la fila 7 — mismo layout que
        // Existencias/Vencimientos (patrón: ExistenciasExportTests.ElExportEsIgualAlEndpointJsonParaLasDosFilas).
        // El header es lo que ata cada celda de datos a su columna: sin este assert un swap de
        // labels ("Reposición"/"Sugerido") pasa inadvertido porque el test de igualdad de abajo
        // solo lee celdas por posición (judgment-day round 1, hallazgo confirmado #1).
        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Artículo", "Nombre", "Cantidad", "Mínimo", "Reposición", "Sugerido", "Proveedor"],
            Enumerable.Range(1, 7).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        // Orden: proveedor ASC NULLS LAST, así que A (con proveedor) precede a B (Sin proveedor).
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < reposicion.Filas.Count; i++)
        {
            var esperado = reposicion.Filas[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(esperado.IdArticulo, fila.Cell(1).GetValue<int>());
            Assert.Equal(esperado.Articulo, fila.Cell(2).GetString());
            Assert.Equal(esperado.Cantidad, fila.Cell(3).GetValue<decimal>());
            Assert.Equal(esperado.Minimo, fila.Cell(4).GetValue<decimal>());

            if (esperado.Reposicion is { } reposicionEsperada)
            {
                Assert.Equal(reposicionEsperada, fila.Cell(5).GetValue<decimal>());
            }
            else
            {
                Assert.True(fila.Cell(5).Value.IsBlank);
            }

            if (esperado.Sugerido is { } sugeridoEsperado)
            {
                Assert.Equal(sugeridoEsperado, fila.Cell(6).GetValue<decimal>());
            }
            else
            {
                Assert.True(fila.Cell(6).Value.IsBlank);
            }

            if (esperado.Proveedor is { } proveedorEsperado)
            {
                Assert.Equal(proveedorEsperado, fila.Cell(7).GetString());
            }
            else
            {
                Assert.True(fila.Cell(7).Value.IsBlank);
            }
        }

        // Sin fila de totales (design: sumar cantidades de artículos distintos no tiene significado
        // propio, mismo criterio que Existencias/Vencimientos).
        Assert.True(hoja.Cell(primeraFilaDeDatos + reposicion.Filas.Count, 1).Value.IsBlank);
    }

    // ---- task 4.10: cap refusal — refuses rather than truncates ------------------------------------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);

        for (var i = 0; i < 4; i++)
        {
            var idArticulo = await SembrarArticuloAsync(ctx, $"articulo-tope-reposicion-{i}");
            await SembrarStockAsync(ctx, idArticulo, cantidad: 0m, minimo: 1m, reposicion: null);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 4 filas", problema.GetProperty("title").GetString());
    }

    // ---- task 4.11: 403 (mitad export) --------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDeReposicion()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDeReposicion));

        var respuesta = await LlamarExportAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- barrido export: FormatoDeExportacion.Parsear en esta ruta -------------------------------

    /// <summary>Sin la llamada a <see cref="FormatoDeExportacion.Parsear"/> dentro de
    /// <c>/stock/reposicion/export</c>, un <c>formato=pdf</c> devolvería 200 XLSX en vez de 400. No
    /// necesita datos sembrados: el parseo del formato corre antes de cualquier lectura.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeReposicion()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeReposicion));

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- barrido export: borde EXACTO del tope (200, no 400) --------------------------------------

    /// <summary>Discriminador real del ÚNICO <c>GuardaDeTope.Exigir</c> de esta ruta AGREGADA del
    /// lado del ÉXITO: sin este test, mutar <c>Exigir(tabla.Filas.Count, tope)</c> a
    /// <c>Exigir(tabla.Filas.Count, tope - 1)</c> sobrevive — <c>UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal</c>
    /// solo cubre el rechazo por ARRIBA del tope. Acá se exportan EXACTAMENTE <c>tope</c> filas
    /// (mismo criterio de alerta que el test de rechazo: <c>minimo</c> configurado, <c>cantidad
    /// &lt;= minimo</c>) y se espera 200 con el workbook completo.</summary>
    [Fact]
    public async Task UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta), factoryBajo);

        for (var i = 0; i < 3; i++)
        {
            var idArticulo = await SembrarArticuloAsync(ctx, $"articulo-tope-reposicion-exacto-{i}");
            await SembrarStockAsync(ctx, idArticulo, cantidad: 0m, minimo: 1m, reposicion: null);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Header en la fila 6, datos desde la 7 (mismo layout que el test de igualdad de arriba):
        // las tope=3 filas ocupan 7-9, y la fila 10 tiene que quedar vacía.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < 3; i++)
        {
            Assert.False(hoja.Row(primeraFilaDeDatos + i).IsEmpty());
        }
        Assert.True(hoja.Row(primeraFilaDeDatos + 3).IsEmpty());
    }
}
