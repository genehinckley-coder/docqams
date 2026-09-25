# Design-system CSS

Copied **unchanged** from the Claude Design project *Ice Blue with Packers Highlights Design System*
(project id `741ce686-82d8-4ea2-9e43-990c8752d45a`), pulled through DesignSync on 2026-09-14.
Do not edit these files; re-pull them instead. App-specific rules belong in `wwwroot/app.css` or in a
component's scoped `.razor.css`.

- `styles.css` is the entry point: it only `@import`s the others, by relative path.
- `tokens/fonts.css` loads the four webfonts from Google Fonts, so fonts need internet access, as the API
  already does. Without it the stacks fall back to Georgia and the system sans.
- `components/*/*.css` holds one file per group (core, forms, display, navigation, feedback), not one per
  component. The navigation (Tabs) and feedback (Dialog, Toast) rules are unused but kept, so that the copy
  stays exact.
- The design's `_ds_bundle.js` (its React components) is deliberately not shipped. The Razor primitives in
  `Components/Ds/` emit the same class names.

The six files the DocQA Prototype design (`plans/DocQA Prototype.dc.html`, project
`a364b4bf-ab9c-4b4a-8a69-bd156cff3e38`) embeds under `_ds/` were compared with these on the same day and are
identical: `tokens/colors.css`, `tokens/typography.css`, and the core, display, forms and feedback component
files.
