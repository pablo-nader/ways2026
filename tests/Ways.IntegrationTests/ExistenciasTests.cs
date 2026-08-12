using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 9: <c>GET /api/reportes/stock/existencias</c> — la casa de
/// las 4 pruebas (cruce de tenant, discriminación por punto de venta, artículo eliminado excluido
/// del join en vivo, fixture hand-computed) más la garantía propia del spec (sin
/// <c>idArticulo</c>) y el rol un escalón debajo del gate. Siembra directa vía
/// <c>IWaysDbContext.Stock</c>/<c>Articulos</c> (nunca a través de <c>ServicioDeStock</c>, que
/// exige un movimiento): esta clase prueba la LECTURA del reporte, no la escritura del caché de
/// stock — ya cubierta por las pruebas de stage-5/8.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ExistenciasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
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

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area existencias", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, idAlicuotaIva,
            admin, supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = eliminado ? ahora : null
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task SembrarStockAsync(Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> LlamarExistenciasAsync(HttpClient cliente, int idPuntoVenta) =>
        cliente.GetAsync($"/api/reportes/stock/existencias?idPuntoVenta={idPuntoVenta}");

    private static async Task<Existencias> ObtenerExistenciasAsync(HttpClient cliente, int idPuntoVenta)
    {
        var respuesta = await LlamarExistenciasAsync(cliente, idPuntoVenta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Existencias>(cuerpo, OpcionesJson)!;
    }

    // ---- task 9.8: house 4-test pattern ---------------------------------------------------------

    /// <summary>Nota de honestidad (mutation-proof-tests, mismo criterio que
    /// <c>ReportesArticulosTopTests.UnaFilaDeOtroTenantNuncaApareceEnElTop</c>): los ids de punto
    /// de venta son globalmente únicos (secuencia compartida entre tenants), así que este test
    /// prueba el aislamiento en la práctica, NO específicamente el filtro Tenant de EF — ese queda
    /// cubierto de forma no confundida por <see cref="UnaFilaDeOtroPuntoDeVentaNuncaApareceEnLasExistencias"/>,
    /// que sí discrimina por la cláusula bajo prueba dentro de un mismo tenant.</summary>
    [Fact]
    public async Task UnaFilaDeOtroTenantNuncaApareceEnLasExistencias()
    {
        var ctxA = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnLasExistencias) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaFilaDeOtroTenantNuncaApareceEnLasExistencias) + "-B");

        var idArticuloB = await SembrarArticuloAsync(ctxB, "articulo-otro-tenant");
        await SembrarStockAsync(ctxB, ctxB.IdPuntoVenta, idArticuloB, 999m);

        var existencias = await ObtenerExistenciasAsync(ctxA.Admin, ctxA.IdPuntoVenta);

        Assert.Empty(existencias.Filas);
    }

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests):
    /// <c>Where(s => s.IdPuntoVenta == idPuntoVenta)</c> en
    /// <c>ServicioDeReportesDeStock.ObtenerExistenciasAsync</c>. Mismo tenant con dos puntos de
    /// venta — las existencias de uno NUNCA pueden traer filas del otro. Mutación aplicada
    /// (reemplazar el <c>Where</c> por un no-op que deja pasar todas las filas del tenant): esta
    /// prueba pasó de FALLAR (la fila del PV secundario aparece en el reporte del PV principal) a
    /// pasar al revertir — evidencia registrada en el resumen de apply.</summary>
    [Fact]
    public async Task UnaFilaDeOtroPuntoDeVentaNuncaApareceEnLasExistencias()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaDeOtroPuntoDeVentaNuncaApareceEnLasExistencias));
        var otroPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV secundario");

        var idArticuloPrincipal = await SembrarArticuloAsync(ctx, "articulo-principal");
        var idArticuloSecundario = await SembrarArticuloAsync(ctx, "articulo-secundario");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticuloPrincipal, 10m);
        await SembrarStockAsync(ctx, otroPuntoVenta, idArticuloSecundario, 999m);

        var existencias = await ObtenerExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(existencias.Filas);
        Assert.Equal(idArticuloPrincipal, fila.IdArticulo);
    }

    [Fact]
    public async Task UnArticuloEliminadoNuncaApareceEnLasExistencias()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloEliminadoNuncaApareceEnLasExistencias));

        var idArticuloEliminado = await SembrarArticuloAsync(ctx, "articulo-eliminado", eliminado: true);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticuloEliminado, 5m);
        var idArticuloVigente = await SembrarArticuloAsync(ctx, "articulo-vigente");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticuloVigente, 7m);

        var existencias = await ObtenerExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(existencias.Filas);
        Assert.Equal(idArticuloVigente, fila.IdArticulo);
    }

    [Fact]
    public async Task LosCamposDeLaFilaCoincidenConElStockSembrado()
    {
        var ctx = await PrepararAsync(nameof(LosCamposDeLaFilaCoincidenConElStockSembrado));

        var idArticulo = await SembrarArticuloAsync(ctx, "Yerba mate 1kg");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticulo, 42.5m);

        var existencias = await ObtenerExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(existencias.Filas);
        Assert.Equal(idArticulo, fila.IdArticulo);
        Assert.Equal("Yerba mate 1kg", fila.Nombre);
        Assert.Equal(42.5m, fila.Cantidad);
    }

    // ---- task 9.11: no-idArticulo-required (spec: Existencias Needs No idArticulo) --------------

    [Fact]
    public async Task LasExistenciasDe40ArticulosVuelvenSinPedirIdArticulo()
    {
        var ctx = await PrepararAsync(nameof(LasExistenciasDe40ArticulosVuelvenSinPedirIdArticulo));

        for (var i = 0; i < 40; i++)
        {
            var idArticulo = await SembrarArticuloAsync(ctx, $"articulo-{i}");
            await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticulo, i + 1m);
        }

        var respuesta = await LlamarExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.DoesNotContain("idArticulo", respuesta.RequestMessage!.RequestUri!.Query);

        var existencias = JsonSerializer.Deserialize<Existencias>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(40, existencias.Filas.Count);
    }

    // ---- task 9.10: rol un escalón debajo del gate ------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDeLasExistencias()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDeLasExistencias));

        var respuesta = await LlamarExistenciasAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeLasExistencias()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeLasExistencias));

        var respuesta = await LlamarExistenciasAsync(ctx.Supervisor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
