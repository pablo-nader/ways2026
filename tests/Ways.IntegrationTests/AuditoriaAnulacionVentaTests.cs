using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
using Ways.Domain.Stock;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 3: <c>venta.anulacion</c> — el caso insignia
/// (100%-servicio sin cuenta corriente, tasks 3.7/3.9), la cobertura sobre un comprobante con
/// consumo de cuenta corriente (task 3.8) y el mutation target de <c>id_punto_venta</c> (task 3.4,
/// <c>RETURNING</c> vs pre-read). Archivo nuevo, dedicado a auditoría — mismo criterio que
/// <c>AuditoriaEscrituraTests.cs</c> de Slice 1 — para no tocar <c>AnulacionTests.cs</c> ni,
/// sobre todo, <c>VentasCheckoutTests.cs</c> (binding verify criterion de esta etapa: ausente del
/// diff, constante 16 intacta — esta slice no abre <c>EmitirAsync</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AuditoriaAnulacionVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdMedioCuentaCorriente, int IdCliente, int IdTipoComprobanteTx,
        int IdEmpleadoAdmin, string MailAdmin, string PasswordAdmin);

    private long _numeroSecuencial = 500_000;

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
            IdTenant = resultado.IdTenant, Nombre = "Auditoria-anulacion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Auditoria Anulacion", EsDefault = false, Modo = ModoLista.Fija,
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

        // stage-6-turnos-caja: el checkout exige un turno abierto (409 turno_no_abierto) — mismo
        // criterio que AnulacionTests.PrepararAsync, sembrado directo por EF.
        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente auditoria anulacion",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 1000m,
            CreditoIlimitado = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        var idCliente = cliente.Id;

        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, medioCc.Id, idCliente, idTipoComprobanteTx, resultado.IdUsuarioAdmin,
            mailAdmin, resultado.PasswordTemporal);
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

    /// <summary>Siembra DIRECTO (EF) un comprobante <c>emitido</c> 100%-servicio, con
    /// <c>id_turno_caja NULL</c> ("Stage-5 NULL-turno comprobante stays anulable"): el checkout
    /// HTTP no puede construir una línea de concepto libre —
    /// <c>LineaDeVenta.IdArticulo</c> es <c>int</c> no-nullable (Ventas/Contratos.cs) — así que la
    /// única forma honesta de sembrar el caso insignia (spec: "TX comprobante composed entirely of
    /// service lines, id_articulo NULL on every item") es directo por EF, mismo criterio que
    /// <c>CajaCierreEndpointsTests.SembrarPagoAsync</c>.</summary>
    private async Task<int> SembrarComprobanteDeServicioAsync(Contexto ctx, decimal total = 500m)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial), Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = null, IdEmpleado = ctx.IdEmpleadoAdmin, IdCliente = ctx.IdCliente, Subtotal = total,
            DescuentoTotal = 0m, Total = total, Estado = EstadoComprobante.Emitido, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = null,
            Descripcion = "Servicio de prueba (concepto libre)", IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = 1m, PrecioUnitario = total, Descuento = 0m,
            Total = total, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    private static async Task<ComprobanteEmitido> EmitirConCcAsync(Contexto ctx, int idArticulo, decimal precio)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, precio, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return (await JsonSerializer.DeserializeAsync<ComprobanteEmitido>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cuerpo)), OpcionesJson))!;
    }

    // ---- task 3.7 (flagship) + task 3.9 sobre el caso insignia (mutation target, slice 3 row 2) --

    /// <summary>Spec `comprobantes-venta`/`auditoria-de-operaciones`, "A 100%-servicio comprobante
    /// without cuenta corriente is attributable on anulación" — el caso insignia: antes de esta
    /// etapa, el único rastro era <c>updated_at</c> sin actor.</summary>
    [Fact]
    public async Task AnulacionDeUnComprobante100PorCientoServicioSinCcEsAtribuible()
    {
        var ctx = await PrepararAsync(nameof(AnulacionDeUnComprobante100PorCientoServicioSinCcEsAtribuible));
        var idComprobante = await SembrarComprobanteDeServicioAsync(ctx);

        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{idComprobante}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "venta.anulacion" && a.IdEntidad == idComprobante);
        Assert.Equal("comprobante_venta", fila.Entidad);
        // Judgment Day fix (slice 3 juez B ronda 1, finding 2): NotEqual(0, ...) no discrimina el
        // actor — un mutante que estampa un actor constante (p. ej. 1) lo pasa igual. Igualdad
        // exacta contra el admin real cierra ese hueco.
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdActor);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);

        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteVenta == idComprobante));
        Assert.Equal(
            0, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdComprobanteVenta == idComprobante));
    }

    /// <summary>Mutation target (slice 3, row 2): <c>RegistrarAsync</c> movido DESPUÉS de
    /// <c>CommitAsync</c> en la transacción de anulación — este test DEBE fallar bajo esa mutación.
    /// design decisión 10 / testing strategy, "el test insignia" (b): el 100%-servicio sin CC es el
    /// ÚNICO caso donde el INSERT de auditoría es el ÚNICO statement de la transacción que toca
    /// <c>usuarios</c> (vía <c>fk_auditoria_actor</c>) — revocar <c>INSERT</c> sobre
    /// <c>auditoria</c> aísla la falla sin ambigüedad, mismo técnica de <c>REVOKE</c> que
    /// <c>AnulacionTests.UnaFallaAlRevertirElStockDejaElComprobanteEmitidoYNadaCambiado</c>.</summary>
    [Fact]
    public async Task UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDelComprobante100PorCientoServicio()
    {
        var ctx = await PrepararAsync(
            nameof(UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDelComprobante100PorCientoServicio));
        var idComprobante = await SembrarComprobanteDeServicioAsync(ctx);

        await RevocarAsync("auditoria", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/ventas/{idComprobante}/anulacion", null);
        }
        finally
        {
            await RestaurarAsync("auditoria", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var estado = await db.ComprobantesVenta.Where(c => c.Id == idComprobante).Select(c => c.Estado).FirstAsync();
        Assert.Equal(EstadoComprobante.Emitido, estado);
        Assert.Equal(
            0, await db.Auditoria.CountAsync(a => a.IdEntidad == idComprobante && a.Accion == "venta.anulacion"));

        // Reintento limpio inmediatamente después tiene que funcionar (mismo criterio que
        // AnulacionTests: la anulación no consume ningún recurso de un solo uso).
        var reintento = await ctx.Admin.PostAsync($"/api/ventas/{idComprobante}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
    }

    /// <summary>Judgment Day fix (slice 3 juez B ronda 1, finding 1, WARNING): el test de arriba
    /// corre sobre el comprobante 100%-servicio, donde el THEN del spec ("no inverse
    /// movimientos_stock/movimientos_cuenta_corriente row exists") es VACUAMENTE verdadero — esa
    /// composición no produce reversas bajo ninguna implementación, ni siquiera una que ignorara el
    /// fail-closed. El GIVEN literal del spec `comprobantes-venta` para este escenario es "3 líneas
    /// de producto y un consumo de cuenta corriente": este test lo cubre con magnitudes distintas
    /// por línea (2/3/1 unidades, $50/$100/$150) para que el CERO de cada aserción sea real — si
    /// el fail-closed fallara, habría 3 filas de <c>movimientos_stock</c> y 1 contramovimiento de
    /// CC que este test SÍ detectaría. Mismo técnica <c>REVOKE INSERT ON auditoria</c> que el test
    /// insignia de arriba (que se mantiene intacto: sigue siendo el flagship de la task 3.7).
    /// </summary>
    [Fact]
    public async Task UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDeUnComprobanteConTresLineasDeProductoYConsumoDeCc()
    {
        var ctx = await PrepararAsync(
            nameof(UnaFallaAlEscribirLaAuditoriaBloqueaLaAnulacionDeUnComprobanteConTresLineasDeProductoYConsumoDeCc));

        var idArticuloA = await SembrarArticuloConPrecioAsync(ctx, "articulo-fail-closed-a", 50m);
        var idArticuloB = await SembrarArticuloConPrecioAsync(ctx, "articulo-fail-closed-b", 100m);
        var idArticuloC = await SembrarArticuloConPrecioAsync(ctx, "articulo-fail-closed-c", 150m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [
                new LineaDeVenta(idArticuloA, 2m, null),
                new LineaDeVenta(idArticuloB, 3m, null),
                new LineaDeVenta(idArticuloC, 1m, null)
            ],
            // 2*50 + 3*100 + 1*150 = 550 — bajo el LimiteCredito (1000m) de PrepararAsync.
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 550m, null, 0m)],
            null, null);

        var emisionRespuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var emisionCuerpo = await emisionRespuesta.Content.ReadAsStringAsync();
        Assert.True(emisionRespuesta.StatusCode == HttpStatusCode.Created, emisionCuerpo);
        var emitido = (await JsonSerializer.DeserializeAsync<ComprobanteEmitido>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(emisionCuerpo)), OpcionesJson))!;

        await using var dbAntes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stockAAntes = await dbAntes.Stock
            .Where(s => s.IdArticulo == idArticuloA && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        var stockBAntes = await dbAntes.Stock
            .Where(s => s.IdArticulo == idArticuloB && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        var stockCAntes = await dbAntes.Stock
            .Where(s => s.IdArticulo == idArticuloC && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();

        await RevocarAsync("auditoria", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        }
        finally
        {
            await RestaurarAsync("auditoria", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var estado = await db.ComprobantesVenta.Where(c => c.Id == emitido.Id).Select(c => c.Estado).FirstAsync();
        Assert.Equal(EstadoComprobante.Emitido, estado);

        Assert.Equal(
            0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion));
        Assert.Equal(
            0, await db.MovimientosCuentaCorriente.CountAsync(
                m => m.IdComprobanteVenta == emitido.Id && m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCc.Ajuste));
        Assert.Equal(
            0, await db.Auditoria.CountAsync(a => a.IdEntidad == emitido.Id && a.Accion == "venta.anulacion"));

        var stockADespues = await db.Stock
            .Where(s => s.IdArticulo == idArticuloA && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        var stockBDespues = await db.Stock
            .Where(s => s.IdArticulo == idArticuloB && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        var stockCDespues = await db.Stock
            .Where(s => s.IdArticulo == idArticuloC && s.IdPuntoVenta == ctx.IdPuntoVenta).Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(stockAAntes, stockADespues);
        Assert.Equal(stockBAntes, stockBDespues);
        Assert.Equal(stockCAntes, stockCDespues);

        // Reintento limpio inmediatamente después tiene que funcionar.
        var reintento = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
    }

    // ---- task 3.8: cobertura sobre un comprobante con consumo de cuenta corriente ------------------

    /// <summary>Cobertura de <c>venta.anulacion</c> sobre un comprobante NO degenerado (con consumo
    /// de cuenta corriente) — payload key por key, discriminando el mutation target de la fila 3
    /// (<c>EstadoComprobante.Emitido</c> reemplazado por un literal <c>"anulado"</c> haría que
    /// <c>valor_anterior.estado</c> leyera <c>"anulado"</c> en vez de <c>"emitido"</c>). El
    /// checkout HTTP no puede construir una línea de concepto libre (ver el doc-comment de
    /// <see cref="SembrarComprobanteDeServicioAsync"/>), así que la composición "mixta" de la spec
    /// se cubre con el caso insignia (100%-servicio, arriba) — acá el eje es la generalidad del
    /// payload sobre un comprobante CON reversas que sí escriben filas.</summary>
    [Fact]
    public async Task VentaAnulacionCoberturaSobreUnComprobanteConConsumoDeCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(VentaAnulacionCoberturaSobreUnComprobanteConConsumoDeCuentaCorriente));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-auditoria-anulacion", 300m);
        var emitido = await EmitirConCcAsync(ctx, idArticulo, 300m);

        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.Auditoria.Where(a => a.Accion == "venta.anulacion" && a.IdEntidad == emitido.Id).ToListAsync();
        var fila = Assert.Single(filas);

        Assert.Equal("comprobante_venta", fila.Entidad);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);

        using var valorAnterior = JsonDocument.Parse(fila.ValorAnterior!);
        using var valorNuevo = JsonDocument.Parse(fila.ValorNuevo);
        Assert.Equal("emitido", valorAnterior.RootElement.GetProperty("estado").GetString());
        Assert.Equal("anulado", valorNuevo.RootElement.GetProperty("estado").GetString());

        // La reversa real sigue existiendo — la cobertura de auditoría no reemplaza al ledger.
        Assert.Equal(
            1, await db.MovimientosCuentaCorriente.CountAsync(
                m => m.IdComprobanteVenta == emitido.Id && m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCc.Ajuste));
    }

    // ---- task 3.4: mutation target — RETURNING id_punto_venta vs pre-read -------------------------

    /// <summary>Mutation target (slice 3, row 1): <c>MarcarAnuladoAsync</c> revertido a
    /// <c>RETURNING id_comprobante_venta</c> + leer el PV desde <c>comprobantePreLectura</c> (el
    /// pre-read <c>AsNoTracking()</c>, tomado ANTES del UPDATE atómico) — este test DEBE fallar
    /// bajo esa mutación. En ejecución normal (sin carrera) el pre-read y el <c>RETURNING</c>
    /// SIEMPRE coinciden (el PV es inmutable tras la emisión), así que un test sin carrera no
    /// discrimina nada (mismo confound de <c>mutation-proof-tests</c> regla 3 que 1.18). Este test
    /// fuerza el desacople: pausa <c>EjecutarAnulacionAsync</c> justo DESPUÉS de que el pre-read leyó
    /// el PV original y ANTES de que el UPDATE atómico corra, y desde una conexión ajena (owner,
    /// fuera de la transacción) reasigna <c>id_punto_venta</c> a un segundo PV del mismo tenant —
    /// un "fix" de datos fuera de banda, el único actor legítimo que puede tocar esa columna hoy.
    /// El UPDATE atómico, al retomar el lock, ve y devuelve el PV YA reasignado — si el código
    /// leyera el PV del pre-read en cambio, la fila de auditoría llevaría el PV viejo.</summary>
    [Fact]
    public async Task LaAuditoriaDeAnulacionLlevaElPuntoDeVentaQueElUpdateAtomicoRealmenteVioNoElDelPreRead()
    {
        var ctx = await PrepararAsync(
            nameof(LaAuditoriaDeAnulacionLlevaElPuntoDeVentaQueElUpdateAtomicoRealmenteVioNoElDelPreRead));
        var idComprobante = await SembrarComprobanteDeServicioAsync(ctx);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var puntoVentaB = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "PV reasignado (race)",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVentaB);
        await db.SaveChangesAsync();
        var idPuntoVentaB = puntoVentaB.Id;

        var preLecturaLista = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasElPreLecturaDeAnulacion(preLecturaLista, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaAnulacion = cliente.PostAsync($"/api/ventas/{idComprobante}/anulacion", null);

        await preLecturaLista.Task;

        // Reasignación fuera de banda, DESDE OTRA conexión, DESPUÉS de que el pre-read ya leyó el
        // PV original — el único actor que hoy toca esta columna fuera de la emisión.
        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var comando = owner.CreateCommand();
            comando.CommandText = "UPDATE comprobantes_venta SET id_punto_venta = $1 WHERE id_comprobante_venta = $2";
            comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVentaB });
            comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
            await comando.ExecuteNonQueryAsync();
        }

        puedeContinuar.TrySetResult();

        var respuesta = await tareaAnulacion;
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var dbDespues = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idPuntoVentaEnAuditoria = await dbDespues.Auditoria
            .Where(a => a.Accion == "venta.anulacion" && a.IdEntidad == idComprobante)
            .Select(a => a.IdPuntoVenta)
            .SingleAsync();

        Assert.Equal(idPuntoVentaB, idPuntoVentaEnAuditoria);
        Assert.NotEqual(ctx.IdPuntoVenta, idPuntoVentaEnAuditoria);
    }

    /// <summary>Pausa la query LINQ del pre-read (<c>db.ComprobantesVenta.AsNoTracking()...</c>)
    /// justo DESPUÉS de que ejecutó — el UPDATE atómico crudo de <c>MarcarAnuladoAsync</c> corre
    /// sobre <c>DbConnection.CreateCommand()</c> directo, fuera del pipeline de interceptores de
    /// EF, así que nunca dispara este hook.</summary>
    private sealed class InterceptorDePausaTrasElPreLecturaDeAnulacion(
        TaskCompletionSource preLecturaLista, TaskCompletionSource puedeContinuar) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("comprobantes_venta", StringComparison.OrdinalIgnoreCase))
            {
                preLecturaLista.TrySetResult();
                await puedeContinuar.Task;
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // ---- REVOKE/RESTORE (mismo patrón que AnulacionTests/ComprasAnulacionYConcurrenciaTests) ------

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
}
