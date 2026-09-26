using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Pos;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-pos-venta-offline-backend (Parte A, DB CHANGE GATE: sin cambio de esquema): superficie
/// HTTP completa de <c>GET /api/pos/instantanea</c>. Mismo trámite de siembra que
/// <c>ReservaDeNumeracionEndpointsTests</c>/<c>VentasModoPuntoVentaTests</c> — no se comparte
/// helper entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class InstantaneaDePosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, resultado.IdTenant, resultado.IdPuntoVenta);
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

    /// <summary>Vincula un dispositivo, siembra un cajero y devuelve un <see cref="HttpClient"/> ya
    /// logueado vía <c>login-dispositivo</c> — mismo flujo que <c>ReservaDeNumeracionEndpointsTests</c>.</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = $"cajero-{sufijo}",
                Mail = $"cajero-{sufijo}-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
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

    /// <summary>Artículo con precio vigente en la lista default y dos códigos de barra — la forma
    /// mínima que <see cref="ArticuloDeInstantanea"/> tiene que reflejar completa.</summary>
    private async Task<int> SembrarArticuloConPrecioYBarrasAsync(
        int idTenant, string sufijo, decimal precio, params string[] codigosBarra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Area-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"art-{sufijo}-{Guid.NewGuid():N}",
            Nombre = $"Artículo {sufijo}",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        foreach (var codigo in codigosBarra)
        {
            db.CodigosBarra.Add(new CodigoBarra
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, Codigo = codigo, Activo = true,
                CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            IdListaPrecio = idListaGeneral,
            Monto = precio,
            VigenteDesde = ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    /// <summary>Artículo ACTIVO pero SIN ninguna fila en <c>precios</c> — el caso que la
    /// instantánea tiene que omitir (mismo motivo que <c>articulo_sin_precio_vigente</c> online).</summary>
    private async Task<int> SembrarArticuloSinPrecioAsync(int idTenant, string sufijo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Area-sin-precio-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"art-sin-precio-{sufijo}-{Guid.NewGuid():N}",
            Nombre = $"Artículo sin precio {sufijo}",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    /// <summary>Oferta de porcentaje sin restricción de lista/empresa/fecha/hora — aplica a
    /// CUALQUIER resolución de ese artículo (<c>ListasObjetivo</c> vacío = sin restricción, ver
    /// <c>ResolvedorDeOfertas.Coincide</c>). Con <paramref name="cantidadMinima"/> seteada es una
    /// oferta por volumen: NO aplica a la cantidad 1 con la que la instantánea resuelve el
    /// resultado plano, solo a partir de su umbral (<c>ArticuloDeInstantanea.Escalones</c>).</summary>
    private async Task SembrarOfertaDePorcentajeAsync(
        int idTenant, int idArticulo, decimal porcentaje, decimal? cantidadMinima = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.Ofertas.Add(new Oferta
        {
            IdTenant = idTenant,
            Nombre = $"Descuento {porcentaje}%",
            IdArticulo = idArticulo,
            Porcentaje = porcentaje,
            CantidadMinima = cantidadMinima,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Bypass directo del ABM (<c>POST /api/organizacion/puntos-venta/{id}/modo</c> exige
    /// "sin dispositivo activo", 409 si lo tiene) — la ÚNICA forma de construir el estado "un
    /// dispositivo vigente, vinculado a un punto de venta que HOY es Web" para probar que
    /// <c>ServicioDeInstantaneaDePos</c> de verdad llama a <c>PoliticaDeModoDePuntoVenta</c> (y no
    /// solo confía en que el dispositivo esté vinculado). Mismo criterio que
    /// <c>SembrarReservaDirectaAsync</c> (SQL crudo para un estado que el ABM normal no permite
    /// alcanzar).</summary>
    private async Task CambiarModoDePuntoVentaDirectoAsync(int idTenant, int idPuntoVenta, ModoPuntoVenta modo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        // `modo` viaja como parámetro Npgsql tipado (mapeo global `npgsql.MapEnum<ModoPuntoVenta>`,
        // DependencyInjection.cs) — nunca un (int)/cast de texto, que no coincide con el tipo
        // enum nativo `modo_punto_venta` de la columna.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE puntos_venta SET modo = {modo} WHERE id_punto_venta = {idPuntoVenta}");
    }

    [Fact]
    public async Task UnActorWebNoPuedePedirLaInstantanea()
    {
        var (admin, _, _) = await AprovisionarComoAdminAsync(nameof(UnActorWebNoPuedePedirLaInstantanea));
        using var _admin = admin;

        var respuesta = await admin.GetAsync("/api/pos/instantanea");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnDispositivoCuyoPuntoVentaPasoAModoWebRecibeConflicto()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoCuyoPuntoVentaPasoAModoWebRecibeConflicto));
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "modo-web");
        using var _cajero = cajero;
        admin.Dispose();

        // Mutación CORRIDA (mutation-proof-tests regla 2): con la línea
        // "await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(...)" comentada en
        // ServicioDeInstantaneaDePos.ObtenerAsync, este mismo request cayó en 200 en vez de 409 —
        // observado, no razonado — y ningún otro test de este archivo (los 6 restantes) se movió.
        // Revertida, vuelve a verde.
        await CambiarModoDePuntoVentaDirectoAsync(idTenant, idPuntoVenta, ModoPuntoVenta.Web);

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LaInstantaneaTraeElArticuloConPrecioIvaYCodigosDeBarraCompletos()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaInstantaneaTraeElArticuloConPrecioIvaYCodigosDeBarraCompletos));
        var idArticulo = await SembrarArticuloConPrecioYBarrasAsync(idTenant, "completo", 250m, "7791234560001", "7791234560002");
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "completo");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var instantanea = (await respuesta.Content.ReadFromJsonAsync<InstantaneaDePos>(OpcionesJson))!;
        Assert.Equal(idPuntoVenta, instantanea.IdPuntoVenta);
        Assert.True((DateTimeOffset.UtcNow - instantanea.Momento).Duration() < TimeSpan.FromMinutes(1));

        var articulo = Assert.Single(instantanea.Articulos, a => a.IdArticulo == idArticulo);
        Assert.Equal(250m, articulo.PrecioOriginal);
        Assert.Equal(250m, articulo.PrecioFinal);
        Assert.Equal(0m, articulo.DescuentoUnitario);
        Assert.Empty(articulo.Aplicadas);
        Assert.Equal(["7791234560001", "7791234560002"], articulo.CodigosBarra.OrderBy(c => c));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var alicuota = await db.Articulos.Where(a => a.Id == idArticulo)
            .Select(a => new { a.IdAlicuotaIva }).FirstAsync();
        var porcentaje = await db.AlicuotasIva.Where(a => a.Id == alicuota.IdAlicuotaIva)
            .Select(a => a.Porcentaje).FirstAsync();
        Assert.Equal(alicuota.IdAlicuotaIva, articulo.IdAlicuotaIva);
        Assert.Equal(porcentaje, articulo.PorcentajeIva);
    }

    [Fact]
    public async Task LaInstantaneaOmiteUnArticuloActivoSinPrecioVigente()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaInstantaneaOmiteUnArticuloActivoSinPrecioVigente));
        var idConPrecio = await SembrarArticuloConPrecioYBarrasAsync(idTenant, "con-precio", 100m);
        var idSinPrecio = await SembrarArticuloSinPrecioAsync(idTenant, "sin-precio");
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "omite");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        var instantanea = (await respuesta.Content.ReadFromJsonAsync<InstantaneaDePos>(OpcionesJson))!;

        Assert.Contains(instantanea.Articulos, a => a.IdArticulo == idConPrecio);
        Assert.DoesNotContain(instantanea.Articulos, a => a.IdArticulo == idSinPrecio);
    }

    [Fact]
    public async Task LaInstantaneaResuelveUnaOfertaVigenteYCongelaElPrecioFinal()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaInstantaneaResuelveUnaOfertaVigenteYCongelaElPrecioFinal));
        var idArticulo = await SembrarArticuloConPrecioYBarrasAsync(idTenant, "oferta", 200m);
        await SembrarOfertaDePorcentajeAsync(idTenant, idArticulo, 10m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "oferta");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        var instantanea = (await respuesta.Content.ReadFromJsonAsync<InstantaneaDePos>(OpcionesJson))!;

        var articulo = Assert.Single(instantanea.Articulos, a => a.IdArticulo == idArticulo);
        Assert.Equal(200m, articulo.PrecioOriginal);
        Assert.Equal(20m, articulo.DescuentoUnitario);
        Assert.Equal(180m, articulo.PrecioFinal);
        var aplicada = Assert.Single(articulo.Aplicadas);
        Assert.Equal($"Descuento 10%", aplicada.Nombre);
        Assert.Equal(20m, aplicada.DescuentoUnitario);

        // Una oferta directa (sin cantidad_minima) ya vive en los campos planos: no hay curva.
        Assert.Null(articulo.Escalones);
    }

    /// <summary>
    /// Una oferta con <c>cantidad_minima &gt; 1</c> NO aplica a la cantidad 1 con la que la
    /// instantánea resuelve el precio plano, y antes de esta etapa se perdía entera: el precio del
    /// dispositivo es autoritativo (<c>ServicioDeVentas.MaterializarItems</c> cobra
    /// <c>linea.PrecioUnitario</c> tal cual), así que la venta offline de 6 unidades se cobraba sin
    /// el descuento por volumen. Ahora viaja como escalón, ya resuelto por el motor real.
    ///
    /// <para>El segundo artículo (sin oferta por volumen) prueba la compatibilidad del payload: su
    /// clave <c>escalones</c> queda AUSENTE del JSON — el conteo de ocurrencias en el cuerpo crudo
    /// es 1, no 2 —, que es lo que lee un dispositivo que quedó offline cruzando el deploy.</para>
    /// </summary>
    [Fact]
    public async Task LaInstantaneaTraeLaCurvaDeEscalonesDeUnaOfertaPorVolumen()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaInstantaneaTraeLaCurvaDeEscalonesDeUnaOfertaPorVolumen));
        var idConVolumen = await SembrarArticuloConPrecioYBarrasAsync(idTenant, "volumen", 200m);
        var idSinVolumen = await SembrarArticuloConPrecioYBarrasAsync(idTenant, "sin-volumen", 300m);
        await SembrarOfertaDePorcentajeAsync(idTenant, idConVolumen, 10m, cantidadMinima: 6m);
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "volumen");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        var instantanea = JsonSerializer.Deserialize<InstantaneaDePos>(cuerpo, OpcionesJson)!;

        var conVolumen = Assert.Single(instantanea.Articulos, a => a.IdArticulo == idConVolumen);
        Assert.Equal(200m, conVolumen.PrecioOriginal);
        Assert.Equal(200m, conVolumen.PrecioFinal);
        Assert.Equal(0m, conVolumen.DescuentoUnitario);
        Assert.Empty(conVolumen.Aplicadas);

        var escalon = Assert.Single(conVolumen.Escalones!);
        Assert.Equal(6m, escalon.CantidadDesde);
        Assert.Equal(20m, escalon.DescuentoUnitario);
        Assert.Equal(180m, escalon.PrecioFinal);
        var aplicada = Assert.Single(escalon.Aplicadas);
        Assert.Equal("Descuento 10%", aplicada.Nombre);
        Assert.Equal(20m, aplicada.DescuentoUnitario);

        var sinVolumen = Assert.Single(instantanea.Articulos, a => a.IdArticulo == idSinVolumen);
        Assert.Equal(300m, sinVolumen.PrecioFinal);
        Assert.Null(sinVolumen.Escalones);
        Assert.Single(Regex.Matches(cuerpo, "escalones"));
    }

    [Fact]
    public async Task LaInstantaneaTraeTodoElCatalogoActivoSinElTopeDePaginaDeLaGrilla()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LaInstantaneaTraeTodoElCatalogoActivoSinElTopeDePaginaDeLaGrilla));

        const int cantidad = 30; // > 25, el tamaño default de ServicioDeArticulos.ListarAsync.
        for (var i = 0; i < cantidad; i++)
        {
            await SembrarArticuloConPrecioYBarrasAsync(idTenant, $"masivo-{i}", 10m + i);
        }

        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "masivo");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        var instantanea = (await respuesta.Content.ReadFromJsonAsync<InstantaneaDePos>(OpcionesJson))!;

        Assert.Equal(cantidad, instantanea.Articulos.Count);
    }

    [Fact]
    public async Task LosMediosDePagoActivosVienenConSusFlagsYToleranciaDePagoResuelveElDefault()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(LosMediosDePagoActivosVienenConSusFlagsYToleranciaDePagoResuelveElDefault));
        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "medios");
        using var _cajero = cajero;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var efectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).FirstAsync();
        admin.Dispose();

        var respuesta = await cajero.GetAsync("/api/pos/instantanea");
        var instantanea = (await respuesta.Content.ReadFromJsonAsync<InstantaneaDePos>(OpcionesJson))!;

        var medio = Assert.Single(instantanea.MediosDePago, m => m.IdMedioPago == efectivo.Id);
        Assert.Equal(efectivo.Nombre, medio.Nombre);
        Assert.Equal(efectivo.Comportamiento, medio.Comportamiento);
        Assert.Equal(efectivo.AdmiteVuelto, medio.AdmiteVuelto);
        Assert.Equal(efectivo.RequiereReferencia, medio.RequiereReferencia);

        // ParametroConocido.ToleranciaPago.ValorPorDefecto == "10", sin fila en `parametros` para
        // este tenant nuevo.
        Assert.Equal(10m, instantanea.ToleranciaPago);
    }
}
