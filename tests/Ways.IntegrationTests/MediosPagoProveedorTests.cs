using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Ways.Application.Catalogos;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El proveedor de cobro de un medio de pago (<c>medios_pago.proveedor</c>): lo que el ABM guarda y devuelve, los 400 tipados y los dos
/// CHECK de respaldo, con la conexión cruda <c>ways_app</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MediosPagoProveedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Medios = "/api/catalogos/medios-pago";

    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Dictionary<string, object?> Cuerpo(
        string nombre, int idEmpresa, string comportamiento = "Electronico", params (string Clave, object? Valor)[] extras)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["nombre"] = nombre,
            ["idEmpresa"] = idEmpresa,
            ["orden"] = 9,
            ["comportamiento"] = comportamiento,
            ["admiteVuelto"] = false,
            ["requiereReferencia"] = false,
            ["recargoPorcentaje"] = null
        };

        foreach (var (clave, valor) in extras)
        {
            cuerpo[clave] = valor;
        }

        return cuerpo;
    }

    private static async Task<MedioPagoListado> LeerAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonSerializer.Deserialize<MedioPagoListado>(texto, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, string codigo)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, $"{respuesta.StatusCode}: {texto}");
        Assert.Equal(codigo, JsonDocument.Parse(texto).RootElement.GetProperty("codigo").GetString());
    }

    // Clausula: el proveedor se guarda, se devuelve a todos los roles y una edición sin el campo lo conserva.
    [Fact]
    public async Task ElProveedorSeGuardaSeDevuelveATodosLosRolesYUnaEdicionQueLoOmiteLoConserva()
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-roundtrip");

        var creado = await LeerAsync(
            await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo("QR MP", ctx.IdEmpresa, "Electronico", ("proveedor", "mercadopago_qr"))),
            HttpStatusCode.Created);
        Assert.Equal("mercadopago_qr", creado.Proveedor);

        var delVendedor = await LeerAsync(await ctx.Vendedor.GetAsync($"{Medios}/{creado.Id}"), HttpStatusCode.OK);
        Assert.Equal("mercadopago_qr", delVendedor.Proveedor);
        var listado = await ctx.Vendedor.GetFromJsonAsync<List<MedioPagoListado>>(Medios, OpcionesJson);
        Assert.Equal("mercadopago_qr", listado!.Single(m => m.Id == creado.Id).Proveedor);

        var sinCampo = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync($"{Medios}/{creado.Id}", Cuerpo("QR MP 2", ctx.IdEmpresa)), HttpStatusCode.OK);
        Assert.Equal("mercadopago_qr", sinCampo.Proveedor);

        var cambiado = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"{Medios}/{creado.Id}", Cuerpo("QR MP 2", ctx.IdEmpresa, "Electronico", ("proveedor", "mercadopago_point"))),
            HttpStatusCode.OK);
        Assert.Equal("mercadopago_point", cambiado.Proveedor);

        var quitado = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"{Medios}/{creado.Id}", Cuerpo("QR MP 2", ctx.IdEmpresa, "Electronico", ("proveedor", null))),
            HttpStatusCode.OK);
        Assert.Null(quitado.Proveedor);

        var repuesto = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"{Medios}/{creado.Id}", Cuerpo("QR MP 2", ctx.IdEmpresa, "Electronico", ("proveedor", "mercadopago_qr"))),
            HttpStatusCode.OK);
        Assert.Equal("mercadopago_qr", repuesto.Proveedor);
        var vaciado = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"{Medios}/{creado.Id}", Cuerpo("QR MP 2", ctx.IdEmpresa, "Electronico", ("proveedor", "  "))),
            HttpStatusCode.OK);
        Assert.Null(vaciado.Proveedor);
    }

    // Clausula: solo un medio Electronico puede tener proveedor; enviarlo a otro comportamiento es un 400 tipado.
    [Theory]
    [InlineData("Efectivo")]
    [InlineData("CuentaCorriente")]
    public async Task ElProveedorEnUnMedioQueNoEsElectronicoEsUn400(string comportamiento)
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-solo-" + comportamiento.ToLowerInvariant());

        await AssertCodigoAsync(
            await ctx.Admin.PostAsJsonAsync(
                Medios, Cuerpo("Raro", ctx.IdEmpresa, comportamiento, ("proveedor", "mercadopago_qr"))),
            "proveedor_solo_electronico");

        var creado = await LeerAsync(
            await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo("Normal", ctx.IdEmpresa, "Electronico", ("proveedor", "mercadopago_qr"))),
            HttpStatusCode.Created);
        await AssertCodigoAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"{Medios}/{creado.Id}", Cuerpo("Normal", ctx.IdEmpresa, comportamiento, ("proveedor", "mercadopago_qr"))),
            "proveedor_solo_electronico");
    }

    // Clausula: pasar un medio con proveedor a un comportamiento no electrónico sin enviarlo lo limpia en vez de fallar.
    [Fact]
    public async Task CambiarElComportamientoSinEnviarElProveedorLoLimpia()
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-limpia");
        var creado = await LeerAsync(
            await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo("QR MP", ctx.IdEmpresa, "Electronico", ("proveedor", "mercadopago_qr"))),
            HttpStatusCode.Created);

        var efectivo = await LeerAsync(
            await ctx.Admin.PutAsJsonAsync($"{Medios}/{creado.Id}", Cuerpo("QR MP", ctx.IdEmpresa, "Efectivo")), HttpStatusCode.OK);

        Assert.Null(efectivo.Proveedor);
    }

    [Theory]
    [InlineData("paypal")]
    [InlineData("MercadoPago_QR")]
    [InlineData("mercadopago")]
    public async Task UnProveedorDesconocidoEsUn400(string proveedor)
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-desconocido-" + proveedor.ToLowerInvariant().Replace("_", string.Empty));

        await AssertCodigoAsync(
            await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo("X", ctx.IdEmpresa, "Electronico", ("proveedor", proveedor))),
            "proveedor_cobro_invalido");
    }

    // Clausula: ck_medios_pago_proveedor y ck_medios_pago_proveedor_solo_electronico respaldan al servicio (23514 por nombre).
    [Theory]
    [InlineData("electronico", "paypal", "ck_medios_pago_proveedor")]
    [InlineData("efectivo", "mercadopago_qr", "ck_medios_pago_proveedor_solo_electronico")]
    [InlineData("cuenta_corriente", "mercadopago_point", "ck_medios_pago_proveedor_solo_electronico")]
    public async Task UnaEscrituraCrudaFueraDeLaReglaViolaSuCheck(string comportamiento, string proveedor, string constraint)
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-check-" + comportamiento.Replace("_", string.Empty));

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, proveedor, " +
            "created_at, updated_at) VALUES ($1, 'crudo', 1, $2::comportamiento_medio_pago, false, false, $3, now(), now())",
            conexion);
        comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = comportamiento });
        comando.Parameters.Add(new NpgsqlParameter { Value = proveedor });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnProveedorValidoSobreUnMedioElectronicoSeInsertaCrudo()
    {
        var ctx = await _apoyo.PrepararAsync("mp-prov-crudo-ok");

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, proveedor, " +
            "created_at, updated_at) VALUES ($1, 'crudo', 1, 'electronico', false, false, 'mercadopago_point', now(), now())",
            conexion);
        comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }
}
