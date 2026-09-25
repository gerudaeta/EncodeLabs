# ADR-004: Tailwind CSS para la interfaz del inbox del operador

**Estado:** Aceptado

## Contexto

La primera interfaz del inbox se entregó con CSS mínimo por componente, funcional pero visualmente básico. La interfaz del operador necesita un lenguaje visual consistente (espaciado, color, tipografía, modo oscuro) que siga siendo fácil de evolucionar a medida que se agregan más pantallas, sin introducir una librería de componentes para una aplicación de una sola pantalla.

## Alternativas consideradas

- **CSS de componentes escrito a mano.** No agrega dependencias, pero obliga a inventar y mantener a mano un sistema de tokens y las convenciones de responsividad y modo oscuro.
- **Librería de componentes (Angular Material, PrimeNG).** Provee widgets ya construidos, pero trae overrides de estilos más pesados y una superficie mayor de la que necesita un inbox de chat.
- **Tailwind CSS v4 (elegida).** Agrega una dependencia de build y listas de clases largas en los templates, a cambio de una escala de diseño compartida y sin hoja de estilos propia que mantener.

## Decisión

Estilizar el cliente Angular con Tailwind CSS v4, integrado a través de PostCSS (`@tailwindcss/postcss` en `.postcssrc.json`), tal como lo soporta el build de Angular ([Angular + Tailwind](https://angular.dev/guide/tailwind); [Tailwind con Angular](https://tailwindcss.com/docs/installation/framework-guides/angular)). La hoja de estilos global solo importa Tailwind; los componentes usan clases utilitarias directamente en sus templates y no llevan CSS propio por componente. El modo oscuro usa la variante `dark:` por defecto de Tailwind, que sigue `prefers-color-scheme`.

## Consecuencias

- **Ganamos:** espaciado, color y comportamiento de modo oscuro consistentes desde una única escala de diseño; los estilos viven junto al markup que afectan; las utilidades no usadas no se emiten.
- **Resignamos:** los templates se vuelven más verbosos; los contribuyentes necesitan familiaridad con Tailwind; el estilo de mensajes según dirección usa variantes arbitrarias (`[&.inbound]:…`) porque la clase `inbound`/`outbound` se asigna desde datos.
- **Implementación:** `tailwindcss`, `@tailwindcss/postcss` y `postcss` son dependencias de desarrollo; `src/styles.css` contiene `@import "tailwindcss"`. Se preservaron los `data-testid` y los textos de estado existentes, por lo que las pruebas de componentes no cambiaron.
