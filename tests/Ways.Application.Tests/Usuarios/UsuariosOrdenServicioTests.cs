using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Tests.Listados;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.Application.Tests.Usuarios;

/// <summary>
/// <see cref="ServicioDeUsuarios.ListarAsync"/> con orden y filtros que no son de texto, sobre InMemory: la secuencia
/// completa sale de recorrer las páginas de a dos (el orden se aplica antes de paginar), con "sin última conexión" al
/// final en las dos direcciones y los textos de tenant y estado ordenados como se muestran. El filtro de texto
/// (<c>Coincide</c>) y la traducción a SQL no corren en InMemory: los cubre <c>UsuariosGrillaEndpointTests</c> contra
/// Postgres.
///
/// Posiciones (1 a 5, en orden de alta), vistas como actor de plataforma: usuario mario/lucia/juan/ana/pedro, mail
/// d/z/b/y/c, tenant Zeta SRL/plataforma/Alfa SA/Beta SA/Alfa SA, rol Admin/Root/Vendedor/Supervisor/Admin, estado
/// Activo/Activo/Bloqueado/Inactivo/Activo, última conexión hace 3 días/-/-/1 día/2 días.
/// </summary>
public class UsuariosOrdenServicioTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = UsuariosOrdenServicioTests.Ahora;
    }

    private sealed class ContextoFijo(RolConocido rol, int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol { get; } = rol;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    private static ServicioDeUsuarios CrearServicio(string baseDeDatos, RolConocido rol, int? idTenant)
    {
        var tenantActual = idTenant is int id ? new TenantActualFijo(ModoDeAcceso.Tenant, id) : TenantActualFijo.Plataforma;
        var db = CrearContexto(baseDeDatos, tenantActual);
        var reloj = new RelojFijo();
        var contexto = new ContextoFijo(rol, idTenant);

        return new ServicioDeUsuarios(
            db,
            CrearContexto(baseDeDatos, TenantActualFijo.Plataforma),
            new HasheadorPbkdf2(),
            reloj,
            contexto,
            new ServicioDeAuditoria(db, reloj, contexto),
            new InspectorDeUso(db));
    }

    private sealed record Sembrado(List<int> Ids, int IdTenantAlfa, int IdTenantDeBaja);

    private static async Task<Sembrado> SembrarAsync(string baseDeDatos)
    {
        await using var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        foreach (var rol in Enum.GetValues<RolConocido>())
        {
            siembra.Roles.Add(new Rol { Id = (int)rol, Nombre = rol.ToString(), CreatedAt = Ahora, UpdatedAt = Ahora });
        }

        Tenant NuevoTenant(string nombre, bool baja = false) => new()
        {
            Nombre = nombre, DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        var zeta = NuevoTenant("Zeta SRL");
        var alfa = NuevoTenant("Alfa SA");
        var beta = NuevoTenant("Beta SA");
        var deBaja = NuevoTenant("Gone", baja: true);
        siembra.Tenants.AddRange(zeta, alfa, beta, deBaja);
        await siembra.SaveChangesAsync();

        (string Usuario, string Mail, int? Tenant, RolConocido Rol, EstadoUsuario Estado, int? DiasDesdeConexion)[] filas =
        [
            ("mario", "d@x", zeta.Id, RolConocido.Admin, EstadoUsuario.Activo, 3),
            ("lucia", "z@x", null, RolConocido.Root, EstadoUsuario.Activo, null),
            ("juan", "b@x", alfa.Id, RolConocido.Vendedor, EstadoUsuario.Bloqueado, null),
            ("ana", "y@x", beta.Id, RolConocido.Supervisor, EstadoUsuario.Inactivo, 1),
            ("pedro", "c@x", alfa.Id, RolConocido.Admin, EstadoUsuario.Activo, 2),
        ];

        var ids = new List<int>();
        var hasheador = new HasheadorPbkdf2();
        foreach (var f in filas)
        {
            var usuario = new Usuario
            {
                IdTenant = f.Tenant, NombreUsuario = f.Usuario, Mail = f.Mail, RolId = (int)f.Rol, Estado = f.Estado,
                UltimaConexion = f.DiasDesdeConexion is int dias ? Ahora.AddDays(-dias) : null,
                PasswordHash = hasheador.Hashear("una-contraseña-larga"), PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = Ahora, CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.Usuarios.Add(usuario);
            await siembra.SaveChangesAsync();
            ids.Add(usuario.Id);
        }

        return new Sembrado(ids, alfa.Id, deBaja.Id);
    }

    private static Task<List<int>> RecorrerAsync(
        ServicioDeUsuarios servicio, string? orden, string? direccion, FiltrosDeUsuarios? filtros = null) =>
        RecorridoDePaginas.RecorrerAsync(
            async (pagina, tamanio) => await servicio.ListarAsync(
                pagina: pagina, tamanio: tamanio, filtros: filtros, orden: orden, direccion: direccion),
            (UsuarioListado u) => u.Id);

    [Theory]
    [InlineData("id", "ascendente", "1,2,3,4,5")]
    [InlineData("id", "descendente", "5,4,3,2,1")]
    [InlineData("usuario", "ascendente", "4,3,2,1,5")]
    [InlineData("usuario", "descendente", "5,1,2,3,4")]
    [InlineData("mail", "ascendente", "3,5,1,4,2")]
    [InlineData("mail", "descendente", "2,4,1,5,3")]
    [InlineData("tenant", "ascendente", "3,5,4,2,1")]
    [InlineData("tenant", "descendente", "1,2,4,3,5")]
    [InlineData("rol", "ascendente", "1,5,2,4,3")]
    [InlineData("rol", "descendente", "3,4,2,1,5")]
    [InlineData("estado", "ascendente", "1,2,5,3,4")]
    [InlineData("estado", "descendente", "4,3,1,2,5")]
    [InlineData("ultimaConexion", "ascendente", "1,5,4,2,3")]
    [InlineData("ultimaConexion", "descendente", "4,5,1,2,3")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var baseDeDatos = $"usuarios-orden-{orden}-{direccion}";
        var sembrado = await SembrarAsync(baseDeDatos);
        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);

        var obtenido = await RecorrerAsync(servicio, orden, direccion);

        Assert.Equal(RecorridoDePaginas.PorPosicion(sembrado.Ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsRolYLuegoUsuario()
    {
        var baseDeDatos = nameof(SinOrdenSigueElOrdenDeSiempreQueEsRolYLuegoUsuario);
        var sembrado = await SembrarAsync(baseDeDatos);
        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);

        // Root(lucia), Admin(mario, pedro), Supervisor(ana), Vendedor(juan)
        Assert.Equal(RecorridoDePaginas.PorPosicion(sembrado.Ids, "2,1,5,4,3"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task LosFiltrosExactosSeCombinanYElTotalEsElDelFiltro()
    {
        var baseDeDatos = nameof(LosFiltrosExactosSeCombinanYElTotalEsElDelFiltro);
        var sembrado = await SembrarAsync(baseDeDatos);
        var ids = sembrado.Ids;
        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);

        var porId = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Id: ids[2]));
        var porRol = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(RolId: (int)RolConocido.Admin), orden: "usuario");
        var porEstado = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Estado: "Bloqueado"));
        var conectados = await servicio.ListarAsync(tamanio: 2, filtros: new FiltrosDeUsuarios(Conectado: true), orden: "ultimaConexion");
        var nuncaConectados = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Conectado: false));
        var delTenant = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: sembrado.IdTenantAlfa.ToString()), orden: "usuario", direccion: "descendente");
        var deLaPlataforma = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: OrdenDeUsuarios.ValorSinTenant));
        var combinado = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: sembrado.IdTenantAlfa.ToString(), RolId: (int)RolConocido.Admin));

        Assert.Equal([ids[2]], porId.Items.Select(u => u.Id).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "1,5"), porRol.Items.Select(u => u.Id).ToList());
        Assert.Equal([ids[2]], porEstado.Items.Select(u => u.Id).ToList());
        Assert.Equal(3, conectados.Total);
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "1,5"), conectados.Items.Select(u => u.Id).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "2,3"), nuncaConectados.Items.Select(u => u.Id).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "5,3"), delTenant.Items.Select(u => u.Id).ToList());
        Assert.Equal([ids[1]], deLaPlataforma.Items.Select(u => u.Id).ToList());
        Assert.Equal([ids[4]], combinado.Items.Select(u => u.Id).ToList());
    }

    [Fact]
    public async Task ElIdDeUnTenantDadoDeBajaNoCoincideConNadaNiConLasCuentasHuerfanas()
    {
        var baseDeDatos = nameof(ElIdDeUnTenantDadoDeBajaNoCoincideConNadaNiConLasCuentasHuerfanas);
        var sembrado = await SembrarAsync(baseDeDatos);
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var huerfano = new Usuario
            {
                IdTenant = sembrado.IdTenantDeBaja, NombreUsuario = "huerfano", Mail = "h@x", RolId = (int)RolConocido.Vendedor,
                PasswordHash = "x", PasswordAlgoritmo = "x", PasswordActualizadoEl = Ahora, CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.Usuarios.Add(huerfano);
            await siembra.SaveChangesAsync();
        }

        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);

        var delTenantDeBaja = await servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: sembrado.IdTenantDeBaja.ToString()));
        var todos = await servicio.ListarAsync(tamanio: 200);

        Assert.Empty(delTenantDeBaja.Items);
        Assert.Equal(0, delTenantDeBaja.Total);
        var huerfanoListado = Assert.Single(todos.Items, u => u.Usuario == "huerfano");
        Assert.Null(huerfanoListado.NombreTenant);
        Assert.Equal(sembrado.IdTenantDeBaja, huerfanoListado.IdTenant);
    }

    [Fact]
    public async Task UnActorDeTenantNoPuedeFiltrarNiOrdenarPorTenant()
    {
        var baseDeDatos = nameof(UnActorDeTenantNoPuedeFiltrarNiOrdenarPorTenant);
        var sembrado = await SembrarAsync(baseDeDatos);
        var servicio = CrearServicio(baseDeDatos, RolConocido.Admin, sembrado.IdTenantAlfa);

        var filtro = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: sembrado.IdTenantAlfa.ToString())));
        var sinTenant = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ListarAsync(filtros: new FiltrosDeUsuarios(Tenant: OrdenDeUsuarios.ValorSinTenant)));
        var orden = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: "tenant"));

        Assert.Equal("filtro_tenant_no_permitido", filtro.Codigo);
        Assert.Equal("filtro_tenant_no_permitido", sinTenant.Codigo);
        Assert.Equal("orden_no_permitido", orden.Codigo);
        Assert.All(new[] { filtro, sinTenant, orden }, e => Assert.Equal(400, e.EstadoHttp));

        // Sus propias filas siguen ordenables por las otras columnas, acotadas a su tenant.
        var propias = await servicio.ListarAsync(orden: "usuario", direccion: "descendente");
        Assert.Equal(["pedro", "juan"], propias.Items.Select(u => u.Usuario).ToList());
    }

    [Theory]
    [InlineData("acciones", null, "orden_invalido")]
    [InlineData("Usuario", null, "orden_invalido")]
    [InlineData("usuario", "asc", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenInvalidoSeRechazaConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var baseDeDatos = $"usuarios-400-{orden}-{direccion}";
        await SembrarAsync(baseDeDatos);
        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("estado", "activo", "estado_invalido")]
    [InlineData("estado", "1", "estado_invalido")]
    [InlineData("tenant", "abc", "tenant_invalido")]
    [InlineData("tenant", "-3", "tenant_invalido")]
    [InlineData("tenant", "3.5", "tenant_invalido")]
    public async Task UnFiltroInvalidoSeRechazaConCuatrocientos(string filtro, string valor, string codigo)
    {
        var baseDeDatos = $"usuarios-filtro-400-{filtro}-{valor}";
        await SembrarAsync(baseDeDatos);
        var servicio = CrearServicio(baseDeDatos, RolConocido.Root, idTenant: null);
        var filtros = filtro == "estado" ? new FiltrosDeUsuarios(Estado: valor) : new FiltrosDeUsuarios(Tenant: valor);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(filtros: filtros));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesDeOrdenSonLasColumnasDeDatosDeLaGrilla()
    {
        Assert.Equal(
            ["estado", "id", "mail", "rol", "tenant", "ultimaConexion", "usuario"],
            OrdenDeUsuarios.Claves.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void LasEtiquetasDeEstadoSonLosNombresDelEnumQueLaWebMuestra()
    {
        Assert.Equal(
            new Dictionary<EstadoUsuario, string>
            {
                [EstadoUsuario.Activo] = "Activo",
                [EstadoUsuario.Inactivo] = "Inactivo",
                [EstadoUsuario.Bloqueado] = "Bloqueado",
            },
            OrdenDeUsuarios.EtiquetasDeEstado);
        Assert.Equal("Plataforma", OrdenDeUsuarios.EtiquetaPlataforma);
        Assert.Equal("—", OrdenDeUsuarios.EtiquetaSinDuenio);
    }
}
