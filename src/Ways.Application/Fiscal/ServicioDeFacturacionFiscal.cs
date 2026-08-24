using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Application.Fiscal;

/// <summary>
/// El orquestador end-to-end de la emisión fiscal (tasks.md Slice 5, targets 64-76) — el ÚNICO
/// escritor de <c>comprobantes_venta</c>/<c>items_comprobante_venta</c> en el camino fiscal (D12/T1:
/// comprobante + items ÚNICAMENTE, CERO stock/pagos/cuenta-corriente/turno) y el ÚNICO orquestador
/// válido de "WSFE <c>600</c> ⇒ invalidar el TA + reintentar UNA vez" (nota vinculante de la slice
/// 3: <see cref="Infrastructure.Fiscal.ClienteWsfe"/> solo detecta/clasifica el <c>600</c>, no puede
/// invalidar/renovar un <see cref="TicketDeAcceso"/> que no posee). El SEGUNDO <c>600</c> consecutivo
/// (con un TA recién firmado) es DEFINITIVO — jamás un loop de refirmado.
///
/// <b>BINDING WARNING — T1 reasertada (design.md D12)</b>: esta clase NUNCA escribe
/// <c>movimientos_stock</c>/<c>pagos_comprobante</c>/<c>movimientos_cuenta_corriente</c> ni exige un
/// turno abierto — seguro ÚNICAMENTE porque I4 (sin certificado activo, CERO bytes en el cable) hace
/// ese camino inalcanzable en producción hoy. El target 75 (zero-rows) es el trip-wire DOCUMENTADO
/// que 19c debe poner en rojo cuando agregue esos tres loops.
///
/// <b>El guard del POS se angosta, jamás se remueve (decisión 9)</b>: esta clase es un escritor
/// PROPIO, con su propio endpoint (<see cref="Api.Endpoints.FiscalEndpoints"/>) —
/// <c>ServicioDeVentas.cs</c> queda BYTE-IDÉNTICO en toda la sub-etapa (target 73).
/// </summary>
public class ServicioDeFacturacionFiscal(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    IOptions<OpcionesFiscales> opciones,
    IClienteWsaa clienteWsaa,
    IClienteWsfe clienteWsfe,
    IRepositorioDeTicketDeAcceso repositorioDeTicket,
    IAlmacenDeClavesFiscales almacen)
{
    private const string ServicioWsfe = "wsfe";

    // --- Emisión (I4: los cinco gates, TODOS antes de resolver ningún puerto) ---

    public async Task<ComprobanteFiscalEmitido> EmitirAsync(
        SolicitudDeEmisionFiscal solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenant();
        var ambiente = ResolverAmbiente();

        // ---- Lecturas puras — CERO red, CERO transacción todavía (D10) ----
        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == solicitud.IdPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {solicitud.IdPuntoVenta}.");

        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == puntoVenta.IdEmpresa, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {puntoVenta.IdEmpresa}.");

        var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == solicitud.IdCliente, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {solicitud.IdCliente}.");

        var tipo = await db.TiposComprobante.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Codigo == solicitud.CodigoTipoComprobante, ct);

        // Gate 1 — empresa_sin_condicion_fiscal (proposal.md §B: NULLABLE a propósito, sin default).
        if (empresa.IdCondicionFiscal is not { } idCondicionFiscalEmisor)
        {
            throw ErrorDominio.Conflicto(
                "empresa_sin_condicion_fiscal", "La empresa no tiene cargada su condición fiscal ARCA.");
        }

        // Gate 2 — punto_venta_sin_numero_fiscal.
        if (puntoVenta.NumeroFiscal is not { } numeroFiscalDePuntoVenta)
        {
            throw ErrorDominio.Conflicto(
                "punto_venta_sin_numero_fiscal", "El punto de venta no tiene cargado su número fiscal ARCA.");
        }

        // Gate 3 — tipo_fiscal_invalido (D9: ResolverTipoFiscalAsync, espejo del resolver del POS —
        // exige EsFiscal en vez de !EsFiscal, NUNCA lee AfectaStock, nunca toca ServicioDeVentas).
        var tipoFiscal = ResolverTipoFiscal(tipo, solicitud.CodigoTipoComprobante);

        // Lecturas auxiliares para el gate 4 y para la resolución de letra (task 5.4) — todavía
        // CERO red.
        var condicionEmisor = await db.CondicionesFiscales.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == idCondicionFiscalEmisor, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la condición fiscal {idCondicionFiscalEmisor}.");

        var condicionReceptor = await db.CondicionesFiscales.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cliente.IdCondicionFiscal, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la condición fiscal {cliente.IdCondicionFiscal}.");

        // Gate 4 — condicion_fiscal_receptor_no_mapeada. OBLIGACIÓN VINCULANTE (nota del slice 1,
        // InicializadorDeBaseDeDatos.cs CondicionesFiscalesBase): NO_RESP tiene CodigoAfip = 15
        // SEMBRADO (RG 5616 "IVA No Alcanzado", un mapeo PROVISORIO, decisión 11) — NO null. El
        // rechazo decide por Codigo, JAMÁS por CodigoAfip (chequear nullity acá sería un 15 falso
        // positivo de "sí está mapeado" — el 15 se confirma recién en 19b contra
        // FEParamGetCondicionIvaReceptor).
        if (condicionReceptor.Codigo == "NO_RESP")
        {
            throw ErrorDominio.Conflicto(
                "condicion_fiscal_receptor_no_mapeada",
                "La condición fiscal del receptor (NO_RESP) todavía no tiene mapeo ARCA confirmado — " +
                "19b la confirma contra FEParamGetCondicionIvaReceptor.");
        }

        // Defensa en profundidad — ningún código sembrado hoy (RI/EXENTO/CF/MONOTRIBUTO) llega
        // acá sin CodigoAfip, pero un código de catálogo futuro sin mapeo cae en el mismo 409 en
        // vez de adivinar (mismo criterio que D11 en ComposicionDeTotalesFiscales).
        if (condicionReceptor.CodigoAfip is not { } condicionIvaReceptorId)
        {
            throw ErrorDominio.Conflicto(
                "condicion_fiscal_receptor_no_mapeada",
                $"La condición fiscal del receptor ('{condicionReceptor.Codigo}') no tiene mapeo ARCA.");
        }

        // Gate 5 — certificado_fiscal_ausente. VA ÚLTIMO A PROPÓSITO (D10): con el primero, un local
        // que nunca cargó su condición fiscal se enteraría de "subí un certificado" que ya tiene.
        var hayCertificadoActivo = await db.CertificadosFiscales.AsNoTracking()
            .AnyAsync(c => c.IdEmpresa == empresa.Id && c.Ambiente == ambiente && c.Activo, ct);
        if (!hayCertificadoActivo)
        {
            throw ErrorDominio.Conflicto(
                "certificado_fiscal_ausente", "No hay un certificado fiscal activo para esta empresa y ambiente.");
        }

        // Defensa en profundidad — no uno de los cinco gates nombrados por D10, pero necesaria para
        // que Auth/Cuit (WSFE) y el campo `cuit` del QR (RG 4291) tengan un valor: CERO red todavía.
        var cuitEmisor = ExigirCuitNumerico(empresa);

        // ---- Fin de los gates — recién ACÁ se resuelve la letra (D9 data flow: "SU PRIMER
        // CALLER") y se componen los totales; ambos puros, todavía CERO red. ----
        var letra = ResolvedorDeLetraComprobante.Resolver(
            MapearCondicionFiscal(condicionEmisor.Codigo), MapearCondicionFiscal(condicionReceptor.Codigo));

        // Gate D10 (pre-transacción, todavía CERO red) — judgment 19a-slice-5 ronda 1 juez B, MAJOR:
        // la letra del catálogo del tipo fiscal solicitado (`tipoFiscal.Letra`) tenía que coincidir
        // con la letra que el CRUCE de condiciones fiscales resuelve — sin este gate, una `FA`
        // (letra 'A') contra un receptor Consumidor Final (letra 'B' resuelta) emitía 201 con una
        // letra que ARCA jamás aceptaría en producción real. Corrige la Deviation 2 registrada al
        // cierre de esta slice (que subestimaba el defecto como "candidato de hardening futuro").
        // judgment 19a-slice-5 ronda 2 juez A — SUGGESTION: tipoFiscal.Letra es char? — un catálogo
        // sin letra cargada compara null != letra y cae en este mismo 409 (falla cerrado), nunca en
        // una NullReferenceException.
        if (tipoFiscal.Letra != letra)
        {
            throw ErrorDominio.Conflicto(
                "tipo_fiscal_letra_no_coincide",
                $"El tipo de comprobante '{tipoFiscal.Codigo}' es letra '{tipoFiscal.Letra}', pero la letra " +
                $"resuelta para este emisor/receptor es '{letra}'.");
        }

        var (lineasFiscales, itemsCalculados, subtotal, descuentoTotal) =
            await ComponerLineasAsync(solicitud.Lineas, ct);
        var totales = ComposicionDeTotalesFiscales.Componer(lineasFiscales);

        var momento = reloj.Ahora;
        var claveDeSerie = new ClaveDeSerie(numeroFiscalDePuntoVenta, tipoFiscal.CodigoAfip!.Value);
        var (tipoDocReceptor, nroDocReceptor) = MapearDocumentoArca(cliente);

        // ---- LA transacción (D1: numeraciones_fiscales, posición 0, ÚNICO lock existente que
        // toma esta transacción — reintento AUTOMÁTICO expresamente descartado, ver el doc-comment
        // de FabricaDeEstrategiaSinReintento: acá un reintento de la lambda completa quemaría un
        // número nuevo cada vez, exactamente la disciplina que decisión 13 existe para rechazar). ----
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await AsignadorDeNumeroFiscal.AsegurarContadorAsync(
                db, idTenant, puntoVenta.Id, tipoFiscal.CodigoAfip.Value, ct);
            var numero = await AsignadorDeNumeroFiscal.AsignarSiguienteAsync(
                db, puntoVenta.Id, tipoFiscal.CodigoAfip.Value, ct);

            var comprobante = new ComprobanteVenta
            {
                IdTipoComprobante = tipoFiscal.Id,
                Numero = numero,
                Fecha = momento,
                IdPuntoVenta = puntoVenta.Id,
                IdTurnoCaja = null,
                // design decisión 11 (mismo criterio que ServicioDeVentas.EmitirAsync): SIEMPRE el
                // actor autenticado — fk_comprobantes_venta_empleado exige una fila real, jamás 0.
                IdEmpleado = contexto.UsuarioId,
                IdCliente = cliente.Id,
                Subtotal = subtotal,
                DescuentoTotal = descuentoTotal,
                Total = totales.ImpTotal,
                NetoGravado = totales.ImpNeto,
                IvaTotal = totales.ImpIVA,
                Observaciones = solicitud.Observaciones,
                Estado = EstadoComprobante.Emitido,
                ResultadoFiscal = ResultadoFiscal.Pendiente,
                CreatedAt = momento,
                UpdatedAt = momento
            };
            db.ComprobantesVenta.Add(comprobante);
            await db.SaveChangesAsync(ct);

            var orden = 1;
            foreach (var itemCalculado in itemsCalculados)
            {
                db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
                {
                    IdComprobanteVenta = comprobante.Id,
                    Orden = orden++,
                    IdArticulo = itemCalculado.Linea.IdArticulo,
                    Descripcion = itemCalculado.Linea.Descripcion,
                    IdArea = itemCalculado.Linea.IdArea,
                    IdListaPrecio = itemCalculado.Linea.IdListaPrecio,
                    IdAlicuotaIva = itemCalculado.Linea.IdAlicuotaIva,
                    PorcentajeIva = itemCalculado.PorcentajeIva,
                    Cantidad = itemCalculado.Calculado.Cantidad,
                    PrecioUnitario = itemCalculado.Calculado.PrecioUnitario,
                    Descuento = itemCalculado.Calculado.Descuento,
                    Total = itemCalculado.Calculado.Total,
                    CreatedAt = momento,
                    UpdatedAt = momento
                });
            }
            await db.SaveChangesAsync(ct);

            // Concepto = 1 (productos) — el único camino que 19a construye (Contratos.cs:
            // FchServDesde/Hasta/FchVtoPago solo aplican a 2/3, quedan en null ⇒ omitidos, target 39).
            const int conceptoProductos = 1;
            var solicitudDeCae = new SolicitudDeCae(
                claveDeSerie, numero, numero, conceptoProductos, tipoDocReceptor, nroDocReceptor,
                DateOnly.FromDateTime(momento.Date), totales.ImpTotal, totales.ImpTotConc, totales.ImpNeto,
                totales.ImpOpEx, totales.ImpTrib, totales.ImpIVA, condicionIvaReceptorId, totales.Iva);

            var respuesta = await SolicitarCaeConReintentoDeTicketAsync(
                empresa, ambiente, cuitEmisor, comprobante.Id, numero, solicitudDeCae, ct);

            var filasAfectadas = await AplicarResultadoGuardadoAsync(idTenant, comprobante.Id, respuesta, ct);
            if (filasAfectadas == 0)
            {
                // Nunca debería pasar: la fila la insertó ESTA MISMA transacción dos statements
                // atrás, todavía 'pendiente' — defensa en profundidad, no un camino ejercitable.
                throw new InvalidOperationException(
                    $"La actualización guardada (U2) no afectó ninguna fila para el comprobante {comprobante.Id}.");
            }

            if (MaquinaDeEstadosCae.EsTerminal(respuesta.Resultado))
            {
                // Paso 7 del data flow: SOLO en aprobación — EsTerminal ya excluye 'rechazado' (I3,
                // solo las dos aprobaciones son terminales); D13: la reconciliación nunca escribe
                // proximo_numero, solo estos dos campos, y jamás en un rechazo (nada que sincronizar).
                await AsignadorDeNumeroFiscal.ReconciliarAsync(
                    db, puntoVenta.Id, tipoFiscal.CodigoAfip.Value, numero, reloj, ct);
            }

            await transaccion.CommitAsync(ct);

            var payloadQr = ConstruirQrSiCorresponde(
                respuesta, momento, cuitEmisor, numeroFiscalDePuntoVenta, tipoFiscal.CodigoAfip.Value, numero,
                totales.ImpTotal, tipoDocReceptor, nroDocReceptor);

            return new ComprobanteFiscalEmitido(
                comprobante.Id, tipoFiscal.Codigo, letra, puntoVenta.Id, numero, DateOnly.FromDateTime(momento.Date),
                respuesta.Resultado, respuesta.Cae, respuesta.CaeVencimiento, payloadQr);
        });
    }

    // --- Reintento (I2: FECompConsultar SIEMPRE antes de cualquier reintento no definitivo) ---

    public async Task<ComprobanteFiscalEmitido> ReintentarAsync(int idComprobante, CancellationToken ct = default)
    {
        var idTenant = ExigirTenant();
        var ambiente = ResolverAmbiente();

        var comprobante = await db.ComprobantesVenta
            .FirstOrDefaultAsync(c => c.Id == idComprobante && c.ResultadoFiscal == ResultadoFiscal.Pendiente, ct)
            ?? throw ErrorDominio.NoEncontrado(
                $"No existe un comprobante fiscal pendiente con id {idComprobante} — un comprobante terminal " +
                "(I3) nunca vuelve a entrar a FECAESolicitar.");

        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == comprobante.IdPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {comprobante.IdPuntoVenta}.");

        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == puntoVenta.IdEmpresa, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {puntoVenta.IdEmpresa}.");

        var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == comprobante.IdCliente, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {comprobante.IdCliente}.");

        var tipoFiscal = await db.TiposComprobante.AsNoTracking()
            .FirstAsync(t => t.Id == comprobante.IdTipoComprobante, ct);

        var condicionEmisor = await db.CondicionesFiscales.AsNoTracking()
            .FirstAsync(c => c.Id == empresa.IdCondicionFiscal, ct);

        var condicionReceptor = await db.CondicionesFiscales.AsNoTracking()
            .FirstAsync(c => c.Id == cliente.IdCondicionFiscal, ct);

        var letra = ResolvedorDeLetraComprobante.Resolver(
            MapearCondicionFiscal(condicionEmisor.Codigo), MapearCondicionFiscal(condicionReceptor.Codigo));

        var cuitEmisor = ExigirCuitNumerico(empresa);
        var claveDeSerie = new ClaveDeSerie(puntoVenta.NumeroFiscal!.Value, tipoFiscal.CodigoAfip!.Value);
        var (tipoDocReceptor, nroDocReceptor) = MapearDocumentoArca(cliente);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // I2 (D4): un reintento SIEMPRE parte de EstadoDeIntento.NoDefinitivo — llegar hasta acá
            // significa que ya existe un intento previo (la fila está 'pendiente'), así que la
            // decisión de la máquina es SIEMPRE ConsultarPrimero (nunca EmitirDirecto). Chequeo en
            // runtime, no un Debug.Assert que el build Release descarta (db-error-backstops: fallar
            // fuerte, no silencioso).
            if (MaquinaDeEstadosCae.Decidir(EstadoDeIntento.NoDefinitivo) != DecisionDeReintento.ConsultarPrimero)
            {
                throw new InvalidOperationException("Estado de máquina inesperado en el camino de reintento (I2).");
            }

            var ticket = await ObtenerTicketAsync(empresa, ambiente, ct);
            var consulta = await clienteWsfe.ConsultarAsync(ticket, cuitEmisor, claveDeSerie, comprobante.Numero, ct);

            RespuestaCae respuesta;
            if (consulta.Encontrado)
            {
                // Adopción (I2): CERO FECAESolicitar — el CAE ya existe en ARCA, se adopta tal cual.
                respuesta = new RespuestaCae(
                    consulta.Resultado!.Value, consulta.Cae, consulta.CaeVencimiento, [], []);
            }
            else
            {
                var condicionIvaReceptorId = condicionReceptor.CodigoAfip
                    ?? throw ErrorDominio.Conflicto(
                        "condicion_fiscal_receptor_no_mapeada",
                        $"La condición fiscal del receptor ('{condicionReceptor.Codigo}') no tiene mapeo ARCA.");

                // judgment 19a-slice-5 ronda 2 juez A — CRITICAL: NUNCA ceros fabricados. Recompone
                // el desglose COMPLETO desde el snapshot congelado de items_comprobante_venta —
                // idéntico criterio que la emisión (ComponerLineasAsync + ComposicionDeTotalesFiscales),
                // así el invariante ImpTotal = ImpNeto+ImpIVA+ImpOpEx+ImpTotConc+ImpTrib se sostiene
                // también con líneas Exento/No-gravado.
                var lineasFiscales = await ComponerLineasFiscalesDesdeItemsAsync(comprobante.Id, ct);
                var totales = ComposicionDeTotalesFiscales.Componer(lineasFiscales);

                const int conceptoProductos = 1;
                var solicitudDeCae = new SolicitudDeCae(
                    claveDeSerie, comprobante.Numero, comprobante.Numero, conceptoProductos, tipoDocReceptor,
                    nroDocReceptor, DateOnly.FromDateTime(comprobante.Fecha.Date), totales.ImpTotal,
                    totales.ImpTotConc, totales.ImpNeto, totales.ImpOpEx, totales.ImpTrib, totales.ImpIVA,
                    condicionIvaReceptorId, totales.Iva);

                respuesta = await SolicitarCaeConReintentoDeTicketAsync(
                    empresa, ambiente, cuitEmisor, comprobante.Id, comprobante.Numero, solicitudDeCae, ct);
            }

            var filasAfectadas = await AplicarResultadoGuardadoAsync(idTenant, comprobante.Id, respuesta, ct);
            if (filasAfectadas == 0)
            {
                // U2 conjunct (c) — el TOCTOU real: otro reintento (o la emisión original) ya
                // resolvió esta fila entre la lectura de arriba y este UPDATE. El perdedor de la
                // carrera relee el estado definitivo en vez de pisar nada.
                await transaccion.RollbackAsync(ct);
                var actual = await db.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idComprobante, ct);
                return Proyectar(actual, tipoFiscal.Codigo, letra, cuitEmisor, puntoVenta.NumeroFiscal!.Value,
                    tipoFiscal.CodigoAfip!.Value, tipoDocReceptor, nroDocReceptor);
            }

            if (MaquinaDeEstadosCae.EsTerminal(respuesta.Resultado))
            {
                await AsignadorDeNumeroFiscal.ReconciliarAsync(
                    db, puntoVenta.Id, tipoFiscal.CodigoAfip.Value, comprobante.Numero, reloj, ct);
            }

            await transaccion.CommitAsync(ct);

            var payloadQr = ConstruirQrSiCorresponde(
                respuesta, comprobante.Fecha, cuitEmisor, puntoVenta.NumeroFiscal!.Value, tipoFiscal.CodigoAfip.Value,
                comprobante.Numero, comprobante.Total, tipoDocReceptor, nroDocReceptor);

            return new ComprobanteFiscalEmitido(
                comprobante.Id, tipoFiscal.Codigo, letra, puntoVenta.Id, comprobante.Numero,
                DateOnly.FromDateTime(comprobante.Fecha.Date), respuesta.Resultado, respuesta.Cae,
                respuesta.CaeVencimiento, payloadQr);
        });
    }

    // --- El "600 ⇒ invalidar + reintentar UNA vez, el segundo es definitivo" (nota vinculante) ---

    private async Task<RespuestaCae> SolicitarCaeConReintentoDeTicketAsync(
        Empresa empresa, AmbienteFiscal ambiente, string cuitEmisor, int idComprobante, long numero,
        SolicitudDeCae solicitudDeCae, CancellationToken ct)
    {
        var claveDeTicket = new ClaveDeTicket(empresa.Id, ambiente, ServicioWsfe);
        var ticket = await ObtenerTicketAsync(empresa, ambiente, ct);
        var permiso = MaquinaDeEstadosCae.AutorizarSolicitud(idComprobante, numero);
        ExigirPermisoConsistenteConLaSolicitud(permiso, solicitudDeCae, idComprobante);

        try
        {
            return await clienteWsfe.SolicitarCaeAsync(ticket, cuitEmisor, permiso, solicitudDeCae, ct);
        }
        catch (ErrorDominio primerIntento) when (primerIntento.Codigo == "ticket_de_acceso_invalido")
        {
            // WSFE 600: el TA usado ya no sirve — se descarta vía el puerto (InvalidarAsync, judgment
            // 19a-slice-5 ronda 2 juez A — WARNING) y el fresco se pide VÍA ObtenerOFirmarAsync (el
            // single-flight elevado en esta misma slice), nunca firmando directo por fuera del
            // cerrojo: dos emisiones concurrentes que pisan el mismo TA inválido comparten UNA sola
            // re-firma en vez de dispararla cada una por su cuenta.
            await repositorioDeTicket.InvalidarAsync(claveDeTicket, ct);
            var ticketFresco = await repositorioDeTicket.ObtenerOFirmarAsync(
                claveDeTicket, token => FirmarTicketNuevoAsync(empresa, ambiente, token), ct);

            var permisoDelReintento = MaquinaDeEstadosCae.AutorizarSolicitud(idComprobante, numero);
            ExigirPermisoConsistenteConLaSolicitud(permisoDelReintento, solicitudDeCae, idComprobante);

            // El SEGUNDO 600 consecutivo (con un TA recién firmado) es DEFINITIVO — se deja
            // propagar tal cual, JAMÁS otro reintento (nota vinculante del header de la slice 5).
            return await clienteWsfe.SolicitarCaeAsync(ticketFresco, cuitEmisor, permisoDelReintento, solicitudDeCae, ct);
        }
    }

    /// <summary>Cross-check runtime del permiso (obligación acumulada de la slice 5, judgment
    /// 19a-slice-3 ronda 2 juez A — SUGGESTION): <c>MaquinaDeEstadosCae</c>'s gate hoy es
    /// puramente ESTRUCTURAL por tipo (D4) — no verifica que el <see cref="PermisoDeSolicitud"/>
    /// en mano autorice de verdad la <see cref="SolicitudDeCae"/> específica que se está por
    /// enviar. Este chequeo lo hace EN RUNTIME, siempre ANTES de <c>ClienteWsfe.SolicitarCaeAsync</c>:
    /// <c>permiso.Numero</c> tiene que matchear <c>s.CbteDesde</c> y <c>permiso.IdComprobante</c>
    /// el comprobante que se está emitiendo. Nunca debería fallar si este archivo es el único
    /// caller (lo es — <c>ClienteWsfe</c> no tiene ningún otro invocador de producción) — defensa
    /// en profundidad contra un futuro refactor que desacople accidentalmente permiso/solicitud.</summary>
    private static void ExigirPermisoConsistenteConLaSolicitud(
        PermisoDeSolicitud permiso, SolicitudDeCae solicitud, int idComprobante)
    {
        if (permiso.Numero != solicitud.CbteDesde || permiso.IdComprobante != idComprobante)
        {
            throw new InvalidOperationException(
                $"El permiso ({permiso.IdComprobante}, {permiso.Numero}) no autoriza la solicitud " +
                $"({idComprobante}, {solicitud.CbteDesde}) — cross-check runtime del permiso violado.");
        }
    }

    private async Task<TicketDeAcceso> ObtenerTicketAsync(Empresa empresa, AmbienteFiscal ambiente, CancellationToken ct)
    {
        var claveDeTicket = new ClaveDeTicket(empresa.Id, ambiente, ServicioWsfe);
        return await repositorioDeTicket.ObtenerOFirmarAsync(
            claveDeTicket, token => FirmarTicketNuevoAsync(empresa, ambiente, token), ct);
    }

    private Task<TicketDeAcceso> FirmarTicketNuevoAsync(Empresa empresa, AmbienteFiscal ambiente, CancellationToken ct)
    {
        var claveDeTicket = new ClaveDeTicket(empresa.Id, ambiente, ServicioWsfe);
        return almacen.UsarCertificadoAsync(
            empresa.Id, ambiente,
            certificado => clienteWsaa.ObtenerTicketAsync(new SolicitudDeTicket(claveDeTicket, certificado), ct),
            ct);
    }

    // --- U2: UPDATE comprobantes_venta SET cae…, resultado_fiscal… WHERE id = $ AND id_tenant = $
    //     AND resultado_fiscal = 'pendiente' (mutation-proof-tests regla 3 v1.1, conjuncts (a)(b)(c)) ---

    private async Task<int> AplicarResultadoGuardadoAsync(
        int idTenant, int idComprobante, RespuestaCae respuesta, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var observaciones = respuesta.Resultado == ResultadoFiscal.Rechazado ? respuesta.Errors : respuesta.Observaciones;
        var observacionesJson = observaciones.Count == 0 ? null : SerializarObservaciones(observaciones);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE comprobantes_venta SET cae = $1, cae_vencimiento = $2, resultado_fiscal = $3, " +
            "observaciones_fiscales = $4::jsonb, updated_at = $5 " +
            "WHERE id_comprobante_venta = $6 AND id_tenant = $7 AND resultado_fiscal = 'pendiente'";

        ParametrosDeComando.AgregarNulo(comando, respuesta.Cae);
        ParametrosDeComando.AgregarNulo(comando, respuesta.CaeVencimiento);
        ParametrosDeComando.Agregar(comando, respuesta.Resultado);
        ParametrosDeComando.AgregarNulo(comando, observacionesJson);
        ParametrosDeComando.Agregar(comando, reloj.Ahora);
        ParametrosDeComando.Agregar(comando, idComprobante);
        ParametrosDeComando.Agregar(comando, idTenant);

        return await comando.ExecuteNonQueryAsync(ct);
    }

    private static string SerializarObservaciones(IReadOnlyList<ObservacionArca> observaciones)
    {
        var items = observaciones.Select(o => new { codigo = o.Codigo, mensaje = o.Mensaje });
        return JsonSerializer.Serialize(items);
    }

    // --- Resolución de tipo / condición / documento / ambiente — pura, CERO red ---

    /// <summary>D9: mirror image del resolver del POS (<c>ServicioDeVentas.ResolverTipoComprobanteAsync</c>)
    /// — exige <c>EsFiscal</c> en vez de <c>!EsFiscal</c>, NUNCA lee <see cref="TipoComprobante.AfectaStock"/>,
    /// nunca toca <c>ServicioDeVentas</c>.</summary>
    private static TipoComprobante ResolverTipoFiscal(TipoComprobante? tipo, string codigo)
    {
        if (tipo is null || !tipo.Activo || tipo.Clase != ClaseComprobante.Venta || !tipo.EsFiscal
            || tipo.CodigoAfip is null)
        {
            throw ErrorDominio.Conflicto(
                "tipo_fiscal_invalido", $"'{codigo}' no es un tipo de comprobante fiscal válido.");
        }

        return tipo;
    }

    private static CondicionFiscalCodigo MapearCondicionFiscal(string codigo) => codigo switch
    {
        "RI" => CondicionFiscalCodigo.ResponsableInscripto,
        "MONOTRIBUTO" => CondicionFiscalCodigo.Monotributo,
        "EXENTO" => CondicionFiscalCodigo.Exento,
        "CF" => CondicionFiscalCodigo.ConsumidorFinal,
        "NO_RESP" => CondicionFiscalCodigo.NoResponsable,
        _ => throw new ErrorDominio(
            "condicion_fiscal_codigo_no_reconocido", $"Código de condición fiscal no reconocido: '{codigo}'.", 500)
    };

    /// <summary>Mapeo condición-fiscal-de-cliente → (DocTipo, DocNro) de ARCA — no nombrado por
    /// design.md (su snippet abreviado no llega a este nivel), necesario para
    /// <c>SolicitudDeCae.DocTipo</c>/<c>DocNro</c> Y para el <c>tipoDocRec</c>/<c>nroDocRec</c> del
    /// QR (RG 4291). DECISIÓN REGISTRADA: 80=CUIT, 86=CUIL, 96=DNI, 94=Pasaporte (códigos AFIP
    /// estándar); Consumidor Final / sin documento cargado / <c>Otro</c> ⇒ 99 con DocNro 0 (la
    /// convención ARCA para receptor sin identificar).</summary>
    private static (short TipoDoc, long NroDoc) MapearDocumentoArca(Cliente cliente)
    {
        if (cliente.EsConsumidorFinal || cliente.TipoDocumento is null || string.IsNullOrWhiteSpace(cliente.NumeroDocumento))
        {
            return (99, 0);
        }

        short tipoDoc = cliente.TipoDocumento.Value switch
        {
            TipoDocumento.Cuit => 80,
            TipoDocumento.Cuil => 86,
            TipoDocumento.Dni => 96,
            TipoDocumento.Pasaporte => 94,
            _ => 99
        };

        if (tipoDoc == 99 || !long.TryParse(cliente.NumeroDocumento, NumberStyles.None, CultureInfo.InvariantCulture, out var numeroDoc))
        {
            return (99, 0);
        }

        return (tipoDoc, numeroDoc);
    }

    private AmbienteFiscal ResolverAmbiente() =>
        string.Equals(opciones.Value.Ambiente, "produccion", StringComparison.OrdinalIgnoreCase)
            ? AmbienteFiscal.Produccion
            : AmbienteFiscal.Homologacion;

    private static string ExigirCuitNumerico(Empresa empresa)
    {
        if (string.IsNullOrWhiteSpace(empresa.Cuit) || !long.TryParse(empresa.Cuit, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw ErrorDominio.Conflicto(
                "empresa_sin_cuit_valido", "La empresa no tiene un CUIT numérico válido cargado.");
        }

        return empresa.Cuit;
    }

    private static string? ConstruirQrSiCorresponde(
        RespuestaCae respuesta, DateTimeOffset fecha, string cuitEmisor, int ptoVta, short tipoCmp, long nroCmp,
        decimal importe, short tipoDocRec, long nroDocRec)
    {
        if (respuesta.Cae is null || !MaquinaDeEstadosCae.EsTerminal(respuesta.Resultado))
        {
            return null;
        }

        return PayloadQrFiscal.Construir(
            DateOnly.FromDateTime(fecha.Date), long.Parse(cuitEmisor, CultureInfo.InvariantCulture), ptoVta, tipoCmp,
            nroCmp, importe, tipoDocRec, nroDocRec, long.Parse(respuesta.Cae, CultureInfo.InvariantCulture));
    }

    private static ComprobanteFiscalEmitido Proyectar(
        ComprobanteVenta comprobante, string codigoTipo, char letra, string cuitEmisor, int ptoVta, short tipoCmp,
        short tipoDocRec, long nroDocRec)
    {
        var payloadQr = ConstruirQrSiCorresponde(
            new RespuestaCae(comprobante.ResultadoFiscal ?? ResultadoFiscal.Pendiente, comprobante.Cae,
                comprobante.CaeVencimiento, [], []),
            comprobante.Fecha, cuitEmisor, ptoVta, tipoCmp, comprobante.Numero, comprobante.Total, tipoDocRec, nroDocRec);

        return new ComprobanteFiscalEmitido(
            comprobante.Id, codigoTipo, letra, comprobante.IdPuntoVenta, comprobante.Numero,
            DateOnly.FromDateTime(comprobante.Fecha.Date), comprobante.ResultadoFiscal ?? ResultadoFiscal.Pendiente,
            comprobante.Cae, comprobante.CaeVencimiento, payloadQr);
    }

    // --- Composición de líneas — CalculadorDeTotales (dominio puro, NUNCA ServicioDeVentas, D9) ---

    private readonly record struct LineaCalculada(
        LineaDeEmisionFiscal Linea, decimal PorcentajeIva, ItemCalculado Calculado);

    private async Task<(IReadOnlyList<LineaFiscal> LineasFiscales, IReadOnlyList<LineaCalculada> Items, decimal Subtotal, decimal DescuentoTotal)>
        ComponerLineasAsync(IReadOnlyList<LineaDeEmisionFiscal> lineas, CancellationToken ct)
    {
        if (lineas.Count == 0)
        {
            throw new ErrorDominio("lineas_requeridas", "La emisión fiscal exige al menos una línea.", 400);
        }

        // judgment 19a-slice-5 ronda 2 juez A — MAJOR: sin este guard, un Vendedor podía acuñar un
        // comprobante fiscal I3-irreversible con cantidad cero/negativa o precio/descuento
        // negativo. Mismo criterio que el precedente del POS
        // (ServicioDeVentas.ExigirLineasValidas): pre-gate, CERO red, corre ANTES de la consulta de
        // alícuotas.
        ExigirLineasFiscalesValidas(lineas);

        var alicuotas = await ObtenerAlicuotasAsync(lineas.Select(l => l.IdAlicuotaIva), ct);

        var itemsCalculados = new List<LineaCalculada>(lineas.Count);
        var lineasFiscales = new List<LineaFiscal>(lineas.Count);
        var subtotal = 0m;
        var descuentoTotal = 0m;

        foreach (var linea in lineas)
        {
            if (!alicuotas.TryGetValue(linea.IdAlicuotaIva, out var alicuota))
            {
                throw ErrorDominio.NoEncontrado($"No existe la alícuota de IVA {linea.IdAlicuotaIva}.");
            }

            var calculado = CalculadorDeTotales.Calcular(
                [new LineaParaCalcular(linea.Cantidad, linea.PrecioUnitario, linea.DescuentoUnitario)]);
            var itemCalculado = calculado.Items[0];

            subtotal += Math.Round(linea.Cantidad * linea.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
            descuentoTotal += itemCalculado.Descuento;

            itemsCalculados.Add(new LineaCalculada(linea, alicuota.Porcentaje, itemCalculado));
            lineasFiscales.Add(new LineaFiscal(
                linea.IdAlicuotaIva, alicuota.Nombre, alicuota.CodigoAfip, alicuota.Porcentaje, itemCalculado.Total));
        }

        return (lineasFiscales, itemsCalculados, subtotal, descuentoTotal);
    }

    /// <summary>Lookup compartido de <c>alicuotas_iva</c> por id (extraído de <c>ComponerLineasAsync</c>
    /// para que <see cref="ComponerLineasFiscalesDesdeItemsAsync"/> — el re-cómputo del reintento,
    /// judgment 19a-slice-5 ronda 2 juez A CRITICAL — nunca diverja en el criterio de resolución de
    /// <see cref="AlicuotaIva.Nombre"/>/<see cref="AlicuotaIva.CodigoAfip"/>).</summary>
    private async Task<IReadOnlyDictionary<int, AlicuotaIva>> ObtenerAlicuotasAsync(
        IEnumerable<int> idsAlicuota, CancellationToken ct)
    {
        var ids = idsAlicuota.Distinct().ToList();
        return await db.AlicuotasIva.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
    }

    /// <summary>El re-cómputo del CRITICAL de judgment 19a-slice-5 ronda 2 juez A: la re-emisión
    /// (<see cref="ReintentarAsync"/>) NUNCA fabrica ceros — relee <c>items_comprobante_venta</c> (el
    /// snapshot congelado, doc 10 principio 6) y reconstruye el mismo shape de <see cref="LineaFiscal"/>
    /// que <c>ComponerLineasAsync</c> arma en la emisión, para que <see cref="ComposicionDeTotalesFiscales.Componer"/>
    /// recomponga el desglose COMPLETO (ImpNeto/ImpIVA/ImpOpEx/ImpTotConc/Iva[]) en vez de mandar
    /// ImpOpEx=0/Iva[]=[] fabricados que rompían el invariante vinculante del spec
    /// (comprobante-fiscal:82-88) para cualquier comprobante con líneas Exento/No-gravado.
    /// <see cref="ItemComprobanteVenta.PorcentajeIva"/>/<see cref="ItemComprobanteVenta.Total"/> son
    /// el snapshot congelado de la línea — SOLO <see cref="AlicuotaIva.Nombre"/>/
    /// <see cref="AlicuotaIva.CodigoAfip"/> vienen del catálogo (el ítem no los snapshotea).</summary>
    private async Task<IReadOnlyList<LineaFiscal>> ComponerLineasFiscalesDesdeItemsAsync(
        int idComprobante, CancellationToken ct)
    {
        var items = await db.ItemsComprobanteVenta.AsNoTracking()
            .Where(i => i.IdComprobanteVenta == idComprobante)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                $"El comprobante {idComprobante} no tiene items — no se puede recomponer su desglose " +
                "fiscal en el reintento.");
        }

        var alicuotas = await ObtenerAlicuotasAsync(items.Select(i => i.IdAlicuotaIva), ct);

        var lineasFiscales = new List<LineaFiscal>(items.Count);
        foreach (var item in items)
        {
            if (!alicuotas.TryGetValue(item.IdAlicuotaIva, out var alicuota))
            {
                throw ErrorDominio.NoEncontrado($"No existe la alícuota de IVA {item.IdAlicuotaIva}.");
            }

            lineasFiscales.Add(new LineaFiscal(
                item.IdAlicuotaIva, alicuota.Nombre, alicuota.CodigoAfip, item.PorcentajeIva, item.Total));
        }

        return lineasFiscales;
    }

    /// <summary>Mismo shape que el precedente del POS (<c>ServicioDeVentas.ExigirLineasValidas</c>):
    /// una línea con cantidad ≤ 0 o precio/descuento negativo puede acuñar un comprobante fiscal
    /// I3-irreversible con monto cero o negativo — corre ANTES de tocar la base (CERO red, CERO
    /// número quemado).</summary>
    private static void ExigirLineasFiscalesValidas(IReadOnlyList<LineaDeEmisionFiscal> lineas)
    {
        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_linea_invalida", "La cantidad de cada línea tiene que ser mayor a cero.", 400);
            }

            if (linea.PrecioUnitario < 0)
            {
                throw new ErrorDominio(
                    "precio_unitario_invalido", "El precio unitario de cada línea no puede ser negativo.", 400);
            }

            if (linea.DescuentoUnitario < 0)
            {
                throw new ErrorDominio(
                    "descuento_unitario_invalido", "El descuento unitario de cada línea no puede ser negativo.", 400);
            }
        }
    }

    /// <summary>Mismo criterio que <c>ServicioDeVentas.ExigirTenantDeLaSesion</c>: <c>OperacionDePos</c>
    /// (capa de API) ya exige un actor de tenant — un actor de plataforma nunca llega hasta acá.
    /// Defensa en profundidad, no un camino alcanzable en operación normal. El <c>id_tenant</c>
    /// explícito en el <c>WHERE</c> de U2 (conjunct (b)) es defensa en profundidad adicional, mismo
    /// criterio que U4 en <see cref="DesactivadorDeCertificadoFiscal"/> — RLS ya lo garantiza solo.</summary>
    private int ExigirTenant() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeFacturacionFiscal requiere un actor de tenant; OperacionDePos no admite plataforma.");
}
