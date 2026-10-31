using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PersonalHorarioHabitual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal[]>(
                name: "horario_habitual",
                table: "personal",
                type: "numeric(4,2)[]",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_horario_habitual",
                table: "personal",
                sql: "horario_habitual IS NULL OR (cardinality(horario_habitual) = 7 AND array_position(horario_habitual, NULL) IS NULL AND 0 <= ALL (horario_habitual) AND 24 >= ALL (horario_habitual))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_horario_habitual",
                table: "personal");

            migrationBuilder.DropColumn(
                name: "horario_habitual",
                table: "personal");
        }
    }
}
