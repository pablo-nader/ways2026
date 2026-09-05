using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Ways.Application.Abstracciones;

/// <summary>
/// La estrategia de toda escritura NO IDEMPOTENTE que no tiene ninguna clave de idempotencia
/// natural que un reintento pueda usar para no duplicar: las altas de
/// clientes/artículos/usuarios/precios/listas de precio/ofertas/certificados fiscales, el
/// aprovisionamiento de un tenant, el backfill de <c>InicializadorDeBaseDeDatos</c>, las bajas de
/// organización/usuario/oferta y las operaciones manuales <c>ServicioDeStock.AjustarAsync</c> /
/// <c>ServicioDeVentas.AnularAsync</c>. Sobre todas ellas, <c>EnableRetryOnFailure</c> (global,
/// <c>DependencyInjection</c>) reintentaría la transacción entera tras un commit ambiguo — el
/// servidor comitea pero el ACK no llega antes de que se corte la conexión — y duplicaría filas en
/// silencio, o mostraría un 409/404 falso sobre una operación que en verdad tuvo éxito. La lista
/// congelada vive en <c>EscriturasSinReintentoEstructuralesTests</c>.
///
/// <para><c>ServicioDeVentas.EmitirAsync</c> es la excepción y SÍ conserva el reintento: su paso de
/// numeración comitea el <c>numero</c> en su propia transacción ANTES de la escritura, y ese número
/// es una clave de idempotencia real — el lambda de escritura hace <c>ChangeTracker.Clear()</c> y
/// después <c>BuscarPorNumeroComprometidoAsync</c>, así que un reintento sobre un commit ambiguo
/// devuelve el comprobante ya emitido en vez de reinsertarlo. El reintento automático es el ÚNICO
/// consumidor de esa clave: un reenvío del cajero traería una <c>SolicitudDeVenta</c> sin número y
/// emitiría un segundo comprobante.</para>
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
