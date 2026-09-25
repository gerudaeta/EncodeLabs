# ADR-001: PostgreSQL para los datos de conversación

**Estado:** Aceptado

## Contexto

Los contactos, las conversaciones y los mensajes necesitan almacenamiento durable. Sus relaciones, el orden de los mensajes y el acceso concurrente de operadores favorecen un almacén relacional transaccional con claves y restricciones exigibles.

## Alternativas consideradas

- **PostgreSQL (elegida).** Soporta restricciones relacionales y transacciones ([restricciones](https://www.postgresql.org/docs/current/ddl-constraints.html); [transacciones](https://www.postgresql.org/docs/current/tutorial-transactions.html)). Ventaja: integridad relacional explícita y un modelo de consulta convencional para el historial del inbox. Desventaja: exige migraciones de esquema, backups y operación de base de datos.
- **SQLite.** Simplificaría una configuración local de un solo proceso, pero encaja peor con una API desplegada por separado con escritores concurrentes.
- **Almacén de documentos.** Facilitaría guardar payloads variables de Telegram, pero traslada más trabajo de integridad de relaciones y de consultas de conversación al código de aplicación.

Ninguna alternativa supera el ajuste de PostgreSQL para estos registros centrales.

## Decisión

Usar PostgreSQL como sistema de registro para contactos, conversaciones y mensajes.

## Consecuencias

- **Ganamos:** integridad relacional explícita y un modelo de consulta convencional para el historial del inbox.
- **Resignamos:** migraciones de esquema, backups y operación de base de datos pasan a ser trabajo obligatorio.
- **Implementación:** las migraciones de EF Core modelan contactos, conversaciones y mensajes con una restricción única sobre el `update_id` entrante de Telegram, lo que da persistencia idempotente ante reentregas del webhook. Los mensajes salientes reutilizan el mismo esquema con `update_id` anulable.
