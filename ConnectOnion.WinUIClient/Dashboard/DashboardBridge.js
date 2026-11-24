(() => {
  const token = "__TOKEN__";

  function find(selector) {
    try {
      return document.querySelector(selector);
    } catch {
      return null;
    }
  }

  document.addEventListener("click", event => {
    const element = event.target instanceof Element ? event.target : null;
    const link = element?.closest("a[href]");
    if (link && !link.getAttribute("href").trim().startsWith("#")) {
      event.preventDefault();
    }

    const button = element?.closest("[data-ochat-skill]");
    if (!button) return;

    event.preventDefault();
    const skill = button.getAttribute("data-ochat-skill").trim();
    if (!skill) return;

    parent.postMessage({
      type: "run_skill",
      token,
      skill,
      args: button.getAttribute("data-ochat-args") || ""
    }, "*");
  }, true);

  document.addEventListener("submit", event => event.preventDefault(), true);

  class CoFilter extends HTMLElement {
    connectedCallback() {
      if (this.querySelector("input")) return;

      const input = document.createElement("input");
      input.type = "search";
      input.placeholder = this.getAttribute("placeholder") || "Filter…";
      input.setAttribute("aria-label", input.placeholder);
      input.style.cssText = `
        box-sizing: border-box;
        width: 100%;
        padding: 8px 10px;
        border: 1px solid GrayText;
        border-radius: 6px;
        background: Field;
        color: FieldText;
        font: inherit;
      `;

      const target = find(this.getAttribute("target") || "");
      input.addEventListener("input", () => {
        if (!target) return;

        const query = input.value.trim().toLocaleLowerCase();
        for (const item of target.children) {
          item.hidden = query.length > 0
            && !item.textContent.toLocaleLowerCase().includes(query);
        }
      });

      this.append(input);
    }
  }

  class CoTable extends HTMLElement {
    connectedCallback() {
      const table = find(this.getAttribute("target") || "");
      const body = table?.tBodies[0];
      if (!table || !body || table.dataset.coSortable) return;

      table.dataset.coSortable = "true";
      for (const [column, header] of [...table.querySelectorAll("thead th")].entries()) {
        header.tabIndex = 0;
        header.setAttribute("role", "button");

        let ascending = true;
        const sort = () => {
          const rows = [...body.rows].sort((left, right) => {
            const leftText = left.cells[column]?.textContent.trim() || "";
            const rightText = right.cells[column]?.textContent.trim() || "";
            const result = leftText.localeCompare(rightText, undefined, {
              numeric: true,
              sensitivity: "base"
            });
            return result * (ascending ? 1 : -1);
          });

          body.append(...rows);
          ascending = !ascending;
        };

        header.addEventListener("click", sort);
        header.addEventListener("keydown", event => {
          if (event.key !== "Enter" && event.key !== " ") return;

          event.preventDefault();
          sort();
        });
      }
    }
  }

  if (!customElements.get("co-filter")) {
    customElements.define("co-filter", CoFilter);
  }
  if (!customElements.get("co-table")) {
    customElements.define("co-table", CoTable);
  }
})();
