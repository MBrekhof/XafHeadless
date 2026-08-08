# Handoff: Modernist theme for XafHeadless

## Overview
Restyle the XafHeadless Blazor client (repo `MBrekhof/XafHeadless`, branch `master`) with the **Modernist** design system: flat, architectural, all-Archivo, red-on-light-grey, zero corner radius, strong 2px rules, flush-left everything. This validates the styled direction chosen in the feasibility study prototype.

## About the Design Files
Files under `reference/` are **design references created in HTML** — a prototype showing intended look and behavior, not production code. The task is to **recreate this look inside the existing Blazor app** (Bootstrap 5 + DevExpress Blazor components + the `.xaf-*` chrome classes in `XafHeadless.Web/wwwroot/app.css`) using its established patterns. Do not port the HTML prototype itself.

## Fidelity
**High-fidelity.** `reference/XafHeadless Prototype (Modernist).dc.html` shows final colors, typography, spacing, and states. Match it closely; exact tokens are in `modernist-theme.css` and `reference/modernist-design-system.css`.

## Implementation order
1. Copy `modernist-theme.css` to `XafHeadless.Web/wwwroot/modernist-theme.css`.
2. Link it in the app shell (`App.razor` in XafHeadless.Web/Components) **after** `bootstrap.min.css` and `app.css` so its overrides win:
   `<link rel="stylesheet" href="modernist-theme.css" />`
3. Verify the DevExpress selectors: the `.dxbl-*` selectors in the theme file target DevExpress Blazor's rendered markup (`DxTextBox`, `DxButton`, `DxGrid`). Class names vary by DevExpress version — inspect the rendered DOM and adjust selectors where they don't bite. Alternatively, switch the DevExpress theme to its Bootstrap-integrated mode so the `--bs-*` remap in `:root` carries through automatically.
4. Walk each screen against the prototype (screen list below) and patch gaps.

## Screens / Views
Reference each in the prototype file; repo sources per the project's screen map.

- **Login** (`XafHeadless.Components/Pages/Login.razor`, `.xaf-login-card` in app.css)
  - Card: 2px solid divider border, **no** border radius, **no** shadow, page-ground background (#f3f2f2).
  - Title: Archivo 800, flush left (remove `text-center`; the theme also forces it).
  - Inputs: surface fill #eae9e9, 1px divider border, square corners, red caret.
  - Login button: solid #ec3013 fill, white-ish label (#f3f2f2), **label flush left**, hover #dd2b0f, active #ae1800.
  - Error text: #ae1800 (accent-700 — accent-500 is too low-contrast for body-size text).
- **Top bar** (`XafHeadless.Components/Layout/MainLayout.razor`, `.xaf-topbar`)
  - Ground background (not blue), ink text, 2px divider rule below, no shadow. Brand in Archivo 800 / 18px.
- **Sidebar nav** (`XafHeadless.Components/Layout/NavMenu.razor`, `.xaf-navmenu`)
  - Ground background, 2px divider rule on the right. Links: ink, 14px, square, hover = 7% ink tint.
  - Active item: **not** a filled pill — accent-red text, Archivo 800, 3px accent rule inset on the left edge.
- **List view (grid)** (`XafHeadless.Components/Services/GridBinding.cs` drives DxGrid)
  - Header row: 11px uppercase, 0.08em tracking, 60% ink, 2px divider rule under headers.
  - Rows: 1px divider rules, hover = 4% ink tint. No zebra striping, no rounded container.
- **Detail view + save** (`DetailBinding.cs`, `.xaf-item`)
  - Field labels: 12px, 70% ink, above the input. Inputs as on Login.
  - Save (primary command): solid accent button, flush-left label. Secondary commands: transparent with 1px divider border.
  - Validation: invalid outline #ae1800, message text #ae1800.
- **Images** (`.xaf-image`): square corners, `grayscale(1) contrast(1.08)` — Modernist prints all photography black and white.

## Interactions & Behavior
- Hover: filled buttons step one ramp down (#dd2b0f); outlined/ghost elements get a 7% ink tint. Pressed: one more step (#ae1800) / 14% tint.
- Keyboard focus: `2px solid #ec3013` outline, offset 2px — never the Bootstrap blue glow (`box-shadow: none` on focus).
- No transitions on nav/hover — the system is flat and immediate.
- Disabled controls: 45% opacity.
- All existing behavior (auth flow, redirects, grid binding, save/validation) is untouched — this is styling only.

## Design Tokens
Full set in `modernist-theme.css` `:root`. Key values:
- Ground #f3f2f2 · Surface #eae9e9 · Ink #201e1d · Accent #ec3013
- Accent ramp: 600 #dd2b0f (hover) · 700 #ae1800 (pressed, accent text) · 100 #fff2ef (tint fills)
- Divider: `color-mix(in srgb, #201e1d 40%, transparent)`, drawn at 2px for major rules, 1px for row rules
- Type: Archivo everywhere (Google Fonts, weights 400/600/800); headings 800, −0.015em; body 15px/1.55; buttons 14px Archivo 800
- Spacing: 4/8/12/16/24/32 px scale · Radius: **0 everywhere** · Shadows: avoid; the system is flat
- Icons: Lucide (https://lucide.dev), if icons are added

## Assets
- Archivo via Google Fonts (imported at the top of `modernist-theme.css`); self-host if offline use matters.
- No images or icon assets required.

## Files
- `modernist-theme.css` — the drop-in stylesheet (step 1). Bootstrap-variable remap + `.xaf-*` and `.dxbl-*` overrides.
- `reference/XafHeadless Prototype (Modernist).dc.html` — the hi-fi prototype (open in a browser).
- `reference/modernist-design-system.css` — the full design-system component layer (buttons, forms, cards, tags, nav, table, dialog) for any pattern not covered above.

## Rules of the system (don't break these)
- No rounded corners, anywhere. No centered button labels or hero copy. No color in photography.
- Accent red is for the primary action and small emphasis only; the UI is mostly ink on ground.
- Dividers stay strong (2px) — don't soften them to hairlines or replace them with whitespace.
