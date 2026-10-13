using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T4: después de registrar una venta del POS el servidor decide si el ticket entero se
/// factura (<c>ServicioDeDecisionDeFacturacion</c>), en una transacción propia y sin poder hacer fallar la venta.
/// Se ejercita por <c>POST /api/ventas</c> (siempre con un cajero de dispositivo, que además puede reenviar con número
/// pre-asignado) y, para fechas fijas y concurrencia, llamando al servicio sobre ventas sembradas.
///
/// Una decisión que falla deja la venta debida (marca en <c>NULL</c>), anota el intento con una espera creciente y la
/// completa el barrido que corre con una venta siguiente del mismo punto de venta cuando la espera venció
/// (<c>SanearDecisionesDebidasAsync</c>).
///
/// Mutation-proof-tests: las cláusulas bajo prueba son el gate del certificado (sin certificado activo del ambiente
/// configurado la venta se marca, con o sin certificados de otro ambiente, y el aviso del log se limita por empresa y por
/// hora), la marca de idempotencia, el <c>FOR UPDATE</c> del acumulado, la derivación del período,
/// <c>ForzarFactura</c> en <c>ExigirMismoContenido</c>, el barrido (llamada desde el endpoint, límite, orden, aislamiento de
/// fallas, filtro por la espera, corte por su presupuesto sin anotar fallo), la anotación de cada fallo y su espera
/// creciente con su tope, el presupuesto de la decisión y el salto del barrido tras una decisión sin decidir.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DecisionDeFacturacionAlIngerirTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroSembrado = 1_000_000;

    private sealed record Escenario(
        HttpClient Cajero, int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleado, int IdArticulo,
        int IdEfectivo, int IdTransferencia, int IdCliente, int IdTipoTx, int IdTipoNcx, int IdTipoFa);

    private sealed record Acumulado(int IdMedioPago, DateOnly Periodo, decimal Saldo, decimal Vendido, decimal Facturado);

    private sealed record Decision(int IdComprobanteVenta, string Motivo, string Estado, string Aportes, DateOnly Periodo);

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    // ---- Siembra ------------------------------------------------------------------------------

    private async Task<Escenario> PrepararAsync(string nombre, bool conCertificado = true)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}";
        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new Ways.Application.Organizacion.SolicitudDeAprovisionamiento(
                unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<Ways.Application.Organizacion.ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        int idArticulo, idEfectivo, idTransferencia, idCliente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
                IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
                Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });

            var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
            var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            var articulo = new Articulo
            {
                IdTenant = resultado.IdTenant, CodigoInterno = "servicio-fiscal", Nombre = "Servicio de prueba",
                IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = false,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();

            db.Precios.Add(new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral, Monto = 100m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });

            if (conCertificado)
            {
                db.CertificadosFiscales.Add(new CertificadoFiscal
                {
                    IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Ambiente = AmbienteFiscal.Homologacion,
                    Alias = "Homo de prueba", CuitTitular = "20111111112", CertificadoPem = "pem",
                    ClavePrivadaCifrada = new byte[48], Nonce = new byte[12], TagAutenticacion = new byte[16],
                    IdClaveMaestra = "v1", HuellaSha256 = new string('a', 64), VigenciaDesde = ahora.AddDays(-1),
                    VigenciaHasta = ahora.AddYears(1), Activo = true, CreatedAt = ahora, UpdatedAt = ahora
                });
            }

            await db.SaveChangesAsync();

            idArticulo = articulo.Id;
            idEfectivo = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
            idTransferencia = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
            idCliente = await db.Clientes.OrderBy(c => c.Numero).Select(c => c.Id).FirstAsync();
        }

        var cajero = await LoguearComoCajeroDeDispositivoAsync(admin, resultado.IdTenant, resultado.IdPuntoVenta);

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(resultado.IdPuntoVenta, "TX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoTx = await plataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoNcx = await plataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();
        var idTipoFa = await plataforma.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync();

        return new Escenario(
            cajero, resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin,
            idArticulo, idEfectivo, idTransferencia, idCliente, idTipoTx, idTipoNcx, idTipoFa);
    }

    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(HttpClient admin, int idTenant, int idPuntoVenta)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        var cookieDispositivo = valor[..valor.IndexOf(';')];

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant, NombreUsuario = "cajero-fiscal", Mail = $"cajero-fiscal-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor, PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-fiscal", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cajero;
    }

    private async Task AgregarCertificadoAsync(Escenario e, AmbienteFiscal ambiente = AmbienteFiscal.Homologacion)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.CertificadosFiscales.Add(new CertificadoFiscal
        {
            IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, Ambiente = ambiente,
            Alias = "Homo agregado", CuitTitular = "20111111112", CertificadoPem = "pem",
            ClavePrivadaCifrada = new byte[48], Nonce = new byte[12], TagAutenticacion = new byte[16],
            IdClaveMaestra = "v1", HuellaSha256 = new string('b', 64), VigenciaDesde = ahora.AddDays(-1),
            VigenciaHasta = ahora.AddYears(1), Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Con <paramref name="zona"/> define el parámetro de zona horaria de la empresa; con <c>null</c> lo quita.</summary>
    private async Task DefinirZonaHorariaAsync(Escenario e, string? zona)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.Parametros.RemoveRange(await db.Parametros.Where(p => p.Clave == ParametroConocido.ZonaHoraria.Clave).ToListAsync());
        if (zona is not null)
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Parametros.Add(new Parametro
            {
                IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, Clave = ParametroConocido.ZonaHoraria.Clave,
                Valor = JsonSerializer.Serialize(zona), CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<int, DateTimeOffset?>> LeerMarcasAsync(int idTenant)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT id_comprobante_venta, facturacion_decidida_en FROM comprobantes_venta";
        var marcas = new Dictionary<int, DateTimeOffset?>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            marcas[lector.GetInt32(0)] = lector.IsDBNull(1) ? null : new DateTimeOffset(lector.GetDateTime(1), TimeSpan.Zero);
        }

        return marcas;
    }

    private async Task FijarPorcentajeAsync(Escenario e, int idMedioPago, decimal porcentaje)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var medio = await db.MediosPago.SingleAsync(m => m.Id == idMedioPago);
        medio.PorcentajeFacturacion = porcentaje;
        await db.SaveChangesAsync();
    }

    /// <summary>Un ticket de 100 con el pago dado; con <paramref name="numero"/> viaja como el reenvío de una venta
    /// offline (número pre-asignado y precio congelado).</summary>
    private static SolicitudDeVenta Venta(
        Escenario e, IReadOnlyList<PagoDeVenta> pagos, bool forzar = false, long? numero = null) =>
        new(e.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(e.IdArticulo, 1m, null, PrecioUnitario: numero is null ? null : 100m)],
            pagos, null, null, NumeroPreasignado: numero, ForzarFactura: forzar);

    private static IReadOnlyList<PagoDeVenta> EnEfectivo(Escenario e, decimal importe = 100m, decimal vuelto = 0m) =>
        [new PagoDeVenta(e.IdEfectivo, importe, null, vuelto)];

    private static IReadOnlyList<PagoDeVenta> PorTransferencia(Escenario e) =>
        [new PagoDeVenta(e.IdTransferencia, 100m, "OP-1", 0m)];

    private static async Task<ComprobanteEmitido> VenderAsync(Escenario e, SolicitudDeVenta solicitud)
    {
        var respuesta = await e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private async Task<int> SembrarVentaAsync(
        Escenario e, DateTimeOffset fecha, int idMedioPago, decimal importe = 100m, decimal vuelto = 0m,
        EstadoComprobante estado = EstadoComprobante.Emitido, int? idTipo = null, int? idVentaOrigen = null,
        bool conPago = true, IReadOnlyList<(int IdMedioPago, decimal Importe, decimal Vuelto)>? pagosAdicionales = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var espejo = idVentaOrigen is not null;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = e.IdTenant, IdTipoComprobante = idTipo ?? e.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSembrado), Fecha = fecha, IdPuntoVenta = e.IdPuntoVenta,
            IdEmpleado = e.IdEmpleado, IdCliente = e.IdCliente, Subtotal = importe - vuelto, Total = importe - vuelto,
            Estado = estado, IdVentaOrigen = idVentaOrigen,
            Cae = espejo ? "12345678901234" : null, CaeVencimiento = espejo ? new DateOnly(2026, 5, 20) : null,
            ResultadoFiscal = espejo ? ResultadoFiscal.Aprobado : null, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        if (conPago)
        {
            db.PagosComprobante.Add(new PagoComprobante
            {
                IdTenant = e.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedioPago,
                Importe = importe, Vuelto = vuelto, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        foreach (var (idMedio, importeExtra, vueltoExtra) in pagosAdicionales ?? [])
        {
            db.PagosComprobante.Add(new PagoComprobante
            {
                IdTenant = e.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedio,
                Importe = importeExtra, Vuelto = vueltoExtra, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    private (ServicioDeDecisionDeFacturacion Servicio, WaysDbContext Db) CrearServicio(
        Escenario e, IRelojDelSistema? relojDelServicio = null, OpcionesFiscales? opciones = null,
        ILogger<ServicioDeDecisionDeFacturacion>? log = null)
    {
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        var reloj = new RelojDelSistema();
        var parametros = new ServicioDeParametros(db, reloj, new ServicioDeLotes(db, reloj, new ContextoFijo(e.IdTenant, e.IdEmpleado)));
        var servicio = new ServicioDeDecisionDeFacturacion(
            db, relojDelServicio ?? reloj, Options.Create(opciones ?? new OpcionesFiscales()), parametros,
            log ?? NullLogger<ServicioDeDecisionDeFacturacion>.Instance);
        return (servicio, db);
    }

    /// <summary>Reloj que el test mueve a mano: la espera de los intentos fallidos se prueba sin dormir.</summary>
    private sealed class RelojControlable(DateTimeOffset inicio) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; set; } = inicio;
    }

    private sealed class RegistroDeLog : ILogger<ServicioDeDecisionDeFacturacion>
    {
        private readonly List<(LogLevel Nivel, string Mensaje)> _entradas = [];

        public IReadOnlyList<(LogLevel Nivel, string Mensaje)> Entradas
        {
            get
            {
                lock (_entradas)
                {
                    return [.. _entradas];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entradas)
            {
                _entradas.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static readonly DateTimeOffset InicioDelReloj = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed record ContabilidadDeIntentos(int Intentos, DateTimeOffset? ProximoIntento, DateTimeOffset? Marca);

    private async Task<ContabilidadDeIntentos> LeerContabilidadAsync(int idTenant, int idComprobante)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT facturacion_intentos, facturacion_proximo_intento_en, facturacion_decidida_en " +
            "FROM comprobantes_venta WHERE id_comprobante_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        return new ContabilidadDeIntentos(
            lector.GetInt32(0),
            lector.IsDBNull(1) ? null : lector.GetFieldValue<DateTimeOffset>(1),
            lector.IsDBNull(2) ? null : lector.GetFieldValue<DateTimeOffset>(2));
    }

    private async Task FijarContabilidadAsync(int idTenant, int idComprobante, int intentos, DateTimeOffset? proximo)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE comprobantes_venta SET facturacion_intentos = $2, facturacion_proximo_intento_en = $3 " +
            "WHERE id_comprobante_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        comando.Parameters.Add(new NpgsqlParameter { Value = intentos });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)proximo ?? DBNull.Value });
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    /// <summary>Siembra una venta en efectivo por el máximo de <c>numeric(14,2)</c>: si el acumulado del mes de
    /// <paramref name="fecha"/> ya tiene un monto vendido, sumarla desborda <c>total_vendido</c> y su decisión falla en cada
    /// intento. Quien la use decide antes otro ticket del mismo mes.</summary>
    private async Task<int> SembrarVentaQueSiempreFallaAsync(Escenario e, DateTimeOffset fecha) =>
        await SembrarVentaAsync(e, fecha, e.IdEfectivo, importe: 999_999_999_999.99m);

    private async Task<EstadoFiscalDeVenta> DecidirAsync(Escenario e, int idComprobante)
    {
        var (servicio, db) = CrearServicio(e);
        await using var _ = db;
        return await servicio.DecidirAsync(idComprobante);
    }

    // ---- Lecturas crudas (ways_app con RLS) ----------------------------------------------------

    private async Task<List<Acumulado>> LeerAcumuladosAsync(int idTenant)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_medio_pago, periodo, saldo_a_facturar, total_vendido, total_facturado " +
            "FROM acumulados_de_facturacion ORDER BY id_medio_pago, periodo";
        var filas = new List<Acumulado>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new Acumulado(
                lector.GetInt32(0), DateOnly.FromDateTime(lector.GetDateTime(1)), lector.GetDecimal(2),
                lector.GetDecimal(3), lector.GetDecimal(4)));
        }

        return filas;
    }

    private async Task<List<Decision>> LeerDecisionesAsync(int idTenant)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_comprobante_venta, motivo::text, estado::text, aportes::text, periodo " +
            "FROM facturaciones_de_venta ORDER BY id_facturacion_venta";
        var filas = new List<Decision>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new Decision(
                lector.GetInt32(0), lector.GetString(1), lector.GetString(2), lector.GetString(3),
                DateOnly.FromDateTime(lector.GetDateTime(4))));
        }

        return filas;
    }

    private async Task<DateTimeOffset?> LeerMarcaDeDecisionAsync(int idTenant, int idComprobante)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT facturacion_decidida_en FROM comprobantes_venta WHERE id_comprobante_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
        var valor = await comando.ExecuteScalarAsync();
        return valor is DateTime fecha ? new DateTimeOffset(fecha, TimeSpan.Zero) : null;
    }

    private static void AfirmarAcumulado(
        IEnumerable<Acumulado> filas, int idMedio, decimal saldo, decimal vendido, decimal facturado)
    {
        var fila = Assert.Single(filas, f => f.IdMedioPago == idMedio);
        Assert.Equal((saldo, vendido, facturado), (fila.Saldo, fila.Vendido, fila.Facturado));
    }

    /// <summary>Suma los meses del medio: las ventas sembradas con fecha fija y las del POS (que usan la hora de hoy) caen
    /// en meses distintos.</summary>
    private static void AfirmarTotalesDelMedio(
        IEnumerable<Acumulado> filas, int idMedio, decimal saldo, decimal vendido, decimal facturado)
    {
        var delMedio = filas.Where(f => f.IdMedioPago == idMedio).ToList();
        Assert.Equal(
            (saldo, vendido, facturado),
            (delMedio.Sum(f => f.Saldo), delMedio.Sum(f => f.Vendido), delMedio.Sum(f => f.Facturado)));
    }

    private static readonly DateTimeOffset Mayo = new(2026, 5, 10, 15, 0, 0, TimeSpan.Zero);

    // ---- Decisión por la API --------------------------------------------------------------------

    [Fact]
    public async Task UnMedioAl100PorCientoFacturaElTicketYMueveElAcumulado()
    {
        var e = await PrepararAsync(nameof(UnMedioAl100PorCientoFacturaElTicketYMueveElAcumulado));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var emitido = await VenderAsync(e, Venta(e, PorTransferencia(e)));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Equal("medio_exige_factura", emitido.Fiscal.Motivo);
        var decision = Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        Assert.Equal((emitido.Id, "medio_exige_factura", "pendiente"), (decision.IdComprobanteVenta, decision.Motivo, decision.Estado));
        Assert.NotNull(emitido.Fiscal.IdFacturacion);
        Assert.Equal(1, decision.Periodo.Day);

        using var aportes = JsonDocument.Parse(decision.Aportes);
        var aporte = Assert.Single(aportes.RootElement.EnumerateArray());
        Assert.Equal(e.IdTransferencia, aporte.GetProperty("idMedioPago").GetInt32());
        Assert.Equal(0m, aporte.GetProperty("aporte").GetDecimal());

        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, emitido.Id));
    }

    [Fact]
    public async Task UnPorcentajeParcialFacturaSoloLosTicketsQueLaPoliticaElige()
    {
        var e = await PrepararAsync(nameof(UnPorcentajeParcialFacturaSoloLosTicketsQueLaPoliticaElige));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);

        var estados = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            estados.Add((await VenderAsync(e, Venta(e, EnEfectivo(e)))).Fiscal!.Estado);
        }

        // Cada ticket suma 10 al saldo; el sexto lo deja en 60, por encima de la mitad del importe, y se factura.
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => i == 6 ? EstadosFiscalesDeVenta.Pendiente : EstadosFiscalesDeVenta.NoCorresponde),
            estados);
        var decision = Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        Assert.Equal("cupo_alcanzado", decision.Motivo);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 0m, vendido: 1000m, facturado: 100m);
    }

    [Fact]
    public async Task UnPagoMixtoConUnMedioAl100PorCientoFacturaElTicketCompleto()
    {
        var e = await PrepararAsync(nameof(UnPagoMixtoConUnMedioAl100PorCientoFacturaElTicketCompleto));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var emitido = await VenderAsync(e, Venta(e, [
            new PagoDeVenta(e.IdEfectivo, 60m, null, 0m),
            new PagoDeVenta(e.IdTransferencia, 40m, "OP-2", 0m)]));

        Assert.Equal("medio_exige_factura", emitido.Fiscal!.Motivo);
        var acumulados = await LeerAcumuladosAsync(e.IdTenant);
        // El efectivo devenga 6 y, facturado el ticket entero, descuenta sus 60; la transferencia devenga 40 y descuenta 40.
        AfirmarAcumulado(acumulados, e.IdEfectivo, saldo: -54m, vendido: 60m, facturado: 60m);
        AfirmarAcumulado(acumulados, e.IdTransferencia, saldo: 0m, vendido: 40m, facturado: 40m);
    }

    [Fact]
    public async Task ElForzadoFacturaUnMedioAl0PorCientoYDescuentaDelCupo()
    {
        var e = await PrepararAsync(nameof(ElForzadoFacturaUnMedioAl0PorCientoYDescuentaDelCupo));
        using var _ = e.Cajero;

        var emitido = await VenderAsync(e, Venta(e, EnEfectivo(e), forzar: true));

        Assert.True(emitido.FacturaForzada);
        Assert.Equal(("pendiente", "forzada"), (emitido.Fiscal!.Estado, emitido.Fiscal.Motivo));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: -100m, vendido: 100m, facturado: 100m);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        Assert.True(await db.ComprobantesVenta.Where(c => c.Id == emitido.Id).Select(c => c.FacturaForzada).SingleAsync());
    }

    [Fact]
    public async Task UnMedioAl0PorCientoNoFacturaPeroSuVentaSiAcumula()
    {
        var e = await PrepararAsync(nameof(UnMedioAl0PorCientoNoFacturaPeroSuVentaSiAcumula));
        using var _ = e.Cajero;

        var emitido = await VenderAsync(e, Venta(e, EnEfectivo(e)));

        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, emitido.Fiscal!.Estado);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 0m, vendido: 100m, facturado: 0m);
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, emitido.Id));
    }

    [Fact]
    public async Task ElMontoDeUnPagoEsElImporteMenosElVuelto()
    {
        var e = await PrepararAsync(nameof(ElMontoDeUnPagoEsElImporteMenosElVuelto));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);

        await VenderAsync(e, Venta(e, EnEfectivo(e, importe: 200m, vuelto: 100m)));

        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 10m, vendido: 100m, facturado: 0m);
    }

    [Fact]
    public async Task SinCertificadoActivoLaVentaQuedaDecididaComoNoAplicableSinFilaNiAcumulado()
    {
        var e = await PrepararAsync(
            nameof(SinCertificadoActivoLaVentaQuedaDecididaComoNoAplicableSinFilaNiAcumulado), conCertificado: false);
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var emitido = await VenderAsync(e, Venta(e, PorTransferencia(e), forzar: true));

        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, emitido.Fiscal!.Estado);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, emitido.Id));
    }

    [Fact]
    public async Task UnaVentaHechaSinCertificadoNoSeFacturaRetroactivamenteCuandoApareceUnCertificado()
    {
        var e = await PrepararAsync(
            nameof(UnaVentaHechaSinCertificadoNoSeFacturaRetroactivamenteCuandoApareceUnCertificado), conCertificado: false);
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var sinCertificado = await VenderAsync(e, Venta(e, PorTransferencia(e)));
        var marcaOriginal = await LeerMarcaDeDecisionAsync(e.IdTenant, sinCertificado.Id);
        Assert.NotNull(marcaOriginal);

        await AgregarCertificadoAsync(e);
        var conCertificado = await VenderAsync(e, Venta(e, PorTransferencia(e)));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, conCertificado.Fiscal!.Estado);
        var decision = Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        Assert.Equal(conCertificado.Id, decision.IdComprobanteVenta);
        Assert.Equal(marcaOriginal, await LeerMarcaDeDecisionAsync(e.IdTenant, sinCertificado.Id));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
    }

    [Fact]
    public async Task UnaDevolucionNuncaSeDecideYForzarFacturaEnEllaSeRechaza()
    {
        var e = await PrepararAsync(nameof(UnaDevolucionNuncaSeDecideYForzarFacturaEnEllaSeRechaza));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var lineas = new[] { new LineaDeVenta(e.IdArticulo, 1m, null) };

        var rechazada = await e.Cajero.PostAsJsonAsync(
            "/api/ventas", new SolicitudDeVenta(e.IdPuntoVenta, null, "NCX", null, lineas, [], null, null, ForzarFactura: true));
        Assert.Equal(HttpStatusCode.BadRequest, rechazada.StatusCode);
        Assert.Equal(
            "forzar_factura_no_admitido",
            (await rechazada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());

        var emitido = await VenderAsync(
            e, new SolicitudDeVenta(e.IdPuntoVenta, null, "NCX", null, lineas, [], null, null));
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, emitido.Fiscal!.Estado);
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
        // La devolución no se decide pero la decisión la examinó: queda marcada para que no figure como debida.
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, emitido.Id));
    }

    // ---- Reenvío con número pre-asignado ----------------------------------------------------------

    [Fact]
    public async Task ElReenvioDeUnaVentaFacturadaDevuelveLaMismaDecisionSinMoverElAcumulado()
    {
        var e = await PrepararAsync(nameof(ElReenvioDeUnaVentaFacturadaDevuelveLaMismaDecisionSinMoverElAcumulado));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var primera = await VenderAsync(e, Venta(e, PorTransferencia(e), numero: 1));
        var acumuladoTrasLaPrimera = await LeerAcumuladosAsync(e.IdTenant);
        var segunda = await VenderAsync(e, Venta(e, PorTransferencia(e), numero: 1));

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(primera.Fiscal, segunda.Fiscal);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, segunda.Fiscal!.Estado);
        Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        Assert.Equal(acumuladoTrasLaPrimera, await LeerAcumuladosAsync(e.IdTenant));
    }

    [Fact]
    public async Task ElReenvioDeUnaVentaQueNoSeFacturaNoVuelveAContarla()
    {
        var e = await PrepararAsync(nameof(ElReenvioDeUnaVentaQueNoSeFacturaNoVuelveAContarla));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);

        await VenderAsync(e, Venta(e, EnEfectivo(e), numero: 1));
        var segunda = await VenderAsync(e, Venta(e, EnEfectivo(e), numero: 1));

        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, segunda.Fiscal!.Estado);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 10m, vendido: 100m, facturado: 0m);
    }

    [Fact]
    public async Task ReenviarElMismoNumeroConOtroForzarFacturaSeRechazaComoOtroContenido()
    {
        var e = await PrepararAsync(nameof(ReenviarElMismoNumeroConOtroForzarFacturaSeRechazaComoOtroContenido));
        using var _ = e.Cajero;

        await VenderAsync(e, Venta(e, EnEfectivo(e), numero: 1));
        var reenvio = await e.Cajero.PostAsJsonAsync("/api/ventas", Venta(e, EnEfectivo(e), forzar: true, numero: 1));

        Assert.Equal(HttpStatusCode.Conflict, reenvio.StatusCode);
        Assert.Equal(
            "numero_preasignado_con_otro_contenido",
            (await reenvio.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
    }

    // ---- Una decisión que falla no frena la caja ------------------------------------------------------

    [Fact]
    public async Task UnaDecisionQueFallaNoFallaLaVentaYSeCompletaEnElReenvio()
    {
        var e = await PrepararAsync(nameof(UnaDecisionQueFallaNoFallaLaVentaYSeCompletaEnElReenvio));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        // Una zona horaria inválida hace fallar la derivación del período, ya con la venta comiteada.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Parametros.Add(new Parametro
            {
                IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, Clave = ParametroConocido.ZonaHoraria.Clave,
                Valor = "\"Nowhere/Land\"", CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var primera = await VenderAsync(e, Venta(e, PorTransferencia(e), numero: 1));

        Assert.Equal(EstadosFiscalesDeVenta.SinDecidir, primera.Fiscal!.Estado);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
        Assert.Null(await LeerMarcaDeDecisionAsync(e.IdTenant, primera.Id));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            db.Parametros.RemoveRange(await db.Parametros.Where(p => p.Clave == ParametroConocido.ZonaHoraria.Clave).ToListAsync());
            await db.SaveChangesAsync();
        }

        var reenvio = await VenderAsync(e, Venta(e, PorTransferencia(e), numero: 1));

        Assert.Equal(primera.Id, reenvio.Id);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, reenvio.Fiscal!.Estado);
        Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
    }

    // ---- Barrido de las decisiones debidas ------------------------------------------------------------------

    [Fact]
    public async Task LaVentaCuyaDecisionFalloSeDecideEnLaSiguienteVentaDelPuntoDeVentaConSuPropioAcumuladoYUnaSolaVez()
    {
        var e = await PrepararAsync(
            nameof(LaVentaCuyaDecisionFalloSeDecideEnLaSiguienteVentaDelPuntoDeVentaConSuPropioAcumuladoYUnaSolaVez));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await DefinirZonaHorariaAsync(e, "Nowhere/Land");

        var fallida = await VenderAsync(e, Venta(e, PorTransferencia(e)));
        Assert.Equal(EstadosFiscalesDeVenta.SinDecidir, fallida.Fiscal!.Estado);
        Assert.Null(await LeerMarcaDeDecisionAsync(e.IdTenant, fallida.Id));

        await DefinirZonaHorariaAsync(e, null);
        var siguiente = await VenderAsync(e, Venta(e, PorTransferencia(e)));

        // La fallida espera su turno (un minuto tras el primer fallo): la venta siguiente no la barre todavía.
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, siguiente.Fiscal!.Estado);
        Assert.Null(await LeerMarcaDeDecisionAsync(e.IdTenant, fallida.Id));
        Assert.Equal(siguiente.Id, Assert.Single(await LeerDecisionesAsync(e.IdTenant)).IdComprobanteVenta);

        // Vencida la espera (se adelanta la fecha en la base, sin dormir), la venta siguiente la completa.
        await FijarContabilidadAsync(e.IdTenant, fallida.Id, intentos: 1, proximo: DateTimeOffset.UtcNow.AddMinutes(-1));
        var tercera = await VenderAsync(e, Venta(e, PorTransferencia(e)));

        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, fallida.Id));
        Assert.Equal(
            [fallida.Id, siguiente.Id, tercera.Id],
            (await LeerDecisionesAsync(e.IdTenant)).Select(d => d.IdComprobanteVenta).Order().ToArray());
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 300m, facturado: 300m);

        // Una cuarta venta no vuelve a contar ninguna de las anteriores.
        await VenderAsync(e, Venta(e, PorTransferencia(e)));
        Assert.Equal(4, (await LeerDecisionesAsync(e.IdTenant)).Count);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 400m, facturado: 400m);
    }

    [Fact]
    public async Task ElBarridoDecideComoMuchoElLimiteDeVentasPorLlamadaLasMasViejasPrimeroYElRestoSeVaciaEnLasSiguientes()
    {
        var e = await PrepararAsync(
            nameof(ElBarridoDecideComoMuchoElLimiteDeVentasPorLlamadaLasMasViejasPrimeroYElRestoSeVaciaEnLasSiguientes));
        using var _ = e.Cajero;
        // Al 1 % nueve tickets de 100 dejan el saldo en 9, lejos del cupo que obligaría a facturar.
        await FijarPorcentajeAsync(e, e.IdEfectivo, 1m);

        var debidas = new List<int>();
        for (var i = 0; i < ServicioDeDecisionDeFacturacion.LimiteDeBarrido + 2; i++)
        {
            debidas.Add(await SembrarVentaAsync(e, Mayo, e.IdEfectivo));
        }

        await VenderAsync(e, Venta(e, EnEfectivo(e)));

        var marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.Equal(
            debidas.Take(ServicioDeDecisionDeFacturacion.LimiteDeBarrido),
            debidas.Where(id => marcas[id] is not null));

        await VenderAsync(e, Venta(e, EnEfectivo(e)));

        marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.All(debidas, id => Assert.NotNull(marcas[id]));
        // Cada venta, sembrada o no, entró una sola vez al acumulado.
        AfirmarTotalesDelMedio(
            await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: debidas.Count + 2,
            vendido: (debidas.Count + 2) * 100m, facturado: 0m);
    }

    [Fact]
    public async Task UnaVentaDebidaQueFallaSiempreNoImpideDecidirLasQueLeSiguen()
    {
        var e = await PrepararAsync(nameof(UnaVentaDebidaQueFallaSiempreNoImpideDecidirLasQueLeSiguen));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 1m);

        // El primer ticket deja el acumulado del mes en 100. El ticket debido más viejo cobra el máximo de numeric(14,2): al
        // sumarlo, total_vendido desborda y su decisión falla siempre. Los dos siguientes son normales.
        await VenderAsync(e, Venta(e, EnEfectivo(e)));
        var hoy = DateTimeOffset.UtcNow;
        var quePermaneceDebida = await SembrarVentaAsync(e, hoy, e.IdEfectivo, importe: 999_999_999_999.99m);
        var sana1 = await SembrarVentaAsync(e, hoy, e.IdEfectivo);
        var sana2 = await SembrarVentaAsync(e, hoy, e.IdEfectivo);

        await VenderAsync(e, Venta(e, EnEfectivo(e)));

        var marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.Null(marcas[quePermaneceDebida]);
        Assert.NotNull(marcas[sana1]);
        Assert.NotNull(marcas[sana2]);

        await VenderAsync(e, Venta(e, EnEfectivo(e)));

        Assert.Null((await LeerMarcasAsync(e.IdTenant))[quePermaneceDebida]);
        // Tres ventas del POS y dos sanas: la que falla no suma y las demás no se cuentan dos veces.
        AfirmarTotalesDelMedio(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 5m, vendido: 500m, facturado: 0m);
    }

    [Fact]
    public async Task ElBarridoVaciaTodasLasClasesDeVentaDebidaYDejaIntactosLosEspejos()
    {
        var e = await PrepararAsync(nameof(ElBarridoVaciaTodasLasClasesDeVentaDebidaYDejaIntactosLosEspejos));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);

        var ticket = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var devolucion = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, idTipo: e.IdTipoNcx);
        var anulada = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, estado: EstadoComprobante.Anulado);
        var sinPagos = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, conPago: false);
        var espejo = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, idTipo: e.IdTipoFa, idVentaOrigen: ticket);

        await VenderAsync(e, Venta(e, PorTransferencia(e)));

        var marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.All(new[] { ticket, devolucion, anulada, sinPagos }, id => Assert.NotNull(marcas[id]));
        Assert.Null(marcas[espejo]);
        // Solo el ticket debido y la venta del POS se facturan; la devolución, la anulada y la sin pagos no mueven nada.
        Assert.Equal(2, (await LeerDecisionesAsync(e.IdTenant)).Count);
        AfirmarTotalesDelMedio(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 200m, facturado: 200m);

        var antes = await LeerMarcasAsync(e.IdTenant);
        await VenderAsync(e, Venta(e, PorTransferencia(e)));
        var despues = await LeerMarcasAsync(e.IdTenant);
        Assert.All(new[] { ticket, devolucion, anulada, sinPagos }, id => Assert.Equal(antes[id], despues[id]));
    }

    // ---- Intentos fallidos con espera creciente --------------------------------------------------------------

    [Fact]
    public async Task CincoVentasDebidasQueFallanSiempreNoHambreanALaVentaSanaQueLasSigue()
    {
        var e = await PrepararAsync(nameof(CincoVentasDebidasQueFallanSiempreNoHambreanALaVentaSanaQueLasSigue));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 1m);

        await VenderAsync(e, Venta(e, EnEfectivo(e)));
        var hoy = DateTimeOffset.UtcNow;
        var venenosas = new List<int>();
        for (var i = 0; i < ServicioDeDecisionDeFacturacion.LimiteDeBarrido; i++)
        {
            venenosas.Add(await SembrarVentaQueSiempreFallaAsync(e, hoy));
        }

        var sana = await SembrarVentaAsync(e, hoy, e.IdEfectivo);

        // El barrido toma las cinco más viejas, que son las venenosas: todas fallan y la sana queda fuera del límite.
        await VenderAsync(e, Venta(e, EnEfectivo(e)));
        var marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.All(venenosas, id => Assert.Null(marcas[id]));
        Assert.Null(marcas[sana]);
        foreach (var id in venenosas)
        {
            var contabilidad = await LeerContabilidadAsync(e.IdTenant, id);
            Assert.Equal(1, contabilidad.Intentos);
            Assert.NotNull(contabilidad.ProximoIntento);
        }

        // Las venenosas esperan: el barrido siguiente llega a la sana y no vuelve a tocar a las venenosas.
        await VenderAsync(e, Venta(e, EnEfectivo(e)));
        marcas = await LeerMarcasAsync(e.IdTenant);
        Assert.NotNull(marcas[sana]);
        Assert.All(venenosas, id => Assert.Null(marcas[id]));
        foreach (var id in venenosas)
        {
            Assert.Equal(1, (await LeerContabilidadAsync(e.IdTenant, id)).Intentos);
        }
    }

    [Fact]
    public async Task CadaFalloSumaUnIntentoYDuplicaLaEsperaYAlQuintoSeRegistraComoErrorSinLoguearLasSalteadas()
    {
        var e = await PrepararAsync(nameof(CadaFalloSumaUnIntentoYDuplicaLaEsperaYAlQuintoSeRegistraComoErrorSinLoguearLasSalteadas));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await DefinirZonaHorariaAsync(e, "Nowhere/Land");
        var id = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var reloj = new RelojControlable(InicioDelReloj);
        var log = new RegistroDeLog();
        var (servicio, db) = CrearServicio(e, reloj, log: log);
        await using var __ = db;

        var espera = TimeSpan.FromMinutes(1);
        for (var intento = 1; intento <= ServicioDeDecisionDeFacturacion.IntentosParaRegistrarComoError; intento++)
        {
            await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);

            var contabilidad = await LeerContabilidadAsync(e.IdTenant, id);
            Assert.Equal(intento, contabilidad.Intentos);
            Assert.Equal(reloj.Ahora + espera, contabilidad.ProximoIntento);
            Assert.Null(contabilidad.Marca);

            reloj.Ahora = contabilidad.ProximoIntento!.Value;
            espera *= 2;
        }

        // Cuatro advertencias y, al llegar al umbral, un error con la venta y la cantidad de intentos.
        Assert.Equal(
            [LogLevel.Warning, LogLevel.Warning, LogLevel.Warning, LogLevel.Warning, LogLevel.Error],
            log.Entradas.Select(x => x.Nivel));
        Assert.Contains($"venta {id} lleva 5 intentos", log.Entradas[^1].Mensaje, StringComparison.Ordinal);

        // Una candidata que todavía espera no se intenta, no cambia su contabilidad y no deja rastro en el log.
        var proximo = (await LeerContabilidadAsync(e.IdTenant, id)).ProximoIntento!.Value;
        reloj.Ahora = proximo - TimeSpan.FromSeconds(1);
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        Assert.Equal(5, (await LeerContabilidadAsync(e.IdTenant, id)).Intentos);
        Assert.Equal(5, log.Entradas.Count);
    }

    [Fact]
    public async Task LaEsperaTrasUnFalloTieneTopeYElContadorGrandeNoDesborda()
    {
        var e = await PrepararAsync(nameof(LaEsperaTrasUnFalloTieneTopeYElContadorGrandeNoDesborda));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await DefinirZonaHorariaAsync(e, "Nowhere/Land");
        var id = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        // Con 2000, power(2, 2000) desborda la precisión doble: solo el LEAST interior sobre el contador lo evita.
        await FijarContabilidadAsync(e.IdTenant, id, intentos: 2000, proximo: null);
        var reloj = new RelojControlable(InicioDelReloj);
        var (servicio, db) = CrearServicio(e, reloj);
        await using var __ = db;

        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);

        var contabilidad = await LeerContabilidadAsync(e.IdTenant, id);
        Assert.Equal(2001, contabilidad.Intentos);
        Assert.Equal(InicioDelReloj + ServicioDeDecisionDeFacturacion.EsperaMaximaTrasFallo, contabilidad.ProximoIntento);
    }

    [Fact]
    public async Task LaVentaQueFalloSeDecideUnaSolaVezCuandoVenceLaEsperaTrasCorregirLaCausa()
    {
        var e = await PrepararAsync(nameof(LaVentaQueFalloSeDecideUnaSolaVezCuandoVenceLaEsperaTrasCorregirLaCausa));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await DefinirZonaHorariaAsync(e, "Nowhere/Land");
        var id = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var reloj = new RelojControlable(InicioDelReloj);
        var (servicio, db) = CrearServicio(e, reloj);
        await using var __ = db;

        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        Assert.Equal(1, (await LeerContabilidadAsync(e.IdTenant, id)).Intentos);

        // La causa ya está corregida pero la espera sigue corriendo: el barrido no la toca.
        await DefinirZonaHorariaAsync(e, null);
        reloj.Ahora = InicioDelReloj + TimeSpan.FromSeconds(30);
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        Assert.Null((await LeerContabilidadAsync(e.IdTenant, id)).Marca);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));

        // Vencida la espera, se decide: una fila, un movimiento del acumulado y el contador queda como historia.
        reloj.Ahora = InicioDelReloj + TimeSpan.FromSeconds(61);
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);

        var contabilidad = await LeerContabilidadAsync(e.IdTenant, id);
        Assert.Equal(reloj.Ahora, contabilidad.Marca);
        Assert.Equal(1, contabilidad.Intentos);
        Assert.Equal(id, Assert.Single(await LeerDecisionesAsync(e.IdTenant)).IdComprobanteVenta);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
    }

    // ---- Presupuesto de la decisión de la venta recién emitida -----------------------------------------------

    [Fact]
    public async Task UnaDecisionBloqueadaPorUnLockDeLaVentaRespondeSinDecidirSinEsperarAlLockYLaVentaSeDecideDespues()
    {
        var e = await PrepararAsync(
            nameof(UnaDecisionBloqueadaPorUnLockDeLaVentaRespondeSinDecidirSinEsperarAlLockYLaVentaSeDecideDespues));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var bloqueada = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var otra = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var (servicio, db) = CrearServicio(e);
        await using var __ = db;

        // Otra sesión tiene tomada la fila de la venta. Se libera sola a los 20 s para que el test termine aunque el
        // servicio no respetara su presupuesto.
        var yaBloqueada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var titular = Task.Run(async () =>
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
            await using var transaccion = await conexion.BeginTransactionAsync();
            await using (var comando = conexion.CreateCommand())
            {
                comando.Transaction = transaccion;
                comando.CommandText = "SELECT 1 FROM comprobantes_venta WHERE id_comprobante_venta = $1 FOR UPDATE";
                comando.Parameters.Add(new NpgsqlParameter { Value = bloqueada });
                await comando.ExecuteScalarAsync();
            }

            yaBloqueada.SetResult();
            await Task.WhenAny(liberar.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            await transaccion.RollbackAsync();
        });
        await yaBloqueada.Task;

        var cronometro = Stopwatch.StartNew();
        var estado = await servicio.DecidirTrasLaVentaAsync(bloqueada);
        cronometro.Stop();
        liberar.SetResult();
        await titular;

        Assert.Equal(EstadoFiscalDeVenta.SinDecidir, estado);
        Assert.True(
            cronometro.Elapsed < ServicioDeDecisionDeFacturacion.PresupuestoDeDecision
                + ServicioDeDecisionDeFacturacion.PresupuestoDeRegistroDeFallo + TimeSpan.FromSeconds(5),
            $"La decisión tardó {cronometro.Elapsed}.");
        Assert.Null((await LeerContabilidadAsync(e.IdTenant, bloqueada)).Marca);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));

        // La misma instancia (mismo DbContext y conexión) sigue sana y decide la venta, y otra más, una sola vez cada una.
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, (await servicio.DecidirTrasLaVentaAsync(bloqueada)).Estado);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, (await servicio.DecidirTrasLaVentaAsync(otra)).Estado);
        Assert.Equal(2, (await LeerDecisionesAsync(e.IdTenant)).Count);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 200m, facturado: 200m);
    }

    [Fact]
    public async Task SiLaDecisionDeLaVentaActualTerminoSinDecidirElBarridoNoCorreEnEsaLlamada()
    {
        var e = await PrepararAsync(nameof(SiLaDecisionDeLaVentaActualTerminoSinDecidirElBarridoNoCorreEnEsaLlamada));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 1m);
        // Un ticket ya decidido deja el acumulado del mes con monto vendido; la venta actual lo desborda y siempre falla.
        await DecidirAsync(e, await SembrarVentaAsync(e, Mayo, e.IdEfectivo));
        var sana = await SembrarVentaAsync(e, Mayo, e.IdEfectivo);
        var actual = await SembrarVentaQueSiempreFallaAsync(e, Mayo);
        var (servicio, db) = CrearServicio(e);
        await using var __ = db;

        var estado = await servicio.DecidirYSanearAsync(actual, e.IdPuntoVenta);

        Assert.Equal(EstadoFiscalDeVenta.SinDecidir, estado);
        Assert.Null((await LeerContabilidadAsync(e.IdTenant, sana)).Marca);
        Assert.Equal(1, (await LeerContabilidadAsync(e.IdTenant, actual)).Intentos);

        // La sana se podía decidir: solo el barrido omitido la dejó debida.
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, actual);
        Assert.NotNull((await LeerContabilidadAsync(e.IdTenant, sana)).Marca);
    }

    // ---- Certificado: sin certificado activo del ambiente configurado -----------------------------------------------

    [Fact]
    public async Task SinNingunCertificadoActivoLaVentaSeMarcaComoNoAplicableSinAnotarFalloNiLoguear()
    {
        var e = await PrepararAsync(
            nameof(SinNingunCertificadoActivoLaVentaSeMarcaComoNoAplicableSinAnotarFalloNiLoguear), conCertificado: false);
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var id = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var log = new RegistroDeLog();
        var (servicio, db) = CrearServicio(e, log: log);
        await using var __ = db;

        var estado = await servicio.DecidirTrasLaVentaAsync(id);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, estado);
        var contabilidad = await LeerContabilidadAsync(e.IdTenant, id);
        Assert.NotNull(contabilidad.Marca);
        Assert.Equal(0, contabilidad.Intentos);
        Assert.Empty(log.Entradas);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
    }

    [Fact]
    public async Task UnCertificadoActivoSoloDeOtroAmbienteMarcaLaVentaSinAnotarFalloYElBarridoSigueCorriendo()
    {
        var e = await PrepararAsync(
            nameof(UnCertificadoActivoSoloDeOtroAmbienteMarcaLaVentaSinAnotarFalloYElBarridoSigueCorriendo),
            conCertificado: false);
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await AgregarCertificadoAsync(e, AmbienteFiscal.Produccion);
        var id = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var otraDebida = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var log = new RegistroDeLog();
        // Un ambiente mal escrito cae a homologación, donde la empresa no tiene certificado.
        var (servicio, db) = CrearServicio(e, opciones: new OpcionesFiscales { Ambiente = "producion" }, log: log);
        await using var __ = db;

        var estado = await servicio.DecidirYSanearAsync(id, e.IdPuntoVenta);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, estado);
        foreach (var venta in new[] { id, otraDebida })
        {
            var contabilidad = await LeerContabilidadAsync(e.IdTenant, venta);
            Assert.NotNull(contabilidad.Marca);
            Assert.Equal(0, contabilidad.Intentos);
            Assert.Null(contabilidad.ProximoIntento);
        }

        var aviso = Assert.Single(log.Entradas);
        Assert.Equal(LogLevel.Warning, aviso.Nivel);
        Assert.Contains($"empresa {e.IdEmpresa}", aviso.Mensaje, StringComparison.Ordinal);
        Assert.Contains(nameof(AmbienteFiscal.Homologacion), aviso.Mensaje, StringComparison.Ordinal);
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
    }

    [Fact]
    public async Task UnCertificadoDelAmbienteConfiguradoActivadoDespuesNoFacturaLasVentasAnterioresYUnaNuevaSeDecideNormal()
    {
        var e = await PrepararAsync(
            nameof(UnCertificadoDelAmbienteConfiguradoActivadoDespuesNoFacturaLasVentasAnterioresYUnaNuevaSeDecideNormal),
            conCertificado: false);
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await AgregarCertificadoAsync(e, AmbienteFiscal.Produccion);
        var anterior = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var otraAnterior = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var (servicio, db) = CrearServicio(e);
        await using var __ = db;
        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, await servicio.DecidirTrasLaVentaAsync(anterior));
        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, await servicio.DecidirTrasLaVentaAsync(otraAnterior));
        var marcaAnterior = await LeerMarcaDeDecisionAsync(e.IdTenant, anterior);
        var marcaOtraAnterior = await LeerMarcaDeDecisionAsync(e.IdTenant, otraAnterior);
        Assert.NotNull(marcaAnterior);
        Assert.NotNull(marcaOtraAnterior);

        await AgregarCertificadoAsync(e, AmbienteFiscal.Homologacion);
        var nueva = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var estado = await servicio.DecidirYSanearAsync(nueva, e.IdPuntoVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, estado.Estado);
        Assert.Equal(nueva, Assert.Single(await LeerDecisionesAsync(e.IdTenant)).IdComprobanteVenta);
        Assert.Equal(marcaAnterior, await LeerMarcaDeDecisionAsync(e.IdTenant, anterior));
        Assert.Equal(marcaOtraAnterior, await LeerMarcaDeDecisionAsync(e.IdTenant, otraAnterior));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
    }

    [Fact]
    public async Task ElAvisoDeCertificadoSoloDeOtroAmbienteSeLimitaAUnoPorEmpresaPorHoraYVuelveDespues()
    {
        var e = await PrepararAsync(
            nameof(ElAvisoDeCertificadoSoloDeOtroAmbienteSeLimitaAUnoPorEmpresaPorHoraYVuelveDespues), conCertificado: false);
        using var _ = e.Cajero;
        var otra = await PrepararAsync(
            nameof(ElAvisoDeCertificadoSoloDeOtroAmbienteSeLimitaAUnoPorEmpresaPorHoraYVuelveDespues) + "-otra",
            conCertificado: false);
        using var __ = otra.Cajero;
        await AgregarCertificadoAsync(e, AmbienteFiscal.Produccion);
        await AgregarCertificadoAsync(otra, AmbienteFiscal.Produccion);
        var reloj = new RelojControlable(InicioDelReloj);
        var log = new RegistroDeLog();
        var (servicio, db) = CrearServicio(e, reloj, log: log);
        await using var ___ = db;
        var (servicioDeLaOtra, dbDeLaOtra) = CrearServicio(otra, reloj, log: log);
        await using var ____ = dbDeLaOtra;

        async Task VenderSinCertificadoAsync(ServicioDeDecisionDeFacturacion quien, Escenario escenario) =>
            Assert.Equal(
                EstadoFiscalDeVenta.NoCorresponde,
                await quien.DecidirTrasLaVentaAsync(await SembrarVentaAsync(escenario, Mayo, escenario.IdTransferencia)));

        await VenderSinCertificadoAsync(servicio, e);
        Assert.Equal(1, ContarAvisosDe(log, e));

        // Dentro de la hora no vuelve a avisar, ni para la misma empresa ni a costa de otra.
        reloj.Ahora = InicioDelReloj + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59);
        await VenderSinCertificadoAsync(servicio, e);
        Assert.Equal(1, ContarAvisosDe(log, e));

        // Otra empresa tiene su propio aviso aunque la primera ya avisó hace instantes.
        await VenderSinCertificadoAsync(servicioDeLaOtra, otra);
        Assert.Equal(1, ContarAvisosDe(log, otra));
        Assert.Equal(1, ContarAvisosDe(log, e));

        // Pasada la hora vuelve a avisar, y otra vez calla dentro de la hora siguiente.
        reloj.Ahora = InicioDelReloj + TimeSpan.FromHours(1);
        await VenderSinCertificadoAsync(servicio, e);
        Assert.Equal(2, ContarAvisosDe(log, e));
        await VenderSinCertificadoAsync(servicio, e);
        Assert.Equal(2, ContarAvisosDe(log, e));
        Assert.All(log.Entradas, x => Assert.Equal(LogLevel.Warning, x.Nivel));
    }

    private static int ContarAvisosDe(RegistroDeLog log, Escenario e) =>
        log.Entradas.Count(x => x.Mensaje.Contains($"empresa {e.IdEmpresa} ", StringComparison.Ordinal));

    // ---- Presupuesto del barrido ------------------------------------------------------------------------------------

    [Fact]
    public async Task ElCorteDelPresupuestoDelBarridoNoAnotaFalloDeLaVentaSanaYSeDecideEnUnaLlamadaPosterior()
    {
        var e = await PrepararAsync(
            nameof(ElCorteDelPresupuestoDelBarridoNoAnotaFalloDeLaVentaSanaYSeDecideEnUnaLlamadaPosterior));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        // Una venta decidida deja la fila del acumulado del mes; la candidata la va a querer bloquear.
        await DecidirAsync(e, await SembrarVentaAsync(e, Mayo, e.IdTransferencia));
        var candidata = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var log = new RegistroDeLog();
        var (servicio, db) = CrearServicio(e, log: log);
        await using var __ = db;

        // Otra sesión tiene tomada la fila del acumulado (no la de la venta, así anotar el fallo no chocaría con ella).
        // Se libera sola a los 20 s para que el test termine aunque el servicio no respetara su presupuesto.
        var yaBloqueada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var titular = Task.Run(async () =>
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
            await using var transaccion = await conexion.BeginTransactionAsync();
            await using (var comando = conexion.CreateCommand())
            {
                comando.Transaction = transaccion;
                comando.CommandText = "SELECT 1 FROM acumulados_de_facturacion WHERE id_empresa = $1 FOR UPDATE";
                comando.Parameters.Add(new NpgsqlParameter { Value = e.IdEmpresa });
                await comando.ExecuteScalarAsync();
            }

            yaBloqueada.SetResult();
            await Task.WhenAny(liberar.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            await transaccion.RollbackAsync();
        });
        await yaBloqueada.Task;

        var cronometro = Stopwatch.StartNew();
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        cronometro.Stop();

        // El barrido esperó hasta que su presupuesto lo cortó; la venta no tiene nada de malo.
        Assert.True(cronometro.Elapsed >= ServicioDeDecisionDeFacturacion.PresupuestoDeBarrido - TimeSpan.FromMilliseconds(200));
        Assert.True(
            cronometro.Elapsed < TimeSpan.FromSeconds(15),
            $"El barrido tardó {cronometro.Elapsed}: no respetó su presupuesto.");
        var cortada = await LeerContabilidadAsync(e.IdTenant, candidata);
        Assert.Equal(0, cortada.Intentos);
        Assert.Null(cortada.ProximoIntento);
        Assert.Null(cortada.Marca);
        Assert.DoesNotContain(log.Entradas, x => x.Nivel >= LogLevel.Warning);

        liberar.SetResult();
        await titular;

        // Sin la otra sesión, la misma venta (sin espera) se decide en la llamada siguiente.
        await servicio.SanearDecisionesDebidasAsync(e.IdPuntoVenta, 0);
        var decidida = await LeerContabilidadAsync(e.IdTenant, candidata);
        Assert.NotNull(decidida.Marca);
        Assert.Equal(0, decidida.Intentos);
        Assert.Equal(2, (await LeerDecisionesAsync(e.IdTenant)).Count);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 200m, facturado: 200m);
    }

    // ---- Composición de pagos de un ticket ---------------------------------------------------------------

    [Fact]
    public async Task VariosPagosDelMismoMedioEnUnTicketSumanEnUnaSolaFilaDelAcumulado()
    {
        var e = await PrepararAsync(nameof(VariosPagosDelMismoMedioEnUnTicketSumanEnUnaSolaFilaDelAcumulado));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        var idVenta = await SembrarVentaAsync(
            e, Mayo, e.IdEfectivo, importe: 60m, pagosAdicionales: [(e.IdEfectivo, 40m, 0m)]);

        await DecidirAsync(e, idVenta);

        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 10m, vendido: 100m, facturado: 0m);
    }

    [Fact]
    public async Task UnPagoDeMontoNetoCeroNoCuentaNiCreaFilaDeAcumuladoYLosPositivosSi()
    {
        var e = await PrepararAsync(nameof(UnPagoDeMontoNetoCeroNoCuentaNiCreaFilaDeAcumuladoYLosPositivosSi));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await FijarPorcentajeAsync(e, e.IdEfectivo, 100m);
        // El efectivo cobró 50 y devolvió 50 de vuelto: su monto neto es cero.
        var idVenta = await SembrarVentaAsync(
            e, Mayo, e.IdTransferencia, importe: 100m, pagosAdicionales: [(e.IdEfectivo, 50m, 50m)]);

        var estado = await DecidirAsync(e, idVenta);

        Assert.Equal(("pendiente", "medio_exige_factura"), (estado.Estado, estado.Motivo));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
        Assert.DoesNotContain(await LeerAcumuladosAsync(e.IdTenant), f => f.IdMedioPago == e.IdEfectivo);
    }

    [Fact]
    public async Task UnPagoDeMontoNetoNegativoTampocoCuentaNiCreaFilaDeAcumulado()
    {
        var e = await PrepararAsync(nameof(UnPagoDeMontoNetoNegativoTampocoCuentaNiCreaFilaDeAcumulado));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        await FijarPorcentajeAsync(e, e.IdEfectivo, 100m);
        // El vuelto supera al importe del pago en efectivo: monto neto de −20.
        var idVenta = await SembrarVentaAsync(
            e, Mayo, e.IdTransferencia, importe: 100m, pagosAdicionales: [(e.IdEfectivo, 30m, 50m)]);

        await DecidirAsync(e, idVenta);

        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
        Assert.DoesNotContain(await LeerAcumuladosAsync(e.IdTenant), f => f.IdMedioPago == e.IdEfectivo);
    }

    /// <summary>Documenta la regla vigente, no un requisito del negocio: la decisión no mira el comportamiento del medio,
    /// así que un pago a cuenta corriente acumula y puede facturar el ticket igual que cualquier otro medio según su
    /// porcentaje de facturación.</summary>
    [Fact]
    public async Task UnPagoACuentaCorrienteAcumulaComoCualquierOtroMedioSegunSuPorcentaje()
    {
        var e = await PrepararAsync(nameof(UnPagoACuentaCorrienteAcumulaComoCualquierOtroMedioSegunSuPorcentaje));
        using var _ = e.Cajero;
        int idCuentaCorriente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var medio = new MedioPago
            {
                IdTenant = e.IdTenant, Nombre = "Cuenta corriente", Orden = 9,
                Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false,
                RequiereReferencia = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.MediosPago.Add(medio);
            await db.SaveChangesAsync();
            idCuentaCorriente = medio.Id;
        }

        var sinPorcentaje = await SembrarVentaAsync(e, Mayo, idCuentaCorriente);
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, (await DecidirAsync(e, sinPorcentaje)).Estado);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), idCuentaCorriente, saldo: 0m, vendido: 100m, facturado: 0m);

        await FijarPorcentajeAsync(e, idCuentaCorriente, 100m);
        var alCien = await SembrarVentaAsync(e, Mayo, idCuentaCorriente);
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, (await DecidirAsync(e, alCien)).Estado);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), idCuentaCorriente, saldo: 0m, vendido: 200m, facturado: 100m);
    }

    // ---- Servicio sobre ventas sembradas ---------------------------------------------------------------

    [Fact]
    public async Task DecidirDosVecesUnaVentaQueNoSeFacturaNoLaCuentaDosVeces()
    {
        var e = await PrepararAsync(nameof(DecidirDosVecesUnaVentaQueNoSeFacturaNoLaCuentaDosVeces));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        var idVenta = await SembrarVentaAsync(e, Mayo, e.IdEfectivo);

        var primera = await DecidirAsync(e, idVenta);
        var segunda = await DecidirAsync(e, idVenta);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, primera);
        Assert.Equal(primera, segunda);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 10m, vendido: 100m, facturado: 0m);
    }

    [Fact]
    public async Task DecidirDosVecesUnaVentaFacturadaDevuelveLaMismaDecision()
    {
        var e = await PrepararAsync(nameof(DecidirDosVecesUnaVentaFacturadaDevuelveLaMismaDecision));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var idVenta = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);

        var primera = await DecidirAsync(e, idVenta);
        var segunda = await DecidirAsync(e, idVenta);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Estado);
        Assert.Equal(primera, segunda);
        Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdTransferencia, saldo: 0m, vendido: 100m, facturado: 100m);
    }

    [Fact]
    public async Task ElPeriodoSaleDelMesDeLaFechaEnLaZonaDelPuntoDeVentaYCadaMesTieneSuPropioSaldo()
    {
        var e = await PrepararAsync(nameof(ElPeriodoSaleDelMesDeLaFechaEnLaZonaDelPuntoDeVentaYCadaMesTieneSuPropioSaldo));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);

        // 23:30 del 31/05 en Buenos Aires es 02:30 del 01/06 UTC: el período es mayo y no junio.
        var finDeMayo = await SembrarVentaAsync(e, new DateTimeOffset(2026, 5, 31, 23, 30, 0, TimeSpan.FromHours(-3)), e.IdEfectivo);
        var inicioDeJunio = await SembrarVentaAsync(e, new DateTimeOffset(2026, 6, 1, 0, 30, 0, TimeSpan.FromHours(-3)), e.IdEfectivo);

        await DecidirAsync(e, finDeMayo);
        await DecidirAsync(e, inicioDeJunio);

        var acumulados = await LeerAcumuladosAsync(e.IdTenant);
        Assert.Equal(
            [
                new Acumulado(e.IdEfectivo, new DateOnly(2026, 5, 1), 10m, 100m, 0m),
                new Acumulado(e.IdEfectivo, new DateOnly(2026, 6, 1), 10m, 100m, 0m)
            ],
            acumulados);
    }

    [Fact]
    public async Task ElPeriodoDeUnaDecisionFacturadaQuedaGuardadoParaRevertirla()
    {
        var e = await PrepararAsync(nameof(ElPeriodoDeUnaDecisionFacturadaQuedaGuardadoParaRevertirla));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var idVenta = await SembrarVentaAsync(
            e, new DateTimeOffset(2026, 5, 31, 23, 30, 0, TimeSpan.FromHours(-3)), e.IdTransferencia);

        await DecidirAsync(e, idVenta);

        Assert.Equal(new DateOnly(2026, 5, 1), Assert.Single(await LeerDecisionesAsync(e.IdTenant)).Periodo);
    }

    [Fact]
    public async Task DecisionesEnParaleloDeLaMismaVentaProducenUnaSolaDecisionYUnSoloMovimiento()
    {
        var e = await PrepararAsync(nameof(DecisionesEnParaleloDeLaMismaVentaProducenUnaSolaDecisionYUnSoloMovimiento));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        var idVenta = await SembrarVentaAsync(e, Mayo, e.IdEfectivo);
        // Forzada: la decisión escribe fila y movimiento; sin forzar, una carrera solo duplicaría el acumulado.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            await db.ComprobantesVenta.Where(c => c.Id == idVenta).ExecuteUpdateAsync(s => s.SetProperty(c => c.FacturaForzada, true));
        }

        var resultados = await EnParaleloAsync(12, _ => DecidirAsync(e, idVenta));

        Assert.Single(resultados.Distinct());
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, resultados[0].Estado);
        Assert.Single(await LeerDecisionesAsync(e.IdTenant));
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: -90m, vendido: 100m, facturado: 100m);
    }

    [Fact]
    public async Task DecisionesEnParaleloDeUnaVentaQueNoSeFacturaLaCuentanUnaSolaVez()
    {
        var e = await PrepararAsync(nameof(DecisionesEnParaleloDeUnaVentaQueNoSeFacturaLaCuentanUnaSolaVez));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        var idVenta = await SembrarVentaAsync(e, Mayo, e.IdEfectivo);

        var resultados = await EnParaleloAsync(12, _ => DecidirAsync(e, idVenta));

        Assert.Single(resultados.Distinct());
        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        // Sin fila de decisión que la frene, solo el lock de la venta evita contar el ticket más de una vez.
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 10m, vendido: 100m, facturado: 0m);
    }

    [Fact]
    public async Task VentasEnParaleloSobreElMismoMedioSumanExactoYRespetanElCupo()
    {
        var e = await PrepararAsync(nameof(VentasEnParaleloSobreElMismoMedioSumanExactoYRespetanElCupo));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdEfectivo, 10m);
        var ids = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await SembrarVentaAsync(e, Mayo, e.IdEfectivo));
        }

        await EnParaleloAsync(ids.Count, i => DecidirAsync(e, ids[i]));

        // 20 tickets de 100 al 10 %: el saldo cruza la mitad del importe en el sexto y en el décimosexto.
        Assert.Equal(2, (await LeerDecisionesAsync(e.IdTenant)).Count);
        AfirmarAcumulado(await LeerAcumuladosAsync(e.IdTenant), e.IdEfectivo, saldo: 0m, vendido: 2000m, facturado: 200m);
    }

    [Fact]
    public async Task UnEspejoFiscalYUnaVentaAnuladaOSinPagosNoMuevenElAcumulado()
    {
        var e = await PrepararAsync(nameof(UnEspejoFiscalYUnaVentaAnuladaOSinPagosNoMuevenElAcumulado));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var venta = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        var espejo = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, idTipo: e.IdTipoFa, idVentaOrigen: venta);
        var anulada = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, estado: EstadoComprobante.Anulado);
        var sinPagos = await SembrarVentaAsync(e, Mayo, e.IdTransferencia, conPago: false);

        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, await DecidirAsync(e, espejo));
        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, await DecidirAsync(e, anulada));
        Assert.Equal(EstadoFiscalDeVenta.NoCorresponde, await DecidirAsync(e, sinPagos));

        Assert.Empty(await LeerDecisionesAsync(e.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
        // Un espejo nunca se decide ni se marca; lo demás queda marcado para no figurar como debido.
        Assert.Null(await LeerMarcaDeDecisionAsync(e.IdTenant, espejo));
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, anulada));
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, sinPagos));
    }

    [Fact]
    public async Task UnaVentaQueYaTieneDecisionPeroNoMarcaDevuelveLaDecisionSinTocarSaldos()
    {
        var e = await PrepararAsync(nameof(UnaVentaQueYaTieneDecisionPeroNoMarcaDevuelveLaDecisionSinTocarSaldos));
        using var _ = e.Cajero;
        await FijarPorcentajeAsync(e, e.IdTransferencia, 100m);
        var idVenta = await SembrarVentaAsync(e, Mayo, e.IdTransferencia);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.FacturacionesDeVenta.Add(new FacturacionDeVenta
            {
                IdTenant = e.IdTenant, IdEmpresa = e.IdEmpresa, IdComprobanteVenta = idVenta,
                Motivo = MotivoDeFacturacion.Forzada, Aportes = "[]", Periodo = new DateOnly(2026, 5, 1),
                CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var estado = await DecidirAsync(e, idVenta);

        Assert.Equal((EstadosFiscalesDeVenta.Pendiente, "forzada"), (estado.Estado, estado.Motivo));
        Assert.Empty(await LeerAcumuladosAsync(e.IdTenant));
        // Sin marca la venta seguiría debida y el barrido la visitaría en cada venta.
        Assert.NotNull(await LeerMarcaDeDecisionAsync(e.IdTenant, idVenta));
    }

    // ---- Aislamiento --------------------------------------------------------------------------------------

    [Fact]
    public async Task OtroTenantNoVeLaDecisionNiLosAcumulados()
    {
        var a = await PrepararAsync(nameof(OtroTenantNoVeLaDecisionNiLosAcumulados) + "A");
        var b = await PrepararAsync(nameof(OtroTenantNoVeLaDecisionNiLosAcumulados) + "B");
        using var _a = a.Cajero;
        using var _b = b.Cajero;
        await FijarPorcentajeAsync(a, a.IdTransferencia, 100m);
        await VenderAsync(a, Venta(a, PorTransferencia(a)));

        Assert.Single(await LeerDecisionesAsync(a.IdTenant));
        Assert.NotEmpty(await LeerAcumuladosAsync(a.IdTenant));
        Assert.Empty(await LeerDecisionesAsync(b.IdTenant));
        Assert.Empty(await LeerAcumuladosAsync(b.IdTenant));
    }

    private static async Task<List<T>> EnParaleloAsync<T>(int cantidad, Func<int, Task<T>> trabajo)
    {
        using var barrera = new Barrier(cantidad);
        var tareas = Enumerable.Range(0, cantidad).Select(i => Task.Run(async () =>
        {
            barrera.SignalAndWait();
            return await trabajo(i);
        }));

        return [.. await Task.WhenAll(tareas)];
    }
}
