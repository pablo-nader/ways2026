using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Domain.Auditoria;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Precios;

/// <summary>
/// Motor de historial de precios (design decisions 3/4, tasks 3.2/3.3): el único punto de
/// escritura de <c>precios</c> es <see cref="AbrirNuevoPrecioAsync"/> — por cada artículo objetivo
/// (el artículo pedido o, si pertenece a una familia y el alcance lo pide, todos los miembros vivos
/// de su familia) cierra la fila actualmente abierta (si hay una) e inserta una nueva, siempre en la
/// MISMA transacción, nunca hay un <c>Update</c> sobre <see cref="Precio.Monto"/> de una fila
/// existente. La lectura
/// (<see cref="PrecioVigenteAsync"/>) resuelve <c>fija</c> por consulta filtrada por fecha y
/// <c>derivada</c> en el momento, sin persistir nunca una fila para una lista derivada (spec:
/// Derived List Price Resolution At Read Time).
///
/// Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API, mismo criterio
/// que <see cref="Articulos.ServicioDeArticulos"/>.
///
/// <para><b>DEVIATION registrada (tasks.md, Slice 2, task 2.1):</b> <paramref name="auditoria"/>
/// es OPCIONAL (default <c>null</c>), no un parámetro requerido — desviación deliberada del
/// patrón de <c>ServicioDeUsuarios</c> (que sí lo exige). Motivo: agregar un parámetro
/// REQUERIDO acá rompía la compilación de 9 archivos de test que instancian
/// <c>ServicioDePrecios</c> a mano sin pasar un cuarto argumento (10 líneas de instanciación en
/// total — <c>VentasCheckoutTests.cs</c> tiene dos): <c>ComprasAnulacionYConcurrenciaTests.cs</c>,
/// <c>OfertasResolucionTests.cs</c>, <c>PlanDeVentaFefoTests.cs</c>, <c>ReliquidacionTests.cs</c>,
/// <c>VentaEscrituraLoteTests.cs</c>, <c>VentasAtomicidadYConcurrenciaTests.cs</c>,
/// <c>VentasCheckoutTests.cs</c>, <c>VentasTurnoWiringTests.cs</c> y
/// <c>ServicioDeOfertasTests.cs</c> — incluido <c>tests/Ways.IntegrationTests/VentasCheckoutTests.cs</c>,
/// el único archivo que Orchestrator Decision 13 (tasks.md) prohíbe tocar en CUALQUIER slice de
/// esta etapa ("nothing in any slice has a reason to touch that file"), justo porque el design no
/// anticipó este ripple mecánico de constructor. Verificado: ninguno de esos call sites llama
/// nunca a <see cref="AbrirNuevoPrecioAsync"/> (todos son lectura pura vía
/// <c>ServicioDeOfertas.PreciosVigentesEnLoteAsync</c> o resolución de <see
/// cref="PrecioVigenteAsync"/>/<see cref="PreciosVigentesAsync"/>), así que un <c>auditoria</c>
/// ausente nunca se dereferencia en esos caminos. La propiedad <see cref="Auditoria"/> revienta
/// fuerte (nunca en silencio) si algún día SÍ se alcanza sin haberla inyectado — el fail-closed
/// de precios (spec, task 2.11) sigue intacto para todo caller real (DI siempre inyecta la
/// instancia real, <c>AddScoped&lt;ServicioDeAuditoria&gt;()</c>).</para>
/// </summary>
public class ServicioDePrecios(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeAuditoria? auditoria = null)
{
    /// <summary>Guard fail-loud del parámetro opcional documentado arriba — nunca un skip
    /// silencioso del fail-closed de auditoría.</summary>
    private ServicioDeAuditoria Auditoria => auditoria
        ?? throw new InvalidOperationException(
            "ServicioDePrecios necesita ServicioDeAuditoria para escribir un cambio de precio; " +
            "el constructor la recibió null. Solo válido en fixtures de test que nunca llaman " +
            "AbrirNuevoPrecioAsync (ver el doc-comment de la clase) — cualquier caller real " +
            "(DI) siempre la inyecta.");

    /// <summary>Tolerancia de desfasaje de reloj entre cliente y servidor para "vigente_desde no
    /// puede estar en el pasado" (spec: Programmable Future Prices — el spec no fija un número;
    /// 30 segundos es una decisión de esta capa de servicio, documentada acá porque no hay una
    /// cifra más autoritativa que citar) — sin esto, un cliente que arma "ahora + 1 segundo" y
    /// tarda en llegar a la red rechazaría de forma espuria.</summary>
    private static readonly TimeSpan ToleranciaReloj = TimeSpan.FromSeconds(30);

    /// <summary>Establece el precio vigente AHORA (spec: Price History Never Overwrites,
    /// "Changing a price closes the old row and opens a new one") — <c>vigente_desde</c> siempre
    /// es "ahora", nunca provisto por el cliente. <c>vigenteDesde: null</c> le indica a
    /// <see cref="AbrirNuevoPrecioAsync"/> que resuelva "ahora" DESPUÉS de tomar todos los locks
    /// (judgment-day, item 3) — capturarlo acá, antes de entrar a la transacción, es
    /// exactamente el bug que ese fix corrige: un llamador que espera el lock bajo contención
    /// terminaría con un <c>vigente_desde</c> más viejo que el de la fila que ya ganó la carrera
    /// y confirmó, disparando un <c>vigente_desde_invalido</c> espurio.
    ///
    /// <see cref="AltaPrecio.Alcance"/> es la decisión del cliente sobre la familia del artículo
    /// (<see cref="ModoDeLaSolicitud"/>): sin valor, un artículo miembro de una familia se rechaza
    /// con <c>alcance_requerido</c>.</summary>
    public Task<PrecioVigente> EstablecerPrecioAsync(int idArticulo, AltaPrecio datos, CancellationToken ct = default) =>
        AbrirNuevoPrecioAsync(
            idArticulo, datos.IdListaPrecio, datos.Precio, vigenteDesde: null, datos.ConfirmarReemplazo,
            ModoDeLaSolicitud(datos.Alcance), ct);

    /// <summary>Programa un precio a futuro (spec: Programmable Future Prices) —
    /// <see cref="ProgramarPrecio.VigenteDesde"/> tiene que ser una fecha futura antes de entrar
    /// a la transacción de <see cref="AbrirNuevoPrecioAsync"/>. <see cref="ProgramarPrecio.Alcance"/>
    /// tiene el mismo significado que en <see cref="EstablecerPrecioAsync"/>.</summary>
    public Task<PrecioVigente> ProgramarPrecioAsync(int idArticulo, ProgramarPrecio datos, CancellationToken ct = default)
    {
        ExigirVigenteDesdeFuturo(datos.VigenteDesde);
        return AbrirNuevoPrecioAsync(
            idArticulo, datos.IdListaPrecio, datos.Precio, datos.VigenteDesde, datos.ConfirmarReemplazo,
            ModoDeLaSolicitud(datos.Alcance), ct);
    }

    /// <summary>
    /// Traduce el <see cref="AlcanceDeFamilia"/> de la API al modo interno, dándole destino a cada
    /// valor: ausente ⇒ <see cref="ModoDeAlcanceDeFamilia.ExigirDecision"/>, <c>Familia</c> ⇒
    /// <see cref="ModoDeAlcanceDeFamilia.Familia"/>, <c>SoloEste</c> ⇒
    /// <see cref="ModoDeAlcanceDeFamilia.SoloEste"/>. El conversor JSON del servidor acepta también
    /// el ordinal del enum, así que un número que no es el de ninguno de los dos (el <c>0</c> incluido)
    /// llega hasta acá: se rechaza con <c>alcance_invalido</c> (400) en vez de caer en silencio en
    /// alguno de los tres. El cuarto modo, <see cref="ModoDeAlcanceDeFamilia.FamiliaSiCorresponde"/>,
    /// no tiene valor en la API: lo piden los llamadores internos directo a
    /// <see cref="AbrirNuevoPrecioAsync"/>.
    /// </summary>
    public static ModoDeAlcanceDeFamilia ModoDeLaSolicitud(AlcanceDeFamilia? alcance) => alcance switch
    {
        null => ModoDeAlcanceDeFamilia.ExigirDecision,
        AlcanceDeFamilia.Familia => ModoDeAlcanceDeFamilia.Familia,
        AlcanceDeFamilia.SoloEste => ModoDeAlcanceDeFamilia.SoloEste,
        _ => throw new ErrorDominio(
            "alcance_invalido", "El alcance indicado no es válido: tiene que ser Familia o SoloEste.", 400)
    };

