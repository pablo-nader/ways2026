using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 4 (task 4.8, "force a failure at each of the six statements"; tasks
/// 4.9-4.11, las tres superficies racy de esta slice). Las fallas de la mitad transaccional se
/// fuerzan REVOCANDO el privilegio de Postgres de la tabla puntual (con <c>ways_owner</c>, nunca
/// con <c>ways_app</c>) durante el único checkout de la prueba — funciona igual para los pasos
/// escritos vía EF (comprobante/items/pagos, <c>SaveChangesAsync</c>) y los escritos con ADO.NET
/// crudo (numeración/stock/cuenta corriente), a diferencia de un <c>DbCommandInterceptor</c> de
/// EF Core, que NUNCA ve un <c>DbCommand</c> creado a mano sobre
/// <c>db.Database.GetDbConnection()</c>.
///
/// Gap semantics (honesto, no el resumen simplificado de design.md): un ROLLBACK LIMPIO —
/// exactamente lo que fuerza esta prueba— deshace TAMBIÉN el <c>UPDATE numeraciones_comprobante</c>
/// del paso 1 (misma transacción), así que el próximo checkout exitoso en el mismo punto de
/// venta/tipo REUSA el número — no lo saltea. Confirmado por
/// <c>AsignadorDeNumeroComprobanteConcurrenciaTests.UnaAsignacionConRollbackAntesDeComitearReusaElNumeroEnVezDeDejarUnHueco</c>
/// (Slice 2). El "hueco aceptado" de la spec nace únicamente de un reintento de
/// <c>CreateExecutionStrategy</c> ante un fallo transitorio de commit ambiguo — un mecanismo
/// distinto, no reproducible con una revocación de privilegio determinística, y por eso NO es lo
/// que estas pruebas afirman.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasAtomicidadYConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string RolApp = "ways_app";

    // Mismo motivo que VentasCheckoutTests.OpcionesJson/ArticulosEndpointsTests.OpcionesJson.
    private static readonly System.Text.Json.JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdMedioCuentaCorriente);

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
            IdTenant = resultado.IdTenant, Nombre = "Ventas-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista de Prueba", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, medioCc.Id);
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

    private async Task<int> SembrarClienteAsync(
        Contexto ctx, string nombre, decimal limiteCredito = 0, bool creditoIlimitado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = limiteCredito,
            CreditoIlimitado = creditoIlimitado, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    /// <summary>Semilla directa por SQL crudo — nunca vía <c>ServicioDeVentas</c> (esta slice no
    /// tiene un endpoint de ajuste todavía, eso es Slice 5). Inserta TAMBIÉN el
    /// <c>movimientos_stock</c> correspondiente (<c>motivo = ajuste</c>) para no romper el
    /// invariante <c>stock.cantidad = Σ movimientos_stock.cantidad</c> que las pruebas de
    /// concurrencia verifican (spec: stock / Cantidad Is Always The Sum Of Its Movimientos) —
    /// si solo se sembrara la fila de <c>stock</c>, la suma de movimientos quedaría corta por
    /// esta cantidad inicial.</summary>
    private async Task SembrarStockInicialAsync(Contexto ctx, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;

        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = cantidad
        });
        db.MovimientosStock.Add(new Ways.Domain.Stock.MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = cantidad,
            Motivo = Ways.Domain.Stock.MotivoStock.Ajuste, IdEmpleado = idEmpleado, CreadoEl = ahora
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta SolicitudSimple(
        Contexto ctx, int idCliente, int idArticulo, decimal precio, decimal cantidad = 1m, int? idMedio = null) =>
        new(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [new PagoDeVenta(idMedio ?? ctx.IdMedioEfectivo, precio * cantidad, null, 0m)],
            null, null);

    // ---- privilegios (fault injection determinística, ver el doc-comment de la clase) --------

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

    private async Task<HttpResponseMessage> IntentarConPrivilegioRevocadoAsync(
        Contexto ctx, SolicitudDeVenta solicitud, string tabla, string privilegios)
    {
        await RevocarAsync(tabla, privilegios);
        try
        {
            return await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        }
        finally
        {
            await RestaurarAsync(tabla, privilegios);
        }
    }

    private async Task VerificarNadaPersistidoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(0, await db.ItemsComprobanteVenta.CountAsync());
        Assert.Equal(0, await db.PagosComprobante.CountAsync());
        Assert.Equal(0, await db.MovimientosStock.CountAsync());
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync());
    }

    /// <summary>Prueba central del "gap honesto": el próximo checkout LIMPIO en el mismo punto
    /// de venta/tipo recibe <c>numero = 1</c> — si el intento fallido hubiera dejado un hueco,
    /// este sería <c>2</c>.</summary>
    private static async Task VerificarElProximoNumeroEsUnoAsync(Contexto ctx, int idCliente, int idArticulo, decimal precio)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudSimple(ctx, idCliente, idArticulo, precio));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(1L, emitido.Numero);
    }

    // ---- task 4.8: atomicidad, un punto de falla por prueba -----------------------------------

    [Fact]
    public async Task UnaFallaEnLaNumeracionNoPersisteNadaYElProximoIntentoReusaElNumero()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnLaNumeracionNoPersisteNadaYElProximoIntentoReusaElNumero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-1", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 1");

        var respuesta = await IntentarConPrivilegioRevocadoAsync(
            ctx, SolicitudSimple(ctx, idCliente, idArticulo, 100m), "numeraciones_comprobante", "INSERT, UPDATE");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);
        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    [Fact]
    public async Task UnaFallaEnElComprobanteNoPersisteNadaYElProximoIntentoReusaElNumero()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElComprobanteNoPersisteNadaYElProximoIntentoReusaElNumero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-2", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 2");

        var respuesta = await IntentarConPrivilegioRevocadoAsync(
            ctx, SolicitudSimple(ctx, idCliente, idArticulo, 100m), "comprobantes_venta", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);
        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    [Fact]
    public async Task UnaFallaEnLosItemsNoPersisteNadaYElProximoIntentoReusaElNumero()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnLosItemsNoPersisteNadaYElProximoIntentoReusaElNumero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-3", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 3");

        var respuesta = await IntentarConPrivilegioRevocadoAsync(
            ctx, SolicitudSimple(ctx, idCliente, idArticulo, 100m), "items_comprobante_venta", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);
        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    [Fact]
    public async Task UnaFallaEnLosPagosNoPersisteNadaYElProximoIntentoReusaElNumero()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnLosPagosNoPersisteNadaYElProximoIntentoReusaElNumero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-4", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 4");

        var respuesta = await IntentarConPrivilegioRevocadoAsync(
            ctx, SolicitudSimple(ctx, idCliente, idArticulo, 100m), "pagos_comprobante", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);
        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    [Fact]
    public async Task UnaFallaEnElStockNoPersisteNadaYElProximoIntentoReusaElNumero()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnElStockNoPersisteNadaYElProximoIntentoReusaElNumero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-5", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 5");

        var respuesta = await IntentarConPrivilegioRevocadoAsync(
            ctx, SolicitudSimple(ctx, idCliente, idArticulo, 100m), "movimientos_stock", "INSERT");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(0, await db.Stock.CountAsync());
        }

        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    [Fact]
    public async Task UnaFallaEnCuentaCorrienteNoPersisteNadaYElSaldoQuedaSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaEnCuentaCorrienteNoPersisteNadaYElSaldoQuedaSinCambios));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-atom-6", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad 6", limiteCredito: 1000m);

        var solicitud = SolicitudSimple(ctx, idCliente, idArticulo, 100m, idMedio: ctx.IdMedioCuentaCorriente);

        var respuesta = await IntentarConPrivilegioRevocadoAsync(ctx, solicitud, "clientes", "UPDATE");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        await VerificarNadaPersistidoAsync(ctx);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
            Assert.Equal(0m, saldo);
        }

        await VerificarElProximoNumeroEsUnoAsync(ctx, idCliente, idArticulo, 100m);
    }

    // ---- task 4.9: dos ventas concurrentes del mismo artículo ---------------------------------

    [Fact]
    public async Task DosVentasConcurrentesDelMismoArticuloNoCorrompenElCacheDeStock()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(DosVentasConcurrentesDelMismoArticuloNoCorrompenElCacheDeStock)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-concurrencia-stock", 10m);
            var idClienteA = await SembrarClienteAsync(ctx, "Concurrencia Stock A");
            var idClienteB = await SembrarClienteAsync(ctx, "Concurrencia Stock B");
            await SembrarStockInicialAsync(ctx, idArticulo, 10m);

            var tareaA = ctx.Admin.PostAsJsonAsync(
                "/api/ventas", SolicitudSimple(ctx, idClienteA, idArticulo, 10m, cantidad: 3m));
            var tareaB = ctx.Admin.PostAsJsonAsync(
                "/api/ventas", SolicitudSimple(ctx, idClienteB, idArticulo, 10m, cantidad: 3m));

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var cantidad = await db.Stock
                .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
                .Select(s => s.Cantidad).FirstAsync();
            var sumaDeMovimientos = await db.MovimientosStock
                .Where(m => m.IdArticulo == idArticulo && m.IdPuntoVenta == ctx.IdPuntoVenta)
                .SumAsync(m => m.Cantidad);

            Assert.Equal(4m, cantidad);
            Assert.Equal(cantidad, sumaDeMovimientos);
        }
    }

    // ---- task 4.10: dos ventas de cuenta corriente cerca del límite ---------------------------

    [Fact]
    public async Task DosVentasConcurrentesDeCuentaCorrienteNuncaSuperanElLimite()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(DosVentasConcurrentesDeCuentaCorrienteNuncaSuperanElLimite)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "art-concurrencia-cc", 600m);
            var idCliente = await SembrarClienteAsync(ctx, "Concurrencia CC", limiteCredito: 1000m);

            var solicitud = SolicitudSimple(ctx, idCliente, idArticulo, 600m, idMedio: ctx.IdMedioCuentaCorriente);

            var tareaA = ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
            var tareaB = ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            var estados = respuestas.Select(r => r.StatusCode).ToList();

            Assert.Contains(HttpStatusCode.Created, estados);
            Assert.Contains(HttpStatusCode.BadRequest, estados);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
            var sumaDeMovimientos = await db.MovimientosCuentaCorriente
                .Where(m => m.IdCliente == idCliente).SumAsync(m => m.Importe);

            Assert.Equal(600m, saldo);
            Assert.Equal(saldo, sumaDeMovimientos);
            Assert.True(saldo <= 1000m, $"El saldo ({saldo}) no puede superar el límite de crédito (1000).");
        }
    }

    // ---- task 4.11: dos ventas concurrentes del mismo punto de venta ---------------------------

    [Fact]
    public async Task DosVentasConcurrentesDelMismoPuntoDeVentaGetNumerosDistintosYConsecutivos()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(DosVentasConcurrentesDelMismoPuntoDeVentaGetNumerosDistintosYConsecutivos)}-{ronda}");
            var idArticuloA = await SembrarArticuloConPrecioAsync(ctx, "art-concurrencia-numero-a", 10m);
            var idArticuloB = await SembrarArticuloConPrecioAsync(ctx, "art-concurrencia-numero-b", 10m);
            var idClienteA = await SembrarClienteAsync(ctx, "Concurrencia Numero A");
            var idClienteB = await SembrarClienteAsync(ctx, "Concurrencia Numero B");

            var tareaA = ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudSimple(ctx, idClienteA, idArticuloA, 10m));
            var tareaB = ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudSimple(ctx, idClienteB, idArticuloB, 10m));

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

            var numeros = new List<long>();
            foreach (var r in respuestas)
            {
                var emitido = (await r.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
                numeros.Add(emitido.Numero);
            }

            Assert.NotEqual(numeros[0], numeros[1]);
            Assert.Equal([1L, 2L], numeros.OrderBy(n => n));
        }
    }
}
