using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Verificación de apply-time exigida por ADR-9 (design.md, stage-1-organization-and-catalogs):
/// confirmar que EF Core 10.0.10 no fuerza <c>IdTenant</c> nullable para poder expresar
/// una FK compuesta opcional. Construye el modelo real de <see cref="WaysDbContext"/> contra
/// el proveedor de Npgsql sin conectar a una base — alcanza para inspeccionar metadata.
/// </summary>
public class ModeloDeOrganizacionTests
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
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    [Fact]
    public void LaFkCompuestaOpcionalNoFuerzaIdTenantNullable()
    {
        using var db = CrearContexto();

        var puntoVenta = db.Model.FindEntityType(typeof(PuntoVenta))!;

        var idTenant = puntoVenta.FindProperty(nameof(PuntoVenta.IdTenant))!;
        var idEmpresa = puntoVenta.FindProperty(nameof(PuntoVenta.IdEmpresa))!;

        // puntos_venta.id_empresa es obligatorio en doc 09 (a diferencia de la FK
        // catálogo→empresa), así que acá la FK entera es requerida — igual sirve para
        // confirmar que id_tenant nunca sale nullable de la configuración compuesta.
        Assert.False(idTenant.IsNullable);
        Assert.False(idTenant.IsColumnNullable());
        Assert.False(idEmpresa.IsNullable);
        Assert.False(idEmpresa.IsColumnNullable());

        var fk = puntoVenta.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Empresa));
        Assert.Equal(
            [nameof(PuntoVenta.IdEmpresa), nameof(PuntoVenta.IdTenant)],
            fk.Properties.Select(p => p.Name));
    }

    [Fact]
    public void EmpresaTieneLaClaveAlternativaCompuestaQueHabilitaLasFksDeSusHijos()
    {
        using var db = CrearContexto();

        var empresa = db.Model.FindEntityType(typeof(Empresa))!;

        var claveAlternativa = empresa.GetKeys()
            .Single(k => !k.IsPrimaryKey());

        Assert.Equal(
            [nameof(Empresa.Id), nameof(Empresa.IdTenant)],
            claveAlternativa.Properties.Select(p => p.Name));
    }
}
