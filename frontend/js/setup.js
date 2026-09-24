// First-run setup: asks for the osu! folder when the server could not find it by itself,
// and lets the user pick a different one later ("Change folder").

import { $, esc } from "./core/dom.js";
import { api } from "./core/api.js";

let onConfigured = () => {};
let canDismiss = false;

function showError(message) {
  const box = $("#setupError");
  box.textContent = message ?? "";
  box.classList.toggle("hidden", !message);
}

function renderCandidates(candidates) {
  $("#setupCandidates").innerHTML = candidates.map(candidate => `
    <button type="button" class="candidate" data-path="${esc(candidate.path)}">
      <strong>${esc(candidate.path)}</strong>
      <span class="muted small">${esc(candidate.source)}</span>
    </button>`).join("");
}

function closeOverlay() {
  $("#setupOverlay").classList.add("hidden");
}

/** Shows the dialog. `dismissable` is true when the app already works and the user is just switching folders. */
async function openOverlay({ dismissable }) {
  canDismiss = dismissable;
  $("#setupCancel").classList.toggle("hidden", !dismissable);
  showError(null);
  $("#setupBrowser").classList.add("hidden");
  $("#setupOverlay").classList.remove("hidden");

  const state = await api("/setup");
  $("#setupPath").value = state.path ?? "";
  renderCandidates(state.candidates);
  $("#setupPath").focus();
}

/** Sends the chosen folder to the server; on success closes the dialog and lets the app load. */
async function submitFolder(path) {
  showError(null);
  if (!path.trim()) {
    showError("Enter or choose a folder first.");
    return;
  }

  try {
    await api("/setup", { method: "POST", body: { path: path.trim() } });
  } catch (error) {
    showError(error.message);
    return;
  }
  closeOverlay();
  onConfigured();
}

// ---------- folder browser ----------

let browsingPath = ""; // "" = the list of drives

function renderFolders(listing) {
  browsingPath = listing.path;
  $("#folderPath").textContent = listing.path || "This PC";

  const rows = [];
  if (listing.parent !== null) {
    rows.push(`<div class="folder-row"><button type="button" class="folder-open" data-path="${esc(listing.parent)}">⬆ Up</button></div>`);
  }
  for (const folder of listing.folders) {
    const badge = folder.hasOsuDb ? `<span class="folder-badge">✔ osu! folder</span>` : "";
    const useButton = folder.hasOsuDb
      ? `<button type="button" class="folder-use primary" data-use="${esc(folder.path)}">Use</button>` : "";
    rows.push(`
      <div class="folder-row">
        <button type="button" class="folder-open" data-path="${esc(folder.path)}">📁 ${esc(folder.name)}${badge}</button>
        ${useButton}
      </div>`);
  }
  if (listing.folders.length === 0) rows.push(`<div class="folder-empty muted small">No sub-folders here.</div>`);
  $("#folderList").innerHTML = rows.join("");

  $("#folderSelect").disabled = !listing.path;
  $("#folderHint").textContent = listing.hasOsuDb ? "✔ osu!.db found in this folder" : "";
}

async function browseTo(path) {
  showError(null);
  try {
    renderFolders(await api("/setup/folders?path=" + encodeURIComponent(path)));
  } catch (error) {
    showError(error.message);
  }
}

/** Opens (or closes) the folder browser, starting at whatever is in the path box. */
function toggleBrowser() {
  const panel = $("#setupBrowser");
  const opening = panel.classList.contains("hidden");
  panel.classList.toggle("hidden", !opening);
  if (opening) browseTo($("#setupPath").value.trim());
}

/**
 * Wires up the dialog and checks whether the server already knows the osu! folder.
 * `whenConfigured` runs once it does, either right away or after the user chooses one.
 */
export async function initSetup(whenConfigured) {
  onConfigured = whenConfigured;

  $("#setupCandidates").addEventListener("click", event => {
    const candidate = event.target.closest(".candidate");
    if (candidate) submitFolder(candidate.dataset.path);
  });
  $("#setupUse").addEventListener("click", () => submitFolder($("#setupPath").value));
  $("#setupPath").addEventListener("keydown", event => {
    if (event.key === "Enter") submitFolder($("#setupPath").value);
  });
  $("#setupBrowse").addEventListener("click", toggleBrowser);
  $("#folderList").addEventListener("click", event => {
    const open = event.target.closest("[data-path]");
    const use = event.target.closest("[data-use]");
    if (use) submitFolder(use.dataset.use);
    else if (open) browseTo(open.dataset.path);
  });
  $("#folderSelect").addEventListener("click", () => submitFolder(browsingPath));
  $("#setupCancel").addEventListener("click", closeOverlay);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && canDismiss) closeOverlay();
  });

  $("#changeOsuFolder").addEventListener("click", () => openOverlay({ dismissable: true }));
  // Raised by api.js when a later request finds the folder missing (e.g. an external drive was unplugged).
  document.addEventListener("setup-required", () => openOverlay({ dismissable: false }));

  const state = await api("/setup");
  if (state.configured) whenConfigured();
  else await openOverlay({ dismissable: false });
}
