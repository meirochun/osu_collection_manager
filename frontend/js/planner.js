// Training Planner tab: build a set of practice collections from a player's profile, review, then apply.
// The form itself lives in planner-form.js.

import { $, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { toast, guard } from "./core/toast.js";
import { formatLength, starBadge, coverImage, beatmapLink } from "./core/format.js";
import { loadCollectionNames } from "./core/state.js";
import { refreshStatus } from "./status.js";
import { trackInline } from "./jobs.js";
import { initPlannerForm, readPlanOptions, setComfortStars } from "./planner-form.js";

const MAX_PROFILE_PLAYS_SHOWN = 50;
const MAX_TAGS_SHOWN = 4;

let currentPlan = null;
let loadedProfileUser = null;

// ---------- profile ----------

const loadProfile = guard(async username => {
  $("#profileBox").innerHTML = `<p class="muted small">Loading profile…</p>`;
  const profile = await api("/profile/" + encodeURIComponent(username));
  loadedProfileUser = username;
  setComfortStars(profile.comfortStars);

  const topPlays = profile.topPlays.slice(0, MAX_PROFILE_PLAYS_SHOWN).map(({ play }) => `
    <div class="small top-play">
      <b>${Math.round(play.pp)}pp</b> ${play.accuracy.toFixed(2)}% ${play.mods ? "+" + esc(play.mods) : ""}
      · ${esc(play.title)} [${esc(play.version)}] ${play.stars.toFixed(2)}★
    </div>`).join("");

  $("#profileBox").innerHTML = `
    <div class="profile">
      <div class="big">${esc(profile.username)}</div>
      <div>${Math.round(profile.pp).toLocaleString()}pp · #${profile.globalRank?.toLocaleString() ?? "?"} global · #${profile.countryRank?.toLocaleString() ?? "?"} country</div>
      <div>${profile.accuracy.toFixed(2)}% accuracy · comfort ≈ ${starBadge(profile.comfortStars)}</div>
      <details>
        <summary class="muted">Top ${profile.topPlays.length} plays</summary>
        ${topPlays}
      </details>
    </div>`;
});

/** Loads the profile once per username, so star ranges show up as soon as the name is entered. */
function ensureProfile(username) {
  if (username && username !== loadedProfileUser) loadProfile(username);
}

// ---------- plan rendering ----------

function renderPlanMap(map, collectionIndex, mapIndex) {
  const installedNote = map.installed ? `<span class="installed">✔ installed</span>` : "";
  const topTags = Object.entries(map.tags)
    .sort((a, b) => b[1] - a[1])
    .slice(0, MAX_TAGS_SHOWN)
    .map(([tag, count]) => `<span class="tag">${esc(tag)} ${count}</span>`)
    .join("");

  // Which skill this map was chosen for, and how much of its community tag votes back that up.
  const familyChip = map.family
    ? `<span class="tag family" title="Share of this map's tag votes that point to this skill">${esc(map.family)} ${Math.round(map.familyShare * 100)}%</span>`
    : "";

  return `
    <tr>
      <td><button class="link danger" data-drop="${collectionIndex}:${mapIndex}" title="Remove from plan">✕</button></td>
      <td>${coverImage(map.setId, map.installed ? map.checksum : null)}</td>
      <td>
        <div class="title">${beatmapLink(map, `${esc(map.artist)} - ${esc(map.title)}`)}</div>
        <div class="sub">[${esc(map.version)}] by ${esc(map.creator)} ${installedNote}</div>
        <div>${familyChip}${topTags}</div>
      </td>
      <td>${starBadge(map.stars)}</td>
      <td>${Math.round(map.bpm)}</td>
      <td>${formatLength(map.length)}</td>
    </tr>`;
}

function renderPlanCollection(collection, collectionIndex) {
  const rows = collection.maps.map((map, mapIndex) => renderPlanMap(map, collectionIndex, mapIndex)).join("")
    || `<tr><td class="muted">No maps matched. Try more tag lookups or other queries.</td></tr>`;

  return `
    <div class="plan-coll ${collection.included ? "" : "excluded"}">
      <div class="plan-coll-head">
        <input type="checkbox" data-include="${collectionIndex}" ${collection.included ? "checked" : ""}
               title="Include this collection when applying the plan">
        <input class="coll-name" data-rename="${collectionIndex}" value="${esc(collection.name)}" aria-label="Collection name">
        <span class="muted small">(${collection.maps.length})</span>
      </div>
      <div class="sub plan-desc">${esc(collection.description)}</div>
      <table class="maps"><tbody>${rows}</tbody></table>
    </div>`;
}

/** Collections that will actually be written: ticked and not empty. */
function collectionsToApply() {
  return currentPlan.collections.filter(collection => collection.included && collection.maps.length > 0);
}

function countSetsToDownload(collections) {
  const setIds = collections.flatMap(collection => collection.maps.filter(map => !map.installed).map(map => map.setId));
  return new Set(setIds).size;
}

function renderPlan() {
  const plan = currentPlan;
  if (!plan) return;

  const chosen = collectionsToApply();
  const setsToDownload = countSetsToDownload(chosen);

  $("#planOut").innerHTML = `
    <div class="toolbar">
      <div><b>${esc(plan.username)}</b> · comfort ${starBadge(plan.baseStars)} · ${plan.candidateSets.toLocaleString()} candidate sets, ${plan.taggedSets} tagged</div>
      <span class="spacer"></span>
      <span class="muted">${chosen.length} collections · ${setsToDownload} sets to download</span>
      <button id="applyPlan" class="primary" ${chosen.length ? "" : "disabled"}>Download &amp; create collections</button>
    </div>
    ${plan.collections.map(renderPlanCollection).join("")}`;

  $("#applyPlan").onclick = guard(applyPlan);
}

async function applyPlan() {
  const chosen = collectionsToApply();

  // Writing two collections with one name would silently drop the first one.
  const names = chosen.map(collection => collection.name.trim());
  if (names.some(name => !name) || new Set(names).size !== names.length) {
    throw new Error("Every collection needs a different, non-empty name.");
  }

  const question = `Download ${countSetsToDownload(chosen)} sets into your Songs folder and write ${chosen.length} collections to collection.db?`;
  if (!confirm(question)) return;

  // `included` is a UI-only flag; the server gets just the collections that were ticked.
  const body = { ...currentPlan, collections: chosen.map(({ included, ...collection }) => collection) };
  const { id } = await api("/plan/apply", { method: "POST", body });

  trackInline(id, "#planProgress", () => {
    toast("Collections created. Start osu! to import the new maps.");
    refreshStatus();
    loadCollectionNames();
  });
}

// ---------- init ----------

export async function initPlanner() {
  const usernameInput = $("#planForm [name=username]");

  $("#planForm").addEventListener("submit", guard(async event => {
    event.preventDefault();
    const options = readPlanOptions();

    ensureProfile(options.username);
    const { id } = await api("/plan", { method: "POST", body: options });

    $("#planOut").innerHTML = "";
    trackInline(id, "#planProgress", job => {
      currentPlan = job.result;
      currentPlan.collections.forEach(collection => { collection.included = true; });
      renderPlan();
    });
  }));

  // Fires when the field loses focus after an edit, or when the status line auto-fills the player name.
  usernameInput.addEventListener("change", () => ensureProfile(usernameInput.value.trim()));

  $("#planOut").addEventListener("click", event => {
    const target = event.target.dataset.drop;
    if (!target) return;

    // The ✕ buttons drop a single map from the proposed plan before it is applied.
    const [collectionIndex, mapIndex] = target.split(":").map(Number);
    currentPlan.collections[collectionIndex].maps.splice(mapIndex, 1);
    renderPlan();
  });

  $("#planOut").addEventListener("change", event => {
    const { include, rename } = event.target.dataset;

    if (include !== undefined) {
      currentPlan.collections[Number(include)].included = event.target.checked;
      renderPlan();
    } else if (rename !== undefined) {
      const collection = currentPlan.collections[Number(rename)];
      collection.name = event.target.value.trim() || collection.name;
      event.target.value = collection.name;
    }
  });

  await initPlannerForm().catch(error => toast(error.message, true));
  ensureProfile(usernameInput.value.trim());
}
