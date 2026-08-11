using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.19, db-error-backstops, design: Backstop Map; ampliado en
/// un follow-up con <c>ck_pagos_comprobante_importe_no_negativo</c>, gate de DB aprobado
/// 2026-08-04): raw-SQL INSERTs que bypasean por completo
/// <c>ValidadorDePagos</c>/<c>ReglaDeComprobantes</c> (no hay <c>ServicioDeVentas</c> todavía,
/// Slice 4) para probar las cuatro CHECKs de esquema nuevas, la unicidad de
/// <c>ux_comprobantes_venta_numero</c> (SQLSTATE 23505 — la traducción exacta al código de
/// dominio, incluido el "ordering trap", vive en <c>ManejadorDeErroresVentasTests</c>, que no
/// depende de Postgres real) y la exención documentada de <c>pk_stock</c> — mismo patrón que
/// <c>OfertasCheckBackstopTests</c>/<c>NumeracionesComprobanteBackstopTests</c>.
///
/// Honesto sobre alcanzabilidad (design: Backstop Map): bajo operación normal (Slice 4 en
/// adelante) ninguna de estas ramas es alcanzable por un cliente HTTP — prueban la traducción de
/// esquema, no un camino de cliente real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasStockBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Prerequisitos(
        int IdTenant, int IdPuntoVenta, int IdArticulo, int IdEmpleado, int IdCliente, int IdTipoComprobanteTx,
        int IdArea, int IdListaPrecio, int IdAlicuotaIva);

    private async Task<Prerequisitos> SembrarPrerequisitosAsync(string nombre)
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

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
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

        return new Prerequisitos(
            tenant.Id, puntoVenta.Id, articulo.Id, usuario.Id, cliente.Id, idTipoComprobanteTx,
            area.Id, listaPrecio.Id, idAlicuotaIva);
    }

    private async Task<int> SembrarComprobanteAsync(Prerequisitos p)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = p.IdTenant,
            IdTipoComprobante = p.IdTipoComprobanteTx,
            Numero = Random.Shared.Next(1, 1_000_000),
            Fecha = ahora,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleado = p.IdEmpleado,
            IdCliente = p.IdCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    // ---- ck_comprobantes_venta_numero_positivo ---------------------------------------------

    [Fact]
    public async Task UnComprobanteConNumeroCeroOMenorViolaLaCheckDeNumeroPositivo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnComprobanteConNumeroCeroOMenorViolaLaCheckDeNumeroPositivo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, " +
            "id_empleado, id_cliente, subtotal, descuento_total, total, estado, created_at, updated_at) " +
            "VALUES ($1, $2, 0, now(), $3, $4, $5, 100, 0, 100, 'emitido', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTipoComprobanteTx });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdCliente });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_venta_numero_positivo", excepcion.ConstraintName);
    }

    // ---- ck_pagos_comprobante_vuelto_no_negativo -------------------------------------------

    [Fact]
    public async Task UnPagoConVueltoNegativoViolaLaCheckDeVueltoNoNegativo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnPagoConVueltoNegativoViolaLaCheckDeVueltoNoNegativo));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = p.IdTenant,
            IdTipoComprobante = p.IdTipoComprobanteTx,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleado = p.IdEmpleado,
            IdCliente = p.IdCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var medioPago = new MedioPago
        {
            IdTenant = p.IdTenant, Nombre = "efectivo", Orden = 1, Comportamiento = ComportamientoMedioPago.Efectivo,
            AdmiteVuelto = true, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO pagos_comprobante (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, " +
            "created_at, updated_at) VALUES ($1, $2, $3, 100, -5, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = comprobante.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = medioPago.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_pagos_comprobante_vuelto_no_negativo", excepcion.ConstraintName);
    }

    // ---- ck_pagos_comprobante_importe_no_negativo (follow-up, gate de DB aprobado 2026-08-04)
    // -------------------------------------------------------------------------------------------
    // Honesto sobre alcanzabilidad: bajo operación normal esta rama es inalcanzable por un
    // cliente vía servicio — la regla 0 de ValidadorDePagos ya rechaza cualquier Importe
    // negativo antes de que la fila llegue a INSERT (misma razón que documenta la clase sobre
    // las otras CHECKs de esta tabla).

    [Fact]
    public async Task UnPagoConImporteNegativoViolaLaCheckDeImporteNoNegativo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnPagoConImporteNegativoViolaLaCheckDeImporteNoNegativo));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = p.IdTenant,
            IdTipoComprobante = p.IdTipoComprobanteTx,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleado = p.IdEmpleado,
            IdCliente = p.IdCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var medioPago = new MedioPago
        {
            IdTenant = p.IdTenant, Nombre = "efectivo", Orden = 1, Comportamiento = ComportamientoMedioPago.Efectivo,
            AdmiteVuelto = true, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO pagos_comprobante (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, " +
            "created_at, updated_at) VALUES ($1, $2, $3, -100, 0, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = comprobante.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = medioPago.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_pagos_comprobante_importe_no_negativo", excepcion.ConstraintName);
    }

    // ---- ck_movimientos_stock_cantidad_no_cero ---------------------------------------------

    [Fact]
    public async Task UnMovimientoDeStockConCantidadCeroViolaLaCheckDeCantidadNoCero()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeStockConCantidadCeroViolaLaCheckDeCantidadNoCero));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_stock (id_tenant, id_articulo, id_punto_venta, cantidad, motivo, " +
            "id_empleado, creado_el) VALUES ($1, $2, $3, 0, 'ajuste', $4, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_movimientos_stock_cantidad_no_cero", excepcion.ConstraintName);
    }

    // ---- ux_comprobantes_venta_numero (SQLSTATE + ConstraintName; el código de dominio lo
    // prueba ManejadorDeErroresVentasTests, que no depende de Postgres real) ------------------

    [Fact]
    public async Task DosComprobantesConElMismoNumeroEnElMismoPuntoDeVentaYTipoViolanLaUnicidad()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosComprobantesConElMismoNumeroEnElMismoPuntoDeVentaYTipoViolanLaUnicidad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO comprobantes_venta (id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, " +
                "id_empleado, id_cliente, subtotal, descuento_total, total, estado, created_at, updated_at) " +
                "VALUES ($1, $2, 7, now(), $3, $4, $5, 100, 0, 100, 'emitido', now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTipoComprobanteTx });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdCliente });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_comprobantes_venta_numero", excepcion.ConstraintName);
    }

    // ---- pk_stock (exención documentada de prueba de carrera) -------------------------------

    [Fact]
    public async Task UnaFilaDeStockConLaMismaClaveInsertadaPorFueraDelUpsertViolaLaPk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaFilaDeStockConLaMismaClaveInsertadaPorFueraDelUpsertViolaLaPk));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarAsync()
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) VALUES ($1, $2, $3, 0)";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarAsync();

        var excepcion = await Assert.ThrowsAsync<PostgresException>(InsertarAsync);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("pk_stock", excepcion.ConstraintName);
    }

    // ---- stage-9-costo-congelado (task 1.14, design: Backstop Map): ambas CHECKs son
    // inalcanzables desde todo camino de escritura verificado (ServicioDeArticulos.ExigirCostoValido
    // y CalculadorDeCompra solo escriben costo_nominal >= 0; nada fuera del backfill escribe
    // costo_es_estimado = true) — el respaldo de esquema existe igual porque un 23514 sin mapear
    // en el checkout, el endpoint de mayor alcance del sistema, degradaría a 500. -------------

    [Fact]
    public async Task UnItemConCostoUnitarioNegativoViolaLaCheckDeCostoNoNegativo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConCostoUnitarioNegativoViolaLaCheckDeCostoNoNegativo));
        var idComprobante = await SembrarComprobanteAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_venta (id_tenant, id_comprobante_venta, orden, id_articulo, " +
            "descripcion, id_area, id_lista_precio, id_alicuota_iva, porcentaje_iva, cantidad, " +
            "precio_unitario, descuento, total, costo_unitario, costo_es_estimado, created_at, updated_at) " +
            "VALUES ($1, $2, 1, $3, 'item-costo-negativo', $4, $5, $6, 0, 1, 100, 0, 100, -1, false, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArea });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_venta_costo_no_negativo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemMarcadoEstimadoSinCostoViolaLaCheckDeEstimadoConCosto()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemMarcadoEstimadoSinCostoViolaLaCheckDeEstimadoConCosto));
        var idComprobante = await SembrarComprobanteAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_venta (id_tenant, id_comprobante_venta, orden, id_articulo, " +
            "descripcion, id_area, id_lista_precio, id_alicuota_iva, porcentaje_iva, cantidad, " +
            "precio_unitario, descuento, total, costo_unitario, costo_es_estimado, created_at, updated_at) " +
            "VALUES ($1, $2, 1, $3, 'item-estimado-sin-costo', $4, $5, $6, 0, 1, 100, 0, 100, NULL, true, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArea });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_venta_estimado_con_costo", excepcion.ConstraintName);
    }
}
