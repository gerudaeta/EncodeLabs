# ADR-003: Encolar las actualizaciones entrantes de Telegram

**Estado:** Aceptado

## Contexto

Telegram envía actualizaciones a un webhook. El procesamiento contra la base de datos no debería mantener abierta esa solicitud HTTP ni acoplar el camino de reintentos de Telegram a la latencia de la base de datos. El sistema tiene un único host ejecutable de la API .NET, no un despliegue de Worker separado.

## Alternativas consideradas

- **Procesamiento síncrono del webhook a la base de datos.** Elimina el broker, pero expone el tiempo de respuesta del webhook a las fallas de la base de datos.
- **Proceso Worker separado.** Aísla el consumo, pero agrega despliegue y coordinación adicionales en este momento.
- **Consumidor en el mismo host con RabbitMQ (elegida).** Mantiene el handler del webhook corto: valida la solicitud, publica la actualización en RabbitMQ y devuelve éxito solo después de que el broker confirma la aceptación. Un consumidor en segundo plano en el **mismo host de la API** procesa y persiste la actualización, y recién ahí confirma la entrega. Es más simple de entrada, a costa de compartir recursos y ciclo de vida con la API. RabbitMQ distingue las confirmaciones del publicador de los acuses de recibo del consumidor; ninguna de las dos garantiza un procesamiento exactamente una vez de punta a punta ([acuses de recibo y confirmaciones de RabbitMQ](https://www.rabbitmq.com/docs/confirms)). ASP.NET Core soporta tareas en segundo plano dentro de un host ([hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)).

## Decisión

Publicar cada actualización validada en RabbitMQ desde el webhook y procesarla de forma asíncrona con un consumidor en segundo plano dentro del mismo host de la API, que confirma la entrega recién después de un procesamiento exitoso.

## Consecuencias

- **Ganamos:** la recepción HTTP y el procesamiento pueden fallar o reintentarse de forma independiente una vez confirmada la publicación en la cola.
- **Resignamos:** siguen siendo posibles los duplicados; la persistencia idempotente por identidad de `update_id` de Telegram absorbe la reentrega del webhook y los reintentos del consumidor. Las fallas de publicación, el backlog y las fallas del consumidor requieren monitoreo.
- **Implementación:** una cola quorum durable y una cola de mensajes muertos respaldan el flujo entrante, con acuse de recibo manual del consumidor y un límite acotado de cinco reintentos antes de enviar a la cola de mensajes muertos. El webhook se registra automáticamente contra el endpoint HTTPS de ngrok descubierto; `GET /health/ready` informa el estado del registro y de la disponibilidad del consumidor.
