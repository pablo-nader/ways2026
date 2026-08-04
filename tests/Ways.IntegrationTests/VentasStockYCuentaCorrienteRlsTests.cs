using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.18, spec: comprobantes-venta / Tenant and Punto de Venta
/// Isolation): mismo patrón que <c>OfertasRlsTests</c> — SQL crudo, independiente de EF, 0
/// filas para SELECT/UPDATE cross-tenant, 42501 para el INSERT que viola <c>WITH CHECK</c>, más
/// un proof a nivel EF (LINQ) por tabla. Cubre las cinco tablas con columna <c>id_x</c> propia
/// (<c>comprobantes_venta</c>, <c>items_comprobante_venta</c>, <c>pagos_comprobante</c>,
/// <c>movimientos_stock</c>, <c>movimientos_cuenta_corriente</c>); <c>stock</c> (PK compuesta
/// sin columna <c>id</c> propia) tiene su propia suite dedicada, <see cref="StockRlsTests"/>,
/// mismo criterio que <c>NumeracionesComprobanteRlsTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasStockYCuentaCorrienteRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "comprobantes_venta", "id_comprobante_venta" },
        { "items_comprobante_venta", "id_item" },
        { "pagos_comprobante", "id_pago" },
        { "movimientos_stock", "id_movimiento" },
        { "movimientos_cuenta_corriente", "id_movimiento" }
    };

    private sealed record Escenario(
        int IdTenant, int IdPuntoVenta, int IdArticulo, int IdArea, int IdAlicuotaIva,
        int IdMedioPago, int IdListaPrecio, int IdCliente, int IdEmpleado, int IdTipoComprobanteTx,
        int IdComprobanteVenta, int IdItem, int IdPago, int IdMovimientoStock, int IdMovimientoCc);

    /// <summary>Arma la cadena completa de prerequisitos y una fila en cada una de las seis
    /// tablas nuevas, todas del mismo tenant A — comparte el escenario entero para no repetir
    /// diez tablas de seed por test.</summary>
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

        var medioPago = new MedioPago
        {
            IdTenant = tenant.Id,
            Nombre = nombre,
            Orden = 1,
            Comportamiento = ComportamientoMedioPago.Efectivo,
            AdmiteVuelto = true,
            RequiereReferencia = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenant.Id,
            Numero = 2,
            Nombre = nombre,
            IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = listaPrecio.Id,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
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

        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.Id,
            IdTipoComprobante = idTipoComprobanteTx,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = puntoVenta.Id,
            IdEmpleado = usuario.Id,
            IdCliente = cliente.Id,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var item = new ItemComprobanteVenta
        {
            IdTenant = tenant.Id,
            IdComprobanteVenta = comprobante.Id,
            Orden = 1,
            IdArticulo = articulo.Id,
            Descripcion = nombre,
            IdArea = area.Id,
            IdListaPrecio = listaPrecio.Id,
            IdAlicuotaIva = idAlicuotaIva,
            PorcentajeIva = 21m,
            Cantidad = 1m,
            PrecioUnitario = 100m,
            Descuento = 0m,
            Total = 100m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ItemsComprobanteVenta.Add(item);
        await db.SaveChangesAsync();

        var pago = new PagoComprobante
        {
            IdTenant = tenant.Id,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = medioPago.Id,
            Importe = 100m,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PagosComprobante.Add(pago);
        await db.SaveChangesAsync();

        var movimientoStock = new MovimientoStock
        {
            IdTenant = tenant.Id,
            IdArticulo = articulo.Id,
            IdPuntoVenta = puntoVenta.Id,
            Cantidad = -1m,
            Motivo = MotivoStock.Venta,
            IdComprobanteVenta = comprobante.Id,
            IdEmpleado = usuario.Id,
            CreadoEl = ahora
        };
        db.MovimientosStock.Add(movimientoStock);
        await db.SaveChangesAsync();

        var movimientoCc = new MovimientoCuentaCorriente
        {
            IdTenant = tenant.Id,
            IdCliente = cliente.Id,
            Fecha = ahora,
            IdPuntoVenta = puntoVenta.Id,
            IdEmpleado = usuario.Id,
            Tipo = TipoMovimientoCc.Consumo,
            IdComprobanteVenta = comprobante.Id,
            IdPagoComprobante = pago.Id,
            Importe = 100m,
            SaldoResultante = 100m
        };
        db.MovimientosCuentaCorriente.Add(movimientoCc);
        await db.SaveChangesAsync();

        return new Escenario(
            tenant.Id, puntoVenta.Id, articulo.Id, area.Id, idAlicuotaIva, medioPago.Id, listaPrecio.Id,
            cliente.Id, usuario.Id, idTipoComprobanteTx,
            comprobante.Id, item.Id, pago.Id, movimientoStock.Id, movimientoCc.Id);
    }

    private static int IdDeFila(Escenario escenario, string tabla) => tabla switch
    {
        "comprobantes_venta" => escenario.IdComprobanteVenta,
        "items_comprobante_venta" => escenario.IdItem,
        "pagos_comprobante" => escenario.IdPago,
        "movimientos_stock" => escenario.IdMovimientoStock,
        "movimientos_cuenta_corriente" => escenario.IdMovimientoCc,
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

        // movimientos_stock/movimientos_cuenta_corriente no tienen updated_at (ledger
        // append-only, sin EntidadBase): el UPDATE toca observaciones/detalle en su lugar --
        // da lo mismo qué columna, lo que se prueba es que USING oculta la fila antes de que
        // el UPDATE la alcance.
        var (columna, valor) = tabla switch
        {
            "movimientos_stock" => ("observaciones", "'x'"),
            "movimientos_cuenta_corriente" => ("detalle", "'x'"),
            _ => ("updated_at", "now()")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"UPDATE {tabla} SET {columna} = {valor} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant también bloquea a las
    /// entidades que pasan por el ORM — <c>movimientos_stock</c>/<c>movimientos_cuenta_corriente</c>
    /// quedan cubiertas acá también aunque usen el filtro manual (no heredan
    /// <c>EntidadTenant</c>, ver los comentarios de <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoStock</c>/
    /// <c>AplicarFiltroDeTenantEnMovimientoCuentaCorriente</c>).</summary>
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
            "comprobantes_venta" => await sesionB.ComprobantesVenta.AnyAsync(c => c.Id == escenario.IdComprobanteVenta),
            "items_comprobante_venta" => await sesionB.ItemsComprobanteVenta.AnyAsync(i => i.Id == escenario.IdItem),
            "pagos_comprobante" => await sesionB.PagosComprobante.AnyAsync(p => p.Id == escenario.IdPago),
            "movimientos_stock" => await sesionB.MovimientosStock.AnyAsync(m => m.Id == escenario.IdMovimientoStock),
            "movimientos_cuenta_corriente" => await sesionB.MovimientosCuentaCorriente.AnyAsync(m => m.Id == escenario.IdMovimientoCc),
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

        // Sesión del tenant A intentando insertar una fila con id_tenant del tenant B (ajeno) —
        // WITH CHECK tiene que rechazarla antes de que cualquier FK se evalúe.
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", escenario.IdTenant);

        var ahora = DateTimeOffset.UtcNow;

        (string Sql, Action<NpgsqlCommand> Bind) insert = tabla switch
        {
            "comprobantes_venta" => (
                "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, " +
                "id_empleado, id_cliente, subtotal, descuento_total, total, estado, created_at, updated_at) " +
                "VALUES ($1, $2, 2, $3, $4, $5, $6, 100, 0, 100, 'emitido', $3, $3)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdTipoComprobanteTx });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdCliente });
                }),
            "items_comprobante_venta" => (
                "INSERT INTO items_comprobante_venta (id_tenant, id_comprobante_venta, orden, descripcion, " +
                "id_area, id_lista_precio, id_alicuota_iva, porcentaje_iva, cantidad, precio_unitario, total, " +
                "created_at, updated_at) " +
                "VALUES ($1, $2, 2, 'intruso', $3, $4, $5, 21, 1, 10, 10, $6, $6)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdComprobanteVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArea });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdListaPrecio });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdAlicuotaIva });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            "pagos_comprobante" => (
                "INSERT INTO pagos_comprobante (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, " +
                "created_at, updated_at) VALUES ($1, $2, $3, 10, 0, $4, $4)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdComprobanteVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdMedioPago });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            "movimientos_stock" => (
                "INSERT INTO movimientos_stock (id_tenant, id_articulo, id_punto_venta, cantidad, motivo, " +
                "id_empleado, creado_el) VALUES ($1, $2, $3, -1, 'venta', $4, $5)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdArticulo });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                }),
            "movimientos_cuenta_corriente" => (
                "INSERT INTO movimientos_cuenta_corriente (id_tenant, id_cliente, fecha, id_punto_venta, " +
                "id_empleado, tipo, importe, saldo_resultante) VALUES ($1, $2, $3, $4, $5, 'consumo', 10, 10)",
                c =>
                {
                    c.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdCliente });
                    c.Parameters.Add(new NpgsqlParameter { Value = ahora });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdPuntoVenta });
                    c.Parameters.Add(new NpgsqlParameter { Value = escenario.IdEmpleado });
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        await using var comando = cruda.CreateCommand();
        comando.CommandText = insert.Sql;
        insert.Bind(comando);

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de
        // cualquier FK, sin importar que el resto de columnas referencien filas válidas del
        // tenant A (la sesión sigue siendo la del tenant A, así que solo id_tenant desentona).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }
}
