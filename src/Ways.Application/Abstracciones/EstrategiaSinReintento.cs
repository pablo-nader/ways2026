using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Ways.Application.Abstracciones;

/// <summary>
/// <c>AjustarAsync</c> (<c>ServicioDeStock</c>) / <c>AnularAsync</c> (<c>ServicioDeVentas</c>) —
/// operaciones raras, humanas y manuales, sin ninguna clave de idempotencia natural (a diferencia
/// de <c>ServicioDeVentas.EmitirAsync</c>, que reintenta con seguridad porque
/// <c>BuscarPorNumeroComprometidoAsync</c> detecta un commit ambiguo previo antes de reinsertar).
/// Sobre esas dos operaciones, <c>EnableRetryOnFailure</c> (global, <c>DependencyInjection</c>)
/// reintentaría la transacción entera tras un commit ambiguo — el servidor comitea pero el ACK no
/// llega antes de que se corte la conexión — y silenciosamente duplicaría un ajuste de stock, o
/// mostraría un 409 falso sobre una anulación que en verdad tuvo éxito.
///
/// <para><see cref="Microsoft.EntityFrameworkCore.Storage.NonRetryingExecutionStrategy"/> NO sirve
/// acá: no hereda de <see cref="ExecutionStrategy"/> y por eso no marca el ambient
/// <c>ExecutionStrategy.Current</c> que <c>BeginTransactionAsync</c> + una consulta EF dentro de
/// esa transacción necesitan para no disparar "does not support user-initiated transactions" (la
/// consulta resuelve su PROPIA estrategia reintentable desde la configuración del
/// <c>DbContext</c>, que sigue siendo <c>NpgsqlRetryingExecutionStrategy</c> sin importar con qué
/// se envolvió la llamada externa — <c>EjecutarAnulacionAsync</c> hace justamente eso, un
/// <c>SELECT</c> de pre-lectura dentro de la transacción). El mecanismo sancionado por EF Core
/// para optar por-operación fuera del retry global sin romper ese ambient tracking es subclasear
/// <see cref="ExecutionStrategy"/> con <c>maxRetryCount: 0</c> — mismo tipo base que la estrategia
/// reintentable, así que <c>Current</c> se sigue marcando igual.</para>
///
/// Con esto, la falla transitoria llega tal cual al operador: el reintento manual del humano es el
/// correcto acá, nunca uno automático y silencioso.
/// </summary>
public static class FabricaDeEstrategiaSinReintento
{
    public static IExecutionStrategy CrearEstrategiaSinReintento(IWaysDbContext db)
    {
        var dependencias = ((IInfrastructure<IServiceProvider>)db.Database).Instance
            .GetRequiredService<ExecutionStrategyDependencies>();
        return new EstrategiaSinReintento(dependencias);
    }
}

/// <summary>Ver el doc-comment de <see cref="FabricaDeEstrategiaSinReintento"/> —
/// <c>maxRetryCount: 0</c> más <see cref="ShouldRetryOn"/> siempre <c>false</c> es "nunca
/// reintentar", pero heredando de <see cref="ExecutionStrategy"/> (no de
/// <c>NonRetryingExecutionStrategy</c>) para preservar el ambient tracking que EF Core necesita
/// dentro de una transacción manual.</summary>
internal sealed class EstrategiaSinReintento(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 0, maxRetryDelay: TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => false;
}
