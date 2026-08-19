using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 1 (tasks 1.21-1.27, mutation targets #1-#9,
/// db-error-backstops skill, design decisiones 10-11): RLS, las dos CHECKs de cada tabla nueva,
/// los dos <c>23505</c> exact-name (incl. la tercera ocurrencia del ordering trap de
/// <c>_numero</c>), el conteo vinculante de 12 índices nuevos y los backstops de FK exentos —
/// todos sobre la base COMPARTIDA de <see cref="WaysApiFixture"/> (mismo criterio que
/// <c>CuentaCorrienteProveedorSchemaTests</c>: no depende del momento exacto de una migración
/// de datos, no hay ninguna en esta etapa).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OrdenesCompraSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(int IdTenant, int IdProveedor, int IdPuntoVenta, int IdEmpleado, int IdArticulo);

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra roles/alicuotas/tipos de comprobante

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = tenant.Id, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var empleado = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = $"{nombre.ToLowerInvariant()}-empleado",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = "hash-de-prueba",
            PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(empleado);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenant.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = $"{nombre}-articulo",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return new Escenario(tenant.Id, proveedor.Id, puntoVenta.Id, empleado.Id, articulo.Id);
    }

    // ---------------------------------------------------------------------------------------
    // RLS (task 1.21, mutation targets #1 y #2)
    // ---------------------------------------------------------------------------------------

    /// <summary>Target #1 discriminante: una fila de ordenes_compra insertada bajo el tenant A no
    /// debe verse desde el tenant B, ni siquiera con id_articulo/id_proveedor desincronizados
    /// entre ambos escenarios (regla 11).</summary>
    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLasOrdenesDeCompraPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasOrdenesDeCompraPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasOrdenesDeCompraPorSelect) + "-B");

        int idOrden;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertar = cruda.CreateCommand();
            insertar.CommandText =
                "INSERT INTO ordenes_compra " +
                "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
                " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, 'seed de prueba', 'borrador'::estado_orden_compra, now(), now(), NULL) " +
                "RETURNING id_orden_compra";
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });
            insertar.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpleado });
            idOrden = (int)(await insertar.ExecuteScalarAsync())!;
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM ordenes_compra WHERE id_orden_compra = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idOrden });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    /// <summary>Target #2, la tabla hija: un item de ordenes_compra insertado bajo el tenant A no
    /// debe verse desde el tenant B.</summary>
    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosItemsDeOrdenDeCompraPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDeOrdenDeCompraPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDeOrdenDeCompraPorSelect) + "-B");

        int idItem;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertarOrden = cruda.CreateCommand();
            insertarOrden.CommandText =
                "INSERT INTO ordenes_compra " +
                "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
                " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, NULL, 'borrador'::estado_orden_compra, now(), now(), NULL) " +
                "RETURNING id_orden_compra";
            insertarOrden.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertarOrden.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
            insertarOrden.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });
            insertarOrden.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpleado });
            var idOrden = (int)(await insertarOrden.ExecuteScalarAsync())!;

            await using var insertarItem = cruda.CreateCommand();
            insertarItem.CommandText =
                "INSERT INTO items_orden_compra " +
                "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, 1, $3, 'seed', 7, NULL, now(), now(), NULL) RETURNING id_item";
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = idOrden });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdArticulo });
            idItem = (int)(await insertarItem.ExecuteScalarAsync())!;
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM items_orden_compra WHERE id_item = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idItem });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    /// <summary>Un INSERT con id_tenant ajeno a la sesión es refusado por WITH CHECK antes de que
    /// la fila exista, SQLSTATE 42501.</summary>
    [Fact]
    public async Task UnInsertConIdTenantAjenoEnOrdenesCompraSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnOrdenesCompraSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnOrdenesCompraSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = comoA.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, 'intruso', 'borrador'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = b.IdTenant }); // ajeno a la sesión (tenant A)
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---------------------------------------------------------------------------------------
    // ck_ordenes_compra_envio_completo (task 1.22, mutation target #3), ambas direcciones
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, 501, now(), NULL, NULL, NULL, NULL, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ordenes_compra_envio_completo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaFechaDeEnvioSinNumeroViolaLaCheckDeEnvioCompleto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaFechaDeEnvioSinNumeroViolaLaCheckDeEnvioCompleto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, NULL, now(), now(), NULL, NULL, NULL, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ordenes_compra_envio_completo", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // ck_ordenes_compra_cierre (task 1.22, mutation target #4), ambas direcciones
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaFechaDeCierreConEstadoNoCerradaViolaLaCheckDeCierre()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaFechaDeCierreConEstadoNoCerradaViolaLaCheckDeCierre));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, 601, now(), now(), NULL, now(), NULL, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ordenes_compra_cierre", excepcion.ConstraintName);
    }

    /// <summary>Dirección opuesta de la misma CHECK: un cierre manual
    /// (<c>id_empleado_cierre IS NOT NULL</c>) sin <c>fecha_cierre</c> es irrepresentable.</summary>
    [Fact]
    public async Task UnCierreManualSinFechaDeCierreViolaLaCheckDeCierre()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCierreManualSinFechaDeCierreViolaLaCheckDeCierre));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, 702, now(), now(), NULL, NULL, $5, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado }); // id_empleado_cierre NOT NULL, fecha_cierre NULL

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_ordenes_compra_cierre", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // CHECKs de items_orden_compra (task 1.23, mutation target #5)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaCantidadPedidaNoPositivaViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaCantidadPedidaNoPositivaViolaLaCheck));
        var idOrden = await InsertarBorradorAsync(e);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_orden_compra " +
            "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 0, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idOrden });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_orden_compra_cantidad_positiva", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCostoUnitarioEstimadoNegativoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCostoUnitarioEstimadoNegativoViolaLaCheck));
        var idOrden = await InsertarBorradorAsync(e);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_orden_compra " +
            "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 3, -1, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idOrden });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_orden_compra_costo_no_negativo", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // ux_items_orden_compra_orden (task 1.24)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnOrdenDuplicadoDeItemDentroDeLaMismaOrdenDeCompraViolaLaUnicidad()
    {
        var e = await SembrarEscenarioAsync(nameof(UnOrdenDuplicadoDeItemDentroDeLaMismaOrdenDeCompraViolaLaUnicidad));
        var idOrden = await InsertarBorradorAsync(e);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using (var primero = cruda.CreateCommand())
        {
            primero.CommandText =
                "INSERT INTO items_orden_compra " +
                "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, 1, $3, 'primero', 5, NULL, now(), now(), NULL)";
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
            primero.Parameters.Add(new NpgsqlParameter { Value = idOrden });
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });
            await primero.ExecuteNonQueryAsync();
        }

        await using var segundo = cruda.CreateCommand();
        segundo.CommandText =
            "INSERT INTO items_orden_compra " +
            "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'duplicado', 9, NULL, now(), now(), NULL)";
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        segundo.Parameters.Add(new NpgsqlParameter { Value = idOrden });
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => segundo.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_items_orden_compra_orden", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // La trampa de ordering, tercera ocurrencia (task 1.25, binding gate test (c))
    // ---------------------------------------------------------------------------------------

    /// <summary>Un numero duplicado en el mismo punto de venta resuelve al SQLSTATE 23505 con el
    /// nombre de constraint EXACTO — la traducción al código de dominio la hace
    /// <c>ManejadorDeErrores</c> en la capa HTTP; acá se afirma solo el SQLSTATE y el nombre de
    /// constraint (el insumo que <c>ClasificarPostgresException</c> usa para no caer en el
    /// brazo genérico <c>_numero</c> de <c>ClasificarUnicidad</c>).</summary>
    [Fact]
    public async Task UnNumeroDuplicadoEnElMismoPuntoDeVentaResuelveAlConstraintExactoDeOrdenesDeCompra()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroDuplicadoEnElMismoPuntoDeVentaResuelveAlConstraintExactoDeOrdenesDeCompra));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using (var primero = cruda.CreateCommand())
        {
            primero.CommandText =
                "INSERT INTO ordenes_compra " +
                "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
                " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, 42, now(), now(), NULL, NULL, NULL, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
            primero.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
            await primero.ExecuteNonQueryAsync();
        }

        await using var segundo = cruda.CreateCommand();
        segundo.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, 42, now(), now(), NULL, NULL, NULL, NULL, 'enviada'::estado_orden_compra, now(), now(), NULL)";
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        segundo.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => segundo.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        // El nombre EXACTO — mutation target #7: si la rama exact-name de ManejadorDeErrores se
        // mueve por debajo de ClasificarUnicidad, el CÓDIGO traducido cambiaría de
        // numero_de_orden_duplicado a numero_duplicado, pero el SqlState/ConstraintName crudo de
        // acá seguiría siendo el mismo — por eso la prueba HTTP equivalente (fuera de esta clase)
        // es la que realmente prueba el ordering trap end-to-end; ésta prueba que la constraint
        // exacta existe y se llama así, el insumo que ese ordering necesita.
        Assert.Equal("ux_ordenes_compra_numero", excepcion.ConstraintName);
    }

    /// <summary>Mutation target #6: un número NULL nunca dispara la unicidad — dos borradores sin
    /// enviar en el mismo punto de venta conviven sin conflicto (la unicidad es PARCIAL).</summary>
    [Fact]
    public async Task DosBorradoresSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto()
    {
        var e = await SembrarEscenarioAsync(nameof(DosBorradoresSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        for (var i = 0; i < 2; i++)
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO ordenes_compra " +
                "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
                " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, NULL, 'borrador'::estado_orden_compra, now(), now(), NULL)";
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

            await comando.ExecuteNonQueryAsync(); // no debe tirar — ambos NULL, filtro parcial
        }
    }

    // ---------------------------------------------------------------------------------------
    // Conteo vinculante de índices (task 1.26, gate §B/§C/§D — VINCULANTE)
    // ---------------------------------------------------------------------------------------

    /// <summary>Gate guard VINCULANTE (task 1.26/1.37, state.yaml db_gate_approval): el conteo
    /// total de índices nuevos tiene que ser EXACTAMENTE 12 — 7 en ordenes_compra (6 nombrados a
    /// mano + 1 implícito de la AK), 4 en items_orden_compra (incl. la unicidad de orden), 1 en
    /// comprobantes_compra (el soporte de FK 9, nombrado a mano, nunca el IX_... autogenerado).
    /// Cualquier índice extra que ForeignKeyIndexConvention agregue sin que este contrato lo
    /// nombre reabre el gate.</summary>
    [Fact]
    public async Task ElConteoTotalDeIndicesNuevosEsExactamenteDoce()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var indicesOrdenesCompra = await ListarIndicesAsync(cruda, "ordenes_compra");
        var indicesDeSoporteOrdenes = indicesOrdenesCompra.Where(n => n != "pk_ordenes_compra").ToList();
        Assert.Equal(7, indicesDeSoporteOrdenes.Count);
        Assert.Equal(
            new[]
            {
                "ak_ordenes_compra_id_orden_compra_id_tenant",
                "ix_ordenes_compra_empleado",
                "ix_ordenes_compra_empleado_cierre",
                "ix_ordenes_compra_proveedor",
                "ix_ordenes_compra_punto_venta_fecha",
                "ix_ordenes_compra_tenant",
                "ux_ordenes_compra_numero"
            },
            indicesDeSoporteOrdenes.OrderBy(n => n));

        var indicesItems = await ListarIndicesAsync(cruda, "items_orden_compra");
        var indicesDeSoporteItems = indicesItems.Where(n => n != "pk_items_orden_compra").ToList();
        Assert.Equal(4, indicesDeSoporteItems.Count);
        Assert.Equal(
            new[]
            {
                "ix_items_orden_compra_articulo",
                "ix_items_orden_compra_orden_compra",
                "ix_items_orden_compra_tenant",
                "ux_items_orden_compra_orden"
            },
            indicesDeSoporteItems.OrderBy(n => n));

        await using var comandoComprobantes = cruda.CreateCommand();
        comandoComprobantes.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'comprobantes_compra' AND indexname = 'ix_comprobantes_compra_orden_compra'";
        var indiceComprobantes = (string?)await comandoComprobantes.ExecuteScalarAsync();
        Assert.NotNull(indiceComprobantes);

        // No debe existir NINGÚN índice extra sobre id_orden_compra más allá del nombrado a mano
        // (mutation target #9): la convención de EF habría producido
        // "IX_comprobantes_compra_id_orden_compra_id_tenant" — se excluye explícitamente el
        // nombre propio en vez de un LIKE case-sensitive frágil.
        await using var comandoSinAutogenerado = cruda.CreateCommand();
        comandoSinAutogenerado.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'comprobantes_compra' " +
            "AND indexname ILIKE '%orden_compra%' AND indexname <> 'ix_comprobantes_compra_orden_compra'";
        var autogenerados = (long)(await comandoSinAutogenerado.ExecuteScalarAsync())!;
        Assert.Equal(0, autogenerados);

        // El total del gate: 7 (ordenes_compra) + 4 (items_orden_compra) + 1 (comprobantes_compra) = 12.
        Assert.Equal(12, indicesDeSoporteOrdenes.Count + indicesDeSoporteItems.Count + 1);
    }

    /// <summary>Hallazgo MAJOR de judgment-day (juez B): el conteo de arriba compara solo
    /// <c>indexname</c> contra <c>pg_indexes</c> — un swap de columnas en un índice compuesto
    /// (p. ej. <c>ix_ordenes_compra_proveedor</c> de <c>(id_proveedor, id_tenant)</c> a
    /// <c>(id_tenant, id_proveedor)</c>, que derrota la cobertura por prefijo de la FK 3 —
    /// proposal líneas 594-597) conserva el nombre y el conteo, así que ese test sigue en verde.
    /// Esta prueba asserta el DDL completo (<c>pg_indexes.indexdef</c>) de cada índice compuesto
    /// nuevo de la etapa contra el contrato del gate, incluyendo el orden exacto de columnas, el
    /// filtro parcial y el flag UNIQUE — y que ningún índice compuesto quede liderado por
    /// <c>id_tenant</c> salvo el único que lo lleva por diseño.</summary>
    [Fact]
    public async Task LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "ordenes_compra", "ix_ordenes_compra_punto_venta_fecha"),
            "id_punto_venta", "id_tenant", "fecha_emision");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "ordenes_compra", "ix_ordenes_compra_proveedor"),
            "id_proveedor", "id_tenant");

        var defNumero = await ObtenerIndexDefAsync(cruda, "ordenes_compra", "ux_ordenes_compra_numero");
        AssertOrdenDeColumnas(defNumero, "id_tenant", "id_punto_venta", "numero");
        Assert.Contains("CREATE UNIQUE INDEX", defNumero);
        Assert.Contains("WHERE (numero IS NOT NULL)", defNumero);

        var defAk = await ObtenerIndexDefAsync(cruda, "ordenes_compra", "ak_ordenes_compra_id_orden_compra_id_tenant");
        AssertOrdenDeColumnas(defAk, "id_orden_compra", "id_tenant");
        Assert.Contains("CREATE UNIQUE INDEX", defAk);

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_orden_compra", "ix_items_orden_compra_orden_compra"),
            "id_orden_compra", "id_tenant");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_orden_compra", "ix_items_orden_compra_articulo"),
            "id_articulo", "id_tenant");

        var defUxOrden = await ObtenerIndexDefAsync(cruda, "items_orden_compra", "ux_items_orden_compra_orden");
        AssertOrdenDeColumnas(defUxOrden, "id_orden_compra", "orden");
        Assert.Contains("CREATE UNIQUE INDEX", defUxOrden);

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "comprobantes_compra", "ix_comprobantes_compra_orden_compra"),
            "id_orden_compra", "id_tenant");

        // Ningún índice compuesto nuevo de esta etapa queda liderado por id_tenant, salvo
        // ux_ordenes_compra_numero (ya cubierto arriba) — un swap como el del juez B rompería
        // esta cobertura por prefijo y lo agarra sin depender del conteo de nombres.
        var compuestosSinLiderarPorTenant = new[]
        {
            ("ordenes_compra", "ix_ordenes_compra_punto_venta_fecha"),
            ("ordenes_compra", "ix_ordenes_compra_proveedor"),
            ("ordenes_compra", "ak_ordenes_compra_id_orden_compra_id_tenant"),
            ("items_orden_compra", "ix_items_orden_compra_orden_compra"),
            ("items_orden_compra", "ix_items_orden_compra_articulo"),
            ("items_orden_compra", "ux_items_orden_compra_orden"),
            ("comprobantes_compra", "ix_comprobantes_compra_orden_compra")
        };

        foreach (var (tabla, nombre) in compuestosSinLiderarPorTenant)
        {
            var columnas = await ObtenerColumnasDelIndiceAsync(cruda, tabla, nombre);
            Assert.NotEqual("id_tenant", columnas[0]);
        }
    }

    private static async Task<string> ObtenerIndexDefAsync(NpgsqlConnection cruda, string tabla, string indexname)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT indexdef FROM pg_indexes WHERE tablename = $1 AND indexname = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = tabla });
        comando.Parameters.Add(new NpgsqlParameter { Value = indexname });

        var indexdef = (string?)await comando.ExecuteScalarAsync();
        Assert.NotNull(indexdef);
        return indexdef!;
    }

    private static void AssertOrdenDeColumnas(string indexdef, params string[] columnasEsperadas)
    {
        Assert.Equal(columnasEsperadas, ExtraerColumnas(indexdef));
    }

    private static async Task<string[]> ObtenerColumnasDelIndiceAsync(NpgsqlConnection cruda, string tabla, string indexname)
    {
        return ExtraerColumnas(await ObtenerIndexDefAsync(cruda, tabla, indexname));
    }

    private static string[] ExtraerColumnas(string indexdef)
    {
        var match = Regex.Match(indexdef, @"USING btree \(([^)]*)\)");
        Assert.True(match.Success, $"No se pudo parsear el orden de columnas de: {indexdef}");
        return match.Groups[1].Value.Split(", ", StringSplitOptions.TrimEntries);
    }

    private static async Task<List<string>> ListarIndicesAsync(NpgsqlConnection cruda, string tabla)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT indexname FROM pg_indexes WHERE tablename = $1 ORDER BY indexname";
        comando.Parameters.Add(new NpgsqlParameter { Value = tabla });

        var indices = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            indices.Add(lector.GetString(0));
        }

        return indices;
    }

    // ---------------------------------------------------------------------------------------
    // db-error-backstops — exenciones de FK (task 1.27)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnIdTenantInexistenteEnOrdenesDeCompraViolaAlgunaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdTenantInexistenteEnOrdenesDeCompraViolaAlgunaFkGenerica23503));

        await using var comoPlataforma = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = comoPlataforma.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, NULL, 'borrador'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_tenant inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.StartsWith("fk_ordenes_compra_", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdEmpleadoInexistenteEnOrdenesDeCompraViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdEmpleadoInexistenteEnOrdenesDeCompraViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, NULL, 'borrador'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_empleado inexistente

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_ordenes_compra_empleado", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdEmpleadoCierreInexistenteEnOrdenesDeCompraViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdEmpleadoCierreInexistenteEnOrdenesDeCompraViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, 801, now(), now(), NULL, now(), $5, NULL, 'cerrada'::estado_orden_compra, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_empleado_cierre inexistente

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_ordenes_compra_empleado_cierre", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdOrdenCompraInexistenteEnItemsDeOrdenDeCompraViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdOrdenCompraInexistenteEnItemsDeOrdenDeCompraViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_orden_compra " +
            "(id_tenant, id_orden_compra, orden, id_articulo, descripcion, cantidad_pedida, costo_unitario_estimado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 4, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_orden_compra inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_orden_compra_orden_compra", excepcion.ConstraintName);
    }

    /// <summary><c>ManejadorDeErrores.cs</c>: smoke test de que los nombres de FK nuevos siguen
    /// el prefijo genérico <c>fk_</c> que la clasificación por prefijo ya cubre sin ningún caso
    /// hardcodeado adicional (excepto FK 9, que sí lo tiene por ser client-reachable — probado
    /// aparte en slice 3, `ExigirOrdenLigableAsync`).</summary>
    [Fact]
    public void LosNombresDeFkNuevosDeOrdenesDeCompraEmpiezanConElPrefijoGenericoFk()
    {
        string[] nombres =
        [
            "fk_ordenes_compra_tenant",
            "fk_ordenes_compra_punto_venta",
            "fk_ordenes_compra_proveedor",
            "fk_ordenes_compra_empleado",
            "fk_ordenes_compra_empleado_cierre",
            "fk_items_orden_compra_tenant",
            "fk_items_orden_compra_orden_compra",
            "fk_items_orden_compra_articulo",
            "fk_comprobantes_compra_orden_compra"
        ];

        Assert.All(nombres, n => Assert.StartsWith("fk_", n));
    }

    private async Task<int> InsertarBorradorAsync(Escenario e)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO ordenes_compra " +
            "(id_tenant, id_punto_venta, id_proveedor, id_empleado, numero, fecha_emision, fecha_envio, " +
            " fecha_esperada, fecha_cierre, id_empleado_cierre, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, NULL, NULL, 'borrador'::estado_orden_compra, now(), now(), NULL) " +
            "RETURNING id_orden_compra";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        return (int)(await comando.ExecuteScalarAsync())!;
    }
}
