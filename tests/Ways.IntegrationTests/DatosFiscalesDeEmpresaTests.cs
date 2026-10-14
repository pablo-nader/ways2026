using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T7b: los datos legales de la empresa que la factura impresa lleva (ingresos brutos e inicio
/// de actividades). El <c>PUT /api/empresas/{id}</c> es un reemplazo completo, igual que con las alícuotas de percepción: un
/// campo ausente o <c>null</c> los borra.
///
/// Mutation-proof-tests: las cláusulas bajo prueba son el trim con vacío a <c>null</c>, el tope de 30 caracteres, que la
/// escritura persista los dos campos (y el reemplazo completo) y el 404 antes que la validación del cuerpo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DatosFiscalesDeEmpresaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private async Task<(HttpClient Admin, EmpresaListado Empresa)> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (admin, await EmpresaAsync(admin));
    }

    private static async Task<EmpresaListado> EmpresaAsync(HttpClient admin)
    {
        var cuerpo = await (await admin.GetAsync("/api/empresas")).Content.ReadAsStringAsync();
        return Assert.Single(JsonSerializer.Deserialize<List<EmpresaListado>>(cuerpo, OpcionesJson)!);
    }

    private static async Task<EmpresaListado> EditarAsync(HttpClient admin, int id, EmpresaEdicion edicion)
    {
        var respuesta = await admin.PutAsJsonAsync($"/api/empresas/{id}", edicion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<EmpresaListado>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, cuerpo);
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static EmpresaEdicion Edicion(EmpresaListado e, string? ingresosBrutos, DateOnly? inicio) =>
        new(e.RazonSocial, e.NombreFantasia, e.Cuit, e.AlicuotaPercepcionIibb, e.AlicuotaPercepcionIva, ingresosBrutos, inicio);

    [Fact]
    public async Task LaEmpresaGuardaYDevuelveIngresosBrutosEInicioDeActividadesRecortadosYLosMuestraElListado()
    {
        var (admin, empresa) = await PrepararAsync(nameof(LaEmpresaGuardaYDevuelveIngresosBrutosEInicioDeActividadesRecortadosYLosMuestraElListado));
        Assert.Null(empresa.IngresosBrutos);
        Assert.Null(empresa.InicioActividades);

        var editada = await EditarAsync(admin, empresa.Id, Edicion(empresa, "  901-123456-7 ", new DateOnly(2015, 3, 1)));

        Assert.Equal("901-123456-7", editada.IngresosBrutos);
        Assert.Equal(new DateOnly(2015, 3, 1), editada.InicioActividades);
        var releida = await EmpresaAsync(admin);
        Assert.Equal("901-123456-7", releida.IngresosBrutos);
        Assert.Equal(new DateOnly(2015, 3, 1), releida.InicioActividades);
    }

    /// <summary>Decisión de T7b: el PUT reemplaza todo. Sin los campos (o con <c>null</c>) se borran, igual que las alícuotas de
    /// percepción; lo que sí se conserva es lo que llega.</summary>
    [Fact]
    public async Task UnPutSinLosDatosFiscalesOConNullLosBorraYUnTextoEnBlancoTambien()
    {
        var (admin, empresa) = await PrepararAsync(nameof(UnPutSinLosDatosFiscalesOConNullLosBorraYUnTextoEnBlancoTambien));
        await EditarAsync(admin, empresa.Id, Edicion(empresa, "Exento", new DateOnly(2018, 7, 15)));

        var sinCampos = await EditarAsync(admin, empresa.Id, new EmpresaEdicion(empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit));
        Assert.Null(sinCampos.IngresosBrutos);
        Assert.Null(sinCampos.InicioActividades);

        await EditarAsync(admin, empresa.Id, Edicion(empresa, "Exento", new DateOnly(2018, 7, 15)));
        var enBlanco = await EditarAsync(admin, empresa.Id, Edicion(empresa, "   ", null));
        Assert.Null(enBlanco.IngresosBrutos);
        Assert.Null(enBlanco.InicioActividades);
        Assert.Null((await EmpresaAsync(admin)).IngresosBrutos);
    }

    [Fact]
    public async Task IngresosBrutosAdmiteTreintaCaracteresYRechazaTreintaYUno()
    {
        var (admin, empresa) = await PrepararAsync(nameof(IngresosBrutosAdmiteTreintaCaracteresYRechazaTreintaYUno));

        var justo = new string('9', 30);
        Assert.Equal(justo, (await EditarAsync(admin, empresa.Id, Edicion(empresa, justo, null))).IngresosBrutos);

        await AssertCodigoAsync(
            await admin.PutAsJsonAsync($"/api/empresas/{empresa.Id}", Edicion(empresa, new string('9', 31), null)),
            HttpStatusCode.BadRequest, "ingresos_brutos_muy_largo");
        Assert.Equal(justo, (await EmpresaAsync(admin)).IngresosBrutos);
    }

    [Fact]
    public async Task UnPutAUnaEmpresaQueNoExisteRespondeNotFoundAunqueElCuerpoSeaInvalido()
    {
        var (admin, empresa) = await PrepararAsync(nameof(UnPutAUnaEmpresaQueNoExisteRespondeNotFoundAunqueElCuerpoSeaInvalido));

        await AssertCodigoAsync(
            await admin.PutAsJsonAsync($"/api/empresas/{empresa.Id + 100000}", Edicion(empresa, new string('9', 31), null)),
            HttpStatusCode.NotFound, "no_encontrado");
    }

    [Fact]
    public async Task LasColumnasNuevasSonOpcionalesConElTipoDeclarado()
    {
        await PrepararAsync(nameof(LasColumnasNuevasSonOpcionalesConElTipoDeclarado));
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT column_name, data_type, is_nullable, character_maximum_length FROM information_schema.columns " +
            "WHERE table_name = 'empresas' AND column_name IN ('ingresos_brutos', 'inicio_actividades') ORDER BY column_name",
            owner);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<(string, string, string, int?)>();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.GetString(0), lector.GetString(1), lector.GetString(2), lector.IsDBNull(3) ? null : lector.GetInt32(3)));
        }

        Assert.Equal([("ingresos_brutos", "USER-DEFINED", "YES", null), ("inicio_actividades", "date", "YES", null)], filas);
    }
}
