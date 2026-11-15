// The repo theme (tailwind.config.js at the root): fonts, type scale and spacing are reused as is; its
// dark palette becomes CSS variables (app.css) so the UI can also have a light theme (03 §10).
const path = require("path");
const base = require("../../tailwind.config.js");
const v = (name) => `rgb(var(--${name}) / <alpha-value>)`;
module.exports = {
  // Globs are anchored to this file, not to the cwd: `npm run build:css` (run from the repo root) and
  // tools/collector/publish.sh (run from the UI dir) must scan the same files and produce the same app.css.
  content: [
    path.join(__dirname, "**/*.razor"),
    path.join(__dirname, "wwwroot/index.html"),
    path.join(__dirname, "wwwroot/js/*.js"),
  ],
  darkMode: "class",
  theme: {
    extend: {
      ...base.theme.extend,
      colors: {
        surface: v("surface"), "surface-2": v("surface-2"), "surface-3": v("surface-3"),
        line: v("line"), ink: v("ink"), muted: v("muted"),
        accent: v("accent"), ok: v("ok"), warn: v("warn"), bad: v("bad"),
      },
    },
  },
  plugins: base.plugins,
};
