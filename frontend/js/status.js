import { $ } from "./core/dom.js";
import { api } from "./core/api.js";

/** Updates the header line (player, library size) and the "osu! is running" warning bar. */
export async function refreshStatus() {
  try {
    const status = await api("/status");
    if (!status.configured) {
      $("#status").textContent = "osu! folder not set";
      return;
    }

    let text = `${status.player ?? "?"} · ${status.beatmaps.toLocaleString()} difficulties · ${status.sets.toLocaleString()} sets`;
    if (status.pendingImports) {
      text += ` · ${status.pendingImports} .osz waiting for import`;
    }
    $("#status").textContent = text;
    $("#gameWarning").classList.toggle("hidden", !status.gameRunning);

    // Convenience: pre-fill the planner's username with the player found in the osu! install.
    const usernameInput = $("#planForm [name=username]");
    if (!usernameInput.value && status.player) {
      usernameInput.value = status.player;
      usernameInput.dispatchEvent(new Event("change")); // lets the planner load the profile right away
    }
  } catch (error) {
    if (error.setupRequired) return;
    $("#status").textContent = "Cannot read osu! install: " + error.message;
  }
}
