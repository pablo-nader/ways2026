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
    /// Reusa <c>InterceptorDeRendezVousOfertas</c> para forzar DELETE-gana-el-lock de forma
    /// DETERMINÍSTICA (no solo "concurrencia genuina"): el punto de rendezvous del DELETE es su
    /// <c>BuscarAsync</c> POST-lock (<see cref="ServicioDeOfertas.EliminarAsync"/> ya no tiene
    /// ninguna otra consulta a <c>ofertas</c>) — para que el DELETE llegue ahí, YA tiene que haber
    /// tomado el <c>pg_advisory_xact_lock</c> primero. El punto de rendezvous del PUT es su
    /// <c>BuscarAsync</c> PRE-transacción (antes de siquiera intentar el lock). El interceptor no
    /// libera a ninguno de los dos hasta que AMBOS llegaron a su respectivo punto, así que,
    /// estructuralmente, el DELETE siempre tiene el lock tomado ANTES de que el PUT llegue a
    /// pedirlo — el PUT SIEMPRE espera detrás del DELETE, nunca al revés. Estable por
    /// construcción, no por suerte de scheduling: se corre 3 veces (estado aislado por iteración)
    /// para confirmarlo, no para "promediar" un resultado probabilístico.</summary>
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

            using var gate = new CountdownEvent(2);
            var interceptor = new InterceptorDeRendezVousOfertas(gate);
            await using var factory = fixture.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddDbContext<WaysDbContext>((_, options) =>
                        options.AddInterceptors(interceptor))));

            using var admin = factory.CreateClient();
            var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, passwordAdmin));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var tareaPut = admin.PutAsJsonAsync($"/api/ofertas/{creada.Id}", edicion);
            var tareaDelete = admin.DeleteAsync($"/api/ofertas/{creada.Id}");

            await Task.WhenAll(tareaPut, tareaDelete);
            var respuestaPut = await tareaPut;
            var respuestaDelete = await tareaDelete;

            Assert.True(interceptor.Participantes >= 2, $"iteración={iteracion} participantes={interceptor.Participantes}");

            // El DELETE siempre gana la carrera del lock (ver doc-comment de arriba) — el PUT ve
            // la oferta ya eliminada y responde el 404 uniforme, nunca un 200 con campos pisados.
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

    /// <summary>Retiene la primera consulta EF a <c>ofertas</c> (la lectura de
    /// <c>ServicioDeOfertas.BuscarAsync</c>, el primer acceso a datos de <c>ActualizarAsync</c>,
    /// ANTES de que abra su transacción y tome el <c>pg_advisory_xact_lock</c>) hasta que ambos
    /// participantes llegaron — mismo mecanismo que
    /// <c>PreciosEndpointsTests.InterceptorDeRendezVousListasPrecio</c>. El filtro excluye
    /// <c>ofertas_listas</c> explícitamente porque su nombre de tabla comparte el prefijo
    /// "ofertas".</summary>
    private sealed class InterceptorDeRendezVousOfertas(CountdownEvent gate) : DbCommandInterceptor
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
            if (!command.CommandText.Contains("ofertas", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("ofertas_listas", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

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
