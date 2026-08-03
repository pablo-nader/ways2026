using System.Reflection;
using Ways.Application.Precios;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// stage-3-articulos-y-precios, Slice 3 (task 3.13; design: Testing Strategy — "History
/// immutability: no code path exposes Precio.Precio as settable... Assert via reflection/API
/// surface, not a DB trigger, documented exemption"). No hay CHECK/trigger en la base que
/// impida un <c>UPDATE precios SET precio = ...</c> directo por SQL crudo — esa es la exención
/// documentada (design decision 3 es una disciplina de código, no una garantía de esquema). Lo
/// que sí se puede y se prueba acá, sin base de datos, es que la superficie PÚBLICA de
/// <see cref="ServicioDePrecios"/>/sus contratos no ofrece ningún camino para editar el
/// <c>Monto</c> de una fila ya insertada — el único punto de escritura es
/// <see cref="ServicioDePrecios.AbrirNuevoPrecioAsync"/> (y sus dos envoltorios de contrato),
/// que siempre ABRE una fila nueva.
/// </summary>
public class ServicioDePreciosSuperficieTests
{
    [Fact]
    public void ServicioDePreciosNoExponeUnMetodoConNombreDeEdicion()
    {
        var metodosPublicos = typeof(ServicioDePrecios)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(ServicioDePrecios))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(metodosPublicos, n =>
            n.Contains("Actualizar", StringComparison.Ordinal) ||
            n.Contains("Editar", StringComparison.Ordinal) ||
            n.Contains("Modificar", StringComparison.Ordinal));
    }

    [Fact]
    public void NingunMetodoPublicoDeEscrituraRecibeUnIdentificadorDeFilaExistente()
    {
        var metodosDeEscritura = new[]
        {
            typeof(ServicioDePrecios).GetMethod(nameof(ServicioDePrecios.EstablecerPrecioAsync))!,
            typeof(ServicioDePrecios).GetMethod(nameof(ServicioDePrecios.ProgramarPrecioAsync))!,
            typeof(ServicioDePrecios).GetMethod(nameof(ServicioDePrecios.AbrirNuevoPrecioAsync))!
        };

        foreach (var metodo in metodosDeEscritura)
        {
            var nombresDeParametros = metodo.GetParameters().Select(p => p.Name).ToList();

            Assert.DoesNotContain(nombresDeParametros, n => n is "idPrecio" or "IdPrecio");
        }
    }

    [Fact]
    public void NingunContratoDeAltaExponeUnIdParaTargetearUnaFilaExistente()
    {
        var contratosDeAlta = new[] { typeof(AltaPrecio), typeof(ProgramarPrecio) };

        foreach (var contrato in contratosDeAlta)
        {
            var propiedades = contrato.GetProperties().Select(p => p.Name).ToList();

            Assert.DoesNotContain(propiedades, n => n is "Id" or "IdPrecio");
        }
    }
}
