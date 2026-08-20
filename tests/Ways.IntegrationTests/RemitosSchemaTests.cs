using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 4 (tasks 4.27-4.35, mutation targets #38-#39,
/// db-error-backstops skill, design decisiones 5/11/18): RLS, las cinco CHECKs nuevas, la
/// partición parcial de <c>ux_remitos_numero</c>, el conteo vinculante de 30 índices nuevos
/// ACUMULADOS de la etapa, los nombres exactos de constraint (QUINTA ocurrencia del ordering
/// trap), el ALTER TYPE aislado, el orden del enum <c>MotivoStock</c> y el <c>TXR</c> guardado —
/// todos sobre la base COMPARTIDA de <see cref="WaysApiFixture"/> (mismo criterio que
/// <c>PresupuestosSchemaTests</c>: no depende del momento exacto de una migración de datos,
/// salvo el <c>TXR</c> guardado, que sí lo hace y se prueba aparte más abajo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RemitosSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(
        int IdTenant, int IdPuntoVenta, int IdCliente, int IdEmpleado, int IdArticulo,
        int IdListaPrecio, int IdAlicuotaIva);

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

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenant.Id, Numero = 601, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = listaPrecio.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
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

        return new Escenario(
            tenant.Id, puntoVenta.Id, cliente.Id, empleado.Id, articulo.Id, listaPrecio.Id, idAlicuotaIva);
    }

    private const string ColumnasRemito =
        "(id_tenant, id_punto_venta, id_cliente, id_empleado, numero, fecha_emision, fecha_salida, " +
        " direccion_entrega, observaciones, subtotal, descuento_total, total, estado, id_comprobante_venta, " +
        " created_at, updated_at, deleted_at)";

    private static async Task<int> InsertarBorradorAsync(NpgsqlConnection cruda, Escenario e)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_remito, NULL, now(), now(), NULL) " +
            "RETURNING id_remito";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<int> ObtenerIdTipoComprobanteAsync(NpgsqlConnection cruda, string codigo)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT id_tipo_comprobante FROM tipos_comprobante WHERE codigo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<int> InsertarVentaAsync(
        NpgsqlConnection cruda, Escenario e, int idTipoComprobante, long numero)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta " +
            "(id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_turno_caja, id_empleado, " +
            " id_cliente, id_comprobante_asociado, id_presupuesto_origen, subtotal, descuento_total, total, " +
            " neto_gravado, iva_total, direccion_entrega, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, now(), $4, NULL, $5, $6, NULL, NULL, 10, 0, 10, NULL, NULL, NULL, NULL, " +
            " 'emitido'::estado_comprobante, now(), now(), NULL) RETURNING id_comprobante_venta";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTipoComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = numero });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    // ---------------------------------------------------------------------------------------
    // RLS (task 4.27, mutation target #38)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosRemitosPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosRemitosPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosRemitosPorSelect) + "-B");

        int idRemito;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            idRemito = await InsertarBorradorAsync(cruda, a);
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM remitos WHERE id_remito = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idRemito });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosItemsDeRemitoPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDeRemitoPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDeRemitoPorSelect) + "-B");

        int idItem;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            var idRemito = await InsertarBorradorAsync(cruda, a);

            await using var insertarItem = cruda.CreateCommand();
            insertarItem.CommandText =
                "INSERT INTO items_remito " +
                "(id_tenant, id_remito, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
                " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
                " costo_unitario, costo_es_estimado, id_lote, created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, 1, $3, 'seed', 2, 10, 0, 20, $4, NULL, $5, 21, NULL, false, NULL, now(), now(), NULL) " +
                "RETURNING id_item";
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = idRemito });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdArticulo });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdListaPrecio });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdAlicuotaIva });
            idItem = (int)(await insertarItem.ExecuteScalarAsync())!;
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM items_remito WHERE id_item = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idItem });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnRemitosSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnRemitosSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnRemitosSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = comoA.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = b.IdTenant }); // ajeno a la sesión (tenant A)
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---------------------------------------------------------------------------------------
    // ck_remitos_salida_completa (task 4.28, mutation target #38)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnNumeroSinFechaDeSalidaViolaLaCheckDeSalidaCompleta()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroSinFechaDeSalidaViolaLaCheckDeSalidaCompleta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, 601, now(), NULL, NULL, NULL, 0, 0, 0, 'emitido'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_remitos_salida_completa", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaFechaDeSalidaSinNumeroViolaLaCheckDeSalidaCompleta()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaFechaDeSalidaSinNumeroViolaLaCheckDeSalidaCompleta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), now(), NULL, NULL, 0, 0, 0, 'emitido'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_remitos_salida_completa", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnEstadoEmitidoSinNumeroViolaLaCheckDeSalidaCompleta()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoEmitidoSinNumeroViolaLaCheckDeSalidaCompleta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'emitido'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_remitos_salida_completa", excepcion.ConstraintName);
    }

    /// <summary>Dirección permitida explícita: <c>anulado</c> sin número/fecha_salida es
    /// admitido — un borrador puede anularse antes de ser emitido.</summary>
    [Fact]
    public async Task UnEstadoAnuladoSinNumeroNoViolaLaCheckDeSalidaCompleta()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoAnuladoSinNumeroNoViolaLaCheckDeSalidaCompleta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'anulado'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // ---------------------------------------------------------------------------------------
    // ck_remitos_facturacion (task 4.28, mutation target #38)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnEstadoFacturadoSinComprobanteLigadoViolaLaCheckDeFacturacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoFacturadoSinComprobanteLigadoViolaLaCheckDeFacturacion));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, 602, now(), now(), NULL, NULL, 0, 0, 0, 'facturado'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_remitos_facturacion", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnComprobanteLigadoConEstadoNoFacturadoViolaLaCheckDeFacturacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnComprobanteLigadoConEstadoNoFacturadoViolaLaCheckDeFacturacion));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipoComprobante = await ObtenerIdTipoComprobanteAsync(cruda, "TXR");
        var idComprobante = await InsertarVentaAsync(cruda, e, idTipoComprobante, numero: 1);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, 603, now(), now(), NULL, NULL, 0, 0, 0, 'emitido'::estado_remito, $5, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_remitos_facturacion", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnEstadoFacturadoConComprobanteLigadoNoViolaLaCheckDeFacturacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoFacturadoConComprobanteLigadoNoViolaLaCheckDeFacturacion));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipoComprobante = await ObtenerIdTipoComprobanteAsync(cruda, "TXR");
        var idComprobante = await InsertarVentaAsync(cruda, e, idTipoComprobante, numero: 2);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, 604, now(), now(), NULL, NULL, 0, 0, 0, 'facturado'::estado_remito, $5, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // ---------------------------------------------------------------------------------------
    // ck_items_remito_cantidad_positiva / costo_no_negativo / estimado_con_costo (task 4.28)
    // ---------------------------------------------------------------------------------------

    private static async Task<NpgsqlCommand> ComandoInsertarItemAsync(
        NpgsqlConnection cruda, Escenario e, int idRemito, decimal cantidad, decimal? costoUnitario, bool costoEsEstimado)
    {
        var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_remito " +
            "(id_tenant, id_remito, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
            " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
            " costo_unitario, costo_es_estimado, id_lote, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', $4, 10, 0, 0, $5, NULL, $6, 21, $7, $8, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idRemito });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = cantidad });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdAlicuotaIva });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)costoUnitario ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = costoEsEstimado });
        return comando;
    }

    [Fact]
    public async Task UnaCantidadNoPositivaViolaLaCheckDeItemsRemito()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaCantidadNoPositivaViolaLaCheckDeItemsRemito));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idRemito = await InsertarBorradorAsync(cruda, e);

        await using var comando = await ComandoInsertarItemAsync(cruda, e, idRemito, cantidad: 0, costoUnitario: null, costoEsEstimado: false);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_remito_cantidad_positiva", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCostoNegativoViolaLaCheckDeCostoNoNegativo()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCostoNegativoViolaLaCheckDeCostoNoNegativo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idRemito = await InsertarBorradorAsync(cruda, e);

        await using var comando = await ComandoInsertarItemAsync(cruda, e, idRemito, cantidad: 1, costoUnitario: -10m, costoEsEstimado: false);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_remito_costo_no_negativo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCostoEstimadoSinCostoViolaLaCheckDeEstimadoConCosto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCostoEstimadoSinCostoViolaLaCheckDeEstimadoConCosto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idRemito = await InsertarBorradorAsync(cruda, e);

        await using var comando = await ComandoInsertarItemAsync(cruda, e, idRemito, cantidad: 1, costoUnitario: null, costoEsEstimado: true);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_remito_estimado_con_costo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCostoEstimadoConCostoNoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCostoEstimadoConCostoNoViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idRemito = await InsertarBorradorAsync(cruda, e);

        await using var comando = await ComandoInsertarItemAsync(cruda, e, idRemito, cantidad: 1, costoUnitario: 15m, costoEsEstimado: true);

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // ---------------------------------------------------------------------------------------
    // Partición parcial de ux_remitos_numero (task 4.28, mutation target #38)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DosBorradoresDeRemitoSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto()
    {
        var e = await SembrarEscenarioAsync(nameof(DosBorradoresDeRemitoSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await InsertarBorradorAsync(cruda, e);
        await InsertarBorradorAsync(cruda, e); // no debe tirar — ambos numero NULL, filtro parcial
    }

    /// <summary>Mismo hallazgo estructural que
    /// <c>PresupuestosSchemaTests.ElTextoFuenteDeLaMigracionConservaLosDosFiltrosParcialesTargets4Y5</c>
    /// (mutation-proof-tests regla 3 exhausted, PROVABLY EQUIVALENT AT RUNTIME): Postgres nunca
    /// considera dos filas duplicadas bajo UNIQUE si cualquier columna indexada es NULL en
    /// cualquiera de las dos, y <c>numero</c> es la única columna nullable de
    /// <c>ux_remitos_numero</c> — quitar <c>filter: "numero IS NOT NULL"</c> no cambia nada
    /// observable en la prueba de arriba. Confirmado empíricamente (no razonado — regla 2): con
    /// el filtro borrado de la migración, la prueba de arriba sigue en VERDE. Prueba de TEXTO
    /// FUENTE en su lugar.</summary>
    [Fact]
    public void ElTextoFuenteDeLaMigracionConservaElFiltroParcialDeUxRemitosNumero()
    {
        var rutaMigracion = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260820004658_RemitosEtapa17.cs");

        Assert.True(File.Exists(rutaMigracion), $"No se encontró la migración en {rutaMigracion}");

        var fuente = File.ReadAllText(rutaMigracion);

        Assert.Contains("name: \"ux_remitos_numero\"", fuente);
        Assert.Contains("filter: \"numero IS NOT NULL\"", fuente);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    // ---------------------------------------------------------------------------------------
    // Conteo vinculante de índices ACUMULADO (task 4.29, gate — VINCULANTE, 30 acumulado)
    // ---------------------------------------------------------------------------------------

    /// <summary>Gate guard VINCULANTE (task 4.29, state.yaml db_gate_approval): el conteo total
    /// de índices nuevos ACUMULADO de la etapa tiene que ser EXACTAMENTE 30 — 14 de slice 1 (6
    /// presupuestos + 7 items_presupuesto + 1 comprobantes_venta) + 16 de esta slice (7 remitos
    /// incl. AK implícita + 8 items_remito + 1 movimientos_stock). Verificado por DEFINICIÓN
    /// contra <c>pg_indexes</c>, nunca por nombre.</summary>
    [Fact]
    public async Task ElConteoTotalDeIndicesNuevosAcumuladoEsExactamenteTreinta()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var indicesPresupuestos = await ListarIndicesAsync(cruda, "presupuestos");
        var indicesItemsPresupuesto = await ListarIndicesAsync(cruda, "items_presupuesto");
        var indicesDeSoportePresupuestos = indicesPresupuestos.Where(n => n != "pk_presupuestos").ToList();
        var indicesDeSoporteItemsPresupuesto = indicesItemsPresupuesto.Where(n => n != "pk_items_presupuesto").ToList();
        Assert.Equal(6, indicesDeSoportePresupuestos.Count);
        Assert.Equal(7, indicesDeSoporteItemsPresupuesto.Count);

        await using var comandoComprobantes = cruda.CreateCommand();
        comandoComprobantes.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'comprobantes_venta' AND indexname = 'ux_comprobantes_venta_presupuesto_origen'";
        var indiceComprobantes = (long)(await comandoComprobantes.ExecuteScalarAsync())!;
        Assert.Equal(1, indiceComprobantes);

        var indicesRemitos = await ListarIndicesAsync(cruda, "remitos");
        var indicesDeSoporteRemitos = indicesRemitos.Where(n => n != "pk_remitos").ToList();
        Assert.Equal(7, indicesDeSoporteRemitos.Count);
        Assert.Equal(
            new[]
            {
                "ak_remitos_id_remito_id_tenant",
                "ix_remitos_cliente",
                "ix_remitos_comprobante_venta",
                "ix_remitos_empleado",
                "ix_remitos_punto_venta_fecha",
                "ix_remitos_tenant",
                "ux_remitos_numero"
            },
            indicesDeSoporteRemitos.OrderBy(n => n));

        var indicesItemsRemito = await ListarIndicesAsync(cruda, "items_remito");
        var indicesDeSoporteItemsRemito = indicesItemsRemito.Where(n => n != "pk_items_remito").ToList();
        Assert.Equal(8, indicesDeSoporteItemsRemito.Count);
        Assert.Equal(
            new[]
            {
                "ix_items_remito_alicuota_iva",
                "ix_items_remito_articulo",
                "ix_items_remito_lista_precio",
                "ix_items_remito_lote",
                "ix_items_remito_oferta",
                "ix_items_remito_remito",
                "ix_items_remito_tenant",
                "ux_items_remito_orden"
            },
            indicesDeSoporteItemsRemito.OrderBy(n => n));

        await using var comandoMovimientos = cruda.CreateCommand();
        comandoMovimientos.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'movimientos_stock' AND indexname = 'ix_movimientos_stock_remito'";
        var indiceMovimientos = (long)(await comandoMovimientos.ExecuteScalarAsync())!;
        Assert.Equal(1, indiceMovimientos);

        // No debe existir NINGÚN índice extra sobre id_remito más allá del nombrado a mano.
        await using var comandoSinAutogenerado = cruda.CreateCommand();
        comandoSinAutogenerado.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'movimientos_stock' " +
            "AND indexname ILIKE '%remito%' AND indexname <> 'ix_movimientos_stock_remito'";
        var autogenerados = (long)(await comandoSinAutogenerado.ExecuteScalarAsync())!;
        Assert.Equal(0, autogenerados);

        var totalAcumulado =
            indicesDeSoportePresupuestos.Count + indicesDeSoporteItemsPresupuesto.Count + (int)indiceComprobantes +
            indicesDeSoporteRemitos.Count + indicesDeSoporteItemsRemito.Count + (int)indiceMovimientos;

        Assert.Equal(30, totalAcumulado);
    }

    [Fact]
    public async Task LasDefinicionesDeLosIndicesCompuestosDeRemitosRespetanElOrdenDeColumnasDelContrato()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "remitos", "ix_remitos_punto_venta_fecha"),
            "id_punto_venta", "id_tenant", "fecha_emision");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "remitos", "ix_remitos_cliente"),
            "id_cliente", "id_tenant");

        var defNumero = await ObtenerIndexDefAsync(cruda, "remitos", "ux_remitos_numero");
        AssertOrdenDeColumnas(defNumero, "id_tenant", "id_punto_venta", "numero");
        Assert.Contains("CREATE UNIQUE INDEX", defNumero);
        Assert.Contains("WHERE (numero IS NOT NULL)", defNumero);

        var defAk = await ObtenerIndexDefAsync(cruda, "remitos", "ak_remitos_id_remito_id_tenant");
        AssertOrdenDeColumnas(defAk, "id_remito", "id_tenant");
        Assert.Contains("CREATE UNIQUE INDEX", defAk);

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_remito", "ix_items_remito_remito"),
            "id_remito", "id_tenant");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_remito", "ix_items_remito_articulo"),
            "id_articulo", "id_tenant");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_remito", "ix_items_remito_lote"),
            "id_lote", "id_articulo", "id_tenant");

        var defUxOrden = await ObtenerIndexDefAsync(cruda, "items_remito", "ux_items_remito_orden");
        AssertOrdenDeColumnas(defUxOrden, "id_remito", "orden");
        Assert.Contains("CREATE UNIQUE INDEX", defUxOrden);

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "movimientos_stock", "ix_movimientos_stock_remito"),
            "id_remito", "id_tenant");

        var compuestosSinLiderarPorTenant = new[]
        {
            ("remitos", "ix_remitos_punto_venta_fecha"),
            ("remitos", "ix_remitos_cliente"),
            ("remitos", "ak_remitos_id_remito_id_tenant"),
            ("items_remito", "ix_items_remito_remito"),
            ("items_remito", "ix_items_remito_articulo"),
            ("items_remito", "ux_items_remito_orden"),
            ("movimientos_stock", "ix_movimientos_stock_remito")
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
    // db-error-backstops — traducción de constraints exactas (tasks 4.22/4.23/4.30)
    // ---------------------------------------------------------------------------------------

    /// <summary>Prueba SOLO el SQLSTATE/ConstraintName crudo — la traducción a
    /// <c>409 numero_de_remito_duplicado</c> (QUINTA ocurrencia del ordering trap) se prueba
    /// contra el <c>ManejadorDeErrores</c> REAL en <c>ManejadorDeErroresRemitosTests.cs</c>.</summary>
    [Fact]
    public async Task UnDuplicadoRawDeUxRemitosNumeroDisparaElSqlstateCorrecto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnDuplicadoRawDeUxRemitosNumeroDisparaElSqlstateCorrecto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        async Task InsertarEmitidoAsync(long numero)
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO remitos " + ColumnasRemito +
                " VALUES ($1, $2, $3, $4, $5, now(), now(), NULL, NULL, 0, 0, 0, 'emitido'::estado_remito, NULL, now(), now(), NULL)";
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
            comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
            comando.Parameters.Add(new NpgsqlParameter { Value = numero });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarEmitidoAsync(700);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => InsertarEmitidoAsync(700));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_remitos_numero", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdClienteInexistenteEnRemitosViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdClienteInexistenteEnRemitosViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO remitos " + ColumnasRemito +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_remito, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_cliente inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_remitos_cliente", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdArticuloInexistenteEnItemsDeRemitoViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdArticuloInexistenteEnItemsDeRemitoViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idRemito = await InsertarBorradorAsync(cruda, e);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_remito " +
            "(id_tenant, id_remito, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
            " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
            " costo_unitario, costo_es_estimado, id_lote, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 2, 10, 0, 20, $4, NULL, $5, 21, NULL, false, NULL, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idRemito });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_articulo inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_remito_articulo", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // MotivoStock.Remito — orden del enum (task 4.31, mutation target #39)
    // ---------------------------------------------------------------------------------------

    /// <summary>Mutation target #39 — CORRECCIÓN REGISTRADA (mutation-proof-tests regla 2, "run
    /// it, don't reason it"): design.md's propia premisa ("insertarlo en el medio haría que
    /// TODOS los valores existentes se lean con el valor incorrecto") NO se sostiene en tiempo
    /// de ejecución. <c>npgsql.MapEnum&lt;T&gt;()</c> resuelve por NOMBRE (vía
    /// <c>NpgsqlSnakeCaseNameTranslator</c>, el default sin tercer argumento — cada miembro C#
    /// se traduce a su label nativo por STRING, nunca por posición ordinal), así que
    /// reordenar <see cref="MotivoStock.Remito"/> al medio del enum es un mutante GENUINAMENTE
    /// EQUIVALENTE para el round-trip de EF, confirmado empíricamente: la prueba de abajo siguió
    /// en VERDE con <c>Remito</c> movido entre <c>Ajuste</c> y <c>Transferencia</c> (revertido
    /// vía <c>git diff --stat</c> limpio después). Ningún cast ordinal de <c>MotivoStock</c>
    /// existe en el repo (`grep` confirmado) — el orden del enum C# es documentación de intención
    /// (mirrors el orden nativo de Postgres para semántica de <c>ORDER BY</c>/comparación, nunca
    /// ejercida hoy), no un invariante de round-trip. La prueba de round-trip de abajo queda como
    /// cobertura de regresión legítima (los ocho motivos pre-existentes siguen leyendo el valor
    /// sembrado), pero NO discrimina este mutante específico — la prueba que sí lo hace es la de
    /// TEXTO FUENTE inmediatamente después (mismo patrón "PROVABLY EQUIVALENT AT RUNTIME" que
    /// <c>PresupuestosSchemaTests</c> targets 4/5).</summary>
    [Fact]
    public async Task TodoMotivoPreexistenteSeLeeDeVueltaConElValorCorrectoConRemitoYaAgregadoAlTipoNativo()
    {
        var e = await SembrarEscenarioAsync(nameof(TodoMotivoPreexistenteSeLeeDeVueltaConElValorCorrectoConRemitoYaAgregadoAlTipoNativo));

        var motivos = new[]
        {
            ("venta", MotivoStock.Venta),
            ("compra", MotivoStock.Compra),
            ("anulacion", MotivoStock.Anulacion),
            ("ajuste", MotivoStock.Ajuste),
            ("transferencia", MotivoStock.Transferencia),
            ("inventario", MotivoStock.Inventario),
            ("decomiso", MotivoStock.Decomiso),
            ("reclasificacion", MotivoStock.Reclasificacion)
        };

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant))
        {
            foreach (var (literal, _) in motivos)
            {
                await using var comando = cruda.CreateCommand();
                comando.CommandText =
                    "INSERT INTO movimientos_stock " +
                    "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, observaciones, creado_el) " +
                    "VALUES ($1, $2, $3, 1, $4::motivo_stock, $5, $6, now())";
                comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
                comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });
                comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
                comando.Parameters.Add(new NpgsqlParameter { Value = literal });
                comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
                comando.Parameters.Add(new NpgsqlParameter { Value = literal });
                await comando.ExecuteNonQueryAsync();
            }
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var leidos = await db.MovimientosStock
            .Where(m => m.IdTenant == e.IdTenant)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.Equal(motivos.Length, leidos.Count);
        for (var i = 0; i < motivos.Length; i++)
        {
            Assert.Equal(motivos[i].Item2, leidos[i].Motivo);
            Assert.Equal(motivos[i].Item1, leidos[i].Observaciones);
        }
    }

    /// <summary>El discriminante real del target #39 (ver el hallazgo registrado en la prueba de
    /// arriba): texto fuente, no round-trip de EF.</summary>
    [Fact]
    public void ElTextoFuenteDeMotivoStockDeclaraRemitoUltimo()
    {
        var rutaEnum = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Domain", "Stock", "MotivoStock.cs");

        Assert.True(File.Exists(rutaEnum), $"No se encontró el enum en {rutaEnum}");

        var fuente = File.ReadAllText(rutaEnum);
        var indiceReclasificacion = fuente.IndexOf("Reclasificacion,", StringComparison.Ordinal);
        var indiceRemito = fuente.IndexOf("Remito\n", StringComparison.Ordinal);

        Assert.True(indiceReclasificacion >= 0, "No se encontró 'Reclasificacion,' en el enum.");
        Assert.True(indiceRemito >= 0, "No se encontró 'Remito' como último miembro del enum.");
        Assert.True(indiceRemito > indiceReclasificacion, "Remito tiene que declararse DESPUÉS de Reclasificacion (último miembro).");
    }

    // ---------------------------------------------------------------------------------------
    // TXR — el comprobante consolidado guardado (task 4.32, gate §I data statement 2)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LaBaseFrescaSiembraTxrActivoConAfectaStockFalse()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT activo, afecta_stock, letra, signo, es_fiscal, discrimina_iva FROM tipos_comprobante WHERE codigo = 'TXR'";
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        Assert.True(lector.GetBoolean(0)); // activo
        Assert.False(lector.GetBoolean(1)); // afecta_stock
        Assert.Equal("X", lector.GetString(2));
        Assert.Equal((short)1, lector.GetInt16(3));
        Assert.False(lector.GetBoolean(4)); // es_fiscal
        Assert.False(lector.GetBoolean(5)); // discrimina_iva
    }

    private const string MigracionAnteriorARemitosEtapa17 = "20260819195638_PresupuestosEtapa17";

    /// <summary>GATE GUARD, data statement 2 (task 4.32): una base YA MIGRADA (con
    /// <c>tipos_comprobante</c> ya poblado ANTES de esta etapa) tiene que ganar la fila
    /// <c>TXR</c> — con <c>afecta_stock = false</c> — al aplicar <c>RemitosEtapa17</c>, SIN pasar
    /// por el seeder (que nunca corre en este camino, mismo patrón que el test net-1 del
    /// <c>PRE</c> en <c>PresupuestosSchemaTests</c>).</summary>
    [Fact]
    public async Task UnaBaseYaMigradaGanaElTipoTxrAlAplicarLaMigracionDeEstaEtapa()
    {
        var nombreBase = $"ways_stage17_txr_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = new DbContextOptionsBuilder<WaysDbContext>()
                .UseNpgsql(cadenaNueva, npgsql =>
                {
                    npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                    npgsql.MapEnum<EstadoTenant>("estado_tenant");
                    npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                    npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                    npgsql.MapEnum<TipoDocumento>("tipo_documento");
                    npgsql.MapEnum<ModoLista>("modo_lista");
                    npgsql.MapEnum<UnidadVenta>("unidad_venta");
                    npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                    npgsql.MapEnum<MotivoStock>("motivo_stock");
                    npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                    npgsql.MapEnum<EstadoTurno>("estado_turno");
                    npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                    npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                    npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                    npgsql.MapEnum<EstadoCompra>("estado_compra");
                    npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                    npgsql.MapEnum<EstadoOrdenCompra>("estado_orden_compra");
                    npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                    npgsql.MapEnum<EstadoRemito>("estado_remito");
                })
                .Options;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnteriorARemitosEtapa17);
            }

            // Simula el catálogo de una base real ya operando desde antes de esta etapa: al
            // menos un tipo_comprobante existente (el guard EXISTS del data statement lo exige).
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();
                await using var comando = conexion.CreateCommand();
                comando.CommandText =
                    "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, " +
                    "es_fiscal, afecta_stock, activo, created_at, updated_at) " +
                    "VALUES ('venta', 'TX', 'Ticket X', 'X', 1, false, false, true, true, now(), now())";
                await comando.ExecuteNonQueryAsync();
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica RemitosEtapa17, la única pendiente — el seeder NUNCA corre acá
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            await using var comandoVerificar = verificacion.CreateCommand();
            comandoVerificar.CommandText = "SELECT activo, afecta_stock FROM tipos_comprobante WHERE codigo = 'TXR'";
            await using var lector = await comandoVerificar.ExecuteReaderAsync();

            Assert.True(await lector.ReadAsync());
            Assert.True(lector.GetBoolean(0));
            Assert.False(lector.GetBoolean(1));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }

    // ---------------------------------------------------------------------------------------
    // GATE GUARD — exactamente dos migraciones en toda la etapa (task 4.33)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExistenExactamenteDosMigracionesDeEstaEtapaYNingunaTercera()
    {
        var directorioMigraciones = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones");

        var archivos = Directory.GetFiles(directorioMigraciones, "*.cs")
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.EndsWith(".Designer.cs", StringComparison.Ordinal) && n.Contains("Etapa17"))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(2, archivos.Count);
        Assert.Contains(archivos, n => n.Contains("PresupuestosEtapa17"));
        Assert.Contains(archivos, n => n.Contains("RemitosEtapa17"));
    }

    // ---------------------------------------------------------------------------------------
    // Non-regression (task 4.35): stock/lotes intactos, ALTER aditivo-only
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LaColumnaIdRemitoDeMovimientosStockEsNullablePorDefectoSinRomperInsertsExistentes()
    {
        var e = await SembrarEscenarioAsync(nameof(LaColumnaIdRemitoDeMovimientosStockEsNullablePorDefectoSinRomperInsertsExistentes));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = e.IdTenant,
            IdArticulo = e.IdArticulo,
            IdPuntoVenta = e.IdPuntoVenta,
            Cantidad = -1m,
            Motivo = MotivoStock.Ajuste,
            IdEmpleado = e.IdEmpleado,
            CreadoEl = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(); // no debe tirar — id_remito queda NULL, motivo ajuste no lo toca
    }
}
