using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
    /// entre las dos fechas programadas).</summary>
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

    // ---- task 3.11: ux_precios_vigente race (db-error-backstops) ----------------------------

    /// <summary>Spec + design: Backstop Map — dos primeros precios concurrentes para el MISMO
    /// par (articulo, lista): no hay ninguna fila que <c>SELECT ... FOR UPDATE</c> pueda
    /// lockear todavía, así que las dos transacciones compiten recién en el <c>INSERT</c>.
    ///
    /// <b>Hallazgo honesto (db-error-backstops: "determine honestly whether the race is
    /// genuinely reachable"):</b> un <c>Task.WhenAll</c> desnudo sobre 2 <c>POST</c> NO alcanza
    /// la carrera de forma confiable — probado empíricamente (5/5 corridas aisladas exponen el
    /// 409, pero 3/3 corridas dentro de la clase completa dan 2×201 sin excepción). Con el pool
    /// de conexiones/JIT ya "caliente" (el caso real de <c>dotnet test</c>, nunca un test
    /// aislado), el segundo request tiende a completar su <c>BEGIN + SELECT ... FOR UPDATE</c>
    /// DESPUÉS de que el primero ya hizo <c>COMMIT</c> — en ese caso el segundo SÍ ve la fila
    /// recién confirmada, y hace un cierre-y-apertura legítimo (200 lógico, sin 23505) en lugar
    /// de chocar. Mismo mecanismo, mismo hallazgo de fondo que
    /// <c>ParametrosTests.DosEstablecimientosConcurrentesConLaMismaClaveYElMismoAlcanceDisparanElBackstopDelSaveChanges</c>
    /// (judgment-day, slice 3 ronda 2) — así que se resuelve con el mismo <c>InterceptorDeRendezVous</c>:
    /// retiene las dos primeras consultas a <c>listas_precio</c> (la última consulta EF antes de
    /// abrir la transacción — el <c>SELECT ... FOR UPDATE</c> en sí es ADO.NET crudo, fuera del
    /// pipeline de comandos de EF Core, así que NO es interceptable directamente) hasta que
    /// ambas llegaron, forzando que las dos entren a la transacción al mismo tiempo.</summary>
    [Fact]
    public async Task LaCreacionConcurrenteDeDosPrimerosPreciosDaExactamenteUnGanador()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteDeDosPrimerosPreciosDaExactamenteUnGanador));

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
        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precio_vigente_duplicado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Retiene las dos primeras consultas EF a <c>listas_precio</c> (la última
    /// consulta EF-interceptable antes de que <c>AbrirNuevoPrecioAsync</c> abra su transacción
    /// y haga el <c>SELECT ... FOR UPDATE</c> crudo) hasta que ambas llegaron — mismo mecanismo
    /// que <c>ParametrosTests.InterceptorDeRendezVous</c>.</summary>
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
}
