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
///
/// stage-13-stock-inteligente, Slice 2 (tasks 2.4-2.10): agrega las tres columnas de reposición —
/// la clasificación de tres estados (2.4/2.5, mutation target sobre la llamada a
/// <c>ReglaDeReposicion.Clasificar</c> en la proyección), la regresión de "sin idArticulo" con las
/// columnas nuevas presentes (2.6), la lectura de Supervisor confirmando el 403 de escritura desde
/// este vantage point (2.8) y el round-trip PUT→GET (2.10).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ExistenciasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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

    private async Task SembrarStockAsync(
        Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad,
        decimal? minimo = null, decimal? reposicion = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
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

    // ---- task 2.4/2.5: los tres estados de ReglaDeReposicion.Clasificar en la proyección ---------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests, task 2.4): la llamada a
    /// <c>ReglaDeReposicion.Clasificar(x.Cantidad, x.Minimo)</c> dentro de la proyección de
    /// <c>ObtenerExistenciasAsync</c>. Mutación aplicada (hard-code <c>EstadoDeReposicion.Ok</c> en
    /// vez de la llamada real): esta prueba pasó de FALLAR (las tres filas clasifican <c>Ok</c> en
    /// vez de <c>Bajo</c>/<c>SinMinimo</c>/<c>Ok</c> respectivamente) a pasar al revertir —
    /// evidencia registrada en el resumen de apply. Tres artículos con valores de
    /// cantidad/mínimo/reposición TODOS distintos (mutation-proof-tests regla 6), así que un swap
    /// de fila también sería detectable. (spec reportes-de-gestion: "An articulo at or below its
    /// minimo classifies bajo" / "…classifies sin_minimo, never bajo" / "…classifies ok")</summary>
    [Fact]
    public async Task LosTresEstadosDeReposicionClasificanCorrectamenteEnExistencias()
    {
        var ctx = await PrepararAsync(nameof(LosTresEstadosDeReposicionClasificanCorrectamenteEnExistencias));

        var idBajo = await SembrarArticuloAsync(ctx, "articulo-bajo");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idBajo, cantidad: 5m, minimo: 5m, reposicion: 20m);

        var idSinMinimo = await SembrarArticuloAsync(ctx, "articulo-sin-minimo");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idSinMinimo, cantidad: 0m, minimo: null, reposicion: null);

        var idOk = await SembrarArticuloAsync(ctx, "articulo-ok");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idOk, cantidad: 20m, minimo: 5m, reposicion: null);

        var existencias = await ObtenerExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Equal(3, existencias.Filas.Count);

        var filaBajo = existencias.Filas.Single(f => f.IdArticulo == idBajo);
        Assert.Equal(5m, filaBajo.Minimo);
        Assert.Equal(20m, filaBajo.Reposicion);
        Assert.Equal(EstadoDeReposicion.Bajo, filaBajo.Estado);

        var filaSinMinimo = existencias.Filas.Single(f => f.IdArticulo == idSinMinimo);
        Assert.Null(filaSinMinimo.Minimo);
        Assert.Null(filaSinMinimo.Reposicion);
        Assert.Equal(EstadoDeReposicion.SinMinimo, filaSinMinimo.Estado);

        var filaOk = existencias.Filas.Single(f => f.IdArticulo == idOk);
        Assert.Equal(5m, filaOk.Minimo);
        Assert.Null(filaOk.Reposicion);
        Assert.Equal(EstadoDeReposicion.Ok, filaOk.Estado);
    }

    // ---- task 9.11 / 2.6: no-idArticulo-required, regresión con las tres columnas nuevas ---------

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
        // Slice 2 (task 2.6): las 40 filas quedan sin mínimo configurado — cada una clasifica
        // SinMinimo, nunca Bajo (spec: "An articulo with no minimo classifies sin_minimo, never bajo").
        Assert.All(existencias.Filas, f =>
        {
            Assert.Null(f.Minimo);
            Assert.Null(f.Reposicion);
            Assert.Equal(EstadoDeReposicion.SinMinimo, f.Estado);
        });
    }

    // ---- task 2.8: Supervisor lee las columnas de reposición y confirma el 403 de escritura -------

    [Fact]
    public async Task UnSupervisorLeeLasColumnasDeReposicionYEsRechazadoDeEscribirlas()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeLasColumnasDeReposicionYEsRechazadoDeEscribirlas));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-supervisor-lectura");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticulo, cantidad: 3m, minimo: 5m, reposicion: 25m);

        var existencias = await ObtenerExistenciasAsync(ctx.Supervisor, ctx.IdPuntoVenta);
        var fila = Assert.Single(existencias.Filas);
        Assert.Equal(5m, fila.Minimo);
        Assert.Equal(25m, fila.Reposicion);
        Assert.Equal(EstadoDeReposicion.Bajo, fila.Estado);

        var escritura = await ctx.Supervisor.PutAsJsonAsync(
            "/api/stock/minimos", new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 10m, null));
        Assert.Equal(HttpStatusCode.Forbidden, escritura.StatusCode);
    }

    // ---- task 2.10: round-trip PUT /api/stock/minimos → GET /existencias devuelve el par persistido

    [Fact]
    public async Task UnRoundTripDeEscrituraYLecturaDevuelveElParPersistido()
    {
        var ctx = await PrepararAsync(nameof(UnRoundTripDeEscrituraYLecturaDevuelveElParPersistido));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-round-trip");

        var escritura = await ctx.Admin.PutAsJsonAsync(
            "/api/stock/minimos", new SolicitudDeMinimos(ctx.IdPuntoVenta, idArticulo, 8m, 30m));
        Assert.Equal(HttpStatusCode.OK, escritura.StatusCode);

        var existencias = await ObtenerExistenciasAsync(ctx.Admin, ctx.IdPuntoVenta);
        var fila = Assert.Single(existencias.Filas);
        Assert.Equal(idArticulo, fila.IdArticulo);
        Assert.Equal(0m, fila.Cantidad);
        Assert.Equal(8m, fila.Minimo);
        Assert.Equal(30m, fila.Reposicion);
        // cantidad queda en 0 (create-at-zero) y 0 <= 8 ⇒ Bajo, no Ok.
        Assert.Equal(EstadoDeReposicion.Bajo, fila.Estado);
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
