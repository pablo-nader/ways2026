using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 5 (tasks 5.5-5.20): el lado de lectura —
/// <c>GET /api/auditoria</c> — contra Postgres real. Las filas de <c>auditoria</c> se siembran
/// DIRECTO por <c>db.Auditoria.Add</c> (design: "5 needs only 1 — the table and the writer's
/// read-side symmetry, not any call site — its integration tests seed auditoria rows directly"),
/// nunca vía los servicios de negocio de las slices 2-4: esta slice prueba el filtro/paginado/
/// autorización, no la escritura (ya cubierta en <c>AuditoriaEscrituraTests</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AuditoriaConsultaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";
    private const string PasswordUsuario = "una-contraseña-larga";
    private static readonly DateTimeOffset MomentoFijo = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta1, int IdPuntoVenta2,
        int IdActorAdmin, int IdActorX, int IdActorY,
        HttpClient Admin, HttpClient Root);

    private sealed record FilaRespuesta(
        long IdAuditoria, DateTimeOffset CreadoEl, string Accion, string Entidad, int IdEntidad,
        int IdActor, string? Actor, int? IdPuntoVenta, JsonElement? ValorAnterior, JsonElement ValorNuevo);

    private sealed record PaginaRespuesta(List<FilaRespuesta> Items, int Total, int Pagina, int Tamanio);

    // ---- provisioning -------------------------------------------------------------------------

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

        // idActorX/idActorY se siembran DIRECTO por DB (no vía POST /api/usuarios): un alta real
        // dispararía su propia fila usuario.alta auditada (slice 2, ya mergeada) y contaminaría
        // los conteos exactos que esta slice verifica. Lo mismo aplica a Supervisor/Vendedor — se
        // crean vía HTTP SOLO en los tests de autorización que los necesitan (5.17/5.18), nunca acá.
        var idActorX = await SembrarUsuarioAsync(resultado.IdTenant, $"{nombre}-x", $"{nombre}-x@ways.test".ToLowerInvariant());
        var idActorY = await SembrarUsuarioAsync(resultado.IdTenant, $"{nombre}-y", $"{nombre}-y@ways.test".ToLowerInvariant());

        var idPuntoVenta2 = await SembrarSegundoPuntoVentaAsync(resultado.IdTenant, resultado.IdEmpresa, nombre);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idPuntoVenta2,
            resultado.IdUsuarioAdmin, idActorX, idActorY,
            admin, root);
    }

    /// <summary>Usado SOLO por los tests de autorización (5.17/5.18): un alta real vía HTTP
    /// dispara su propia fila <c>usuario.alta</c> auditada — evitarlo en <see cref="PrepararAsync"/>
    /// es lo que mantiene los conteos exactos del resto de los tests sin ruido.</summary>
    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordUsuario));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarSegundoPuntoVentaAsync(int idTenant, int idEmpresa, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant, IdEmpresa = idEmpresa, Nombre = $"{nombre}-pv2", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    private async Task<int> SembrarUsuarioAsync(
        int? idTenant, string usuario, string mail, RolConocido rol = RolConocido.Vendedor, DateTimeOffset? deletedAt = null)
    {
        var tenantActual = idTenant is int id
            ? new TenantActualFijo(ModoDeAcceso.Tenant, id)
            : TenantActualFijo.Plataforma;

        await using var db = fixture.CrearContextoDeAplicacion(tenantActual);
        var hasheador = new HasheadorPbkdf2();
        var ahora = DateTimeOffset.UtcNow;

        var entidad = new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = usuario,
            Mail = mail,
            RolId = (int)rol,
            Estado = EstadoUsuario.Activo,
            PasswordHash = hasheador.Hashear(PasswordUsuario),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = deletedAt
        };
        db.Usuarios.Add(entidad);
        await db.SaveChangesAsync();

        return entidad.Id;
    }

    /// <summary>El id de un usuario existente de plataforma (root) — el actor de plataforma que
    /// ejercita el escenario "root leído por un Admin de tenant" (task 5.10).</summary>
    private async Task<int> ObtenerIdDeUsuarioRootAsync()
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra root)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Usuarios.Where(u => u.Mail == MailRoot).Select(u => u.Id).FirstAsync();
    }

    private async Task<long> SembrarFilaAsync(
        int idTenant, int? idPuntoVenta, int idActor, string accion, string entidad, int idEntidad,
        DateTimeOffset creadoEl, string? valorAnterior, string valorNuevo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var fila = new Domain.Auditoria.Auditoria
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdActor = idActor,
            Accion = accion,
            Entidad = entidad,
            IdEntidad = idEntidad,
            ValorAnterior = valorAnterior,
            ValorNuevo = valorNuevo,
            CreadoEl = creadoEl
        };
        db.Auditoria.Add(fila);
        await db.SaveChangesAsync();

        return fila.Id;
    }

    /// <summary>
    /// Escenario de filtros (tasks 5.11-5.16, 5.18): 8 filas, TODAS con fecha, acción, actor,
    /// entidad/id_entidad y PV distintos entre sí (<c>mutation-proof-tests</c> regla 6) — así
    /// ningún filtro puede devolver "de más" sin que el test lo note. Coincide con el escenario
    /// del spec (auditoria-de-operaciones: "Filtering by entidad + id_entidad..."): exactamente 3
    /// filas de articulo 41 (R1, R4, R5) y 2 de articulo 42 (R6, R7).
    /// </summary>
    private async Task<Dictionary<string, long>> SembrarEscenarioDeFiltrosAsync(Contexto ctx)
    {
        var ids = new Dictionary<string, long>();

        ids["R1"] = await SembrarFilaAsync(
            ctx.IdTenant, null, ctx.IdActorAdmin, "precio.cambio", "articulo", 41,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "{\"monto\":100}");
        ids["R2"] = await SembrarFilaAsync(
            // idEntidad = 41 a propósito, COLISIONANDO con R1/R4/R5 (articulo 41) bajo una
            // entidad distinta — sin esto, el clon entidad==null||... es indistinguible de
            // idEntidad==null||... (mutation-proof-tests regla 3: sin la colisión, idEntidad=41
            // ya identifica el mismo subconjunto por sí solo, así que mutar la cláusula de
            // entidad no cambiaría nada observable).
            ctx.IdTenant, ctx.IdPuntoVenta1, ctx.IdActorX, "venta.anulacion", "comprobante_venta", 41,
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), "{\"estado\":\"emitido\"}", "{\"estado\":\"anulado\"}");
        ids["R3"] = await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta2, ctx.IdActorY, "compra.anulacion", "comprobante_compra", 601,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "{\"estado\":\"confirmada\"}", "{\"estado\":\"anulada\"}");
        ids["R4"] = await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta1, ctx.IdActorX, "stock.ajuste", "articulo", 41,
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), "{\"cantidad\":5}", "{\"cantidad\":8}");
        ids["R5"] = await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta2, ctx.IdActorY, "stock.decomiso", "articulo", 41,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "{\"cantidad\":8}", "{\"cantidad\":6}");
        ids["R6"] = await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta1, ctx.IdActorAdmin, "stock.ajuste", "articulo", 42,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), "{\"cantidad\":1}", "{\"cantidad\":3}");
        ids["R7"] = await SembrarFilaAsync(
            ctx.IdTenant, ctx.IdPuntoVenta2, ctx.IdActorX, "stock.conteo", "articulo", 42,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), "{\"cantidad\":3}", "{\"cantidad\":3,\"movimientos_generados\":[]}");
        ids["R8"] = await SembrarFilaAsync(
            ctx.IdTenant, null, ctx.IdActorAdmin, "usuario.actualizacion", "usuario", 999,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), "{\"estado\":\"activo\"}", "{\"estado\":\"bloqueado\"}");

        return ids;
    }

    private static async Task<PaginaRespuesta> ConsultarAsync(HttpClient cliente, string queryString)
    {
        var respuesta = await cliente.GetAsync($"/api/auditoria{queryString}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<PaginaRespuesta>())!;
    }

    // ---- 5.11: desde/hasta ----------------------------------------------------------------------

    /// <summary>Judgment-day fix (juez B, slice 5 ronda 1, WARNING): el borde superior inclusivo
    /// de <c>Hasta</c> no estaba pinneado — ninguna fila caía EXACTAMENTE en el instante
    /// <c>hasta</c>, así que <c>&lt;=</c>→<c>&lt;</c> sobrevivía. La fila agregada acá cae justo
    /// en <c>hasta</c> y tiene que seguir contando.</summary>
    [Fact]
    public async Task FiltroDeFechaDevuelveElSubconjuntoEsperado()
    {
        var ctx = await PrepararAsync(nameof(FiltroDeFechaDevuelveElSubconjuntoEsperado));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var idFilaEnElBordeHasta = await SembrarFilaAsync(
            ctx.IdTenant, null, ctx.IdActorAdmin, "precio.cambio", "articulo", 999,
            new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero), null, "{\"monto\":999}");

        var pagina = await ConsultarAsync(
            ctx.Admin, "?desde=2026-03-01T00:00:00Z&hasta=2026-05-31T00:00:00Z&tamanio=50");

        Assert.Equal(4, pagina.Total);
        var idsDevueltos = pagina.Items.Select(f => f.IdAuditoria).ToHashSet();
        Assert.Equal(new HashSet<long> { ids["R3"], ids["R4"], ids["R5"], idFilaEnElBordeHasta }, idsDevueltos);
    }

    // ---- 5.12: accion -----------------------------------------------------------------------------

    [Fact]
    public async Task FiltroDeAccionDevuelveElSubconjuntoEsperado()
    {
        var ctx = await PrepararAsync(nameof(FiltroDeAccionDevuelveElSubconjuntoEsperado));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?accion=stock.ajuste&tamanio=50");

        Assert.Equal(2, pagina.Total);
        var idsDevueltos = pagina.Items.Select(f => f.IdAuditoria).ToHashSet();
        Assert.Equal(new HashSet<long> { ids["R4"], ids["R6"] }, idsDevueltos);
    }

    [Fact]
    public async Task UnaAccionDesconocidaDevuelve200ConCeroFilas()
    {
        var ctx = await PrepararAsync(nameof(UnaAccionDesconocidaDevuelve200ConCeroFilas));
        await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?accion=accion.retirada.no.existe&tamanio=50");

        Assert.Equal(0, pagina.Total);
        Assert.Empty(pagina.Items);
    }

    // ---- 5.13: idActor ------------------------------------------------------------------------

    [Fact]
    public async Task FiltroDeActorDevuelveElSubconjuntoEsperado()
    {
        var ctx = await PrepararAsync(nameof(FiltroDeActorDevuelveElSubconjuntoEsperado));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, $"?idActor={ctx.IdActorX}&tamanio=50");

        Assert.Equal(3, pagina.Total);
        var idsDevueltos = pagina.Items.Select(f => f.IdAuditoria).ToHashSet();
        Assert.Equal(new HashSet<long> { ids["R2"], ids["R4"], ids["R7"] }, idsDevueltos);
    }

    // ---- 5.14: entidad + idEntidad --------------------------------------------------------------

    [Fact]
    public async Task FiltroDeEntidadMasIdEntidadDevuelveSoloLaHistoriaDeEseAgregado()
    {
        var ctx = await PrepararAsync(nameof(FiltroDeEntidadMasIdEntidadDevuelveSoloLaHistoriaDeEseAgregado));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina41 = await ConsultarAsync(ctx.Admin, "?entidad=articulo&idEntidad=41&tamanio=50");
        Assert.Equal(3, pagina41.Total);
        Assert.Equal(
            new HashSet<long> { ids["R1"], ids["R4"], ids["R5"] },
            pagina41.Items.Select(f => f.IdAuditoria).ToHashSet());

        var pagina42 = await ConsultarAsync(ctx.Admin, "?entidad=articulo&idEntidad=42&tamanio=50");
        Assert.Equal(2, pagina42.Total);
        Assert.Equal(
            new HashSet<long> { ids["R6"], ids["R7"] },
            pagina42.Items.Select(f => f.IdAuditoria).ToHashSet());
    }

    // ---- 5.15/5.18: idPuntoVenta + "todos" (Admin cross-PV) --------------------------------------

    [Fact]
    public async Task FiltroDePuntoDeVentaDevuelveElSubconjuntoEsperado()
    {
        var ctx = await PrepararAsync(nameof(FiltroDePuntoDeVentaDevuelveElSubconjuntoEsperado));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var paginaPv1 = await ConsultarAsync(ctx.Admin, $"?idPuntoVenta={ctx.IdPuntoVenta1}&tamanio=50");
        Assert.Equal(3, paginaPv1.Total);
        Assert.Equal(
            new HashSet<long> { ids["R2"], ids["R4"], ids["R6"] },
            paginaPv1.Items.Select(f => f.IdAuditoria).ToHashSet());

        var paginaPv2 = await ConsultarAsync(ctx.Admin, $"?idPuntoVenta={ctx.IdPuntoVenta2}&tamanio=50");
        Assert.Equal(3, paginaPv2.Total);
        Assert.Equal(
            new HashSet<long> { ids["R3"], ids["R5"], ids["R7"] },
            paginaPv2.Items.Select(f => f.IdAuditoria).ToHashSet());
    }

    /// <summary>Spec "Tenant-wide rows appear under 'todos' punto de venta" y "Admin reads across
    /// every punto de venta of the tenant" (task 5.18): sin <c>idPuntoVenta</c>, la respuesta
    /// incluye las 8 filas — las tenant-wide (<c>id_punto_venta IS NULL</c>, R1/R8) Y las de AMBOS
    /// puntos de venta (R2/R4/R6 de PV1, R3/R5/R7 de PV2) en la MISMA respuesta.</summary>
    [Fact]
    public async Task SinFiltroDePuntoDeVentaTodosIncluyeLasTenantWideYAmbosPuntosDeVenta()
    {
        var ctx = await PrepararAsync(nameof(SinFiltroDePuntoDeVentaTodosIncluyeLasTenantWideYAmbosPuntosDeVenta));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?tamanio=50");

        Assert.Equal(8, pagina.Total);
        var idsDevueltos = pagina.Items.Select(f => f.IdAuditoria).ToHashSet();
        Assert.Equal(ids.Values.ToHashSet(), idsDevueltos);

        // Tenant-wide (PV NULL) presente entre las devueltas.
        Assert.Contains(pagina.Items, f => f.IdAuditoria == ids["R1"] && f.IdPuntoVenta is null);
        // Ambos puntos de venta presentes en la MISMA respuesta.
        Assert.Contains(pagina.Items, f => f.IdAuditoria == ids["R2"] && f.IdPuntoVenta == ctx.IdPuntoVenta1);
        Assert.Contains(pagina.Items, f => f.IdAuditoria == ids["R3"] && f.IdPuntoVenta == ctx.IdPuntoVenta2);
    }

    // ---- 5.16: idEntidad sin entidad → 400 -------------------------------------------------------

    /// <summary>Mutation target (slice 5, row 4 — validación 400): design decisión 16.</summary>
    [Fact]
    public async Task IdEntidadSinEntidadRechazaCon400EntidadRequerida()
    {
        var ctx = await PrepararAsync(nameof(IdEntidadSinEntidadRechazaCon400EntidadRequerida));

        var respuesta = await ctx.Admin.GetAsync("/api/auditoria?idEntidad=41");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entidad_requerida", cuerpo.GetProperty("codigo").GetString());
    }

    // ---- contenido de los payloads (Judgment-day, juez B, slice 5 ronda 1, WARNING) --------------

    /// <summary>Mutation target (slice 5, juez B ronda 1, WARNING): el contenido de
    /// <c>ValorAnterior</c>/<c>ValorNuevo</c> nunca se comparaba contra lo sembrado — proyectar
    /// <c>ValorAnterior</c> desde <c>a.ValorNuevo</c> sobrevivía 16/16. R2 (estado
    /// emitido→anulado) discrimina el contenido REAL de ambos payloads; R1 (<c>ValorAnterior</c>
    /// null) discrimina la null-ness.</summary>
    [Fact]
    public async Task ElContenidoDeAmbosPayloadsCoincideConLoSembradoYLaNullNessDeValorAnteriorSeRespeta()
    {
        var ctx = await PrepararAsync(nameof(ElContenidoDeAmbosPayloadsCoincideConLoSembradoYLaNullNessDeValorAnteriorSeRespeta));
        var ids = await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?tamanio=50");

        var filaR2 = pagina.Items.Single(f => f.IdAuditoria == ids["R2"]);
        Assert.Equal("emitido", filaR2.ValorAnterior!.Value.GetProperty("estado").GetString());
        Assert.Equal("anulado", filaR2.ValorNuevo.GetProperty("estado").GetString());

        var filaR1 = pagina.Items.Single(f => f.IdAuditoria == ids["R1"]);
        Assert.Null(filaR1.ValorAnterior);
        Assert.Equal(100, filaR1.ValorNuevo.GetProperty("monto").GetInt32());
    }

    // ---- clamp de pagina/tamanio (Judgment-day, juez B, slice 5 ronda 1, WARNING) -----------------

    /// <summary>Mutation target (slice 5, juez B ronda 1, WARNING): <c>Math.Clamp(tamanio, 1,
    /// 200)</c> sin ningún test — borrar el clamp sobrevivía (<c>tamanio=0</c> se traduciría en
    /// <c>Take(0)</c>, cero filas, en vez de la mínima página de 1).</summary>
    [Fact]
    public async Task TamanioCeroSeTrataComoElMinimoUnaFila()
    {
        var ctx = await PrepararAsync(nameof(TamanioCeroSeTrataComoElMinimoUnaFila));
        await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?tamanio=0");

        Assert.Equal(8, pagina.Total);
        Assert.Equal(1, pagina.Tamanio);
        Assert.Single(pagina.Items);
    }

    /// <summary>Mutation target (slice 5, juez B ronda 1, WARNING): mismo <c>Math.Clamp</c>, borde
    /// superior — sin el clamp, <c>tamanio=500</c> viajaría tal cual en vez de topearse en
    /// 200.</summary>
    [Fact]
    public async Task TamanioQuinientosSeTopeaEnDoscientos()
    {
        var ctx = await PrepararAsync(nameof(TamanioQuinientosSeTopeaEnDoscientos));
        await SembrarEscenarioDeFiltrosAsync(ctx);

        var pagina = await ConsultarAsync(ctx.Admin, "?tamanio=500");

        Assert.Equal(200, pagina.Tamanio);
    }

    /// <summary>Mutation target (slice 5, juez B ronda 1, WARNING): <c>Math.Max(pagina, 1)</c> sin
    /// ningún test — borrar el clamp sobrevivía. <c>pagina=0</c> tiene que tratarse igual que
    /// <c>pagina=1</c>.</summary>
    [Fact]
    public async Task PaginaCeroSeTrataComoLaPrimera()
    {
        var ctx = await PrepararAsync(nameof(PaginaCeroSeTrataComoLaPrimera));
        await SembrarEscenarioDeFiltrosAsync(ctx);

        var paginaConCero = await ConsultarAsync(ctx.Admin, "?tamanio=50&pagina=0");
        var paginaConUno = await ConsultarAsync(ctx.Admin, "?tamanio=50&pagina=1");

        Assert.Equal(1, paginaConCero.Pagina);
        Assert.Equal(8, paginaConCero.Total);
        Assert.Equal(
            paginaConUno.Items.Select(f => f.IdAuditoria).ToList(),
            paginaConCero.Items.Select(f => f.IdAuditoria).ToList());
    }

    // ---- 5.5/5.9: tiebreaker id_auditoria DESC bajo creado_el empatado ---------------------------

    /// <summary>Mutation target (slice 5, row 1): <c>ThenByDescending(a.Id)</c>. Todas las filas
    /// comparten el MISMO <c>creado_el</c> (RelojFijo, decisión 12: "under RelojFijo an entire
    /// fixture ties") — el orden esperado, por diseño, es EXACTAMENTE descendente por id, nunca
    /// solo "sin duplicar ni saltear" (design Testing Strategy: "orden como secuencia, no como
    /// conjunto"; <c>mutation-proof-tests</c> regla 6). Sin el tiebreaker, Postgres no está
    /// obligado a devolver los empates en ese orden entre dos SELECTs con OFFSET distintos.</summary>
    [Fact]
    public async Task PaginacionConCreadoElEmpatadoNoRepiteNiSalteaYRespetaElOrdenDescendentePorId()
    {
        var ctx = await PrepararAsync(nameof(PaginacionConCreadoElEmpatadoNoRepiteNiSalteaYRespetaElOrdenDescendentePorId));

        var idsEnOrdenDeInsercion = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var id = await SembrarFilaAsync(
                ctx.IdTenant, null, ctx.IdActorAdmin, "precio.cambio", "articulo", 100 + i,
                MomentoFijo, null, $"{{\"monto\":{100 + i}}}");
            idsEnOrdenDeInsercion.Add(id);
        }

        var esperadoDescendente = idsEnOrdenDeInsercion.OrderByDescending(x => x).ToList();

        var pagina1 = await ConsultarAsync(ctx.Admin, "?tamanio=2&pagina=1");
        var pagina2 = await ConsultarAsync(ctx.Admin, "?tamanio=2&pagina=2");
        var pagina3 = await ConsultarAsync(ctx.Admin, "?tamanio=2&pagina=3");

        Assert.Equal(5, pagina1.Total);
        var secuenciaCompleta = pagina1.Items.Select(f => f.IdAuditoria)
            .Concat(pagina2.Items.Select(f => f.IdAuditoria))
            .Concat(pagina3.Items.Select(f => f.IdAuditoria))
            .ToList();

        Assert.Equal(esperadoDescendente, secuenciaCompleta);
    }

    // ---- 5.6/5.7/5.10: visibilidad del actor -----------------------------------------------------

    /// <summary>Mutation targets (slice 5, rows 2 y 3): <c>DefaultIfEmpty()</c> (LEFT JOIN) e
    /// <c>IgnoreQueryFilters(["BajaLogica"])</c> — un actor root (invisible por el filtro de
    /// tenant/RLS propio de <c>usuarios</c>) y un actor dado de baja (invisible por
    /// <c>BajaLogica</c> si no se lo ignora) son los dos casos que un INNER JOIN o un filtro sin
    /// ignorar borrarían del log — justo las dos filas que un auditor más necesita leer (design
    /// decisión 14).</summary>
    [Fact]
    public async Task UnActorSoftDeletedSigueMostrandoElNombreYUnActorRootApareceConActorNuloEIdActorPresente()
    {
        var ctx = await PrepararAsync(nameof(UnActorSoftDeletedSigueMostrandoElNombreYUnActorRootApareceConActorNuloEIdActorPresente));

        var idActorBaja = await SembrarUsuarioAsync(
            ctx.IdTenant, "vendedor-de-baja", "de-baja@ways.test", deletedAt: DateTimeOffset.UtcNow);
        var idActorRoot = await ObtenerIdDeUsuarioRootAsync();

        var idFilaBaja = await SembrarFilaAsync(
            ctx.IdTenant, null, idActorBaja, "usuario.password", "usuario", 777,
            MomentoFijo, null, "{\"por_el_propio_usuario\":true}");
        var idFilaRoot = await SembrarFilaAsync(
            ctx.IdTenant, null, idActorRoot, "usuario.actualizacion", "usuario", 778,
            MomentoFijo, "{\"estado\":\"activo\"}", "{\"estado\":\"bloqueado\"}");

        var pagina = await ConsultarAsync(ctx.Admin, "?tamanio=50");

        var filaBaja = pagina.Items.Single(f => f.IdAuditoria == idFilaBaja);
        Assert.Equal("vendedor-de-baja", filaBaja.Actor);

        var filaRoot = pagina.Items.Single(f => f.IdAuditoria == idFilaRoot);
        Assert.Null(filaRoot.Actor);
        Assert.Equal(idActorRoot, filaRoot.IdActor);
    }

    // ---- 5.17/5.18: autorización ------------------------------------------------------------------

    [Fact]
    public async Task UnSupervisorEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazado));
        var supervisor = await CrearYLoguearAsync(ctx.Admin, nameof(UnSupervisorEsRechazado), "supervisor", RolConocido.Supervisor);

        var respuesta = await supervisor.GetAsync("/api/auditoria");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazado));
        var vendedor = await CrearYLoguearAsync(ctx.Admin, nameof(UnVendedorEsRechazado), "vendedor", RolConocido.Vendedor);

        var respuesta = await vendedor.GetAsync("/api/auditoria");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRootEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazado));

        var respuesta = await ctx.Root.GetAsync("/api/auditoria");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAdminEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnAdminEsAceptado));
        await SembrarEscenarioDeFiltrosAsync(ctx);

        var respuesta = await ctx.Admin.GetAsync("/api/auditoria?tamanio=50");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var pagina = await respuesta.Content.ReadFromJsonAsync<PaginaRespuesta>();
        Assert.Equal(8, pagina!.Total);
    }

    // ---- 5.19/5.20: aislamiento de tenant -----------------------------------------------------

    /// <summary>Mitad HTTP (task 5.20): un Admin del tenant B nunca ve las filas del tenant A a
    /// través del endpoint completo (EF filter + RLS + policy, la pila real).</summary>
    [Fact]
    public async Task UnAdminDeOtroTenantNuncaVeFilasDelTenantAjenoATravesDelEndpoint()
    {
        var ctxA = await PrepararAsync(nameof(UnAdminDeOtroTenantNuncaVeFilasDelTenantAjenoATravesDelEndpoint) + "-A");
        await SembrarEscenarioDeFiltrosAsync(ctxA);

        var ctxB = await PrepararAsync(nameof(UnAdminDeOtroTenantNuncaVeFilasDelTenantAjenoATravesDelEndpoint) + "-B");

        var paginaB = await ConsultarAsync(ctxB.Admin, "?tamanio=50");

        Assert.Equal(0, paginaB.Total);
        Assert.Empty(paginaB.Items);
    }

    /// <summary>
    /// Mutation target (slice 5, row 6): el filtro de tenant/RLS de la consulta. Sobre
    /// <c>ways_app</c> (usado por el test de arriba), RLS por sí sola ya aísla — mutar/quitar el
    /// filtro de EF de <c>ConstruirQuery</c> no haría fallar ese test (confound documentado en
    /// <c>mutation-proof-tests</c> regla 3, mismo defecto que <c>LotesRlsTests</c> ya resolvió
    /// para <c>lotes</c>/<c>stock_lotes</c>). Este test corre <see cref="ServicioDeConsultaDeAuditoria"/>
    /// sobre una conexión DUEÑA de las tablas (<c>ways_owner</c>, bypassea RLS) — así el ÚNICO
    /// mecanismo que puede aislar es el query filter de EF que <see cref="ServicioDeConsultaDeAuditoria.ConstruirQuery"/>
    /// hereda de <c>db.Auditoria</c> (<c>WaysDbContext.AplicarFiltroDeTenantEnAuditoria</c>,
    /// Slice 1) — genuinamente discriminante.
    ///
    /// Evidencia de mutación: comentando <c>AplicarFiltroDeTenantEnAuditoria(modelBuilder);</c> en
    /// <c>WaysDbContext.AplicarFiltrosDeTenant</c> y corriendo
    /// <c>--filter "FullyQualifiedName~ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls"</c>,
    /// este test FALLÓ (<c>visibleAjena</c> pasó a <c>true</c> — la fila del tenant A quedó
    /// visible para la sesión del tenant B) — revertida la mutación, vuelve a estar verde.
    /// </summary>
    [Fact]
    public async Task ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls()
    {
        var ctxA = await PrepararAsync(nameof(ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls) + "-A");
        var idFilaA = await SembrarFilaAsync(
            ctxA.IdTenant, null, ctxA.IdActorAdmin, "precio.cambio", "articulo", 41,
            MomentoFijo, null, "{\"monto\":100}");

        var ctxB = await PrepararAsync(nameof(ElFiltroDeTenantDeLaConsultaAislaAunSobreUnaConexionQueBypaseaRls) + "-B");
        var idFilaB = await SembrarFilaAsync(
            ctxB.IdTenant, null, ctxB.IdActorAdmin, "precio.cambio", "articulo", 42,
            MomentoFijo, null, "{\"monto\":200}");

        await using var sesionOwnerDeB = fixture.CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, ctxB.IdTenant));
        var servicio = new ServicioDeConsultaDeAuditoria(sesionOwnerDeB);

        var pagina = await servicio.ConsultarAsync(
            new FiltrosDeAuditoria(null, null, null, null, null, null, null), pagina: 1, tamanio: 50, CancellationToken.None);

        var idsVisibles = pagina.Items.Select(f => f.IdAuditoria).ToHashSet();
        Assert.DoesNotContain(idFilaA, idsVisibles);
        Assert.Contains(idFilaB, idsVisibles);
    }
}
