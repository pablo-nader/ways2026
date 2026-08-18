using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 4 (task 4.5, tasks.md decisión 4 / `state.yaml` OD7): la
/// fórmula VINCULANTE del estado de pago por-compra, re-sourceada sobre <c>gastos</c> +
/// <c>movimientos_cuenta_corriente_proveedor.tipo = 'ajuste'</c> — NI la del proposal
/// (<c>SUM(importe) ... &lt;= 0 ⇒ pagada</c>) NI la del design (<c>−Σ importe WHERE tipo &lt;&gt;
/// 'compra'</c>), ambas RECHAZADAS. Los casos 4.12/4.13 seedean directo por EF una compra
/// confirmada SIN su propio movimiento `compra` en el ledger — la forma exacta que la población
/// pre-cutover del backfill (Slice 1) deja: la deuda no vive en ningún movimiento propio de la
/// compra. `SaldoDeProveedorTests.cs` (stage-8, sin tocar) sigue siendo la prueba de regresión de
/// byte-compatibilidad para el camino post-cutover ordinario — este archivo cubre lo que ese no
/// puede: los dos casos pre-cutover y el byte-compat con dataset discriminante (task 4.10).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SaldoDeProveedorReSourceadoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdAlicuotaIva21,
        int IdTipoCFB, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin, int IdEmpleadoAdmin);

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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "CC-proveedor-saldo-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21,
            idTipoCFB, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal, resultado.IdUsuarioAdmin);
    }

    private static SolicitudDeCompra SolicitudSimple(Contexto ctx, decimal costoUnitario, string numeroExterno) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFB, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 1m, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearYConfirmarCompraDeTotalAsync(Contexto ctx, decimal total, string numeroExterno)
    {
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, total, numeroExterno));
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    /// <summary>Seedea directo por EF una compra `confirmada`, SALTEANDO `ServicioDeCompras`
    /// enteramente — nunca se escribe un movimiento `compra` propio en el ledger. Es la forma
    /// exacta que la población pre-cutover del backfill (Slice 1) deja: la deuda de esta compra
    /// no vive en ningún movimiento del ledger, solo en su <c>Total</c> (tasks.md decisión 4 /
    /// `state.yaml` OD7 — "la deuda pre-cutover no tiene movimiento propio").</summary>
    private async Task<int> SembrarCompraPreCutoverConfirmadaAsync(
        Contexto ctx, decimal total, string numeroExterno)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var compra = new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = ctx.IdProveedor,
            IdTipoComprobante = ctx.IdTipoCFB,
            NumeroExterno = numeroExterno,
            FechaComprobante = DateOnly.FromDateTime(ahora.UtcDateTime),
            FechaRecepcion = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoCompra.Confirmada,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();
        return compra.Id;
    }

    /// <summary>Seedea directo por EF un gasto de categoría proveedor, ligado, SIN pasar por
    /// <c>ServicioDeGastos.InsertarGastoAsync</c> — nunca se escribe su movimiento `pago` en el
    /// ledger. Simula un pago registrado por el mecanismo RETIRADO, de antes de que este ledger
    /// tuviera capacidad de escribir pagos (design.md: "the design's formula loses a pre-cutover
    /// partial payment because it never queries gastos at all").</summary>
    private async Task SembrarGastoLigadoSinMovimientoDeLedgerAsync(
        Contexto ctx, int idTurno, int idComprobanteCompra, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.Gastos.Add(new Gasto
        {
            IdTenant = ctx.IdTenant,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Categoria = CategoriaGasto.Proveedor,
            IdProveedor = ctx.IdProveedor,
            Concepto = "Pago pre-cutover (mecanismo retirado)",
            IdMedioPago = ctx.IdMedioEfectivo,
            Importe = importe,
            IdComprobanteCompra = idComprobanteCompra,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SaldoDeProveedor> ObtenerSaldoAsync(Contexto ctx) =>
        JsonSerializer.Deserialize<SaldoDeProveedor>(
            await (await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/saldo")).Content.ReadAsStringAsync(),
            OpcionesJson)!;

    // ---- task 4.12: compra pre-cutover sin pagos ⇒ impaga (discriminador de proposal Y design) ----

    [Fact]
    public async Task UnaCompraPreCutoverSinPagosLeeImpaga()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraPreCutoverSinPagosLeeImpaga));
        var idCompra = await SembrarCompraPreCutoverConfirmadaAsync(ctx, total: 1234.56m, "pre-cutover-sin-pago");

        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/saldo");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var saldo = JsonSerializer.Deserialize<SaldoDeProveedor>(cuerpo, OpcionesJson)!;

        var linea = Assert.Single(saldo.Compras, c => c.IdComprobanteCompra == idCompra);
        // Bajo la fórmula del proposal (SUM(importe) sobre movimientos del ledger, incluido el
        // propio `compra`) esta compra NO TIENE movimiento propio ⇒ suma 0 ⇒ "<= 0 ⇒ pagada"
        // (RECHAZADO — evidencia de mutación abajo). OD7 nunca lee el ledger para este término:
        // pagado = 0 (sin gastos ligados) ⇒ Impaga, correcto.
        Assert.Equal(0m, linea.Pagado);
        Assert.Equal(EstadoPago.Impaga, linea.EstadoPago);
    }

    // ---- task 4.13: compra pre-cutover PARCIALMENTE pagada por un gasto sin movimiento de ledger --

    [Fact]
    public async Task UnaCompraPreCutoverPagadaParcialmentePorUnGastoSinMovimientoDeLedgerLeeParcial()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraPreCutoverPagadaParcialmentePorUnGastoSinMovimientoDeLedgerLeeParcial));
        var idTurno = await AbrirTurnoAsync(ctx);
        var idCompra = await SembrarCompraPreCutoverConfirmadaAsync(ctx, total: 1000m, "pre-cutover-parcial");
        await SembrarGastoLigadoSinMovimientoDeLedgerAsync(ctx, idTurno, idCompra, importe: 400m);

        var saldo = await ObtenerSaldoAsync(ctx);

        var linea = Assert.Single(saldo.Compras, c => c.IdComprobanteCompra == idCompra);
        // Bajo la fórmula del design (−Σ importe WHERE tipo <> 'compra', solo el ledger): NO hay
        // NINGÚN movimiento en el ledger para esta compra (ni compra, ni pago) ⇒ suma 0 ⇒ pagado
        // = 0 ⇒ Impaga (RECHAZADO — el pago pre-cutover queda perdido, exactamente lo que
        // tasks.md decisión 4 / state.yaml OD7 documentan). OD7 SÍ consulta `gastos`
        // directamente: pagado = 400 (el gasto ligado) + 0 (sin ajustes) = 400 ⇒ Parcial.
        Assert.Equal(400m, linea.Pagado);
        Assert.Equal(EstadoPago.Parcial, linea.EstadoPago);
        Assert.Equal(1000m, linea.Total);
    }

    // ---- task 4.10: /saldo byte-compatible, dataset discriminante por fila (mutation rule 6) -----

    [Fact]
    public async Task ElSaldoEsByteCompatibleConValoresDiscriminantesPorFila()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoEsByteCompatibleConValoresDiscriminantesPorFila));

        // Tres compras con Total/Pagado/EstadoPago TODOS distintos entre sí — ningún valor
        // coincide con otro campo de otra fila (mutation-proof-tests rule 6).
        var pagada = await CrearYConfirmarCompraDeTotalAsync(ctx, 1111m, "byte-compat-pagada");
        var parcial = await CrearYConfirmarCompraDeTotalAsync(ctx, 2222m, "byte-compat-parcial");
        var impaga = await CrearYConfirmarCompraDeTotalAsync(ctx, 3333m, "byte-compat-impaga");
        var idTurno = await AbrirTurnoAsync(ctx);

        var pagoPagada = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, ctx.IdProveedor, null, "Pago total", null,
                ctx.IdMedioEfectivo, null, 1111m, pagada.Id));
        Assert.Equal(HttpStatusCode.Created, pagoPagada.StatusCode);

        var pagoParcial = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, ctx.IdProveedor, null, "Pago parcial", null,
                ctx.IdMedioEfectivo, null, 777m, parcial.Id));
        Assert.Equal(HttpStatusCode.Created, pagoParcial.StatusCode);

        var saldo = await ObtenerSaldoAsync(ctx);

        Assert.Equal(ctx.IdProveedor, saldo.IdProveedor);
        // 1111 + 2222 + 3333 − 1111 (pago total) − 777 (pago parcial) = 4778.
        Assert.Equal(4778m, saldo.Saldo);
        Assert.Equal(3, saldo.Compras.Count);

        var lineaPagada = saldo.Compras.Single(c => c.IdComprobanteCompra == pagada.Id);
        Assert.Equal("byte-compat-pagada", lineaPagada.NumeroExterno);
        Assert.Equal(1111m, lineaPagada.Total);
        Assert.Equal(1111m, lineaPagada.Pagado);
        Assert.Equal(EstadoPago.Pagada, lineaPagada.EstadoPago);

        var lineaParcial = saldo.Compras.Single(c => c.IdComprobanteCompra == parcial.Id);
        Assert.Equal("byte-compat-parcial", lineaParcial.NumeroExterno);
        Assert.Equal(2222m, lineaParcial.Total);
        Assert.Equal(777m, lineaParcial.Pagado);
        Assert.Equal(EstadoPago.Parcial, lineaParcial.EstadoPago);

        var lineaImpaga = saldo.Compras.Single(c => c.IdComprobanteCompra == impaga.Id);
        Assert.Equal("byte-compat-impaga", lineaImpaga.NumeroExterno);
        Assert.Equal(3333m, lineaImpaga.Total);
        Assert.Equal(0m, lineaImpaga.Pagado);
        Assert.Equal(EstadoPago.Impaga, lineaImpaga.EstadoPago);
    }
}
