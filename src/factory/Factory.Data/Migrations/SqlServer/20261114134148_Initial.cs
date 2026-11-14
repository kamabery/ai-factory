using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Skeleton = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Ts = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Job = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Idea = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Line = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "Ideas",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Target = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ideas", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: false),
                    Idea = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Round = table.Column<int>(type: "int", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Holder = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => new { x.TenantId, x.Name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_Job",
                table: "Events",
                columns: new[] { "TenantId", "Job" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_Seq",
                table: "Events",
                columns: new[] { "TenantId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_Stage",
                table: "Jobs",
                columns: new[] { "TenantId", "Stage" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Boards");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Ideas");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Leases");
        }
    }
}
