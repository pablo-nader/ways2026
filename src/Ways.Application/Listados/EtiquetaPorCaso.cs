using System.Linq.Expressions;

namespace Ways.Application.Listados;

public static class EtiquetaPorCaso
{
    /// <summary>Arma <c>clave == k1 ? etiqueta1 : clave == k2 ? etiqueta2 : ... : alternativa</c> como un
    /// único árbol de expresión que EF traduce a <c>CASE</c>: sirve para ordenar en SQL por el texto que
    /// muestra la pantalla cuando la base solo guarda el código o el enum. <paramref name="clave"/> puede
    /// ser nullable (el valor ausente cae en <paramref name="alternativa"/>).</summary>
    public static Expression De<TClave>(
        Expression clave, IReadOnlyDictionary<TClave, string> etiquetas, Expression alternativa)
        where TClave : notnull
    {
        var cuerpo = alternativa;
        foreach (var (valor, etiqueta) in etiquetas.Reverse())
        {
            cuerpo = Expression.Condition(
                Expression.Equal(clave, Expression.Constant(valor, clave.Type)),
                Expression.Constant(etiqueta),
                cuerpo);
        }

        return cuerpo;
    }
}
