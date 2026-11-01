using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Fiscal;
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
/// Cambio de los medios de pago de una venta ya emitida: <c>PUT /api/ventas/{id}/medios</c> (caja, turno abierto) y
/// <c>PUT /api/ventas/administracion/{id}/medios</c> (Admin, cualquier turno). Cubre el reemplazo de cobros con auditoría, las
/// guardas (turno cerrado en la caja, CAE, cuenta corriente, total distinto, conciliación, 404 antes que el cuerpo), el
/// recálculo del arqueo de un turno cerrado con su marca, la compensación de las cuentas de tesorería, el traslado del
/// acumulado de facturación (y que la cancelación posterior lo deje en cero) y el reenvío offline de una venta ya cambiada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CambioDeMediosDeVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private static long _numeroSembrado = 5_000_000;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdAdmin, HttpClient Admin, HttpClient Cajero, int IdCliente,
        int IdTipoTx, int IdArticulo, int IdEfectivo, int IdTransferencia);

    private sealed record Acumulado(int IdMedioPago, decimal Saldo, decimal Vendido, decimal Facturado);

    // ---- Siembra ------------------------------------------------------------------------------

    /// <summary>Tenant con un artículo de 100, un certificado activo (para que la venta se decida) y un cajero: un Vendedor
    /// web, o con <paramref name="escritorio"/> un cajero de dispositivo con un bloque de numeración reservado.</summary>
    private async Task<Contexto> PrepararAsync(string nombre, bool escritorio = false)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}";
        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new Ways.Application.Organizacion.SolicitudDeAprovisionamiento(
                unico, $"{unico} SA", "Local 1", mailAdmin, escritorio ? ModoPuntoVenta.Escritorio : ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<Ways.Application.Organizacion.ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        int idArticulo, idEfectivo, idTransferencia, idCliente, idTipoTx;
        await using (var db = Db(resultado.IdTenant))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            var articulo = new Articulo
            {
                IdTenant = resultado.IdTenant, CodigoInterno = "servicio", Nombre = "Servicio de prueba", IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
                EsProducto = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();

            db.Precios.Add(new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo.Id,
                IdListaPrecio = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync(), Monto = 100m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            db.CertificadosFiscales.Add(new CertificadoFiscal
            {
                IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Ambiente = AmbienteFiscal.Homologacion,
                Alias = "Homo de prueba", CuitTitular = "20111111112", CertificadoPem = "pem",
                ClavePrivadaCifrada = new byte[48], Nonce = new byte[12], TagAutenticacion = new byte[16],
                IdClaveMaestra = "v1", HuellaSha256 = new string('a', 64), VigenciaDesde = ahora.AddDays(-1),
                VigenciaHasta = ahora.AddYears(1), Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();

            idArticulo = articulo.Id;
            idEfectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
            idTransferencia = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).OrderBy(m => m.Id).Select(m => m.Id).FirstAsync();
            idCliente = await db.Clientes.OrderBy(c => c.Numero).Select(c => c.Id).FirstAsync();
            idTipoTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
        }

        var cajero = escritorio
            ? await LoguearComoCajeroDeDispositivoAsync(admin, resultado.IdTenant, resultado.IdPuntoVenta)
            : await CrearVendedorAsync(admin, unico);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, cajero, idCliente,
            idTipoTx, idArticulo, idEfectivo, idTransferencia);
    }

    private async Task<HttpClient> CrearVendedorAsync(HttpClient admin, string nombre)
    {
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-medios", mail, (int)RolConocido.Vendedor, PasswordCajero));
        Assert.True(alta.StatusCode == HttpStatusCode.Created, await alta.Content.ReadAsStringAsync());

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordCajero));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(HttpClient admin, int idTenant, int idPuntoVenta)
    {
        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        var cookie = valor[..valor.IndexOf(';')];

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant, NombreUsuario = "cajero-medios", Mail = $"cajero-medios-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor, PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-medios", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookie}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cajero;
    }

    private WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private WaysDbContext Db(Contexto ctx) => Db(ctx.IdTenant);

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    /// <summary>Turno abierto sembrado directo (el POS de escritorio no abre turnos desde el navegador del admin).</summary>
    private async Task<int> SembrarTurnoAbiertoAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleadoApertura = ctx.IdAdmin, FechaApertura = ahora,
            FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();
        return turno.Id;
    }

    private static async Task CerrarPorRetiroAsync(Contexto ctx, int idTurno)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    /// <summary>Una venta sembrada directo en la base, sin decisión de facturación (queda debida y la decisión no corre).</summary>
    private async Task<int> SembrarVentaAsync(
        Contexto ctx, int? idTurno, params (int IdMedioPago, decimal Importe)[] cobros)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var total = cobros.Sum(c => c.Importe);
        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoTx, Numero = Interlocked.Increment(ref _numeroSembrado),
            Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdTurnoCaja = idTurno, IdEmpleado = ctx.IdAdmin,
            IdCliente = ctx.IdCliente, Subtotal = total, DescuentoTotal = 0m, Total = total, Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        foreach (var (idMedio, importe) in cobros)
        {
            db.Cobros.Add(new Cobro
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedio, Importe = importe,
                Vuelto = 0m, Referencia = "REF-0", CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private async Task<int> CrearCuentaAsync(Contexto ctx, string nombre)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var cuenta = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, SaldoInicial = 0m, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<int> CrearMedioAsync(
        Contexto ctx, string nombre, int? idCuenta = null, decimal comision = 0m,
        ComportamientoMedioPago comportamiento = ComportamientoMedioPago.Electronico, decimal porcentajeFacturacion = 0m)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 60, Comportamiento = comportamiento, AdmiteVuelto = false,
            RequiereReferencia = false, IdCuentaTesoreria = idCuenta, ComisionPorcentaje = comision,
            PorcentajeFacturacion = porcentajeFacturacion, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private async Task FijarPorcentajeAsync(Contexto ctx, int idMedioPago, decimal porcentaje)
    {
        await using var db = Db(ctx);
        var medio = await db.MediosPago.SingleAsync(m => m.Id == idMedioPago);
        medio.PorcentajeFacturacion = porcentaje;
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta Venta(Contexto ctx, IReadOnlyList<PagoDeVenta> pagos, long? numero = null) =>
        new(ctx.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null, PrecioUnitario: numero is null ? null : 100m)],
            pagos, null, null, NumeroPreasignado: numero);

    private static async Task<ComprobanteEmitido> VenderAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> CambiarEnLaCajaAsync(HttpClient cliente, int idVenta, params PagoDeVenta[] pagos) =>
        cliente.PutAsJsonAsync($"/api/ventas/{idVenta}/medios", new SolicitudDeCambioDeMedios(pagos));

    private static Task<HttpResponseMessage> CambiarEnAdministracionAsync(HttpClient cliente, int idVenta, params PagoDeVenta[] pagos) =>
        cliente.PutAsJsonAsync($"/api/ventas/administracion/{idVenta}/medios", new SolicitudDeCambioDeMedios(pagos));

    private static async Task<ComprobanteEmitido> LeerOkAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task AfirmarErrorAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private async Task<List<Cobro>> CobrosAsync(Contexto ctx, int idVenta, bool conBajas = false)
    {
        await using var db = Db(ctx);
        var cobros = conBajas ? db.Cobros.IgnoreQueryFilters(["BajaLogica"]) : db.Cobros;
        return await cobros.Where(c => c.IdComprobanteVenta == idVenta).OrderBy(c => c.Id).ToListAsync();
    }

    private async Task<List<Acumulado>> AcumuladosAsync(Contexto ctx)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_medio_pago, saldo_a_facturar, total_vendido, total_facturado FROM acumulados_de_facturacion ORDER BY id_medio_pago";
        var filas = new List<Acumulado>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new Acumulado(lector.GetInt32(0), lector.GetDecimal(1), lector.GetDecimal(2), lector.GetDecimal(3)));
        }

        return filas;
    }

    private static Acumulado AcumuladoDe(List<Acumulado> acumulados, int idMedio) =>
        acumulados.SingleOrDefault(a => a.IdMedioPago == idMedio) ?? new Acumulado(idMedio, 0m, 0m, 0m);

    // ---- Caja ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorCambiaElMedioDeUnaVentaDelTurnoAbiertoYQuedaAuditado()
    {
        var ctx = await PrepararAsync("medioscaja");
        var turno = await AbrirTurnoAsync(ctx);
        var tarjeta = await CrearMedioAsync(ctx, "Tarjeta");
        var venta = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 600m), (tarjeta, 400m));

        var cambiada = await LeerOkAsync(await CambiarEnLaCajaAsync(
            ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 1000m, " OP-9 ", 0m)));

        var pago = Assert.Single(cambiada.Pagos);
        Assert.Equal((ctx.IdTransferencia, 1000m, "OP-9", 0m), (pago.IdMedioPago, pago.Importe, pago.Referencia, pago.Vuelto));

        var vivos = await CobrosAsync(ctx, venta);
        var vivo = Assert.Single(vivos);
        Assert.Equal((ctx.IdTransferencia, 1000m, "OP-9"), (vivo.IdMedioPago, vivo.Importe, vivo.Referencia));
        var todos = await CobrosAsync(ctx, venta, conBajas: true);
        Assert.Equal(3, todos.Count);
        Assert.All(todos.Where(c => c.Id != vivo.Id), c => Assert.NotNull(c.DeletedAt));

        await using var db = Db(ctx);
        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "venta.medios" && a.IdEntidad == venta);
        Assert.Equal("comprobante_venta", auditoria.Entidad);
        Assert.Equal(ctx.IdPuntoVenta, auditoria.IdPuntoVenta);
        var anterior = JsonDocument.Parse(auditoria.ValorAnterior!).RootElement.GetProperty("cobros");
        Assert.Equal(
            [ctx.IdEfectivo, tarjeta], anterior.EnumerateArray().Select(c => c.GetProperty("id_medio_pago").GetInt32()));
        var nuevo = JsonDocument.Parse(auditoria.ValorNuevo).RootElement;
        Assert.Equal(ctx.IdTransferencia, Assert.Single(nuevo.GetProperty("cobros").EnumerateArray()).GetProperty("id_medio_pago").GetInt32());
        Assert.Equal(JsonValueKind.Null, nuevo.GetProperty("id_turno_recalculado").ValueKind);

        // El turno sigue abierto: su resumen ya deriva con el medio nuevo.
        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno}/resumen", OpcionesJson);
        Assert.Equal(1000m, resumen!.Medios.Single(m => m.IdMedioPago == ctx.IdTransferencia).ImporteEsperado);
        Assert.DoesNotContain(resumen.Medios, m => m.IdMedioPago == tarjeta && m.ImporteEsperado != 0m);
    }

    [Fact]
    public async Task LaCajaNoCambiaLosMediosDeUnaVentaDeUnTurnoCerrado()
    {
        var ctx = await PrepararAsync("medioscerrado");
        var turno = await AbrirTurnoAsync(ctx);
        var venta = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));
        await CerrarPorRetiroAsync(ctx, turno);

        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "venta_turno_cerrado");
        Assert.Equal(ctx.IdEfectivo, Assert.Single(await CobrosAsync(ctx, venta)).IdMedioPago);
    }

    [Fact]
    public async Task LaCajaNoCambiaLosMediosDeUnaVentaSinTurnoYElVendedorNoUsaLaRutaDeAdministracion()
    {
        var ctx = await PrepararAsync("mediossinturno");
        var venta = await SembrarVentaAsync(ctx, idTurno: null, (ctx.IdEfectivo, 100m));

        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "venta_turno_cerrado");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CambiarEnAdministracionAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m))).StatusCode);

        // Administración sí, sin turno: no hay arqueo que recalcular.
        var cambiada = await LeerOkAsync(await CambiarEnAdministracionAsync(
            ctx.Admin, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)));
        Assert.Equal(ctx.IdTransferencia, Assert.Single(cambiada.Pagos).IdMedioPago);
    }

    // ---- Guardas ------------------------------------------------------------------------------

    [Fact]
    public async Task UnaVentaConCaeOConLaFacturaEnTramiteNoCambiaSusMedios()
    {
        var ctx = await PrepararAsync("medioscae");
        var turno = await AbrirTurnoAsync(ctx);
        var conCae = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));
        var enTramite = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));
        await SembrarEspejoAsync(ctx, conCae, ResultadoFiscal.Aprobado);
        await SembrarEspejoAsync(ctx, enTramite, ResultadoFiscal.Pendiente);

        await AfirmarErrorAsync(
            await CambiarEnAdministracionAsync(ctx.Admin, conCae, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "venta_con_cae");
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, enTramite, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "venta_con_factura_en_tramite");
    }

    private async Task SembrarEspejoAsync(Contexto ctx, int idVenta, ResultadoFiscal resultado)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var aprobado = resultado is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;
        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = await db.TiposComprobante.Where(t => t.Codigo == "FB").Select(t => t.Id).FirstAsync(),
            Numero = Interlocked.Increment(ref _numeroSembrado), Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdAdmin, IdCliente = ctx.IdCliente, Subtotal = 100m, Total = 100m, Estado = EstadoComprobante.Emitido,
            IdVentaOrigen = idVenta, ResultadoFiscal = resultado, Cae = aprobado ? "12345678901234" : null,
            CaeVencimiento = aprobado ? new DateOnly(2027, 5, 20) : null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LaCuentaCorrienteNoEntraNiSaleDelCambio()
    {
        var ctx = await PrepararAsync("medioscc");
        var turno = await AbrirTurnoAsync(ctx);
        var cc = await CrearMedioAsync(ctx, "Cuenta corriente", comportamiento: ComportamientoMedioPago.CuentaCorriente);
        var enCuentaCorriente = await SembrarVentaAsync(ctx, turno, (cc, 100m));
        var enEfectivo = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));

        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, enCuentaCorriente, new PagoDeVenta(ctx.IdEfectivo, 100m, null, 0m)),
            HttpStatusCode.Conflict, "cambio_de_medios_con_cuenta_corriente");
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, enEfectivo, new PagoDeVenta(cc, 100m, null, 0m)),
            HttpStatusCode.Conflict, "cambio_de_medios_con_cuenta_corriente");
        Assert.Equal(ctx.IdEfectivo, Assert.Single(await CobrosAsync(ctx, enEfectivo)).IdMedioPago);
    }

    [Fact]
    public async Task UnTotalDistintoUnaVentaAnuladaYUnMedioInexistenteSeRechazan()
    {
        var ctx = await PrepararAsync("mediostotal");
        var turno = await AbrirTurnoAsync(ctx);
        var venta = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));

        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 90m, "OP-1", 0m)),
            HttpStatusCode.BadRequest, "total_de_cobros_distinto");
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(999_999, 100m, null, 0m)),
            HttpStatusCode.BadRequest, "referencia_invalida");
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 110m, "OP-1", 10m)),
            HttpStatusCode.BadRequest, "vuelto_no_justificado");
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdEfectivo, 100m, " REF-0 ", 0m)),
            HttpStatusCode.Conflict, "cambio_de_medios_sin_diferencia");

        var anulacion = await ctx.Cajero.PostAsync($"/api/ventas/{venta}/anulacion", null);
        Assert.True(anulacion.IsSuccessStatusCode, await anulacion.Content.ReadAsStringAsync());
        await AfirmarErrorAsync(
            await CambiarEnLaCajaAsync(ctx.Cajero, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "venta_anulada");
        Assert.Equal(ctx.IdEfectivo, Assert.Single(await CobrosAsync(ctx, venta)).IdMedioPago);
    }

    [Fact]
    public async Task UnaVentaDeUnaFechaConciliadaNoCambiaSusMedios()
    {
        var ctx = await PrepararAsync("mediosconc");
        var turno = await AbrirTurnoAsync(ctx);
        var venta = await SembrarVentaAsync(ctx, turno, (ctx.IdEfectivo, 100m));
        await CerrarPorRetiroAsync(ctx, turno);

        await using (var db = Db(ctx))
        {
            db.ConciliacionesTesoreria.Add(new ConciliacionTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Hasta = DateTimeOffset.UtcNow, IdUsuario = ctx.IdAdmin,
                CreadoEl = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await AfirmarErrorAsync(
            await CambiarEnAdministracionAsync(ctx.Admin, venta, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)),
            HttpStatusCode.Conflict, "periodo_conciliado");
        Assert.Equal(ctx.IdEfectivo, Assert.Single(await CobrosAsync(ctx, venta)).IdMedioPago);
    }

    [Fact]
    public async Task UnaVentaInexistenteODeOtroTenantEsUn404AunqueElCuerpoSeaInvalido()
    {
        var ctx = await PrepararAsync("medios404");
        var otro = await PrepararAsync("medios404b");
        var turno = await AbrirTurnoAsync(otro);
        var ajena = await SembrarVentaAsync(otro, turno, (otro.IdEfectivo, 100m));

        foreach (var id in new[] { 999_999_999, ajena })
        {
            foreach (var cuerpo in new[] { new SolicitudDeCambioDeMedios([]), new SolicitudDeCambioDeMedios(null) })
            {
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await ctx.Cajero.PutAsJsonAsync($"/api/ventas/{id}/medios", cuerpo)).StatusCode);
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    (await ctx.Admin.PutAsJsonAsync($"/api/ventas/administracion/{id}/medios", cuerpo)).StatusCode);
            }
        }

        Assert.Equal(otro.IdEfectivo, Assert.Single(await CobrosAsync(otro, ajena)).IdMedioPago);
        await AfirmarErrorAsync(
            await otro.Admin.PutAsJsonAsync($"/api/ventas/administracion/{ajena}/medios", new SolicitudDeCambioDeMedios([])),
            HttpStatusCode.BadRequest, "pago_no_ingresado");
    }

    // ---- Turno cerrado: arqueo y cuentas -------------------------------------------------------

    [Fact]
    public async Task AdministracionSobreUnTurnoCerradoRecalculaElArqueoConMarcaYCompensaLasCuentas()
    {
        var ctx = await PrepararAsync("medioscuentas");
        var cuentaA = await CrearCuentaAsync(ctx, "Cuenta A");
        var cuentaB = await CrearCuentaAsync(ctx, "Cuenta B");
        var cuentaC = await CrearCuentaAsync(ctx, "Cuenta C");
        var medioA = await CrearMedioAsync(ctx, "Medio A", cuentaA, comision: 10m);
        var medioB = await CrearMedioAsync(ctx, "Medio B", cuentaB, comision: 5m);
        var medioC = await CrearMedioAsync(ctx, "Medio C", cuentaC, comision: 2m);

        var turno = await AbrirTurnoAsync(ctx);
        var venta = await SembrarVentaAsync(ctx, turno, (medioA, 1000m));
        await SembrarVentaAsync(ctx, turno, (medioB, 500m));
        await CerrarPorRetiroAsync(ctx, turno);

        // 600 pasan de A a B (que ya acreditó en el cierre) y 400 a C (que el cierre no acreditó).
        var cambiada = await LeerOkAsync(await CambiarEnAdministracionAsync(
            ctx.Admin, venta, new PagoDeVenta(medioB, 600m, null, 0m), new PagoDeVenta(medioC, 400m, null, 0m)));
        Assert.Equal([medioB, medioC], cambiada.Pagos.Select(p => p.IdMedioPago).Order());

        await using var db = Db(ctx);
        var arqueos = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == turno).ToDictionaryAsync(a => a.IdMedioPago);
        Assert.Equal((0m, 1000m), (arqueos[medioA].ImporteEsperado, arqueos[medioA].ImporteEsperadoOriginal));
        Assert.Equal((1100m, 500m), (arqueos[medioB].ImporteEsperado, arqueos[medioB].ImporteEsperadoOriginal));
        Assert.Equal((400m, 0m), (arqueos[medioC].ImporteEsperado, arqueos[medioC].ImporteEsperadoOriginal));
        var turnoRecalculado = await db.TurnosCaja.SingleAsync(t => t.Id == turno);
        Assert.NotNull(turnoRecalculado.FechaRecalculo);
        Assert.Equal(ctx.IdAdmin, turnoRecalculado.IdEmpleadoRecalculo);

        var libro = await db.MovimientosCuenta.OrderBy(m => m.Id).ToListAsync();

        // A: el cierre acreditó 1000 − 100; el ajuste devuelve exactamente eso.
        var ajusteA = Assert.Single(libro, m => m.IdCuentaTesoreria == cuentaA && m.Tipo == TipoMovimientoCuenta.Ajuste);
        Assert.Equal((900m, 0m, 900m, 0m), (ajusteA.Inicio, ajusteA.Ingreso, ajusteA.Egreso, ajusteA.Final));
        Assert.Equal((turno, medioA, -1000m), (ajusteA.IdTurnoCaja!.Value, ajusteA.IdMedioPago!.Value, ajusteA.BaseBruta!.Value));

        // B: ya tenía sus filas de cierre (500 − 25); el ajuste suma 600 − 30.
        var ajusteB = Assert.Single(libro, m => m.IdCuentaTesoreria == cuentaB && m.Tipo == TipoMovimientoCuenta.Ajuste);
        Assert.Equal((475m, 570m, 0m, 1045m), (ajusteB.Inicio, ajusteB.Ingreso, ajusteB.Egreso, ajusteB.Final));
        Assert.Equal(2, libro.Count(m => m.IdCuentaTesoreria == cuentaB && m.Tipo != TipoMovimientoCuenta.Ajuste));

        // C: sin filas de cierre, recibe las del cierre por lo que cobra ahora en el turno.
        var filasC = libro.Where(m => m.IdCuentaTesoreria == cuentaC).ToList();
        Assert.Equal([TipoMovimientoCuenta.Cobro, TipoMovimientoCuenta.Comision], filasC.Select(f => f.Tipo));
        Assert.Equal((400m, 0m), (filasC[0].Ingreso, filasC[0].Egreso));
        Assert.Equal((0m, 8m, 392m), (filasC[1].Ingreso, filasC[1].Egreso, filasC[1].Final));
        Assert.All(filasC, f => Assert.Equal((turno, medioC), (f.IdTurnoCaja!.Value, f.IdMedioPago!.Value)));

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "venta.medios" && a.IdEntidad == venta);
        Assert.Equal(turno, JsonDocument.Parse(auditoria.ValorNuevo).RootElement.GetProperty("id_turno_recalculado").GetInt32());
    }

    // ---- Facturación ---------------------------------------------------------------------------

    [Fact]
    public async Task UnaVentaFacturadaMueveSusAportesYLaCancelacionPosteriorDejaElAcumuladoEnCero()
    {
        var ctx = await PrepararAsync("mediosfact", escritorio: true);
        await SembrarTurnoAbiertoAsync(ctx);
        await FijarPorcentajeAsync(ctx, ctx.IdTransferencia, 100m);
        var tarjeta = await CrearMedioAsync(ctx, "Tarjeta", porcentajeFacturacion: 40m);

        var vendida = await VenderAsync(ctx, Venta(ctx, [new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-1", 0m)]));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, vendida.Fiscal!.Estado);
        Assert.Equal(new Acumulado(ctx.IdTransferencia, 0m, 100m, 100m), AcumuladoDe(await AcumuladosAsync(ctx), ctx.IdTransferencia));

        // Desde administración, con el turno abierto.
        await LeerOkAsync(await CambiarEnAdministracionAsync(ctx.Admin, vendida.Id, new PagoDeVenta(tarjeta, 100m, null, 0m)));

        var acumulados = await AcumuladosAsync(ctx);
        Assert.Equal(new Acumulado(ctx.IdTransferencia, 0m, 0m, 0m), AcumuladoDe(acumulados, ctx.IdTransferencia));
        Assert.Equal(new Acumulado(tarjeta, -60m, 100m, 100m), AcumuladoDe(acumulados, tarjeta));

        var anulacion = await ctx.Cajero.PostAsync($"/api/ventas/{vendida.Id}/anulacion", null);
        Assert.True(anulacion.IsSuccessStatusCode, await anulacion.Content.ReadAsStringAsync());
        var procesada = await ctx.Admin.PostAsync($"/api/fiscal/facturaciones/{vendida.Fiscal.IdFacturacion}/procesar", null);
        var cuerpo = await procesada.Content.ReadAsStringAsync();
        Assert.True(procesada.StatusCode == HttpStatusCode.OK, cuerpo);
        Assert.Equal(EstadosFiscalesDeVenta.Cancelado, JsonSerializer.Deserialize<EstadoFiscalDeVenta>(cuerpo, OpcionesJson)!.Estado);

        Assert.All(await AcumuladosAsync(ctx), a => Assert.Equal((0m, 0m, 0m), (a.Saldo, a.Vendido, a.Facturado)));
    }

    [Fact]
    public async Task UnaVentaQueNoSeFacturoMueveElDevengoYLoVendidoAlMedioNuevo()
    {
        var ctx = await PrepararAsync("mediosnofact", escritorio: true);
        await SembrarTurnoAbiertoAsync(ctx);
        await FijarPorcentajeAsync(ctx, ctx.IdEfectivo, 10m);
        await FijarPorcentajeAsync(ctx, ctx.IdTransferencia, 30m);

        var vendida = await VenderAsync(ctx, Venta(ctx, [new PagoDeVenta(ctx.IdEfectivo, 100m, null, 0m)]));
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, vendida.Fiscal!.Estado);
        Assert.Equal(new Acumulado(ctx.IdEfectivo, 10m, 100m, 0m), AcumuladoDe(await AcumuladosAsync(ctx), ctx.IdEfectivo));

        await LeerOkAsync(await CambiarEnLaCajaAsync(
            ctx.Cajero, vendida.Id, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-2", 0m)));

        var acumulados = await AcumuladosAsync(ctx);
        Assert.Equal(new Acumulado(ctx.IdEfectivo, 0m, 0m, 0m), AcumuladoDe(acumulados, ctx.IdEfectivo));
        Assert.Equal(new Acumulado(ctx.IdTransferencia, 30m, 100m, 0m), AcumuladoDe(acumulados, ctx.IdTransferencia));
    }

    // ---- Reenvío offline -----------------------------------------------------------------------

    [Fact]
    public async Task ElReenvioOfflineDeUnaVentaYaCambiadaDevuelveLaMismaVentaYOtraComposicionSigueRechazada()
    {
        var ctx = await PrepararAsync("mediosoffline", escritorio: true);
        await SembrarTurnoAbiertoAsync(ctx);
        var reserva = await ctx.Cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(ctx.IdPuntoVenta, "TX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        var original = Venta(ctx, [new PagoDeVenta(ctx.IdEfectivo, 100m, null, 0m)], numero: 1);
        var vendida = await VenderAsync(ctx, original);
        await LeerOkAsync(await CambiarEnLaCajaAsync(
            ctx.Cajero, vendida.Id, new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-3", 0m)));

        var reenvio = await VenderAsync(ctx, original);

        Assert.Equal(vendida.Id, reenvio.Id);
        Assert.Equal(ctx.IdTransferencia, Assert.Single(reenvio.Pagos).IdMedioPago);
        Assert.Single(await CobrosAsync(ctx, vendida.Id));

        var otraComposicion = await ctx.Cajero.PostAsJsonAsync(
            "/api/ventas", Venta(ctx, [new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-OTRA", 0m)], numero: 1));
        await AfirmarErrorAsync(otraComposicion, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");

        // Los medios vigentes también valen como reenvío.
        var conLosVigentes = await VenderAsync(
            ctx, Venta(ctx, [new PagoDeVenta(ctx.IdTransferencia, 100m, "OP-3", 0m)], numero: 1));
        Assert.Equal(vendida.Id, conLosVigentes.Id);
    }
}
