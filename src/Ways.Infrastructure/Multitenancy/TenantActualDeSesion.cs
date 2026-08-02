using System.Data;
using System.Data.Common;
using Npgsql;
using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Implementación de <see cref="ITenantActual"/> para requests HTTP: scoped, mutable.
/// Tiene tres mutadores: dos corren antes de la primera conexión de la request —
/// <c>OnValidatePrincipal</c> (ADR-2), para toda request con cookie ya emitida, y
/// <c>AuthEndpoints</c> (endpoint de login), que la pone en modo
/// <see cref="ModoDeAcceso.Login"/> antes de que exista cookie alguna — y un tercero,
/// <see cref="Suplantar"/>, que corre DESPUÉS de que la conexión de una transacción de
/// aprovisionamiento ya está abierta (ADR-16), por eso necesita el reaplicado explícito de
/// <see cref="ReaplicarSobreConexionAsync"/> — <see cref="InterceptorDeContextoDeTenant"/>
/// solo se dispara en <c>ConnectionOpened(Async)</c>, y para ese momento la conexión de la
/// transacción ya estaba abierta.
/// </summary>
public sealed class TenantActualDeSesion : ITenantActual
{
    public int? Id { get; private set; }

    public ModoDeAcceso Modo { get; private set; } = ModoDeAcceso.Ninguno;

    public void Establecer(ModoDeAcceso modo, int? idTenant)
    {
        if (modo == ModoDeAcceso.Tenant && idTenant is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ModoDeAcceso.Tenant)} requiere un id_tenant.");
        }

        Modo = modo;
        Id = modo == ModoDeAcceso.Tenant ? idTenant : null;
    }

    public IDisposable Suplantar(int idTenant)
    {
        var modoPrevio = Modo;
        var idPrevio = Id;

        Establecer(ModoDeAcceso.Tenant, idTenant);

        return new Restaurador(this, modoPrevio, idPrevio);
    }

    public async Task ReaplicarSobreConexionAsync(DbConnection conexion, CancellationToken ct = default)
    {
        var laAbrimosAca = conexion.State != ConnectionState.Open;
        if (laAbrimosAca)
        {
            await conexion.OpenAsync(ct);
        }

        try
        {
            await using var comando = new NpgsqlCommand(
                // is_local: true — a diferencia del set_config de InterceptorDeContextoDeTenant
                // (sesión completa), acá el GUC solo tiene que durar lo que dure ESTA
                // transacción de aprovisionamiento: vuelve solo al valor anterior al hacer
                // commit o rollback, sin depender de que Suplantar.Dispose() lo revierta.
                "SELECT set_config('app.acceso', $1, true), set_config('app.tenant_id', $2, true)",
                (NpgsqlConnection)conexion);
            comando.Parameters.Add(new NpgsqlParameter { Value = Modo.ComoGuc() });
            comando.Parameters.Add(new NpgsqlParameter { Value = Id?.ToString() ?? string.Empty });

            await comando.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (laAbrimosAca)
            {
                await conexion.CloseAsync();
            }
        }
    }

    private sealed class Restaurador(TenantActualDeSesion contexto, ModoDeAcceso modoPrevio, int? idPrevio)
        : IDisposable
    {
        public void Dispose() => contexto.Establecer(modoPrevio, idPrevio);
    }
}
