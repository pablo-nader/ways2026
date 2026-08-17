using System.Data;
using System.Data.Common;

namespace Ways.Application.Abstracciones;

/// <summary>
/// Fábrica única de parámetros para statements raw-ADO (Npgsql) — reemplaza los 16 métodos
/// privados <c>AgregarParametro</c>/<c>AgregarParametroNulo</c>/<c>AgregarParametroNullable</c>
/// duplicados en src/Ways.Application (judgment-day del PR #129). Normaliza a UTC cualquier
/// <see cref="DateTimeOffset"/> antes de escribirlo: la convención de EF
/// (<c>WaysDbContext.NormalizacionAUtc</c>) no alcanza este camino porque acá el
/// <c>DbParameter</c> se arma a mano, y Npgsql rechaza escribir contra <c>timestamptz</c> con
/// offset distinto de cero. <c>ToUniversalTime()</c> es una reexpresión, nunca mueve el
/// instante.
/// </summary>
internal static class ParametrosDeComando
{
    public static void Agregar(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = Normalizar(valor);
        comando.Parameters.Add(parametro);
    }

    public static void AgregarNulo(DbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor is null ? DBNull.Value : Normalizar(valor);
        comando.Parameters.Add(parametro);
    }

    public static void AgregarNulo(IDbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor is null ? DBNull.Value : Normalizar(valor);
        comando.Parameters.Add(parametro);
    }

    private static object Normalizar(object valor) =>
        valor is DateTimeOffset dto ? dto.ToUniversalTime() : valor;
}
