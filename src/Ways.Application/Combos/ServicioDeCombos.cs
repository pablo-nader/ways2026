using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;
using Ways.Domain.Common;

namespace Ways.Application.Combos;

/// <summary>
/// ABM de combos (doc 10 §Combos): el combo y su conjunto de componentes se escriben juntos y el conjunto se reemplaza
/// completo en cada edición. Autorización: <c>Politicas.GestionDeCatalogo</c> en las escrituras, aplicada en la capa
/// de API.
///
/// <para>Toda escritura (alta, edición y baja) toma, como primera sentencia de su transacción, un lock advisory por
/// tenant (<see cref="ClaveDeLockDeCombos"/>). Con él las escrituras de combos de un tenant se serializan, y eso
/// sostiene tres cosas: el código asignado por el servidor (máximo vivo + 1) no se repite entre dos altas
/// concurrentes, el pre-chequeo de código duplicado se decide sobre el estado ya comiteado por la escritura anterior,
/// y una edición no pisa una baja concurrente. <c>ux_combos_codigo</c> sigue siendo el respaldo de esquema ante una
/// escritura que no pase por acá.</para>
///
/// <para>Un artículo o una familia referenciada se toma con <c>FOR KEY SHARE</c> sobre su fila viva
/// (<see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>) dentro de la misma transacción: la baja de ese
/// artículo o de esa familia toma <c>FOR UPDATE</c> sobre la fila, así que espera a que el combo comitee y entonces
/// ve el componente, o gana la carrera y el combo recibe <c>referencia_invalida</c>. El pre-chequeo de existencia
/// sin lock dejaba esa carrera abierta.</para>
///
/// <para>Baja lógica: el combo queda con <c>deleted_at</c> y sus componentes se borran físicamente en la misma
/// transacción. Es lo que hace que un combo dado de baja no bloquee la baja de sus artículos y familias (sus FK son
/// <c>Restrict</c> y la guarda de referencias cuenta cualquier fila que exista); el código queda libre por el índice
/// único parcial.</para>
///
/// <para>Sin reintento (<c>ef-retry-safe-writes</c>, forma (b)): las filas de componentes se construyen nuevas en cada
/// intento y no hay clave de idempotencia, y tras un commit ambiguo un reintento de la baja respondería 404 a una baja
/// que sí tuvo éxito.</para>
/// </summary>
public class ServicioDeCombos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, GuardaDeReferencias guarda)
{
    private const int LargoMaximoDelNombre = 150;

    /// <summary>"COMB" en ASCII: la mitad alta de la clave de 64 bits del lock. Es la variante de UNA clave
    /// <c>bigint</c> de <c>pg_advisory_xact_lock</c>, cuyo espacio no se superpone con el de la variante de dos
    /// <c>int</c> que usan ofertas, precios y bajas de organización.</summary>
    private const int PrefijoDeClave = 0x434F4D42;

    /// <summary>La clave del lock de combos de un tenant. Pública para que las pruebas puedan sostenerlo o
    /// buscarlo desde otra conexión.</summary>
    public static long ClaveDeLockDeCombos(int idTenant) => ((long)PrefijoDeClave << 32) | unchecked((uint)idTenant);

    /// <summary>Los combos vivos del tenant, ordenados por código. Sin filtro de <c>Activo</c>: un combo inactivo
    /// sigue siendo administrable desde el listado.</summary>
    public async Task<IReadOnlyList<ComboListado>> ListarAsync(CancellationToken ct = default)
    {
        var combos = await db.Combos.OrderBy(c => c.Codigo).ToListAsync(ct);

        return await ProyectarAsync(combos, ct);
    }

    public async Task<ComboListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var combo = await BuscarAsync(id, ct);

