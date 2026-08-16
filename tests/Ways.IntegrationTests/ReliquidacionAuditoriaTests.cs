using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 4 (task 4.13): <c>cc.reliquidacion</c> punta a punta
/// contra Postgres real — before-image tomado del <c>SELECT … FOR UPDATE</c> ya lockeado (design
/// decisión 9, mutation target de la slice, fila 3) y los dos caminos no-op (sin elegibles, delta
/// cero) mudos tanto en el ledger como en la auditoría.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReliquidacionAuditoriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdArea, int IdAlicuotaIva, int IdListaPrecio,
        int IdMedioCuentaCorriente, int IdTipoComprobanteTx, HttpClient Admin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Auditoria-cc-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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

        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        // La reliquidación no exige turno (design decisión 4), pero el checkout que crea el
        // Consumo sí — sembrado directo, mismo criterio que ReliquidacionTests.PrepararAsync.
        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, area.Id, idAlicuotaIva, idListaPrecio,
            medioCc.Id, idTipoComprobanteTx, admin);
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
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
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

    /// <summary>Un Consumo real, vía checkout — mismo criterio que
    /// <c>ReliquidacionTests.RealizarConsumoAsync</c>.</summary>
    private static async Task RealizarConsumoAsync(Contexto ctx, int idCliente, int idArticulo, decimal cantidad, decimal precio)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, cantidad * precio, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
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

    private static async Task<HttpResponseMessage> EjecutarAsync(Contexto ctx, int idCliente) =>
        await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/reliquidacion", new SolicitudDeReliquidacion(ctx.IdPuntoVenta));

    // ---- task 4.13 / mutation target 4.8 (design mutation-targets, slice 4, fila 3) ----------------

    /// <summary>spec `auditoria-de-operaciones`: cobertura de `cc.reliquidacion`. Mutation target
    /// (slice 4, fila 3): releer <c>saldo</c> DESPUÉS del <c>UPDATE</c> de saldo (en vez de usar el
    /// del <c>FOR UPDATE</c> ya tomado) hace que <c>anterior</c> quede igual a <c>nuevo</c> acá.</summary>
    [Fact]
    public async Task UnaReliquidacionConDiferenciaEscribeUnaFilaDeAuditoriaConSaldoAnteriorDistintoDeNuevo()
    {
        var ctx = await PrepararAsync(nameof(UnaReliquidacionConDiferenciaEscribeUnaFilaDeAuditoriaConSaldoAnteriorDistintoDeNuevo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-reliquidacion-auditoria", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente reliquidacion auditoria");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var respuesta = await EjecutarAsync(ctx, idCliente);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        var resultado = JsonSerializer.Deserialize<ResultadoDeReliquidacion>(cuerpo, OpcionesJson)!;
        Assert.Equal(50m, resultado.Delta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "cc.reliquidacion" && a.IdEntidad == idCliente);

        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Equal("cliente", fila.Entidad);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdActor);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(100m, anterior.GetProperty("saldo").GetDecimal());
        Assert.Equal(150m, nuevo.GetProperty("saldo").GetDecimal());
        Assert.NotEqual(anterior.GetProperty("saldo").GetDecimal(), nuevo.GetProperty("saldo").GetDecimal());
        Assert.Equal(50m, nuevo.GetProperty("diferencia").GetDecimal());
        Assert.Equal(1, nuevo.GetProperty("consumos_actualizados").GetInt32());

        var movimiento = await db.MovimientosCuentaCorriente
            .SingleAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios);
        Assert.Equal(movimiento.Id, nuevo.GetProperty("id_movimiento").GetInt32());
    }

    // ---- task 4.13: los dos caminos no-op --------------------------------------------------------

    /// <summary>spec `auditoria-de-operaciones`: el primero de los dos no-ops — sin consumos
    /// elegibles, la transacción comitea sin escribir nada, ni ledger ni auditoría.</summary>
    [Fact]
    public async Task UnaReliquidacionSinConsumosElegiblesNoEscribeFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnaReliquidacionSinConsumosElegiblesNoEscribeFilaDeAuditoria));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente reliquidacion sin elegibles");

        var respuesta = await EjecutarAsync(ctx, idCliente);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        var resultado = JsonSerializer.Deserialize<ResultadoDeReliquidacion>(cuerpo, OpcionesJson)!;
        Assert.Equal(0m, resultado.Delta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "cc.reliquidacion" && a.IdEntidad == idCliente));
    }

    /// <summary>El segundo no-op — un consumo elegible cuyo precio nunca cambió (delta cero): la
    /// transacción comitea sin escribir nada, mismo criterio que el camino "sin elegibles" aunque
    /// la causa sea distinta.</summary>
    [Fact]
    public async Task UnaReliquidacionConDeltaCeroNoEscribeFilaDeAuditoria()
    {
        var ctx = await PrepararAsync(nameof(UnaReliquidacionConDeltaCeroNoEscribeFilaDeAuditoria));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-reliquidacion-auditoria-delta-cero", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente reliquidacion delta cero");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        // Precio SIN cambios — hay un consumo elegible, pero re-precificarlo da delta cero.

        var respuesta = await EjecutarAsync(ctx, idCliente);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        var resultado = JsonSerializer.Deserialize<ResultadoDeReliquidacion>(cuerpo, OpcionesJson)!;
        Assert.Equal(0m, resultado.Delta);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "cc.reliquidacion" && a.IdEntidad == idCliente));
        Assert.Equal(
            0, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));
    }
}
