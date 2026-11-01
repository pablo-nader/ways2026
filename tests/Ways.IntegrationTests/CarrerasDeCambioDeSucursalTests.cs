using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Una caja que cambia de sucursal entre la lectura previa de un escritor y su transacción. El cambio lo hace el owner
/// justo antes de que la request abra su transacción (<see cref="AlAbrirLaPrimeraTransaccion"/>), que es el hueco
/// entre las lecturas sin lock y los locks.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CarrerasDeCambioDeSucursalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdEmpresa, int IdAdmin, HttpClient Admin, int IdCaja1, int IdSucursal1);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, admin, resultado.IdPuntoVenta,
            await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta));
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task<PuntoVentaListado> CrearCajaAsync(Contexto ctx, string nombre, int? idSucursal = null) =>
        await LeerAsync<PuntoVentaListado>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, nombre, ModoPuntoVenta.Web, IdSucursal: idSucursal)),
            HttpStatusCode.Created);

    private static async Task<SucursalListado> CrearSucursalAsync(Contexto ctx, string nombre) =>
        await LeerAsync<SucursalListado>(
            await ctx.Admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(ctx.IdEmpresa, nombre)), HttpStatusCode.Created);

    private async Task<int> SembrarArticuloConStockAsync(Contexto ctx, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = ctx.IdTenant, Nombre = "Area carrera", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"carrera-{Guid.NewGuid():N}", Nombre = "Carrera", IdArea = area.Id,
            IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        await using var escritura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        await using var transaccion = await escritura.Database.BeginTransactionAsync();
        await EscritorDeStock.AsentarAsync(
            escritura.Database.GetDbConnection(), transaccion.GetDbTransaction(),
            [new AsientoDeStock(ctx.IdTenant, ctx.IdSucursal1, ctx.IdCaja1, articulo.Id, cantidad, MotivoStock.Ajuste, ctx.IdAdmin, ahora)],
            CancellationToken.None);
        await transaccion.CommitAsync();

        return articulo.Id;
    }

    /// <summary>Cláusula bajo prueba: dentro de la transacción, la sucursal de cada caja (leída con <c>FOR SHARE</c>)
    /// tiene que ser la pedida. La caja de referencia de la sucursal de origen se muda a otra sucursal después de que la
    /// transferencia la eligió: sin la comparación, la transferencia saldría de la sucursal equivocada.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaTransferenciaRechazaUnaCajaQueCambioDeSucursalAntesDeSuTransaccion(bool porSucursal)
    {
        var ctx = await PrepararAsync($"{nameof(LaTransferenciaRechazaUnaCajaQueCambioDeSucursalAntesDeSuTransaccion)}{porSucursal}");
        var caja2 = await CrearCajaAsync(ctx, "Local 2");
        var tercera = await CrearSucursalAsync(ctx, "Tercera");
        var articulo = await SembrarArticuloConStockAsync(ctx, 10m);
        var solicitud = porSucursal
            ? new SolicitudDeTransferencia(null, null, "Carrera", [new LineaDeTransferencia(articulo, 3m)], ctx.IdSucursal1, caja2.IdSucursal)
            : new SolicitudDeTransferencia(ctx.IdCaja1, caja2.Id, "Carrera", [new LineaDeTransferencia(articulo, 3m)]);

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(
            new AlAbrirLaPrimeraTransaccion(() => SucursalDePrueba.ReapuntarAsync(fixture, ctx.IdCaja1, tercera.Id))))
        {
            respuesta = await ctx.Admin.PostAsJsonAsync("/api/stock/transferencias", solicitud);
        }

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal("sucursal_cambio_durante_transferencia", JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.False(await db.MovimientosStock.AnyAsync(m => m.Motivo == MotivoStock.Transferencia));
        Assert.Equal(10m, await db.Stock.Where(s => s.IdArticulo == articulo).SumAsync(s => s.Cantidad));
    }

    /// <summary>Cláusula bajo prueba: la baja de la caja lee el punto de venta UNA sola vez, después de los locks
    /// (<c>single-read-under-lock</c>). La caja se muda a una sucursal vacía entre la validación previa y los locks; la
    /// cascada tiene que mirar la sucursal NUEVA (que queda sin cajas y cae), no la vieja, que sigue teniendo otra caja.</summary>
    [Fact]
    public async Task LaBajaDeUnaCajaDecideLaCascadaConLaSucursalLeidaBajoElLock()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDeUnaCajaDecideLaCascadaConLaSucursalLeidaBajoElLock));
        var aBajar = await CrearCajaAsync(ctx, "Local 2", idSucursal: ctx.IdSucursal1);
        var vacia = await CrearSucursalAsync(ctx, "Vacía");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(
            new AlAbrirLaPrimeraTransaccion(() => SucursalDePrueba.ReapuntarAsync(fixture, aBajar.Id, vacia.Id))))
        {
            respuesta = await ctx.Admin.DeleteAsync($"/api/puntos-venta/{aBajar.Id}");
        }

        Assert.True(respuesta.StatusCode == HttpStatusCode.NoContent, await respuesta.Content.ReadAsStringAsync());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var sucursales = await db.Sucursales.IgnoreQueryFilters()
            .Where(s => s.Id == vacia.Id || s.Id == ctx.IdSucursal1)
            .ToDictionaryAsync(s => s.Id, s => s.DeletedAt);
        Assert.NotNull(sucursales[vacia.Id]);
        Assert.Null(sucursales[ctx.IdSucursal1]);
    }

    /// <summary>Corre <paramref name="accion"/> una sola vez, justo antes de que la request abra su primera transacción.</summary>
    private sealed class AlAbrirLaPrimeraTransaccion(Func<Task> accion) : DbTransactionInterceptor
    {
        private int _disparos;

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _disparos) == 1)
            {
                await accion();
            }

            return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }
    }
}
