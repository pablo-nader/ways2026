using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-12-lotes-vencimientos, Slice 1 (tasks 1.20-1.23, db-error-backstops, design decisión
/// 5): raw-SQL INSERTs que bypasean por completo <c>ServicioDeLotes</c> (no existe todavía,
/// Slice 3) — mismo patrón que <c>ComprasSchemaBackstopTests</c>/<c>VentasStockBackstopTests</c>.
/// Prueban la traducción de esquema (SQLSTATE + <c>ConstraintName</c>), no un camino de cliente
/// HTTP real — honesto sobre alcanzabilidad: bajo operación normal (Slice 3 en adelante) el
/// get-or-create serializa sobre <c>ux_lotes_articulo_codigo</c>, así que estas ramas quedan
/// como backstop de una escritura cruda/fuera de banda o de una carrera genuina en el alta admin
/// (<c>POST /api/stock/lotes</c>, Slice 3).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class LotesBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Prerequisitos(
        int IdTenant, int IdPuntoVenta, int IdArticuloA, int IdArticuloB, int IdEmpleado,
        int IdCliente, int IdProveedor, int IdListaPrecio, int IdAlicuotaIva, int IdArea,
        int IdTipoComprobanteTx, int IdTipoComprobanteCompra);

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

        var articuloA = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-A",
            Nombre = $"{nombre}-A",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            ControlaLote = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articuloA);
        await db.SaveChangesAsync();

        var articuloB = new Articulo
        {
            IdTenant = tenant.Id,
            CodigoInterno = $"{nombre}-B",
            Nombre = $"{nombre}-B",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            ControlaLote = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articuloB);
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

        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
        var idTipoComprobanteCompra = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Prerequisitos(
            tenant.Id, puntoVenta.Id, articuloA.Id, articuloB.Id, usuario.Id, cliente.Id, proveedor.Id,
            listaPrecio.Id, idAlicuotaIva, area.Id, idTipoComprobanteTx, idTipoComprobanteCompra);
    }

    /// <summary>Crea un lote fechado (no sin-identificar) para <see cref="Prerequisitos.IdArticuloA"/>
    /// vía EF — sin pasar por <c>ServicioDeLotes</c>, que no existe todavía.</summary>
    private async Task<int> SembrarLoteDeArticuloAAsync(Prerequisitos p, string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var lote = new Lote
        {
            IdTenant = p.IdTenant,
            IdArticulo = p.IdArticuloA,
            Codigo = codigo,
            FechaVencimiento = new DateOnly(2026, 12, 31),
            EsSinIdentificar = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync();
        return lote.Id;
    }

    private async Task<int> SembrarComprobanteVentaAsync(Prerequisitos p)
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

    private async Task<int> SembrarComprobanteCompraAsync(Prerequisitos p)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteCompra
        {
            IdTenant = p.IdTenant,
            IdProveedor = p.IdProveedor,
            IdTipoComprobante = p.IdTipoComprobanteCompra,
            IdPuntoVenta = p.IdPuntoVenta,
            IdEmpleado = p.IdEmpleado,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoCompra.Borrador,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    // ---- ux_lotes_articulo_codigo: carrera genuina (task 1.20) --------------------------------

    [Fact]
    public async Task DosLotesConcurrentesDelMismoArticuloYCodigoDanExactamenteUnGanador()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosLotesConcurrentesDelMismoArticuloYCodigoDanExactamenteUnGanador));

        await using var conexionA = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var conexionB = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarAsync(NpgsqlConnection cx)
        {
            await using var comando = cx.CreateCommand();
            comando.CommandText =
                "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
                "created_at, updated_at) VALUES ($1, $2, 'L-CARRERA', $3, false, now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloA });
            comando.Parameters.Add(new NpgsqlParameter { Value = new DateOnly(2026, 12, 31) });
            await comando.ExecuteNonQueryAsync();
        }

        var tareaA = InsertarAsync(conexionA);
        var tareaB = InsertarAsync(conexionB);

        // Espera las dos tareas sin dejar que la primera excepción cancele la espera de la
        // otra (a diferencia de Task.WhenAll, que relanza apenas la primera falla).
        await Task.WhenAll(tareaA.ContinueWith(_ => { }), tareaB.ContinueWith(_ => { }));

        var tareas = new[] { tareaA, tareaB };
        Assert.Equal(1, tareas.Count(t => t.IsCompletedSuccessfully));
        Assert.Equal(1, tareas.Count(t => t.IsFaulted));

        var tareaFallida = tareas.Single(t => t.IsFaulted);
        var excepcion = Assert.IsType<PostgresException>(tareaFallida.Exception!.InnerException);
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_lotes_articulo_codigo", excepcion.ConstraintName);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var sobrevivientes = await db.Lotes.CountAsync(l => l.IdArticulo == p.IdArticuloA && l.Codigo == "L-CARRERA");
        Assert.Equal(1, sobrevivientes);
    }

    // ---- ux_lotes_sin_identificar: exención documentada, raw-SQL directo (task 1.21) ----------

    [Fact]
    public async Task UnSegundoLoteSinIdentificarDelMismoArticuloViolaLaUnicidadParcial()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnSegundoLoteSinIdentificarDelMismoArticuloViolaLaUnicidadParcial));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);

        async Task InsertarSinIdentificarAsync(string codigo)
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText =
                "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
                "created_at, updated_at) VALUES ($1, $2, $3, NULL, true, now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloA });
            comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
            await comando.ExecuteNonQueryAsync();
        }

        await InsertarSinIdentificarAsync("SIN-IDENTIFICAR");

        // Código distinto a propósito: lo que se prueba acá es ux_lotes_sin_identificar
        // (es_sin_identificar), no ux_lotes_articulo_codigo.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => InsertarSinIdentificarAsync("SIN-IDENTIFICAR-2"));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_lotes_sin_identificar", excepcion.ConstraintName);
    }

    // ---- las cuatro FKs de coherencia lote/artículo (task 1.22) --------------------------------

    [Fact]
    public async Task UnMovimientoDeStockReferenciandoElLoteDeUnArticuloAjenoViolaLaFk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoDeStockReferenciandoElLoteDeUnArticuloAjenoViolaLaFk));
        var idLoteDeA = await SembrarLoteDeArticuloAAsync(p, "L-001");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_stock (id_tenant, id_articulo, id_punto_venta, cantidad, motivo, " +
            "id_lote, id_empleado, creado_el) VALUES ($1, $2, $3, 5, 'ajuste', $4, $5, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloB });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLoteDeA });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_stock_lote", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemDeVentaReferenciandoElLoteDeUnArticuloAjenoViolaLaFk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemDeVentaReferenciandoElLoteDeUnArticuloAjenoViolaLaFk));
        var idLoteDeA = await SembrarLoteDeArticuloAAsync(p, "L-002");
        var idComprobante = await SembrarComprobanteVentaAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_venta (id_tenant, id_comprobante_venta, orden, id_articulo, " +
            "descripcion, id_area, id_lista_precio, id_alicuota_iva, porcentaje_iva, cantidad, " +
            "precio_unitario, descuento, total, id_lote, created_at, updated_at) " +
            "VALUES ($1, $2, 1, $3, 'item-lote-ajeno', $4, $5, $6, 21, 1, 100, 0, 100, $7, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloB });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArea });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLoteDeA });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_venta_lote", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemDeCompraReferenciandoElLoteDeUnArticuloAjenoViolaLaFk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemDeCompraReferenciandoElLoteDeUnArticuloAjenoViolaLaFk));
        var idLoteDeA = await SembrarLoteDeArticuloAAsync(p, "L-003");
        var idComprobante = await SembrarComprobanteCompraAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "id_lote, created_at, updated_at) " +
            "VALUES ($1, $2, 1, $3, 'item-lote-ajeno', 1, 10, 0, $4, 21, 10, $5, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloB });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLoteDeA });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_compra_lote", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnStockLoteReferenciandoElLoteDeUnArticuloAjenoViolaLaFk()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnStockLoteReferenciandoElLoteDeUnArticuloAjenoViolaLaFk));
        var idLoteDeA = await SembrarLoteDeArticuloAAsync(p, "L-004");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, 0)";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloB });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLoteDeA });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_stock_lotes_lote", excepcion.ConstraintName);
    }

    // ---- las tres CHECKs de esta slice (task 1.23) ---------------------------------------------

    [Fact]
    public async Task UnLoteFechadoSinVencimientoViolaLaCheckDeVencimientoSegunTipo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnLoteFechadoSinVencimientoViolaLaCheckDeVencimientoSegunTipo));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
            "created_at, updated_at) VALUES ($1, $2, 'L-SIN-FECHA', NULL, false, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloA });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_lotes_vencimiento_segun_tipo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnLoteConCodigoEnBlancoViolaLaCheckDeCodigoNoVacio()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnLoteConCodigoEnBlancoViolaLaCheckDeCodigoNoVacio));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
            "created_at, updated_at) VALUES ($1, $2, '   ', $3, false, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = new DateOnly(2026, 12, 31) });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_lotes_codigo_no_vacio", excepcion.ConstraintName);
    }

    /// <summary>judgment-day (slice 5, FIX 1b): esta CHECK ahora tiene mapeo HTTP —
    /// <c>ManejadorDeErrores.ClasificarCheckDeCompras</c> traduce <c>ck_items_comprobante_compra_lote_input</c>
    /// a 400 <c>lote_input_incompleto</c>, backstop del guard primario de
    /// <c>ServicioDeCompras.ValidarVencimientosDeRecepcion</c> (FIX 1a). Esta prueba sigue siendo
    /// SQL crudo a propósito — verifica la CHECK de esquema en sí (SQLSTATE + ConstraintName), no
    /// el mapeo HTTP; la prueba end-to-end del mapeo vive en
    /// ComprasRecepcionDeLotesTests.CrearBorradorConCodigoDeLoteSinFechaDeVencimientoDa400LoteInputIncompletoNunca500.</summary>
    [Fact]
    public async Task UnItemDeCompraConCodigoDeLoteSinVencimientoViolaLaCheckDeLoteInput()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnItemDeCompraConCodigoDeLoteSinVencimientoViolaLaCheckDeLoteInput));
        var idComprobante = await SembrarComprobanteCompraAsync(p);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_comprobante_compra (id_tenant, id_comprobante_compra, orden, id_articulo, " +
            "descripcion, cantidad, costo_unitario, descuento, id_alicuota_iva, porcentaje_iva, total, " +
            "codigo_lote, fecha_vencimiento, created_at, updated_at) " +
            "VALUES ($1, $2, 1, $3, 'item-lote-input-invalido', 1, 10, 0, $4, 21, 10, 'L-005', NULL, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdArticuloA });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_comprobante_compra_lote_input", excepcion.ConstraintName);
    }
}
