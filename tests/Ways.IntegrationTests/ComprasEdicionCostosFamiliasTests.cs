using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Compras;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// E6-S4: la edición de una compra confirmada reescribe el costo de una familia con el mismo camino de la confirmación
/// (todos los miembros vivos, idénticos) y no pisa el costo que ya escribió una compra confirmada más nueva de CUALQUIER
/// miembro de la familia. Factura A de 10 unidades: costo efectivo = costo unitario x 1,21.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasEdicionCostosFamiliasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static int contadorDeFacturas = 5_000;

    private sealed record Contexto(Entorno E, int IdTipoCFA, int IdAlicuotaIva21) : IDisposable
    {
        public void Dispose() => E.Dispose();
    }

    private sealed record Miembros(int M1, int M2, int DeBaja, int Suelto);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var e = await apoyo.PrepararAsync(nombre);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idAlicuota21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        return new Contexto(e, idTipo, idAlicuota21);
    }

    private async Task<Miembros> SembrarAsync(Entorno e)
    {
        var @base = ValoresBase(e);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", @base, familia);
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", @base, familia);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", @base, familia, dadoDeBaja: true);
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", @base);

        return new Miembros(m1, m2, deBaja, suelto);
    }

    private static SolicitudDeCompra Solicitud(Contexto c, string numero, params (int IdArticulo, decimal Costo)[] lineas) =>
        new(
            c.E.Proveedores[0], c.IdTipoCFA, c.E.IdPuntoVenta, numero, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [.. lineas.Select(l => new LineaDeCompraSolicitada(l.IdArticulo, "Línea de prueba", 10m, null, null, l.Costo, 0m, c.IdAlicuotaIva21, true))]);

    private static string NumeroNuevo() => $"0001-{Interlocked.Increment(ref contadorDeFacturas):D8}";

    private static async Task<CompraDetalle> CrearYConfirmarAsync(Contexto c, SolicitudDeCompra solicitud)
    {
        var alta = await c.E.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var creada = System.Text.Json.JsonSerializer.Deserialize<CompraDetalle>(cuerpoAlta, OpcionesJson)!;

        var confirmar = await c.E.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpo = await confirmar.Content.ReadAsStringAsync();
        Assert.True(confirmar.StatusCode == HttpStatusCode.OK, cuerpo);
        return System.Text.Json.JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task EditarAsync(Contexto c, int id, SolicitudDeCompra solicitud)
    {
        var respuesta = await c.E.Admin.PutAsJsonAsync($"/api/compras/{id}", solicitud);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private async Task<decimal?> CostoAsync(int idArticulo) => (await apoyo.LeerAsync(idArticulo)).CostoNominal;

    /// <summary>Editar el costo de la línea de un miembro escribe el mismo costo en TODOS los miembros vivos de la familia
    /// (el dado de baja y el suelto conservan el suyo): es el camino de la confirmación, no uno propio de la edición.</summary>
    [Fact]
    public async Task EditarElCostoDeLaLineaDeUnMiembroLoReplicaATodosLosMiembrosVivos()
    {
        using var c = await PrepararAsync(nameof(EditarElCostoDeLaLineaDeUnMiembroLoReplicaATodosLosMiembrosVivos));
        var m = await SembrarAsync(c.E);
        var creada = await CrearYConfirmarAsync(c, Solicitud(c, NumeroNuevo(), (m.M1, 100m)));
        Assert.Equal(121m, await CostoAsync(m.M2));

        await EditarAsync(c, creada.Id, Solicitud(c, creada.NumeroExterno, (m.M1, 150m)));

        Assert.Equal(181.5m, await CostoAsync(m.M1));
        Assert.Equal(181.5m, await CostoAsync(m.M2));
        Assert.Equal(45m, await CostoAsync(m.DeBaja));
        Assert.Equal(45m, await CostoAsync(m.Suelto));
    }

    /// <summary>La compra más nueva de OTRO miembro de la familia domina a toda la familia: editar la vieja no toca a ninguno.</summary>
    [Fact]
    public async Task UnaCompraMasNuevaDeOtroMiembroDeLaFamiliaImpideQueLaEdicionLaPise()
    {
        using var c = await PrepararAsync(nameof(UnaCompraMasNuevaDeOtroMiembroDeLaFamiliaImpideQueLaEdicionLaPise));
        var m = await SembrarAsync(c.E);
        var vieja = await CrearYConfirmarAsync(c, Solicitud(c, NumeroNuevo(), (m.M1, 100m)));
        await CrearYConfirmarAsync(c, Solicitud(c, NumeroNuevo(), (m.M2, 200m)));
        Assert.Equal(242m, await CostoAsync(m.M1));

        await EditarAsync(c, vieja.Id, Solicitud(c, vieja.NumeroExterno, (m.M1, 150m)));

        Assert.Equal(242m, await CostoAsync(m.M1));
        Assert.Equal(242m, await CostoAsync(m.M2));
    }
}
