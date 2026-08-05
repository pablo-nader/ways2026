using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 1 (task 1.13, db-error-backstops, design:
/// Backstop Map): raw-SQL INSERTs que bypasean por completo <c>CalculadorDeCompra</c>/
/// <c>ServicioDeCompras</c> (Slice 2, todavía no existe) — mismo patrón que
/// <c>TurnosCajaYGastosBackstopTests</c>/<c>VentasStockBackstopTests</c>. Prueban la traducción
/// de esquema (SQLSTATE + <c>ConstraintName</c>), no un camino de cliente HTTP real.
///
/// El único caso genuinamente racy de esta slice es la unicidad parcial de
/// <c>numero_externo</c> (design: Backstop Map, superficie racy 1 la ejerce Slice 2 con el
/// servicio real; acá se prueba la carrera contra el índice desnudo, misma exención temporal que
/// <c>ux_turnos_caja_abierto</c> tuvo en stage-6 Slice 1).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasSchemaBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const int IdInexistente = 999_999;

    private sealed record Prerequisitos(
        int IdTenant, int IdProveedor, int IdPuntoVenta, int IdEmpleado, int IdArticulo,
        int IdAlicuotaIva, int IdTipoComprobanteCompra);

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

        return new Prerequisitos(
            tenant.Id, proveedor.Id, puntoVenta.Id, usuario.Id, articulo.Id, idAlicuotaIva, idTipoCompra);
    }

    private static async Task<int> InsertarComprobanteAsync(
        NpgsqlConnection cruda, Prerequisitos p, string estado, string? numeroExterno)
    {
        // fecha_comprobante/fecha_recepcion se derivan en C# (no en una CASE de SQL sobre el
        // mismo parámetro reusado): Npgsql no puede inferir el tipo de un parámetro que solo
        // aparece dentro de expresiones condicionales (42P08).
        var tieneIdentidad = numeroExterno is not null;

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, numero_externo, " +
            "fecha_comprobante, fecha_recepcion, id_punto_venta, id_empleado, subtotal, descuento_total, total, " +
            "estado, created_at, updated_at) VALUES " +
            "($1, $2, $3, $4::citext, $5, $6, $7, $8, 100, 0, 100, $9::estado_compra, now(), now()) " +
            "RETURNING id_comprobante_compra";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTipoComprobanteCompra });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)numeroExterno ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = tieneIdentidad ? DateOnly.FromDateTime(DateTime.UtcNow) : (object)DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = tieneIdentidad ? DateTimeOffset.UtcNow : (object)DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = estado });

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    // ---- ux_comprobantes_compra_numero_externo (partial UNIQUE) ------------------------------

    [Fact]
    public async Task DosComprobantesConElMismoNumeroExternoViolanLaUnicidadParcial()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosComprobantesConElMismoNumeroExternoViolanLaUnicidadParcial));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        await InsertarComprobanteAsync(cruda, p, "confirmada", "0001-00000001");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarComprobanteAsync(cruda, p, "confirmada", "0001-00000001"));

        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_comprobantes_compra_numero_externo", excepcion.ConstraintName);
    }

    /// <summary>El predicado parcial (<c>WHERE estado &lt;&gt; 'anulada'</c>) es lo que hace
    /// posible reingresar una factura mal cargada que se anuló: sin la carga parcial, esta
    /// segunda inserción también chocaría.</summary>
    [Fact]
    public async Task UnNumeroExternoDeUnComprobanteAnuladoSePuedeReingresar()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnNumeroExternoDeUnComprobanteAnuladoSePuedeReingresar));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var idOriginal = await InsertarComprobanteAsync(cruda, p, "confirmada", "0001-00000002");

        await using (var anular = cruda.CreateCommand())
        {
            anular.CommandText = "UPDATE comprobantes_compra SET estado = 'anulada'::estado_compra WHERE id_comprobante_compra = $1";
            anular.Parameters.Add(new NpgsqlParameter { Value = idOriginal });
            await anular.ExecuteNonQueryAsync();
        }

        // No debe tirar: el original está anulado, así que el predicado parcial lo excluye del
        // índice y el reingreso es una nueva fila legítima con el mismo numero_externo.
        var idReingreso = await InsertarComprobanteAsync(cruda, p, "confirmada", "0001-00000002");

        Assert.NotEqual(idOriginal, idReingreso);
    }

    /// <summary>Carrera genuina (task 1.13): dos INSERTs concurrentes del mismo
    /// <c>(proveedor, tipo, numero_externo)</c> — exactamente un ganador, el otro choca contra
    /// el índice parcial. Misma exención temporal que <c>ux_turnos_caja_abierto</c> en stage-6
    /// Slice 1: la carrera real por el camino de servicio (confirmar dos borradores con el mismo
    /// número) es tarea de Slice 2.</summary>
    [Fact]
    public async Task DosInsertsConcurrentesDelMismoNumeroExternoDanExactamenteUnGanador()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosInsertsConcurrentesDelMismoNumeroExternoDanExactamenteUnGanador));

        await using var conexionA = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var conexionB = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var tareaA = InsertarComprobanteAsync(conexionA, p, "confirmada", "0001-00000003");
        var tareaB = InsertarComprobanteAsync(conexionB, p, "confirmada", "0001-00000003");

        // Espera las dos tareas sin dejar que la primera excepción cancele la espera de la otra
        // (a diferencia de Task.WhenAll, que relanza apenas la primera falla).
        await Task.WhenAll(tareaA.ContinueWith(_ => { }), tareaB.ContinueWith(_ => { }));

        var tareas = new[] { tareaA, tareaB };
        var exitosos = tareas.Count(t => t.IsCompletedSuccessfully);
        var fallidos = tareas.Count(t => t.IsFaulted);

        Assert.Equal(1, exitosos);
        Assert.Equal(1, fallidos);

        var tareaFallida = tareas.Single(t => t.IsFaulted);
        var excepcion = Assert.IsType<PostgresException>(tareaFallida.Exception!.InnerException);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_comprobantes_compra_numero_externo", excepcion.ConstraintName);
    }

    // ---- ux_items_comprobante_compra_orden ----------------------------------------------------

    private static async Task InsertarItemAsync(NpgsqlConnection cruda, Prerequisitos p, int idComprobante, int orden)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, $3, $4, 'item de prueba', 1, 10, 0, $5, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = orden });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });
        await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DosItemsConElMismoOrdenEnLaMismaCompraViolanLaUnicidad()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosItemsConElMismoOrdenEnLaMismaCompraViolanLaUnicidad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await InsertarItemAsync(cruda, p, idComprobante, 1);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => InsertarItemAsync(cruda, p, idComprobante, 1));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_items_comprobante_compra_orden", excepcion.ConstraintName);
    }

    // ---- ck_comprobantes_compra_confirmada_completa -------------------------------------------

    [Fact]
    public async Task UnaCompraConfirmadaSinNumeroExternoViolaLaCheckDeConfirmadaCompleta()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaCompraConfirmadaSinNumeroExternoViolaLaCheckDeConfirmadaCompleta));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarComprobanteAsync(cruda, p, "confirmada", null));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_compra_confirmada_completa", excepcion.ConstraintName);
    }

    // ---- ck_comprobantes_compra_totales_no_negativos -------------------------------------------

    [Fact]
    public async Task UnaCompraConTotalNegativoViolaLaCheckDeTotalesNoNegativos()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaCompraConTotalNegativoViolaLaCheckDeTotalesNoNegativos));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, id_punto_venta, " +
            "id_empleado, subtotal, descuento_total, total, estado, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, 100, 0, -10, 'borrador', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTipoComprobanteCompra });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_compra_totales_no_negativos", excepcion.ConstraintName);
    }

    // ---- las tres CHECKs de items_comprobante_compra --------------------------------------------

    [Fact]
    public async Task UnItemConCantidadCeroViolaLaCheckDeCantidadPositiva()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConCantidadCeroViolaLaCheckDeCantidadPositiva));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 0, 10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_compra_cantidad_positiva", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemConCostoUnitarioNegativoViolaLaCheckDeCostoNoNegativo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConCostoUnitarioNegativoViolaLaCheckDeCostoNoNegativo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, -10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_compra_costo_no_negativo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemConTotalNegativoViolaLaCheckDeImportesNoNegativos()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemConTotalNegativoViolaLaCheckDeImportesNoNegativos));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, 10, 0, $4, 21, -10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_compra_importes_no_negativos", excepcion.ConstraintName);
    }

    // ---- FKs de comprobantes_compra -------------------------------------------------------------

    private async Task<PostgresException> InsertarComprobanteConFkInvalidaAsync(
        Prerequisitos p, string columna, int valorInvalido)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        var valores = new Dictionary<string, object>
        {
            ["id_proveedor"] = p.IdProveedor,
            ["id_tipo_comprobante"] = p.IdTipoComprobanteCompra,
            ["id_punto_venta"] = p.IdPuntoVenta,
            ["id_empleado"] = p.IdEmpleado
        };
        valores[columna] = valorInvalido;

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, id_punto_venta, " +
            "id_empleado, subtotal, descuento_total, total, estado, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, 100, 0, 100, 'borrador', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = valores["id_proveedor"] });
        comando.Parameters.Add(new NpgsqlParameter { Value = valores["id_tipo_comprobante"] });
        comando.Parameters.Add(new NpgsqlParameter { Value = valores["id_punto_venta"] });
        comando.Parameters.Add(new NpgsqlParameter { Value = valores["id_empleado"] });

        return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task UnProveedorInexistenteViolaLaFkDeProveedor()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnProveedorInexistenteViolaLaFkDeProveedor));
        var excepcion = await InsertarComprobanteConFkInvalidaAsync(p, "id_proveedor", IdInexistente);
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_proveedor", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnPuntoVentaInexistenteViolaLaFkDePuntoVenta()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnPuntoVentaInexistenteViolaLaFkDePuntoVenta));
        var excepcion = await InsertarComprobanteConFkInvalidaAsync(p, "id_punto_venta", IdInexistente);
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_punto_venta", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnEmpleadoInexistenteViolaLaFkDeEmpleado()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnEmpleadoInexistenteViolaLaFkDeEmpleado));
        var excepcion = await InsertarComprobanteConFkInvalidaAsync(p, "id_empleado", IdInexistente);
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_empleado", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnTipoDeComprobanteInexistenteViolaLaFkDeTipoComprobante()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTipoDeComprobanteInexistenteViolaLaFkDeTipoComprobante));
        var excepcion = await InsertarComprobanteConFkInvalidaAsync(p, "id_tipo_comprobante", IdInexistente);
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_tipo_comprobante", excepcion.ConstraintName);
    }

    /// <summary>El simple FK de <c>id_tenant</c> no se puede aislar de las FKs compuestas de
    /// <c>proveedor</c>/<c>punto_venta</c> (ambas también referencian <c>id_tenant</c>): con un
    /// tenant apócrifo, Postgres valida las constraints en el orden en que se declararon
    /// (alfabético en esta migración) y <c>fk_comprobantes_compra_proveedor</c> cae ANTES que
    /// <c>fk_comprobantes_compra_tenant</c>. Se prueba entonces que ALGUNA <c>fk_</c> nueva de
    /// esta tabla dispara — que es exactamente lo que el backstop genérico necesita, sin
    /// importar cuál de las FKs compuestas ganó la carrera de validación.</summary>
    [Fact]
    public async Task UnTenantInexistenteViolaAlgunaFkDeComprobante()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTenantInexistenteViolaAlgunaFkDeComprobante));

        // id_tenant apócrifo: usa una conexión de plataforma (sin RLS de por medio) para
        // aislar la FK del backstop de WITH CHECK (RLS), que ya prueba ComprasSchemaRlsTests.
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, id_punto_venta, " +
            "id_empleado, subtotal, descuento_total, total, estado, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, 100, 0, 100, 'borrador', now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdProveedor });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTipoComprobanteCompra });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.StartsWith("fk_comprobantes_compra_", excepcion.ConstraintName);
    }

    // ---- FKs de items_comprobante_compra ---------------------------------------------------------

    [Fact]
    public async Task UnArticuloInexistenteViolaLaFkDeArticuloDeItem()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnArticuloInexistenteViolaLaFkDeArticuloDeItem));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, 10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_compra_articulo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaAlicuotaInexistenteViolaLaFkDeAlicuotaDeItem()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaAlicuotaInexistenteViolaLaFkDeAlicuotaDeItem));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, 10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_compra_alicuota_iva", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnComprobanteInexistenteViolaLaFkDeComprobanteDeItem()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnComprobanteInexistenteViolaLaFkDeComprobanteDeItem));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, 10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_compra_comprobante", excepcion.ConstraintName);
    }

    /// <summary>Mismo motivo que <c>UnTenantInexistenteViolaAlgunaFkDeComprobante</c>: el simple
    /// FK de <c>id_tenant</c> no se puede aislar de las FKs compuestas de <c>comprobante</c>/
    /// <c>articulo</c> (ambas también referencian <c>id_tenant</c>) — Postgres valida en orden
    /// alfabético de declaración, y <c>fk_items_comprobante_compra_articulo</c> cae primero.</summary>
    [Fact]
    public async Task UnTenantInexistenteViolaAlgunaFkDeItem()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnTenantInexistenteViolaAlgunaFkDeItem));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idComprobante = await InsertarComprobanteAsync(cruda, p, "borrador", null);

        // id_tenant apócrifo del ÍTEM: conexión de plataforma para aislar del backstop de WITH
        // CHECK (RLS), que ya prueba ComprasSchemaRlsTests.
        await using var crudaPlataforma = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = crudaPlataforma.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "created_at, updated_at) VALUES ($1, $2, 1, $3, 'item', 1, 10, 0, $4, 21, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.StartsWith("fk_items_comprobante_compra_", excepcion.ConstraintName);
    }

    // ---- Las dos FKs diferidas: movimientos_stock/gastos → comprobantes_compra ------------------

    [Fact]
    public async Task UnMovimientoDeStockConComprobanteDeCompraInexistenteViolaLaFkDiferida()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeStockConComprobanteDeCompraInexistenteViolaLaFkDiferida));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_stock (id_tenant, id_articulo, id_punto_venta, cantidad, motivo, " +
            "id_comprobante_compra, id_empleado, creado_el) " +
            "VALUES ($1, $2, $3, 5, 'compra', $4, $5, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_stock_comprobante_compra", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnGastoConComprobanteDeCompraInexistenteViolaLaFkDiferida()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnGastoConComprobanteDeCompraInexistenteViolaLaFkDiferida));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var medioPago = new MedioPago
        {
            IdTenant = p.IdTenant,
            Nombre = "efectivo",
            Orden = 1,
            Comportamiento = ComportamientoMedioPago.Efectivo,
            AdmiteVuelto = true,
            RequiereReferencia = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        var turno = new TurnoCaja
        {
            IdTenant = p.IdTenant,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleadoApertura = p.IdEmpleado,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO gastos (id_tenant, fecha, id_punto_venta, id_turno_caja, id_empleado, categoria, " +
            "concepto, id_medio_pago, importe, id_comprobante_compra, created_at, updated_at) " +
            "VALUES ($1, now(), $2, $3, $4, 'proveedor', 'pago de prueba', $5, 10, $6, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = turno.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = medioPago.Id });
        comando.Parameters.Add(new NpgsqlParameter { Value = IdInexistente });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_gastos_comprobante_compra", excepcion.ConstraintName);
    }
}
