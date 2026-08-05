using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 5 (tasks 5.5-5.7): cobertura NUEVA del cableado quirúrgico de
/// checkout/anulación contra <c>turnos_caja</c> — no modifica ningún archivo de stage 5 (esos
/// solo ganan la siembra de un turno abierto en su <c>PrepararAsync</c>, ver
/// <see cref="VentasCheckoutTests"/>/<see cref="VentasAtomicidadYConcurrenciaTests"/>/
/// <see cref="AnulacionTests"/>). A diferencia de esos archivos, <see cref="PrepararAsync"/> acá
/// NUNCA abre un turno por defecto — cada prueba lo abre (o no) explícitamente, porque el punto
/// de varias de ellas es precisamente el estado del turno.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasTurnoWiringTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdMedioEfectivo,
        int IdListaPrecio, int IdArea, int IdAlicuotaIva, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Turno-wiring-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Turno Wiring", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idTipoComprobanteTx,
            idMedioEfectivo, lista.Id, area.Id, idAlicuotaIva, admin);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, decimal limiteCredito = 0)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = limiteCredito,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private long _numeroSecuencial = 1;

    /// <summary>Pago sembrado directo (bypass <c>EmitirAsync</c>) — mismo helper que
    /// <c>CajaCierreAtomicidadYConcurrenciaTests.SembrarPagoAsync</c>: deja el medio arqueable
    /// ANTES de una carrera, así el set de conteos declarados en el cierre queda fijo sin
    /// importar el resultado de la carrera (solo el SET de medios tiene que matchear lo
    /// arqueable, no el valor declarado).</summary>
    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = await db.Clientes.Select(c => c.Id).FirstAsync(),
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- task 5.5: el turno resuelto server-side queda persistido, siempre --------------------

    [Fact]
    public async Task UnaVentaEmitidaPersisteElTurnoAbiertoResueltoServerSide()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaEmitidaPersisteElTurnoAbiertoResueltoServerSide));
        var turno = await AbrirTurnoAsync(ctx);
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-turno-wiring", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Turno Wiring");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idTurnoPersistido = await db.ComprobantesVenta
            .Where(c => c.Id == emitido.Id).Select(c => c.IdTurnoCaja).SingleAsync();

        Assert.Equal(turno.Id, idTurnoPersistido);
    }

    // ---- task 5.5: sin turno abierto, rechaza ANTES de cualquier consulta de precio/oferta ----

    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>Mismo criterio de aislamiento que <c>VentasCheckoutTests.EmitirYContarConsultasAsync</c>:
    /// <c>ServicioDeVentas</c> se construye DIRECTO (sin HTTP) para que el conteo de consultas no
    /// se contamine con las de autenticación/sesión — acá el punto es contar SOLO lo que
    /// <c>EmitirAsync</c> dispara antes de lanzar.</summary>
    [Fact]
    public async Task VenderSinTurnoAbiertoRechazaAntesDeCualquierConsultaDePrecioUOferta()
    {
        var ctx = await PrepararAsync(nameof(VenderSinTurnoAbiertoRechazaAntesDeCualquierConsultaDePrecioUOferta));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-sin-turno", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Turno");
        // Turno NUNCA se abre para este punto de venta acá — a diferencia de las otras pruebas
        // de este archivo.

        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<Ways.Domain.Stock.MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<Ways.Domain.Caja.EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: ctx.IdEmpleadoAdmin);
        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new Ways.Application.Ofertas.ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var lector = new LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new ServicioDeTurnos(db, reloj, contexto, lector);
        var servicioDeVentas = new ServicioDeVentas(db, reloj, contexto, servicioDeOfertas, servicioDeTurnos);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        var excepcion = await Assert.ThrowsAsync<ErrorDominio>(() => servicioDeVentas.EmitirAsync(solicitud));

        Assert.Equal("turno_no_abierto", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);

        // ResolverTipoComprobanteAsync + ResolverPuntoVentaAsync + ResolverTurnoAbiertoAsync: 3
        // consultas EF antes del rechazo — ServicioDeOfertas.ResolverAsync (pricing/ofertas) SOLA
        // ya dispara 7 por sí misma (doc-comment de EmitirAsync), así que 3 confirma que ninguna
        // de esas corrió.
        Assert.Equal(3, contador.Consultas);
    }

    // ---- task 5.6: 3ra superficie racy — una venta compitiendo con un cierre -------------------

    [Fact]
    public async Task UnaVentaQueCompiteConUnCierreQuedaContadaEnElArqueoORechazadaNuncaSinContar()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(UnaVentaQueCompiteConUnCierreQuedaContadaEnElArqueoORechazadaNuncaSinContar)}-{ronda}");
            var turno = await AbrirTurnoAsync(ctx);
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-race-cierre", 100m);
            var idCliente = await SembrarClienteAsync(ctx, "Cliente Race Cierre");

            // El ancla (efectivo) ya queda arqueable ANTES de la carrera — el conteo declarado
            // de abajo tiene que ser un SET fijo sin importar si la venta gana o pierde.
            await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 500m);

            var solicitudDeVenta = new SolicitudDeVenta(
                ctx.IdPuntoVenta, idCliente, "TX", null,
                [new LineaDeVenta(idArticulo, 1m, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
                null, null);
            var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 0m)], null);

            var tareaVenta = ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudDeVenta);
            var tareaCierre = ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);

            var respuestaVenta = await tareaVenta;
            var respuestaCierre = await tareaCierre;

            Assert.Contains(respuestaVenta.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            if (respuestaVenta.StatusCode == HttpStatusCode.Conflict)
            {
                var problema = await respuestaVenta.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
            }

            // El cierre es el único que compite por cerrar — siempre tiene que ganar en algún
            // momento (ya cerró antes de que la venta lo viera, o se cierra después de que la
            // venta comiteó).
            Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var importeEsperado = await db.ArqueosTurno
                .Where(a => a.IdTurnoCaja == turno.Id && a.IdMedioPago == ctx.IdMedioEfectivo)
                .Select(a => a.ImporteEsperado).SingleAsync();

            var esperado = respuestaVenta.StatusCode == HttpStatusCode.Created ? 600m : 500m;
            Assert.Equal(esperado, importeEsperado);
        }
    }

    // ---- task 5.7: anulación respeta el gate de turno cerrado ----------------------------------

    [Fact]
    public async Task AnulacionEsRechazadaCon409TurnoCerradoCuandoElTurnoDelComprobanteYaCerro()
    {
        var ctx = await PrepararAsync(nameof(AnulacionEsRechazadaCon409TurnoCerradoCuandoElTurnoDelComprobanteYaCerro));
        var turno = await AbrirTurnoAsync(ctx);
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-anulacion-turno-cerrado", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulación Turno Cerrado");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);
        var respuestaVenta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuestaVenta.StatusCode);
        var emitido = (await respuestaVenta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var solicitudDeCierre = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null);
        var respuestaCierre = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitudDeCierre);
        Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuestaAnulacion.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuestaAnulacion.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("turno_cerrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnComprobanteConTurnoNuloDeStage5SigueSiendoAnulable()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteConTurnoNuloDeStage5SigueSiendoAnulable));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Turno Nulo");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = null, // era stage-5 — nunca se resiembra (decisión 8, sin backfill).
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = idCliente,
            Subtotal = 50m,
            DescuentoTotal = 0m,
            Total = 50m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{comprobante.Id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var anulado = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);
    }
}