    /// <summary>
    /// Design decision 3/4 (revisado en judgment-day, items 1-3) — única fila de escritura de
    /// <c>precios</c>. Todo ocurre en UNA transacción, sin reintento, en este orden:
    ///
    /// <list type="number">
    /// <item>El lock de MEMBRESÍA de familias (<see cref="LockDeMembresiaDeFamilias"/>) como
    /// PRIMERA sentencia: exclusivo si <paramref name="modo"/> puede sacar al artículo de su familia
    /// (<see cref="ReglaDeFamilias.RequiereLockExclusivo"/>), compartido en cualquier otro caso. La
    /// pertenencia que se lee después no puede cambiar hasta el commit.</item>
    /// <item>La lectura de la pertenencia del artículo (<see cref="LeerIdFamiliaAsync"/>, una sola, ya
    /// bajo ese lock) y la decisión de <see cref="ReglaDeFamilias.ResolverAlcance"/>: escribir solo
    /// el artículo, escribir toda su familia —las filas de <c>articulos</c> de los miembros vivos
    /// quedan bloqueadas en orden ascendente de id, <see cref="BloquearMiembrosAsync"/>—, o sacarlo de
    /// la familia y escribir solo él —su fila queda bloqueada
    /// (<see cref="BloquearFilaDelArticuloAsync"/>) y la salida misma se escribe más abajo—; o un
    /// rechazo 409 sin escribir nada (<c>alcance_requerido</c>, <c>familia_cambio</c>). Quien no es
    /// miembro no toma ningún lock de fila: solo el lock de membresía compartido.</item>
    /// <item>Un <c>pg_advisory_xact_lock</c> determinístico sobre el par <c>(artículo, lista)</c> de
    /// este tenant por CADA artículo objetivo, en orden ascendente de la CLAVE del lock
    /// (<see cref="OrdenDeLocksDePares"/>, <see cref="TomarLockDelParAsync"/>) — serializa CUALQUIER
    /// escritura concurrente sobre el mismo par, exista o no una fila abierta todavía (a diferencia del
    /// viejo <c>SELECT ... FOR UPDATE</c>, que solo podía lockear una fila YA EXISTENTE). Que los tres
    /// pasos suban siempre en el mismo orden (membresía; filas por id ascendente; pares por clave
    /// ascendente) es lo que impide el ciclo entre dos escritores.</item>
    /// <item>Recién ahí resuelve "ahora" —UNA vez, igual para todos los objetivos y para la salida de
    /// la familia— y trabaja en dos fases. FASE 1, solo lecturas: por cada objetivo lee la fila
    /// actualmente abierta con un SELECT plano (<see cref="BuscarFilaAbiertaAsync"/>; seguro porque el
    /// lock del par ya garantiza que ningún otro escritor está tocando ese par), decide si hace falta
    /// confirmación (fila pendiente —<c>vigente_desde &gt; ahora</c>— sin
    /// <paramref name="confirmarReemplazo"/>) y valida las fechas contra esa fila y su PREDECESOR
    /// (<see cref="PlanificarPrecioDeUnArticuloAsync"/>): un rechazo de CUALQUIER objetivo corta acá,
    /// antes de que ningún otro escriba. FASE 2, solo escrituras: la salida de la familia (en "solo
    /// este"), el cierre de las filas abiertas y de los predecesores
    /// (<see cref="CerrarFilasDelPlanAsync"/>) y el encolado de cada fila nueva con su fila de
    /// auditoría (<see cref="EncolarPrecioNuevo"/>).</item>
    /// </list>
    ///
    /// Es todo o nada: un conflicto de CUALQUIER objetivo (<c>precio_pendiente_existe</c>,
    /// <c>vigente_desde_invalido</c>) se detecta en la fase 1 y no escribe nada, y un fallo de la
    /// fase 2 aborta la transacción entera, incluida la salida de la familia de "solo este". Se
    /// confirma con un solo <c>SaveChangesAsync</c>. Si la fase 2 falla, además se sueltan del
    /// <c>ChangeTracker</c> las entidades que esta operación agregó
    /// (<see cref="DesacoplarLoAgregadoDesde"/>): el contexto vive todo el request, y un llamador que
    /// sigue con otro artículo después de un rechazo (<c>ServicioDeCompras.AplicarPrecioSugeridoAsync</c>)
    /// no puede terminar guardando por detrás las filas de una escritura revertida. El resultado es el
    /// <see cref="PrecioVigente"/> del artículo pedido.
    ///
    /// Con el advisory lock, la carrera de <c>ux_precios_vigente</c> (dos primeros precios
    /// concurrentes para el mismo par) YA NO es alcanzable por este camino de servicio: el
    /// segundo llamador espera el lock, y al retomarlo lee el estado YA COMITEADO por el
    /// primero, así que hace un cierre-y-apertura legítimo en vez de chocar contra el índice
    /// único (task 3.11, test adaptado en judgment-day: ambas altas concurrentes terminan en
    /// 201). El backstop (<c>ux_precios_vigente</c>, <c>ManejadorDeErrores</c> → 409
    /// <c>precio_vigente_duplicado</c>) se mantiene igual como defensa de esquema — solo queda
    /// alcanzable por una escritura cruda/fuera de banda que bypasee este servicio (misma
    /// familia que <c>PK_articulos_empresas</c>, Slice 2 judgment-day ronda 2).
    /// </summary>
    /// <param name="modo">Lo que decidió el llamador sobre la familia del artículo
    /// (<see cref="ModoDeAlcanceDeFamilia"/>). El valor por defecto exige decidir: un artículo
    /// miembro de una familia sin decisión se rechaza en vez de escribirse a medias.</param>
    public async Task<PrecioVigente> AbrirNuevoPrecioAsync(
        int idArticulo, int idListaPrecio, decimal precio, DateTimeOffset? vigenteDesde, bool confirmarReemplazo,
        ModoDeAlcanceDeFamilia modo = ModoDeAlcanceDeFamilia.ExigirDecision, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        await BuscarListaFijaAsync(idListaPrecio, ct);
        ExigirPrecioValido(precio);

        var idTenant = ExigirTenantDeLaSesion();

        // Sin reintento: ni la fila de precio ni la de auditoría son idempotentes y no hay clave
        // de idempotencia — ambas se construyen de cero dentro del lambda, así que un reintento
        // duplicaría el rastro (y chocaría contra ux_precios_vigente con un 409 falso).
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeMembresiaAsync(idTenant, modo, ct);

            var objetivos = await ResolverObjetivosAsync(idArticulo, idTenant, modo, ct);

            foreach (var idObjetivo in OrdenDeLocksDePares(idTenant, idListaPrecio, objetivos.Ids))
            {
                await TomarLockDelParAsync(idTenant, idObjetivo, idListaPrecio, ct);
            }

            // "ahora" se captura DESPUÉS de todos los locks (judgment-day, item 3) — nunca antes de
            // entrar a la transacción ni en un helper de la escritura. vigenteDesde == null (caso
            // inmediato, EstablecerPrecioAsync) también se resuelve acá, con el mismo "ahora"
            // post-lock para todos los objetivos y para la salida de la familia.
            var ahora = reloj.Ahora;
            var vigenteDesdeEfectivo = vigenteDesde ?? ahora;

            var planes = new List<PlanDeUnArticulo>(objetivos.Ids.Count);

            foreach (var idObjetivo in objetivos.Ids)
            {
                planes.Add(await PlanificarPrecioDeUnArticuloAsync(
                    idObjetivo, idListaPrecio, vigenteDesdeEfectivo, ahora, confirmarReemplazo, idTenant,
                    esDeFamilia: objetivos.Ids.Count > 1, ct));
            }

            // Desde acá todo es escritura. Cualquier fallo, incluido el del guardado, deja las entidades
            // agregadas rastreadas por el contexto: se sueltan antes de propagar el error.
            var yaRastreadas = db.ChangeTracker.Entries()
                .Select(entrada => entrada.Entity)
                .ToHashSet(ReferenceEqualityComparer.Instance);

            try
            {
                if (objetivos.IdFamiliaDeLaQueSale is not null)
                {
                    await SacarDeLaFamiliaAsync(idArticulo, idTenant, ahora, ct);
                }

                foreach (var plan in planes)
                {
                    await CerrarFilasDelPlanAsync(plan, vigenteDesdeEfectivo, ahora, ct);
                }

                foreach (var plan in planes)
                {
                    EncolarPrecioNuevo(plan, idListaPrecio, precio, vigenteDesdeEfectivo, ahora, idTenant);
                }

                await db.SaveChangesAsync(ct);
                await transaccion.CommitAsync(ct);
            }
            catch
            {
                DesacoplarLoAgregadoDesde(yaRastreadas);
                throw;
            }

            return new PrecioVigente(idArticulo, idListaPrecio, precio, vigenteDesdeEfectivo);
        });
    }

    /// <summary>Lock de membresía de familias del tenant (<see cref="LockDeMembresiaDeFamilias"/>),
    /// PRIMERA sentencia de la transacción: exclusivo solo cuando el modo puede cambiar la pertenencia
    /// (<see cref="ReglaDeFamilias.RequiereLockExclusivo"/>), compartido en los demás. La elección es
    /// previa a cualquier lectura y no se promueve después.</summary>
    private async Task TomarLockDeMembresiaAsync(int idTenant, ModoDeAlcanceDeFamilia modo, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        if (ReglaDeFamilias.RequiereLockExclusivo(modo))
        {
            await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);
        }
        else
        {
            await LockDeMembresiaDeFamilias.TomarCompartidoAsync(conexion, transaccionCruda, idTenant, ct);
        }
    }

    /// <summary>
    /// Lee la pertenencia del artículo bajo el lock de membresía, bloquea lo que corresponda y devuelve
    /// los ids que hay que escribir, ascendentes. Ni <c>BuscarArticuloAsync</c> ni ninguna lectura
    /// previa a la transacción decide acá: la lectura de <c>id_familia</c> es UNA sola y nace después
    /// del lock (<c>single-read-under-lock</c>). No escribe nada: la salida de la familia de "solo
    /// este" queda para <see cref="SacarDeLaFamiliaAsync"/>, después de resolver "ahora".
    /// </summary>
    private async Task<ObjetivosDeLaEscritura> ResolverObjetivosAsync(
        int idArticulo, int idTenant, ModoDeAlcanceDeFamilia modo, CancellationToken ct)
    {
        var idFamilia = await LeerIdFamiliaAsync(idArticulo, idTenant, ct);

        switch (ReglaDeFamilias.ResolverAlcance(modo, esMiembro: idFamilia is not null))
        {
            case ResolucionDeAlcanceDeFamilia.SoloElArticulo:
                return new ObjetivosDeLaEscritura([idArticulo], IdFamiliaDeLaQueSale: null);

            case ResolucionDeAlcanceDeFamilia.TodaLaFamilia:
                var miembros = await BloquearMiembrosAsync(idFamilia!.Value, idTenant, ct);

                // Los escritores de pertenencia toman el lock de membresía exclusivo, así que bajo
                // el lock este artículo es miembro. Si no aparece, alguien cambió su pertenencia
                // (o lo dio de baja) sin respetar el protocolo: se rechaza en vez de devolverle
                // al cliente un precio aplicado a la familia que no lo incluye.
                if (!miembros.Contains(idArticulo))
                {
                    throw ErrorDeFamiliaCambio();
                }

                return new ObjetivosDeLaEscritura(miembros, IdFamiliaDeLaQueSale: null);

            case ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia:
                await BloquearFilaDelArticuloAsync(idArticulo, idFamilia!.Value, idTenant, ct);
                return new ObjetivosDeLaEscritura([idArticulo], IdFamiliaDeLaQueSale: idFamilia);

            case ResolucionDeAlcanceDeFamilia.AlcanceRequerido:
                throw await ErrorDeAlcanceRequeridoAsync(idFamilia!.Value, idTenant, ct);

            case ResolucionDeAlcanceDeFamilia.FamiliaCambio:
                throw ErrorDeFamiliaCambio();

            default:
                throw new InvalidOperationException("Resolución de alcance de familia desconocida.");
        }
    }

    /// <summary>
    /// FASE 1, solo lecturas y validaciones: lee el estado del par de UN artículo objetivo y rechaza lo
    /// que no se puede escribir. No cierra ninguna fila, no registra auditoría y no agrega ninguna
    /// entidad: así un rechazo de un miembro posterior no deja nada hecho por los anteriores. Todos los objetivos comparten <paramref name="ahora"/> y
    /// <paramref name="vigenteDesdeEfectivo"/>. <paramref name="esDeFamilia"/> solo cambia la redacción
    /// de <c>precio_pendiente_existe</c>, que con más de un objetivo puede referirse a otro miembro.
    /// </summary>
    private async Task<PlanDeUnArticulo> PlanificarPrecioDeUnArticuloAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset vigenteDesdeEfectivo, DateTimeOffset ahora,
        bool confirmarReemplazo, int idTenant, bool esDeFamilia, CancellationToken ct)
    {
        var filaAbierta = await BuscarFilaAbiertaAsync(idArticulo, idListaPrecio, idTenant, ct);

        if (filaAbierta is not { } fila)
        {
            return new PlanDeUnArticulo(idArticulo, FilaAbierta: null, EsPendiente: false, Predecesor: null);
        }

        // Reemplazar una fila pendiente con la MISMA fecha ("corregir el importe
        // manteniendo la fecha") es una operación legítima — exactamente por eso
        // BuscarPredecesorAsync tiene que ser determinístico y excluir filas muertas
        // (judgment-day ronda 3, item 1): un reemplazo mismo-fecha deja una fila muerta
        // (vigente_desde == vigente_hasta) que puede compartir límite con el predecesor
        // real si esa fila, a su vez, se vuelve a reemplazar.
        var esPendiente = fila.VigenteDesde > ahora;

        if (esPendiente && !confirmarReemplazo)
        {
            throw ErrorDominio.Conflicto(
                "precio_pendiente_existe",
                esDeFamilia
                    ? "Ya existe un precio pendiente en esta lista para al menos un artículo de la familia; hay que confirmar el reemplazo."
                    : "Ya existe un precio pendiente para este artículo en esta lista; confirmá el reemplazo.");
        }

        if (!esPendiente && vigenteDesdeEfectivo < fila.VigenteDesde)
        {
            throw new ErrorDominio(
                "vigente_desde_invalido",
                "vigente_desde no puede ser anterior al del precio vigente actual.",
                400);
        }

        // (judgment-day ronda 2, item 1) Chequeo SIMÉTRICO al de arriba, pero contra el
        // PREDECESOR en vez de contra la fila activa: si `fila` es pendiente, buscamos
        // ANTES de tocar nada quién es su predecesor (la fila cuyo vigente_hasta coincide
        // con el vigente_desde original de `fila`) y rechazamos si la fecha nueva cae en
        // o antes del inicio de ESE predecesor — mismo criterio de "no anterior al inicio
        // de la fila que se está por afectar" que el chequeo de la fila activa, aplicado
        // un nivel más atrás. Sin esto, el re-cierre del predecesor
        // (CerrarFilasDelPlanAsync) lo dejaba con un límite ANTERIOR a su propio inicio,
        // invirtiendo su intervalo (vigente_hasta < vigente_desde) — silencioso hasta la
        // constraint de esquema (ck_precios_ventana_valida), que acá se adelanta con un
        // 400 claro y sin tocar ninguna fila.
        FilaVigente? predecesor = esPendiente
            ? await BuscarPredecesorAsync(idArticulo, idListaPrecio, idTenant, fila.Id, fila.VigenteDesde, ct)
            : null;

        if (predecesor is { } pred && vigenteDesdeEfectivo <= pred.VigenteDesde)
        {
            throw new ErrorDominio(
                "vigente_desde_invalido",
                "vigente_desde no puede ser anterior o igual al del precio predecesor.",
                400);
        }

        return new PlanDeUnArticulo(idArticulo, fila, esPendiente, predecesor);
    }

    /// <summary>
    /// FASE 2, primera mitad: cierra la fila abierta del objetivo y, si era pendiente, re-cierra también
    /// su predecesor. Son <c>UPDATE</c> crudos sobre la transacción: no dejan nada en el
    /// <c>ChangeTracker</c>.
    /// </summary>
    private async Task CerrarFilasDelPlanAsync(
        PlanDeUnArticulo plan, DateTimeOffset vigenteDesdeEfectivo, DateTimeOffset ahora, CancellationToken ct)
    {
        if (plan.FilaAbierta is not { } fila)
        {
            return;
        }

        // Reemplazo de una fila PENDIENTE: se cierra en su PROPIO vigente_desde (ventana
        // vacía, vigente_hasta == vigente_desde), no en el vigente_desde de la fila
        // nueva. Si se cerrara ahí, un reemplazo con una fecha nueva POSTERIOR a la
        // original dejaría al precio reemplazado brevemente "vigente" entre su fecha
        // original y la fecha nueva — exactamente lo que "reemplazado" dice que NO tiene
        // que pasar (spec: "the $150 pending row is REPLACED by the $160 one", no
        // "activo hasta que el nuevo empiece"). Para la fila ACTIVA (no pendiente) el
        // criterio es el opuesto y correcto: se cierra en el vigente_desde de la fila
        // nueva, porque esa fila SÍ estuvo vigente hasta ese momento (spec: "the $100
        // row's vigente_hasta is set to the new row's vigente_desde").
        var vigenteHastaDeLaFilaCerrada = plan.EsPendiente ? fila.VigenteDesde : vigenteDesdeEfectivo;

        await CerrarFilaAsync(fila.Id, vigenteHastaDeLaFilaCerrada, ahora, ct);

        if (plan.Predecesor is { } predecesor)
        {
            // (judgment-day, item 1) El PREDECESOR — la fila que se cerró originalmente
            // al abrirse `fila` (su vigente_hasta == fila.VigenteDesde) — queda con un
            // límite VIEJO si no se corrige acá. Sin esto: una fecha nueva ANTERIOR a la
            // original produce SOLAPAMIENTO (dos filas satisfacen el predicado "vigente"
            // en el rango entre ambas fechas — el historial miente); una fecha nueva
            // POSTERIOR produce un HUECO (ningún precio vigente en ese rango). Se
            // re-cierra al vigente_desde EFECTIVO de la fila nueva — mismo criterio que
            // la fila ACTIVA usa arriba para su propio cierre. El chequeo simétrico de la
            // fase 1 ya garantizó que `vigenteDesdeEfectivo` es estrictamente posterior
            // al inicio de este predecesor, así que el intervalo resultante nunca se
            // invierte.
            await CerrarFilaAsync(predecesor.Id, vigenteDesdeEfectivo, ahora, ct);
        }
    }

    /// <summary>
    /// FASE 2, segunda mitad, sin I/O: encola la fila de auditoría y la fila de precio nueva de UN
    /// objetivo (design call site 1 / task 2.1), después de los cierres: ambas quedan en el MISMO
    /// <c>SaveChangesAsync</c> de <see cref="AbrirNuevoPrecioAsync"/>, así que un INSERT de auditoría
    /// roto (fail-closed) revierte también el cambio de precio —y, con una familia, el de todos los
    /// miembros—.
    /// </summary>
    private void EncolarPrecioNuevo(
        PlanDeUnArticulo plan, int idListaPrecio, decimal precio, DateTimeOffset vigenteDesdeEfectivo,
        DateTimeOffset ahora, int idTenant)
    {
        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.CambioDePrecio(
            idListaPrecio, plan.FilaAbierta?.Monto, plan.FilaAbierta?.VigenteDesde, precio, vigenteDesdeEfectivo);

        Auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, idPuntoVenta: null, AccionAuditada.PrecioCambio, plan.IdArticulo, valorAnterior, valorNuevo));

        db.Precios.Add(new Precio
        {
            IdArticulo = plan.IdArticulo,
            IdListaPrecio = idListaPrecio,
            Monto = precio,
            VigenteDesde = vigenteDesdeEfectivo,
            VigenteHasta = null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
    }

    /// <summary>Suelta del <c>ChangeTracker</c> todo lo que esta operación le agregó —lo rastreado ahora
    /// que no estaba en <paramref name="yaRastreadas"/>, en el estado en que esté— y nada más: lo que el
    /// llamador ya tenía rastreado queda como estaba, a diferencia de <c>ChangeTracker.Clear()</c>. Así un
    /// <c>SaveChangesAsync</c> posterior sobre el mismo contexto no vuelve a ver las filas de una escritura
    /// que se revirtió.</summary>
    private void DesacoplarLoAgregadoDesde(IReadOnlySet<object> yaRastreadas)
    {
        var agregadas = db.ChangeTracker.Entries()
            .Where(entrada => !yaRastreadas.Contains(entrada.Entity))
            .ToList();

        foreach (var entrada in agregadas)
        {
            entrada.State = EntityState.Detached;
        }
    }

    /// <summary>Precio vigente de UN artículo en UNA lista a una fecha (spec: Current-Price
    /// Query Semantics By Date; Derived List Price Resolution At Read Time). <paramref
    /// name="fecha"/> por defecto es <c>reloj.Ahora</c>.
    ///
    /// <para>(judgment-day, item 5b) Divergencia DELIBERADA con <see cref="PreciosVigentesAsync"/>:
    /// acá NO se filtra por <c>lista.Activo</c> — una búsqueda puntual por id explícito puede
    /// resolver una lista inactiva (el llamador ya sabe qué lista quiere; una lista
    /// desactivada no deja de tener historial de precios válido). <see
    /// cref="PreciosVigentesAsync"/> sí filtra por <c>Activo</c> porque ahí el criterio es "qué
    /// listas mostrar por default", no "resolvé esta lista puntual". Documentado acá y en el
    /// otro método para que la próxima persona que lo lea no lo confunda con un bug.</para>
    /// </summary>
    public async Task<PrecioVigente> PrecioVigenteAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset? fecha, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        var lista = await BuscarListaAsync(idListaPrecio, ct);

        return await ResolverPrecioAsync(idArticulo, lista, fecha ?? reloj.Ahora, ct);
    }

    /// <summary>Precio vigente de un artículo en TODAS las listas activas del tenant a una fecha
    /// — endpoint "single artículo across listas" (scope de esta slice).
    ///
    /// <para>(judgment-day, item 5b) Filtra por <c>Activo</c> a propósito, a diferencia de <see
    /// cref="PrecioVigenteAsync"/> (que resuelve cualquier lista por id explícito, activa o no)
    /// — ver el doc-comment de ese método para el criterio completo.</para>
    ///
    /// <para>(judgment-day, item 5c, INFO para la etapa POS) <c>N+1</c> deliberado: una consulta
    /// por lista dentro del <c>foreach</c>, sin batchear. Aceptable para este endpoint
    /// "single artículo" (pocas listas por tenant), pero el catálogo del POS (etapa 5) va a
    /// necesitar resolver precios de MUCHOS artículos a la vez — ahí sí va a hacer falta
    /// batchear esta resolución (probablemente una consulta por lista sobre TODOS los artículos
    /// del catálogo, no una por artículo). No se refactoriza acá porque está fuera del alcance
    /// de esta slice.</para>
    /// </summary>
    public async Task<IReadOnlyList<PrecioVigente>> PreciosVigentesAsync(
        int idArticulo, DateTimeOffset? fecha, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);

        var fechaConsulta = fecha ?? reloj.Ahora;
        var listas = await db.ListasPrecio.Where(l => l.Activo).ToListAsync(ct);

        var resultado = new List<PrecioVigente>(listas.Count);
        foreach (var lista in listas)
        {
            resultado.Add(await ResolverPrecioAsync(idArticulo, lista, fechaConsulta, ct));
        }

        return resultado;
    }

    /// <summary>
    /// Resolución batch (stage-4-ofertas, task 3.4; spec: precios / Batch Current-Price
    /// Resolution; design: Batch Boundary) — ADITIVA: <see cref="PrecioVigenteAsync"/> y
    /// <see cref="PreciosVigentesAsync"/> quedan sin tocar, misma firma y semántica (design
    /// decision 5). Devuelve el producto cartesiano completo de <paramref name="idsArticulo"/> ×
    /// <paramref name="idsListaPrecio"/> — nunca una consulta por par.
    ///
    /// <para>Presupuesto fijo de 3 consultas, independiente de N artículos × M listas: (1) las
    /// listas pedidas por id, SIN filtro de <c>Activo</c> — mismo criterio explícito-por-id que
    /// <see cref="PrecioVigenteAsync"/>, documentado ahí, no el "qué listas mostrar por default"
    /// de <see cref="PreciosVigentesAsync"/>; (2) las listas base de las <c>derivada</c>s
    /// pedidas, solo si alguna no vino ya incluida en (1); (3) UNA consulta de <c>precios</c> con
    /// <c>= ANY</c> sobre el set de artículos y el set de listas <c>fija</c> involucradas (pedidas
    /// + bases), agrupada en memoria por <c>OrderByDescending(VigenteDesde)</c> — misma
    /// determinismo defensivo que <see cref="ObtenerPrecioFijaAsync"/>. Las derivadas se resuelven
    /// con el mismo <see cref="ResolvedorDePrecios.ResolverPrecioDerivado"/> que
    /// <see cref="ResolverPrecioAsync"/> (profundidad 1, guarda de precio negativo incluida).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), decimal?>> PreciosVigentesEnLoteAsync(
        IReadOnlyList<int> idsArticulo, IReadOnlyList<int> idsListaPrecio, DateTimeOffset fecha,
        CancellationToken ct = default)
    {
        if (idsArticulo.Count == 0 || idsListaPrecio.Count == 0)
        {
            return new Dictionary<(int, int), decimal?>();
        }

        // (1) Listas pedidas por id — sin filtro de Activo.
        var listasPedidas = await db.ListasPrecio
            .Where(l => idsListaPrecio.Contains(l.Id))
            .ToListAsync(ct);

        var idsListaFaltantes = idsListaPrecio.Except(listasPedidas.Select(l => l.Id)).ToList();
        if (idsListaFaltantes.Count > 0)
        {
            throw new ErrorDominio(
                "referencia_invalida", $"No existe la lista de precios {idsListaFaltantes[0]}.", 400);
        }

        var listaPorId = listasPedidas.ToDictionary(l => l.Id);

        var derivadas = listasPedidas.Where(l => l.Modo == ModoLista.Derivada).ToList();
        var idsBase = derivadas
            .Select(d => d.IdListaBase
                ?? throw new InvalidOperationException(
                    $"La lista {d.Id} es derivada sin id_lista_base — invariante de ServicioDeListasPrecio violado."))
            .Distinct()
            .ToList();

        // (2) Listas base de las derivadas — solo las que no vinieron ya en (1).
        var idsBaseFaltantes = idsBase.Except(listaPorId.Keys).ToList();
        if (idsBaseFaltantes.Count > 0)
        {
            var basesCargadas = await db.ListasPrecio
                .Where(l => idsBaseFaltantes.Contains(l.Id))
                .ToListAsync(ct);

            foreach (var listaBaseCargada in basesCargadas)
            {
                listaPorId[listaBaseCargada.Id] = listaBaseCargada;
            }
        }

        foreach (var derivada in derivadas)
        {
            var idListaBase = derivada.IdListaBase!.Value;
            if (!listaPorId.TryGetValue(idListaBase, out var listaBase))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe la lista base {idListaBase}.", 400);
            }

            if (listaBase.Modo != ModoLista.Fija)
            {
                throw new ErrorDominio(
                    "lista_base_invalida",
                    "La lista base de una lista derivada no puede ser a su vez derivada.",
                    400);
            }
        }

        // (3) UNA consulta de precios sobre todas las listas fija involucradas.
        var idsListaFija = listaPorId.Values
            .Where(l => l.Modo == ModoLista.Fija)
            .Select(l => l.Id)
            .Distinct()
            .ToList();

        var filas = idsListaFija.Count == 0
            ? []
            : await db.Precios
                .Where(p =>
                    idsArticulo.Contains(p.IdArticulo) && idsListaFija.Contains(p.IdListaPrecio) &&
                    p.VigenteDesde <= fecha && (p.VigenteHasta == null || p.VigenteHasta > fecha))
                .ToListAsync(ct);

        var montoFijaPorPar = filas
            .GroupBy(p => (p.IdArticulo, p.IdListaPrecio))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.VigenteDesde).First().Monto);

        var resultado = new Dictionary<(int, int), decimal?>(idsArticulo.Count * idsListaPrecio.Count);

        foreach (var idArticulo in idsArticulo)
        {
            foreach (var idListaPrecio in idsListaPrecio)
            {
                var lista = listaPorId[idListaPrecio];

                if (lista.Modo == ModoLista.Fija)
                {
                    resultado[(idArticulo, idListaPrecio)] =
                        montoFijaPorPar.TryGetValue((idArticulo, idListaPrecio), out var montoFija)
                            ? montoFija
                            : null;
                    continue;
                }

                var idListaBase = lista.IdListaBase!.Value;
                var montoBase = montoFijaPorPar.TryGetValue((idArticulo, idListaBase), out var montoBaseValor)
                    ? montoBaseValor
                    : (decimal?)null;

                if (montoBase is null)
                {
                    resultado[(idArticulo, idListaPrecio)] = null;
                    continue;
                }

                var porcentaje = lista.Porcentaje
                    ?? throw new ErrorDominio(
                        "precio_derivado_invalido",
                        $"La lista derivada {lista.Id} no tiene porcentaje configurado.",
                        422);

                resultado[(idArticulo, idListaPrecio)] = ResolvedorDePrecios.ResolverPrecioDerivado(montoBase.Value, porcentaje);
            }
        }

        return resultado;
    }

    /// <summary>Historial completo (spec: Price History Never Overwrites, "Historical prices
    /// remain queryable") — solo tiene sentido para una lista <c>fija</c>: una <c>derivada</c>
    /// nunca tiene filas propias.</summary>
    public async Task<IReadOnlyList<HistorialDePrecio>> HistorialDePrecioAsync(
        int idArticulo, int idListaPrecio, CancellationToken ct = default)
    {
        await BuscarArticuloAsync(idArticulo, ct);
        await BuscarListaFijaAsync(idListaPrecio, ct);

        return await db.Precios
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == idListaPrecio)
            .OrderByDescending(p => p.VigenteDesde)
            .Select(p => new HistorialDePrecio(p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta))
            .ToListAsync(ct);
    }

    /// <summary>Resuelve <paramref name="lista"/>: <c>fija</c> ⇒ consulta directa por fecha;
    /// <c>derivada</c> ⇒ resuelve la base (guarda de profundidad 1, orchestrator decision 2 —
    /// la escritura la bloquea <c>ServicioDeListasPrecio</c> en la Slice 4; acá es defensa en
    /// profundidad en LECTURA, por si una fila inconsistente llega a existir) y aplica
    /// <see cref="ResolvedorDePrecios.ResolverPrecioDerivado"/>.</summary>
    private async Task<PrecioVigente> ResolverPrecioAsync(
        int idArticulo, ListaPrecio lista, DateTimeOffset fecha, CancellationToken ct)
    {
        if (lista.Modo == ModoLista.Fija)
        {
            var montoFijo = await ObtenerPrecioFijaAsync(idArticulo, lista.Id, fecha, ct);
            return new PrecioVigente(idArticulo, lista.Id, montoFijo, fecha);
        }

        var idListaBase = lista.IdListaBase
            ?? throw new InvalidOperationException(
                $"La lista {lista.Id} es derivada sin id_lista_base — invariante de ServicioDeListasPrecio (Slice 4) violado.");

        var listaBase = await db.ListasPrecio.FirstOrDefaultAsync(l => l.Id == idListaBase, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la lista base {idListaBase}.", 400);

        if (listaBase.Modo != ModoLista.Fija)
        {
            throw new ErrorDominio(
                "lista_base_invalida",
                "La lista base de una lista derivada no puede ser a su vez derivada.",
                400);
        }

        var montoBase = await ObtenerPrecioFijaAsync(idArticulo, listaBase.Id, fecha, ct);

        // (judgment-day, item 4) explícito en lugar de `lista.Porcentaje!.Value`: una lista
        // derivada sin porcentaje configurado es un invariante violado (ServicioDeListasPrecio,
        // Slice 4, lo exige al escribir) — mismo código de dominio que un precio derivado
        // negativo (ResolvedorDePrecios.ResolverPrecioDerivado), nunca un NRE crudo.
        var porcentaje = lista.Porcentaje
            ?? throw new ErrorDominio(
                "precio_derivado_invalido",
                $"La lista derivada {lista.Id} no tiene porcentaje configurado.",
                422);

        var monto = montoBase is { } b
            ? ResolvedorDePrecios.ResolverPrecioDerivado(b, porcentaje)
            : (decimal?)null;

        return new PrecioVigente(idArticulo, lista.Id, monto, fecha);
    }

    private async Task<decimal?> ObtenerPrecioFijaAsync(
        int idArticulo, int idListaPrecio, DateTimeOffset fecha, CancellationToken ct) =>
        await db.Precios
            .Where(p =>
                p.IdArticulo == idArticulo && p.IdListaPrecio == idListaPrecio &&
                p.VigenteDesde <= fecha && (p.VigenteHasta == null || p.VigenteHasta > fecha))
            .OrderByDescending(p => p.VigenteDesde)
            .Select(p => (decimal?)p.Monto)
            .FirstOrDefaultAsync(ct);

    /// <summary>Solo comprueba que el artículo existe y es visible: sin rastreo, para no dejar en el
    /// <c>ChangeTracker</c> una foto previa al lock de una fila cuya pertenencia a la familia este
    /// servicio puede escribir con SQL crudo (<see cref="SacarDeLaFamiliaAsync"/>).</summary>
    private async Task<Articulo> BuscarArticuloAsync(int id, CancellationToken ct) =>
        await db.Articulos.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant — esto
            // solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el artículo {id}.");

    private async Task<ListaPrecio> BuscarListaAsync(int id, CancellationToken ct) =>
        await db.ListasPrecio.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new ErrorDominio("referencia_invalida", $"No existe la lista de precios {id}.", 400);

    /// <summary>Spec: "lista must be fija to store rows (derivada rejected with clear 400)" —
    /// pre-chequeo antes de cualquier escritura en <c>precios</c>.</summary>
    private async Task<ListaPrecio> BuscarListaFijaAsync(int id, CancellationToken ct)
    {
        var lista = await BuscarListaAsync(id, ct);

        if (lista.Modo != ModoLista.Fija)
        {
            throw new ErrorDominio(
                "lista_no_es_fija",
                "Solo se pueden registrar precios propios en listas de modo fija; una derivada se resuelve en lectura.",
                400);
        }

        return lista;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de plataforma
            // nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable en
            // operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDePrecios requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    /// <summary>Columna <c>numeric(14,2)</c> (<c>PrecioConfiguration</c>) — mismo bound que
    /// <c>ServicioDeArticulos.ExigirCostoValido</c> (misma precisión de columna).</summary>
    private static void ExigirPrecioValido(decimal precio)
    {
        if (precio < 0 || precio >= 1_000_000_000_000m)
        {
            throw new ErrorDominio("precio_invalido", "El campo precio debe estar entre 0 y 999999999999.99.", 400);
        }
    }

    private void ExigirVigenteDesdeFuturo(DateTimeOffset vigenteDesde)
    {
        if (vigenteDesde < reloj.Ahora - ToleranciaReloj)
        {
            throw new ErrorDominio(
                "vigente_desde_en_el_pasado",
                "vigente_desde no puede estar en el pasado (tolerancia de desfasaje de reloj de "
                    + $"{ToleranciaReloj.TotalSeconds:0} segundos).",
                400);
        }
    }

    /// <summary>Deriva la clave determinística de <c>pg_advisory_xact_lock(int, int)</c> para el
    /// par <c>(idArticulo, idListaPrecio)</c> de este tenant (judgment-day, item 2). Primer
    /// argumento: <c>idTenant</c> — no hace falta mezclarlo con nada más, cada tenant ocupa su
    /// propio subespacio de claves. Segundo argumento: combinación aritmética simple de
    /// <c>idArticulo</c>/<c>idListaPrecio</c> — DELIBERADAMENTE no <c>HashCode.Combine</c>, que
    /// incorpora una semilla aleatoria por proceso (dos instancias de la app, o la misma tras un
    /// reinicio, calcularían claves DISTINTAS para el MISMO par, y el lock dejaría de
    /// serializarlas entre sí — justo lo opuesto de lo que se busca). Una colisión de la clave 2
    /// entre dos pares DISTINTOS del mismo tenant (pares de listas distintas: dentro de una lista cada
    /// artículo tiene la suya) no compromete la corrección de una escritura sola: dos pares no
    /// relacionados se esperan entre sí sin necesidad, y el estado real siempre se lee de la fila
    /// (<see cref="BuscarFilaAbiertaAsync"/>) DESPUÉS de tomar el lock, nunca del hash en sí. Lo que
    /// una colisión SÍ puede hacer es cruzar el orden de dos escrituras que toman VARIOS pares (dos
    /// familias distintas, cada una en su lista): por eso los pares se toman en orden ascendente de esta
    /// clave (<see cref="OrdenDeLocksDePares"/>) y no de <c>id_articulo</c>. Pública para que las pruebas
    /// puedan sostener, o buscar en <c>pg_locks</c>, el mismo lock del par desde otra conexión.</summary>
    public static (int Clave1, int Clave2) ClaveDeLockDePar(int idTenant, int idArticulo, int idListaPrecio) =>
        (idTenant, unchecked((idArticulo * 397) ^ idListaPrecio));

    /// <summary>Los artículos en el orden en que se toman sus locks de par: ascendente por la CLAVE del
    /// lock (<see cref="ClaveDeLockDePar"/>), no por <c>id_articulo</c>. La identidad de un lock
    /// advisory es su clave, y la de dos pares de listas distintas puede coincidir: dos escrituras de
    /// familias distintas, cada una en su lista, pueden tener las mismas dos claves en orden cruzado
    /// respecto de <c>id_articulo</c>, y cada una esperaría la que la otra ya tiene. Dos escrituras de la
    /// misma familia no llegan a competir por los pares: se esperan antes, en el lock de membresía o en
    /// las filas de los miembros. Con todas las escrituras subiendo por clave, el orden de adquisición es
    /// el mismo para cualquier par de ellas. Todos los objetivos de una escritura comparten tenant y
    /// lista.</summary>
    public static IReadOnlyList<int> OrdenDeLocksDePares(int idTenant, int idListaPrecio, IEnumerable<int> idsArticulo) =>
        [.. idsArticulo.OrderBy(idArticulo => ClaveDeLockDePar(idTenant, idArticulo, idListaPrecio))];

    /// <summary><c>pg_advisory_xact_lock</c> con alcance de TRANSACCIÓN (se libera solo al
    /// COMMIT/ROLLBACK) tomado ANTES de leer nada de precios (judgment-day, item 2) — a diferencia del
    /// viejo <c>SELECT ... FOR UPDATE</c> sobre la fila mutable (que no existía para lockear
    /// cuando el par no tenía ninguna fila abierta todavía), esto serializa CUALQUIER escritura
    /// concurrente sobre el mismo par, exista o no una fila abierta: el segundo llamador espera
    /// acá hasta que el primero comitee o revierta, y recién ahí lee el estado ACTUAL — la
    /// semántica de "esperar y actuar sobre el estado actual" que el doc-comment de
    /// <see cref="AbrirNuevoPrecioAsync"/> promete. Va después del lock de membresía y de resolver
    /// los objetivos, y se toma una vez por objetivo en el orden de <see cref="OrdenDeLocksDePares"/>.</summary>
    private async Task TomarLockDelParAsync(int idTenant, int idArticulo, int idListaPrecio, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var (clave1, clave2) = ClaveDeLockDePar(idTenant, idArticulo, idListaPrecio);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        ParametrosDeComando.Agregar(comando, clave1);
        ParametrosDeComando.Agregar(comando, clave2);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>La familia del artículo (<c>null</c> ⇒ no es miembro) leída DENTRO de la transacción
    /// y DESPUÉS del lock de membresía — la única lectura de la pertenencia de esta operación
    /// (<c>single-read-under-lock</c>). Una proyección escalar con <c>deleted_at IS NULL</c>, nunca
    /// una entidad rastreada: la salida de la familia es un <c>UPDATE</c> crudo
    /// (<see cref="SacarDeLaFamiliaAsync"/>) sobre una fila que se bloqueó guardada por este valor
    /// (<see cref="BloquearFilaDelArticuloAsync"/>). Si la fila ya no existe (la dieron de baja entre
    /// el pre-chequeo y el lock) es el mismo 404 que el pre-chequeo.</summary>
    private async Task<int?> LeerIdFamiliaAsync(int idArticulo, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_familia FROM articulos WHERE id_articulo = $1 AND id_tenant = $2 AND deleted_at IS NULL";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        if (!await lector.ReadAsync(ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el artículo {idArticulo}.");
        }

        return lector.IsDBNull(0) ? null : lector.GetInt32(0);
    }

    /// <summary>Los miembros vivos de la familia, ascendentes por <c>id_articulo</c>, con sus filas de
    /// <c>articulos</c> bloqueadas <c>FOR NO KEY UPDATE</c> — paso (2) del orden global de locks. Un
    /// solo statement con <c>ORDER BY</c>: PostgreSQL toma los locks de fila en el orden del sort, que
    /// es el orden que evita el ciclo entre dos escritores de la misma familia. Nunca <c>FOR UPDATE</c>
    /// sobre varias filas: choca con el <c>FOR KEY SHARE</c> que toman las ventas por sus FK y puede
    /// formar un deadlock. Si una fila cambió mientras se esperaba su lock, PostgreSQL reevalúa el
    /// <c>WHERE</c> sobre la versión nueva y la descarta si ya no es miembro vivo.</summary>
    private async Task<List<int>> BloquearMiembrosAsync(int idFamilia, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_articulo FROM articulos " +
            "WHERE id_familia = $1 AND id_tenant = $2 AND deleted_at IS NULL " +
            "ORDER BY id_articulo FOR NO KEY UPDATE";

        ParametrosDeComando.Agregar(comando, idFamilia);
        ParametrosDeComando.Agregar(comando, idTenant);

        var miembros = new List<int>();
        await using var lector = await comando.ExecuteReaderAsync(ct);

        while (await lector.ReadAsync(ct))
        {
            miembros.Add(lector.GetInt32(0));
        }

        return miembros;
    }

    /// <summary>"Solo este", paso (2) del orden de locks: bloquea la fila del artículo
    /// <c>FOR NO KEY UPDATE</c> —el mismo modo que <see cref="BloquearMiembrosAsync"/>— guardada por la
    /// familia que se leyó bajo el lock de membresía y por la baja lógica. La salida de la familia se
    /// escribe después, con el "ahora" de la operación (<see cref="SacarDeLaFamiliaAsync"/>). Si la fila
    /// no cumple el <c>WHERE</c> —otro escritor cambió su pertenencia o la dio de baja sin respetar el
    /// lock de membresía, también mientras se esperaba el lock de la fila— se rechaza como
    /// <c>familia_cambio</c>.</summary>
    private async Task BloquearFilaDelArticuloAsync(int idArticulo, int idFamilia, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT 1 FROM articulos " +
            "WHERE id_articulo = $1 AND id_tenant = $2 AND id_familia = $3 AND deleted_at IS NULL " +
            "FOR NO KEY UPDATE";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idFamilia);

        if (await comando.ExecuteScalarAsync(ct) is null)
        {
            throw ErrorDeFamiliaCambio();
        }
    }

    /// <summary>"Solo este": el artículo sale de la familia, con el "ahora" de la operación (FASE 2,
    /// después de resolverlo). <c>UPDATE</c> crudo por clave sobre la fila que
    /// <see cref="BloquearFilaDelArticuloAsync"/> ya dejó bloqueada, viva y en esa familia.</summary>
    private async Task SacarDeLaFamiliaAsync(int idArticulo, int idTenant, DateTimeOffset ahora, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        // Sin id_familia ni deleted_at: la fila ya quedó bloqueada y verificada en la fase de filas.
        comando.CommandText =
            "UPDATE articulos SET id_familia = NULL, updated_at = $1 WHERE id_articulo = $2 AND id_tenant = $3";

        ParametrosDeComando.Agregar(comando, ahora);
        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>409 <c>alcance_requerido</c>: el artículo es miembro y el cliente no eligió. El mensaje
    /// nombra la familia y la cantidad de artículos vivos (ErrorDominio no lleva datos estructurados);
    /// se lee acá, solo en el camino de error, bajo el mismo lock de membresía.</summary>
    private async Task<ErrorDominio> ErrorDeAlcanceRequeridoAsync(int idFamilia, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT f.nombre, " +
            "(SELECT count(*) FROM articulos a WHERE a.id_familia = f.id_familia AND a.id_tenant = f.id_tenant " +
            "AND a.deleted_at IS NULL) " +
            "FROM familias f WHERE f.id_familia = $1 AND f.id_tenant = $2";

        ParametrosDeComando.Agregar(comando, idFamilia);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        // La FK compuesta de articulos garantiza que la familia de un miembro existe: sin fila,
        // GetString falla fuerte en vez de inventar un nombre.
        await lector.ReadAsync(ct);
        var nombre = lector.GetString(0);
        var cantidad = lector.GetInt64(1);
        var articulos = cantidad == 1 ? "1 artículo" : $"{cantidad} artículos";

        return ErrorDominio.Conflicto(
            "alcance_requerido",
            $"El artículo pertenece a la familia \"{nombre}\" ({articulos}): el cambio de precio tiene que indicar " +
            "si se aplica a toda la familia o solo a este artículo.");
    }

    /// <summary>409 <c>familia_cambio</c>: la pertenencia que el cliente daba por cierta ya no lo es
    /// (eligió un alcance sobre un artículo que dejó de ser miembro, o que dejó de existir mientras
    /// esta escritura esperaba el lock de su fila).</summary>
    private static ErrorDominio ErrorDeFamiliaCambio() =>
        ErrorDominio.Conflicto(
            "familia_cambio",
            "La pertenencia del artículo a su familia cambió desde que se cargó la pantalla; hay que recargar y volver a intentar.");

    /// <summary>SELECT plano (sin <c>FOR UPDATE</c>) vía ADO.NET crudo sobre la
    /// conexión/transacción activa del <see cref="IWaysDbContext"/> inyectado — mismo criterio
    /// de "nunca <c>FromSqlRaw&lt;T&gt;()</c>" que <c>AsignadorDeCodigoInternoArticulo</c>/
    /// <c>AsignadorDeNumeroCliente</c>. Seguro sin lock de fila propio porque
    /// <see cref="TomarLockDelParAsync"/> ya se tomó ANTES en la misma transacción — ningún otro
    /// escritor puede estar tocando este par en simultáneo. <c>id_tenant</c> se filtra
    /// explícitamente (defensa en profundidad) aunque RLS ya lo garantiza — mismo criterio
    /// dual-capa que el resto del código de escritura.</summary>
    private async Task<FilaVigente?> BuscarFilaAbiertaAsync(
        int idArticulo, int idListaPrecio, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            // Task 2.2 (design call site 1) — la columna `precio` (mapeo de `Precio.Monto`,
            // PrecioConfiguration.cs:56 — NO se llama "monto" en la base) sumada a la
            // proyección: una columna más en un SELECT que YA corre bajo el advisory lock, cero
            // round trips nuevos — es el before-image de `precio.cambio`
            // (PayloadDeAuditoria.CambioDePrecio, cuya clave jsonb SÍ es "monto").
            "SELECT id_precio, vigente_desde, precio FROM precios " +
            "WHERE id_articulo = $1 AND id_lista_precio = $2 AND id_tenant = $3 " +
            "AND vigente_hasta IS NULL AND deleted_at IS NULL";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idListaPrecio);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new FilaVigente(lector.GetInt32(0), lector.GetFieldValue<DateTimeOffset>(1), lector.GetDecimal(2));
    }

    /// <summary>(judgment-day, item 1; ronda 2, item 1) Localiza el PREDECESOR de una fila
    /// pendiente que está por reemplazarse — la fila cuyo <c>vigente_hasta</c> coincide EXACTO
    /// con <paramref name="limiteOriginal"/> (el <c>vigente_desde</c> original de la pendiente,
    /// antes del reemplazo). Devuelve <c>null</c> si no hay predecesor (la pendiente reemplazada
    /// era el primer precio del par) — nada que validar ni re-cerrar.
    ///
    /// Solo BUSCA: <see cref="PlanificarPrecioDeUnArticuloAsync"/> valida el límite nuevo contra
    /// <see cref="FilaVigente.VigenteDesde"/> en la fase 1, ANTES de que ninguna fila se cierre
    /// (ronda 2, item 1) — separar la búsqueda del cierre (<see cref="CerrarFilasDelPlanAsync"/>) es
    /// lo que permite ese orden: sin esto, el cierre del predecesor ocurría a ciegas, sin chance de
    /// rechazar un límite inválido antes de escribir.
    ///
    /// <paramref name="idFilaPendienteCerrada"/> se EXCLUYE explícitamente de la búsqueda —
    /// cuando el reemplazo cierra la pendiente en su ventana muerta (<c>vigente_hasta ==
    /// vigente_desde == limiteOriginal</c>, ver <see cref="CerrarFilasDelPlanAsync"/>), esa MISMA fila también matchea
    /// <c>vigente_hasta = limiteOriginal</c>. Sin esta exclusión, la pendiente recién cerrada
    /// aparecería como su propio predecesor — el bug encontrado corriendo el caso "primer precio
    /// del par es directamente un programado, sin predecesor real".
    ///
    /// (judgment-day ronda 3, item 1) DOS defensas más, necesarias porque un reemplazo con la
    /// MISMA fecha ("corregir el importe manteniendo la fecha", ver
    /// <see cref="PlanificarPrecioDeUnArticuloAsync"/>) deja una fila
    /// MUERTA (<c>vigente_desde == vigente_hasta</c>) que comparte el mismo límite que el
    /// predecesor REAL cuando ese reemplazo mismo-fecha, a su vez, se vuelve a reemplazar con una
    /// fecha nueva: <c>vigente_hasta = limiteOriginal</c> por sí solo es AMBIGUO entre la fila
    /// muerta y el predecesor real, y Postgres no garantiza cuál de las dos filas devuelve. Si
    /// devuelve la muerta, el cierre subsiguiente la REABRE (le pisa el <c>vigente_hasta</c>),
    /// resucitando un precio que el usuario ya había reemplazado — invisible en los tests que no
    /// prueban el camino "reemplazo mismo-fecha seguido de un reemplazo con fecha distinta".
    /// <c>vigente_desde &lt;&gt; vigente_hasta</c> excluye toda fila muerta (nunca es el
    /// predecesor real, que siempre tiene una ventana con contenido); <c>ORDER BY vigente_desde
    /// ASC LIMIT 1</c> hace el resultado determinístico incluso si llegara a haber más de una
    /// fila con contenido compartiendo el límite — el predecesor real siempre es el de menor
    /// <c>vigente_desde</c>.</summary>
    private async Task<FilaVigente?> BuscarPredecesorAsync(
        int idArticulo, int idListaPrecio, int idTenant, int idFilaPendienteCerrada, DateTimeOffset limiteOriginal,
        CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_precio, vigente_desde FROM precios " +
            "WHERE id_articulo = $1 AND id_lista_precio = $2 AND id_tenant = $3 " +
            "AND vigente_hasta = $4 AND id_precio != $5 AND deleted_at IS NULL " +
            "AND vigente_desde <> vigente_hasta " +
            "ORDER BY vigente_desde ASC LIMIT 1";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idListaPrecio);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, limiteOriginal);
        ParametrosDeComando.Agregar(comando, idFilaPendienteCerrada);

        await using var lector = await comando.ExecuteReaderAsync(ct);

        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        // El predecesor solo se usa para re-cerrar su ventana (CerrarFilaAsync) — nunca alimenta
        // un payload de auditoría, así que Monto no se proyecta acá (task 2.2, design call site 1).
        return new FilaVigente(lector.GetInt32(0), lector.GetFieldValue<DateTimeOffset>(1), Monto: 0m);
    }

    private async Task CerrarFilaAsync(int idPrecio, DateTimeOffset vigenteHasta, DateTimeOffset ahora, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "UPDATE precios SET vigente_hasta = $1, updated_at = $2 WHERE id_precio = $3";

        ParametrosDeComando.Agregar(comando, vigenteHasta);
        ParametrosDeComando.Agregar(comando, ahora);
        ParametrosDeComando.Agregar(comando, idPrecio);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary><c>Monto</c> es el before-image de <c>precio.cambio</c> (task 2.2) — solo
    /// significativo cuando la fila viene de <see cref="BuscarFilaAbiertaAsync"/>; el predecesor
    /// (<see cref="BuscarPredecesorAsync"/>) nunca lo lee.</summary>
    private readonly record struct FilaVigente(int Id, DateTimeOffset VigenteDesde, decimal Monto);

    /// <summary>Lo que resolvió la lectura de pertenencia bajo el lock de membresía: los artículos que
    /// hay que escribir, ascendentes por <c>id_articulo</c>, y —solo en "solo este"— la familia de la
    /// que sale el artículo pedido.</summary>
    private readonly record struct ObjetivosDeLaEscritura(IReadOnlyList<int> Ids, int? IdFamiliaDeLaQueSale);

    /// <summary>Lo que la fase 1 leyó y validó de UN objetivo y la fase 2 necesita para escribirlo: la
    /// fila abierta (<c>null</c> si el par todavía no tiene precio), si era pendiente y, entonces, su
    /// predecesor.</summary>
    private readonly record struct PlanDeUnArticulo(
        int IdArticulo, FilaVigente? FilaAbierta, bool EsPendiente, FilaVigente? Predecesor);
}
