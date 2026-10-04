using System.Reflection;
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

    /// <summary>Cambia UNA sola propiedad compartida de <see cref="Articulo"/> (clave: nombre de la
    /// propiedad) a un valor distinto del de <see cref="CrearArticuloBase"/>.</summary>
    private static readonly IReadOnlyDictionary<string, Action<Articulo>> CambiosCompartidos =
        new Dictionary<string, Action<Articulo>>
        {
            [nameof(Articulo.IdArea)] = a => a.IdArea = 91,
            [nameof(Articulo.IdCategoria)] = a => a.IdCategoria = 92,
            [nameof(Articulo.IdGrupo)] = a => a.IdGrupo = 94,
            [nameof(Articulo.IdProveedorHabitual)] = a => a.IdProveedorHabitual = 95,
            [nameof(Articulo.IdAlicuotaIva)] = a => a.IdAlicuotaIva = 96,
            [nameof(Articulo.UnidadVenta)] = a => a.UnidadVenta = UnidadVenta.Peso,
            [nameof(Articulo.UnidadesPorBulto)] = a => a.UnidadesPorBulto = 24m,
            [nameof(Articulo.EsProducto)] = a => a.EsProducto = false,
            [nameof(Articulo.ControlaLote)] = a => a.ControlaLote = true,
            [nameof(Articulo.AcumulaEnVenta)] = a => a.AcumulaEnVenta = false,
            [nameof(Articulo.CostoLista)] = a => a.CostoLista = 150m,
            [nameof(Articulo.DescuentoProveedor)] = a => a.DescuentoProveedor = 15m,
            [nameof(Articulo.CostoNominal)] = a => a.CostoNominal = 130m
        };

    public static TheoryData<string> PropiedadesCompartidas()
    {
        var datos = new TheoryData<string>();

        foreach (var propiedad in CambiosCompartidos.Keys)
        {
            datos.Add(propiedad);
        }

        return datos;
    }

    private static Articulo CrearArticuloBase() => new()
    {
        CodigoInterno = "ART-1",
        Nombre = "Gaseosa cola 500 cc",
        IdArea = 1,
        IdCategoria = 2,
        IdGrupo = 4,
        IdProveedorHabitual = 5,
        IdAlicuotaIva = 6,
        UnidadVenta = UnidadVenta.Unidad,
        UnidadesPorBulto = 12m,
        EsProducto = true,
        ControlaLote = false,
        AcumulaEnVenta = true,
        CostoLista = 100m,
        DescuentoProveedor = 10m,
        CostoNominal = 90m
    };

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
    public void ArticulosTieneLasOchoFksEsperadas()
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
        Assert.Contains("fk_articulos_familia", nombresDeFk);
        Assert.Equal(8, nombresDeFk.Count);
    }

    [Fact]
    public void ArticulosTieneLaFkCompuestaOpcionalAFamiliasYSuIndiceParcial()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Articulo))!;

        var idFamilia = entidad.FindProperty(nameof(Articulo.IdFamilia))!;
        Assert.True(idFamilia.IsNullable);
        Assert.Equal("id_familia", idFamilia.GetColumnName());

        var fk = entidad.GetForeignKeys().Single(f => f.GetConstraintName() == "fk_articulos_familia");
        Assert.Equal(typeof(Familia), fk.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(Articulo.IdFamilia), nameof(Articulo.IdTenant)], fk.Properties.Select(p => p.Name));
        Assert.Equal([nameof(Familia.Id), nameof(Familia.IdTenant)], fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.False(fk.IsRequired);

        var indice = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ix_articulos_familia");
        Assert.False(indice.IsUnique);
        Assert.Equal("id_familia IS NOT NULL", indice.GetFilter());
        Assert.Equal(
            [nameof(Articulo.IdFamilia), nameof(Articulo.IdTenant)],
            indice.Properties.Select(p => p.Name));
    }

    /// <summary>El nombre con que <see cref="ValoresCompartidosDeFamilia.CamposDistintos"/> informa un
    /// campo es la columna que el modelo de EF mapea para esa propiedad de <see cref="Articulo"/>. Un
    /// nombre mal escrito en la regla y repetido en <c>ValoresCompartidosDeFamiliaTests</c> —que compara
    /// contra su propia lista— pasaría allá y falla acá.</summary>
    [Theory]
    [MemberData(nameof(PropiedadesCompartidas))]
    public void ElNombreDeColumnaQueInformaLaReglaEsElQueElModeloMapeaParaEsaPropiedad(string propiedad)
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(Articulo))!;

        var otro = CrearArticuloBase();
        CambiosCompartidos[propiedad](otro);

        var distintos = ValoresCompartidosDeFamilia.De(CrearArticuloBase())
            .CamposDistintos(ValoresCompartidosDeFamilia.De(otro));

        Assert.Equal([entidad.FindProperty(propiedad)!.GetColumnName()!], distintos);
    }

    /// <summary>Completitud de la prueba anterior: tiene un caso por cada propiedad del registro de la
    /// regla.</summary>
    [Fact]
    public void LaPruebaDeNombresDeColumnaCubreExactamenteLasPropiedadesDeLaRegla()
    {
        var delRegistro = typeof(ValoresCompartidosDeFamilia)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        Assert.Equal(delRegistro.Order(), CambiosCompartidos.Keys.Order());
    }

    [Fact]
    public void FamiliasTieneLaClaveAlternaElIndiceUnicoParcialDeNombreYLaFkAlTenant()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Familia))!;
        Assert.Equal("familias", entidad.GetTableName());

        var pk = entidad.FindPrimaryKey()!;
        Assert.Equal("pk_familias", pk.GetName());
        Assert.Equal([nameof(Familia.Id)], pk.Properties.Select(p => p.Name));
        Assert.Equal("id_familia", entidad.FindProperty(nameof(Familia.Id))!.GetColumnName());

        var claveAlterna = entidad.GetKeys().Single(k => k.GetName() == "ak_familias_id_familia_id_tenant");
        Assert.Equal(
            [nameof(Familia.Id), nameof(Familia.IdTenant)],
            claveAlterna.Properties.Select(p => p.Name));

        var nombre = entidad.FindProperty(nameof(Familia.Nombre))!;
        Assert.Equal("citext", nombre.GetColumnType());
        Assert.Equal(150, nombre.GetMaxLength());
        Assert.False(nombre.IsNullable);

        var indice = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ux_familias_nombre");
        Assert.True(indice.IsUnique);
        Assert.Equal("deleted_at IS NULL", indice.GetFilter());
        Assert.Equal(
            [nameof(Familia.IdTenant), nameof(Familia.Nombre)],
            indice.Properties.Select(p => p.Name));

        var fk = Assert.Single(entidad.GetForeignKeys());
        Assert.Equal("fk_familias_tenant", fk.GetConstraintName());
        Assert.Equal(typeof(Tenant), fk.PrincipalEntityType.ClrType);
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
