using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Auditoria;
using Ways.Application.Catalogos;
using Ways.Application.Clientes;
using Ways.Application.Fiscal;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// LA CLÁUSULA, una prueba por sitio corregido: cada escritura no idempotente corre bajo
/// <c>FabricaDeEstrategiaSinReintento</c>, así que un fallo transitorio NO se reintenta y NO
/// duplica nada.
///
/// <c>EnableRetryOnFailure(5, 3s)</c> es global (<c>DependencyInjection</c>). Toda entidad
/// construida con <c>Add</c> DENTRO del lambda de <c>ExecuteAsync</c> se duplica ante un reintento:
/// el <c>ChangeTracker</c> conserva en <c>Added</c> las del intento fallido, el intento N+1 vuelve
/// a correr el lambda entero y agrega un set nuevo, y el <c>SaveChangesAsync</c> final inserta los
/// dos. En <c>clientes</c> y <c>articulos</c> el daño es una fila duplicada EN SILENCIO (el número
/// y el <c>codigo_interno</c> se re-sortean dentro de la transacción, así que ni siquiera chocan
/// contra su índice único); en los demás el índice único lo convierte en un 409 falso sobre una
/// operación que quizás sí persistió.
///
/// Cada prueba tiene las dos mitades, y hacen falta las dos:
/// <list type="bullet">
/// <item><b>(a)</b> con el interceptor, el <c>40001</c> llega TAL CUAL al llamador,
/// <c>Intentos == 1</c> y la base queda intacta. <c>Intentos</c> es el valor discriminante
/// (<c>mutation-proof-tests</c> regla 4): que el error se propague NO distingue las dos
/// estrategias —la reintentable también propaga si agota los cinco intentos—, el conteo sí;</item>
/// <item><b>(b)</b> sin el interceptor, la MISMA operación escribe EXACTAMENTE una fila por
/// entidad. Sin esta mitad, (a) pasaría también con una operación rota que no escribe nunca.</item>
/// </list>
///
/// El contexto se crea siempre con <c>CrearContextoDeAplicacionConReintentos</c>: la estrategia
/// reintentable EXISTE y la única razón por la que no reintenta es la que elige el código de
/// producción. La mitad estructural —los once sitios a la vez, sin contenedor— vive en
/// <c>Ways.Application.Tests.Abstracciones.EscriturasSinReintentoEstructuralesTests</c>.
///
/// <para>judgment-day fix/retry-double-add: dos pruebas de esta clase NO pertenecen a ese molde y
/// están documentadas en su propio lugar. <c>LaEmisionDeVentaReintentaConElTrackerLimpio…</c> es el
/// espejo exacto — el único sitio del audit que SÍ reintenta, y la prueba de que su
/// <c>ChangeTracker.Clear()</c> + guarda de commit ambiguo hacen que eso sea seguro (item C1);
/// <c>UnFalloTransitorioEnUnAltaSinReintentoLlegaComo503ResultadoIncierto</c> prueba el RESIDUAL
/// declarado de la forma (b) llegando al operador como <c>503 resultado_incierto</c> (item
/// C2).</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EscriturasSinReintentoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    /// <summary><c>serialization_failure</c>: transitorio para <c>NpgsqlRetryingExecutionStrategy</c>
    /// — exactamente la clase de falla por la que <c>EnableRetryOnFailure</c> existe. Si no fuera
    /// transitoria, ni la estrategia reintentable reintentaría y la prueba pasaría por el motivo
    /// equivocado.</summary>
    private const string SqlStateTransitorio = "40001";

    private static readonly DateTimeOffset Ahora = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Sembrado(
        int IdTenant,
        int IdEmpresa,
        int IdPuntoVenta,
        int IdAdmin,
        int IdArea,
        int IdAlicuotaIva,
        int IdListaDefault,
        int IdCondicionFiscal,
        string MailAdmin,
        string PasswordAdmin);

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

    // ---- siembra ------------------------------------------------------------------------------

    /// <summary>Un tenant por el camino REAL (<c>POST /api/plataforma/tenants</c>) más los ids de
    /// catálogo que las altas de esta clase necesitan. El área se siembra acá porque el
    /// aprovisionamiento no crea ninguna.</summary>
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

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, resultado!.IdTenant));

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = $"Área {unico}", Orden = 1,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaDefault = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        return new Sembrado(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin,
            area.Id, idAlicuota, idListaDefault, idCondicionFiscal, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<int> SembrarArticuloAsync(Sembrado s, string nombre)
    {
        await using var db = ContextoDelTenant(s);

        var articulo = new Articulo
        {
            IdTenant = s.IdTenant, CodigoInterno = Guid.NewGuid().ToString("N")[..12], Nombre = nombre,
            IdArea = s.IdArea, IdAlicuotaIva = s.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, DisponibleParaTodas = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private WaysDbContext ContextoDelTenant(Sembrado s) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenant));

    private WaysDbContext ContextoDePlataforma() =>
        fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

    private WaysDbContext ContextoConReintentos(Sembrado s, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] extra) =>
        fixture.CrearContextoDeAplicacionConReintentos(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenant), extra);

    private static RelojFijo Reloj() => new(Ahora);

    private static ContextoFijo ContextoAdmin(Sembrado s) => new(RolConocido.Admin, s.IdAdmin, s.IdTenant);

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

    /// <summary>Las dos afirmaciones del lado (a) que valen para TODOS los sitios: el error
    /// transitorio llegó tal cual, y el INSERT se intentó UNA sola vez.</summary>
    private static void AfirmarFallaSinReintento(Exception error, InterceptorQueRompeLaPrimeraEscritura interceptor)
    {
        Assert.Equal(SqlStateTransitorio, ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
    }

    // ---- 1. clientes: duplicado SILENCIOSO (el número se re-sortea adentro) --------------------

    [Fact]
    public async Task ElAltaDeClienteNoSeReintentaYCreaExactamenteUnCliente()
    {
        var s = await AprovisionarAsync("sin-reintento-clientes");
        var nombre = $"Cliente {Guid.NewGuid().ToString("N")[..10]}";

        var datos = new AltaCliente(
            Nombre: nombre, Apellido: null, RazonSocial: null, TipoDocumento: null, NumeroDocumento: null,
            IdCondicionFiscal: s.IdCondicionFiscal, Nacimiento: null, Domicilio: null, Telefono: null,
            Celular: null, Email: null, Observaciones: null, IdListaPrecio: s.IdListaDefault);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("clientes", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var servicio = new ServicioDeClientes(db, Reloj(), ContextoAdmin(s));
            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarClientesAsync(s.IdTenant, nombre));

        await using (var db = ContextoConReintentos(s))
        {
            await new ServicioDeClientes(db, Reloj(), ContextoAdmin(s)).CrearAsync(datos);
        }

        // UNA sola fila por UNA sola llamada. Con la estrategia reintentable serían DOS, cada una
        // con su propio `numero` — el índice único no las ve chocar, así que el duplicado es
        // invisible salvo por este conteo.
        Assert.Equal(1, await ContarClientesAsync(s.IdTenant, nombre));
    }

    private async Task<int> ContarClientesAsync(int idTenant, string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.Clientes.IgnoreQueryFilters()
            .CountAsync(c => c.IdTenant == idTenant && c.Nombre == nombre);
    }

    // ---- 2. articulos: duplicado SILENCIOSO (el codigo_interno se re-sortea adentro) -----------

    [Fact]
    public async Task ElAltaDeArticuloNoSeReintentaYCreaExactamenteUnArticulo()
    {
        var s = await AprovisionarAsync("sin-reintento-articulos");
        var nombre = $"Artículo {Guid.NewGuid().ToString("N")[..10]}";

        // CodigoInterno null a propósito: es el camino que lo AUTOGENERA dentro de la transacción,
        // así que un reintento se llevaría un código nuevo y el índice único no vería el duplicado.
        var datos = new AltaArticulo(
            CodigoInterno: null, Nombre: nombre, Descripcion: null, IdArea: s.IdArea, IdCategoria: null,
            IdMarca: null, IdGrupo: null, IdProveedorHabitual: null, IdAlicuotaIva: s.IdAlicuotaIva,
            UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: null, EsProducto: true, CostoLista: null,
            DescuentoProveedor: null, CostoNominal: null);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("articulos", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var servicio = new ServicioDeArticulos(
                db, Reloj(), ContextoAdmin(s), new ServicioDeLotes(db, Reloj(), ContextoAdmin(s)));
            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarArticulosAsync(s.IdTenant, nombre));

        await using (var db = ContextoConReintentos(s))
        {
            await new ServicioDeArticulos(
                db, Reloj(), ContextoAdmin(s), new ServicioDeLotes(db, Reloj(), ContextoAdmin(s)))
                .CrearAsync(datos);
        }

        Assert.Equal(1, await ContarArticulosAsync(s.IdTenant, nombre));
    }

    private async Task<int> ContarArticulosAsync(int idTenant, string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.Articulos.IgnoreQueryFilters()
            .CountAsync(a => a.IdTenant == idTenant && a.Nombre == nombre);
    }

    // ---- 3. usuarios: la fila de AUDITORÍA es la que se duplicaba -----------------------------

    /// <summary>
    /// <c>usuario</c> está izado FUERA del lambda a propósito (comentario de la slice 2 de la
    /// etapa 20), así que un reintento reusaba la MISMA instancia y no duplicaba la cuenta. Lo que
    /// el izado no protege es <c>auditoria.Registrar</c>: construye un <c>Auditoria</c> NUEVO en
    /// cada intento. Por eso el interceptor rompe el INSERT de <c>auditoria</c> (el segundo flush)
    /// y no el de <c>usuarios</c>.
    /// </summary>
    [Fact]
    public async Task ElAltaDeUsuarioNoSeReintentaYEscribeExactamenteUnaFilaDeAuditoria()
    {
        var s = await AprovisionarAsync("sin-reintento-usuarios");
        var corto = Guid.NewGuid().ToString("N")[..8];
        var datos = new CrearUsuario($"vendedor-{corto}", $"vendedor-{corto}@ways.test",
            (int)RolConocido.Vendedor, "una-contraseña-larga");

        var ultimoId = await UltimoIdDeAuditoriaAsync();
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("auditoria", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        await using (var dbPlataforma = ContextoDePlataforma())
        {
            var servicio = ServicioDeUsuariosSobre(db, dbPlataforma, s);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        // Fail-closed: el rollback del segundo flush también deshace el alta.
        Assert.Equal(0, await ContarUsuariosAsync(s.IdTenant, datos.Mail));
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, s.IdTenant));

        await using (var db = ContextoConReintentos(s))
        await using (var dbPlataforma = ContextoDePlataforma())
        {
            await ServicioDeUsuariosSobre(db, dbPlataforma, s).CrearAsync(datos);
        }

        Assert.Equal(1, await ContarUsuariosAsync(s.IdTenant, datos.Mail));

        var rastro = await RastroPosteriorAAsync(ultimoId, s.IdTenant);
        Assert.Single(rastro);
        Assert.Equal("usuario.alta", rastro[0].Accion);
    }

    private static ServicioDeUsuarios ServicioDeUsuariosSobre(WaysDbContext db, WaysDbContext dbPlataforma, Sembrado s) =>
        new(db, dbPlataforma, new HasheadorPbkdf2(), Reloj(), ContextoAdmin(s),
            new ServicioDeAuditoria(db, Reloj(), ContextoAdmin(s)), new InspectorDeUso(db));

    private async Task<int> ContarUsuariosAsync(int idTenant, string mail)
    {
        await using var db = ContextoDePlataforma();
        return await db.Usuarios.IgnoreQueryFilters()
            .CountAsync(u => u.IdTenant == idTenant && u.Mail == mail);
    }

    private async Task<long> UltimoIdDeAuditoriaAsync()
    {
        await using var db = ContextoDePlataforma();
        return await db.Auditoria.IgnoreQueryFilters().MaxAsync(a => (long?)a.Id) ?? 0;
    }

    private async Task<List<Ways.Domain.Auditoria.Auditoria>> RastroPosteriorAAsync(long ultimoId, int idTenant)
    {
        await using var db = ContextoDePlataforma();
        return await db.Auditoria.IgnoreQueryFilters()
            .Where(a => a.Id > ultimoId && a.IdTenant == idTenant)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    // ---- 4. precios: fila de precio + fila de auditoría, las dos nuevas por intento ------------

    [Fact]
    public async Task LaAperturaDePrecioNoSeReintentaYEscribeExactamenteUnPrecioYUnRastro()
    {
        var s = await AprovisionarAsync("sin-reintento-precios");
        var idArticulo = await SembrarArticuloAsync(s, "Artículo con precio");

        var ultimoId = await UltimoIdDeAuditoriaAsync();
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("precios", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var servicio = ServicioDePreciosSobre(db, s);
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.AbrirNuevoPrecioAsync(idArticulo, s.IdListaDefault, 150m, null, false));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarPreciosAsync(idArticulo));
        Assert.Empty(await RastroPosteriorAAsync(ultimoId, s.IdTenant));

        await using (var db = ContextoConReintentos(s))
        {
            await ServicioDePreciosSobre(db, s)
                .AbrirNuevoPrecioAsync(idArticulo, s.IdListaDefault, 150m, null, false);
        }

        // Una fila de precio (el reintento chocaría contra ux_precios_vigente con un 409 falso) y
        // UNA sola de auditoría (esa no tiene índice único que la frene: se duplicaba en silencio).
        Assert.Equal(1, await ContarPreciosAsync(idArticulo));

        var rastro = await RastroPosteriorAAsync(ultimoId, s.IdTenant);
        Assert.Single(rastro);
        Assert.Equal("precio.cambio", rastro[0].Accion);
    }

    private static ServicioDePrecios ServicioDePreciosSobre(WaysDbContext db, Sembrado s) =>
        new(db, Reloj(), ContextoAdmin(s), new ServicioDeAuditoria(db, Reloj(), ContextoAdmin(s)));

    private async Task<int> ContarPreciosAsync(int idArticulo)
    {
        await using var db = ContextoDePlataforma();
        return await db.Precios.IgnoreQueryFilters().CountAsync(p => p.IdArticulo == idArticulo);
    }

    // ---- 5. listas_precio: el alta de la lista default (intercambio en transacción) ------------

    [Fact]
    public async Task ElAltaDeListaDefaultNoSeReintentaYCreaExactamenteUnaLista()
    {
        var s = await AprovisionarAsync("sin-reintento-listas");
        var nombre = $"Lista {Guid.NewGuid().ToString("N")[..10]}";

        // EsDefault: true es lo que entra a la rama transaccional (desmarcar la vieja + crear la
        // nueva); con false, CrearAsync delega directo en la base y no hay estrategia que probar.
        var datos = new ListaPrecioAlta(
            Nombre: nombre, IdEmpresa: null, EsDefault: true, Modo: ModoLista.Fija,
            IdListaBase: null, Porcentaje: null);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("listas_precio", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => new ServicioDeListasPrecio(db, Reloj()).CrearAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarListasAsync(s.IdTenant, nombre));

        // Y el rollback dejó intacta a la default anterior: el tenant nunca queda sin ninguna.
        Assert.True(await EsDefaultAsync(s.IdListaDefault));

        await using (var db = ContextoConReintentos(s))
        {
            await new ServicioDeListasPrecio(db, Reloj()).CrearAsync(datos);
        }

        Assert.Equal(1, await ContarListasAsync(s.IdTenant, nombre));
    }

    private async Task<int> ContarListasAsync(int idTenant, string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.ListasPrecio.IgnoreQueryFilters()
            .CountAsync(l => l.IdTenant == idTenant && l.Nombre == nombre);
    }

    private async Task<bool> EsDefaultAsync(int idLista)
    {
        await using var db = ContextoDePlataforma();
        return await db.ListasPrecio.IgnoreQueryFilters()
            .Where(l => l.Id == idLista).Select(l => l.EsDefault).SingleAsync();
    }

    // ---- 6/7. ofertas_listas: las filas de targeting se reconstruyen en cada intento -----------

    [Fact]
    public async Task ElAltaDeOfertaNoSeReintentaYEscribeExactamenteUnaFilaDeTargeting()
    {
        var s = await AprovisionarAsync("sin-reintento-ofertas-alta");
        var idArticulo = await SembrarArticuloAsync(s, "Artículo con oferta");
        var nombre = $"Oferta {Guid.NewGuid().ToString("N")[..10]}";
        var datos = AltaDeOferta(nombre, idArticulo, s.IdListaDefault);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("ofertas_listas", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => ServicioDeOfertasSobre(db, s).CrearAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarOfertasAsync(s.IdTenant, nombre));

        int idOferta;
        await using (var db = ContextoConReintentos(s))
        {
            idOferta = (await ServicioDeOfertasSobre(db, s).CrearAsync(datos)).Id;
        }

        Assert.Equal(1, await ContarOfertasAsync(s.IdTenant, nombre));
        Assert.Equal(1, await ContarFilasDeListasAsync(idOferta));
    }

    [Fact]
    public async Task LaEdicionDeOfertaNoSeReintentaYDejaUnaSolaFilaDeTargeting()
    {
        var s = await AprovisionarAsync("sin-reintento-ofertas-edicion");
        var idArticulo = await SembrarArticuloAsync(s, "Artículo con oferta editable");
        var nombre = $"Oferta {Guid.NewGuid().ToString("N")[..10]}";

        int idOferta;
        await using (var db = ContextoConReintentos(s))
        {
            idOferta = (await ServicioDeOfertasSobre(db, s)
                .CrearAsync(AltaDeOferta(nombre, idArticulo, s.IdListaDefault))).Id;
        }

        // El targeting tiene que MOVERSE a otra lista: si la edicion repite la misma lista, EF
        // fusiona el RemoveRange y el Add de la misma clave en un UPDATE y nunca hay INSERT que
        // romper — el interceptor no dispara y la prueba no prueba nada.
        int idOtraLista;
        await using (var db = ContextoConReintentos(s))
        {
            idOtraLista = (await new ServicioDeListasPrecio(db, Reloj()).CrearAsync(new ListaPrecioAlta(
                Nombre: $"Lista secundaria {Guid.NewGuid().ToString("N")[..8]}", IdEmpresa: null,
                EsDefault: false, Modo: ModoLista.Fija, IdListaBase: null, Porcentaje: null))).Id;
        }

        var edicion = new EdicionOferta(
            Nombre: $"{nombre} editada", IdEmpresa: null, IdArticulo: idArticulo, IdGrupo: null,
            IdCategoria: null, FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null,
            DiasSemana: null, CantidadMinima: null, PrecioUnitario: null, Porcentaje: 20m,
            ImporteFijo: null, Prioridad: 0, Acumulable: false, IdsListas: [idOtraLista],
            Activo: true);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("ofertas_listas", SqlStateTransitorio);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => ServicioDeOfertasSobre(db, s).ActualizarAsync(idOferta, edicion));
            AfirmarFallaSinReintento(error, interceptor);
        }

        // El PUT revirtió entero: sigue el nombre viejo y su única fila de targeting apunta a la
        // lista ORIGINAL, no a la nueva.
        Assert.Equal(1, await ContarOfertasAsync(s.IdTenant, nombre));
        Assert.Equal([s.IdListaDefault], await IdsListasDeOfertaAsync(idOferta));

        await using (var db = ContextoConReintentos(s))
        {
            await ServicioDeOfertasSobre(db, s).ActualizarAsync(idOferta, edicion);
        }

        Assert.Equal(1, await ContarOfertasAsync(s.IdTenant, $"{nombre} editada"));
        Assert.Equal([idOtraLista], await IdsListasDeOfertaAsync(idOferta));
    }

    private static AltaOferta AltaDeOferta(string nombre, int idArticulo, int idLista) => new(
        Nombre: nombre, IdEmpresa: null, IdArticulo: idArticulo, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: null, Porcentaje: 10m, ImporteFijo: null,
        Prioridad: 0, Acumulable: false, IdsListas: [idLista]);

    private static ServicioDeOfertas ServicioDeOfertasSobre(WaysDbContext db, Sembrado s) =>
        new(db, Reloj(), ContextoAdmin(s), new ServicioDePrecios(db, Reloj(), ContextoAdmin(s)));

    private async Task<int> ContarOfertasAsync(int idTenant, string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.Ofertas.IgnoreQueryFilters()
            .CountAsync(o => o.IdTenant == idTenant && o.Nombre == nombre && o.DeletedAt == null);
    }

    private async Task<int> ContarFilasDeListasAsync(int idOferta)
    {
        await using var db = ContextoDePlataforma();
        return await db.OfertasListas.IgnoreQueryFilters().CountAsync(f => f.IdOferta == idOferta);
    }

    private async Task<int[]> IdsListasDeOfertaAsync(int idOferta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.OfertasListas.IgnoreQueryFilters()
            .Where(ol => ol.IdOferta == idOferta).Select(ol => ol.IdListaPrecio).OrderBy(x => x).ToArrayAsync();
    }

    // ---- 8. certificados fiscales: rotación atómica con backstop de índice único --------------

    [Fact]
    public async Task ElRegistroDeCertificadoNoSeReintentaYEscribeExactamenteUnaFila()
    {
        var s = await AprovisionarAsync("sin-reintento-certificados");
        var (pfx, password) = GenerarPfx("CN=Ways Sin Reintento");
        var configuracion = ConfiguracionConClaveMaestra();

        var datos = new RegistroDeCertificadoFiscal(
            s.IdEmpresa, AmbienteFiscal.Homologacion, "Homo sin reintento", "20111111112", pfx, password);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("certificados_fiscales", SqlStateTransitorio);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(
            TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeCertificados(
                db, Reloj(), new CifradoDeClavesFiscales(db, configuracion));
            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.RegistrarAsync(datos));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarCertificadosAsync(s.IdEmpresa));

        // El PFX se zerea en el finally del servicio, así que el segundo intento necesita uno nuevo.
        var (pfx2, password2) = GenerarPfx("CN=Ways Sin Reintento");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma))
        {
            await new ServicioDeCertificados(db, Reloj(), new CifradoDeClavesFiscales(db, configuracion))
                .RegistrarAsync(datos with { Pfx = pfx2, PasswordPfx = password2 });
        }

        Assert.Equal(1, await ContarCertificadosAsync(s.IdEmpresa));
    }

    private async Task<int> ContarCertificadosAsync(int idEmpresa)
    {
        await using var db = ContextoDePlataforma();
        return await db.CertificadosFiscales.IgnoreQueryFilters().CountAsync(c => c.IdEmpresa == idEmpresa);
    }

    private static (byte[] Pfx, string Password) GenerarPfx(string cn)
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var ahora = DateTimeOffset.UtcNow;
        using var certificado = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));

        var password = Guid.NewGuid().ToString("N");
        return (certificado.Export(X509ContentType.Pkcs12, password), password);
    }

    private static IConfiguration ConfiguracionConClaveMaestra() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ways:Fiscal:ClaveMaestraActual"] = "v1",
                ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();

    // ---- 9. aprovisionamiento: TODAS las entidades son nuevas en cada intento ------------------

    /// <summary>
    /// Se rompe el INSERT de <c>usuarios</c> —el ÚLTIMO de la cadena— y no el de <c>tenants</c>: es
    /// el punto donde un reintento ya habría reconstruido tenant, empresa y punto de venta. Que hoy
    /// <c>ux_usuarios_mail</c> abortara la transacción entera es un accidente del ORDEN de
    /// inserción, no una garantía; sin reintento la seguridad no depende de ese orden.
    ///
    /// <c>TenantActualFijo</c> no sirve acá: <c>CrearTenantAsync</c> llama a <c>Suplantar</c> y a
    /// <c>ReaplicarSobreConexionAsync</c>, y esa implementación las tira. Se usa la real
    /// (<c>TenantActualDeSesion</c>), que es exactamente lo que corre en una request.
    /// </summary>
    [Fact]
    public async Task ElAprovisionamientoNoSeReintentaYCreaExactamenteUnTenant()
    {
        var nombre = $"tenant-sin-reintento-{Guid.NewGuid().ToString("N")[..8]}";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SRL", $"{nombre} - Local 1", $"{nombre}@ways.test");

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("usuarios", SqlStateTransitorio);

        var tenantActual = new TenantActualDeSesion();
        tenantActual.Establecer(ModoDeAcceso.Plataforma, null);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(tenantActual, interceptor))
        {
            var servicio = new ServicioDeAprovisionamiento(db, tenantActual, new HasheadorPbkdf2(), Reloj());
            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearTenantAsync(solicitud));
            AfirmarFallaSinReintento(error, interceptor);
        }

        Assert.Equal(0, await ContarTenantsAsync(nombre));

        var tenantActualLimpio = new TenantActualDeSesion();
        tenantActualLimpio.Establecer(ModoDeAcceso.Plataforma, null);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(tenantActualLimpio))
        {
            await new ServicioDeAprovisionamiento(db, tenantActualLimpio, new HasheadorPbkdf2(), Reloj())
                .CrearTenantAsync(solicitud);
        }

        Assert.Equal(1, await ContarTenantsAsync(nombre));
        Assert.Equal(1, await ContarEmpresasAsync(nombre));
    }

    private async Task<int> ContarTenantsAsync(string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.Nombre == nombre);
    }

    private async Task<int> ContarEmpresasAsync(string nombre)
    {
        await using var db = ContextoDePlataforma();
        return await db.Empresas.IgnoreQueryFilters().CountAsync(e => e.RazonSocial == $"{nombre} SRL");
    }

    // ---- 10. ofertas (baja lógica): el reintento devolvía 404 sobre una baja que sí ocurrió -----

    /// <summary>
    /// judgment-day fix/retry-double-add (item C4). El interceptor mira el <c>UPDATE</c> —una baja
    /// lógica no inserta nada, escribe <c>deleted_at</c>— y por eso <see cref="ClaseDeSentencia"/>
    /// existe.
    ///
    /// El daño del mutante NO es una fila duplicada: es un 404 sobre una baja EXITOSA. Con la
    /// estrategia reintentable, el intento 2 vuelve a llamar a <c>BuscarAsync</c>, que filtra
    /// <c>BajaLogica</c> — si el intento 1 comiteó y perdió el ACK, la oferta ya está borrada y el
    /// operador recibe "No existe la oferta" sobre algo que sí se dio de baja.
    /// </summary>
    [Fact]
    public async Task LaBajaDeOfertaNoSeReintentaYDejaLaOfertaIntacta()
    {
        var s = await AprovisionarAsync("sin-reintento-ofertas-baja");
        var idArticulo = await SembrarArticuloAsync(s, "Artículo con oferta a dar de baja");
        var nombre = $"Oferta {Guid.NewGuid().ToString("N")[..10]}";

        int idOferta;
        await using (var db = ContextoConReintentos(s))
        {
            idOferta = (await ServicioDeOfertasSobre(db, s)
                .CrearAsync(AltaDeOferta(nombre, idArticulo, s.IdListaDefault))).Id;
        }

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura(
            "ofertas", SqlStateTransitorio, ClaseDeSentencia.Update);

        await using (var db = ContextoConReintentos(s, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => ServicioDeOfertasSobre(db, s).EliminarAsync(idOferta));
            AfirmarFallaSinReintento(error, interceptor);
        }

        // El rollback dejó la oferta viva: sigue contando como no borrada.
        Assert.Equal(1, await ContarOfertasAsync(s.IdTenant, nombre));

        await using (var db = ContextoConReintentos(s))
        {
            await ServicioDeOfertasSobre(db, s).EliminarAsync(idOferta);
        }

        Assert.Equal(0, await ContarOfertasAsync(s.IdTenant, nombre));
    }

    // ---- 11. clientes por HTTP: la copia del commit ambiguo llega al operador ------------------

    /// <summary>
    /// judgment-day fix/retry-double-add (item C2). El residual declarado de la forma (b) del skill
    /// <c>ef-retry-safe-writes</c> (regla 4) tiene que llegar al operador como tal: el fallo
    /// transitorio que escapa de una escritura sin reintento es un COMMIT AMBIGUO, no un error
    /// interno. Como <c>500 error_interno</c> la pantalla decía "error inesperado" y el operador
    /// reintentaba a ciegas sobre algo que quizás ya se escribió.
    ///
    /// <para>Tiene que ser por HTTP: <c>ManejadorDeErrores</c> solo corre en el pipeline, así que
    /// un <c>DbContext</c> armado a mano (el de todas las pruebas de arriba) nunca lo atraviesa —
    /// de ahí <see cref="WaysApiFixture.ConInterceptorEnElHost"/>. El interceptor se arma DESPUÉS
    /// del aprovisionamiento a propósito: ese flujo también inserta un <c>clientes</c> (el
    /// Consumidor Final) y se llevaría la falla inyectada.</para>
    ///
    /// <para>El mutante: borrar el brazo transitorio de <c>ManejadorDeErrores</c> devuelve
    /// <c>500 error_interno</c> y esta prueba se pone en rojo por las tres afirmaciones (estado,
    /// código y copia), no por una sola.</para>
    /// </summary>
    [Fact]
    public async Task UnFalloTransitorioEnUnAltaSinReintentoLlegaComo503ResultadoIncierto()
    {
        var s = await AprovisionarAsync("resultado-incierto-clientes");

        using var admin = fixture.CreateClient();
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(s.MailAdmin, s.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var alta = new AltaCliente(
            Nombre: $"Cliente {Guid.NewGuid().ToString("N")[..10]}", Apellido: null, RazonSocial: null,
            TipoDocumento: null, NumeroDocumento: null, IdCondicionFiscal: s.IdCondicionFiscal,
            Nacimiento: null, Domicilio: null, Telefono: null, Celular: null, Email: null,
            Observaciones: null, IdListaPrecio: s.IdListaDefault);

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(
            new InterceptorQueRompeLaPrimeraEscritura("clientes", SqlStateTransitorio)))
        {
            respuesta = await admin.PostAsJsonAsync("/api/clientes", alta);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("resultado_incierto", problema.GetProperty("codigo").GetString());
        Assert.Equal(
            "No se pudo confirmar el resultado de la operación: verificá el listado antes de reintentar.",
            problema.GetProperty("title").GetString());
    }

    // ---- 12. ventas: el ÚNICO sitio del audit que SÍ reintenta ---------------------------------

    /// <summary>
    /// judgment-day fix/retry-double-add (item C1). <c>EmitirAsync</c> es la excepción declarada:
    /// conserva la estrategia REINTENTABLE en sus dos pasos, y esta prueba es el kill de las dos
    /// piezas que lo vuelven seguro.
    ///
    /// <list type="bullet">
    /// <item><c>Intentos == 2</c> prueba que el <c>40001</c> del primer INSERT NO salió al cajero:
    /// la estrategia re-entró en el lambda y la venta se emitió igual. Sin el reintento, un commit
    /// ambiguo sale como 500 con el carrito intacto, el cajero vuelve a apretar Cobrar y —como
    /// <c>SolicitudDeVenta</c> no lleva número— se sortea uno NUEVO: segundo comprobante, segundo
    /// descuento de stock, segundos movimientos de caja y cuenta corriente. Esa es la razón por la
    /// que la guarda <c>BuscarPorNumeroComprometidoAsync</c> necesita al reintento: es su ÚNICO
    /// consumidor.</item>
    /// <item>Los cuatro conteos en 1 son el kill del <c>db.ChangeTracker.Clear()</c>: sin él, el
    /// intento 2 arrastra el comprobante, los ítems y los pagos que el intento 1 dejó en
    /// <c>Added</c> y agrega un segundo set — el <c>SaveChangesAsync</c> final inserta los dos (o
    /// choca contra <c>ux_comprobantes_venta_numero</c>, que es el mismo defecto disfrazado).
    /// Se cuenta cada tabla por separado porque el duplicado puede aparecer en una sola.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LaEmisionDeVentaReintentaConElTrackerLimpioYEscribeExactamenteUnaVenta()
    {
        var s = await AprovisionarAsync("con-reintento-ventas");
        var idArticulo = await SembrarArticuloAsync(s, "Artículo vendible");
        await SembrarPrecioAsync(s, idArticulo, 100m);
        await AbrirTurnoAsync(s);

        var idMedioEfectivo = await IdDeMedioEfectivoAsync(s);
        var idClienteCf = await IdDelConsumidorFinalAsync(s);

        var solicitud = new SolicitudDeVenta(
            s.IdPuntoVenta, idClienteCf, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioEfectivo, 100m, null, 0m)],
            null, null);

        // El interceptor mira comprobantes_venta y no numeraciones_comprobante: el paso de
        // numeración corre en su propia transacción, ya comiteada cuando el lambda de escritura
        // arranca, y es el que hace que el reintento vea el MISMO número.
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("comprobantes_venta", SqlStateTransitorio);

        ComprobanteEmitido emitido;
        await using (var db = ContextoConReintentos(s, interceptor))
        {
            emitido = await ServicioDeVentasSobre(db, s).EmitirAsync(solicitud);
        }

        Assert.Equal(2, interceptor.Intentos);

        Assert.Equal(1, await ContarComprobantesAsync(s.IdPuntoVenta));
        Assert.Equal(1, await ContarItemsDeComprobanteAsync(emitido.Id));
        Assert.Equal(1, await ContarPagosDeComprobanteAsync(emitido.Id));
        Assert.Equal(1, await ContarMovimientosDeStockAsync(emitido.Id));
    }

    private static ServicioDeVentas ServicioDeVentasSobre(WaysDbContext db, Sembrado s)
    {
        var reloj = Reloj();
        var contexto = ContextoAdmin(s);
        var precios = new ServicioDePrecios(db, reloj, contexto);
        var ofertas = new ServicioDeOfertas(db, reloj, contexto, precios);
        var turnos = new Ways.Application.Caja.ServicioDeTurnos(
            db, reloj, contexto, new Ways.Application.Caja.LectorDeMovimientosDelTurno(db));
        var lotes = new ServicioDeLotes(db, reloj, contexto);

        return new ServicioDeVentas(db, reloj, contexto, ofertas, turnos, lotes);
    }

    private async Task SembrarPrecioAsync(Sembrado s, int idArticulo, decimal monto)
    {
        await using var db = ContextoDelTenant(s);

        db.Precios.Add(new Ways.Domain.Precios.Precio
        {
            IdTenant = s.IdTenant, IdArticulo = idArticulo, IdListaPrecio = s.IdListaDefault,
            Monto = monto, VigenteDesde = Ahora.AddDays(-1), VigenteHasta = null,
            CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task AbrirTurnoAsync(Sembrado s)
    {
        await using var db = ContextoDelTenant(s);

        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = s.IdTenant, IdPuntoVenta = s.IdPuntoVenta, IdEmpleadoApertura = s.IdAdmin,
            FechaApertura = Ahora.AddHours(-1), FondoInicial = 0m, Estado = EstadoTurno.Abierto,
            CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> IdDeMedioEfectivoAsync(Sembrado s)
    {
        await using var db = ContextoDelTenant(s);
        return await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id)
            .FirstAsync();
    }

    private async Task<int> IdDelConsumidorFinalAsync(Sembrado s)
    {
        await using var db = ContextoDelTenant(s);
        return await db.Clientes
            .Where(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal)
            .Select(c => c.Id)
            .FirstAsync();
    }

    private async Task<int> ContarComprobantesAsync(int idPuntoVenta)
    {
        await using var db = ContextoDePlataforma();
        return await db.ComprobantesVenta.IgnoreQueryFilters().CountAsync(c => c.IdPuntoVenta == idPuntoVenta);
    }

    private async Task<int> ContarItemsDeComprobanteAsync(int idComprobante)
    {
        await using var db = ContextoDePlataforma();
        return await db.ItemsComprobanteVenta.IgnoreQueryFilters()
            .CountAsync(i => i.IdComprobanteVenta == idComprobante);
    }

    private async Task<int> ContarPagosDeComprobanteAsync(int idComprobante)
    {
        await using var db = ContextoDePlataforma();
        return await db.PagosComprobante.IgnoreQueryFilters()
            .CountAsync(p => p.IdComprobanteVenta == idComprobante);
    }

    private async Task<int> ContarMovimientosDeStockAsync(int idComprobante)
    {
        await using var db = ContextoDePlataforma();
        return await db.MovimientosStock.IgnoreQueryFilters()
            .CountAsync(m => m.IdComprobanteVenta == idComprobante);
    }
}
