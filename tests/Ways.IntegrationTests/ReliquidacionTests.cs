using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente (Slice 3, tasks 3.6-3.12, 3.14): la reliquidación a precio del día
/// punta a punta contra Postgres real — identidad de derivación preview/commit, atomicidad de los
/// tres statements de escritura reales (pasos 6/7/8; los pasos 1-5 son lock/lectura/puros, sin
/// punto de falla propio, mismo criterio que <see cref="CajaCierreAtomicidadYConcurrenciaTests"/>),
/// las dos carreras enumeradas por design (reliquidación × venta, dos reliquidaciones),
/// idempotencia, un único movimiento por corrida, la lista de precios ACTUAL del cliente, el
/// presupuesto de consultas y la matriz de autorización.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReliquidacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdArea, int IdAlicuotaIva, int IdListaPrecio,
        int IdListaPrecioAlterna, int IdMedioCuentaCorriente, int IdTipoComprobanteTx, HttpClient Admin);

    private long _numeroSecuencial = 1;

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area RQ", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        var listaAlterna = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista alterna RQ", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaAlterna);
        await db.SaveChangesAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        // La reliquidación no exige turno (design decisión 4), pero el checkout que crea los
        // Consumo sí — sembrado directo, mismo criterio que VentasCheckoutTests.
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, area.Id, idAlicuotaIva, idListaPrecio,
            listaAlterna.Id, medioCc.Id, idTipoComprobanteTx, admin);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio, int? idListaPrecio = null)
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
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaPrecio ?? ctx.IdListaPrecio,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, int? idListaPrecio = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idListaPrecio ?? ctx.IdListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = 0m, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    /// <summary>Un Consumo real, vía checkout (design: el único camino de escritura de la etapa
    /// que produce un item snapshot honesto para re-precificar).</summary>
    private static async Task<ComprobanteEmitido> RealizarConsumoAsync(
        Contexto ctx, int idCliente, int idArticulo, decimal cantidad, decimal precio)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, cantidad * precio, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Seed crudo (sin checkout) para pruebas de volumen/presupuesto — mismo criterio que
    /// <c>CajaCierreAtomicidadYConcurrenciaTests.SembrarPagoAsync</c>: no hace falta el camino de
    /// escritura completo cuando lo que se mide es la forma de la consulta, no el negocio.</summary>
    private async Task SembrarConsumoCrudoAsync(Contexto ctx, int idCliente, int idArticulo, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial), Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin, IdCliente = idCliente, Subtotal = precio, DescuentoTotal = 0m, Total = precio,
            Estado = EstadoComprobante.Emitido, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = idArticulo,
            Descripcion = "item de prueba", IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = 1m, PrecioUnitario = precio, Descuento = 0m,
            Total = precio, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        db.MovimientosCuentaCorriente.Add(new MovimientoCuentaCorriente
        {
            IdTenant = ctx.IdTenant, IdCliente = idCliente, Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin, Tipo = TipoMovimientoCc.Consumo, IdComprobanteVenta = comprobante.Id,
            IdPagoComprobante = null, Importe = precio, SaldoResultante = 0m
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> PreviewAsync(Contexto ctx, int idCliente, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).GetAsync($"/api/clientes/{idCliente}/cuenta-corriente/reliquidacion");

    private static async Task<HttpResponseMessage> EjecutarAsync(Contexto ctx, int idCliente, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/reliquidacion", new SolicitudDeReliquidacion(ctx.IdPuntoVenta));

    private static async Task<ResultadoDeReliquidacion> LeerResultadoAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        return JsonSerializer.Deserialize<ResultadoDeReliquidacion>(cuerpo, OpcionesJson)!;
    }

    // ---- task 3.6: identidad de derivación (preview == commit) --------------------------------

    [Fact]
    public async Task ElPreviewInmediatamenteAntesDelCommitDaUnDeltaByteIdenticoAlMovimientoComiteado()
    {
        var ctx = await PrepararAsync(nameof(ElPreviewInmediatamenteAntesDelCommitDaUnDeltaByteIdenticoAlMovimientoComiteado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-derivacion", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente derivacion");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);

        // Sube el precio DESPUÉS de la venta — la reliquidación tiene algo que re-precificar.
        await SembrarArticuloConPrecioAsync(ctx, "articulo-derivacion-precio-nuevo", 1m); // no-op, fuerza refresco de contexto
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = ctx.IdListaPrecio, Monto = 150m,
                VigenteDesde = ahora, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            var vieja = await db.Precios.Where(p => p.IdArticulo == idArticulo && p.VigenteHasta == null && p.Monto == 100m).SingleAsync();
            vieja.VigenteHasta = ahora;
            await db.SaveChangesAsync();
        }

        var preview = await LeerResultadoAsync(await PreviewAsync(ctx, idCliente));
        var ejecucion = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        Assert.Equal(preview.Delta, ejecucion.Delta);
        Assert.Equal(50m, ejecucion.Delta);

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await dbFinal.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios).SingleAsync();
        Assert.Equal(ejecucion.Delta, movimiento.Importe);
    }

    // ---- task 3.7: atomicidad en los 3 statements de escritura reales (pasos 6/7/8) -----------

    private async Task RevocarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"REVOKE {privilegios} ON {tabla} FROM {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task RestaurarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"GRANT {privilegios} ON {tabla} TO {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<HttpResponseMessage> EjecutarConPrivilegioRevocadoAsync(
        Contexto ctx, int idCliente, string tabla, string privilegios)
    {
        await RevocarAsync(tabla, privilegios);
        try
        {
            return await EjecutarAsync(ctx, idCliente);
        }
        finally
        {
            await RestaurarAsync(tabla, privilegios);
        }
    }

    private async Task<(decimal Saldo, int Movimientos)> LeerEstadoAsync(Contexto ctx, int idCliente)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        var movimientos = await db.MovimientosCuentaCorriente
            .CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios);
        return (saldo, movimientos);
    }

    [Fact]
    public async Task UnaFallaAlEscribirElSaldoDelClienteNoPersisteNadaEnElLedger()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirElSaldoDelClienteNoPersisteNadaEnElLedger));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-fault-saldo", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente fault saldo");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var respuesta = await EjecutarConPrivilegioRevocadoAsync(ctx, idCliente, "clientes", "UPDATE");
        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        // El saldo queda tal como lo dejó el Consumo original (100) — la reliquidación nunca
        // llegó a sumarle su delta.
        var (saldo, movimientos) = await LeerEstadoAsync(ctx, idCliente);
        Assert.Equal(100m, saldo);
        Assert.Equal(0, movimientos);
    }

    [Fact]
    public async Task UnaFallaAlInsertarElMovimientoNoPersisteNadaYElSaldoQuedaSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlInsertarElMovimientoNoPersisteNadaYElSaldoQuedaSinCambios));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-fault-insert", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente fault insert");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var respuesta = await EjecutarConPrivilegioRevocadoAsync(ctx, idCliente, "movimientos_cuenta_corriente", "INSERT");
        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        // El UPDATE de saldo (paso 6) YA corrió cuando el INSERT del movimiento (paso 7) falla —
        // prueba que el rollback deshace también un statement anterior de la misma transacción.
        // El saldo tiene que quedar en 100 (el Consumo original), nunca en 150 (100 + el delta 50
        // que el UPDATE de saldo alcanzó a escribir antes del rollback).
        var (saldo, movimientos) = await LeerEstadoAsync(ctx, idCliente);
        Assert.Equal(100m, saldo);
        Assert.Equal(0, movimientos);
    }

    [Fact]
    public async Task UnaFallaAlMarcarLosConsumosNoPersisteNadaNiElMovimientoNiElSaldo()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlMarcarLosConsumosNoPersisteNadaNiElMovimientoNiElSaldo));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-fault-marcador", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente fault marcador");
        var consumo = await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var respuesta = await EjecutarConPrivilegioRevocadoAsync(ctx, idCliente, "movimientos_cuenta_corriente", "UPDATE");
        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        // El INSERT del movimiento ActualizacionPrecios (paso 7) YA corrió — prueba que el
        // rollback también lo deshace, no solo el UPDATE del marcador (paso 8) que tiró la
        // excepción. El saldo tiene que quedar en 100 (el Consumo original).
        var (saldo, movimientos) = await LeerEstadoAsync(ctx, idCliente);
        Assert.Equal(100m, saldo);
        Assert.Equal(0, movimientos);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var marcador = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == consumo.Id && m.Tipo == TipoMovimientoCc.Consumo)
            .Select(m => m.IdMovimientoActualizacion).SingleAsync();
        Assert.Null(marcador);
    }

    private async Task SubirPrecioAsync(Contexto ctx, int idArticulo, decimal nuevoPrecio, int? idListaPrecio = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var lista = idListaPrecio ?? ctx.IdListaPrecio;

        var vieja = await db.Precios
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == lista && p.VigenteHasta == null)
            .SingleAsync();
        vieja.VigenteHasta = ahora;

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = lista, Monto = nuevoPrecio,
            VigenteDesde = ahora, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- task 3.8: reliquidación × venta, dos reliquidaciones concurrentes --------------------

    [Fact]
    public async Task UnaReliquidacionQueCompiteConUnaVentaDelMismoClienteNoPierdeNiDuplicaNingunConsumo()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(UnaReliquidacionQueCompiteConUnaVentaDelMismoClienteNoPierdeNiDuplicaNingunConsumo)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, $"articulo-race-{ronda}", 100m);
            var idCliente = await SembrarClienteAsync(ctx, $"Cliente race {ronda}");
            await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
            await SubirPrecioAsync(ctx, idArticulo, 150m);

            var tareaReliquidacion = EjecutarAsync(ctx, idCliente);
            var tareaVenta = RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 150m);

            var respuestaReliquidacion = await tareaReliquidacion;
            await tareaVenta;

            Assert.True(respuestaReliquidacion.IsSuccessStatusCode);

            // Invariante robusto a la interleaving real (no depende de forzar el rendezvous, mismo
            // criterio que CajaCierreAtomicidadYConcurrenciaTests): Cliente.Saldo == Σ importe,
            // sin importar el orden real en que ambas transacciones se sirvieron.
            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
            var sumaMovimientos = await db.MovimientosCuentaCorriente.Where(m => m.IdCliente == idCliente).SumAsync(m => m.Importe);
            Assert.Equal(sumaMovimientos, saldo);

            // Los dos Consumo del cliente (el original + el de la venta que compitió) tienen que
            // seguir siendo exactamente dos filas — ninguna se pierde ni se duplica.
            Assert.Equal(2, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.Consumo));
        }
    }

    [Fact]
    public async Task DosReliquidacionesConcurrentesDelMismoClienteEscribenExactamenteUnMovimiento()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync($"{nameof(DosReliquidacionesConcurrentesDelMismoClienteEscribenExactamenteUnMovimiento)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, $"articulo-doble-{ronda}", 100m);
            var idCliente = await SembrarClienteAsync(ctx, $"Cliente doble {ronda}");
            await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
            await SubirPrecioAsync(ctx, idArticulo, 150m);

            var tareaA = EjecutarAsync(ctx, idCliente);
            var tareaB = EjecutarAsync(ctx, idCliente);

            var respuestas = await Task.WhenAll(tareaA, tareaB);

            // Las dos tienen que ser 200 — la que pierde la carrera re-escanea, encuentra el
            // conjunto vacío y devuelve un no-op limpio, NUNCA un 409 (design: "Exactamente one
            // movement, no 409 needed").
            Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            Assert.Equal(1, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));

            // 100 (Consumo original) + 50 (delta) = 150.
            var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
            Assert.Equal(150m, saldo);
        }
    }

    // ---- task 3.9: idempotencia, marcador, lista de precios ACTUAL -----------------------------

    [Fact]
    public async Task CorrerLaReliquidacionDosVecesEsUnNoOpLimpioLaSegundaVez()
    {
        var ctx = await PrepararAsync(nameof(CorrerLaReliquidacionDosVecesEsUnNoOpLimpioLaSegundaVez));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-idempotencia", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente idempotencia");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var primera = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));
        Assert.Equal(50m, primera.Delta);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            // 100 (Consumo original) + 50 (delta) = 150.
            Assert.Equal(150m, await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync());
        }

        var segunda = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));
        Assert.Equal(0m, segunda.Delta);
        Assert.Empty(segunda.IdsMovimientosCubiertos);

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(150m, await dbFinal.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync());
        Assert.Equal(1, await dbFinal.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));
    }

    [Fact]
    public async Task LaReprecificacionUsaLaListaDePreciosActualDelClienteNoLaDeLaVenta()
    {
        var ctx = await PrepararAsync(nameof(LaReprecificacionUsaLaListaDePreciosActualDelClienteNoLaDeLaVenta));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-lista-actual", 100m);
        // El mismo artículo también tiene precio en la lista alterna — la reliquidación tiene que
        // ignorar la lista de venta (ctx.IdListaPrecio) y usar la que el cliente tiene AHORA.
        await SembrarArticuloConPrecioAsync(ctx, "articulo-lista-actual-alterna", 999m, ctx.IdListaPrecioAlterna);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdListaPrecio = ctx.IdListaPrecioAlterna, Monto = 200m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var idCliente = await SembrarClienteAsync(ctx, "Cliente lista actual", ctx.IdListaPrecio);
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);

        // Mueve al cliente a la lista alterna DESPUÉS de la venta.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cliente = await db.Clientes.SingleAsync(c => c.Id == idCliente);
            cliente.IdListaPrecio = ctx.IdListaPrecioAlterna;
            await db.SaveChangesAsync();
        }

        var resultado = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        // 200 (lista alterna) − 100 (histórico) = 100 — nunca contra la lista original de venta.
        Assert.Equal(100m, resultado.Delta);
    }

    [Fact]
    public async Task UnConsumoYaReliquidadoSeExcluyeDeLaSiguienteCorrida()
    {
        var ctx = await PrepararAsync(nameof(UnConsumoYaReliquidadoSeExcluyeDeLaSiguienteCorrida));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-excluido", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente excluido");
        var consumoViejo = await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);

        var primera = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));
        Assert.Single(primera.IdsMovimientosCubiertos);

        // Un consumo NUEVO se agrega — la corrida siguiente cubre solo el nuevo, nunca reprocesa
        // el ya marcado (spec: A previously reliquidated consumo is skipped). El precio sube DE
        // NUEVO después de la compra para que el nuevo consumo aporte un delta distinto de cero —
        // si quedara en cero la corrida sería un no-op y no escribiría ningún marcador (mismo
        // criterio que UnDeltaTotalCeroPorDeltasQueSeCancelanNoDejaNingunConsumoMarcadoComoCubierto).
        await SubirPrecioAsync(ctx, idArticulo, 200m);
        var consumoNuevo = await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 200m);
        await SubirPrecioAsync(ctx, idArticulo, 250m);

        var segunda = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));
        Assert.Single(segunda.IdsMovimientosCubiertos);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var marcadorViejo = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == consumoViejo.Id && m.Tipo == TipoMovimientoCc.Consumo)
            .Select(m => m.IdMovimientoActualizacion).SingleAsync();
        Assert.NotNull(marcadorViejo);

        var marcadorNuevo = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == consumoNuevo.Id && m.Tipo == TipoMovimientoCc.Consumo)
            .Select(m => m.IdMovimientoActualizacion).SingleAsync();
        Assert.NotNull(marcadorNuevo);
    }

    // ---- task 3.10: un único movimiento por N comprobantes/líneas; sin reversión --------------

    [Fact]
    public async Task DosComprobantesEscribenExactamenteUnMovimientoConElDeltaSumado()
    {
        var ctx = await PrepararAsync(nameof(DosComprobantesEscribenExactamenteUnMovimientoConElDeltaSumado));
        var idArticuloA = await SembrarArticuloConPrecioAsync(ctx, "articulo-suma-a", 100m);
        var idArticuloB = await SembrarArticuloConPrecioAsync(ctx, "articulo-suma-b", 80m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente suma");

        await RealizarConsumoAsync(ctx, idCliente, idArticuloA, 1m, 100m);
        await RealizarConsumoAsync(ctx, idCliente, idArticuloB, 1m, 80m);
        await SubirPrecioAsync(ctx, idArticuloA, 130m);
        await SubirPrecioAsync(ctx, idArticuloB, 100m);

        var resultado = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        // (130-100) + (100-80) = 50.
        Assert.Equal(50m, resultado.Delta);
        Assert.Equal(2, resultado.IdsMovimientosCubiertos.Count);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));
    }

    [Fact]
    public async Task UnMovimientoDeActualizacionPreciosNoEsAnulableViaLaSuperficieDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnMovimientoDeActualizacionPreciosNoEsAnulableViaLaSuperficieDeVentas));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-no-anulable", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente no anulable");
        await RealizarConsumoAsync(ctx, idCliente, idArticulo, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticulo, 150m);
        await EjecutarAsync(ctx, idCliente);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idMovimiento = await db.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios)
            .Select(m => m.Id).SingleAsync();

        // No existe ruta que direccione un movimiento — la única superficie de anulación del
        // proyecto es /api/ventas/{id}/anulacion, direccionada por id de COMPROBANTE. Un
        // ActualizacionPrecios nunca tiene un comprobante propio, así que el id del movimiento no
        // resuelve a nada anulable (spec: No reversal endpoint exists for ActualizacionPrecios).
        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{idMovimiento}/anulacion", null);
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 3.11: presupuesto de consultas, constante independiente de N ---------------------

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

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Consultas++;
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Consultas++;
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
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

    private async Task<int> EjecutarYContarConsultasAsync(Contexto ctx, int idCliente)
    {
        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<Ways.Domain.Clientes.TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<Ways.Domain.Articulos.UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<Ways.Domain.Stock.MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: ctx.IdEmpleadoAdmin);
        var lector = new LectorDeConsumosReliquidables(db);
        var servicioDePrecios = new ServicioDePrecios(db, reloj, contexto);
        var servicioDeAuditoria = new Ways.Application.Auditoria.ServicioDeAuditoria(db, reloj, contexto);
        var servicio = new ServicioDeReliquidacion(db, reloj, contexto, lector, servicioDePrecios, servicioDeAuditoria);

        await servicio.EjecutarAsync(idCliente, new SolicitudDeReliquidacion(ctx.IdPuntoVenta));

        return contador.Consultas;
    }

    [Fact]
    public async Task LaReliquidacionEmiteUnPresupuestoConstanteDeConsultasIndependienteDeLaCantidadDeConsumos()
    {
        var ctx = await PrepararAsync(nameof(LaReliquidacionEmiteUnPresupuestoConstanteDeConsultasIndependienteDeLaCantidadDeConsumos));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-presupuesto", 10m);

        var idCliente2 = await SembrarClienteAsync(ctx, "Cliente presupuesto 2");
        for (var i = 0; i < 2; i++)
        {
            await SembrarConsumoCrudoAsync(ctx, idCliente2, idArticulo, 10m);
        }

        var idCliente50 = await SembrarClienteAsync(ctx, "Cliente presupuesto 50");
        for (var i = 0; i < 50; i++)
        {
            await SembrarConsumoCrudoAsync(ctx, idCliente50, idArticulo, 10m);
        }

        var idCliente200 = await SembrarClienteAsync(ctx, "Cliente presupuesto 200");
        for (var i = 0; i < 200; i++)
        {
            await SembrarConsumoCrudoAsync(ctx, idCliente200, idArticulo, 10m);
        }

        await SubirPrecioAsync(ctx, idArticulo, 15m);

        var consultasCon2 = await EjecutarYContarConsultasAsync(ctx, idCliente2);
        var consultasCon50 = await EjecutarYContarConsultasAsync(ctx, idCliente50);
        var consultasCon200 = await EjecutarYContarConsultasAsync(ctx, idCliente200);

        Assert.Equal(consultasCon2, consultasCon50);
        Assert.Equal(consultasCon2, consultasCon200);
    }

    // ---- task 3.12: matriz de autorización -----------------------------------------------------

    private async Task<HttpClient> CrearUsuarioConRolAsync(Contexto ctx, string nombre, RolConocido rol)
    {
        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(nombre, mail, (int)rol, PasswordUsuario));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDeLaReliquidacionPreviewYCommit()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDeLaReliquidacionPreviewYCommit));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente vendedor rechazado");
        using var vendedor = await CrearUsuarioConRolAsync(ctx, "vendedor-rq", RolConocido.Vendedor);

        var preview = await PreviewAsync(ctx, idCliente, vendedor);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);

        var commit = await EjecutarAsync(ctx, idCliente, vendedor);
        Assert.Equal(HttpStatusCode.Forbidden, commit.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorPuedeCorrerElPreviewYElCommit()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorPuedeCorrerElPreviewYElCommit));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente supervisor");
        using var supervisor = await CrearUsuarioConRolAsync(ctx, "supervisor-rq", RolConocido.Supervisor);

        var preview = await PreviewAsync(ctx, idCliente, supervisor);
        Assert.True(preview.IsSuccessStatusCode);

        var commit = await EjecutarAsync(ctx, idCliente, supervisor);
        Assert.True(commit.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UnAdminPuedeCorrerElPreviewYElCommit()
    {
        var ctx = await PrepararAsync(nameof(UnAdminPuedeCorrerElPreviewYElCommit));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente admin");

        var preview = await PreviewAsync(ctx, idCliente);
        Assert.True(preview.IsSuccessStatusCode);

        var commit = await EjecutarAsync(ctx, idCliente);
        Assert.True(commit.IsSuccessStatusCode);
    }

    [Fact]
    public async Task LaReliquidacionSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var preview = await cliente.GetAsync("/api/clientes/1/cuenta-corriente/reliquidacion");
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);

        var commit = await cliente.PostAsJsonAsync(
            "/api/clientes/1/cuenta-corriente/reliquidacion", new SolicitudDeReliquidacion(1));
        Assert.Equal(HttpStatusCode.Unauthorized, commit.StatusCode);
    }

    [Fact]
    public async Task LaReliquidacionContraUnClienteDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(LaReliquidacionContraUnClienteDeOtroTenantDevuelve404) + "-A");
        var idClienteDeA = await SembrarClienteAsync(ctxA, "Cliente A cross-tenant RQ");

        var ctxB = await PrepararAsync(nameof(LaReliquidacionContraUnClienteDeOtroTenantDevuelve404) + "-B");

        var preview = await PreviewAsync(ctxB, idClienteDeA);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);

        var commit = await EjecutarAsync(ctxB, idClienteDeA);
        Assert.Equal(HttpStatusCode.NotFound, commit.StatusCode);
    }

    // ---- no-op semantics -----------------------------------------------------------------------

    [Fact]
    public async Task UnClienteSinConsumosElegiblesEsUnNoOpLimpio()
    {
        var ctx = await PrepararAsync(nameof(UnClienteSinConsumosElegiblesEsUnNoOpLimpio));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente sin consumos");

        var resultado = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        Assert.Equal(0m, resultado.Delta);
        Assert.Empty(resultado.IdsMovimientosCubiertos);
        Assert.False(resultado.HayMas);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente));
    }

    [Fact]
    public async Task UnDeltaTotalCeroPorDeltasQueSeCancelanNoDejaNingunConsumoMarcadoComoCubierto()
    {
        // Dos consumos elegibles, uno sube (+50) y otro baja (-50) — el delta TOTAL de la corrida
        // da 0, así que el servicio no escribe absolutamente nada (paso 6/7/8 se saltean). La
        // respuesta tiene que reflejar esa realidad de la DB: IdsMovimientosCubiertos vacío, nunca
        // la lista de "procesados" que devuelve el calculador puro.
        var ctx = await PrepararAsync(nameof(UnDeltaTotalCeroPorDeltasQueSeCancelanNoDejaNingunConsumoMarcadoComoCubierto));
        var idArticuloSube = await SembrarArticuloConPrecioAsync(ctx, "articulo-cancela-sube", 100m);
        var idArticuloBaja = await SembrarArticuloConPrecioAsync(ctx, "articulo-cancela-baja", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente delta cancela");

        var consumoSube = await RealizarConsumoAsync(ctx, idCliente, idArticuloSube, 1m, 100m);
        var consumoBaja = await RealizarConsumoAsync(ctx, idCliente, idArticuloBaja, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticuloSube, 150m); // delta +50.
        await SubirPrecioAsync(ctx, idArticuloBaja, 50m); // delta -50.

        var respuesta = await EjecutarAsync(ctx, idCliente);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resultado = await LeerResultadoAsync(respuesta);

        Assert.Equal(0m, resultado.Delta);
        Assert.Empty(resultado.IdsMovimientosCubiertos);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync(
            m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));

        var marcadorSube = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == consumoSube.Id && m.Tipo == TipoMovimientoCc.Consumo)
            .Select(m => m.IdMovimientoActualizacion).SingleAsync();
        var marcadorBaja = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == consumoBaja.Id && m.Tipo == TipoMovimientoCc.Consumo)
            .Select(m => m.IdMovimientoActualizacion).SingleAsync();
        Assert.Null(marcadorSube);
        Assert.Null(marcadorBaja);
    }

    [Fact]
    public async Task UnPreviewConDeltaTotalCeroPorDeltasQueSeCancelanNoMuestraNingunConsumoCubierto()
    {
        // Mismo escenario de deltas que se cancelan (+50/-50) que
        // UnDeltaTotalCeroPorDeltasQueSeCancelanNoDejaNingunConsumoMarcadoComoCubierto, pero
        // consultado por GET: el preview tiene que anticipar la MISMA respuesta que el commit
        // (never two formulas) — nada de "cubiertos" para un delta que no va a escribir nada.
        var ctx = await PrepararAsync(nameof(UnPreviewConDeltaTotalCeroPorDeltasQueSeCancelanNoMuestraNingunConsumoCubierto));
        var idArticuloSube = await SembrarArticuloConPrecioAsync(ctx, "articulo-preview-cancela-sube", 100m);
        var idArticuloBaja = await SembrarArticuloConPrecioAsync(ctx, "articulo-preview-cancela-baja", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente preview delta cancela");

        await RealizarConsumoAsync(ctx, idCliente, idArticuloSube, 1m, 100m);
        await RealizarConsumoAsync(ctx, idCliente, idArticuloBaja, 1m, 100m);
        await SubirPrecioAsync(ctx, idArticuloSube, 150m); // delta +50.
        await SubirPrecioAsync(ctx, idArticuloBaja, 50m); // delta -50.

        var resultado = await LeerResultadoAsync(await PreviewAsync(ctx, idCliente));

        Assert.Equal(0m, resultado.Delta);
        Assert.Empty(resultado.IdsMovimientosCubiertos);
    }

    // ---- el cap de 500 consumos por corrida, punta a punta -------------------------------------

    /// <summary>Seed crudo en lote (<c>AddRange</c> + un único <c>SaveChangesAsync</c> por tabla,
    /// en vez de N×3 round-trips) — mismo criterio que <see cref="SembrarConsumoCrudoAsync"/>: el
    /// camino de escritura completo no hace falta para probar la FORMA del cap de 500, solo el
    /// volumen de filas.</summary>
    private async Task<List<int>> SembrarConsumosCrudosEnLoteAsync(
        Contexto ctx, int idCliente, int idArticulo, decimal precio, int cantidad, DateTimeOffset fecha)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var comprobantes = Enumerable.Range(0, cantidad)
            .Select(_ => new ComprobanteVenta
            {
                IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoComprobanteTx,
                Numero = Interlocked.Increment(ref _numeroSecuencial), Fecha = fecha, IdPuntoVenta = ctx.IdPuntoVenta,
                IdEmpleado = ctx.IdEmpleadoAdmin, IdCliente = idCliente, Subtotal = precio, DescuentoTotal = 0m,
                Total = precio, Estado = EstadoComprobante.Emitido, CreatedAt = fecha, UpdatedAt = fecha
            })
            .ToList();
        db.ComprobantesVenta.AddRange(comprobantes);
        await db.SaveChangesAsync();

        var items = comprobantes
            .Select(c => new ItemComprobanteVenta
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = c.Id, Orden = 1, IdArticulo = idArticulo,
                Descripcion = "item de prueba", IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
                IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = 1m, PrecioUnitario = precio,
                Descuento = 0m, Total = precio, CreatedAt = fecha, UpdatedAt = fecha
            })
            .ToList();
        db.ItemsComprobanteVenta.AddRange(items);
        await db.SaveChangesAsync();

        var movimientos = comprobantes
            .Select(c => new MovimientoCuentaCorriente
            {
                IdTenant = ctx.IdTenant, IdCliente = idCliente, Fecha = fecha, IdPuntoVenta = ctx.IdPuntoVenta,
                IdEmpleado = ctx.IdEmpleadoAdmin, Tipo = TipoMovimientoCc.Consumo, IdComprobanteVenta = c.Id,
                IdPagoComprobante = null, Importe = precio, SaldoResultante = 0m
            })
            .ToList();
        db.MovimientosCuentaCorriente.AddRange(movimientos);
        await db.SaveChangesAsync();

        return movimientos.OrderBy(m => m.Id).Select(m => m.Id).ToList();
    }

    [Fact]
    public async Task ConQuinientosUnoElegiblesLaPrimeraCorridaCubreLosQuinientosMasViejosYLaSegundaElResto()
    {
        var ctx = await PrepararAsync(nameof(ConQuinientosUnoElegiblesLaPrimeraCorridaCubreLosQuinientosMasViejosYLaSegundaElResto));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-cap-500", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente cap 500");

        // Misma fecha para las 501 filas — el desempate determinístico por id (Fix del lector) es
        // lo que decide cuáles 500 quedan del lado de la primera corrida.
        var fecha = DateTimeOffset.UtcNow;
        var idsMovimiento = await SembrarConsumosCrudosEnLoteAsync(ctx, idCliente, idArticulo, 100m, 501, fecha);
        Assert.Equal(501, idsMovimiento.Count);

        await SubirPrecioAsync(ctx, idArticulo, 110m); // delta +10 por consumo.

        var primera = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        Assert.Equal(500, primera.IdsMovimientosCubiertos.Count);
        Assert.True(primera.HayMas);
        Assert.Equal(5000m, primera.Delta); // 500 × 10.

        var idsEsperadosPrimeraCorrida = idsMovimiento.Take(500).ToList();
        Assert.Equal(idsEsperadosPrimeraCorrida, primera.IdsMovimientosCubiertos.OrderBy(id => id).ToList());

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(1, await db.MovimientosCuentaCorriente.CountAsync(
                m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));

            var idUltimoNoCubierto = idsMovimiento[500];
            var marcadorPendiente = await db.MovimientosCuentaCorriente
                .Where(m => m.Id == idUltimoNoCubierto).Select(m => m.IdMovimientoActualizacion).SingleAsync();
            Assert.Null(marcadorPendiente);
        }

        var segunda = await LeerResultadoAsync(await EjecutarAsync(ctx, idCliente));

        Assert.Single(segunda.IdsMovimientosCubiertos);
        Assert.Equal(idsMovimiento[500], segunda.IdsMovimientosCubiertos[0]);
        Assert.False(segunda.HayMas);
        Assert.Equal(10m, segunda.Delta);

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(2, await dbFinal.MovimientosCuentaCorriente.CountAsync(
            m => m.IdCliente == idCliente && m.Tipo == TipoMovimientoCc.ActualizacionPrecios));

        var saldoFinal = await dbFinal.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        // El seed crudo (SembrarConsumosCrudosEnLoteAsync) inserta los 501 Consumo directo en el
        // ledger sin pasar por el checkout, así que nunca toca Cliente.Saldo (mismo criterio que
        // SembrarConsumoCrudoAsync) — el único efecto en saldo es el de las dos corridas de
        // reliquidación (5000 + 10).
        Assert.Equal(primera.Delta + segunda.Delta, saldoFinal);
    }
}
