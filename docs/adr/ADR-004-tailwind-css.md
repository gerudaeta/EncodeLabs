# ADR-004: Tailwind CSS for the operator inbox UI

**Status:** Accepted and implemented.

## Context

The first inbox UI shipped with minimal component-scoped CSS that was functional but visually bare. The operator UI needs a consistent visual language (spacing, color, typography, dark mode) that stays easy to evolve as more screens are added, without introducing a component library for a single-screen app.

## Decision

Style the Angular client with Tailwind CSS v4, integrated through PostCSS (`@tailwindcss/postcss` in `.postcssrc.json`) as supported by the Angular build ([Angular + Tailwind](https://angular.dev/guide/tailwind); [Tailwind with Angular](https://tailwindcss.com/docs/installation/framework-guides/angular)). The global stylesheet only imports Tailwind; components use utility classes directly in their templates and carry no component-scoped CSS. Dark mode uses Tailwind's default `dark:` variant, which follows `prefers-color-scheme`.

## Alternatives and tradeoffs

Hand-written component CSS has no dependency but requires inventing and maintaining a token system and responsive/dark-mode conventions by hand. A component library (Angular Material, PrimeNG) provides ready-made widgets but brings heavier styling overrides and a larger surface than a chat inbox needs. Tailwind adds a build-time dependency and long class lists in templates, in exchange for a shared design scale and no custom stylesheet to maintain.

## Consequences

- **Positive:** Consistent spacing, color, and dark-mode behavior from one design scale; styles live next to the markup they affect; unused utilities are not emitted.
- **Negative:** Templates become more verbose; contributors need Tailwind familiarity; direction-dependent message styling uses arbitrary variants (`[&.inbound]:…`) because the `inbound`/`outbound` class is bound from data.
- **Implementation:** `tailwindcss`, `@tailwindcss/postcss`, and `postcss` are dev dependencies; `src/styles.css` contains `@import "tailwindcss"`. Existing `data-testid` hooks and state texts were preserved, so component tests are unchanged.
