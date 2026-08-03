using Microsoft.EntityFrameworkCore;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Stage-3-articulos-y-precios, Slice 1 (tarea 1F, "tests que no necesitan la migración"):
/// construye el modelo real de <see cref="WaysDbContext"/> contra el proveedor de Npgsql sin
/// conectar a una base (mismo patrón que <c>ModeloDeClientesYProveedoresTests</c>) — alcanza
/// para confirmar índices/FKs/claves alternas antes de que exista la migración (DB CHANGE GATE
/// pendiente).
/// </summary>
public class ModeloDeArticulosYPreciosTests
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
                    npgsql.MapEnum<UnidadVenta>("unidad_venta");
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    [Fact]
    public void ArticulosTieneElIndiceUnicoDeCodigoInternoYLaClaveAlterna()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Articulo))!;

        var indice = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ux_articulos_codigo_interno");
        Assert.True(indice.IsUnique);
        Assert.Equal("deleted_at IS NULL", indice.GetFilter());
        Assert.Equal(
            [nameof(Articulo.IdTenant), nameof(Articulo.CodigoInterno)],
            indice.Properties.Select(p => p.Name));

        var claveAlterna = entidad.GetKeys().Single(k => k.GetName() == "ak_articulos_id_articulo_id_tenant");
        Assert.Equal(
            [nameof(Articulo.Id), nameof(Articulo.IdTenant)],
            claveAlterna.Properties.Select(p => p.Name));
    }

    [Fact]
    public void ArticulosTieneLasSieteFksEsperadas()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Articulo))!;
        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();

        Assert.Contains("fk_articulos_tenant", nombresDeFk);
        Assert.Contains("fk_articulos_area", nombresDeFk);
        Assert.Contains("fk_articulos_categoria", nombresDeFk);
        Assert.Contains("fk_articulos_marca", nombresDeFk);
        Assert.Contains("fk_articulos_grupo", nombresDeFk);
        Assert.Contains("fk_articulos_proveedor_habitual", nombresDeFk);
        Assert.Contains("fk_articulos_alicuota_iva", nombresDeFk);
        Assert.Equal(7, nombresDeFk.Count);
    }

    [Fact]
    public void ArticulosEmpresasTieneLaPkCompuestaYLasTresFks()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(ArticuloEmpresa))!;

        var pk = entidad.FindPrimaryKey();
        Assert.NotNull(pk);
        Assert.Equal(
            [nameof(ArticuloEmpresa.IdArticulo), nameof(ArticuloEmpresa.IdEmpresa)],
            pk!.Properties.Select(p => p.Name));

        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();
        Assert.Contains("fk_articulos_empresas_tenant", nombresDeFk);
        Assert.Contains("fk_articulos_empresas_articulo", nombresDeFk);
        Assert.Contains("fk_articulos_empresas_empresa", nombresDeFk);
    }

    [Fact]
    public void CodigosBarraTieneElIndiceUnicoPorTenant()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(CodigoBarra))!;

        var indice = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ux_codigos_barra_codigo_tenant");
        Assert.True(indice.IsUnique);
        Assert.Equal("deleted_at IS NULL", indice.GetFilter());
        Assert.Equal(
            [nameof(CodigoBarra.Codigo), nameof(CodigoBarra.IdTenant)],
            indice.Properties.Select(p => p.Name));

        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();
        Assert.Contains("fk_codigos_barra_tenant", nombresDeFk);
        Assert.Contains("fk_codigos_barra_articulo", nombresDeFk);
    }

    [Fact]
    public void PreciosTieneElIndiceUnicoDeVigenteYLasTresFks()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Precio))!;

        var indice = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ux_precios_vigente");
        Assert.True(indice.IsUnique);
        Assert.Equal("vigente_hasta IS NULL AND deleted_at IS NULL", indice.GetFilter());
        Assert.Equal(
            [nameof(Precio.IdArticulo), nameof(Precio.IdListaPrecio)],
            indice.Properties.Select(p => p.Name));

        var nombresDeFk = entidad.GetForeignKeys().Select(f => f.GetConstraintName()).ToList();
        Assert.Contains("fk_precios_tenant", nombresDeFk);
        Assert.Contains("fk_precios_articulo", nombresDeFk);
        Assert.Contains("fk_precios_lista_precio", nombresDeFk);
    }

    [Fact]
    public void NumeracionesArticulosTieneIdTenantComoPkSinIdentity()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(NumeracionArticulo))!;

        var pk = entidad.FindPrimaryKey();
        Assert.NotNull(pk);
        Assert.Equal([nameof(NumeracionArticulo.IdTenant)], pk!.Properties.Select(p => p.Name));

        var idTenant = entidad.FindProperty(nameof(NumeracionArticulo.IdTenant))!;
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, idTenant.ValueGenerated);

        var fk = entidad.GetForeignKeys().Single();
        Assert.Equal("fk_numeraciones_articulos_tenant", fk.GetConstraintName());
        Assert.Equal(typeof(Tenant), fk.PrincipalEntityType.ClrType);
    }

    [Theory]
    [InlineData(typeof(Area), "ak_areas_id_area_id_tenant")]
    [InlineData(typeof(Marca), "ak_marcas_id_marca_id_tenant")]
    [InlineData(typeof(Grupo), "ak_grupos_id_grupo_id_tenant")]
    [InlineData(typeof(Proveedor), "ak_proveedores_id_proveedor_id_tenant")]
    public void LasCuatroTablasExistentesGananLaClaveAlterna(Type tipoDeEntidad, string nombreDeClave)
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(tipoDeEntidad)!;
        var claveAlterna = entidad.GetKeys().SingleOrDefault(k => k.GetName() == nombreDeClave);

        Assert.NotNull(claveAlterna);
        Assert.Equal(2, claveAlterna!.Properties.Count);
    }
}
