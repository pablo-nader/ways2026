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

    /// <summary>Judgment-day slice 2 (ronda 1, juez B, WARNING) — guard fail-loud documentado en
    /// el doc-comment de la clase (tasks.md task 2.1, DEVIATION registrada): un
    /// <c>auditoria</c> ausente tiene que reventar con <see cref="InvalidOperationException"/> al
    /// primer acceso, nunca saltearse en silencio. Vía reflexión sobre la property PRIVADA
    /// <c>Auditoria</c> — el camino real (<see cref="ServicioDePrecios.AbrirNuevoPrecioAsync"/>)
    /// abre una transacción (<c>CreateExecutionStrategy</c> + <c>BeginTransactionAsync</c>) que el
    /// proveedor InMemory no soporta (mismo motivo documentado en
    /// <c>ServicioDeOfertasTests</c>/<c>ServicioDeUsuariosTests</c>), así que no hay forma de
    /// alcanzar el write path real sin Postgres. La property no toca ninguna de las otras tres
    /// dependencias del constructor, así que <c>null</c> alcanza para las tres.</summary>
    [Fact]
    public void ElGuardDeAuditoriaAusenteFallaFuerteEnVezDeSaltearseEnSilencio()
    {
        var servicio = new ServicioDePrecios(db: null!, reloj: null!, contexto: null!, auditoria: null);

        var propiedad = typeof(ServicioDePrecios)
            .GetProperty("Auditoria", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var excepcion = Record.Exception(() => propiedad.GetValue(servicio));

        var deReflexion = Assert.IsType<TargetInvocationException>(excepcion);
        Assert.IsType<InvalidOperationException>(deReflexion.InnerException);
    }
}
