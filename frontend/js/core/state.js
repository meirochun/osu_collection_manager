import { $, esc } from "./dom.js";
import { api } from "./api.js";

/** Collection names + counts as last returned by the server (shared by several tabs). */
export let collections = [];

/**
 * Reloads the list of collections and refreshes every control that offers them:
 * the Library "add to collection" dropdown and the Search download autocomplete.
 */
export async function loadCollectionNames() {
  collections = await api("/collections");

  $("#libTarget").innerHTML =
    `<option value="">Add to collection…</option>` +
    collections.map(collection => `<option>${esc(collection.name)}</option>`).join("");

  $("#collNames").innerHTML = collections
    .map(collection => `<option value="${esc(collection.name)}">`)
    .join("");
}
