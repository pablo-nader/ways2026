using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AplicacionesAlCambiarLaCompra : Migration
    {
        /// <summary>E6-S3: el trigger diferido que exige que una compra admita sus aplicaciones vivas ahora también dispara
        /// cuando cambia el proveedor o el punto de venta de la compra (la edición los libera en la misma transacción; un
        /// UPDATE directo que no lo haga falla al commit con 23514 y el nombre del trigger como <c>CONSTRAINT</c>). La
        /// función que valida no cambia: ya compara el proveedor de cada pago aplicado con el de la compra.</summary>
        public const string SqlDelTriggerAmpliado =
            """
            DROP TRIGGER IF EXISTS tg_comprobantes_compra_admiten_aplicaciones ON comprobantes_compra;

            CREATE CONSTRAINT TRIGGER tg_comprobantes_compra_admiten_aplicaciones
                AFTER UPDATE OF total, estado, id_proveedor, id_punto_venta ON comprobantes_compra
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.total IS DISTINCT FROM NEW.total OR OLD.estado IS DISTINCT FROM NEW.estado
                      OR OLD.id_proveedor IS DISTINCT FROM NEW.id_proveedor
                      OR OLD.id_punto_venta IS DISTINCT FROM NEW.id_punto_venta)
                EXECUTE FUNCTION fn_aplicaciones_pago_validar_compra_desde_compra();
            """;

        /// <summary>El trigger tal cual lo creó <c>AplicacionesDePago</c>.</summary>
        public const string SqlDelTriggerOriginal =
            """
            DROP TRIGGER IF EXISTS tg_comprobantes_compra_admiten_aplicaciones ON comprobantes_compra;

            CREATE CONSTRAINT TRIGGER tg_comprobantes_compra_admiten_aplicaciones
                AFTER UPDATE OF total, estado ON comprobantes_compra
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD.total IS DISTINCT FROM NEW.total OR OLD.estado IS DISTINCT FROM NEW.estado)
                EXECUTE FUNCTION fn_aplicaciones_pago_validar_compra_desde_compra();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDelTriggerAmpliado);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDelTriggerOriginal);
        }
    }
}
