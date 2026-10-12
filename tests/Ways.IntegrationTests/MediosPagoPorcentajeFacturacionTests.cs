using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// El porcentaje de facturación del medio de pago (<c>medios_pago.porcentaje_facturacion</c>):
/// el ABM lo guarda, lo valida entre 0 y 100 con hasta 2 decimales y lo devuelve; la CHECK de la
/// base es el backstop de una escritura cruda. Nada lo consume todavía.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MediosPagoPorcentajeFacturacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(HttpClient Admin, int IdTenant)> AprovisionarAsync(string nombre)
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

        return (admin, resultado.IdTenant);
    }

    private static MedioPagoAlta Alta(string nombre, decimal porcentaje) =>
        new(nombre, null, 9, ComportamientoMedioPago.Electronico, false, false, null, porcentaje);

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    [Fact]
    public async Task CrearYLeerDevuelvenElPorcentajeDeFacturacion()
    {
        var (admin, _) = await AprovisionarAsync(nameof(CrearYLeerDevuelvenElPorcentajeDeFacturacion));
        using var _admin = admin;

        var creado = await admin.PostAsJsonAsync("/api/catalogos/medios-pago", Alta("QR", 37.5m));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var medio = (await creado.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal(37.5m, medio.PorcentajeFacturacion);

        var leido = await admin.GetFromJsonAsync<MedioPagoListado>($"/api/catalogos/medios-pago/{medio.Id}", OpcionesJson);
        Assert.Equal(37.5m, leido!.PorcentajeFacturacion);

        var listado = await admin.GetFromJsonAsync<List<MedioPagoListado>>("/api/catalogos/medios-pago", OpcionesJson);
        Assert.Equal(37.5m, Assert.Single(listado!, m => m.Id == medio.Id).PorcentajeFacturacion);
    }

    [Fact]
    public async Task ActualizarCambiaElPorcentajeYLosExtremosSeAdmiten()
    {
        var (admin, _) = await AprovisionarAsync(nameof(ActualizarCambiaElPorcentajeYLosExtremosSeAdmiten));
        using var _admin = admin;

        var medio = (await (await admin.PostAsJsonAsync("/api/catalogos/medios-pago", Alta("QR", 0m)))
            .Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal(0m, medio.PorcentajeFacturacion);

        foreach (var valor in new[] { 100m, 0.01m, 99.99m, 0m })
        {
            var put = await admin.PutAsJsonAsync($"/api/catalogos/medios-pago/{medio.Id}", Alta("QR", valor));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(valor, (await put.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!.PorcentajeFacturacion);
        }
    }

    [Fact]
    public async Task SinElCampoEnElCuerpoElPorcentajeEsCero()
    {
        var (admin, _) = await AprovisionarAsync(nameof(SinElCampoEnElCuerpoElPorcentajeEsCero));
        using var _admin = admin;

        var cuerpo = new
        {
            nombre = "Billetera",
            idEmpresa = (int?)null,
            orden = 9,
            comportamiento = "Electronico",
            admiteVuelto = false,
            requiereReferencia = false,
            recargoPorcentaje = (decimal?)null
        };
        var creado = await admin.PostAsJsonAsync("/api/catalogos/medios-pago", cuerpo);
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        Assert.Equal(0m, (await creado.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!.PorcentajeFacturacion);
    }

    [Fact]
    public async Task ActualizarSinElCampoConservaElPorcentajeGuardado()
    {
        var (admin, _) = await AprovisionarAsync(nameof(ActualizarSinElCampoConservaElPorcentajeGuardado));
        using var _admin = admin;

        var medio = (await (await admin.PostAsJsonAsync("/api/catalogos/medios-pago", Alta("QR", 37.5m)))
            .Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;

        var cuerpoSinPorcentaje = new
        {
            nombre = "QR renombrado",
            idEmpresa = (int?)null,
            orden = 9,
            comportamiento = "Electronico",
            admiteVuelto = false,
            requiereReferencia = false,
            recargoPorcentaje = (decimal?)null
        };
        var put = await admin.PutAsJsonAsync($"/api/catalogos/medios-pago/{medio.Id}", cuerpoSinPorcentaje);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var actualizado = (await put.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal("QR renombrado", actualizado.Nombre);
        Assert.Equal(37.5m, actualizado.PorcentajeFacturacion);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("150")]
    [InlineData("10.123")]
    public async Task UnPorcentajeFueraDeRangoOConMasDeDosDecimalesSeRechazaAlCrearYAlActualizar(string crudo)
    {
        var (admin, _) = await AprovisionarAsync("Rango" + crudo.Replace(".", "p").Replace("-", "n"));
        using var _admin = admin;
        var invalido = decimal.Parse(crudo, System.Globalization.CultureInfo.InvariantCulture);

        var alCrear = await admin.PostAsJsonAsync("/api/catalogos/medios-pago", Alta("QR", invalido));
        Assert.Equal(HttpStatusCode.BadRequest, alCrear.StatusCode);
        Assert.Equal("porcentaje_facturacion_invalido", await CodigoAsync(alCrear));

        var medio = (await (await admin.PostAsJsonAsync("/api/catalogos/medios-pago", Alta("QR", 20m)))
            .Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        var alActualizar = await admin.PutAsJsonAsync($"/api/catalogos/medios-pago/{medio.Id}", Alta("QR", invalido));
        Assert.Equal(HttpStatusCode.BadRequest, alActualizar.StatusCode);
        Assert.Equal("porcentaje_facturacion_invalido", await CodigoAsync(alActualizar));

        var intacto = await admin.GetFromJsonAsync<MedioPagoListado>($"/api/catalogos/medios-pago/{medio.Id}", OpcionesJson);
        Assert.Equal(20m, intacto!.PorcentajeFacturacion);
    }

    [Fact]
    public async Task ActualizarUnIdInexistenteResponde404AunConUnPorcentajeInvalido()
    {
        var (admin, _) = await AprovisionarAsync(nameof(ActualizarUnIdInexistenteResponde404AunConUnPorcentajeInvalido));
        using var _admin = admin;

        var put = await admin.PutAsJsonAsync("/api/catalogos/medios-pago/999999", Alta("QR", 500m));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task UnaEscrituraCrudaFueraDeRangoViolaLaCheckDeLaBase()
    {
        var (admin, idTenant) = await AprovisionarAsync(nameof(UnaEscrituraCrudaFueraDeRangoViolaLaCheckDeLaBase));
        using var _admin = admin;
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var idMedio = await db.MediosPago.Select(m => m.Id).FirstAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = new NpgsqlCommand(
            "UPDATE medios_pago SET porcentaje_facturacion = 100.01 WHERE id_medio_pago = @id", cruda);
        comando.Parameters.AddWithValue("id", idMedio);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_medios_pago_porcentaje_facturacion", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaCheckDelPorcentajeSeTraduceA400PorcentajeFacturacionInvalido()
    {
        var postgres = new PostgresException(
            "mensaje de prueba", "ERROR", "ERROR", "23514", constraintName: "ck_medios_pago_porcentaje_facturacion");
        var falso = new ProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(falso, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contexto, new DbUpdateException("check", postgres), CancellationToken.None);

        Assert.True(manejado);
        Assert.Equal(StatusCodes.Status400BadRequest, contexto.Response.StatusCode);
        Assert.Equal("porcentaje_facturacion_invalido", falso.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }

    private sealed class ProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }
}
