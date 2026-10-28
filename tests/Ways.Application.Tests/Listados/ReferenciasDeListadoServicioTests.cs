using Ways.Application.Compras;
using Ways.Application.Listados;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Application.Tests.Listados;

/// <summary>El cliente o proveedor de cada fila de Compras, Presupuestos, Remitos y Órdenes de compra sale del servidor
/// junto con la fila, nunca de una lista de referencia acotada del front: una fila cuya referencia está dada de baja o
/// colgante sigue en la página con el dato vacío, igual que cuando se ordena por esa columna.</summary>
public class ReferenciasDeListadoServicioTests : OrdenDeDocumentosBase
{
    private static Cliente CliCompleto(int numero, string? razonSocial, string nombre, string? apellido, bool baja = false) => new()
    {
        IdTenant = IdTenant, Numero = numero, RazonSocial = razonSocial, Nombre = nombre, Apellido = apellido,
        DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
    };

    [Theory]
    [InlineData("Ferretería Sur", "Ana", "Pérez", "Ferretería Sur")]
    [InlineData(null, "Ana", "Pérez", "Ana Pérez")]
    [InlineData(null, "Ana", null, "Ana")]
    [InlineData(null, "Ana", "", "Ana")]
    public void ElNombreDelClienteEsLaRazonSocialOSuNombreYApellidoSinHuecos(
        string? razonSocial, string nombre, string? apellido, string esperado)
    {
        Assert.Equal(esperado, ReferenciasDeListado.NombreDeCliente(razonSocial, nombre, apellido));
    }

