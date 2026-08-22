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
/// stage-19a-slice1 (tasks 1.25-1.47, mutation targets 1-23, mutation-proof-tests v1.1,
/// db-error-backstops, design.md "The migration — exact statement order"): schema fiscal —
/// enums, 8 CHECKs, 8 índices por definición, RLS de ambas tablas nuevas, doble red de
/// codigo_afip, reversibilidad exacta — sobre la base COMPARTIDA de <see cref="WaysApiFixture"/>
/// (mismo criterio que <c>RemitosSchemaTests</c>: no depende del momento exacto de una migración
/// de datos, salvo la doble red y la reversibilidad, que sí lo hacen y se prueban aparte).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FiscalSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdCliente, int IdEmpleado, int IdCondicionFiscal);

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra roles/catálogos fiscales

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
            IdTenant = tenant.Id, Numero = 701, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
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

        return new Escenario(tenant.Id, empresa.Id, puntoVenta.Id, cliente.Id, empleado.Id, condicionFiscal.Id);
    }

    private static async Task<int> ObtenerIdTipoComprobanteAsync(NpgsqlConnection cruda, string codigo)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT id_tipo_comprobante FROM tipos_comprobante WHERE codigo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    // ---------------------------------------------------------------------------------------
    // comprobantes_venta fiscal — INSERT crudo con las 4 columnas nuevas
    // ---------------------------------------------------------------------------------------

    private const string ColumnasComprobante =
        "(id_tenant, id_tipo_comprobante, numero, fecha, id_punto_venta, id_turno_caja, id_empleado, " +
        " id_cliente, id_comprobante_asociado, id_presupuesto_origen, subtotal, descuento_total, total, " +
        " neto_gravado, iva_total, direccion_entrega, observaciones, estado, cae, cae_vencimiento, " +
        " resultado_fiscal, observaciones_fiscales, created_at, updated_at, deleted_at)";

    private static NpgsqlCommand ComandoInsertarComprobante(
        NpgsqlConnection cruda, Escenario e, int idTipoComprobante, long numero,
        string? cae, DateOnly? caeVencimiento, string? resultadoFiscal, string? observacionesFiscales)
    {
        var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO comprobantes_venta " + ColumnasComprobante +
            " VALUES ($1, $2, $3, now(), $4, NULL, $5, $6, NULL, NULL, 10, 0, 10, NULL, NULL, NULL, NULL, " +
            " 'emitido'::estado_comprobante, $7, $8, $9::resultado_fiscal, $10::jsonb, now(), now(), NULL)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTipoComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = numero });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpleado });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)cae ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)caeVencimiento ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)resultadoFiscal ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)observacionesFiscales ?? DBNull.Value });
        return comando;
    }

    // ---------------------------------------------------------------------------------------
    // certificados_fiscales — INSERT crudo con material GCM válido por defecto
    // ---------------------------------------------------------------------------------------

    private const string ColumnasCertificado =
        "(id_tenant, id_empresa, ambiente, alias, cuit_titular, certificado_pem, clave_privada_cifrada, " +
        " nonce, tag_autenticacion, id_clave_maestra, huella_sha256, vigencia_desde, vigencia_hasta, " +
        " activo, created_at, updated_at, deleted_at)";

    private static NpgsqlCommand ComandoInsertarCertificado(
        NpgsqlConnection cruda, Escenario e, string alias,
        string ambiente = "homologacion",
        bool activo = true,
        DateTimeOffset? deletedAt = null,
        string cuitTitular = "20111111112",
        DateTimeOffset? vigenciaDesde = null,
        DateTimeOffset? vigenciaHasta = null,
        int nonceLength = 12,
        int tagLength = 16,
        int clavePrivadaLength = 32)
    {
        var ahora = DateTimeOffset.UtcNow;
        var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO certificados_fiscales " + ColumnasCertificado +
            " VALUES ($1, $2, $3::ambiente_fiscal, $4, $5, 'PEM-DE-PRUEBA', $6, $7, $8, 'v1', " +
            " '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef', $9, $10, $11, now(), now(), $12)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = ambiente });
        comando.Parameters.Add(new NpgsqlParameter { Value = alias });
        comando.Parameters.Add(new NpgsqlParameter { Value = cuitTitular });
        comando.Parameters.Add(new NpgsqlParameter { Value = new byte[Math.Max(clavePrivadaLength, 0)] });
        comando.Parameters.Add(new NpgsqlParameter { Value = new byte[Math.Max(nonceLength, 0)] });
        comando.Parameters.Add(new NpgsqlParameter { Value = new byte[Math.Max(tagLength, 0)] });
        comando.Parameters.Add(new NpgsqlParameter { Value = vigenciaDesde ?? ahora });
        comando.Parameters.Add(new NpgsqlParameter { Value = vigenciaHasta ?? ahora.AddYears(2) });
        comando.Parameters.Add(new NpgsqlParameter { Value = activo });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)deletedAt ?? DBNull.Value });
        return comando;
    }

    // ---------------------------------------------------------------------------------------
    // numeraciones_fiscales — INSERT crudo
    // ---------------------------------------------------------------------------------------

    private static NpgsqlCommand ComandoInsertarNumeracionFiscal(
        NpgsqlConnection cruda, Escenario e, short codigoAfip,
        long proximoNumero = 1, long? ultimoAutorizadoArca = null, DateTimeOffset? sincronizadoEn = null)
    {
        var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero, " +
            " ultimo_autorizado_arca, sincronizado_en) VALUES ($1, $2, $3, $4, $5, $6)";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = codigoAfip });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = proximoNumero });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)ultimoAutorizadoArca ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)sincronizadoEn ?? DBNull.Value });
        return comando;
    }

    // =========================================================================================
    // Target 1 — orden de CREATE TYPE = orden de ciclo de vida = pg_enum.enumsortorder
    // =========================================================================================

    [Theory]
    [InlineData("ambiente_fiscal", new[] { "homologacion", "produccion" })]
    [InlineData("resultado_fiscal", new[] { "pendiente", "aprobado", "aprobado_con_observaciones", "rechazado" })]
    public async Task ElOrdenDeCicloDeVidaDelEnumCoincideConPgEnumEnsortorder(string tipo, string[] ordenEsperado)
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT e.enumlabel FROM pg_enum e JOIN pg_type t ON e.enumtypid = t.oid " +
            "WHERE t.typname = $1 ORDER BY e.enumsortorder";
        comando.Parameters.Add(new NpgsqlParameter { Value = tipo });

        var etiquetas = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            etiquetas.Add(lector.GetString(0));
        }

        Assert.Equal(ordenEsperado, etiquetas);
    }

    // =========================================================================================
    // Target 2 [S] — cero ALTER TYPE ... ADD VALUE en el archivo de la migración
    // =========================================================================================

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuenteDeLaMigracion()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260822002214_FiscalArcaEtapa19a.cs");

        Assert.True(File.Exists(ruta), $"No se encontró la migración en {ruta}");
        return File.ReadAllText(ruta);
    }

    [Fact]
    public void LaMigracionNoContieneNingunAlterTypeAddValue()
    {
        var fuente = LeerFuenteDeLaMigracion();

        // Excluye los comentarios (el archivo documenta "cero ALTER TYPE ... ADD VALUE" en
        // prosa): el scan real busca la frase en CÓDIGO ejecutable, nunca en un comentario.
        var codigoSinComentarios = string.Join(
            '\n',
            fuente.Split('\n').Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("ADD VALUE", codigoSinComentarios, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================================
    // Target 3 — los 8 índices nuevos, por DEFINICIÓN (nunca por nombre)
    // =========================================================================================

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

    [Fact]
    public async Task LosOchoIndicesNuevosExistenPorDefinicionYSonExactamenteOcho()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var defEmpresas = await ObtenerIndexDefAsync(cruda, "empresas", "ix_empresas_condicion_fiscal");
        Assert.Contains("btree (id_condicion_fiscal)", defEmpresas);
        Assert.DoesNotContain("id_tenant", defEmpresas);

        var defPuntoVenta = await ObtenerIndexDefAsync(cruda, "puntos_venta", "ux_puntos_venta_numero_fiscal");
        Assert.Contains("UNIQUE INDEX", defPuntoVenta);
        Assert.Contains("btree (id_tenant, id_empresa, numero_fiscal)", defPuntoVenta);
        Assert.Contains("WHERE (numero_fiscal IS NOT NULL)", defPuntoVenta);

        var defPendientes = await ObtenerIndexDefAsync(cruda, "comprobantes_venta", "ix_comprobantes_venta_fiscal_pendientes");
        Assert.Contains("btree (id_punto_venta, id_tenant)", defPendientes);
        Assert.Contains("WHERE (resultado_fiscal = 'pendiente'::resultado_fiscal)", defPendientes);

        var defCertTenant = await ObtenerIndexDefAsync(cruda, "certificados_fiscales", "ix_certificados_fiscales_tenant");
        Assert.Contains("btree (id_tenant)", defCertTenant);

        var defCertEmpresa = await ObtenerIndexDefAsync(cruda, "certificados_fiscales", "ix_certificados_fiscales_empresa");
        Assert.Contains("btree (id_empresa, id_tenant)", defCertEmpresa);

        var defCertActivo = await ObtenerIndexDefAsync(cruda, "certificados_fiscales", "ux_certificados_fiscales_activo");
        Assert.Contains("UNIQUE INDEX", defCertActivo);
        Assert.Contains("btree (id_tenant, id_empresa, ambiente)", defCertActivo);
        Assert.Contains("WHERE (activo AND (deleted_at IS NULL))", defCertActivo);

        var defNumTenant = await ObtenerIndexDefAsync(cruda, "numeraciones_fiscales", "ix_numeraciones_fiscales_tenant");
        Assert.Contains("btree (id_tenant)", defNumTenant);

        var defNumPuntoVenta = await ObtenerIndexDefAsync(cruda, "numeraciones_fiscales", "ix_numeraciones_fiscales_punto_venta");
        Assert.Contains("btree (id_punto_venta, id_tenant)", defNumPuntoVenta);

        // Conteo total = 8, incluyendo las dos PKs de las tablas nuevas por separado (excluidas
        // del conteo de índices "nuevos" per el gate — se cuentan acá para confirmar que no hay
        // ningún índice extra autogenerado por EF).
        async Task<List<string>> ListarAsync(string tabla)
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

        var indicesCertificados = (await ListarAsync("certificados_fiscales")).Where(n => n != "pk_certificados_fiscales").ToList();
        var indicesNumeraciones = (await ListarAsync("numeraciones_fiscales")).Where(n => n != "pk_numeraciones_fiscales").ToList();

        Assert.Equal(3, indicesCertificados.Count);
        Assert.Equal(2, indicesNumeraciones.Count);

        var totalIndicesNuevos = 1 /* empresas */ + 1 /* puntos_venta */ + 1 /* comprobantes_venta */
            + indicesCertificados.Count + indicesNumeraciones.Count;
        Assert.Equal(8, totalIndicesNuevos);
    }

    // =========================================================================================
    // Target 4 — CHECK 1: ck_puntos_venta_numero_fiscal_rango
    // =========================================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public async Task UnNumeroFiscalFueraDeRangoViolaLaCheckDeRango(int numeroFiscal)
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroFiscalFueraDeRangoViolaLaCheckDeRango) + numeroFiscal);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE puntos_venta SET numero_fiscal = $1 WHERE id_punto_venta = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = numeroFiscal });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_puntos_venta_numero_fiscal_rango", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnNumeroFiscalDentroDeRangoNoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroFiscalDentroDeRangoNoViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE puntos_venta SET numero_fiscal = 1 WHERE id_punto_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // =========================================================================================
    // Target 5 — CHECK 2: ck_comprobantes_venta_fiscal_coherente (4 conjuntos)
    // =========================================================================================

    [Fact]
    public async Task UnCaeSinVencimientoViolaLaCheckDeCoherenciaFiscal()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCaeSinVencimientoViolaLaCheckDeCoherenciaFiscal));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = ComandoInsertarComprobante(
            cruda, e, idTipo, 801, cae: "12345678901234", caeVencimiento: null,
            resultadoFiscal: "aprobado", observacionesFiscales: null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_venta_fiscal_coherente", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnRechazadoConCaeViolaLaCheckDeCoherenciaFiscal()
    {
        var e = await SembrarEscenarioAsync(nameof(UnRechazadoConCaeViolaLaCheckDeCoherenciaFiscal));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = ComandoInsertarComprobante(
            cruda, e, idTipo, 802, cae: "12345678901234", caeVencimiento: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            resultadoFiscal: "rechazado", observacionesFiscales: null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_venta_fiscal_coherente", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnAprobadoSinCaeViolaLaCheckDeCoherenciaFiscal()
    {
        var e = await SembrarEscenarioAsync(nameof(UnAprobadoSinCaeViolaLaCheckDeCoherenciaFiscal));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = ComandoInsertarComprobante(
            cruda, e, idTipo, 803, cae: null, caeVencimiento: null,
            resultadoFiscal: "aprobado", observacionesFiscales: null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_venta_fiscal_coherente", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCaeSetConResultadoFiscalNullViolaLaCheckDeCoherenciaFiscal()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCaeSetConResultadoFiscalNullViolaLaCheckDeCoherenciaFiscal));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = ComandoInsertarComprobante(
            cruda, e, idTipo, 804, cae: "12345678901234", caeVencimiento: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            resultadoFiscal: null, observacionesFiscales: null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_comprobantes_venta_fiscal_coherente", excepcion.ConstraintName);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("12345678901234", "aprobado", "[]")]
    [InlineData("12345678901234", "aprobado_con_observaciones", "[{\"codigo\":10,\"mensaje\":\"obs\"}]")]
    public async Task UnComprobanteCoherenteNoViolaLaCheck(string? cae, string? resultado, string? observaciones)
    {
        var e = await SembrarEscenarioAsync(nameof(UnComprobanteCoherenteNoViolaLaCheck) + (resultado ?? "null"));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        var vencimiento = cae is null ? (DateOnly?)null : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        await using var comando = ComandoInsertarComprobante(cruda, e, idTipo, 805, cae, vencimiento, resultado, observaciones);

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // =========================================================================================
    // Target 6 — CHECK 3: ck_comprobantes_venta_cae_digitos
    // =========================================================================================

    [Theory]
    [InlineData("1234567890123")]     // 13 dígitos
    [InlineData("1234567890ABCD")]    // alfanumérico
    public async Task UnCaeMalFormadoViolaLaCheckDeDigitos(string cae)
    {
        var e = await SembrarEscenarioAsync(nameof(UnCaeMalFormadoViolaLaCheckDeDigitos) + cae.Length);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var idTipo = await ObtenerIdTipoComprobanteAsync(cruda, "TX");

        await using var comando = ComandoInsertarComprobante(
            cruda, e, idTipo, 806, cae, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), "aprobado", null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        // Postgres evalúa las CHECKs en orden de declaración; ambas fallan acá porque un CAE de
        // 13/14 dígitos alfanuméricos también incumple ck_comprobantes_venta_cae_digitos.
        Assert.Equal("ck_comprobantes_venta_cae_digitos", excepcion.ConstraintName);
    }

    // =========================================================================================
    // Target 7 — CHECK 4: ck_certificados_fiscales_vigencia
    // =========================================================================================

    [Fact]
    public async Task UnaVigenciaIgualViolaLaCheckDeVigencia()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaVigenciaIgualViolaLaCheckDeVigencia));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        await using var comando = ComandoInsertarCertificado(
            cruda, e, "cert-vigencia", vigenciaDesde: ahora, vigenciaHasta: ahora);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_certificados_fiscales_vigencia", excepcion.ConstraintName);
    }

    // =========================================================================================
    // Target 8 — CHECK 5: ck_certificados_fiscales_cuit
    // =========================================================================================

    [Fact]
    public async Task UnCuitDe10DigitosViolaLaCheckDeCuit()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCuitDe10DigitosViolaLaCheckDeCuit));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarCertificado(cruda, e, "cert-cuit", cuitTitular: "2011111111");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_certificados_fiscales_cuit", excepcion.ConstraintName);
    }

    // =========================================================================================
    // Target 9 — CHECK 6: ck_certificados_fiscales_material (3 conjuntos GCM)
    // =========================================================================================

    [Fact]
    public async Task UnNonceDe11BytesViolaLaCheckDeMaterial()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNonceDe11BytesViolaLaCheckDeMaterial));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarCertificado(cruda, e, "cert-nonce", nonceLength: 11);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_certificados_fiscales_material", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnTagDe15BytesViolaLaCheckDeMaterial()
    {
        var e = await SembrarEscenarioAsync(nameof(UnTagDe15BytesViolaLaCheckDeMaterial));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarCertificado(cruda, e, "cert-tag", tagLength: 15);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_certificados_fiscales_material", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaClavePrivadaVaciaViolaLaCheckDeMaterial()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaClavePrivadaVaciaViolaLaCheckDeMaterial));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarCertificado(cruda, e, "cert-vacio", clavePrivadaLength: 0);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_certificados_fiscales_material", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCertificadoConMaterialValidoNoViolaNingunaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCertificadoConMaterialValidoNoViolaNingunaCheck));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarCertificado(cruda, e, "cert-valido");
        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    // =========================================================================================
    // Target 10 — CHECK 7: ck_numeraciones_fiscales_rango
    // =========================================================================================

    [Fact]
    public async Task UnUltimoAutorizadoArcaEnCeroEsLegalYNoViolaLaCheckDeRango()
    {
        var e = await SembrarEscenarioAsync(nameof(UnUltimoAutorizadoArcaEnCeroEsLegalYNoViolaLaCheckDeRango));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarNumeracionFiscal(
            cruda, e, codigoAfip: 1, ultimoAutorizadoArca: 0, sincronizadoEn: DateTimeOffset.UtcNow);

        await comando.ExecuteNonQueryAsync(); // "serie sin usar" — no debe tirar
    }

    [Fact]
    public async Task UnUltimoAutorizadoArcaPorEncimaDelTopeViolaLaCheckDeRango()
    {
        var e = await SembrarEscenarioAsync(nameof(UnUltimoAutorizadoArcaPorEncimaDelTopeViolaLaCheckDeRango));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarNumeracionFiscal(
            cruda, e, codigoAfip: 1, ultimoAutorizadoArca: 100_000_000, sincronizadoEn: DateTimeOffset.UtcNow);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_numeraciones_fiscales_rango", excepcion.ConstraintName);
    }

    /// <summary>Corrección registrada (mutation-proof-tests regla 2, "run it, don't reason it"):
    /// <c>tasks.md</c> 1.34 dice literalmente "proximo_numero = 0 must succeed", pero el propio
    /// CHECK 7 exige <c>proximo_numero BETWEEN 1 AND 99999999</c> — <c>0</c> NUNCA es legal para
    /// esa columna (el "0 legal" del design es exclusivamente para <c>ultimo_autorizado_arca</c>,
    /// probado arriba). Registrado como desvío de redacción de tasks.md, no un hallazgo de
    /// diseño; esta prueba fija el comportamiento REAL de la CHECK para <c>proximo_numero</c>.</summary>
    [Fact]
    public async Task UnProximoNumeroEnCeroVIOLALaCheckDeRango()
    {
        var e = await SembrarEscenarioAsync(nameof(UnProximoNumeroEnCeroVIOLALaCheckDeRango));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarNumeracionFiscal(cruda, e, codigoAfip: 1, proximoNumero: 0);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_numeraciones_fiscales_rango", excepcion.ConstraintName);
    }

    // =========================================================================================
    // Target 11 — CHECK 8: ck_numeraciones_fiscales_sincronizacion
    // =========================================================================================

    [Fact]
    public async Task UnUltimoAutorizadoArcaSinSincronizadoEnViolaLaCheckDeSincronizacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnUltimoAutorizadoArcaSinSincronizadoEnViolaLaCheckDeSincronizacion));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using var comando = ComandoInsertarNumeracionFiscal(
            cruda, e, codigoAfip: 1, ultimoAutorizadoArca: 5, sincronizadoEn: null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_numeraciones_fiscales_sincronizacion", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnSincronizadoEnSinUltimoAutorizadoArcaViolaLaCheckDeSincronizacion()
    {
        var e = await SembrarEscenarioAsync(nameof(UnSincronizadoEnSinUltimoAutorizadoArcaViolaLaCheckDeSincronizacion));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        // INSERT directo con SQL crudo para evitar el helper (que exige el par consistente).
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO numeraciones_fiscales (id_punto_venta, codigo_afip, id_tenant, proximo_numero, " +
            " ultimo_autorizado_arca, sincronizado_en) VALUES ($1, 1, $2, 1, NULL, now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_numeraciones_fiscales_sincronizacion", excepcion.ConstraintName);
    }

    // =========================================================================================
    // Target 12 — ux_puntos_venta_numero_fiscal: UNIQUE parcial
    // =========================================================================================

    [Fact]
    public async Task UnNumeroFiscalDuplicadoParaLaMismaEmpresaViolaLaUnicidad()
    {
        var e = await SembrarEscenarioAsync(nameof(UnNumeroFiscalDuplicadoParaLaMismaEmpresaViolaLaUnicidad));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var otroPuntoVenta = new PuntoVenta
        {
            IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, Nombre = "otro-pv",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.PuntosVenta.Add(otroPuntoVenta);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        async Task SetearAsync(int idPuntoVenta, int numeroFiscal)
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "UPDATE puntos_venta SET numero_fiscal = $1 WHERE id_punto_venta = $2";
            comando.Parameters.Add(new NpgsqlParameter { Value = numeroFiscal });
            comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
            await comando.ExecuteNonQueryAsync();
        }

        await SetearAsync(e.IdPuntoVenta, 5);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => SetearAsync(otroPuntoVenta.Id, 5));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_puntos_venta_numero_fiscal", excepcion.ConstraintName);
    }

    [Fact]
    public async Task DosPuntosDeVentaSinNumeroFiscalConvivenSinConflicto()
    {
        var e = await SembrarEscenarioAsync(nameof(DosPuntosDeVentaSinNumeroFiscalConvivenSinConflicto));
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        db.PuntosVenta.Add(new PuntoVenta
        {
            IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, Nombre = "otro-pv-2",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(); // ambos numero_fiscal NULL — filtro parcial, no debe tirar
    }

    // =========================================================================================
    // Target 13 — ux_certificados_fiscales_activo: UNIQUE parcial (activo AND deleted_at IS NULL)
    // =========================================================================================

    [Fact]
    public async Task UnSegundoCertificadoActivoParaLaMismaEmpresaYAmbienteViolaLaUnicidad()
    {
        var e = await SembrarEscenarioAsync(nameof(UnSegundoCertificadoActivoParaLaMismaEmpresaYAmbienteViolaLaUnicidad));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using (var primero = ComandoInsertarCertificado(cruda, e, "cert-a"))
        {
            await primero.ExecuteNonQueryAsync();
        }

        await using var segundo = ComandoInsertarCertificado(cruda, e, "cert-b");
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => segundo.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_certificados_fiscales_activo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnCertificadoSoftDeletedNoViolaLaUnicidadDeActivo()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCertificadoSoftDeletedNoViolaLaUnicidadDeActivo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using (var borrado = ComandoInsertarCertificado(cruda, e, "cert-borrado", deletedAt: DateTimeOffset.UtcNow))
        {
            await borrado.ExecuteNonQueryAsync();
        }

        await using var activo = ComandoInsertarCertificado(cruda, e, "cert-activo");
        await activo.ExecuteNonQueryAsync(); // no debe tirar — el borrado no cuenta
    }

    [Fact]
    public async Task UnCertificadoInactivoNoViolaLaUnicidadDeActivo()
    {
        var e = await SembrarEscenarioAsync(nameof(UnCertificadoInactivoNoViolaLaUnicidadDeActivo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);

        await using (var inactivo = ComandoInsertarCertificado(cruda, e, "cert-inactivo", activo: false))
        {
            await inactivo.ExecuteNonQueryAsync();
        }

        await using var activo = ComandoInsertarCertificado(cruda, e, "cert-activo-2");
        await activo.ExecuteNonQueryAsync(); // no debe tirar — el inactivo no cuenta
    }

    // =========================================================================================
    // Targets 14/15 — RLS cross-tenant, lectura Y escritura, en ambas tablas nuevas
    // =========================================================================================

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLosCertificadosFiscalesPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosCertificadosFiscalesPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLosCertificadosFiscalesPorSelect) + "-B");

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertar = ComandoInsertarCertificado(cruda, a, "cert-a");
            await insertar.ExecuteNonQueryAsync();
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM certificados_fiscales WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnCertificadosFiscalesSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnCertificadosFiscalesSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnCertificadosFiscalesSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = ComandoInsertarCertificado(comoA, b, "cert-ajeno"); // id_tenant/id_empresa de B, sesión de A

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoVeLasNumeracionesFiscalesPorSelect()
    {
        var a = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasNumeracionesFiscalesPorSelect) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnaSesionDeOtroTenantNoVeLasNumeracionesFiscalesPorSelect) + "-B");

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertar = ComandoInsertarNumeracionFiscal(cruda, a, codigoAfip: 1);
            await insertar.ExecuteNonQueryAsync();
        }

        await using var comoB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = comoB.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM numeraciones_fiscales WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });

        var total = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoEnNumeracionesFiscalesSeRechaza()
    {
        var a = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnNumeracionesFiscalesSeRechaza) + "-A");
        var b = await SembrarEscenarioAsync(nameof(UnInsertConIdTenantAjenoEnNumeracionesFiscalesSeRechaza) + "-B");

        await using var comoA = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant);
        await using var comando = ComandoInsertarNumeracionFiscal(comoA, b, codigoAfip: 1); // id_tenant de B, sesión de A

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // =========================================================================================
    // Target 16 — AplicarFiltroDeTenantEnNumeracionFiscal (LINQ/EF)
    // =========================================================================================

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant manual bloquea
    /// <see cref="NumeracionFiscal"/> (no hereda <c>EntidadTenant</c>) — mismo patrón que
    /// <c>NumeracionesComprobanteRlsTests.ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant</c>. Mutación
    /// verificada a mano durante la implementación (mutation-proof-tests regla 2): comentar la
    /// llamada a <c>AplicarFiltroDeTenantEnNumeracionFiscal</c> en <c>OnModelCreating</c> hace que
    /// esta prueba deje de estar vacía (rojo), revertido después — evidencia en el PR body.</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveNumeracionesFiscalesDeOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveNumeracionesFiscalesDeOtroTenant) + "-A");
        var b = await SembrarEscenarioAsync(nameof(ElFiltroDeEfNuncaDevuelveNumeracionesFiscalesDeOtroTenant) + "-B");

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", a.IdTenant))
        {
            await using var insertar = ComandoInsertarNumeracionFiscal(cruda, a, codigoAfip: 1);
            await insertar.ExecuteNonQueryAsync();
        }

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, b.IdTenant));
        var visibles = await sesionB.NumeracionesFiscales
            .Where(n => n.IdPuntoVenta == a.IdPuntoVenta).ToListAsync();

        Assert.Empty(visibles);
    }

    // =========================================================================================
    // Target 17 — RechazarEscriturasDeNumeracionFiscal
    // =========================================================================================

    [Fact]
    public async Task UnSaveChangesSobreUnaNumeracionFiscalTrackeadaTira()
    {
        var e = await SembrarEscenarioAsync(nameof(UnSaveChangesSobreUnaNumeracionFiscalTrackeadaTira));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.NumeracionesFiscales.Add(new NumeracionFiscal
        {
            IdPuntoVenta = e.IdPuntoVenta, CodigoAfip = 1, IdTenant = e.IdTenant, ProximoNumero = 1
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    // =========================================================================================
    // Targets 18/20 — data statements (net 1) sobre una base YA MIGRADA desde RemitosEtapa17
    // =========================================================================================

    private const string MigracionAnteriorAFiscalArcaEtapa19a = "20260820004658_RemitosEtapa17";

    private static DbContextOptions<WaysDbContext> ConstruirOpcionesDeMigracion(string cadena) =>
        new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(cadena, npgsql =>
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

    [Fact]
    public async Task LosTresDataStatementsAterrizanEnUnaBaseYaMigradaSinTocarFilasNiInsertarNinguna()
    {
        var nombreBase = $"ways_stage19a_ds_{Guid.NewGuid():N}";
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
            var opciones = ConstruirOpcionesDeMigracion(cadenaNueva);

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnteriorAFiscalArcaEtapa19a);
            }

            // Simula un catálogo real ya poblado ANTES de esta etapa (codigo_afip NULL en las 3
            // tablas — la base ya tenía las filas desde la etapa 1, esta migración solo agrega
            // el valor).
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();

                async Task InsertarAsync(string sql)
                {
                    await using var comando = conexion.CreateCommand();
                    comando.CommandText = sql;
                    await comando.ExecuteNonQueryAsync();
                }

                foreach (var codigo in new[] { "FA", "FB", "FC", "NCA", "NCB", "NCC", "NDA", "TX" })
                {
                    await InsertarAsync(
                        "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, " +
                        $"es_fiscal, afecta_stock, activo, created_at, updated_at) VALUES ('venta', '{codigo}', " +
                        $"'{codigo}', 'A', 1, false, true, true, true, now(), now())");
                }

                foreach (var codigo in new[] { "RI", "MONOTRIBUTO", "EXENTO", "CF", "NO_RESP" })
                {
                    await InsertarAsync(
                        $"INSERT INTO condiciones_fiscales (codigo, nombre, created_at, updated_at) " +
                        $"VALUES ('{codigo}', '{codigo}', now(), now())");
                }

                foreach (var (nombre, porcentaje) in new (string, decimal)[]
                    { ("21%", 21m), ("10.5%", 10.5m), ("27%", 27m), ("0%", 0m), ("Exento", 0m), ("No gravado", 0m) })
                {
                    await InsertarAsync(
                        $"INSERT INTO alicuotas_iva (nombre, porcentaje, created_at, updated_at) " +
                        $"VALUES ('{nombre}', {porcentaje.ToString(System.Globalization.CultureInfo.InvariantCulture)}, now(), now())");
                }
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica FiscalArcaEtapa19a — el seeder NUNCA corre acá
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            async Task<short?> LeerCodigoAfipAsync(string tabla, string columnaFiltro, string valorFiltro)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = $"SELECT codigo_afip FROM {tabla} WHERE {columnaFiltro} = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = valorFiltro });
                var resultado = await comando.ExecuteScalarAsync();
                return resultado is DBNull or null ? null : (short)(short)resultado;
            }

            // DS1 — tipos_comprobante (target 18)
            Assert.Equal((short)1, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "FA"));
            Assert.Equal((short)2, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "NDA"));
            Assert.Equal((short)3, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "NCA"));
            Assert.Equal((short)6, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "FB"));
            Assert.Equal((short)8, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "NCB"));
            Assert.Equal((short)11, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "FC"));
            Assert.Equal((short)13, await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "NCC"));
            Assert.Null(await LeerCodigoAfipAsync("tipos_comprobante", "codigo", "TX")); // no fiscal, sin tocar

            // DS2 — condiciones_fiscales (target 18)
            Assert.Equal((short)1, await LeerCodigoAfipAsync("condiciones_fiscales", "codigo", "RI"));
            Assert.Equal((short)4, await LeerCodigoAfipAsync("condiciones_fiscales", "codigo", "EXENTO"));
            Assert.Equal((short)5, await LeerCodigoAfipAsync("condiciones_fiscales", "codigo", "CF"));
            Assert.Equal((short)6, await LeerCodigoAfipAsync("condiciones_fiscales", "codigo", "MONOTRIBUTO"));
            Assert.Equal((short)15, await LeerCodigoAfipAsync("condiciones_fiscales", "codigo", "NO_RESP"));

            // DS3 — alicuotas_iva (targets 18 y 20: Exento/No gravado quedan NULL)
            Assert.Equal((short)3, await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "0%"));
            Assert.Equal((short)4, await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "10.5%"));
            Assert.Equal((short)5, await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "21%"));
            Assert.Equal((short)6, await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "27%"));
            Assert.Null(await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "Exento"));
            Assert.Null(await LeerCodigoAfipAsync("alicuotas_iva", "nombre", "No gravado"));

            // CERO filas insertadas/activadas/desactivadas — mismos conteos que antes de migrar.
            async Task<long> ContarAsync(string tabla)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = $"SELECT count(*) FROM {tabla}";
                return (long)(await comando.ExecuteScalarAsync())!;
            }

            Assert.Equal(8, await ContarAsync("tipos_comprobante"));
            Assert.Equal(5, await ContarAsync("condiciones_fiscales"));
            Assert.Equal(6, await ContarAsync("alicuotas_iva"));
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

    // =========================================================================================
    // Target 19 — cada seed net (fresh DB, InicializadorDeBaseDeDatos) probado independientemente
    // =========================================================================================

    [Fact]
    public async Task LaBaseFrescaSiembraElCodigoAfipDeLosTresCatalogosDesdeLosSeedNets()
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra los tres catálogos fiscales

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        async Task<short?> CodigoAfipDeAsync(IQueryable<short?> query) => await query.FirstAsync();

        Assert.Equal((short)1, await CodigoAfipDeAsync(db.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.CodigoAfip)));
        Assert.Equal((short)6, await CodigoAfipDeAsync(db.TiposComprobante.Where(t => t.Codigo == "FB").Select(t => t.CodigoAfip)));
        Assert.Null(await CodigoAfipDeAsync(db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.CodigoAfip)));

        Assert.Equal((short)1, await CodigoAfipDeAsync(db.CondicionesFiscales.Where(c => c.Codigo == "RI").Select(c => c.CodigoAfip)));
        Assert.Equal((short)15, await CodigoAfipDeAsync(db.CondicionesFiscales.Where(c => c.Codigo == "NO_RESP").Select(c => c.CodigoAfip)));

        Assert.Equal((short)5, await CodigoAfipDeAsync(db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.CodigoAfip)));
        Assert.Null(await CodigoAfipDeAsync(db.AlicuotasIva.Where(a => a.Nombre == "Exento").Select(a => a.CodigoAfip)));
        Assert.Null(await CodigoAfipDeAsync(db.AlicuotasIva.Where(a => a.Nombre == "No gravado").Select(a => a.CodigoAfip)));
    }

    // =========================================================================================
    // Target 21 [S] — ix_empresas_condicion_fiscal es SIMPLE, no liderado por id_tenant
    // =========================================================================================

    [Fact]
    public async Task IxEmpresasCondicionFiscalEsSimpleNoLideradaPorIdTenant()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var def = await ObtenerIndexDefAsync(cruda, "empresas", "ix_empresas_condicion_fiscal");
        Assert.Contains("btree (id_condicion_fiscal)", def);
        Assert.DoesNotContain("id_tenant", def);
    }

    // =========================================================================================
    // Target 22 [S] — Up → Down → Up, has-pending-model-changes limpio en cada leg
    // =========================================================================================

    [Fact]
    public async Task UpDownUpEsLimpioYRevierteExactamenteElCodigoAfipQueSeteo()
    {
        var nombreBase = $"ways_stage19a_updownup_{Guid.NewGuid():N}";
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
            var opciones = ConstruirOpcionesDeMigracion(cadenaNueva);

            // Up hasta HEAD (incluye FiscalArcaEtapa19a) — sin seeder: solo migraciones.
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync();

                Assert.False(db.Database.HasPendingModelChanges());
            }

            async Task<short?> LeerAsync(string sql)
            {
                await using var conexion = new NpgsqlConnection(cadenaNueva);
                await conexion.OpenAsync();
                await using var comando = conexion.CreateCommand();
                comando.CommandText = sql;
                var resultado = await comando.ExecuteScalarAsync();
                return resultado is DBNull or null ? null : (short)(short)resultado;
            }

            // Como la base fresca no pasó por el seeder de la aplicación (Up() del migrator
            // crudo), las tres catálogos están vacías tras las migraciones — se siembra una fila
            // mínima por tabla a mano para poder observar el revert exacto.
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();
                await using var comando = conexion.CreateCommand();
                comando.CommandText =
                    "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, codigo_afip, created_at, updated_at) " +
                    "VALUES ('venta', 'FA', 'Factura A', 'A', 1, true, true, true, true, 1, now(), now())";
                await comando.ExecuteNonQueryAsync();
            }

            Assert.Equal((short)1, await LeerAsync("SELECT codigo_afip FROM tipos_comprobante WHERE codigo = 'FA'"));

            // Down — revierte FiscalArcaEtapa19a exactamente.
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionAnteriorAFiscalArcaEtapa19a);
            }

            Assert.Null(await LeerAsync("SELECT codigo_afip FROM tipos_comprobante WHERE codigo = 'FA'"));

            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();
                await using var comandoTablas = conexion.CreateCommand();
                comandoTablas.CommandText =
                    "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('certificados_fiscales','numeraciones_fiscales')";
                Assert.Equal(0L, (long)(await comandoTablas.ExecuteScalarAsync())!);

                await using var comandoTipo = conexion.CreateCommand();
                comandoTipo.CommandText = "SELECT count(*) FROM pg_type WHERE typname IN ('resultado_fiscal', 'ambiente_fiscal')";
                Assert.Equal(0L, (long)(await comandoTipo.ExecuteScalarAsync())!);
            }

            // Up otra vez — reaplica FiscalArcaEtapa19a; limpio de nuevo.
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync();

                Assert.False(db.Database.HasPendingModelChanges());
            }
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

    // =========================================================================================
    // Target 23 [S] — NumeracionFiscalConfiguration: nombres de índice explícitos
    // =========================================================================================

    [Fact]
    public async Task LosDosIndicesDeNumeracionesFiscalesTienenNombreExplicitoSnakeCase()
    {
        using var _ = fixture.CreateClient();

        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();

        var defTenant = await ObtenerIndexDefAsync(cruda, "numeraciones_fiscales", "ix_numeraciones_fiscales_tenant");
        Assert.Contains("btree (id_tenant)", defTenant);

        var defPuntoVenta = await ObtenerIndexDefAsync(cruda, "numeraciones_fiscales", "ix_numeraciones_fiscales_punto_venta");
        Assert.Contains("btree (id_punto_venta, id_tenant)", defPuntoVenta);

        // Sin este nombramiento explícito, EF autogeneraría el índice de soporte de la FK
        // compuesta con su convención PascalCase (mismo trap que NumeracionComprobanteConfiguration
        // documenta): ninguna fila con "IX_" debe existir.
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM pg_indexes WHERE tablename = 'numeraciones_fiscales' AND indexname LIKE 'IX\\_%'";
        var autogenerados = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, autogenerados);
    }

    // =========================================================================================
    // Non-regression (task 1.48) — ServicioDeVentas.cs untouched this slice
    // =========================================================================================

    [Fact]
    public void ServicioDeVentasQuedaByteIdenticoEnEstaSlice()
    {
        var salida = EjecutarGit("diff", "--exit-code", "--", "src/Ways.Application/Ventas/ServicioDeVentas.cs");
        Assert.Equal(0, salida.CodigoDeSalida);
    }

    private static (int CodigoDeSalida, string Salida) EjecutarGit(params string[] argumentos)
    {
        var raiz = Path.Combine(Path.GetDirectoryName(RutaDeEsteArchivo())!, "..", "..");
        var proceso = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = raiz,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argumento in argumentos)
        {
            proceso.StartInfo.ArgumentList.Add(argumento);
        }
        proceso.Start();
        var salida = proceso.StandardOutput.ReadToEnd();
        proceso.WaitForExit();
        return (proceso.ExitCode, salida);
    }

    // =========================================================================================
    // GATE GUARD (task 1.49) — exactamente una migración, has-pending-model-changes limpio,
    // cero ALTER TYPE ADD VALUE, índices = 8, CHECKs = 8 — todo por definición
    // =========================================================================================

    [Fact]
    public void ExisteExactamenteUnaMigracionDeEstaEtapaYEsLaUltima()
    {
        var directorioMigraciones = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones");

        // Filtro por prefijo de timestamp (14 dígitos + '_'): excluye WaysDbContextModelSnapshot.cs
        // (único archivo no-migración del directorio), que además ordenaría alfabéticamente
        // DESPUÉS de toda migración ('W' > cualquier dígito en ASCII) y rompería la aserción de
        // "última migración" si no se excluyera.
        var archivos = Directory.GetFiles(directorioMigraciones, "*.cs")
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{14}_"))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();

        var fiscales = archivos.Where(n => n.Contains("FiscalArcaEtapa19a")).ToList();
        Assert.Single(fiscales);

        Assert.Equal(fiscales[0], archivos[^1]); // la última migración del repo, por orden de timestamp
    }

    [Fact]
    public async Task LaBaseCompartidaNoTienePendingModelChangesDespuesDeMigrarYSembrar()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
