// Bundled (03 §10: no external fetches; the CSP forbids inline script).
(function () {
  const root = document.documentElement;
  const saved = (() => { try { return localStorage.getItem("spyglass.theme"); } catch { return null; } })();
  const dark = saved ? saved === "dark" : !window.matchMedia("(prefers-color-scheme: light)").matches;
  root.classList.toggle("dark", dark);

  window.spyglass = {
    toggleTheme() {
      const d = !root.classList.contains("dark");
      root.classList.toggle("dark", d);
      try { localStorage.setItem("spyglass.theme", d ? "dark" : "light"); } catch { }
      return d;
    },
    isDark: () => root.classList.contains("dark"),
    copy: (text) => navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.resolve(),
    // live feed: paused while the user is scrolled away from the top
    scrollTop: (el) => el ? el.scrollTop : 0,
  };

  // Tables: rows with data-href are focusable; ↑/↓ move, Enter opens, c copies the focused cell's row text.
  document.addEventListener("keydown", (e) => {
    const row = document.activeElement && document.activeElement.closest && document.activeElement.closest("tr[data-href]");
    if (!row) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      const next = e.key === "ArrowDown" ? row.nextElementSibling : row.previousElementSibling;
      if (next && next.matches("tr[data-href]")) { next.focus(); e.preventDefault(); }
    } else if (e.key === "Enter") {
      row.querySelector("a")?.click();
      e.preventDefault();
    } else if (e.key === "c" && !e.ctrlKey && !e.metaKey) {
      window.spyglass.copy(row.innerText.replace(/\t/g, " "));
    }
  });
})();
