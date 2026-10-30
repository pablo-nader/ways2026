using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Listados;

/// <summary>Base de los tests de orden en el servidor de los listados de documentos, sobre InMemory: cada caso
/// recorre las páginas de a dos y compara la secuencia completa (el orden se aplica antes de paginar), con
/// vacíos al final en las dos direcciones y referencias de baja lógica o colgantes tratadas como vacías. Lo que
/// InMemory NO puede probar —la traducción a SQL y la colación— lo cubre cada test de integración contra Postgres.
/// Las posiciones (1 a 5) son el orden de alta, es decir de id.</summary>
public abstract class OrdenDeDocumentosBase
{
    protected const int IdTenant = 7;
    protected static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    protected const int IdColgante = 9999;

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = OrdenDeDocumentosBase.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => OrdenDeDocumentosBase.IdTenant;
    }

    protected static IRelojDelSistema Reloj { get; } = new RelojFijo();

    protected static IContextoDeUsuario Contexto { get; } = new ContextoFijo();

    protected static DateTimeOffset Dia(int mes, int dia) => new(2026, mes, dia, 12, 0, 0, TimeSpan.Zero);

    protected static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    protected static WaysDbContext ContextoDelTenant(string baseDeDatos) =>
        CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));

    protected static Proveedor Prov(string razon, bool baja = false) => new()
    {
        IdTenant = IdTenant, RazonSocial = razon, IdCondicionFiscal = 1, DeletedAt = baja ? Ahora : null,
        CreatedAt = Ahora, UpdatedAt = Ahora
    };

    protected static Cliente Cli(int numero, bool baja = false) => new()
    {
        IdTenant = IdTenant, Numero = numero, Nombre = $"C{numero}", DeletedAt = baja ? Ahora : null,
        CreatedAt = Ahora, UpdatedAt = Ahora
    };

    protected static PuntoVenta Pv(string nombre) => new()
    {
        IdTenant = IdTenant, Nombre = nombre, CreatedAt = Ahora, UpdatedAt = Ahora
    };

    protected static List<int> PorPosicion(int[] ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];
}

