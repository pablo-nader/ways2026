using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Ventas;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Los SEIS conjuntos del <c>WHERE</c> de
/// <see cref="AsignadorDeNumeroComprobante.RegistrarRendicionAsync"/>, uno por test, probados POR
/// DEBAJO del confound que <c>mutation-proof-tests</c> regla 3 describe: el pre-chequeo de
/// <c>ServicioDeRendicionDeCola</c> (existe bloque vivo para este tenant/PV/tipo/dispositivo, y su
/// marca de agua no baja) espeja el mismo predicado, así que cualquier prueba por HTTP muere en el
/// pre-chequeo y los seis conjuntos sobreviven a su borrado. Acá se llama al asignador DIRECTO, con
/// entradas que el servicio nunca produciría, y se afirma el valor discriminante que solo el conjunto
/// puede producir: la CANTIDAD DE FILAS AFECTADAS.
///
/// El conjunto <c>id_tenant</c> necesita además salir de abajo de un SEGUNDO confound: RLS. Con la
/// conexión de aplicación, una fila de otro tenant es invisible y el UPDATE devuelve 0 con o sin el
/// conjunto. Su test corre sobre la conexión del DUEÑO (<c>CrearContextoDeOwner</c>, el helper que
/// el fixture tiene justo para esto) — no es una prueba de RLS (regla 5), es la única forma de que
/// la cláusula de C# sea la que decide el resultado. El último test del archivo hace lo MISMO para la
/// consulta de LECTURA de la guarda de cierre (<see cref="LectorDeRendicionDeDispositivos"/>), que
/// tenía el mismo confound sin resolver (judgment-day).
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

    /// <param name="entregadoHasta">Marca de agua ya registrada, o <c>null</c> para un bloque que
    /// todavía no rindió — las tres columnas del reporte se escriben juntas
    /// (<c>ck_reservas_numeracion_reporte_consistente</c>).</param>
    private async Task SembrarBloqueAsync(
        int idTenant, int idPuntoVenta, int idDispositivo, string tipo = "TX", bool abandonado = false,
        long? entregadoHasta = null)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, abandonada_at, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 1, 10, $5, $6, $7, $8, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = tipo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idDispositivo });
        comando.Parameters.Add(new NpgsqlParameter
        {
            Value = abandonado ? DateTimeOffset.UtcNow : (object)DBNull.Value
        });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)entregadoHasta ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = entregadoHasta is null ? DBNull.Value : (object)0 });
        comando.Parameters.Add(new NpgsqlParameter
        {
            Value = entregadoHasta is null ? DBNull.Value : (object)DateTimeOffset.UtcNow
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
    /// Sin el conjunto afectaría 1, y el reporte quedaría escrito sobre un bloque muerto: la guarda
    /// de cierre SÍ mira los abandonados (su único filtro de alcance es
    /// <c>rendicion_saldada_at IS NULL</c>), pero le daría una frescura que ese bloque no puede
    /// tener, porque ya nadie puede rendir sobre él.</summary>
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

    /// <summary>Conjunto <c>$1 &gt;= COALESCE(entregado_hasta, desde - 1)</c>, el PISO MONÓTONO
    /// (judgment-day, SEVERE): el bloque ya tiene registrado hasta el 7 y llega un reporte que declara
    /// 5 — 0 filas, y el 7 sigue ahí. Sin el conjunto, ese reporte pisaba la marca de agua hacia abajo,
    /// y con <c>desde - 1</c> dejaba el rango esperado vacío: el bloque pasaba el cierre con cualquier
    /// cosa en la cola.</summary>
    [Fact]
    public async Task ElPisoMonotonoImpideBajarLaMarcaDeAgua()
    {
        var e = await SembrarAsync(nameof(ElPisoMonotonoImpideBajarLaMarcaDeAgua));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo, entregadoHasta: 7);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 5, pendientes: 0, momento: Momento);

        Assert.Equal(0, filas);
        Assert.Equal(7, await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>El otro lado del MISMO borde: el reporte que declara EXACTAMENTE la marca de agua ya
    /// registrada sí pasa (la comparación es <c>&gt;=</c>, no <c>&gt;</c>). No es cortesía: el
    /// reintento de un commit ambiguo reescribe el mismo valor, y con <c>&gt;</c> ese reintento
    /// aparecería como <c>rendicion_regresiva</c> sobre una rendición que ya había funcionado. Los dos
    /// tests juntos matan los dos mutantes de la comparación.</summary>
    [Fact]
    public async Task ElPisoMonotonoAceptaElMismoValor()
    {
        var e = await SembrarAsync(nameof(ElPisoMonotonoAceptaElMismoValor));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo, entregadoHasta: 7);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
            db, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
            entregadoHasta: 7, pendientes: 3, momento: Momento);

        Assert.Equal(1, filas);
        Assert.Equal(7, await LeerEntregadoHastaAsync(e.IdTenant, e.IdDispositivo));
    }

    /// <summary>El positivo del mismo statement, que fija que los 0 de arriba no son un UPDATE roto:
    /// con las cinco claves correctas escribe las TRES columnas del reporte y afecta una fila. Mata
    /// además el <c>COALESCE</c> del piso monótono: el bloque todavía no rindió
    /// (<c>entregado_hasta NULL</c>), y sin ese <c>COALESCE</c> la comparación contra <c>NULL</c> da
    /// <c>NULL</c> —nunca verdadero— así que ninguna PRIMERA rendición podría escribirse.</summary>
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

    /// <summary>Conjunto <c>r.id_tenant = $1</c> de la consulta de LECTURA de la guarda de cierre
    /// (<see cref="LectorDeRendicionDeDispositivos"/>), por debajo del mismo confound de RLS que el
    /// conjunto homónimo del escritor: sobre la conexión de aplicación la fila de otro tenant es
    /// invisible y la consulta devuelve vacío con o sin el conjunto, así que este test corre sobre la
    /// conexión del DUEÑO (judgment-day: el conjunto era inmatable y quedó probado acá).
    ///
    /// La mitad POSITIVA no es decorado: sin ella, "vacío" sería el resultado de cualquier consulta
    /// rota. Con el tenant propio el bloque bloquea por fail-closed; con un tenant ajeno la consulta
    /// no ve nada, y borrar el conjunto la haría ver el bloque igual (el <c>id_punto_venta</c> solo
    /// alcanza).
    ///
    /// Los otros dos conjuntos de tenant de esa consulta —<c>d.id_tenant = r.id_tenant</c> del join a
    /// <c>dispositivos</c> y <c>cv.id_tenant = r.id_tenant</c> del lateral— NO tienen test y no pueden
    /// tenerlo: espejan las FK compuestas (ADR-9) sobre columnas que ya fijan el tenant por sí mismas
    /// (<c>id_dispositivo</c> e <c>id_punto_venta</c> son identidades globales, cada una de un solo
    /// tenant), así que borrarlos no cambia NINGÚN resultado, ni sobre la conexión del dueño. Se
    /// documenta en vez de fingir que están cubiertos.</summary>
    [Fact]
    public async Task ElConjuntoDeTenantDeLaGuardaDeCierreNoMiraElBloqueDeOtroTenant()
    {
        var e = await SembrarAsync(nameof(ElConjuntoDeTenantDeLaGuardaDeCierreNoMiraElBloqueDeOtroTenant));
        await SembrarBloqueAsync(e.IdTenant, e.IdPuntoVenta, e.IdDispositivo);

        await using var db = fixture.CrearContextoDeOwner(TenantActualFijo.Plataforma);
        var lector = new LectorDeRendicionDeDispositivos(db);

        var propio = await lector.LeerPendientesAsync(e.IdTenant, e.IdPuntoVenta, Momento);
        var ajeno = await lector.LeerPendientesAsync(e.IdTenant + 9_000, e.IdPuntoVenta, Momento);

        Assert.Equal(MotivoDeRendicionPendiente.SinReporte, Assert.Single(propio).Motivo);
        Assert.Empty(ajeno);
    }
}
