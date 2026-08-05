using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 1 (task 1.12, design: Table Shapes — F):
/// mismo patrón que <c>TurnosCajaYGastosRlsTests</c> — SQL crudo, independiente de EF, 0 filas
/// para SELECT/UPDATE cross-tenant, 42501 para el INSERT que viola <c>WITH CHECK</c>, más un
/// proof a nivel EF (LINQ) por tabla. Cubre las dos tablas nuevas de esta slice
/// (<c>comprobantes_compra</c>/<c>items_comprobante_compra</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasSchemaRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "comprobantes_compra", "id_comprobante_compra" },
        { "items_comprobante_compra", "id_item" }
    };

    private sealed record Escenario(
        int IdTenant, int IdProveedor, int IdPuntoVenta, int IdEmpleado, int IdArticulo,
        int IdAlicuotaIva, int IdTipoComprobanteCompra, int IdComprobanteCompra, int IdItem);

    /// <summary>Arma la cadena completa de prerequisitos y una fila en cada una de las dos
    /// tablas nuevas, todas del mismo tenant A.</summary>
    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra roles, alícuotas, tipos de comprobante)

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

        var area = new Area { IdTenant = tenant.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = tenant.Id,
            RazonSocial = nombre,
            IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "vendedor",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = "hash-de-prueba",
            PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        var idTipoCompra = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var comprobante = new ComprobanteCompra
        {
            IdTenant = tenant.Id,
            IdProveedor = proveedor.Id,
            IdTipoComprobante = idTipoCompra,
            IdPuntoVenta = puntoVenta.Id,
            IdEmpleado = usuario.Id,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoCompra.Borrador,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(comprobante);
        await db.SaveChangesAsync();

        var item = new ItemComprobanteCompra
        {
            IdTenant = tenant.Id,
            IdComprobanteCompra = comprobante.Id,
            Orden = 1,
            IdArticulo = articulo.Id,
            Descripcion = nombre,
            Cantidad = 1m,
            CostoUnitario = 100m,
            Descuento = 0m,
            IdAlicuotaIva = idAlicuotaIva,
            PorcentajeIva = 21m,
            Total = 100m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ItemsComprobanteCompra.Add(item);
        await db.SaveChangesAsync();

        return new Escenario(
            tenant.Id, proveedor.Id, puntoVenta.Id, usuario.Id, articulo.Id, idAlicuotaIva,
            idTipoCompra, comprobante.Id, item.Id);
    }

    private static int IdDeFila(Escenario escenario, string tabla) => tabla switch
    {
        "comprobantes_compra" => escenario.IdComprobanteCompra,
        "items_comprobante_compra" => escenario.IdItem,
        _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
    };

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);
        var idFila = IdDeFila(escenario, tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var escenario = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);
        var idFila = IdDeFila(escenario, tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        var (columna, valor) = tabla switch
        {
            "comprobantes_compra" => ("observaciones", "'tocado por intruso'"),
            "items_comprobante_compra" => ("descripcion", "'tocado por intruso'"),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"UPDATE {tabla} SET {columna} = {valor} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    /// <summary>Proof a nivel EF (LINQ): ambas tablas heredan <c>EntidadTenant</c>, así que el
    /// filtro genérico de <c>WaysDbContext.AplicarFiltroDeTenant</c> las cubre sin filtro manual
    /// — a diferencia de <c>movimientos_stock</c>.</summary>
    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant(string tabla, string columnaId)
    {
        _ = columnaId;
        var escenario = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenantB.Id));

        var visible = tabla switch
        {
            "comprobantes_compra" => await sesionB.ComprobantesCompra.AnyAsync(c => c.Id == escenario.IdComprobanteCompra),
            "items_comprobante_compra" => await sesionB.ItemsComprobanteCompra.AnyAsync(i => i.Id == escenario.IdItem),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        Assert.False(visible);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnInsertConIdTenantAjenoSeRechaza(string tabla, string columnaId)
    {
        _ = columnaId;
        var escenario = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var tenantB = new Tenant
        {
            Nombre = nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla + "-B",
            Estado = EstadoTenant.Activo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        // Sesión del tenant A intentando insertar con id_tenant del tenant B (ajeno) — WITH
        // CHECK tiene que rechazarla antes de que cualquier FK/CHECK se evalúe.
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", escenario.IdTenant);

        var ahora = DateTimeOffset.UtcNow;

        (string Sql, Action<NpgsqlCommand> Bind) insert = tabla switch
        {
            "comprobantes_compra" => (
                "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, " +
                "id_punto_venta, id_empleado, subtotal, descuento_total, total, estado, created_at, updated_at) " +
                "VALUES ($1, $2, $3, $4, $5, 10, 0, 10, 'borrador', $6, $6)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdProveedor });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTipoComprobanteCompra });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            // orden = 2 a propósito: esquiva ux_items_comprobante_compra_orden contra la fila ya
            // sembrada con orden = 1 — lo que se prueba acá es el 42501, no la unicidad.
            "items_comprobante_compra" => (
                "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
                "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
                "created_at, updated_at) VALUES ($1, $2, 2, $3, 'intruso', 1, 10, 0, $4, 21, 10, $5, $5)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdComprobanteCompra });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdAlicuotaIva });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = insert.Sql;
        insert.Bind(comando);

        // 42501 = insufficient_privilege (violación de WITH CHECK) — se dispara antes de
        // cualquier FK/CHECK, sin importar que el resto de columnas referencien filas válidas
        // del tenant A.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
