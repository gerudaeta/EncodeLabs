# ADR-005: Cache de la lista de conversaciones

**Estado:** Aceptado

## Contexto

`GET /api/conversations` (sin cursor) es la lectura más caliente del sistema: se carga en cada sesión del operador y cada pestaña la vuelve a pedir ante cada evento realtime `messageStored` (mensaje entrante, respuesta o marcado como leído), repitiendo la consulta y la agregación de no leídos. Los datos desactualizados no son aceptables, pero una cache corta y bien invalidada elimina la carga repetida sin agregar infraestructura.

## Alternativas consideradas

- **Sin cache.** Más simple; se descarta porque la consulta se repite en cada evento de cada pestaña sin beneficio medido.
- **Redis como L2 distribuido.** Necesario para instancias múltiples con cache/invalidación compartida. Se descarta por ahora: agrega un servicio a Compose y nuevos modos de falla sin necesidad medida en un despliegue de instancia única; `HybridCache` ya aísla esto en configuración, así que agregarlo después no cambiaría código de aplicación.
- **Output caching (`AddOutputCache`).** Más simple de conectar, pero su invalidación por tags es más gruesa para eliminar exactamente una página desde un decorador de notificador.
- **`HybridCache` con clave exacta (elegida).** Cachea solo la primera página (sin cursor, tamaño por defecto), en memoria, sin L2 distribuido, acorde a la instancia única. Páginas con cursor o `limit` no default leen siempre la base de datos.

## Decisión

Usar `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`, `AddHybridCache()`) en memoria para la primera página, con invalidación por evento: `CacheInvalidatingInboxNotifier` decora el `IInboxNotifier` existente y elimina la entrada por clave exacta *antes* de delegar al notificador real (SignalR). Los tres caminos de escritura que afectan la lista — mensaje entrante, respuesta persistida, conversación leída — ya pasan por esa única llamada, así que un decorador los invalida a los tres sin tocar los casos de uso. Al evictar primero, el refetch de realtime nunca ve datos viejos. Cada llamador ya envuelve la notificación en un try/catch que absorbe fallas, cubriendo también una falla de evicción. Se mantiene una expiración absoluta de 30 segundos como red de seguridad, no como mecanismo principal.

La evicción usa clave exacta (`RemoveAsync`), no tags: `RemoveByTagAsync` es por timestamp y se verificó que no eliminaba entradas creadas en el mismo tick que la invalidación (limitación real de HybridCache, dotnet/aspnetcore#58857), causando una prueba intermitente. La clave exacta no tiene esa condición de carrera.

## Consecuencias

- **Ganamos:** el camino caliente se sirve desde memoria entre escrituras; invalidación centralizada en un decorador; sin infraestructura nueva.
- **Resignamos:** un `limit` no default en la primera página evita la cache (aceptable: la interfaz nunca lo hace).
- **Riesgo:** con múltiples instancias, cache y conexiones SignalR quedarían locales a cada una; haría falta Redis como L2 y un backplane de SignalR. Ninguna es necesaria hoy.
