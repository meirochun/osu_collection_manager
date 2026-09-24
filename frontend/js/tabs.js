import { $$ } from "./core/dom.js";

/**
 * Wires up the top navigation. `loaders` maps a tab name (the button's data-tab)
 * to a function that refreshes that tab's content whenever it is opened.
 */
export function initTabs(loaders) {
  const navButtons = $$("nav button");

  navButtons.forEach(button => {
    button.addEventListener("click", () => {
      const tabName = button.dataset.tab;

      navButtons.forEach(other => other.classList.toggle("active", other === button));
      $$(".tab").forEach(tab => tab.classList.toggle("active", tab.id === "tab-" + tabName));

      loaders[tabName]?.();
    });
  });
}
