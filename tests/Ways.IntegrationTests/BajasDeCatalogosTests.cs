using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// fix/bajas-catalogos-guarda-de-uso: el guard de <c>GuardaDeReferencias</c> sobre los 6
/// catálogos de tenant (áreas, categorías, marcas, grupos, medios de pago, listas de precio) y
/// proveedores, contra Postgres real y sobre <c>ways_app</c> (RLS aplicado) — mismo criterio que
/// <c>BajasDeOrganizacionTests</c>, del que reusa el patrón de siembra/lectura.
///
/// Por qué acá y no en Application: el guard abre transacción y hace ADO crudo
/// (<c>GuardaDeReferencias.BloquearFilaAsync</c>), que el proveedor InMemory no soporta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BajasDeCatalogosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string SqlStateTransitorio = "40001";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private long _numeroClienteSecuencial = 1000;

    private sealed record Sembrado(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdAdmin, string MailAdmin, string PasswordAdmin);

    // ---- siembra de tenant ----------------------------------------------------------------------

    private async Task<Sembrado> AprovisionarAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}";
        var mailAdmin = $"{unico}@ways.test";

        using var root = fixture.CreateClient();
        var login = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SRL", $"{unico} - Local 1", mailAdmin));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        return new Sembrado(
            resultado!.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin,
            mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteAdminAsync(Sembrado s)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(s.MailAdmin, s.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private WaysDbContext ContextoDelTenant(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private WaysDbContext ContextoConReintentos(int idTenant, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra) =>
        fixture.CrearContextoDeAplicacionConReintentos(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant), extra);

    // ---- lectura de respuestas --------------------------------------------------------------------

    private static async Task<(string Codigo, string Mensaje)> LeerConflictoAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return (cuerpo.GetProperty("codigo").GetString()!, cuerpo.GetProperty("title").GetString()!);
    }

    private static async Task<string> LeerCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        Assert.Equal(esperado, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return cuerpo.GetProperty("codigo").GetString()!;
    }

    // ---- catálogos vía API ---------------------------------------------------------------------

    private static async Task<AreaListado> CrearAreaAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/catalogos/areas", new AreaAlta(nombre, null, 1));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<AreaListado>())!;
    }

    private static async Task<CategoriaListado> CrearCategoriaAsync(HttpClient cliente, string nombre, int? idPadre = null)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/categorias", new CategoriaAlta(nombre, null, 1, idPadre));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CategoriaListado>())!;
    }

    private static async Task<MarcaListado> CrearMarcaAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/catalogos/marcas", new MarcaAlta(nombre, null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<MarcaListado>())!;
    }

    private static async Task<GrupoListado> CrearGrupoAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/grupos", new GrupoAlta(nombre, null, Margen: null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<GrupoListado>())!;
    }

    private static async Task<MedioPagoListado> CrearMedioPagoAsync(HttpClient cliente, string nombre)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/medios-pago",
            new MedioPagoAlta(
                nombre, null, Orden: 1, Comportamiento: ComportamientoMedioPago.Efectivo,
                AdmiteVuelto: true, RequiereReferencia: false, RecargoPorcentaje: null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
    }

    private static async Task<ListaPrecioListado> CrearListaPrecioAsync(
        HttpClient cliente, string nombre, ModoLista modo = ModoLista.Fija, int? idListaBase = null,
        decimal? porcentaje = null, bool activo = true)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/catalogos/listas-precio",
            new ListaPrecioAlta(nombre, null, EsDefault: false, modo, idListaBase, porcentaje, activo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ListaPrecioListado>(OpcionesJson))!;
    }

    private async Task<ProveedorListado> CrearProveedorAsync(HttpClient cliente, string nombre)
    {
        var idCondicionFiscal = await IdDeCondicionFiscalAsync();
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/proveedores",
            new AltaProveedor(
                RazonSocial: nombre, NombreFantasia: null, Cuit: null, IdCondicionFiscal: idCondicionFiscal,
                Domicilio: null, Telefono: null, Email: null, Vendedor: null, CelularVendedor: null,
                Supervisor: null, CelularSupervisor: null, Margen: null, Observaciones: null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ProveedorListado>())!;
    }

    private async Task<int> IdDeCondicionFiscalAsync()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
    }

    private async Task<int> IdDeAlicuotaIvaAsync()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
    }

    // ---- siembra directa de las filas referenciantes --------------------------------------------

    private async Task<int> SembrarArticuloAsync(
        int idTenant, int idArea, DateTimeOffset instante, int? idCategoria = null, int? idMarca = null,
        int? idGrupo = null, int? idProveedorHabitual = null)
    {
        var idAlicuota = await IdDeAlicuotaIvaAsync();

        await using var db = ContextoDelTenant(idTenant);
        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = Guid.NewGuid().ToString("N")[..12],
            Nombre = "Artículo del guard de referencias",
            IdArea = idArea,
            IdCategoria = idCategoria,
            IdMarca = idMarca,
            IdGrupo = idGrupo,
            IdProveedorHabitual = idProveedorHabitual,
            IdAlicuotaIva = idAlicuota,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            DisponibleParaTodas = true,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarTurnoAbiertoAsync(int idTenant, int idPuntoVenta, int idEmpleado, DateTimeOffset instante)
    {
        await using var db = ContextoDelTenant(idTenant);
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleado,
            FechaApertura = instante,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return turno.Id;
    }

    private async Task SembrarArqueoAsync(int idTenant, int idTurno, int idMedioPago)
    {
        await using var db = ContextoDelTenant(idTenant);
        db.ArqueosTurno.Add(new ArqueoTurno
        {
            IdTenant = idTenant,
            IdTurnoCaja = idTurno,
            IdMedioPago = idMedioPago,
            ImporteEsperado = 0m,
            ImporteDeclarado = 0m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>judgment-day (rebase de feat/caja-cierre-por-retiro sobre fix/bajas-catalogos-guarda-de-uso):
    /// turno YA cerrado con <c>IdMedioPagoEfectivo</c> pineado — el ancla que
    /// <c>ServicioDeTurnos.InsertarArqueosYTesoreriaAsync</c> fija al cerrar (los dos modos de
    /// cierre). Sembrado directo por EF, no por el flujo de cierre completo — mismo criterio que
    /// <see cref="SembrarArqueoAsync"/> para esta clase: <c>ck_turnos_caja_medio_efectivo_solo_cerrado</c>
    /// se satisface porque el turno nace <c>Cerrado</c>.</summary>
    private async Task<int> SembrarTurnoCerradoConMedioEfectivoAsync(
        int idTenant, int idPuntoVenta, int idEmpleado, int idMedioPago, DateTimeOffset instante)
    {
        await using var db = ContextoDelTenant(idTenant);
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleado,
            IdEmpleadoCierre = idEmpleado,
            FechaApertura = instante,
            FechaCierre = instante,
            FondoInicial = 0m,
            Estado = EstadoTurno.Cerrado,
            IdMedioPagoEfectivo = idMedioPago,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return turno.Id;
    }

    private async Task<int> SembrarClienteAsync(int idTenant, int idListaPrecio, DateTimeOffset instante)
    {
        var idCondicionFiscal = await IdDeCondicionFiscalAsync();

        await using var db = ContextoDelTenant(idTenant);
        var cliente = new Cliente
        {
            IdTenant = idTenant,
            Numero = (int)Interlocked.Increment(ref _numeroClienteSecuencial),
            Nombre = "Cliente del guard de referencias",
            IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = idListaPrecio,
            LimiteCredito = 0m,
            CreditoIlimitado = true,
            CreatedAt = instante,
            UpdatedAt = instante
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private async Task SembrarMovimientoCcProveedorAsync(int idTenant, int idProveedor, DateTimeOffset instante)
    {
        await using var db = ContextoDelTenant(idTenant);
        db.MovimientosCuentaCorrienteProveedor.Add(new MovimientoCuentaCorrienteProveedor
        {
            IdTenant = idTenant,
            IdProveedor = idProveedor,
            Fecha = instante,
            Tipo = TipoMovimientoCcProveedor.Apertura,
            Importe = 0m,
            SaldoResultante = 0m
        });
        await db.SaveChangesAsync();
    }

    private async Task ActualizarMarcaDelArticuloAsync(int idTenant, int idArticulo, int idMarca)
    {
        await using var db = ContextoDelTenant(idTenant);
        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.IdMarca = idMarca;
        await db.SaveChangesAsync();
    }

    private async Task DarDeBajaDirectamenteAsync(int idTenant, int idArticulo, DateTimeOffset instante)
    {
        await using var db = ContextoDelTenant(idTenant);
        var articulo = await db.Articulos.SingleAsync(a => a.Id == idArticulo);
        articulo.DeletedAt = instante;
        articulo.UpdatedAt = instante;
        await db.SaveChangesAsync();
    }

    private async Task<int> IdDeListaDefaultAsync(int idTenant)
    {
        await using var db = ContextoDelTenant(idTenant);
        return await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
    }

    // =============================================================================================
    // C1 + C5: el guard llamado en cada sitio, y una hermana pristina que se elimina sin tocar a la
    // bloqueada (mutation-proof-tests regla 12c: el conjunto de identidad, no solo el de tenant).
    // =============================================================================================

    [Fact]
    public async Task UnAreaReferenciadaPorUnArticuloBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("area-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;

        var referenciada = await CrearAreaAsync(admin, "Área referenciada");
        var pristina = await CrearAreaAsync(admin, "Área pristina");
        await SembrarArticuloAsync(s.IdTenant, referenciada.Id, ancla);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/areas/{referenciada.Id}"));
        Assert.Equal("area_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/areas/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/areas/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/areas/{referenciada.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnaCategoriaReferenciadaPorUnArticuloBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("categoria-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;
        var area = await CrearAreaAsync(admin, "Área");

        var referenciada = await CrearCategoriaAsync(admin, "Categoría referenciada");
        var pristina = await CrearCategoriaAsync(admin, "Categoría pristina");
        await SembrarArticuloAsync(s.IdTenant, area.Id, ancla, idCategoria: referenciada.Id);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/categorias/{referenciada.Id}"));
        Assert.Equal("categoria_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/categorias/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/categorias/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/categorias/{referenciada.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnaMarcaReferenciadaPorUnArticuloBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("marca-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;
        var area = await CrearAreaAsync(admin, "Área");

        var referenciada = await CrearMarcaAsync(admin, "Marca referenciada");
        var pristina = await CrearMarcaAsync(admin, "Marca pristina");
        await SembrarArticuloAsync(s.IdTenant, area.Id, ancla, idMarca: referenciada.Id);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/marcas/{referenciada.Id}"));
        Assert.Equal("marca_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/marcas/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/marcas/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/marcas/{referenciada.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnGrupoReferenciadoPorUnArticuloBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("grupo-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;
        var area = await CrearAreaAsync(admin, "Área");

        var referenciado = await CrearGrupoAsync(admin, "Grupo referenciado");
        var pristino = await CrearGrupoAsync(admin, "Grupo pristino");
        await SembrarArticuloAsync(s.IdTenant, area.Id, ancla, idGrupo: referenciado.Id);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/grupos/{referenciado.Id}"));
        Assert.Equal("grupo_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/grupos/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/grupos/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/grupos/{referenciado.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnMedioDePagoReferenciadoPorUnArqueoBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("medio-pago-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;

        var referenciado = await CrearMedioPagoAsync(admin, "Medio referenciado");
        var pristino = await CrearMedioPagoAsync(admin, "Medio pristino");

        var idTurno = await SembrarTurnoAbiertoAsync(s.IdTenant, s.IdPuntoVenta, s.IdAdmin, ancla);
        await SembrarArqueoAsync(s.IdTenant, idTurno, referenciado.Id);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/medios-pago/{referenciado.Id}"));
        Assert.Equal("medio_pago_en_uso", codigo);
        Assert.Contains("arqueos de caja", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/medios-pago/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/medios-pago/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/medios-pago/{referenciado.Id}")).StatusCode);
    }

    /// <summary>La FK nueva de la etapa 5 (<c>fk_turnos_caja_medio_pago_efectivo</c>,
    /// judgment-day JD-E5a-2): <c>turnos_caja</c> pasa a ser dependiente de <c>medios_pago</c>
    /// desde <see cref="TurnoCaja.IdMedioPagoEfectivo"/>, el ancla que el cierre pinea. El guard
    /// la descubre solo (recorrido de metadata de EF, <c>InventarioDeDependientes</c>) — esta
    /// prueba es la contraparte de integración del golden N3 actualizado
    /// (<c>Fixtures/inventario-de-dependientes.txt</c>): confirma que bloquea de punta a punta,
    /// no solo que aparece en el inventario.</summary>
    [Fact]
    public async Task UnMedioDePagoReferenciadoPorElAnclaPineadaDeUnTurnoCerradoBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("medio-pago-ancla-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;

        var referenciado = await CrearMedioPagoAsync(admin, "Medio referenciado por cierre");
        var pristino = await CrearMedioPagoAsync(admin, "Medio pristino de cierre");

        await SembrarTurnoCerradoConMedioEfectivoAsync(s.IdTenant, s.IdPuntoVenta, s.IdAdmin, referenciado.Id, ancla);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/medios-pago/{referenciado.Id}"));
        Assert.Equal("medio_pago_en_uso", codigo);
        Assert.Contains("turnos de caja", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/medios-pago/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/medios-pago/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/medios-pago/{referenciado.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnaListaDePreciosReferenciadaPorUnClienteBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("lista-precio-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;

        var referenciada = await CrearListaPrecioAsync(admin, "Lista referenciada");
        var pristina = await CrearListaPrecioAsync(admin, "Lista pristina");
        await SembrarClienteAsync(s.IdTenant, referenciada.Id, ancla);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/listas-precio/{referenciada.Id}"));
        Assert.Equal("lista_precio_en_uso", codigo);
        Assert.Contains("clientes", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/catalogos/listas-precio/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/catalogos/listas-precio/{pristina.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/listas-precio/{referenciada.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnProveedorReferenciadoPorUnMovimientoDeCuentaCorrienteBloqueaYUnaHermanaPristinaSeEliminaSinTocarLaBloqueada()
    {
        var s = await AprovisionarAsync("proveedor-en-uso");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;

        var referenciado = await CrearProveedorAsync(admin, "Proveedor referenciado");
        var pristino = await CrearProveedorAsync(admin, "Proveedor pristino");
        await SembrarMovimientoCcProveedorAsync(s.IdTenant, referenciado.Id, ancla);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/proveedores/{referenciado.Id}"));
        Assert.Equal("proveedor_en_uso", codigo);
        Assert.Contains("movimientos de cuenta corriente de proveedores", mensaje, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/proveedores/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/proveedores/{pristino.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/proveedores/{referenciado.Id}")).StatusCode);
    }

    // =============================================================================================
    // C2: sin corte por created_at — una reasignación POSTERIOR a la creación de la marca igual
    // bloquea (a diferencia del modo organización, que la dejaría pasar).
    // =============================================================================================

    [Fact]
    public async Task UnArticuloCreadoAntesYReasignadoDespuesIgualBloqueaLaBajaDeLaMarca()
    {
        var s = await AprovisionarAsync("marca-sin-corte-temporal");
        using var admin = await ClienteAdminAsync(s);
        var area = await CrearAreaAsync(admin, "Área");

        // El artículo se crea ANTES que la marca (created_at más viejo) y se reasigna DESPUÉS —
        // el modo organización (created_at > ancla) dejaría pasar esta reasignación sin bloquear;
        // el modo referencia no tiene ningún corte temporal.
        var idArticulo = await SembrarArticuloAsync(s.IdTenant, area.Id, DateTimeOffset.UtcNow.AddDays(-30));

        var marca = await CrearMarcaAsync(admin, "Marca creada después");
        await ActualizarMarcaDelArticuloAsync(s.IdTenant, idArticulo, marca.Id);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/marcas/{marca.Id}"));
        Assert.Equal("marca_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);
    }

    // =============================================================================================
    // C3 (OD4): una fila referenciante YA dada de baja lógicamente IGUAL bloquea.
    // =============================================================================================

    [Fact]
    public async Task UnArticuloDadoDeBajaQueSigueReferenciandoLaMarcaIgualBloqueaLaBaja()
    {
        var s = await AprovisionarAsync("marca-od4");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;
        var area = await CrearAreaAsync(admin, "Área");

        var marca = await CrearMarcaAsync(admin, "Marca con hijo dado de baja");
        var idArticulo = await SembrarArticuloAsync(s.IdTenant, area.Id, ancla, idMarca: marca.Id);
        await DarDeBajaDirectamenteAsync(s.IdTenant, idArticulo, ancla.AddMinutes(1));

        var (codigo, _) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/marcas/{marca.Id}"));
        Assert.Equal("marca_en_uso", codigo);
    }

    // =============================================================================================
    // C4: todas las ramas que dispararon se nombran en el mismo mensaje.
    // =============================================================================================

    [Fact]
    public async Task UnProveedorReferenciadoPorDosTablasNombraLasDosEnElMismoMensaje()
    {
        var s = await AprovisionarAsync("proveedor-dos-ramas");
        using var admin = await ClienteAdminAsync(s);
        var ancla = DateTimeOffset.UtcNow;
        var area = await CrearAreaAsync(admin, "Área");

        var proveedor = await CrearProveedorAsync(admin, "Proveedor con dos ramas");
        await SembrarArticuloAsync(s.IdTenant, area.Id, ancla, idProveedorHabitual: proveedor.Id);
        await SembrarMovimientoCcProveedorAsync(s.IdTenant, proveedor.Id, ancla);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/proveedores/{proveedor.Id}"));

        Assert.Equal("proveedor_en_uso", codigo);
        Assert.Contains("artículos", mensaje, StringComparison.Ordinal);
        Assert.Contains("movimientos de cuenta corriente de proveedores", mensaje, StringComparison.Ordinal);
    }

    // =============================================================================================
    // C7a/C7b: el lock de fila serializa la baja contra un escritor concurrente.
    // =============================================================================================

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, int pidExcluido1, int pidExcluido2)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1 AND pid <> $2",
                conexionPoll);
            comando.Parameters.AddWithValue(pidExcluido1);
            comando.Parameters.AddWithValue(pidExcluido2);

            var cantidad = (long)(await comando.ExecuteScalarAsync())!;
            if (cantidad > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    /// <summary>
    /// C7a: un escritor concurrente que YA insertó/actualizó (sin comitear) una fila referenciante
    /// hace ESPERAR a la baja — su chequeo de FK toma <c>FOR KEY SHARE</c> sobre la marca, que
    /// choca contra el <c>FOR UPDATE</c> de <c>BloquearFilaAsync</c>. Cuando el escritor comitea, la
    /// baja ve la referencia YA COMITEADA y responde 409.
    /// </summary>
    [Fact]
    public async Task UnEscritorConcurrenteQueReasignaLaMarcaHaceEsperarALaBajaYGanaLaReferencia()
    {
        var s = await AprovisionarAsync("marca-carrera-escritor");
        using var admin = await ClienteAdminAsync(s);
        var area = await CrearAreaAsync(admin, "Área");

        var marca = await CrearMarcaAsync(admin, "Marca en carrera");
        var idArticulo = await SembrarArticuloAsync(s.IdTenant, area.Id, DateTimeOffset.UtcNow);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);

        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var comandoUpdate = new NpgsqlCommand(
            "UPDATE articulos SET id_marca = $1 WHERE id_articulo = $2 AND id_tenant = $3",
            conexionEscritor, transaccionEscritor))
        {
            comandoUpdate.Parameters.AddWithValue(marca.Id);
            comandoUpdate.Parameters.AddWithValue(idArticulo);
            comandoUpdate.Parameters.AddWithValue(s.IdTenant);
            await comandoUpdate.ExecuteNonQueryAsync();
        }

        var bajaTask = admin.DeleteAsync($"/api/catalogos/marcas/{marca.Id}");

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);

        var observado = await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll);
        Assert.True(observado, "La baja nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        var respuesta = await bajaTask;
        var (codigo, _) = await LeerConflictoAsync(respuesta);
        Assert.Equal("marca_en_uso", codigo);
    }

    /// <summary>
    /// C7b: el lock se toma ANTES de cargar la entidad. Un escritor concurrente que ya soft-borró la
    /// marca (sin comitear) hace esperar a la baja; cuando comitea, la relectura de EF (después del
    /// lock) ya no ve la fila —el filtro de baja lógica la esconde— y la baja responde 404, nunca un
    /// intento de pisar <c>deleted_at</c> sobre una fila ya borrada.
    /// </summary>
    [Fact]
    public async Task UnaBajaConcurrenteYaComiteadaDejaUn404EnVezDePisarLaFila()
    {
        var s = await AprovisionarAsync("marca-carrera-baja");
        using var admin = await ClienteAdminAsync(s);

        var marca = await CrearMarcaAsync(admin, "Marca que se pierde en la carrera");

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);

        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var comandoUpdate = new NpgsqlCommand(
            "UPDATE marcas SET deleted_at = now() WHERE id_marca = $1 AND id_tenant = $2",
            conexionEscritor, transaccionEscritor))
        {
            comandoUpdate.Parameters.AddWithValue(marca.Id);
            comandoUpdate.Parameters.AddWithValue(s.IdTenant);
            await comandoUpdate.ExecuteNonQueryAsync();
        }

        var bajaTask = admin.DeleteAsync($"/api/catalogos/marcas/{marca.Id}");

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);

        var observado = await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll);
        Assert.True(observado, "La baja nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        var respuesta = await bajaTask;
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // =============================================================================================
    // C8: sin reintento — un fallo transitorio en la baja llega tal cual, nunca duplicado ni
    // enmascarado.
    // =============================================================================================

    [Fact]
    public async Task UnFalloTransitorioEnLaBajaDeUnaMarcaNoSeReintentaYLlegaComo503ResultadoIncierto()
    {
        var s = await AprovisionarAsync("marca-resultado-incierto");
        using var admin = await ClienteAdminAsync(s);

        var marca = await CrearMarcaAsync(admin, "Marca sin reintento");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(
            new InterceptorQueRompeLaPrimeraEscritura("marcas", SqlStateTransitorio, ClaseDeSentencia.Update)))
        {
            respuesta = await admin.DeleteAsync($"/api/catalogos/marcas/{marca.Id}");
        }

        var codigo = await LeerCodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("resultado_incierto", codigo);

        // La marca sigue viva: el fallo transitorio abortó la transacción entera, nunca dejó un
        // deleted_at a medio escribir.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/catalogos/marcas/{marca.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnFalloTransitorioEnLaBajaDeUnProveedorNoSeReintentaYLlegaComo503ResultadoIncierto()
    {
        var s = await AprovisionarAsync("proveedor-resultado-incierto");
        using var admin = await ClienteAdminAsync(s);

        var proveedor = await CrearProveedorAsync(admin, "Proveedor sin reintento");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(
            new InterceptorQueRompeLaPrimeraEscritura("proveedores", SqlStateTransitorio, ClaseDeSentencia.Update)))
        {
            respuesta = await admin.DeleteAsync($"/api/proveedores/{proveedor.Id}");
        }

        var codigo = await LeerCodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("resultado_incierto", codigo);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/proveedores/{proveedor.Id}")).StatusCode);
    }

    // =============================================================================================
    // C9: las guardas propias de listas de precio siguen corriendo, bajo el mismo lock, antes del
    // guard genérico de referencias.
    // =============================================================================================

    [Fact]
    public async Task NoSePuedeEliminarLaListaDefault()
    {
        var s = await AprovisionarAsync("lista-default-no-se-elimina");
        using var admin = await ClienteAdminAsync(s);

        var idListaDefault = await IdDeListaDefaultAsync(s.IdTenant);

        var codigo = await LeerCodigoAsync(
            await admin.DeleteAsync($"/api/catalogos/listas-precio/{idListaDefault}"), HttpStatusCode.Conflict);
        Assert.Equal("lista_default_no_se_puede_eliminar", codigo);
    }

    [Fact]
    public async Task NoSePuedeEliminarUnaListaReferenciadaComoBaseParaUnaDerivadaActiva()
    {
        var s = await AprovisionarAsync("lista-base-de-derivada-activa");
        using var admin = await ClienteAdminAsync(s);

        var baseFija = await CrearListaPrecioAsync(admin, "Base de una derivada activa");
        await CrearListaPrecioAsync(
            admin, "Derivada activa", ModoLista.Derivada, baseFija.Id, porcentaje: 10m, activo: true);

        var codigo = await LeerCodigoAsync(
            await admin.DeleteAsync($"/api/catalogos/listas-precio/{baseFija.Id}"), HttpStatusCode.Conflict);
        Assert.Equal("lista_referenciada_como_base", codigo);
    }

    /// <summary>La derivada INACTIVA no dispara la guarda ESPECÍFICA de "dependientes activos"
    /// (<c>ExigirSinDependientesActivosAsync</c> filtra por <c>Activo</c>), pero el guard GENÉRICO
    /// de referencias no filtra por activo — la sigue viendo y bloquea con su propio código.</summary>
    [Fact]
    public async Task UnaListaReferenciadaSoloPorUnaDerivadaInactivaDaElCodigoGenericoConListasDerivadas()
    {
        var s = await AprovisionarAsync("lista-base-de-derivada-inactiva");
        using var admin = await ClienteAdminAsync(s);

        var baseFija = await CrearListaPrecioAsync(admin, "Base de una derivada inactiva");
        await CrearListaPrecioAsync(
            admin, "Derivada inactiva", ModoLista.Derivada, baseFija.Id, porcentaje: 10m, activo: false);

        var (codigo, mensaje) = await LeerConflictoAsync(
            await admin.DeleteAsync($"/api/catalogos/listas-precio/{baseFija.Id}"));

        Assert.Equal("lista_precio_en_uso", codigo);
        Assert.Contains("listas derivadas", mensaje, StringComparison.Ordinal);
    }

    // =============================================================================================
    // C10: un id inexistente y un id de otro tenant devuelven 404, nunca un 500.
    // =============================================================================================

    [Fact]
    public async Task UnIdDeCatalogoInexistenteYDeOtroTenantDevuelven404()
    {
        var a = await AprovisionarAsync("catalogo-404-a");
        var b = await AprovisionarAsync("catalogo-404-b");
        using var adminA = await ClienteAdminAsync(a);
        using var adminB = await ClienteAdminAsync(b);

        Assert.Equal(
            HttpStatusCode.NotFound, (await adminA.DeleteAsync("/api/catalogos/marcas/999999999")).StatusCode);

        var marcaDeA = await CrearMarcaAsync(adminA, "Marca de A");
        Assert.Equal(
            HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/catalogos/marcas/{marcaDeA.Id}")).StatusCode);

        // Y sigue viva del lado de A: el 404 de B no la tocó.
        Assert.Equal(HttpStatusCode.OK, (await adminA.GetAsync($"/api/catalogos/marcas/{marcaDeA.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnIdDeProveedorInexistenteYDeOtroTenantDevuelven404()
    {
        var a = await AprovisionarAsync("proveedor-404-a");
        var b = await AprovisionarAsync("proveedor-404-b");
        using var adminA = await ClienteAdminAsync(a);
        using var adminB = await ClienteAdminAsync(b);

        Assert.Equal(
            HttpStatusCode.NotFound, (await adminA.DeleteAsync("/api/proveedores/999999999")).StatusCode);

        var proveedorDeA = await CrearProveedorAsync(adminA, "Proveedor de A");
        Assert.Equal(
            HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/proveedores/{proveedorDeA.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adminA.GetAsync($"/api/proveedores/{proveedorDeA.Id}")).StatusCode);
    }
}
