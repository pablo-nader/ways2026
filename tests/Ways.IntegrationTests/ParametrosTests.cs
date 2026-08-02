using System.Net;
using System.Net.Http.Json;
using Ways.Application.Parametros;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 3 (task 3.20): resolución de <c>parametros</c> punta a punta a través de la API
/// (ADR-13) — punto de venta gana sobre empresa, empresa gana sobre el default declarado en
/// <see cref="ParametroConocido"/>. Corre contra Postgres real, migración 5 aplicada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ParametrosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";

    private async Task<(int IdEmpresa, int IdPuntoVenta, int IdOtroPuntoVenta, string Mail)> SembrarTenantConAdminAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = "Local", CreatedAt = ahora, UpdatedAt = ahora
        };
        // Segundo punto de venta REAL de la misma empresa (judgment-day, slice 3 ronda 1): la
        // prueba de "otro punto de venta sin fila propia cae al de empresa" necesita un id que
        // exista de verdad — ServicioDeParametros ahora valida que idPuntoVenta pertenezca a
        // idEmpresa antes de resolver/escribir, así que un id inventado ya no sirve de doble.
        var otroPuntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = "Local 2", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.AddRange(puntoVenta, otroPuntoVenta);

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = mail,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (empresa.Id, puntoVenta.Id, otroPuntoVenta.Id, mail);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task ElValorDePuntoDeVentaGanaSobreElDeEmpresaYElDeEmpresaSobreElDefault()
    {
        var (idEmpresa, idPuntoVenta, idOtroPuntoVenta, mail) =
            await SembrarTenantConAdminAsync(nameof(ElValorDePuntoDeVentaGanaSobreElDeEmpresaYElDeEmpresaSobreElDefault));
        using var cliente = await ClienteLogueadoAsync(mail);

        // Sin ninguna fila todavía: resuelve al default declarado en ParametroConocido.
        var sinFilas = await cliente.GetFromJsonAsync<ParametroResuelto>(
            $"/api/parametros/vuelto_maximo?idEmpresa={idEmpresa}&idPuntoVenta={idPuntoVenta}");
        Assert.Equal(ParametroConocido.VueltoMaximo.ValorPorDefecto, sinFilas!.Valor);

        // Fila de empresa (id_punto_venta NULL): gana sobre el default.
        var altaEmpresa = await cliente.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={idEmpresa}", new ParametroAlta("tolerancia_pago", "15", null));
        Assert.Equal(HttpStatusCode.OK, altaEmpresa.StatusCode);

        var soloEmpresa = await cliente.GetFromJsonAsync<ParametroResuelto>(
            $"/api/parametros/tolerancia_pago?idEmpresa={idEmpresa}&idPuntoVenta={idPuntoVenta}");
        Assert.Equal("15", soloEmpresa!.Valor);

        // Fila de punto de venta: gana sobre la de empresa.
        var altaPuntoVenta = await cliente.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={idEmpresa}",
            new ParametroAlta("tolerancia_pago", "25", idPuntoVenta));
        Assert.Equal(HttpStatusCode.OK, altaPuntoVenta.StatusCode);

        var conPuntoVenta = await cliente.GetFromJsonAsync<ParametroResuelto>(
            $"/api/parametros/tolerancia_pago?idEmpresa={idEmpresa}&idPuntoVenta={idPuntoVenta}");
        Assert.Equal("25", conPuntoVenta!.Valor);

        // Otro punto de venta REAL de la misma empresa, sin fila propia: cae al de empresa
        // (15), no al de punto de venta de otro punto de venta.
        var otroPuntoVenta = await cliente.GetFromJsonAsync<ParametroResuelto>(
            $"/api/parametros/tolerancia_pago?idEmpresa={idEmpresa}&idPuntoVenta={idOtroPuntoVenta}");
        Assert.Equal("15", otroPuntoVenta!.Valor);

        var listado = await cliente.GetFromJsonAsync<List<ParametroListado>>($"/api/parametros?idEmpresa={idEmpresa}");
        Assert.Equal(2, listado!.Count);
    }

    [Fact]
    public async Task UnPuntoDeVentaDeOtraEmpresaDevuelve400()
    {
        // Judgment-day (slice 3, ronda 1): parametros.id_punto_venta no tiene FK a empresas
        // en el esquema (decisión del usuario, sin cambio de esquema) — un punto de venta
        // real pero de otra empresa del mismo tenant tiene que rechazarse en el servicio.
        var (idEmpresa, _, _, mail) = await SembrarTenantConAdminAsync(nameof(UnPuntoDeVentaDeOtraEmpresaDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var otraEmpresa = new Empresa
        {
            IdTenant = db.Empresas.First(e => e.Id == idEmpresa).IdTenant,
            RazonSocial = nameof(UnPuntoDeVentaDeOtraEmpresaDevuelve400) + "-otra",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Empresas.Add(otraEmpresa);
        await db.SaveChangesAsync();

        var puntoVentaAjeno = new PuntoVenta
        {
            IdTenant = otraEmpresa.IdTenant,
            IdEmpresa = otraEmpresa.Id,
            Nombre = "Local ajeno",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.PuntosVenta.Add(puntoVentaAjeno);
        await db.SaveChangesAsync();

        var respuesta = await cliente.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={idEmpresa}",
            new ParametroAlta("tolerancia_pago", "15", puntoVentaAjeno.Id));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnaClaveDesconocidaDevuelve400()
    {
        var (idEmpresa, _, _, mail) = await SembrarTenantConAdminAsync(nameof(UnaClaveDesconocidaDevuelve400));
        using var cliente = await ClienteLogueadoAsync(mail);

        var respuesta = await cliente.GetAsync($"/api/parametros/clave_inventada?idEmpresa={idEmpresa}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }
}
