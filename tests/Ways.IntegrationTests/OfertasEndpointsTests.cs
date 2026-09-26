using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones; // ModoDeAcceso
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
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
/// Slice 2 (tasks 2.6-2.10, db-error-backstops skill): <c>ServicioDeOfertas</c>/
/// <c>OfertasEndpoints</c> punta a punta contra Postgres real — ABM completo con la policy
/// <c>GestionDeCatalogo</c> (admin-only), el 404 uniforme cross-tenant (ADR-8), el estado
/// persistido del targeting de listas (spec: Multi-Lista Targeting via ofertas_listas), la
/// serialización real del replace-set de <c>ofertas_listas</c> bajo PUT concurrentes
/// (judgment-day, item 1 — <c>pg_advisory_xact_lock</c> por oferta, mismo mecanismo que
/// <see cref="Ways.Application.Precios.ServicioDePrecios"/>) y los FK smoke tests de las
/// referencias nuevas de esta etapa.
///
/// (judgment-day, item 1) <see cref="ServicioDeOfertas.ActualizarAsync"/> completo (edición de
/// campos básicos, reemplazo del subconjunto de listas, de-dup de <c>IdsListas</c>) se cubre
/// ACÁ desde el fix del lock — antes vivía parcialmente en <c>ServicioDeOfertasTests</c>
/// (Ways.Application.Tests, proveedor InMemory), que ya no lo soporta porque
/// <c>ActualizarAsync</c> ahora abre transacción explícita.
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
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);

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

    /// <summary>(judgment-day, hallazgo A-1) La PRECEDENCIA entre el 404 y el 400 del PUT: un id que
    /// no existe rinde 404 AUNQUE el body sea inválido, nunca 400. Cuando la lectura de la oferta se
    /// mudó adentro de la transacción (skill <c>single-read-under-lock</c>), la validación del payload
    /// quedó primero y ese caso pasó a 400 — un cambio de contrato que ningún test fijaba. La cláusula
    /// que este test prueba es el fast-path <c>EXISTS</c> del tope de
    /// <see cref="ServicioDeOfertas.ActualizarAsync"/>; borrarlo devuelve 400 en la primera mitad.
    ///
    /// Las dos mitades son la MISMA afirmación y las dos hacen falta: sin la segunda, un body que
    /// resultara VÁLIDO haría pasar la primera por la razón equivocada (cualquier PUT a un id
    /// inexistente da 404). La segunda prueba que ese mismo body es de verdad un 400 cuando la oferta
    /// existe, así que el 404 de la primera solo puede venir del fast-path.</summary>
    [Fact]
    public async Task UnPutConBodyInvalidoSobreUnaOfertaInexistenteDa404YNo400()
    {
        var (_, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnPutConBodyInvalidoSobreUnaOfertaInexistenteDa404YNo400));

        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var creada = await CrearOfertaAsync(admin, idGrupo);

        var bodyInvalido = EdicionDesde(creada, idsListas: null) with { Nombre = "   " };

        var inexistente = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id + 100_000}", bodyInvalido);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);

        var existente = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", bodyInvalido);
        Assert.Equal(HttpStatusCode.BadRequest, existente.StatusCode);
        var problema = await existente.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nombre_requerido", problema.GetProperty("codigo").GetString());
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
    /// por completo el subconjunto anterior.
    ///
    /// (judgment-day ronda 2, item 3, triage Judge A) Antes solo aserteaba el <c>IdsListas</c>
    /// ECOADO en la respuesta del PUT — eso prueba lo que <c>Proyectar</c> devuelve, no lo que
    /// quedó escrito en <c>ofertas_listas</c>. Agrega una lectura independiente de la fila (GET +
    /// consulta directa a la DB, mismo patrón que
    /// <c>EditarConIdsListasVacioExplicitoRevierteAlAlcanceDeTodasLasListas</c>) para asertar el
    /// estado PERSISTIDO, no el eco.</summary>
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

        var detalle = await admin.GetFromJsonAsync<OfertaListado>($"/api/ofertas/{creada.Id}");
        Assert.Equal([idListaDos], detalle!.IdsListas);

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();
        Assert.Equal([idListaDos], filas.Select(f => f.IdListaPrecio));
    }

    /// <summary>Movido desde <c>ServicioDeOfertasTests.EditarUnaOfertaFunciona</c> (judgment-day,
    /// item 1): <c>ActualizarAsync</c> ahora abre transacción explícita, fuera del alcance del
    /// proveedor InMemory. Cubre la edición de campos básicos sin tocar el targeting de
    /// listas.</summary>
    [Fact]
    public async Task EditarActualizaCamposBasicosDeLaOferta()
    {
        var (_, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(EditarActualizaCamposBasicosDeLaOferta));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo);
        var edicion = EdicionDesde(creada, idsListas: null) with
        {
            Nombre = "2x1 Verano editada",
            Porcentaje = 15m,
            Prioridad = 1,
            Acumulable = true
        };

        var respuesta = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var editada = await respuesta.Content.ReadFromJsonAsync<OfertaListado>();
        Assert.Equal("2x1 Verano editada", editada!.Nombre);
        Assert.Equal(15m, editada.Porcentaje);
        Assert.Equal(1, editada.Prioridad);
        Assert.True(editada.Acumulable);
    }

    /// <summary>Movido desde <c>ServicioDeOfertasTests.EditarConIdsListasDuplicadosInsertaUnaSolaFila</c>
    /// (judgment-day, item 1) — mismo motivo que <c>EditarActualizaCamposBasicosDeLaOferta</c>.</summary>
    [Fact]
    public async Task EditarConIdsListasDuplicadosPersisteUnaSolaFila()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nameof(EditarConIdsListasDuplicadosPersisteUnaSolaFila));
        var idLista = await SembrarListaAsync(idTenant, "Lista");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo);
        var edicion = EdicionDesde(creada, idsListas: [idLista, idLista]);

        var respuesta = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var editada = await respuesta.Content.ReadFromJsonAsync<OfertaListado>();
        Assert.Equal([idLista], editada!.IdsListas);
    }

    /// <summary>Spec: "No junction rows targets every lista", aplicado a la edición — revertir
    /// una oferta previamente restringida a un subconjunto enviando <c>IdsListas: []</c>
    /// explícito borra las filas de targeting existentes y no inserta ninguna nueva (orchestrator
    /// triage, judgment-day item 3): el estado persistido vuelve a "aplica a todas las
    /// listas".</summary>
    [Fact]
    public async Task EditarConIdsListasVacioExplicitoRevierteAlAlcanceDeTodasLasListas()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(EditarConIdsListasVacioExplicitoRevierteAlAlcanceDeTodasLasListas));
        var idLista = await SembrarListaAsync(idTenant, "Lista restringida");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo, idsListas: [idLista]);
        Assert.Equal([idLista], creada.IdsListas);

        var edicion = EdicionDesde(creada, idsListas: Array.Empty<int>());
        var respuesta = await admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var editada = await respuesta.Content.ReadFromJsonAsync<OfertaListado>();
        Assert.Empty(editada!.IdsListas);

        var detalle = await admin.GetFromJsonAsync<OfertaListado>($"/api/ofertas/{creada.Id}");
        Assert.Empty(detalle!.IdsListas);

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();
        Assert.Empty(filas);
    }

    // ---- task 2.9 / judgment-day item 1: pk_ofertas_listas race, serializada por el lock -------

    /// <summary>design: Backstop Map — <c>pk_ofertas_listas</c> es la única superficie
    /// genuinamente racy de esta etapa.
    ///
    /// <b>Reescrito en judgment-day (item 1), mismo criterio que
    /// <c>PreciosEndpointsTests.LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden</c>:</b>
    /// antes de este fix, dos PUT concurrentes reemplazando el MISMO conjunto de listas de la
    /// MISMA oferta competían sin ningún lock que serializara la carrera por construcción — un
    /// ganador (200) y un perdedor (409 <c>oferta_lista_duplicada</c>). Ahora
    /// <c>ServicioDeOfertas.ActualizarAsync</c> toma un <c>pg_advisory_xact_lock</c>
    /// determinístico por oferta ANTES de releer <c>ofertas_listas</c> — el segundo llamador
    /// espera el lock y, al retomarlo, ve el estado YA COMITEADO por el primero, así que hace un
    /// reemplazo LIMPIO (delete-then-insert del MISMO par) en vez de competir contra el índice:
    /// las dos escrituras se serializan de verdad y las DOS suceden (2×200), nunca un 409 ni un
    /// 500. El backstop de esquema (<c>pk_ofertas_listas</c>, <c>ManejadorDeErrores</c> → 409
    /// <c>oferta_lista_duplicada</c>) se mantiene igual como defensa de esquema — solo queda
    /// alcanzable por una escritura cruda/fuera de banda que bypasee el servicio.
    ///
    /// El rendezvous con <c>InterceptorDeRendezVousOfertas</c> fuerza que las dos transacciones
    /// arranquen genuinamente solapadas — sin esto, el pool/JIT ya calientes podrían dejar que la
    /// primera termine antes de que la segunda arranque, y el lock nunca llegaría a contenderse
    /// de verdad.</summary>
    [Fact]
    public async Task DosPutsConcurrentesReemplazandoElMismoConjuntoDeListasSeSerializanYAmbosSuceden()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(DosPutsConcurrentesReemplazandoElMismoConjuntoDeListasSeSerializanYAmbosSuceden));
        var idLista = await SembrarListaAsync(idTenant, "Lista carrera");

        using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var creada = await CrearOfertaAsync(admin0, idGrupo);
        var edicion = EdicionDesde(creada, idsListas: [idLista]);

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousOfertas(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) =>
                    options.AddInterceptors(interceptor))));

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
        var tareaB = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.All(estados, e => Assert.Equal(HttpStatusCode.OK, e));

        // El estado final es consistente: exactamente una fila de targeting sobrevive, no dos
        // ni cero — el último committer reemplaza limpio, nunca una unión ni un DELETE fantasma.
        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();
        Assert.Single(filas);
        Assert.Equal(idLista, filas[0].IdListaPrecio);
    }

    /// <summary>NUEVO (judgment-day, item 3): dos PUT concurrentes reemplazando el conjunto de
    /// listas de la MISMA oferta con targets DISTINTOS (A → lista uno, B → lista dos), sobre una
    /// oferta PRE-POBLADA con un subconjunto previo. Antes del fix (item 1), esto era exactamente
    /// el hallazgo CRITICAL confirmado por los dos jueces: sin ningún lock, las dos lecturas de
    /// <c>filasActuales</c> partían del mismo estado previo, y el orden de commit podía dejar la
    /// UNIÓN de ambos targets persistida (lost update silencioso) o, si las dos intentaban borrar
    /// la misma fila previa, un <c>DbUpdateConcurrencyException</c> sin traducir (500 crudo) en
    /// el perdedor.
    ///
    /// Con el fix: las dos escrituras se serializan por el <c>pg_advisory_xact_lock</c> por
    /// oferta — nunca un 500, y el conjunto final persistido es EXACTAMENTE el target de UNO de
    /// los dos llamadores (el último committer), nunca la unión de ambos ni un conjunto vacío por
    /// accidente. Repetido 3 veces con estado aislado por iteración (tenant/oferta nuevos) para
    /// probar estabilidad — no un resultado de una sola corrida con suerte.
    ///
    /// (judgment-day ronda 2, item 2) Tolerar 409 acá era un falso negativo: el lock serializa de
    /// verdad, así que las DOS escrituras SIEMPRE tienen que suceder (2×200) — exactamente lo que
    /// ya asegura <c>DosPutsConcurrentesReemplazandoElMismoConjuntoDeListasSeSerializanYAmbosSuceden</c>
    /// para el caso de targets iguales. Un 409 acá indicaría que el reemplazo del perdedor volvió
    /// a competir contra <c>pk_ofertas_listas</c> en vez de encontrar el estado ya comiteado tras
    /// el lock — señal de que el fix se rompió, no un resultado válido a tolerar.</summary>
    [Fact]
    public async Task DosPutsConcurrentesConTargetsDistintosSeSerializanYElUltimoCommitPersisteExactamenteUnTarget()
    {
        for (var iteracion = 0; iteracion < 3; iteracion++)
        {
            var nombreDeCorrida = $"{nameof(DosPutsConcurrentesConTargetsDistintosSeSerializanYElUltimoCommitPersisteExactamenteUnTarget)}-{iteracion}";
            var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nombreDeCorrida);
            var idListaPrevia = await SembrarListaAsync(idTenant, "Lista previa");
            var idListaA = await SembrarListaAsync(idTenant, "Lista target A");
            var idListaB = await SembrarListaAsync(idTenant, "Lista target B");

            using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
            var creada = await CrearOfertaAsync(admin0, idGrupo, idsListas: [idListaPrevia]);

            var edicionA = EdicionDesde(creada, idsListas: [idListaA]);
            var edicionB = EdicionDesde(creada, idsListas: [idListaB]);

            using var gate = new CountdownEvent(2);
            var interceptor = new InterceptorDeRendezVousOfertas(gate);
            await using var factory = fixture.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddDbContext<WaysDbContext>((_, options) =>
                        options.AddInterceptors(interceptor))));

            using var admin = factory.CreateClient();
            var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var tareaA = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicionA);
            var tareaB = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicionB);

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            var estados = respuestas.Select(r => r.StatusCode).ToList();

            Assert.True(interceptor.Participantes >= 2, $"iteración={iteracion} participantes={interceptor.Participantes}");
            // (judgment-day ronda 2, item 2) Estrictamente las DOS 200 — el lock serializa de
            // verdad, así que tolerar un 409 acá esconde una regresión (ver doc-comment).
            Assert.All(estados, e => Assert.Equal(HttpStatusCode.OK, e));

            await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
            var filas = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();

            // Nunca la unión de los dos targets (2 filas) ni un conjunto vacío por accidente (0
            // filas) — exactamente UNA fila, y es el target de A o el de B, nunca la previa.
            Assert.Single(filas);
            Assert.Contains(filas[0].IdListaPrecio, new[] { idListaA, idListaB });
        }
    }

    /// <summary>db-error-backstops (judgment-day ronda 2, item 4a, triage Judge B): coverage gap
    /// del backstop <c>pk_ofertas_listas</c> — hasta acá la única prueba de esta PK era la carrera
    /// serializada por el lock (que nunca la alcanza, por diseño). Mismo patrón que
    /// <c>ArticulosEndpointsTests.UnaFilaDeSubsetDuplicadaInsertadaPorFueraDelServicioViolaLaPk</c>:
    /// INSERT crudo por SQL que bypasea <c>ServicioDeOfertas</c> por completo para forzar el
    /// duplicado <c>(id_oferta, id_lista_precio)</c> directamente contra la constraint de
    /// esquema.</summary>
    [Fact]
    public async Task UnaFilaDeOfertasListasDuplicadaInsertadaPorFueraDelServicioViolaLaPk()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnaFilaDeOfertasListasDuplicadaInsertadaPorFueraDelServicioViolaLaPk));
        var idLista = await SembrarListaAsync(idTenant, "Lista backstop");
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var creada = await CrearOfertaAsync(admin, idGrupo, idsListas: [idLista]);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ofertas_listas (id_oferta, id_lista_precio, id_tenant) VALUES ($1, $2, $3)";
        comando.Parameters.Add(new NpgsqlParameter { Value = creada.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLista });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("pk_ofertas_listas", excepcion.ConstraintName);
    }

    /// <summary>NUEVO (judgment-day ronda 2, item 1 — CRITICAL): PUT y DELETE concurrentes sobre
    /// la MISMA oferta. Antes del fix, <c>EliminarAsync</c> no abría transacción ni tomaba lock —
    /// un PUT podía leer la oferta viva, un DELETE concurrente comiteaba primero fuera de
    /// cualquier lock, y el PUT (que nunca revalidaba <c>DeletedAt</c>) terminaba pisando los
    /// campos editables sobre una fila YA ELIMINADA: ghost edit, <c>deleted_at</c> seteado a la
    /// vez que los campos/targeting frescos del PUT persistidos con un 200.
    ///
    /// Fuerza DELETE-gana-la-carrera de forma DETERMINÍSTICA (no solo "concurrencia genuina") con
    /// <c>InterceptorDePausaTrasIniciarLaTransaccion</c>: el PUT queda pausado justo después de
    /// abrir su transacción —antes de su primer statement, o sea antes de pedir el
    /// <c>pg_advisory_xact_lock</c>—, el DELETE corre ENTERO sobre el cliente sin interceptor y
    /// comitea, y solo entonces el PUT retoma. El PUT SIEMPRE ve la baja ya comiteada, nunca al
    /// revés.
    ///
    /// Antes la asimetría la daba <c>InterceptorDeRendezVousOfertas</c>: el DELETE gateaba en su
    /// <c>BuscarAsync</c> POST-lock (así que llegaba con el lock ya tomado) y el PUT en su
    /// <c>BuscarAsync</c> PRE-transacción (antes de pedirlo). Esa asimetría desapareció cuando el
    /// PUT dejó de tener una lectura pre-lock (skill <c>single-read-under-lock</c>): con los dos
    /// puntos del mismo lado del lock, el barrier se auto-bloqueaba. La pausa es más simple y más
    /// fuerte — no depende de dónde caiga la lectura de cada lado, solo de que el PUT abra su
    /// transacción primero y decida después.
    ///
    /// Lo que el test prueba no cambió: el PUT lee la oferta bajo el lock y se niega. La cláusula
    /// exacta es la EXISTENCIA de esa lectura post-lock, no cuál instancia se muta: con la lectura
    /// devuelta a antes de la transacción PERO conservando una consulta post-lock descartada, el
    /// test sigue VERDE —el filtro <c>BajaLogica</c> de esa consulta ya da 0 filas y el 404 sale
    /// igual— y es correcto que siga verde, porque ese mutante no reintroduce ningún ghost edit.
    /// El mutante que sí mata a este test es borrar la lectura post-lock ENTERA (verificado: el PUT
    /// pasa a contestar 200 y pisa los campos). El lost update silencioso es una cláusula distinta,
    /// con su propio test —<see cref="ElPutQuePierdeLaCarreraNoPierdeSuActivoPorLaFotoPreLock"/>—,
    /// que sí muere cuando la instancia mutada vuelve a ser la pre-lock.
    ///
    /// Se corre 3 veces con estado aislado por iteración para confirmar que es estable por
    /// construcción, no por suerte de scheduling.</summary>
    [Fact]
    public async Task UnPutYUnDeleteConcurrentesNuncaProducenUnGhostEdit()
    {
        for (var iteracion = 0; iteracion < 3; iteracion++)
        {
            var nombreDeCorrida = $"{nameof(UnPutYUnDeleteConcurrentesNuncaProducenUnGhostEdit)}-{iteracion}";
            var (idTenant, idGrupo, mailAdmin, passwordAdmin) = await AprovisionarTenantAsync(nombreDeCorrida);
            var idListaPrevia = await SembrarListaAsync(idTenant, "Lista previa");
            var idListaNueva = await SembrarListaAsync(idTenant, "Lista nueva");

            using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
            var creada = await CrearOfertaAsync(admin0, idGrupo, idsListas: [idListaPrevia]);

            var edicion = EdicionDesde(creada, idsListas: [idListaNueva]) with { Nombre = "2x1 Verano editada" };

            var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);
            await using var factory = fixture.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddDbContext<WaysDbContext>((_, options) =>
                        options.AddInterceptors(interceptor))));

            using var admin = factory.CreateClient();
            var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var tareaPut = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);

            // Que esta espera termine es la prueba de que el PUT ya abrió su transacción y todavía
            // no pidió el lock: el DELETE recién entonces corre y comitea entero.
            await transaccionIniciada.Task;

            var respuestaDelete = await admin0.DeleteAsync($"/api/ofertas/{creada.Id}");

            puedeContinuar.TrySetResult();

            var respuestaPut = await tareaPut;

            // El DELETE siempre gana la carrera (ver doc-comment de arriba) — el PUT relee bajo su
            // lock, ve la oferta ya eliminada y responde el 404 uniforme, nunca un 200 con campos
            // pisados.
            Assert.Equal(HttpStatusCode.NoContent, respuestaDelete.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, respuestaPut.StatusCode);

            await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
            var ofertaFinal = await lectura.Ofertas.IgnoreQueryFilters(["BajaLogica"]).FirstAsync(o => o.Id == creada.Id);
            var filasFinal = await lectura.OfertasListas.Where(ol => ol.IdOferta == creada.Id).ToListAsync();

            // Estado final: soft-deleted, con los campos y el targeting ORIGINALES — el PUT no
            // tocó absolutamente nada, ni un campo ni una fila de ofertas_listas.
            Assert.NotNull(ofertaFinal.DeletedAt);
            Assert.Equal("2x1 Verano", ofertaFinal.Nombre);
            Assert.Equal([idListaPrevia], filasFinal.Select(f => f.IdListaPrecio));
        }
    }

    /// <summary>
    /// <c>single-read-under-lock</c> / <c>mutation-proof-tests</c> — LA CLÁUSULA: la ÚNICA lectura
    /// de la oferta en <see cref="ServicioDeOfertas.ActualizarAsync"/> nace DESPUÉS de
    /// <c>TomarLockDeOfertaAsync</c>. El perdedor queda pausado justo al abrir su transacción
    /// manual —ANTES de siquiera pedir el lock, mismo punto que
    /// <c>OrganizacionTests.ElFlipDeModoQuePierdeLaCarreraAuditaYEscribeSobreElEstadoQueVioBajoElLock</c>—
    /// mientras el ganador corre su PUT completo sin pausa (cliente pelado de <c>fixture</c>, nunca
    /// pasa por el interceptor) y comitea <c>Activo = false</c>. Bajo el fix, el perdedor retoma,
    /// toma el lock YA LIBRE, y su ÚNICA lectura nace ahí: ve el <c>Activo = false</c> recién
    /// comiteado, así que pedir <c>Activo = true</c> SÍ es un cambio para EF y la columna entra al
    /// <c>UPDATE</c>. Bajo el mutante (lectura pre-lock que se muta, con la lectura post-lock
    /// reducida a un discard sobre el identity map), el perdedor muta la instancia vieja cuyo valor
    /// ORIGINAL de <c>Activo</c> ya era <c>true</c> (el seed) — pedir <c>true</c> no es un cambio
    /// para EF, la columna se omite del <c>UPDATE</c>, y la fila se queda en el
    /// <c>Activo = false</c> del ganador mientras el 200 del perdedor devuelve <c>true</c>.
    ///
    /// LA ÚNICA ASERCIÓN DISCRIMINANTE es sobre la FILA de la base releída con un contexto nuevo:
    /// <c>Activo == true</c>. <c>Prioridad == 5</c> en la MISMA fila es contexto —prueba que el
    /// <c>UPDATE</c> realmente corrió y que bajo el mutante solo <c>activo</c> se hubiera caído—
    /// pero ni <c>Prioridad</c> ni el cuerpo de ninguna de las dos respuestas mueren bajo el
    /// mutante (el cuerpo del perdedor proyecta la instancia ya mutada en memoria, así que afirma
    /// <c>true</c> aunque la base diga <c>false</c>), así que esta prueba afirma UNA sola cosa.
    /// </summary>
    [Fact]
    public async Task ElPutQuePierdeLaCarreraNoPierdeSuActivoPorLaFotoPreLock()
    {
        var (idTenant, idGrupo, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(ElPutQuePierdeLaCarreraNoPierdeSuActivoPorLaFotoPreLock));

        using var admin0 = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var altaConPrioridad = AltaValida(idGrupo) with { Prioridad = 1 };
        var respuestaAlta = await admin0.PostAsJsonAsync("/api/ofertas", altaConPrioridad);
        Assert.Equal(HttpStatusCode.Created, respuestaAlta.StatusCode);
        var creada = (await respuestaAlta.Content.ReadFromJsonAsync<OfertaListado>())!;
        Assert.True(creada.Activo);
        Assert.Equal(1, creada.Prioridad);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clientePerdedor = factory.CreateClient();
        var loginPerdedor = await clientePerdedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
        Assert.Equal(HttpStatusCode.OK, loginPerdedor.StatusCode);

        // Perdedor: Activo = true (sin cambio respecto del seed), Prioridad = 5.
        var edicionPerdedor = EdicionDesde(creada, idsListas: null) with { Prioridad = 5 };
        var tareaPerdedor = clientePerdedor.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicionPerdedor);

        await transaccionIniciada.Task;

        // Ganador: Activo = false, Prioridad = 1 (sin cambio respecto del seed) — corre y comitea
        // ENTERO mientras el perdedor sigue pausado.
        using var clienteGanador = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);
        var edicionGanador = EdicionDesde(creada, idsListas: null) with { Activo = false };
        var respuestaGanadora = await clienteGanador.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicionGanador);
        var cuerpoGanador = await respuestaGanadora.Content.ReadAsStringAsync();
        Assert.True(respuestaGanadora.StatusCode == HttpStatusCode.OK, cuerpoGanador);

        puedeContinuar.TrySetResult();

        var respuestaPerdedora = await tareaPerdedor;
        var cuerpoPerdedor = await respuestaPerdedora.Content.ReadAsStringAsync();
        Assert.True(respuestaPerdedora.StatusCode == HttpStatusCode.OK, cuerpoPerdedor);

        await using var lectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await lectura.Ofertas.FirstAsync(o => o.Id == creada.Id);

        // ÚNICA aserción discriminante.
        Assert.True(fila.Activo);

        // Contexto (ver doc-comment) — no discrimina el mutante por sí sola.
        Assert.Equal(5, fila.Prioridad);
    }

    /// <summary>Retiene la APERTURA de la transacción de cada participante
    /// (<c>BeginTransactionAsync</c>: después del BEGIN, antes del primer statement y por lo tanto
    /// antes del <c>pg_advisory_xact_lock</c>) hasta que los dos llegaron — mismo mecanismo que
    /// <c>PreciosEndpointsTests.InterceptorDeRendezVousListasPrecio</c>, un escalón más arriba.
    ///
    /// Antes el punto de rendezvous era la primera consulta EF a <c>ofertas</c>, que era la lectura
    /// PRE-transacción de <c>ServicioDeOfertas.BuscarAsync</c>. Ese punto YA NO EXISTE: la única
    /// lectura de la oferta nació adentro de la transacción y después del lock (skill
    /// <c>single-read-under-lock</c>). Gatear ahí dejaba al primer participante esperando un
    /// compañero que no podía llegar —el segundo estaba bloqueado detrás del advisory lock que el
    /// primero ya tenía—, así que el barrier moría por timeout y el 500 tapaba lo que el test
    /// afirmaba.
    ///
    /// La apertura de la transacción es el último punto que los dos participantes alcanzan SIN
    /// haber pedido el lock, y es por eso el único que sirve para forzar solapamiento genuino: los
    /// dos abren, los dos se liberan, y desde ahí contienden DE VERDAD por el lock. Cada request
    /// abre exactamente una transacción, así que el contador de participantes es exacto sin
    /// filtrar por tabla (antes había que excluir <c>ofertas_listas</c> a mano por el prefijo
    /// compartido).</summary>
    private sealed class InterceptorDeRendezVousOfertas(CountdownEvent gate) : DbTransactionInterceptor
    {
        private int _participantes;

        public int Participantes => _participantes;

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Esperar();
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }

        private void Esperar()
        {
            if (Interlocked.Increment(ref _participantes) > 2)
            {
                return;
            }

            gate.Signal();

            var senializo = gate.Wait(TimeSpan.FromSeconds(10));
            Assert.True(senializo, "El rendezvous de InterceptorDeRendezVousOfertas no llegó a los 2 participantes a tiempo.");
        }
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
