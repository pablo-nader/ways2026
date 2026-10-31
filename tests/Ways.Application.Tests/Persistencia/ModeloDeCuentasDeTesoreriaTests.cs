using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Forma del modelo EF de las cuentas de tesorería: nombres de tabla y columna, tipos, claves,
/// índices y FKs de <see cref="CuentaTesoreria"/>, <see cref="MovimientoCuenta"/> y las columnas
/// nuevas de <see cref="MedioPago"/>. Corren sin contenedor sobre el modelo Npgsql real; las
/// CHECK y las políticas RLS se prueban contra Postgres en <c>CuentasTesoreriaEsquemaTests</c>.
/// </summary>
public class ModeloDeCuentasDeTesoreriaTests
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
                    npgsql.MapEnum<TipoMovimientoCuenta>("tipo_movimiento_cuenta");
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    private static string Columna(IEntityType entidad, string propiedad) =>
        entidad.FindProperty(propiedad)!.GetColumnName();

    private static string Tipo(IEntityType entidad, string propiedad) =>
        entidad.FindProperty(propiedad)!.GetColumnType();

    [Fact]
    public void CuentaTesoreriaMapeaTablaColumnasYTipos()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(CuentaTesoreria))!;

        Assert.Equal("cuentas_tesoreria", entidad.GetTableName());
        Assert.Equal("id_cuenta_tesoreria", Columna(entidad, nameof(CuentaTesoreria.Id)));
        Assert.Equal("id_tenant", Columna(entidad, nameof(CuentaTesoreria.IdTenant)));
        Assert.Equal("id_empresa", Columna(entidad, nameof(CuentaTesoreria.IdEmpresa)));
        Assert.Equal("citext", Tipo(entidad, nameof(CuentaTesoreria.Nombre)));
        Assert.Equal("numeric(14,2)", Tipo(entidad, nameof(CuentaTesoreria.SaldoInicial)));
        Assert.Equal(0m, entidad.FindProperty(nameof(CuentaTesoreria.SaldoInicial))!.GetDefaultValue());
        Assert.Equal("deleted_at", Columna(entidad, nameof(CuentaTesoreria.DeletedAt)));
        Assert.False(entidad.FindProperty(nameof(CuentaTesoreria.IdEmpresa))!.IsNullable);
    }

    [Fact]
    public void CuentaTesoreriaTieneLaClaveAlternaConElTenantYLaFkCompuestaObligatoriaAEmpresas()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(CuentaTesoreria))!;

        var claves = entidad.GetKeys().Where(k => !k.IsPrimaryKey()).ToList();
        Assert.Equal(2, claves.Count);

        var clave = claves.Single(k => k.Properties.Count == 2);
        Assert.Equal(["Id", "IdTenant"], clave.Properties.Select(p => p.Name));
        Assert.Equal("ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant", clave.GetName());

        // Destino de la FK compuesta de conexiones_mercadopago: la base rechaza una cuenta de otra empresa.
        var conEmpresa = claves.Single(k => k.Properties.Count == 3);
        Assert.Equal(["Id", "IdEmpresa", "IdTenant"], conEmpresa.Properties.Select(p => p.Name));
        Assert.Equal("ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant", conEmpresa.GetName());

        var fk = entidad.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Empresa));
        Assert.Equal(["IdEmpresa", "IdTenant"], fk.Properties.Select(p => p.Name));
        Assert.True(fk.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal("fk_cuentas_tesoreria_empresa", fk.GetConstraintName());
    }

    [Fact]
    public void CuentaTesoreriaTieneElNombreUnicoPorEmpresaEntreLasVivas()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(CuentaTesoreria))!;

        var unico = entidad.GetIndexes().Single(i => i.GetDatabaseName() == "ux_cuentas_tesoreria_nombre_empresa");

        Assert.True(unico.IsUnique);
        Assert.Equal(["IdTenant", "IdEmpresa", "Nombre"], unico.Properties.Select(p => p.Name));
        Assert.Equal("deleted_at IS NULL", unico.GetFilter());
    }

    [Fact]
    public void MovimientoCuentaMapeaTablaColumnasYTipos()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(MovimientoCuenta))!;

        Assert.Equal("movimientos_cuenta", entidad.GetTableName());
        Assert.Equal("id_movimiento_cuenta", Columna(entidad, nameof(MovimientoCuenta.Id)));
        Assert.Equal("id_cuenta_tesoreria", Columna(entidad, nameof(MovimientoCuenta.IdCuentaTesoreria)));
        Assert.Equal("tipo_movimiento_cuenta", Tipo(entidad, nameof(MovimientoCuenta.Tipo)));

        foreach (var importe in new[]
                 {
                     nameof(MovimientoCuenta.Inicio), nameof(MovimientoCuenta.Ingreso),
                     nameof(MovimientoCuenta.Egreso), nameof(MovimientoCuenta.Final),
                     nameof(MovimientoCuenta.BaseBruta)
                 })
        {
            Assert.Equal("numeric(14,2)", Tipo(entidad, importe));
        }

        Assert.Equal("numeric(6,3)", Tipo(entidad, nameof(MovimientoCuenta.PorcentajeAplicado)));
        Assert.True(entidad.FindProperty(nameof(MovimientoCuenta.BaseBruta))!.IsNullable);
        Assert.True(entidad.FindProperty(nameof(MovimientoCuenta.PorcentajeAplicado))!.IsNullable);
        Assert.True(entidad.FindProperty(nameof(MovimientoCuenta.IdTurnoCaja))!.IsNullable);
        Assert.True(entidad.FindProperty(nameof(MovimientoCuenta.IdMedioPago))!.IsNullable);
        Assert.False(entidad.FindProperty(nameof(MovimientoCuenta.IdEmpleado))!.IsNullable);
    }

    [Fact]
    public void MovimientoCuentaDeclaraLasTresCheck()
    {
        using var db = CrearContexto();
        var entidad = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(MovimientoCuenta))!;

        var checks = entidad.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        Assert.Equal("final = inicio + ingreso - egreso", checks["ck_movimientos_cuenta_cadena"]);
        Assert.Equal("ingreso >= 0 AND egreso >= 0", checks["ck_movimientos_cuenta_importes_no_negativos"]);
        Assert.Equal("ingreso = 0 OR egreso = 0", checks["ck_movimientos_cuenta_un_solo_lado"]);
        Assert.Equal(3, checks.Count);
    }

    [Fact]
    public void MovimientoCuentaTieneLosIndicesDeLecturaYElUnicoParcialDelCierre()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(MovimientoCuenta))!;
        var indices = entidad.GetIndexes().ToDictionary(i => i.GetDatabaseName()!);

        Assert.Equal(
            ["IdCuentaTesoreria", "IdTenant", "Id"], indices["ix_movimientos_cuenta_cuenta_id"].Properties.Select(p => p.Name));
        Assert.False(indices["ix_movimientos_cuenta_cuenta_id"].IsUnique);

        var cierre = indices["ux_movimientos_cuenta_cierre"];
        Assert.True(cierre.IsUnique);
        Assert.Equal(["IdTenant", "IdTurnoCaja", "IdMedioPago", "Tipo"], cierre.Properties.Select(p => p.Name));
        Assert.Equal("id_turno_caja IS NOT NULL AND tipo IN ('cobro', 'comision', 'retencion_iibb', 'retencion_iva', 'retencion_ganancias')", cierre.GetFilter());
    }

    [Theory]
    [InlineData(typeof(CuentaTesoreria), "fk_movimientos_cuenta_cuenta_tesoreria", new[] { "IdCuentaTesoreria", "IdTenant" }, true)]
    [InlineData(typeof(TurnoCaja), "fk_movimientos_cuenta_turno", new[] { "IdTurnoCaja", "IdTenant" }, false)]
    [InlineData(typeof(MedioPago), "fk_movimientos_cuenta_medio_pago", new[] { "IdMedioPago", "IdTenant" }, false)]
    public void MovimientoCuentaReferenciaConFkCompuestaConElTenantYRestrict(
        Type principal, string nombre, string[] columnas, bool obligatoria)
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(MovimientoCuenta))!;

        var fk = entidad.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == principal);

        Assert.Equal(nombre, fk.GetConstraintName());
        Assert.Equal(columnas, fk.Properties.Select(p => p.Name));
        Assert.Equal(obligatoria, fk.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void MovimientoCuentaTieneElFiltroDeTenantEscritoAMano()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(MovimientoCuenta))!;

        Assert.NotNull(entidad.GetDeclaredQueryFilters().SingleOrDefault(f => f.Key == "Tenant"));
    }

    [Fact]
    public void LasColumnasNuevasDeMediosPagoTienenTipoDefaultYFkCompuesta()
    {
        using var db = CrearContexto();
        var entidad = db.Model.FindEntityType(typeof(MedioPago))!;

        Assert.Equal("id_cuenta_tesoreria", Columna(entidad, nameof(MedioPago.IdCuentaTesoreria)));
        Assert.True(entidad.FindProperty(nameof(MedioPago.IdCuentaTesoreria))!.IsNullable);

        var porcentajes = new Dictionary<string, string>
        {
            [nameof(MedioPago.ComisionPorcentaje)] = "comision_porcentaje",
            [nameof(MedioPago.RetencionIibbPorcentaje)] = "retencion_iibb_porcentaje",
            [nameof(MedioPago.RetencionIvaPorcentaje)] = "retencion_iva_porcentaje",
            [nameof(MedioPago.RetencionGananciasPorcentaje)] = "retencion_ganancias_porcentaje"
        };
        foreach (var (propiedad, columna) in porcentajes)
        {
            var p = entidad.FindProperty(propiedad)!;
            Assert.Equal(columna, p.GetColumnName());
            Assert.Equal("numeric(6,3)", p.GetColumnType());
            Assert.False(p.IsNullable);
            Assert.Equal(0m, p.GetDefaultValue());
        }

        var fk = entidad.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(CuentaTesoreria));
        Assert.Equal("fk_medios_pago_cuenta_tesoreria", fk.GetConstraintName());
        Assert.Equal(["IdCuentaTesoreria", "IdTenant"], fk.Properties.Select(p => p.Name));
        Assert.False(fk.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void MediosPagoDeclaraLasTresCheckDeCuentaYLaDeFacturacion()
    {
        using var db = CrearContexto();
        var entidad = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(MedioPago))!;

        var checks = entidad.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        Assert.Contains("ck_medios_pago_porcentaje_facturacion", checks.Keys);
        Assert.Contains("ck_medios_pago_porcentajes_cuenta_rango", checks.Keys);
        Assert.Contains("ck_medios_pago_porcentajes_cuenta_suma", checks.Keys);
        Assert.Contains("comportamiento = 'electronico'", checks["ck_medios_pago_cuenta_solo_electronico"]);
    }

    [Fact]
    public void ElEnumDeTipoMovimientoCuentaDeclaraLosNueveLabelsEnSnakeCase()
    {
        using var db = CrearContexto();

        var enums = db.GetService<IDesignTimeModel>().Model.GetPostgresEnums().Single(e => e.Name == "tipo_movimiento_cuenta");

        Assert.Equal(
            ["ajuste", "cobro", "comision", "deposito", "retencion_ganancias", "retencion_iibb", "retencion_iva", "retiro", "transferencia"],
            enums.Labels.OrderBy(l => l, StringComparer.Ordinal));
        Assert.Equal(9, Enum.GetValues<TipoMovimientoCuenta>().Length);
    }
}
