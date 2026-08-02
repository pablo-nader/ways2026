using Microsoft.EntityFrameworkCore;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Stage-2-clientes-proveedores, Slice 1 (tarea 1G, "tests that don't need the migration"):
/// construye el modelo real de <see cref="WaysDbContext"/> contra el proveedor de Npgsql sin
/// conectar a una base (mismo patrón que <c>ModeloDeCatalogosTests</c>/
/// <c>ModeloDeOrganizacionTests</c>) — alcanza para confirmar índices/FKs/constraint antes
/// de que exista la migración (DB CHANGE GATE pendiente).
/// </summary>
public class ModeloDeClientesYProveedoresTests
{
    private static WaysDbContext CrearContexto()
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=probe;Username=probe;Password=probe",
                npgsql =>
                {
                    npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                    npgsql.MapEnum<EstadoTenant>("estado_tenant");
                    npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                    npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                    npgsql.MapEnum<TipoDocumento>("tipo_documento");
                    npgsql.MapEnum<ModoLista>("modo_lista");
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    [Fact]
    public void ClientesTieneElIndiceUnicoDeNumero()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Cliente))!;

        var indiceNumero = entidad.GetIndexes()
            .Single(i => i.GetDatabaseName() == "ux_clientes_numero");
        Assert.True(indiceNumero.IsUnique);
        Assert.Equal("deleted_at IS NULL", indiceNumero.GetFilter());
        Assert.Equal(
            [nameof(Cliente.IdTenant), nameof(Cliente.Numero)],
            indiceNumero.Properties.Select(p => p.Name));

        // La check constraint ck_clientes_cf_protegido vive en el modelo de design-time
        // (GetCheckConstraints() no está disponible sobre el modelo "read-optimized" de
        // runtime que expone db.Model) — cubierta en runtime por
        // ClientesYProveedoresRlsTests (gated, pendiente de la migración) en vez de acá.
    }

    [Fact]
    public void ClientesTieneLasCuatroFksEsperadas()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Cliente))!;
        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();

        Assert.Contains("fk_clientes_tenant", nombresDeFk);
        Assert.Contains("fk_clientes_empresa", nombresDeFk);
        Assert.Contains("fk_clientes_condicion_fiscal", nombresDeFk);
        Assert.Contains("fk_clientes_lista_precio", nombresDeFk);
    }

    [Fact]
    public void ClientesNumeroDocumentoNoTieneNingunIndiceUnico()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Cliente))!;
        var indicesConNumeroDocumento = entidad.GetIndexes()
            .Where(i => i.Properties.Any(p => p.Name == nameof(Cliente.NumeroDocumento)));

        Assert.Empty(indicesConNumeroDocumento);
    }

    [Fact]
    public void ProveedoresTieneElIndiceUnicoDeCuitSinIdEmpresaEnLaClave()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Proveedor))!;

        var indiceCuit = entidad.GetIndexes()
            .Single(i => i.GetDatabaseName() == "ux_proveedores_cuit");
        Assert.True(indiceCuit.IsUnique);
        Assert.Equal("deleted_at IS NULL AND cuit IS NOT NULL", indiceCuit.GetFilter());
        Assert.Equal(
            [nameof(Proveedor.IdTenant), nameof(Proveedor.Cuit)],
            indiceCuit.Properties.Select(p => p.Name));

        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();
        Assert.Contains("fk_proveedores_tenant", nombresDeFk);
        Assert.Contains("fk_proveedores_empresa", nombresDeFk);
        Assert.Contains("fk_proveedores_condicion_fiscal", nombresDeFk);
    }

    [Fact]
    public void ListasPrecioReusaElParDeIndicesDeNombreYAgregaElParDeDefault()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(ListaPrecio))!;
        var indices = entidad.GetIndexes().ToList();

        Assert.Contains(indices, i => i.GetDatabaseName() == "ux_listas_precio_nombre_compartido");
        Assert.Contains(indices, i => i.GetDatabaseName() == "ux_listas_precio_nombre_empresa");

        var defaultCompartido = indices.Single(i => i.GetDatabaseName() == "ux_listas_precio_default_compartido");
        Assert.True(defaultCompartido.IsUnique);
        Assert.Equal("id_empresa IS NULL AND deleted_at IS NULL AND es_default = true", defaultCompartido.GetFilter());

        var defaultEmpresa = indices.Single(i => i.GetDatabaseName() == "ux_listas_precio_default_empresa");
        Assert.True(defaultEmpresa.IsUnique);
        Assert.Equal(
            "id_empresa IS NOT NULL AND deleted_at IS NULL AND es_default = true", defaultEmpresa.GetFilter());
    }

    [Fact]
    public void NumeracionesClientesTieneIdTenantComoPkSinIdentity()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(NumeracionCliente))!;

        var pk = entidad.FindPrimaryKey();
        Assert.NotNull(pk);
        Assert.Equal([nameof(NumeracionCliente.IdTenant)], pk!.Properties.Select(p => p.Name));

        var idTenant = entidad.FindProperty(nameof(NumeracionCliente.IdTenant))!;
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, idTenant.ValueGenerated);

        var fk = entidad.GetForeignKeys().Single();
        Assert.Equal("fk_numeraciones_clientes_tenant", fk.GetConstraintName());
        Assert.Equal(typeof(Tenant), fk.PrincipalEntityType.ClrType);
    }
}
