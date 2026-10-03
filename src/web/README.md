# WarrantyOS web (SPA)

React 19 + TypeScript (strict) + Vite single-page app for the claimant portal and the staff
workspace. Its look and behaviour follow `specs/001-ai-claim-adjudication/ui-design.md`.

| Script | What it does |
|--------|--------------|
| `npm run dev` | Vite dev server on port 5173; `/api` is proxied to the API with the original Host header, so `aurora.localhost:5173` and `borealis.localhost:5173` act as tenant claimant channels |
| `npm run build` | Type-check (`tsc -b`) and production build to `dist/` |
| `npm test` | Vitest (jsdom + Testing Library + MSW); tests live in `tests/` |
| `npm run lint` | ESLint, zero warnings allowed |
| `npm run gen:api` | Regenerate `src/shared/api/schema.d.ts` from the OpenAPI contract |

`package.json` overrides `openapi-typescript`'s TypeScript peer to the project's TypeScript 6
(its published peer range still says `^5`); `gen:api` is verified to work with it.

Run everything (API, Keycloak, databases and this app) through the Aspire AppHost — see
`specs/001-ai-claim-adjudication/quickstart.md`.
