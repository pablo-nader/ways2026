using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// La dirección estructurada de la sucursal: alta y edición por la API (ida y vuelta, normalización, rechazos tipados,
/// 404 antes que el cuerpo, rastro de auditoría) y los CHECK de coordenadas como backstop de una escritura cruda.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SucursalesDireccionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdEmpresa, HttpClient Admin, int IdSucursal);

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
        var idSucursal = await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta);

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, admin, idSucursal);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static SucursalAlta Alta(
        int idEmpresa, string nombre, string? provincia = "Córdoba", decimal? latitud = -31.4201m,
        decimal? longitud = -64.1888m, string? calle = "Colón") =>
        new(idEmpresa, nombre, calle, "1234 bis", "Córdoba", provincia, "5000", latitud, longitud, "Frente a la plaza");

    [Fact]
    public async Task LaDireccionSeGuardaNormalizadaSeListaSeReemplazaYQuedaEnLaAuditoria()
    {
        var ctx = await PrepararAsync(nameof(LaDireccionSeGuardaNormalizadaSeListaSeReemplazaYQuedaEnLaAuditoria));

        var creada = await LeerAsync<SucursalListado>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/sucursales",
                new SucursalAlta(
                    ctx.IdEmpresa, "Centro", "  Colón ", " 1234 bis", "Córdoba", "  cordoba ", "5000",
                    -31.4201m, -64.1888m, "Frente a la plaza")),
            HttpStatusCode.Created);

        Assert.Equal("Colón", creada.Calle);
        Assert.Equal("1234 bis", creada.Altura);
        Assert.Equal("Córdoba", creada.Provincia);
        Assert.Equal("5000", creada.CodigoPostal);
        Assert.Equal(-31.4201m, creada.Latitud);
        Assert.Equal(-64.1888m, creada.Longitud);
        Assert.Equal("Frente a la plaza", creada.Referencia);

        var enLaLista = (await LeerAsync<List<SucursalListado>>(
            await ctx.Admin.GetAsync($"/api/sucursales?idEmpresa={ctx.IdEmpresa}"), HttpStatusCode.OK))
            .Single(s => s.Id == creada.Id);
        Assert.Equal(creada, enLaLista);

        var editada = await LeerAsync<SucursalListado>(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/sucursales/{creada.Id}",
                new SucursalEdicion("Centro", "Mitre", "S/N", "Rosario", "SANTA FE", "2000", 0m, 0m, "  ")),
            HttpStatusCode.OK);
        Assert.Equal("Mitre", editada.Calle);
        Assert.Equal("Santa Fe", editada.Provincia);
        Assert.Equal(0m, editada.Latitud);
        Assert.Null(editada.Referencia);

        var vaciada = await LeerAsync<SucursalListado>(
            await ctx.Admin.PutAsJsonAsync($"/api/sucursales/{creada.Id}", new SucursalEdicion("Centro")),
            HttpStatusCode.OK);
        Assert.Null(vaciada.Calle);
        Assert.Null(vaciada.Provincia);
        Assert.Null(vaciada.Latitud);
        Assert.Null(vaciada.Longitud);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var filas = await db.Auditoria.Where(a => a.IdEntidad == creada.Id && a.Entidad == "sucursal")
            .OrderBy(a => a.Id).Select(a => new { a.Accion, a.ValorAnterior, a.ValorNuevo }).ToListAsync();
        Assert.Equal(["sucursal.alta", "sucursal.edicion", "sucursal.edicion"], filas.Select(f => f.Accion));
        Assert.Equal("Córdoba", JsonDocument.Parse(filas[0].ValorNuevo).RootElement.GetProperty("provincia").GetString());
        Assert.Equal("Santa Fe", JsonDocument.Parse(filas[1].ValorNuevo).RootElement.GetProperty("provincia").GetString());
        Assert.Equal("Mitre", JsonDocument.Parse(filas[2].ValorAnterior!).RootElement.GetProperty("calle").GetString());
    }

    [Theory]
    [InlineData("provincia", "provincia_invalida")]
    [InlineData("latitud", "latitud_invalida")]
    [InlineData("longitud", "longitud_invalida")]
    [InlineData("incompleta", "coordenadas_incompletas")]
    [InlineData("calle", "calle_muy_largo")]
    public async Task UnaDireccionInvalidaSeRechazaConSuCodigoEnElAltaYEnLaEdicion(string caso, string codigo)
    {
        var ctx = await PrepararAsync($"Rechazo{caso}");
        var alta = caso switch
        {
            "provincia" => Alta(ctx.IdEmpresa, "X", provincia: "Narnia"),
            "latitud" => Alta(ctx.IdEmpresa, "X", latitud: 91m),
            "longitud" => Alta(ctx.IdEmpresa, "X", longitud: -181m),
            "incompleta" => Alta(ctx.IdEmpresa, "X", longitud: null),
            _ => Alta(ctx.IdEmpresa, "X", calle: new string('a', 121))
        };

        await AssertCodigoAsync(await ctx.Admin.PostAsJsonAsync("/api/sucursales", alta), HttpStatusCode.BadRequest, codigo);

        var edicion = new SucursalEdicion(
            "X", alta.Calle, alta.Altura, alta.Ciudad, alta.Provincia, alta.CodigoPostal, alta.Latitud, alta.Longitud,
            alta.Referencia);
        await AssertCodigoAsync(
            await ctx.Admin.PutAsJsonAsync($"/api/sucursales/{ctx.IdSucursal}", edicion), HttpStatusCode.BadRequest, codigo);
    }

    /// <summary>update-404-first: una sucursal inexistente contesta 404 aunque la dirección sea inválida.</summary>
    [Fact]
    public async Task UnaSucursalInexistenteContesta404AntesDeValidarLaDireccion()
    {
        var ctx = await PrepararAsync(nameof(UnaSucursalInexistenteContesta404AntesDeValidarLaDireccion));

        var put = await ctx.Admin.PutAsJsonAsync(
            "/api/sucursales/2000000000",
            new SucursalEdicion("X", Provincia: "Narnia", Latitud: 500m));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    private async Task<PostgresException> EscribirCrudoAsync(Contexto ctx, string asignaciones)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE sucursales SET {asignaciones} WHERE id_sucursal = {ctx.IdSucursal}";

        return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    /// <summary>Backstop de <c>DireccionDeSucursal.Normalizar</c>: una escritura cruda que esquive el servicio se detiene
    /// en el CHECK con el nombre que <c>ManejadorDeErrores</c> traduce a 400. Mutación corrida (ver el documento de la
    /// funcionalidad): cada CHECK quitado de la migración deja pasar su fila y la aserción cae.</summary>
    [Theory]
    [InlineData("latitud = 90.000001, longitud = 0", "ck_sucursales_latitud", "a")]
    [InlineData("latitud = -91, longitud = 0", "ck_sucursales_latitud", "b")]
    [InlineData("latitud = 0, longitud = 180.000001", "ck_sucursales_longitud", "c")]
    [InlineData("latitud = 0, longitud = -181", "ck_sucursales_longitud", "d")]
    [InlineData("latitud = 10, longitud = NULL", "ck_sucursales_coordenadas_completas", "e")]
    [InlineData("latitud = NULL, longitud = 10", "ck_sucursales_coordenadas_completas", "f")]
    public async Task LosCheckDeCoordenadasRechazanUnaEscrituraCrudaFueraDeRangoOIncompleta(
        string asignaciones, string constraint, string sufijo)
    {
        var ctx = await PrepararAsync($"CheckCoordenadas{sufijo}");

        var error = await EscribirCrudoAsync(ctx, asignaciones);

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task LosCheckAceptanLosLimitesYLaAusenciaDeCoordenadas()
    {
        var ctx = await PrepararAsync(nameof(LosCheckAceptanLosLimitesYLaAusenciaDeCoordenadas));
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        foreach (var asignaciones in new[]
                 {
                     "latitud = -90, longitud = 180", "latitud = 90, longitud = -180", "latitud = NULL, longitud = NULL"
                 })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = $"UPDATE sucursales SET {asignaciones} WHERE id_sucursal = {ctx.IdSucursal}";
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }
    }
}
