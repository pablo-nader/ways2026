using System.Net;
using System.Net.Http.Json;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;

namespace Ways.IntegrationTests;

/// <summary>
/// Pregunta de diseño (coordinador, tras el shipping de <c>dispositivos</c>): ¿un dispositivo
/// REVOCADO (<c>deleted_at</c> seteado) sigue bloqueando la baja de su tenant/PV/el admin que lo
/// vinculó?
///
/// Respuesta, con evidencia: SÍ sigue bloqueando, y es el comportamiento CORRECTO per el diseño
/// existente de la etapa 20 (<c>InspectorDeUso</c>, "OD4" en su doc-comment): "UNA FILA DADA DE
/// BAJA LÓGICAMENTE IGUAL BLOQUEA... Ninguna rama emite AND d.deleted_at IS NULL" — una regla
/// GLOBAL, aplicada por igual a TODA tabla "marcada" del inventario (ventas anuladas, compras
/// anuladas, etc.), no un caso especial de <c>dispositivos</c>. <c>InventarioDeDependientes</c>
/// clasificó <c>dispositivos</c> como <c>Marcado</c> automáticamente (tiene <c>created_at</c>),
/// así que hereda esa misma regla sin que este PR haya escrito una sola línea de guard nuevo.
///
/// Esto es DELIBERADO por la misma razón que ya protege a una venta anulada: la baja de la fila
/// dependiente no borra que el cliente operó ahí. Si se quisiera lo contrario para
/// <c>dispositivos</c> específicamente (solo activos bloquean), hace falta una decisión de
/// producto explícita — el mecanismo de <c>InventarioDeDependientes</c>/<c>InspectorDeUso</c> no
/// tiene hoy un "carve-out parcial" por rama (el <see cref="Ways.Application.Organizacion.ClasificacionDeDependiente.Excluido"/>
/// existente es TODO o nada, nunca "solo si sigue activo") — agregarlo es un cambio de diseño
/// transversal (afecta el golden N3 y potencialmente otras ramas), no un ajuste de esta feature.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DispositivoRevocadoYGuardDeUsoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    [Fact]
    public async Task UnDispositivoRevocadoTodaviaBloqueaLaBajaDelTenant()
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var nombre = nameof(UnDispositivoRevocadoTodaviaBloqueaLaBajaDelTenant);
        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nombre,
            RazonSocialEmpresa: $"Empresa {nombre}",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nombre.ToLowerInvariant()}-admin@ways.test",
            Modo: ModoPuntoVenta.Escritorio);

        var alta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(solicitud.MailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var dispositivo = (await altaDispositivo.Content.ReadFromJsonAsync<DispositivoActual>())!;

        // Revocado — deleted_at seteado, baja lógica.
        var revocar = await admin.DeleteAsync($"/api/dispositivos/{dispositivo.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revocar.StatusCode);

        // La baja del tenant la hace ROOT (SoloPlataforma) — mismo guard (InspectorDeUso) que
        // protege empresa/PV/usuario, cascada de ServicioDeOrganizacion.EliminarTenantAsync.
        var bajaTenant = await root.DeleteAsync($"/api/plataforma/tenants/{resultado.IdTenant}");

        // EVIDENCIA: bloqueado, no permitido — el dispositivo revocado SIGUE contando como uso.
        Assert.Equal(HttpStatusCode.Conflict, bajaTenant.StatusCode);
        var problema = await bajaTenant.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("tenant_en_uso", problema.GetProperty("codigo").GetString());
    }
}
