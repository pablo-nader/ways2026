using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Caja;
using Ways.Domain.Caja;

namespace Ways.IntegrationTests;

/// <summary>
/// Ajuste manual del efectivo de tesorería contra Postgres real: política (solo Admin), fila encadenada de tipo
/// <c>ajuste_manual</c>, 404 de empresa ajena antes que la validación y los 400 con su código.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TesoreriaAjusteDeEfectivoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/tesoreria/efectivo/ajustes";

    private static Task<HttpResponseMessage> PostearJsonAsync(HttpClient cliente, string json) =>
        cliente.PostAsync(Ruta, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    private static async Task<List<MovimientoTesoreria>> FilasAsync(ContextoDeTesoreriaDePrueba ctx)
    {
        await using var db = ctx.Db();
        return await db.MovimientosTesoreria.Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderBy(m => m.Id).AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task UnImportePositivoEscribeUnaFilaAjusteManualEnElLadoIngresoYResponde201()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEfPositivo");
        var antes = DateTimeOffset.UtcNow.AddSeconds(-5);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "  Efectivo hallado  ", 150.5m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoDeAjusteDeEfectivo>())!;
        var fila = Assert.Single(await FilasAsync(ctx));
        Assert.Equal(TipoMovimientoTesoreria.AjusteManual, fila.Tipo);
        Assert.Equal("Efectivo hallado", fila.Concepto);
        Assert.Equal((0m, 150.5m, 0m, 150.5m), (fila.Inicio, fila.Ingreso, fila.Egreso, fila.Final));
        Assert.Equal(ctx.IdUsuarioAdmin, fila.IdEmpleado);
        Assert.Null(fila.IdPuntoVenta);
        Assert.Null(fila.IdTurnoCaja);
        Assert.Null(fila.IdPago);
        Assert.InRange(fila.Fecha, antes, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal((fila.Id, 150.5m), (resultado.IdMovimiento, resultado.Importe));
    }

    [Fact]
    public async Task UnImporteNegativoEscribeElLadoEgresoYEncadenaConLaFilaAnterior()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEfNegativo");

        Assert.Equal(HttpStatusCode.Created, (await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "Alta", 100m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "Faltante", -130m))).StatusCode);

        var segunda = (await FilasAsync(ctx))[1];
        Assert.Equal((100m, 0m, 130m, -30m), (segunda.Inicio, segunda.Ingreso, segunda.Egreso, segunda.Final));
    }

    [Fact]
    public async Task UnaEmpresaAjenaOInexistenteDa404AntesQueLaValidacionDelCuerpo()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEf404A");
        var ajeno = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEf404B");

        var delOtroTenant = await ctx.Admin.PostAsJsonAsync(Ruta, new SolicitudDeAjusteDeEfectivo(ajeno.IdEmpresa, "x", 10m));
        var inexistente = await PostearJsonAsync(ctx.Admin, """{"idEmpresa":999999,"concepto":"","importe":0}""");

        Assert.Equal(HttpStatusCode.NotFound, delOtroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Empty(await FilasAsync(ajeno));
        Assert.Empty(await FilasAsync(ctx));
    }

    [Fact]
    public async Task SoloElAdminAjustaYSupervisorYVendedorRecibenElRechazoDeLaPolitica()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEfPolitica");
        var cuerpo = new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "x", 10m);

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.PostAsJsonAsync(Ruta, cuerpo)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.PostAsJsonAsync(Ruta, cuerpo)).StatusCode);
        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync(Ruta, cuerpo)).StatusCode);
        Assert.Empty(await FilasAsync(ctx));
    }

    [Theory]
    [InlineData("""{"idEmpresa":@E,"concepto":"   ","importe":10}""", "concepto_requerido", "Cr")]
    [InlineData("""{"idEmpresa":@E,"importe":10}""", "concepto_requerido", "Cs")]
    [InlineData("""{"idEmpresa":@E,"concepto":"x"}""", "importe_requerido", "Ir")]
    [InlineData("""{"idEmpresa":@E,"concepto":"x","importe":0}""", "importe_invalido", "I0")]
    [InlineData("""{"idEmpresa":@E,"concepto":"x","importe":1.005}""", "importe_invalido", "I3")]
    [InlineData("""{"idEmpresa":@E,"concepto":"x","importe":1000000000000}""", "importe_invalido", "Ig")]
    public async Task UnCuerpoInvalidoDa400ConSuCodigoYNoEscribeNada(string plantilla, string codigo, string sufijo)
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "AjEfInvalido" + sufijo);

        var respuesta = await PostearJsonAsync(ctx.Admin, plantilla.Replace("@E", ctx.IdEmpresa.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigo, await CodigoAsync(respuesta));
        Assert.Empty(await FilasAsync(ctx));
    }
}