    private sealed class ContextoDeRol(RolConocido rol) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => rol;
        public int? IdTenant => OrdenDeDocumentosBase.IdTenant;
    }

    // El catálogo de proveedores es solo de Admin: un Vendedor o un Supervisor ven la fila, pero rotulada por su id.
    [Theory]
    [InlineData(RolConocido.Admin, "Distribuidora Norte")]
    [InlineData(RolConocido.Supervisor, null)]
    [InlineData(RolConocido.Vendedor, null)]
    public async Task ElListadoDeComprasTraeLaRazonSocialDelProveedorSoloAAdminYVacioSiEstaDadoDeBajaOColgante(RolConocido rol, string? nombreVisible)
    {
        var baseDeDatos = $"referencias-compras-{rol}";
        int[] ids;
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var viva = Prov("Distribuidora Norte");
            var baja = Prov("ZZ-baja", baja: true);
            var pv = Pv("PV");
            var tipo = new TipoComprobante
            {
                Clase = ClaseComprobante.Compra, Codigo = "FA", Nombre = "FA", CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.AddRange(viva, baja, pv, tipo);
            await siembra.SaveChangesAsync();

            ids = new int[3];
            var proveedores = new[] { viva.Id, baja.Id, IdColgante };
            for (var i = 0; i < proveedores.Length; i++)
            {
                var compra = new ComprobanteCompra
                {
                    IdTenant = IdTenant, IdPuntoVenta = pv.Id, IdProveedor = proveedores[i], IdTipoComprobante = tipo.Id,
                    NumeroExterno = $"N-{i}", Estado = EstadoCompra.Borrador, Total = 10m, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.ComprobantesCompra.Add(compra);
                await siembra.SaveChangesAsync();
                ids[i] = compra.Id;
            }
        }

        var servicio = new ServicioDeCompras(ContextoDelTenant(baseDeDatos), Reloj, new ContextoDeRol(rol), null!, null!);

        var pagina = await servicio.ListarAsync(tamanio: 10);

        Assert.Equal(
            [(ids[2], (string?)null), (ids[1], null), (ids[0], nombreVisible)],
            pagina.Items.Select(i => (i.Id, i.NombreProveedor)).ToList());
    }

    [Theory]
    [InlineData(RolConocido.Admin, "Distribuidora Norte")]
    [InlineData(RolConocido.Supervisor, null)]
    [InlineData(RolConocido.Vendedor, null)]
    public async Task ElListadoDeOrdenesDeCompraTraeLaRazonSocialDelProveedorSoloAAdminYVacioSiEstaDadoDeBajaOColgante(RolConocido rol, string? nombreVisible)
    {
        var baseDeDatos = $"referencias-oc-{rol}";
        int[] ids;
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var viva = Prov("Distribuidora Norte");
            var baja = Prov("ZZ-baja", baja: true);
            var pv = Pv("PV");
            siembra.AddRange(viva, baja, pv);
            await siembra.SaveChangesAsync();

            ids = new int[3];
            var proveedores = new[] { viva.Id, baja.Id, IdColgante };
            for (var i = 0; i < proveedores.Length; i++)
            {
                var orden = new OrdenCompra
                {
                    IdTenant = IdTenant, IdPuntoVenta = pv.Id, IdProveedor = proveedores[i], IdEmpleado = 1,
                    Estado = EstadoOrdenCompra.Borrador, FechaEmision = Dia(5, 1 + i), CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.OrdenesCompra.Add(orden);
                await siembra.SaveChangesAsync();
                ids[i] = orden.Id;
            }
        }

        var servicio = new ServicioDeOrdenesDeCompra(ContextoDelTenant(baseDeDatos), Reloj, new ContextoDeRol(rol));

        var pagina = await servicio.ListarAsync(tamanio: 10);

        Assert.Equal(
            [(ids[2], (string?)null), (ids[1], null), (ids[0], nombreVisible)],
            pagina.Items.Select(i => (i.Id, i.NombreProveedor)).ToList());
    }

    private static async Task<(int Pv, int[] Clientes)> SembrarClientesAsync(string baseDeDatos)
    {
        await using var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        var empresa = new Empresa { IdTenant = IdTenant, RazonSocial = "E", CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        var pv = Pv("PV");
        pv.IdEmpresa = empresa.Id;
        var conRazon = CliCompleto(10, "Ferretería Sur", "Ana", "Pérez");
        var sinRazon = CliCompleto(11, null, "Juan", "Gómez");
        var baja = CliCompleto(12, "ZZ-baja", "Zoe", null, baja: true);
        siembra.AddRange(pv, conRazon, sinRazon, baja);
        await siembra.SaveChangesAsync();

        return (pv.Id, [conRazon.Id, sinRazon.Id, baja.Id, IdColgante]);
    }

    [Fact]
    public async Task ElListadoDePresupuestosTraeNumeroYNombreDelClienteYVacioSiEstaDadoDeBajaOColgante()
    {
        const string baseDeDatos = "referencias-presupuestos";
        var (idPv, clientes) = await SembrarClientesAsync(baseDeDatos);
        var ids = new int[clientes.Length];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            for (var i = 0; i < clientes.Length; i++)
            {
                var presupuesto = new Presupuesto
                {
                    IdTenant = IdTenant, IdPuntoVenta = idPv, IdCliente = clientes[i], Estado = EstadoPresupuesto.Borrador,
                    FechaEmision = Dia(5, 1 + i), Total = 10m, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Presupuestos.Add(presupuesto);
                await siembra.SaveChangesAsync();
                ids[i] = presupuesto.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        var parametros = new ServicioDeParametros(db, Reloj, new ServicioDeLotes(db, Reloj, Contexto));
        var servicio = new ServicioDePresupuestos(db, Reloj, Contexto, null!, parametros);

        var pagina = await servicio.ListarAsync(tamanio: 10);

        Assert.Equal(
            [(ids[3], (int?)null, (string?)null), (ids[2], null, null), (ids[1], 11, "Juan Gómez"), (ids[0], 10, "Ferretería Sur")],
            pagina.Items.Select(i => (i.Id, i.NumeroCliente, i.NombreCliente)).ToList());
    }

    [Fact]
    public async Task ElListadoDeRemitosTraeNumeroYNombreDelClienteYVacioSiEstaDadoDeBajaOColgante()
    {
        const string baseDeDatos = "referencias-remitos";
        var (idPv, clientes) = await SembrarClientesAsync(baseDeDatos);
        var ids = new int[clientes.Length];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            for (var i = 0; i < clientes.Length; i++)
            {
                var remito = new Remito
                {
                    IdTenant = IdTenant, IdPuntoVenta = idPv, IdCliente = clientes[i], Estado = EstadoRemito.Borrador,
                    FechaEmision = Dia(5, 1 + i), Total = 10m, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Remitos.Add(remito);
                await siembra.SaveChangesAsync();
                ids[i] = remito.Id;
            }
        }

        var servicio = new ServicioDeRemitos(ContextoDelTenant(baseDeDatos), Reloj, Contexto, null!, null!);

        var pagina = await servicio.ListarAsync(tamanio: 10);

        Assert.Equal(
            [(ids[3], (int?)null, (string?)null), (ids[2], null, null), (ids[1], 11, "Juan Gómez"), (ids[0], 10, "Ferretería Sur")],
            pagina.Items.Select(i => (i.Id, i.NumeroCliente, i.NombreCliente)).ToList());
    }
}
