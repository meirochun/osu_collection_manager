// Collections tab: list, view, rename, delete, create, export and import collections.

import { $, $$, esc } from "./core/dom.js";
import { api } from "./core/api.js";
import { toast, guard } from "./core/toast.js";
import { formatLength, starBadge, coverImage, beatmapLink } from "./core/format.js";
import { collections, loadCollectionNames } from "./core/state.js";
import { watchJob } from "./jobs.js";
import { buildExportFile, downloadJson, exportFileName, parseImportFile } from "./collection-transfer.js";

let currentName = null;

// ---------- rendering ----------

function renderCollectionList() {
  $("#collList").innerHTML = collections.map(collection => {
    const missing = collection.missing
      ? ` <span class="bad" title="not in osu!.db (not imported yet or missing)">· ${collection.missing} missing</span>`
      : "";
    return `
      <li data-name="${esc(collection.name)}" class="${collection.name === currentName ? "active" : ""}">
        <span>${esc(collection.name)}</span>
        <span class="count">${collection.count}${missing}</span>
      </li>`;
  }).join("");
}

function renderHeader(collection) {
  $("#collHeader").innerHTML = `
    <strong class="coll-title">${esc(collection.name)}</strong>
    <span class="muted">${collection.maps.length} maps</span>
    <span class="spacer"></span>
    <button id="collExport" title="Save this collection as a .json file">Export</button>
    <button id="collRename">Rename</button>
    <button id="collDelete" class="danger">Delete</button>`;
}

function renderInstalledMapRow({ md5, beatmap }) {
  return `
    <tr>
      <td>${coverImage(beatmap.setId, beatmap.md5)}</td>
      <td>
        <div class="title">${beatmapLink(beatmap, `${esc(beatmap.artist)} - ${esc(beatmap.title)}`)}</div>
        <div class="sub">[${esc(beatmap.version)}] by ${esc(beatmap.creator)}</div>
      </td>
      <td>${starBadge(beatmap.starRating)}</td>
      <td>${beatmap.maxBpm}</td>
      <td>${formatLength(beatmap.drainSeconds)}</td>
      <td><button class="link danger" data-remove="${md5}">Remove</button></td>
    </tr>`;
}

function renderMissingMapRow({ md5 }) {
  return `
    <tr class="missing">
      <td><div class="thumb"></div></td>
      <td>
        <div class="title">Not in osu!.db</div>
        <div class="sub">${md5} · not imported yet, or the map was deleted/updated</div>
      </td>
      <td></td><td></td><td></td>
      <td><button class="link danger" data-remove="${md5}">Remove</button></td>
    </tr>`;
}

// ---------- loading ----------

export const loadCollections = guard(async () => {
  await loadCollectionNames();
  renderCollectionList();
  if (currentName) showCollection(currentName);
});

const showCollection = guard(async name => {
  const collection = await api("/collection?name=" + encodeURIComponent(name));

  $("#collEmpty").classList.toggle("hidden", collection.maps.length > 0);
  $("#collEmpty").textContent = "This collection is empty.";
  renderHeader(collection);

  // Easiest maps first; maps missing from osu!.db have no rating, so they sink to the bottom.
  const sortedMaps = [...collection.maps].sort(
    (a, b) => (a.beatmap?.starRating ?? 99) - (b.beatmap?.starRating ?? 99));
  $("#collRows").innerHTML = sortedMaps
    .map(map => map.beatmap ? renderInstalledMapRow(map) : renderMissingMapRow(map))
    .join("");

  $("#collExport").onclick = guard(() => exportCollection(collection));
  $("#collRename").onclick = guard(() => renameCollection(collection));
  $("#collDelete").onclick = guard(() => deleteCollection(collection));
});

// ---------- actions ----------

async function renameCollection(collection) {
  const newName = prompt("New name", collection.name);
  if (!newName || newName === collection.name) return;

  await api("/collection", { method: "POST", body: { name: collection.name, newName } });
  currentName = newName;
  loadCollections();
}

async function deleteCollection(collection) {
  const question = `Delete collection "${collection.name}"? (the maps stay installed; a backup of collection.db is kept)`;
  if (!confirm(question)) return;

  await api("/collection?name=" + encodeURIComponent(collection.name), { method: "DELETE" });
  currentName = null;
  $("#collRows").innerHTML = "";
  $("#collHeader").innerHTML = "";
  loadCollections();
}

async function exportCollection(collection) {
  const { player } = await api("/status");
  const fileName = exportFileName(player);

  downloadJson(fileName, buildExportFile(collection, player));
  toast(`Exported ${collection.maps.length} maps to ${fileName}`);
}

async function importCollection(file) {
  const { name, maps } = parseImportFile(await file.text());

  // Importing on top of an existing collection merges the maps; make that an explicit choice.
  const alreadyExists = collections.some(collection => collection.name === name);
  if (alreadyExists && !confirm(`A collection named "${name}" already exists. Add the imported maps to it?`)) {
    return;
  }

  // Step 1: create the collection. The server tells us which maps are not in this osu! library.
  const result = await api("/collection/import", { method: "POST", body: { name, maps } });
  currentName = name;
  await loadCollections();

  if (result.missing.length === 0) {
    toast(`Imported ${result.total} maps into "${name}"`);
    return;
  }

  // Step 2 (optional): find the missing maps online and download them. Downloads can be large, so ask first.
  const question = `Imported ${result.total} maps into "${name}", but ${result.missing.length} of them are not in your osu! library.

` +
    "Search for them online and download them now?";
  if (!confirm(question)) {
    toast(`Imported ${result.total} maps. ${result.missing.length} are listed as missing until you download them.`);
    return;
  }

  const { id } = await api("/collection/restore", { method: "POST", body: { name, maps: result.missing } });
  toast(`Looking up and downloading ${result.missing.length} maps. Progress is in the Jobs tab.`);
  watchJob(id, () => {
    toast("Missing maps downloaded. Start osu! to import them.");
    loadCollections();
  });
}

export function initCollections() {
  $("#collList").addEventListener("click", event => {
    const item = event.target.closest("li");
    if (!item) return;

    currentName = item.dataset.name;
    $$("#collList li").forEach(other => other.classList.toggle("active", other === item));
    showCollection(currentName);
  });

  // Remove-a-map buttons (delegated, rows are re-rendered whenever a collection is opened).
  $("#collRows").addEventListener("click", guard(async event => {
    const md5 = event.target.dataset.remove;
    if (!md5) return;

    await api("/collection", { method: "POST", body: { name: currentName, remove: [md5] } });
    loadCollections();
  }));

  $("#newCollBtn").addEventListener("click", guard(async () => {
    const name = $("#newColl").value.trim();
    if (!name) return;

    await api("/collection", { method: "POST", body: { name } });
    $("#newColl").value = "";
    currentName = name;
    loadCollections();
  }));

  // The visible "Import" button just opens the hidden file picker.
  const fileInput = $("#importCollFile");
  $("#importCollBtn").addEventListener("click", () => fileInput.click());
  fileInput.addEventListener("change", guard(async () => {
    const file = fileInput.files[0];
    fileInput.value = ""; // lets the same file be picked again later
    if (file) await importCollection(file);
  }));
}
