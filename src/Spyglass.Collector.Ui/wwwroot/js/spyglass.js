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
    // true when the text is on the clipboard; navigator.clipboard needs a secure context, the collector is often plain http on a LAN
    copy: async (text) => {
      try {
        if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; }
      } catch { }
      const ta = document.createElement("textarea");
      ta.value = text;
      ta.setAttribute("readonly", "");
      ta.style.cssText = "position:fixed;top:0;left:0;opacity:0";
      document.body.appendChild(ta);
      ta.select();
      try { return document.execCommand("copy"); } catch { return false; } finally { ta.remove(); }
    },
    // live feed: pause while the user is scrolled away from the top. A passive listener that calls back into .NET only when the
    // at-top state flips, never once per scroll event.
    watchTop(el, ref) {
      if (!el) return;
      let atTop = true;
      const h = () => {
        const now = el.scrollTop <= 4;
        if (now !== atTop) { atTop = now; ref.invokeMethodAsync("SetAtTop", now); }
      };
      el.addEventListener("scroll", h, { passive: true });
      el.__spyglassTop = h;
    },
    unwatchTop(el) {
      if (el && el.__spyglassTop) { el.removeEventListener("scroll", el.__spyglassTop); el.__spyglassTop = null; }
    },
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
