using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Articulos;

/// <summary>
/// <see cref="ServicioDeGrillaDeArticulos"/> con orden, sobre InMemory: la secuencia completa sale
/// de recorrer las páginas de a dos (el orden se aplica antes de paginar), con vacíos al final en las
/// dos direcciones y un proveedor de baja lógica ordenado como "sin proveedor". Lo que InMemory NO
/// puede probar —la traducción a SQL, la colación y el desempate por id contra un empate real—
/// lo cubre <c>ArticulosGrillaOrdenEndpointTests</c> contra Postgres.
///
/// Posiciones (1 a 5, en orden de alta): nombre CC/AA/EE/BB/DD, código K3/K5/K1/K4/K2, costo
/// 20/-/40/10/-, proveedor PB/-/PA/baja/PC, activo sí/no/sí/sí/no, precio 300/-/100/200/400.
/// </summary>
public class ArticulosGrillaOrdenServicioTests
{
    private const int IdTenant = 7;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ArticulosGrillaOrdenServicioTests.Ahora;
    }

    private sealed class ContextoFijo(RolConocido rol) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol { get; } = rol;
        public int? IdTenant => ArticulosGrillaOrdenServicioTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    private static async Task<(ServicioDeGrillaDeArticulos Servicio, int[] Ids)> PrepararAsync(
        string baseDeDatos, RolConocido rol = RolConocido.Admin)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var area = new Area { IdTenant = IdTenant, Nombre = "A", Orden = 1, CreatedAt = Ahora, UpdatedAt = Ahora };
            var alicuota = new AlicuotaIva { Nombre = "21%", Porcentaje = 21m, CreatedAt = Ahora, UpdatedAt = Ahora };
            var lista = new ListaPrecio
            {
                IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija, Activo = true,
                CreatedAt = Ahora, UpdatedAt = Ahora
            };
            Proveedor Prov(string razon, bool baja = false) => new()
            {
                IdTenant = IdTenant, RazonSocial = razon, IdCondicionFiscal = 1, DeletedAt = baja ? Ahora : null,
                CreatedAt = Ahora, UpdatedAt = Ahora
            };
            var pa = Prov("PA");
            var pb = Prov("PB");
            var pc = Prov("PC");
            var baja = Prov("ZZ-baja", baja: true);
            siembra.AddRange(area, alicuota, lista, pa, pb, pc, baja);
            await siembra.SaveChangesAsync();

            (string Nombre, string Codigo, decimal? Costo, int? Prov, bool Activo, decimal? Precio)[] filas =
            [
                ("CC", "K3", 20m, pb.Id, true, 300m),
                ("AA", "K5", null, null, false, null),
                ("EE", "K1", 40m, pa.Id, true, 100m),
                ("BB", "K4", 10m, baja.Id, true, 200m),
                ("DD", "K2", null, pc.Id, false, 400m),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var articulo = new Articulo
                {
                    IdTenant = IdTenant, CodigoInterno = f.Codigo, Nombre = f.Nombre, IdArea = area.Id,
                    IdAlicuotaIva = alicuota.Id, IdProveedorHabitual = f.Prov, UnidadVenta = UnidadVenta.Unidad,
                    EsProducto = true, Activo = f.Activo, CostoNominal = f.Costo, DisponibleVenta = true,
                    CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Articulos.Add(articulo);
                await siembra.SaveChangesAsync();
                ids[i] = articulo.Id;

                if (f.Precio is { } monto)
                {
                    siembra.Precios.Add(new Precio
                    {
                        IdTenant = IdTenant, IdArticulo = articulo.Id, IdListaPrecio = lista.Id, Monto = monto,
                        VigenteDesde = Ahora.AddYears(-1), CreatedAt = Ahora, UpdatedAt = Ahora
                    });
                    await siembra.SaveChangesAsync();
                }
            }
        }

        var db = CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));
        var reloj = new RelojFijo();
        var contexto = new ContextoFijo(rol);
        var servicio = new ServicioDeGrillaDeArticulos(
            db, reloj, new ServicioDePrecios(db, reloj, contexto), contexto, Options.Create(new OpcionesDeGrillaDeArticulos()));

        return (servicio, ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeGrillaDeArticulos servicio, string? orden, string? direccion)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 5; pagina++)
        {
            var p = await servicio.ListarAsync(pagina: pagina, tamanio: 2, orden: orden, direccion: direccion);
            ids.AddRange(p.Items.Select(i => i.Id));
            if (pagina * 2 >= p.Total) break;
        }

        return ids;
    }

    private static List<int> PorPosicion(int[] ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];

    [Theory]
    [InlineData("nombre", "ascendente", "2,4,1,5,3")]
    [InlineData("nombre", "descendente", "3,5,1,4,2")]
    [InlineData("codigo", "ascendente", "3,5,1,4,2")]
    [InlineData("costo", "ascendente", "4,1,3,2,5")]
    [InlineData("costo", "descendente", "3,1,4,2,5")]
    [InlineData("precio", "ascendente", "3,4,1,5,2")]
    [InlineData("precio", "descendente", "5,1,4,3,2")]
    [InlineData("proveedor", "ascendente", "3,1,5,2,4")]
    [InlineData("proveedor", "descendente", "5,1,3,2,4")]
    [InlineData("estado", "ascendente", "2,5,1,3,4")]
    [InlineData("estado", "descendente", "1,3,4,2,5")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"orden-{orden}-{direccion}");

        var obtenido = await RecorrerAsync(servicio, orden, direccion);

        Assert.Equal(PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempre()
    {
        var (servicio, ids) = await PrepararAsync(nameof(SinOrdenSigueElOrdenDeSiempre));

        Assert.Equal(PorPosicion(ids, "2,4,1,5,3"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task OrdenarPorPrecioConUnFiltroDePrecioOrdenaSoloLosQueCumplen()
    {
        var (servicio, ids) = await PrepararAsync(nameof(OrdenarPorPrecioConUnFiltroDePrecioOrdenaSoloLosQueCumplen));

        var pagina = await servicio.ListarAsync(precioDesde: 150m, orden: "precio", direccion: "descendente");

        Assert.Equal(PorPosicion(ids, "5,1,4"), pagina.Items.Select(i => i.Id).ToList());
        Assert.Equal(3, pagina.Total);
    }

    [Fact]
    public async Task UnVendedorNoPuedeOrdenarPorCostoNiAunqueElOrdenSeaValido()
    {
        var (servicio, _) = await PrepararAsync(nameof(UnVendedorNoPuedeOrdenarPorCostoNiAunqueElOrdenSeaValido), RolConocido.Vendedor);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: "costo", direccion: "ascendente"));

        Assert.Equal("orden_no_permitido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.NotEmpty((await servicio.ListarAsync(orden: "nombre")).Items);
    }

    [Fact]
    public async Task UnOrdenInvalidoSeRechazaSinConsultarNada()
    {
        var (servicio, _) = await PrepararAsync(nameof(UnOrdenInvalidoSeRechazaSinConsultarNada));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: "acciones"));

        Assert.Equal("orden_invalido", error.Codigo);
    }

    [Fact]
    public async Task OrdenarPorPrecioRechazaSiLosCandidatosExcedenElTope()
    {
        await PrepararAsync(nameof(OrdenarPorPrecioRechazaSiLosCandidatosExcedenElTope));
        var conTopeBajo = new ServicioDeGrillaDeArticulos(
            CrearContexto(nameof(OrdenarPorPrecioRechazaSiLosCandidatosExcedenElTope), new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant)),
            new RelojFijo(), null!, new ContextoFijo(RolConocido.Admin),
            Options.Create(new OpcionesDeGrillaDeArticulos { TopeDeCandidatosPorFiltroDePrecio = 3 }));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => conTopeBajo.ListarAsync(orden: "precio", direccion: "ascendente"));

        Assert.Equal("orden_precio_excede_tope", error.Codigo);
    }
}
