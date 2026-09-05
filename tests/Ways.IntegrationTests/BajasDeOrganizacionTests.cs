using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Xunit.Abstractions;

namespace Ways.IntegrationTests;

/// <summary>
/// Etapa 20, slice 4: las bajas lógicas de organización de punta a punta, contra Postgres real y
/// sobre la conexión <c>ways_app</c> (NOSUPERUSER/NOBYPASSRLS) — la única bajo la cual una
/// afirmación sobre RLS prueba algo.
///
/// Por qué acá y no en la suite de Application: el guard emite SQL crudo y las bajas abren
/// transacción, y el proveedor InMemory no soporta ninguna de las dos cosas (mismo
/// "transaction-blocked-provider caveat" que documenta <c>ServicioDeOfertasTests</c>).
///
/// OD5, y hay que decirlo acá arriba porque explica la forma de medio archivo: Ways NO tiene hoy
/// ningún endpoint que cree una SEGUNDA empresa o un SEGUNDO punto de venta. Por lo tanto, a
/// través de las rutas, el mínimo estructural dispara SIEMPRE antes que el guard de uso, y
/// <c>empresa_en_uso</c>/<c>punto_venta_en_uso</c> son alcanzables únicamente POR DEBAJO de la
/// API, con el hermano sembrado a mano. Escribir una prueba de API para esos dos códigos la haría
/// pasar por el motivo equivocado (<c>mutation-proof-tests</c> regla 3), así que no se escribe.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BajasDeOrganizacionTests(WaysApiFixture fixture, ITestOutputHelper salida)
    : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Un tenant aprovisionado por el camino REAL. <paramref name="Ancla"/> es el
    /// instante único que <c>ServicioDeAprovisionamiento</c> leyó una sola vez y estampó en todo
    /// lo que creó: es lo que hace válida la línea base "prístino".</summary>
    private sealed record Sembrado(
        int IdTenant,
        int IdEmpresa,
        int IdPuntoVenta,
        int IdAdmin,
        string MailAdmin,
        string PasswordAdmin,
        DateTimeOffset Ancla);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(RolConocido rol, int usuarioId, int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol { get; } = rol;
        public int? IdTenant { get; } = idTenant;
    }

    // ---- infraestructura de la prueba ---------------------------------------------------------

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<HttpClient> ClienteComoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Aprovisiona por el endpoint real de plataforma: la línea base prístina tiene que
    /// ser la que produce el aprovisionamiento, no una que la prueba se invente.</summary>
    private async Task<Sembrado> AprovisionarAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}";

        using var cliente = await ClienteComoRootAsync();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SRL", $"{unico} - Local 1", $"{unico}@ways.test"));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenant = await db.Tenants.SingleAsync(t => t.Id == resultado!.IdTenant);

        return new Sembrado(
            resultado!.IdTenant,
            resultado.IdEmpresa,
            resultado.IdPuntoVenta,
            resultado.IdUsuarioAdmin,
            $"{unico}@ways.test",
            resultado.PasswordTemporal,
            tenant.CreatedAt);
    }

    private WaysDbContext ContextoDePlataforma() =>
        fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

    /// <summary>Ejecuta una acción del servicio POR DEBAJO de la API (OD5). El reloj se puede fijar
    /// para afirmar el instante exacto de la cascada; si no se fija, se usa el real.</summary>
    private async Task ConServicioAsync(
        int? idTenant,
        Func<ServicioDeOrganizacion, Task> accion,
        DateTimeOffset? instanteDeBaja = null,
        int idActor = 1)
    {
        var tenantActual = idTenant is int id
            ? new TenantActualFijo(ModoDeAcceso.Tenant, id)
            : TenantActualFijo.Plataforma;

        await using var db = fixture.CrearContextoDeAplicacion(tenantActual);

        var contexto = idTenant is int t
            ? new ContextoFijo(RolConocido.Admin, idActor, idTenant: t)
            : new ContextoFijo(RolConocido.Root, idActor, idTenant: null);

        var reloj = new RelojFijo(instanteDeBaja ?? DateTimeOffset.UtcNow);

        await accion(new ServicioDeOrganizacion(
            db, reloj, contexto, new InspectorDeUso(db), new ServicioDeAuditoria(db, reloj, contexto)));
    }

    private static async Task<ErrorDominio> ErrorDeAsync(Func<Task> accion) =>
        await Assert.ThrowsAsync<ErrorDominio>(accion);

    private static async Task<(string Codigo, string Mensaje)> LeerConflictoAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        // ManejadorDeErrores rinde el mensaje de ErrorDominio en `title` y el código en la
        // extensión `codigo` (ProblemDetails).
        return (cuerpo.GetProperty("codigo").GetString()!, cuerpo.GetProperty("title").GetString()!);
    }

    // ---- siembra a mano (lo que ningún endpoint crea hoy) --------------------------------------

    private async Task<Empresa> SembrarEmpresaAsync(int idTenant, string razonSocial, DateTimeOffset instante)
    {
        await using var db = ContextoDePlataforma();

        var empresa = new Empresa
        {
            IdTenant = idTenant,
            RazonSocial = razonSocial,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa;
    }

    private async Task<PuntoVenta> SembrarPuntoVentaAsync(
        int idTenant, int idEmpresa, string nombre, DateTimeOffset instante)
    {
        await using var db = ContextoDePlataforma();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta;
    }

    private async Task<Categoria> SembrarCategoriaAsync(
        int idTenant, int? idEmpresa, string nombre, DateTimeOffset instante)
    {
        await using var db = ContextoDePlataforma();

        var categoria = new Categoria
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            Orden = 1,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        return categoria;
    }

    private async Task<Articulo> SembrarArticuloAsync(
        int idTenant, DateTimeOffset instante, int? idEmpresaHabilitada = null)
    {
        await using var db = ContextoDePlataforma();

        var idArea = await db.Areas.Where(a => a.IdTenant == idTenant).Select(a => a.Id).FirstAsync();
        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = Guid.NewGuid().ToString("N")[..12],
            Nombre = "Artículo del cliente",
            IdArea = idArea,
            IdAlicuotaIva = idAlicuota,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            DisponibleParaTodas = idEmpresaHabilitada is null,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (idEmpresaHabilitada is int idEmpresa)
        {
            db.ArticulosEmpresas.Add(new ArticuloEmpresa
            {
                IdArticulo = articulo.Id,
                IdEmpresa = idEmpresa,
                IdTenant = idTenant
            });
            await db.SaveChangesAsync();
        }

        return articulo;
    }

    /// <summary><paramref name="idTenant"/> en <c>null</c> siembra una cuenta de PLATAFORMA: no
    /// hay endpoint que las cree y es la única forma de tener un objetivo Root para probar el
    /// orden entre <c>PoliticaDeRoles</c> y el guard.</summary>
    private async Task<Usuario> SembrarUsuarioAsync(
        int? idTenant, RolConocido rol, string password, DateTimeOffset instante)
    {
        await using var db = ContextoDePlataforma();

        var hasheador = new Ways.Infrastructure.Seguridad.HasheadorPbkdf2();
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var usuario = new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = $"{rol}-{sufijo}".ToLowerInvariant(),
            Mail = $"{rol}-{sufijo}@ways.test".ToLowerInvariant(),
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = instante,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return usuario;
    }

    private async Task<TurnoCaja> SembrarTurnoAsync(
        int idTenant, int idPuntoVenta, int idEmpleadoApertura, DateTimeOffset instante,
        int? idEmpleadoCierre = null)
    {
        await using var db = ContextoDePlataforma();

        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            IdEmpleadoCierre = idEmpleadoCierre,
            FechaApertura = instante,
            FechaCierre = idEmpleadoCierre is null ? null : instante,
            FondoInicial = 0m,
            Estado = idEmpleadoCierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return turno;
    }

    private async Task DarDeBajaAManoAsync<T>(Func<WaysDbContext, Task<T>> buscar, DateTimeOffset instante)
        where T : EntidadBase
    {
        await using var db = ContextoDePlataforma();

        var fila = await buscar(db);
        fila.DeletedAt = instante;
        fila.UpdatedAt = instante;
        await db.SaveChangesAsync();
    }

    // ---- lecturas con los filtros apagados ----------------------------------------------------

    private async Task<Tenant> LeerTenantAsync(int id)
    {
        await using var db = ContextoDePlataforma();
        return await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == id);
    }

    private async Task<Empresa> LeerEmpresaAsync(int id)
    {
        await using var db = ContextoDePlataforma();
        return await db.Empresas.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
    }

    private async Task<PuntoVenta> LeerPuntoVentaAsync(int id)
    {
        await using var db = ContextoDePlataforma();
        return await db.PuntosVenta.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
    }

    private async Task<Usuario> LeerUsuarioAsync(int id)
    {
        await using var db = ContextoDePlataforma();
        return await db.Usuarios.IgnoreQueryFilters().SingleAsync(u => u.Id == id);
    }

    // =========================================================================================
    // N4 y la cascada
    // =========================================================================================

    /// <summary>
    /// N4 — LA RED QUE NO SE DEGRADA (task 4.11, BO-R2). Un tenant recién aprovisionado, con su
    /// empresa, su punto de venta, su admin y toda la plantilla, es PRÍSTINO y se da de baja.
    ///
    /// Es la única red que puede ver a la línea base del aprovisionamiento moviéndose: se pone en
    /// rojo el día que <c>ServicioDeAprovisionamiento</c> deje de leer el reloj una sola vez (una
    /// fila quedaría estrictamente posterior al ancla y bloquearía), o el día que el
    /// aprovisionamiento cree una fila sin marca temporal fuera de los dos carve-outs.
    ///
    /// Y de paso B1/BO-R1: las filas SIGUEN en la base después de la baja, con
    /// <c>deleted_at</c> escrito. Nada se borra físicamente.
    /// </summary>
    [Fact]
    public async Task UnTenantReciennAprovisionadoSeDaDeBajaYSusFilasSiguenEnLaBase()
    {
        var sembrado = await AprovisionarAsync("n4");

        using var root = await ClienteComoRootAsync();
        var respuesta = await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        var tenant = await LeerTenantAsync(sembrado.IdTenant);
        Assert.NotNull(tenant.DeletedAt);
        Assert.Equal(EstadoTenant.Baja, tenant.Estado);

        Assert.NotNull((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.NotNull((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
        Assert.NotNull((await LeerUsuarioAsync(sembrado.IdAdmin)).DeletedAt);

        var listado = await root.GetFromJsonAsync<List<TenantListado>>(
            "/api/plataforma/tenants", OpcionesJson);
        Assert.NotNull(listado);
        Assert.DoesNotContain(listado!, t => t.Id == sembrado.IdTenant);
    }

    /// <summary>
    /// Cláusula (task 4.16, BO-R9, TO-R5): el padre y los tres hijos comparten UN instante, y se
    /// afirma por IGUALDAD contra un reloj fijo, no por "no es null". Con <c>reloj.Ahora</c> leído
    /// una vez por fila, las cuatro serían no-nulas y distintas, y el restore por instante
    /// (<c>UPDATE ... SET deleted_at = NULL WHERE deleted_at = '&lt;instante&gt;'</c>) dejaría filas
    /// afuera. También se afirma <c>updated_at</c>: la cascada estampa las dos columnas.
    /// </summary>
    [Fact]
    public async Task LaCascadaDeUnTenantEstampaElMismoInstanteEnLosCuatroYDejaElEstadoEnBaja()
    {
        var sembrado = await AprovisionarAsync("cascada-instante");
        var momento = new DateTimeOffset(2026, 9, 5, 15, 30, 0, TimeSpan.Zero);

        await ConServicioAsync(null, s => s.EliminarTenantAsync(sembrado.IdTenant), momento);

        var tenant = await LeerTenantAsync(sembrado.IdTenant);
        var empresa = await LeerEmpresaAsync(sembrado.IdEmpresa);
        var puntoVenta = await LeerPuntoVentaAsync(sembrado.IdPuntoVenta);
        var admin = await LeerUsuarioAsync(sembrado.IdAdmin);

        Assert.Equal(momento, tenant.DeletedAt);
        Assert.Equal(momento, empresa.DeletedAt);
        Assert.Equal(momento, puntoVenta.DeletedAt);
        Assert.Equal(momento, admin.DeletedAt);

        Assert.Equal(momento, tenant.UpdatedAt);
        Assert.Equal(momento, empresa.UpdatedAt);
        Assert.Equal(momento, puntoVenta.UpdatedAt);
        Assert.Equal(momento, admin.UpdatedAt);

        Assert.Equal(EstadoTenant.Baja, tenant.Estado);
    }

    /// <summary>
    /// Cláusula (task 4.17, BO-R9): la cascada está ACOTADA a la proyección de organización. El
    /// resto de la plantilla —áreas, medios de pago, listas de precio, el cliente Consumidor Final
    /// y el contador de numeración de clientes— queda intacto, porque
    /// <c>EstadoTenant.Baja</c> promete que los datos siguen disponibles para exportar.
    ///
    /// Y del otro lado: los tres listados de raíz no devuelven NINGUNA de las filas arrastradas
    /// para un actor de plataforma, así que no queda huérfano visible apuntando a un tenant que ya
    /// no resuelve.
    /// </summary>
    [Fact]
    public async Task LaCascadaNoTocaElRestoDeLaPlantillaNiDejaHuerfanosVisibles()
    {
        var sembrado = await AprovisionarAsync("cascada-borde");

        using var root = await ClienteComoRootAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}")).StatusCode);

        await using (var db = ContextoDePlataforma())
        {
            Assert.All(
                await db.Areas.IgnoreQueryFilters().Where(a => a.IdTenant == sembrado.IdTenant).ToListAsync(),
                a => Assert.Null(a.DeletedAt));
            Assert.All(
                await db.MediosPago.IgnoreQueryFilters().Where(m => m.IdTenant == sembrado.IdTenant).ToListAsync(),
                m => Assert.Null(m.DeletedAt));
            Assert.All(
                await db.ListasPrecio.IgnoreQueryFilters().Where(l => l.IdTenant == sembrado.IdTenant).ToListAsync(),
                l => Assert.Null(l.DeletedAt));
            Assert.All(
                await db.Clientes.IgnoreQueryFilters().Where(c => c.IdTenant == sembrado.IdTenant).ToListAsync(),
                c => Assert.Null(c.DeletedAt));

            Assert.NotEmpty(await db.Areas.IgnoreQueryFilters()
                .Where(a => a.IdTenant == sembrado.IdTenant).ToListAsync());

            // numeraciones_clientes no hereda de EntidadBase: no tiene deleted_at que mirar, así
            // que lo que se afirma es la PRESENCIA de la fila del contador.
            Assert.True(await db.NumeracionesClientes.AnyAsync(n => n.IdTenant == sembrado.IdTenant));
        }

        var empresas = await root.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas", OpcionesJson);
        var puntos = await root.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        var usuarios = await root.GetFromJsonAsync<PaginaDe<UsuarioListado>>(
            "/api/usuarios?tamanio=200", OpcionesJson);

        Assert.NotNull(empresas);
        Assert.NotNull(puntos);
        Assert.NotNull(usuarios);

        Assert.DoesNotContain(empresas!, e => e.Id == sembrado.IdEmpresa);
        Assert.DoesNotContain(puntos!, p => p.Id == sembrado.IdPuntoVenta);
        Assert.DoesNotContain(usuarios!.Items, u => u.Id == sembrado.IdAdmin);
    }

    /// <summary>
    /// Cláusula S3 (task 4.18, BO-R9): la cascada alcanza SOLO a los hijos vivos. Un hijo que ya
    /// estaba dado de baja conserva su instante ORIGINAL, más viejo — si la cascada lo re-estampara,
    /// el restore por instante de la baja anterior quedaría destruido para siempre.
    /// </summary>
    [Fact]
    public async Task UnHijoYaDadoDeBajaConservaSuInstanteOriginalCuandoCaeElTenant()
    {
        var sembrado = await AprovisionarAsync("s3");
        var instanteViejo = sembrado.Ancla.AddMinutes(1);

        await DarDeBajaAManoAsync(db => db.PuntosVenta.FirstAsync(p => p.Id == sembrado.IdPuntoVenta), instanteViejo);
        await DarDeBajaAManoAsync(db => db.Empresas.FirstAsync(e => e.Id == sembrado.IdEmpresa), instanteViejo);

        var momento = instanteViejo.AddHours(2);
        await ConServicioAsync(null, s => s.EliminarTenantAsync(sembrado.IdTenant), momento);

        Assert.Equal(momento, (await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
        Assert.Equal(momento, (await LeerUsuarioAsync(sembrado.IdAdmin)).DeletedAt);

        Assert.Equal(instanteViejo, (await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.Equal(instanteViejo, (await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
    }

    // =========================================================================================
    // El rastro de auditoría de las tres bajas (judgment-day ronda 1, hallazgo C1)
    // =========================================================================================

    private async Task<long> UltimoIdDeAuditoriaAsync()
    {
        await using var db = ContextoDePlataforma();
        return await db.Auditoria.IgnoreQueryFilters().MaxAsync(a => (long?)a.Id) ?? 0;
    }

    /// <summary>Las filas que la baja ESCRIBIÓ, aisladas por id: aprovisionar no escribe auditoría
    /// hoy, pero afirmar un conteo absoluto ataría esta prueba a esa propiedad ajena.</summary>
    private async Task<List<Ways.Domain.Auditoria.Auditoria>> RastroPosteriorAAsync(long ultimoId, int idTenant)
    {
        await using var db = ContextoDePlataforma();
        return await db.Auditoria.IgnoreQueryFilters()
            .Where(a => a.Id > ultimoId && a.IdTenant == idTenant)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    /// <summary>El <c>deleted_at</c> del payload, truncado a MICROSEGUNDOS. El payload se serializa
    /// desde el <c>DateTimeOffset</c> en memoria (100 ns de resolución) y la columna
    /// <c>timestamptz</c> guarda microsegundos, así que comparar el JSON crudo contra la fila leída
    /// fallaría por los últimos 7 ticks — una diferencia de representación, no de instante.</summary>
    private static DateTimeOffset DeletedAtDe(string? json) =>
        AMicrosegundos(JsonDocument.Parse(json!).RootElement.GetProperty("deleted_at").GetDateTimeOffset());

    private static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    private static bool PorCascadaDe(string? json) =>
        JsonDocument.Parse(json!).RootElement.GetProperty("por_cascada").GetBoolean();

    private static void AssertDeletedAtAnteriorEsNulo(string? json) =>
        Assert.Equal(
            JsonValueKind.Null,
            JsonDocument.Parse(json!).RootElement.GetProperty("deleted_at").ValueKind);

    private async Task<int> IdDelRootAsync()
    {
        await using var db = ContextoDePlataforma();
        return await db.Usuarios.IgnoreQueryFilters().Where(u => u.Mail == MailRoot).Select(u => u.Id).SingleAsync();
    }

    /// <summary>
    /// C1 (judgment-day ronda 1, juez B): la acción MÁS destructiva del sistema no puede ser la
    /// única que no deja rastro. La baja del tenant escribe CUATRO filas —una por cada entidad que
    /// la cascada estampó— dentro de la misma transacción, y la del usuario arrastrado es la MISMA
    /// <c>usuario.baja</c> que escribe el camino directo: desaparecer por cascada no puede dejar
    /// menos rastro que ser dado de baja a mano.
    ///
    /// Se afirma la fila entera y no solo su presencia (<c>mutation-proof-tests</c> regla 12b): el
    /// par (acción, entidad), el <c>id_entidad</c>, el <c>id_tenant</c> del SUJETO, el
    /// <c>id_actor</c> —que acá es una cuenta de PLATAFORMA escribiendo bajo el tenant X, que es
    /// justamente el caso que había que resolver—, el <c>deleted_at</c> del payload contra el
    /// instante realmente estampado en la fila, y el <c>por_cascada</c> de cada hijo.
    /// </summary>
    [Fact]
    public async Task LaBajaDelTenantDejaRastroDeLaCascadaEntera()
    {
        var sembrado = await AprovisionarAsync("rastro-tenant");
        var idRoot = await IdDelRootAsync();
        var ultimoId = await UltimoIdDeAuditoriaAsync();

        using var root = await ClienteComoRootAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}")).StatusCode);

        var momento = (await LeerTenantAsync(sembrado.IdTenant)).DeletedAt!.Value;
        var rastro = await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant);

        Assert.Equal(4, rastro.Count);
        Assert.All(rastro, fila => Assert.Equal(idRoot, fila.IdActor));
        Assert.All(rastro, fila => Assert.Equal(AMicrosegundos(momento), DeletedAtDe(fila.ValorNuevo)));
        Assert.All(rastro, fila => AssertDeletedAtAnteriorEsNulo(fila.ValorAnterior));

        var delTenant = Assert.Single(rastro, f => f.Accion == "tenant.baja");
        Assert.Equal("tenant", delTenant.Entidad);
        Assert.Equal(sembrado.IdTenant, delTenant.IdEntidad);
        Assert.Null(delTenant.IdPuntoVenta);
        Assert.Equal(
            "baja",
            JsonDocument.Parse(delTenant.ValorNuevo).RootElement.GetProperty("estado").GetString());
        Assert.Equal(
            "activo",
            JsonDocument.Parse(delTenant.ValorAnterior!).RootElement.GetProperty("estado").GetString());

        var deLaEmpresa = Assert.Single(rastro, f => f.Accion == "empresa.baja");
        Assert.Equal("empresa", deLaEmpresa.Entidad);
        Assert.Equal(sembrado.IdEmpresa, deLaEmpresa.IdEntidad);
        Assert.True(PorCascadaDe(deLaEmpresa.ValorNuevo));

        var delPunto = Assert.Single(rastro, f => f.Accion == "pv.baja");
        Assert.Equal("punto_venta", delPunto.Entidad);
        Assert.Equal(sembrado.IdPuntoVenta, delPunto.IdEntidad);
        Assert.Equal(sembrado.IdPuntoVenta, delPunto.IdPuntoVenta);
        Assert.True(PorCascadaDe(delPunto.ValorNuevo));

        var delUsuario = Assert.Single(rastro, f => f.Accion == "usuario.baja");
        Assert.Equal("usuario", delUsuario.Entidad);
        Assert.Equal(sembrado.IdAdmin, delUsuario.IdEntidad);

        // R2-8 (judgment-day ronda 2, juez A): la cuenta arrastrada era la ÚNICA de las cuatro
        // filas de la cascada que no decía por qué cayó. Ahora lleva el mismo `por_cascada` que sus
        // hermanas, y el camino DIRECTO no lo lleva — la diferencia entre los dos payloads es la
        // información, así que se afirma en los dos lados: acá arriba `true`, y en
        // `PreciosYUsuariosAuditoriaTests` la baja a mano sigue sin el campo.
        Assert.True(PorCascadaDe(delUsuario.ValorNuevo));
        Assert.Equal(
            "activo",
            JsonDocument.Parse(delUsuario.ValorNuevo).RootElement.GetProperty("estado").GetString());
    }

    /// <summary>
    /// C1, segundo camino: la baja de una empresa deja SU fila y una por cada punto de venta que
    /// arrastró, ninguna <c>usuario.baja</c> (la cascada de empresa no toca cuentas), y el admin
    /// del tenant las LEE por <c>GET /api/auditoria</c> — que es donde el hallazgo decía que no
    /// había nada. Se afirma el conteo por acción, no solo la presencia: una fila de más
    /// (<c>tenant.baja</c> escapándose de la cascada equivocada) también rompe.
    /// </summary>
    [Fact]
    public async Task LaBajaDeUnaEmpresaDejaSuRastroYElAdminLoLeePorLaApi()
    {
        var sembrado = await AprovisionarAsync("rastro-empresa");
        var despues = sembrado.Ancla.AddMinutes(1);
        var momento = despues.AddHours(1);

        var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
        var puntoDeLaSegunda = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, segunda.Id, "Local de la segunda", despues);

        var ultimoId = await UltimoIdDeAuditoriaAsync();

        await ConServicioAsync(
            sembrado.IdTenant, s => s.EliminarEmpresaAsync(segunda.Id), momento, idActor: sembrado.IdAdmin);

        var rastro = await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant);

        Assert.Equal(2, rastro.Count);
        Assert.All(rastro, fila => Assert.Equal(sembrado.IdAdmin, fila.IdActor));
        Assert.All(rastro, fila => Assert.Equal(AMicrosegundos(momento), DeletedAtDe(fila.ValorNuevo)));

        var deLaEmpresa = Assert.Single(rastro, f => f.Accion == "empresa.baja");
        Assert.Equal(segunda.Id, deLaEmpresa.IdEntidad);
        Assert.False(PorCascadaDe(deLaEmpresa.ValorNuevo));

        var delPunto = Assert.Single(rastro, f => f.Accion == "pv.baja");
        Assert.Equal(puntoDeLaSegunda.Id, delPunto.IdEntidad);
        Assert.True(PorCascadaDe(delPunto.ValorNuevo));

        using var admin = await ClienteComoAsync(sembrado.MailAdmin, sembrado.PasswordAdmin);
        var pagina = await admin.GetFromJsonAsync<JsonElement>("/api/auditoria?tamanio=200");

        var acciones = pagina.GetProperty("items").EnumerateArray()
            .Select(item => (
                Accion: item.GetProperty("accion").GetString(),
                IdEntidad: item.GetProperty("idEntidad").GetInt32()))
            .ToList();

        Assert.Contains(("empresa.baja", segunda.Id), acciones);
        Assert.Contains(("pv.baja", puntoDeLaSegunda.Id), acciones);
    }

    /// <summary>
    /// C1, tercer camino: la baja de un punto de venta deja UNA sola fila, marcada como NO
    /// cascada, con su propio id en <c>id_punto_venta</c> — la única de las tres que puede llenar
    /// esa columna honestamente, y lo que hace que el filtro <c>idPuntoVenta</c> del log encuentre
    /// la baja del propio punto de venta.
    /// </summary>
    [Fact]
    public async Task LaBajaDeUnPuntoDeVentaDejaUnaSolaFilaYNoLaMarcaComoCascada()
    {
        var sembrado = await AprovisionarAsync("rastro-pv");
        var despues = sembrado.Ancla.AddMinutes(1);
        var momento = despues.AddHours(1);

        var segundo = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, sembrado.IdEmpresa, "Local 2", despues);

        var ultimoId = await UltimoIdDeAuditoriaAsync();

        await ConServicioAsync(null, s => s.EliminarPuntoVentaAsync(segundo.Id), momento);

        var fila = Assert.Single(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));

        Assert.Equal("pv.baja", fila.Accion);
        Assert.Equal("punto_venta", fila.Entidad);
        Assert.Equal(segundo.Id, fila.IdEntidad);
        Assert.Equal(segundo.Id, fila.IdPuntoVenta);
        Assert.Equal(AMicrosegundos(momento), DeletedAtDe(fila.ValorNuevo));
        Assert.False(PorCascadaDe(fila.ValorNuevo));
    }

    /// <summary>
    /// C1, el lado negativo: una baja RECHAZADA no deja rastro. La fila de auditoría se encola en
    /// el mismo <c>SaveChangesAsync</c> de la transacción, así que el 409 del guard la lleva
    /// consigo — sin esto, el log terminaría afirmando bajas que nunca ocurrieron.
    /// </summary>
    [Fact]
    public async Task UnaBajaRechazadaPorElGuardNoDejaNingunRastro()
    {
        var sembrado = await AprovisionarAsync("rastro-rechazo");
        var despues = sembrado.Ancla.AddMinutes(1);

        await SembrarArticuloAsync(sembrado.IdTenant, despues);

        var ultimoId = await UltimoIdDeAuditoriaAsync();

        using var root = await ClienteComoRootAsync();
        var (codigo, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}"));

        Assert.Equal("tenant_en_uso", codigo);
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));

        // R2-9 (judgment-day ronda 2, juez B): el guard de uso es la dirección que NO puede fallar
        // —tira antes de encolar ninguna fila—, así que probarlo solo a él dejaba sin cubrir a los
        // dos códigos que sí se evalúan sobre el mismo camino. Los mínimos ESTRUCTURALES corren en
        // otro punto del método y tienen su propio 409: se afirman los dos, cada uno en su
        // condición exacta.
        var (codigoDeLaEmpresa, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/empresas/{sembrado.IdEmpresa}"));

        Assert.Equal("ultima_empresa_del_tenant", codigoDeLaEmpresa);
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));

        var (codigoDelPunto, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/puntos-venta/{sembrado.IdPuntoVenta}"));

        Assert.Equal("ultimo_punto_venta_de_la_empresa", codigoDelPunto);
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));

        // Y nada quedó estampado por ninguno de los tres rechazos.
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
        Assert.Null((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
    }

    // =========================================================================================
    // "Un artículo bloquea" y los baldes
    // =========================================================================================

    /// <summary>
    /// LA PRUEBA QUE NO SE DEGRADA (task 4.12, BO-R3): "en uso" es CUALQUIER cosa que el cliente
    /// haya cargado más allá de la línea base, no solo lo transaccional. Un solo artículo — sin
    /// una venta, sin un movimiento de stock, sin un turno — alcanza para que el tenant no se
    /// pueda dar de baja. B2 probado, no afirmado.
    /// </summary>
    [Fact]
    public async Task UnSoloArticuloDelClienteBloqueaLaBajaDelTenant()
    {
        var sembrado = await AprovisionarAsync("un-articulo");
        await SembrarArticuloAsync(sembrado.IdTenant, sembrado.Ancla.AddMinutes(1));

        using var root = await ClienteComoRootAsync();
        var respuesta = await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}");

        var (codigo, mensaje) = await LeerConflictoAsync(respuesta);

        Assert.Equal("tenant_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);

        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
    }

    /// <summary>
    /// La otra mitad de task 4.12, POR DEBAJO DE LA API (OD5): el mismo artículo, habilitado para
    /// una empresa concreta, bloquea a ESA empresa con su propio código.
    ///
    /// ALCANCE, y esta prueba es donde se ve (enmienda de la slice 3): el uso sube por la
    /// jerarquía, nunca baja. Un artículo TENANT-WIDE marca en uso al tenant y NO a la empresa —
    /// dar de baja la empresa no tocaría ese catálogo—; lo que marca a la empresa es la fila de
    /// disponibilidad, que sí le pertenece. Por eso el escenario del spec "un artículo vuelve
    /// indeleteable al tenant, a su empresa y a su punto de venta" es cierto para el tenant, cierto
    /// para la empresa por la fila que le pertenece, y FALSO para el punto de venta: ninguna fila
    /// de artículo se cuelga de un punto de venta (ver
    /// <see cref="UnArticuloNoBloqueaAlPuntoDeVentaYUnaFilaDeStockSi"/>).
    /// </summary>
    [Fact]
    public async Task LaFilaDeDisponibilidadDeUnArticuloBloqueaLaBajaDeSuEmpresa()
    {
        var sembrado = await AprovisionarAsync("empresa-en-uso");
        var despues = sembrado.Ancla.AddMinutes(1);

        // El hermano que hace pasar el mínimo estructural: sin él, el 409 sería
        // ultima_empresa_del_tenant y esta prueba pasaría por el motivo equivocado.
        var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, segunda.Id, "Local 2", despues);

        await SembrarArticuloAsync(sembrado.IdTenant, despues, idEmpresaHabilitada: sembrado.IdEmpresa);

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(sembrado.IdEmpresa));

            Assert.Equal("empresa_en_uso", error.Codigo);
            Assert.Equal(409, error.EstadoHttp);
            Assert.Contains("artículos habilitados", error.Message, StringComparison.Ordinal);
        });

        Assert.Null((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
    }

    /// <summary>
    /// U8 (task 4.23, BO-R5) y el cierre del alcance de task 4.12: un dependiente SIN marca
    /// temporal bloquea por EXISTENCIA, sin comparación de instantes — <c>stock</c> no tiene
    /// columna <c>created_at</c>, así que no hay nada que comparar y la rama pregunta solo si la
    /// fila existe.
    ///
    /// La primera mitad afirma el alcance honesto: el artículo tenant-wide, que sí bloquea al
    /// tenant, NO bloquea al punto de venta. La segunda, que una sola fila de stock sí.
    /// </summary>
    [Fact]
    public async Task UnArticuloNoBloqueaAlPuntoDeVentaYUnaFilaDeStockSi()
    {
        var sembrado = await AprovisionarAsync("pv-stock");
        var despues = sembrado.Ancla.AddMinutes(1);

        var segundo = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, sembrado.IdEmpresa, "Local 2", despues);

        var articulo = await SembrarArticuloAsync(sembrado.IdTenant, despues);

        // La rama de stock del ancla PuntoVenta es SinMarca por definición del inventario: no usa
        // el ancla, así que no puede haber comparación de created_at sobre ella.
        await using (var db = ContextoDePlataforma())
        {
            var ramaDeStock = Assert.Single(
                InventarioDeDependientes.Construir(db.Model, typeof(PuntoVenta)),
                rama => rama.Tabla == "stock");

            Assert.Equal(ClasificacionDeDependiente.SinMarca, ramaDeStock.Clasificacion);
            Assert.False(ramaDeStock.UsaAncla);
        }

        // Con el artículo cargado (y nada más), el punto de venta sigue prístino.
        await ConServicioAsync(null, servicio => servicio.EliminarPuntoVentaAsync(segundo.Id));
        Assert.NotNull((await LeerPuntoVentaAsync(segundo.Id)).DeletedAt);

        await using (var db = ContextoDePlataforma())
        {
            db.Stock.Add(new Stock
            {
                IdTenant = sembrado.IdTenant,
                IdPuntoVenta = sembrado.IdPuntoVenta,
                IdArticulo = articulo.Id,
                Cantidad = 3m
            });
            await db.SaveChangesAsync();
        }

        var tercero = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, sembrado.IdEmpresa, "Local 3", despues);

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarPuntoVentaAsync(sembrado.IdPuntoVenta));

            Assert.Equal("punto_venta_en_uso", error.Codigo);
            Assert.Contains("stock", error.Message, StringComparison.Ordinal);
        });

        Assert.Null((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(tercero.Id)).DeletedAt);
    }

    /// <summary>
    /// Carve-out 1, NO DEGRADABLE (task 4.13, BO-R6): el rastro de auditoría es un registro ACERCA
    /// de la entidad, no algo que el cliente operó ahí. Si bloqueara, la primera acción registrada
    /// sobre un tenant lo volvería indeleteable para siempre.
    ///
    /// Y la razón por la que el carve-out es seguro: la baja es LÓGICA, así que la fila
    /// referenciada sobrevive y el rastro se sigue pudiendo renderizar.
    /// </summary>
    [Fact]
    public async Task SoloFilasDeAuditoriaNoBloqueanLaBajaYElRastroSigueResolviendo()
    {
        var sembrado = await AprovisionarAsync("carve-auditoria");
        var despues = sembrado.Ancla.AddMinutes(1);

        await using (var db = ContextoDePlataforma())
        {
            db.Auditoria.Add(new Ways.Domain.Auditoria.Auditoria
            {
                IdTenant = sembrado.IdTenant,
                IdActor = sembrado.IdAdmin,
                Accion = "usuario.actualizacion",
                Entidad = "usuarios",
                IdEntidad = sembrado.IdAdmin,
                ValorNuevo = "{}",
                CreadoEl = despues
            });
            await db.SaveChangesAsync();
        }

        await ConServicioAsync(null, s => s.EliminarTenantAsync(sembrado.IdTenant));

        Assert.NotNull((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);

        await using (var db = ContextoDePlataforma())
        {
            // La acción se nombra a propósito: desde judgment-day ronda 1 la cascada escribe
            // además su propia `usuario.baja` sobre el MISMO id de entidad, así que sin
            // desambiguar esto ya no identifica la fila sembrada por la prueba.
            var fila = await db.Auditoria.IgnoreQueryFilters()
                .SingleAsync(a => a.IdTenant == sembrado.IdTenant
                    && a.IdEntidad == sembrado.IdAdmin
                    && a.Accion == "usuario.actualizacion");

            // La fila referenciada sigue existiendo (baja lógica), así que el rastro resuelve.
            Assert.True(await db.Usuarios.IgnoreQueryFilters().AnyAsync(u => u.Id == fila.IdEntidad));
        }
    }

    /// <summary>
    /// Carve-out 2, NO DEGRADABLE (task 4.14, BO-R6): el contador <c>numeraciones_clientes</c> que
    /// inserta el aprovisionamiento no hereda de <c>EntidadBase</c> — sin el carve-out caería en el
    /// balde "sin marca" y bloquearía a TODO tenant recién aprovisionado, que es exactamente el
    /// fallo que N4 no podría distinguir de un aprovisionamiento roto.
    /// </summary>
    [Fact]
    public async Task ElContadorDeNumeracionDeClientesNoBloqueaLaBaja()
    {
        var sembrado = await AprovisionarAsync("carve-numeracion");

        await using (var db = ContextoDePlataforma())
        {
            Assert.True(await db.NumeracionesClientes.AnyAsync(n => n.IdTenant == sembrado.IdTenant));
        }

        await ConServicioAsync(null, s => s.EliminarTenantAsync(sembrado.IdTenant));

        Assert.NotNull((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
    }

    /// <summary>
    /// OD4 (task 4.15, BO-R7): "en uso" significa que el cliente OPERÓ acá, no que hoy haya dato
    /// vivo. Un artículo que el cliente cargó y después dio de baja SIGUE bloqueando: borrar la
    /// fila no rebobina la historia, y es además la dirección que falla del lado seguro.
    ///
    /// Mecánicamente sale solo: el guard corre en SQL crudo, que no aplica el query filter
    /// <c>"BajaLogica"</c> de EF. Revertir OD4 cuesta un conjunto <c>AND deleted_at IS NULL</c> por
    /// rama, dar vuelta esta prueba y regenerar el golden N3.
    /// </summary>
    [Fact]
    public async Task UnArticuloDadoDeBajaIgualBloqueaLaBajaDelTenant()
    {
        var sembrado = await AprovisionarAsync("od4");
        var despues = sembrado.Ancla.AddMinutes(1);

        var articulo = await SembrarArticuloAsync(sembrado.IdTenant, despues);
        await DarDeBajaAManoAsync(db => db.Articulos.FirstAsync(a => a.Id == articulo.Id), despues.AddMinutes(1));

        using var root = await ClienteComoRootAsync();
        var (codigo, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}"));

        Assert.Equal("tenant_en_uso", codigo);
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
    }

    // =========================================================================================
    // Los kills de la enumeración de conjuntos (U1-U8)
    // =========================================================================================

    /// <summary>
    /// U4 (task 4.19, BO-R9): la cascada de una empresa llega SOLO a SUS puntos de venta. Con una
    /// segunda empresa sembrada a mano en el mismo tenant, un <c>Where</c> que se olvidara el
    /// <c>IdEmpresa</c> arrastraría también al punto de venta de la hermana — y el tenant y sus
    /// usuarios, que no son hijos de una empresa, quedan intactos en cualquier caso.
    /// </summary>
    [Fact]
    public async Task LaBajaDeUnaEmpresaSoloArrastraSusPropiosPuntosDeVenta()
    {
        var sembrado = await AprovisionarAsync("cascada-empresa");
        var despues = sembrado.Ancla.AddMinutes(1);
        var momento = despues.AddHours(1);

        var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
        var puntoDeLaSegunda = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, segunda.Id, "Local de la segunda", despues);

        await ConServicioAsync(null, s => s.EliminarEmpresaAsync(segunda.Id), momento);

        Assert.Equal(momento, (await LeerEmpresaAsync(segunda.Id)).DeletedAt);
        Assert.Equal(momento, (await LeerPuntoVentaAsync(puntoDeLaSegunda.Id)).DeletedAt);

        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
        Assert.Null((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerUsuarioAsync(sembrado.IdAdmin)).DeletedAt);
    }

    /// <summary>
    /// U1, U2 y U3 (task 4.20, BO-R9): los tres <c>Where(hijo.IdTenant == id)</c> de la cascada del
    /// tenant. Un tenant HERMANO, aprovisionado igual, conserva su empresa, su punto de venta y su
    /// admin — afirmado por identidad Y por conteo exacto (<c>mutation-proof-tests</c> regla 12c):
    /// sin el conteo, una cascada que arrastrara de más seguiría pasando la aserción de identidad
    /// sobre las filas que sí sobrevivieron.
    /// </summary>
    [Fact]
    public async Task LasFilasDeUnTenantHermanoSobrevivenALaBajaDelPrimero()
    {
        var primero = await AprovisionarAsync("hermano-a");
        var hermano = await AprovisionarAsync("hermano-b");

        await ConServicioAsync(null, s => s.EliminarTenantAsync(primero.IdTenant));

        Assert.Null((await LeerTenantAsync(hermano.IdTenant)).DeletedAt);
        Assert.Null((await LeerEmpresaAsync(hermano.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(hermano.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerUsuarioAsync(hermano.IdAdmin)).DeletedAt);

        await using var db = ContextoDePlataforma();

        Assert.Equal(1, await db.Empresas.CountAsync(e => e.IdTenant == hermano.IdTenant));
        Assert.Equal(1, await db.PuntosVenta.CountAsync(p => p.IdTenant == hermano.IdTenant));
        Assert.Equal(1, await db.Usuarios.CountAsync(u => u.IdTenant == hermano.IdTenant));

        Assert.Equal(0, await db.Empresas.CountAsync(e => e.IdTenant == primero.IdTenant));
        Assert.Equal(0, await db.PuntosVenta.CountAsync(p => p.IdTenant == primero.IdTenant));
        Assert.Equal(0, await db.Usuarios.CountAsync(u => u.IdTenant == primero.IdTenant));
    }

    /// <summary>
    /// U5 y U6 (task 4.21, BO-R10): los dos <c>COUNT</c> de los mínimos estructurales. Si el conteo
    /// de empresas se olvidara del <c>IdTenant</c>, vería la empresa del tenant hermano y el mínimo
    /// NUNCA dispararía; lo mismo con el conteo de puntos de venta y el <c>IdEmpresa</c>. Los dos
    /// mueren acá, y por la dirección peligrosa: la mutación no rompe una aserción de igualdad,
    /// APAGA una protección.
    /// </summary>
    [Fact]
    public async Task LosMinimosNoCuentanHermanosDeOtroTenantNiDeOtraEmpresa()
    {
        var primero = await AprovisionarAsync("minimo-a");
        var hermano = await AprovisionarAsync("minimo-b");

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(primero.IdEmpresa));
            Assert.Equal("ultima_empresa_del_tenant", error.Codigo);
            Assert.Equal(409, error.EstadoHttp);
        });

        // Segunda empresa CON su punto de venta: el mínimo de empresas ya no aplica, y el punto de
        // venta de esa hermana es el que no se tiene que contar como sobreviviente del original.
        var despues = primero.Ancla.AddMinutes(1);
        var segunda = await SembrarEmpresaAsync(primero.IdTenant, "Segunda SRL", despues);
        await SembrarPuntoVentaAsync(primero.IdTenant, segunda.Id, "Local de la segunda", despues);

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarPuntoVentaAsync(primero.IdPuntoVenta));
            Assert.Equal("ultimo_punto_venta_de_la_empresa", error.Codigo);
        });

        Assert.Null((await LeerEmpresaAsync(primero.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(primero.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerEmpresaAsync(hermano.IdEmpresa)).DeletedAt);
    }

    /// <summary>
    /// U7 (task 4.22, BO-R2) — EL KILL DE <c>&gt;</c> CONTRA <c>&gt;=</c>, que es toda la corrección
    /// del discriminante. Tres tenants aprovisionados por el camino real:
    ///
    /// <list type="bullet">
    /// <item>uno con un dependiente creado EXACTAMENTE en el instante del ancla — no bloquea, y es
    /// la razón por la que todo tenant recién aprovisionado se puede dar de baja;</item>
    /// <item>uno con el mismo dependiente UN TICK después (1 µs, la resolución real de
    /// <c>timestamptz</c>) — bloquea;</item>
    /// <item>uno sin nada, que prueba U7(a): el dependiente del hermano no lo alcanza.</item>
    /// </list>
    ///
    /// Con <c>&gt;=</c> el primero pasaría a bloquear y ningún tenant nuevo sería deleteable; sin el
    /// conjunto de la FK, el tercero también bloquearía.
    /// </summary>
    [Fact]
    public async Task UnDependienteEnElInstanteDelAnclaNoBloqueaYUnTickDespuesSi()
    {
        var enElAncla = await AprovisionarAsync("borde-igual");
        var unTickDespues = await AprovisionarAsync("borde-mayor");
        var sinNada = await AprovisionarAsync("borde-hermano");

        await SembrarCategoriaAsync(enElAncla.IdTenant, null, "En el ancla", enElAncla.Ancla);
        await SembrarCategoriaAsync(
            unTickDespues.IdTenant, null, "Un tick después", unTickDespues.Ancla.AddTicks(10));

        using var root = await ClienteComoRootAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{enElAncla.IdTenant}")).StatusCode);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/plataforma/tenants/{unTickDespues.IdTenant}"));

        Assert.Equal("tenant_en_uso", codigo);
        Assert.Contains("categorías", mensaje, StringComparison.Ordinal);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{sinNada.IdTenant}")).StatusCode);
    }

    // =========================================================================================
    // Descubrimiento por metadata, catálogo compartido y mínimos
    // =========================================================================================

    /// <summary>
    /// BO-R4 (task 4.24): el conjunto de dependientes sale de la metadata, así que cubre las FKs
    /// que una lista escrita a mano perdería. Las dos formas que más fácil se olvidan:
    ///
    /// <list type="bullet">
    /// <item>una SEGUNDA FK al mismo principal — un movimiento de stock que referencia al punto de
    /// venta SOLO por <c>id_punto_venta_destino</c> (la transferencia entrante);</item>
    /// <item>una FK cuyo nombre de propiedad no sigue la convención — un turno que referencia al
    /// usuario SOLO por <c>id_empleado_cierre</c>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LasFksSecundariasYLasDeNombreNoConvencionalTambienBloquean()
    {
        var sembrado = await AprovisionarAsync("descubrimiento");
        var despues = sembrado.Ancla.AddMinutes(1);

        var origen = await SembrarPuntoVentaAsync(sembrado.IdTenant, sembrado.IdEmpresa, "Origen", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, sembrado.IdEmpresa, "Tercero", despues);

        var articulo = await SembrarArticuloAsync(sembrado.IdTenant, despues);
        var empleado = await SembrarUsuarioAsync(
            sembrado.IdTenant, RolConocido.Vendedor, "una-contraseña-larga", despues);

        await using (var db = ContextoDePlataforma())
        {
            db.MovimientosStock.Add(new MovimientoStock
            {
                IdTenant = sembrado.IdTenant,
                IdArticulo = articulo.Id,
                IdPuntoVenta = origen.Id,
                IdPuntoVentaDestino = sembrado.IdPuntoVenta,
                Cantidad = 2m,
                Motivo = MotivoStock.Transferencia,
                IdEmpleado = sembrado.IdAdmin,
                CreadoEl = despues
            });
            await db.SaveChangesAsync();
        }

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarPuntoVentaAsync(sembrado.IdPuntoVenta));

            Assert.Equal("punto_venta_en_uso", error.Codigo);
            Assert.Contains("movimientos de stock", error.Message, StringComparison.Ordinal);
        });

        // El turno referencia al empleado SOLO por id_empleado_cierre: lo abrió el admin. Va
        // ESTRICTAMENTE después del created_at del empleado — su propio ancla —, si no la rama
        // Marcado no bloquearía y la prueba pasaría por el motivo equivocado.
        await SembrarTurnoAsync(
            sembrado.IdTenant, sembrado.IdPuntoVenta, sembrado.IdAdmin, despues.AddMinutes(1),
            idEmpleadoCierre: empleado.Id);

        using var root = await ClienteComoRootAsync();
        var (codigo, mensaje) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/usuarios/{empleado.Id}"));

        Assert.Equal("usuario_en_uso", codigo);
        Assert.Contains("turnos de caja", mensaje, StringComparison.Ordinal);
        Assert.Null((await LeerUsuarioAsync(empleado.Id)).DeletedAt);
    }

    /// <summary>
    /// BO-R8 (task 4.25): una fila de catálogo COMPARTIDO (<c>id_empresa IS NULL</c> por diseño)
    /// no bloquea la baja de ninguna empresa en particular — no le pertenece a ninguna. La misma
    /// fila, con la empresa puesta, sí la bloquea: es el par el que prueba la cláusula, porque una
    /// sola mitad la pasaría igual un guard que no mirara nada.
    /// </summary>
    [Fact]
    public async Task UnaFilaDeCatalogoCompartidoNoBloqueaALaEmpresaYUnaPropiaSi()
    {
        var sembrado = await AprovisionarAsync("catalogo-compartido");
        var despues = sembrado.Ancla.AddMinutes(1);

        var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, segunda.Id, "Local de la segunda", despues);

        await SembrarCategoriaAsync(sembrado.IdTenant, null, "Compartida", despues);

        await ConServicioAsync(null, servicio => servicio.EliminarEmpresaAsync(sembrado.IdEmpresa));
        Assert.NotNull((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);

        // Estrictamente posterior al created_at de `segunda`, que es SU ancla.
        await SembrarCategoriaAsync(
            sembrado.IdTenant, segunda.Id, "Propia de la segunda", despues.AddMinutes(1));

        var tercera = await SembrarEmpresaAsync(sembrado.IdTenant, "Tercera SRL", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, tercera.Id, "Local de la tercera", despues);

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(segunda.Id));

            Assert.Equal("empresa_en_uso", error.Codigo);
            Assert.Contains("categorías", error.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Los dos mínimos estructurales completos (task 4.26, BO-R10), con sus dos reglas finas:
    ///
    /// <list type="bullet">
    /// <item><b>S2</b> — el conteo mira HERMANOS VIVOS: después de dar de baja a una de dos
    /// empresas, la que queda vuelve a ser "la última" y el mínimo dispara. Una hermana dada de
    /// baja no es una sobreviviente;</item>
    /// <item><b>S6</b> — cuando aplican el mínimo Y el uso, gana el ESTRUCTURAL: las dos respuestas
    /// le dicen cosas opuestas al operador ("hay datos acá" contra "dá de baja el padre"), y la que
    /// corresponde es la segunda.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LosMinimosDisparanEnSuCondicionExactaYGananSobreElVeredictoDeUso()
    {
        var sembrado = await AprovisionarAsync("minimos");
        var despues = sembrado.Ancla.AddMinutes(1);

        var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, segunda.Id, "Local de la segunda", despues);

        // Con dos empresas vivas, dar de baja una anda.
        await ConServicioAsync(null, servicio => servicio.EliminarEmpresaAsync(segunda.Id));
        Assert.NotNull((await LeerEmpresaAsync(segunda.Id)).DeletedAt);

        // S2: la hermana dada de baja no cuenta, así que la que queda es la última.
        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(sembrado.IdEmpresa));
            Assert.Equal("ultima_empresa_del_tenant", error.Codigo);
        });

        // S6: la misma última empresa, ahora CON uso propio, sigue contestando el código
        // estructural y no empresa_en_uso.
        await SembrarCategoriaAsync(sembrado.IdTenant, sembrado.IdEmpresa, "Propia", despues);

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(sembrado.IdEmpresa));
            Assert.Equal("ultima_empresa_del_tenant", error.Codigo);
        });

        // Y el mínimo de puntos de venta, con la misma forma: el único de la empresa no se va,
        // aunque además tenga uso.
        var articulo = await SembrarArticuloAsync(sembrado.IdTenant, despues);

        await using (var db = ContextoDePlataforma())
        {
            db.Stock.Add(new Stock
            {
                IdTenant = sembrado.IdTenant,
                IdPuntoVenta = sembrado.IdPuntoVenta,
                IdArticulo = articulo.Id,
                Cantidad = 1m
            });
            await db.SaveChangesAsync();
        }

        await ConServicioAsync(null, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarPuntoVentaAsync(sembrado.IdPuntoVenta));
            Assert.Equal("ultimo_punto_venta_de_la_empresa", error.Codigo);
        });
    }

    // =========================================================================================
    // El conjunto EXACTO de seis códigos
    // =========================================================================================

    /// <summary>
    /// BO-R11 (task 4.27): los SEIS códigos, cada uno con un fixture construido para satisfacer
    /// SOLO su condición. Cuatro salen por la API; <c>empresa_en_uso</c> y
    /// <c>punto_venta_en_uso</c> salen por debajo (OD5: a través de las rutas el mínimo estructural
    /// dispara siempre primero, así que una prueba de API para esos dos pasaría por el motivo
    /// equivocado).
    ///
    /// Lo que fija esta prueba es que cada condición rinde SU código y no el del vecino: sin los
    /// seis fixtures separados, un guard que contestara siempre <c>tenant_en_uso</c> pasaría.
    /// </summary>
    [Fact]
    public async Task CadaUnoDeLosSeisCodigosDisparaSoloEnSuPropiaCondicion()
    {
        using var root = await ClienteComoRootAsync();

        // 1. tenant_en_uso
        var conUso = await AprovisionarAsync("codigo-tenant");
        await SembrarCategoriaAsync(conUso.IdTenant, null, "Del cliente", conUso.Ancla.AddMinutes(1));
        var (codigoTenant, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/plataforma/tenants/{conUso.IdTenant}"));
        Assert.Equal("tenant_en_uso", codigoTenant);

        // 2. ultima_empresa_del_tenant — el tenant aprovisionado tiene exactamente una.
        var unaEmpresa = await AprovisionarAsync("codigo-ultima-empresa");
        var (codigoEmpresaMinima, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/empresas/{unaEmpresa.IdEmpresa}"));
        Assert.Equal("ultima_empresa_del_tenant", codigoEmpresaMinima);

        // 3. ultimo_punto_venta_de_la_empresa — idem, exactamente uno.
        var (codigoPuntoMinimo, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/puntos-venta/{unaEmpresa.IdPuntoVenta}"));
        Assert.Equal("ultimo_punto_venta_de_la_empresa", codigoPuntoMinimo);

        // 4. usuario_en_uso
        var conTurno = await AprovisionarAsync("codigo-usuario");
        await SembrarTurnoAsync(
            conTurno.IdTenant, conTurno.IdPuntoVenta, conTurno.IdAdmin, conTurno.Ancla.AddMinutes(1));
        var (codigoUsuario, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/usuarios/{conTurno.IdAdmin}"));
        Assert.Equal("usuario_en_uso", codigoUsuario);

        // 5 y 6. Los dos que solo existen por debajo de la API.
        var bajoLaApi = await AprovisionarAsync("codigo-bajo-api");
        var despues = bajoLaApi.Ancla.AddMinutes(1);

        var segunda = await SembrarEmpresaAsync(bajoLaApi.IdTenant, "Segunda SRL", despues);
        await SembrarPuntoVentaAsync(bajoLaApi.IdTenant, segunda.Id, "Local de la segunda", despues);
        await SembrarPuntoVentaAsync(bajoLaApi.IdTenant, bajoLaApi.IdEmpresa, "Local 2", despues);

        await SembrarArticuloAsync(bajoLaApi.IdTenant, despues, idEmpresaHabilitada: bajoLaApi.IdEmpresa);

        var articulo = await SembrarArticuloAsync(bajoLaApi.IdTenant, despues);
        await using (var db = ContextoDePlataforma())
        {
            db.Stock.Add(new Stock
            {
                IdTenant = bajoLaApi.IdTenant,
                IdPuntoVenta = bajoLaApi.IdPuntoVenta,
                IdArticulo = articulo.Id,
                Cantidad = 1m
            });
            await db.SaveChangesAsync();
        }

        await ConServicioAsync(null, async servicio =>
        {
            Assert.Equal(
                "empresa_en_uso",
                (await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(bajoLaApi.IdEmpresa))).Codigo);

            Assert.Equal(
                "punto_venta_en_uso",
                (await ErrorDeAsync(() => servicio.EliminarPuntoVentaAsync(bajoLaApi.IdPuntoVenta))).Codigo);
        });
    }

    /// <summary>
    /// La otra mitad de BO-R11 (task 4.27): una tabla bloqueante SIN etiqueta rinde igual el código
    /// EXACTO, y solo el mensaje degrada a la frase genérica. El código nunca depende del mensaje —
    /// es lo que le permite a la web mapear su copia por <c>codigo</c> y nunca por texto.
    ///
    /// El fixture es deliberadamente fino: la categoría y la oferta se crean EN el instante del
    /// ancla (no bloquean, son ramas Marcado), y la única fila que bloquea es
    /// <c>ofertas_listas</c> — sin marca temporal y sin etiqueta en el diccionario.
    /// </summary>
    [Fact]
    public async Task UnaTablaSinEtiquetaRindeElCodigoExactoYDegradaSoloElMensaje()
    {
        var sembrado = await AprovisionarAsync("sin-etiqueta");

        await using (var db = ContextoDePlataforma())
        {
            var categoria = new Categoria
            {
                IdTenant = sembrado.IdTenant,
                Nombre = "Categoría en el ancla",
                Orden = 1,
                CreatedAt = sembrado.Ancla,
                UpdatedAt = sembrado.Ancla
            };
            db.Categorias.Add(categoria);
            await db.SaveChangesAsync();

            var oferta = new Ways.Domain.Ofertas.Oferta
            {
                IdTenant = sembrado.IdTenant,
                Nombre = "Oferta en el ancla",
                IdCategoria = categoria.Id,
                Porcentaje = 10m,
                Prioridad = 1,
                CreatedAt = sembrado.Ancla,
                UpdatedAt = sembrado.Ancla
            };
            db.Ofertas.Add(oferta);
            await db.SaveChangesAsync();

            var idLista = await db.ListasPrecio
                .Where(l => l.IdTenant == sembrado.IdTenant).Select(l => l.Id).FirstAsync();

            db.OfertasListas.Add(new Ways.Domain.Ofertas.OfertaLista
            {
                IdOferta = oferta.Id,
                IdListaPrecio = idLista,
                IdTenant = sembrado.IdTenant
            });
            await db.SaveChangesAsync();
        }

        using var root = await ClienteComoRootAsync();
        var (codigo, mensaje) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}"));

        Assert.Equal("tenant_en_uso", codigo);
        Assert.Contains(EtiquetasDeTablas.Generica, mensaje, StringComparison.Ordinal);
        Assert.DoesNotContain("ofertas_listas", mensaje, StringComparison.Ordinal);
    }

    // =========================================================================================
    // La baja de usuario (UT-R2)
    // =========================================================================================

    /// <summary>
    /// UT-R2 (task 4.28): el guard entra DESPUÉS de <c>PoliticaDeRoles</c> y NUNCA en su lugar, y
    /// todo lo que ya regía sigue rigiendo. Las cinco afirmaciones:
    ///
    /// <list type="number">
    /// <item>el admin aprovisionado es deleteable MIENTRAS no haya operado — y la fila de auditoría
    /// de la baja se sigue escribiendo;</item>
    /// <item>después de abrir un turno, la misma baja es <c>409 usuario_en_uso</c> y NO escribe
    /// <c>deleted_at</c>;</item>
    /// <item>un objetivo Root con uso pesado rinde el error de <c>PoliticaDeRoles</c>, no
    /// <c>usuario_en_uso</c>: el orden importa y es observable;</item>
    /// <item>la autobaja sigue prohibida aunque la cuenta tenga uso — otra vez el orden;</item>
    /// <item>el 404 deliberado de <c>ValidarAlcanceDeTenant</c> sigue tapando el alcance ajeno
    /// (ver <see cref="UnaBajaFueraDeAlcanceEs404YNuncaFiltraElUso"/>).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LaBajaDeUsuarioCorreElGuardDespuesDePoliticaDeRolesYNuncaEnSuLugar()
    {
        using var root = await ClienteComoRootAsync();

        // 1. Nunca usado ⇒ se da de baja, con su fila de auditoría.
        var sinUso = await AprovisionarAsync("usuario-sin-uso");
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/usuarios/{sinUso.IdAdmin}")).StatusCode);

        Assert.NotNull((await LeerUsuarioAsync(sinUso.IdAdmin)).DeletedAt);

        await using (var db = ContextoDePlataforma())
        {
            Assert.True(await db.Auditoria.IgnoreQueryFilters()
                .AnyAsync(a => a.IdTenant == sinUso.IdTenant && a.IdEntidad == sinUso.IdAdmin));
        }

        // 2. El mismo admin, después de abrir un turno ⇒ 409 y NADA escrito.
        var conTurno = await AprovisionarAsync("usuario-con-turno");
        await SembrarTurnoAsync(
            conTurno.IdTenant, conTurno.IdPuntoVenta, conTurno.IdAdmin, conTurno.Ancla.AddMinutes(1));

        var (codigo, _) = await LeerConflictoAsync(
            await root.DeleteAsync($"/api/usuarios/{conTurno.IdAdmin}"));

        Assert.Equal("usuario_en_uso", codigo);
        Assert.Null((await LeerUsuarioAsync(conTurno.IdAdmin)).DeletedAt);

        // 3. Objetivo Root CON uso pesado ⇒ gana PoliticaDeRoles, no el guard.
        var otroRoot = await SembrarUsuarioAsync(
            null, RolConocido.Root, "una-contraseña-larga", conTurno.Ancla);

        // CERRADO por el root: ux_turnos_caja_abierto admite un solo turno abierto por punto de
        // venta, y de paso el uso del root entra por id_empleado_cierre.
        await SembrarTurnoAsync(
            conTurno.IdTenant, conTurno.IdPuntoVenta, conTurno.IdAdmin, conTurno.Ancla.AddMinutes(2),
            idEmpleadoCierre: otroRoot.Id);

        var respuestaRoot = await root.DeleteAsync($"/api/usuarios/{otroRoot.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, respuestaRoot.StatusCode);

        var cuerpoRoot = await respuestaRoot.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("prohibido", cuerpoRoot.GetProperty("codigo").GetString());
        Assert.Null((await LeerUsuarioAsync(otroRoot.Id)).DeletedAt);

        // 4. Autobaja, con uso ⇒ tampoco llega al guard.
        using var admin = await ClienteComoAsync(conTurno.MailAdmin, conTurno.PasswordAdmin);
        var autobaja = await admin.DeleteAsync($"/api/usuarios/{conTurno.IdAdmin}");

        Assert.Equal(HttpStatusCode.Forbidden, autobaja.StatusCode);
        var cuerpoAutobaja = await autobaja.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("prohibido", cuerpoAutobaja.GetProperty("codigo").GetString());
    }

    // =========================================================================================
    // Anti-oráculo, idempotencia y login
    // =========================================================================================

    /// <summary>
    /// BO-R12 (task 4.29, ADR-8): una baja fuera de alcance es <c>404</c>, idéntica en estado y en
    /// forma de cuerpo a la de un id que no existe — nunca 403 y nunca un 409 que confirme que del
    /// otro lado hay datos. Se prueba justamente sobre una empresa AJENA CON USO: es el caso donde
    /// un guard mal ordenado filtraría el uso de otro tenant.
    /// </summary>
    [Fact]
    public async Task UnaBajaFueraDeAlcanceEs404YNuncaFiltraElUso()
    {
        var propio = await AprovisionarAsync("oraculo-a");
        var ajeno = await AprovisionarAsync("oraculo-b");

        var despues = ajeno.Ancla.AddMinutes(1);
        var segundaAjena = await SembrarEmpresaAsync(ajeno.IdTenant, "Segunda ajena SRL", despues);
        await SembrarPuntoVentaAsync(ajeno.IdTenant, segundaAjena.Id, "Local ajeno", despues);
        await SembrarArticuloAsync(ajeno.IdTenant, despues, idEmpresaHabilitada: ajeno.IdEmpresa);

        using var admin = await ClienteComoAsync(propio.MailAdmin, propio.PasswordAdmin);

        var ajena = await admin.DeleteAsync($"/api/empresas/{ajeno.IdEmpresa}");
        var inexistente = await admin.DeleteAsync("/api/empresas/999999999");
        var usuarioAjeno = await admin.DeleteAsync($"/api/usuarios/{ajeno.IdAdmin}");

        Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, usuarioAjeno.StatusCode);

        var cuerpoAjena = await ajena.Content.ReadFromJsonAsync<JsonElement>();
        var cuerpoInexistente = await inexistente.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("no_encontrado", cuerpoAjena.GetProperty("codigo").GetString());
        Assert.Equal("no_encontrado", cuerpoInexistente.GetProperty("codigo").GetString());
        Assert.Equal(
            cuerpoInexistente.GetProperty("status").GetInt32(),
            cuerpoAjena.GetProperty("status").GetInt32());

        Assert.Null((await LeerEmpresaAsync(ajeno.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerUsuarioAsync(ajeno.IdAdmin)).DeletedAt);
    }

    /// <summary>
    /// BO-R1 (task 4.30): una segunda baja del mismo id es <c>404</c> —la fila ya es invisible para
    /// la propia búsqueda del servicio— y NO escribe un segundo <c>deleted_at</c>. Si lo escribiera,
    /// el restore por instante de la primera baja quedaría roto y el 500 sería lo de menos.
    /// </summary>
    [Fact]
    public async Task UnaSegundaBajaDelMismoIdEs404YNoPisaElInstante()
    {
        var sembrado = await AprovisionarAsync("idempotente");
        var momento = new DateTimeOffset(2026, 9, 5, 18, 0, 0, TimeSpan.Zero);

        await ConServicioAsync(null, s => s.EliminarTenantAsync(sembrado.IdTenant), momento);

        using var root = await ClienteComoRootAsync();
        var segunda = await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}");

        Assert.Equal(HttpStatusCode.NotFound, segunda.StatusCode);
        Assert.Equal(momento, (await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
    }

    /// <summary>
    /// OD6 (task 4.31, Reconciliación 1): el admin de un tenant arrastrado por la cascada recibe
    /// <c>401 credenciales_invalidas</c> y NO <c>403 tenant_suspendido</c> — la búsqueda del login
    /// corre bajo <c>"BajaLogica"</c> sin <c>IgnoreQueryFilters</c>, así que la cuenta simplemente no
    /// existe y la request muere antes de llegar a mirar el estado del tenant.
    ///
    /// La segunda mitad es la REGRESIÓN que hace que la primera signifique algo: el 403 sigue vivo
    /// y alcanzable para un tenant SUSPENDIDO. Sin ella, un 401 podría venir de haber roto el
    /// camino de suspensión.
    /// </summary>
    [Fact]
    public async Task ElAdminDeUnTenantDadoDeBajaRecibe401YElDeUnoSuspendido403()
    {
        var dadoDeBaja = await AprovisionarAsync("login-baja");
        var suspendido = await AprovisionarAsync("login-suspendido");

        using var root = await ClienteComoRootAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{dadoDeBaja.IdTenant}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await root.PostAsync($"/api/plataforma/tenants/{suspendido.IdTenant}/suspender", null)).StatusCode);

        using var clienteBaja = fixture.CreateClient();
        var loginBaja = await clienteBaja.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(dadoDeBaja.MailAdmin, dadoDeBaja.PasswordAdmin));

        Assert.Equal(HttpStatusCode.Unauthorized, loginBaja.StatusCode);
        Assert.Equal(
            "credenciales_invalidas",
            (await loginBaja.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        using var clienteSuspendido = fixture.CreateClient();
        var loginSuspendido = await clienteSuspendido.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(suspendido.MailAdmin, suspendido.PasswordAdmin));

        Assert.Equal(HttpStatusCode.Forbidden, loginSuspendido.StatusCode);
        Assert.Equal(
            "tenant_suspendido",
            (await loginSuspendido.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }

    // =========================================================================================
    // RLS y autorización
    // =========================================================================================

    /// <summary>
    /// BO-R7/BO-R12 (task 4.32, <c>mutation-proof-tests</c> regla 5): el guard corre sobre la
    /// conexión <c>ways_app</c> (NOSUPERUSER/NOBYPASSRLS), que es la única bajo la cual RLS prueba
    /// algo — un fixture superusuario no probaría nada.
    ///
    /// El par, y las dos direcciones importan:
    /// <list type="bullet">
    /// <item>un admin de tenant VE todos los dependientes de su propio tenant, así que no puede
    /// SUB-contar y dar de baja algo en uso (la dirección de pérdida de datos);</item>
    /// <item>y NUNCA ve un dependiente de otro tenant, así que el uso ajeno no le bloquea una baja
    /// legítima (la dirección de sobre-bloqueo, que además sería una filtración).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ElGuardVeTodoLoDeSuTenantYNadaDeOtroSobreLaConexionDeAplicacion()
    {
        var conUso = await AprovisionarAsync("rls-con-uso");
        var sinUso = await AprovisionarAsync("rls-sin-uso");

        foreach (var sembrado in new[] { conUso, sinUso })
        {
            var despues = sembrado.Ancla.AddMinutes(1);
            var segunda = await SembrarEmpresaAsync(sembrado.IdTenant, "Segunda SRL", despues);
            await SembrarPuntoVentaAsync(sembrado.IdTenant, segunda.Id, "Local de la segunda", despues);
        }

        await SembrarCategoriaAsync(
            conUso.IdTenant, conUso.IdEmpresa, "Propia del que usa", conUso.Ancla.AddMinutes(2));

        await ConServicioAsync(conUso.IdTenant, async servicio =>
        {
            var error = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(conUso.IdEmpresa));
            Assert.Equal("empresa_en_uso", error.Codigo);
        });

        await ConServicioAsync(sinUso.IdTenant, servicio => servicio.EliminarEmpresaAsync(sinUso.IdEmpresa));

        Assert.Null((await LeerEmpresaAsync(conUso.IdEmpresa)).DeletedAt);
        Assert.NotNull((await LeerEmpresaAsync(sinUso.IdEmpresa)).DeletedAt);
    }

    /// <summary>
    /// TO-R4/TO-R5 y S4 (task 4.33): las tres rutas nuevas reusan la policy del grupo al que ya
    /// pertenecen — cero policies nuevas (criterio V5) — y las transiciones de estado que ya
    /// existían se comportan exactamente igual que antes.
    ///
    /// <list type="bullet">
    /// <item>un admin de tenant no puede dar de baja un tenant (<c>SoloPlataforma</c>);</item>
    /// <item>un vendedor que SÍ puede leer puntos de venta (<c>LecturaDePuntosVenta</c>, el
    /// selector del POS) NO puede darlos de baja (<c>GestionDeOrganizacion</c>) — la asimetría
    /// deliberada del grupo;</item>
    /// <item>suspender y reactivar no leen ni escriben <c>deleted_at</c>;</item>
    /// <item>reactivar un tenant DADO DE BAJA es <c>404</c> (S4): la fila es invisible, así que el
    /// <c>409 tenant_dado_de_baja</c> preexistente queda como backstop inalcanzable por esta ruta,
    /// preservado sin cambios.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LasPoliciesYLasTransicionesDeEstadoSeComportanIgualQueAntes()
    {
        var sembrado = await AprovisionarAsync("policies");
        var vendedor = await SembrarUsuarioAsync(
            sembrado.IdTenant, RolConocido.Vendedor, "una-contraseña-larga", sembrado.Ancla);

        using var admin = await ClienteComoAsync(sembrado.MailAdmin, sembrado.PasswordAdmin);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await admin.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}")).StatusCode);

        using var clienteVendedor = await ClienteComoAsync(vendedor.Mail, "una-contraseña-larga");
        Assert.Equal(HttpStatusCode.OK, (await clienteVendedor.GetAsync("/api/puntos-venta")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await clienteVendedor.DeleteAsync($"/api/puntos-venta/{sembrado.IdPuntoVenta}")).StatusCode);

        using var root = await ClienteComoRootAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await root.PostAsync($"/api/plataforma/tenants/{sembrado.IdTenant}/suspender", null)).StatusCode);
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);

        Assert.Equal(
            HttpStatusCode.OK,
            (await root.PostAsync($"/api/plataforma/tenants/{sembrado.IdTenant}/reactivar", null)).StatusCode);
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await root.DeleteAsync($"/api/plataforma/tenants/{sembrado.IdTenant}")).StatusCode);

        // S4 — el tenant dado de baja es invisible, así que reactivarlo es 404 y no el 409.
        var reactivacion = await root.PostAsync(
            $"/api/plataforma/tenants/{sembrado.IdTenant}/reactivar", null);

        Assert.Equal(HttpStatusCode.NotFound, reactivacion.StatusCode);
        Assert.Equal(
            EstadoTenant.Baja,
            (await LeerTenantAsync(sembrado.IdTenant)).Estado);
    }

    // =========================================================================================
    // [S] estructural con base: cobertura de índices
    // =========================================================================================

    /// <summary>
    /// Task 4.36 (design D, T6) — cobertura de ÍNDICES de las ramas del inspector, que se REPORTA
    /// y no se arregla: arreglarla sería DDL y el gate de esta etapa es ZERO-SCHEMA. Una rama sin
    /// índice de soporte es un hallazgo nombrado para una etapa futura, nunca un seq scan
    /// silencioso ni un bloqueo acá.
    ///
    /// Una rama PUENTEADA necesita cobertura sobre DOS relaciones, no una (entrada de judgment-day
    /// de la slice 3, item 3): la columna de unión en la HOJA y las columnas del PUENTE hacia el
    /// ancla. Mirando una sola tabla, las ramas puenteadas de <c>Empresa</c> se reportarían como
    /// cubiertas mientras el predicado del lado <c>pv</c> corre sin índice.
    ///
    /// El conjunto de hallazgos está CONGELADO abajo: así esto no es un reporte que nadie lee sino
    /// un trip-wire — una rama nueva sin índice pone la prueba en rojo nombrándola.
    /// </summary>
    [Fact]
    public async Task CadaRamaDelInspectorTieneIndiceDeSoporteOQuedaReportada()
    {
        // Congelado a partir de la corrida de esta slice. Vacío = todas las ramas (hoja y puente)
        // tienen un índice cuya PRIMERA columna participa del predicado.
        string[] hallazgosEsperados = [];

        await using var db = ContextoDePlataforma();

        var primerasColumnas = new HashSet<string>(StringComparer.Ordinal);

        var conexion = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();

        await using (var comando = conexion.CreateCommand())
        {
            comando.CommandText =
                """
                SELECT c.relname, a.attname
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = i.indkey[0]
                WHERE c.relnamespace = 'public'::regnamespace
                """;

            await using var lector = await comando.ExecuteReaderAsync();

            while (await lector.ReadAsync())
            {
                primerasColumnas.Add($"{lector.GetString(0)}.{lector.GetString(1)}");
            }
        }

        Assert.NotEmpty(primerasColumnas);

        var hallazgos = new List<string>();

        foreach (var ancla in new[] { typeof(Tenant), typeof(Empresa), typeof(PuntoVenta), typeof(Usuario) })
        {
            foreach (var rama in InventarioDeDependientes.Construir(db.Model, ancla))
            {
                var relaciones = new List<(string Tabla, IReadOnlyList<string> Columnas)>
                {
                    (rama.Tabla, rama.Columnas)
                };

                if (rama.Puente is { } puente)
                {
                    relaciones.Add((puente.Tabla, puente.ColumnasHaciaElAncla));
                }

                foreach (var (tabla, columnas) in relaciones)
                {
                    if (!columnas.Any(columna => primerasColumnas.Contains($"{tabla}.{columna}")))
                    {
                        hallazgos.Add($"{ancla.Name} | {rama.Etiqueta} | {tabla}({string.Join(',', columnas)})");
                    }
                }
            }
        }

        var ordenados = hallazgos.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        foreach (var hallazgo in ordenados)
        {
            salida.WriteLine($"SIN ÍNDICE DE SOPORTE: {hallazgo}");
        }

        Assert.Equal(hallazgosEsperados, ordenados);
    }

    // =========================================================================================
    // judgment-day ronda 2 — reintentos, carreras y atribución del bloqueo
    // =========================================================================================

    /// <summary>
    /// Da de baja el tenant entero —por el camino REAL, desde otro contexto y otra transacción— en
    /// el instante en que la transacción de la prueba se abre: o sea DESPUÉS de la lectura previa
    /// del sujeto y ANTES de que se tome el lock. Es el rendezvous exacto de la carrera de R2-2.
    ///
    /// El punto de enganche es <c>TransactionStarted</c> y no un comando, a propósito: el
    /// <c>pg_advisory_xact_lock</c> se emite por ADO crudo y NO pasa por
    /// <see cref="DbCommandInterceptor"/> (<c>mutation-proof-tests</c> regla 13), y engancharse a
    /// la relectura misma haría que el mutante —que borra la relectura— no dispare la carrera y la
    /// prueba se ponga roja por el motivo equivocado. La apertura de la transacción existe en las
    /// dos versiones.
    /// </summary>
    private sealed class InterceptorQueDaDeBajaElTenantAlAbrir(Func<Task> cascada) : DbTransactionInterceptor
    {
        private int disparos;

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref disparos) == 1)
            {
                await cascada();
            }

            return await base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }
    }

    private static ServicioDeOrganizacion ServicioDeOrganizacionSobre(
        WaysDbContext db, DateTimeOffset momento, int idActor)
    {
        var contexto = new ContextoFijo(RolConocido.Root, idActor, idTenant: null);
        var reloj = new RelojFijo(momento);

        return new ServicioDeOrganizacion(
            db, reloj, contexto, new InspectorDeUso(db), new ServicioDeAuditoria(db, reloj, contexto));
    }

    /// <summary>
    /// R2-1 (judgment-day ronda 2, jueces A y B) — LA CLÁUSULA: las bajas corren bajo
    /// <c>FabricaDeEstrategiaSinReintento</c> y no bajo <c>CreateExecutionStrategy</c>.
    ///
    /// <c>EnableRetryOnFailure(5)</c> es global. <c>ServicioDeAuditoria.Registrar</c> hace
    /// <c>Add</c> de una instancia NUEVA cada vez que se lo llama, y un
    /// <c>SaveChangesAsync</c> fallido deja las del intento anterior en <c>Added</c>: un reintento
    /// no rehace el rastro, lo DUPLICA, y el segundo intento inserta 2N filas por una sola baja.
    /// Peor: la relectura del tenant resuelve contra el identity map, así que el intento 2 lee
    /// <c>estadoAnterior</c> de la instancia que el intento 1 ya mutó y la fila duplicada de
    /// <c>tenant.baja</c> afirma que el estado previo ya era <c>baja</c> — un rastro que miente.
    ///
    /// El contexto tiene los reintentos ACTIVOS (<c>CrearContextoDeAplicacionConReintentos</c>):
    /// la estrategia reintentable existe y la única razón por la que no reintenta es la que elige
    /// el código de producción. Se afirman tres valores discriminantes: el error transitorio llega
    /// TAL CUAL al llamador, el <c>INSERT</c> del rastro se intentó UNA sola vez, y la base quedó
    /// intacta. Después, sin interceptor, la MISMA baja escribe sus cuatro filas exactas con
    /// <c>estado: "activo"</c> del lado anterior — el valor que el reintento falsificaba.
    /// </summary>
    [Fact]
    public async Task UnaFallaTransitoriaSobreElRastroNoSeReintentaYNoDuplicaNiFalsificaNada()
    {
        var sembrado = await AprovisionarAsync("reintento-rastro");
        var idRoot = await IdDelRootAsync();
        var ultimoId = await UltimoIdDeAuditoriaAsync();
        var momento = sembrado.Ancla.AddHours(1);

        // 40001 (serialization_failure) es transitorio para NpgsqlRetryingExecutionStrategy: es
        // exactamente la clase de falla por la que EnableRetryOnFailure existe.
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("auditoria", "40001");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(
            TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = ServicioDeOrganizacionSobre(db, momento, idRoot);

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.EliminarTenantAsync(sembrado.IdTenant));

            var postgres = ErrorDePostgres(error);
            Assert.Equal("40001", postgres.SqlState);
        }

        // UNA sola vez: la unidad no se reintentó. Con la estrategia reintentable serían dos, y el
        // segundo intento comitearía 8 filas de rastro en vez de 4.
        Assert.Equal(1, interceptor.Intentos);

        Assert.Empty(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
        Assert.Equal(EstadoTenant.Activo, (await LeerTenantAsync(sembrado.IdTenant)).Estado);
        Assert.Null((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerUsuarioAsync(sembrado.IdAdmin)).DeletedAt);

        // Y la misma baja, sin la falla inyectada, escribe EXACTAMENTE su rastro: cuatro filas, una
        // sola `tenant.baja`, y su estado anterior es el real.
        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma))
        {
            await ServicioDeOrganizacionSobre(db, momento, idRoot).EliminarTenantAsync(sembrado.IdTenant);
        }

        var rastro = await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant);

        Assert.Equal(4, rastro.Count);
        Assert.Equal(
            ["empresa.baja", "pv.baja", "tenant.baja", "usuario.baja"],
            rastro.Select(f => f.Accion).Order(StringComparer.Ordinal));

        var delTenant = Assert.Single(rastro, f => f.Accion == "tenant.baja");
        Assert.Equal(
            "activo",
            JsonDocument.Parse(delTenant.ValorAnterior!).RootElement.GetProperty("estado").GetString());
        Assert.Equal(
            "baja",
            JsonDocument.Parse(delTenant.ValorNuevo).RootElement.GetProperty("estado").GetString());
    }

    /// <summary>
    /// R2-9 (judgment-day ronda 2, juez B), la mitad que faltaba del lado negativo: una falla NO
    /// transitoria DESPUÉS de que las filas de rastro ya están encoladas no persiste ni una.
    ///
    /// La prueba de rechazo por guard cubre solo la dirección que no puede fallar (el guard tira
    /// ANTES de encolar nada). Acá las cuatro filas ya están en el <c>ChangeTracker</c> y los
    /// cuatro <c>UPDATE</c> ya están en el mismo lote: lo que sostiene la atomicidad es la
    /// transacción, y esto es lo que la ejerce. <c>23505</c> no es transitorio, así que tampoco hay
    /// reintento del que sospechar.
    /// </summary>
    [Fact]
    public async Task UnaFallaNoTransitoriaConElRastroYaEncoladoNoPersisteNadaDeLaCascada()
    {
        var sembrado = await AprovisionarAsync("rastro-atomico");
        var idRoot = await IdDelRootAsync();
        var ultimoId = await UltimoIdDeAuditoriaAsync();

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("auditoria", "23505");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(
            TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = ServicioDeOrganizacionSobre(db, sembrado.Ancla.AddHours(1), idRoot);

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.EliminarTenantAsync(sembrado.IdTenant));

            Assert.Equal("23505", ErrorDePostgres(error).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant));

        // La cascada entera revirtió: ninguna de las cuatro entidades quedó estampada.
        Assert.Null((await LeerTenantAsync(sembrado.IdTenant)).DeletedAt);
        Assert.Equal(EstadoTenant.Activo, (await LeerTenantAsync(sembrado.IdTenant)).Estado);
        Assert.Null((await LeerEmpresaAsync(sembrado.IdEmpresa)).DeletedAt);
        Assert.Null((await LeerPuntoVentaAsync(sembrado.IdPuntoVenta)).DeletedAt);
        Assert.Null((await LeerUsuarioAsync(sembrado.IdAdmin)).DeletedAt);
    }

    private static PostgresException ErrorDePostgres(Exception error)
    {
        for (Exception? actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException($"La excepción no envuelve ninguna PostgresException: {error}");
    }

    /// <summary>
    /// R2-2 (judgment-day ronda 2, jueces A y B) — LA CLÁUSULA: la relectura del sujeto BAJO el
    /// lock en <c>ServicioDeUsuarios.EliminarAsync</c>.
    ///
    /// La carrera es real y esta prueba la fuerza: la cascada del tenant corre entre la lectura
    /// previa (que ve la cuenta viva) y el lock, y gana. Sin la relectura, el perdedor estampa un
    /// <c>deleted_at</c> NUEVO sobre una fila ya dada de baja y escribe un SEGUNDO
    /// <c>usuario.baja</c>: el instante compartido que hace exacto al restore de la cascada
    /// (<c>UPDATE ... SET deleted_at = NULL WHERE deleted_at = '&lt;instante&gt;'</c>) queda roto
    /// justo en la cuenta re-estampada, y el rastro dice que la cuenta se dio de baja dos veces.
    ///
    /// Va por DEBAJO del pre-chequeo, que es el confound de manual (<c>mutation-proof-tests</c>
    /// regla 3): un test secuencial —dar de baja el tenant y después pedir la baja del usuario—
    /// muere en la lectura previa, así que el mutante que borra la relectura sobreviviría. El
    /// rendezvous se engancha en la APERTURA de la transacción, que existe con relectura y sin
    /// ella.
    ///
    /// Valores discriminantes: el 404, el <c>deleted_at</c> EXACTO de la cascada (no uno nuevo) y
    /// UNA sola fila <c>usuario.baja</c>, la de la cascada, marcada <c>por_cascada</c>.
    /// </summary>
    [Fact]
    public async Task LaBajaDeUsuarioQuePierdeLaCarreraContraLaCascadaEs404YNoRePisaElInstante()
    {
        var sembrado = await AprovisionarAsync("carrera-usuario");
        var idRoot = await IdDelRootAsync();
        var ultimoId = await UltimoIdDeAuditoriaAsync();
        var momentoDeLaCascada = sembrado.Ancla.AddHours(1);
        var momentoDeLaBaja = sembrado.Ancla.AddHours(2);

        var interceptor = new InterceptorQueDaDeBajaElTenantAlAbrir(async () =>
        {
            await using var otro = ContextoDePlataforma();
            await ServicioDeOrganizacionSobre(otro, momentoDeLaCascada, idRoot)
                .EliminarTenantAsync(sembrado.IdTenant);
        });

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma, interceptor))
        await using (var dbPlataforma = ContextoDePlataforma())
        {
            var contexto = new ContextoFijo(RolConocido.Root, idRoot, idTenant: null);
            var reloj = new RelojFijo(momentoDeLaBaja);

            var servicio = new ServicioDeUsuarios(
                db, dbPlataforma, new Ways.Infrastructure.Seguridad.HasheadorPbkdf2(), reloj, contexto,
                new ServicioDeAuditoria(db, reloj, contexto), new InspectorDeUso(db));

            var error = await ErrorDeAsync(() => servicio.EliminarAsync(sembrado.IdAdmin));

            Assert.Equal(404, error.EstadoHttp);
            Assert.Equal("no_encontrado", error.Codigo);
        }

        // El instante es el de la CASCADA, no el de la baja perdedora: nadie re-estampó.
        var usuario = await LeerUsuarioAsync(sembrado.IdAdmin);
        Assert.Equal(AMicrosegundos(momentoDeLaCascada), AMicrosegundos(usuario.DeletedAt!.Value));
        Assert.NotEqual(AMicrosegundos(momentoDeLaBaja), AMicrosegundos(usuario.DeletedAt!.Value));

        var rastro = await RastroPosteriorAAsync(ultimoId, sembrado.IdTenant);
        var bajasDeLaCuenta = rastro.Where(f => f.Accion == "usuario.baja" && f.IdEntidad == sembrado.IdAdmin).ToList();

        var fila = Assert.Single(bajasDeLaCuenta);
        Assert.True(PorCascadaDe(fila.ValorNuevo));
        Assert.Equal(AMicrosegundos(momentoDeLaCascada), DeletedAtDe(fila.ValorNuevo));
    }

    /// <summary>
    /// R2-6 (judgment-day ronda 2, juez A) — LA CLÁUSULA: el inspector proyecta la ETIQUETA DE LA
    /// RAMA, así que la copia del 409 atribuye el bloqueo donde la fila realmente vive.
    ///
    /// <c>parametros</c> es la hoja MIXTA: llega a una empresa por una rama directa
    /// (<c>id_empresa</c>, la fila de nivel empresa) y por el puente de sus puntos de venta
    /// (<c>id_punto_venta</c>). Con la hoja pelada, la redacción tenía que adivinar, y las dos
    /// reglas anteriores adivinaban mal en direcciones opuestas: la ronda 1 afirmaba "en sus
    /// puntos de venta" incluso sobre una fila de nivel EMPRESA, que es lo que se prueba acá.
    ///
    /// Las dos direcciones en el mismo test, sobre el mismo tenant: la fila de nivel empresa se
    /// nombra pelada, y una hoja a la que SOLO se llega por el puente (<c>turnos_caja</c>) sigue
    /// nombrando el puente. Una sola de las dos mitades la pasaría cualquier implementación que
    /// devolviera siempre lo mismo.
    /// </summary>
    [Fact]
    public async Task LaCopiaDelBloqueoAtribuyeLaFilaDeEmpresaYLaDeSuPuntoDeVentaPorSeparado()
    {
        var sembrado = await AprovisionarAsync("atribucion-mixta");
        var despues = sembrado.Ancla.AddMinutes(1);

        var conParametro = await SembrarEmpresaAsync(sembrado.IdTenant, "Con parámetro SRL", despues);
        await SembrarPuntoVentaAsync(sembrado.IdTenant, conParametro.Id, "Local del parámetro", despues);

        var conTurno = await SembrarEmpresaAsync(sembrado.IdTenant, "Con turno SRL", despues);
        var puntoDelTurno = await SembrarPuntoVentaAsync(
            sembrado.IdTenant, conTurno.Id, "Local del turno", despues);

        // Estrictamente posterior al ancla de SU empresa, si no la rama Marcado no bloquea.
        var propio = despues.AddMinutes(1);

        await using (var db = ContextoDePlataforma())
        {
            db.Parametros.Add(new Parametro
            {
                IdTenant = sembrado.IdTenant,
                IdEmpresa = conParametro.Id,
                IdPuntoVenta = null,
                Clave = "clave.de.la.prueba",
                Valor = "\"x\"",
                CreatedAt = propio,
                UpdatedAt = propio
            });
            await db.SaveChangesAsync();
        }

        await SembrarTurnoAsync(sembrado.IdTenant, puntoDelTurno.Id, sembrado.IdAdmin, propio);

        await ConServicioAsync(null, async servicio =>
        {
            // Nivel EMPRESA: la fila NO vive en ningún punto de venta y la copia no lo puede decir.
            var porParametro = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(conParametro.Id));

            Assert.Equal("empresa_en_uso", porParametro.Codigo);
            Assert.Contains("parámetros", porParametro.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("en sus", porParametro.Message, StringComparison.Ordinal);

            // Nivel PUNTO DE VENTA, hoja a la que solo se llega por el puente: se sigue nombrando.
            var porTurno = await ErrorDeAsync(() => servicio.EliminarEmpresaAsync(conTurno.Id));

            Assert.Equal("empresa_en_uso", porTurno.Codigo);
            Assert.Contains("turnos de caja en sus puntos de venta", porTurno.Message, StringComparison.Ordinal);
        });
    }
}
