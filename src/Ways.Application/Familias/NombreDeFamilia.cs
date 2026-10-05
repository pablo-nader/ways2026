using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;

namespace Ways.Application.Familias;

/// <summary>
/// El nombre de una familia (doc 10 §3): cómo se normaliza y cuándo está disponible. Lo comparten quien edita una
/// familia (<see cref="ServicioDeFamilias.ActualizarAsync"/>) y quien la crea
/// (<see cref="ServicioDeAgrupacionDeFamilias.CrearAsync"/>), para que un nombre valga lo mismo en los dos.
/// </summary>
internal static class NombreDeFamilia
{
    /// <summary>El tope del nombre, el mismo que el alta de artículos aplica a <c>articulos.nombre</c>. La columna
    /// <c>familias.nombre</c> es <c>citext</c>, que no limita el largo: el tope lo hace cumplir la aplicación y no la
    /// base.</summary>
    public const int LargoMaximo = 150;

    /// <summary>El nombre sin espacios en los extremos. <c>400 nombre_requerido</c> si queda vacío y
    /// <c>400 nombre_muy_largo</c> si supera <see cref="LargoMaximo"/>.</summary>
    public static string Normalizar(string? valor)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El campo nombre es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximo)
        {
            throw new ErrorDominio(
                "nombre_muy_largo", $"El campo nombre no puede superar los {LargoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    /// <summary>Pre-chequeo best-effort del nombre (<c>db-error-backstops</c>): el contrato real es
    /// <c>ux_familias_nombre</c> con su traducción a <c>409 familia_nombre_duplicado</c>. La comparación es la de la
    /// columna <c>citext</c>, sin distinguir mayúsculas. <paramref name="excluirId"/> es la familia que se edita: su
    /// propio nombre no es un duplicado.</summary>
    public static async Task ExigirDisponibleAsync(
        IWaysDbContext db, string nombre, int? excluirId, CancellationToken ct)
    {
        if (await db.Familias.AnyAsync(f => f.Nombre == nombre && f.Id != excluirId, ct))
        {
            throw ErrorDominio.Conflicto(
                "familia_nombre_duplicado", $"Ya existe una familia llamada \"{nombre}\" en este tenant.");
        }
    }
}
