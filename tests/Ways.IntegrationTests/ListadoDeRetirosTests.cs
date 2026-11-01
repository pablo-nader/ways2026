using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.RetirosDeAdministracionTests;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/caja/retiros</c>: un filtro por columna y un orden por columna sobre un escenario fijo de cuatro retiros vivos de
/// tres turnos, más un retiro dado de baja, un refuerzo y un retiro de otro tenant que nunca aparecen.
///
/// <list type="bullet">
/// <item>a1: turno A (Local 1, Admin), cerrado y conciliado, 200, "Retiro para pagar", 2026-10-01.</item>
/// <item>aC: el retiro del cierre por retiro del turno A, 300, "Retiro de cierre de turno", fecha del cierre (hoy).</item>
/// <item>c1: turno C (Local 1, Admin), cerrado después de la marca, 50, "Pago de hielo", 2026-10-10.</item>
/// <item>b1: turno B (Caja Dos, Vendedor), abierto, 80, "Depósito en banco", 2026-10-20.</item>
/// </list>
///
/// Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) con todas las cláusulas de
/// <c>ServicioDeRetiros.Filtrar</c> borradas a la vez, cada fila de <see cref="CadaFiltroDejaSoloSusRetiros"/> falla (cada fila pasa
/// un solo filtro, así que las otras cláusulas no la afectan) y devuelve los cuatro; (2) con el <c>switch</c> de <c>Ordenar</c>
/// reemplazado por el orden por id, cada fila de <see cref="CadaColumnaOrdenaTodoElConjunto"/> falla (los datos están elegidos para
/// que ningún orden pedido coincida con el de los ids); (3) con <c>m.DeletedAt == null</c> borrado de <c>Proyectar</c>,
/// <see cref="SinFiltrosListaLosRetirosVivosDelTenantYNadaMas"/> falla.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ListadoDeRetirosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        Contexto Ctx, int IdPuntoVentaDos, int IdTurnoA, int IdVendedor, IReadOnlyDictionary<string, int> Ids);

    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Escenario? _escenario;

    private async Task<Escenario> EscenarioAsync()
    {
        await Candado.WaitAsync();
        try
        {
            return _escenario ??= await SembrarAsync();
        }
        finally
        {
            Candado.Release();
        }
    }

    private async Task<Escenario> SembrarAsync()
    {
        var ctx = await PrepararAsync(fixture, "listado-retiros");
        var dos = await ctx.Admin.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "Caja Dos", ModoPuntoVenta.Web));
        Assert.True(dos.StatusCode == HttpStatusCode.Created, await dos.Content.ReadAsStringAsync());
        var idPuntoVentaDos = JsonSerializer.Deserialize<PuntoVentaListado>(await dos.Content.ReadAsStringAsync(), OpcionesJson)!.Id;
        var (vendedor, idVendedor) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Vendedor, "vendedor");

        var turnoA = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var a1 = await RegistrarAsync(ctx.Admin, turnoA, TipoMovimientoCaja.Retiro, 200m, "Retiro para pagar");
        var cierreA = await CerrarPorRetiroAsync(ctx.Admin, turnoA, 300m);
        var conciliacion = await ctx.Admin.PostAsJsonAsync(
            "/api/tesoreria/conciliaciones", new SolicitudDeConciliacion(ctx.IdEmpresa, cierreA, null));
        Assert.True(conciliacion.StatusCode == HttpStatusCode.Created, await conciliacion.Content.ReadAsStringAsync());

        var turnoC = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 0m);
        var c1 = await RegistrarAsync(ctx.Admin, turnoC, TipoMovimientoCaja.Retiro, 50m, "Pago de hielo");
        await CerrarPorRetiroAsync(ctx.Admin, turnoC, 0m);

        var turnoB = await AbrirTurnoAsync(vendedor, idPuntoVentaDos, 500m);
        var b1 = await RegistrarAsync(vendedor, turnoB, TipoMovimientoCaja.Retiro, 80m, "Depósito en banco");
        await RegistrarAsync(vendedor, turnoB, TipoMovimientoCaja.Refuerzo, 70m, "Refuerzo de cambio");
        var dadoDeBaja = await RegistrarAsync(vendedor, turnoB, TipoMovimientoCaja.Retiro, 60m, "Retiro que se elimina");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/caja/retiros/{dadoDeBaja}")).StatusCode);

        var otro = await PrepararAsync(fixture, "listado-retiros-otro");
        var turnoOtro = await AbrirTurnoAsync(otro.Admin, otro.IdPuntoVenta, 0m);
        await RegistrarAsync(otro.Admin, turnoOtro, TipoMovimientoCaja.Retiro, 100m, "Retiro de otro tenant");

        int aC;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            aC = await db.MovimientosCaja.Where(m => m.IdTurnoCaja == turnoA && m.EsRetiroDeCierre).Select(m => m.Id).SingleAsync();
        }

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var (id, fecha) in new[] { (a1, "2026-10-01T12:00:00Z"), (c1, "2026-10-10T12:00:00Z"), (b1, "2026-10-20T12:00:00Z") })
        {
            await EjecutarAsync(
                owner, "UPDATE movimientos_caja SET creado_el = $1 WHERE id_movimiento = $2", DateTimeOffset.Parse(fecha), id);
        }

        return new Escenario(
            ctx, idPuntoVentaDos, turnoA, idVendedor,
            new Dictionary<string, int> { ["a1"] = a1, ["aC"] = aC, ["c1"] = c1, ["b1"] = b1 });
    }

    private static async Task<PaginaDeRetiros> ListarAsync(Escenario e, string consulta)
    {
        var respuesta = await e.Ctx.Admin.GetAsync($"/api/caja/retiros?{consulta}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDeRetiros>(cuerpo, OpcionesJson)!;
    }

    private static string Nombres(Escenario e, PaginaDeRetiros pagina) =>
        string.Join(",", pagina.Items.Select(f => e.Ids.Single(p => p.Value == f.Id).Key));

    [Fact]
    public async Task SinFiltrosListaLosRetirosVivosDelTenantYNadaMas()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "");

        Assert.Equal(4, pagina.Total);
        Assert.Equal("aC,b1,c1,a1", Nombres(e, pagina));

        var a1 = pagina.Items.Single(f => f.Id == e.Ids["a1"]);
        Assert.Equal(
            (e.IdTurnoA, e.Ctx.IdPuntoVenta, "Local 1", e.Ctx.IdAdmin, 200m, "Retiro para pagar", true, true, false),
            (a1.IdTurnoCaja, a1.IdPuntoVenta, a1.PuntoVenta, a1.IdEmpleado, a1.Importe, a1.Motivo, a1.TurnoCerrado, a1.Conciliado,
                a1.EsRetiroDeCierre));
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T12:00:00Z"), a1.Fecha);
        var b1 = pagina.Items.Single(f => f.Id == e.Ids["b1"]);
        Assert.Equal(
            (e.IdPuntoVentaDos, "Caja Dos", e.IdVendedor, "vendedor-retiros", false, false),
            (b1.IdPuntoVenta, b1.PuntoVenta, b1.IdEmpleado, b1.Empleado, b1.TurnoCerrado, b1.Conciliado));
        Assert.True(pagina.Items.Single(f => f.Id == e.Ids["aC"]).EsRetiroDeCierre);
    }

    [Theory]
    [InlineData("desde=2026-10-05T00:00:00Z", "aC,b1,c1")]
    [InlineData("hasta=2026-10-15T00:00:00Z", "c1,a1")]
    [InlineData("idPuntoVenta={pv2}", "b1")]
    [InlineData("idTurnoCaja={turnoA}", "aC,a1")]
    [InlineData("idEmpleado={vendedor}", "b1")]
    [InlineData("importeDesde=200", "aC,a1")]
    [InlineData("importeHasta=80", "b1,c1")]
    [InlineData("motivo=DEPOSITO", "b1")]
    [InlineData("turnoCerrado=false", "b1")]
    [InlineData("turnoCerrado=true", "aC,c1,a1")]
    [InlineData("conciliado=true", "aC,a1")]
    [InlineData("conciliado=false", "b1,c1")]
    [InlineData("esRetiroDeCierre=true", "aC")]
    [InlineData("esRetiroDeCierre=false", "b1,c1,a1")]
    public async Task CadaFiltroDejaSoloSusRetiros(string filtro, string esperados)
    {
        var e = await EscenarioAsync();
        var consulta = filtro
            .Replace("{pv2}", e.IdPuntoVentaDos.ToString())
            .Replace("{turnoA}", e.IdTurnoA.ToString())
            .Replace("{vendedor}", e.IdVendedor.ToString());

        var pagina = await ListarAsync(e, consulta);

        Assert.Equal(esperados, Nombres(e, pagina));
        Assert.Equal(pagina.Items.Count, pagina.Total);
    }

    [Theory]
    [InlineData("fecha", "ascendente", "a1,c1,b1,aC")]
    [InlineData("puntoVenta", "ascendente", "b1,a1,aC,c1")]
    [InlineData("turno", "descendente", "b1,c1,a1,aC")]
    [InlineData("empleado", "descendente", "b1,a1,aC,c1")]
    [InlineData("importe", "ascendente", "c1,b1,a1,aC")]
    [InlineData("motivo", "ascendente", "b1,c1,aC,a1")]
    [InlineData("estadoTurno", "ascendente", "b1,a1,aC,c1")]
    [InlineData("conciliado", "ascendente", "c1,b1,a1,aC")]
    [InlineData("esRetiroDeCierre", "ascendente", "a1,c1,b1,aC")]
    public async Task CadaColumnaOrdenaTodoElConjunto(string orden, string direccion, string esperados)
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, $"orden={orden}&direccion={direccion}");

        Assert.Equal(esperados, Nombres(e, pagina));
    }

    [Fact]
    public async Task ElOrdenSeAplicaAntesDePaginar()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "orden=importe&direccion=descendente&pagina=2&tamanio=2");

        Assert.Equal((4, 2, 2), (pagina.Total, pagina.Pagina, pagina.Tamanio));
        Assert.Equal("b1,c1", Nombres(e, pagina));
    }

    [Theory]
    [InlineData("orden=saldo", "orden_invalido")]
    [InlineData("orden=importe&direccion=arriba", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenDesconocidoSeRechaza(string consulta, string codigo)
    {
        var e = await EscenarioAsync();

        var respuesta = await e.Ctx.Admin.GetAsync($"/api/caja/retiros?{consulta}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        using var problema = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(codigo, problema.RootElement.GetProperty("codigo").GetString());
    }

    /// <summary>dangling-fk-read-models: un punto de venta y un empleado dados de baja (sellados directo, como un dato heredado)
    /// se proyectan con id y nombre <c>null</c>, sus filtros por id dejan de encontrar el retiro, y el retiro sigue en el listado.</summary>
    [Fact]
    public async Task UnPuntoDeVentaYUnEmpleadoDadosDeBajaSeMuestranVaciosYSusFiltrosNoLosEncuentran()
    {
        var ctx = await PrepararAsync(fixture, "listado-retiros-bajas");
        var (vendedor, idVendedor) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Vendedor, "vendedor");
        var turno = await AbrirTurnoAsync(vendedor, ctx.IdPuntoVenta, 0m);
        var retiro = await RegistrarAsync(vendedor, turno, TipoMovimientoCaja.Retiro, 40m, "Retiro de prueba");

        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await EjecutarAsync(owner, "UPDATE usuarios SET deleted_at = now() WHERE id_usuario = $1", idVendedor);
            await EjecutarAsync(owner, "UPDATE puntos_venta SET deleted_at = now() WHERE id_punto_venta = $1", ctx.IdPuntoVenta);
        }

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDeRetiros>("/api/caja/retiros", OpcionesJson);
        var fila = Assert.Single(pagina!.Items);
        Assert.Equal((retiro, null, null, null, null), (fila.Id, fila.IdPuntoVenta, fila.PuntoVenta, fila.IdEmpleado, fila.Empleado));
        Assert.Equal(1, pagina.Total);

        foreach (var consulta in new[] { $"idPuntoVenta={ctx.IdPuntoVenta}", $"idEmpleado={idVendedor}" })
        {
            var filtrada = await ctx.Admin.GetFromJsonAsync<PaginaDeRetiros>($"/api/caja/retiros?{consulta}", OpcionesJson);
            Assert.Equal(0, filtrada!.Total);
        }
    }
}
