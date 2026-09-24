// The Training Planner form: what to train, how hard, advanced settings, and remembering them.

import { $, $$, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { loadJson, saveJson } from "./core/storage.js";

const STORAGE_KEY = "planner.form.v1";
const SECONDS_PER_TAG_LOOKUP = 1.1;

/** One-click selections. An empty list means "everything". */
const PRESETS = {
  all: [],
  aim: ["flow", "jumps"],
  tech: ["tech"],
  speed: ["speed", "stamina"],
};

let categories = [];
let comfortStars = null;

const form = () => $("#planForm");
const field = name => form().elements[name];

// ---------- focus chips ----------

function selectedCategoryIds() {
  return $$("#focusChips input:checked").map(input => input.value);
}

function renderChips() {
  $("#focusChips").innerHTML = categories.map(category => `
    <label class="chip" title="${esc(category.description)}">
      <input type="checkbox" value="${esc(category.id)}">
      <span>${esc(category.name)}</span>
    </label>`).join("");
}

function setSelection(ids) {
  $$("#focusChips input").forEach(input => { input.checked = ids.includes(input.value); });
}

function sameSelection(a, b) {
  return a.length === b.length && a.every(id => b.includes(id));
}

// ---------- derived text ----------

/** Shows which star range each chosen category will use, once the player's comfort level is known. */
function renderDifficultySummary() {
  const offset = Number(field("starOffset").value);
  const direction = offset > 0 ? "harder" : "easier";
  const shift = offset === 0 ? "Centered on your comfort level" : `${Math.abs(offset).toFixed(1)}★ ${direction} than your comfort level`;

  if (comfortStars === null) {
    $("#offsetSummary").innerHTML = `<div>${shift}</div><div>Enter your username to see the star ranges.</div>`;
    return;
  }

  const chosenIds = selectedCategoryIds();
  const chosen = categories.filter(category => chosenIds.length === 0 || chosenIds.includes(category.id));
  const ranges = chosen.map(category => {
    const low = (comfortStars + category.minOffset + offset).toFixed(1);
    const high = (comfortStars + category.maxOffset + offset).toFixed(1);
    return `<div>${esc(category.name)}: ${low}–${high}★</div>`;
  });
  $("#offsetSummary").innerHTML = `<div>${shift} (comfort ${comfortStars.toFixed(2)}★)</div>${ranges.join("")}`;
}

function renderLookupEstimate() {
  const lookups = Number(field("tagLookups").value) || 0;
  const minutes = Math.max(1, Math.round(lookups * SECONDS_PER_TAG_LOOKUP / 60));
  $("#lookupEstimate").textContent = `(up to ≈ ${minutes} min)`;
}

/** Keeps the focus-dependent parts of the form in sync with the current selection. */
function refreshFocusUi() {
  const ids = selectedCategoryIds();

  // Splitting into tiers only makes sense when training one specific pattern.
  const canUseTiers = ids.length === 1 && ids[0] !== "sight";
  $("#tiersRow").classList.toggle("hidden", !canUseTiers);

  $$("#focusPresets button").forEach(button => {
    button.classList.toggle("active", sameSelection(ids, PRESETS[button.dataset.preset]));
  });

  renderDifficultySummary();
}

// ---------- remembering the form ----------

function saveState() {
  const fields = {};
  for (const element of form().elements) {
    if (!element.name) continue;
    fields[element.name] = element.type === "checkbox" ? element.checked : element.value;
  }
  saveJson(STORAGE_KEY, { fields, categories: selectedCategoryIds() });
}

function restoreState() {
  const state = loadJson(STORAGE_KEY);
  if (!state) return;

  for (const [name, value] of Object.entries(state.fields ?? {})) {
    const element = field(name);
    if (!element) continue;
    if (element.type === "checkbox") element.checked = Boolean(value);
    else if (name !== "username" || value) element.value = value; // never blank out the auto-detected player name
  }
  setSelection(state.categories ?? []);
}

// ---------- public API ----------

/** Called by the planner once the profile is known (or unknown, with null) so star ranges can be shown. */
export function setComfortStars(stars) {
  comfortStars = stars;
  renderDifficultySummary();
}

/** Reads the form into the request body the server expects. */
export function readPlanOptions() {
  const data = new FormData(form());

  const revisitBeatmapIds = data.get("revisitBeatmapIds")
    .split(/[,\s]+/)
    .filter(Boolean)
    .map(Number)
    .filter(id => id > 0);

  const queries = data.get("queries")
    .split("\n")
    .map(line => line.trim())
    .filter(Boolean);

  const tiersVisible = !$("#tiersRow").classList.contains("hidden");

  return {
    username: data.get("username").trim(),
    prefix: data.get("prefix").trim() || "Training",
    categories: selectedCategoryIds(),
    starOffset: Number(data.get("starOffset")),
    tiers: tiersVisible ? Number(data.get("tiers")) : 1,
    includeRevisit: field("includeRevisit").checked,
    includeFarm: field("includeFarm").checked,
    mapsPerCollection: Number(data.get("mapsPerCollection")),
    sightReadingMaps: Number(data.get("sightReadingMaps")),
    tagLookups: Number(data.get("tagLookups")),
    minTagVotes: Number(data.get("minTagVotes")) || 2,
    revisitBelowAccuracy: Number(data.get("revisitBelowAccuracy")),
    revisitBeatmapIds,
    queries,
  };
}

/** Builds the focus chips from the server's category list, then restores the last-used settings. */
export async function initPlannerForm() {
  categories = await api("/plan/categories");
  renderChips();
  restoreState();
  renderLookupEstimate();
  refreshFocusUi();

  $("#focusPresets").addEventListener("click", event => {
    const preset = event.target.dataset.preset;
    if (preset === undefined) return;
    setSelection(PRESETS[preset]);
    refreshFocusUi();
    saveState();
  });

  form().addEventListener("input", () => {
    renderLookupEstimate();
    refreshFocusUi();
    saveState();
  });
}
