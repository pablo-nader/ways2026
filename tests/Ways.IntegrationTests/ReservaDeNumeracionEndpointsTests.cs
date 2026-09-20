using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): superficie HTTP completa —
/// <c>POST /api/ventas/reservas-numeracion</c> (autorización + validación de
/// <c>ServicioDeReservasDeNumeracion</c>) y <c>SolicitudDeVenta.NumeroPreasignado</c> en
/// <c>POST /api/ventas</c> (<c>ServicioDeVentas.EmitirAsync</c>). Mismo trámite de siembra que
/// <c>VentasModoPuntoVentaTests</c> — no se comparte helper entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReservaDeNumeracionEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private async Task<(HttpClient Cliente, int IdTenant, int IdPuntoVenta)> AprovisionarComoAdminAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio);
        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, resultado.IdTenant, resultado.IdPuntoVenta);
    }

    private async Task<int> AgregarSegundoPuntoVentaAsync(int idTenant, string nombre, ModoPuntoVenta modo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var idEmpresa = await db.Empresas.Select(e => e.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta
        {
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            Modo = modo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    /// <summary>Inserta una fila de <c>reservas_numeracion</c> directo (bypass del endpoint de
    /// reserva) — necesario para los tres tests de aislamiento de conjuntos de abajo: el endpoint
    /// real (<c>ServicioDeReservasDeNumeracion.ReservarAsync</c>) exige
    /// <c>PoliticaDeModoDePuntoVenta</c>, así que un dispositivo NUNCA puede terminar con una fila
    /// viva para un punto de venta que no es el suyo (o, en el caso de tipo, no valida el código
    /// contra <c>tipos_comprobante</c> como sí lo hace el endpoint) — la única forma de expresar
    /// esos fixtures es escribiendo la tabla directo, igual que <c>AsignadorDeNumeroComprobante</c>
    /// (SQL crudo, mismo criterio que <c>NumeracionesComprobanteBackstopTests</c>).</summary>
    private async Task SembrarReservaDirectaAsync(
        int idTenant, int idPuntoVenta, string tipoComprobante, int idDispositivo, long desde, long hasta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO reservas_numeracion
                (id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, created_at, updated_at)
            VALUES ({idTenant}, {idPuntoVenta}, {tipoComprobante}, {idDispositivo}, {desde}, {hasta}, {ahora}, {ahora})
            """);
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Vincula un dispositivo, siembra un cajero y devuelve un <see cref="HttpClient"/> ya
    /// logueado vía <c>login-dispositivo</c> — mismo flujo que <c>VentasModoPuntoVentaTests</c>,
    /// más el id del dispositivo (necesario para leer <c>reservas_numeracion</c> directo).</summary>
    private async Task<(HttpClient Cliente, int IdDispositivo)> LoguearComoCajeroDeDispositivoAsync(
        HttpClient admin, int idTenant, int idPuntoVenta, string sufijo)
    {
        var alta = await admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vinculado = (await alta.Content.ReadFromJsonAsync<DispositivoVinculado>())!;
        var cookieDispositivo = ExtraerCookieDeDispositivo(alta);

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = $"cajero-{sufijo}",
                Mail = $"cajero-{sufijo}-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo($"cajero-{sufijo}", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (cajero, vinculado.Datos.Id);
    }

    private async Task AbrirTurnoAsync(int idTenant, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEmpleadoApertura = await db.Usuarios
            .Where(u => u.IdTenant == idTenant && u.NombreUsuario == "admin")
            .Select(u => u.Id)
            .FirstAsync();

        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int IdArticulo, int IdMedioEfectivo)> SembrarServicioYMedioEfectivoAsync(
        int idTenant, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        // Nombre único por llamada (Guid): ux_areas_nombre_compartido choca si este helper se
        // llama dos veces para el mismo tenant (necesario para sembrar dos artículos con precios
        // distintos en un mismo test, ver ReenviarConUnContenidoDistintoBajoElMismoNumeroPreasignadoEs409).
        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Ventas-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"servicio-{Guid.NewGuid():N}",
            Nombre = "Servicio de prueba",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            IdListaPrecio = idListaGeneral,
            Monto = precio,
            VigenteDesde = ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (articulo.Id, idMedioEfectivo);
    }

    private static SolicitudDeVenta SolicitudDeServicio(
        int idPuntoVenta, int idArticulo, int idMedioPago, decimal precio, long? numeroPreasignado = null,
        int? idCliente = null) =>
        new(idPuntoVenta, idCliente, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioPago, precio, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numeroPreasignado);

    /// <summary>judgment-day (WARNING, ronda 2): cliente REAL distinto del Consumidor Final —
    /// aísla el disyunto idCliente de <see cref="ServicioDeVentas.ExigirMismoContenido"/> sin
    /// tocar ningún otro (mismo artículo, mismo pago, mismo comprobante asociado).</summary>
    private async Task<int> SembrarClienteAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = idTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idListaGeneral, CreditoIlimitado = true,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    /// <summary>judgment-day (WARNING, ronda 2): medio Electrónico (Transferencia, sembrado por
    /// <c>PlantillaDeAprovisionamiento.V1</c>) — aísla el disyunto de pagos de <see
    /// cref="ServicioDeVentas.ExigirMismoContenido"/> con un medio distinto del Efectivo, mismo
    /// importe total.</summary>
    private async Task<int> ObtenerMedioElectronicoAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
    }

    // ---- POST /api/ventas/reservas-numeracion ---------------------------------------------

    /// <summary>Prueba el 403 observable, no una capa aislada: la política del endpoint
    /// (<c>Politicas.RequiereDispositivo</c>) y el chequeo de <c>ServicioDeReservasDeNumeracion.
    /// ReservarAsync</c> (<c>contexto.IdDispositivo ?? throw ... 403</c>, defensa en profundidad
    /// deliberada) devuelven el MISMO 403 por separado — las dos leen la MISMA claim
    /// (<c>ContextoDeUsuarioHttp.IdDispositivo</c>), así que ningún actor real las hace discrepar:
    /// mutar cualquiera de las dos sola no pone ESTA prueba en rojo, confirmado (no solo
    /// asumido), y no hay forma de aislarlas a este nivel HTTP. Cada capa se prueba aislada
    /// aparte, donde sí se puede: la guarda propia del servicio a nivel unitario, sin pasar por
    /// ASP.NET Core (<c>ServicioDeReservasDeNumeracionTests
    /// .ReservarSinClaimDeDispositivoEsProhibido</c>, Ways.Application.Tests); la policy del
    /// endpoint, de forma estructural sobre el <c>EndpointDataSource</c> real
    /// (<c>SuperficieDeAutorizacionTests.CadaRutaConPolicyAdicionalSobreSuGrupoLaApila</c>).</summary>
    [Fact]
    public async Task UnActorWebNoPuedeReservarUnBloque()
    {
        var (admin, _, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnActorWebNoPuedeReservarUnBloque));
        using var _admin = admin;

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnDispositivoNoPuedeReservarParaUnPuntoDeVentaQueNoEsElSuyo()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeReservarParaUnPuntoDeVentaQueNoEsElSuyo));
        var idPuntoVentaAjeno = await AgregarSegundoPuntoVentaAsync(idTenant, "Local 2", ModoPuntoVenta.Escritorio);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVentaPropio, "propio");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVentaAjeno, "TX", 10));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_modo_incompatible", problema.GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task UnaCantidadFueraDeRangoEs400(int cantidad)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(UnaCantidadFueraDeRangoEs400)}-{cantidad}");
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "rango");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", cantidad));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cantidad_invalida", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Kill del límite superior: 500 (el tope exacto,
    /// <c>ServicioDeReservasDeNumeracion.CantidadMaxima</c>) tiene que funcionar — un mutante que
    /// cambie <c>&gt; CantidadMaxima</c> por <c>&gt;= CantidadMaxima</c> lo pone en rojo.</summary>
    [Fact]
    public async Task LaCantidadEnElTopeMaximoFunciona()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(LaCantidadEnElTopeMaximoFunciona));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "tope");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion",
            new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", ServicioDeReservasDeNumeracion.CantidadMaxima));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var bloque = (await respuesta.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(500, bloque.Hasta - bloque.Desde + 1);
    }

    [Fact]
    public async Task UnTipoDeComprobanteInvalidoEs400()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnTipoDeComprobanteInvalidoEs400));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "tipo-invalido");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "NO_EXISTE", 10));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_comprobante_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPuntoDeVentaInexistenteEs404()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(nameof(UnPuntoDeVentaInexistenteEs404));
        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "pv-inexistente");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(999_999, "TX", 10));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task ReservarUnBloqueDevuelveElRangoYCreaUnaFilaViva()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReservarUnBloqueDevuelveElRangoYCreaUnaFilaViva));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "ok");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var bloque = (await respuesta.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(1, bloque.Desde);
        Assert.Equal(10, bloque.Hasta);
        Assert.Equal(idPuntoVenta, bloque.IdPuntoVenta);
        Assert.Equal("TX", bloque.CodigoTipoComprobante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var fila = await db.ReservasNumeracion.SingleAsync(r => r.IdDispositivo == idDispositivo);
        Assert.Equal(1, fila.Desde);
        Assert.Equal(10, fila.Hasta);
        Assert.Null(fila.AbandonadaAt);
    }

    [Fact]
    public async Task PedirUnBloqueNuevoAbandonaElAnteriorViaEndpoint()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(PedirUnBloqueNuevoAbandonaElAnteriorViaEndpoint));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "reissue");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        var segunda = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var bloque2 = (await segunda.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(6, bloque2.Desde);
        Assert.Equal(10, bloque2.Hasta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await db.ReservasNumeracion
            .Where(r => r.IdDispositivo == idDispositivo).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, filas.Count);
        Assert.NotNull(filas[0].AbandonadaAt);
        Assert.Null(filas[1].AbandonadaAt);
    }

    /// <summary>db-error-backstops — INVARIANTE, no una carrera forzada (claims-match-code: se
    /// intentó forzar el 409 de <c>ux_reservas_numeracion_dispositivo_activo</c> con dos POSTs
    /// concurrentes y no se pudo reproducir de forma confiable bajo <c>Task.WhenAll</c> — el row
    /// lock intermedio de <c>AsignarBloqueAsync</c> le da a la segunda transacción margen de sobra
    /// para que su abandono encuentre ya comiteada la fila de la primera y la abandone en limpio,
    /// así que en este arnés las dos siempre terminan en 200. Lo que SÍ es una propiedad
    /// determinística, con cualquier entrelazado posible: al final quedan EXACTAMENTE dos filas
    /// (una viva, una abandonada) — nunca dos vivas — sin importar el orden de llegada. El 409
    /// traducido en sí (para el caso en que la carrera SÍ se dé, p.ej. contra un backend con más
    /// latencia real) lo prueba el bypass crudo de <c>ReservaDeNumeracionBackstopTests</c>.</summary>
    [Fact]
    public async Task DosPedidosConcurrentesDelMismoDispositivoTerminanConExactamenteUnaReservaViva()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(DosPedidosConcurrentesDelMismoDispositivoTerminanConExactamenteUnaReservaViva));
        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "carrera");
        using var _cajero = cajero;
        admin.Dispose();

        var tareaA = cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        var tareaB = cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r =>
            Assert.True(
                r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Status inesperado: {r.StatusCode}"));

        foreach (var conflicto in respuestas.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var problema = await conflicto.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("reserva_de_numeracion_duplicada", problema.GetProperty("codigo").GetString());
        }

        // Bajo el entrelazado común (las dos comitean, 200+200) quedan DOS filas — la primera
        // abandonada por la segunda. Bajo el entrelazado racy (200+409) queda UNA sola — el
        // INSERT que chocó nunca comitea, así que ni su fila ni su bump del contador sobreviven
        // (la transacción entera hace rollback). La única invariante verdadera en los DOS casos
        // es "a lo sumo una viva" — nunca dos.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var filas = await db.ReservasNumeracion.Where(r => r.IdDispositivo == idDispositivo).ToListAsync();
        Assert.Single(filas, r => r.AbandonadaAt is null);
    }

    // ---- POST /api/ventas con NumeroPreasignado ------------------------------------------

    [Fact]
    public async Task UnActorWebNoPuedeMandarUnNumeroPreasignado()
    {
        // PV en modo Web a propósito (no el Escritorio default de AprovisionarComoAdminAsync):
        // sin esto, un actor web contra un PV Escritorio ya recibe 409
        // punto_venta_modo_incompatible por PoliticaDeModoDePuntoVenta, un confound que
        // enmascararía la mutación del guard bajo prueba (mutation-proof-tests regla 3).
        var (admin, idTenant, _) = await AprovisionarComoAdminAsync(
            nameof(UnActorWebNoPuedeMandarUnNumeroPreasignado));
        using var _admin = admin;
        var idPuntoVentaWeb = await AgregarSegundoPuntoVentaAsync(idTenant, "Web", ModoPuntoVenta.Web);

        await AbrirTurnoAsync(idTenant, idPuntoVentaWeb);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var respuesta = await admin.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVentaWeb, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_admitido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroQueNoReservo()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroQueNoReservo));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "sin-reserva");
        using var _cajero = cajero;
        admin.Dispose();

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    // ---- Aislamiento de los TRES conjuntos de igualdad de ExigirNumeroPreasignadoPropioAsync --
    // (judgment-day: solo AbandonadaAt y el rango tenían evidencia de mutación — IdDispositivo,
    // IdPuntoVenta y TipoComprobante, los TRES conjuntos de aislamiento, no. mutation-proof-tests
    // regla 3, "enumerar los conjuntos": cada test de abajo deja los otros cuatro conjuntos
    // satisfechos y solo el suyo propio en desacuerdo, así que borrar CUALQUIER otra cláusula no
    // lo pone en rojo — solo borrar la propia lo hace.

    /// <summary>Aísla <c>r.IdDispositivo == idDispositivo</c> — la cláusula de aislamiento más
    /// crítica de las tres: sin ella, cualquier dispositivo del tenant podría gastar el bloque
    /// reservado de OTRO. Un punto de venta solo admite UN dispositivo activo a la vez
    /// (<c>ux_dispositivos_punto_venta_activo</c>), así que el dispositivo A reserva un bloque
    /// real vía el endpoint y se revoca (<c>DELETE /api/dispositivos/{id}</c> — baja lógica, no
    /// toca <c>reservas_numeracion</c>: el bloque de A sigue VIGENTE); recién ahí el dispositivo B
    /// puede vincularse al MISMO punto de venta e, incluso sin ninguna reserva propia, intenta
    /// vender con un número que cae dentro del rango vigente de A. Punto de venta, tipo, vigencia
    /// y rango coinciden igual — solo el dispositivo no. Mutación: borrar esta cláusula sola
    /// convierte este 409 en un 201 (B emite con el número reservado de A). Confirmado — RED al
    /// borrar la cláusula, GREEN al revertir.</summary>
    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroDeLaReservaVigenteDeOtroDispositivo()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroDeLaReservaVigenteDeOtroDispositivo));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (dispositivoA, idDispositivoA) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVenta, "ajeno-a");
        using (dispositivoA)
        {
            await dispositivoA.PostAsJsonAsync(
                "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));
        }

        var revocacion = await admin.DeleteAsync($"/api/dispositivos/{idDispositivoA}");
        Assert.Equal(HttpStatusCode.NoContent, revocacion.StatusCode);

        var (dispositivoB, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "ajeno-b");
        using var _b = dispositivoB;
        admin.Dispose();

        var respuesta = await dispositivoB.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 5));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Aísla <c>r.IdPuntoVenta == idPuntoVenta</c>. El MISMO dispositivo tiene una fila
    /// viva, en rango, para el tipo correcto — pero de OTRO punto de venta. Esa combinación es
    /// inalcanzable por el endpoint real de reserva (<c>PoliticaDeModoDePuntoVenta</c> nunca deja
    /// a un dispositivo reservar contra un punto de venta que no es el suyo), así que la fila se
    /// siembra directo (<see cref="SembrarReservaDirectaAsync"/>) — prueba la cláusula del SELECT,
    /// no un camino de reserva real. Mutación: borrar esta cláusula sola convierte el 409 en 201.
    /// Confirmado — RED al borrar la cláusula, GREEN al revertir.</summary>
    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaVigenteDeOtroPuntoDeVenta()
    {
        var (admin, idTenant, idPuntoVentaPropio) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaVigenteDeOtroPuntoDeVenta));
        var idPuntoVentaAjeno = await AgregarSegundoPuntoVentaAsync(idTenant, "Local ajeno", ModoPuntoVenta.Escritorio);
        await AbrirTurnoAsync(idTenant, idPuntoVentaPropio);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVentaPropio, "pv-ajeno");
        using var _cajero = cajero;
        admin.Dispose();

        await SembrarReservaDirectaAsync(idTenant, idPuntoVentaAjeno, "TX", idDispositivo, desde: 1, hasta: 10);

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVentaPropio, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 5));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Aísla <c>r.TipoComprobante == codigoTipoComprobante</c>. El MISMO dispositivo, el
    /// MISMO punto de venta, una fila viva y en rango — pero para OTRO tipo de comprobante. La
    /// fila se siembra directo (<see cref="SembrarReservaDirectaAsync"/>): el endpoint de reserva
    /// nunca deja pasar un código no válido, y lo que esta cláusula verifica no es esa validación,
    /// es la pertenencia. Mutación: borrar esta cláusula sola convierte el 409 en 201. Confirmado
    /// — RED al borrar la cláusula, GREEN al revertir.</summary>
    [Fact]
    public async Task UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaVigenteDeOtroTipoDeComprobante()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoNoPuedeUsarUnNumeroDeUnaReservaVigenteDeOtroTipoDeComprobante));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, idDispositivo) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVenta, "tipo-ajeno");
        using var _cajero = cajero;
        admin.Dispose();

        await SembrarReservaDirectaAsync(idTenant, idPuntoVenta, "NCX", idDispositivo, desde: 1, hasta: 10);

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 5));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    // ---- Cross-tenant: NO probado, ver el doc-comment de arriba ------------------------------
    // No hay un cuarto test análogo para "la reserva pertenece a otro tenant": reservas_numeracion
    // tiene RLS de tenant habilitado (migración 20260920105427_ReservaDeNumeracion,
    // HabilitarRlsDeTenant) — un WaysDbContext scopeado al tenant del actor nunca puede LEER una
    // fila de otro tenant, con o sin la cláusula IdDispositivo/IdPuntoVenta/TipoComprobante en el
    // predicado C#. Ese caso sería sobredeterminado por RLS (mutation-proof-tests regla 1/3): un
    // test así pasaría igual con las tres cláusulas de igualdad borradas, así que no probaría lo
    // que su nombre diría que prueba (claims-match-code) — no se escribe.

    /// <summary>LOS DOS CONJUNTOS del rango, <c>numero &gt;= Desde</c> y <c>numero &lt;= Hasta</c>,
    /// cada uno aislado (mutation-proof-tests regla 3, "enumerar los conjuntos") — a diferencia de
    /// <see cref="UnDispositivoNoPuedeUsarUnNumeroQueNoReservo"/> (sin NINGUNA reserva, donde los
    /// cinco conjuntos fallan a la vez y no aíslan nada), acá SÍ hay una reserva propia y vigente
    /// del dispositivo/PV/tipo correctos — un número apenas por encima y otro apenas por debajo
    /// del rango, cada uno matando solo su propio conjunto.
    ///
    /// judgment-day (FIX 1, ronda 1 — reescrito): el bloque descartable [1,9] que arma el "apenas
    /// por debajo" ahora lo quema un dispositivo DISTINTO, revocado antes de vincular al
    /// dispositivo bajo prueba — nunca el MISMO dispositivo. Desde que
    /// <c>ExigirNumeroPreasignadoPropioAsync</c> dejó de exigir <c>AbandonadaAt IS NULL</c> (FIX
    /// 1), un bloque descartable propio y ABANDONADO seguiría perteneciendo al dispositivo bajo
    /// prueba, y el número 9 pasaría a aceptarse por esa fila en vez de rechazarse — dejando de
    /// aislar nada. Con el descartable en otro dispositivo, el dispositivo bajo prueba tiene una
    /// única fila propia — [10,15], vigente — así que 9 y 16 solo pueden fallar por el rango.
    /// </summary>
    [Theory]
    [InlineData(9L)]  // apenas por debajo de Desde=10 — mata "numero >= Desde".
    [InlineData(16L)] // apenas por encima de Hasta=15 — mata "numero <= Hasta".
    public async Task UnDispositivoNoPuedeUsarUnNumeroFueraDelRangoDeSuPropiaReservaViva(long numeroFueraDeRango)
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            $"{nameof(UnDispositivoNoPuedeUsarUnNumeroFueraDelRangoDeSuPropiaReservaViva)}-{numeroFueraDeRango}");
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        // Bloque descartable [1,9], quemado por un dispositivo DISTINTO del que está bajo prueba
        // (revocado antes de que ese otro se vincule) — ver el doc-comment de arriba.
        var (descartable, idDispositivoDescartable) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVenta, $"descartable-{numeroFueraDeRango}");
        using (descartable)
        {
            await descartable.PostAsJsonAsync(
                "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 9));
        }
        var revocacion = await admin.DeleteAsync($"/api/dispositivos/{idDispositivoDescartable}");
        Assert.Equal(HttpStatusCode.NoContent, revocacion.StatusCode);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(
            admin, idTenant, idPuntoVenta, $"fuera-de-rango-{numeroFueraDeRango}");
        using var _cajero = cajero;
        admin.Dispose();

        // Bloque propio del dispositivo bajo prueba, vigente: [10,15].
        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 6));

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: numeroFueraDeRango));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_no_reservado", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day (CRITICAL, ronda 1 — invierte la premisa original de este test, que
    /// afirmaba 409 acá): abandonar un bloque gobierna de qué bloque el DISPOSITIVO puede sacar
    /// números NUEVOS, nunca cuáles acepta el SERVIDOR — un número que en verdad se reservó para
    /// este dispositivo sigue siendo suyo aunque el bloque que lo contenía ya no esté vigente,
    /// porque el ticket físico ya pudo haber quedado en manos del cliente antes del abandono. El
    /// doble uso lo sigue previniendo <c>ux_comprobantes_venta_numero</c> más la guarda de
    /// idempotencia, no <c>abandonada_at</c>.</summary>
    [Fact]
    public async Task UnDispositivoPuedeUsarUnNumeroDeUnaReservaAbandonada()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoPuedeUsarUnNumeroDeUnaReservaAbandonada));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "abandonada-ok");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));
        // Segundo pedido: abandona el bloque [1,5] y entrega [6,10].
        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        // Un número del bloque YA ABANDONADO [1,5] — antes rechazado con 409, ahora aceptado.
        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 3));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(3, emitido.Numero);
    }

    /// <summary>Happy path + prueba de que el contador NO se vuelve a bumpear: una venta
    /// server-asignada posterior (sin NumeroPreasignado) tiene que seguir en 11, no en 6 — si
    /// <c>EmitirAsync</c> llamara a <c>AsignarComprometidoAsync</c> además de usar el número
    /// pre-asignado, el contador avanzaría de más.</summary>
    [Fact]
    public async Task UnDispositivoPuedeVenderConUnNumeroPreasignadoPropioYElContadorNoSeDuplica()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(UnDispositivoPuedeVenderConUnNumeroPreasignadoPropioYElContadorNoSeDuplica));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "propio");
        using var _cajero = cajero;

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));

        var respuesta = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 5));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(5, emitido.Numero);

        // Una venta server-asignada (admin, web) sobre otro punto de venta Web del mismo tenant no
        // comparte la serie — se usa el MISMO punto de venta vía un segundo cajero de escritorio
        // para verificar el contador sin abrir un tercer actor: pide un bloque nuevo (abandona el
        // vigente) y el desde tiene que ser 11, nunca 6.
        var siguiente = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 1));
        var bloqueSiguiente = (await siguiente.Content.ReadFromJsonAsync<BloqueDeNumeracionReservado>())!;
        Assert.Equal(11, bloqueSiguiente.Desde);
    }

    /// <summary>judgment-day (CRITICAL, ronda 1): reenvía dos <see cref="SolicitudDeVenta"/>
    /// EQUIVALENTES pero construidas por separado (dos llamadas a <see cref="SolicitudDeServicio"/>,
    /// nunca la misma instancia reusada) — antes este test reenviaba el MISMO objeto, así que
    /// pasaba aunque la comparación de contenido de <c>EmitirAsync</c> comparara por identidad de
    /// referencia en vez de por valor; con dos instancias separadas, solo sigue en verde si la
    /// comparación es de verdad por contenido.</summary>
    [Fact]
    public async Task ReenviarLaMismaVentaConElMismoNumeroPreasignadoDevuelveElMismoComprobante()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarLaMismaVentaConElMismoNumeroPreasignadoDevuelveElMismoComprobante));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var primera = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));
        var segunda = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);

        var emitido1 = (await primera.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        var emitido2 = (await segunda.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(emitido1.Id, emitido2.Id);
        Assert.Equal(emitido1.Numero, emitido2.Numero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var cantidad = await db.ComprobantesVenta
            .CountAsync(c => c.IdPuntoVenta == idPuntoVenta && c.Numero == 1);
        Assert.Equal(1, cantidad);
    }

    /// <summary>judgment-day (CRITICAL, ronda 1; aislada en ronda 2 — mutation-proof-tests): el
    /// reenvío trae otro artículo bajo el MISMO número pre-asignado — antes de este fix
    /// <c>BuscarPorNumeroComprometidoAsync</c> devolvía en silencio la PRIMERA venta, con items
    /// ajenos al pedido que en verdad llegó. Mismo cliente (Consumidor Final, ambos null), mismo
    /// comprobante asociado (null) y mismo pago (Efectivo $100 en las dos: el segundo artículo
    /// tiene el MISMO precio, a propósito — desde ronda 2 el total no participa de la
    /// comparación) — la ÚNICA diferencia entre ambas solicitudes es la línea, así que un
    /// mutante que borre cualquier OTRO disyunto de <see
    /// cref="ServicioDeVentas.ExigirMismoContenido"/> no puede salvar esta prueba (evidencia de
    /// mutación: borrar solo el disyunto de líneas la pone en rojo; revertido, vuelve a
    /// verde).</summary>
    [Fact]
    public async Task ReenviarConOtroArticuloBajoElMismoNumeroPreasignadoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarConOtroArticuloBajoElMismoNumeroPreasignadoEs409));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo1, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var (idArticulo2, _) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay-articulo");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var primera = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo1, idMedioEfectivo, 100m, numeroPreasignado: 1));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segunda = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo2, idMedioEfectivo, 100m, numeroPreasignado: 1));

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_con_otro_contenido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var cantidad = await db.ComprobantesVenta
            .CountAsync(c => c.IdPuntoVenta == idPuntoVenta && c.Numero == 1);
        Assert.Equal(1, cantidad);
    }

    /// <summary>judgment-day (WARNING, ronda 2 — mutation-proof-tests): aísla el disyunto
    /// idCliente de <see cref="ServicioDeVentas.ExigirMismoContenido"/>. Mismo artículo, mismo
    /// pago (Efectivo $100) y mismo comprobante asociado (null) en las dos solicitudes — la
    /// ÚNICA diferencia es el cliente (Consumidor Final vs un cliente real), así que solo ese
    /// disyunto puede tirar el 409 acá (evidencia de mutación: borrar solo el disyunto de
    /// idCliente la pone en rojo; revertido, vuelve a verde).</summary>
    [Fact]
    public async Task ReenviarConOtroClienteBajoElMismoNumeroPreasignadoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarConOtroClienteBajoElMismoNumeroPreasignadoEs409));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var idClienteDistinto = await SembrarClienteAsync(idTenant, "Cliente replay");

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay-cliente");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var primera = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segunda = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(
                idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1, idCliente: idClienteDistinto));

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_con_otro_contenido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day (WARNING, ronda 2 — mutation-proof-tests): aísla el disyunto
    /// idComprobanteAsociado de <see cref="ServicioDeVentas.ExigirMismoContenido"/>. Dos
    /// devoluciones NCX con el mismo artículo, mismo cliente (Consumidor Final) y mismos pagos
    /// (ninguno, como toda devolución) bajo el mismo número pre-asignado — construidas con
    /// <c>with</c> a partir de la MISMA solicitud base, así que la ÚNICA diferencia entre ambas
    /// es a qué comprobante original se asocian (evidencia de mutación: borrar solo el disyunto
    /// de idComprobanteAsociado la pone en rojo; revertido, vuelve a verde).</summary>
    [Fact]
    public async Task ReenviarConOtroComprobanteAsociadoBajoElMismoNumeroPreasignadoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarConOtroComprobanteAsociadoBajoElMismoNumeroPreasignadoEs409));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay-asociado");
        using var _cajero = cajero;
        admin.Dispose();

        var original1 = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m));
        Assert.Equal(HttpStatusCode.Created, original1.StatusCode);
        var emitidoOriginal1 = (await original1.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var original2 = await cajero.PostAsJsonAsync(
            "/api/ventas", SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m));
        Assert.Equal(HttpStatusCode.Created, original2.StatusCode);
        var emitidoOriginal2 = (await original2.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "NCX", 5));

        var devolucion = new SolicitudDeVenta(
            idPuntoVenta, null, "NCX", emitidoOriginal1.Id,
            [new LineaDeVenta(idArticulo, 1m, null)], [], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: 1);
        var primera = await cajero.PostAsJsonAsync("/api/ventas", devolucion);
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var devolucionOtroAsociado = devolucion with { IdComprobanteAsociado = emitidoOriginal2.Id };
        var segunda = await cajero.PostAsJsonAsync("/api/ventas", devolucionOtroAsociado);

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_con_otro_contenido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day (WARNING, ronda 2 — mutation-proof-tests): aísla el disyunto de
    /// pagos de <see cref="ServicioDeVentas.ExigirMismoContenido"/>. Mismo artículo, mismo
    /// cliente (Consumidor Final) y mismo comprobante asociado (null) en las dos solicitudes —
    /// la ÚNICA diferencia es la composición de pagos (Efectivo vs Transferencia, mismo importe
    /// total), así que solo ese disyunto puede tirar el 409 acá (evidencia de mutación: borrar
    /// solo el disyunto de pagos la pone en rojo; revertido, vuelve a verde).</summary>
    [Fact]
    public async Task ReenviarConOtraComposicionDePagosBajoElMismoNumeroPreasignadoEs409()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarConOtraComposicionDePagosBajoElMismoNumeroPreasignadoEs409));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);
        var idMedioTransferencia = await ObtenerMedioElectronicoAsync(idTenant);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay-pagos");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var primera = await cajero.PostAsJsonAsync(
            "/api/ventas",
            SolicitudDeServicio(idPuntoVenta, idArticulo, idMedioEfectivo, 100m, numeroPreasignado: 1));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segundaSolicitud = new SolicitudDeVenta(
            idPuntoVenta, null, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioTransferencia, 100m, "ref-replay-pagos", 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: 1);
        var segunda = await cajero.PostAsJsonAsync("/api/ventas", segundaSolicitud);

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_con_otro_contenido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>judgment-day (ronda 2, decisión de diseño — no un disyunto de <see
    /// cref="ServicioDeVentas.ExigirMismoContenido"/>, a propósito): <c>Observaciones</c> es
    /// texto libre, metadata incidental sobre el pedido, no un rasgo que distinga una venta de
    /// otra (ver el comentario dentro de <c>ExigirMismoContenido</c>) — así que NO participa de
    /// la guarda de identidad. Mismo artículo, cliente, comprobante asociado y pagos en las dos
    /// solicitudes bajo el mismo número pre-asignado; solo cambia la nota — tiene que devolver el
    /// MISMO comprobante, nunca 409.</summary>
    [Fact]
    public async Task ReenviarConOtraObservacionBajoElMismoNumeroPreasignadoDevuelveElMismoComprobante()
    {
        var (admin, idTenant, idPuntoVenta) = await AprovisionarComoAdminAsync(
            nameof(ReenviarConOtraObservacionBajoElMismoNumeroPreasignadoDevuelveElMismoComprobante));
        await AbrirTurnoAsync(idTenant, idPuntoVenta);
        var (idArticulo, idMedioEfectivo) = await SembrarServicioYMedioEfectivoAsync(idTenant, 100m);

        var (cajero, _) = await LoguearComoCajeroDeDispositivoAsync(admin, idTenant, idPuntoVenta, "replay-observacion");
        using var _cajero = cajero;
        admin.Dispose();

        await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 5));

        var primeraSolicitud = new SolicitudDeVenta(
            idPuntoVenta, null, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(idMedioEfectivo, 100m, null, 0m)], null, "entregar en mostrador",
            IdPresupuestoOrigen: null, NumeroPreasignado: 1);
        var primera = await cajero.PostAsJsonAsync("/api/ventas", primeraSolicitud);
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        var emitido1 = (await primera.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var segundaSolicitud = primeraSolicitud with { Observaciones = "ENTREGAR EN MOSTRADOR, urgente" };
        var segunda = await cajero.PostAsJsonAsync("/api/ventas", segundaSolicitud);

        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        var emitido2 = (await segunda.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(emitido1.Id, emitido2.Id);
    }
}
