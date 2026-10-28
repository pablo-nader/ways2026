using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Tests.Listados;
using Ways.Domain.Common;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Proveedores;

/// <summary>
/// <see cref="ServicioDeProveedores.ListarAsync"/> con orden y filtros que no son de texto, sobre InMemory: la secuencia
/// completa sale de recorrer las páginas de a dos (el orden se aplica antes de paginar), con las celdas vacías al final
/// en las dos direcciones. El filtro de texto (<c>Coincide</c>) y la traducción a SQL no corren en InMemory: los cubre
/// <c>ProveedoresGrillaEndpointTests</c> contra Postgres.
///
/// Posiciones (1 a 5, en orden de alta): razón social Delta/Alfa/Eco/Beta/Cima, CUIT 30-2/-/30-5/30-1/30-4,
/// teléfono 5/3/-/1/4, email -/z/y/x/-, margen 10/-/30/20/-, activo no/sí/sí/no/sí.
/// </summary>
public class ProveedoresOrdenServicioTests
{
    private const int IdTenant = 7;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ProveedoresOrdenServicioTests.Ahora;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    private static async Task<(ServicioDeProveedores Servicio, List<int> Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new List<int>();
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            (string Razon, string? Cuit, string? Telefono, string? Email, decimal? Margen, bool Activo)[] filas =
            [
                ("Delta", "30-2", "5", null, 10m, false),
                ("Alfa", null, "3", "z@x", null, true),
                ("Eco", "30-5", null, "y@x", 30m, true),
                ("Beta", "30-1", "1", "x@x", 20m, false),
                ("Cima", "30-4", "4", null, null, true),
            ];

            foreach (var f in filas)
            {
                var proveedor = new Proveedor
                {
                    IdTenant = IdTenant, RazonSocial = f.Razon, Cuit = f.Cuit, Telefono = f.Telefono, Email = f.Email,
                    Margen = f.Margen, Activo = f.Activo, IdCondicionFiscal = 1, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Proveedores.Add(proveedor);
                await siembra.SaveChangesAsync();
                ids.Add(proveedor.Id);
            }
        }

        var contexto = CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));
        var servicio = new ServicioDeProveedores(
            contexto, new RelojFijo(), new GuardaDeReferencias(contexto, new InspectorDeUso(contexto)));

        return (servicio, ids);
    }

    private static Task<List<int>> RecorrerAsync(
        ServicioDeProveedores servicio, string? orden, string? direccion, FiltrosDeProveedores? filtros = null) =>
        RecorridoDePaginas.RecorrerAsync(
            async (pagina, tamanio) => await servicio.ListarAsync(
                pagina: pagina, tamanio: tamanio, filtros: filtros, orden: orden, direccion: direccion),
            (ProveedorListado p) => p.Id);

    [Theory]
    [InlineData("razonSocial", "ascendente", "2,4,5,1,3")]
    [InlineData("razonSocial", "descendente", "3,1,5,4,2")]
    [InlineData("cuit", "ascendente", "4,1,5,3,2")]
    [InlineData("cuit", "descendente", "3,5,1,4,2")]
    [InlineData("telefono", "ascendente", "4,2,5,1,3")]
    [InlineData("telefono", "descendente", "1,5,2,4,3")]
    [InlineData("email", "ascendente", "4,3,2,1,5")]
    [InlineData("email", "descendente", "2,3,4,1,5")]
    [InlineData("margen", "ascendente", "1,4,3,2,5")]
    [InlineData("margen", "descendente", "3,4,1,2,5")]
    [InlineData("estado", "ascendente", "2,3,5,1,4")]
    [InlineData("estado", "descendente", "1,4,2,3,5")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"proveedores-orden-{orden}-{direccion}");

        var obtenido = await RecorrerAsync(servicio, orden, direccion);

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsLaRazonSocial()
    {
        var (servicio, ids) = await PrepararAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsLaRazonSocial));

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "2,4,5,1,3"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task ElRangoDeMargenEsInclusivoExcluyeAlQueNoTieneMargenYSeCombinaConElEstado()
    {
        var (servicio, ids) = await PrepararAsync(nameof(ElRangoDeMargenEsInclusivoExcluyeAlQueNoTieneMargenYSeCombinaConElEstado));

        var entre = await servicio.ListarAsync(filtros: new FiltrosDeProveedores(MargenDesde: 10m, MargenHasta: 20m));
        var desde = await servicio.ListarAsync(filtros: new FiltrosDeProveedores(MargenDesde: 15m), orden: "margen", direccion: "descendente");
        var hasta = await servicio.ListarAsync(filtros: new FiltrosDeProveedores(MargenHasta: 10m));
        var activosConMargen = await servicio.ListarAsync(filtros: new FiltrosDeProveedores(MargenDesde: 10m, Activo: true));

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "4,1"), entre.Items.Select(p => p.Id).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "3,4"), desde.Items.Select(p => p.Id).ToList());
        Assert.Equal([ids[0]], hasta.Items.Select(p => p.Id).ToList());
        Assert.Equal(1, activosConMargen.Total);
        Assert.Equal(ids[2], Assert.Single(activosConMargen.Items).Id);
    }

    [Theory]
    [InlineData("acciones", null, "orden_invalido")]
    [InlineData("RazonSocial", null, "orden_invalido")]
    [InlineData("margen", "asc", "direccion_invalida")]
    [InlineData(null, "descendente", "direccion_sin_orden")]
    public async Task UnOrdenInvalidoSeRechazaConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"proveedores-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesDeOrdenSonLasColumnasDeDatosDeLaGrilla()
    {
        Assert.Equal(
            ["cuit", "email", "estado", "margen", "razonSocial", "telefono"],
            OrdenDeProveedores.Claves.Keys.Order(StringComparer.Ordinal).ToList());
    }
}
