# ADR-002: SignalR para las actualizaciones del navegador

**Estado:** Aceptado

## Contexto

El inbox del operador debe recibir actualizaciones de mensajes y conversaciones nuevas sin refrescos manuales repetidos. La API .NET es la fuente de esas actualizaciones; el cliente Angular necesita un ciclo de vida de conexión manejable.

## Alternativas consideradas

- **Polling.** Es más simple, pero agrega solicitudes repetidas y demora en la actualización.
- **WebSockets crudos.** Ofrecen control directo del transporte, pero requieren más protocolo, direccionamiento y código de reconexión propio.
- **SignalR (elegida).** ASP.NET Core SignalR aporta un modelo de hub con entrega dirigida y un cliente JavaScript con reconexión configurable; la reconexión **no está habilitada por defecto** ([resumen de SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0); [cliente JavaScript](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0)). A cambio de esos mecanismos a medida, agrega un límite de conexión propio del framework.

## Decisión

Usar ASP.NET Core SignalR para las notificaciones de la API hacia el navegador. Los datos persistidos siguen siendo la fuente de verdad; una notificación indica al cliente que reconcilie su estado en lugar de reemplazar el almacenamiento.

## Consecuencias

- **Ganamos:** actualizaciones de menor latencia en el navegador con un modelo de cliente/servidor soportado por .NET.
- **Resignamos:** hay que diseñar y probar el ciclo de vida de la conexión, la reconexión, la pertenencia a grupos y, eventualmente, la autorización de las suscripciones; las notificaciones no son entrega durable.
- **Implementación:** el hub `/hubs/inbox` difunde `messageStored` con el ID de conversación después de confirmar un mensaje entrante nuevo y después de persistir una respuesta. Los fallos de notificación se registran y nunca bloquean la persistencia ni el acuse de recibo. El cliente Angular usa `withAutomaticReconnect()` y vuelve a pedir la lista y el hilo abierto. Las suscripciones todavía no están autenticadas ni dirigidas.
