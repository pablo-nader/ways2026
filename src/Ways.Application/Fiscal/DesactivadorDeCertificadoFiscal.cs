using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Implementa U4 (tasks.md Slice 4, mutation-proof-tests regla 3 v1.1): <c>UPDATE
/// certificados_fiscales SET activo = false … WHERE id_tenant = $1 AND id_empresa = $2 AND
/// ambiente = $3 AND activo AND deleted_at IS NULL</c> — la mitad "desactivar" de una rotación
/// (design.md: "rotation = deactivate+activate inside one transaction",
/// <see cref="ServicioDeCertificados.RegistrarAsync"/> la corre SIEMPRE antes del alta, dentro de
/// la MISMA transacción; un no-op si no había fila activa). Clase estática propia, no un método
/// privado de <see cref="ServicioDeCertificados"/> — mismo criterio que
/// <see cref="AsignadorDeNumeroFiscal"/>: el repo no tiene <c>InternalsVisibleTo</c> en ningún
/// lado (precedente <c>SobreSoap</c>, slice 2), así que un método <c>public static</c> propio es
/// lo que deja a los cinco kills de U4 (a-e) testeables DIRECTO desde
/// <c>Ways.IntegrationTests</c>, sin pasar por el flujo completo de PFX+cifrado de
/// <see cref="ServicioDeCertificados.RegistrarAsync"/> para cada conjunto.
///
/// <c>id_tenant</c> viaja EXPLÍCITO en el <c>WHERE</c> (a diferencia de U1/U3, que confían
/// enteramente en RLS) — defensa en profundidad deliberada para la tabla que guarda material de
/// clave: aun si algún día un caller pasara el <c>id_tenant</c> equivocado, RLS sigue sin dejar
/// pasar una fila de otro tenant (conjunct (a), probado bajo <c>ways_app</c>).
/// </summary>
public static class DesactivadorDeCertificadoFiscal
{
    /// <summary>Devuelve la cantidad de filas afectadas — el conjunct (d) ("un certificado ya
    /// inactivo no se toca") se prueba sobre este número, no sobre el estado final (que ya era
    /// <c>false</c> de todos modos, mutation-proof-tests regla 4).</summary>
    public static async Task<int> DesactivarActivoAsync(
        IWaysDbContext db,
        int idTenant,
        int idEmpresa,
        AmbienteFiscal ambiente,
        DateTimeOffset ahora,
        CancellationToken ct = default)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE certificados_fiscales SET activo = false, updated_at = $1 " +
            "WHERE id_tenant = $2 AND id_empresa = $3 AND ambiente = $4 AND activo AND deleted_at IS NULL";

        ParametrosDeComando.Agregar(comando, ahora);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idEmpresa);
        ParametrosDeComando.Agregar(comando, ambiente);

        return await comando.ExecuteNonQueryAsync(ct);
    }
}
