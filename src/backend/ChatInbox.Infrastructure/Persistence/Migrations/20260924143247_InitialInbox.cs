using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_telegram_message_id = table.Column<long>(type: "bigint", nullable: true),
                    last_message_preview = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_updates",
                columns: table => new
                {
                    update_id = table.Column<long>(type: "bigint", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_updates", x => x.update_id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_update_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_activity",
                table: "conversations",
                columns: new[] { "last_message_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_conversations_chat",
                table: "conversations",
                column: "telegram_chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_time",
                table: "messages",
                columns: new[] { "conversation_id", "sent_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_messages_chat_message",
                table: "messages",
                columns: new[] { "conversation_id", "telegram_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_messages_update",
                table: "messages",
                column: "telegram_update_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "processed_updates");

            migrationBuilder.DropTable(
                name: "conversations");
        }
    }
}
