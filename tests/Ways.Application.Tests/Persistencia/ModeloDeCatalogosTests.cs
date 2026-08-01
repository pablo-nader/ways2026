using Microsoft.EntityFrameworkCore;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Prueba la máquina genérica de catálogos (ADR-11: <c>ConfiguracionDeCatalogo&lt;T&gt;</c>)
/// construyendo el modelo real de <see cref="WaysDbContext"/> contra el proveedor de Npgsql
/// sin conectar a una base — alcanza para inspeccionar metadata y confirmar que los 5
/// catálogos de tenant, los 3 catálogos globales y <c>parametros</c> mapean sin errores,
/// sin tener que escribir el mismo test 5 veces.
/// </summary>
public class ModeloDeCatalogosTests
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
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    [Theory]
    [InlineData(typeof(Area), "areas")]
    [InlineData(typeof(Marca), "marcas")]
    [InlineData(typeof(Grupo), "grupos")]
    [InlineData(typeof(MedioPago), "medios_pago")]
    [InlineData(typeof(Categoria), "categorias")]
    public void CadaCatalogoDeTenantMapeaElParDeIndicesCompartidoEmpresa(Type tipo, string tabla)
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(tipo)!;
        var indices = entidad.GetIndexes().ToList();

        var compartido = indices.Single(i => i.GetDatabaseName() == $"ux_{tabla}_nombre_compartido");
        Assert.True(compartido.IsUnique);
        Assert.Equal("id_empresa IS NULL AND deleted_at IS NULL", compartido.GetFilter());

        var propioDeEmpresa = indices.Single(i => i.GetDatabaseName() == $"ux_{tabla}_nombre_empresa");
        Assert.True(propioDeEmpresa.IsUnique);
        Assert.Equal("id_empresa IS NOT NULL AND deleted_at IS NULL", propioDeEmpresa.GetFilter());
    }

    [Theory]
    [InlineData(typeof(Area))]
    [InlineData(typeof(Marca))]
    [InlineData(typeof(Grupo))]
    [InlineData(typeof(MedioPago))]
    [InlineData(typeof(Categoria))]
    public void CadaCatalogoDeTenantTieneLaFkCompuestaOpcionalAEmpresasSinForzarIdTenantNullable(Type tipo)
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(tipo)!;
        var idTenant = entidad.FindProperty("IdTenant")!;

        Assert.False(idTenant.IsNullable);
        Assert.False(idTenant.IsColumnNullable());

        var fk = entidad.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Empresa));
        Assert.Equal(["IdEmpresa", "IdTenant"], fk.Properties.Select(p => p.Name));
        Assert.False(fk.IsRequired); // IdEmpresa nullable ⇒ FK opcional (ADR-9/ADR-10)
    }

    [Fact]
    public void CategoriaTieneLaClaveAlternativaYLaFkCompuestaASiMisma()
    {
        using var db = CrearContexto();

        var categoria = db.Model.FindEntityType(typeof(Categoria))!;

        var claveAlternativa = categoria.GetKeys().Single(k => !k.IsPrimaryKey());
        Assert.Equal(
            [nameof(Categoria.Id), nameof(Categoria.IdTenant)],
            claveAlternativa.Properties.Select(p => p.Name));

        var fkPadre = categoria.GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(Categoria));
        Assert.Equal(
            [nameof(Categoria.IdCategoriaPadre), nameof(Categoria.IdTenant)],
            fkPadre.Properties.Select(p => p.Name));
        Assert.False(fkPadre.IsRequired);
    }

    [Theory]
    [InlineData(typeof(CondicionFiscal))]
    [InlineData(typeof(AlicuotaIva))]
    [InlineData(typeof(TipoComprobante))]
    public void LosCatalogosGlobalesNoTienenColumnaIdTenantNiFiltroDeTenant(Type tipo)
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(tipo)!;

        Assert.Null(entidad.FindProperty("IdTenant"));

        var claves = entidad.GetDeclaredQueryFilters().Select(f => f.Key).ToList();
        Assert.Contains("BajaLogica", claves);
        Assert.DoesNotContain("Tenant", claves);
    }

    [Fact]
    public void ParametrosMapeaLosDosIndicesUnicosParcialesDeAdr13()
    {
        using var db = CrearContexto();

        var entidad = db.Model.FindEntityType(typeof(Parametro))!;
        var indices = entidad.GetIndexes().ToList();

        var deLaEmpresa = indices.Single(i => i.GetDatabaseName() == "ux_parametros_empresa");
        Assert.True(deLaEmpresa.IsUnique);
        Assert.Equal("id_punto_venta IS NULL AND deleted_at IS NULL", deLaEmpresa.GetFilter());

        var delPuntoVenta = indices.Single(i => i.GetDatabaseName() == "ux_parametros_punto_venta");
        Assert.True(delPuntoVenta.IsUnique);
        Assert.Equal("id_punto_venta IS NOT NULL AND deleted_at IS NULL", delPuntoVenta.GetFilter());
    }
}