        return (await ProyectarAsync([combo], ct))[0];
    }

    /// <summary>El combo activo <paramref name="id"/> con los artículos que completan cada componente, para cargarlo en el
    /// carrito del POS. <c>404</c> si no existe, está dado de baja, está inactivo, es de otro tenant o es de una empresa
    /// distinta de <paramref name="idEmpresa"/> (<c>null</c> ⇒ solo vale uno de todo el tenant, como en la venta).</summary>
    public Task<ComboCargable> ObtenerCargableAsync(int id, int? idEmpresa, CancellationToken ct = default) =>
        CargarParaPosAsync(db, id, idEmpresa, ct);

    /// <summary>
    /// Tres consultas sin importar cuántos componentes o miembros haya: el combo, sus componentes con el nombre del
    /// artículo o de la familia (LEFT JOIN: una fila dada de baja deja el componente con <c>Nombre</c> nulo en vez de
    /// desaparecer, dangling-fk-read-models) y los artículos candidatos de todos los componentes juntos. Un candidato es
    /// lo que el escaneo resuelve (<see cref="ServicioDeEscaneo"/>: artículo vivo y activo, sin mirar precio, que se
    /// resuelve aparte) y además de una unidad de venta que admite la cantidad del componente
    /// (<see cref="ReglaDeCantidadDeVenta"/>): el ABM lo comprueba al guardar, pero después un artículo puede cambiar
    /// de unidad o sumarse a la familia. Un miembro de una familia dada de baja no cuenta.
    /// </summary>
    public static async Task<ComboCargable> CargarParaPosAsync(
        IWaysDbContext db, int id, int? idEmpresa, CancellationToken ct = default)
    {
        var combo = await db.Combos
            .Where(c => c.Id == id && c.Activo && (c.IdEmpresa == null || c.IdEmpresa == idEmpresa))
            .Select(c => new { c.Id, c.Codigo, c.Nombre, c.Precio })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el combo {id}.");

        var componentes = await (
            from componente in db.CombosComponentes
            where componente.IdCombo == combo.Id
            join a in db.Articulos on componente.IdArticulo equals (int?)a.Id into articulos
            from articulo in articulos.DefaultIfEmpty()
            join f in db.Familias on componente.IdFamilia equals (int?)f.Id into familias
            from familia in familias.DefaultIfEmpty()
            orderby componente.Id
            select new
            {
                componente.IdArticulo,
                componente.IdFamilia,
                componente.Cantidad,
                Nombre = articulo != null ? articulo.Nombre : familia != null ? familia.Nombre : null
            }).ToListAsync(ct);

        var idsArticulos = componentes.Where(c => c.IdArticulo is not null).Select(c => c.IdArticulo!.Value).ToList();
        var idsFamilias = componentes.Where(c => c.IdFamilia is not null).Select(c => c.IdFamilia!.Value).ToList();

        var candidatos = await (
            from a in db.Articulos
            where a.Activo
                && (idsArticulos.Contains(a.Id)
                    || (a.IdFamilia != null
                        && idsFamilias.Contains(a.IdFamilia.Value)
                        && db.Familias.Any(f => f.Id == a.IdFamilia)))
            orderby a.Nombre, a.Id
            select new { a.Id, a.IdFamilia, a.CodigoInterno, a.Nombre, a.AcumulaEnVenta, a.UnidadVenta })
            .ToListAsync(ct);

        return new ComboCargable(
            combo.Id, combo.Codigo, combo.Nombre, combo.Precio,
            [
                .. componentes.Select(c => new ComponenteCargable(
                    c.IdArticulo, c.IdFamilia, c.Nombre, c.Cantidad,
                    [
                        .. candidatos
                            .Where(x => (c.IdArticulo is { } idArticulo ? x.Id == idArticulo : x.IdFamilia == c.IdFamilia)
                                && ReglaDeCantidadDeVenta.EsCantidadAdmitida(x.UnidadVenta, c.Cantidad))
                            .Select(x => new ArticuloEscaneado(
                                x.Id, x.CodigoInterno, x.Nombre, null, 1m, x.AcumulaEnVenta, x.UnidadVenta))
                    ]))
            ]);
    }

    public async Task<ComboListado> CrearAsync(AltaCombo datos, CancellationToken ct = default)
    {
        var nombre = NormalizarNombre(datos.Nombre);
        var codigo = ValidarCodigoPedido(datos.Codigo);
        var componentes = ValidarConjunto(datos.Precio, datos.Componentes);

        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var combo = await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeCombosAsync(idTenant, ct);

            await ExigirReferenciasUtilizablesAsync(componentes, ct);

            var codigoFinal = codigo ?? await SiguienteCodigoLibreAsync(ct);

            if (codigo is not null)
            {
                await ExigirCodigoLibreAsync(codigoFinal, idDelCombo: null, ct);
            }

            var ahora = reloj.Ahora;
            var nuevo = new Combo
            {
                Codigo = codigoFinal,
                Nombre = nombre,
                Precio = datos.Precio,
                IdEmpresa = datos.IdEmpresa,
                Activo = datos.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };

            db.Combos.Add(nuevo);
            await db.SaveChangesAsync(ct);

            AgregarComponentes(nuevo.Id, idTenant, componentes);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);

            return nuevo;
        });

        return (await ProyectarAsync([combo], ct))[0];
    }

    public async Task<ComboListado> ActualizarAsync(int id, EdicionCombo datos, CancellationToken ct = default)
    {
        // 404 primero (update-404-first): un id inexistente, dado de baja o de otro tenant responde 404 aunque el
        // cuerpo sea inválido o su código choque. EXISTS y no una entidad, para no ensuciar la única lectura de la
        // fila, que nace bajo el lock.
        if (!await db.Combos.AnyAsync(c => c.Id == id, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el combo {id}.");
        }

        var nombre = NormalizarNombre(datos.Nombre);
        var activo = datos.Activo
            ?? throw new ErrorDominio("activo_requerido", "El campo activo es obligatorio.", 400);
        var codigo = ValidarCodigoPedido(datos.Codigo);
        var componentes = ValidarConjunto(datos.Precio, datos.Componentes);

        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var combo = await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeCombosAsync(idTenant, ct);

            // La ÚNICA lectura del combo, nacida bajo el lock (single-read-under-lock): una baja concurrente que
            // ganó el lock ya comiteó y el filtro de baja lógica responde 404.
            var existente = await BuscarAsync(id, ct);

            await ExigirReferenciasUtilizablesAsync(componentes, ct);

            if (codigo is { } nuevoCodigo)
            {
                await ExigirCodigoLibreAsync(nuevoCodigo, id, ct);
            }

            existente.Codigo = codigo ?? existente.Codigo;
            existente.Nombre = nombre;
            existente.Precio = datos.Precio;
            existente.IdEmpresa = datos.IdEmpresa;
            existente.Activo = activo;
            existente.UpdatedAt = reloj.Ahora;

            // Dos SaveChanges en la misma transacción: el DELETE del conjunto viejo se emite antes que el INSERT del
            // nuevo aunque repitan artículos, sin depender de cómo ordene EF las sentencias sobre los índices únicos.
            await QuitarComponentesAsync(id, ct);
            await db.SaveChangesAsync(ct);

            AgregarComponentes(id, idTenant, componentes);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);

            return existente;
        });

        return (await ProyectarAsync([combo], ct))[0];
    }

    /// <summary>Baja lógica del combo y borrado físico de sus componentes, en una transacción. <c>404</c> si no existe,
    /// ya está dado de baja o es de otro tenant.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeCombosAsync(idTenant, ct);

            var combo = await BuscarAsync(id, ct);

            await QuitarComponentesAsync(id, ct);

            var ahora = reloj.Ahora;
            combo.DeletedAt = ahora;
            combo.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Proyecta los combos con los nombres de sus componentes en dos consultas, sin una por combo. Los nombres salen de
    /// LEFT JOIN contra artículos y familias (dangling-fk-read-models): el filtro de baja lógica deja invisible a una
    /// fila dada de baja, y su componente vuelve con <c>Nombre</c> nulo y <c>Disponible</c> en <c>false</c> en vez de
    /// desaparecer del combo.
    /// </summary>
    private async Task<IReadOnlyList<ComboListado>> ProyectarAsync(IReadOnlyList<Combo> combos, CancellationToken ct)
    {
        if (combos.Count == 0)
        {
            return [];
        }

        var ids = combos.Select(c => c.Id).ToList();

        var filas = await (
            from componente in db.CombosComponentes
            where ids.Contains(componente.IdCombo)
            join a in db.Articulos on componente.IdArticulo equals (int?)a.Id into articulos
            from articulo in articulos.DefaultIfEmpty()
            join f in db.Familias on componente.IdFamilia equals (int?)f.Id into familias
            from familia in familias.DefaultIfEmpty()
            orderby componente.Id
            select new
            {
                componente.IdCombo,
                componente.IdArticulo,
                componente.IdFamilia,
                componente.Cantidad,
                NombreDelArticulo = articulo != null ? articulo.Nombre : null,
                NombreDeLaFamilia = familia != null ? familia.Nombre : null
            }).ToListAsync(ct);

        var porCombo = filas
            .GroupBy(f => f.IdCombo)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ComponenteDeComboListado>)
                [
                    .. g.Select(f =>
                    {
                        var nombreDelComponente = f.NombreDelArticulo ?? f.NombreDeLaFamilia;
                        return new ComponenteDeComboListado(
                            f.IdArticulo, f.IdFamilia, nombreDelComponente, f.Cantidad, nombreDelComponente is not null);
                    })
                ]);

        return
        [
            .. combos.Select(c => new ComboListado(
                c.Id, c.Codigo, c.Nombre, c.Precio, c.IdEmpresa, c.Activo,
                porCombo.TryGetValue(c.Id, out var componentes) ? componentes : []))
        ];
    }

    private static ComponenteDeCombo[] ValidarConjunto(
        decimal precio, IReadOnlyList<ComponenteDeSolicitudDeCombo>? solicitados)
    {
        var componentes = (solicitados ?? []).Select(c => c is null
            ? throw new ErrorDominio(
                "combo_componente_invalido",
                "Cada componente del combo tiene que ser exactamente un artículo o una familia.",
                400)
            : new ComponenteDeCombo(c.IdArticulo, c.IdFamilia, c.Cantidad)).ToArray();

        ReglaDeCombos.Validar(precio, componentes);

        return componentes;
    }

    private static int? ValidarCodigoPedido(int? codigo)
    {
        if (codigo is { } valor)
        {
            ReglaDeCombos.ValidarCodigo(valor);
        }

        return codigo;
    }

    /// <summary>Bajo el lock de combos: toma cada fila referenciada <c>FOR KEY SHARE</c>, en orden ascendente de id y
    /// artículos antes que familias, y comprueba que sea viva y del tenant (RLS esconde la ajena); después exige que
    /// la cantidad de cada componente sea una que admite la unidad de venta de su artículo, o la de todos los miembros
    /// vivos de su familia (<see cref="ReglaDeCantidadDeVenta"/>; <c>combo_cantidad_no_admitida</c>). Es un chequeo
    /// puntual: ni cambiar la unidad de venta de un componente ni sumar un artículo a una familia ya usada por un combo
    /// se bloquea después, así que la detección de combos en la venta y la carga en el POS vuelven a decidir por la
    /// unidad de venta de cada artículo.</summary>
    private async Task ExigirReferenciasUtilizablesAsync(
        IReadOnlyCollection<ComponenteDeCombo> componentes, CancellationToken ct)
    {
        var idsArticulos = componentes.Where(c => c.IdArticulo is not null).Select(c => c.IdArticulo!.Value).Order().ToList();
        var idsFamilias = componentes.Where(c => c.IdFamilia is not null).Select(c => c.IdFamilia!.Value).Order().ToList();

        foreach (var idArticulo in idsArticulos)
        {
            if (!await guarda.BloquearSiEstaVivaAsync<Articulo>(idArticulo, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);
            }
        }

        foreach (var idFamilia in idsFamilias)
        {
            if (!await guarda.BloquearSiEstaVivaAsync<Familia>(idFamilia, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe la familia {idFamilia}.", 400);
            }
        }

        var unidades = await db.Articulos
            .Where(a => idsArticulos.Contains(a.Id) || (a.IdFamilia != null && idsFamilias.Contains(a.IdFamilia.Value)))
            .Select(a => new { a.Id, a.IdFamilia, a.UnidadVenta })
            .ToListAsync(ct);

        foreach (var componente in componentes)
        {
            var noLaAdmite = unidades.Any(u =>
                (componente.IdArticulo is { } idArticulo ? u.Id == idArticulo : u.IdFamilia == componente.IdFamilia)
                && !ReglaDeCantidadDeVenta.EsCantidadAdmitida(u.UnidadVenta, componente.Cantidad));

            if (noLaAdmite)
            {
                throw new ErrorDominio(
                    "combo_cantidad_no_admitida",
                    componente.IdArticulo is not null
                        ? "Un artículo que se vende por unidad lleva una cantidad entera en el combo."
                        : "Una familia con artículos que se venden por unidad lleva una cantidad entera en el combo.",
                    400);
            }
        }
    }

    private async Task<int> SiguienteCodigoLibreAsync(CancellationToken ct)
    {
        var maximo = await db.Combos.MaxAsync(c => (int?)c.Codigo, ct) ?? 0;

        if (maximo >= ReglaDeCombos.CodigoMaximo)
        {
            throw ErrorDominio.Conflicto(
                "combo_codigo_agotado",
                "El último código posible ya está en uso: indicá un código libre.");
        }

        return maximo + 1;
    }

    /// <summary>Pre-chequeo de código: UX y base de la decisión bajo el lock. El contrato es <c>ux_combos_codigo</c>
    /// con su traducción 23505 → 409 en <c>ManejadorDeErrores</c>.</summary>
    private async Task ExigirCodigoLibreAsync(int codigo, int? idDelCombo, CancellationToken ct)
    {
        if (await db.Combos.AnyAsync(c => c.Codigo == codigo && c.Id != idDelCombo, ct))
        {
            throw ErrorDominio.Conflicto("combo_codigo_duplicado", $"Ya existe un combo con el código {codigo}.");
        }
    }

    private async Task ExigirEmpresaValidaAsync(int? idEmpresa, CancellationToken ct)
    {
        if (idEmpresa is null)
        {
            return;
        }

        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
        }
    }

    private void AgregarComponentes(int idCombo, int idTenant, IEnumerable<ComponenteDeCombo> componentes)
    {
        foreach (var componente in componentes)
        {
            db.CombosComponentes.Add(new ComboComponente
            {
                IdCombo = idCombo,
                IdTenant = idTenant,
                IdArticulo = componente.IdArticulo,
                IdFamilia = componente.IdFamilia,
                Cantidad = componente.Cantidad
            });
        }
    }

    /// <summary>Marca para borrar el conjunto vigente de componentes del combo; lo borra el próximo
    /// <c>SaveChangesAsync</c>. Se lee bajo el lock de combos, así que es el estado ya comiteado por la escritura
    /// anterior.</summary>
    private async Task QuitarComponentesAsync(int idCombo, CancellationToken ct)
    {
        var vigentes = await db.CombosComponentes.Where(c => c.IdCombo == idCombo).ToListAsync(ct);
        db.CombosComponentes.RemoveRange(vigentes);
    }

    private async Task TomarLockDeCombosAsync(int idTenant, CancellationToken ct)
    {
        var transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "El lock de combos es de alcance de transacción: tomarlo fuera de una no protege nada.");

        var conexion = db.Database.GetDbConnection();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1)";
        ParametrosDeComando.Agregar(comando, ClaveDeLockDeCombos(idTenant));
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Los combos que pueden aplicarse a una venta de la empresa <paramref name="idEmpresa"/> (<c>null</c> ⇒
    /// solo los de todo el tenant): vivos
    /// (el filtro global excluye los dados de baja), activos y de esa empresa o de todo el tenant, con sus
    /// componentes. Solo figuran los combos con al menos un componente que el carrito pueda satisfacer (un artículo
    /// de <paramref name="idsDeArticulos"/> o una familia de <paramref name="idsDeFamilias"/>), pero de esos se
    /// traen TODOS los componentes: el motor necesita el conjunto completo para decidir si el combo se completa.
    /// Una sola consulta (combos unidos con sus componentes) sin importar cuántos combos haya; un combo sin
    /// componentes no figura. Estático y sin estado para que el checkout no sume una dependencia al constructor de
    /// <c>ServicioDeVentas</c>.</summary>
    public static async Task<IReadOnlyList<ComboCandidato>> CargarCandidatosParaVentaAsync(
        IWaysDbContext db, int? idEmpresa, IReadOnlyCollection<int> idsDeArticulos,
        IReadOnlyCollection<int> idsDeFamilias, CancellationToken ct = default)
    {
        var filas = await (
                from combo in db.Combos
                where combo.Activo && (combo.IdEmpresa == null || combo.IdEmpresa == idEmpresa)
                    && db.CombosComponentes.Any(m =>
                        m.IdCombo == combo.Id
                        && ((m.IdArticulo != null && idsDeArticulos.Contains(m.IdArticulo.Value))
                            || (m.IdFamilia != null && idsDeFamilias.Contains(m.IdFamilia.Value))))
                join componente in db.CombosComponentes on combo.Id equals componente.IdCombo
                orderby combo.Id, componente.Id
                select new
                {
                    combo.Id,
                    combo.Codigo,
                    combo.Nombre,
                    combo.Precio,
                    componente.IdArticulo,
                    componente.IdFamilia,
                    componente.Cantidad
                })
            .ToListAsync(ct);

        return AgruparCandidatos(filas.Select(f => (f.Id, f.Codigo, f.Nombre, f.Precio, f.IdArticulo, f.IdFamilia, f.Cantidad)));
    }

    /// <summary>Los combos vivos, activos y de la empresa <paramref name="idEmpresa"/> o de todo el tenant, con TODOS
    /// sus componentes, para la instantánea del punto de venta de escritorio. Una sola consulta. Ordenados por id de
    /// combo y, dentro de cada uno, por contenido (artículos primero, por id; después familias, por id), así que dos
    /// lecturas del mismo estado dan la misma lista aunque cambien los ids de las filas de componentes.</summary>
    public static async Task<IReadOnlyList<ComboCandidato>> CargarParaInstantaneaAsync(
        IWaysDbContext db, int idEmpresa, CancellationToken ct = default)
    {
        var filas = await (
                from combo in db.Combos
                where combo.Activo && (combo.IdEmpresa == null || combo.IdEmpresa == idEmpresa)
                join componente in db.CombosComponentes on combo.Id equals componente.IdCombo
                orderby combo.Id, componente.IdArticulo == null, componente.IdArticulo, componente.IdFamilia
                select new
                {
                    combo.Id,
                    combo.Codigo,
                    combo.Nombre,
                    combo.Precio,
                    componente.IdArticulo,
                    componente.IdFamilia,
                    componente.Cantidad
                })
            .ToListAsync(ct);

        return AgruparCandidatos(filas.Select(f => (f.Id, f.Codigo, f.Nombre, f.Precio, f.IdArticulo, f.IdFamilia, f.Cantidad)));
    }

    private static IReadOnlyList<ComboCandidato> AgruparCandidatos(
        IEnumerable<(int Id, int Codigo, string Nombre, decimal Precio, int? IdArticulo, int? IdFamilia, decimal Cantidad)> filas) =>
        filas
            .GroupBy(f => f.Id)
            .Select(g => new ComboCandidato(
                g.Key,
                g.First().Nombre,
                g.First().Precio,
                g.Select(f => new ComponenteDeCombo(f.IdArticulo, f.IdFamilia, f.Cantidad)).ToList(),
                g.First().Codigo))
            .ToList();

    private async Task<Combo> BuscarAsync(int id, CancellationToken ct) =>
        await db.Combos.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el combo {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCombos requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private static string NormalizarNombre(string? valor)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El campo nombre es obligatorio.", 400);
        }

        if (limpio.Length > LargoMaximoDelNombre)
        {
            throw new ErrorDominio(
                "nombre_muy_largo", $"El campo nombre no puede superar los {LargoMaximoDelNombre} caracteres.", 400);
        }

        return limpio;
    }
}
