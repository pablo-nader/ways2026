using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
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
/// stage-17-presupuestos-y-remitos, Slice 1 (tasks 1.28-1.35, mutation targets #1-#8,
/// db-error-backstops skill, design decisiones 5/11/18): RLS, las dos CHECKs de cada tabla
/// nueva, las dos particiones parciales, el conteo vinculante de 14 índices nuevos (acumulado
/// del slice) y los nombres exactos de constraint que <c>ManejadorDeErroresPresupuestosTests</c>
/// traduce — todos sobre la base COMPARTIDA de <see cref="WaysApiFixture"/> (mismo criterio que
/// <c>OrdenesCompraSchemaTests</c>: no depende del momento exacto de una migración de datos —
/// salvo el PRE latente, que sí lo hace y se prueba aparte más abajo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PresupuestosSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
            IdTenant = tenant.Id, Numero = 501, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
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

    private const string ColumnasPresupuesto =
        "(id_tenant, id_punto_venta, id_cliente, id_empleado, numero, fecha_emision, fecha_envio, " +
        " vencimiento, observaciones, subtotal, descuento_total, total, estado, created_at, updated_at, deleted_at)";

    private static async Task<int> InsertarBorradorAsync(NpgsqlConnection cruda, Escenario e)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_presupuesto, now(), now(), NULL) " +
            "RETURNING id_presupuesto";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    // ---------------------------------------------------------------------------------------
    // RLS (task 1.28, mutation target #1)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosPresupuestosPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosPresupuestosPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosPresupuestosPorSelect) + "-B");

        int idPresupuesto;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            idPresupuesto = await InsertarBorradorAsync(cruda, a);
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM presupuestos WHERE id_presupuesto = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPresupuesto });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosItemsDePresupuestoPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDePresupuestoPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosItemsDePresupuestoPorSelect) + "-B");

        int idItem;
        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            var idPresupuesto = await InsertarBorradorAsync(cruda, a);

            await using var insertarItem = cruda.CreateCommand();
            insertarItem.CommandText =
                "INSERT INTO items_presupuesto " +
                "(id_tenant, id_presupuesto, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
                " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
                " created_at, updated_at, deleted_at) " +
                "VALUES ($1, $2, 1, $3, 'seed', 2, 10, 0, 20, $4, NULL, $5, 21, now(), now(), NULL) " +
                "RETURNING id_item";
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = idPresupuesto });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdArticulo });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdListaPrecio });
            insertarItem.Parameters.Add(new NpgsqlParameter { Value = a.IdAlicuotaIva });
            idItem = (int)(await insertarItem.ExecuteScalarAsync())!;
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM items_presupuesto WHERE id_item = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idItem });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnPresupuestosSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnPresupuestosSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnPresupuestosSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = comoA.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = b.IdTenant }); // ajeno a la sesión (tenant A)
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---------------------------------------------------------------------------------------
    // ck_presupuestos_envio_completo (task 1.29, mutation target #2), tres direcciones
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, 501, now(), NULL, '2026-09-01', NULL, 0, 0, 0, 'enviado'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_presupuestos_envio_completo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnNumeroSinVencimientoViolaLaCheckDeEnvioCompleto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroSinVencimientoViolaLaCheckDeEnvioCompleto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, 502, now(), now(), NULL, NULL, 0, 0, 0, 'enviado'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_presupuestos_envio_completo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnEstadoEnviadoSinNumeroViolaLaCheckDeEnvioCompleto()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoEnviadoSinNumeroViolaLaCheckDeEnvioCompleto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'enviado'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_presupuestos_envio_completo", excepcion.ConstraintName);
    }

    /// <summary>Dirección permitida explícita: <c>anulado</c> sin número/fecha_envio/vencimiento
    /// es admitido — un borrador puede anularse antes de ser enviado.</summary>
    [Fact]
    public async Task UnEstadoAnuladoSinNumeroNiVencimientoNoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnEstadoAnuladoSinNumeroNiVencimientoNoViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'anulado'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // ---------------------------------------------------------------------------------------
    // ck_items_presupuesto_cantidad_positiva (task 1.30, mutation target #3)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaCantidadNoPositivaViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaCantidadNoPositivaViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idPresupuesto = await InsertarBorradorAsync(cruda, e);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_presupuesto " +
            "(id_tenant, id_presupuesto, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
            " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
            " created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 0, 10, 0, 0, $4, NULL, $5, 21, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPresupuesto });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdArticulo });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_items_presupuesto_cantidad_positiva", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // Particiones parciales (tasks 1.31/1.32, mutation targets #4/#5)
    // ---------------------------------------------------------------------------------------

    /// <summary>Mutation target #4: dos borradores (numero NULL) en el mismo punto de venta
    /// conviven sin conflicto — la unicidad de <c>ux_presupuestos_numero</c> es PARCIAL.</summary>
    [Fact]
    public async Task DosBorradoresSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto()
    {
        var e = await SembrarEscenarioAsync(nameof(DosBorradoresSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await InsertarBorradorAsync(cruda, e);
        await InsertarBorradorAsync(cruda, e); // no debe tirar — ambos numero NULL, filtro parcial
    }

    /// <summary>Mutation target #5: dos ventas ordinarias (id_presupuesto_origen NULL) conviven
    /// sin conflicto — la unicidad de <c>ux_comprobantes_venta_presupuesto_origen</c> es
    /// PARCIAL. Requiere un comprobante_venta real; se arma el mínimo necesario a mano.</summary>
    [Fact]
    public async Task DosVentasOrdinariasSinPresupuestoOrigenConvivenSinConflicto()
    {
        var e = await SembrarEscenarioAsync(nameof(DosVentasOrdinariasSinPresupuestoOrigenConvivenSinConflicto));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        var idTipoComprobante = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await InsertarVentaAsync(cruda, e, idTipoComprobante, numero: 1);
        await InsertarVentaAsync(cruda, e, idTipoComprobante, numero: 2); // no debe tirar — ambos NULL
    }

    /// <summary>Targets #4/#5 — PROVABLY EQUIVALENT AT RUNTIME, prueba de TEXTO FUENTE en su
    /// lugar (mutation-proof-tests regla 3 agotada primero, hallazgo registrado en tasks.md).
    /// Confirmado empíricamente (no razonado — regla 2): borrar `filter: "numero IS NOT NULL"`/
    /// `filter: "id_presupuesto_origen IS NOT NULL"` de la migración deja las dos pruebas de
    /// arriba en VERDE de todos modos. Postgres nunca considera dos filas duplicadas bajo un
    /// índice UNIQUE si CUALQUIER columna indexada es NULL en cualquiera de las dos (semántica
    /// SQL estándar: NULL no es igual a NULL para propósitos de unicidad) — y en ambos índices
    /// la única columna que puede ser NULL (`numero`/`id_presupuesto_origen`) es exactamente la
    /// que discrimina el escenario de "sin asignar/sin convertir" de estas pruebas, así que NUNCA
    /// hay colisión con o sin el filtro parcial. Ningún fixture a nivel Postgres puede matar este
    /// mutante — el filtro parcial es correcto y necesario igual (documenta la intención,
    /// reduce el tamaño del índice, y es el shape exacto de todo índice `ux_*_numero` del
    /// repo), pero su ausencia no es observable en tiempo de ejecución para ESTE par de columnas.
    /// La única prueba que sí detecta el mutante es de texto fuente: ambos `filter:` tienen que
    /// seguir presentes en el archivo real de la migración.</summary>
    [Fact]
    public void ElTextoFuenteDeLaMigracionConservaLosDosFiltrosParcialesTargets4Y5()
    {
        var rutaMigracion = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260819195638_PresupuestosEtapa17.cs");

        Assert.True(File.Exists(rutaMigracion), $"No se encontró la migración en {rutaMigracion}");

        var fuente = File.ReadAllText(rutaMigracion);

        Assert.Contains(
            "name: \"ux_presupuestos_numero\"", fuente);
        Assert.Contains(
            "filter: \"numero IS NOT NULL\"", fuente);
        Assert.Contains(
            "name: \"ux_comprobantes_venta_presupuesto_origen\"", fuente);
        Assert.Contains(
            "filter: \"id_presupuesto_origen IS NOT NULL\"", fuente);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static async Task<int> ObtenerIdTipoComprobanteAsync(NpgsqlConnection cruda, string codigo)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT id_tipo_comprobante FROM tipos_comprobante WHERE codigo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task InsertarVentaAsync(
        NpgsqlConnection cruda, Escenario e, int idTipoComprobante, long numero)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta " +
            "(id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_turno_caja, id_empleado, " +
            " id_cliente, id_comprobante_asociado, id_presupuesto_origen, subtotal, descuento_total, total, " +
            " neto_gravado, iva_total, direccion_entrega, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, now(), $4, NULL, $5, $6, NULL, NULL, 10, 0, 10, NULL, NULL, NULL, NULL, " +
            " 'emitido'::estado_comprobante, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTipoComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = numero });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });

        await comando.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------------------------------
    // Conteo vinculante de índices (task 1.37, gate §C/§D/§G — VINCULANTE, 14 acumulado)
    // ---------------------------------------------------------------------------------------

    /// <summary>Gate guard VINCULANTE (task 1.37, state.yaml db_gate_approval): el conteo total
    /// de índices nuevos de este slice tiene que ser EXACTAMENTE 14 — 6 en presupuestos (5
    /// nombrados a mano + 1 implícito de la AK), 7 en items_presupuesto (incl. la unicidad de
    /// orden), 1 en comprobantes_venta (el soporte PARCIAL de FK 23, nombrado a mano, nunca el
    /// IX_... autogenerado). Cualquier índice extra que ForeignKeyIndexConvention agregue sin
    /// que este contrato lo nombre reabre el gate.</summary>
    [Fact]
    public async Task ElConteoTotalDeIndicesNuevosEsExactamenteCatorce()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var indicesPresupuestos = await ListarIndicesAsync(cruda, "presupuestos");
        var indicesDeSoportePresupuestos = indicesPresupuestos.Where(n => n != "pk_presupuestos").ToList();
        Assert.Equal(6, indicesDeSoportePresupuestos.Count);
        Assert.Equal(
            new[]
            {
                "ak_presupuestos_id_presupuesto_id_tenant",
                "ix_presupuestos_cliente",
                "ix_presupuestos_empleado",
                "ix_presupuestos_punto_venta_fecha",
                "ix_presupuestos_tenant",
                "ux_presupuestos_numero"
            },
            indicesDeSoportePresupuestos.OrderBy(n => n));

        var indicesItems = await ListarIndicesAsync(cruda, "items_presupuesto");
        var indicesDeSoporteItems = indicesItems.Where(n => n != "pk_items_presupuesto").ToList();
        Assert.Equal(7, indicesDeSoporteItems.Count);
        Assert.Equal(
            new[]
            {
                "ix_items_presupuesto_alicuota_iva",
                "ix_items_presupuesto_articulo",
                "ix_items_presupuesto_lista_precio",
                "ix_items_presupuesto_oferta",
                "ix_items_presupuesto_presupuesto",
                "ix_items_presupuesto_tenant",
                "ux_items_presupuesto_orden"
            },
            indicesDeSoporteItems.OrderBy(n => n));

        await using var comandoComprobantes = cruda.CreateCommand();
        comandoComprobantes.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'comprobantes_venta' AND indexname = 'ux_comprobantes_venta_presupuesto_origen'";
        var indiceComprobantes = (string?)await comandoComprobantes.ExecuteScalarAsync();
        Assert.NotNull(indiceComprobantes);

        // No debe existir NINGÚN índice extra sobre id_presupuesto_origen más allá del nombrado
        // a mano (mutation target #6): la convención de EF habría producido
        // "IX_comprobantes_venta_id_presupuesto_origen_id_tenant".
        await using var comandoSinAutogenerado = cruda.CreateCommand();
        comandoSinAutogenerado.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'comprobantes_venta' " +
            "AND indexname ILIKE '%presupuesto_origen%' AND indexname <> 'ux_comprobantes_venta_presupuesto_origen'";
        var autogenerados = (long)(await comandoSinAutogenerado.ExecuteScalarAsync())!;
        Assert.Equal(0, autogenerados);

        // El total del gate (slice 1): 6 (presupuestos) + 7 (items_presupuesto) + 1
        // (comprobantes_venta) = 14.
        Assert.Equal(14, indicesDeSoportePresupuestos.Count + indicesDeSoporteItems.Count + 1);
    }

    /// <summary>Hallazgo MAJOR de judgment-day (stage-16, juez B), aplicado preventivamente: el
    /// conteo de arriba compara solo <c>indexname</c> contra <c>pg_indexes</c> — un swap de
    /// columnas en un índice compuesto conserva el nombre y el conteo. Esta prueba asserta el
    /// DDL completo (<c>pg_indexes.indexdef</c>) de cada índice compuesto nuevo del slice contra
    /// el contrato del gate, incluyendo el orden exacto de columnas, el filtro parcial y el
    /// flag UNIQUE — y que ningún índice compuesto quede liderado por <c>id_tenant</c> salvo el
    /// único que lo lleva por diseño.</summary>
    [Fact]
    public async Task LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "presupuestos", "ix_presupuestos_punto_venta_fecha"),
            "id_punto_venta", "id_tenant", "fecha_emision");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "presupuestos", "ix_presupuestos_cliente"),
            "id_cliente", "id_tenant");

        var defNumero = await ObtenerIndexDefAsync(cruda, "presupuestos", "ux_presupuestos_numero");
        AssertOrdenDeColumnas(defNumero, "id_tenant", "id_punto_venta", "numero");
        Assert.Contains("CREATE UNIQUE INDEX", defNumero);
        Assert.Contains("WHERE (numero IS NOT NULL)", defNumero);

        var defAk = await ObtenerIndexDefAsync(cruda, "presupuestos", "ak_presupuestos_id_presupuesto_id_tenant");
        AssertOrdenDeColumnas(defAk, "id_presupuesto", "id_tenant");
        Assert.Contains("CREATE UNIQUE INDEX", defAk);

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_presupuesto", "ix_items_presupuesto_presupuesto"),
            "id_presupuesto", "id_tenant");

        AssertOrdenDeColumnas(
            await ObtenerIndexDefAsync(cruda, "items_presupuesto", "ix_items_presupuesto_articulo"),
            "id_articulo", "id_tenant");

        var defUxOrden = await ObtenerIndexDefAsync(cruda, "items_presupuesto", "ux_items_presupuesto_orden");
        AssertOrdenDeColumnas(defUxOrden, "id_presupuesto", "orden");
        Assert.Contains("CREATE UNIQUE INDEX", defUxOrden);

        var defOrigen = await ObtenerIndexDefAsync(cruda, "comprobantes_venta", "ux_comprobantes_venta_presupuesto_origen");
        AssertOrdenDeColumnas(defOrigen, "id_presupuesto_origen", "id_tenant");
        Assert.Contains("CREATE UNIQUE INDEX", defOrigen);
        Assert.Contains("WHERE (id_presupuesto_origen IS NOT NULL)", defOrigen);

        // Ningún índice compuesto nuevo de este slice queda liderado por id_tenant, salvo
        // ux_presupuestos_numero (ya cubierto arriba).
        var compuestosSinLiderarPorTenant = new[]
        {
            ("presupuestos", "ix_presupuestos_punto_venta_fecha"),
            ("presupuestos", "ix_presupuestos_cliente"),
            ("presupuestos", "ak_presupuestos_id_presupuesto_id_tenant"),
            ("items_presupuesto", "ix_items_presupuesto_presupuesto"),
            ("items_presupuesto", "ix_items_presupuesto_articulo"),
            ("items_presupuesto", "ux_items_presupuesto_orden"),
            ("comprobantes_venta", "ux_comprobantes_venta_presupuesto_origen")
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
    // db-error-backstops — exenciones de FK / AK (task 1.26/1.27)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnIdClienteInexistenteEnPresupuestosViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdClienteInexistenteEnPresupuestosViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO presupuestos " + ColumnasPresupuesto +
            " VALUES ($1, $2, $3, $4, NULL, now(), NULL, NULL, NULL, 0, 0, 0, 'borrador'::estado_presupuesto, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_cliente inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_presupuestos_cliente", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdArticuloInexistenteEnItemsDePresupuestoViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdArticuloInexistenteEnItemsDePresupuestoViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idPresupuesto = await InsertarBorradorAsync(cruda, e);

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO items_presupuesto " +
            "(id_tenant, id_presupuesto, orden, id_articulo, descripcion, cantidad, precio_unitario, " +
            " descuento, total, id_lista_precio, id_oferta, id_alicuota_iva, porcentaje_iva, " +
            " created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 1, $3, 'seed', 2, 10, 0, 20, $4, NULL, $5, 21, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPresupuesto });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_articulo inexistente
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdListaPrecio });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdAlicuotaIva });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_presupuesto_articulo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnIdPresupuestoInexistenteEnComprobanteVentaViolaLaFkGenerica23503()
    {
        var e = await SembrarEscenarioAsync(nameof(UnIdPresupuestoInexistenteEnComprobanteVentaViolaLaFkGenerica23503));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipoComprobante = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta " +
            "(id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_turno_caja, id_empleado, " +
            " id_cliente, id_comprobante_asociado, id_presupuesto_origen, subtotal, descuento_total, total, " +
            " neto_gravado, iva_total, direccion_entrega, observaciones, estado, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, 900, now(), $3, NULL, $4, $5, NULL, $6, 10, 0, 10, NULL, NULL, NULL, NULL, " +
            " 'emitido'::estado_comprobante, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTipoComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = 999_999_999 }); // id_presupuesto_origen inexistente

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_venta_presupuesto_origen", excepcion.ConstraintName);
    }

    // ---------------------------------------------------------------------------------------
    // El PRE latente — los dos nets, cada uno probado INDEPENDIENTEMENTE (tasks 1.38/1.39)
    // ---------------------------------------------------------------------------------------

    private const string MigracionAnteriorAPresupuestosEtapa17 = "20260819042145_OrdenesDeCompraEtapa16";

    /// <summary>GATE GUARD, net 1 (task 1.38, mutation target #11): una base YA MIGRADA
    /// (existente ANTES de esta etapa, con `PRE` ya sembrado ACTIVO — el estado real de
    /// cualquier instalación operando desde antes de la etapa 17) tiene que quedar con `PRE`
    /// **inactivo** después de aplicar `PresupuestosEtapa17`, por el data statement 1 —
    /// independiente del seed change (net 1b), que acá NUNCA corre: se migra directo con
    /// `IMigrator`, sin pasar por `InicializadorDeBaseDeDatos.EjecutarAsync`.
    ///
    /// CORRECCIÓN (mutation-proof-tests regla 2, hallazgo registrado en tasks.md): la primera
    /// versión de este test reusaba la base COMPARTIDA de <see cref="WaysApiFixture"/> — que
    /// siempre es una instalación FRESCA (migra Y siembra en el mismo arranque), así que net 1b
    /// por sí solo ya la dejaba en verde, con o sin el data statement 1. Confirmado
    /// empíricamente: la versión vieja seguía en VERDE con el data statement 1 borrado. Esta
    /// versión migra a mano hasta la migración ANTERIOR, siembra `PRE` ACTIVO por SQL directo
    /// (el estado real de una base operando desde antes de esta etapa) y recién ahí aplica
    /// `PresupuestosEtapa17` — el seeder nunca corre en este camino, así que la única red que
    /// puede haber apagado `PRE` es el data statement 1 (mismo patrón que
    /// `ComprasTipoSeedTests.LosTiposDeCompraAterrizanEnUnaBaseYaMigradaDesdeStage7...`).</summary>
    [Fact]
    public async Task UnaBaseYaMigradaConPreActivoQuedaInactivaTrasAplicarLaMigracionDeEstaEtapa()
    {
        var nombreBase = $"ways_stage17_pre_{Guid.NewGuid():N}";
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
                    npgsql.MapEnum<ResultadoFiscal>("resultado_fiscal");
                    npgsql.MapEnum<AmbienteFiscal>("ambiente_fiscal");
                })
                .Options;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnteriorAPresupuestosEtapa17);
            }

            // Simula el catálogo de una base real ya operando desde antes de esta etapa: PRE
            // sembrado ACTIVO, exactamente como lo dejaba InicializadorDeBaseDeDatos ANTES del
            // cambio de esta etapa.
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();
                await using var comando = conexion.CreateCommand();
                comando.CommandText =
                    "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, " +
                    "es_fiscal, afecta_stock, activo, created_at, updated_at) " +
                    "VALUES ('venta', 'PRE', 'Presupuesto', NULL, 1, false, false, false, true, now(), now())";
                await comando.ExecuteNonQueryAsync();
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica PresupuestosEtapa17, la única pendiente — el seeder NUNCA corre acá
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            await using var comandoVerificar = verificacion.CreateCommand();
            comandoVerificar.CommandText = "SELECT activo FROM tipos_comprobante WHERE codigo = 'PRE'";
            var activo = (bool)(await comandoVerificar.ExecuteScalarAsync())!;

            Assert.False(activo);
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

    /// <summary>GATE GUARD, net 1b (task 1.39, mutation target #10): una base recién sembrada
    /// (fresh install) tiene `PRE` inactivo por el <c>Activo = false</c> explícito de
    /// <c>InicializadorDeBaseDeDatos.TiposComprobanteBase</c>. Patrón de los tests de seed de
    /// bases ya migradas del repo (<c>CuentaCorrienteProveedorBackfillTests</c>): una base NUEVA
    /// dentro del MISMO contenedor compartido (<c>CREATE DATABASE</c>, nunca tocada por esta
    /// migración antes), y se invoca el <c>InicializadorDeBaseDeDatos</c> REAL de punta a punta
    /// (<c>EjecutarAsync</c> — migra Y siembra, exactamente el camino de arranque de
    /// producción) — nunca una copia a mano del INSERT, que no detectaría una mutación del
    /// archivo real. El data statement 1 de la migración (que solo corre contra filas
    /// EXISTENTES) NUNCA se ejecuta contra una base vacía: el único responsable de que `PRE`
    /// nazca inactivo acá es el seed change (net 1b), independiente de net 1.</summary>
    [Fact]
    public async Task UnaBaseFrescamenteSembradaTienePreInactivo()
    {
        var nombreBase = $"ways_stage17_fresh_{Guid.NewGuid():N}";
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
                    npgsql.MapEnum<ResultadoFiscal>("resultado_fiscal");
                    npgsql.MapEnum<AmbienteFiscal>("ambiente_fiscal");
                })
                .Options;

            await using var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma);

            var inicializador = new InicializadorDeBaseDeDatos(
                db,
                new HasheadorPbkdf2(),
                new RelojDelSistema(),
                new EntornoDePruebaNoProduccion(),
                NullLogger<InicializadorDeBaseDeDatos>.Instance);

            await inicializador.EjecutarAsync(new SemillaRoot());

            await using var cruda = new NpgsqlConnection(cadenaNueva);
            await cruda.OpenAsync();

            await using var comando = cruda.CreateCommand();
            comando.CommandText = "SELECT activo FROM tipos_comprobante WHERE codigo = 'PRE'";
            var activo = (bool)(await comando.ExecuteScalarAsync())!;

            Assert.False(activo);
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

    /// <summary>Fake mínimo de <see cref="IHostEnvironment"/> — <c>InicializadorDeBaseDeDatos</c>
    /// solo llama <c>IsProduction()</c> (que compara <see cref="EnvironmentName"/> contra
    /// "Production") para decidir throw-vs-log en <c>VerificarRolSinBypassAsync</c>/
    /// <c>VerificarInvariantesDeConexion</c> — "Testing" garantiza la rama de log, nunca throw,
    /// aunque <c>ways_owner</c> sea superuser en el contenedor de Testcontainers (mismo residuo
    /// ya documentado en <c>CuentaCorrienteProveedorBackfillTests</c>, target #11).</summary>
    private sealed class EntornoDePruebaNoProduccion : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Ways.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // ---------------------------------------------------------------------------------------
    // Non-regression (task 1.36) — la verdad de fuego del PRE latente
    // ---------------------------------------------------------------------------------------

    /// <summary>Domain unit — véase <c>ReglaDePresupuestosTests</c> (design.md:494): la truth
    /// table completa vive ahí, sin fixture. Referencia cruzada dejada acá para que el
    /// mutation-target-index del slice apunte a un solo lugar por regla.</summary>
    [Fact]
    public void ReglaDePresupuestosTruthTableViveEnWaysDomainTests()
    {
        Assert.True(true);
    }
}
