using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Clientes;
using Ways.Application.Tests.Listados;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Clientes;

/// <summary>
/// <see cref="ServicioDeClientes.ListarAsync"/> con orden y filtros que no son de texto, sobre InMemory: la secuencia
/// completa sale de recorrer las páginas de a dos (el orden se aplica antes de paginar), con las celdas vacías al final
/// en las dos direcciones. El filtro de texto (<c>Coincide</c>) y la traducción a SQL no corren en InMemory: los cubre
/// <c>ClientesGrillaEndpointTests</c> contra Postgres.
///
/// Posiciones (1 a 5, en orden de alta): número 20/50/30/10/40, nombre Carla Zeta/Ana/Eva Gómez/Beto/Dora Paz,
/// documento 300/-/100/-/200, teléfono 33/- (celular 22)/11/-/44, email d/-/e/b/c, activo sí/no/sí/sí/no.
/// </summary>
public class ClientesOrdenServicioTests
{
    private const int IdTenant = 7;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ClientesOrdenServicioTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => ClientesOrdenServicioTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    private static async Task<(ServicioDeClientes Servicio, List<int> Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new List<int>();
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            (int Numero, string Nombre, string? Apellido, string? Documento, string? Telefono, string? Celular, string? Email, bool Activo)[] filas =
            [
                (20, "Carla", "Zeta", "300", "33", null, "d@x", true),
                (50, "Ana", null, null, null, "22", null, false),
                (30, "Eva", "Gómez", "100", "11", null, "e@x", true),
                (10, "Beto", "", null, null, null, "b@x", true),
                (40, "Dora", "Paz", "200", "44", null, "c@x", false),
            ];

            foreach (var f in filas)
            {
                var cliente = new Cliente
                {
                    IdTenant = IdTenant, Numero = f.Numero, Nombre = f.Nombre, Apellido = f.Apellido,
                    TipoDocumento = f.Documento is null ? null : TipoDocumento.Dni, NumeroDocumento = f.Documento,
                    Telefono = f.Telefono, Celular = f.Celular, Email = f.Email, Activo = f.Activo,
                    IdCondicionFiscal = 1, IdListaPrecio = 1, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Clientes.Add(cliente);
                await siembra.SaveChangesAsync();
                ids.Add(cliente.Id);
            }
        }

        var servicio = new ServicioDeClientes(
            CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant)), new RelojFijo(), new ContextoFijo());

        return (servicio, ids);
    }

    private static Task<List<int>> RecorrerAsync(
        ServicioDeClientes servicio, string? orden, string? direccion, FiltrosDeClientes? filtros = null) =>
        RecorridoDePaginas.RecorrerAsync(
            async (pagina, tamanio) => await servicio.ListarAsync(
                pagina: pagina, tamanio: tamanio, filtros: filtros, orden: orden, direccion: direccion),
            (ClienteListado c) => c.Id);

    [Theory]
    [InlineData("numero", "ascendente", "4,1,3,5,2")]
    [InlineData("numero", "descendente", "2,5,3,1,4")]
    [InlineData("nombre", "ascendente", "2,4,1,5,3")]
    [InlineData("nombre", "descendente", "3,5,1,4,2")]
    [InlineData("documento", "ascendente", "3,5,1,2,4")]
    [InlineData("documento", "descendente", "1,5,3,2,4")]
    [InlineData("telefono", "ascendente", "3,2,1,5,4")]
    [InlineData("telefono", "descendente", "5,1,2,3,4")]
    [InlineData("email", "ascendente", "4,5,1,3,2")]
    [InlineData("email", "descendente", "3,1,5,4,2")]
    [InlineData("estado", "ascendente", "1,3,4,2,5")]
    [InlineData("estado", "descendente", "2,5,1,3,4")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"clientes-orden-{orden}-{direccion}");

        var obtenido = await RecorrerAsync(servicio, orden, direccion);

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsElNumero()
    {
        var (servicio, ids) = await PrepararAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsElNumero));

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "4,1,3,5,2"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task LaDireccionOmitidaEsAscendente()
    {
        var (servicio, ids) = await PrepararAsync(nameof(LaDireccionOmitidaEsAscendente));

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "2,4,1,5,3"), await RecorrerAsync(servicio, "nombre", null));
    }

    [Fact]
    public async Task ElFiltroDeEstadoYElDeNumeroSeCombinanConElOrdenYElTotalEsElDelFiltro()
    {
        var (servicio, ids) = await PrepararAsync(nameof(ElFiltroDeEstadoYElDeNumeroSeCombinanConElOrdenYElTotalEsElDelFiltro));

        var activos = await servicio.ListarAsync(
            tamanio: 2, filtros: new FiltrosDeClientes(Activo: true), orden: "numero", direccion: "descendente");
        var porNumero = await servicio.ListarAsync(filtros: new FiltrosDeClientes(Numero: 40));
        var inactivosConNumero = await servicio.ListarAsync(filtros: new FiltrosDeClientes(Numero: 20, Activo: false));

        Assert.Equal(3, activos.Total);
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "3,1"), activos.Items.Select(c => c.Id).ToList());
        Assert.Equal([ids[4]], porNumero.Items.Select(c => c.Id).ToList());
        Assert.Empty(inactivosConNumero.Items);
    }

    [Theory]
    [InlineData("acciones", null, "orden_invalido")]
    [InlineData("Numero", null, "orden_invalido")]
    [InlineData("numero", "asc", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenInvalidoSeRechazaConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"clientes-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesDeOrdenSonLasColumnasDeDatosDeLaGrilla()
    {
        Assert.Equal(
            ["documento", "email", "estado", "nombre", "numero", "telefono"],
            OrdenDeClientes.Claves.Keys.Order(StringComparer.Ordinal).ToList());
    }
}
