// Renders ../status.json (written by tools/status.py).
// Every value from the file goes in through textContent, never innerHTML:
// task titles and summaries come from chat and are untrusted.
(function () {
  "use strict";
  const STATUSES = ["open", "acked", "done", "blocked", "rejected"];
  // ?demo loads the bundled fixture. No other source is accepted, so the page
  // can't be pointed at an arbitrary URL.
  const demo = new URLSearchParams(location.search).has("demo");
  const src = demo ? "sample.json" : "../status.json";

  const $ = (id) => document.getElementById(id);
  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }
  function when(iso) {
    const d = new Date(iso);
    return isNaN(d) ? String(iso || "?") : d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
  }
  function banner(text, withDemoLink) {
    const b = $("banner");
    b.textContent = text;
    if (withDemoLink) {
      b.append(" ");
      const a = el("a", null, "See the demo with sample data.");
      a.href = "?demo";
      b.append(a);
    }
    b.hidden = false;
  }

  function render(data) {
    if (!data || typeof data !== "object" || !Array.isArray(data.tasks)) {
      banner("status.json is malformed, so nothing is shown.", true);
      return;
    }
    if (demo) banner("Demo: this is bundled sample data, not the live log.", false);

    const meta = $("meta");
    const g = el("span"); g.append("Generated ", el("b", null, when(data.generated)));
    meta.append(g);
    if (Array.isArray(data.publish_repos)) {
      const r = el("span"); r.append("Repos ", el("b", null, data.publish_repos.join(", ") || "none"));
      meta.append(r);
    }
    meta.hidden = false;

    const counts = $("counts");
    const c = data.counts && typeof data.counts === "object" ? data.counts : {};
    for (const s of STATUSES) {
      const box = el("div", "count");
      box.append(el("span", "v", Number(c[s]) || 0), s);
      counts.append(box);
    }
    counts.hidden = false;

    const list = $("tasks");
    if (!data.tasks.length) {
      list.append(el("p", "empty", "No published tasks. Only tasks tagged with a listed repo appear here."));
    }
    for (const t of data.tasks) {
      if (!t || typeof t !== "object") continue;
      const st = STATUSES.includes(t.status) ? t.status : "open";
      const card = el("article", "task s-" + st);
      const row = el("div", "row");
      row.append(el("span", "id", t.id), el("span", "title", t.title || "(untitled)"), el("span", "st", st));
      card.append(row);
      const parts = [];
      if (t.from) parts.push(t.from + " → " + (t.to || "?"));
      if (t.repo) parts.push(t.repo);
      if (t.created) parts.push("opened " + when(t.created));
      if (t.updated && t.updated !== t.created) parts.push("updated " + when(t.updated));
      card.append(el("div", "sub", parts.join(" · ")));
      if (t.summary) card.append(el("p", "summary", t.summary));
      list.append(card);
    }

    const cov = Array.isArray(data.coverage) ? data.coverage.filter((w) => w && w.from && w.to) : [];
    if (cov.length) {
      const ul = $("coverage"), tl = $("timeline");
      const t0 = Date.parse(cov[0].from), t1 = Date.parse(cov[cov.length - 1].to);
      const span = Math.max(1, t1 - t0);
      for (const w of cov) {
        ul.append(el("li", null, when(w.from) + " → " + when(w.to)));
        const a = Date.parse(w.from), b = Date.parse(w.to);
        if (isFinite(a) && isFinite(b)) {
          const bar = el("span");
          bar.style.left = ((a - t0) / span) * 100 + "%";
          bar.style.width = ((b - a) / span) * 100 + "%";
          tl.append(bar);
        }
      }
      $("cov-section").hidden = false;
    }
  }

  fetch(src, { cache: "no-store" })
    .then((r) => {
      if (r.status === 404) throw Object.assign(new Error("missing"), { missing: true });
      if (!r.ok) throw new Error("HTTP " + r.status);
      return r.json();
    })
    .then(render)
    .catch((e) => {
      if (e && e.missing) banner("No status has been published yet.", !demo);
      else banner("Couldn't load status (" + (e && e.message ? e.message : e) + ").", !demo);
    });
})();
