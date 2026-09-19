using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Articulos;

/// <summary>
/// <see cref="ServicioDeArticulos"/> sobre el proveedor InMemory.
///
/// <see cref="ServicioDeArticulos.CrearAsync"/> completo (INSERT real) NO se cubre acá a
/// propósito: envuelve el INSERT en <c>Database.BeginTransactionAsync</c> (design.md,
/// <c>ServicioDeArticulos</c> doc-comment) — el proveedor InMemory no lo soporta, mismo motivo
/// por el que <c>ServicioDeClientesTests</c> tampoco cubre
/// <see cref="Clientes.ServicioDeClientes.CrearAsync"/> completo. Todas las validaciones de
/// este archivo corren ANTES de abrir esa transacción, así que sí son alcanzables acá; el alta
/// de punta a punta (incl. autogeneración de <c>codigo_interno</c>) se prueba contra Postgres
/// real en <c>ArticulosEndpointsTests</c> (Ways.IntegrationTests).
///
/// <see cref="ServicioDeArticulos.ActualizarAsync"/> TAMPOCO se cubre completo acá desde
/// fix/articulos-lock-referencias: ahora también abre <c>Database.BeginTransactionAsync</c> para
/// envolver los 5 chequeos de referencia lockeados (<see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>)
/// junto con el UPDATE que los usa — mismo "transaction-blocked-provider caveat" que
/// <see cref="ServicioDeArticulos.CrearAsync"/> de arriba (antes no aplicaba: el
/// <c>codigo_interno</c> no es editable). Las tres pruebas de round-trip que vivían acá (edición
/// básica de campos, subset de empresas persistido, de-dup de <c>IdsEmpresas</c> repetidos) se
/// movieron a <c>ArticulosEndpointsTests</c> (Ways.IntegrationTests, Postgres real) — ver
/// <c>EditarActualizaCamposBasicosDelArticulo</c>, <c>EditarConSubsetDeEmpresasPersisteElSubconjunto</c>
/// y <c>EditarConIdsEmpresasDuplicadosPersisteUnaSolaFila</c>. Las validaciones que corren ANTES
/// de abrir esa transacción (los chequeos en memoria, <c>ReglaDeArticulos</c>, el pre-chequeo de
/// empresas) siguen alcanzables acá y NO se movieron.
/// </summary>
public class ServicioDeArticulosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

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

    private static ServicioDeArticulos CrearServicio(string nombreDeBase, int idTenant)
    {
        var db = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var reloj = new RelojFijo(Ahora);
        var contexto = new ContextoFijo(idTenant);

        // stage-12-lotes-vencimientos, Slice 4: sin fila de stock sembrada en ninguno de estos
        // tests, ReconciliarAsync retorna antes de abrir transacción (pares.Count == 0) — el
        // InMemory provider, que NO soporta BeginTransactionAsync (doc-comment de la clase),
        // nunca llega a ese camino acá. El disparador real (con Stock) se prueba en
        // ReconciliacionTests (Ways.IntegrationTests), contra Postgres.
        return new ServicioDeArticulos(
            db, reloj, contexto, new ServicioDeLotes(db, reloj, contexto),
            new GuardaDeReferencias(db, new InspectorDeUso(db)));
    }

    private static async Task<(int IdArea, int IdAlicuotaIva)> SembrarCatalogosAsync(string nombreDeBase, int idTenant)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var area = new Area { IdTenant = idTenant, Nombre = "Almacén", Orden = 1, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Areas.Add(area);

        var alicuota = new AlicuotaIva { Nombre = "21%", Porcentaje = 21m, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.AlicuotasIva.Add(alicuota);

        await siembra.SaveChangesAsync();
        return (area.Id, alicuota.Id);
    }

    private static async Task<int> SembrarEmpresaAsync(string nombreDeBase, int idTenant)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = "Empresa de prueba", CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Empresas.Add(empresa);

        await siembra.SaveChangesAsync();
        return empresa.Id;
    }

    private static async Task<Articulo> SembrarArticuloAsync(
        string nombreDeBase, int idTenant, int idArea, int idAlicuotaIva, string codigoInterno = "1",
        bool disponibleParaTodas = true)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = codigoInterno,
            Nombre = "Artículo de prueba",
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            DisponibleParaTodas = disponibleParaTodas,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };

        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();
        return articulo;
    }

    private static AltaArticulo AltaValida(int idArea, int idAlicuotaIva, string codigoInterno = "1001") =>
        new(
            CodigoInterno: codigoInterno,
            Nombre: "Coca Cola 500ml",
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

    private static EdicionArticulo EdicionValida(int idArea, int idAlicuotaIva, string nombre = "Editado") =>
        new(
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
            CostoNominal: null,
            DisponibleParaTodas: true,
            IdsEmpresas: null,
            Activo: true,
            ControlaLote: false);

    [Fact]
    public async Task CrearSinIdAreaEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (_, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea: 0, idAlicuotaIva);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("id_area_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinIdAlicuotaIvaEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, _) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva: 0);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("id_alicuota_iva_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinNombreEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva) with { Nombre = "   " };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("nombre_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // CrearConIdAreaInexistenteEsRechazado y CrearConIdAreaDeOtroTenantEsRechazado se retiraron
    // de acá desde fix/articulos-lock-referencias: los 5 chequeos de referencia lockeados de
    // CrearAsync (área incluida) ahora corren DENTRO de la transacción explícita, fuera del
    // alcance del proveedor InMemory — mismo "transaction-blocked-provider caveat" del resto de
    // CrearAsync (ver el doc-comment de la clase). La primera ya estaba duplicada por
    // ArticulosEndpointsTests.CrearConIdAreaInexistenteDevuelve400 (Postgres real), así que se
    // retira sin reemplazo; la segunda se movió a
    // ArticulosEndpointsTests.CrearConIdAreaDeOtroTenantDevuelve400.

    [Fact]
    public async Task CrearConIdAlicuotaIvaInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, _) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva: 999_999);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: Restricting availability requires at least one subset row — mismo guard
    /// que <c>ReglaDeArticulosTests</c>, acá a través del camino real de
    /// <see cref="ServicioDeArticulos.CrearAsync"/> (crear directamente con
    /// disponible_para_todas=false sin subconjunto).</summary>
    [Fact]
    public async Task CrearConDisponibleParaTodasFalseSinSubsetEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva) with { DisponibleParaTodas = false, IdsEmpresas = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: Cross-tenant empresa reference is blocked — pre-chequeo tenant-scoped
    /// antes de escribir cualquier fila de <c>articulos_empresas</c>.</summary>
    [Fact]
    public async Task CrearConEmpresaDeOtroTenantEnElSubsetEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var idEmpresaDeOtroTenant = await SembrarEmpresaAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva) with
        {
            DisponibleParaTodas = false,
            IdsEmpresas = [idEmpresaDeOtroTenant]
        };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: Duplicate user-supplied codigo_interno is rejected (pre-chequeo de
    /// servicio; el backstop real de 23505 se prueba contra Postgres real en
    /// ArticulosEndpointsTests, task 2.8).</summary>
    [Fact]
    public async Task CrearConCodigoInternoDuplicadoEnElMismoTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, codigoInterno: "1001");
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idArea, idAlicuotaIva, codigoInterno: "1001");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("codigo_interno_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF ya
    /// deja invisible la fila de otro tenant antes de que el servicio decida nada.</summary>
    [Fact]
    public async Task ObtenerUnArticuloDeOtroTenantDevuelve404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 2);
        var ajeno = await SembrarArticuloAsync(nombreDeBase, idTenant: 2, idArea, idAlicuotaIva);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(ajeno.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    // EditarUnArticuloFunciona se movió a ArticulosEndpointsTests.EditarActualizaCamposBasicosDelArticulo
    // (Postgres real) — ver el doc-comment de la clase.

    /// <summary>Spec: Restricting availability requires at least one subset row — a través del
    /// camino real de edición (true -&gt; false sin subset).</summary>
    [Fact]
    public async Task EditarConDisponibleParaTodasFalseSinSubsetEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(
            nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, disponibleParaTodas: true);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = EdicionValida(idArea, idAlicuotaIva) with { DisponibleParaTodas = false, IdsEmpresas = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(articulo.Id, datos));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>judgment-day ronda 1 (root cause, item 1a): un artículo YA restringido que se
    /// vuelve a guardar con <c>IdsEmpresas</c> en <c>null</c> (false -&gt; false, sin
    /// transición) tiene que rechazarse igual que una restricción nueva — antes de este fix
    /// esquivaba el guard de <c>ReglaDeArticulos</c> y reventaba con un
    /// <see cref="NullReferenceException"/> en <c>ExigirEmpresasValidasAsync</c>.</summary>
    [Fact]
    public async Task EditarUnArticuloYaRestringidoConIdsEmpresasNuloEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var idEmpresa = await SembrarEmpresaAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(
            nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, disponibleParaTodas: false);

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            siembra.ArticulosEmpresas.Add(new ArticuloEmpresa { IdArticulo = articulo.Id, IdEmpresa = idEmpresa, IdTenant = 1 });
            await siembra.SaveChangesAsync();
        }

        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = EdicionValida(idArea, idAlicuotaIva) with { DisponibleParaTodas = false, IdsEmpresas = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(articulo.Id, datos));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);

        await using var lectura = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));
        var filas = await lectura.ArticulosEmpresas.Where(ae => ae.IdArticulo == articulo.Id).ToListAsync();
        Assert.Single(filas);
    }

    /// <summary>judgment-day ronda 1 (item 1b): mismo caso que el anterior, pero con
    /// <c>IdsEmpresas = []</c> en vez de <c>null</c> — mismo 400, y el subset existente NO se
    /// borra (la excepción se dispara antes de tocar <c>ArticulosEmpresas</c>).</summary>
    [Fact]
    public async Task EditarUnArticuloYaRestringidoConIdsEmpresasVacioEsRechazadoYNoBorraElSubset()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var idEmpresa = await SembrarEmpresaAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(
            nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, disponibleParaTodas: false);

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            siembra.ArticulosEmpresas.Add(new ArticuloEmpresa { IdArticulo = articulo.Id, IdEmpresa = idEmpresa, IdTenant = 1 });
            await siembra.SaveChangesAsync();
        }

        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = EdicionValida(idArea, idAlicuotaIva) with { DisponibleParaTodas = false, IdsEmpresas = [] };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(articulo.Id, datos));

        Assert.Equal("subset_de_empresas_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);

        await using var lectura = CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, 1));
        var filas = await lectura.ArticulosEmpresas.Where(ae => ae.IdArticulo == articulo.Id).ToListAsync();
        Assert.Single(filas);
        Assert.Equal(idEmpresa, filas[0].IdEmpresa);
    }

    // EditarConDisponibleParaTodasFalseConSubsetFunciona se movió a
    // ArticulosEndpointsTests.EditarConSubsetDeEmpresasPersisteElSubconjunto (Postgres real).

    // EditarConIdsEmpresasDuplicadosInsertaUnaSolaFila se movió a
    // ArticulosEndpointsTests.EditarConIdsEmpresasDuplicadosPersisteUnaSolaFila (Postgres real).

    /// <summary>judgment-day ronda 1 (item 2): el detalle de un artículo restringido expone su
    /// subset actual, para que un cliente pueda armar un PUT de no-op sin perder las filas.</summary>
    [Fact]
    public async Task ObtenerUnArticuloRestringidoDevuelveSuSubsetDeEmpresas()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var idEmpresa = await SembrarEmpresaAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(
            nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, disponibleParaTodas: false);

        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            siembra.ArticulosEmpresas.Add(new ArticuloEmpresa { IdArticulo = articulo.Id, IdEmpresa = idEmpresa, IdTenant = 1 });
            await siembra.SaveChangesAsync();
        }

        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var detalle = await servicio.ObtenerAsync(articulo.Id);

        Assert.False(detalle.DisponibleParaTodas);
        Assert.Equal([idEmpresa], detalle.IdsEmpresas);
    }

    [Fact]
    public async Task EliminarUnArticuloFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        await servicio.EliminarAsync(articulo.Id);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(articulo.Id));
        Assert.Equal("no_encontrado", error.Codigo);
    }

    [Fact]
    public async Task AgregarCodigoBarraFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var creado = await servicio.AgregarCodigoBarraAsync(articulo.Id, new AltaCodigoBarra("7791234567890"));

        Assert.Equal("7791234567890", creado.Codigo);
        Assert.Equal(articulo.Id, creado.IdArticulo);
    }

    /// <summary>Spec: Duplicate barcode within the same tenant is rejected (pre-chequeo de
    /// servicio; el backstop real se prueba contra Postgres real en ArticulosEndpointsTests,
    /// task 2.9).</summary>
    [Fact]
    public async Task AgregarCodigoBarraDuplicadoEnElMismoTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var articuloUno = await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, codigoInterno: "1");
        var articuloDos = await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva, codigoInterno: "2");
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        await servicio.AgregarCodigoBarraAsync(articuloUno.Id, new AltaCodigoBarra("7791234567890"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.AgregarCodigoBarraAsync(articuloDos.Id, new AltaCodigoBarra("7791234567890")));

        Assert.Equal("codigo_barra_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EliminarCodigoBarraFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var articulo = await SembrarArticuloAsync(nombreDeBase, idTenant: 1, idArea, idAlicuotaIva);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);
        var codigoBarra = await servicio.AgregarCodigoBarraAsync(articulo.Id, new AltaCodigoBarra("7791234567890"));

        await servicio.EliminarCodigoBarraAsync(articulo.Id, codigoBarra.Id);

        // Sin backstop de excepción propio: el código dado de baja queda reutilizable
        // (índice parcial WHERE deleted_at IS NULL), mismo criterio que el cuit de un
        // proveedor dado de baja.
        var reagregado = await servicio.AgregarCodigoBarraAsync(articulo.Id, new AltaCodigoBarra("7791234567890"));
        Assert.Equal("7791234567890", reagregado.Codigo);
    }

    /// <summary>ADR-8: mismo 404 uniforme cuando el artículo padre no existe (o es de otro
    /// tenant) al gestionar sus códigos de barra.</summary>
    [Fact]
    public async Task AgregarCodigoBarraAUnArticuloInexistenteDevuelve404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.AgregarCodigoBarraAsync(999_999, new AltaCodigoBarra("7791234567890")));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }

    /// <summary>Spec: Margin-Based Price Suggestion, "Grupo margin wins over proveedor
    /// margin" — acá se prueba el wiring de <see cref="ServicioDeArticulos.SugerirPrecioAsync"/>
    /// (resolución de <c>grupos.margen</c>/<c>proveedores.margen</c> desde las referencias del
    /// artículo), no la lógica pura en sí (ver <c>SugeridorDePrecioTests</c>, Ways.Domain.Tests).</summary>
    [Fact]
    public async Task SugerirPrecioUsaElMargenDelGrupoDelArticulo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArea, idAlicuotaIva) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);

        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var grupo = new Grupo { IdTenant = 1, Nombre = "Gaseosas", Margen = 30m, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Grupos.Add(grupo);
        await siembra.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = 1,
            CodigoInterno = "1",
            Nombre = "Con grupo",
            IdArea = idArea,
            IdAlicuotaIva = idAlicuotaIva,
            IdGrupo = grupo.Id,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CostoNominal = 100m,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var sugerencia = await servicio.SugerirPrecioAsync(articulo.Id);

        Assert.Equal(130m, sugerencia.PrecioSugerido);
    }
}
