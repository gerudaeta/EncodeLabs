using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatInbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "read_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_unread",
                table: "messages",
                column: "conversation_id",
                filter: "direction = 'inbound' AND read_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_unread",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "read_at",
                table: "messages");
        }
    }
}
