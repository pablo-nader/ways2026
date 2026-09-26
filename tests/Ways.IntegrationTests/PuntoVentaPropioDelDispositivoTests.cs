using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
using Ways.Infrastructure.Persistencia;
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
/// sitio 13: <c>ServicioDeOrdenesDeCompra.AnularAsync</c>).
///
/// Los sitios 10-13 tienen DOS chequeos, no uno: la pre-lectura de la que autoriza el atajo y la
/// autoridad in-transacción que lee el <c>id_punto_venta</c> de la fila recién bloqueada. Los
/// tests secuenciales de esos cuatro sitios mueren en la pre-lectura, así que NO cubren el
/// chequeo in-transacción — eso lo hacen las cuatro <c>…ViaCarreraRealQueMueveElPuntoVenta</c>
/// (rendezvous con <see cref="InterceptorDePausaTrasElPreLecturaDeGuard"/>, judgment-day ronda 2):
/// son las únicas que lo matan, y el par secuencial/carrera es la demostración del confound que
/// pide mutation-proof-tests regla 3.
///
/// Más dos familias de regresión por servicio (una para creación/emisión, una para anular/cerrar):
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
    /// sobre <c>OperacionDePos</c> (<c>OrdenesDeCompraEndpoints.cs</c>). <paramref
    /// name="crearCliente"/> (judgment-day ronda 2) permite que las pruebas de carrera real logueen
    /// al cajero contra un <c>WebApplicationFactory</c> con un <c>DbCommandInterceptor</c> propio —
    /// default <c>fixture.CreateClient</c>, sin interceptor, igual que siempre.</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        Contexto ctx, int idPuntoVenta, string sufijo, RolConocido rol = RolConocido.Vendedor,
        Func<HttpClient>? crearCliente = null)
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

        var cajero = (crearCliente ?? fixture.CreateClient)();
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

    // ==================================================================================================
    // ---- Carrera real (judgment-day ronda 2): la autoridad in-transacción, no la pre-lectura --------
    // ==================================================================================================
    //
    // Los sitios 10-13 re-verifican el guard DOS VECES: la pre-lectura de arriba (atajo barato,
    // fuera de la transacción) Y el UPDATE/lock guardado (autoridad, adentro, sobre el
    // id_punto_venta que ESE lock vio). Las pruebas secuenciales de arriba mueren en la pre-lectura
    // — nunca ejercitan la segunda verificación (mutation-proof-tests regla 3: "confound: un
    // PRE-CHECK que espeja un guard transaccional"). Estas cuatro fuerzan la carrera real con un
    // DbCommandInterceptor: pausan justo DESPUÉS de que la pre-lectura EF (AsNoTracking, LINQ)
    // ejecutó — los UPDATEs/locks guardados corren sobre DbConnection.CreateCommand() crudo, fuera
    // del pipeline de interceptores de EF, así que nunca disparan este hook (mismo patrón que
    // AuditoriaAnulacionVentaTests.InterceptorDePausaTrasElPreLecturaDeAnulacion).

    /// <summary>Pausa la primera query cuyo <c>CommandText</c> es un <c>SELECT</c> que toca
    /// <paramref name="tabla"/> justo DESPUÉS de que ejecutó — un rendezvous de UN solo participante
    /// (a diferencia de <c>ParametrosTests.InterceptorDeRendezVous</c>, acá alcanza con soltar la
    /// carrera cuando el actor externo terminó: ese actor usa <c>ctx.Admin</c>, un cliente de OTRO
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> sin este interceptor, así que nunca pasa por
    /// acá). El filtro <c>StartsWith("SELECT")</c> es necesario, no cosmético: el <c>cajero</c>
    /// también crea el borrador ANTES de llamar anular/cerrar, y ese <c>INSERT ... RETURNING</c>
    /// también toca <paramref name="tabla"/> y también corre vía <c>ReaderExecutedAsync</c> (Npgsql
    /// lee el id generado con un <c>DataReader</c>) — sin el filtro, el rendezvous se dispara
    /// durante la CREACIÓN y el pre-read real de anular/cerrar nunca lo encuentra pausado.</summary>
    private sealed class InterceptorDePausaTrasElPreLecturaDeGuard(
        string tabla, TaskCompletionSource preLecturaLista, TaskCompletionSource puedeContinuar) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(tabla, StringComparison.OrdinalIgnoreCase))
            {
                preLecturaLista.TrySetResult();
                await puedeContinuar.Task;
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Sitio 10, carrera real: el remito nace <c>borrador</c> en el PV PROPIO del
    /// dispositivo. El dispositivo llama <c>anular</c> de inmediato — la pre-lectura lee el PV
    /// propio (pasa el atajo) y se pausa ahí. Un actor WEB (intocado por el guard) mueve el remito
    /// al PV AJENO (todavía <c>borrador</c>, legal) y lo EMITE ahí — escribe stock real en el PV
    /// ajeno. Al reanudar, el <c>UPDATE</c> guardado de <c>MarcarAnuladoAsync</c> matchea por
    /// <c>estado</c> solo (ahora <c>emitido</c>) y devuelve el PV YA movido — la autoridad
    /// in-transacción rechaza ANTES de que el loop de reversa toque una sola fila. Discriminante
    /// (mutation-proof-tests regla 4): el stock del PV ajeno, ya decrementado por la emisión, queda
    /// EXACTAMENTE igual — nunca revertido.</summary>
    [Fact]
    public async Task RemitoDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(RemitoDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta));

        var preLecturaLista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasElPreLecturaDeGuard("remitos", preLecturaLista, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "rem-race", crearCliente: factory.CreateClient);

        var creado = await cajero.PostAsJsonAsync(
            "/api/remitos", RemitoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var tareaAnular = cajero.PostAsync($"/api/remitos/{borrador.Id}/anular", content: null);

        await preLecturaLista.Task;

        var relink = await ctx.Admin.PutAsJsonAsync(
            $"/api/remitos/{borrador.Id}", RemitoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

        var emitidoEnAjeno = await ctx.Admin.PostAsync($"/api/remitos/{borrador.Id}/emitir", content: null);
        var cuerpoEmitido = await emitidoEnAjeno.Content.ReadAsStringAsync();
        Assert.True(emitidoEnAjeno.StatusCode == HttpStatusCode.OK, cuerpoEmitido);

        var stockTrasEmitir = await LeerStockAsync(ctx.IdTenant, ctx.IdArticulo, ctx.IdPuntoVentaAjeno);
        Assert.Equal(-1m, stockTrasEmitir);

        puedeContinuar.TrySetResult();

        var respuesta = await tareaAnular;
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(stockTrasEmitir, await LeerStockAsync(ctx.IdTenant, ctx.IdArticulo, ctx.IdPuntoVentaAjeno));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var remitoFinal = await db.Remitos.AsNoTracking().FirstAsync(r => r.Id == borrador.Id);
        Assert.Equal(EstadoRemito.Emitido, remitoFinal.Estado);
        Assert.Equal(ctx.IdPuntoVentaAjeno, remitoFinal.IdPuntoVenta);
    }

    /// <summary>Sitio 11, carrera real: el presupuesto nace <c>borrador</c> en el PV PROPIO. El
    /// dispositivo llama <c>anular</c> de inmediato — la pre-lectura lee el PV propio (pasa el
    /// atajo) y se pausa ahí. Un actor WEB mueve el presupuesto al PV AJENO (todavía <c>borrador</c>,
    /// legal — <c>anular</c> admite <c>borrador</c> directo, sin necesidad de enviarlo). Al
    /// reanudar, el <c>UPDATE</c> guardado matchea por <c>estado</c> (sigue <c>borrador</c>) y
    /// devuelve el PV YA movido — la autoridad in-transacción rechaza. Discriminante: el presupuesto
    /// sigue <c>borrador</c> en el PV ajeno, nunca <c>anulado</c>.</summary>
    [Fact]
    public async Task PresupuestoDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(PresupuestoDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta));

        var preLecturaLista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasElPreLecturaDeGuard("presupuestos", preLecturaLista, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "pres-race", crearCliente: factory.CreateClient);

        var creado = await cajero.PostAsJsonAsync(
            "/api/presupuestos", PresupuestoConLinea(ctx.IdPuntoVentaPropio, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var tareaAnular = cajero.PostAsync($"/api/presupuestos/{borrador.Id}/anular", content: null);

        await preLecturaLista.Task;

        var relink = await ctx.Admin.PutAsJsonAsync(
            $"/api/presupuestos/{borrador.Id}", PresupuestoConLinea(ctx.IdPuntoVentaAjeno, ctx.IdArticulo));
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

        puedeContinuar.TrySetResult();

        var respuesta = await tareaAnular;
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoPresupuesto.Borrador, await LeerEstadoPresupuestoAsync(ctx.IdTenant, borrador.Id));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var presupuestoFinal = await db.Presupuestos.AsNoTracking().FirstAsync(p => p.Id == borrador.Id);
        Assert.Equal(ctx.IdPuntoVentaAjeno, presupuestoFinal.IdPuntoVenta);
    }

    /// <summary>Sitio 12, carrera real: la OC nace <c>borrador</c> en el PV PROPIO. El dispositivo
    /// llama <c>cerrar</c> de inmediato (todavía <c>borrador</c> — la pre-lectura de
    /// <c>CerrarAsync</c> no filtra por estado, solo lee el PV): la pre-lectura lee el PV propio
    /// (pasa el atajo) y se pausa ahí. Un actor WEB mueve la OC al PV AJENO (todavía <c>borrador</c>,
    /// legal) Y la ENVÍA ahí — <c>enviada</c> congela el PV nuevo. Al reanudar,
    /// <c>CerrarHeaderAsync</c> matchea por <c>estado</c> (ahora <c>enviada</c>) y devuelve el PV YA
    /// movido — la autoridad in-transacción rechaza. Discriminante: la OC sigue <c>enviada</c> en el
    /// PV ajeno, nunca <c>cerrada</c>.</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeCerrarViaCarreraRealQueMueveElPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeCerrarViaCarreraRealQueMueveElPuntoVenta));

        var preLecturaLista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasElPreLecturaDeGuard("ordenes_compra", preLecturaLista, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-cerrar-race", RolConocido.Admin, factory.CreateClient);

        var creada = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var tareaCerrar = cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/cerrar", content: null);

        await preLecturaLista.Task;

        var relink = await ctx.Admin.PutAsJsonAsync(
            $"/api/ordenes-compra/{borrador.Id}", OrdenConLinea(ctx, ctx.IdPuntoVentaAjeno));
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

        var enviadaEnAjeno = await ctx.Admin.PostAsync($"/api/ordenes-compra/{borrador.Id}/enviar", content: null);
        var cuerpoEnviada = await enviadaEnAjeno.Content.ReadAsStringAsync();
        Assert.True(enviadaEnAjeno.StatusCode == HttpStatusCode.OK, cuerpoEnviada);

        puedeContinuar.TrySetResult();

        var respuesta = await tareaCerrar;
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Enviada, await LeerEstadoOrdenDeCompraAsync(ctx.IdTenant, borrador.Id));
    }

    /// <summary>Sitio 13, carrera real: la OC nace <c>borrador</c> en el PV PROPIO. El dispositivo
    /// llama <c>anular</c> de inmediato — el ÚNICO lock del método
    /// (<c>BloquearYLeerEstadoOrdenAsync</c>, statement 1) todavía no corrió cuando la pre-lectura
    /// de <c>AnularAsync</c> se pausa (lee el PV propio, pasa el atajo). Un actor WEB mueve la OC al
    /// PV AJENO (todavía <c>borrador</c>, legal — <c>anular</c> admite <c>borrador</c> directo). Al
    /// reanudar, el statement 1 toma el lock y lee el PV YA movido — la autoridad in-transacción
    /// rechaza ANTES de los statements 2-4. Discriminante: la OC sigue <c>borrador</c> en el PV
    /// ajeno, nunca <c>anulada</c>.</summary>
    [Fact]
    public async Task OrdenDeCompraDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(OrdenDeCompraDispositivoNoPuedeAnularViaCarreraRealQueMueveElPuntoVenta));

        var preLecturaLista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasElPreLecturaDeGuard("ordenes_compra", preLecturaLista, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(
            ctx, ctx.IdPuntoVentaPropio, "oc-anular-race", RolConocido.Admin, factory.CreateClient);

        var creada = await cajero.PostAsJsonAsync("/api/ordenes-compra", OrdenConLinea(ctx, ctx.IdPuntoVentaPropio));
        Assert.Equal(HttpStatusCode.Created, creada.StatusCode);
        var borrador = (await creada.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var tareaAnular = cajero.PostAsync($"/api/ordenes-compra/{borrador.Id}/anular", content: null);

        await preLecturaLista.Task;

        var relink = await ctx.Admin.PutAsJsonAsync(
            $"/api/ordenes-compra/{borrador.Id}", OrdenConLinea(ctx, ctx.IdPuntoVentaAjeno));
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

        puedeContinuar.TrySetResult();

        var respuesta = await tareaAnular;
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal(CodigoRechazo, problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Borrador, await LeerEstadoOrdenDeCompraAsync(ctx.IdTenant, borrador.Id));
    }
}
