using Npgsql;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-pos-reserva-de-numeracion (db-error-backstops): los backstops de esquema de
/// <c>reservas_numeracion</c>, probados por escritura cruda fuera de banda — los dos
/// originales (rango e índice único de bloque vivo) más los TRES de la rendición de cola
/// (<c>entregado_hasta</c>/<c>pendientes</c>/<c>reportado_at</c>).
///
/// <c>ck_reservas_numeracion_rango</c> — defensa pura (misma familia que
/// <c>ck_precios_ventana_valida</c>): el único escritor legítimo calcula <c>hasta</c> como
/// <c>desde + cantidad - 1</c> con <c>cantidad &gt;= 1</c> ya validado, nunca puede violar esto por
/// el camino de servicio.
///
/// <c>ux_reservas_numeracion_dispositivo_activo</c> — acá la exención de prueba de carrera es
/// distinta a la de <c>pk_numeraciones_comprobante</c>/<c>pk_stock</c>: no es una imposibilidad
/// ESTRUCTURAL (el camino normal SÍ podría chocar en teoría, ver el comentario de
/// <c>ReservaDeNumeracionMecanismoTests</c>), sino una ventana demasiado angosta para forzar de
/// forma confiable sin un rendezvous dedicado que hoy no existe en el repo — la traducción del
/// 23505 se prueba acá con dos filas vivas insertadas por bypass directo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReservaDeNumeracionBackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(int IdTenant, int IdPuntoVenta, int IdDispositivo)> SembrarEscenarioAsync(string nombre)
    {
        // Fuerza el arranque del host (seed de roles, catálogos globales, etc.) antes de
        // insertar por EF directo — mismo trámite que AsignadorDeNumeroComprobanteConcurrenciaTests.
        using var _ = fixture.CreateClient();

        await using var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = nombre,
            Modo = ModoPuntoVenta.Escritorio,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = $"admin-{nombre}",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Admin,
            PasswordHash = "x",
            PasswordAlgoritmo = "pbkdf2",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.Usuarios.Add(usuario);
        await siembra.SaveChangesAsync();

        var dispositivo = new Dispositivo
        {
            IdTenant = tenant.Id,
            IdPuntoVenta = puntoVenta.Id,
            Nombre = "Caja",
            TokenHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            IdUsuarioAlta = usuario.Id,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.Dispositivos.Add(dispositivo);
        await siembra.SaveChangesAsync();

        return (tenant.Id, puntoVenta.Id, dispositivo.Id);
    }

    [Fact]
    public async Task UnRangoInvertidoInsertadoPorFueraDelAsignadorViolaLaCheckDeRango()
    {
        var e = await SembrarEscenarioAsync(nameof(UnRangoInvertidoInsertadoPorFueraDelAsignadorViolaLaCheckDeRango));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 10, 5, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_reservas_numeracion_rango", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnRangoValidoInsertadoPorFueraDelAsignadorNoViolaLaCheck()
    {
        var e = await SembrarEscenarioAsync(nameof(UnRangoValidoInsertadoPorFueraDelAsignadorNoViolaLaCheck));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 5, 10, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    /// <summary>LA CARRERA de <c>ux_reservas_numeracion_dispositivo_activo</c>, probada por bypass
    /// (ver el doc-comment de la clase sobre por qué no como carrera real): dos filas VIVAS
    /// insertadas para la misma clave — la primera de servicio, la segunda cruda — tiene que
    /// chocar contra el índice único y traducirse al 409 de dominio.</summary>
    [Fact]
    public async Task UnaSegundaFilaVivaInsertadaPorFueraDelAsignadorViolaLaUnicidadDeDispositivoActivo()
    {
        var e = await SembrarEscenarioAsync(
            nameof(UnaSegundaFilaVivaInsertadaPorFueraDelAsignadorViolaLaUnicidadDeDispositivoActivo));

        await using var siembra = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using (var primera = siembra.CreateCommand())
        {
            primera.CommandText =
                "INSERT INTO reservas_numeracion " +
                "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at) " +
                "VALUES ($1, $2, 'TX', $3, 1, 10, now(), now())";
            primera.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
            primera.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
            primera.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });
            await primera.ExecuteNonQueryAsync();
        }

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var segunda = cruda.CreateCommand();
        segunda.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 11, 20, now(), now())";
        segunda.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        segunda.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        segunda.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => segunda.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_reservas_numeracion_dispositivo_activo", excepcion.ConstraintName);
    }
    // ---- rendición de cola: los tres CHECK nuevos ---------------------------------------------

    /// <summary><c>ck_reservas_numeracion_entregado_en_rango</c>, con input de CLIENTE detrás:
    /// <c>ServicioDeRendicionDeCola</c> pre-valida <c>entregadoHasta</c> contra
    /// <c>[desde - 1, hasta]</c> (por eso el camino de servicio devuelve 400
    /// <c>entregado_hasta_invalido</c> y no 500), y este backstop prueba que el esquema rechaza igual
    /// una escritura que esquive esa pre-validación. Los dos valores son los primeros FUERA del
    /// rango a cada lado del bloque <c>[5, 10]</c>.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public async Task UnEntregadoHastaFueraDelBloqueViolaLaCheckDeRangoEntregado(long entregadoHasta)
    {
        var e = await SembrarEscenarioAsync(
            $"{nameof(UnEntregadoHastaFueraDelBloqueViolaLaCheckDeRangoEntregado)}{entregadoHasta}");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 5, 10, $4, 0, now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });
        comando.Parameters.Add(new NpgsqlParameter { Value = entregadoHasta });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_reservas_numeracion_entregado_en_rango", excepcion.ConstraintName);
    }

    /// <summary>Los dos bordes VÁLIDOS del mismo CHECK (<c>desde - 1</c> = no repartió ninguno;
    /// <c>hasta</c> = agotó el bloque): sin este test, un CHECK escrito con <c>&gt;</c>/<c>&lt;</c>
    /// en vez de <c>&gt;=</c>/<c>&lt;=</c> rechazaría las dos rendiciones legítimas del borde y el
    /// test de arriba seguiría verde.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public async Task LosBordesDelBloqueNoViolanLaCheckDeRangoEntregado(long entregadoHasta)
    {
        var e = await SembrarEscenarioAsync(
            $"{nameof(LosBordesDelBloqueNoViolanLaCheckDeRangoEntregado)}{entregadoHasta}");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 5, 10, $4, 0, now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });
        comando.Parameters.Add(new NpgsqlParameter { Value = entregadoHasta });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }

    /// <summary><c>ck_reservas_numeracion_pendientes_no_negativo</c>, el segundo CHECK con input de
    /// cliente detrás (<c>ServicioDeRendicionDeCola</c> lo pre-valida con 400
    /// <c>pendientes_invalido</c>).</summary>
    [Fact]
    public async Task UnaCantidadDePendientesNegativaViolaLaCheckDeNoNegatividad()
    {
        var e = await SembrarEscenarioAsync(nameof(UnaCantidadDePendientesNegativaViolaLaCheckDeNoNegatividad));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, 5, 10, 7, -1, now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_reservas_numeracion_pendientes_no_negativo", excepcion.ConstraintName);
    }

    /// <summary><c>ck_reservas_numeracion_reporte_consistente</c>: las tres columnas del reporte son
    /// UN hecho, así que media rendición no existe. Es el único de los tres sin pre-validación
    /// posible (el único escritor legítimo escribe las tres en el mismo statement) — backstop de
    /// esquema puro, y el que sostiene el fail-closed de la guarda de cierre (<c>reportado_at IS
    /// NULL</c> ⇒ nunca rindió, sin un cuarto estado ambiguo). Se prueban las tres combinaciones de
    /// UNA columna presente.</summary>
    [Theory]
    [InlineData("7, NULL, NULL")]
    [InlineData("NULL, 0, NULL")]
    [InlineData("NULL, NULL, now()")]
    public async Task UnaRendicionAMediasViolaLaCheckDeConsistenciaDelReporte(string columnas)
    {
        var e = await SembrarEscenarioAsync(
            $"{nameof(UnaRendicionAMediasViolaLaCheckDeConsistenciaDelReporte)}{Math.Abs(columnas.GetHashCode())}");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            $"VALUES ($1, $2, 'TX', $3, 5, 10, {columnas}, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_reservas_numeracion_reporte_consistente", excepcion.ConstraintName);
    }

    /// <summary>El otro lado del MISMO CHECK: sin ninguna de las tres (bloque recién reservado, tal
    /// como lo escribe <c>InsertarReservaAsync</c>) y con las tres (una rendición completa) son los
    /// dos estados legítimos. Sin este test, un CHECK que exigiera SIEMPRE las tres columnas dejaría
    /// verde al de arriba y rompería toda reserva nueva.</summary>
    [Theory]
    [InlineData("NULL, NULL, NULL")]
    [InlineData("7, 2, now()")]
    public async Task LosDosEstadosCompletosNoViolanLaCheckDeConsistenciaDelReporte(string columnas)
    {
        var e = await SembrarEscenarioAsync(
            $"{nameof(LosDosEstadosCompletosNoViolanLaCheckDeConsistenciaDelReporte)}{Math.Abs(columnas.GetHashCode())}");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            $"VALUES ($1, $2, 'TX', $3, 5, 10, {columnas}, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        await comando.ExecuteNonQueryAsync(); // no debe tirar
    }
}
