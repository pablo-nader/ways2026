using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Judgment-day (batch 9, ronda 2), issue CRITICAL: <c>InicializadorDeBaseDeDatos.BackfillDeUsuariosAsync</c>
/// es la única mutación NULL→valor legítima de <c>Usuario.IdTenant</c> en todo el sistema — el
/// guard de tamper de <c>WaysDbContext.EstamparTenant</c> rechazaba CUALQUIER <c>Modified</c>
/// con <c>IdTenant</c> tocado, sin distinguirla de una reasignación real, así que el backfill
/// (cargar las filas y asignarles la propiedad) reventaba en cada arranque de una base real con
/// una cuenta preexistente sin tenant. En una instalación nueva esto se escapaba: solo existe
/// <c>root</c> (que el filtro de rol excluye) y el backfill es un no-op — por eso ninguna
/// prueba lo había disparado nunca. Esta clase siembra a mano, ANTES de que el host arranque
/// (antes del primer <c>CreateClient()</c> de este fixture — el único momento en el que corre
/// <c>InicializadorDeBaseDeDatos.EjecutarAsync</c>), una cuenta huérfana con <c>id_tenant
/// NULL</c> que no es root, para forzar el caso real que se escapaba.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class InicializadorDeBaseDeDatosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailHuerfano = "huerfano@ways.test";

    /// <summary>Inserta el rol y la cuenta huérfana con EF, en modo plataforma, directo contra
    /// el contenedor recién migrado — antes de que exista cualquier fila de <c>roles</c> o
    /// <c>tenants</c> (todavía no arrancó el host, así que <c>InicializadorDeBaseDeDatos</c>
    /// no corrió). El estado <c>Added</c> de <c>Usuario</c> no lo valida
    /// <c>EstamparTenant</c> (ver su comentario), así que insertar con <c>IdTenant = null</c>
    /// acá no dispara el guard.</summary>
    private async Task SembrarUsuarioHuerfanoAntesDeArrancarAsync()
    {
        await using var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        siembra.Roles.Add(new Rol
        {
            Id = (int)RolConocido.Admin,
            Nombre = "admin",
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        siembra.Usuarios.Add(new Usuario
        {
            IdTenant = null,
            NombreUsuario = "huerfano",
            Mail = MailHuerfano,
            RolId = (int)RolConocido.Admin,
            PasswordHash = "hash-de-prueba",
            PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });

        await siembra.SaveChangesAsync();
    }

    [Fact]
    public async Task ElBackfillNoRompeElArranqueYAsignaElTenant1AUnaCuentaHuerfana()
    {
        await SembrarUsuarioHuerfanoAntesDeArrancarAsync();

        // Antes del fix, EjecutarAsync (disparado acá, en el primer CreateClient de este
        // fixture) tiraba InvalidOperationException desde el guard de tamper de
        // EstamparTenant y el host nunca terminaba de arrancar.
        var excepcion = await Record.ExceptionAsync(async () =>
        {
            using var cliente = fixture.CreateClient();
            await cliente.GetAsync("/api/auth/me");
        });

        Assert.Null(excepcion);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var huerfano = await db.Usuarios.SingleAsync(u => u.Mail == MailHuerfano);

        Assert.Equal(1, huerfano.IdTenant);
    }
}
