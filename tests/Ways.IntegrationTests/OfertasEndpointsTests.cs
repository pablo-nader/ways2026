using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones; // ModoDeAcceso
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios; // SolicitudDeLogin
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 2 (tasks 2.6-2.10, db-error-backstops skill): <c>ServicioDeOfertas</c>/
/// <c>OfertasEndpoints</c> punta a punta contra Postgres real — ABM completo con la policy
/// <c>GestionDeCatalogo</c> (admin-only), el 404 uniforme cross-tenant (ADR-8), el estado
/// persistido del targeting de listas (spec: Multi-Lista Targeting via ofertas_listas), la
/// carrera genuina de <c>pk_ofertas_listas</c> (design: Backstop Map) y los FK smoke tests de
/// las referencias nuevas de esta etapa.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private async Task<(int IdTenant, int IdGrupo, string MailAdmin, string PasswordAdmin)>
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

        var grupo = new Grupo { IdTenant = resultado!.IdTenant, Nombre = $"{nombre}-grupo", CreatedAt = ahora, UpdatedAt = ahora };
        db.Grupos.Add(grupo);
        await db.SaveChangesAsync();

        return (resultado.IdTenant, grupo.Id, mailAdmin, resultado.PasswordTemporal);
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

    private async Task<int> SembrarCategoriaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var categoria = new Categoria { IdTenant = idTenant, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();

        return categoria.Id;
    }

    private async Task<int> SembrarArticuloAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
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

    private async Task<int> SembrarEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa.Id;
    }

    private async Task<int> SembrarListaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    private static AltaOferta AltaValida(int idGrupo, IReadOnlyList<int>? idsListas = null) => new(
        Nombre: "2x1 Verano",
        IdEmpresa: null,
        IdArticulo: null,
        IdGrupo: idGrupo,
        IdCategoria: null,
        FechaDesde: null,
        FechaHasta: null,
        HoraDesde: null,
        HoraHasta: null,
        DiasSemana: null,
        CantidadMinima: null,
        PrecioUnitario: null,
        Porcentaje: 10m,
        ImporteFijo: null,
        Prioridad: 0,
        Acumulable: false,
        IdsListas: idsListas);

    private static EdicionOferta EdicionDesde(OfertaListado oferta, IReadOnlyList<int>? idsListas) => new(
        Nombre: oferta.Nombre,
        IdEmpresa: oferta.IdEmpresa,
        IdArticulo: oferta.IdArticulo,
        IdGrupo: oferta.IdGrupo,
        IdCategoria: oferta.IdCategoria,
        FechaDesde: oferta.FechaDesde,
        FechaHasta: oferta.FechaHasta,
        HoraDesde: oferta.HoraDesde,
        HoraHasta: oferta.HoraHasta,
        DiasSemana: oferta.DiasSemana,
        CantidadMinima: oferta.CantidadMinima,
        PrecioUnitario: oferta.PrecioUnitario,
        Porcentaje: oferta.Porcentaje,
        ImporteFijo: oferta.ImporteFijo,
        Prioridad: oferta.Prioridad,
        Acumulable: oferta.Acumulable,
        IdsListas: idsListas,
        Activo: oferta.Activo);

    private static async Task<OfertaListado> CrearOfertaAsync(
        HttpClient cliente, int idGrupo, IReadOnlyList<int>? idsListas = null)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupo, idsListas));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<OfertaListado>())!;
    }

    // ---- task 2.6: ABM round trip + autorización ----------------------------------------------

    [Fact]
    public async Task UnAdminCreaYDaDeBajaUnaOferta()
    {
        var (_, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(UnAdminCreaYDaDeBajaUnaOferta));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo);
        Assert.Equal("2x1 Verano", creada.Nombre);

        var baja = await admin.DeleteAsync($"/api/ofertas/{creada.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var obtener = await admin.GetAsync($"/api/ofertas/{creada.Id}");
        // Baja lógica: el filtro global de EF la deja invisible por el camino normal de lectura.
        Assert.Equal(HttpStatusCode.NotFound, obtener.StatusCode);

        var listado = await admin.GetFromJsonAsync<List<OfertaListado>>("/api/ofertas");
        Assert.DoesNotContain(listado!, o => o.Id == creada.Id);
    }

    [Fact]
    public async Task UnVendedorNoPuedeCrearOfertas()
    {
        var (idTenant, idGrupo, _, _) = await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeCrearOfertas));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeCrearOfertas));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupo));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorNoPuedeEditarOfertas()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeEditarOfertas));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var creada = await CrearOfertaAsync(admin, idGrupo);

        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeEditarOfertas));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.PutAsJsonAsync(
            $"/api/ofertas/{creada.Id}", EdicionDesde(creada, idsListas: null));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 2.7: cross-tenant → 404 uniforme (ADR-8) -----------------------------------------

    [Fact]
    public async Task UnaOfertaDeOtroTenantDevuelve404()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(UnaOfertaDeOtroTenantDevuelve404) + "-A");
        var (_, _, mailAdminB, passwordAdminB) = await AprovisionarTenantAsync(nameof(UnaOfertaDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var ofertaDeA = await CrearOfertaAsync(adminA, idGrupoA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.GetAsync($"/api/ofertas/{ofertaDeA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnPutSobreUnaOfertaDeOtroTenantDevuelve404()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(UnPutSobreUnaOfertaDeOtroTenantDevuelve404) + "-A");
        var (_, idGrupoB, mailAdminB, passwordAdminB) = await AprovisionarTenantAsync(nameof(UnPutSobreUnaOfertaDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var ofertaDeA = await CrearOfertaAsync(adminA, idGrupoA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var edicion = EdicionDesde(ofertaDeA, idsListas: null) with { IdGrupo = idGrupoB };
        var respuesta = await adminB.PutAsJsonAsync($"/api/ofertas/{ofertaDeA.Id}", edicion);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnDeleteSobreUnaOfertaDeOtroTenantDevuelve404()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(UnDeleteSobreUnaOfertaDeOtroTenantDevuelve404) + "-A");
        var (_, _, mailAdminB, passwordAdminB) = await AprovisionarTenantAsync(nameof(UnDeleteSobreUnaOfertaDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var ofertaDeA = await CrearOfertaAsync(adminA, idGrupoA);

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.DeleteAsync($"/api/ofertas/{ofertaDeA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 2.8: ofertas_listas ---------------------------------------------------------------

    /// <summary>Spec: No junction rows targets every lista — a nivel de ABM (Slice 2, sin
    /// resolver todavía) esto se prueba por el ESTADO PERSISTIDO: sin <c>IdsListas</c>, no se
    /// crea ninguna fila de <c>ofertas_listas</c> (la semántica de "aplica a todas" la interpreta
    /// el resolver, Slice 3).</summary>
    [Fact]
    public async Task CrearSinIdsListasNoCreaNingunaFilaDeJuncion()
    {
        var (_, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(CrearSinIdsListasNoCreaNingunaFilaDeJuncion));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo);

        Assert.Empty(creada.IdsListas);

        var detalle = await admin.GetFromJsonAsync<OfertaListado>($"/api/ofertas/{creada.Id}");
        Assert.Empty(detalle!.IdsListas);
    }

    /// <summary>Spec: Junction rows restrict targeting — el estado persistido refleja
    /// exactamente el subconjunto enviado.</summary>
    [Fact]
    public async Task CrearConIdsListasPersisteExactamenteEseSubconjunto()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(CrearConIdsListasPersisteExactamenteEseSubconjunto));
        var idListaUno = await SembrarListaAsync(idTenant, "Lista 1");
        var idListaDos = await SembrarListaAsync(idTenant, "Lista 2");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo, idsListas: [idListaUno, idListaDos]);

        Assert.Equal([idListaUno, idListaDos], creada.IdsListas.OrderBy(i => i));
    }

    /// <summary>Spec: Junction row references must belong to the same tenant.</summary>
    [Fact]
    public async Task CrearConListaDeOtroTenantDevuelve400ReferenciaInvalida()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(CrearConListaDeOtroTenantDevuelve400ReferenciaInvalida) + "-A");
        var (idTenantB, _, _, _) = await AprovisionarTenantAsync(nameof(CrearConListaDeOtroTenantDevuelve400ReferenciaInvalida) + "-B");
        var idListaDeB = await SembrarListaAsync(idTenantB, "Lista de B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuesta = await adminA.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupoA, idsListas: [idListaDeB]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Spec: Junction rows restrict targeting, aplicado a la edición — el PUT reemplaza
    /// por completo el subconjunto anterior.</summary>
    [Fact]
    public async Task EditarReemplazaElSubconjuntoDeListasPersistido()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(EditarReemplazaElSubconjuntoDeListasPersistido));
        var idListaUno = await SembrarListaAsync(idTenant, "Lista 1");
        var idListaDos = await SembrarListaAsync(idTenant, "Lista 2");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo, idsListas: [idListaUno]);

        var edicion = EdicionDesde(creada, idsListas: [idListaDos]);
        var respuestaEdicion = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        Assert.Equal(HttpStatusCode.OK, respuestaEdicion.StatusCode);

        var editada = await respuestaEdicion.Content.ReadFromJsonAsync<OfertaListado>();
        Assert.Equal([idListaDos], editada!.IdsListas);
    }

    // ---- task 2.9: pk_ofertas_listas race -------------------------------------------------------

    /// <summary>design: Backstop Map — <c>pk_ofertas_listas</c> es la única superficie
    /// genuinamente racy de esta etapa. Dos PUT concurrentes reemplazando el MISMO conjunto de
    /// listas de la MISMA oferta: sin ningún lock que serialice la carrera por construcción
    /// (mismo criterio que <c>ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoDeBarraDaExactamenteUnGanador</c>)
    /// — exactamente un ganador (200), el perdedor un 409 traducido, nunca un 500.</summary>
    [Fact]
    public async Task DosPutsConcurrentesReemplazandoElMismoConjuntoDeListasDanExactamenteUnGanador()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(DosPutsConcurrentesReemplazandoElMismoConjuntoDeListasDanExactamenteUnGanador));
        var idLista = await SembrarListaAsync(idTenant, "Lista carrera");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo);
        var edicion = EdicionDesde(creada, idsListas: [idLista]);

        var tareaA = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        var tareaB = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.DoesNotContain(HttpStatusCode.InternalServerError, estados);
        Assert.Contains(HttpStatusCode.OK, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oferta_lista_duplicada", problema.GetProperty("codigo").GetString());

        // El estado final es consistente: exactamente una fila de targeting sobrevive, no dos
        // ni cero (el ganador la insertó, el perdedor nunca comiteó la suya).
        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();
        Assert.Single(filas);
        Assert.Equal(idLista, filas[0].IdListaPrecio);
    }

    // ---- task 2.10: FK smoke tests ---------------------------------------------------------------

    [Fact]
    public async Task CrearConIdGrupoInexistenteDevuelve400()
    {
        var (_, _, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(CrearConIdGrupoInexistenteDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var respuesta = await admin.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupo: 999_999));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdCategoriaDeOtroTenantDevuelve400()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(CrearConIdCategoriaDeOtroTenantDevuelve400) + "-A");
        var (idTenantB, _, _, _) = await AprovisionarTenantAsync(nameof(CrearConIdCategoriaDeOtroTenantDevuelve400) + "-B");
        var idCategoriaDeB = await SembrarCategoriaAsync(idTenantB, "Categoría de B");
        _ = idGrupoA;

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = AltaValida(idGrupo: 0) with { IdGrupo = null, IdCategoria = idCategoriaDeB };
        var respuesta = await adminA.PostAsJsonAsync("/api/ofertas", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdArticuloInexistenteDevuelve400()
    {
        var (_, _, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(CrearConIdArticuloInexistenteDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = AltaValida(idGrupo: 0) with { IdGrupo = null, IdArticulo = 999_999 };
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdArticuloDeOtroTenantDevuelve400()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(CrearConIdArticuloDeOtroTenantDevuelve400) + "-A");
        var (idTenantB, _, _, _) = await AprovisionarTenantAsync(nameof(CrearConIdArticuloDeOtroTenantDevuelve400) + "-B");
        var idArticuloDeB = await SembrarArticuloAsync(idTenantB, "Artículo de B");
        _ = idGrupoA;

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = AltaValida(idGrupo: 0) with { IdGrupo = null, IdArticulo = idArticuloDeB };
        var respuesta = await adminA.PostAsJsonAsync("/api/ofertas", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task CrearConIdEmpresaDeOtroTenantDevuelve400()
    {
        var (_, idGrupoA, mailAdminA, passwordAdminA) = await AprovisionarTenantAsync(nameof(CrearConIdEmpresaDeOtroTenantDevuelve400) + "-A");
        var (idTenantB, _, _, _) = await AprovisionarTenantAsync(nameof(CrearConIdEmpresaDeOtroTenantDevuelve400) + "-B");
        var idEmpresaDeB = await SembrarEmpresaAsync(idTenantB, "Empresa de B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = AltaValida(idGrupoA) with { IdEmpresa = idEmpresaDeB };
        var respuesta = await adminA.PostAsJsonAsync("/api/ofertas", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.11: regression smoke -------------------------------------------------------------

    [Fact]
    public async Task ListarDevuelveLasOfertasDelTenantOrdenadasPorNombre()
    {
        var (_, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(ListarDevuelveLasOfertasDelTenantOrdenadasPorNombre));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        await admin.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupo) with { Nombre = "Zeta" });
        await admin.PostAsJsonAsync("/api/ofertas", AltaValida(idGrupo) with { Nombre = "Alfa" });

        var listado = await admin.GetFromJsonAsync<List<OfertaListado>>("/api/ofertas");

        Assert.NotNull(listado);
        var nombres = listado!.Select(o => o.Nombre).ToList();
        Assert.Equal(nombres.OrderBy(n => n, StringComparer.Ordinal), nombres);
    }
}
