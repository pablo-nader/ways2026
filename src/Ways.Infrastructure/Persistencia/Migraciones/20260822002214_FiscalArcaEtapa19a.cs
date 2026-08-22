using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FiscalArcaEtapa19a : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orden de statements per gate (binding, design.md "The migration — exact statement
            // order"): 01) AlterDatabase — ESTE statement es quien ejecuta los DOS `CREATE TYPE`
            // (ambiente_fiscal, resultado_fiscal). CERO `ALTER TYPE ... ADD VALUE` en todo el
            // archivo — a diferencia de las etapas 12/17, esta sub-etapa no tiene NINGÚN
            // artefacto irreversible: los dos tipos son enteramente nuevos, así que Down() los
            // dropea limpio. 02-04) §B empresas. 05-07) §C puntos_venta. 08-11) §D
            // comprobantes_venta. 12-13) §E certificados_fiscales. 14-15) §F
            // numeraciones_fiscales. 16-18) §G los tres data statements idempotentes (doble red
            // de la etapa 17 — el seed net gemelo vive en InicializadorDeBaseDeDatos.
            // SembrarCatalogosFiscalesAsync). 19-20) RLS al final, en las DOS tablas nuevas
            // (convención de las etapas 12/14/15/16/17: la conexión de migración no tiene
            // app_tenant_actual() seteado, y los data statements de arriba no dependen de que
            // RLS esté activo todavía — las tres tablas de catálogo son globales).
            //
            // `dotnet ef migrations add` serializa los valores de enum en orden ALFABÉTICO por
            // defecto (mismo residuo documentado en las etapas 15/16/17) — `resultado_fiscal`
            // corregido a mano acá al orden de CICLO DE VIDA que el design fija
            // (pendiente → aprobado → aprobado_con_observaciones → rechazado, design.md §A):
            // EF lo había emitido "aprobado,aprobado_con_observaciones,pendiente,rechazado".
            // `ambiente_fiscal` no necesitó corrección: su orden alfabético
            // ("homologacion,produccion") coincide con el orden de ciclo de vida declarado en
            // AmbienteFiscal.cs.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:resultado_fiscal", "pendiente,aprobado,aprobado_con_observaciones,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            // gate §B (proposal.md:503-521): empresas +id_condicion_fiscal — NULLABLE a
            // propósito (no hay default honesto), FK simple (condiciones_fiscales es global,
            // ADR-11) e índice de soporte SIMPLE (la trampa de la enmienda de la etapa 14: un
            // índice liderado por id_tenant no cubre una FK simple).
            migrationBuilder.AddColumn<int>(
                name: "id_condicion_fiscal",
                table: "empresas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_empresas_condicion_fiscal",
                table: "empresas",
                column: "id_condicion_fiscal",
                principalTable: "condiciones_fiscales",
                principalColumn: "id_condicion_fiscal",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ix_empresas_condicion_fiscal",
                table: "empresas",
                column: "id_condicion_fiscal");

            // gate §C (proposal.md:522-533, decisión 2): puntos_venta +numero_fiscal — CHECK de
            // rango 1..99999 (PtoVta de ARCA es de 5 dígitos) + ux_puntos_venta_numero_fiscal
            // UNIQUE PARCIAL, PORTANTE: vuelve inyectivo el mapa serie-ARCA (PtoVta, CbteTipo) a
            // (id_punto_venta, codigo_afip).
            migrationBuilder.AddColumn<int>(
                name: "numero_fiscal",
                table: "puntos_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_puntos_venta_numero_fiscal_rango",
                table: "puntos_venta",
                sql: "numero_fiscal IS NULL OR (numero_fiscal BETWEEN 1 AND 99999)");

            migrationBuilder.CreateIndex(
                name: "ux_puntos_venta_numero_fiscal",
                table: "puntos_venta",
                columns: new[] { "id_tenant", "id_empresa", "numero_fiscal" },
                unique: true,
                filter: "numero_fiscal IS NOT NULL");

            // gate §D (proposal.md:535-557): comprobantes_venta +4 columnas — todas NULL en el
            // 100% del tráfico existente y de siempre para TX/NCX/TXR/RC. CHECK 2 (4 conjuntos):
            // o las cuatro NULL, o resultado_fiscal seteado con cae/cae_vencimiento juntos y
            // presentes SII aprobado/aprobado_con_observaciones. CHECK 3: formato de 14 dígitos.
            // Índice PARCIAL sobre 'pendiente' — vacío en el 100% de las filas existentes.
            migrationBuilder.AddColumn<string>(
                name: "cae",
                table: "comprobantes_venta",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "cae_vencimiento",
                table: "comprobantes_venta",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<ResultadoFiscal>(
                name: "resultado_fiscal",
                table: "comprobantes_venta",
                type: "resultado_fiscal",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "observaciones_fiscales",
                table: "comprobantes_venta",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_comprobantes_venta_fiscal_coherente",
                table: "comprobantes_venta",
                sql: "(resultado_fiscal IS NULL AND cae IS NULL AND cae_vencimiento IS NULL AND observaciones_fiscales IS NULL) " +
                     "OR (resultado_fiscal IS NOT NULL AND ((cae IS NULL) = (cae_vencimiento IS NULL)) " +
                     "AND ((resultado_fiscal IN ('aprobado','aprobado_con_observaciones')) = (cae IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comprobantes_venta_cae_digitos",
                table: "comprobantes_venta",
                sql: "cae IS NULL OR cae ~ '^[0-9]{14}$'");

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_fiscal_pendientes",
                table: "comprobantes_venta",
                columns: new[] { "id_punto_venta", "id_tenant" },
                filter: "resultado_fiscal = 'pendiente'::resultado_fiscal");

            // gate §E (proposal.md:559-606, decisiones 1/5): certificados_fiscales — 18
            // columnas, scoping id_tenant + id_empresa NOT NULL (DESVIACIÓN documentada del
            // catálogo doc-09: un certificado es de UN CUIT y nunca se comparte, misma forma que
            // puntos_venta), PK, FK2 (tenant simple), FK3 (empresa compuesta contra la AK que
            // puntos_venta ya usa), CHECK4 vigencia, CHECK5 cuit, CHECK6 tamaños de GCM (3
            // conjuntos) — todas inline.
            migrationBuilder.CreateTable(
                name: "certificados_fiscales",
                columns: table => new
                {
                    id_certificado = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    ambiente = table.Column<AmbienteFiscal>(type: "ambiente_fiscal", nullable: false),
                    alias = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    cuit_titular = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    certificado_pem = table.Column<string>(type: "text", nullable: false),
                    clave_privada_cifrada = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag_autenticacion = table.Column<byte[]>(type: "bytea", nullable: false),
                    id_clave_maestra = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    huella_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vigencia_desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vigencia_hasta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificados_fiscales", x => x.id_certificado);
                    table.CheckConstraint("ck_certificados_fiscales_vigencia", "vigencia_hasta > vigencia_desde");
                    table.CheckConstraint("ck_certificados_fiscales_cuit", "cuit_titular ~ '^[0-9]{11}$'");
                    table.CheckConstraint("ck_certificados_fiscales_material", "octet_length(nonce) = 12 AND octet_length(tag_autenticacion) = 16 AND octet_length(clave_privada_cifrada) > 0");
                    table.ForeignKey(
                        name: "fk_certificados_fiscales_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_certificados_fiscales_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificados_fiscales_tenant",
                table: "certificados_fiscales",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_certificados_fiscales_empresa",
                table: "certificados_fiscales",
                columns: new[] { "id_empresa", "id_tenant" });

            // A lo sumo un certificado activo por empresa+ambiente — la rotación (dar de baja +
            // activar) dentro de UNA transacción es lo que evita una ventana con dos activos.
            migrationBuilder.CreateIndex(
                name: "ux_certificados_fiscales_activo",
                table: "certificados_fiscales",
                columns: new[] { "id_tenant", "id_empresa", "ambiente" },
                unique: true,
                filter: "activo AND deleted_at IS NULL");

            // gate §F (proposal.md:607-641, decisión 13): numeraciones_fiscales — PK compuesta
            // (id_punto_venta, codigo_afip), SIN auditoría (mismo criterio que
            // NumeracionComprobante — espeja NumeracionComprobanteConfiguration.cs:44-59 línea a
            // línea, incluida la FK compuesta a puntos_venta y los dos nombres de índice
            // explícitos en snake_case). CHECK7 rango (0 legal = "serie sin usar"), CHECK8
            // sincronización — inline.
            migrationBuilder.CreateTable(
                name: "numeraciones_fiscales",
                columns: table => new
                {
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    codigo_afip = table.Column<short>(type: "smallint", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    proximo_numero = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ultimo_autorizado_arca = table.Column<long>(type: "bigint", nullable: true),
                    sincronizado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_numeraciones_fiscales", x => new { x.id_punto_venta, x.codigo_afip });
                    table.CheckConstraint("ck_numeraciones_fiscales_rango", "proximo_numero BETWEEN 1 AND 99999999 AND (ultimo_autorizado_arca IS NULL OR ultimo_autorizado_arca BETWEEN 0 AND 99999999)");
                    table.CheckConstraint("ck_numeraciones_fiscales_sincronizacion", "(ultimo_autorizado_arca IS NULL) = (sincronizado_en IS NULL)");
                    table.ForeignKey(
                        name: "fk_numeraciones_fiscales_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_numeraciones_fiscales_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_numeraciones_fiscales_tenant",
                table: "numeraciones_fiscales",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_numeraciones_fiscales_punto_venta",
                table: "numeraciones_fiscales",
                columns: new[] { "id_punto_venta", "id_tenant" });

            // gate §G (proposal.md:643-676, decisión 11) — TRES data statements IDEMPOTENTES
            // (`WHERE ... codigo_afip IS NULL`), la mitad de la doble red de la etapa 17: el
            // gemelo (net 1b, para una base FRESCA) vive en InicializadorDeBaseDeDatos.
            // SembrarCatalogosFiscalesAsync — cada net se prueba INDEPENDIENTEMENTE (target 18).
            // Ningún ALTER hace falta: las tres tablas ya tienen codigo_afip smallint NULL desde
            // 20260801233937_CatalogosGlobales.cs. CERO filas insertadas, activadas o
            // desactivadas — solo el campo nuevo.
            //
            // DS1 — tipos_comprobante (7 filas): FA=1, NDA=2, NCA=3, FB=6, NCB=8, FC=11, NCC=13.
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 1 WHERE codigo = 'FA' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 2 WHERE codigo = 'NDA' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 3 WHERE codigo = 'NCA' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 6 WHERE codigo = 'FB' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 8 WHERE codigo = 'NCB' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 11 WHERE codigo = 'FC' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = 13 WHERE codigo = 'NCC' AND codigo_afip IS NULL;");

            // DS2 — condiciones_fiscales (5 filas, RG 5616 CondicionIVAReceptorId): RI=1,
            // EXENTO=4, CF=5, MONOTRIBUTO=6. NO_RESP=15 (RG 5616 "IVA No Alcanzado") — LA ÚNICA
            // INCERTIDUMBRE FLAGGEADA del proposal (decisión 11): sin contraparte exacta en esa
            // tabla, mapeada a la condición más cercana; se confirma contra
            // FEParamGetCondicionIvaReceptor en 19b, y hasta entonces un receptor NO_RESP se
            // rechaza por su Código (no por este valor) con 409 nombrado — nunca se factura
            // sobre una adivinanza (spec comprobante-fiscal).
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = 1 WHERE codigo = 'RI' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = 4 WHERE codigo = 'EXENTO' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = 5 WHERE codigo = 'CF' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = 6 WHERE codigo = 'MONOTRIBUTO' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = 15 WHERE codigo = 'NO_RESP' AND codigo_afip IS NULL;");

            // DS3 — alicuotas_iva (4 filas, FEParamGetTiposIva): 0%=3, 10.5%=4, 21%=5, 27%=6.
            // Exento/No gravado quedan DELIBERADAMENTE sin tocar — no son alícuotas, sus
            // importes van a ImpOpEx/ImpTotConc y jamás al array Iva[] (decisión 11).
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = 3 WHERE nombre = '0%' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = 4 WHERE nombre = '10.5%' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = 5 WHERE nombre = '21%' AND codigo_afip IS NULL;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = 6 WHERE nombre = '27%' AND codigo_afip IS NULL;");

            // RLS al final, en las DOS tablas nuevas (ADR-15 / convención etapas 12-17): la
            // conexión de migración no tiene app_tenant_actual() seteado y los tres data
            // statements de arriba no dependen de RLS estar activo (las tres catálogos son
            // globales, sin id_tenant).
            migrationBuilder.HabilitarRlsDeTenant("certificados_fiscales");
            migrationBuilder.HabilitarRlsDeTenant("numeraciones_fiscales");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Los tres data statements se revierten PRIMERO, doblemente guardeados
            // (`WHERE codigo = … AND codigo_afip = <valor exacto que Up() puso>`) — Down() toca
            // SOLO las filas que Up() efectivamente seteó; una fila que ya traía un código antes
            // de esta migración (imposible hoy, barato de garantizar para siempre) queda intacta.
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = NULL WHERE nombre = '27%' AND codigo_afip = 6;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = NULL WHERE nombre = '21%' AND codigo_afip = 5;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = NULL WHERE nombre = '10.5%' AND codigo_afip = 4;");
            migrationBuilder.Sql("UPDATE alicuotas_iva SET codigo_afip = NULL WHERE nombre = '0%' AND codigo_afip = 3;");

            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'NO_RESP' AND codigo_afip = 15;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'MONOTRIBUTO' AND codigo_afip = 6;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'CF' AND codigo_afip = 5;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'EXENTO' AND codigo_afip = 4;");
            migrationBuilder.Sql("UPDATE condiciones_fiscales SET codigo_afip = NULL WHERE codigo = 'RI' AND codigo_afip = 1;");

            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'NCC' AND codigo_afip = 13;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'FC' AND codigo_afip = 11;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'NCB' AND codigo_afip = 8;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'FB' AND codigo_afip = 6;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'NCA' AND codigo_afip = 3;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'NDA' AND codigo_afip = 2;");
            migrationBuilder.Sql("UPDATE tipos_comprobante SET codigo_afip = NULL WHERE codigo = 'FA' AND codigo_afip = 1;");

            // DropTable arrastra su PK, sus FKs, sus CHECKs, sus índices y su policy de RLS —
            // Down() no necesita un statement de policy explícito.
            migrationBuilder.DropTable(
                name: "numeraciones_fiscales");

            migrationBuilder.DropTable(
                name: "certificados_fiscales");

            migrationBuilder.DropIndex(
                name: "ix_comprobantes_venta_fiscal_pendientes",
                table: "comprobantes_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comprobantes_venta_cae_digitos",
                table: "comprobantes_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comprobantes_venta_fiscal_coherente",
                table: "comprobantes_venta");

            // DropColumn ×4 ANTES del AlterDatabase final: DROP TYPE resultado_fiscal falla
            // mientras una columna todavía usa ese tipo.
            migrationBuilder.DropColumn(
                name: "observaciones_fiscales",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "resultado_fiscal",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "cae_vencimiento",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "cae",
                table: "comprobantes_venta");

            migrationBuilder.DropIndex(
                name: "ux_puntos_venta_numero_fiscal",
                table: "puntos_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_puntos_venta_numero_fiscal_rango",
                table: "puntos_venta");

            migrationBuilder.DropColumn(
                name: "numero_fiscal",
                table: "puntos_venta");

            migrationBuilder.DropIndex(
                name: "ix_empresas_condicion_fiscal",
                table: "empresas");

            migrationBuilder.DropForeignKey(
                name: "fk_empresas_condicion_fiscal",
                table: "empresas");

            migrationBuilder.DropColumn(
                name: "id_condicion_fiscal",
                table: "empresas");

            // Último statement: Annotation/OldAnnotation invertidos ⇒ DROP TYPE resultado_fiscal;
            // DROP TYPE ambiente_fiscal. Ambos SALEN LIMPIOS — CERO valor de enum agregado a un
            // tipo preexistente en toda esta migración (a diferencia de las etapas 12/17), así
            // que esta sub-etapa no deja NINGÚN artefacto irreversible tras un rollback.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "pendiente,aprobado,aprobado_con_observaciones,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
