using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios; // SolicitudDeLogin
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3 (tasks 3.5, 3.7-3.12, db-error-backstops skill): <c>ServicioDePrecios</c>/las rutas
/// <c>/api/articulos/{id}/precios*</c> punta a punta contra Postgres real — cierre-y-apertura
/// transaccional, precios programables con "a lo sumo un pendiente", resolución por fecha,
/// resolución de listas derivadas en lectura, y la carrera GENUINA de
/// <c>ux_precios_vigente</c> (primer precio de un par, sin fila que lockear).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PreciosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    // Mismo motivo que ArticulosEndpointsTests.OpcionesJson: el server registra
    // JsonStringEnumConverter (Program.cs) pero ReadFromJsonAsync<T>() sin opciones usa las
    // opciones DEFAULT del lado cliente, que no lo traen — ArticuloListado.UnidadVenta (nunca
    // null) revienta la deserialización sin esto.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, int IdListaGeneral, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        // La lista General (fija, es_default) ya viene sembrada por ServicioDeAprovisionamiento
        // (stage-2-clientes-proveedores) — no hace falta crearla acá.
        var idListaGeneral = await db.ListasPrecio.Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault).Select(l => l.Id).SingleAsync();

        return (resultado.IdTenant, area.Id, idAlicuotaIva, idListaGeneral, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarVendedorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "vendedor",
            Mail = mail,
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    /// <summary>Siembra una lista de precios directo por EF (bypass del ABM — Slice 4 todavía no
    /// existe esta slice). Modo <see cref="ModoLista.Derivada"/> requiere <paramref
    /// name="idListaBase"/>/<paramref name="porcentaje"/>.</summary>
    private async Task<int> SembrarListaPrecioAsync(
        int idTenant, string nombre, ModoLista modo = ModoLista.Fija, int? idListaBase = null, decimal? porcentaje = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = nombre,
            EsDefault = false,
            Modo = modo,
            IdListaBase = idListaBase,
            Porcentaje = porcentaje,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    private static AltaArticulo AltaValida(int idArea, int idAlicuotaIva, string nombre = "Artículo de prueba") =>
        new(
            CodigoInterno: null,
            Nombre: nombre,
            Descripcion: null,
            IdArea: idArea,
            IdCategoria: null,
            IdMarca: null,
            IdGrupo: null,
            IdProveedorHabitual: null,
            IdAlicuotaIva: idAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad,
            UnidadesPorBulto: null,
            EsProducto: true,
            CostoLista: null,
            DescuentoProveedor: null,
            CostoNominal: null);

    private static async Task<ArticuloListado> CrearArticuloAsync(
        HttpClient cliente, int idArea, int idAlicuotaIva, string nombre = "Artículo de prueba")
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/articulos", AltaValida(idArea, idAlicuotaIva, nombre));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
    }

    // ---- task 3.7: close-and-open transaction -----------------------------------------------

    /// <summary>Spec: "Changing a price closes the old row and opens a new one".</summary>
    [Fact]
    public async Task UnCambioDePrecioCierraLaFilaAnteriorYAbreUnaNueva()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnCambioDePrecioCierraLaFilaAnteriorYAbreUnaNueva));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var primero = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        var segundo = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 120m));
        Assert.Equal(HttpStatusCode.Created, segundo.StatusCode);

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(2, historial!.Count);

        var filaVieja = historial.Single(h => h.Precio == 100m);
        var filaNueva = historial.Single(h => h.Precio == 120m);

        Assert.Null(filaNueva.VigenteHasta);
        Assert.NotNull(filaVieja.VigenteHasta);
        Assert.Equal(filaNueva.VigenteDesde, filaVieja.VigenteHasta);
    }

    /// <summary>Spec: "Historical prices remain queryable" — tres cambios de precio, las tres
    /// filas siguen siendo consultables con su vigente_desde/vigente_hasta.</summary>
    [Fact]
    public async Task ElHistorialCompletoQuedaDisponibleTrasVariosCambios()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElHistorialCompletoQuedaDisponibleTrasVariosCambios));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        foreach (var precio in new[] { 100m, 110m, 120m })
        {
            var respuesta = await admin.PostAsJsonAsync(
                $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, precio));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(3, historial!.Count);
        Assert.Equal([100m, 110m, 120m], historial.Select(h => h.Precio).OrderBy(p => p));
        Assert.Single(historial, h => h.VigenteHasta == null);
    }

    // ---- task 3.8: pending-future, at most one pending --------------------------------------

    /// <summary>Spec: "Scheduling a future price with none pending" succeeds sin afectar el
    /// precio vigente.</summary>
    [Fact]
    public async Task ProgramarUnPrecioFuturoSinPendientePreviaSucede()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ProgramarUnPrecioFuturoSinPendientePreviaSucede));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var inmediato = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, inmediato.StatusCode);

        var vigenteEnTresDias = DateTimeOffset.UtcNow.AddDays(3);
        var programado = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, vigenteEnTresDias));
        Assert.Equal(HttpStatusCode.Created, programado.StatusCode);

        // El precio vigente HOY sigue siendo el inmediato — el programado todavía no tomó efecto.
        var vigente = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}");
        Assert.Equal(100m, vigente!.Precio);
    }

    /// <summary>Spec: "Scheduling replaces the existing pending price" — sin confirmación
    /// rechaza con 409 <c>precio_pendiente_existe</c>; con <c>confirmarReemplazo: true</c>
    /// reemplaza, y el precio reemplazado NUNCA se vuelve visible (ni siquiera en la ventana
    /// entre las dos fechas programadas).
    ///
    /// También es la guarda de regresión de la exclusión por id en
    /// <see cref="ServicioDePrecios.BuscarPredecesorAsync"/> (judgment-day, item 1): acá
    /// "primero" es directamente el PRIMER precio del par (no hay una fila activa previa), así
    /// que al reemplazarlo no existe un predecesor real. Sin excluir
    /// <c>idFilaPendienteCerrada</c> de la búsqueda, la propia "primero" recién cerrada matchearía
    /// como su propio predecesor (<c>vigente_hasta == limiteOriginal</c>) y se re-abriría a sí
    /// misma en la ventana intermedia — exactamente lo que la aserción de la línea de abajo
    /// prueba que NO pasa.</summary>
    [Fact]
    public async Task ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ProgramarUnSegundoPrecioPendienteSinConfirmarDevuelve409YConConfirmarReemplaza));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var vigenteEnTresDias = DateTimeOffset.UtcNow.AddDays(3);
        var primero = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, vigenteEnTresDias));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        var vigenteEnDiezDias = DateTimeOffset.UtcNow.AddDays(10);

        var sinConfirmar = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 160m, vigenteEnDiezDias));
        Assert.Equal(HttpStatusCode.Conflict, sinConfirmar.StatusCode);
        var problema = await sinConfirmar.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_pendiente_existe", problema.GetProperty("codigo").GetString());

        var conConfirmar = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 160m, vigenteEnDiezDias, ConfirmarReemplazo: true));
        Assert.Equal(HttpStatusCode.Created, conConfirmar.StatusCode);

        // El pendiente reemplazado ($150, a 3 días) nunca se vuelve visible — ni siquiera en la
        // ventana entre día 3 y día 10, que es exactamente lo que estaría MAL si se hubiera
        // cerrado en el vigente_desde de la fila nueva en lugar del propio.
        var enElMedio = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(5).ToString("O"))}");
        Assert.Null(enElMedio!.Precio);

        var enDiezDias = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(vigenteEnDiezDias.AddMinutes(1).ToString("O"))}");
        Assert.Equal(160m, enDiezDias!.Precio);
    }

    // ---- judgment-day ronda 1, item 1: predecessor re-close on pending replacement ----------

    /// <summary>Secuencia (a) del hallazgo CRITICAL: inmediato → programado → inmediato con
    /// reemplazo, donde el reemplazo cae ANTES de la fecha del pendiente original. Sin el fix
    /// del predecesor, el inmediato original (100) quedaría abierto hasta la fecha vieja del
    /// pendiente (t+3d), SOLAPANDO con la fila nueva (160) — dos filas satisfarían el predicado
    /// "vigente" en ese rango. Este test prueba el re-cierre del predecesor y que exactamente una
    /// fila esté vigente en cada instante sondeado.</summary>
    [Fact]
    public async Task ReemplazarUnPendienteConUnaFechaAnteriorALaOriginalRecierraElPredecesorSinSolapar()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ReemplazarUnPendienteConUnaFechaAnteriorALaOriginalRecierraElPredecesorSinSolapar));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var inmediato = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, inmediato.StatusCode);

        var vigenteEnTresDias = DateTimeOffset.UtcNow.AddDays(3);
        var programado = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, vigenteEnTresDias));
        Assert.Equal(HttpStatusCode.Created, programado.StatusCode);

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Reemplazo INMEDIATO del pendiente ($150 a t+3d) -- "ahora" es muy anterior a t+3d.
        var reemplazo = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios",
            new AltaPrecio(idListaGeneral, 160m, ConfirmarReemplazo: true));
        Assert.Equal(HttpStatusCode.Created, reemplazo.StatusCode);
        var vigenteDesdeDelReemplazo = (await reemplazo.Content.ReadFromJsonAsync<PrecioVigente>())!.Fecha;

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(3, historial!.Count);

        var filaInmediataOriginal = historial.Single(h => h.Precio == 100m);
        var filaPendienteReemplazada = historial.Single(h => h.Precio == 150m);
        var filaFinal = historial.Single(h => h.Precio == 160m);

        // El predecesor (100) se re-cierra en la fecha del reemplazo -- NO se queda en t+3d.
        // Comparado contra filaFinal.VigenteDesde (no contra vigenteDesdeDelReemplazo, tomado
        // de la respuesta del POST): ambos lados de esta igualdad tienen que venir del MISMO
        // round-trip por Postgres (timestamptz trunca a microsegundos, 1 dígito menos que los
        // ticks de .NET) para no comparar valores con distinta precisión.
        Assert.Equal(filaFinal.VigenteDesde, filaInmediataOriginal.VigenteHasta);
        // La pendiente reemplazada sigue con ventana vacía (nunca visible).
        Assert.Equal(filaPendienteReemplazada.VigenteDesde, filaPendienteReemplazada.VigenteHasta);
        Assert.Null(filaFinal.VigenteHasta);

        // Exactamente una fila satisface el predicado "vigente" en cada instante sondeado --
        // el probe crítico es el punto medio entre el reemplazo y t+3d: sin el fix, ahí
        // coincidían DOS filas (la original, todavía abierta hasta t+3d, y la nueva).
        var puntoMedioAntesDelReemplazo = filaInmediataOriginal.VigenteDesde
            + TimeSpan.FromTicks((vigenteDesdeDelReemplazo - filaInmediataOriginal.VigenteDesde).Ticks / 2);
        var puntoMedioSolapamiento = vigenteDesdeDelReemplazo
            + TimeSpan.FromTicks((vigenteEnTresDias - vigenteDesdeDelReemplazo).Ticks / 2);

        foreach (var instante in new[]
                 {
                     puntoMedioAntesDelReemplazo,
                     vigenteDesdeDelReemplazo,
                     puntoMedioSolapamiento,
                     vigenteEnTresDias.AddSeconds(1)
                 })
        {
            var cantidadVigente = historial.Count(h =>
                h.VigenteDesde <= instante && (h.VigenteHasta is null || h.VigenteHasta > instante));
            Assert.True(cantidadVigente == 1, $"instante={instante:O} dio {cantidadVigente} filas vigentes");
        }
    }

    /// <summary>Secuencia (b) del hallazgo CRITICAL: inmediato → programado(t+3d) →
    /// programado(t+10d) con reemplazo, donde el reemplazo cae DESPUÉS de la fecha del
    /// pendiente original. Sin el fix del predecesor, el inmediato original (100) quedaría
    /// cerrado en t+3d mientras la fila nueva recién abre en t+10d — un HUECO sin ningún precio
    /// vigente entre esas dos fechas.</summary>
    [Fact]
    public async Task ReemplazarUnPendienteConUnaFechaPosteriorALaOriginalNoDejaUnHueco()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ReemplazarUnPendienteConUnaFechaPosteriorALaOriginalNoDejaUnHueco));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var inmediato = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, inmediato.StatusCode);

        var vigenteEnTresDias = DateTimeOffset.UtcNow.AddDays(3);
        var programado = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, vigenteEnTresDias));
        Assert.Equal(HttpStatusCode.Created, programado.StatusCode);

        var vigenteEnDiezDias = DateTimeOffset.UtcNow.AddDays(10);
        var reemplazo = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 160m, vigenteEnDiezDias, ConfirmarReemplazo: true));
        Assert.Equal(HttpStatusCode.Created, reemplazo.StatusCode);

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(3, historial!.Count);

        var filaInmediataOriginal = historial.Single(h => h.Precio == 100m);
        var filaPendienteReemplazada = historial.Single(h => h.Precio == 150m);
        var filaFinal = historial.Single(h => h.Precio == 160m);

        // El predecesor (100) se re-cierra en t+10d -- NO se queda en t+3d (eso dejaría hueco).
        // Comparado contra filaFinal.VigenteDesde (mismo round-trip por Postgres que
        // filaInmediataOriginal.VigenteHasta -- timestamptz trunca a microsegundos), no contra
        // vigenteEnDiezDias directo (ticks de .NET, un dígito más de precisión).
        Assert.Equal(filaFinal.VigenteDesde, filaInmediataOriginal.VigenteHasta);
        Assert.Equal(filaPendienteReemplazada.VigenteDesde, filaPendienteReemplazada.VigenteHasta);
        Assert.Null(filaFinal.VigenteHasta);

        // Consulta en el punto medio de la ventana [t+3d, t+10d): sin el fix acá NO había
        // ningún precio vigente (null); con el fix devuelve el precio original.
        var enElMedio = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(vigenteEnTresDias.AddDays(2).ToString("O"))}");
        Assert.Equal(100m, enElMedio!.Precio);

        var enDiezDias = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(vigenteEnDiezDias.AddMinutes(1).ToString("O"))}");
        Assert.Equal(160m, enDiezDias!.Precio);
    }

    // ---- judgment-day ronda 2, item 1: validación simétrica contra el predecesor ------------

    /// <summary>Repro exacto del hallazgo CRITICAL confirmado por los dos jueces: inmediato →
    /// programado(T+20s) → programado(T-1s, dentro de la tolerancia de reloj, con
    /// <c>confirmarReemplazo</c>). La tercera llamada intenta reemplazar la pendiente (150, a
    /// T+20s) con una fecha ANTERIOR al inicio de su PREDECESOR (100, a T) — sin el chequeo
    /// simétrico, <c>BuscarPredecesorAsync</c> + <c>CerrarFilaAsync</c> re-cerraban ese predecesor en T-1s,
    /// produciendo un intervalo INVERTIDO (<c>vigente_hasta &lt; vigente_desde</c>, exactamente
    /// lo que <c>ck_precios_ventana_valida</c> prohíbe a nivel de esquema). Con el fix, la
    /// tercera llamada rechaza con 400 <c>vigente_desde_invalido</c> ANTES de tocar ninguna fila:
    /// el predecesor queda intacto y la pendiente reemplazada sigue abierta.</summary>
    [Fact]
    public async Task ReemplazarUnaPendienteConUnaFechaAnteriorAlPredecesorRechazaCon400SinTocarNada()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ReemplazarUnaPendienteConUnaFechaAnteriorAlPredecesorRechazaCon400SinTocarNada));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var t = DateTimeOffset.UtcNow;

        var inmediato = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, inmediato.StatusCode);

        var vigenteEnVeinteSegundos = t.AddSeconds(20);
        var programado = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, vigenteEnVeinteSegundos));
        Assert.Equal(HttpStatusCode.Created, programado.StatusCode);

        // Un segundo ANTES de T (el "ahora" del alta inmediata) — dentro de la tolerancia de
        // reloj de 30s (no dispara vigente_desde_en_el_pasado) pero anterior al inicio del
        // predecesor real (100, a T).
        var unSegundoAntesDeT = t.AddSeconds(-1);
        var reemplazoInvalido = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 999m, unSegundoAntesDeT, ConfirmarReemplazo: true));

        Assert.Equal(HttpStatusCode.BadRequest, reemplazoInvalido.StatusCode);
        var problema = await reemplazoInvalido.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vigente_desde_invalido", problema.GetProperty("codigo").GetString());

        // Nada se tocó: exactamente 2 filas (100 y 150), el predecesor sigue cerrado en el
        // vigente_desde original de la pendiente, y la pendiente sigue abierta.
        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(2, historial!.Count);

        var filaInmediata = historial.Single(h => h.Precio == 100m);
        var filaPendiente = historial.Single(h => h.Precio == 150m);

        Assert.Equal(filaPendiente.VigenteDesde, filaInmediata.VigenteHasta);
        Assert.Null(filaPendiente.VigenteHasta);
    }

    // ---- judgment-day ronda 3, item 1: predecessor determinístico y libre de filas muertas --

    /// <summary>Repro del hallazgo CRITICAL confirmado en ronda 3: inmediato → programado(D) →
    /// programado(D, MISMA fecha, confirmarReemplazo — el reemplazo ordinario "corregir el
    /// importe manteniendo la fecha") → programado(D2 &gt; D, confirmarReemplazo). El segundo
    /// reemplazo busca el predecesor de la pendiente vigente filtrando por <c>vigente_hasta =
    /// D</c> — y esa fecha la comparten DOS filas: el predecesor REAL (el inmediato original,
    /// cerrado en D) y la fila MUERTA que dejó el reemplazo mismo-fecha anterior
    /// (<c>vigente_desde == vigente_hasta == D</c>). Sin <c>vigente_desde &lt;&gt;
    /// vigente_hasta</c> + <c>ORDER BY vigente_desde ASC LIMIT 1</c> (<see
    /// cref="ServicioDePrecios.BuscarPredecesorAsync"/>), Postgres puede devolver la fila muerta,
    /// y el cierre subsiguiente la REABRE — resucitando un precio que el usuario ya había
    /// reemplazado.</summary>
    [Fact]
    public async Task ReemplazarUnaPendienteMismaFechaYLuegoConFechaDistintaNoResucitaLaFilaMuerta()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ReemplazarUnaPendienteMismaFechaYLuegoConFechaDistintaNoResucitaLaFilaMuerta));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var inmediato = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, inmediato.StatusCode);

        var d = DateTimeOffset.UtcNow.AddDays(3);
        var programado = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 150m, d));
        Assert.Equal(HttpStatusCode.Created, programado.StatusCode);

        // Reemplazo MISMA fecha ("corregir el importe manteniendo la fecha") -- deja una fila
        // muerta (150, vigente_desde == vigente_hasta == d) que comparte vigente_hasta = d con
        // el predecesor real (100).
        var reemplazoMismaFecha = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 160m, d, ConfirmarReemplazo: true));
        Assert.Equal(HttpStatusCode.Created, reemplazoMismaFecha.StatusCode);

        // Segundo reemplazo, con una fecha POSTERIOR -- este es el que busca el predecesor
        // filtrando por vigente_hasta = d, ambiguo entre la fila muerta y el predecesor real.
        var d2 = DateTimeOffset.UtcNow.AddDays(10);
        var reemplazoFechaDistinta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 170m, d2, ConfirmarReemplazo: true));
        Assert.Equal(HttpStatusCode.Created, reemplazoFechaDistinta.StatusCode);

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(4, historial!.Count);

        var filaInmediataOriginal = historial.Single(h => h.Precio == 100m);
        var filaMuertaMismaFecha = historial.Single(h => h.Precio == 150m);
        var filaPendienteReemplazada = historial.Single(h => h.Precio == 160m);
        var filaFinal = historial.Single(h => h.Precio == 170m);

        // El predecesor REAL (100) es el que se extiende a d2 -- NO la fila muerta.
        Assert.Equal(filaFinal.VigenteDesde, filaInmediataOriginal.VigenteHasta);
        // La fila muerta del reemplazo mismo-fecha queda con la ventana vacía intacta -- si el
        // bug estuviera presente, esta fila terminaría resucitada (vigente_hasta ==
        // filaFinal.VigenteDesde) en lugar de vigente_desde == vigente_hasta.
        Assert.Equal(filaMuertaMismaFecha.VigenteDesde, filaMuertaMismaFecha.VigenteHasta);
        // La pendiente reemplazada por el segundo reemplazo también queda con ventana vacía.
        Assert.Equal(filaPendienteReemplazada.VigenteDesde, filaPendienteReemplazada.VigenteHasta);
        Assert.Null(filaFinal.VigenteHasta);

        // Exactamente una fila vigente en cada instante sondeado, y el precio resucitado (150 o
        // 160, ambos reemplazados) nunca es visible -- el probe crítico es el punto medio entre
        // d y d2, donde sin el fix podía no haber ninguna fila vigente (predecesor real sin
        // re-cerrar) o la fila muerta resucitada quedaba visible.
        var puntoMedioAntesDeD = filaInmediataOriginal.VigenteDesde
            + TimeSpan.FromTicks((d - filaInmediataOriginal.VigenteDesde).Ticks / 2);
        var puntoMedioEntreDYD2 = d + TimeSpan.FromTicks((d2 - d).Ticks / 2);

        foreach (var instante in new[] { puntoMedioAntesDeD, d, puntoMedioEntreDYD2, d2.AddSeconds(1) })
        {
            var cantidadVigente = historial.Count(h =>
                h.VigenteDesde <= instante && (h.VigenteHasta is null || h.VigenteHasta > instante));
            Assert.True(cantidadVigente == 1, $"instante={instante:O} dio {cantidadVigente} filas vigentes");

            var vigente = await admin.GetFromJsonAsync<PrecioVigente>(
                $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(instante.ToString("O"))}");
            Assert.NotEqual(150m, vigente!.Precio);
            Assert.NotEqual(160m, vigente.Precio);
        }

        var vigenteHoy = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}");
        Assert.Equal(100m, vigenteHoy!.Precio);
    }

    // ---- task 3.9: point-in-time query -------------------------------------------------------

    /// <summary>Spec: "Query at present date returns the active row" / "Point-in-time query
    /// resolves a past price".</summary>
    [Fact]
    public async Task LaConsultaPorFechaResuelveElPrecioVigenteOUnoHistorico()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaConsultaPorFechaResuelveElPrecioVigenteOUnoHistorico));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var primero = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        var fechaDelPrimerCambio = (await primero.Content.ReadFromJsonAsync<PrecioVigente>())!.Fecha;

        // Un cambio inmediato con vigente_desde == ahora requiere que la segunda alta ocurra
        // estrictamente DESPUÉS: sin esto, dos llamadas al reloj real podrían coincidir en el
        // mismo tick y violar "vigente_desde no puede ser anterior al del precio vigente actual".
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var segundo = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 120m));
        Assert.Equal(HttpStatusCode.Created, segundo.StatusCode);

        var hoy = await admin.GetFromJsonAsync<PrecioVigente>($"/api/articulos/{articulo.Id}/precios/{idListaGeneral}");
        Assert.Equal(120m, hoy!.Precio);

        var enElPasado = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}?fecha={Uri.EscapeDataString(fechaDelPrimerCambio.ToString("O"))}");
        Assert.Equal(100m, enElPasado!.Precio);
    }

    // ---- task 3.10: derivada resolution ------------------------------------------------------

    /// <summary>Spec: "Derived lista price follows the base lista automatically" / "Base price
    /// change propagates without a write" — nunca se persiste una fila de <c>precios</c> para
    /// la lista derivada.</summary>
    [Fact]
    public async Task UnaListaDerivadaResuelveSuPrecioDesdeLaBaseYSiguePropagandoCambios()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnaListaDerivadaResuelveSuPrecioDesdeLaBaseYSiguePropagandoCambios));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var idListaDerivada = await SembrarListaPrecioAsync(
            idTenant, "Mayorista -10%", ModoLista.Derivada, idListaBase: idListaGeneral, porcentaje: -10m);

        var alta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var derivado = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaDerivada}");
        Assert.Equal(90m, derivado!.Precio);

        // El precio base cambia — la derivada se recalcula sin ningún write adicional.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var cambio = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 200m));
        Assert.Equal(HttpStatusCode.Created, cambio.StatusCode);

        var derivadoTrasElCambio = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaDerivada}");
        Assert.Equal(180m, derivadoTrasElCambio!.Precio);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var filasDeLaDerivada = await db.Precios.CountAsync(p => p.IdListaPrecio == idListaDerivada);
        Assert.Equal(0, filasDeLaDerivada);
    }

    /// <summary>Validación: "lista must be fija to store rows (derivada rejected with clear
    /// 400)".</summary>
    [Fact]
    public async Task EstablecerUnPrecioSobreUnaListaDerivadaDevuelve400()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioSobreUnaListaDerivadaDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var idListaDerivada = await SembrarListaPrecioAsync(
            idTenant, "Derivada", ModoLista.Derivada, idListaBase: idListaGeneral, porcentaje: 5m);

        var respuesta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaDerivada, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_no_es_fija", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.11 / judgment-day ronda 1 item 2: serialización real de escrituras --------

    /// <summary>Spec + design: Backstop Map — dos primeros precios concurrentes para el MISMO
    /// par (articulo, lista).
    ///
    /// <b>Reescrito en judgment-day ronda 1 (item 2):</b> antes de este fix, el
    /// <c>SELECT ... FOR UPDATE</c> solo podía lockear una fila YA EXISTENTE, así que el primer
    /// precio de un par competía directo contra <c>ux_precios_vigente</c> en el <c>INSERT</c>
    /// (un 201 + un 409). Ahora <c>AbrirNuevoPrecioAsync</c> toma un
    /// <c>pg_advisory_xact_lock</c> determinístico por par ANTES de leer nada — el segundo
    /// llamador espera el lock y, al retomarlo, ve la fila recién comiteada por el primero, así
    /// que hace un cierre-y-apertura LEGÍTIMO en vez de chocar contra el índice: las dos
    /// escrituras se serializan de verdad y las DOS suceden (2×201), no una gana y la otra
    /// choca. El backstop de esquema sigue existiendo (ver el comentario de
    /// <c>ManejadorDeErrores.ClasificarUnicidad</c> junto a la rama <c>_vigente</c>) pero ya no
    /// es alcanzable por este camino HTTP.
    ///
    /// El rendezvous con <c>InterceptorDeRendezVousListasPrecio</c> se mantiene para forzar que
    /// las dos transacciones arranquen genuinamente solapadas (si no, el pool/JIT ya calientes
    /// dejan que la primera termine antes de que la segunda arranque, y el lock nunca llega a
    /// contenderse de verdad) — mismo mecanismo que <c>ParametrosTests.InterceptorDeRendezVous</c>
    /// (judgment-day, slice 3 ronda 2).</summary>
    [Fact]
    public async Task LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden));

        using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin0, idArea, idAlicuotaIva);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousListasPrecio(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) =>
                    options.AddInterceptors(interceptor))));

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = admin.PostAsJsonAsync($"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        var tareaB = admin.PostAsJsonAsync($"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 105m));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.All(estados, e => Assert.Equal(HttpStatusCode.Created, e));

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(2, historial!.Count);
        Assert.Equal([100m, 105m], historial.Select(h => h.Precio).OrderBy(p => p));

        var abierta = Assert.Single(historial, h => h.VigenteHasta == null);
        var cerrada = historial.Single(h => h.VigenteHasta != null);
        Assert.Equal(abierta.VigenteDesde, cerrada.VigenteHasta);
    }

    /// <summary>NUEVO (judgment-day ronda 1, item 2b): dos cambios inmediatos concurrentes sobre
    /// un par que YA tiene un precio vigente (a diferencia del test anterior, acá el
    /// <c>SELECT ... FOR UPDATE</c> viejo SÍ tenía una fila que lockear — este caso ya estaba
    /// protegido de una colisión de <c>ux_precios_vigente</c> antes del fix). Lo que prueba este
    /// test es la semántica de "esperar y actuar sobre el estado ACTUAL" del advisory lock: las
    /// dos escrituras se serializan, NINGUNA da 409, y el resultado final es una cadena
    /// consistente de 3 filas (la original + las dos nuevas, una cerrando a la otra).</summary>
    [Fact]
    public async Task LaModificacionConcurrenteDeUnPrecioYaExistenteSeSerializaYAmbosSuceden()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaModificacionConcurrenteDeUnPrecioYaExistenteSeSerializaYAmbosSuceden));

        using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin0, idArea, idAlicuotaIva);

        var primero = await admin0.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, primero.StatusCode);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousListasPrecio(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) =>
                    options.AddInterceptors(interceptor))));

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = admin.PostAsJsonAsync($"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 150m));
        var tareaB = admin.PostAsJsonAsync($"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 200m));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.All(estados, e => Assert.Equal(HttpStatusCode.Created, e));

        var historial = await admin.GetFromJsonAsync<List<HistorialDePrecio>>(
            $"/api/articulos/{articulo.Id}/precios/{idListaGeneral}/historial");
        Assert.NotNull(historial);
        Assert.Equal(3, historial!.Count);
        Assert.Equal([100m, 150m, 200m], historial.Select(h => h.Precio).OrderBy(p => p));

        var ordenada = historial.OrderBy(h => h.VigenteDesde).ToList();
        Assert.Null(ordenada[2].VigenteHasta);
        Assert.Equal(ordenada[1].VigenteDesde, ordenada[0].VigenteHasta);
        Assert.Equal(ordenada[2].VigenteDesde, ordenada[1].VigenteHasta);
    }

    /// <summary>Retiene las dos primeras consultas EF a <c>listas_precio</c> (la última
    /// consulta EF-interceptable antes de que <c>AbrirNuevoPrecioAsync</c> abra su transacción
    /// y tome el <c>pg_advisory_xact_lock</c>/lea la fila abierta) hasta que ambas llegaron —
    /// mismo mecanismo que <c>ParametrosTests.InterceptorDeRendezVous</c>. Reusado por los dos
    /// tests de serialización de arriba.</summary>
    private sealed class InterceptorDeRendezVousListasPrecio(CountdownEvent gate) : DbCommandInterceptor
    {
        private int _participantes;

        public int Participantes => _participantes;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            EsperarSiCorresponde(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EsperarSiCorresponde(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void EsperarSiCorresponde(DbCommand command)
        {
            if (!command.CommandText.Contains("listas_precio", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _participantes) > 2)
            {
                return;
            }

            gate.Signal();

            var senializo = gate.Wait(TimeSpan.FromSeconds(10));
            Assert.True(senializo, "El rendezvous de InterceptorDeRendezVousListasPrecio no llegó a los 2 participantes a tiempo.");
        }
    }

    // ---- task 3.12: FK smoke -----------------------------------------------------------------

    /// <summary>Backstop map: <c>fk_precios_articulo</c> — un artículo inexistente (o de otro
    /// tenant, invisible por RLS) da el mismo 404 (ADR-8), atrapado ANTES de llegar a la FK por
    /// el pre-chequeo del servicio (mismo criterio que <c>fk_codigos_barra_articulo</c> en
    /// Slice 2, task 2.11).</summary>
    [Fact]
    public async Task EstablecerUnPrecioSobreUnArticuloInexistenteDevuelve404()
    {
        var (_, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioSobreUnArticuloInexistenteDevuelve404));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/articulos/999999/precios", new AltaPrecio(idListaGeneral, 100m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Backstop map: <c>fk_precios_lista_precio</c> — una lista inexistente da 400
    /// <c>referencia_invalida</c>, atrapado por el pre-chequeo del servicio antes de la FK.</summary>
    [Fact]
    public async Task EstablecerUnPrecioConUnaListaInexistenteDevuelve400()
    {
        var (_, idArea, idAlicuotaIva, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioConUnaListaInexistenteDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var respuesta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(999999, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Cross-tenant: el filtro de EF (+ RLS por debajo) deja invisible una lista de
    /// OTRO tenant — mismo 400 que "no existe en absoluto".</summary>
    [Fact]
    public async Task EstablecerUnPrecioConUnaListaDeOtroTenantDevuelve400()
    {
        var (_, idAreaA, idAlicuotaIvaA, _, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioConUnaListaDeOtroTenantDevuelve400) + "-A");
        var (_, _, _, idListaGeneralB, _, _) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioConUnaListaDeOtroTenantDevuelve400) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var articuloA = await CrearArticuloAsync(adminA, idAreaA, idAlicuotaIvaA);

        var respuesta = await adminA.PostAsJsonAsync(
            $"/api/articulos/{articuloA.Id}/precios", new AltaPrecio(idListaGeneralB, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day ronda 1, item 5: cobertura y consistencia ------------------------------

    /// <summary>(item 5a) Cross-tenant con un id de artículo REAL de OTRO tenant (no un id
    /// inexistente) en las TRES rutas GET de precios — el filtro de EF (+ RLS por debajo) lo
    /// deja invisible, mismo 404 que "no existe en absoluto" (ADR-8).</summary>
    [Fact]
    public async Task ConsultarPreciosDeUnArticuloRealDeOtroTenantDevuelve404EnLasTresRutasDeGet()
    {
        var (_, _, _, _, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(ConsultarPreciosDeUnArticuloRealDeOtroTenantDevuelve404EnLasTresRutasDeGet) + "-A");
        var (_, idAreaB, idAlicuotaIvaB, idListaGeneralB, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(ConsultarPreciosDeUnArticuloRealDeOtroTenantDevuelve404EnLasTresRutasDeGet) + "-B");

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var articuloB = await CrearArticuloAsync(adminB, idAreaB, idAlicuotaIvaB);
        var alta = await adminB.PostAsJsonAsync(
            $"/api/articulos/{articuloB.Id}/precios", new AltaPrecio(idListaGeneralB, 100m));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);

        var todasLasListas = await adminA.GetAsync($"/api/articulos/{articuloB.Id}/precios");
        Assert.Equal(HttpStatusCode.NotFound, todasLasListas.StatusCode);

        var unaLista = await adminA.GetAsync($"/api/articulos/{articuloB.Id}/precios/{idListaGeneralB}");
        Assert.Equal(HttpStatusCode.NotFound, unaLista.StatusCode);

        var historial = await adminA.GetAsync($"/api/articulos/{articuloB.Id}/precios/{idListaGeneralB}/historial");
        Assert.Equal(HttpStatusCode.NotFound, historial.StatusCode);
    }

    /// <summary>(item 5b) Prueba explícita de la divergencia documentada en
    /// <c>ServicioDePrecios.PrecioVigenteAsync</c>/<c>PreciosVigentesAsync</c>: una lista
    /// desactivada SIGUE resolviendo por id explícito, pero desaparece del listado de "todas
    /// las listas activas".</summary>
    [Fact]
    public async Task UnaListaInactivaResuelvePorIdExplicitoPeroNoApareceEnElListadoDeTodas()
    {
        var (idTenant, idArea, idAlicuotaIva, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnaListaInactivaResuelvePorIdExplicitoPeroNoApareceEnElListadoDeTodas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var idListaInactiva = await SembrarListaPrecioAsync(idTenant, "Lista inactiva");
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var lista = await db.ListasPrecio.SingleAsync(l => l.Id == idListaInactiva);
            lista.Activo = false;
            await db.SaveChangesAsync();
        }

        var alta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaInactiva, 55m));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var porId = await admin.GetFromJsonAsync<PrecioVigente>(
            $"/api/articulos/{articulo.Id}/precios/{idListaInactiva}");
        Assert.Equal(55m, porId!.Precio);

        var todas = await admin.GetFromJsonAsync<List<PrecioVigente>>(
            $"/api/articulos/{articulo.Id}/precios");
        Assert.NotNull(todas);
        Assert.DoesNotContain(todas!, p => p.IdListaPrecio == idListaInactiva);
    }

    // ---- Validaciones adicionales de scope (bounds, tolerancia de reloj, autorización) -------

    /// <summary>Columna <c>numeric(14,2)</c> — mismo criterio que
    /// <c>ServicioDeArticulos.ExigirCostoValido</c>.</summary>
    [Fact]
    public async Task EstablecerUnPrecioNegativoDevuelve400()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EstablecerUnPrecioNegativoDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var respuesta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, -1m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_invalido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: "A future price (vigente_desde after now) MAY be scheduled" — programar
    /// con una fecha claramente en el pasado (más allá de la tolerancia de desfasaje de reloj)
    /// se rechaza.</summary>
    [Fact]
    public async Task ProgramarUnPrecioConVigenteDesdeEnElPasadoDevuelve400()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ProgramarUnPrecioConVigenteDesdeEnElPasadoDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var respuesta = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios/programados",
            new ProgramarPrecio(idListaGeneral, 100m, DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vigente_desde_en_el_pasado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Lista ABM/precios gated by <c>GestionDeCatalogo</c> (tenant admin only) —
    /// mismo criterio que el resto de <c>ArticulosEndpoints</c>.</summary>
    [Fact]
    public async Task UnVendedorNoPuedeEstablecerUnPrecio()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeEstablecerUnPrecio));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeEstablecerUnPrecio));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- judgment-day ronda 2, item 2: ck_precios_ventana_valida (GATE-APROBADO 2026-08-03) --

    /// <summary>db-error-backstops: prueba de humo cruda para <c>ck_precios_ventana_valida</c> —
    /// un INSERT directo por SQL con <c>vigente_hasta</c> ANTERIOR a <c>vigente_desde</c>
    /// bypasea por completo <c>ServicioDePrecios</c> (el chequeo simétrico del item 1 de esta
    /// misma ronda) y fuerza a Postgres a rechazar el intervalo invertido.</summary>
    [Fact]
    public async Task UnPrecioConVigenteHastaAnteriorAVigenteDesdeViolaLaCheckConstraint()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnPrecioConVigenteHastaAnteriorAVigenteDesdeViolaLaCheckConstraint));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var articulo = await CrearArticuloAsync(admin, idArea, idAlicuotaIva);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO precios (id_tenant, id_articulo, id_lista_precio, precio, vigente_desde, vigente_hasta, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 100, now(), now() - interval '1 day', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = articulo.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = idListaGeneral });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_precios_ventana_valida", excepcion.ConstraintName);
    }
}
