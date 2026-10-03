using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Familias;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Precios con alcance de familia (doc 10 §3, "Familias de artículos") contra Postgres real: lo que
/// <c>POST /api/articulos/{id}/precios</c> y <c>.../programados</c> hacen cuando el artículo
/// pertenece a una familia, y el protocolo de locks que lo sostiene — membresía (compartido o
/// exclusivo), filas de <c>articulos</c>, pares artículo-lista. Todavía no existe ninguna API de
/// familias: las familias y sus miembros se siembran por EF.
///
/// <para>Los tests de concurrencia son rendezvous determinísticos, nunca carreras probabilísticas: una
/// conexión cruda sostiene un lock, la escritura queda observada esperando en <c>pg_locks</c>, y recién
/// ahí se libera. Los statements de <c>ServicioDePrecios</c> son SQL crudo que un interceptor de EF no
/// ve, así que el orden de los locks se afirma mirando <c>pg_locks</c> desde afuera, con la
/// transacción detenida en un punto conocido (<c>mutation-proof-tests</c>, regla 13).</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PreciosDeFamiliaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";
    private const string PasswordRoot = "root";
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset MomentoFijo = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed record Contexto(
        int IdTenant, int IdArea, int IdAlicuotaIva, int IdListaGeneral, int IdListaMayorista, int IdActorAdmin,
        HttpClient Admin) : IDisposable
    {
        public void Dispose() => Admin.Dispose();
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => idTenant;
    }

    // =================================================================================================
    // Siembra y lectura
    // =================================================================================================

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var mailAdmin = $"{unico}@ways.test";

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{unico}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio
            .Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        var mayorista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Mayorista", EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(mayorista);
        await db.SaveChangesAsync();

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return new Contexto(
            resultado.IdTenant, area.Id, idAlicuotaIva, idListaGeneral, mayorista.Id, resultado.IdUsuarioAdmin, admin);
    }

    private async Task<int> SembrarFamiliaAsync(Contexto c, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var familia = new Familia { IdTenant = c.IdTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    /// <summary>Los artículos de una misma familia se siembran con los mismos campos compartidos.
    /// Un artículo "dado de baja" lleva <c>DeletedAt</c> y sigue apuntando a su familia: es lo que un
    /// escritor de baja deja, y no cuenta como miembro.</summary>
    private async Task<int> SembrarArticuloAsync(
        Contexto c, string nombre, int? idFamilia = null, bool dadoDeBaja = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = c.IdTenant,
            CodigoInterno = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}",
            Nombre = nombre,
            IdArea = c.IdArea,
            IdAlicuotaIva = c.IdAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            IdFamilia = idFamilia,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = dadoDeBaja ? ahora : null
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    /// <summary>Un precio vigente (<c>vigente_hasta</c> nulo) que arrancó hace dos días.</summary>
    private Task SembrarPrecioVigenteAsync(Contexto c, int idArticulo, int idLista, decimal monto) =>
        SembrarPrecioAsync(c, idArticulo, idLista, monto, DateTimeOffset.UtcNow.AddDays(-2), null);

    /// <summary>Un precio vigente hasta la fecha del pendiente, y el pendiente a partir de ahí
    /// (<c>vigente_hasta</c> nulo, <c>vigente_desde</c> a futuro): el estado que deja programar un
    /// precio.</summary>
    private async Task SembrarPrecioPendienteAsync(
        Contexto c, int idArticulo, int idLista, decimal montoVigente, decimal montoPendiente)
    {
        var desdePendiente = DateTimeOffset.UtcNow.AddDays(3);
        await SembrarPrecioAsync(c, idArticulo, idLista, montoVigente, DateTimeOffset.UtcNow.AddDays(-2), desdePendiente);
        await SembrarPrecioAsync(c, idArticulo, idLista, montoPendiente, desdePendiente, null);
    }

    private async Task SembrarPrecioAsync(
        Contexto c, int idArticulo, int idLista, decimal monto, DateTimeOffset desde, DateTimeOffset? hasta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        db.Precios.Add(new Precio
        {
            IdTenant = c.IdTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = idLista,
            Monto = monto,
            VigenteDesde = desde,
            VigenteHasta = hasta,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<Precio>> FilasDePrecioAsync(int idArticulo, int idLista)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Precios.IgnoreQueryFilters()
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == idLista)
            .OrderBy(p => p.VigenteDesde)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <summary>La única fila abierta (<c>vigente_hasta</c> nulo) del par, o <c>null</c>.</summary>
    private async Task<Precio?> FilaAbiertaAsync(int idArticulo, int idLista) =>
        (await FilasDePrecioAsync(idArticulo, idLista)).SingleOrDefault(p => p.VigenteHasta is null);

    private async Task<int?> IdFamiliaDeAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters()
            .Where(a => a.Id == idArticulo)
            .Select(a => a.IdFamilia)
            .SingleAsync();
    }

    private async Task<List<Ways.Domain.Auditoria.Auditoria>> AuditoriaDePreciosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Auditoria.IgnoreQueryFilters()
            .Where(a => a.IdTenant == idTenant && a.Accion == "precio.cambio")
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    // =================================================================================================
    // Llamadas HTTP
    // =================================================================================================

    /// <summary>El cuerpo se arma a mano con el alcance como TEXTO, que es como lo manda un cliente:
    /// serializar el record mandaría el ordinal del enum.</summary>
    private static Task<HttpResponseMessage> PostPrecioAsync(
        HttpClient admin, int idArticulo, int idLista, decimal precio, string? alcance = null, bool confirmar = false)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["idListaPrecio"] = idLista,
            ["precio"] = precio,
            ["confirmarReemplazo"] = confirmar
        };

        if (alcance is not null)
        {
            cuerpo["alcance"] = alcance;
        }

        return admin.PostAsJsonAsync($"/api/articulos/{idArticulo}/precios", cuerpo);
    }

    private static Task<HttpResponseMessage> PostProgramadoAsync(
        HttpClient admin, int idArticulo, int idLista, decimal precio, DateTimeOffset desde, string? alcance = null,
        bool confirmar = false)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["idListaPrecio"] = idLista,
            ["precio"] = precio,
            ["vigenteDesde"] = desde,
            ["confirmarReemplazo"] = confirmar
        };

        if (alcance is not null)
        {
            cuerpo["alcance"] = alcance;
        }

        return admin.PostAsJsonAsync($"/api/articulos/{idArticulo}/precios/programados", cuerpo);
    }

    private static Task<HttpResponseMessage> PostSegunAsync(
        bool programado, HttpClient admin, int idArticulo, int idLista, decimal precio, string? alcance = null,
        bool confirmar = false) =>
        programado
            ? PostProgramadoAsync(admin, idArticulo, idLista, precio, DateTimeOffset.UtcNow.AddDays(3), alcance, confirmar)
            : PostPrecioAsync(admin, idArticulo, idLista, precio, alcance, confirmar);

    /// <summary><c>timestamptz</c> guarda microsegundos y <see cref="DateTimeOffset"/> tiene 100 ns de
    /// resolución: un instante tomado del reloj se trunca al escribirlo, y compararlo sin truncar
    /// arriesga una diferencia de representación, no de instante.</summary>
    private static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    private static async Task<(string? Codigo, string? Mensaje)> ProblemaAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        return (
            problema.GetProperty("codigo").GetString(),
            problema.TryGetProperty("title", out var titulo) ? titulo.GetString() : null);
    }

    // =================================================================================================
    // Artículo sin familia: comportamiento de siempre
    // =================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnArticuloSinFamiliaSeEscribeSoloYSinAlcanceComoSiempre(bool programado)
    {
        using var c = await PrepararAsync(nameof(UnArticuloSinFamiliaSeEscribeSoloYSinAlcanceComoSiempre));
        var articulo = await SembrarArticuloAsync(c, "solo");
        var otroSinFamilia = await SembrarArticuloAsync(c, "otro");
        await SembrarPrecioVigenteAsync(c, articulo, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, otroSinFamilia, c.IdListaGeneral, 70m);

        var respuesta = await PostSegunAsync(programado, c.Admin, articulo, c.IdListaGeneral, 150m);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var vigente = (await respuesta.Content.ReadFromJsonAsync<PrecioVigente>())!;
        Assert.Equal(articulo, vigente.IdArticulo);
        Assert.Equal(150m, vigente.Precio);

        var filas = await FilasDePrecioAsync(articulo, c.IdListaGeneral);
        Assert.Equal([100m, 150m], filas.Select(f => f.Monto));

        var intacto = Assert.Single(await FilasDePrecioAsync(otroSinFamilia, c.IdListaGeneral));
        Assert.Equal(70m, intacto.Monto);
        Assert.Null(intacto.VigenteHasta);

        var auditoria = await AuditoriaDePreciosAsync(c.IdTenant);
        Assert.Equal([articulo], auditoria.Select(a => a.IdEntidad));
    }

    [Theory]
    [InlineData("Familia", false)]
    [InlineData("SoloEste", false)]
    [InlineData("Familia", true)]
    [InlineData("SoloEste", true)]
    public async Task UnAlcanceExplicitoSobreUnArticuloSinFamiliaDa409FamiliaCambioYNoEscribeNada(
        string alcance, bool programado)
    {
        using var c = await PrepararAsync(nameof(UnAlcanceExplicitoSobreUnArticuloSinFamiliaDa409FamiliaCambioYNoEscribeNada));
        var articulo = await SembrarArticuloAsync(c, "sin-familia");
        await SembrarPrecioVigenteAsync(c, articulo, c.IdListaGeneral, 100m);

        var respuesta = await PostSegunAsync(programado, c.Admin, articulo, c.IdListaGeneral, 150m, alcance);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);

        var fila = Assert.Single(await FilasDePrecioAsync(articulo, c.IdListaGeneral));
        Assert.Equal(100m, fila.Monto);
        Assert.Null(fila.VigenteHasta);
        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }

    // =================================================================================================
    // Miembro sin decisión: alcance_requerido
    // =================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnMiembroSinAlcanceDa409AlcanceRequeridoNombraLaFamiliaYNoEscribeNada(bool programado)
    {
        using var c = await PrepararAsync(nameof(UnMiembroSinAlcanceDa409AlcanceRequeridoNombraLaFamiliaYNoEscribeNada));
        var familia = await SembrarFamiliaAsync(c, "Gaseosas cola");
        var a1 = await SembrarArticuloAsync(c, "cola-500", familia);
        var a2 = await SembrarArticuloAsync(c, "cola-1000", familia);
        var a3 = await SembrarArticuloAsync(c, "cola-2000", familia);
        var dadoDeBaja = await SembrarArticuloAsync(c, "cola-vieja", familia, dadoDeBaja: true);
        var otro = await SembrarArticuloAsync(c, "otro");
        foreach (var id in new[] { a1, a2, a3, dadoDeBaja, otro })
        {
            await SembrarPrecioVigenteAsync(c, id, c.IdListaGeneral, 100m);
        }

        var respuesta = await PostSegunAsync(programado, c.Admin, a2, c.IdListaGeneral, 150m);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("alcance_requerido", codigo);

        // Nombra la familia y cuenta solo los miembros VIVOS (3, no los 4 que apuntan a ella).
        Assert.Contains("Gaseosas cola", mensaje, StringComparison.Ordinal);
        Assert.Contains("3 artículos", mensaje, StringComparison.Ordinal);

        foreach (var id in new[] { a1, a2, a3, dadoDeBaja, otro })
        {
            var fila = Assert.Single(await FilasDePrecioAsync(id, c.IdListaGeneral));
            Assert.Equal(100m, fila.Monto);
            Assert.Null(fila.VigenteHasta);
        }

        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));

        foreach (var id in new[] { a1, a2, a3 })
        {
            Assert.Equal(familia, await IdFamiliaDeAsync(id));
        }
    }

    /// <summary>Un valor numérico fuera del enum llega al servidor (el conversor JSON acepta el
    /// ordinal) y no se interpreta como ningún alcance ni se ignora: 400 y nada escrito.</summary>
    [Fact]
    public async Task UnAlcanceFueraDelEnumDa400AlcanceInvalidoYNoEscribeNada()
    {
        using var c = await PrepararAsync(nameof(UnAlcanceFueraDelEnumDa400AlcanceInvalidoYNoEscribeNada));
        var familia = await SembrarFamiliaAsync(c, "Familia con alcance invalido");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);

        var respuesta = await c.Admin.PostAsJsonAsync(
            $"/api/articulos/{a1}/precios",
            new Dictionary<string, object?> { ["idListaPrecio"] = c.IdListaGeneral, ["precio"] = 150m, ["alcance"] = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("alcance_invalido", (await ProblemaAsync(respuesta)).Codigo);

        Assert.Single(await FilasDePrecioAsync(a1, c.IdListaGeneral));
        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }

    // =================================================================================================
    // Alcance Familia: todos los miembros vivos, en una transacción
    // =================================================================================================

    [Fact]
    public async Task ConAlcanceFamiliaTodosLosMiembrosVivosRecibenElPrecioInmediatoConUnaAuditoriaCadaUno()
    {
        using var c = await PrepararAsync(nameof(ConAlcanceFamiliaTodosLosMiembrosVivosRecibenElPrecioInmediatoConUnaAuditoriaCadaUno));
        var familia = await SembrarFamiliaAsync(c, "Gaseosas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var a3 = await SembrarArticuloAsync(c, "a3", familia);
        var dadoDeBaja = await SembrarArticuloAsync(c, "baja", familia, dadoDeBaja: true);
        var otro = await SembrarArticuloAsync(c, "otro");

        // Estados previos DISTINTOS por miembro: cada uno cierra y abre lo suyo.
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, a2, c.IdListaGeneral, 110m);
        await SembrarPrecioVigenteAsync(c, dadoDeBaja, c.IdListaGeneral, 90m);
        await SembrarPrecioVigenteAsync(c, otro, c.IdListaGeneral, 70m);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaMayorista, 80m);

        var respuesta = await PostPrecioAsync(c.Admin, a2, c.IdListaGeneral, 150m, "Familia");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var vigente = (await respuesta.Content.ReadFromJsonAsync<PrecioVigente>())!;
        Assert.Equal(a2, vigente.IdArticulo);
        Assert.Equal(c.IdListaGeneral, vigente.IdListaPrecio);
        Assert.Equal(150m, vigente.Precio);

        var abiertas = new List<Precio>();
        foreach (var id in new[] { a1, a2, a3 })
        {
            var abierta = (await FilaAbiertaAsync(id, c.IdListaGeneral))!;
            Assert.Equal(150m, abierta.Monto);
            abiertas.Add(abierta);
        }

        // "Ahora" se resolvió UNA vez: el mismo vigente_desde para todos.
        Assert.Single(abiertas.Select(f => f.VigenteDesde).Distinct());
        Assert.Equal(abiertas[0].VigenteDesde, AMicrosegundos(vigente.Fecha));

        // a1 y a2 cerraron su fila anterior exactamente donde abrió la nueva; a3 no tenía ninguna.
        Assert.Equal([100m, 150m], (await FilasDePrecioAsync(a1, c.IdListaGeneral)).Select(f => f.Monto));
        Assert.Equal([110m, 150m], (await FilasDePrecioAsync(a2, c.IdListaGeneral)).Select(f => f.Monto));
        Assert.Equal([150m], (await FilasDePrecioAsync(a3, c.IdListaGeneral)).Select(f => f.Monto));
        Assert.Equal(abiertas[0].VigenteDesde, (await FilasDePrecioAsync(a1, c.IdListaGeneral))[0].VigenteHasta);
        Assert.Equal(abiertas[0].VigenteDesde, (await FilasDePrecioAsync(a2, c.IdListaGeneral))[0].VigenteHasta);

        // El miembro dado de baja, el artículo sin familia y OTRA lista quedan intactos.
        var deBaja = Assert.Single(await FilasDePrecioAsync(dadoDeBaja, c.IdListaGeneral));
        Assert.Equal(90m, deBaja.Monto);
        Assert.Null(deBaja.VigenteHasta);
        var ajeno = Assert.Single(await FilasDePrecioAsync(otro, c.IdListaGeneral));
        Assert.Equal(70m, ajeno.Monto);
        Assert.Null(ajeno.VigenteHasta);
        var otraLista = Assert.Single(await FilasDePrecioAsync(a1, c.IdListaMayorista));
        Assert.Equal(80m, otraLista.Monto);
        Assert.Null(otraLista.VigenteHasta);

        // La pertenencia no cambia, y hay exactamente una auditoría por miembro vivo.
        foreach (var id in new[] { a1, a2, a3, dadoDeBaja })
        {
            Assert.Equal(familia, await IdFamiliaDeAsync(id));
        }


        var auditoria = await AuditoriaDePreciosAsync(c.IdTenant);
        Assert.Equal([a1, a2, a3], auditoria.Select(a => a.IdEntidad).Order());
        Assert.All(auditoria, a => Assert.Equal("articulo", a.Entidad));

        var anteriorDeA1 = JsonDocument.Parse(auditoria.Single(a => a.IdEntidad == a1).ValorAnterior!).RootElement;
        Assert.Equal(100m, anteriorDeA1.GetProperty("monto").GetDecimal());
        var anteriorDeA3 = auditoria.Single(a => a.IdEntidad == a3).ValorAnterior;
        Assert.Null(anteriorDeA3);
        Assert.All(auditoria, a =>
            Assert.Equal(150m, JsonDocument.Parse(a.ValorNuevo).RootElement.GetProperty("monto").GetDecimal()));
    }

    [Fact]
    public async Task ConAlcanceFamiliaUnPrecioProgramadoLlegaATodosLosMiembrosConLaMismaFecha()
    {
        using var c = await PrepararAsync(nameof(ConAlcanceFamiliaUnPrecioProgramadoLlegaATodosLosMiembrosConLaMismaFecha));
        var familia = await SembrarFamiliaAsync(c, "Snacks");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var dadoDeBaja = await SembrarArticuloAsync(c, "baja", familia, dadoDeBaja: true);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, a2, c.IdListaGeneral, 110m);

        var enTresDias = DateTimeOffset.UtcNow.AddDays(3);
        var respuesta = await PostProgramadoAsync(c.Admin, a1, c.IdListaGeneral, 150m, enTresDias, "Familia");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        foreach (var (id, anterior) in new[] { (a1, 100m), (a2, 110m) })
        {
            var filas = await FilasDePrecioAsync(id, c.IdListaGeneral);
            Assert.Equal([anterior, 150m], filas.Select(f => f.Monto));

            // El vigente de hoy sigue siendo el viejo: se cerró en la fecha programada, igual que
            // para un artículo sin familia, y el pendiente arranca ahí.
            Assert.Equal(AMicrosegundos(enTresDias), filas[1].VigenteDesde);
            Assert.Equal(AMicrosegundos(enTresDias), filas[0].VigenteHasta);
            Assert.Null(filas[1].VigenteHasta);
        }

        Assert.Empty(await FilasDePrecioAsync(dadoDeBaja, c.IdListaGeneral));
        Assert.Equal([a1, a2], (await AuditoriaDePreciosAsync(c.IdTenant)).Select(a => a.IdEntidad).Order());
    }

    // =================================================================================================
    // Alcance SoloEste: sale de la familia
    // =================================================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConAlcanceSoloEsteSoloCambiaEseArticuloYLoSacaDeLaFamilia(bool programado)
    {
        using var c = await PrepararAsync(nameof(ConAlcanceSoloEsteSoloCambiaEseArticuloYLoSacaDeLaFamilia));
        var familia = await SembrarFamiliaAsync(c, "Galletitas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var a3 = await SembrarArticuloAsync(c, "a3", familia);
        foreach (var id in new[] { a1, a2, a3 })
        {
            await SembrarPrecioVigenteAsync(c, id, c.IdListaGeneral, 100m);
        }

        var respuesta = await PostSegunAsync(programado, c.Admin, a2, c.IdListaGeneral, 150m, "SoloEste");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(a2, (await respuesta.Content.ReadFromJsonAsync<PrecioVigente>())!.IdArticulo);

        Assert.Equal([100m, 150m], (await FilasDePrecioAsync(a2, c.IdListaGeneral)).Select(f => f.Monto));
        foreach (var id in new[] { a1, a3 })
        {
            var fila = Assert.Single(await FilasDePrecioAsync(id, c.IdListaGeneral));
            Assert.Equal(100m, fila.Monto);
            Assert.Null(fila.VigenteHasta);
        }

        Assert.Null(await IdFamiliaDeAsync(a2));
        Assert.Equal(familia, await IdFamiliaDeAsync(a1));
        Assert.Equal(familia, await IdFamiliaDeAsync(a3));
        Assert.Equal([a2], (await AuditoriaDePreciosAsync(c.IdTenant)).Select(a => a.IdEntidad));

        // Ya no es miembro: un cambio sin alcance se comporta como el de cualquier artículo suelto
        // (con el pendiente programado de arriba hay que confirmar el reemplazo, como siempre), y
        // los que quedaron en la familia no se enteran.
        var siguiente = await PostPrecioAsync(c.Admin, a2, c.IdListaGeneral, 160m, confirmar: programado);
        Assert.Equal(HttpStatusCode.Created, siguiente.StatusCode);
        Assert.Equal(160m, (await FilaAbiertaAsync(a2, c.IdListaGeneral))!.Monto);
        Assert.Equal(100m, (await FilaAbiertaAsync(a1, c.IdListaGeneral))!.Monto);
        Assert.Equal(100m, (await FilaAbiertaAsync(a3, c.IdListaGeneral))!.Monto);
    }

    // =================================================================================================
    // Todo o nada
    // =================================================================================================

    /// <summary>El pendiente está en el miembro de id MÁS ALTO: para cuando se lo detecta, los dos
    /// anteriores ya cerraron su fila vigente con un <c>UPDATE</c> crudo dentro de la transacción. Que
    /// ninguno quede cambiado prueba que el rechazo revierte lo ya escrito, no que corta antes de
    /// escribir.</summary>
    [Fact]
    public async Task UnPrecioPendienteEnUnMiembroSinConfirmarAbortaTodoYNingunMiembroCambia()
    {
        using var c = await PrepararAsync(nameof(UnPrecioPendienteEnUnMiembroSinConfirmarAbortaTodoYNingunMiembroCambia));
        var familia = await SembrarFamiliaAsync(c, "Lacteos");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var a3 = await SembrarArticuloAsync(c, "a3", familia);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, a2, c.IdListaGeneral, 110m);
        await SembrarPrecioPendienteAsync(c, a3, c.IdListaGeneral, montoVigente: 120m, montoPendiente: 130m);

        var respuesta = await PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("precio_pendiente_existe", codigo);
        Assert.Contains("familia", mensaje, StringComparison.Ordinal);

        foreach (var (id, monto) in new[] { (a1, 100m), (a2, 110m) })
        {
            var fila = Assert.Single(await FilasDePrecioAsync(id, c.IdListaGeneral));
            Assert.Equal(monto, fila.Monto);
            Assert.Null(fila.VigenteHasta);
        }

        Assert.Equal([120m, 130m], (await FilasDePrecioAsync(a3, c.IdListaGeneral)).Select(f => f.Monto));
        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));

        // Confirmando el reemplazo la misma solicitud sí se aplica a toda la familia, y el pendiente
        // de a3 queda reemplazado.
        var confirmada = await PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia", confirmar: true);

        Assert.Equal(HttpStatusCode.Created, confirmada.StatusCode);
        foreach (var id in new[] { a1, a2, a3 })
        {
            Assert.Equal(150m, (await FilaAbiertaAsync(id, c.IdListaGeneral))!.Monto);
        }

        Assert.Equal([a1, a2, a3], (await AuditoriaDePreciosAsync(c.IdTenant)).Select(a => a.IdEntidad).Order());
    }

    /// <summary>La salida de la familia de "solo este" es parte de la misma transacción: si el cambio
    /// de precio se rechaza, el artículo SIGUE en su familia.</summary>
    [Fact]
    public async Task UnRechazoDeSoloEsteRevierteTambienLaSalidaDeLaFamilia()
    {
        using var c = await PrepararAsync(nameof(UnRechazoDeSoloEsteRevierteTambienLaSalidaDeLaFamilia));
        var familia = await SembrarFamiliaAsync(c, "Fiambres");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioPendienteAsync(c, a2, c.IdListaGeneral, montoVigente: 110m, montoPendiente: 130m);

        var respuesta = await PostPrecioAsync(c.Admin, a2, c.IdListaGeneral, 150m, "SoloEste");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("precio_pendiente_existe", (await ProblemaAsync(respuesta)).Codigo);

        Assert.Equal(familia, await IdFamiliaDeAsync(a2));
        Assert.Equal(familia, await IdFamiliaDeAsync(a1));
        Assert.Equal([110m, 130m], (await FilasDePrecioAsync(a2, c.IdListaGeneral)).Select(f => f.Monto));
        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }

    /// <summary>El otro conflicto de la tabla —<c>vigente_desde_invalido</c>— también aborta todo. El
    /// reloj es fijo para poder armar la ventana: la solicitud programa 20 segundos antes de "ahora"
    /// (dentro de la tolerancia de reloj) y el último miembro ya tiene una fila vigente que arrancó 5
    /// segundos antes.</summary>
    [Fact]
    public async Task UnVigenteDesdeInvalidoEnUnMiembroAbortaTodoYNingunMiembroCambia()
    {
        using var c = await PrepararAsync(nameof(UnVigenteDesdeInvalidoEnUnMiembroAbortaTodoYNingunMiembroCambia));
        var familia = await SembrarFamiliaAsync(c, "Bebidas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        await SembrarPrecioAsync(c, a1, c.IdListaGeneral, 100m, MomentoFijo.AddDays(-2), null);
        await SembrarPrecioAsync(c, a2, c.IdListaGeneral, 110m, MomentoFijo.AddSeconds(-5), null);

        var (db, servicio) = CrearServicio(c, new RelojFijo(MomentoFijo));
        await using var _ = db;

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ProgramarPrecioAsync(
            a1,
            new ProgramarPrecio(c.IdListaGeneral, 150m, MomentoFijo.AddSeconds(-20), false, AlcanceDeFamilia.Familia)));

        Assert.Equal("vigente_desde_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);

        foreach (var (id, monto) in new[] { (a1, 100m), (a2, 110m) })
        {
            var fila = Assert.Single(await FilasDePrecioAsync(id, c.IdListaGeneral));
            Assert.Equal(monto, fila.Monto);
            Assert.Null(fila.VigenteHasta);
        }

        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }

    // =================================================================================================
    // Modos internos (ServicioDePrecios directo): FamiliaSiCorresponde y el valor por defecto
    // =================================================================================================

    private (WaysDbContext Db, ServicioDePrecios Servicio) CrearServicio(Contexto c, IRelojDelSistema reloj)
    {
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant));
        var contexto = new ContextoFijo(c.IdTenant, c.IdActorAdmin);

        return (db, new ServicioDePrecios(db, reloj, contexto, new ServicioDeAuditoria(db, reloj, contexto)));
    }

    /// <summary>El modo de los llamadores internos (aplicar el precio sugerido de una compra): un miembro
    /// se escribe con toda su familia y quien no lo es, solo — sin error en ningún caso.</summary>
    [Fact]
    public async Task FamiliaSiCorrespondeEscribeLaFamiliaDeUnMiembroYSoloAlQueNoLoEs()
    {
        using var c = await PrepararAsync(nameof(FamiliaSiCorrespondeEscribeLaFamiliaDeUnMiembroYSoloAlQueNoLoEs));
        var familia = await SembrarFamiliaAsync(c, "Limpieza");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var suelto = await SembrarArticuloAsync(c, "suelto");
        var otroSuelto = await SembrarArticuloAsync(c, "otro-suelto");

        var (db, servicio) = CrearServicio(c, new RelojFijo(MomentoFijo));
        await using var _ = db;

        var delMiembro = await servicio.AbrirNuevoPrecioAsync(
            a2, c.IdListaGeneral, 150m, vigenteDesde: null, confirmarReemplazo: false,
            ModoDeAlcanceDeFamilia.FamiliaSiCorresponde);
        var delSuelto = await servicio.AbrirNuevoPrecioAsync(
            suelto, c.IdListaGeneral, 90m, vigenteDesde: null, confirmarReemplazo: false,
            ModoDeAlcanceDeFamilia.FamiliaSiCorresponde);

        Assert.Equal(a2, delMiembro.IdArticulo);
        Assert.Equal(suelto, delSuelto.IdArticulo);

        Assert.Equal(150m, (await FilaAbiertaAsync(a1, c.IdListaGeneral))!.Monto);
        Assert.Equal(150m, (await FilaAbiertaAsync(a2, c.IdListaGeneral))!.Monto);
        Assert.Equal(90m, (await FilaAbiertaAsync(suelto, c.IdListaGeneral))!.Monto);
        Assert.Empty(await FilasDePrecioAsync(otroSuelto, c.IdListaGeneral));

        Assert.Equal(familia, await IdFamiliaDeAsync(a2));
        Assert.Equal([a1, a2, suelto], (await AuditoriaDePreciosAsync(c.IdTenant)).Select(a => a.IdEntidad).Order());
    }

    /// <summary>Un llamador que no decide nada recibe el valor por defecto, que exige decidir: sobre un
    /// miembro rechaza en vez de escribir a medias.</summary>
    [Fact]
    public async Task SinModoElServicioExigeDecidirSobreUnMiembro()
    {
        using var c = await PrepararAsync(nameof(SinModoElServicioExigeDecidirSobreUnMiembro));
        var familia = await SembrarFamiliaAsync(c, "Perfumeria");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);

        var (db, servicio) = CrearServicio(c, new RelojFijo(MomentoFijo));
        await using var _ = db;

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.AbrirNuevoPrecioAsync(a1, c.IdListaGeneral, 150m, null, false));

        Assert.Equal("alcance_requerido", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        Assert.Empty(await FilasDePrecioAsync(a1, c.IdListaGeneral));
        Assert.Empty(await FilasDePrecioAsync(a2, c.IdListaGeneral));
    }

    /// <summary>"Solo este" saca al artículo con un <c>UPDATE</c> crudo: el servicio no deja en el
    /// <c>ChangeTracker</c> una foto previa al lock de esa fila, así que releerla por EF en el mismo
    /// contexto ve la pertenencia nueva, no la vieja.</summary>
    [Fact]
    public async Task DespuesDeSoloEsteElMismoContextoVeLaPertenenciaNueva()
    {
        using var c = await PrepararAsync(nameof(DespuesDeSoloEsteElMismoContextoVeLaPertenenciaNueva));
        var familia = await SembrarFamiliaAsync(c, "Almacen");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);

        var (db, servicio) = CrearServicio(c, new RelojFijo(MomentoFijo));
        await using var _ = db;

        await servicio.AbrirNuevoPrecioAsync(
            a2, c.IdListaGeneral, 150m, null, false, ModoDeAlcanceDeFamilia.SoloEste);

        var releido = await db.Articulos.SingleAsync(a => a.Id == a2);
        Assert.Null(releido.IdFamilia);
        Assert.Equal(familia, (await db.Articulos.SingleAsync(a => a.Id == a1)).IdFamilia);
    }

    // =================================================================================================
    // Locks: membresía, filas de articulos y pares, vistos desde pg_locks
    // =================================================================================================

    private static async Task EjecutarAsync(
        NpgsqlConnection conexion, NpgsqlTransaction transaccion, string sql, params object[] parametros)
    {
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<T> EsperarAsync<T>(Func<Task<T?>> buscar, string mensaje)
        where T : class
    {
        var limite = DateTime.UtcNow.Add(EsperaMaxima);

        while (DateTime.UtcNow < limite)
        {
            if (await buscar() is { } encontrado)
            {
                return encontrado;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(mensaje);
    }

    /// <summary>El pid del backend que espera la fila de otra transacción (un <c>transactionid</c> sin
    /// conceder), o <c>null</c> si ninguno.</summary>
    private static async Task<Pid?> EsperandoUnaFilaAsync(NpgsqlConnection poll)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT pid FROM pg_locks WHERE locktype = 'transactionid' AND NOT granted", poll);

        return await comando.ExecuteScalarAsync() is int pid ? new Pid(pid) : null;
    }

    private sealed record Pid(int Valor);

    private sealed record Candado(int Pid, long ClassId, long ObjId, int ObjSubId, string Modo, bool Concedido);

    private static async Task<List<Candado>> CandadosAdvisoryAsync(NpgsqlConnection poll, string filtro, params object[] parametros)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT pid, classid::bigint, objid::bigint, objsubid, mode, granted FROM pg_locks " +
            $"WHERE locktype = 'advisory' AND {filtro}",
            poll);
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        var candados = new List<Candado>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            candados.Add(new Candado(
                lector.GetInt32(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetInt32(3),
                lector.GetString(4), lector.GetBoolean(5)));
        }

        return candados;
    }

    private static (long Alto, long Bajo) PartesDeLaClave(long clave) => (clave >> 32, clave & 0xFFFFFFFFL);

    /// <summary>El candado sin conceder (con el pid del backend que espera) del lock de membresía del
    /// tenant, o <c>null</c> si nadie lo espera. Una clave <c>bigint</c> se ve en <c>pg_locks</c> con
    /// <c>objsubid = 1</c>, la mitad alta en <c>classid</c> y la baja en <c>objid</c>.</summary>
    private static async Task<Candado?> EsperandoLaMembresiaAsync(NpgsqlConnection poll, int idTenant)
    {
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(idTenant));

        var esperando = await CandadosAdvisoryAsync(
            poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

        return esperando.SingleOrDefault();
    }

    /// <summary>Los locks advisory CONCEDIDOS al backend <paramref name="pid"/>.</summary>
    private static Task<List<Candado>> CandadosConcedidosAsync(NpgsqlConnection poll, int pid) =>
        CandadosAdvisoryAsync(poll, "granted AND pid = $1", pid);

    private static (long ClassId, long ObjId) ClaveDelPar(int idTenant, int idArticulo, int idLista)
    {
        var (clave1, clave2) = ServicioDePrecios.ClaveDeLockDePar(idTenant, idArticulo, idLista);

        return (clave1, unchecked((uint)clave2));
    }

    private async Task<(NpgsqlConnection Poll, NpgsqlConnection Sostenedor, NpgsqlTransaction Transaccion)> AbrirSostenedorAsync(
        int idTenant)
    {
        var sostenedor = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var transaccion = await sostenedor.BeginTransactionAsync();
        var poll = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        return (poll, sostenedor, transaccion);
    }

    /// <summary>Un escritor de membresía (stand-in de entrar/salir de una familia) sostiene el lock
    /// EXCLUSIVO y mueve a un artículo a la familia SIN commitear. La escritura de precios con alcance
    /// de familia queda esperando ESE lock (observado en <c>pg_locks</c>, pidiendo el modo
    /// compartido), y cuando el escritor comitea lee la pertenencia nueva: el artículo que entró recibe
    /// el precio. Sin el lock de membresía la escritura no espera, lee la familia vieja y comitea antes
    /// de que el artículo entre — la familia queda con miembros de precios distintos.</summary>
    [Fact]
    public async Task UnEscritorDeMembresiaConElLockExclusivoHaceEsperarAlPrecioYElPrecioVeLaFamiliaNueva()
    {
        using var c = await PrepararAsync(nameof(UnEscritorDeMembresiaConElLockExclusivoHaceEsperarAlPrecioYElPrecioVeLaFamiliaNueva));
        var familia = await SembrarFamiliaAsync(c, "Pastas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var queEntra = await SembrarArticuloAsync(c, "entra");
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, a2, c.IdListaGeneral, 110m);
        await SembrarPrecioVigenteAsync(c, queEntra, c.IdListaGeneral, 120m);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(c.IdTenant));
        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", familia, queEntra);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia");

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, c.IdTenant),
            "La escritura de precios nunca se observó esperando el lock de membresía: la prueba no está probando la carrera.");

        // Pide el modo COMPARTIDO (la escritura no cambia la pertenencia) y espera ANTES de tomar
        // ningún otro lock: la membresía es la primera sentencia de la transacción.
        Assert.Equal("ShareLock", esperando.Modo);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        foreach (var id in new[] { a1, a2, queEntra })
        {
            Assert.Equal(150m, (await FilaAbiertaAsync(id, c.IdListaGeneral))!.Monto);
        }

        Assert.Equal([a1, a2, queEntra], (await AuditoriaDePreciosAsync(c.IdTenant)).Select(a => a.IdEntidad).Order());
    }

    /// <summary>El lock compartido no espera a otro compartido: dos escrituras de precios que no
    /// cambian la pertenencia conviven. La prueba sostiene el lock compartido y exige que una escritura
    /// con alcance de familia (y una sin alcance sobre un artículo suelto) termine SIN liberarlo.</summary>
    [Fact]
    public async Task UnaEscrituraQueNoCambiaLaMembresiaNoEsperaAUnLockCompartidoAjeno()
    {
        using var c = await PrepararAsync(nameof(UnaEscrituraQueNoCambiaLaMembresiaNoEsperaAUnLockCompartidoAjeno));
        var familia = await SembrarFamiliaAsync(c, "Conservas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        var suelto = await SembrarArticuloAsync(c, "suelto");

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(c.IdTenant));

        var deLaFamilia = await PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia").WaitAsync(EsperaMaxima);
        var delSuelto = await PostPrecioAsync(c.Admin, suelto, c.IdListaGeneral, 90m).WaitAsync(EsperaMaxima);

        Assert.Equal(HttpStatusCode.Created, deLaFamilia.StatusCode);
        Assert.Equal(HttpStatusCode.Created, delSuelto.StatusCode);
        Assert.Equal(150m, (await FilaAbiertaAsync(a2, c.IdListaGeneral))!.Monto);
    }

    /// <summary>"Solo este" cambia la pertenencia y por eso pide el lock EXCLUSIVO: con un compartido
    /// ajeno sostenido, queda esperando (observado pidiendo <c>ExclusiveLock</c>) hasta que el otro
    /// lo libera.</summary>
    [Fact]
    public async Task SoloEstePideElLockExclusivoYEsperaAUnCompartidoAjeno()
    {
        using var c = await PrepararAsync(nameof(SoloEstePideElLockExclusivoYEsperaAUnCompartidoAjeno));
        var familia = await SembrarFamiliaAsync(c, "Verduras");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(c.IdTenant));

        var escritura = PostPrecioAsync(c.Admin, a2, c.IdListaGeneral, 150m, "SoloEste");

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, c.IdTenant),
            "El 'solo este' nunca se observó esperando el lock de membresía.");
        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(escritura.IsCompleted);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.Created, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Null(await IdFamiliaDeAsync(a2));
        Assert.Equal(familia, await IdFamiliaDeAsync(a1));
    }

    /// <summary>Orden de locks, paso (2) antes que el (3): un tercero sostiene <c>FOR UPDATE</c> sobre la
    /// fila del miembro de id más alto. La escritura con alcance de familia espera ESA fila —con el
    /// lock de membresía ya tomado— y todavía no tiene ningún lock de par. Si el bloqueo de filas se
    /// borrara, la escritura tomaría los locks de pares y recién después se trabaría (en la FK de
    /// <c>precios</c> contra <c>articulos</c>), con pares en la mano.</summary>
    [Fact]
    public async Task LasFilasDeLosMiembrosSeBloqueanDespuesDeLaMembresiaYAntesDeLosPares()
    {
        using var c = await PrepararAsync(nameof(LasFilasDeLosMiembrosSeBloqueanDespuesDeLaMembresiaYAntesDeLosPares));
        var familia = await SembrarFamiliaAsync(c, "Frutas");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", a2);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia");

        // El backend espera la fila: un lock de tipo transactionid sin conceder.
        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La escritura nunca se observó esperando la fila de un miembro.");

        var concedidos = await CandadosConcedidosAsync(poll, esperandoLaFila.Valor);
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(c.IdTenant));

        var unico = Assert.Single(concedidos);
        Assert.Equal(1, unico.ObjSubId);
        Assert.Equal((alto, bajo), (unico.ClassId, unico.ObjId));
        Assert.Equal("ShareLock", unico.Modo);

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.Created, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Equal(150m, (await FilaAbiertaAsync(a2, c.IdListaGeneral))!.Monto);
    }

    /// <summary>Las filas de los miembros se bloquean <c>FOR NO KEY UPDATE</c> y no <c>FOR UPDATE</c>:
    /// una venta toma <c>FOR KEY SHARE</c> sobre el artículo por sus FK, y no tiene que esperar —ni
    /// hacer esperar— a una escritura de precios de la familia.</summary>
    [Fact]
    public async Task UnaVentaQueTomaForKeyShareSobreUnMiembroNoHaceEsperarALaEscrituraDeLaFamilia()
    {
        using var c = await PrepararAsync(nameof(UnaVentaQueTomaForKeyShareSobreUnMiembroNoHaceEsperarALaEscrituraDeLaFamilia));
        var familia = await SembrarFamiliaAsync(c, "Bazar");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR KEY SHARE", a2);

        var respuesta = await PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia").WaitAsync(EsperaMaxima);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(150m, (await FilaAbiertaAsync(a2, c.IdListaGeneral))!.Monto);
    }

    /// <summary>Orden de los pares: ascendente por <c>id_articulo</c>. Un tercero sostiene el lock del
    /// par del miembro de id más alto; la escritura, que arranca por el de id más bajo, queda esperando
    /// el segundo CON el primero ya concedido. En orden inverso esperaría el de id más alto sin tener
    /// ninguno, y dos escrituras de la misma familia podrían cruzarse.</summary>
    [Fact]
    public async Task LosLocksDeParesSeTomanEnOrdenAscendenteDeArticulo()
    {
        using var c = await PrepararAsync(nameof(LosLocksDeParesSeTomanEnOrdenAscendenteDeArticulo));
        var familia = await SembrarFamiliaAsync(c, "Panaderia");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        Assert.True(a1 < a2);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        var (claseDelPar2, objetoDelPar2) = ClaveDelPar(c.IdTenant, a2, c.IdListaGeneral);
        var (clave1DelPar2, clave2DelPar2) = ServicioDePrecios.ClaveDeLockDePar(c.IdTenant, a2, c.IdListaGeneral);
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1, $2)", clave1DelPar2, clave2DelPar2);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia");

        var esperandoElPar = await EsperarAsync(
            async () => (await CandadosAdvisoryAsync(
                    poll, "NOT granted AND objsubid = 2 AND classid::bigint = $1 AND objid::bigint = $2",
                    claseDelPar2, objetoDelPar2)).SingleOrDefault(),
            "La escritura nunca se observó esperando el lock del par del segundo miembro.");

        var concedidos = await CandadosConcedidosAsync(poll, esperandoElPar.Pid);
        var (claseDelPar1, objetoDelPar1) = ClaveDelPar(c.IdTenant, a1, c.IdListaGeneral);

        Assert.Contains(concedidos, k => k.ObjSubId == 2 && k.ClassId == claseDelPar1 && k.ObjId == objetoDelPar1);
        Assert.Contains(concedidos, k => k.ObjSubId == 1);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.Created, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>En "solo este" la salida de la familia es un <c>UPDATE</c> de la fila del artículo, que
    /// la bloquea: paso (2) del orden, con el lock de membresía EXCLUSIVO ya tomado y antes de cualquier
    /// lock de par.</summary>
    [Fact]
    public async Task SoloEsteTomaLaMembresiaExclusivaYLaFilaAntesDeLosPares()
    {
        using var c = await PrepararAsync(nameof(SoloEsteTomaLaMembresiaExclusivaYLaFilaAntesDeLosPares));
        var familia = await SembrarFamiliaAsync(c, "Quesos");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        await SembrarArticuloAsync(c, "a2", familia);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", a1);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "SoloEste");

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El 'solo este' nunca se observó esperando la fila del artículo.");

        var unico = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        Assert.Equal(1, unico.ObjSubId);
        Assert.Equal("ExclusiveLock", unico.Modo);

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.Created, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Null(await IdFamiliaDeAsync(a1));
    }

    // =================================================================================================
    // Un escritor que NO respeta el protocolo: las guardas de defensa
    // =================================================================================================

    /// <summary>Una escritura que cambia la pertenencia sin tomar el lock de membresía (el protocolo
    /// lo exige, nada del esquema lo fuerza) saca al artículo pedido de la familia mientras la
    /// escritura de precios espera la fila de ese miembro. Al retomar, el artículo ya no es miembro: se
    /// rechaza con 409 <c>familia_cambio</c> en vez de aplicar a la familia un precio que ya no lo
    /// incluye.</summary>
    [Fact]
    public async Task SiElArticuloPedidoSaleDeLaFamiliaSinElLockLaEscrituraDeLaFamiliaSeRechaza()
    {
        using var c = await PrepararAsync(nameof(SiElArticuloPedidoSaleDeLaFamiliaSinElLockLaEscrituraDeLaFamiliaSeRechaza));
        var familia = await SembrarFamiliaAsync(c, "Helados");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        var a2 = await SembrarArticuloAsync(c, "a2", familia);
        await SembrarPrecioVigenteAsync(c, a1, c.IdListaGeneral, 100m);
        await SembrarPrecioVigenteAsync(c, a2, c.IdListaGeneral, 110m);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET id_familia = NULL WHERE id_articulo = $1", a1);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "Familia");

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La escritura nunca se observó esperando la fila del artículo que sale de la familia.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);

        foreach (var (id, monto) in new[] { (a1, 100m), (a2, 110m) })
        {
            var fila = Assert.Single(await FilasDePrecioAsync(id, c.IdListaGeneral));
            Assert.Equal(monto, fila.Monto);
            Assert.Null(fila.VigenteHasta);
        }

        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }

    /// <summary>Lo mismo para "solo este": el <c>UPDATE</c> que saca al artículo está guardado por la
    /// familia que se leyó bajo el lock. Si otro escritor ya la había cambiado, afecta cero filas y la
    /// solicitud se rechaza con 409 <c>familia_cambio</c> en vez de pisar la pertenencia nueva.</summary>
    [Fact]
    public async Task SiLaFamiliaCambioSinElLockElSoloEsteSeRechazaYNoPisaLaPertenenciaNueva()
    {
        using var c = await PrepararAsync(nameof(SiLaFamiliaCambioSinElLockElSoloEsteSeRechazaYNoPisaLaPertenenciaNueva));
        var familia = await SembrarFamiliaAsync(c, "Vinos");
        var otraFamilia = await SembrarFamiliaAsync(c, "Espumantes");
        var a1 = await SembrarArticuloAsync(c, "a1", familia);
        await SembrarArticuloAsync(c, "a2", familia);

        var (poll, sostenedor, transaccion) = await AbrirSostenedorAsync(c.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", otraFamilia, a1);

        var escritura = PostPrecioAsync(c.Admin, a1, c.IdListaGeneral, 150m, "SoloEste");

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El 'solo este' nunca se observó esperando la fila del artículo.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);

        Assert.Equal(otraFamilia, await IdFamiliaDeAsync(a1));
        Assert.Empty(await FilasDePrecioAsync(a1, c.IdListaGeneral));
        Assert.Empty(await AuditoriaDePreciosAsync(c.IdTenant));
    }
}
