using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Ways.Application.Abstracciones;

namespace Ways.Api.ConectorMcp;

[McpServerToolType]
public sealed class HerramientasDeIdentidad
{
    /// <summary>Lee usuario y tenant con el <see cref="IWaysDbContext"/> de la request: el modo de
    /// tenant ya lo fijó <see cref="ManejadorDeTokenMcp"/>, así que la lectura pasa por RLS como
    /// ese usuario. Las dos filas existen: el manejador acaba de validar la cuenta y su tenant, y un
    /// usuario de plataforma nunca obtiene un token del conector.</summary>
    [McpServerTool(
        Name = "quien_soy",
        Title = "Quién soy",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Informa el usuario de Ways con el que se autorizó la conexión: nombre, correo, rol, tenant y la hora del servidor.")]
    public static async Task<string> QuienSoyAsync(
        IContextoDeUsuario actual,
        IWaysDbContext db,
        IRelojDelSistema reloj,
        CancellationToken ct)
    {
        var usuario = await db.Usuarios
            .AsNoTracking()
            .Where(u => u.Id == actual.UsuarioId)
            .Select(u => new { u.NombreUsuario, u.Mail, Rol = u.Rol!.Nombre, u.IdTenant })
            .FirstAsync(ct);

        var idTenant = usuario.IdTenant!.Value;
        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == idTenant)
            .Select(t => t.Nombre)
            .FirstAsync(ct);

        return string.Join(
            Environment.NewLine,
            $"Usuario: {usuario.NombreUsuario} ({usuario.Mail})",
            $"Rol: {usuario.Rol}",
            $"Tenant: {tenant} (id {idTenant})",
            $"Hora del servidor: {reloj.Ahora.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
    }
}