public class OrdenDeComprasServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeCompras Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var pa = Prov("PA");
            var pb = Prov("PB");
            var pc = Prov("PC");
            var baja = Prov("ZZ-baja", baja: true);
            var pv = Pv("PV");
            TipoComprobante Tipo(string codigo) => new()
            {
                Clase = ClaseComprobante.Compra, Codigo = codigo, Nombre = codigo, CreatedAt = Ahora, UpdatedAt = Ahora
            };
            var fa = Tipo("FA");
            var fb = Tipo("FB");
            var fc = Tipo("FC");
            siembra.AddRange(pa, pb, pc, baja, pv, fa, fb, fc);
            await siembra.SaveChangesAsync();

            (string? Numero, int Prov, int Tipo, EstadoCompra Estado, DateTimeOffset? Fecha, decimal Total)[] filas =
            [
                ("B-2", pb.Id, fa.Id, EstadoCompra.Confirmada, Dia(3, 5), 50m),
                (null, pa.Id, fc.Id, EstadoCompra.Borrador, null, 10m),
                ("A-1", baja.Id, fb.Id, EstadoCompra.Confirmada, Dia(1, 10), 30m),
                ("C-3", pc.Id, IdColgante, EstadoCompra.Anulada, Dia(2, 1), 20m),
                ("A-0", IdColgante, fa.Id, EstadoCompra.Borrador, Dia(4, 1), 40m),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var compra = new ComprobanteCompra
                {
                    IdTenant = IdTenant, IdPuntoVenta = pv.Id, IdProveedor = f.Prov, IdTipoComprobante = f.Tipo,
                    NumeroExterno = f.Numero, Estado = f.Estado, FechaRecepcion = f.Fecha, Total = f.Total,
                    CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.ComprobantesCompra.Add(compra);
                await siembra.SaveChangesAsync();
                ids[i] = compra.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeCompras(db, Reloj, Contexto, null!, null!), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeCompras servicio, string? orden, string? direccion)
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

    [Theory]
    [InlineData("numero", "ascendente", "5,3,1,4,2")]
    [InlineData("numero", "descendente", "4,1,3,5,2")]
    [InlineData("proveedor", "ascendente", "2,1,4,3,5")]
    [InlineData("proveedor", "descendente", "4,1,2,3,5")]
    [InlineData("tipo", "ascendente", "1,5,3,2,4")]
    [InlineData("tipo", "descendente", "2,3,1,5,4")]
    [InlineData("estado", "ascendente", "4,2,5,1,3")]
    [InlineData("estado", "descendente", "1,3,2,5,4")]
    [InlineData("fechaRecepcion", "ascendente", "3,4,1,5,2")]
    [InlineData("fechaRecepcion", "descendente", "5,1,4,3,2")]
    [InlineData("total", "ascendente", "2,4,3,5,1")]
    [InlineData("total", "descendente", "1,5,3,4,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"compras-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorIdDescendente()
    {
        var (servicio, ids) = await PrepararAsync("compras-defecto");

        Assert.Equal(PorPosicion(ids, "5,4,3,2,1"), await RecorrerAsync(servicio, null, null));
    }

    [Theory]
    [InlineData("estadoPago", null, "orden_invalido")]
    [InlineData("numero", "sube", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"compras-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["numero", "proveedor", "tipo", "estado", "fechaRecepcion", "total"],
            ServicioDeCompras.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDePresupuestosServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDePresupuestos Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var empresa = new Empresa { IdTenant = IdTenant, RazonSocial = "E", CreatedAt = Ahora, UpdatedAt = Ahora };
            siembra.Empresas.Add(empresa);
            await siembra.SaveChangesAsync();

            var pvA = Pv("Zeta");
            var pvB = Pv("Alfa");
            pvA.IdEmpresa = empresa.Id;
            pvB.IdEmpresa = empresa.Id;
            var c1 = Cli(10);
            var c2 = Cli(2);
            var baja = Cli(50, baja: true);
            siembra.AddRange(pvA, pvB, c1, c2, baja);
            await siembra.SaveChangesAsync();

            (long? Numero, int Cliente, int Pv, EstadoPresupuesto Estado, DateOnly? Vence, DateTimeOffset Emision, decimal Total)[] filas =
            [
                (5, c1.Id, pvA.Id, EstadoPresupuesto.Enviado, new DateOnly(2026, 6, 10), Dia(5, 3), 100m),
                (null, c2.Id, pvB.Id, EstadoPresupuesto.Borrador, null, Dia(5, 1), 50m),
                (7, baja.Id, pvA.Id, EstadoPresupuesto.Enviado, new DateOnly(2026, 6, 1), Dia(5, 5), 300m),
                (3, IdColgante, pvB.Id, EstadoPresupuesto.Convertido, new DateOnly(2026, 7, 1), Dia(5, 2), 200m),
                (1, c2.Id, pvA.Id, EstadoPresupuesto.Anulado, null, Dia(5, 4), 400m),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var presupuesto = new Presupuesto
                {
                    IdTenant = IdTenant, IdPuntoVenta = f.Pv, IdCliente = f.Cliente, Numero = f.Numero,
                    Estado = f.Estado, Vencimiento = f.Vence, FechaEmision = f.Emision, Total = f.Total,
                    CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Presupuestos.Add(presupuesto);
                await siembra.SaveChangesAsync();
                ids[i] = presupuesto.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        var parametros = new ServicioDeParametros(db, Reloj, new ServicioDeLotes(db, Reloj, Contexto));
        return (new ServicioDePresupuestos(db, Reloj, Contexto, null!, parametros), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDePresupuestos servicio, string? orden, string? direccion)
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

    [Theory]
    [InlineData("cliente", "ascendente", "2,5,1,3,4")]
    [InlineData("cliente", "descendente", "1,2,5,3,4")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "5,2,4,1,3")]
    [InlineData("estado", "descendente", "1,3,4,2,5")]
    [InlineData("vencimiento", "ascendente", "3,1,2,4,5")]
    [InlineData("vencimiento", "descendente", "1,3,2,4,5")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("total", "ascendente", "2,1,4,3,5")]
    [InlineData("total", "descendente", "5,3,4,1,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"presupuestos-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task ElNumeroOrdenaPorPuntoDeVentaYNumeroConElBorradorSinNumeroAlFinal()
    {
        var (servicio, ids) = await PrepararAsync("presupuestos-numero");

        Assert.Equal(PorPosicion(ids, "5,1,3,4,2"), await RecorrerAsync(servicio, "numero", "ascendente"));
        Assert.Equal(PorPosicion(ids, "4,3,1,5,2"), await RecorrerAsync(servicio, "numero", "descendente"));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendente()
    {
        var (servicio, ids) = await PrepararAsync("presupuestos-defecto");

        Assert.Equal(PorPosicion(ids, "3,5,1,4,2"), await RecorrerAsync(servicio, null, null));
    }

    [Theory]
    [InlineData("iva", null, "orden_invalido")]
    [InlineData("total", "sube", "direccion_invalida")]
    [InlineData(null, "descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"presupuestos-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["numero", "cliente", "puntoVenta", "estado", "vencimiento", "fechaEmision", "total"],
            ServicioDePresupuestos.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDeRemitosServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeRemitos Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var pvA = Pv("Zeta");
            var pvB = Pv("Alfa");
            var c1 = Cli(10);
            var c2 = Cli(2);
            var baja = Cli(50, baja: true);
            siembra.AddRange(pvA, pvB, c1, c2, baja);
            await siembra.SaveChangesAsync();

            (long? Numero, int Cliente, int Pv, EstadoRemito Estado, DateTimeOffset Emision, decimal Total)[] filas =
            [
                (5, c1.Id, pvA.Id, EstadoRemito.Emitido, Dia(5, 3), 100m),
                (null, c2.Id, pvB.Id, EstadoRemito.Borrador, Dia(5, 1), 50m),
                (7, baja.Id, pvA.Id, EstadoRemito.Facturado, Dia(5, 5), 300m),
                (3, IdColgante, pvB.Id, EstadoRemito.Anulado, Dia(5, 2), 200m),
                (1, c2.Id, pvA.Id, EstadoRemito.Emitido, Dia(5, 4), 400m),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var remito = new Remito
                {
                    IdTenant = IdTenant, IdPuntoVenta = f.Pv, IdCliente = f.Cliente, Numero = f.Numero,
                    Estado = f.Estado, FechaEmision = f.Emision, Total = f.Total, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Remitos.Add(remito);
                await siembra.SaveChangesAsync();
                ids[i] = remito.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeRemitos(db, Reloj, Contexto, null!, null!), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeRemitos servicio, string? orden, string? direccion)
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

    [Theory]
    [InlineData("numero", "ascendente", "5,1,3,4,2")]
    [InlineData("numero", "descendente", "4,3,1,5,2")]
    [InlineData("cliente", "ascendente", "2,5,1,3,4")]
    [InlineData("cliente", "descendente", "1,2,5,3,4")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "4,2,1,5,3")]
    [InlineData("estado", "descendente", "3,1,5,2,4")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("total", "ascendente", "2,1,4,3,5")]
    [InlineData("total", "descendente", "5,3,4,1,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"remitos-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendente()
    {
        var (servicio, ids) = await PrepararAsync("remitos-defecto");

        Assert.Equal(PorPosicion(ids, "3,5,1,4,2"), await RecorrerAsync(servicio, null, null));
    }

    [Theory]
    [InlineData("vencimiento", null, "orden_invalido")]
    [InlineData("total", "sube", "direccion_invalida")]
    [InlineData(null, "descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"remitos-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["numero", "cliente", "puntoVenta", "estado", "fechaEmision", "total"],
            ServicioDeRemitos.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDeOrdenesDeCompraServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeOrdenesDeCompra Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var pa = Prov("PA");
            var pb = Prov("PB");
            var pc = Prov("PC");
            var baja = Prov("ZZ-baja", baja: true);
            var pvA = Pv("Zeta");
            var pvB = Pv("Alfa");
            siembra.AddRange(pa, pb, pc, baja, pvA, pvB);
            await siembra.SaveChangesAsync();

            (long? Numero, int Prov, int Pv, EstadoOrdenCompra Estado, DateTimeOffset Emision, DateOnly? Espera)[] filas =
            [
                (30, pb.Id, pvA.Id, EstadoOrdenCompra.Enviada, Dia(5, 3), new DateOnly(2026, 6, 10)),
                (null, pa.Id, pvB.Id, EstadoOrdenCompra.Borrador, Dia(5, 1), null),
                (10, baja.Id, pvA.Id, EstadoOrdenCompra.RecibidaParcial, Dia(5, 5), new DateOnly(2026, 6, 1)),
                (20, pc.Id, pvB.Id, EstadoOrdenCompra.Cerrada, Dia(5, 2), new DateOnly(2026, 7, 1)),
                (5, IdColgante, pvA.Id, EstadoOrdenCompra.Anulada, Dia(5, 4), null),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var orden = new OrdenCompra
                {
                    IdTenant = IdTenant, IdPuntoVenta = f.Pv, IdProveedor = f.Prov, Numero = f.Numero,
                    Estado = f.Estado, FechaEmision = f.Emision, FechaEsperada = f.Espera, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.OrdenesCompra.Add(orden);
                await siembra.SaveChangesAsync();
                ids[i] = orden.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeOrdenesDeCompra(db, Reloj, Contexto), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeOrdenesDeCompra servicio, string? orden, string? direccion)
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

    [Theory]
    [InlineData("numero", "ascendente", "5,3,4,1,2")]
    [InlineData("numero", "descendente", "1,4,3,5,2")]
    [InlineData("proveedor", "ascendente", "2,1,4,3,5")]
    [InlineData("proveedor", "descendente", "4,1,2,3,5")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "5,2,4,1,3")]
    [InlineData("estado", "descendente", "3,1,4,2,5")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("fechaEsperada", "ascendente", "3,1,4,2,5")]
    [InlineData("fechaEsperada", "descendente", "4,1,3,2,5")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"oc-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendente()
    {
        var (servicio, ids) = await PrepararAsync("oc-defecto");

        Assert.Equal(PorPosicion(ids, "3,5,1,4,2"), await RecorrerAsync(servicio, null, null));
    }

    [Theory]
    [InlineData("total", null, "orden_invalido")]
    [InlineData("numero", "sube", "direccion_invalida")]
    [InlineData(null, "descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"oc-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["numero", "proveedor", "puntoVenta", "estado", "fechaEmision", "fechaEsperada"],
            ServicioDeOrdenesDeCompra.ClavesDeOrden.Keys.ToArray());
    }
}
