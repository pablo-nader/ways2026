using System.Text.RegularExpressions;

namespace Ways.Domain.Auditoria;

/// <summary>
/// El registro que <see cref="Ways.Application.Auditoria.ServicioDeAuditoria"/> escribe, validado
/// enteramente en el constructor (design decisión 3) — un registro ilegal no es construible, así
/// que ningún call site puede saltear la regla sin que el compilador/runtime lo frene primero.
///
/// <c>dto-contract-honesty</c>: las cuatro reglas de abajo son el contrato completo que
/// <see cref="PayloadDeAuditoria"/> (y cualquier fábrica futura) tiene que satisfacer — no hay una
/// quinta regla escondida en el writer ni en la base.
/// </summary>
public sealed partial record RegistroDeAuditoria
{
    private static readonly string[] ClavesProhibidas = ["password", "contrasena", "hash", "token", "secret"];

    public int IdTenant { get; }
    public int? IdPuntoVenta { get; }
    public AccionAuditada Accion { get; }
    public int IdEntidad { get; }
    public IReadOnlyDictionary<string, object?>? ValorAnterior { get; }
    public IReadOnlyDictionary<string, object?> ValorNuevo { get; }

    public RegistroDeAuditoria(
        int idTenant,
        int? idPuntoVenta,
        AccionAuditada accion,
        int idEntidad,
        IReadOnlyDictionary<string, object?>? valorAnterior,
        IReadOnlyDictionary<string, object?> valorNuevo)
    {
        // 1. valorNuevo no vacío: toda acción audita un hecho o un estado nuevo, nunca "nada".
        if (valorNuevo.Count == 0)
        {
            throw new InvalidOperationException(
                $"El invariante de escritura de auditoría fue violado: valorNuevo no puede estar " +
                $"vacío (acción {accion.Accion}).");
        }

        foreach (var (clave, valor) in valorNuevo)
        {
            ValidarClave(clave, accion.Accion);
            ValidarValorAnidado(valor, accion.Accion);
        }

        if (valorAnterior is not null)
        {
            foreach (var (clave, valor) in valorAnterior)
            {
                ValidarClave(clave, accion.Accion);
                ValidarValorAnidado(valor, accion.Accion);

                // 2. Regla de subconjunto: toda clave de valorAnterior está en valorNuevo — la
                // inversa NO (valorNuevo lleva además metadata propia de la operación).
                if (!valorNuevo.ContainsKey(clave))
                {
                    throw new InvalidOperationException(
                        $"El invariante de escritura de auditoría fue violado: la clave '{clave}' " +
                        $"de valorAnterior no está presente en valorNuevo (acción {accion.Accion}).");
                }
            }
        }

        IdTenant = idTenant;
        IdPuntoVenta = idPuntoVenta;
        Accion = accion;
        IdEntidad = idEntidad;
        ValorAnterior = valorAnterior;
        ValorNuevo = valorNuevo;
    }

    private static void ValidarClave(string clave, string accion)
    {
        // 4. Toda clave en snake_case.
        if (!ClaveSnakeCase().IsMatch(clave))
        {
            throw new InvalidOperationException(
                $"El invariante de escritura de auditoría fue violado: la clave '{clave}' no " +
                $"respeta snake_case (acción {accion}).");
        }

        // 3. Denylist sobre claves — backstop del hecho estructural de que ninguna fábrica de
        // PayloadDeAuditoria acepta una entidad (design decisión 5): ningún secreto debería
        // poder llegar acá, pero esta regla lo rechaza igual si alguna vez lo hiciera, sin
        // importar a qué profundidad de anidamiento aparezca la clave, incluso dentro de
        // diccionarios anidados de otro TValue o no genéricos (ver
        // <see cref="ValidarValorAnidado"/>).
        foreach (var prohibida in ClavesProhibidas)
        {
            if (clave.Contains(prohibida, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"El invariante de escritura de auditoría fue violado: la clave '{clave}' está " +
                    $"en la denylist de secretos (acción {accion}).");
            }
        }
    }

    /// <summary>Recorre <paramref name="valor"/> buscando diccionarios anidados (a cualquier
    /// profundidad, incluso dentro de listas de diccionarios, dentro de un
    /// <see cref="Dictionary{TKey, TValue}"/> con otro TValue por invarianza de tipos, o dentro de
    /// un <see cref="System.Collections.Hashtable"/> no genérico) y valida sus claves con las
    /// mismas reglas 3/4 que las claves top-level — el backstop de <see cref="ValidarClave"/> es
    /// verdadero para el registro completo, no solo para su primer nivel. Una clave no-string en
    /// un diccionario no genérico se rechaza directamente: validarla por su <c>ToString()</c>
    /// sería deshonesto, porque esa representación puede no coincidir con la identidad real de
    /// la clave.</summary>
    private static void ValidarValorAnidado(object? valor, string accion)
    {
        switch (valor)
        {
            case IReadOnlyDictionary<string, object?> diccionarioAnidado:
                foreach (var (claveAnidada, valorAnidado) in diccionarioAnidado)
                {
                    ValidarClave(claveAnidada, accion);
                    ValidarValorAnidado(valorAnidado, accion);
                }

                break;

            case string:
                break;

            case System.Collections.IDictionary diccionarioNoGenerico:
                foreach (System.Collections.DictionaryEntry entrada in diccionarioNoGenerico)
                {
                    if (entrada.Key is not string claveAnidadaNoGenerica)
                    {
                        throw new InvalidOperationException(
                            $"El invariante de escritura de auditoría fue violado: un diccionario " +
                            $"anidado tiene una clave no-string ('{entrada.Key}') (acción {accion}).");
                    }

                    ValidarClave(claveAnidadaNoGenerica, accion);
                    ValidarValorAnidado(entrada.Value, accion);
                }

                break;

            case System.Collections.IEnumerable lista:
                foreach (var elemento in lista)
                {
                    ValidarValorAnidado(elemento, accion);
                }

                break;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ClaveSnakeCase();
}
