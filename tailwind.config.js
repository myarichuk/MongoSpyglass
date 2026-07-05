/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./MongoSpyglass.Service/**/*.{razor,cshtml,html,js}"
  ],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        "secondary-fixed": "#6ffbbe",
        "surface-bright": "#31394d",
        "surface-container-highest": "#2d3449",
        "inverse-primary": "#00668a",
        "tertiary-fixed": "#ffddb8",
        "on-surface-variant": "#bdc8d1",
        "tertiary-container": "#f59e0b",
        "inverse-surface": "#dae2fd",
        "on-secondary-fixed-variant": "#005236",
        "on-secondary-container": "#00311f",
        "primary-fixed-dim": "#7bd0ff",
        "surface-container-low": "#131b2e",
        "secondary": "#4edea3",
        "outline": "#87929a",
        "on-tertiary-container": "#613b00",
        "on-error": "#690005",
        "error": "#ffb4ab",
        "outline-variant": "#3e484f",
        "primary": "#8ed5ff",
        "on-secondary": "#003824",
        "primary-fixed": "#c4e7ff",
        "tertiary": "#ffc174",
        "on-primary-fixed": "#001e2c",
        "surface-container": "#171f33",
        "on-secondary-fixed": "#002113",
        "on-tertiary-fixed-variant": "#653e00",
        "on-primary-container": "#004965",
        "primary-container": "#38bdf8",
        "secondary-container": "#00a572",
        "on-primary-fixed-variant": "#004c69",
        "background": "#0b1326",
        "inverse-on-surface": "#283044",
        "on-surface": "#dae2fd",
        "on-primary": "#00354a",
        "surface-tint": "#7bd0ff",
        "on-tertiary": "#472a00",
        "tertiary-fixed-dim": "#ffb95f",
        "on-background": "#dae2fd",
        "surface-dim": "#0b1326",
        "on-error-container": "#ffdad6",
        "secondary-fixed-dim": "#4edea3",
        "on-tertiary-fixed": "#2a1700",
        "surface-container-high": "#222a3d",
        "surface-variant": "#2d3449",
        "error-container": "#93000a",
        "surface-container-lowest": "#060e20",
        "surface": "#0b1326"
      },
      borderRadius: {
        DEFAULT: "0.125rem",
        lg: "0.25rem",
        xl: "0.5rem",
        full: "0.75rem"
      },
      spacing: {
        gutter: "8px",
        "cell-padding-y": "4px",
        "cell-padding-x": "8px",
        "component-gap": "4px",
        unit: "4px",
        "container-padding": "16px"
      },
      fontFamily: {
        "ui-body": ["Inter"],
        "code-block": ["JetBrains Mono"],
        "ui-label-sm": ["Inter"],
        "ui-header": ["Inter"],
        "display-mono": ["JetBrains Mono"]
      },
      fontSize: {
        "ui-body": ["13px", { lineHeight: "18px", fontWeight: "400" }],
        "code-block": ["13px", { lineHeight: "18px", fontWeight: "400" }],
        "ui-label-sm": ["11px", { lineHeight: "16px", letterSpacing: "0.02em", fontWeight: "600" }],
        "ui-header": ["16px", { lineHeight: "24px", fontWeight: "600" }],
        "display-mono": ["14px", { lineHeight: "20px", fontWeight: "500" }]
      }
    }
  },
  plugins: [
    require("@tailwindcss/forms"),
    require("@tailwindcss/container-queries")
  ]
};
