using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ofertas;

/// <summary>
/// <see cref="ServicioDeOfertas"/> sobre el proveedor InMemory.
///
/// <see cref="ServicioDeOfertas.CrearAsync"/> COMPLETO (round-trip persistido) NO se cubre acá a
/// propósito: envuelve el INSERT en <c>Database.BeginTransactionAsync</c> (doc-comment de la
/// clase) — el proveedor InMemory no lo soporta, mismo motivo por el que
/// <c>ServicioDeArticulosTests</c> tampoco cubre <c>ServicioDeArticulos.CrearAsync</c> completo.
/// Todas las validaciones de este archivo (incl. las cinco guardas de <c>ReglaDeOfertas</c> y
/// los pre-chequeos de referencia) corren ANTES de abrir esa transacción, así que sí son
/// alcanzables acá; el alta de punta a punta se prueba contra Postgres real en
/// <c>OfertasEndpointsTests</c> (Ways.IntegrationTests).
///
/// <see cref="ServicioDeOfertas.ActualizarAsync"/> COMPLETO (round-trip persistido, incluido el
/// reemplazo del subconjunto de listas) TAMPOCO se cubre acá desde el fix de judgment-day (item
/// 1, stage-4-ofertas): ahora también envuelve el replace-set en
/// <c>Database.BeginTransactionAsync</c> + un <c>pg_advisory_xact_lock</c> por oferta (mismo
/// motivo/mismo "transaction-blocked-provider caveat" que <see cref="ServicioDeOfertas.CrearAsync"/>
/// de arriba). Las tres pruebas de round-trip que vivían acá (edición básica de campos, reemplazo
/// del subconjunto de listas, de-dup de <c>IdsListas</c> repetidos) se movieron a
/// <c>OfertasEndpointsTests</c> (Ways.IntegrationTests, Postgres real) — ver
/// <c>EditarActualizaCamposBasicosDeLaOferta</c>, <c>EditarReemplazaElSubconjuntoDeListasPersistido</c>
/// y <c>EditarConIdsListasDuplicadosPersisteUnaSolaFila</c>. Las validaciones que corren ANTES de
/// abrir esa transacción (las cinco guardas de <c>ReglaDeOfertas</c>, los pre-chequeos de
/// referencia) siguen alcanzables acá y NO se movieron.
///
/// <see cref="ServicioDeOfertas.EliminarAsync"/> TAMPOCO se cubre acá desde el fix de judgment-day
/// ronda 2 (item 1, CRITICAL): ahora también abre <c>Database.BeginTransactionAsync</c> + toma el
/// mismo <c>pg_advisory_xact_lock</c> por oferta que <see cref="ServicioDeOfertas.ActualizarAsync"/>
/// (para serializarse contra un PUT concurrente y evitar el ghost edit) — mismo
/// "transaction-blocked-provider caveat" de arriba. La prueba que vivía acá
/// (<c>EliminarUnaOfertaFunciona</c>) ya estaba duplicada por
/// <c>OfertasEndpointsTests.UnAdminCreaYDaDeBajaUnaOferta</c> (Postgres real, alta + baja + 404 +
/// ausencia en el listado), así que se retira de acá sin reemplazo — esa prueba de integración
/// sigue cubriendo el mismo camino.
/// </summary>
public class ServicioDeOfertasTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeOfertas CrearServicio(string nombreDeBase, int idTenant) =>
        new(
            CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)),
            new RelojFijo(Ahora),
            new ContextoFijo(idTenant));

    private static async Task<int> SembrarGrupoAsync(string nombreDeBase, int idTenant, string nombre = "Gaseosas")
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var grupo = new Grupo { IdTenant = idTenant, Nombre = nombre, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Grupos.Add(grupo);
        await siembra.SaveChangesAsync();

        return grupo.Id;
    }

    private static async Task<int> SembrarListaAsync(string nombreDeBase, int idTenant, string nombre = "General")
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();

        return lista.Id;
    }

    private static async Task<int> SembrarEmpresaAsync(string nombreDeBase, int idTenant)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = "Empresa de prueba", CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        return empresa.Id;
    }

    private static async Task<Oferta> SembrarOfertaAsync(
        string nombreDeBase, int idTenant, int idGrupo, string nombre = "2x1 Verano")
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var oferta = new Oferta
        {
            IdTenant = idTenant,
            Nombre = nombre,
            IdGrupo = idGrupo,
            Porcentaje = 10m,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Ofertas.Add(oferta);
        await siembra.SaveChangesAsync();

        return oferta;
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

    private static EdicionOferta EdicionValida(int idGrupo, IReadOnlyList<int>? idsListas = null) => new(
        Nombre: "2x1 Verano editada",
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
        Porcentaje: 15m,
        ImporteFijo: null,
        Prioridad: 1,
        Acumulable: true,
        IdsListas: idsListas,
        Activo: true);

    // ---- required-field validation ----------------------------------------------------------

    [Fact]
    public async Task CrearSinNombreEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { Nombre = "   " };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("nombre_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- spec: Domain guard rejects invalid shapes before the database -----------------------

    [Fact]
    public async Task CrearSinNingunAlcanceEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo: 0) with { IdGrupo = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("oferta_alcance_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConDosAlcancesEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { IdCategoria = 999 };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("oferta_alcance_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinNingunBeneficioEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { Porcentaje = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("oferta_beneficio_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConDosBeneficiosEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { ImporteFijo = 5m };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("oferta_beneficio_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConCantidadMinimaCeroEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { CantidadMinima = 0m };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("cantidad_minima_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConVentanaDeFechasInvertidaEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with
        {
            FechaDesde = new DateOnly(2026, 8, 10),
            FechaHasta = new DateOnly(2026, 8, 1)
        };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("ventana_de_oferta_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConDiaDeSemanaFueraDeRangoEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { DiasSemana = [8] };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("dias_semana_invalidos", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- spec: Invalid scope reference maps to 400 -------------------------------------------

    [Fact]
    public async Task CrearConIdGrupoInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo: 999_999);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>El filtro de EF ya deja afuera un grupo de OTRO tenant, así que da el mismo 400
    /// que "no existe" — misma paridad que <c>ServicioDeArticulosTests.CrearConIdAreaDeOtroTenantEsRechazado</c>.</summary>
    [Fact]
    public async Task CrearConIdGrupoDeOtroTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupoDeOtroTenant = await SembrarGrupoAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupoDeOtroTenant);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConIdEmpresaDeOtroTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var idEmpresaDeOtroTenant = await SembrarEmpresaAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo) with { IdEmpresa = idEmpresaDeOtroTenant };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- spec: Junction row references must belong to the same tenant ------------------------

    [Fact]
    public async Task CrearConIdListaDeOtroTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var idListaDeOtroTenant = await SembrarListaAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idGrupo, idsListas: [idListaDeOtroTenant]);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- ADR-8: cross-tenant 404 --------------------------------------------------------------

    [Fact]
    public async Task ObtenerUnaOfertaDeOtroTenantDevuelve404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 2);
        var ajena = await SembrarOfertaAsync(nombreDeBase, idTenant: 2, idGrupo);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(ajena.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    // ---- ActualizarAsync: round-trip persistido movido a OfertasEndpointsTests (judgment-day,
    // item 1) — ActualizarAsync ahora abre transacción explícita (pg_advisory_xact_lock por
    // oferta), fuera del alcance del proveedor InMemory. Ver
    // EditarActualizaCamposBasicosDeLaOferta, EditarReemplazaElSubconjuntoDeListasPersistido y
    // EditarConIdsListasDuplicadosPersisteUnaSolaFila en Ways.IntegrationTests.

    [Fact]
    public async Task ObtenerUnaOfertaSinFilasDeListasDevuelveConjuntoVacio()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idGrupo = await SembrarGrupoAsync(nombreDeBase, idTenant: 1);
        var oferta = await SembrarOfertaAsync(nombreDeBase, idTenant: 1, idGrupo);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var detalle = await servicio.ObtenerAsync(oferta.Id);

        Assert.Empty(detalle.IdsListas);
    }
}
