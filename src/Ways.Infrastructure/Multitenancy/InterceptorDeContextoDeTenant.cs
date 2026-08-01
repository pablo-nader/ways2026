using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Setea los GUC de Postgres (<c>app.acceso</c>, <c>app.tenant_id</c>) que las policies
/// de RLS leen, cada vez que EF abre una conexión física (ADR-3).
///
/// <c>set_config(..., false)</c> es a nivel de sesión, no <c>SET LOCAL</c>: EF abre y
/// cierra una conexión por query fuera de una transacción explícita, así que
/// <c>SET LOCAL</c> sería un no-op la mayoría de las veces. Npgsql manda
/// <c>DISCARD ALL</c> al devolver la conexión al pool, así que el GUC no puede
/// filtrarse al siguiente tenant que la reutilice.
/// </summary>
public sealed class InterceptorDeContextoDeTenant(ITenantActual tenantActual) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await AplicarContextoAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        AplicarContextoAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private async Task AplicarContextoAsync(DbConnection connection, CancellationToken ct)
    {
        var modo = tenantActual.Modo switch
        {
            ModoDeAcceso.Tenant => "tenant",
            ModoDeAcceso.Plataforma => "plataforma",
            ModoDeAcceso.Login => "login",
            _ => string.Empty
        };

        var idTenant = tenantActual.Id?.ToString() ?? string.Empty;

        await using var comando = new NpgsqlCommand(
            "SELECT set_config('app.acceso', $1, false), set_config('app.tenant_id', $2, false)",
            (NpgsqlConnection)connection);

        comando.Parameters.Add(new NpgsqlParameter { Value = modo });
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });

        await comando.ExecuteNonQueryAsync(ct);
    }
}
