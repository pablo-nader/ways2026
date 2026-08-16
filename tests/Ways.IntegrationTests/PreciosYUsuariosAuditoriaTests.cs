using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 2 (tasks 2.8-2.21): <c>precio.cambio</c> y los cinco
/// <c>usuario.*</c> contra Postgres real. Igual que <c>AuditoriaEscrituraTests</c> (Slice 1),
/// invoca <c>ServicioDePrecios</c>/<c>ServicioDeUsuarios</c> DIRECTAMENTE (sin HTTP) con
/// <see cref="RelojFijo"/>/<see cref="ContextoFijo"/> — necesario para forzar un
/// <c>contexto.UsuarioId</c> inexistente (fail-closed, design decisión 10) y para fijar el reloj
/// (Orchestrator Decision 12, <c>2026-08-14T12:00:00Z</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PreciosYUsuariosAuditoriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";
    private const string PasswordUsuario = "una-contraseña-larga";
    private static readonly DateTimeOffset MomentoFijo = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant, int usuarioId, RolConocido rol = RolConocido.Admin)
        : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public RolConocido Rol => rol;
        public int? IdTenant => idTenant;
    }

    // ---- provisioning ------------------------------------------------------------------------

    private async Task<(int IdTenant, int IdArea, int IdAlicuotaIva, int IdListaGeneral, int IdActorAdmin)>
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
            .Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        return (resultado.IdTenant, area.Id, idAlicuotaIva, idListaGeneral, resultado.IdUsuarioAdmin);
    }

    private async Task<int> SembrarArticuloAsync(int idTenant, int idArea, int idAlicuotaIva, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarUsuarioAsync(
        int? idTenant, string usuario, string mail, RolConocido rol = RolConocido.Vendedor,
        EstadoUsuario estado = EstadoUsuario.Activo)
    {
        var tenantActual = idTenant is int id
            ? new TenantActualFijo(ModoDeAcceso.Tenant, id)
            : TenantActualFijo.Plataforma;

        await using var db = fixture.CrearContextoDeAplicacion(tenantActual);
        var hasheador = new HasheadorPbkdf2();
        var ahora = DateTimeOffset.UtcNow;

        var entidad = new Domain.Usuarios.Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = usuario,
            Mail = mail,
            RolId = (int)rol,
            Estado = estado,
            PasswordHash = hasheador.Hashear(PasswordUsuario),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(entidad);
        await db.SaveChangesAsync();

        return entidad.Id;
    }

    /// <summary>El id de un usuario existente de plataforma (root), válido como actor cuando la
    /// prueba necesita un <c>id_actor</c> real fuera de un tenant (task 2.20).</summary>
    private async Task<int> ObtenerIdDeUsuarioRootAsync()
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra root)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Usuarios.Where(u => u.Mail == MailRoot).Select(u => u.Id).FirstAsync();
    }

    // ---- factories de servicio ----------------------------------------------------------------

    private (WaysDbContext Db, ServicioDePrecios Servicio) CrearServicioDePrecios(
        int idTenant, int idActor, IRelojDelSistema reloj)
    {
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var contexto = new ContextoFijo(idTenant, idActor);
        var auditoria = new ServicioDeAuditoria(db, reloj, contexto);

        return (db, new ServicioDePrecios(db, reloj, contexto, auditoria));
    }

    private (WaysDbContext Db, ServicioDeUsuarios Servicio) CrearServicioDeUsuarios(
        int? idTenant, int idActor, IRelojDelSistema reloj, RolConocido rolActor = RolConocido.Admin)
    {
        var tenantActual = idTenant is int id ? new TenantActualFijo(ModoDeAcceso.Tenant, id) : TenantActualFijo.Plataforma;
        var db = fixture.CrearContextoDeAplicacion(tenantActual);
        var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var contexto = new ContextoFijo(idTenant, idActor, rolActor);
        var auditoria = new ServicioDeAuditoria(db, reloj, contexto);

        return (db, new ServicioDeUsuarios(db, dbPlataforma, new HasheadorPbkdf2(), reloj, contexto, auditoria));
    }

    // ---- helpers de lectura --------------------------------------------------------------------

    private static (JsonElement? Anterior, JsonElement Nuevo) Parsear(Domain.Auditoria.Auditoria fila) => (
        fila.ValorAnterior is null ? null : JsonDocument.Parse(fila.ValorAnterior).RootElement.Clone(),
        JsonDocument.Parse(fila.ValorNuevo).RootElement.Clone());

    // ---- precio.cambio (task 2.8, + evidencia cross-slice de la mutación 1.27) ----------------

    /// <summary>Task 2.8 — cobertura de <c>precio.cambio</c>: exactamente una fila, ambos
    /// payloads completos, actor identificado. También el primer slot donde la evidencia de la
    /// mutación 1.27 (<c>id_actor</c> hardcodeado) se puede colectar: un literal en vez de
    /// <c>contexto.UsuarioId</c> haría que <see cref="idActorAdmin"/> nunca aparezca.</summary>
    [Fact]
    public async Task PrecioCambioEscribeUnaFilaConPayloadCompletoYActorIdentificado()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(PrecioCambioEscribeUnaFilaConPayloadCompletoYActorIdentificado));
        var idArticulo = await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "art-cobertura");

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDePrecios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        await servicio.EstablecerPrecioAsync(idArticulo, new AltaPrecio(idListaGeneral, 100m));

        var fila = await db.Auditoria.Where(a => a.IdEntidad == idArticulo && a.Entidad == "articulo").SingleAsync();

        Assert.Equal("precio.cambio", fila.Accion);
        Assert.Equal(idActorAdmin, fila.IdActor);
        Assert.Null(fila.IdPuntoVenta);
        Assert.Null(fila.ValorAnterior);

        var (_, nuevo) = Parsear(fila);
        Assert.Equal(idListaGeneral, nuevo.GetProperty("id_lista_precio").GetInt32());
        Assert.Equal(100m, nuevo.GetProperty("monto").GetDecimal());
        Assert.Equal(MomentoFijo, nuevo.GetProperty("vigente_desde").GetDateTimeOffset());
    }

    /// <summary>Task 2.9 — el reemplazo de una fila pendiente (que cierra la pendiente Y
    /// re-cierra su predecesor) escribe UNA sola fila de auditoría, no dos: la tercera llamada de
    /// abajo toca dos filas de <c>precios</c> (la pendiente + el predecesor) pero es una sola
    /// operación de servicio. Mata la mutación "una llamada por fila cerrada".</summary>
    [Fact]
    public async Task PrecioCambioQueCierraPredecesorEscribeUnaSolaFila()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(PrecioCambioQueCierraPredecesorEscribeUnaSolaFila));
        var idArticulo = await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "art-predecesor");

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDePrecios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        // 1) Precio inicial, vigente ahora.
        await servicio.EstablecerPrecioAsync(idArticulo, new AltaPrecio(idListaGeneral, 100m));

        // 2) Un precio programado a futuro cierra el (1) — todavía sin predecesor pendiente.
        await servicio.ProgramarPrecioAsync(
            idArticulo, new ProgramarPrecio(idListaGeneral, 150m, MomentoFijo.AddDays(1)));

        // 3) Reemplazo del programado (2): cierra (2) en su ventana muerta Y re-cierra (1), el
        // predecesor real — UNA sola llamada de servicio, UNA sola fila de auditoría nueva.
        await servicio.ProgramarPrecioAsync(
            idArticulo,
            new ProgramarPrecio(idListaGeneral, 160m, MomentoFijo.AddDays(2), ConfirmarReemplazo: true));

        var total = await db.Auditoria.CountAsync(a => a.IdEntidad == idArticulo && a.Entidad == "articulo");
        Assert.Equal(3, total); // una por cada una de las tres llamadas de servicio, no cuatro.
    }

    /// <summary>Task 2.11 / mutation target 2.10 — fail-closed sobre precios: un actor
    /// inexistente hace que el INSERT de auditoría (encolado en el MISMO <c>SaveChangesAsync</c>
    /// que el <c>Precio</c> nuevo) dispare <c>fk_auditoria_actor</c>, y la transacción entera
    /// revierte — ni la fila de <c>precios</c> ni la de <c>auditoria</c> quedan escritas.</summary>
    [Fact]
    public async Task FallaDeAuditoriaBloqueaElCambioDePrecio()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, _) =
            await AprovisionarTenantAsync(nameof(FallaDeAuditoriaBloqueaElCambioDePrecio));
        var idArticulo = await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "art-fail-closed");

        var reloj = new RelojFijo(MomentoFijo);
        const int idActorInexistente = int.MaxValue;
        var (db, servicio) = CrearServicioDePrecios(idTenant, idActorInexistente, reloj);
        await using var _ = db;

        var excepcion = await Assert.ThrowsAsync<DbUpdateException>(
            () => servicio.EstablecerPrecioAsync(idArticulo, new AltaPrecio(idListaGeneral, 100m)));

        var postgres = Assert.IsType<PostgresException>(excepcion.InnerException);
        Assert.Equal("23503", postgres.SqlState);
        Assert.Equal("fk_auditoria_actor", postgres.ConstraintName);

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        Assert.Equal(0, await lectura.Precios.CountAsync(p => p.IdArticulo == idArticulo));
        Assert.Equal(0, await lectura.Auditoria.CountAsync(a => a.IdEntidad == idArticulo && a.Entidad == "articulo"));
    }

    // ---- usuario.alta (task 2.13, mutation target 2.12) ----------------------------------------

    /// <summary>Task 2.13 / mutation target 2.12 — fail-closed sobre <c>usuario.alta</c>: un
    /// actor inexistente hace fallar el SEGUNDO <c>SaveChangesAsync</c> (el de auditoría, con el
    /// id ya generado); la transacción explícita revierte el PRIMER flush también, así que el
    /// usuario nunca queda creado — con dos <c>SaveChangesAsync</c> sueltos (mutation target
    /// 2.12), el primero ya habría comiteado antes de que el segundo fallara.</summary>
    [Fact]
    public async Task FallaDeAuditoriaBloqueaElAltaDeUsuario()
    {
        var (idTenant, _, _, _, _) = await AprovisionarTenantAsync(nameof(FallaDeAuditoriaBloqueaElAltaDeUsuario));

        var reloj = new RelojFijo(MomentoFijo);
        const int idActorInexistente = int.MaxValue;
        var (db, servicio) = CrearServicioDeUsuarios(idTenant, idActorInexistente, reloj);
        await using var _ = db;

        var datos = new CrearUsuario("nuevo-vendedor", "nuevo-vendedor@ways.test", (int)RolConocido.Vendedor, PasswordUsuario);

        var excepcion = await Assert.ThrowsAsync<DbUpdateException>(() => servicio.CrearAsync(datos));

        var postgres = Assert.IsType<PostgresException>(excepcion.InnerException);
        Assert.Equal("23503", postgres.SqlState);
        Assert.Equal("fk_auditoria_actor", postgres.ConstraintName);

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        Assert.Equal(0, await lectura.Usuarios.CountAsync(u => u.NombreUsuario == "nuevo-vendedor"));
        Assert.Equal(0, await lectura.Auditoria.CountAsync(a => a.Accion == "usuario.alta"));
    }

    // ---- usuario.actualizacion (task 2.15, mutation targets 2.14/2.16) -------------------------

    /// <summary>Task 2.15 / mutation target 2.14 — cobertura de <c>usuario.actualizacion</c>:
    /// ambos payloads llevan las cuatro columnas editables con valores genuinamente distintos
    /// (<c>mutation-proof-tests</c> regla 6) — mueve la captura después de la mutación y
    /// <c>valor_anterior == valor_nuevo</c>.</summary>
    [Fact]
    public async Task UsuarioActualizacionEscribeValoresDistintosPrePostMutacion()
    {
        var (idTenant, _, _, _, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(UsuarioActualizacionEscribeValoresDistintosPrePostMutacion));
        var idObjetivo = await SembrarUsuarioAsync(idTenant, "vendedor-original", "original@ways.test");

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDeUsuarios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        await servicio.ActualizarAsync(
            idObjetivo,
            new ActualizarUsuario("vendedor-nuevo", "nuevo@ways.test", (int)RolConocido.Supervisor, EstadoUsuario.Activo));

        var fila = await db.Auditoria.Where(a => a.IdEntidad == idObjetivo && a.Accion == "usuario.actualizacion").SingleAsync();
        var (anterior, nuevo) = Parsear(fila);

        Assert.NotNull(anterior);
        Assert.Equal("vendedor-original", anterior!.Value.GetProperty("usuario").GetString());
        Assert.Equal("original@ways.test", anterior.Value.GetProperty("mail").GetString());
        Assert.Equal((int)RolConocido.Vendedor, anterior.Value.GetProperty("id_rol").GetInt32());
        Assert.Equal("activo", anterior.Value.GetProperty("estado").GetString());

        Assert.Equal("vendedor-nuevo", nuevo.GetProperty("usuario").GetString());
        Assert.Equal("nuevo@ways.test", nuevo.GetProperty("mail").GetString());
        Assert.Equal((int)RolConocido.Supervisor, nuevo.GetProperty("id_rol").GetInt32());
        Assert.Equal("activo", nuevo.GetProperty("estado").GetString());

        // mutation-proof-tests regla 6: ningún valor coincide entre anterior/nuevo en ninguna
        // de las cuatro columnas — un "anterior == nuevo" (mutación 2.14) no podría pasar esto.
        Assert.NotEqual(anterior.Value.GetProperty("usuario").GetString(), nuevo.GetProperty("usuario").GetString());
        Assert.NotEqual(anterior.Value.GetProperty("mail").GetString(), nuevo.GetProperty("mail").GetString());
        Assert.NotEqual(anterior.Value.GetProperty("id_rol").GetInt32(), nuevo.GetProperty("id_rol").GetInt32());
    }

    /// <summary>Mutation target 2.16 — <c>monto</c> en el <c>SELECT</c> de
    /// <c>BuscarFilaAbiertaAsync</c>: sin él (o hardcodeado a 0), el <c>valor_anterior.monto</c>
    /// de un segundo cambio de precio no sería el monto REAL del primero.</summary>
    [Fact]
    public async Task SegundoCambioDePrecioLlevaElMontoAnteriorReal()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(SegundoCambioDePrecioLlevaElMontoAnteriorReal));
        var idArticulo = await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "art-monto-anterior");

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDePrecios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        await servicio.EstablecerPrecioAsync(idArticulo, new AltaPrecio(idListaGeneral, 100m));
        await servicio.ProgramarPrecioAsync(idArticulo, new ProgramarPrecio(idListaGeneral, 130m, MomentoFijo.AddDays(1)));

        var segunda = await db.Auditoria
            .Where(a => a.IdEntidad == idArticulo && a.Entidad == "articulo")
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        var (anterior, _) = Parsear(segunda);
        Assert.NotNull(anterior);
        Assert.Equal(100m, anterior!.Value.GetProperty("monto").GetDecimal());
    }

    // ---- usuario.desbloqueo (task 2.17) ---------------------------------------------------------

    /// <summary>Task 2.17 — cobertura de <c>usuario.desbloqueo</c>: el ANTES es el
    /// <c>estado</c> REAL (bloqueado), no un literal asumido.</summary>
    [Fact]
    public async Task UsuarioDesbloqueoEscribeEstadoRealPreYPostDesbloqueo()
    {
        var (idTenant, _, _, _, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(UsuarioDesbloqueoEscribeEstadoRealPreYPostDesbloqueo));
        var idObjetivo = await SembrarUsuarioAsync(
            idTenant, "vendedor-bloqueado", "bloqueado@ways.test", estado: EstadoUsuario.Bloqueado);

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDeUsuarios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        await servicio.DesbloquearAsync(idObjetivo);

        var fila = await db.Auditoria.Where(a => a.IdEntidad == idObjetivo && a.Accion == "usuario.desbloqueo").SingleAsync();
        var (anterior, nuevo) = Parsear(fila);

        Assert.NotNull(anterior);
        Assert.Equal("bloqueado", anterior!.Value.GetProperty("estado").GetString());
        Assert.Equal("activo", nuevo.GetProperty("estado").GetString());
    }

    // ---- usuario.password (task 2.18) -----------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsuarioPasswordEscribeHechoSinHashConValorAnteriorNulo(bool porElPropioUsuario)
    {
        var (idTenant, _, _, _, idActorAdmin) =
            await AprovisionarTenantAsync(
                nameof(UsuarioPasswordEscribeHechoSinHashConValorAnteriorNulo) + porElPropioUsuario);
        var idObjetivo = await SembrarUsuarioAsync(
            idTenant, "vendedor-password", $"password-owner-{porElPropioUsuario}@ways.test");

        var reloj = new RelojFijo(MomentoFijo);
        var idActor = porElPropioUsuario ? idObjetivo : idActorAdmin;
        var rolActor = porElPropioUsuario ? RolConocido.Vendedor : RolConocido.Admin;
        var (db, servicio) = CrearServicioDeUsuarios(idTenant, idActor, reloj, rolActor);
        await using var _ = db;

        await servicio.CambiarPasswordAsync(idObjetivo, new CambiarPassword("una-contraseña-larguísima-2"));

        var fila = await db.Auditoria.Where(a => a.IdEntidad == idObjetivo && a.Accion == "usuario.password").SingleAsync();
        var (anterior, nuevo) = Parsear(fila);

        Assert.Null(anterior);
        Assert.Equal(porElPropioUsuario, nuevo.GetProperty("por_el_propio_usuario").GetBoolean());
    }

    // ---- denylist real (task 2.19) ---------------------------------------------------------------

    /// <summary>Task 2.19 — denylist real: el texto CRUDO de <c>valor_anterior</c>/
    /// <c>valor_nuevo</c> (no un DTO) para <c>usuario.actualizacion</c> y <c>usuario.password</c>
    /// nunca contiene el hash conocido ni la subcadena "password" como clave.</summary>
    [Fact]
    public async Task NingunPayloadDeUsuariosContieneHashPasswordNiSuSubcadena()
    {
        var (idTenant, _, _, _, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(NingunPayloadDeUsuariosContieneHashPasswordNiSuSubcadena));

        await using var siembra = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var hasheador = new HasheadorPbkdf2();
        var hashConocido = hasheador.Hashear(PasswordUsuario);
        var ahora = DateTimeOffset.UtcNow;

        var objetivo = new Domain.Usuarios.Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "vendedor-denylist",
            Mail = "denylist@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hashConocido,
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.Usuarios.Add(objetivo);
        await siembra.SaveChangesAsync();

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDeUsuarios(idTenant, idActorAdmin, reloj);
        await using var _ = db;

        await servicio.ActualizarAsync(
            objetivo.Id,
            new ActualizarUsuario("vendedor-denylist-2", "denylist2@ways.test", (int)RolConocido.Vendedor, EstadoUsuario.Activo));
        await servicio.CambiarPasswordAsync(objetivo.Id, new CambiarPassword("otra-contraseña-larga"));

        var filas = await db.Auditoria
            .Where(a => a.IdEntidad == objetivo.Id && (a.Accion == "usuario.actualizacion" || a.Accion == "usuario.password"))
            .ToListAsync();

        Assert.Equal(2, filas.Count);

        foreach (var fila in filas)
        {
            var texto = (fila.ValorAnterior ?? string.Empty) + fila.ValorNuevo;
            Assert.DoesNotContain(hashConocido, texto);
            Assert.DoesNotContain("password", texto, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- límite registrado: cuenta de plataforma (task 2.20) -------------------------------------

    /// <summary>Task 2.20 — editar una cuenta de plataforma (<c>IdTenant IS NULL</c>) NO escribe
    /// ninguna fila de auditoría, y la operación de negocio se completa igual (design "Sujeto sin
    /// tenant").</summary>
    [Fact]
    public async Task EdicionDeCuentaDePlataformaNoEscribeFilaDeAuditoria()
    {
        var idRoot = await ObtenerIdDeUsuarioRootAsync();
        var idPlataforma = await SembrarUsuarioAsync(
            idTenant: null, "staff-plataforma", "staff-plataforma@ways.test", RolConocido.Root);

        var reloj = new RelojFijo(MomentoFijo);
        var (db, servicio) = CrearServicioDeUsuarios(idTenant: null, idRoot, reloj, RolConocido.Root);
        await using var _ = db;

        // RolId sin cambios (Root → Root): esquiva ValidarPuedeAsignarRol("root no asignable").
        var actualizado = await servicio.ActualizarAsync(
            idPlataforma,
            new ActualizarUsuario("staff-plataforma-2", "staff-plataforma-2@ways.test", (int)RolConocido.Root, EstadoUsuario.Activo));

        Assert.Equal("staff-plataforma-2", actualizado.Usuario);

        var total = await db.Auditoria.CountAsync(a => a.IdEntidad == idPlataforma && a.Entidad == "usuario");
        Assert.Equal(0, total);
    }

    // ---- reloj fijo en las seis acciones de la slice (task 2.21) ---------------------------------

    /// <summary>Task 2.21 — <c>precio.cambio</c> y las cinco <c>usuario.*</c> estampan
    /// <c>creado_el</c> exactamente igual al reloj fijo — cierra la evidencia cross-slice de la
    /// mutación 1.27 (id_actor) en conjunto con la cobertura de arriba.</summary>
    [Fact]
    public async Task TodasLasSeisAccionesEstampanElRelojFijo()
    {
        var (idTenant, idArea, idAlicuotaIva, idListaGeneral, idActorAdmin) =
            await AprovisionarTenantAsync(nameof(TodasLasSeisAccionesEstampanElRelojFijo));
        var idArticulo = await SembrarArticuloAsync(idTenant, idArea, idAlicuotaIva, "art-reloj");
        var idObjetivo = await SembrarUsuarioAsync(idTenant, "vendedor-reloj", "reloj@ways.test");

        var reloj = new RelojFijo(MomentoFijo);

        var (dbPrecios, servicioDePrecios) = CrearServicioDePrecios(idTenant, idActorAdmin, reloj);
        await using (dbPrecios)
        {
            await servicioDePrecios.EstablecerPrecioAsync(idArticulo, new AltaPrecio(idListaGeneral, 100m));
        }

        var (dbUsuarios, servicioDeUsuarios) = CrearServicioDeUsuarios(idTenant, idActorAdmin, reloj);
        await using (dbUsuarios)
        {
            var altaDatos = new CrearUsuario("vendedor-alta-reloj", "alta-reloj@ways.test", (int)RolConocido.Vendedor, PasswordUsuario);
            var creado = await servicioDeUsuarios.CrearAsync(altaDatos);

            await servicioDeUsuarios.ActualizarAsync(
                idObjetivo, new ActualizarUsuario("vendedor-reloj-2", "reloj2@ways.test", (int)RolConocido.Vendedor, EstadoUsuario.Activo));
            await servicioDeUsuarios.CambiarPasswordAsync(idObjetivo, new CambiarPassword("otra-contraseña-larga-3"));
            await servicioDeUsuarios.DesbloquearAsync(idObjetivo);
            await servicioDeUsuarios.EliminarAsync(idObjetivo);

            var filaAlta = await dbUsuarios.Auditoria.Where(a => a.IdEntidad == creado.Id && a.Accion == "usuario.alta").SingleAsync();
            Assert.Null(filaAlta.ValorAnterior);
            var (_, nuevoAlta) = Parsear(filaAlta);
            Assert.Equal("vendedor-alta-reloj", nuevoAlta.GetProperty("usuario").GetString());
        }

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var creadosEl = await lectura.Auditoria.Where(a => a.IdTenant == idTenant).Select(a => a.CreadoEl).ToListAsync();

        Assert.Equal(6, creadosEl.Count); // precio.cambio + alta + actualizacion + password + desbloqueo + baja
        Assert.All(creadosEl, c => Assert.Equal(MomentoFijo, c));
    }
}
