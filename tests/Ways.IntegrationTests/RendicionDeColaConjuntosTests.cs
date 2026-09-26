using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Ventas;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Los CINCO conjuntos del <c>WHERE</c> de
/// <see cref="AsignadorDeNumeroComprobante.RegistrarRendicionAsync"/>, uno por test, probados POR
/// DEBAJO del confound que <c>mutation-proof-tests</c> regla 3 describe: el pre-chequeo de
/// <c>ServicioDeRendicionDeCola</c> (existe bloque vivo para este tenant/PV/tipo/dispositivo)
/// espeja el mismo predicado, así que cualquier prueba por HTTP muere en el pre-chequeo y los cinco
/// conjuntos sobreviven a su borrado. Acá se llama al asignador DIRECTO, con entradas que el
/// servicio nunca produciría, y se afirma el valor discriminante que solo el conjunto puede
/// producir: la CANTIDAD DE FILAS AFECTADAS.
///
/// El conjunto <c>id_tenant</c> necesita además salir de abajo de un SEGUNDO confound: RLS. Con la
/// conexión de aplicación, una fila de otro tenant es invisible y el UPDATE devuelve 0 con o sin el
/// conjunto. Su test corre sobre la conexión del DUEÑO (<c>CrearContextoDeOwner</c>, el helper que
/// el fixture tiene justo para esto) — no es una prueba de RLS (regla 5), es la única forma de que
/// la cláusula de C# sea la que decide el resultado.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RendicionDeColaConjuntosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);

    private sealed record Escenario(
        int IdTenant, int IdPuntoVenta, int IdPuntoVentaAlterno, int IdDispositivo, int IdDispositivoAlterno);

    /// <summary>Siembra por EF directo (bypass del endpoint) — mismo trámite exacto que
    /// <see cref="ReservaDeNumeracionBackstopTests"/>, más un segundo punto de venta y un segundo
    /// dispositivo: los conjuntos de PV y de dispositivo solo se pueden aislar con un hermano al
    /// que el UPDATE no tiene que tocar.</summary>
    private async Task<Escenario> SembrarAsync(string nombre)
    {
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
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = $"{nombre} propio",
            Modo = ModoPuntoVenta.Escritorio, CreatedAt = ahora, UpdatedAt = ahora
        };
        var puntoVentaAlterno = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = $"{nombre} alterno",
            Modo = ModoPuntoVenta.Escritorio, CreatedAt = ahora, UpdatedAt = ahora
        };
        siembra.PuntosVenta.AddRange(puntoVenta, puntoVentaAlterno);
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
            IdTenant = tenant.Id, IdPuntoVenta = puntoVenta.Id, Nombre = "Caja propia",
            TokenHash = Guid.NewGuid().ToString("N").PadRight(64, '0'), IdUsuarioAlta = usuario.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        // En el punto de venta ALTERNO: ux_dispositivos_punto_venta admite un solo dispositivo
        // vigente por punto de venta. El bloque que este hermano recibe más abajo sí se siembra
        // contra el punto de venta propio — reservas_numeracion no ata dispositivo a punto de
        // venta, y es justamente la entrada que las capas de arriba nunca producirían que
        // mutation-proof-tests regla 3 pide para aislar el conjunto.
        var dispositivoAlterno = new Dispositivo
        {
            IdTenant = tenant.Id, IdPuntoVenta = puntoVentaAlterno.Id, Nombre = "Caja hermana",
            TokenHash = Guid.NewGuid().ToString("N").PadRight(64, '0'), IdUsuarioAlta = usuario.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        siembra.Dispositivos.AddRange(dispositivo, dispositivoAlterno);
        await siembra.SaveChangesAsync();

        return new Escenario(
            tenant.Id, puntoVenta.Id, puntoVentaAlterno.Id, dispositivo.Id, dispositivoAlterno.Id);
    }

    private async Task SembrarBloqueAsync(
        int idTenant, int idPuntoVenta, int idDispositivo, string tipo = "TX", bool abandonado = false)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, abandonada_at, " +
            " created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 1, 10, $5, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = tipo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idDispositivo });
        comando.Parameters.Add(new NpgsqlParameter
        {
            Value = abandonado ? DateTimeOffset.UtcNow : (object)DBNull.Value
        });
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<long?> LeerEntregadoHastaAsync(int idTenant, int idDispositivo)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT entregado_hasta FROM reservas_numeracion WHERE id_dispositivo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idDispositivo });

        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? null : Convert.ToInt64(valor);
    }

    /// <summary>Conjunto <c>id_tenant = $4</c>. Sobre la conexión del DUEÑO (ver el doc-comment de
    /// la clase): con el conjunto vivo, un tenant ajeno afecta 0 filas; borrándolo, el trío
    /// (PV, tipo, dispositivo) alcanza para pisar la fila y afectaría 1.</summary>
    [Fact]
    public async Task ElConjuntoDeTenantImpidePisarElBloqueDeOtroTenant()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDeTenantImpidePisarElBloqueDeOtroTenant));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo);

        await using var db = fixture.CrearContextoDeOwner(TenantActualFijo.Plataforma);
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant + 9_000, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(0, filas);
        Assert.Null(await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>Conjunto <c>id_punto_venta = $5</c>: el bloque es del PV propio, la rendición llega
    /// con el PV hermano del MISMO tenant — 0 filas. Sin el conjunto, (tenant, tipo, dispositivo)
    /// alcanza y afectaría 1.</summary>
    [Fact]
    public async Task ElConjuntoDePuntoDeVentaImpidePisarElBloqueDeOtroPuntoDeVenta()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDePuntoDeVentaImpidePisarElBloqueDeOtroPuntoDeVenta));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVentaAlterno, "TX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(0, filas);
        Assert.Null(await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>Conjunto <c>tipo_comprobante = $6</c>: cada serie numera aparte, así que rendir
    /// sobre NCX no puede tocar el bloque de TX — 0 filas. Sin el conjunto afectaría 1.</summary>
    [Fact]
    public async Task ElConjuntoDeTipoDeComprobanteImpidePisarElBloqueDeOtraSerie()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDeTipoDeComprobanteImpidePisarElBloqueDeOtraSerie));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo, tipo: "TX");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "NCX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(0, filas);
        Assert.Null(await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>Conjunto <c>id_dispositivo = $7</c>: dos dispositivos del MISMO punto de venta, cada
    /// uno con su bloque vivo. La rendición del primero afecta EXACTAMENTE una fila y deja la del
    /// hermano intacta — sin el conjunto pisaría las dos (2 filas), y el hermano perdería su
    /// reporte.</summary>
    [Fact]
    public async Task ElConjuntoDeDispositivoNoPisaElBloqueDelHermano()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDeDispositivoNoPisaElBloqueDelHermano));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo);
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivoAlterno);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(1, filas);
        Assert.Equal(5, await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
        Assert.Null(await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivoAlterno));
    }

    /// <summary>Conjunto <c>abandonada_at IS NULL</c>: un bloque abandonado ya no reparte números,
    /// así que rendir sobre él no tiene sentido — 0 filas, y el 409
    /// <c>rendicion_sin_bloque_vivo</c> que el servicio devuelve sale justamente de este conteo.
    /// Sin el conjunto afectaría 1, y el reporte quedaría escrito sobre un bloque muerto, invisible
    /// para la guarda de cierre (que también filtra por <c>abandonada_at IS NULL</c>).</summary>
    [Fact]
    public async Task ElConjuntoDeAbandonadaImpideRendirSobreUnBloqueMuerto()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDeAbandonadaImpideRendirSobreUnBloqueMuerto));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo, abandonado: true);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(0, filas);
        Assert.Null(await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>El positivo del mismo statement, que fija que los 0 de arriba no son un UPDATE roto:
    /// con las cinco claves correctas escribe las TRES columnas del reporte y afecta una fila.</summary>
    [Fact]
    public async Task ConLasCincoClavesCorrectasEscribeElReporte()
    {
        var e = await SembrarAsync(nameof(ConLasCincoClavesCorrectasEscribeElReporte));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 7, pendientes: 2, momento: Momento);

        Assert.Equal(1, filas);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT entregado_hasta, pendientes, reportado_at, updated_at FROM reservas_numeracion " +
            "WHERE id_dispositivo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        Assert.Equal(7L, lector.GetInt64(0));
        Assert.Equal(2, lector.GetInt32(1));
        Assert.Equal(Momento, lector.GetFieldValue<DateTimeOffset>(2));
        Assert.Equal(Momento, lector.GetFieldValue<DateTimeOffset>(3));
    }
}
