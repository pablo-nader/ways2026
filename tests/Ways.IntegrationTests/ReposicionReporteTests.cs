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
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-13-stock-inteligente, Slice 4 (tasks 4.6-4.9, 4.11): <c>GET /api/reportes/stock/reposicion</c>
/// — los tres mutation targets de <c>ConstruirQueryDeReposicion</c> (<c>s.Minimo != null</c>,
/// <c>candidatos.DefaultIfEmpty()</c>, el primer campo de <c>orderby</c>), el seed discriminante
/// (task 4.9, mutation-proof-tests reglas 4 y 6: nueve escenarios de la spec reposicion-de-stock
/// en un único punto de venta, cada fila con valores DISTINTOS en cada columna, el orden asertado
/// como secuencia) y el 403 del gate. El export sibling (equality fila-por-fila, cap y su propio
/// 403) vive en <see cref="ReposicionExportTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReposicionReporteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdArea, int IdAlicuotaIva,
        HttpClient Admin, HttpClient Vendedor);

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

        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area reposicion", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, area.Id, idAlicuotaIva, admin, vendedor);
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

    private async Task<int> SembrarProveedorAsync(Contexto ctx, string razonSocial, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = razonSocial, IdCondicionFiscal = idCondicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = eliminado ? ahora : null
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

    private async Task SembrarStockAsync(
        Contexto ctx, int idPuntoVenta, int idArticulo, decimal cantidad, decimal? minimo, decimal? reposicion, int? idTenant = null)
    {
        var tenant = idTenant ?? ctx.IdTenant;
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = tenant, IdPuntoVenta = idPuntoVenta, IdArticulo = idArticulo, Cantidad = cantidad,
            Minimo = minimo, Reposicion = reposicion
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> LlamarReporteAsync(HttpClient cliente, int idPuntoVenta, int? dias = null) =>
        cliente.GetAsync(
            $"/api/reportes/stock/reposicion?idPuntoVenta={idPuntoVenta}"
            + (dias is { } valorDias ? $"&dias={valorDias}" : string.Empty));

    private static async Task<Reposicion> ObtenerReposicionAsync(HttpClient cliente, int idPuntoVenta, int? dias = null)
    {
        var respuesta = await LlamarReporteAsync(cliente, idPuntoVenta, dias);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Reposicion>(cuerpo, OpcionesJson)!;
    }

    // ---- task 4.6: cobertura de spec (NO mutation target — ver nota) ------------------------------

    /// <summary>Cobertura de spec (reposicion-de-stock: "An articulo with no minimo never alerts,
    /// even at zero stock"), NO un mutation target pese a lo que dice task 4.6: se corrió la
    /// mutación (borrar <c>s.Minimo != null</c> de <c>ConstruirQueryDeReposicion</c>) contra este
    /// seed y el test siguió en VERDE. Investigado con <c>ToQueryString()</c> — Npgsql traduce
    /// <c>s.cantidad &lt;= s.minimo</c> a SQL con lógica de tres valores: <c>x &lt;= NULL</c> es
    /// siempre desconocido, así que ninguna fila con <c>minimo</c> NULL puede pasar el <c>WHERE</c>
    /// con o sin el chequeo explícito — no hay combinación de datos que discrimine la mutación
    /// (mutation-proof-tests regla 3 agotada: el "confound" es la semántica NULL de SQL misma, no
    /// otra capa que rodear). La cláusula se conserva en el código por legibilidad/intención
    /// documental, nunca por necesidad funcional. Desvío y evidencia registrados en tasks.md, task
    /// 4.6.</summary>
    [Fact]
    public async Task UnArticuloSinMinimoNuncaApareceEnLaReposicion()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinMinimoNuncaApareceEnLaReposicion));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-minimo");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticulo, cantidad: 0m, minimo: null, reposicion: null);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Empty(reposicion.Filas);
    }

    // ---- task 4.7: MUTATION TARGET — candidatos.DefaultIfEmpty() ---------------------------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): <c>candidatos.DefaultIfEmpty()</c>
    /// en <c>ConstruirQueryDeReposicion</c> — sin el LEFT JOIN la fila desaparece en silencio.
    /// Mutación aplicada (reemplazar por un INNER JOIN, borrando el <c>DefaultIfEmpty()</c>): este
    /// test pasó de FALLAR (<c>Assert.Single</c> sin filas) a pasar al revertir — evidencia
    /// registrada en el resumen de apply.</summary>
    [Fact]
    public async Task UnArticuloSinProveedorHabitualApareceBajoSinProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinProveedorHabitualApareceBajoSinProveedor));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-proveedor-habitual");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticulo, cantidad: 2m, minimo: 5m, reposicion: null);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        var fila = Assert.Single(reposicion.Filas);
        Assert.Equal(idArticulo, fila.IdArticulo);
        Assert.Null(fila.IdProveedor);
        Assert.Null(fila.Proveedor);
    }

    // ---- task 4.8: MUTATION TARGET — orderby a.IdProveedorHabitual, a.Id (primer campo) -----------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): el PRIMER campo del
    /// <c>orderby a.IdProveedorHabitual, a.Id</c> de <c>ConstruirQueryDeReposicion</c>. El artículo
    /// SIN proveedor se crea PRIMERO (id de artículo más bajo) pero tiene que ordenar ÚLTIMO —
    /// discrimina de un orden que dependiera solo de <c>a.Id</c>. Mutación aplicada (borrar
    /// <c>a.IdProveedorHabitual,</c> del orderby, dejando solo <c>orderby a.Id</c>): este test pasó
    /// de FALLAR (el artículo sin proveedor, de id más bajo, ordena PRIMERO) a pasar al revertir —
    /// evidencia registrada en el resumen de apply.</summary>
    [Fact]
    public async Task ElArticuloSinProveedorSiempreOrdenaAlFinalIndependientementeDeSuId()
    {
        var ctx = await PrepararAsync(nameof(ElArticuloSinProveedorSiempreOrdenaAlFinalIndependientementeDeSuId));

        // Creado PRIMERO ⇒ id de artículo más bajo, y sin embargo tiene que ordenar último.
        var idArticuloSinProveedor = await SembrarArticuloAsync(ctx, "articulo-sin-proveedor-orden");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticuloSinProveedor, cantidad: 1m, minimo: 5m, reposicion: null);

        var idProveedor = await SembrarProveedorAsync(ctx, "Proveedor de orden");
        var idArticuloConProveedor = await SembrarArticuloAsync(ctx, "articulo-con-proveedor-orden", idProveedor);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, idArticuloConProveedor, cantidad: 2m, minimo: 8m, reposicion: null);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(2, reposicion.Filas.Count);
        Assert.Equal(idArticuloConProveedor, reposicion.Filas[0].IdArticulo);
        Assert.Equal(idArticuloSinProveedor, reposicion.Filas[1].IdArticulo);
    }

    // ---- task 4.9: discriminating-seed integration test (mutation-proof-tests reglas 4 y 6) -------

    /// <summary>El seed discriminante completo de la spec reposicion-de-stock: nueve escenarios en
    /// un único punto de venta, cinco filas ESPERADAS (cada una con valores distintos de
    /// <c>Cantidad</c>/<c>Minimo</c>/<c>Reposicion</c>/<c>Sugerido</c> — mutation-proof-tests regla
    /// 6, ningún swap ni rotación pasa desapercibido) y cuatro escenarios de ausencia. El orden se
    /// asegura primero por presencia de proveedor efectivo, luego por FK creciente dentro de cada
    /// bucket (orchestrator decision 12, tasks.md): proveedor A → proveedor C → proveedor D
    /// (proveedores activos, FK creciente) → proveedor B (eliminado, cae al MISMO bucket final
    /// "Sin proveedor" que la fila sin FK — <c>IdProveedor</c>/<c>Proveedor</c> ambos <c>null</c>,
    /// nunca el FK crudo) → sin proveedor (FK NULL, siempre último dentro del bucket).</summary>
    [Fact]
    public async Task ElSeedDiscriminanteCubreLosNueveEscenariosDeLaSpec()
    {
        var ctx = await PrepararAsync(nameof(ElSeedDiscriminanteCubreLosNueveEscenariosDeLaSpec));

        var provA = await SembrarProveedorAsync(ctx, "Proveedor A");
        var provB = await SembrarProveedorAsync(ctx, "Proveedor B eliminado", eliminado: true);
        var provC = await SembrarProveedorAsync(ctx, "Proveedor C");
        var provD = await SembrarProveedorAsync(ctx, "Proveedor D");

        // Fila 1: minimo = 0, cantidad = 0 — "minimo = 0 alerta solo agotado" (appears).
        var artMinimoCero = await SembrarArticuloAsync(ctx, "seed-minimo-cero", provA);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artMinimoCero, cantidad: 0m, minimo: 0m, reposicion: 8m);

        // Fila 2: proveedor soft-deleted — appears bajo "Sin proveedor" (Proveedor null).
        var artProveedorEliminado = await SembrarArticuloAsync(ctx, "seed-proveedor-eliminado", provB);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artProveedorEliminado, cantidad: 1m, minimo: 3m, reposicion: 50m);

        // Fila 3: cantidad == minimo — borde inclusive (appears).
        var artIgual = await SembrarArticuloAsync(ctx, "seed-cantidad-igual-minimo", provC);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artIgual, cantidad: 5m, minimo: 5m, reposicion: 20m);

        // Fila 4: reposicion sin configurar — sugerido null, nunca 0 (appears).
        var artReposicionUnset = await SembrarArticuloAsync(ctx, "seed-reposicion-unset", provD);
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artReposicionUnset, cantidad: 2m, minimo: 12m, reposicion: null);

        // Fila 5: id_proveedor_habitual NULL — appears, proveedor null, SIEMPRE último.
        var artSinProveedor = await SembrarArticuloAsync(ctx, "seed-sin-proveedor");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artSinProveedor, cantidad: 4m, minimo: 9m, reposicion: 100m);

        // Ausencias.
        var artSobreMinimo = await SembrarArticuloAsync(ctx, "seed-sobre-minimo");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artSobreMinimo, cantidad: 6.001m, minimo: 6m, reposicion: null);

        var artSinMinimo = await SembrarArticuloAsync(ctx, "seed-sin-minimo");
        await SembrarStockAsync(ctx, ctx.IdPuntoVenta, artSinMinimo, cantidad: 0m, minimo: null, reposicion: null);

        var otroPv = await SembrarPuntoVentaAsync(ctx, "PV secundario reposicion");
        var artOtroPv = await SembrarArticuloAsync(ctx, "seed-otro-pv");
        await SembrarStockAsync(ctx, otroPv, artOtroPv, cantidad: 1m, minimo: 2m, reposicion: null);

        var ctxOtroTenant = await PrepararAsync(nameof(ElSeedDiscriminanteCubreLosNueveEscenariosDeLaSpec) + "-otro-tenant");
        var artOtroTenant = await SembrarArticuloAsync(ctxOtroTenant, "seed-otro-tenant");
        await SembrarStockAsync(
            ctxOtroTenant, ctxOtroTenant.IdPuntoVenta, artOtroTenant, cantidad: 0m, minimo: 1m, reposicion: null);

        var reposicion = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.Equal(5, reposicion.Filas.Count);
        var idsPresentes = reposicion.Filas.Select(f => f.IdArticulo).ToList();
        Assert.DoesNotContain(artSobreMinimo, idsPresentes);
        Assert.DoesNotContain(artSinMinimo, idsPresentes);
        Assert.DoesNotContain(artOtroPv, idsPresentes);
        Assert.DoesNotContain(artOtroTenant, idsPresentes);

        // Secuencia exacta — bucket "con proveedor efectivo" (FK creciente) primero, bucket
        // "Sin proveedor" (FK soft-deleted o FK NULL, FK creciente dentro del bucket) al final.
        var fila1 = reposicion.Filas[0];
        Assert.Equal(artMinimoCero, fila1.IdArticulo);
        Assert.Equal("seed-minimo-cero", fila1.Articulo);
        Assert.Equal(0m, fila1.Cantidad);
        Assert.Equal(0m, fila1.Minimo);
        Assert.Equal(8m, fila1.Reposicion);
        Assert.Equal(8m, fila1.Sugerido);
        Assert.Equal(provA, fila1.IdProveedor);
        Assert.Equal("Proveedor A", fila1.Proveedor);

        var fila2 = reposicion.Filas[1];
        Assert.Equal(artIgual, fila2.IdArticulo);
        Assert.Equal("seed-cantidad-igual-minimo", fila2.Articulo);
        Assert.Equal(5m, fila2.Cantidad);
        Assert.Equal(5m, fila2.Minimo);
        Assert.Equal(20m, fila2.Reposicion);
        Assert.Equal(15m, fila2.Sugerido);
        Assert.Equal(provC, fila2.IdProveedor);
        Assert.Equal("Proveedor C", fila2.Proveedor);

        var fila3 = reposicion.Filas[2];
        Assert.Equal(artReposicionUnset, fila3.IdArticulo);
        Assert.Equal("seed-reposicion-unset", fila3.Articulo);
        Assert.Equal(2m, fila3.Cantidad);
        Assert.Equal(12m, fila3.Minimo);
        Assert.Null(fila3.Reposicion);
        Assert.Null(fila3.Sugerido);
        Assert.Equal(provD, fila3.IdProveedor);
        Assert.Equal("Proveedor D", fila3.Proveedor);

        var fila4 = reposicion.Filas[3];
        Assert.Equal(artProveedorEliminado, fila4.IdArticulo);
        Assert.Equal("seed-proveedor-eliminado", fila4.Articulo);
        Assert.Equal(1m, fila4.Cantidad);
        Assert.Equal(3m, fila4.Minimo);
        Assert.Equal(50m, fila4.Reposicion);
        Assert.Equal(49m, fila4.Sugerido);
        // IdProveedor null pese al FK vivo (design decisión 3 + orchestrator decision 12,
        // tasks.md): un proveedor soft-deleted resuelve p == null igual que un FK NULL, así que
        // cae en el MISMO bucket final "Sin proveedor" — el FK crudo nunca viaja al cliente.
        Assert.Null(fila4.IdProveedor);
        Assert.Null(fila4.Proveedor);

        var fila5 = reposicion.Filas[4];
        Assert.Equal(artSinProveedor, fila5.IdArticulo);
        Assert.Equal("seed-sin-proveedor", fila5.Articulo);
        Assert.Equal(4m, fila5.Cantidad);
        Assert.Equal(9m, fila5.Minimo);
        Assert.Equal(100m, fila5.Reposicion);
        Assert.Equal(96m, fila5.Sugerido);
        Assert.Null(fila5.IdProveedor);
        Assert.Null(fila5.Proveedor);
    }

    // ---- task 4.2: MUTATION TARGET — ReglaDeReposicion.ExigirVentanaValida(?dias=) -----------------

    /// <summary>Nombra la cláusula bajo prueba (mutation-proof-tests): la llamada a
    /// <c>ReglaDeReposicion.ExigirVentanaValida</c> dentro de <c>ObtenerReposicionAsync</c> — sin
    /// ella, un <c>?dias=0</c> (o negativo) nunca rechaza. Mutación aplicada (borrar la llamada):
    /// este test pasó de FALLAR (200 en lugar de 400) a pasar al revertir — evidencia de mutación
    /// registrada en el resumen de apply (judgment-day round 1, hallazgo confirmado #2).</summary>
    [Fact]
    public async Task UnDiasDeRotacionInvalidoEsRechazadoConCuatrocientos()
    {
        var ctx = await PrepararAsync(nameof(UnDiasDeRotacionInvalidoEsRechazadoConCuatrocientos));

        var respuesta = await LlamarReporteAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 0);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dias_rotacion_invalido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Eco del horizonte efectivamente resuelto en <see cref="Reposicion.DiasDeRotacion"/>
    /// — mata también el mutante "DiasDeRotacion hard-codeado": un <c>?dias=45</c> explícito viaja
    /// tal cual, y con <c>dias</c> omitido la respuesta ecoa el default de <c>dias_rotacion</c>
    /// (<c>30</c>).</summary>
    [Fact]
    public async Task LaRespuestaEcoaElDiasDeRotacionEfectivamenteResuelto()
    {
        var ctx = await PrepararAsync(nameof(LaRespuestaEcoaElDiasDeRotacionEfectivamenteResuelto));

        var conDiasExplicito = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta, dias: 45);
        Assert.Equal(45, conDiasExplicito.DiasDeRotacion);

        var conDiasOmitido = await ObtenerReposicionAsync(ctx.Admin, ctx.IdPuntoVenta);
        Assert.Equal(30, conDiasOmitido.DiasDeRotacion);
    }

    // ---- task 4.11: 403 (mitad reporte) -------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelReporteDeReposicion()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelReporteDeReposicion));

        var respuesta = await LlamarReporteAsync(ctx.Vendedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
