using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente (Slice 3, task 3.13, judgment-day slice-2 finding, judge A): cierra el
/// TOCTOU entre <c>ServicioDeVentas.AnularAsync</c> y <c>ServicioDeReliquidacion.EjecutarAsync</c>
/// — las dos transacciones ahora comparten el MISMO lock del cliente (<c>SELECT ... FROM clientes
/// ... FOR UPDATE</c>, tomado como su primer statement de escritura real), así que Postgres
/// serializa genuinamente cuál de las dos avanza primero. Carrera natural con <c>Task.WhenAll</c>
/// sobre varias rondas — mismo criterio que
/// <c>CajaCierreAtomicidadYConcurrenciaTests.DosCierresConcurrentesDelMismoTurnoProducenExactamenteUnGanador</c>
/// y <c>ReliquidacionTests.DosReliquidacionesConcurrentesDelMismoClienteEscribenExactamenteUnMovimiento</c>:
/// las dos primeras usan exactamente el mismo tipo de <c>SELECT ... FOR UPDATE</c> crudo vía
/// ADO.NET (no vía LINQ), que <c>DbCommandInterceptor</c> no puede interceptar (solo ve comandos
/// que EF Core arma para sus propias consultas/SaveChanges) — así que un rendezvous forzado con
/// <c>DbCommandInterceptor</c> (el mecanismo de <c>ParametrosTests</c>, que sí apunta a una
/// consulta LINQ) no aplica acá; la serialización real del lock de Postgres es la que prueba el
/// invariante, ronda tras ronda.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AnulacionReliquidacionRaceTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva, int IdListaPrecio,
        int IdMedioCuentaCorriente);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area race", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva, idListaPrecio, medioCc.Id);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Ways.Domain.Articulos.Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = Ways.Domain.Articulos.UnidadVenta.Unidad,
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

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = 0m, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private async Task SubirPrecioAsync(Contexto ctx, int idArticulo, decimal nuevoPrecio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var vieja = await db.Precios
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == ctx.IdListaPrecio && p.VigenteHasta == null)
            .SingleAsync();
        vieja.VigenteHasta = ahora;

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = ctx.IdListaPrecio, Monto = nuevoPrecio,
            VigenteDesde = ahora, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnulacionYReliquidacionDelMismoClienteEnCarreraGananExactamenteUna()
    {
        for (var ronda = 0; ronda < 5; ronda++)
        {
            var ctx = await PrepararAsync($"{nameof(AnulacionYReliquidacionDelMismoClienteEnCarreraGananExactamenteUna)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, $"articulo-toctou-{ronda}", 100m);
            var idCliente = await SembrarClienteAsync(ctx, $"Cliente toctou {ronda}");

            var solicitudVenta = new SolicitudDeVenta(
                ctx.IdPuntoVenta, idCliente, "TX", null,
                [new LineaDeVenta(idArticulo, 1m, null)],
                [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 100m, null, 0m)],
                null, null);
            var respuestaVenta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitudVenta);
            Assert.Equal(HttpStatusCode.Created, respuestaVenta.StatusCode);
            var venta = (await respuestaVenta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

            await SubirPrecioAsync(ctx, idArticulo, 150m);

            var tareaAnulacion = ctx.Admin.PostAsync($"/api/ventas/{venta.Id}/anulacion", null);
            var tareaReliquidacion = ctx.Admin.PostAsJsonAsync(
                $"/api/clientes/{idCliente}/cuenta-corriente/reliquidacion", new SolicitudDeReliquidacion(ctx.IdPuntoVenta));

            var respuestaAnulacion = await tareaAnulacion;
            var respuestaReliquidacion = await tareaReliquidacion;

            // La reliquidación NUNCA falla por la carrera en sí (siempre es 200, no-op incluido):
            // o corre ANTES de la anulación (marca el consumo, delta 50) o corre DESPUÉS (el
            // consumo ya no está 'emitido', el scan de elegibilidad lo excluye, no-op limpio).
            var cuerpoReliquidacion = await respuestaReliquidacion.Content.ReadAsStringAsync();
            Assert.True(respuestaReliquidacion.StatusCode == HttpStatusCode.OK, cuerpoReliquidacion);

            // La anulación gana (200) o pierde por reliquidación (409 consumo_reliquidado) — nunca
            // ninguna otra cosa.
            Assert.Contains(respuestaAnulacion.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            if (respuestaAnulacion.StatusCode == HttpStatusCode.Conflict)
            {
                var problema = await respuestaAnulacion.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("consumo_reliquidado", problema.GetProperty("codigo").GetString());
            }

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var movimientoConsumo = await db.MovimientosCuentaCorriente
                .Where(m => m.IdComprobanteVenta == venta.Id && m.Tipo == TipoMovimientoCc.Consumo).SingleAsync();
            var estadoComprobante = await db.ComprobantesVenta.Where(c => c.Id == venta.Id).Select(c => c.Estado).SingleAsync();

            if (respuestaAnulacion.StatusCode == HttpStatusCode.OK)
            {
                // La anulación ganó: el comprobante quedó anulado y su consumo NUNCA puede
                // terminar marcado como reliquidado — el estado "revertido y reliquidado" a la
                // vez es justamente el que este fix vuelve irrepresentable.
                Assert.Equal(EstadoComprobante.Anulado, estadoComprobante);
                Assert.Null(movimientoConsumo.IdMovimientoActualizacion);
            }
            else
            {
                // La reliquidación ganó: el consumo quedó marcado y el comprobante sigue emitido
                // — la anulación, al perder la carrera, nunca revirtió nada.
                Assert.Equal(EstadoComprobante.Emitido, estadoComprobante);
                Assert.NotNull(movimientoConsumo.IdMovimientoActualizacion);
            }

            // Invariante final, sin importar quién ganó: Cliente.Saldo == Σ importe.
            var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
            var sumaMovimientos = await db.MovimientosCuentaCorriente.Where(m => m.IdCliente == idCliente).SumAsync(m => m.Importe);
            Assert.Equal(sumaMovimientos, saldo);
        }
    }
}
