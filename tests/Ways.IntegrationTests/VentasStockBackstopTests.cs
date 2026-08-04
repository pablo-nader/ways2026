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
/// stage-5-pos-ventas, Slice 3 (task 3.19, db-error-backstops, design: Backstop Map): raw-SQL
/// INSERTs que bypasean por completo <c>ValidadorDePagos</c>/<c>ReglaDeComprobantes</c> (no hay
/// <c>ServicioDeVentas</c> todavía, Slice 4) para probar las tres CHECKs de esquema nuevas, la
/// unicidad de <c>ux_comprobantes_venta_numero</c> (SQLSTATE 23505 — la traducción exacta al
/// código de dominio, incluido el "ordering trap", vive en
/// <c>ManejadorDeErroresVentasTests</c>, que no depende de Postgres real) y la exención
/// documentada de <c>pk_stock</c> — mismo patrón que <c>OfertasCheckBackstopTests</c>/
/// <c>NumeracionesComprobanteBackstopTests</c>.
///
/// Honesto sobre alcanzabilidad (design: Backstop Map): bajo operación normal (Slice 4 en
/// adelante) ninguna de estas ramas es alcanzable por un cliente HTTP — prueban la traducción de
/// esquema, no un camino de cliente real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasStockBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Prerequisitos(
        int IdTenant, int IdPuntoVenta, int IdArticulo, int IdEmpleado, int IdCliente, int IdTipoComprobanteTx);

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

        return new Prerequisitos(tenant.Id, puntoVenta.Id, articulo.Id, usuario.Id, cliente.Id, idTipoComprobanteTx);
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
}
