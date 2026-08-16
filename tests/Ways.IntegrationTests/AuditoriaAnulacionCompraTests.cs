using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 3: <c>compra.anulacion</c> — cobertura (task 3.10) y
/// fail-closed (task 3.11). Espejo de <c>AuditoriaAnulacionVentaTests.cs</c>: archivo nuevo,
/// dedicado a auditoría, para no tocar <c>ComprasAnulacionYConcurrenciaTests.cs</c> (esa clase
/// además construye <c>ServicioDeCompras</c> directo con los 4 parámetros del constructor actual —
/// esta slice lo deja intacto, ver el doc-comment de <c>ServicioDeCompras.EjecutarAnulacionAsync</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AuditoriaAnulacionCompraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdAlicuotaIva21,
        int IdTipoCFA, int IdEmpleadoAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Auditoria-compras-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

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

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21, idTipoCFA,
            resultado.IdUsuarioAdmin);
    }

    private static SolicitudDeCompra SolicitudSimple(Contexto ctx, decimal unidades = 50m, decimal costoUnitario = 100m) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra? solicitud = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud ?? SolicitudSimple(ctx));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    // ---- task 3.10: cobertura -----------------------------------------------------------------

    /// <summary>Spec `comprobantes-compra`, "A compra anulación is attributable to its actor": una
    /// compra confirmada de 50 unidades, ninguna vendida, anulada — una fila, actor identificado,
    /// MISMA transacción que el <c>-50</c> de <c>movimientos_stock</c>.</summary>
    [Fact]
    public async Task CompraAnulacionCoberturaSobreUnaCompraConfirmadaSinVender()
    {
        var ctx = await PrepararAsync(nameof(CompraAnulacionCoberturaSobreUnaCompraConfirmadaSinVender));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 50m));
        await ConfirmarAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "compra.anulacion" && a.IdEntidad == creada.Id);
        Assert.Equal("comprobante_compra", fila.Entidad);
        // Judgment Day fix (slice 3 juez B ronda 1, finding 2): NotEqual(0, ...) no discrimina el
        // actor — un mutante que estampa un actor constante (p. ej. 1) lo pasa igual. Igualdad
        // exacta contra el admin real cierra ese hueco.
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdActor);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);

        using var valorAnterior = JsonDocument.Parse(fila.ValorAnterior!);
        using var valorNuevo = JsonDocument.Parse(fila.ValorNuevo);
        Assert.Equal("confirmada", valorAnterior.RootElement.GetProperty("estado").GetString());
        Assert.Equal("anulada", valorNuevo.RootElement.GetProperty("estado").GetString());

        // MISMA transacción que la reversa real del ledger.
        Assert.Equal(
            1, await db.MovimientosStock.CountAsync(
                m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion && m.Cantidad == -50m));
    }

    // ---- task 3.11: fail-closed ----------------------------------------------------------------

    /// <summary>Spec `comprobantes-compra`, "An audit failure blocks the anulación, same as the
    /// negative-stock refusal": <c>REVOKE INSERT ON auditoria</c> — mismo técnica de
    /// <c>ComprasAnulacionYConcurrenciaTests</c>.</summary>
    [Fact]
    public async Task UnaFallaAlEscribirLaAuditoriaBloqueaLaCompraAnulacion()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirLaAuditoriaBloqueaLaCompraAnulacion));
        var creada = await CrearBorradorAsync(ctx, SolicitudSimple(ctx, unidades: 20m));
        await ConfirmarAsync(ctx, creada.Id);

        await RevocarAsync("auditoria", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        }
        finally
        {
            await RestaurarAsync("auditoria", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Confirmada, (await db.ComprobantesCompra.FirstAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(
            0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id && m.Motivo == MotivoStock.Anulacion));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.IdEntidad == creada.Id && a.Accion == "compra.anulacion"));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == ctx.IdArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(20m, cantidad);

        // Reintento limpio inmediatamente después tiene que funcionar.
        var reintento = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
    }

    // ---- REVOKE/RESTORE (mismo patrón que ComprasAnulacionYConcurrenciaTests) -------------------

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
