using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Articulos;
using Ways.Application.Catalogos;
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
/// Slice 4 (stage-3-articulos-y-precios, tasks 4.5-4.8, db-error-backstops skill):
/// <c>ServicioDeListasPrecio</c>/las rutas <c>/api/catalogos/listas-precio*</c> punta a punta
/// contra Postgres real — profundidad 1, bloqueo de cambio de modo con historial, bloqueo de
/// desactivación mientras una derivada activa depende de la lista, y la carrera GENUINA del
/// intercambio de <c>es_default</c> (dos listas distintas compitiendo por el mismo alcance).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ListasPrecioEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    // Mismo motivo que ArticulosEndpointsTests/PreciosEndpointsTests: el server registra
    // JsonStringEnumConverter (Program.cs) pero ReadFromJsonAsync<T>() sin opciones usa las
    // opciones DEFAULT del lado cliente, que no lo traen.
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

        var idListaGeneral = await db.ListasPrecio
            .Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault).Select(l => l.Id).SingleAsync();

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

    private static ListaPrecioAlta AltaFijaValida(string nombre, bool esDefault = false, bool activo = true) =>
        new(nombre, IdEmpresa: null, EsDefault: esDefault, Modo: ModoLista.Fija, IdListaBase: null, Porcentaje: null, Activo: activo);

    private static ListaPrecioAlta AltaDerivadaValida(string nombre, int idListaBase, decimal porcentaje = -10m) =>
        new(nombre, IdEmpresa: null, EsDefault: false, Modo: ModoLista.Derivada, IdListaBase: idListaBase, Porcentaje: porcentaje);

    private static async Task<ListaPrecioListado> CrearListaAsync(HttpClient cliente, ListaPrecioAlta datos)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/catalogos/listas-precio", datos);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ListaPrecioListado>(OpcionesJson))!;
    }

    // ---- task 4.5: admin crea fija y derivada; base inexistente -> 400 -----------------------

    [Fact]
    public async Task AdminCreaUnaListaFijaYUnaDerivadaBasadaEnElla()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(AdminCreaUnaListaFijaYUnaDerivadaBasadaEnElla));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var fija = await CrearListaAsync(admin, AltaFijaValida("Mayorista"));
        Assert.Equal(ModoLista.Fija, fija.Modo);

        var derivada = await CrearListaAsync(admin, AltaDerivadaValida("Mayorista -10%", fija.Id));
        Assert.Equal(ModoLista.Derivada, derivada.Modo);
        Assert.Equal(fija.Id, derivada.IdListaBase);
        Assert.Equal(-10m, derivada.Porcentaje);
    }

    [Fact]
    public async Task CrearConIdListaBaseInexistenteDevuelve400ReferenciaInvalida()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConIdListaBaseInexistenteDevuelve400ReferenciaInvalida));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/catalogos/listas-precio", AltaDerivadaValida("Derivada", idListaBase: 999_999));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task VendedorBloqueadoDeCrearYEditar()
    {
        var (idTenant, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(VendedorBloqueadoDeCrearYEditar));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(VendedorBloqueadoDeCrearYEditar));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var creacion = await vendedor.PostAsJsonAsync("/api/catalogos/listas-precio", AltaFijaValida("Mayorista"));
        Assert.Equal(HttpStatusCode.Forbidden, creacion.StatusCode);

        var edicion = await vendedor.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{idListaGeneral}", AltaFijaValida("General", esDefault: true));
        Assert.Equal(HttpStatusCode.Forbidden, edicion.StatusCode);
    }

    // ---- task 4.6: mode switch blocked once history exists ------------------------------------

    [Fact]
    public async Task CambiarElModoDeUnaListaConPreciosEsRechazado()
    {
        var (_, idArea, idAlicuotaIva, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CambiarElModoDeUnaListaConPreciosEsRechazado));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var otraBase = await CrearListaAsync(admin, AltaFijaValida("Otra base"));

        var altaArticulo = new AltaArticulo(
            CodigoInterno: null, Nombre: "Artículo de prueba", Descripcion: null, IdArea: idArea,
            IdCategoria: null, IdMarca: null, IdGrupo: null, IdProveedorHabitual: null,
            IdAlicuotaIva: idAlicuotaIva, UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null,
            EsProducto: true, CostoLista: null, DescuentoProveedor: null, CostoNominal: null);
        var creacionArticulo = await admin.PostAsJsonAsync("/api/articulos", altaArticulo);
        Assert.Equal(HttpStatusCode.Created, creacionArticulo.StatusCode);
        var articulo = (await creacionArticulo.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        var altaPrecio = await admin.PostAsJsonAsync(
            $"/api/articulos/{articulo.Id}/precios", new AltaPrecio(idListaGeneral, 100m));
        Assert.Equal(HttpStatusCode.Created, altaPrecio.StatusCode);

        var edicion = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{idListaGeneral}",
            AltaDerivadaValida("General", otraBase.Id) with { EsDefault = true });

        Assert.Equal(HttpStatusCode.Conflict, edicion.StatusCode);
        var problema = await edicion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_modo_bloqueado_por_historial", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CambiarElModoDeUnaListaSinPreciosEsPermitido()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CambiarElModoDeUnaListaSinPreciosEsPermitido));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var base_ = await CrearListaAsync(admin, AltaFijaValida("Base"));
        var sinHistorial = await CrearListaAsync(admin, AltaFijaValida("Sin historial"));

        var edicion = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{sinHistorial.Id}", AltaDerivadaValida("Sin historial", base_.Id));

        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var actualizada = await edicion.Content.ReadFromJsonAsync<ListaPrecioListado>(OpcionesJson);
        Assert.Equal(ModoLista.Derivada, actualizada!.Modo);
    }

    // ---- task 4.7: deactivation blocked while referenced as base ------------------------------

    [Fact]
    public async Task DesactivarUnaListaReferenciadaComoBaseEsRechazado()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(DesactivarUnaListaReferenciadaComoBaseEsRechazado));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var base_ = await CrearListaAsync(admin, AltaFijaValida("Base"));
        await CrearListaAsync(admin, AltaDerivadaValida("Derivada", base_.Id));

        var desactivacion = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{base_.Id}", AltaFijaValida("Base", activo: false));

        Assert.Equal(HttpStatusCode.Conflict, desactivacion.StatusCode);
        var problema = await desactivacion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_referenciada_como_base", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task DesactivarUnaListaSinDependientesActivosEsPermitido()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(DesactivarUnaListaSinDependientesActivosEsPermitido));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var base_ = await CrearListaAsync(admin, AltaFijaValida("Base"));
        var derivada = await CrearListaAsync(admin, AltaDerivadaValida("Derivada", base_.Id));

        var desactivarDerivada = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{derivada.Id}", AltaDerivadaValida("Derivada", base_.Id) with { Activo = false });
        Assert.Equal(HttpStatusCode.OK, desactivarDerivada.StatusCode);

        var desactivarBase = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{base_.Id}", AltaFijaValida("Base", activo: false));
        Assert.Equal(HttpStatusCode.OK, desactivarBase.StatusCode);
    }

    // ---- es_default: swap explícito y su carrera -----------------------------------------------

    /// <summary>Verifica el intercambio en el caso simple, no concurrente: asignar
    /// <c>EsDefault: true</c> a una lista nueva desmarca automáticamente General en la MISMA
    /// operación — nunca hay un instante consultable con cero o dos defaults en el mismo
    /// alcance.</summary>
    [Fact]
    public async Task AsignarEsDefaultAUnaListaNuevaDesmarcaLaAnterior()
    {
        var (_, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(AsignarEsDefaultAUnaListaNuevaDesmarcaLaAnterior));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var nueva = await CrearListaAsync(admin, AltaFijaValida("Nueva default"));

        var edicion = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{nueva.Id}", AltaFijaValida("Nueva default", esDefault: true));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);

        var general = await admin.GetFromJsonAsync<ListaPrecioListado>(
            $"/api/catalogos/listas-precio/{idListaGeneral}", OpcionesJson);
        Assert.False(general!.EsDefault);

        var listado = await admin.GetFromJsonAsync<List<ListaPrecioListado>>(
            "/api/catalogos/listas-precio", OpcionesJson);
        Assert.Single(listado!.Where(l => l.IdEmpresa is null), l => l.EsDefault);
    }

    [Fact]
    public async Task QuitarEsDefaultSinAsignarloAOtraListaEsRechazado()
    {
        var (_, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(QuitarEsDefaultSinAsignarloAOtraListaEsRechazado));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{idListaGeneral}", AltaFijaValida("General", esDefault: false));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_default_requiere_reemplazo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task EliminarLaListaDefaultEsRechazado()
    {
        var (_, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EliminarLaListaDefaultEsRechazado));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var baja = await admin.DeleteAsync($"/api/catalogos/listas-precio/{idListaGeneral}");

        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        var problema = await baja.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lista_default_no_se_puede_eliminar", problema.GetProperty("codigo").GetString());
    }

    /// <summary>db-error-backstops: la carrera GENUINA del intercambio de <c>es_default</c> —
    /// dos listas DISTINTAS del mismo alcance compitiendo por convertirse en la nueva default al
    /// mismo tiempo. El intercambio desmarca primero la fila compartida (General) — esa fila
    /// serializa a los dos escritores (mismo lock de fila que cualquier UPDATE concurrente); el
    /// que reanuda segundo ve, en su propia transacción, que la OTRA lista ya quedó default
    /// (comiteada por el primero) y su propio intento de marcarse default choca contra
    /// <c>ux_listas_precio_default_compartido</c> con un 23505 genuino — <c>ManejadorDeErrores</c>
    /// lo traduce a 409 <c>default_duplicado</c>. Exactamente una de las dos listas queda
    /// default al final, nunca cero ni dos.
    ///
    /// El rendezvous con <c>InterceptorDeRendezVousListasPrecio</c> fuerza que las dos
    /// transacciones arranquen genuinamente solapadas — mismo mecanismo que
    /// <c>PreciosEndpointsTests</c>.</summary>
    [Fact]
    public async Task LaAsignacionConcurrenteDeEsDefaultAOtrasDosListasDaExactamenteUnGanador()
    {
        var (_, _, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaAsignacionConcurrenteDeEsDefaultAOtrasDosListasDaExactamenteUnGanador));

        using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var listaB = await CrearListaAsync(admin0, AltaFijaValida("Candidata B"));
        var listaC = await CrearListaAsync(admin0, AltaFijaValida("Candidata C"));

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousListasPrecio(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) =>
                    options.AddInterceptors(interceptor))));

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaB = admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{listaB.Id}", AltaFijaValida("Candidata B", esDefault: true));
        var tareaC = admin.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{listaC.Id}", AltaFijaValida("Candidata C", esDefault: true));

        var respuestas = await Task.WhenAll(tareaB, tareaC);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.Contains(HttpStatusCode.OK, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedora.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("default_duplicado", problema.GetProperty("codigo").GetString());

        var listado = await admin.GetFromJsonAsync<List<ListaPrecioListado>>(
            "/api/catalogos/listas-precio", OpcionesJson);
        Assert.Single(listado!.Where(l => l.IdEmpresa is null), l => l.EsDefault);
    }

    /// <summary>Retiene la primera consulta EF a <c>listas_precio</c> de cada request (la que
    /// <c>ActualizarAsync</c> hace al principio, vía <c>BuscarAsync</c>) hasta que ambas
    /// llegaron — mismo mecanismo que <c>PreciosEndpointsTests.InterceptorDeRendezVousListasPrecio</c>
    /// (duplicado a propósito: cada archivo de test es dueño de su propia copia, mismo criterio
    /// que <c>ParametrosTests.InterceptorDeRendezVous</c>).</summary>
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

    // ---- ADR-8: 404 uniforme cross-tenant, lectura y escritura --------------------------------

    [Fact]
    public async Task UnaListaDeOtroTenantDevuelve404EnLecturaYEscritura()
    {
        var (_, _, _, _, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnaListaDeOtroTenantDevuelve404EnLecturaYEscritura) + "A");
        var (_, _, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnaListaDeOtroTenantDevuelve404EnLecturaYEscritura) + "B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var listaDeA = await CrearListaAsync(adminA, AltaFijaValida("Lista de A"));

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);

        var lectura = await adminB.GetAsync($"/api/catalogos/listas-precio/{listaDeA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, lectura.StatusCode);

        var edicion = await adminB.PutAsJsonAsync(
            $"/api/catalogos/listas-precio/{listaDeA.Id}", AltaFijaValida("Lista de A"));
        Assert.Equal(HttpStatusCode.NotFound, edicion.StatusCode);

        var baja = await adminB.DeleteAsync($"/api/catalogos/listas-precio/{listaDeA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, baja.StatusCode);
    }

    // ---- task 4.8: regression ------------------------------------------------------------------

    [Fact]
    public async Task LaRutaDeSoloLecturaDeStage2SigueFuncionandoSinColisionDeRutas()
    {
        var (_, _, _, idListaGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaRutaDeSoloLecturaDeStage2SigueFuncionandoSinColisionDeRutas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var listado = await admin.GetFromJsonAsync<List<JsonElement>>("/api/listas-precio");
        Assert.NotNull(listado);
        Assert.Contains(listado!, l => l.GetProperty("id").GetInt32() == idListaGeneral);
    }
}
