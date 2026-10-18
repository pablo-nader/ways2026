using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y llamadas de las pruebas de las herramientas de ventas del conector MCP: un tenant
/// aprovisionado como lo hace la plataforma (empresa, punto de venta, medios de pago y cliente),
/// usuarios por rol con la contraseña de <see cref="ApoyoDeConectorMcp"/>, comprobantes y pagos
/// sembrados directo con fecha explícita, y la llamada a una herramienta con su texto y su marca de error.
/// </summary>
internal sealed class ApoyoDeConsultasMcp(WaysApiFixture fixture, ApoyoDeConectorMcp conector)
{
    private static long _numeroSecuencial = 5_000_000;

    public sealed record TenantDeVentas(
        int IdTenant, string NombreTenant, int IdEmpresa, int IdPuntoVenta, int IdCliente,
        int IdTipoTx, int IdTipoNcx, int IdMedioEfectivo, string NombreEfectivo, int IdMedioTransferencia,
        string NombreTransferencia);

    public sealed record ResultadoDeHerramienta(bool EsError, string Texto);

    public sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    public async Task<TenantDeVentas> SembrarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(ApoyoDeConectorMcp.MailRoot, ApoyoDeConectorMcp.PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var solicitud = new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", $"{unico}@ways.test", ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();
        var medios = await dbTenant.MediosPago.OrderBy(m => m.Orden).Select(m => new { m.Id, m.Nombre }).ToListAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new TenantDeVentas(
            resultado.IdTenant, unico, resultado.IdEmpresa, resultado.IdPuntoVenta, idCliente, idTx, idNcx,
            medios[0].Id, medios[0].Nombre, medios[1].Id, medios[1].Nombre);
    }

    /// <summary>Un usuario del tenant con el rol dado y la contraseña del conector, para autorizar por OAuth.</summary>
    public async Task<ApoyoDeConectorMcp.UsuarioDeTenant> SembrarUsuarioAsync(TenantDeVentas tenant, RolConocido rol)
    {
        using var _ = fixture.CreateClient();
        var unico = $"{rol}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var usuario = new Usuario
        {
            IdTenant = tenant.IdTenant,
            NombreUsuario = $"{unico}",
            Mail = $"{unico}@ways.test",
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(ApoyoDeConectorMcp.Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return new ApoyoDeConectorMcp.UsuarioDeTenant(tenant.IdTenant, tenant.NombreTenant, usuario.Id, usuario.Mail);
    }

    public async Task<int> SembrarEmpresaAsync(TenantDeVentas tenant, string razonSocial)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = tenant.IdTenant, RazonSocial = razonSocial, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    public async Task<int> SembrarPuntoVentaAsync(TenantDeVentas tenant, string nombre, int? idEmpresa = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var punto = new PuntoVenta
        {
            IdTenant = tenant.IdTenant,
            IdEmpresa = idEmpresa ?? tenant.IdEmpresa,
            Nombre = nombre,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(punto);
        await db.SaveChangesAsync();
        return punto.Id;
    }

    /// <summary>Siembra directo, sin pasar por el servicio de ventas. Un NCX lleva el total negativo, como
    /// lo guarda el sistema.</summary>
    public async Task<int> SembrarComprobanteAsync(
        TenantDeVentas tenant, DateTimeOffset fecha, decimal total, int idEmpleado, int? idPuntoVenta = null,
        bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.IdTenant,
            IdTipoComprobante = esNcx ? tenant.IdTipoNcx : tenant.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = idPuntoVenta ?? tenant.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            IdCliente = tenant.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    /// <summary><c>importe</c> es lo entregado por el cliente (nunca negativo) y <c>vuelto</c> lo devuelto;
    /// el signo de una NCX lo aporta el tipo del comprobante.</summary>
    public async Task SembrarPagoAsync(TenantDeVentas tenant, int idComprobante, int idMedioPago, decimal importe, decimal vuelto = 0m)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = tenant.IdTenant,
            IdComprobanteVenta = idComprobante,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = vuelto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Host del conector con un reloj fijo y los mails dados habilitados.</summary>
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostConReloj(
        DateTimeOffset ahora, params string[] mails) =>
        conector.HostConConector(
            string.Join(';', mails),
            configurarMas: builder => builder.ConfigureServices(
                servicios => servicios.AddSingleton<IRelojDelSistema>(new RelojFijo(ahora))));

    public static async Task<string> AccessTokenAsync(HttpClient cliente, string mail) =>
        (await ApoyoDeConectorMcp.ObtenerTokensAsync(cliente, mail)).GetProperty("access_token").GetString()!;

    public static async Task<ResultadoDeHerramienta> LlamarHerramientaAsync(
        HttpClient cliente, string accessToken, string herramienta, object argumentos)
    {
        var (estado, mensaje) = await ApoyoDeConectorMcp.LlamarMcpAsync(
            cliente, accessToken, "tools/call", new { name = herramienta, arguments = argumentos });
        Assert.Equal(HttpStatusCode.OK, estado);

        var resultado = mensaje!.Value.GetProperty("result");
        var esError = resultado.TryGetProperty("isError", out var marca) && marca.ValueKind == JsonValueKind.True;
        return new ResultadoDeHerramienta(esError, resultado.GetProperty("content")[0].GetProperty("text").GetString()!);
    }
}
