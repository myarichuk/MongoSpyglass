// The repo theme (tailwind.config.js at the root): fonts, type scale and spacing are reused as is; its
// dark palette becomes CSS variables (app.css) so the UI can also have a light theme (03 §10).
const base = require("../../tailwind.config.js");
const v = (name) => `rgb(var(--${name}) / <alpha-value>)`;
module.exports = {
  content: ["./**/*.razor", "./wwwroot/index.html", "./wwwroot/js/*.js"],
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
