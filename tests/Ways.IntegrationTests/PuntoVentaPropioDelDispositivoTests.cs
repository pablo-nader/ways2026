using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Revisión adversarial post-stage-17 (ver el doc-comment de <see
/// cref="PoliticaDeModoDePuntoVenta.ExigirPuntoVentaPropioDelDispositivoAsync"/>): un actor de
/// dispositivo podía crear/editar/emitir remitos, presupuestos y órdenes de compra contra
/// CUALQUIER punto de venta de su tenant — el daño concreto es que
/// <c>ServicioDeRemitos.EmitirAsync</c> decrementa <c>stock</c>/<c>stock_lotes</c> del punto de
/// venta del documento, y el <c>id_punto_venta</c> de una orden de compra es el destino de
/// recepción que <c>ServicioDeCompras</c> usa después.
///
/// Esta clase prueba las 13 réplicas del guard (mutation-proof-tests regla 15: un kill POR
/// sitio, nunca uno solo por todos) — las primeras 9 en creación/edición/emisión-envío, las 4
/// restantes agregadas en la revisión adversarial que encontró el mismo hueco en anular/cerrar
/// (sitio 10: <c>ServicioDeRemitos.AnularAsync</c>; sitio 11:
/// <c>ServicioDePresupuestos.AnularAsync</c>; sitio 12: <c>ServicioDeOrdenesDeCompra.CerrarAsync</c>;
/// sitio 13: <c>ServicioDeOrdenesDeCompra.AnularAsync</c>) — más dos familias de regresión por
/// servicio (una para creación/emisión, una para anular/cerrar):
/// las <c>ActorWebPuedeOperarContraPuntoVentaEscritorio</c>/<c>ActorWebPuedeAnularContra…</c>/
/// <c>ActorWebPuedeCerrarContra…</c> son las ÚNICAS que prueban que la
/// mitad WEB nunca se agregó — un actor sin claim de dispositivo sigue eligiendo cualquier punto de
/// venta de su tenant, en cualquier <c>modo</c> (docs/09-multi-tenancy.md:210-212;
/// openspec/specs/operacion-de-pos/spec.md:53-58); las
/// <c>DispositivoPuedeOperarContraSuPropioPuntoVenta</c>/<c>DispositivoPuedeAnularSuPropio…</c>/
/// <c>DispositivoPuedeCerrarSuPropio…</c> solo prueban que el guard no rompió el
/// camino feliz del dispositivo, y no dicen nada sobre la mitad web.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PuntoVentaPropioDelDispositivoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";
    private const string CodigoRechazo = "punto_venta_ajeno_al_dispositivo";

    private static readonly DateTimeOffset InicioDeVigenciaFijo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        HttpClient Admin, int IdTenant, int IdEmpresa, int IdPuntoVentaPropio, int IdPuntoVentaAjeno,
        int IdArticulo, int IdProveedor);

    /// <summary>Un tenant con dos puntos de venta (el segundo agregado vía EF directo — no hay
    /// endpoint de alta, plataforma-only vía aprovisionamiento, ADR-16, mismo criterio que
    /// <c>VentasModoPuntoVentaTests.AgregarSegundoPuntoVentaAsync</c>) y un artículo PRODUCTO con
    /// precio en la lista general default (para que remitos/presupuestos puedan resolver precio al
    /// crear el borrador) más un proveedor (para las órdenes de compra).</summary>
    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVentaAjeno = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = $"{nombre}-ajeno",
            Modo = ModoPuntoVenta.Web, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVentaAjeno);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"pv-propio-{Guid.NewGuid():N}", Nombre = "Producto de prueba",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = resultado.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral, Monto = 100m,
            VigenteDesde = InicioDeVigenciaFijo, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-proveedor", IdCondicionFiscal = idCondicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return new Contexto(
            admin, resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVentaAjeno.Id,
            articulo.Id, proveedor.Id);
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Vincula un dispositivo al punto de venta indicado y devuelve un cliente YA logueado
    /// como cajero vía <c>login-dispositivo</c> (mismo flujo que
    /// <c>VentasModoPuntoVentaTests</c>/<c>DispositivosTests</c>). <paramref name="rol"/> es
    /// <see cref="RolConocido.Vendedor"/> por default; las órdenes de compra necesitan
    /// <see cref="RolConocido.Admin"/> — sus rutas de escritura apilan <c>GestionDeCatalogo</c>
    /// sobre <c>OperacionDePos</c> (<c>OrdenesDeCompraEndpoints.cs</c>).</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        Contexto ctx, int idPuntoVenta, string sufijo, RolConocido rol = RolConocido.Vendedor)
    {
        var alta = await ctx.Admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = ctx.IdTenant,
                NombreUsuario = $"cajero-{sufijo}",
                Mail = $"cajero-{sufijo}-{ctx.IdTenant}@ways.test",
                RolId = (int)rol,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo($"cajero-{sufijo}", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cajero;
    }

    private async Task<long?> LeerProximoNumeroAsync(int idTenant, int idPuntoVenta, string tipoComprobante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.NumeracionesComprobante.AsNoTracking()
            .Where(n => n.IdPuntoVenta == idPuntoVenta && n.TipoComprobante == tipoComprobante)
            .Select(n => (long?)n.ProximoNumero)
            .FirstOrDefaultAsync();
    }

    private async Task<decimal?> LeerStockAsync(int idTenant, int idArticulo, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.Stock.AsNoTracking()
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => (decimal?)s.Cantidad)
            .FirstOrDefaultAsync();
    }

    private async Task<EstadoPresupuesto> LeerEstadoPresupuestoAsync(int idTenant, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.Presupuestos.AsNoTracking().Where(p => p.Id == id).Select(p => p.Estado).FirstAsync();
    }

    private async Task<EstadoOrdenCompra> LeerEstadoOrdenDeCompraAsync(int idTenant, int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.OrdenesCompra.AsNoTracking().Where(o => o.Id == id).Select(o => o.Estado).FirstAsync();
    }

    private static async Task<JsonElement> LeerCodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema;
    }

    // ==================================================================================
    // ---- Remitos: sitios 1 (CrearBorradorAsync), 2 (EditarAsync), 3 (EmitirAsync) ----
    // ==================================================================================

    private static SolicitudDeRemito RemitoVacio(int idPuntoVenta) =>
        new(idPuntoVenta, null, null, null, []);

    private static SolicitudDeRemito RemitoConLinea(int idPuntoVenta, int idArticulo) =>
        new(idPuntoVenta, null, null, null, [new LineaDeRemito(idArticulo, 1m, null)]);

    /// <summary>Sitio 1 — kill (a): un dispositivo no puede CREAR un remito contra un punto de
    /// venta que no es el suyo.</summary>
    [Fact]
    public async Task RemitoDispositivoNoPuedeCrearContraPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoNoPuedeCrearContraPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-crear");

        var respuesta = await cajero.PostAsJsonAsync("/api/remitos", RemitoVacio(ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 2 — kill (b): un dispositivo crea un remito contra SU PROPIO punto de venta
    /// (éxito) y después intenta moverlo, vía PUT, a un punto de venta ajeno.</summary>
    [Fact]
    public async Task RemitoDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-editar");

        var creado = await cajero.PostAsJsonAsync("/api/remitos", RemitoVacio(ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var respuesta = await cajero.PutAsJsonAsync($"/api/remitos/{borrador.Id}", RemitoVacio(ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 3 — kill (c): un borrador que YA carga un punto de venta ajeno (sembrado por
    /// un actor WEB — el sitio 1 ya lo rechazaría si se intentara crear como dispositivo, así que
    /// esta prueba lo esquiva por debajo, mutation-proof-tests regla 3) se intenta EMITIR desde el
    /// dispositivo. Se verifica que el rechazo llega genuinamente a <c>EmitirAsync</c> (no antes):
    /// el borrador tiene un item real de producto con precio, así que si esta prueba pasara con el
    /// guard borrado, sería porque el guard nunca se ejecutó — no porque otro chequeo lo
    /// reemplace.</summary>
    [Fact]
    public async Task RemitoDispositivoNoPuedeEmitirUnBorradorConPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoNoPuedeEmitirUnBorradorConPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-emitir");

        var creadoPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creadoPorWeb.StatusCode);
        var borrador = (await creadoPorWeb.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.Equal(ctx.IdPuntoVentaAjeno, borrador.IdPuntoVenta);

        var respuesta = await cajero.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        // Discriminante (mutation-proof-tests regla 4): el rechazo es ANTES de gastar un número o
        // tocar stock — ninguno de los dos efectos del emitir real debe existir.
        Assert.Null(await LeerProximoNumeroAsync(ctx.IdTenant, ctx.IdPuntoVentaAjeno, "REM"));
        Assert.Null(await LeerStockAsync(ctx.IdTenant, ctx.IdArticulo, ctx.IdPuntoVentaAjeno));
    }

    /// <summary>Regresión (d): un actor WEB (sin claim de dispositivo) sigue pudiendo crear Y
    /// emitir un remito contra un punto de venta Escritorio — la mitad web de la regla
    /// deliberadamente NO existe (docs/09-multi-tenancy.md:210-212).</summary>
    [Fact]
    public async Task RemitoActorWebPuedeOperarContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(RemitoActorWebPuedeOperarContraPuntoVentaEscritorio));

        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);

        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        var detalle = (await emitido.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoRemito.Emitido, detalle.Estado);
    }

    /// <summary>Regresión (e): un dispositivo puede crear Y emitir un remito de punta a punta
    /// contra SU PROPIO punto de venta.</summary>
    [Fact]
    public async Task RemitoDispositivoPuedeOperarContraSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoPuedeOperarContraSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-ok");

        var creado = await cajero.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitido = await cajero.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);

        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        var detalle = (await emitido.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoRemito.Emitido, detalle.Estado);
        Assert.StartsWith($"{ctx.IdPuntoVentaPropio:D4}-", detalle.NumeroFormateado);
    }

    /// <summary>Sitio 10 — kill: mismo esquive de confound que el emitir (el borrador con PV ajeno
    /// lo siembra Y lo emite el actor WEB — el guard de creación ya rechazaría a un dispositivo
    /// intentándolo). Discriminante (mutation-proof-tests regla 4): ANULAR es el write site que
    /// REVIERTE stock (<c>EjecutarAnulacionAsync</c>) — el valor que solo este guard puede producir
    /// es que esa reversa nunca corrió, así que el stock del punto de venta ajeno queda intacto.</summary>
    [Fact]
    public async Task RemitoDispositivoNoPuedeAnularUnoEmitidoDePuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoNoPuedeAnularUnoEmitidoDePuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-anular");

        var creadoPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creadoPorWeb.StatusCode);
        var borrador = (await creadoPorWeb.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitidoPorWeb = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);
        Assert.Equal(HttpStatusCode.OK, emitidoPorWeb.StatusCode);

        var stockAntes = await LeerStockAsync(ctx.IdTenant, ctx.IdArticulo, ctx.IdPuntoVentaAjeno);

        var respuesta = await cajero.PostAsync($"/api/remitos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        var stockDespues = await LeerStockAsync(ctx.IdTenant, ctx.IdArticulo, ctx.IdPuntoVentaAjeno);
        Assert.Equal(stockAntes, stockDespues);
    }

    /// <summary>Regresión (f): un actor WEB sigue pudiendo anular un remito emitido contra un punto
    /// de venta Escritorio — la mitad web tampoco se agregó acá.</summary>
    [Fact]
    public async Task RemitoActorWebPuedeAnularContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(RemitoActorWebPuedeAnularContraPuntoVentaEscritorio));

        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitido = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);

        var anulado = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);
        var detalle = (await anulado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.Equal(EstadoRemito.Anulado, detalle.Estado);
    }

    /// <summary>Regresión (g): un dispositivo puede anular un remito emitido de SU PROPIO punto de
    /// venta.</summary>
    [Fact]
    public async Task RemitoDispositivoPuedeAnularSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoPuedeAnularSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "rem-anular-ok");

        var creado = await cajero.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitido = await cajero.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);

        var anulado = await cajero.PostAsync($"/api/remitos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);
        var detalle = (await anulado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
        Assert.Equal(EstadoRemito.Anulado, detalle.Estado);
    }

    // ==========================================================================================
    // ---- Presupuestos: sitios 4 (CrearBorradorAsync), 5 (EditarAsync), 6 (EnviarAsync) ----
    // ==========================================================================================

    private static SolicitudDePresupuesto PresupuestoVacio(int idPuntoVenta) =>
        new(idPuntoVenta, null, null, []);

    private static SolicitudDePresupuesto PresupuestoConLinea(int idPuntoVenta, int idArticulo) =>
        new(idPuntoVenta, null, null, [new LineaDePresupuesto(idArticulo, 1m)]);

    private static SolicitudDeEnvio EnvioAFuturo() => new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));

    /// <summary>Sitio 4 — kill (a).</summary>
    [Fact]
    public async Task PresupuestoDispositivoNoPuedeCrearContraPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoNoPuedeCrearContraPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-crear");

        var respuesta = await cajero.PostAsJsonAsync("/api/presupuestos", PresupuestoVacio(ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 5 — kill (b).</summary>
    [Fact]
    public async Task PresupuestoDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-editar");

        var creado = await cajero.PostAsJsonAsync("/api/presupuestos", PresupuestoVacio(ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var respuesta = await cajero.PutAsJsonAsync(
            $"/api/presupuestos/{borrador.Id}", PresupuestoVacio(ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 6 — kill (c): mismo esquive de confound que el remito — el borrador con PV
    /// ajeno lo siembra el actor WEB.</summary>
    [Fact]
    public async Task PresupuestoDispositivoNoPuedeEnviarUnBorradorConPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoNoPuedeEnviarUnBorradorConPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-enviar");

        var creadoPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creadoPorWeb.StatusCode);
        var borrador = (await creadoPorWeb.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(ctx.IdPuntoVentaAjeno, borrador.IdPuntoVenta);

        var respuesta = await cajero.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Null(await LeerProximoNumeroAsync(ctx.IdTenant, ctx.IdPuntoVentaAjeno, "PRES"));
    }

    /// <summary>Regresión (d).</summary>
    [Fact]
    public async Task PresupuestoActorWebPuedeOperarContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoActorWebPuedeOperarContraPuntoVentaEscritorio));

        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var enviado = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());

        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);
        var detalle = (await enviado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoPresupuesto.Enviado, detalle.Estado);
    }

    /// <summary>Regresión (e).</summary>
    [Fact]
    public async Task PresupuestoDispositivoPuedeOperarContraSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoPuedeOperarContraSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-ok");

        var creado = await cajero.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var enviado = await cajero.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());

        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);
        var detalle = (await enviado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoPresupuesto.Enviado, detalle.Estado);
        Assert.StartsWith($"{ctx.IdPuntoVentaPropio:D4}-", detalle.NumeroFormateado);
    }

    /// <summary>Sitio 11 — kill: mismo esquive de confound — el borrador con PV ajeno lo siembra Y
    /// lo envía el actor WEB.</summary>
    [Fact]
    public async Task PresupuestoDispositivoNoPuedeAnularUnoAjeno()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoNoPuedeAnularUnoAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-anular");

        var creadoPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creadoPorWeb.StatusCode);
        var borrador = (await creadoPorWeb.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var enviadoPorWeb = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());
        Assert.Equal(HttpStatusCode.OK, enviadoPorWeb.StatusCode);

        var respuesta = await cajero.PostAsync($"/api/presupuestos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoPresupuesto.Enviado, await LeerEstadoPresupuestoAsync(ctx.IdTenant, borrador.Id));
    }

    /// <summary>Regresión (f): un actor WEB sigue pudiendo anular un presupuesto enviado contra un
    /// punto de venta Escritorio.</summary>
    [Fact]
    public async Task PresupuestoActorWebPuedeAnularContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoActorWebPuedeAnularContraPuntoVentaEscritorio));

        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var enviado = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());
        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);

        var anulado = await ctx.Admin.PostAsync($"/api/presupuestos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);
        var detalle = (await anulado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(EstadoPresupuesto.Anulado, detalle.Estado);
    }

    /// <summary>Regresión (g): un dispositivo puede anular un presupuesto enviado de SU PROPIO
    /// punto de venta.</summary>
    [Fact]
    public async Task PresupuestoDispositivoPuedeAnularSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoPuedeAnularSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx, ctx.IdPuntoVentaPropio, "pres-anular-ok");

        var creado = await cajero.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var enviado = await cajero.PostAsJsonAsync($"/api/presupuestos/{borrador.Id}/enviar", EnvioAFuturo());
        Assert.Equal(HttpStatusCode.OK, enviado.StatusCode);

        var anulado = await cajero.PostAsync($"/api/presupuestos/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.OK, anulado.StatusCode);
        var detalle = (await anulado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(EstadoPresupuesto.Anulado, detalle.Estado);
    }

    // ================================================================================================
    // ---- Órdenes de compra: sitios 7 (CrearBorradorAsync), 8 (ActualizarBorradorAsync), 9 (EnviarAsync) ----
    // ================================================================================================
    //
    // Las rutas de escritura de OrdenesDeCompraEndpoints apilan GestionDeCatalogo (Admin-only)
    // sobre OperacionDePos — así que el cajero de dispositivo de esta sección se loguea con rol
    // Admin (login-dispositivo lo permite: RolesPermitidosEnPos incluye Admin).

    private static SolicitudDeOrdenDeCompra OrdenVacia(Contexto ctx, int idPuntoVenta) =>
        new(ctx.IdProveedor, idPuntoVenta, null, null, []);

    private static SolicitudDeOrdenDeCompra OrdenConLinea(Contexto ctx, int idPuntoVenta) =>
        new(ctx.IdProveedor, idPuntoVenta, null, null, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de prueba", 1m, null)]);

    /// <summary>Sitio 7 — kill (a).</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeCrearContraPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeCrearContraPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-crear", RolConocido.Admin);

        var respuesta = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenVacia(ctx, ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 8 — kill (b).</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeEditarMoviendoAPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-editar", RolConocido.Admin);

        var creada = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenVacia(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var respuesta = await cajero.PutAsJsonAsync(
            $"/api/ordenes-compra/{borrador.Id}", OrdenVacia(ctx, ctx.IdPuntoVentaAjeno));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());
    }

    /// <summary>Sitio 9 — kill (c): mismo esquive de confound — el borrador con PV ajeno lo siembra
    /// el actor WEB (que en esta sección también es Admin, así que basta con el cliente
    /// <c>ctx.Admin</c> de siempre).</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeEnviarUnBorradorConPuntoVentaAjeno()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeEnviarUnBorradorConPuntoVentaAjeno));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-enviar", RolConocido.Admin);

        var creadaPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaAjeno));
        Assert.Equal(HttpStatusCode.Created, creadaPorWeb.StatusCode);
        var borrador = (await creadaPorWeb.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(ctx.IdPuntoVentaAjeno, borrador.IdPuntoVenta);

        var respuesta = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Null(await LeerProximoNumeroAsync(ctx.IdTenant, ctx.IdPuntoVentaAjeno, "OC"));
    }

    /// <summary>Regresión (d).</summary>
    [Fact]
    public async Task OrdenDeCompraActorWebPuedeOperarContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraActorWebPuedeOperarContraPuntoVentaEscritorio));

        var creada = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var enviada = await ctx.Admin.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);

        Assert.Equal(HttpStatusCode.OK, enviada.StatusCode);
        var detalle = (await enviada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoOrdenCompra.Enviada, detalle.Estado);
    }

    /// <summary>Regresión (e).</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoPuedeOperarContraSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoPuedeOperarContraSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-ok", RolConocido.Admin);

        var creada = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var enviada = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);

        Assert.Equal(HttpStatusCode.OK, enviada.StatusCode);
        var detalle = (await enviada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.NotNull(detalle.Numero);
        Assert.Equal(EstadoOrdenCompra.Enviada, detalle.Estado);
    }

    /// <summary>Sitio 12 — kill: mismo esquive de confound — el borrador con PV ajeno lo siembra Y
    /// lo envía el actor WEB (Admin en esta sección).</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeCerrarUnaAjena()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeCerrarUnaAjena));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-cerrar", RolConocido.Admin);

        var creadaPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaAjeno));
        Assert.Equal(HttpStatusCode.Created, creadaPorWeb.StatusCode);
        var borrador = (await creadaPorWeb.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var enviadaPorWeb = await ctx.Admin.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);
        Assert.Equal(HttpStatusCode.OK, enviadaPorWeb.StatusCode);

        var respuesta = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/cerrar", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Enviada, await LeerEstadoOrdenDeCompraAsync(ctx.IdTenant, borrador.Id));
    }

    /// <summary>Sitio 13 — kill: el borrador con PV ajeno lo siembra el actor WEB (Admin), sin
    /// necesidad de enviarlo — <c>AnularAsync</c> admite <c>borrador</c>.</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeAnularUnaAjena()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeAnularUnaAjena));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-anular", RolConocido.Admin);

        var creadaPorWeb = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaAjeno));
        Assert.Equal(HttpStatusCode.Created, creadaPorWeb.StatusCode);
        var borrador = (await creadaPorWeb.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var respuesta = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/anular", content: null);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await LeerCodigoAsync(respuesta);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Borrador, await LeerEstadoOrdenDeCompraAsync(ctx.IdTenant, borrador.Id));
    }

    /// <summary>Regresión (f): un actor WEB (Admin) sigue pudiendo cerrar una orden de compra
    /// enviada contra un punto de venta Escritorio.</summary>
    [Fact]
    public async Task OrdenDeCompraActorWebPuedeCerrarContraPuntoVentaEscritorio()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraActorWebPuedeCerrarContraPuntoVentaEscritorio));

        var creada = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var enviada = await ctx.Admin.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);
        Assert.Equal(HttpStatusCode.OK, enviada.StatusCode);

        var cerrada = await ctx.Admin.PostAsync($"/api/ordenes-compra/{borrador.Id}/cerrar", content: null);

        Assert.Equal(HttpStatusCode.OK, cerrada.StatusCode);
        var detalle = (await cerrada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(EstadoOrdenCompra.Cerrada, detalle.Estado);
    }

    /// <summary>Regresión (g): un dispositivo puede cerrar una orden de compra enviada de SU
    /// PROPIO punto de venta.</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoPuedeCerrarSuPropioPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoPuedeCerrarSuPropioPuntoVenta));
        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-cerrar-ok", RolConocido.Admin);

        var creada = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var enviada = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);
        Assert.Equal(HttpStatusCode.OK, enviada.StatusCode);

        var cerrada = await cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/cerrar", content: null);

        Assert.Equal(HttpStatusCode.OK, cerrada.StatusCode);
        var detalle = (await cerrada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(EstadoOrdenCompra.Cerrada, detalle.Estado);
    }
}
